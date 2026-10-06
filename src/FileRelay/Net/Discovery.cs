using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

using static FileRelay.L;
namespace FileRelay
{
    /// <summary>
    /// LAN 검색 (UDP 브로드캐스트). 공유와는 무관 — 누가 FileRelay 를 켜 두었는지 찾는 용도.
    /// 찾은 뒤 '추가'는 사용자가 직접 한다.
    /// </summary>
    public sealed class Discovery
    {
        const string ReqTag = "FRDISC1";
        const string RespTag = "FRHELLO1";

        readonly AppCore _c;
        UdpClient _udp;
        Thread _thread;
        volatile bool _run;

        public Discovery(AppCore c) { _c = c; }

        public void Start(int port)
        {
            Stop();
            try
            {
                var u = new UdpClient(AddressFamily.InterNetwork);
                u.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                u.Client.Bind(new IPEndPoint(IPAddress.Any, port));
                _udp = u;
                _run = true;
                _thread = new Thread(Loop) { IsBackground = true, Name = "discovery" };
                _thread.Start();
            }
            catch (Exception ex)
            {
                Log.Warn(Cat.Search, T("log.udpFailed", port, ex.Message));
            }
        }

        public void Stop()
        {
            _run = false;
            try { _udp?.Close(); } catch { }
            _udp = null;
        }

        void Loop()
        {
            var u = _udp;
            while (_run && u != null)
            {
                var ep = new IPEndPoint(IPAddress.Any, 0);
                byte[] data;
                try { data = u.Receive(ref ep); }
                catch { if (!_run) break; continue; }
                try
                {
                    string s = Encoding.UTF8.GetString(data);
                    if (!s.StartsWith(ReqTag) || !_c.Config.Discoverable) continue;
                    var o = Json.Parse(s.Substring(ReqTag.Length)) as JObj;
                    if (o == null || o.S("id") == _c.MyId || o.S("key") != _c.KeyHash) continue;
                    var reply = Encoding.UTF8.GetBytes(RespTag + Json.Serialize(new JObj
                    {
                        ["id"] = _c.MyId,
                        ["name"] = _c.Config.UserName,
                        ["port"] = _c.Config.Port,
                        ["status"] = AccessParse.PresenceKey(_c.MyStatus),
                        ["machine"] = Environment.MachineName,
                        ["ver"] = AppInfo.Version,
                    }));
                    u.Send(reply, reply.Length, ep);
                }
                catch { }
            }
        }

        public sealed class Found
        {
            public string Id, Name, Host, Machine, Status;
            public int Port;
        }

        /// <summary>브로드캐스트로 찾기 (블로킹, 약 waitMs).</summary>
        public static List<Found> Search(AppCore c, int waitMs)
        {
            var found = new Dictionary<string, Found>();
            using (var u = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
            {
                u.EnableBroadcast = true;
                var msg = Encoding.UTF8.GetBytes(ReqTag + Json.Serialize(new JObj { ["id"] = c.MyId, ["key"] = c.KeyHash }));
                // 내 포트, 기본 포트, 이미 아는 피어들의 포트로 보낸다 (사무실마다 포트가 다를 수 있음).
                var ports = new[] { c.Config.Port, AppConfig.DefaultPort }
                    .Concat(c.Peers.All().Select(p => p.Port)).Where(p => p > 0).Distinct().Take(16).ToList();
                for (int round = 0; round < 2; round++)
                {
                    foreach (var bc in NetUtil.BroadcastAddresses())
                        foreach (var port in ports)
                            try { u.Send(msg, msg.Length, new IPEndPoint(bc, port)); } catch { }
                    // 같은 PC 의 다른 인스턴스도 찾도록 루프백으로도.
                    foreach (var port in ports.Concat(Enumerable.Range(AppConfig.DefaultPort, 5)).Distinct())
                        if (port != c.Config.Port)
                            try { u.Send(msg, msg.Length, new IPEndPoint(IPAddress.Loopback, port)); } catch { }
                    if (round == 0) Thread.Sleep(150);
                }

                var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
                while (true)
                {
                    int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) break;
                    u.Client.ReceiveTimeout = left;
                    var ep = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data;
                    try { data = u.Receive(ref ep); }
                    catch (SocketException) { break; }
                    try
                    {
                        string s = Encoding.UTF8.GetString(data);
                        if (!s.StartsWith(RespTag)) continue;
                        var o = Json.Parse(s.Substring(RespTag.Length)) as JObj;
                        if (o == null) continue;
                        string id = o.S("id");
                        if (id.Length == 0 || id == c.MyId || found.ContainsKey(id)) continue;
                        found[id] = new Found
                        {
                            Id = id,
                            Name = o.S("name"),
                            Host = ep.Address.ToString(),
                            Port = o.I("port", AppConfig.DefaultPort),
                            Machine = o.S("machine"),
                            Status = o.S("status"),
                        };
                    }
                    catch { }
                }
            }
            return found.Values.OrderBy(f => f.Name, NaturalComparer.Instance).ToList();
        }
    }
}
