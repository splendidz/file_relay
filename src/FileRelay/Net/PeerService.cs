using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using static FileRelay.L;
namespace FileRelay
{
    /// <summary>요청 헤더에서 읽은 상대(호출자) 정보.</summary>
    public sealed class Caller
    {
        public string Id = "", Name = "", Status = "", Key = "", Ver = "", Machine = "";
        public int Port;
        public IPAddress IP = IPAddress.None;
        public string Host => IP == null || IP.Equals(IPAddress.None) ? "" : IP.ToString();

        public static Caller From(HttpRequest r)
        {
            var c = new Caller
            {
                Id = r.Header(ApiClient.HId) ?? "",
                Name = ApiClient.Dec(r.Header(ApiClient.HName)).Trim(),
                Status = r.Header(ApiClient.HStatus) ?? "",
                Key = r.Header(ApiClient.HKey) ?? "",
                Ver = r.Header(ApiClient.HVer) ?? "",
                Machine = ApiClient.Dec(r.Header("X-FR-Machine")),
                IP = r.RemoteIP,
            };
            int.TryParse(r.Header(ApiClient.HPort), out c.Port);
            if (c.Name.Length > 64) c.Name = c.Name.Substring(0, 64);
            return c;
        }
    }

    public sealed class AddResult
    {
        public bool Ok;
        public bool Unreachable;
        public bool Pending;
        public string Message = "";
        public Peer Peer;
    }

    /// <summary>
    /// 피어 관계 관리.
    ///  · 추가: hello → register. 상대는 나를 자동 추가(또는 수락 대기)한다.
    ///  · 하트비트: 주기적으로 ping — 응답의 known 으로 상대가 나를 아직 갖고 있는지 확인.
    ///  · 삭제: unregister 로 상대 목록에서도 나를 지운다 (관계는 양방향).
    ///  · 종료: goodbye 로 즉시 오프라인 표시.
    /// </summary>
    public sealed class PeerService
    {
        readonly AppCore _c;
        readonly object _lock = new object();
        Timer _timer;
        int _busy;
        volatile bool _stopped;

        public PeerService(AppCore c) { _c = c; }

        /// <summary>다른 PC 인데 ID 가 같을 때 (이전 버전에서 데이터 폴더를 복사한 경우).</summary>
        public static string SameIdMessage => T("err.same_id");

        int IntervalMs => _c.Config.HeartbeatSeconds * 1000;

        public void Start()
        {
            _timer = new Timer(_ => Tick(), null, 800, IntervalMs);
        }

        public void UpdateInterval() => _timer?.Change(IntervalMs, IntervalMs);

        public void Stop()
        {
            _stopped = true;
            _timer?.Dispose();
        }

        // ─── 들어온 요청 ───────────────────────────────────────────────────
        /// <summary>상대가 나를 추가함 (POST /api/register).</summary>
        public Peer OnRegister(Caller c, out bool accepted, out bool pending)
        {
            accepted = false;
            pending = false;
            Peer p;
            bool isNew, wasPaired, ask = false;
            lock (_lock)
            {
                p = _c.Peers.FindById(c.Id) ?? MatchAddress(c);
                isNew = p == null;
                if (p != null && p.Blocked)
                    throw ApiException.Of(403, "blocked");
                if (isNew) p = new Peer { Id = c.Id, Host = c.Host, Port = c.Port, AddedAt = DateTime.Now };
                p.Id = c.Id;
                wasPaired = p.State == PeerState.Paired;
                ApplyCaller(p, c);

                if (p.State == PeerState.Paired || p.State == PeerState.Pending || _c.Config.AutoAcceptPeers)
                {
                    // 이미 관계가 있거나, 내가 먼저 추가해 둔(Pending) 상대거나, 자동 수락.
                    p.State = PeerState.Paired;
                    accepted = true;
                }
                else
                {
                    ask = p.State != PeerState.Incoming;   // 이미 묻는 중이면 다시 묻지 않는다
                    p.State = PeerState.Incoming;
                    pending = true;
                }
                if (isNew) _c.Peers.Add(p);
                else _c.Peers.Touch(true);
            }

            string who = $"{p.DisplayName} ({p.Host})";
            if (accepted && !wasPaired)
            {
                Log.Success(Cat.Peer, T(isNew ? "log.peerAutoAdded" : "log.peerConnected", who));
                _c.RaiseNotify(T(isNew ? "notify.peerAutoAdded.title" : "notify.peerConnected.title"),
                    T(isNew ? "notify.peerAutoAdded.text" : "notify.peerConnected.text", who),
                    NotifyKind.Peer);
            }
            if (ask)
            {
                Log.Info(Cat.Peer, T("log.peerRequest", who));
                _c.RaisePeerRequest(p);
            }
            return p;
        }

        Peer MatchAddress(Caller c)
        {
            // ID 가 없는(응답한 적 없는) 대기 항목이나, 재설치로 ID 가 바뀐 같은 주소의 항목.
            return _c.Peers.All().FirstOrDefault(x => x.Host.Eq(c.Host) && x.Port == c.Port &&
                                                      (string.IsNullOrEmpty(x.Id) || _c.Peers.FindById(c.Id) == null));
        }

        /// <summary>하트비트 수신. 반환: 상대가 내 정상 피어인지(known).</summary>
        public bool OnPing(Caller c, out Peer peer)
        {
            peer = _c.Peers.FindById(c.Id);
            if (peer == null || peer.Blocked) return false;
            ApplyCaller(peer, c);
            return peer.State == PeerState.Paired;
        }

        public void OnUnregister(Caller c, string reason)
        {
            var p = _c.Peers.FindById(c.Id);
            if (p == null) return;
            _c.Peers.Remove(p);
            bool rejected = reason == "rejected";
            Log.Info(Cat.Peer, T(rejected ? "log.peerRejected" : "log.peerRemovedMe", p.DisplayName));
            _c.RaiseNotify(T(rejected ? "notify.peerRejected.title" : "notify.peerRemovedMe.title"),
                T(rejected ? "notify.peerRejected.text" : "notify.peerRemovedMe.text", p.DisplayName),
                NotifyKind.Peer);
        }

        public void OnGoodbye(Caller c)
        {
            var p = _c.Peers.FindById(c.Id);
            if (p == null) return;
            p.LastInbound = DateTime.MinValue;
            p.LastOutboundOk = DateTime.MinValue;
            SetPresence(p, Presence.Offline);
        }

        /// <summary>인증된 요청이 들어올 때마다 — 이름/주소/상태 갱신.</summary>
        public void ApplyCaller(Peer p, Caller c)
        {
            bool persist = false;
            if (!string.IsNullOrEmpty(c.Name) && p.Name != c.Name) { p.Name = c.Name; persist = true; }
            if (c.Port > 0 && p.Port != c.Port) { p.Port = c.Port; persist = true; }
            string ip = c.Host;
            if (ip.Length > 0 && !p.Host.Eq(ip) && (p.Host.Length == 0 || IPAddress.TryParse(p.Host, out _)) &&
                !(IPAddress.IsLoopback(c.IP) && p.Host.Eq("localhost")))
            {
                Log.Info(Cat.Peer, T("log.peerAddressChanged", p.DisplayName, p.Host, ip));
                p.Host = ip;
                persist = true;
            }
            if (!string.IsNullOrEmpty(c.Ver)) p.Version = c.Ver;
            if (!string.IsNullOrEmpty(c.Machine)) p.Machine = c.Machine;
            p.LastInbound = DateTime.Now;
            p.LastSeen = DateTime.Now;
            var pres = AccessParse.ParsePresence(c.Status);
            if (pres == Presence.Offline) pres = Presence.Online;
            // 요청마다 불리므로 목록에 보이는 값이 바뀌었을 때만 알린다 (안 그러면 피어 목록이 계속 다시 그려진다).
            SetPresence(p, pres);
            if (persist) _c.Peers.Touch(true);
        }

        /// <summary>접속 상태 변경. 바뀌었으면 true.</summary>
        public bool SetPresence(Peer p, Presence pres)
        {
            var old = p.Presence;
            if (old == pres) return false;
            p.Presence = pres;
            if (pres != Presence.Offline) p.LastSeen = DateTime.Now;
            _c.Peers.Touch(pres == Presence.Offline || old == Presence.Offline);
            if (old == Presence.Offline && pres != Presence.Offline)
            {
                Log.Info(Cat.Peer, T("log.peerOnline", p.DisplayName, p.Address));
                if (_c.Config.NotifyPeerOnline && p.State == PeerState.Paired && !p.Blocked)
                    _c.RaiseNotify(T("notify.peerOnline.title"), T("notify.peerOnline.text", p.DisplayName), NotifyKind.Peer);
            }
            else if (pres == Presence.Offline)
            {
                Log.Info(Cat.Peer, T("log.peerOffline", p.DisplayName));
            }
            return true;
        }

        // ─── 하트비트 ──────────────────────────────────────────────────────
        void Tick()
        {
            if (_stopped || Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                var peers = _c.Peers.All().Where(p => !p.Blocked && p.State != PeerState.Incoming).ToArray();
                Parallel.ForEach(peers, new ParallelOptions { MaxDegreeOfParallelism = 16 }, PingOne);
                // 받기만 하고 보내기는 실패하는 경우(상대 방화벽 등)도 고려해 최근 수신으로 판단.
                var stale = TimeSpan.FromMilliseconds(IntervalMs * 2.5);
                foreach (var p in _c.Peers.All())
                {
                    if (!p.IsOnline) continue;
                    var last = p.LastInbound > p.LastOutboundOk ? p.LastInbound : p.LastOutboundOk;
                    if (DateTime.Now - last > stale) SetPresence(p, Presence.Offline);
                }
            }
            catch (Exception ex) { Log.Debug(Cat.Peer, T("log.heartbeatError", ex.Message)); }
            finally { _busy = 0; }
        }

        public void PingOne(Peer p)
        {
            if (_stopped) return;
            try
            {
                var r = ApiClient.Call(p, "POST", "/api/ping", null, 4000);
                p.LastOutboundOk = DateTime.Now;
                p.FailCount = 0;
                string rid = r.S("id");
                if (rid == _c.MyId)
                {
                    p.LastError = NetUtil.IsLocalAddress(p.Host) && p.Port == _c.Config.Port ? T("peer.selfAddress") : SameIdMessage;
                    SetPresence(p, Presence.Offline);
                    return;
                }
                bool persist = false;
                if (!string.IsNullOrEmpty(rid) && p.Id != rid)
                {
                    var dup = _c.Peers.FindById(rid);
                    if (dup != null && dup != p)
                    {
                        // 같은 상대가 두 번 등록됨 — 주소로 추가한 대기 항목을 지운다.
                        _c.Peers.Remove(p);
                        return;
                    }
                    if (!string.IsNullOrEmpty(p.Id))
                    {
                        Log.Warn(Cat.Peer, T("log.peerReinstalled", p.Address));
                        p.State = PeerState.Pending;
                    }
                    p.Id = rid;
                    persist = true;
                }
                string name = r.S("name");
                if (name.Length > 0 && p.Name != name) { p.Name = name; persist = true; }
                p.Version = r.S("ver", p.Version);
                p.Machine = r.S("machine", p.Machine);
                p.ShareCount = r.I("shares", p.ShareCount);
                p.LastError = "";

                bool known = r.B("known");
                if (!known)
                {
                    if (p.State == PeerState.Pending) TryRegister(p);
                    else if (p.State == PeerState.Paired)
                    {
                        p.State = PeerState.Unpaired;
                        persist = true;
                        Log.Warn(Cat.Peer, T("log.peerUnpaired", p.DisplayName));
                    }
                }
                else if (p.State != PeerState.Paired)
                {
                    p.State = PeerState.Paired;
                    persist = true;
                }
                var pres = AccessParse.ParsePresence(r.S("status"));
                if (pres == Presence.Offline) pres = Presence.Online;
                // 하트비트마다 불리므로 바뀐 것이 있을 때만 알린다.
                SetPresence(p, pres);
                if (persist) _c.Peers.Touch(true);
            }
            catch (ApiException ex)
            {
                p.FailCount++;
                string err = ex.Code == "bad_key" ? T("err.bad_key") : ex.Message;
                bool errChanged = p.LastError != err;
                p.LastError = err;
                var stale = TimeSpan.FromMilliseconds(IntervalMs * 1.5);
                if (ex.Status != 0 || (DateTime.Now - p.LastOutboundOk > stale && DateTime.Now - p.LastInbound > stale))
                {
                    if (!SetPresence(p, Presence.Offline) && errChanged) _c.Peers.Touch(false);
                }
            }
            catch (Exception ex)
            {
                p.LastError = ex.Message;
            }
        }

        void TryRegister(Peer p)
        {
            try
            {
                var r = ApiClient.Call(p, "POST", "/api/register", null, 6000);
                string name = r.S("name");
                if (name.Length > 0) p.Name = name;
                if (string.IsNullOrEmpty(p.Id)) p.Id = r.S("id");
                if (r.B("accepted"))
                {
                    if (p.State != PeerState.Paired) Log.Success(Cat.Peer, T("log.peerPaired", p.DisplayName));
                    p.State = PeerState.Paired;
                }
                else p.State = PeerState.Pending;
                _c.Peers.Touch(true);
            }
            catch (ApiException ex)
            {
                p.LastError = ex.Code == "blocked" ? T("err.blocked") : ex.Message;
            }
            catch (Exception ex)
            {
                p.LastError = ex.Message;
            }
        }

        public void PingAsync(Peer p) => ThreadPool.QueueUserWorkItem(_ => PingOne(p));

        public void BroadcastPresence()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var peers = _c.Peers.All().Where(p => !p.Blocked && p.State != PeerState.Incoming).ToArray();
                Parallel.ForEach(peers, new ParallelOptions { MaxDegreeOfParallelism = 16 }, PingOne);
            });
        }

        // ─── 사용자 조작 ───────────────────────────────────────────────────
        /// <summary>주소로 피어 추가 (블로킹 — 백그라운드에서 호출).</summary>
        public AddResult AddPeer(string host, int port)
        {
            JObj hello;
            try { hello = ApiClient.Call(host, port, "GET", "/api/hello", null, 5000); }
            catch (ApiException ex)
            {
                return new AddResult { Unreachable = ex.IsNetwork, Message = ex.Message };
            }
            if (hello.S("app") != AppInfo.Name)
                return new AddResult { Message = T("peer.notFileRelay") };
            string id = hello.S("id");
            if (id == _c.MyId)
                return new AddResult
                {
                    Message = NetUtil.IsLocalAddress(host) && port == _c.Config.Port
                        ? T("peer.cannotAddSelf")
                        : SameIdMessage,
                };
            if (!hello.B("keyMatch", true))
                return new AddResult { Message = T("peer.keyMismatch") };

            JObj r;
            try { r = ApiClient.Call(host, port, "POST", "/api/register", null, 8000); }
            catch (ApiException ex)
            {
                return new AddResult { Message = ex.Code == "blocked" ? T("err.blocked") : ex.Message };
            }

            Peer p;
            bool isNew;
            lock (_lock)
            {
                p = _c.Peers.FindById(id) ?? _c.Peers.FindByAddress(host, port);
                isNew = p == null;
                if (isNew) p = new Peer { AddedAt = DateTime.Now };
                p.Id = id;
                p.Host = host;
                p.Port = port;
                p.Name = r.S("name", hello.S("name"));
                p.Blocked = false;
                p.Version = hello.S("ver");
                p.Machine = hello.S("machine");
                p.State = r.B("accepted") ? PeerState.Paired : PeerState.Pending;
                p.LastOutboundOk = DateTime.Now;
                p.LastError = "";
                if (isNew) _c.Peers.Add(p);
                else _c.Peers.Touch(true);
            }
            var pres = AccessParse.ParsePresence(r.S("status", hello.S("status")));
            SetPresence(p, pres == Presence.Offline ? Presence.Online : pres);
            Log.Success(Cat.Peer, T(p.State == PeerState.Pending ? "log.peerAddedPending" : "log.peerAdded", p.DisplayName, p.Address));
            return new AddResult { Ok = true, Pending = p.State == PeerState.Pending, Peer = p };
        }

        /// <summary>응답이 없는 주소를 대기 상태로 추가 — 상대가 켜지면 자동으로 연결된다.</summary>
        public Peer AddPending(string host, int port)
        {
            var p = _c.Peers.FindByAddress(host, port);
            if (p != null) return p;
            p = new Peer { Host = host, Port = port, State = PeerState.Pending, AddedAt = DateTime.Now };
            _c.Peers.Add(p);
            Log.Info(Cat.Peer, T("log.peerAddedWaiting", p.Address));
            return p;
        }

        public void RemovePeer(Peer p, string reason = "removed")
        {
            _c.Peers.Remove(p);
            Log.Info(Cat.Peer, T("log.peerRemoved", p.DisplayName));
            if (string.IsNullOrEmpty(p.Id)) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { ApiClient.Call(p, "POST", "/api/unregister", new JObj { ["reason"] = reason }, 3000); }
                catch { }
            });
        }

        public void Reconnect(Peer p)
        {
            p.State = PeerState.Pending;
            p.Blocked = false;
            _c.Peers.Touch(true);
            ThreadPool.QueueUserWorkItem(_ => { TryRegister(p); PingOne(p); });
        }

        public void AcceptIncoming(Peer p)
        {
            p.State = PeerState.Paired;
            _c.Peers.Touch(true);
            Log.Success(Cat.Peer, T("log.peerAccepted", p.DisplayName));
            ThreadPool.QueueUserWorkItem(_ => { TryRegister(p); PingOne(p); });
        }

        public void RejectIncoming(Peer p) => RemovePeer(p, "rejected");

        public void SetBlocked(Peer p, bool blocked)
        {
            p.Blocked = blocked;
            if (blocked) SetPresence(p, Presence.Offline);
            _c.Peers.Touch(true);
            Log.Info(Cat.Peer, T(blocked ? "log.peerBlocked" : "log.peerUnblocked", p.DisplayName));
            if (!blocked) PingAsync(p);
        }

        public void SayGoodbye()
        {
            var peers = _c.Peers.All().Where(p => p.IsOnline && !p.Blocked && !string.IsNullOrEmpty(p.Id)).ToArray();
            if (peers.Length == 0) return;
            var tasks = peers.Select(p => Task.Run(() =>
            {
                try { ApiClient.Call(p, "POST", "/api/goodbye", null, 1500); } catch { }
            })).ToArray();
            Task.WaitAll(tasks, 2000);
        }

        /// <summary>설정의 bootstrapPeers 를 자동 추가 (없는 주소만).</summary>
        public void ConnectBootstrap()
        {
            var list = _c.Config.BootstrapPeers.ToList();
            if (list.Count == 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                foreach (var addr in list)
                {
                    if (!NetUtil.TryParseAddress(addr, _c.Config.Port, out var host, out var port)) continue;
                    if (_c.Peers.FindByAddress(host, port) != null) continue;
                    var r = AddPeer(host, port);
                    if (!r.Ok && r.Unreachable) AddPending(host, port);
                }
            });
        }
    }
}
