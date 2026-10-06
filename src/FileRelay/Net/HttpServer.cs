using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

using static FileRelay.L;
namespace FileRelay
{
    /// <summary>
    /// TcpListener 기반 최소 HTTP/1.1 서버.
    ///  · HttpListener 는 외부 바인딩에 관리자 권한(URL ACL)이 필요해서 쓰지 않는다.
    ///  · keep-alive, Content-Length 본문, Expect: 100-continue 를 지원 (chunked 요청은 미지원 — 우리 클라이언트는 안 씀).
    ///  · 연결당 스레드 1개 (사무실 규모에 충분).
    /// </summary>
    public sealed class MiniHttpServer
    {
        const int IdleTimeoutMs = 120_000;

        TcpListener _listener;
        Thread _accept;
        volatile bool _running;
        readonly HashSet<TcpClient> _clients = new HashSet<TcpClient>();
        readonly object _lock = new object();

        public int Port { get; private set; }
        public bool Running => _running;
        public Action<HttpCtx> Handler;

        public void Start(int port)
        {
            Stop();
            var l = new TcpListener(IPAddress.Any, port);
            l.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
            l.Start(128);
            _listener = l;
            Port = port;
            _running = true;
            _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "http-accept" };
            _accept.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;
            lock (_lock)
            {
                foreach (var c in _clients) { try { c.Close(); } catch { } }
                _clients.Clear();
            }
        }

        void AcceptLoop()
        {
            var l = _listener;
            while (_running && l != null)
            {
                TcpClient c;
                try { c = l.AcceptTcpClient(); }
                catch
                {
                    if (!_running) break;
                    Thread.Sleep(50);
                    continue;
                }
                lock (_lock) _clients.Add(c);
                var t = new Thread(() => Serve(c)) { IsBackground = true, Name = "http-conn" };
                t.Start();
            }
        }

        void Serve(TcpClient c)
        {
            IPAddress remote = IPAddress.None;
            try
            {
                c.NoDelay = true;
                c.ReceiveTimeout = IdleTimeoutMs;
                c.SendTimeout = 60_000;
                c.ReceiveBufferSize = 256 * 1024;
                c.SendBufferSize = 256 * 1024;
                if (c.Client.RemoteEndPoint is IPEndPoint ep)
                    remote = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
                using (var ns = c.GetStream())
                {
                    var input = new HttpInput(ns);
                    while (_running)
                    {
                        HttpRequest req;
                        try { req = HttpRequest.Read(input, remote); }
                        catch (IOException) { break; }
                        catch (SocketException) { break; }
                        catch (Exception ex)
                        {
                            Log.Debug(Cat.Server, T("log.badRequest", ex.Message));
                            break;
                        }
                        if (req == null) break;

                        var ctx = new HttpCtx(req, ns);
                        if (req.Header("Expect").Eq("100-continue"))
                        {
                            var cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                            ns.Write(cont, 0, cont.Length);
                        }
                        try
                        {
                            Handler?.Invoke(ctx);
                            if (!ctx.HeadersSent) ctx.SendError(404, "unknown_request", T("err.unknown_request", ""), new[] { "" });
                        }
                        catch (ApiException ex)
                        {
                            if (!ctx.HeadersSent) { ctx.KeepAlive = false; ctx.SendError(ex.Status == 0 ? 500 : ex.Status, ex.Code, ex.Message, ex.Args); }
                            else break;
                        }
                        catch (IOException) { break; }
                        catch (Exception ex)
                        {
                            Log.Warn(Cat.Server, T("log.requestError", req.Method, req.Path, ex.Message));
                            if (!ctx.HeadersSent) { ctx.KeepAlive = false; ctx.SendError(500, "internal", ex.Message); }
                            else break;
                        }
                        if (!ctx.KeepAlive) break;
                        // 읽지 않은 본문이 남으면 다음 요청을 읽을 수 없다 — 작으면 버리고, 크면 연결을 끊는다.
                        if (!((BodyStream)req.Body).TryDrain(64 * 1024)) break;
                    }
                }
            }
            catch { }
            finally
            {
                lock (_lock) _clients.Remove(c);
                try { c.Close(); } catch { }
            }
        }
    }

    /// <summary>버퍼를 가진 입력 — 헤더를 줄 단위로 읽고, 남은 바이트는 본문으로 넘긴다.</summary>
    sealed class HttpInput
    {
        readonly Stream _s;
        readonly byte[] _buf = new byte[16 * 1024];
        int _pos, _len;

        public HttpInput(Stream s) { _s = s; }

        public string ReadLine(int max)
        {
            var ms = new MemoryStream();
            while (true)
            {
                if (_pos >= _len)
                {
                    _pos = 0;
                    _len = _s.Read(_buf, 0, _buf.Length);
                    if (_len <= 0)
                    {
                        _len = 0;
                        if (ms.Length == 0) return null;
                        break;
                    }
                }
                byte b = _buf[_pos++];
                if (b == (byte)'\n') break;
                ms.WriteByte(b);
                if (ms.Length > max) throw new InvalidDataException(T("http.headerTooLong"));
            }
            var s = Encoding.UTF8.GetString(ms.ToArray());
            return s.EndsWith("\r") ? s.Substring(0, s.Length - 1) : s;
        }

        public int Read(byte[] dst, int off, int count)
        {
            if (_pos < _len)
            {
                int n = Math.Min(count, _len - _pos);
                Buffer.BlockCopy(_buf, _pos, dst, off, n);
                _pos += n;
                return n;
            }
            return _s.Read(dst, off, count);
        }
    }

    /// <summary>Content-Length 만큼만 읽히는 요청 본문 스트림.</summary>
    sealed class BodyStream : Stream
    {
        readonly HttpInput _in;
        long _remaining;

        public BodyStream(HttpInput input, long length) { _in = input; _remaining = Math.Max(0, length); }

        public long Remaining => _remaining;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            int n = _in.Read(buffer, offset, (int)Math.Min(count, _remaining));
            if (n <= 0) throw new IOException(T("http.bodyAborted"));
            _remaining -= n;
            return n;
        }

        public bool TryDrain(long limit)
        {
            if (_remaining == 0) return true;
            if (_remaining > limit) return false;
            var tmp = new byte[8192];
            try { while (_remaining > 0) Read(tmp, 0, tmp.Length); return true; }
            catch { return false; }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public sealed class HttpRequest
    {
        public string Method = "";
        public string Path = "";
        public string Version = "HTTP/1.1";
        public Dictionary<string, string> Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public IPAddress RemoteIP = IPAddress.None;
        public long ContentLength;
        public Stream Body;

        public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
        public string Q(string name) => Query.TryGetValue(name, out var v) ? v : null;

        public long QLong(string name, long def = 0) =>
            long.TryParse(Q(name), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long v) ? v : def;

        public JObj ReadJson(int max = 8 * 1024 * 1024)
        {
            if (ContentLength <= 0) return new JObj();
            if (ContentLength > max) throw ApiException.Of(413, "too_large");
            var buf = new byte[ContentLength];
            int off = 0;
            while (off < buf.Length)
            {
                int n = Body.Read(buf, off, buf.Length - off);
                if (n <= 0) throw new IOException(T("http.bodyTruncated"));
                off += n;
            }
            try { return Json.Parse(Encoding.UTF8.GetString(buf)) as JObj ?? new JObj(); }
            catch (FormatException ex) { throw new ApiException(400, "bad_json", ex.Message); }
        }

        internal static HttpRequest Read(HttpInput input, IPAddress remote)
        {
            string line;
            do
            {
                line = input.ReadLine(8192);
                if (line == null) return null;
            } while (line.Length == 0);

            var parts = line.Split(' ');
            if (parts.Length < 3) throw new InvalidDataException(T("http.badRequestLine"));
            var req = new HttpRequest { Method = parts[0].ToUpperInvariant(), RemoteIP = remote, Version = parts[2] };
            string target = parts[1];
            int q = target.IndexOf('?');
            req.Path = Uri.UnescapeDataString(q >= 0 ? target.Substring(0, q) : target);
            if (q >= 0) ParseQuery(target.Substring(q + 1), req.Query);

            int total = 0;
            while (true)
            {
                string h = input.ReadLine(16384);
                if (h == null) throw new IOException(T("http.eofInHeaders"));
                if (h.Length == 0) break;
                total += h.Length;
                if (total > 64 * 1024) throw new InvalidDataException(T("http.headersTooLarge"));
                int c = h.IndexOf(':');
                if (c <= 0) continue;
                req.Headers[h.Substring(0, c).Trim()] = h.Substring(c + 1).Trim();
            }
            if (req.Header("Transfer-Encoding") != null)
                throw new InvalidDataException(T("http.chunked"));
            long.TryParse(req.Header("Content-Length") ?? "0", out req.ContentLength);
            if (req.ContentLength < 0) req.ContentLength = 0;
            req.Body = new BodyStream(input, req.ContentLength);
            return req;
        }

        static void ParseQuery(string qs, Dictionary<string, string> into)
        {
            foreach (var pair in qs.Split('&'))
            {
                if (pair.Length == 0) continue;
                int e = pair.IndexOf('=');
                string k = e >= 0 ? pair.Substring(0, e) : pair;
                string v = e >= 0 ? pair.Substring(e + 1) : "";
                try { into[Uri.UnescapeDataString(k)] = Uri.UnescapeDataString(v); }
                catch { }
            }
        }
    }

    /// <summary>요청 하나의 처리 컨텍스트 (요청 + 응답 쓰기).</summary>
    public sealed class HttpCtx
    {
        readonly Stream _out;
        public readonly HttpRequest Req;
        public bool HeadersSent { get; private set; }
        public bool KeepAlive = true;
        public int Status;

        /// <summary>인증된 요청자 (ApiServer 가 채움).</summary>
        public Peer Peer;

        public HttpCtx(HttpRequest req, Stream output)
        {
            Req = req;
            _out = output;
            string conn = req.Header("Connection") ?? "";
            if (conn.Eq("close")) KeepAlive = false;
            else if (req.Version.Eq("HTTP/1.0") && !conn.Eq("keep-alive")) KeepAlive = false;
        }

        public void SendJson(int status, object body) =>
            SendBytes(status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(Json.Serialize(body)));

        public void SendOk(JObj body = null) => SendJson(200, body ?? new JObj { ["ok"] = true });

        public void SendError(int status, string code, string message, string[] args = null) =>
            SendJson(status, new JObj
            {
                ["error"] = code ?? "error",
                ["message"] = message ?? "",
                ["args"] = new List<object>(args ?? new string[0]),
            });

        public void SendBytes(int status, string contentType, byte[] body, IDictionary<string, string> headers = null)
        {
            WriteHead(status, contentType, body.LongLength, headers);
            _out.Write(body, 0, body.Length);
            _out.Flush();
        }

        /// <summary>헤더를 보내고 본문 스트림을 돌려준다. 호출자는 정확히 length 바이트를 써야 한다.</summary>
        public Stream BeginBody(int status, string contentType, long length, IDictionary<string, string> headers = null)
        {
            WriteHead(status, contentType, length, headers);
            return _out;
        }

        void WriteHead(int status, string contentType, long length, IDictionary<string, string> headers)
        {
            if (HeadersSent) throw new InvalidOperationException(T("http.headersSent"));
            Status = status;
            var sb = new StringBuilder(256);
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
            sb.Append("Server: FileRelay\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(length).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            sb.Append("Connection: ").Append(KeepAlive ? "keep-alive" : "close").Append("\r\n");
            if (headers != null)
                foreach (var kv in headers) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
            sb.Append("\r\n");
            var b = Encoding.UTF8.GetBytes(sb.ToString());
            _out.Write(b, 0, b.Length);
            HeadersSent = true;
        }

        static string Reason(int s)
        {
            switch (s)
            {
                case 200: return "OK";
                case 206: return "Partial Content";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 409: return "Conflict";
                case 413: return "Payload Too Large";
                case 416: return "Range Not Satisfiable";
                case 500: return "Internal Server Error";
                case 507: return "Insufficient Storage";
                default: return "Status";
            }
        }
    }

    /// <summary>API 오류 — 서버에서는 HTTP 오류 응답으로, 클라이언트에서는 받은 오류로 쓴다.</summary>
    public sealed class ApiException : Exception
    {
        public int Status;      // HTTP 상태 (0 = 네트워크 오류)
        public string Code;
        /// <summary>메시지 인자 — 받는 쪽이 자기 언어로 err.{Code} 를 다시 만들 때 쓴다.</summary>
        public string[] Args = new string[0];

        public ApiException(int status, string code, string message, Exception inner = null)
            : base(message, inner)
        {
            Status = status;
            Code = code ?? "";
        }

        public bool IsNetwork => Status == 0;

        /// <summary>코드(err.{code})로 현재 언어 메시지를 만들고, 인자도 함께 담는다.</summary>
        public static ApiException Of(int status, string code, params object[] args)
        {
            var e = new ApiException(status, code, L.T("err." + code, args));
            e.Args = Array.ConvertAll(args ?? new object[0], a => Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture));
            return e;
        }
    }
}
