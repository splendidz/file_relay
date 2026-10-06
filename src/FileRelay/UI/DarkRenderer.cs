using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FileRelay.UI
{
    sealed class DarkColors : ProfessionalColorTable
    {
        public DarkColors() { UseSystemColors = false; }
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Theme.Hover;
        public override Color MenuItemSelectedGradientBegin => Theme.Hover;
        public override Color MenuItemSelectedGradientEnd => Theme.Hover;
        public override Color MenuItemPressedGradientBegin => Theme.Raised;
        public override Color MenuItemPressedGradientEnd => Theme.Raised;
        public override Color MenuStripGradientBegin => Theme.Chrome;
        public override Color MenuStripGradientEnd => Theme.Chrome;
        public override Color ToolStripDropDownBackground => Theme.MenuBg;
        public override Color ImageMarginGradientBegin => Theme.MenuBg;
        public override Color ImageMarginGradientMiddle => Theme.MenuBg;
        public override Color ImageMarginGradientEnd => Theme.MenuBg;
        public override Color ToolStripBorder => Theme.Chrome;
        public override Color ToolStripGradientBegin => Theme.Chrome;
        public override Color ToolStripGradientMiddle => Theme.Chrome;
        public override Color ToolStripGradientEnd => Theme.Chrome;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Chrome;
        public override Color StatusStripGradientBegin => Theme.Chrome;
        public override Color StatusStripGradientEnd => Theme.Chrome;
        public override Color ButtonSelectedBorder => Color.Transparent;
        public override Color ButtonSelectedHighlight => Theme.Hover;
        public override Color ButtonSelectedGradientBegin => Theme.Hover;
        public override Color ButtonSelectedGradientEnd => Theme.Hover;
        public override Color ButtonPressedGradientBegin => Theme.Pressed;
        public override Color ButtonPressedGradientEnd => Theme.Pressed;
        public override Color ButtonCheckedGradientBegin => Theme.Selection;
        public override Color ButtonCheckedGradientEnd => Theme.Selection;
        public override Color CheckBackground => Theme.Selection;
        public override Color CheckSelectedBackground => Theme.Selection;
        public override Color OverflowButtonGradientBegin => Theme.Chrome;
        public override Color OverflowButtonGradientEnd => Theme.Chrome;
        public override Color OverflowButtonGradientMiddle => Theme.Chrome;
        public override Color GripDark => Theme.Border;
        public override Color GripLight => Theme.Chrome;
    }

    /// <summary>메뉴/툴바/상태 표시줄/컨텍스트 메뉴 공용 다크 렌더러.</summary>
    public sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            Color c = e.ToolStrip is ToolStripDropDown ? Theme.MenuBg : e.ToolStrip.BackColor;
            using (var b = new SolidBrush(c)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (e.ToolStrip is ToolStripDropDown)
            {
                if (Native.IsWin11) return; // DWM 이 둥근 테두리를 그린다
                var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
                using (var p = new Pen(Theme.Border)) e.Graphics.DrawRectangle(p, r);
                return;
            }
            if (e.ToolStrip is StatusStrip)
            {
                using (var p = new Pen(Theme.BorderSoft)) e.Graphics.DrawLine(p, 0, 0, e.ToolStrip.Width, 0);
                return;
            }
            if (e.ToolStrip is MenuStrip) return;
            using (var p = new Pen(Theme.BorderSoft)) e.Graphics.DrawLine(p, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var item = e.Item;
            if (!item.Selected && !item.Pressed) return;
            if (!item.Enabled && !(item.Owner is MenuStrip)) return;
            var g = e.Graphics;
            Theme.HQ(g);
            Rectangle r;
            if (item.IsOnDropDown)
                r = new Rectangle(Theme.S(4), 1, item.Width - Theme.S(8), item.Height - 2);
            else
                r = new Rectangle(1, Theme.S(2), item.Width - 2, item.Height - Theme.S(4));
            var c = item.Pressed && !item.IsOnDropDown ? Theme.Raised : Theme.Hover;
            Theme.FillRound(g, c, r, Theme.S(4));
        }

        void ButtonBg(ToolStripItemRenderEventArgs e, bool isChecked)
        {
            var item = e.Item;
            bool hot = item.Selected || item.Pressed;
            if (!hot && !isChecked) return;
            if (!item.Enabled) return;
            var g = e.Graphics;
            Theme.HQ(g);
            var r = new Rectangle(1, Theme.S(3), item.Width - 2, item.Height - Theme.S(6));
            Color c = item.Pressed ? Theme.Pressed : isChecked ? (hot ? Theme.Lift(Theme.Selection, 0.06f) : Theme.Selection) : Theme.Hover;
            Theme.FillRound(g, c, r, Theme.S(5));
        }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e) =>
            ButtonBg(e, e.Item is ToolStripButton b && b.Checked);

        protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e) => ButtonBg(e, false);

        protected override void OnRenderSplitButtonBackground(ToolStripItemRenderEventArgs e)
        {
            ButtonBg(e, false);
            if (e.Item is ToolStripSplitButton sb)
            {
                var r = sb.DropDownButtonBounds;
                DrawArrow(new ToolStripArrowRenderEventArgs(e.Graphics, sb, r, Theme.TextDim, ArrowDirection.Down));
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? Theme.TextFaint :
                          e.Item.Tag is Color tc ? tc :
                          e.Item.Owner is StatusStrip ? Theme.TextDim : Theme.Text;
            // 메뉴의 단축키 글자는 흐리게.
            if (e.Item.Enabled && e.Item.IsOnDropDown && e.Item is ToolStripMenuItem mi && e.Text != mi.Text)
                e.TextColor = Theme.TextFaint;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item != null && !e.Item.Enabled ? Theme.TextFaint : Theme.TextDim;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var g = e.Graphics;
            var item = e.Item;
            using (var p = new Pen(Theme.Border))
            {
                if (e.Vertical || item.Owner is ToolStrip && !(item.Owner is ToolStripDropDown) && !(item.Owner is MenuStrip))
                {
                    int x = item.Width / 2;
                    g.DrawLine(p, x, Theme.S(8), x, item.Height - Theme.S(8));
                }
                else
                {
                    int y = item.Height / 2;
                    g.DrawLine(p, Theme.S(10), y, item.Width - Theme.S(10), y);
                }
            }
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(Theme.MenuBg)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            Theme.HQ(g);
            var r = e.ImageRectangle;
            r.Inflate(Theme.S(2), Theme.S(2));
            Theme.FillRound(g, Theme.Selection, r, Theme.S(4));
            var img = Icons.Glyph(G.Check, Theme.S(12), Theme.Text);
            g.DrawImage(img, r.X + (r.Width - img.Width) / 2, r.Y + (r.Height - img.Height) / 2);
        }

        protected override void OnRenderStatusStripSizingGrip(ToolStripRenderEventArgs e) { }

        protected override void OnRenderToolStripPanelBackground(ToolStripPanelRenderEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Chrome)) e.Graphics.FillRectangle(b, e.ToolStripPanel.ClientRectangle);
            e.Handled = true;
        }

        /// <summary>드롭다운(메뉴/컨텍스트 메뉴)에 Win11 둥근 모서리를 적용.</summary>
        public static void Polish(ToolStripDropDown dd)
        {
            if (dd == null) return;
            dd.Opening -= OnDropDownOpening;
            dd.Opening += OnDropDownOpening;
            dd.Padding = new Padding(0, Theme.S(4), 0, Theme.S(4));
            foreach (ToolStripItem it in dd.Items)
                if (it is ToolStripDropDownItem di) Polish(di.DropDown);
        }

        public static void Polish(ToolStrip ts)
        {
            foreach (ToolStripItem it in ts.Items)
                if (it is ToolStripDropDownItem di) Polish(di.DropDown);
        }

        static void OnDropDownOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            var dd = (ToolStripDropDown)sender;
            Theme.RoundCorners(dd.Handle, true, Theme.Border);
            foreach (ToolStripItem it in dd.Items)
                if (it is ToolStripDropDownItem di) Polish(di.DropDown);
        }
    }
}
