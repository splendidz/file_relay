using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

using static FileRelay.L;
namespace FileRelay.UI
{
    /// <summary>
    /// 드롭다운 선택 상자. 기본 ComboBox 는 다크 테마가 잘 안 먹어서
    /// 닫힌 상태는 직접 그리고, 펼친 목록은 다크 ContextMenuStrip 으로 띄운다.
    /// </summary>
    public class DarkSelect : Control
    {
        public sealed class Item
        {
            public object Value;
            public string Text;
            public Image Image;
            public override string ToString() => Text;
        }

        public readonly List<Item> Items = new List<Item>();
        int _sel = -1;
        bool _hover, _open;
        public string Placeholder = T("common.select");
        /// <summary>쪽지 창처럼 배경이 다른 곳에 놓을 때 색을 덮어쓴다.</summary>
        public Color? BoxColor, BoxBorder, BoxFore;

        public event EventHandler SelectedIndexChanged;

        public DarkSelect()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Height = Theme.S(32);
            Width = Theme.S(200);
            Font = Theme.Ui;
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        public Item Add(object value, string text, Image image = null)
        {
            var it = new Item { Value = value, Text = text, Image = image };
            Items.Add(it);
            Invalidate();
            return it;
        }

        public int SelectedIndex
        {
            get => _sel;
            set
            {
                int v = value < -1 || value >= Items.Count ? -1 : value;
                if (v == _sel) return;
                _sel = v;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public Item SelectedItem => _sel >= 0 && _sel < Items.Count ? Items[_sel] : null;

        public object SelectedValue
        {
            get => SelectedItem?.Value;
            set
            {
                for (int i = 0; i < Items.Count; i++)
                    if (Equals(Items[i].Value, value)) { SelectedIndex = i; return; }
                SelectedIndex = -1;
            }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { Focus(); ShowList(); }
        }

        protected override bool IsInputKey(Keys k) =>
            k == Keys.Up || k == Keys.Down || k == Keys.Space || k == Keys.Enter || base.IsInputKey(k);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Down && e.Alt || e.KeyCode == Keys.F4 || e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { ShowList(); e.Handled = true; }
            else if (e.KeyCode == Keys.Down) { if (_sel < Items.Count - 1) SelectedIndex = _sel + 1; e.Handled = true; }
            else if (e.KeyCode == Keys.Up) { if (_sel > 0) SelectedIndex = _sel - 1; e.Handled = true; }
        }

        void ShowList()
        {
            if (Items.Count == 0 || _open) return;
            var cms = new ContextMenuStrip { ShowImageMargin = true, ShowCheckMargin = false, Font = Font };
            for (int i = 0; i < Items.Count; i++)
            {
                int idx = i;
                var mi = new ToolStripMenuItem(Items[i].Text, Items[i].Image ?? (i == _sel ? Icons.Glyph(G.Check, Theme.S(14), Theme.Accent) : null));
                if (i == _sel) mi.Font = new Font(Font, FontStyle.Bold);
                mi.Click += (s, e) => SelectedIndex = idx;
                cms.Items.Add(mi);
            }
            cms.MinimumSize = new Size(Width, 0);
            DarkRenderer.Polish(cms);
            cms.Closed += (s, e) => { _open = false; Invalidate(); BeginInvoke(new Action(cms.Dispose)); };
            _open = true;
            Invalidate();
            cms.Show(this, new Point(0, Height + Theme.S(2)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            Theme.HQ(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            var baseBack = BoxColor ?? Theme.Input;
            var back = Enabled ? (_hover || _open ? Theme.Lift(baseBack, 0.04f) : baseBack) : Theme.Blend(baseBack, Theme.Bg, 0.5f);
            Theme.FillRound(g, back, r, Theme.S(6));
            var baseBorder = BoxBorder ?? Theme.Border;
            var bc = Focused || _open ? (BoxBorder.HasValue ? Theme.Blend(baseBorder, Color.Black, 0.3f) : Theme.Accent) : _hover ? Theme.Lift(baseBorder, 0.12f) : baseBorder;
            Theme.DrawRound(g, bc, r, Theme.S(6), Focused ? 1.4f : 1f);

            int x = Theme.S(10);
            var it = SelectedItem;
            if (it?.Image != null)
            {
                g.DrawImage(it.Image, x, (Height - it.Image.Height) / 2);
                x += it.Image.Width + Theme.S(6);
            }
            var chev = Icons.Glyph(G.ChevronDown, Theme.S(12), Enabled ? Theme.TextDim : Theme.TextFaint);
            var tr = new Rectangle(x, 0, Width - x - Theme.S(28), Height);
            var fore = BoxFore ?? Theme.Text;
            Theme.DrawText(g, it?.Text ?? Placeholder, Font, it == null ? Theme.Blend(fore, back, 0.45f) : Enabled ? fore : Theme.TextFaint, tr);
            if (BoxFore.HasValue) chev = Icons.Glyph(G.ChevronDown, Theme.S(12), Theme.Blend(fore, back, 0.35f));
            g.DrawImage(chev, Width - Theme.S(22), (Height - chev.Height) / 2);
        }
    }
}
