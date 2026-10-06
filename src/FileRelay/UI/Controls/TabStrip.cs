using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace FileRelay.UI
{
    /// <summary>밑줄 강조형 탭 머리글. 탭마다 글리프와 숫자 배지를 달 수 있다.</summary>
    public class TabStrip : Control
    {
        public sealed class Tab
        {
            public string Text;
            public char Glyph;
            public string Badge = "";
            public Color BadgeColor = Theme.Raised;
            internal Rectangle Bounds;
        }

        public readonly List<Tab> Tabs = new List<Tab>();
        int _sel, _hot = -1;
        public event EventHandler SelectedChanged;
        /// <summary>오른쪽 끝에 그려지는 작은 도구 버튼 영역용 여백.</summary>
        public int RightReserve;

        public TabStrip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Theme.S(36);
            Font = Theme.Ui;
            BackColor = Theme.Panel;
        }

        public Tab Add(string text, char glyph = '\0')
        {
            var t = new Tab { Text = text, Glyph = glyph };
            Tabs.Add(t);
            Invalidate();
            return t;
        }

        public int Selected
        {
            get => _sel;
            set
            {
                if (value < 0 || value >= Tabs.Count || value == _sel) return;
                _sel = value;
                Invalidate();
                SelectedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void SetBadge(int i, string badge, Color? color = null)
        {
            if (i < 0 || i >= Tabs.Count) return;
            var t = Tabs[i];
            var c = color ?? Theme.Raised;
            if (t.Badge == badge && t.BadgeColor == c) return;
            t.Badge = badge ?? "";
            t.BadgeColor = c;
            Invalidate();
        }

        void Layout2()
        {
            int x = Theme.S(8);
            foreach (var t in Tabs)
            {
                int w = Theme.S(24) + Theme.Measure(t.Text, Theme.UiBold).Width;   // 선택하면 굵게 그리므로 굵은 글꼴 기준 (폭이 바뀌지 않게)
                if (t.Glyph != '\0') w += Theme.S(22);
                if (!string.IsNullOrEmpty(t.Badge)) w += Theme.Measure(t.Badge, Theme.Small).Width + Theme.S(16);
                t.Bounds = new Rectangle(x, 0, w, Height);
                x += w;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hot = Tabs.FindIndex(t => t.Bounds.Contains(e.Location));
            if (hot != _hot) { _hot = hot; Invalidate(); }
            Cursor = hot >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = -1; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int i = Tabs.FindIndex(t => t.Bounds.Contains(e.Location));
            if (i >= 0) Selected = i;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Layout2();
            var g = e.Graphics;
            g.Clear(BackColor);
            Theme.HQ(g);
            using (var p = new Pen(Theme.BorderSoft)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
            for (int i = 0; i < Tabs.Count; i++)
            {
                var t = Tabs[i];
                bool sel = i == _sel, hot = i == _hot;
                var fore = sel ? Theme.Text : hot ? Theme.Lift(Theme.TextDim, 0.3f) : Theme.TextDim;
                int x = t.Bounds.X + Theme.S(12);
                if (t.Glyph != '\0')
                {
                    var img = Icons.Glyph(t.Glyph, Theme.S(15), sel ? Theme.Accent : fore);
                    g.DrawImage(img, x, (Height - img.Height) / 2);
                    x += Theme.S(22);
                }
                int tw = Theme.Measure(t.Text, Theme.UiBold).Width;
                Theme.DrawText(g, t.Text, sel ? Theme.UiBold : Font, fore, new Rectangle(x, 0, tw + Theme.S(6), Height));
                x += tw + Theme.S(8);
                if (!string.IsNullOrEmpty(t.Badge))
                {
                    var bs = Theme.Measure(t.Badge, Theme.Small);
                    var br = new Rectangle(x, (Height - Theme.S(18)) / 2, bs.Width + Theme.S(12), Theme.S(18));
                    Theme.FillRound(g, t.BadgeColor, br, Theme.S(9));
                    Theme.DrawText(g, t.Badge, Theme.Small, t.BadgeColor == Theme.Raised ? Theme.TextDim : Color.White, br, Theme.TextCenter);
                }
                if (sel)
                {
                    var ur = new RectangleF(t.Bounds.X + Theme.S(10), Height - Theme.S(3), t.Bounds.Width - Theme.S(20), Theme.S(3));
                    Theme.FillRound(g, Theme.Accent, ur, Theme.S(2));
                }
            }
        }
    }
}
