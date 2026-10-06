using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using static FileRelay.L;
namespace FileRelay
{
    public struct Perms
    {
        public bool Read, Write, Rename, Delete;

        public static Perms From(AccessLevel a) => new Perms
        {
            Read = a >= AccessLevel.Read,
            Write = a >= AccessLevel.Write,
            Rename = a >= AccessLevel.Full,
            Delete = a >= AccessLevel.Full,
        };

        public static readonly Perms All = new Perms { Read = true, Write = true, Rename = true, Delete = true };
        public static readonly Perms ReadOnly = new Perms { Read = true };
    }

    /// <summary>파일 창 하나가 보는 파일 시스템 (로컬 / 원격 피어). List 등은 백그라운드에서 호출.</summary>
    public abstract class FsSource
    {
        public abstract bool IsLocal { get; }
        public abstract string RootName { get; }
        public abstract List<FsItem> List(string path, out Perms perms, out long free);
        public abstract string Parent(string path);
        public abstract string Combine(string dir, string name);
        public abstract string Display(string path);
        public abstract string ParseInput(string text, string current);
        public abstract void MakeDir(string dir, string name);
        public abstract void Rename(FsItem item, string newName);
    }

    public sealed class LocalFs : FsSource
    {
        public bool ShowHidden;

        public override bool IsLocal => true;
        public override string RootName => T("pane.thisPc");

        public override List<FsItem> List(string path, out Perms perms, out long free)
        {
            var res = new List<FsItem>();
            if (string.IsNullOrEmpty(path))
            {
                perms = Perms.ReadOnly;
                free = -1;
                void Special(string p, string name)
                {
                    if (!string.IsNullOrEmpty(p) && Directory.Exists(p))
                        res.Add(new FsItem { Name = name, Path = p, Kind = FsKind.Special, MTime = SafeTime(p), Info = T("fs.favorites") });
                }
                Special(KnownFolders.Desktop, T("fs.desktop"));
                Special(KnownFolders.Documents, T("fs.documents"));
                Special(KnownFolders.Downloads, T("fs.downloads"));
                foreach (var d in DriveInfo.GetDrives())
                {
                    var it = new FsItem { Name = d.Name.TrimEnd('\\'), Path = d.Name, Kind = FsKind.Drive };
                    try
                    {
                        if (d.DriveType == DriveType.Network) it.Info = T("fs.networkDrive");
                        else if (d.DriveType == DriveType.Removable) it.Info = T("fs.removable");
                        else if (d.DriveType == DriveType.CDRom) it.Info = T("fs.cdrom");
                        else it.Info = T("fs.localDisk");
                        if (d.DriveType != DriveType.Network && d.IsReady)
                        {
                            if (!string.IsNullOrEmpty(d.VolumeLabel)) it.Name += "  " + d.VolumeLabel;
                            it.Free = d.AvailableFreeSpace;
                            it.Total = d.TotalSize;
                        }
                    }
                    catch { }
                    res.Add(it);
                }
                return res;
            }

            perms = Perms.All;
            free = ShellOps.FreeSpace(path);
            var di = new DirectoryInfo(path);
            if (!di.Exists) throw new DirectoryNotFoundException(T("err.no_folder", path));
            foreach (var f in di.EnumerateFileSystemInfos())
            {
                try
                {
                    var attr = f.Attributes;
                    bool hidden = (attr & (FileAttributes.Hidden | FileAttributes.System)) != 0;
                    if (hidden && !ShowHidden) continue;
                    if (!ShowHidden && f.Name.EndsWith(PathUtil.PartExt, StringComparison.OrdinalIgnoreCase)) continue;
                    bool dir = (attr & FileAttributes.Directory) != 0;
                    res.Add(new FsItem
                    {
                        Name = f.Name,
                        Path = f.FullName,
                        Kind = dir ? FsKind.Dir : FsKind.File,
                        Size = dir ? 0 : ((FileInfo)f).Length,
                        MTime = f.LastWriteTime,
                        Hidden = hidden,
                    });
                }
                catch { }
            }
            return res;
        }

        static DateTime SafeTime(string p)
        {
            try { return Directory.GetLastWriteTime(p); } catch { return DateTime.MinValue; }
        }

        public override string Parent(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string t = path.TrimEnd('\\');
            if (t.Length <= 2 && t.EndsWith(":")) return "";
            string p = Path.GetDirectoryName(t);
            return string.IsNullOrEmpty(p) ? "" : p;
        }

        public override string Combine(string dir, string name) => Path.Combine(dir, name);

        public override string Display(string path) => path ?? "";

        public override string ParseInput(string text, string current)
        {
            text = Environment.ExpandEnvironmentVariables((text ?? "").Trim().Trim('"'));
            if (text.Length == 0 || text == RootName) return "";
            if (text.Length == 2 && text[1] == ':') text += "\\";
            if (!Path.IsPathRooted(text) && !string.IsNullOrEmpty(current)) text = Path.Combine(current, text);
            try { return Path.GetFullPath(text); } catch { return text; }
        }

        public override void MakeDir(string dir, string name)
        {
            string p = Path.Combine(dir, name);
            if (Directory.Exists(p) || File.Exists(p)) throw new IOException(T("err.exists_name"));
            Directory.CreateDirectory(p);
        }

        public override void Rename(FsItem item, string newName)
        {
            string dir = Path.GetDirectoryName(item.Path.TrimEnd('\\'));
            string target = Path.Combine(dir, newName);
            bool caseOnly = string.Equals(item.Path, target, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && (File.Exists(target) || Directory.Exists(target))) throw new IOException(T("err.exists_name"));
            if (item.Kind == FsKind.Dir)
            {
                if (caseOnly)
                {
                    string tmp = item.Path + ".fr_tmp_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                    Directory.Move(item.Path, tmp);
                    Directory.Move(tmp, target);
                }
                else Directory.Move(item.Path, target);
            }
            else File.Move(item.Path, target);
        }
    }

    public sealed class RemoteFs : FsSource
    {
        public readonly Peer Peer;

        public RemoteFs(Peer peer) { Peer = peer; }

        public override bool IsLocal => false;
        public override string RootName => Peer.DisplayName;

        void EnsureUsable()
        {
            if (Peer.Blocked) throw new InvalidOperationException(T("peer.isBlocked"));
            if (Peer.State == PeerState.Pending) throw new InvalidOperationException(T("peer.isPending"));
            if (Peer.State == PeerState.Incoming) throw new InvalidOperationException(T("peer.isIncoming"));
            if (Peer.State == PeerState.Unpaired) throw new InvalidOperationException(T("peer.isUnpaired"));
        }

        public override List<FsItem> List(string path, out Perms perms, out long free)
        {
            EnsureUsable();
            if (!Peer.IsOnline)
                throw new ApiException(0, "offline", T("peer.isOffline", Peer.DisplayName));
            var res = new List<FsItem>();
            if (string.IsNullOrEmpty(path))
            {
                var r = ApiClient.Call(Peer, "GET", "/api/shares", null, 8000);
                perms = Perms.ReadOnly;
                free = -1;
                foreach (var o in (r.A("shares") ?? new List<object>()).OfType<JObj>())
                {
                    res.Add(new FsItem
                    {
                        Name = o.S("name"),
                        Path = o.S("name"),
                        Kind = FsKind.Share,
                        Access = AccessParse.Parse(o.S("access")),
                        Free = o.L("free", -1),
                        Total = o.L("total", -1),
                        MTime = TimeUtil.FromUnixMs(o.L("m")),
                        Info = o.B("available", true) ? o.S("description") : T("fs.folderMissing"),
                    });
                }
                Peer.ShareCount = res.Count;
                return res;
            }
            JobBuilder.SplitRemote(path, out string share, out string rel);
            var l = ApiClient.Call(Peer, "GET", "/api/list" + ApiClient.Q("share", share, "path", rel), null, 15000);
            perms = Perms.From(AccessParse.Parse(l.S("access")));
            free = l.L("free", -1);
            foreach (var o in (l.A("items") ?? new List<object>()).OfType<JObj>())
            {
                bool d = o.B("d");
                string n = o.S("n");
                res.Add(new FsItem
                {
                    Name = n,
                    Path = PathUtil.Join(path, n),
                    Kind = d ? FsKind.Dir : FsKind.File,
                    Size = o.L("s"),
                    MTime = TimeUtil.FromUnixMs(o.L("m")),
                    Hidden = o.B("h"),
                });
            }
            return res;
        }

        public override string Parent(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            return PathUtil.Parent(path);
        }

        public override string Combine(string dir, string name) => PathUtil.Join(dir, name);

        public override string Display(string path) => PathUtil.Normalize(path);

        public override string ParseInput(string text, string current)
        {
            text = (text ?? "").Trim().Trim('"').Replace('/', '\\');
            if (text.StartsWith("\\\\"))
            {
                // \\이름\공유\경로 형태로 붙여 넣은 경우 이름 부분을 뗀다.
                var segs = PathUtil.Segments(text);
                text = segs.Length > 1 ? string.Join("\\", segs, 1, segs.Length - 1) : "";
            }
            return PathUtil.Normalize(text);
        }

        public override void MakeDir(string dir, string name)
        {
            JobBuilder.SplitRemote(PathUtil.Join(dir, name), out string share, out string rel);
            ApiClient.Call(Peer, "POST", "/api/mkdir", new JObj { ["share"] = share, ["path"] = rel }, 15000);
        }

        public override void Rename(FsItem item, string newName)
        {
            JobBuilder.SplitRemote(item.Path, out string share, out string rel);
            ApiClient.Call(Peer, "POST", "/api/rename", new JObj { ["share"] = share, ["path"] = rel, ["newName"] = newName }, 30000);
        }

        public JObj Delete(IList<FsItem> items)
        {
            JObj last = null;
            foreach (var g in items.GroupBy(i => PathUtil.Segments(i.Path).FirstOrDefault() ?? ""))
            {
                var rels = g.Select(i => { JobBuilder.SplitRemote(i.Path, out _, out string rel); return (object)rel; }).ToList();
                last = ApiClient.Call(Peer, "POST", "/api/delete", new JObj { ["share"] = g.Key, ["paths"] = rels }, 120_000);
            }
            return last;
        }
    }
}
