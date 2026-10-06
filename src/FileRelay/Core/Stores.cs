using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

using static FileRelay.L;
namespace FileRelay
{
    /// <summary>디바운스 저장 도우미 — 잦은 변경을 모아서 한 번에 쓴다.</summary>
    sealed class Debouncer : IDisposable
    {
        readonly Timer _timer;
        readonly Action _action;
        readonly int _ms;

        public Debouncer(Action action, int ms = 600)
        {
            _action = action;
            _ms = ms;
            _timer = new Timer(_ => { try { _action(); } catch (Exception ex) { Log.Warn(Cat.Sys, T("log.saveFailed", ex.Message)); } }, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Kick() => _timer.Change(_ms, Timeout.Infinite);

        public void Flush()
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            try { _action(); } catch (Exception ex) { Log.Warn(Cat.Sys, T("log.saveFailed", ex.Message)); }
        }

        public void Dispose() => _timer.Dispose();
    }

    /// <summary>창 위치·열 너비·마지막 경로 같은 UI 상태 + 인스턴스 ID (state.json).</summary>
    public sealed class UiState
    {
        readonly object _lock = new object();
        JObj _d = new JObj();
        string _path;
        Debouncer _save;

        public void Load(string path)
        {
            _path = path;
            _save = new Debouncer(SaveNow, 800);
            try
            {
                if (File.Exists(path)) _d = Json.ParseObject(File.ReadAllText(path, Encoding.UTF8)) ?? new JObj();
            }
            catch (Exception ex)
            {
                Log.Warn(Cat.Sys, T("log.stateReadFailed", ex.Message));
                _d = new JObj();
            }
        }

        /// <summary>이번 실행에서 ID 를 새로 만들었는지 (처음 실행 제외 — 복사된 데이터/이전 버전 데이터).</summary>
        public bool IdRegenerated { get; private set; }

        /// <summary>
        /// 이 PC 의 FileRelay 고유 ID. PC 지문(MachineGuid + PC 이름)과 함께 저장해서,
        /// 데이터 폴더(FileRelay_data)를 다른 PC 로 복사해 가도 그 PC 에서는 새 ID 를 쓰게 한다.
        /// (같은 ID 를 쓰는 두 PC 는 서로를 '나 자신'으로 판단해 연결할 수 없다.)
        /// </summary>
        public string InstanceId
        {
            get
            {
                lock (_lock)
                {
                    string id = _d.S("instanceId");
                    string fp = MachineFingerprint();
                    if (string.IsNullOrEmpty(id) || _d.S("instanceMachine") != fp)
                    {
                        if (!string.IsNullOrEmpty(id))
                        {
                            IdRegenerated = true;
                            Log.Warn(Cat.Sys, _d.Has("instanceMachine")
                                ? T("log.idCopied")
                                : T("log.idLegacy"));
                        }
                        id = Guid.NewGuid().ToString("N");
                        _d["instanceId"] = id;
                        _d["instanceMachine"] = fp;
                        _save?.Kick();
                    }
                    return id;
                }
            }
        }

        static string MachineFingerprint()
        {
            string guid = "";
            try
            {
                using (var hk = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                using (var k = hk.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                    guid = k?.GetValue("MachineGuid") as string ?? "";
            }
            catch { }
            return Hash.Short(Environment.MachineName.ToUpperInvariant() + "|" + guid, 16);
        }

        public string GetStr(string k, string def = "") { lock (_lock) return _d.S(k, def); }
        public int GetInt(string k, int def = 0) { lock (_lock) return _d.I(k, def); }
        public bool GetBool(string k, bool def = false) { lock (_lock) return _d.B(k, def); }

        public List<int> GetInts(string k)
        {
            lock (_lock)
            {
                var a = _d.A(k);
                var r = new List<int>();
                if (a != null) foreach (var x in a) if (x is long l) r.Add((int)l);
                return r;
            }
        }

        public List<string> GetStrs(string k) { lock (_lock) return _d.StrList(k); }

        public JObj GetObj(string k) { lock (_lock) return _d.O(k); }

        public void Set(string k, object v)
        {
            lock (_lock) _d[k] = v;
            _save?.Kick();
        }

        public void SetInts(string k, IEnumerable<int> v) => Set(k, v.Select(x => (object)(long)x).ToList());
        public void SetStrs(string k, IEnumerable<string> v) => Set(k, v.Cast<object>().ToList());

        public void SaveNow()
        {
            if (_path == null) return;
            string text;
            lock (_lock) text = Json.Serialize(_d, true);
            FileUtil.WriteAtomic(_path, text);
        }

        public void Flush() => _save?.Flush();
    }

    /// <summary>피어 목록 (peers.json). 스레드 안전.</summary>
    public sealed class PeerStore
    {
        readonly object _lock = new object();
        readonly List<Peer> _list = new List<Peer>();
        string _path;
        Debouncer _save;

        /// <summary>목록 구조 또는 표시 값이 바뀜 (아무 스레드).</summary>
        public event Action Changed;

        public void Load(string path)
        {
            _path = path;
            _save = new Debouncer(SaveNow, 600);
            try
            {
                if (!File.Exists(path)) return;
                var arr = Json.Parse(File.ReadAllText(path, Encoding.UTF8)) as List<object>;
                if (arr == null) return;
                lock (_lock)
                {
                    foreach (var x in arr)
                        if (x is JObj o)
                        {
                            var p = Peer.FromJson(o);
                            if (!string.IsNullOrWhiteSpace(p.Host) && p.Port > 0) _list.Add(p);
                        }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(Cat.Peer, T("log.peersReadFailed", ex.Message));
            }
        }

        public Peer[] All() { lock (_lock) return _list.ToArray(); }

        public int Count { get { lock (_lock) return _list.Count; } }

        public Peer FindById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_lock) return _list.FirstOrDefault(p => p.Id == id);
        }

        public Peer FindByAddress(string host, int port)
        {
            lock (_lock) return _list.FirstOrDefault(p => p.Host.Eq(host) && p.Port == port);
        }

        public void Add(Peer p)
        {
            lock (_lock) { if (!_list.Contains(p)) _list.Add(p); }
            Touch(true);
        }

        public void Remove(Peer p)
        {
            lock (_lock) _list.Remove(p);
            Touch(true);
        }

        /// <summary>변경 알림 + (persist 면) 저장 예약.</summary>
        public void Touch(bool persist)
        {
            if (persist) _save?.Kick();
            try { Changed?.Invoke(); } catch (Exception ex) { Log.Warn(Cat.Peer, ex.Message); }
        }

        public void SaveNow()
        {
            if (_path == null) return;
            List<object> arr;
            lock (_lock) arr = _list.Select(p => (object)p.ToJson()).ToList();
            FileUtil.WriteAtomic(_path, Json.Serialize(arr, true));
        }

        public void Flush() => _save?.Flush();
    }

    /// <summary>쪽지함 (notes.json). 스레드 안전.</summary>
    public sealed class NoteStore
    {
        readonly object _lock = new object();
        readonly List<Note> _list = new List<Note>();
        string _path;
        Debouncer _save;

        public event Action Changed;

        public void Load(string path, int retentionDays)
        {
            _path = path;
            _save = new Debouncer(SaveNow, 600);
            try
            {
                if (!File.Exists(path)) return;
                var arr = Json.Parse(File.ReadAllText(path, Encoding.UTF8)) as List<object>;
                if (arr == null) return;
                var limit = retentionDays > 0 ? DateTime.Now.AddDays(-retentionDays) : DateTime.MinValue;
                lock (_lock)
                {
                    foreach (var x in arr)
                        if (x is JObj o)
                        {
                            var n = Note.FromJson(o);
                            if (n.Time >= limit || (n.Incoming && !n.IsRead)) _list.Add(n);
                        }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(Cat.Note, T("log.notesReadFailed", ex.Message));
            }
        }

        public Note[] All() { lock (_lock) return _list.ToArray(); }

        public Note Find(string id) { lock (_lock) return _list.FirstOrDefault(n => n.Id == id); }

        public int UnreadCount { get { lock (_lock) return _list.Count(n => n.Incoming && !n.IsRead); } }

        public int UnreadFrom(string peerId)
        {
            lock (_lock) return _list.Count(n => n.Incoming && !n.IsRead && n.PeerId == peerId);
        }

        public void Add(Note n)
        {
            lock (_lock)
            {
                if (_list.Any(x => x.Id == n.Id)) return;
                _list.Add(n);
            }
            Touch();
        }

        public void Remove(IEnumerable<Note> notes)
        {
            var set = new HashSet<Note>(notes);
            lock (_lock) _list.RemoveAll(set.Contains);
            Touch();
        }

        public void Touch()
        {
            _save?.Kick();
            try { Changed?.Invoke(); } catch (Exception ex) { Log.Warn(Cat.Note, ex.Message); }
        }

        public void SaveNow()
        {
            if (_path == null) return;
            List<object> arr;
            lock (_lock) arr = _list.Select(n => (object)n.ToJson()).ToList();
            FileUtil.WriteAtomic(_path, Json.Serialize(arr, true));
        }

        public void Flush() => _save?.Flush();
    }

    /// <summary>내 공유에 들어온 작업 기록 (공유 활동 탭). 최근 500건 유지.</summary>
    public sealed class ActivityLog
    {
        readonly object _lock = new object();
        readonly List<ActivityEntry> _list = new List<ActivityEntry>();
        int _seq;
        public int Version;

        public ActivityEntry Start(string peer, string kind, string action, string path, long size)
        {
            var e = new ActivityEntry { PeerName = peer, Kind = kind, Action = action, Path = path, Size = size };
            lock (_lock)
            {
                e.Id = ++_seq;
                _list.Add(e);
                if (_list.Count > 500) _list.RemoveRange(0, _list.Count - 500);
            }
            Interlocked.Increment(ref Version);
            return e;
        }

        public void Finish(ActivityEntry e, bool ok, string status = null)
        {
            e.Finished = true;
            e.Failed = !ok;
            e.Speed = 0;
            e.Status = status ?? (ok ? T("act.status.done") : T("act.status.failed"));
            Interlocked.Increment(ref Version);
        }

        public void Bump() => Interlocked.Increment(ref Version);

        public ActivityEntry[] Snapshot() { lock (_lock) return _list.ToArray(); }

        public int ActiveCount { get { lock (_lock) return _list.Count(e => !e.Finished); } }

        public void ClearFinished()
        {
            lock (_lock) _list.RemoveAll(e => e.Finished);
            Interlocked.Increment(ref Version);
        }
    }
}
