using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using static FileRelay.L;
namespace FileRelay
{
    /// <summary>선택 항목 → 전송 작업 목록 (폴더는 하위까지 펼치고 대상 폴더를 미리 만든다). 백그라운드에서 호출.</summary>
    public static class JobBuilder
    {
        public static void SplitRemote(string path, out string share, out string rel)
        {
            var segs = PathUtil.Segments(path);
            share = segs.Length > 0 ? segs[0] : "";
            rel = segs.Length > 1 ? string.Join("\\", segs, 1, segs.Length - 1) : "";
        }

        public static List<TransferItem> Downloads(Peer peer, IList<FsItem> items, string localDir, Action<string> progress, CancellationToken ct)
        {
            var jobs = new List<TransferItem>();
            foreach (var it in items)
            {
                ct.ThrowIfCancellationRequested();
                SplitRemote(it.Path, out string share, out string rel);
                if (it.IsDir)
                {
                    progress?.Invoke(T("job.listing", it.Name));
                    var w = ApiClient.Call(peer, "GET", "/api/walk" + ApiClient.Q("share", share, "path", rel), null, 180_000);
                    string baseLocal = Path.Combine(localDir, it.Name);
                    Directory.CreateDirectory(baseLocal);
                    foreach (var d in w.StrList("dirs"))
                        Directory.CreateDirectory(Path.Combine(baseLocal, d));
                    var files = w.A("files") ?? new List<object>();
                    foreach (var o in files.OfType<JObj>())
                    {
                        string p = o.S("p");
                        jobs.Add(new TransferItem
                        {
                            Dir = TDir.Download,
                            PeerId = peer.Id,
                            PeerName = peer.DisplayName,
                            Share = share,
                            RemotePath = PathUtil.Join(rel, p),
                            LocalPath = Path.Combine(baseLocal, p),
                            Size = o.L("s"),
                            SrcTime = TimeUtil.FromUnixMs(o.L("m")),
                        });
                    }
                }
                else
                {
                    jobs.Add(new TransferItem
                    {
                        Dir = TDir.Download,
                        PeerId = peer.Id,
                        PeerName = peer.DisplayName,
                        Share = share,
                        RemotePath = rel,
                        LocalPath = Path.Combine(localDir, it.Name),
                        Size = it.Size,
                        SrcTime = it.MTime,
                    });
                }
            }
            return jobs;
        }

        public static List<TransferItem> Uploads(Peer peer, IList<string> localPaths, string remoteDir, Action<string> progress, CancellationToken ct)
        {
            SplitRemote(remoteDir, out string share, out string relDir);
            if (share.Length == 0) throw new InvalidOperationException(T("job.openShareFirst"));
            var jobs = new List<TransferItem>();
            var mk = new List<string>();
            foreach (var lp0 in localPaths)
            {
                ct.ThrowIfCancellationRequested();
                string lp = lp0.TrimEnd('\\');
                if (Directory.Exists(lp0))
                {
                    if (lp.Length <= 2) throw new InvalidOperationException(T("job.noWholeDrive", lp0));
                    string name = Path.GetFileName(lp);
                    string baseRel = PathUtil.Join(relDir, name);
                    mk.Add(baseRel);
                    progress?.Invoke(T("job.scanning", name));
                    var stack = new Stack<string>();
                    stack.Push(lp);
                    while (stack.Count > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        string dir = stack.Pop();
                        string relHere = dir.Length > lp.Length ? dir.Substring(lp.Length + 1) : "";
                        List<FileSystemInfo> entries;
                        try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
                        catch (Exception ex) { Log.Warn(Cat.Xfer, T("log.folderSkipped", dir, ex.Message)); continue; }
                        foreach (var f in entries)
                        {
                            if (f.Name.EndsWith(PathUtil.PartExt, StringComparison.OrdinalIgnoreCase)) continue;
                            string rel = relHere.Length == 0 ? f.Name : relHere + "\\" + f.Name;
                            if ((f.Attributes & FileAttributes.Directory) != 0)
                            {
                                mk.Add(PathUtil.Join(baseRel, rel));
                                if ((f.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(f.FullName);
                            }
                            else
                            {
                                jobs.Add(new TransferItem
                                {
                                    Dir = TDir.Upload,
                                    PeerId = peer.Id,
                                    PeerName = peer.DisplayName,
                                    LocalPath = f.FullName,
                                    Share = share,
                                    RemotePath = PathUtil.Join(baseRel, rel),
                                    Size = ((FileInfo)f).Length,
                                    SrcTime = f.LastWriteTime,
                                });
                            }
                        }
                    }
                }
                else if (File.Exists(lp0))
                {
                    var fi = new FileInfo(lp0);
                    jobs.Add(new TransferItem
                    {
                        Dir = TDir.Upload,
                        PeerId = peer.Id,
                        PeerName = peer.DisplayName,
                        LocalPath = fi.FullName,
                        Share = share,
                        RemotePath = PathUtil.Join(relDir, fi.Name),
                        Size = fi.Length,
                        SrcTime = fi.LastWriteTime,
                    });
                }
            }
            if (mk.Count > 0)
            {
                progress?.Invoke(T("job.creatingFolders", mk.Count));
                for (int i = 0; i < mk.Count; i += 2000)
                    ApiClient.Call(peer, "POST", "/api/mkdir", new JObj
                    {
                        ["share"] = share,
                        ["paths"] = mk.Skip(i).Take(2000).Cast<object>().ToList(),
                    }, 60_000);
            }
            return jobs;
        }
    }
}
