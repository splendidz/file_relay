using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

using static FileRelay.L;
namespace FileRelay
{
    /// <summary>
    /// HTTP API 라우팅과 처리.
    ///
    ///  공개      GET  /api/hello
    ///  관계      POST /api/register | /api/unregister | /api/ping | /api/goodbye
    ///  공유      GET  /api/shares | /api/list | /api/walk | /api/stat | /api/file
    ///            PUT  /api/file          (올리기 — .frpart 에 쓰고 완료 시 교체)
    ///            POST /api/mkdir | /api/rename | /api/delete | /api/discard
    ///  쪽지      POST /api/note | /api/note/read
    ///
    ///  공유/쪽지 API 는 '서로 등록된(Paired) 피어'만 쓸 수 있다.
    /// </summary>
    public sealed class ApiServer
    {
        readonly AppCore _c;
        const int CopyBuf = 128 * 1024;

        public ApiServer(AppCore c) { _c = c; }

        public void Handle(HttpCtx ctx)
        {
            string path = ctx.Req.Path.TrimEnd('/');
            switch (ctx.Req.Method + " " + path)
            {
                case "GET ":
                case "GET /api/hello": Hello(ctx); return;
                case "POST /api/register": Register(ctx); return;
                case "POST /api/unregister": Unregister(ctx); return;
                case "POST /api/ping": Ping(ctx); return;
                case "POST /api/goodbye": Goodbye(ctx); return;
                case "GET /api/shares": Shares(ctx); return;
                case "GET /api/list": List(ctx); return;
                case "GET /api/walk": Walk(ctx); return;
                case "GET /api/stat": Stat(ctx); return;
                case "GET /api/file": Download(ctx); return;
                case "PUT /api/file": Upload(ctx); return;
                case "POST /api/mkdir": Mkdir(ctx); return;
                case "POST /api/rename": Rename(ctx); return;
                case "POST /api/delete": Delete(ctx); return;
                case "POST /api/discard": Discard(ctx); return;
                case "POST /api/note": NoteIn(ctx); return;
                case "POST /api/note/read": NoteRead(ctx); return;
            }
            ctx.SendError(404, "unknown_request", T("err.unknown_request", ctx.Req.Method + " " + path), new[] { ctx.Req.Method + " " + path });
        }

        // ─── 인증 ──────────────────────────────────────────────────────────
        Caller CheckCaller(HttpCtx ctx)
        {
            var c = Caller.From(ctx.Req);
            if (string.IsNullOrEmpty(c.Id))
                throw ApiException.Of(400, "no_identity");
            if (c.Key != _c.KeyHash)
                throw ApiException.Of(403, "bad_key");
            return c;
        }

        Peer Auth(HttpCtx ctx)
        {
            var c = CheckCaller(ctx);
            var p = _c.Peers.FindById(c.Id);
            if (p == null || p.Blocked || p.State != PeerState.Paired)
                throw ApiException.Of(403, "not_peer");
            _c.PeerSvc.ApplyCaller(p, c);
            ctx.Peer = p;
            return p;
        }

        sealed class Target
        {
            public ShareConfig Share;
            public AccessLevel Access;
            public string Full;
            public string Rel;
            public string Display => PathUtil.Join(Share.Name, Rel);
        }

        Target Resolve(Peer p, string share, string rel, AccessLevel need)
        {
            var s = _c.Config.Shares.FirstOrDefault(x => x.Enabled && x.Name.Eq(share));
            var acc = s?.AccessFor(p) ?? AccessLevel.None;
            if (s == null || acc == AccessLevel.None)
                throw ApiException.Of(404, "no_share", share);
            if (acc < need)
                throw ApiException.Of(403, need == AccessLevel.Full ? "denied_full" : need == AccessLevel.Write ? "denied_write" : "denied_read");
            if (!Directory.Exists(s.Path))
                throw ApiException.Of(404, "share_missing", s.Name);
            return new Target { Share = s, Access = acc, Full = ResolveUnder(s.Path, rel), Rel = PathUtil.Normalize(rel) };
        }

        static readonly char[] BadChars = Path.GetInvalidFileNameChars();

        /// <summary>공유 루트 아래의 실제 경로. '..', 드라이브, 스트림(:) 등으로 벗어나는 경로는 거부.</summary>
        public static string ResolveUnder(string root, string rel)
        {
            string rootFull = Path.GetFullPath(root);
            string rootSep = rootFull.EndsWith("\\") ? rootFull : rootFull + "\\";
            var segs = PathUtil.Segments(rel);
            foreach (var seg in segs)
                if (seg == "." || seg == ".." || seg.IndexOfAny(BadChars) >= 0)
                    throw ApiException.Of(400, "bad_path");
            if (segs.Length == 0) return rootFull;
            string full = Path.GetFullPath(rootSep + string.Join("\\", segs));
            if (!full.StartsWith(rootSep, StringComparison.OrdinalIgnoreCase))
                throw ApiException.Of(400, "outside_share");
            return full;
        }

        bool Visible(FileSystemInfo f)
        {
            if (f.Name.EndsWith(PathUtil.PartExt, StringComparison.OrdinalIgnoreCase)) return false;
            if (_c.Config.ShareHiddenFiles) return true;
            return (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0;
        }

        // ─── 관계 ──────────────────────────────────────────────────────────
        JObj MyInfo() => new JObj
        {
            ["app"] = AppInfo.Name,
            ["proto"] = AppInfo.Protocol,
            ["ver"] = AppInfo.Version,
            ["id"] = _c.MyId,
            ["name"] = _c.Config.UserName,
            ["port"] = _c.Config.Port,
            ["status"] = AccessParse.PresenceKey(_c.MyStatus),
            ["machine"] = Environment.MachineName,
        };

        void Hello(HttpCtx ctx)
        {
            var o = MyInfo();
            string key = ctx.Req.Header(ApiClient.HKey);
            o["keyMatch"] = key == null || key == _c.KeyHash;
            ctx.SendOk(o);
        }

        void Register(HttpCtx ctx)
        {
            var c = CheckCaller(ctx);
            if (c.Id == _c.MyId)
                throw NetUtil.IsLocalAddress(c.Host) || IPAddress.IsLoopback(c.IP)
                    ? ApiException.Of(400, "self")
                    : ApiException.Of(409, "same_id");
            if (c.Port <= 0) throw ApiException.Of(400, "no_port");
            _c.PeerSvc.OnRegister(c, out bool accepted, out bool pending);
            var o = MyInfo();
            o["ok"] = true;
            o["accepted"] = accepted;
            o["pending"] = pending;
            ctx.SendOk(o);
        }

        void Unregister(HttpCtx ctx)
        {
            var c = CheckCaller(ctx);
            var body = ctx.Req.ReadJson();
            _c.PeerSvc.OnUnregister(c, body.S("reason"));
            ctx.SendOk();
        }

        void Ping(HttpCtx ctx)
        {
            var c = CheckCaller(ctx);
            bool known = _c.PeerSvc.OnPing(c, out var p);
            var o = MyInfo();
            o["known"] = known;
            if (known && p != null) o["shares"] = _c.Config.Shares.Count(s => s.Enabled && s.AccessFor(p) > AccessLevel.None);
            ctx.SendOk(o);
        }

        void Goodbye(HttpCtx ctx)
        {
            var c = CheckCaller(ctx);
            _c.PeerSvc.OnGoodbye(c);
            ctx.SendOk();
        }

        // ─── 공유 탐색 ─────────────────────────────────────────────────────
        void Shares(HttpCtx ctx)
        {
            var p = Auth(ctx);
            var arr = new List<object>();
            foreach (var s in _c.Config.Shares)
            {
                if (!s.Enabled) continue;
                var acc = s.AccessFor(p);
                if (acc == AccessLevel.None) continue;
                bool ok = Directory.Exists(s.Path);
                long free = -1, total = -1;
                DateTime mtime = DateTime.MinValue;
                if (ok)
                {
                    free = ShellOps.FreeSpace(s.Path, out total);
                    try { mtime = Directory.GetLastWriteTime(s.Path); } catch { }
                }
                arr.Add(new JObj
                {
                    ["name"] = s.Name,
                    ["access"] = AccessParse.ToKey(acc),
                    ["description"] = s.Description,
                    ["available"] = ok,
                    ["free"] = free,
                    ["total"] = total,
                    ["m"] = TimeUtil.ToUnixMs(mtime),
                });
            }
            ctx.SendOk(new JObj { ["shares"] = arr });
        }

        void List(HttpCtx ctx)
        {
            var p = Auth(ctx);
            var t = Resolve(p, ctx.Req.Q("share"), ctx.Req.Q("path"), AccessLevel.Read);
            if (!Directory.Exists(t.Full)) throw ApiException.Of(404, "no_folder", t.Display);
            var items = new List<object>();
            try
            {
                foreach (var f in new DirectoryInfo(t.Full).EnumerateFileSystemInfos())
                {
                    try
                    {
                        if (!Visible(f)) continue;
                        bool dir = (f.Attributes & FileAttributes.Directory) != 0;
                        items.Add(new JObj
                        {
                            ["n"] = f.Name,
                            ["d"] = dir,
                            ["s"] = dir ? 0L : ((FileInfo)f).Length,
                            ["m"] = TimeUtil.ToUnixMs(f.LastWriteTime),
                            ["h"] = (f.Attributes & FileAttributes.Hidden) != 0,
                        });
                    }
                    catch { }
                }
            }
            catch (UnauthorizedAccessException) { throw ApiException.Of(403, "os_denied_dir"); }
            ctx.SendOk(new JObj
            {
                ["access"] = AccessParse.ToKey(t.Access),
                ["items"] = items,
                ["free"] = ShellOps.FreeSpace(t.Full),
            });
        }

        void Walk(HttpCtx ctx)
        {
            var p = Auth(ctx);
            var t = Resolve(p, ctx.Req.Q("share"), ctx.Req.Q("path"), AccessLevel.Read);
            if (!Directory.Exists(t.Full)) throw ApiException.Of(404, "no_folder", t.Display);
            var files = new List<object>();
            var dirs = new List<object>();
            var stack = new Stack<string>();
            stack.Push(t.Full);
            string baseSep = t.Full.EndsWith("\\") ? t.Full : t.Full + "\\";
            long total = 0;
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                IEnumerable<FileSystemInfo> entries;
                try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
                catch { continue; }
                foreach (var f in entries)
                {
                    if (!Visible(f)) continue;
                    string rel = f.FullName.Substring(baseSep.Length);
                    if ((f.Attributes & FileAttributes.Directory) != 0)
                    {
                        dirs.Add(rel);
                        if ((f.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(f.FullName);
                    }
                    else
                    {
                        long len = ((FileInfo)f).Length;
                        total += len;
                        files.Add(new JObj { ["p"] = rel, ["s"] = len, ["m"] = TimeUtil.ToUnixMs(f.LastWriteTime) });
                    }
                    if (files.Count + dirs.Count > 300_000)
                        throw ApiException.Of(413, "too_many");
                }
            }
            ctx.SendOk(new JObj { ["files"] = files, ["dirs"] = dirs, ["total"] = total });
        }

        void Stat(HttpCtx ctx)
        {
            var p = Auth(ctx);
            var t = Resolve(p, ctx.Req.Q("share"), ctx.Req.Q("path"), AccessLevel.Read);
            var o = new JObj
            {
                ["access"] = AccessParse.ToKey(t.Access),
                ["free"] = ShellOps.FreeSpace(t.Full),
            };
            if (File.Exists(t.Full))
            {
                var fi = new FileInfo(t.Full);
                o["exists"] = true;
                o["isDir"] = false;
                o["size"] = fi.Length;
                o["m"] = TimeUtil.ToUnixMs(fi.LastWriteTime);
            }
            else if (Directory.Exists(t.Full))
            {
                o["exists"] = true;
                o["isDir"] = true;
                o["size"] = 0L;
                o["m"] = TimeUtil.ToUnixMs(Directory.GetLastWriteTime(t.Full));
            }
            else o["exists"] = false;
            string part = t.Full + PathUtil.PartExt;
            o["part"] = File.Exists(part) ? new FileInfo(part).Length : -1L;
            ctx.SendOk(o);
        }

        // ─── 내려받기 ──────────────────────────────────────────────────────
        void Download(HttpCtx ctx)
        {
            var p = Auth(ctx);
            var t = Resolve(p, ctx.Req.Q("share"), ctx.Req.Q("path"), AccessLevel.Read);
            if (!File.Exists(t.Full)) throw ApiException.Of(404, "no_file", t.Display);

            FileStream fs;
            try { fs = new FileStream(t.Full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, CopyBuf, FileOptions.SequentialScan); }
            catch (UnauthorizedAccessException) { throw ApiException.Of(403, "os_denied_file"); }
            catch (IOException ex) { throw ApiException.Of(409, "locked", ex.Message); }

            using (fs)
            {
                long total = fs.Length;
                long start = 0;
                string range = ctx.Req.Header("Range");
                if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    string spec = range.Substring(6).Split(',')[0].Trim();
                    int dash = spec.IndexOf('-');
                    if (dash > 0) long.TryParse(spec.Substring(0, dash), NumberStyles.Integer, CultureInfo.InvariantCulture, out start);
                    if (start < 0 || start > total)
                        throw ApiException.Of(416, "bad_range");
                }
                long length = total - start;
                var headers = new Dictionary<string, string>
                {
                    ["Accept-Ranges"] = "bytes",
                    ["X-FR-Size"] = total.ToString(CultureInfo.InvariantCulture),
                    ["X-FR-MTime"] = TimeUtil.ToUnixMs(File.GetLastWriteTime(t.Full)).ToString(CultureInfo.InvariantCulture),
                };
                int status = 200;
                if (start > 0)
                {
                    status = 206;
                    headers["Content-Range"] = $"bytes {start}-{Math.Max(start, total - 1)}/{total}";
                }
                var act = _c.Activity.Start(p.DisplayName, "download", T(start > 0 ? "act.download.resume" : "act.download"), t.Display, total);
                act.Done = start;
                var output = ctx.BeginBody(status, "application/octet-stream", length, headers);
                fs.Position = start;
                var buf = new byte[CopyBuf];
                var sw = Stopwatch.StartNew();
                long lastMs = 0, lastBytes = start, sent = 0;
                try
                {
                    while (sent < length)
                    {
                        int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, length - sent));
                        if (n <= 0) break;
                        _c.ServeLimiter.Take(n, null);
                        output.Write(buf, 0, n);
                        sent += n;
                        act.Done = start + sent;
                        long ms = sw.ElapsedMilliseconds;
                        if (ms - lastMs >= 500)
                        {
                            act.Speed = (act.Done - lastBytes) * 1000.0 / Math.Max(1, ms - lastMs);
                            lastMs = ms;
                            lastBytes = act.Done;
                            _c.Activity.Bump();
                        }
                    }
                    output.Flush();
                    if (sent < length) { ctx.KeepAlive = false; throw new IOException(T("http.fileShrank")); }
                    _c.Activity.Finish(act, true);
                }
                catch (Exception)
                {
                    _c.Activity.Finish(act, false, T("act.status.aborted"));
                    throw new IOException(T("http.aborted"));
                }
            }
        }

        // ─── 올리기 ────────────────────────────────────────────────────────
        void Upload(HttpCtx ctx)
        {
            var p = Auth(ctx);
            var r = ctx.Req;
            var t = Resolve(p, r.Q("share"), r.Q("path"), AccessLevel.Write);
            if (t.Rel.Length == 0) throw ApiException.Of(400, "root_needs_name");
            string name = Path.GetFileName(t.Full);
            var bad = PathUtil.ValidateName(name);
            if (bad != null) throw new ApiException(400, "bad_name", bad);
            if (Directory.Exists(t.Full)) throw ApiException.Of(409, "is_dir");

            long offset = r.QLong("offset");
            long total = r.QLong("total", -1);
            long mtime = r.QLong("mtime");
            string mode = r.Q("mode") ?? "new";
            if (total < 0 || offset < 0 || offset > total) throw ApiException.Of(400, "bad_args");
            if (r.ContentLength != total - offset) throw ApiException.Of(400, "bad_length");

            string dir = Path.GetDirectoryName(t.Full);
            Directory.CreateDirectory(dir);
            bool exists = File.Exists(t.Full);
            string writePath;

            if (mode == "append")
            {
                if (t.Access < AccessLevel.Full) throw ApiException.Of(403, "denied_append");
                if (!exists) throw ApiException.Of(409, "no_resume_file");
                if (new FileInfo(t.Full).Length != offset) throw ApiException.Of(409, "offset_mismatch");
                writePath = t.Full;
            }
            else
            {
                if (exists && mode != "overwrite") throw ApiException.Of(409, "exists");
                if (exists && t.Access < AccessLevel.Full) throw ApiException.Of(403, "denied_overwrite");
                writePath = t.Full + PathUtil.PartExt;
                if (offset > 0 && (!File.Exists(writePath) || new FileInfo(writePath).Length < offset))
                    throw ApiException.Of(409, "part_mismatch");
            }
            long free = ShellOps.FreeSpace(dir);
            if (free >= 0 && total - offset > free)
                throw ApiException.Of(507, "no_space", Fmt.Size(total - offset), Fmt.Size(free));

            var act = _c.Activity.Start(p.DisplayName, "upload", T(offset > 0 ? "act.upload.resume" : "act.upload.title"), t.Display, total);
            act.Done = offset;
            try
            {
                using (var fs = new FileStream(writePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, CopyBuf))
                {
                    if (writePath != t.Full)
                        try { File.SetAttributes(writePath, File.GetAttributes(writePath) | FileAttributes.Hidden); } catch { }
                    fs.SetLength(offset);
                    fs.Position = offset;
                    var buf = new byte[CopyBuf];
                    var sw = Stopwatch.StartNew();
                    long lastMs = 0, lastBytes = offset;
                    while (true)
                    {
                        int n = r.Body.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        fs.Write(buf, 0, n);
                        act.Done += n;
                        long ms = sw.ElapsedMilliseconds;
                        if (ms - lastMs >= 500)
                        {
                            act.Speed = (act.Done - lastBytes) * 1000.0 / Math.Max(1, ms - lastMs);
                            lastMs = ms;
                            lastBytes = act.Done;
                            _c.Activity.Bump();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _c.Activity.Finish(act, false, T("act.status.aborted"));
                ctx.KeepAlive = false;
                if (ex is IOException) throw;
                throw ApiException.Of(500, "write_failed", ex.Message);
            }

            long got = new FileInfo(writePath).Length;
            if (got != total)
            {
                _c.Activity.Finish(act, false, T("act.status.sizeMismatch"));
                throw ApiException.Of(409, "size_mismatch", got, total);
            }

            if (writePath != t.Full)
            {
                if (File.Exists(t.Full))
                {
                    if (mode != "overwrite") { _c.Activity.Finish(act, false, T("act.status.conflict")); throw ApiException.Of(409, "exists_now"); }
                    RemoveForOverwrite(t.Full);
                }
                File.Move(writePath, t.Full);
                try { File.SetAttributes(t.Full, File.GetAttributes(t.Full) & ~FileAttributes.Hidden); } catch { }
            }
            if (mtime > 0) { try { File.SetLastWriteTime(t.Full, TimeUtil.FromUnixMs(mtime)); } catch { } }
            _c.Activity.Finish(act, true);
            _c.ReportRemoteChange(p, exists ? "overwrite" : "upload", t.Display);
            ctx.SendOk(new JObj { ["ok"] = true, ["size"] = got, ["name"] = name });
        }

        void RemoveForOverwrite(string path)
        {
            try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
            if (_c.Config.RemoteDeleteToRecycleBin && ShellOps.Recycle(new[] { path }, IntPtr.Zero, true, out _) && !File.Exists(path)) return;
            File.Delete(path);
        }

        // ─── 폴더/이름/삭제 ────────────────────────────────────────────────
        void Mkdir(HttpCtx ctx)
        {
            var p = Auth(ctx);
            var body = ctx.Req.ReadJson();
            string share = body.S("share");
            var paths = body.StrList("paths");
            if (body.Has("path")) paths.Add(body.S("path"));
            int created = 0;
            string first = null;
            foreach (var rel in paths)
            {
                var t = Resolve(p, share, rel, AccessLevel.Write);
                if (t.Rel.Length == 0) continue;
                foreach (var seg in PathUtil.Segments(rel))
                {
                    var bad = PathUtil.ValidateName(seg);
                    if (bad != null) throw new ApiException(400, "bad_name", $"'{seg}': {bad}");
                }
                if (File.Exists(t.Full)) throw ApiException.Of(409, "exists_file", t.Display);
                if (!Directory.Exists(t.Full))
                {
                    Directory.CreateDirectory(t.Full);
                    created++;
                    first = first ?? t.Display;
                }
            }
            if (created > 0)
                _c.ReportRemoteChange(p, "mkdir", first, created);
            ctx.SendOk(new JObj { ["ok"] = true, ["created"] = created });
        }

        void Rename(HttpCtx ctx)
        {
            var p = Auth(ctx);
            var body = ctx.Req.ReadJson();
            var t = Resolve(p, body.S("share"), body.S("path"), AccessLevel.Full);
            if (t.Rel.Length == 0) throw ApiException.Of(400, "root_rename");
            string newName = body.S("newName").Trim();
            var bad = PathUtil.ValidateName(newName);
            if (bad != null) throw new ApiException(400, "bad_name", bad);
            string target = ResolveUnder(t.Share.Path, PathUtil.Join(PathUtil.Parent(t.Rel), newName));
            bool isDir = Directory.Exists(t.Full);
            if (!isDir && !File.Exists(t.Full)) throw ApiException.Of(404, "no_target");
            bool caseOnly = string.Equals(t.Full, target, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
                throw ApiException.Of(409, "exists_name");
            if (isDir)
            {
                if (caseOnly)
                {
                    string tmp = t.Full + ".fr_tmp_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                    Directory.Move(t.Full, tmp);
                    Directory.Move(tmp, target);
                }
                else Directory.Move(t.Full, target);
            }
            else File.Move(t.Full, target);
            _c.ReportRemoteChange(p, "rename", t.Display, 1, newName);
            ctx.SendOk();
        }

        void Delete(HttpCtx ctx)
        {
            var p = Auth(ctx);
            var body = ctx.Req.ReadJson();
            string share = body.S("share");
            var rels = body.StrList("paths");
            var fulls = new List<string>();
            var names = new List<string>();
            foreach (var rel in rels)
            {
                var t = Resolve(p, share, rel, AccessLevel.Full);
                if (t.Rel.Length == 0) throw ApiException.Of(400, "root_delete");
                if (File.Exists(t.Full) || Directory.Exists(t.Full)) { fulls.Add(t.Full); names.Add(t.Display); }
            }
            if (fulls.Count == 0) { ctx.SendOk(new JObj { ["ok"] = true, ["deleted"] = 0 }); return; }

            var act = _c.Activity.Start(p.DisplayName, "delete", T("act.delete.title"), names.Count == 1 ? names[0] : T("act.andMore", names[0], names.Count - 1), -1);
            var errors = new List<string>();
            bool recycled = false;
            if (_c.Config.RemoteDeleteToRecycleBin)
            {
                try
                {
                    foreach (var f in fulls.Where(File.Exists))
                        try { File.SetAttributes(f, File.GetAttributes(f) & ~FileAttributes.ReadOnly); } catch { }
                    recycled = ShellOps.Recycle(fulls, IntPtr.Zero, true, out _) && fulls.All(f => !File.Exists(f) && !Directory.Exists(f));
                }
                catch { }
            }
            if (!recycled)
            {
                foreach (var f in fulls)
                {
                    try
                    {
                        if (Directory.Exists(f)) DeleteDirForce(f);
                        else if (File.Exists(f)) { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); }
                    }
                    catch (Exception ex) { errors.Add(Path.GetFileName(f) + ": " + ex.Message); }
                }
            }
            _c.Activity.Finish(act, errors.Count == 0, errors.Count == 0 ? T(recycled ? "act.status.doneRecycled" : "act.status.done") : T("act.status.partial"));
            _c.ReportRemoteChange(p, recycled ? "deleteRecycled" : "delete", names[0], names.Count);
            if (errors.Count > 0)
                throw ApiException.Of(500, "partial", string.Join("\n", errors.Take(10)));
            ctx.SendOk(new JObj { ["ok"] = true, ["deleted"] = fulls.Count, ["recycled"] = recycled });
        }

        static void DeleteDirForce(string dir)
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            Directory.Delete(dir, true);
        }

        void Discard(HttpCtx ctx)
        {
            var p = Auth(ctx);
            var body = ctx.Req.ReadJson();
            var t = Resolve(p, body.S("share"), body.S("path"), AccessLevel.Write);
            string part = t.Full + PathUtil.PartExt;
            try { if (File.Exists(part)) { File.SetAttributes(part, FileAttributes.Normal); File.Delete(part); } } catch { }
            ctx.SendOk();
        }

        // ─── 쪽지 ──────────────────────────────────────────────────────────
        void NoteIn(HttpCtx ctx)
        {
            var p = Auth(ctx);
            var body = ctx.Req.ReadJson(256 * 1024);
            string text = body.S("text");
            if (string.IsNullOrWhiteSpace(text)) throw ApiException.Of(400, "empty");
            if (text.Length > 20000) text = text.Substring(0, 20000);
            var n = new Note
            {
                Id = body.S("id", Guid.NewGuid().ToString("N")),
                Incoming = true,
                PeerId = p.Id,
                PeerName = p.DisplayName,
                Text = text,
                Color = AppConfig.NoteColors.Contains(body.S("color")) ? body.S("color") : "yellow",
                Time = DateTime.Now,
                ReplyTo = body.S("replyTo"),
            };
            _c.OnNoteReceived(n, p);
            ctx.SendOk(new JObj { ["ok"] = true, ["status"] = AccessParse.PresenceKey(_c.MyStatus) });
        }

        void NoteRead(HttpCtx ctx)
        {
            var p = Auth(ctx);
            var body = ctx.Req.ReadJson();
            var n = _c.Notes.Find(body.S("id"));
            if (n != null && !n.Incoming && n.PeerId == p.Id && !n.IsRead)
            {
                n.ReadAt = DateTime.Now;
                _c.Notes.Touch();
                Log.Info(Cat.Note, T("log.noteRead", p.DisplayName, Fmt.OneLine(n.Text, 40)));
            }
            ctx.SendOk();
        }
    }
}
