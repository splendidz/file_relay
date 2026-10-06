using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using static FileRelay.L;
namespace FileRelay
{
    public enum TDir { Download, Upload }
    public enum TStatus { Queued, Active, Done, Failed, Canceled, Skipped }

    public sealed class TransferItem
    {
        public int Id;
        public int Batch;
        public int Run;
        public TDir Dir;
        public string PeerId = "";
        public string PeerName = "";
        public string LocalPath = "";
        public string Share = "";
        public string RemotePath = "";      // 공유 안의 경로
        public long Size;
        public DateTime SrcTime;
        public long Done;
        public double Speed;
        public volatile TStatus Status = TStatus.Queued;
        public string Error = "";
        public string Note = "";
        public DateTime Added = DateTime.Now, Started, Finished, NotBefore;
        public int Attempts;
        public bool OpenAfter;              // 받은 뒤 열기 (원격 파일 열기)
        public bool ForceOverwrite;         // 충돌 확인 없이 덮어쓰기 (임시 폴더용)

        internal volatile bool CancelFlag, PauseFlag;
        internal volatile HttpWebRequest Req;

        public string Name => Dir == TDir.Download ? Path.GetFileName(LocalPath) : PathUtil.Name(RemotePath);
        public string RemoteDisplay => PathUtil.Join(Share, RemotePath);
        public bool IsFinished => Status >= TStatus.Done;
        public double Percent => Size > 0 ? Math.Min(100, Done * 100.0 / Size) : (Status == TStatus.Done ? 100 : 0);

        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case TStatus.Queued: return string.IsNullOrEmpty(Note) ? T("xfer.st.queued") : Note;
                    case TStatus.Active: return string.IsNullOrEmpty(Note) ? T("xfer.st.active") : Note;
                    case TStatus.Done: return string.IsNullOrEmpty(Note) ? T("xfer.st.done") : T("xfer.st.doneNote", Note);
                    case TStatus.Failed: return T("xfer.st.failed", Error);
                    case TStatus.Canceled: return T("xfer.st.canceled");
                    case TStatus.Skipped: return string.IsNullOrEmpty(Note) ? T("xfer.st.skipped") : T("xfer.st.skippedNote", Note);
                }
                return "";
            }
        }
    }

    /// <summary>충돌(같은 이름 존재) 질문 — UI 가 Answer/ApplyAll 을 채운다.</summary>
    public sealed class ConflictRequest
    {
        public TransferItem Item;
        public long TargetSize;
        public DateTime TargetTime;
        public bool CanOverwrite;
        public bool CanResume;
        public string Answer = "skip";
        public bool ApplyAll;
        public bool CancelAll;
    }

    public sealed class TransferStats
    {
        public int Active, Queued, Done, Failed, Canceled, Skipped;
        public long TotalBytes, DoneBytes;
        public double Speed;
        public bool Busy => Active + Queued > 0;
        public double Percent => TotalBytes > 0 ? DoneBytes * 100.0 / TotalBytes : 0;
        public double Eta => Speed > 1 ? (TotalBytes - DoneBytes) / Speed : -1;
    }

    sealed class SkipException : Exception
    {
        public SkipException(string note) : base(note) { }
    }

    /// <summary>전송 도중 연결이 끊김 (재시도 대상).</summary>
    sealed class ConnectionLostException : IOException
    {
        public ConnectionLostException() : base(L.T("xfer.connectionLost")) { }
    }

    /// <summary>
    /// 전송 대기열. 워커 스레드들이 대기 항목을 꺼내 처리한다.
    ///  · 받는 쪽은 'name.frpart' 에 쓰고, 다 받으면 원래 이름으로 바꾼다 → 끊겨도 자동 이어받기.
    ///  · 일시 정지 = 진행 중 연결을 끊고 대기로 되돌림 (임시 파일 유지) → 재개 시 이어서.
    ///  · 취소 = 임시 파일 삭제. 실패(네트워크) = 임시 파일 유지 + 자동 재시도.
    /// </summary>
    public sealed class TransferManager
    {
        const int MaxWorkers = 8;
        const int BufSize = 128 * 1024;

        readonly AppCore _c;
        readonly object _lock = new object();
        readonly List<TransferItem> _items = new List<TransferItem>();
        readonly Dictionary<int, int[]> _batches = new Dictionary<int, int[]>(); // batch → [남은 수, 완료, 실패]
        readonly object _askLock = new object();
        int _seq, _batchSeq, _run;
        volatile bool _stop, _paused;
        string _applyAll;
        public int Version;

        /// <summary>충돌 질문 (워커 스레드에서 호출 — 구현은 UI 에 동기 호출).</summary>
        public Func<ConflictRequest, ConflictRequest> AskConflict;
        /// <summary>항목 종료 (워커 스레드).</summary>
        public event Action<TransferItem> ItemFinished;
        /// <summary>묶음 종료: batch, 완료 수, 실패 수 (워커 스레드).</summary>
        public event Action<int, int, int> BatchFinished;

        public TransferManager(AppCore c) { _c = c; }

        public bool Paused => _paused;

        public void Start()
        {
            for (int i = 0; i < MaxWorkers; i++)
                new Thread(WorkerLoop) { IsBackground = true, Name = "xfer-" + i }.Start();
        }

        public void Stop()
        {
            _stop = true;
            TransferItem[] active;
            lock (_lock)
            {
                active = _items.Where(i => i.Status == TStatus.Active).ToArray();
                Monitor.PulseAll(_lock);
            }
            foreach (var it in active) { it.PauseFlag = true; Abort(it); }
        }

        void Bump() => Interlocked.Increment(ref Version);

        public void Kick()
        {
            lock (_lock) Monitor.PulseAll(_lock);
        }

        public int Enqueue(IList<TransferItem> items)
        {
            if (items.Count == 0) return 0;
            int batch;
            lock (_lock)
            {
                bool busy = _items.Any(i => i.Status == TStatus.Queued || i.Status == TStatus.Active);
                if (!busy) { _run++; _applyAll = null; }
                batch = ++_batchSeq;
                foreach (var it in items)
                {
                    it.Id = ++_seq;
                    it.Batch = batch;
                    it.Run = _run;
                    it.Status = TStatus.Queued;
                    _items.Add(it);
                }
                _batches[batch] = new[] { items.Count, 0, 0 };
                Monitor.PulseAll(_lock);
            }
            Bump();
            return batch;
        }

        public TransferItem[] Snapshot() { lock (_lock) return _items.ToArray(); }

        public TransferStats Stats()
        {
            var s = new TransferStats();
            lock (_lock)
            {
                foreach (var i in _items)
                {
                    switch (i.Status)
                    {
                        case TStatus.Active: s.Active++; s.Speed += i.Speed; break;
                        case TStatus.Queued: s.Queued++; break;
                        case TStatus.Done: s.Done++; break;
                        case TStatus.Failed: s.Failed++; break;
                        case TStatus.Canceled: s.Canceled++; break;
                        case TStatus.Skipped: s.Skipped++; break;
                    }
                    if (i.Run == _run && (i.Status == TStatus.Active || i.Status == TStatus.Queued || i.Status == TStatus.Done))
                    {
                        s.TotalBytes += i.Size;
                        s.DoneBytes += i.Status == TStatus.Done ? i.Size : Math.Min(i.Done, i.Size);
                    }
                }
            }
            return s;
        }

        public void SetPaused(bool paused)
        {
            _paused = paused;
            TransferItem[] active = Array.Empty<TransferItem>();
            lock (_lock)
            {
                if (paused) active = _items.Where(i => i.Status == TStatus.Active).ToArray();
                Monitor.PulseAll(_lock);
            }
            foreach (var it in active) { it.PauseFlag = true; Abort(it); }
            Log.Info(Cat.Xfer, T(paused ? "log.xferPaused" : "log.xferResumed"));
            Bump();
        }

        public void Cancel(IEnumerable<TransferItem> items)
        {
            var finished = new List<TransferItem>();
            lock (_lock)
            {
                foreach (var it in items)
                {
                    if (it.Status == TStatus.Queued)
                    {
                        it.Status = TStatus.Canceled;
                        it.Finished = DateTime.Now;
                        it.Note = "";
                        finished.Add(it);
                    }
                    else if (it.Status == TStatus.Active)
                    {
                        it.CancelFlag = true;
                        Abort(it);
                    }
                }
            }
            foreach (var it in finished) OnFinished(it);
            Bump();
        }

        public void CancelAll()
        {
            Cancel(Snapshot().Where(i => !i.IsFinished));
        }

        public void Retry(IEnumerable<TransferItem> items)
        {
            lock (_lock)
            {
                bool busy = _items.Any(i => i.Status == TStatus.Queued || i.Status == TStatus.Active);
                if (!busy) { _run++; _applyAll = null; }
                foreach (var it in items)
                {
                    if (it.Status != TStatus.Failed && it.Status != TStatus.Canceled) continue;
                    it.Status = TStatus.Queued;
                    it.Error = "";
                    it.Note = "";
                    it.Attempts = 0;
                    it.Done = 0;
                    it.Run = _run;
                    it.NotBefore = DateTime.MinValue;
                    if (_batches.TryGetValue(it.Batch, out var b)) b[0]++;
                }
                Monitor.PulseAll(_lock);
            }
            Bump();
        }

        public void Remove(Func<TransferItem, bool> pred)
        {
            lock (_lock) _items.RemoveAll(i => i.IsFinished && pred(i));
            Bump();
        }

        /// <summary>대기 항목을 맨 앞으로.</summary>
        public void Prioritize(IEnumerable<TransferItem> items)
        {
            lock (_lock)
            {
                var sel = items.Where(i => i.Status == TStatus.Queued).ToList();
                foreach (var i in sel) _items.Remove(i);
                int at = _items.FindIndex(i => i.Status == TStatus.Queued);
                _items.InsertRange(at < 0 ? _items.Count : at, sel);
            }
            Bump();
        }

        static void Abort(TransferItem it)
        {
            try { it.Req?.Abort(); } catch { }
        }

        // ─── 워커 ──────────────────────────────────────────────────────────
        void WorkerLoop()
        {
            while (!_stop)
            {
                TransferItem it = null;
                lock (_lock)
                {
                    if (!_paused && _items.Count(i => i.Status == TStatus.Active) < _c.Config.MaxConcurrentTransfers)
                    {
                        var now = DateTime.Now;
                        it = _items.FirstOrDefault(i => i.Status == TStatus.Queued && i.NotBefore <= now);
                        if (it != null)
                        {
                            it.Status = TStatus.Active;
                            it.Started = now;
                            it.CancelFlag = false;
                            it.PauseFlag = false;
                            it.Note = "";
                            it.Speed = 0;
                        }
                    }
                    if (it == null)
                    {
                        Monitor.Wait(_lock, 700);
                        continue;
                    }
                }
                Bump();
                Process(it);
                Bump();
                lock (_lock) Monitor.PulseAll(_lock);
            }
        }

        void Process(TransferItem it)
        {
            it.Attempts++;
            try
            {
                if (it.Dir == TDir.Download) DoDownload(it);
                else DoUpload(it);
                it.Status = TStatus.Done;
            }
            catch (SkipException sk)
            {
                it.Status = TStatus.Skipped;
                it.Note = sk.Message;
            }
            catch (Exception ex)
            {
                if (it.PauseFlag && !it.CancelFlag)
                {
                    it.Status = TStatus.Queued;
                    it.Speed = 0;
                    it.Note = _stop ? "" : T("xfer.note.paused");
                    it.Req = null;
                    return;
                }
                if (it.CancelFlag)
                {
                    it.Status = TStatus.Canceled;
                    CleanupCanceled(it);
                }
                else
                {
                    string msg = Describe(ex);
                    if (IsNetwork(ex) && it.Attempts <= _c.Config.RetryCount && !_stop)
                    {
                        it.Status = TStatus.Queued;
                        it.NotBefore = DateTime.Now.AddSeconds(3);
                        it.Note = T("xfer.note.retryWait", it.Attempts, _c.Config.RetryCount);
                        it.Error = msg;
                        it.Speed = 0;
                        it.Req = null;
                        Log.Warn(Cat.Xfer, T("log.xferRetry", it.Name, msg));
                        return;
                    }
                    it.Status = TStatus.Failed;
                    it.Error = msg;
                }
            }
            it.Finished = DateTime.Now;
            it.Speed = 0;
            it.Req = null;
            OnFinished(it);
        }

        void OnFinished(TransferItem it)
        {
            bool down = it.Dir == TDir.Download;
            switch (it.Status)
            {
                case TStatus.Done:
                    Log.Success(Cat.Xfer, T(down ? "log.downloadDone" : "log.uploadDone", it.Name, Fmt.Size(it.Size), it.PeerName));
                    break;
                case TStatus.Failed:
                    Log.Error(Cat.Xfer, T(down ? "log.downloadFailed" : "log.uploadFailed", it.Name, it.Error));
                    break;
                case TStatus.Skipped:
                    Log.Info(Cat.Xfer, string.IsNullOrEmpty(it.Note) ? T("log.xferSkipped", it.Name) : T("log.xferSkippedNote", it.Name, it.Note));
                    break;
                case TStatus.Canceled:
                    Log.Info(Cat.Xfer, T("log.xferCanceled", it.Name));
                    break;
            }
            try { ItemFinished?.Invoke(it); } catch { }

            int[] b = null;
            bool batchDone = false, idle;
            lock (_lock)
            {
                if (_batches.TryGetValue(it.Batch, out b))
                {
                    b[0]--;
                    if (it.Status == TStatus.Done) b[1]++;
                    if (it.Status == TStatus.Failed) b[2]++;
                    if (b[0] <= 0) { batchDone = true; _batches.Remove(it.Batch); }
                }
                idle = !_items.Any(i => i.Status == TStatus.Queued || i.Status == TStatus.Active);
                if (idle) _applyAll = null;
            }
            if (batchDone) { try { BatchFinished?.Invoke(it.Batch, b[1], b[2]); } catch { } }
        }

        static bool IsNetwork(Exception ex)
        {
            switch (ex)
            {
                case ApiException ae: return ae.IsNetwork;
                case WebException we: return !(we.Response is HttpWebResponse);
                case SocketException _: return true;
                case IOException io:
                    return io is ConnectionLostException || io.InnerException is SocketException || io.InnerException is WebException;
            }
            return false;
        }

        static string Describe(Exception ex)
        {
            if (ex is WebException we) return ApiClient.Translate(we).Message;
            if (ex is UnauthorizedAccessException) return T("xfer.accessDenied");
            return ex.Message;
        }

        void CleanupCanceled(TransferItem it)
        {
            if (it.Dir == TDir.Download)
            {
                try
                {
                    string part = it.LocalPath + PathUtil.PartExt;
                    if (File.Exists(part)) { File.SetAttributes(part, FileAttributes.Normal); File.Delete(part); }
                }
                catch { }
            }
            else
            {
                var peer = _c.Peers.FindById(it.PeerId);
                if (peer == null) return;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { ApiClient.Call(peer, "POST", "/api/discard", new JObj { ["share"] = it.Share, ["path"] = it.RemotePath }, 4000); }
                    catch { }
                });
            }
        }

        // ─── 충돌 ──────────────────────────────────────────────────────────
        string ResolveConflict(TransferItem it, long tSize, DateTime tTime, bool canOverwrite, bool canResume)
        {
            if (it.ForceOverwrite) return "overwrite";
            string pol = _applyAll ?? _c.Config.DefaultConflictAction;
            if (pol == "ask")
            {
                lock (_askLock)
                {
                    pol = _applyAll ?? "ask";
                    if (pol == "ask")
                    {
                        if (it.CancelFlag || it.PauseFlag) throw new OperationCanceledException();
                        var req = new ConflictRequest { Item = it, TargetSize = tSize, TargetTime = tTime, CanOverwrite = canOverwrite, CanResume = canResume };
                        ConflictRequest ans = null;
                        try { ans = AskConflict?.Invoke(req); } catch { }
                        if (ans == null) ans = new ConflictRequest { Answer = "skip" };
                        if (ans.CancelAll)
                        {
                            it.CancelFlag = true;
                            ThreadPool.QueueUserWorkItem(_ => CancelAll());
                            throw new OperationCanceledException();
                        }
                        pol = ans.Answer;
                        if (ans.ApplyAll) _applyAll = pol;
                    }
                }
            }
            switch (pol)
            {
                case "overwriteIfNewer":
                    if (it.SrcTime > tTime.AddSeconds(2)) pol = "overwrite";
                    else throw new SkipException(T("xfer.skip.notNewer"));
                    break;
                case "overwriteIfDifferentSize":
                    if (tSize != it.Size) pol = "overwrite";
                    else throw new SkipException(T("xfer.skip.sameSize"));
                    break;
                case "resume":
                    if (!canResume) throw new SkipException(T(tSize >= it.Size ? "xfer.skip.complete" : "xfer.skip.cantResume"));
                    break;
                case "skip":
                    throw new SkipException(T("xfer.skip.exists"));
            }
            if ((pol == "overwrite" || pol == "resume") && !canOverwrite)
                throw new InvalidOperationException(T("xfer.noOverwrite"));
            return pol;
        }

        // ─── 받기 ──────────────────────────────────────────────────────────
        Peer PeerOf(TransferItem it)
        {
            var p = _c.Peers.FindById(it.PeerId);
            if (p == null) throw new InvalidOperationException(T("xfer.unknownPeer"));
            if (p.Blocked) throw new InvalidOperationException(T("peer.isBlocked"));
            return p;
        }

        void DoDownload(TransferItem it)
        {
            var peer = PeerOf(it);
            string target = it.LocalPath;
            string dir = Path.GetDirectoryName(target);
            Directory.CreateDirectory(dir);
            if (Directory.Exists(target)) throw new IOException(T("err.is_dir"));

            bool append = false, overwrite = false;
            long offset = 0;
            if (File.Exists(target))
            {
                var fi = new FileInfo(target);
                string pol = ResolveConflict(it, fi.Length, fi.LastWriteTime, true, fi.Length < it.Size);
                if (pol == "rename")
                {
                    target = PathUtil.UniqueLocalPath(target);
                    it.LocalPath = target;
                    it.Note = T("xfer.note.renamed", Path.GetFileName(target));
                }
                else if (pol == "resume") { append = true; offset = fi.Length; it.Note = T("xfer.note.resumed"); }
                else overwrite = true;
            }
            string part = target + PathUtil.PartExt;
            if (!append && File.Exists(part))
            {
                long pl = new FileInfo(part).Length;
                if (pl > 0 && pl <= it.Size) { offset = pl; it.Note = T("xfer.note.resumed"); }
            }
            long free = ShellOps.FreeSpace(dir);
            if (free >= 0 && it.Size - offset > free)
                throw new IOException(T("xfer.noSpaceLocal", Fmt.Size(it.Size - offset), Fmt.Size(free)));

            var req = ApiClient.Create(peer, "GET", "/api/file" + ApiClient.Q("share", it.Share, "path", it.RemotePath), 15000);
            req.ReadWriteTimeout = 60000;
            if (offset > 0) req.AddRange(offset);
            it.Req = req;
            if (it.CancelFlag || it.PauseFlag) throw new OperationCanceledException();

            HttpWebResponse resp;
            try { resp = (HttpWebResponse)req.GetResponse(); }
            catch (WebException ex) { throw ApiClient.Translate(ex); }
            using (resp)
            {
                long total = long.TryParse(resp.Headers["X-FR-Size"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ts) ? ts : resp.ContentLength + offset;
                if (offset > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
                {
                    offset = 0;
                    if (append) throw new IOException(T("xfer.noRangeSupport"));
                }
                it.Size = total;
                long mtime = long.TryParse(resp.Headers["X-FR-MTime"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long mt) ? mt : 0;
                string writePath = append ? target : part;

                using (var fs = new FileStream(writePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, BufSize))
                {
                    if (!append) try { File.SetAttributes(part, File.GetAttributes(part) | FileAttributes.Hidden); } catch { }
                    fs.SetLength(offset);
                    fs.Position = offset;
                    using (var rs = resp.GetResponseStream())
                        Pump(rs, fs, it, offset, total - offset);
                }
                if (it.CancelFlag || it.PauseFlag) throw new OperationCanceledException();
                long got = new FileInfo(writePath).Length;
                if (got != total) throw new IOException(T("err.size_mismatch", Fmt.Size(got), Fmt.Size(total)));

                if (!append)
                {
                    if (File.Exists(target))
                    {
                        if (!overwrite) target = PathUtil.UniqueLocalPath(target);
                        else { File.SetAttributes(target, FileAttributes.Normal); File.Delete(target); }
                        it.LocalPath = target;
                    }
                    File.Move(part, target);
                    try { File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.Hidden); } catch { }
                }
                if (_c.Config.PreserveTimestamps && mtime > 0)
                    try { File.SetLastWriteTime(target, TimeUtil.FromUnixMs(mtime)); } catch { }
            }
        }

        // ─── 보내기 ────────────────────────────────────────────────────────
        void DoUpload(TransferItem it)
        {
            var peer = PeerOf(it);
            var fi = new FileInfo(it.LocalPath);
            if (!fi.Exists) throw new FileNotFoundException(T("xfer.sourceMissing", it.LocalPath));
            it.Size = fi.Length;
            it.SrcTime = fi.LastWriteTime;

            var st = ApiClient.Call(peer, "GET", "/api/stat" + ApiClient.Q("share", it.Share, "path", it.RemotePath), null, 10000);
            var access = AccessParse.Parse(st.S("access"), AccessLevel.Read);
            if (access < AccessLevel.Write) throw new InvalidOperationException(T("err.denied_write"));
            string mode = "new";
            long offset = 0;
            if (st.B("exists"))
            {
                if (st.B("isDir")) throw new IOException(T("xfer.remoteIsDir"));
                long tSize = st.L("size");
                bool canOverwrite = access >= AccessLevel.Full;
                string pol = ResolveConflict(it, tSize, TimeUtil.FromUnixMs(st.L("m")), canOverwrite, canOverwrite && tSize < it.Size);
                if (pol == "rename")
                {
                    string dir = PathUtil.Parent(it.RemotePath);
                    string newName = PathUtil.UniqueName(PathUtil.Name(it.RemotePath), n =>
                    {
                        var s2 = ApiClient.Call(peer, "GET", "/api/stat" + ApiClient.Q("share", it.Share, "path", PathUtil.Join(dir, n)), null, 10000);
                        return s2.B("exists");
                    });
                    it.RemotePath = PathUtil.Join(dir, newName);
                    it.Note = T("xfer.note.renamed", newName);
                    st = ApiClient.Call(peer, "GET", "/api/stat" + ApiClient.Q("share", it.Share, "path", it.RemotePath), null, 10000);
                }
                else if (pol == "resume") { mode = "append"; offset = tSize; it.Note = T("xfer.note.resumedUp"); }
                else mode = "overwrite";
            }
            if (mode != "append")
            {
                long part = st.L("part", -1);
                if (part > 0 && part <= it.Size) { offset = part; it.Note = T("xfer.note.resumedUp"); }
            }
            long free = st.L("free", -1);
            if (free >= 0 && it.Size - offset > free)
                throw new IOException(T("err.no_space", Fmt.Size(it.Size - offset), Fmt.Size(free)));

            var req = ApiClient.Create(peer, "PUT", "/api/file" + ApiClient.Q(
                "share", it.Share, "path", it.RemotePath, "offset", offset, "total", it.Size,
                "mtime", _c.Config.PreserveTimestamps ? TimeUtil.ToUnixMs(it.SrcTime) : 0, "mode", mode), 30000);
            req.ContentType = "application/octet-stream";
            req.ContentLength = it.Size - offset;
            req.AllowWriteStreamBuffering = false;
            req.SendChunked = false;
            req.ReadWriteTimeout = 60000;
            it.Req = req;
            if (it.CancelFlag || it.PauseFlag) throw new OperationCanceledException();

            try
            {
                using (var fs = new FileStream(it.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufSize, FileOptions.SequentialScan))
                {
                    fs.Position = offset;
                    using (var rs = req.GetRequestStream())
                        Pump(fs, rs, it, offset, it.Size - offset);
                }
                if (it.CancelFlag || it.PauseFlag) throw new OperationCanceledException();
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    var r = ApiClient.ReadJson(resp);
                    if (r.L("size", -1) != it.Size) throw new IOException(T("xfer.remoteSizeMismatch"));
                }
            }
            catch (WebException ex) { throw ApiClient.Translate(ex); }
        }

        /// <summary>스트림 복사 + 진행률/속도/속도 제한/취소.</summary>
        void Pump(Stream src, Stream dst, TransferItem it, long start, long count)
        {
            var buf = new byte[BufSize];
            long copied = 0;
            it.Done = start;
            var sw = Stopwatch.StartNew();
            long lastMs = 0, lastBytes = start;
            while (copied < count)
            {
                if (it.CancelFlag || it.PauseFlag) throw new OperationCanceledException();
                int n = src.Read(buf, 0, (int)Math.Min(buf.Length, count - copied));
                if (n <= 0) break;
                _c.ClientLimiter.Take(n, () => it.CancelFlag || it.PauseFlag);
                dst.Write(buf, 0, n);
                copied += n;
                it.Done = start + copied;
                long ms = sw.ElapsedMilliseconds;
                if (ms - lastMs >= 400)
                {
                    double inst = (it.Done - lastBytes) * 1000.0 / Math.Max(1, ms - lastMs);
                    it.Speed = it.Speed <= 0 ? inst : it.Speed * 0.55 + inst * 0.45;
                    lastMs = ms;
                    lastBytes = it.Done;
                    Bump();
                }
            }
            if (copied < count) throw new ConnectionLostException();
        }
    }
}
