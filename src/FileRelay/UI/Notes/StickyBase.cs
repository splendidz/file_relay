using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

using static FileRelay.L;
namespace FileRelay.UI
{
    /// <summary>쪽지 색 팔레트 (파스텔 = Windows 스티커 메모풍, 다크 = 진한 본문 + 색 머리글).</summary>
    public sealed class NotePalette
    {
        public string Key = "yellow", DisplayName = T("sticky.color.yellow");
        public Color Header, Body, Text, SubText, Accent, Border, ButtonBack, ButtonHover, InputBack;
        public bool Dark;

        static readonly Dictionary<string, (string Name, string PastelHead, string PastelBody, string Accent)> Defs =
            new Dictionary<string, (string, string, string, string)>
            {
                ["yellow"] = (T("sticky.color.yellow"), "FFEB8A", "FFF7D1", "F2C744"),
                ["green"] = (T("sticky.color.green"), "BDEDB3", "E4F9E0", "5CC27A"),
                ["pink"] = (T("sticky.color.pink"), "FFC2DF", "FFE4F1", "F07AAE"),
                ["purple"] = (T("sticky.color.purple"), "DFC2FF", "F2E6FF", "A985F2"),
                ["blue"] = (T("sticky.color.blue"), "C2E2FF", "E2F1FF", "5AA9F5"),
                ["gray"] = (T("sticky.color.gray"), "DADADA", "F3F2F1", "A9AEB6"),
                ["charcoal"] = (T("sticky.color.charcoal"), "474747", "3B3B3B", "6E737B"),
            };

        public static NotePalette Get(string key, string style)
        {
            if (key == null || !Defs.ContainsKey(key)) key = "yellow";
            var d = Defs[key];
            var p = new NotePalette { Key = key, DisplayName = d.Name };
            var accent = Theme.Hex(d.Accent);
            p.Accent = accent;
            if (style == "pastel" && key != "charcoal")
            {
                p.Header = Theme.Hex(d.PastelHead);
                p.Body = Theme.Hex(d.PastelBody);
                p.Text = Theme.Hex("232323");
                p.SubText = Theme.Hex("5E5E5E");
                p.Border = Theme.Blend(p.Header, Color.Black, 0.12f);
                p.ButtonBack = Theme.Blend(p.Body, Color.Black, 0.06f);
                p.ButtonHover = Theme.Blend(p.Body, Color.Black, 0.11f);
                p.InputBack = Theme.Blend(p.Body, Color.White, 0.45f);
            }
            else
            {
                p.Dark = true;
                p.Header = key == "charcoal" ? Theme.Hex("4A4D53") : accent;
                p.Body = Theme.Blend(Theme.Hex("242529"), accent, key == "charcoal" ? 0.02f : 0.07f);
                p.Text = Theme.Hex("ECEDEF");
                p.SubText = Theme.Hex("A6A9AF");
                p.Border = Theme.Blend(p.Body, Color.White, 0.10f);
                p.ButtonBack = Theme.Blend(p.Body, Color.White, 0.07f);
                p.ButtonHover = Theme.Blend(p.Body, Color.White, 0.13f);
                p.InputBack = Theme.Blend(p.Body, Color.Black, 0.25f);
            }
            return p;
        }

        /// <summary>머리글 위 글자색 (밝은 머리글이면 어두운 글자).</summary>
        public Color HeaderText => Header.GetBrightness() > 0.55f ? Theme.Hex("1E1E20") : Color.White;
        public Color HeaderSub => Header.GetBrightness() > 0.55f ? Theme.Hex("4A4A4E") : Theme.Hex("D8D8DC");

        public static Bitmap Swatch(string key, int size)
        {
            var p = Get(key, "dark");
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                Theme.HQ(g);
                using (var b = new SolidBrush(p.Accent)) g.FillEllipse(b, 1, 1, size - 2, size - 2);
            }
            return bmp;
        }
    }

    /// <summary>
    /// 스티커 메모풍 테두리 없는 창의 공통 틀:
    /// 색 머리글(끌어서 이동, 글리프 버튼), 가장자리 크기 조절, 그림자, Win11 둥근 모서리.
    /// </summary>
    public class StickyBase : Form
    {
        protected sealed class HeadButton
        {
            public char Glyph;
            public string Tip;
            public Action Click;
            public bool Toggled;
            public Rectangle Bounds;
            public bool Visible = true;
        }

        protected NotePalette Pal;
        protected readonly List<HeadButton> HeadButtons = new List<HeadButton>();
        protected string HeadTitle = "", HeadSub = "";
        protected char HeadGlyph = G.Note;
        protected int HeadHeight = Theme.S(40);
        readonly ToolTip _tip = new ToolTip { InitialDelay = 400 };
        HeadButton _hot;
        bool _topMostAtCreate;
        int _grip = Theme.S(6);

        public StickyBase(NotePalette pal)
        {
            Pal = pal;
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            KeyPreview = true;
            Font = Theme.Ui;
            BackColor = pal.Body;
            Padding = new Padding(Theme.S(6), HeadHeight, Theme.S(6), Theme.S(6));
            MinimumSize = new Size(Theme.S(260), Theme.S(180));
            Icon = Icons.App();
            Text = T("sticky.title");
        }

        /// <summary>처음 만들 때부터 '항상 위' (포커스를 뺏지 않기 위해 CreateParams 로).</summary>
        public void SetTopMostAtCreate(bool on) => _topMostAtCreate = on;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= Native.CS_DROPSHADOW;
                if (_topMostAtCreate) cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.RoundCorners(Handle, false, Pal.Border);
        }

        public virtual void ApplyPalette(NotePalette p)
        {
            Pal = p;
            BackColor = p.Body;
            Theme.RoundCorners(Handle, false, p.Border);
            Invalidate(true);
        }

        protected HeadButton AddHeadButton(char glyph, string tip, Action click)
        {
            var b = new HeadButton { Glyph = glyph, Tip = tip, Click = click };
            HeadButtons.Add(b);
            return b;
        }

        protected void LayoutHead()
        {
            int s = Theme.S(30);
            int x = Width - Theme.S(6) - s;
            for (int i = HeadButtons.Count - 1; i >= 0; i--)
            {
                var b = HeadButtons[i];
                if (!b.Visible) { b.Bounds = Rectangle.Empty; continue; }
                b.Bounds = new Rectangle(x, (HeadHeight - s) / 2, s, s);
                x -= s + Theme.S(2);
            }
        }

        protected int HeadButtonsLeft()
        {
            LayoutHead();
            int left = Width;
            foreach (var b in HeadButtons) if (b.Visible && b.Bounds.Left < left) left = b.Bounds.Left;
            return left;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutHead();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Pal.Body);
            Theme.HQ(g);
            using (var b = new SolidBrush(Pal.Header)) g.FillRectangle(b, 0, 0, Width, HeadHeight);
            LayoutHead();
            int x = Theme.S(12);
            var gi = Icons.Glyph(HeadGlyph, Theme.S(16), Pal.HeaderText);
            g.DrawImage(gi, x, (HeadHeight - gi.Height) / 2);
            x += Theme.S(26);
            int right = HeadButtonsLeft() - Theme.S(6);
            DrawHeadContent(g, new Rectangle(x, 0, right - x, HeadHeight));
            foreach (var hb in HeadButtons)
            {
                if (!hb.Visible) continue;
                if (hb == _hot || hb.Toggled)
                {
                    var c = Pal.HeaderText == Color.White ? Color.FromArgb(hb == _hot ? 50 : 34, Color.White) : Color.FromArgb(hb == _hot ? 34 : 24, Color.Black);
                    using (var br = new SolidBrush(c)) g.FillEllipse(br, hb.Bounds);
                }
                var img = Icons.Glyph(hb.Glyph, Theme.S(14), Pal.HeaderText);
                g.DrawImage(img, hb.Bounds.X + (hb.Bounds.Width - img.Width) / 2, hb.Bounds.Y + (hb.Bounds.Height - img.Height) / 2);
            }
            // 오른쪽 아래 크기 조절 표시
            using (var b = new SolidBrush(Theme.Blend(Pal.Body, Pal.Text, 0.28f)))
            {
                int s = Theme.S(2), gx = Width - Theme.S(5), gy = Height - Theme.S(5);
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j <= i; j++)
                        g.FillRectangle(b, gx - (i - j) * Theme.S(4) - s, gy - j * Theme.S(4) - s, s, s);
            }
            if (!Native.IsWin11)
                using (var p = new Pen(Pal.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }

        protected virtual void DrawHeadContent(Graphics g, Rectangle r)
        {
            int tw = Theme.Measure(HeadTitle, Theme.UiBold).Width;
            Theme.DrawText(g, HeadTitle, Theme.UiBold, Pal.HeaderText, new Rectangle(r.X, r.Y, Math.Min(tw + Theme.S(4), r.Width), r.Height));
            if (!string.IsNullOrEmpty(HeadSub) && tw + Theme.S(12) < r.Width)
                Theme.DrawText(g, HeadSub, Theme.Small, Pal.HeaderSub, new Rectangle(r.X + tw + Theme.S(8), r.Y, r.Width - tw - Theme.S(8), r.Height));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            HeadButton hot = null;
            foreach (var b in HeadButtons) if (b.Visible && b.Bounds.Contains(e.Location)) hot = b;
            if (hot != _hot)
            {
                _hot = hot;
                Invalidate(new Rectangle(0, 0, Width, HeadHeight));
                _tip.SetToolTip(this, hot?.Tip ?? "");
            }
            Cursor = hot != null ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hot != null) { _hot = null; Invalidate(new Rectangle(0, 0, Width, HeadHeight)); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            foreach (var b in HeadButtons)
                if (b.Visible && b.Bounds.Contains(e.Location)) { b.Click?.Invoke(); return; }
            if (e.Y < HeadHeight)
            {
                Native.ReleaseCapture();
                Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            // 머리글 더블클릭 = 접기/펴기
            if (e.Y < HeadHeight && !HeadButtons.Exists(b => b.Visible && b.Bounds.Contains(e.Location))) ToggleCollapse();
        }

        int _expandedHeight;

        protected void ToggleCollapse()
        {
            if (_expandedHeight == 0)
            {
                _expandedHeight = Height;
                MinimumSize = new Size(MinimumSize.Width, HeadHeight);
                Height = HeadHeight;
            }
            else
            {
                Height = _expandedHeight;
                _expandedHeight = 0;
                MinimumSize = new Size(MinimumSize.Width, Theme.S(180));
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_NCHITTEST)
            {
                base.WndProc(ref m);
                var p = PointToClient(new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF)));
                int gsz = _grip;
                bool l = p.X <= gsz, r = p.X >= Width - gsz, t = p.Y <= Theme.S(4), b = p.Y >= Height - gsz;
                if (r && b || p.X >= Width - Theme.S(16) && p.Y >= Height - Theme.S(16)) m.Result = (IntPtr)Native.HTBOTTOMRIGHT;
                else if (l && b) m.Result = (IntPtr)Native.HTBOTTOMLEFT;
                else if (l && t) m.Result = (IntPtr)Native.HTTOPLEFT;
                else if (r && t) m.Result = (IntPtr)Native.HTTOPRIGHT;
                else if (l) m.Result = (IntPtr)Native.HTLEFT;
                else if (r) m.Result = (IntPtr)Native.HTRIGHT;
                else if (t) m.Result = (IntPtr)Native.HTTOP;
                else if (b) m.Result = (IntPtr)Native.HTBOTTOM;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tip.Dispose();
            base.Dispose(disposing);
        }

        // ── 쪽지 창 공용 위젯 ──
        protected RichTextBox MakeBody(bool readOnly)
        {
            var rt = new RichTextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Pal.Body,
                ForeColor = Pal.Text,
                Font = new Font(Theme.FontName, Theme.Ui.Size + 1.5f),
                ReadOnly = readOnly,
                DetectUrls = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                ShortcutsEnabled = true,
                Dock = DockStyle.Fill,
                HideSelection = true,
            };
            Theme.ApplyControlTheme(rt, Pal.Dark);
            rt.LinkClicked += (s, e) => { try { ShellOps.Open(e.LinkText); } catch { } };
            return rt;
        }

        protected FlatButton MakeButton(string text, char glyph, bool primary = false)
        {
            var b = new FlatButton(text, primary ? ButtonKind.Primary : ButtonKind.Secondary, glyph) { Height = Theme.S(30) };
            StyleButton(b, primary);
            b.AutoFit();
            return b;
        }

        protected void StyleButton(FlatButton b, bool primary)
        {
            if (primary)
            {
                b.Kind = ButtonKind.Secondary;
                var c = Pal.Dark ? Pal.Accent : Theme.Blend(Pal.Header, Color.Black, 0.18f);
                b.CustomBack = c;
                b.CustomHover = Theme.Blend(c, Color.White, 0.12f);
                b.CustomFore = c.GetBrightness() > 0.55f ? Theme.Hex("1E1E20") : Color.White;
            }
            else
            {
                b.CustomBack = Pal.ButtonBack;
                b.CustomHover = Pal.ButtonHover;
                b.CustomFore = Pal.Text;
            }
            b.Invalidate();
        }
    }
}
