using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

using static FileRelay.L;
namespace FileRelay.UI
{
    /// <summary>Segoe Fluent Icons / MDL2 글리프 코드.</summary>
    public static class G
    {
        public const char Back = '', Forward = '', Up = '', Refresh = '', Home = '',
            NewFolder = '', Delete = '', Rename = '', Copy = '', Paste = '', Cut = '',
            Upload = '', Download = '', Settings = '', Add = '', Remove = '',
            Mail = '', Note = '', Send = '', Pause = '', Play = '', Stop = '',
            Cancel = '', Clear = '', Contact = '', AddFriend = '', Folder = '',
            OpenFolder = '', Sync = '', Pin = '', Unpin = '', Reply = '', Search = '',
            Info = '', History = '', Share = '', Network = '', PC = '', Filter = '',
            Help = '', Power = '', Shield = '', Lock = '', More = '', ChevronDown = '',
            ChevronRight = '', Check = '', Error = '', Warning = '', Link = '', Edit = '',
            Save = '', Star = '', StarFill = '', View = '', Hide = '', Document = '',
            Drive = '', Close = '', People = '', Accept = '', Block = '', Moon = '',
            Clock = '', Globe = '', Compare = '', SelectAll = '', Retry = '', Bell = '',
            Explorer = '', OpenFile = '', Admin = '', Color = '', Minimize = '',
            CircleFill = '', Ethernet = '', Copy2 = '', Sort = '', Priority = '',
            Location = '', Recent = '', List = '', Activity = '', Feedback = '';
    }

    public static class Icons
    {
        static readonly Dictionary<string, Bitmap> Cache = new Dictionary<string, Bitmap>();
        static readonly Dictionary<string, string> TypeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ── 글리프 ──
        public static Bitmap Glyph(char g, int px, Color c)
        {
            string key = "g" + (int)g + "|" + px + "|" + c.ToArgb();
            if (Cache.TryGetValue(key, out var bmp)) return bmp;
            bmp = new Bitmap(px, px, PixelFormat.Format32bppPArgb);
            using (var gr = Graphics.FromImage(bmp))
            {
                Theme.HQ(gr);
                if (Theme.IconFont != null)
                {
                    using (var fam = new FontFamily(Theme.IconFont))
                    using (var path = new GraphicsPath())
                    using (var br = new SolidBrush(c))
                    {
                        float em = px * 0.94f;
                        path.AddString(g.ToString(), fam, (int)FontStyle.Regular, em, PointF.Empty, StringFormat.GenericTypographic);
                        var b = path.GetBounds();
                        if (b.Width > 0 && b.Height > 0)
                        {
                            float scale = Math.Min(1f, Math.Min(px * 0.98f / b.Width, px * 0.98f / b.Height));
                            using (var m = new Matrix())
                            {
                                m.Translate(px / 2f, px / 2f);
                                m.Scale(scale, scale);
                                m.Translate(-(b.X + b.Width / 2f), -(b.Y + b.Height / 2f));
                                path.Transform(m);
                            }
                            gr.FillPath(br, path);
                        }
                    }
                }
                else
                {
                    using (var br = new SolidBrush(c)) gr.FillEllipse(br, px * 0.3f, px * 0.3f, px * 0.4f, px * 0.4f);
                }
            }
            Cache[key] = bmp;
            return bmp;
        }

        public static Bitmap Glyph(char g, Color c) => Glyph(g, Theme.S(16), c);

        // ── 셸 파일 아이콘 ──
        public static Bitmap ForItem(FsItem it, bool local)
        {
            switch (it.Kind)
            {
                case FsKind.Dir: return Shell("<dir>", "folder", Native.FILE_ATTRIBUTE_DIRECTORY, true);
                case FsKind.Share: return ShareIcon();
                case FsKind.Drive: return Shell("drv:" + it.Path, it.Path, 0, false) ?? Glyph(G.Drive, Theme.TextDim);
                case FsKind.Special: return Shell("sp:" + it.Path, it.Path, 0, false) ?? Shell("<dir>", "folder", Native.FILE_ATTRIBUTE_DIRECTORY, true);
            }
            string ext = Path.GetExtension(it.Name);
            if (string.IsNullOrEmpty(ext)) ext = ".";
            return Shell("ext:" + ext, "file" + ext, Native.FILE_ATTRIBUTE_NORMAL, true);
        }

        static Bitmap Shell(string key, string path, uint attr, bool useAttr)
        {
            if (Cache.TryGetValue(key, out var bmp)) return bmp;
            bmp = null;
            try
            {
                var info = new Native.SHFILEINFO();
                uint flags = Native.SHGFI_ICON | Native.SHGFI_SMALLICON | (useAttr ? Native.SHGFI_USEFILEATTRIBUTES : 0);
                Native.SHGetFileInfo(path, attr, ref info, (uint)Marshal.SizeOf(info), flags);
                if (info.hIcon != IntPtr.Zero)
                {
                    using (var ico = Icon.FromHandle(info.hIcon)) bmp = ico.ToBitmap();
                    Native.DestroyIcon(info.hIcon);
                }
            }
            catch { }
            if (bmp == null && key != "<dir>") bmp = Glyph(G.Document, Theme.S(16), Theme.TextDim);
            Cache[key] = bmp;
            return bmp;
        }

        static Bitmap ShareIcon()
        {
            const string key = "<share>";
            if (Cache.TryGetValue(key, out var bmp)) return bmp;
            var folder = Shell("<dir>", "folder", Native.FILE_ATTRIBUTE_DIRECTORY, true);
            int w = folder?.Width ?? Theme.S(16), h = folder?.Height ?? Theme.S(16);
            bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                Theme.HQ(g);
                if (folder != null) g.DrawImage(folder, 0, 0, w, h);
                float d = w * 0.52f;
                var r = new RectangleF(w - d, h - d, d - 0.5f, d - 0.5f);
                using (var br = new SolidBrush(Theme.Accent)) g.FillEllipse(br, r);
                using (var pen = new Pen(Theme.Bg, Math.Max(1f, w / 16f))) g.DrawEllipse(pen, r);
                var gl = Glyph(G.Link, (int)(d * 0.62f), Color.White);
                g.DrawImage(gl, r.X + (r.Width - gl.Width) / 2f, r.Y + (r.Height - gl.Height) / 2f);
            }
            Cache[key] = bmp;
            return bmp;
        }

        public static string TypeName(FsItem it)
        {
            switch (it.Kind)
            {
                case FsKind.Dir: return T("ftype.folder");
                case FsKind.Share: return T("ftype.share", Fmt.Access(it.Access)) + (string.IsNullOrEmpty(it.Info) ? "" : " · " + it.Info);
                case FsKind.Drive: return it.Info;
                case FsKind.Special: return T("ftype.system");
            }
            string ext = Path.GetExtension(it.Name);
            if (string.IsNullOrEmpty(ext)) return T("common.file");
            if (TypeNames.TryGetValue(ext, out var t)) return t;
            t = "";
            try
            {
                var info = new Native.SHFILEINFO();
                Native.SHGetFileInfo("file" + ext, Native.FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf(info),
                    Native.SHGFI_TYPENAME | Native.SHGFI_USEFILEATTRIBUTES);
                t = info.szTypeName;
            }
            catch { }
            if (string.IsNullOrWhiteSpace(t)) t = T("ftype.ext", ext.TrimStart('.').ToUpperInvariant());
            TypeNames[ext] = t;
            return t;
        }

        // ── 아바타 ──
        static readonly Color[] AvatarColors =
        {
            Theme.Hex("5B8DEF"), Theme.Hex("2BB6A3"), Theme.Hex("E07A5F"), Theme.Hex("9B72F2"),
            Theme.Hex("E6A23C"), Theme.Hex("EF6F9A"), Theme.Hex("4FB0E8"), Theme.Hex("7CB342"),
            Theme.Hex("D46A6A"), Theme.Hex("8D8FE6"),
        };

        public static Color AvatarColor(string key)
        {
            unchecked
            {
                int h = 17;
                foreach (char ch in key ?? "") h = h * 31 + ch;
                return AvatarColors[(h & 0x7fffffff) % AvatarColors.Length];
            }
        }

        public static string Initials(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return "?";
            bool hangul = true;
            foreach (char ch in name) if (ch < '가' || ch > '힣') { hangul = false; break; }
            if (hangul) return name.Length >= 3 ? name.Substring(name.Length - 2) : name;
            var parts = name.Split(new[] { ' ', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) return (char.ToUpperInvariant(parts[0][0]).ToString() + char.ToUpperInvariant(parts[1][0])).Trim();
            if (char.IsLetter(name[0]) && name.Length >= 2 && name[0] < 128) return char.ToUpperInvariant(name[0]).ToString() + char.ToLowerInvariant(name[1]);
            return name.Substring(0, 1).ToUpperInvariant();
        }

        /// <summary>원형 아바타 + 상태 배지.</summary>
        public static Bitmap Avatar(string name, string colorKey, int size, Presence pres, PeerState state, bool blocked, Color ringBg, bool showStatus = true)
        {
            string key = $"av|{name}|{colorKey}|{size}|{pres}|{state}|{blocked}|{ringBg.ToArgb()}|{showStatus}";
            if (Cache.TryGetValue(key, out var bmp)) return bmp;
            bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                Theme.HQ(g);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                bool dim = blocked || pres == Presence.Offline || state != PeerState.Paired;
                var col = AvatarColor(colorKey);
                if (dim) col = Theme.Blend(col, Theme.AvatarDim, 0.62f);
                float pad = 0.5f;
                var circle = new RectangleF(pad, pad, size - 1 - pad * 2, size - 1 - pad * 2);
                using (var lg = new LinearGradientBrush(circle, Theme.Blend(col, Color.White, 0.12f), Theme.Blend(col, Color.Black, 0.12f), 90f))
                    g.FillEllipse(lg, circle);
                string ini = Initials(name);
                using (var f = new Font(Theme.FontName, size * (ini.Length >= 2 ? 0.30f : 0.40f), FontStyle.Bold, GraphicsUnit.Pixel))
                using (var br = new SolidBrush(dim ? Theme.AvatarDimText : Color.White))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(ini, f, br, new RectangleF(0, size * 0.02f, size, size), sf);

                if (showStatus)
                {
                    float d = size * 0.36f;
                    var r = new RectangleF(size - d - 0.5f, size - d - 0.5f, d, d);
                    float ring = Math.Max(1.5f, size / 18f);
                    using (var bg = new SolidBrush(ringBg)) g.FillEllipse(bg, RectangleF.Inflate(r, ring * 0.6f, ring * 0.6f));
                    var inner = RectangleF.Inflate(r, -ring * 0.4f, -ring * 0.4f);
                    if (blocked)
                    {
                        using (var p = new Pen(Theme.Danger, Math.Max(1.5f, d / 6f))) g.DrawEllipse(p, inner);
                        using (var p = new Pen(Theme.Danger, Math.Max(1.5f, d / 6f)))
                            g.DrawLine(p, inner.Left + inner.Width * 0.22f, inner.Bottom - inner.Height * 0.22f, inner.Right - inner.Width * 0.22f, inner.Top + inner.Height * 0.22f);
                    }
                    else if (state == PeerState.Pending || state == PeerState.Incoming)
                    {
                        using (var p = new Pen(Theme.Warn, Math.Max(1.5f, d / 6f)) { DashStyle = DashStyle.Dot }) g.DrawEllipse(p, inner);
                    }
                    else if (state == PeerState.Unpaired)
                    {
                        using (var p = new Pen(Theme.Danger, Math.Max(1.5f, d / 6f))) g.DrawEllipse(p, inner);
                    }
                    else switch (pres)
                    {
                        case Presence.Online:
                            using (var br = new SolidBrush(Theme.Online)) g.FillEllipse(br, inner);
                            break;
                        case Presence.Away:
                            using (var br = new SolidBrush(Theme.Away)) g.FillEllipse(br, inner);
                            using (var p = new Pen(ringBg, Math.Max(1.2f, d / 9f)))
                            {
                                float cx = inner.X + inner.Width / 2, cy = inner.Y + inner.Height / 2;
                                g.DrawLine(p, cx, cy, cx, inner.Top + inner.Height * 0.24f);
                                g.DrawLine(p, cx, cy, inner.Right - inner.Width * 0.28f, cy);
                            }
                            break;
                        case Presence.Busy:
                            using (var br = new SolidBrush(Theme.Busy)) g.FillEllipse(br, inner);
                            using (var p = new Pen(Color.White, Math.Max(1.4f, d / 7f)))
                                g.DrawLine(p, inner.Left + inner.Width * 0.26f, inner.Top + inner.Height / 2, inner.Right - inner.Width * 0.26f, inner.Top + inner.Height / 2);
                            break;
                        default:
                            using (var p = new Pen(Theme.Offline, Math.Max(1.5f, d / 5.5f))) g.DrawEllipse(p, RectangleF.Inflate(inner, -0.5f, -0.5f));
                            break;
                    }
                }
            }
            Cache[key] = bmp;
            return bmp;
        }

        /// <summary>상태 점 하나 (메뉴용).</summary>
        public static Bitmap StatusDot(Presence p, int size)
        {
            string key = "dot|" + p + "|" + size;
            if (Cache.TryGetValue(key, out var bmp)) return bmp;
            bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                Theme.HQ(g);
                float d = size * 0.55f;
                var r = new RectangleF((size - d) / 2, (size - d) / 2, d, d);
                if (p == Presence.Offline) using (var pen = new Pen(Theme.Offline, 2f)) g.DrawEllipse(pen, r);
                else using (var br = new SolidBrush(Theme.PresenceColor(p))) g.FillEllipse(br, r);
            }
            Cache[key] = bmp;
            return bmp;
        }

        // ── 앱 아이콘 ──
        static Icon _appIconCache;

        public static Icon App(int size = 0)
        {
            try
            {
                using (var s = typeof(Icons).Assembly.GetManifestResourceStream("FileRelay.app.ico"))
                {
                    if (s == null) return SystemIcons.Application;
                    if (size <= 0) return _appIconCache ?? (_appIconCache = new Icon(s));
                    return new Icon(s, size, size);
                }
            }
            catch { return SystemIcons.Application; }
        }

        public static Bitmap AppBitmap(int size)
        {
            string key = "app|" + size;
            if (Cache.TryGetValue(key, out var bmp)) return bmp;
            using (var ico = App(size >= 48 ? 256 : size)) bmp = new Bitmap(ico.ToBitmap(), size, size);
            Cache[key] = bmp;
            return bmp;
        }

        /// <summary>트레이 아이콘 (안 읽은 쪽지/오류 배지 포함). 반환 아이콘은 호출자가 DestroyIcon 해야 함.</summary>
        public static Icon Tray(bool badge, Color badgeColor)
        {
            int size = Native.GetSystemMetrics(49); // SM_CXSMICON
            if (size <= 0) size = 16;
            using (var ico = App(size))
            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    Theme.HQ(g);
                    g.DrawIcon(ico, new Rectangle(0, 0, size, size));
                    if (badge)
                    {
                        float d = size * 0.5f;
                        var r = new RectangleF(size - d, 0, d, d);
                        using (var bg = new SolidBrush(Color.FromArgb(255, 30, 31, 34))) g.FillEllipse(bg, RectangleF.Inflate(r, 1, 1));
                        using (var br = new SolidBrush(badgeColor)) g.FillEllipse(br, r);
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }
}
