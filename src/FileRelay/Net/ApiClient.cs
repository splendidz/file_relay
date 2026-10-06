using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

using static FileRelay.L;
namespace FileRelay
{
    /// <summary>
    /// 피어 API 호출 (HttpWebRequest). 모든 요청에 내 신원 헤더를 붙인다.
    /// 시스템 프록시는 쓰지 않는다 — 사내 프록시가 LAN 주소를 가로채는 일을 막는다.
    /// </summary>
    public static class ApiClient
    {
        public const string HId = "X-FR-Id";
        public const string HName = "X-FR-Name";
        public const string HPort = "X-FR-Port";
        public const string HStatus = "X-FR-Status";
        public const string HKey = "X-FR-Key";
        public const string HVer = "X-FR-Ver";

        /// <summary>내 신원 헤더 공급자 (AppCore 가 설정).</summary>
        public static Func<IDictionary<string, string>> Identity = () => new Dictionary<string, string>();

        static ApiClient()
        {
            ServicePointManager.DefaultConnectionLimit = 64;
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.UseNagleAlgorithm = false;
            // 서버 유휴 타임아웃(120초)보다 짧게 — 끊긴 연결을 재사용하는 일을 막는다.
            ServicePointManager.MaxServicePointIdleTime = 45_000;
        }

        public static string Url(string host, int port, string pathAndQuery)
        {
            string h = host;
            if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6) h = "[" + host + "]";
            return "http://" + h + ":" + port.ToString(CultureInfo.InvariantCulture) + pathAndQuery;
        }

        public static string Q(params object[] kv)
        {
            var sb = new StringBuilder();
            for (int i = 0; i + 1 < kv.Length; i += 2)
            {
                sb.Append(sb.Length == 0 ? '?' : '&');
                sb.Append(Uri.EscapeDataString(Convert.ToString(kv[i], CultureInfo.InvariantCulture)));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(Convert.ToString(kv[i + 1], CultureInfo.InvariantCulture) ?? ""));
            }
            return sb.ToString();
        }

        public static HttpWebRequest Create(string host, int port, string method, string pathAndQuery, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(Url(host, port, pathAndQuery));
            req.Method = method;
            req.Proxy = null;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = Math.Max(timeoutMs, 30_000);
            req.KeepAlive = true;
            req.AllowAutoRedirect = false;
            req.UserAgent = "FileRelay/" + AppInfo.Version;
            req.ServicePoint.Expect100Continue = false;
            foreach (var kv in Identity()) req.Headers[kv.Key] = kv.Value;
            return req;
        }

        public static HttpWebRequest Create(Peer p, string method, string pathAndQuery, int timeoutMs) =>
            Create(p.Host, p.Port, method, pathAndQuery, timeoutMs);

        public static JObj Call(string host, int port, string method, string pathAndQuery, JObj body = null, int timeoutMs = 8000)
        {
            HttpWebRequest req;
            try { req = Create(host, port, method, pathAndQuery, timeoutMs); }
            catch (Exception ex) { throw new ApiException(0, "bad_address", T("err.bad_address", ex.Message), ex); }
            try
            {
                if (body != null)
                {
                    var bytes = Encoding.UTF8.GetBytes(Json.Serialize(body));
                    req.ContentType = "application/json; charset=utf-8";
                    req.ContentLength = bytes.Length;
                    using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                }
                else if (method == "POST" || method == "PUT")
                {
                    req.ContentLength = 0;
                }
                using (var resp = (HttpWebResponse)req.GetResponse())
                    return ReadJson(resp);
            }
            catch (WebException ex) { throw Translate(ex); }
            catch (IOException ex) { throw new ApiException(0, "network", T("net.dropped") + " " + ex.Message, ex); }
            catch (ApiException) { throw; }
            catch (Exception ex) { throw new ApiException(0, "network", ex.Message, ex); }
        }

        public static JObj Call(Peer p, string method, string pathAndQuery, JObj body = null, int timeoutMs = 8000) =>
            Call(p.Host, p.Port, method, pathAndQuery, body, timeoutMs);

        public static JObj ReadJson(HttpWebResponse resp)
        {
            using (var s = resp.GetResponseStream())
            using (var r = new StreamReader(s, Encoding.UTF8))
            {
                string text = r.ReadToEnd();
                try { return Json.Parse(text) as JObj ?? new JObj(); }
                catch (FormatException) { throw new ApiException((int)resp.StatusCode, "bad_response", T("err.bad_response")); }
            }
        }

        public static ApiException Translate(WebException ex)
        {
            if (ex.Response is HttpWebResponse r)
            {
                using (r)
                {
                    try
                    {
                        var o = ReadJson(r);
                        string code = o.S("error", "http");
                        string msg = o.S("message");
                        var args = o.StrList("args");
                        // 받는 쪽(나)의 언어로 다시 만든다. 인자가 필요한데 없으면(이전 버전 상대) 상대가 보낸 문장 사용.
                        string tmpl = L.Has("err." + code) ? T("err." + code) : null;
                        if (tmpl != null && (args.Count > 0 || !tmpl.Contains("{0}")))
                            msg = T("err." + code, args.ConvertAll(a => (object)a).ToArray());
                        if (string.IsNullOrEmpty(msg)) msg = $"HTTP {(int)r.StatusCode} {r.StatusDescription}";
                        return new ApiException((int)r.StatusCode, code, msg, ex) { Args = args.ToArray() };
                    }
                    catch (ApiException ae) { return ae; }
                    catch { return new ApiException((int)r.StatusCode, "http", $"HTTP {(int)r.StatusCode} {r.StatusDescription}", ex); }
                }
            }
            return new ApiException(0, "network", NetworkMessage(ex), ex);
        }

        public static string NetworkMessage(WebException ex)
        {
            switch (ex.Status)
            {
                case WebExceptionStatus.ConnectFailure:
                    return T("net.connectFailed");
                case WebExceptionStatus.Timeout:
                    // 연결 시간 초과는 대부분 상대 PC 의 방화벽이 패킷을 버리는 경우다.
                    return T("net.timeout");
                case WebExceptionStatus.NameResolutionFailure:
                    return T("net.nameResolution");
                case WebExceptionStatus.RequestCanceled:
                    return T("net.canceled");
                case WebExceptionStatus.ConnectionClosed:
                case WebExceptionStatus.KeepAliveFailure:
                case WebExceptionStatus.ReceiveFailure:
                case WebExceptionStatus.SendFailure:
                    return T("net.dropped");
                default:
                    return ex.Message;
            }
        }

        /// <summary>신원 헤더 값 인코딩 (헤더는 ASCII 만 안전).</summary>
        public static string Enc(string s) => Uri.EscapeDataString(s ?? "");

        public static string Dec(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try { return Uri.UnescapeDataString(s); } catch { return s; }
        }
    }

    public static class AppInfo
    {
        public const string Name = "FileRelay";
        public const int Protocol = 1;
        public static readonly string Version =
            typeof(AppInfo).Assembly.GetName().Version is Version v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";
    }

    public static class NetUtil
    {
        /// <summary>내 PC 의 사용 가능한 IPv4 주소 (가상/루프백 제외, 게이트웨이 있는 NIC 우선).</summary>
        public static List<string> LocalIPv4()
        {
            var good = new List<string>();
            var other = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    var props = ni.GetIPProperties();
                    bool hasGw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                    string desc = (ni.Description + " " + ni.Name).ToLowerInvariant();
                    bool virt = desc.Contains("virtual") || desc.Contains("vmware") || desc.Contains("hyper-v") || desc.Contains("vethernet") || desc.Contains("loopback") || desc.Contains("wsl");
                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(ua.Address)) continue;
                        string s = ua.Address.ToString();
                        if (s.StartsWith("169.254.")) continue;
                        if (hasGw && !virt) good.Add(s); else other.Add(s);
                    }
                }
            }
            catch { }
            good.AddRange(other.Where(o => !good.Contains(o)));
            return good;
        }

        static string _primary;
        static DateTime _primaryAt = DateTime.MinValue;

        /// <summary>대표 IPv4 (30초 캐시 — 화면 그리기에서 자주 부른다).</summary>
        public static string PrimaryIPv4()
        {
            if (_primary == null || (DateTime.UtcNow - _primaryAt).TotalSeconds > 30)
            {
                _primary = LocalIPv4().FirstOrDefault() ?? "127.0.0.1";
                _primaryAt = DateTime.UtcNow;
            }
            return _primary;
        }

        /// <summary>서브넷 브로드캐스트 주소들 (+ 255.255.255.255).</summary>
        public static List<IPAddress> BroadcastAddresses()
        {
            var list = new List<IPAddress> { IPAddress.Broadcast };
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        var ip = ua.Address.GetAddressBytes();
                        var mask = ua.IPv4Mask.GetAddressBytes();
                        if (mask.All(b => b == 0)) continue;
                        var bc = new byte[4];
                        for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                        var addr = new IPAddress(bc);
                        if (!list.Contains(addr)) list.Add(addr);
                    }
                }
            }
            catch { }
            return list;
        }

        public static bool IsLocalAddress(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            if (host.Eq("localhost") || host == "127.0.0.1") return true;
            return LocalIPv4().Contains(host);
        }

        /// <summary>"호스트", "호스트:포트" 를 나눈다.</summary>
        public static bool TryParseAddress(string text, int defPort, out string host, out int port)
        {
            host = "";
            port = defPort;
            text = (text ?? "").Trim();
            if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) text = text.Substring(7);
            text = text.TrimEnd('/');
            if (text.Length == 0) return false;
            int c = text.LastIndexOf(':');
            if (c > 0 && text.IndexOf(':') == c)
            {
                if (!int.TryParse(text.Substring(c + 1), out port) || port < 1 || port > 65535) return false;
                text = text.Substring(0, c);
            }
            host = text.Trim();
            return host.Length > 0 && host.IndexOfAny(new[] { ' ', '/', '\\' }) < 0;
        }
    }

    /// <summary>
    /// 토큰 버킷 속도 제한 (여러 스레드 공용). '빚' 방식이라 청크가 제한보다 커도 동작한다.
    /// </summary>
    public sealed class RateLimiter
    {
        readonly object _lock = new object();
        double _tokens;
        long _last = DateTime.UtcNow.Ticks;

        /// <summary>초당 바이트 (0 = 무제한).</summary>
        public long BytesPerSecond;

        public void Take(int bytes, Func<bool> cancelled)
        {
            while (true)
            {
                long rate = BytesPerSecond;
                if (rate <= 0) return;
                int waitMs;
                lock (_lock)
                {
                    long now = DateTime.UtcNow.Ticks;
                    double sec = (now - _last) / (double)TimeSpan.TicksPerSecond;
                    _last = now;
                    _tokens = Math.Min(_tokens + sec * rate, rate * 0.5);
                    if (_tokens > 0)
                    {
                        _tokens -= bytes;
                        return;
                    }
                    waitMs = (int)Math.Ceiling(-_tokens * 1000.0 / rate);
                }
                Thread.Sleep(Num.Clamp(waitMs, 5, 200));
                if (cancelled != null && cancelled()) return;
            }
        }
    }
}
