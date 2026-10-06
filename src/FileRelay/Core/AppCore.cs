using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

using static FileRelay.L;
namespace FileRelay
{
    public enum NotifyKind { Info, Success, Warning, Error, Peer, Note, Transfer }

    /// <summary>
    /// 앱 서비스 허브 — 설정, 저장소, 서버, 피어 서비스, 전송 관리자를 묶고 UI 이벤트를 낸다.
    /// UI 이벤트는 항상 UI 스레드에서 발생한다.
    /// </summary>
    public sealed class AppCore
    {
        public static AppCore I { get; private set; }

        public AppConfig Config { get; private set; } = new AppConfig();
        public readonly UiState State = new UiState();
        public readonly PeerStore Peers = new PeerStore();
        public readonly NoteStore Notes = new NoteStore();
        public readonly ActivityLog Activity = new ActivityLog();
        public readonly MiniHttpServer Http = new MiniHttpServer();
        public readonly RateLimiter ClientLimiter = new RateLimiter();
        public readonly RateLimiter ServeLimiter = new RateLimiter();
        public ApiServer Api { get; private set; }
        public PeerService PeerSvc { get; private set; }
        public TransferManager Transfers { get; private set; }
        public Discovery Disc { get; private set; }

        public SynchronizationContext Ui { get; private set; }
        public string MyId { get; private set; } = "";
        public Presence MyStatus { get; private set; } = Presence.Online;
        public bool StatusIsAuto { get; private set; }
        public string ServerError { get; private set; } = "";
        public string ConfigError { get; private set; } = "";
        public DateTime StartedAt { get; } = DateTime.Now;

        // ── UI 이벤트 (UI 스레드) ──
        public event Action PeersChanged;
        public event Action<Note> NoteArrived;
        public event Action NotesChanged;
        public event Action ConfigChanged;
        public event Action ServerStateChanged;
        public event Action StatusChanged;
        public event Action<Peer> PeerRequest;
        /// <summary>트레이 알림 요청: 제목, 내용, 종류, 클릭 시 동작(없을 수 있음).</summary>
        public event Action<string, string, NotifyKind, Action> Notify;

        System.Threading.Timer _awayTimer;
        FileSystemWatcher _cfgWatcher;
        Debouncer _cfgReload;
        string _lastSavedConfigText = "";
        int _peersChangedPending;

        public static AppCore Create(SynchronizationContext ui)
        {
            I = new AppCore { Ui = ui };
            return I;
        }

        public bool HasConfigFile => File.Exists(AppPaths.ConfigFile);

        /// <summary>설정 파일만 먼저 읽는다 (첫 실행 판단, 글꼴 결정용).</summary>
        public void LoadConfig()
        {
            try
            {
                Config = AppConfig.Load(AppPaths.ConfigFile);
                ConfigError = "";
            }
            catch (Exception ex)
            {
                ConfigError = ex.Message;
                Config = new AppConfig();
                // 망가진 설정을 덮어쓰지 않도록 백업해 둔다.
                try { File.Copy(AppPaths.ConfigFile, AppPaths.ConfigFile + ".broken", true); } catch { }
            }
        }

        /// <summary>첫 실행 마법사 결과 저장 (Start 전에 호출).</summary>
        public void ApplyFirstRun(AppConfig cfg)
        {
            Config = cfg;
            SaveConfig();
            SetAutoStart(cfg.StartWithWindows);
        }

        public void Start()
        {
            Log.Init(AppPaths.LogDir, Config.LogRetentionDays);
            Log.Info(Cat.Sys, T("log.started", AppInfo.Version, AppPaths.ConfigFile));
            if (!string.IsNullOrEmpty(ConfigError))
                Log.Error(Cat.Sys, T("log.configBrokenDefaults", ConfigError));

            State.Load(AppPaths.StateFile);
            MyId = State.InstanceId;
            Peers.Load(AppPaths.PeersFile);
            foreach (var p in Peers.All().Where(p => p.Id == MyId)) Peers.Remove(p);   // 나 자신 항목 정리
            if (State.IdRegenerated)
            {
                // 내 ID 가 바뀌었다 — 상대들은 예전 ID 를 알고 있으므로 다시 등록한다 (상대는 주소로 같은 항목을 찾아 갱신).
                foreach (var p in Peers.All())
                    if (p.State == PeerState.Paired || p.State == PeerState.Unpaired) p.State = PeerState.Pending;
                Peers.Touch(true);
            }
            Notes.Load(AppPaths.NotesFile, Config.NoteHistoryDays);
            foreach (var p in Peers.All()) p.Unread = Notes.UnreadFrom(p.Id);

            Peers.Changed += () =>
            {
                // 여러 번 연속 변경을 UI 에 한 번만 전달.
                if (Interlocked.Exchange(ref _peersChangedPending, 1) == 1) return;
                Post(() => { _peersChangedPending = 0; PeersChanged?.Invoke(); });
            };
            Notes.Changed += () => Post(() => NotesChanged?.Invoke());

            ApiClient.Identity = IdentityHeaders;
            Api = new ApiServer(this);
            Http.Handler = Api.Handle;
            PeerSvc = new PeerService(this);
            Transfers = new TransferManager(this);
            Disc = new Discovery(this);

            ApplyLimits();
            StartServer();
            Disc.Start(Config.Port);
            PeerSvc.Start();
            Transfers.Start();
            PeerSvc.ConnectBootstrap();

            _awayTimer = new System.Threading.Timer(_ => CheckIdle(), null, 15000, 15000);
            WatchConfig();
            if (!HasConfigFile) SaveConfig();
            else _lastSavedConfigText = SafeRead(AppPaths.ConfigFile);
        }

        /// <summary>공유 서버 시작. quiet = 자동 재시도 (같은 오류는 다시 알리지 않음).</summary>
        public void StartServer(bool quiet = false)
        {
            try
            {
                bool wasDown = ServerError.Length > 0;
                Http.Start(Config.Port);
                ServerError = "";
                Log.Success(Cat.Server, T("log.serverStarted", NetUtil.PrimaryIPv4(), Config.Port));
                if (wasDown && quiet)
                {
                    RaiseNotify(T("notify.serverStarted.title"), T("notify.serverStarted.text", Config.Port), NotifyKind.Success);
                    PeerSvc?.BroadcastPresence();
                }
            }
            catch (Exception ex)
            {
                string err;
                if (ex is System.Net.Sockets.SocketException se && se.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
                {
                    string other = WhoUsesPort(Config.Port);
                    err = other != null
                        ? T("err.portInUseByFileRelay", other, Config.Port)
                        : T("err.portInUse", Config.Port);
                }
                else err = ex.Message;
                bool changed = err != ServerError;
                ServerError = err;
                if (!quiet || changed)
                {
                    Log.Error(Cat.Server, T("log.serverStartFailed", ServerError));
                    if (!quiet) RaiseNotify(T("notify.serverFailed.title"), ServerError, NotifyKind.Error);
                }
            }
            Post(() => ServerStateChanged?.Invoke());
        }

        /// <summary>같은 PC 의 포트를 FileRelay 가 쓰고 있으면 그 이름.</summary>
        static string WhoUsesPort(int port)
        {
            try
            {
                var h = ApiClient.Call("127.0.0.1", port, "GET", "/api/hello", null, 1500);
                if (h.S("app") == AppInfo.Name) return "'" + h.S("name") + "'";
            }
            catch { }
            return null;
        }

        public IDictionary<string, string> IdentityHeaders()
        {
            return new Dictionary<string, string>
            {
                [ApiClient.HId] = MyId,
                [ApiClient.HName] = ApiClient.Enc(Config.UserName),
                [ApiClient.HPort] = Config.Port.ToString(),
                [ApiClient.HStatus] = AccessParse.PresenceKey(MyStatus),
                [ApiClient.HKey] = KeyHash,
                [ApiClient.HVer] = AppInfo.Version,
                ["X-FR-Machine"] = ApiClient.Enc(Environment.MachineName),
            };
        }

        /// <summary>그룹 키는 해시로만 주고받는다.</summary>
        public string KeyHash => string.IsNullOrEmpty(Config.NetworkKey) ? "" : Hash.Short("FileRelay|" + Config.NetworkKey, 32);

        public void Post(Action a)
        {
            if (Ui == null) { a(); return; }
            Ui.Post(_ =>
            {
                try { a(); }
                catch (Exception ex) { Log.Error(Cat.Sys, T("log.uiError", ex)); }
            }, null);
        }

        public void RaiseNotify(string title, string text, NotifyKind kind, Action onClick = null) =>
            Post(() => Notify?.Invoke(title, text, kind, onClick));

        public void RaisePeerRequest(Peer p) => Post(() => PeerRequest?.Invoke(p));

        // ─── 내 상태 ────────────────────────────────────────────────────────
        public void SetStatus(Presence p, bool auto)
        {
            if (p == Presence.Offline) p = Presence.Online;
            bool changed = MyStatus != p;
            MyStatus = p;
            StatusIsAuto = auto;
            if (!changed) return;
            Log.Info(Cat.Sys, T(auto ? "log.myStatusAuto" : "log.myStatus", Fmt.Presence(p)));
            PeerSvc?.BroadcastPresence();
            Post(() => StatusChanged?.Invoke());
        }

        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        public static double IdleSeconds()
        {
            var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref li)) return 0;
            return unchecked((uint)Environment.TickCount - li.dwTime) / 1000.0;
        }

        void CheckIdle()
        {
            // 서버가 못 떴으면(포트 충돌 등) 15초마다 조용히 다시 시도.
            try
            {
                if (!Http.Running && ServerError.Length > 0)
                {
                    StartServer(true);
                    if (Http.Running) Disc?.Start(Config.Port);
                }
            }
            catch { }
            try
            {
                int mins = Config.AutoAwayMinutes;
                double idle = IdleSeconds();
                if (mins > 0 && MyStatus == Presence.Online && idle >= mins * 60)
                    SetStatus(Presence.Away, true);
                else if (StatusIsAuto && MyStatus == Presence.Away && idle < 20)
                    SetStatus(Presence.Online, false);
            }
            catch { }
        }

        // ─── 설정 ──────────────────────────────────────────────────────────
        public void SaveConfig()
        {
            try
            {
                string text = Config.ToJsonc();
                _lastSavedConfigText = text;
                FileUtil.WriteAtomic(AppPaths.ConfigFile, text);
            }
            catch (Exception ex)
            {
                Log.Error(Cat.Sys, T("log.configSaveFailed", ex.Message));
                RaiseNotify(T("notify.configSaveFailed.title"), ex.Message, NotifyKind.Error);
            }
        }

        /// <summary>새 설정을 적용한다 (포트 변경 시 서버 재시작, 이름 변경 시 피어에 알림).</summary>
        public void ApplyConfig(AppConfig cfg, bool save)
        {
            var old = Config;
            Config = cfg;
            if (save) SaveConfig();
            ApplyLimits();
            if (old.Port != cfg.Port || !Http.Running)
            {
                StartServer();
                Disc.Start(cfg.Port);
            }
            if (old.UserName != cfg.UserName || old.NetworkKey != cfg.NetworkKey || old.Port != cfg.Port)
                PeerSvc.BroadcastPresence();
            if (old.HeartbeatSeconds != cfg.HeartbeatSeconds) PeerSvc.UpdateInterval();
            if (old.StartWithWindows != cfg.StartWithWindows || cfg.StartWithWindows) SetAutoStart(cfg.StartWithWindows);
            Transfers.Kick();
            Log.Info(Cat.Sys, T("log.configApplied"));
            Post(() => ConfigChanged?.Invoke());
        }

        void ApplyLimits()
        {
            ClientLimiter.BytesPerSecond = Config.SpeedLimitKBps * 1024L;
            ServeLimiter.BytesPerSecond = Config.ServeSpeedLimitKBps * 1024L;
        }

        public void SetSpeedLimit(int kbps)
        {
            var c = Config.Clone();
            c.SpeedLimitKBps = Math.Max(0, kbps);
            ApplyConfig(c, true);
        }

        void WatchConfig()
        {
            try
            {
                _cfgReload = new Debouncer(ReloadConfigFromDisk, 700);
                _cfgWatcher = new FileSystemWatcher(Path.GetDirectoryName(AppPaths.ConfigFile), Path.GetFileName(AppPaths.ConfigFile))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                _cfgWatcher.Changed += (s, e) => _cfgReload.Kick();
                _cfgWatcher.Created += (s, e) => _cfgReload.Kick();
                _cfgWatcher.Renamed += (s, e) => _cfgReload.Kick();
            }
            catch (Exception ex) { Log.Warn(Cat.Sys, T("log.configWatchFailed", ex.Message)); }
        }

        static string SafeRead(string path)
        {
            try { return File.ReadAllText(path, Encoding.UTF8); } catch { return ""; }
        }

        /// <summary>외부 편집기로 jsonc 를 고친 경우 다시 읽는다.</summary>
        void ReloadConfigFromDisk()
        {
            if (!File.Exists(AppPaths.ConfigFile)) return;
            string text = SafeRead(AppPaths.ConfigFile);
            if (text.Length == 0 || text == _lastSavedConfigText) return;
            _lastSavedConfigText = text;
            AppConfig cfg;
            try { cfg = AppConfig.FromJson(Json.ParseObject(text)); }
            catch (Exception ex)
            {
                Log.Error(Cat.Sys, T("log.configErrorKeep", ex.Message));
                RaiseNotify(T("notify.configError.title"), T("notify.configError.keep", ex.Message), NotifyKind.Error);
                return;
            }
            var errs = cfg.Validate();
            if (errs.Count > 0 && string.IsNullOrWhiteSpace(cfg.UserName))
            {
                Log.Error(Cat.Sys, T("log.configError", string.Join(" / ", errs)));
                RaiseNotify(T("notify.configError.title"), string.Join("\n", errs), NotifyKind.Error);
                return;
            }
            foreach (var e in errs) Log.Warn(Cat.Sys, T("log.configCheck", e));
            Log.Info(Cat.Sys, T("log.configReloaded"));
            bool fontChanged = cfg.UiFont != Config.UiFont || Math.Abs(cfg.UiFontSize - Config.UiFontSize) > 0.01 || cfg.Theme != Config.Theme || cfg.Language != Config.Language;
            Post(() =>
            {
                ApplyConfig(cfg, false);
                RaiseNotify(T("notify.configReloaded.title"), fontChanged ? T("notify.configReloaded.restart") : T("notify.configReloaded.applied"), NotifyKind.Info);
            });
        }

        public string AutoStartValueName =>
            string.IsNullOrEmpty(AppPaths.CustomDataArg) ? "FileRelay" : "FileRelay_" + Hash.Short(AppPaths.RootDir, 6);

        public void SetAutoStart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (on)
                    {
                        string cmd = "\"" + AppPaths.ExePath + "\" --tray";
                        if (!string.IsNullOrEmpty(AppPaths.CustomDataArg)) cmd += " --data \"" + AppPaths.RootDir + "\"";
                        k.SetValue(AutoStartValueName, cmd);
                    }
                    else if (k.GetValue(AutoStartValueName) != null) k.DeleteValue(AutoStartValueName, false);
                }
            }
            catch (Exception ex) { Log.Warn(Cat.Sys, T("log.autostartFailed", ex.Message)); }
        }

        // ─── 쪽지 ──────────────────────────────────────────────────────────
        /// <summary>서버 스레드에서 호출 — 받은 쪽지를 저장하고 UI 에 알린다.</summary>
        public void OnNoteReceived(Note n, Peer from)
        {
            Notes.Add(n);
            from.Unread = Notes.UnreadFrom(from.Id);
            Peers.Touch(false);
            Log.Info(Cat.Note, T("log.noteReceived", from.DisplayName, Fmt.OneLine(n.Text, 60)));
            Post(() => NoteArrived?.Invoke(n));
        }

        public void MarkNoteRead(Note n)
        {
            if (!n.Incoming || n.IsRead) return;
            n.ReadAt = DateTime.Now;
            Notes.Touch();
            var p = Peers.FindById(n.PeerId);
            if (p != null) { p.Unread = Notes.UnreadFrom(p.Id); Peers.Touch(false); }
            if (Config.SendReadReceipt && p != null && p.IsOnline)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { ApiClient.Call(p, "POST", "/api/note/read", new JObj { ["id"] = n.Id, ["readAt"] = n.ReadAt }, 4000); }
                    catch { }
                });
            }
        }

        /// <summary>쪽지 보내기 (블로킹 — 백그라운드에서 호출).</summary>
        public Note SendNote(Peer to, string text, string color, string replyTo)
        {
            var n = new Note { Incoming = false, PeerId = to.Id, PeerName = to.DisplayName, Text = text, Color = color, ReplyTo = replyTo ?? "", Time = DateTime.Now };
            var r = ApiClient.Call(to, "POST", "/api/note", new JObj
            {
                ["id"] = n.Id,
                ["text"] = text,
                ["color"] = color,
                ["sentAt"] = n.Time,
                ["replyTo"] = n.ReplyTo,
            }, 8000);
            Notes.Add(n);
            Log.Success(Cat.Note, T(r.S("status") == "busy" ? "log.noteSentBusy" : "log.noteSent", to.DisplayName));
            return n;
        }

        // ─── 원격 활동 알림 (묶어서) ─────────────────────────────────────────
        readonly object _actLock = new object();
        sealed class ActBuf { public string Who, Act, First, Extra; public int Count; }
        readonly Dictionary<string, ActBuf> _actBuf = new Dictionary<string, ActBuf>();
        System.Threading.Timer _actTimer;

        /// <summary>
        /// 피어가 내 공유를 변경함 — 2.5초 동안 모아서 알림 한 번.
        /// act: upload | overwrite | mkdir | rename | delete | deleteRecycled,  extra: rename 의 새 이름.
        /// </summary>
        public void ReportRemoteChange(Peer p, string act, string path, int count = 1, string extra = null)
        {
            Log.Info(Cat.Share, T("log.remoteChange", p.DisplayName, T("act." + act), path) + (count > 1 ? " (+" + (count - 1) + ")" : ""));
            if (!Config.NotifyRemoteActivity) return;
            lock (_actLock)
            {
                string key = p.DisplayName + "\u0001" + act;
                if (!_actBuf.TryGetValue(key, out var b)) _actBuf[key] = b = new ActBuf { Who = p.DisplayName, Act = act, First = path, Extra = extra };
                b.Count += Math.Max(1, count);
                if (_actTimer == null) _actTimer = new System.Threading.Timer(_ => FlushActivity(), null, 2500, Timeout.Infinite);
                else _actTimer.Change(2500, Timeout.Infinite);
            }
        }

        void FlushActivity()
        {
            List<ActBuf> buf;
            lock (_actLock)
            {
                buf = new List<ActBuf>(_actBuf.Values);
                _actBuf.Clear();
            }
            foreach (var b in buf)
            {
                string text = T("notify.remote." + b.Act + (b.Count == 1 ? ".one" : ".many"), b.Who, b.First, b.Count - 1, b.Extra ?? "");
                RaiseNotify(T("notify.remote.title"), text, NotifyKind.Info);
            }
        }

        // ─── 종료 ──────────────────────────────────────────────────────────
        public void Shutdown()
        {
            Log.Info(Cat.Sys, T("log.exiting"));
            try { _awayTimer?.Dispose(); } catch { }
            try { _cfgWatcher?.Dispose(); } catch { }
            try { Transfers?.Stop(); } catch { }
            try { PeerSvc?.Stop(); } catch { }
            try { PeerSvc?.SayGoodbye(); } catch { }
            try { Disc?.Stop(); } catch { }
            try { Http.Stop(); } catch { }
            try { Peers.Flush(); } catch { }
            try { Notes.Flush(); } catch { }
            try { State.Flush(); } catch { }
            Log.Close();
        }
    }
}
