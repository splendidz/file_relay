using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using static FileRelay.L;

namespace FileRelay
{
    /// <summary>공유 폴더 하나의 설정.</summary>
    public sealed class ShareConfig
    {
        public string Name = "";
        public string Path = "";
        public AccessLevel Access = AccessLevel.Read;
        public bool Enabled = true;
        public string Description = "";
        /// <summary>피어별 권한. 키 = 피어 이름, IP 또는 ID (대소문자 무시).</summary>
        public Dictionary<string, AccessLevel> PeerAccess = new Dictionary<string, AccessLevel>(StringComparer.OrdinalIgnoreCase);

        public AccessLevel AccessFor(Peer p)
        {
            if (p == null || !Enabled) return AccessLevel.None;
            if (!string.IsNullOrEmpty(p.Id) && PeerAccess.TryGetValue(p.Id, out var a)) return a;
            if (!string.IsNullOrEmpty(p.Host) && PeerAccess.TryGetValue(p.Host, out a)) return a;
            if (!string.IsNullOrEmpty(p.Name) && PeerAccess.TryGetValue(p.Name, out a)) return a;
            return Access;
        }

        public ShareConfig Clone()
        {
            var c = (ShareConfig)MemberwiseClone();
            c.PeerAccess = new Dictionary<string, AccessLevel>(PeerAccess, StringComparer.OrdinalIgnoreCase);
            return c;
        }

        public JObj ToJson()
        {
            var pa = new JObj();
            foreach (var kv in PeerAccess) pa[kv.Key] = AccessParse.ToKey(kv.Value);
            return new JObj
            {
                ["name"] = Name,
                ["path"] = Path,
                ["access"] = AccessParse.ToKey(Access),
                ["enabled"] = Enabled,
                ["description"] = Description,
                ["peerAccess"] = pa,
            };
        }

        public static ShareConfig FromJson(JObj o)
        {
            var s = new ShareConfig
            {
                Name = o.S("name").Trim(),
                Path = o.S("path").Trim().Replace('/', '\\'),
                Access = AccessParse.Parse(o.S("access", "read")),
                Enabled = o.B("enabled", true),
                Description = o.S("description"),
            };
            var pa = o.O("peerAccess");
            if (pa != null)
                foreach (var kv in pa)
                    if (!string.IsNullOrWhiteSpace(kv.Key))
                        s.PeerAccess[kv.Key.Trim()] = AccessParse.Parse(kv.Value as string, s.Access);
            if (string.IsNullOrEmpty(s.Name) && !string.IsNullOrEmpty(s.Path))
                s.Name = DefaultNameFor(s.Path);
            return s;
        }

        /// <summary>
        /// 폴더 경로로 만든 기본 공유 이름. 드라이브 전체(D:\)는 "D 드라이브 (볼륨 이름)".
        /// 공유 이름은 상대 PC 에서 폴더 이름으로도 쓰이므로 ':' '\' 같은 문자는 넣을 수 없다.
        /// </summary>
        public static string DefaultNameFor(string path)
        {
            string p = (path ?? "").Trim().TrimEnd('\\', '/');
            if (p.Length == 2 && p[1] == ':' && char.IsLetter(p[0]))
            {
                string drive = T("share.drive", char.ToUpperInvariant(p[0]));
                try
                {
                    var di = new System.IO.DriveInfo(p + "\\");
                    if (di.IsReady && !string.IsNullOrWhiteSpace(di.VolumeLabel))
                    {
                        string withLabel = drive + " (" + di.VolumeLabel.Trim() + ")";
                        if (PathUtil.ValidateName(withLabel) == null) return withLabel;
                    }
                }
                catch { }
                return drive;
            }
            string name = System.IO.Path.GetFileName(p);
            if (string.IsNullOrWhiteSpace(name)) return T("share.default");
            foreach (char ch in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            name = name.Trim().TrimEnd('.');
            return PathUtil.ValidateName(name) == null ? name : T("share.default");
        }
    }

    /// <summary>
    /// 사용자 설정 (FileRelay.jsonc). 설정 창에서 저장할 때 주석이 달린 JSONC 로 다시 쓴다.
    /// 실행 상태(피어 목록, 창 위치 등)는 여기 두지 않는다 — data 폴더에 따로 저장.
    /// </summary>
    public sealed class AppConfig
    {
        public const int DefaultPort = 45200;

        public static readonly string[] ConflictActions =
            { "ask", "overwrite", "overwriteIfNewer", "overwriteIfDifferentSize", "resume", "rename", "skip" };

        public static string ConflictText(string key) =>
            ConflictActions.Contains(key) ? T("conflict." + key) : T("conflict.ask");

        public static readonly string[] NoteColors = { "yellow", "green", "pink", "purple", "blue", "gray", "charcoal" };

        // ── 사용자 / 네트워크 ──
        public string UserName = "";
        public int Port = DefaultPort;
        public string NetworkKey = "";
        public bool AutoAcceptPeers = true;
        public bool Discoverable = true;
        public List<string> BootstrapPeers = new List<string>();

        // ── 공유 ──
        public List<ShareConfig> Shares = new List<ShareConfig>();
        public bool ShareHiddenFiles;
        public bool RemoteDeleteToRecycleBin = true;
        public int ServeSpeedLimitKBps;

        // ── 전송 ──
        public string DownloadFolder = "";
        public int MaxConcurrentTransfers = 2;
        public int SpeedLimitKBps;
        public int RetryCount = 2;
        public string DefaultConflictAction = "ask";
        public bool ConfirmBeforeTransfer = true;
        public bool PreserveTimestamps = true;
        public string TempFolder = "";
        public bool NotifyTransferComplete = true;

        // ── 쪽지 ──
        public bool NotePopup = true;
        public bool NoteSound = true;
        public string NoteStyle = "auto";
        public string NoteColor = "yellow";
        public bool NoteTopMost = true;
        public bool SendReadReceipt = true;
        public int NoteHistoryDays = 180;

        // ── 알림 / 동작 ──
        public bool NotifyPeerOnline;
        public bool NotifyRemoteActivity = true;
        public int AutoAwayMinutes = 10;
        public bool StartMinimized;
        public bool StartWithWindows;
        public bool ConfirmOnExit = true;
        public bool ShowHiddenFiles;

        // ── 고급 ──
        public int HeartbeatSeconds = 10;
        public string Language = "auto";
        public string Theme = "light";
        public string UiFont = "auto";
        public float UiFontSize = 9f;
        public int LogRetentionDays = 30;

        public string EffectiveDownloadFolder =>
            !string.IsNullOrWhiteSpace(DownloadFolder) ? DownloadFolder : KnownFolders.Downloads;

        /// <summary>실제로 쓸 쪽지 스타일 (auto 면 화면 테마를 따른다).</summary>
        public string EffectiveNoteStyle => NoteStyle == "auto" ? (Theme == "dark" ? "dark" : "pastel") : NoteStyle;

        public string EffectiveTempFolder =>
            !string.IsNullOrWhiteSpace(TempFolder) ? TempFolder : AppPaths.DefaultTempDir;

        /// <summary>실제로 쓸 글꼴 (auto 면 언어에 맞춰: 한국어 맑은 고딕, 영어 Segoe UI).</summary>
        public string EffectiveUiFont =>
            UiFont != "auto" ? UiFont : (L.Resolve(Language) == "en" ? "Segoe UI" : "Malgun Gothic");

        public AppConfig Clone() => FromJson(ToJson());

        // ─── 읽기 ───────────────────────────────────────────────────────────
        public static AppConfig FromJson(JObj o)
        {
            var c = new AppConfig();
            if (o == null) return c;
            c.UserName = o.S("userName").Trim();
            c.Port = Num.Clamp(o.I("port", DefaultPort), 1, 65535);
            c.NetworkKey = o.S("networkKey");
            c.AutoAcceptPeers = o.B("autoAcceptPeers", true);
            c.Discoverable = o.B("discoverable", true);
            c.BootstrapPeers = o.StrList("bootstrapPeers").Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();

            c.Shares = new List<ShareConfig>();
            var arr = o.A("shares");
            if (arr != null)
                foreach (var x in arr)
                    if (x is JObj so) c.Shares.Add(ShareConfig.FromJson(so));
            c.ShareHiddenFiles = o.B("shareHiddenFiles");
            c.RemoteDeleteToRecycleBin = o.B("remoteDeleteToRecycleBin", true);
            c.ServeSpeedLimitKBps = Math.Max(0, o.I("serveSpeedLimitKBps"));

            c.DownloadFolder = o.S("downloadFolder").Trim();
            c.MaxConcurrentTransfers = Num.Clamp(o.I("maxConcurrentTransfers", 2), 1, 8);
            c.SpeedLimitKBps = Math.Max(0, o.I("speedLimitKBps"));
            c.RetryCount = Num.Clamp(o.I("retryCount", 2), 0, 10);
            c.DefaultConflictAction = o.S("defaultConflictAction", "ask");
            if (!ConflictActions.Contains(c.DefaultConflictAction)) c.DefaultConflictAction = "ask";
            c.ConfirmBeforeTransfer = o.B("confirmBeforeTransfer", true);
            c.PreserveTimestamps = o.B("preserveTimestamps", true);
            c.TempFolder = o.S("tempFolder").Trim();
            c.NotifyTransferComplete = o.B("notifyTransferComplete", true);

            c.NotePopup = o.B("notePopup", true);
            c.NoteSound = o.B("noteSound", true);
            string ns = o.S("noteStyle", "auto").ToLowerInvariant();
            c.NoteStyle = ns == "pastel" || ns == "dark" ? ns : "auto";
            // 1.0.0 은 테마 설정 없이 noteStyle 기본값 dark 를 써 넣었다 — 화면 테마를 따르게 옮긴다.
            if (!o.Has("theme") && c.NoteStyle == "dark") c.NoteStyle = "auto";
            c.NoteColor = o.S("noteColor", "yellow").ToLowerInvariant();
            if (!NoteColors.Contains(c.NoteColor)) c.NoteColor = "yellow";
            c.NoteTopMost = o.B("noteTopMost", true);
            c.SendReadReceipt = o.B("sendReadReceipt", true);
            c.NoteHistoryDays = Num.Clamp(o.I("noteHistoryDays", 180), 0, 3650);

            c.NotifyPeerOnline = o.B("notifyPeerOnline");
            c.NotifyRemoteActivity = o.B("notifyRemoteActivity", true);
            c.AutoAwayMinutes = Num.Clamp(o.I("autoAwayMinutes", 10), 0, 1440);
            c.StartMinimized = o.B("startMinimized");
            c.StartWithWindows = o.B("startWithWindows");
            c.ConfirmOnExit = o.B("confirmOnExit", true);
            c.ShowHiddenFiles = o.B("showHiddenFiles");

            c.HeartbeatSeconds = Num.Clamp(o.I("heartbeatSeconds", 10), 3, 300);
            string lang = o.S("language", "auto").ToLowerInvariant();
            c.Language = lang == "ko" || lang == "en" ? lang : "auto";
            c.Theme = o.S("theme", "light").Eq("dark") ? "dark" : "light";
            c.UiFont = o.S("uiFont", "auto");
            // 1.1 이하는 기본 글꼴 이름을 그대로 써 넣었다 — 언어에 따라 바뀌도록 auto 로 옮긴다.
            if (string.IsNullOrWhiteSpace(c.UiFont) || (!o.Has("language") && c.UiFont == "Malgun Gothic")) c.UiFont = "auto";
            c.UiFontSize = (float)Num.Clamp(o.D("uiFontSize", 9), 7, 16);
            c.LogRetentionDays = Num.Clamp(o.I("logRetentionDays", 30), 1, 3650);
            return c;
        }

        public JObj ToJson()
        {
            return new JObj
            {
                ["userName"] = UserName,
                ["port"] = Port,
                ["networkKey"] = NetworkKey,
                ["autoAcceptPeers"] = AutoAcceptPeers,
                ["discoverable"] = Discoverable,
                ["bootstrapPeers"] = BootstrapPeers.Cast<object>().ToList(),
                ["shares"] = Shares.Select(s => (object)s.ToJson()).ToList(),
                ["shareHiddenFiles"] = ShareHiddenFiles,
                ["remoteDeleteToRecycleBin"] = RemoteDeleteToRecycleBin,
                ["serveSpeedLimitKBps"] = ServeSpeedLimitKBps,
                ["downloadFolder"] = DownloadFolder,
                ["maxConcurrentTransfers"] = MaxConcurrentTransfers,
                ["speedLimitKBps"] = SpeedLimitKBps,
                ["retryCount"] = RetryCount,
                ["defaultConflictAction"] = DefaultConflictAction,
                ["confirmBeforeTransfer"] = ConfirmBeforeTransfer,
                ["preserveTimestamps"] = PreserveTimestamps,
                ["tempFolder"] = TempFolder,
                ["notifyTransferComplete"] = NotifyTransferComplete,
                ["notePopup"] = NotePopup,
                ["noteSound"] = NoteSound,
                ["noteStyle"] = NoteStyle,
                ["noteColor"] = NoteColor,
                ["noteTopMost"] = NoteTopMost,
                ["sendReadReceipt"] = SendReadReceipt,
                ["noteHistoryDays"] = NoteHistoryDays,
                ["notifyPeerOnline"] = NotifyPeerOnline,
                ["notifyRemoteActivity"] = NotifyRemoteActivity,
                ["autoAwayMinutes"] = AutoAwayMinutes,
                ["startMinimized"] = StartMinimized,
                ["startWithWindows"] = StartWithWindows,
                ["confirmOnExit"] = ConfirmOnExit,
                ["showHiddenFiles"] = ShowHiddenFiles,
                ["heartbeatSeconds"] = HeartbeatSeconds,
                ["language"] = Language,
                ["theme"] = Theme,
                ["uiFont"] = UiFont,
                ["uiFontSize"] = (double)UiFontSize,
                ["logRetentionDays"] = LogRetentionDays,
            };
        }

        public static AppConfig Load(string path)
        {
            if (!File.Exists(path)) return new AppConfig();
            string text = File.ReadAllText(path, Encoding.UTF8);
            return FromJson(Json.ParseObject(text));
        }

        public void Save(string path)
        {
            FileUtil.WriteAtomic(path, ToJsonc());
        }

        /// <summary>설정 값 검증. 문제 목록(사람이 읽는 문장)을 돌려준다.</summary>
        public List<string> Validate()
        {
            var errs = new List<string>();
            if (string.IsNullOrWhiteSpace(UserName)) errs.Add(T("cfgerr.noName"));
            if (Port < 1024 || Port > 65535) errs.Add(T("cfgerr.port"));
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in Shares)
            {
                string label = string.IsNullOrEmpty(s.Name) ? s.Path : s.Name;
                var ne = PathUtil.ValidateName(s.Name);
                if (ne != null) errs.Add(T("cfgerr.shareName", s.Name, ne));
                else if (!names.Add(s.Name)) errs.Add(T("cfgerr.shareDup", s.Name));
                if (string.IsNullOrWhiteSpace(s.Path)) errs.Add(T("cfgerr.sharePathEmpty", label));
                else if (!System.IO.Path.IsPathRooted(s.Path)) errs.Add(T("cfgerr.sharePathRooted", label));
            }
            return errs;
        }

        // ─── JSONC 쓰기 (주석 포함) ─────────────────────────────────────────
        public string ToJsonc()
        {
            var b = new JsoncBuilder();
            b.Header(T("cfg.header").Split('\n'));

            b.Section(T("cfg.sec.user"));
            b.Prop("userName", UserName, T("cfg.p.userName"));
            b.Prop("port", Port, T("cfg.p.port"));
            b.Prop("networkKey", NetworkKey, T("cfg.p.networkKey"));
            b.Prop("autoAcceptPeers", AutoAcceptPeers, T("cfg.p.autoAcceptPeers"));
            b.Prop("discoverable", Discoverable, T("cfg.p.discoverable"));
            b.Prop("bootstrapPeers", BootstrapPeers.Cast<object>().ToList(), T("cfg.p.bootstrapPeers"));

            b.Section(T("cfg.sec.shares"));
            foreach (var line in T("cfg.c.shares").Split('\n')) b.Comment(line);
            b.Prop("shares", Shares.Select(s => (object)s.ToJson()).ToList());
            b.Prop("shareHiddenFiles", ShareHiddenFiles, T("cfg.p.shareHiddenFiles"));
            b.Prop("remoteDeleteToRecycleBin", RemoteDeleteToRecycleBin, T("cfg.p.remoteDeleteToRecycleBin"));
            b.Prop("serveSpeedLimitKBps", ServeSpeedLimitKBps, T("cfg.p.serveSpeedLimitKBps"));

            b.Section(T("cfg.sec.transfer"));
            b.Prop("downloadFolder", DownloadFolder, T("cfg.p.downloadFolder"));
            b.Prop("maxConcurrentTransfers", MaxConcurrentTransfers, T("cfg.p.maxConcurrentTransfers"));
            b.Prop("speedLimitKBps", SpeedLimitKBps, T("cfg.p.speedLimitKBps"));
            b.Prop("retryCount", RetryCount, T("cfg.p.retryCount"));
            b.Prop("defaultConflictAction", DefaultConflictAction, T("cfg.p.defaultConflictAction"));
            b.Prop("confirmBeforeTransfer", ConfirmBeforeTransfer, T("cfg.p.confirmBeforeTransfer"));
            b.Prop("preserveTimestamps", PreserveTimestamps, T("cfg.p.preserveTimestamps"));
            b.Prop("tempFolder", TempFolder, T("cfg.p.tempFolder"));
            b.Prop("notifyTransferComplete", NotifyTransferComplete, T("cfg.p.notifyTransferComplete"));

            b.Section(T("cfg.sec.notes"));
            b.Prop("notePopup", NotePopup, T("cfg.p.notePopup"));
            b.Prop("noteSound", NoteSound, T("cfg.p.noteSound"));
            b.Prop("noteStyle", NoteStyle, T("cfg.p.noteStyle"));
            b.Prop("noteColor", NoteColor, T("cfg.p.noteColor"));
            b.Prop("noteTopMost", NoteTopMost, T("cfg.p.noteTopMost"));
            b.Prop("sendReadReceipt", SendReadReceipt, T("cfg.p.sendReadReceipt"));
            b.Prop("noteHistoryDays", NoteHistoryDays, T("cfg.p.noteHistoryDays"));

            b.Section(T("cfg.sec.behavior"));
            b.Prop("notifyPeerOnline", NotifyPeerOnline, T("cfg.p.notifyPeerOnline"));
            b.Prop("notifyRemoteActivity", NotifyRemoteActivity, T("cfg.p.notifyRemoteActivity"));
            b.Prop("autoAwayMinutes", AutoAwayMinutes, T("cfg.p.autoAwayMinutes"));
            b.Prop("startMinimized", StartMinimized, T("cfg.p.startMinimized"));
            b.Prop("startWithWindows", StartWithWindows, T("cfg.p.startWithWindows"));
            b.Prop("confirmOnExit", ConfirmOnExit, T("cfg.p.confirmOnExit"));
            b.Prop("showHiddenFiles", ShowHiddenFiles, T("cfg.p.showHiddenFiles"));

            b.Section(T("cfg.sec.advanced"));
            b.Prop("heartbeatSeconds", HeartbeatSeconds, T("cfg.p.heartbeatSeconds"));
            b.Prop("language", Language, T("cfg.p.language"));
            b.Prop("theme", Theme, T("cfg.p.theme"));
            b.Prop("uiFont", UiFont, T("cfg.p.uiFont"));
            b.Prop("uiFontSize", (double)UiFontSize, T("cfg.p.uiFontSize"));
            b.Prop("logRetentionDays", LogRetentionDays, T("cfg.p.logRetentionDays"));
            return b.Build();
        }

        sealed class JsoncBuilder
        {
            sealed class Entry
            {
                public List<string> Comments = new List<string>();
                public string Key;
                public string Json;
                public string Trailing;
            }

            readonly List<string> _header = new List<string>();
            readonly List<Entry> _entries = new List<Entry>();
            List<string> _pending = new List<string>();

            public void Header(params string[] lines) => _header.AddRange(lines);

            public void Section(string title)
            {
                _pending.Add("");
                _pending.Add("// ───────── " + title + " " + new string('─', Math.Max(4, 50 - title.Length)));
            }

            public void Comment(string line) => _pending.Add("// " + line);

            public void Prop(string key, object value, string trailing = null)
            {
                _entries.Add(new Entry
                {
                    Comments = _pending,
                    Key = key,
                    Json = global::FileRelay.Json.Serialize(value, true),
                    Trailing = trailing,
                });
                _pending = new List<string>();
            }

            public string Build()
            {
                var sb = new StringBuilder();
                foreach (var h in _header) sb.Append(h.Length == 0 ? "//" : "// " + h).Append("\r\n");
                sb.Append("{");
                for (int i = 0; i < _entries.Count; i++)
                {
                    var e = _entries[i];
                    foreach (var c in e.Comments) sb.Append("\r\n").Append(c.Length == 0 ? "" : "  " + c);
                    string line = "  " + global::FileRelay.Json.Quote(e.Key) + ": " + e.Json.Replace("\n", "\r\n  ");
                    if (i < _entries.Count - 1) line += ",";
                    if (!string.IsNullOrEmpty(e.Trailing))
                    {
                        int lastLineLen = line.Length - line.LastIndexOf('\n') - 1;
                        line += new string(' ', Math.Max(2, 44 - lastLineLen)) + "// " + e.Trailing;
                    }
                    sb.Append("\r\n").Append(line);
                }
                sb.Append("\r\n}\r\n");
                return sb.ToString();
            }
        }
    }

    public static class FileUtil
    {
        static readonly object WriteLock = new object();

        /// <summary>
        /// 임시 파일에 쓴 뒤 교체 — 쓰는 도중 꺼져도 원본이 깨지지 않게.
        /// 여러 스레드(디바운스 타이머, 즉시 저장)가 같은 파일을 동시에 써도 안전하도록 직렬화한다.
        /// </summary>
        public static void WriteAtomic(string path, string text)
        {
            lock (WriteLock)
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                try
                {
                    if (File.Exists(path))
                    {
                        try { File.Replace(tmp, path, null, true); return; }
                        catch { /* 다른 볼륨, 잠김 등 — 아래로 */ }
                        File.Copy(tmp, path, true);
                    }
                    else
                    {
                        File.Move(tmp, path);
                        return;
                    }
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
            }
        }
    }
}
