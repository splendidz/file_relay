using System;
using System.Drawing;
using System.Windows.Forms;

namespace FileRelay.UI
{
    /// <summary>다크 제목 표시줄을 쓰는 기본 폼.</summary>
    public class DarkForm : Form
    {
        public DarkForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui;
            Icon = Icons.App();
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;
            ShowInTaskbar = false;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.StyleTitleBar(Handle, CaptionColor);
        }

        protected virtual Color CaptionColor => Theme.Bg;
    }

    /// <summary>
    /// 표준 대화상자 틀: 위쪽 본문(Body), 아래쪽 버튼 막대(Footer).
    /// 버튼은 AddButton 으로 오른쪽부터 쌓인다.
    /// </summary>
    public class DialogBase : DarkForm
    {
        public readonly Panel Body;
        public readonly Panel Footer;
        readonly FlowLayoutPanel _buttons;
        public readonly FlowLayoutPanel FooterLeft;
        public FlowLayoutPanel ButtonPanel => _buttons;

        public DialogBase()
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Padding = Padding.Empty;

            Footer = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(60), BackColor = Theme.Panel, Padding = new Padding(Theme.S(16), Theme.S(14), Theme.S(16), Theme.S(14)) };
            _buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                BackColor = Theme.Panel,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
            };
            FooterLeft = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                BackColor = Theme.Panel,
            };
            Footer.Controls.Add(_buttons);
            Footer.Controls.Add(FooterLeft);
            Footer.Paint += (s, e) => { using (var p = new Pen(Theme.BorderSoft)) e.Graphics.DrawLine(p, 0, 0, Footer.Width, 0); };

            Body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(Theme.S(22), Theme.S(18), Theme.S(22), Theme.S(14)) };
            Controls.Add(Body);
            Controls.Add(Footer);
        }

        public FlatButton AddButton(string text, DialogResult result, ButtonKind kind = ButtonKind.Secondary, char glyph = '\0')
        {
            var b = new FlatButton(text, kind, glyph) { DialogResult = result, Margin = new Padding(Theme.S(8), 0, 0, 0) };
            b.AutoFit(Theme.S(88));
            _buttons.Controls.Add(b);
            if (result == DialogResult.OK || result == DialogResult.Yes) { if (AcceptButton == null) AcceptButton = b; }
            if (result == DialogResult.Cancel || result == DialogResult.No) CancelButton = b;
            return b;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && CancelButton == null) { DialogResult = DialogResult.Cancel; e.Handled = true; }
            base.OnKeyDown(e);
        }
    }

    /// <summary>다크 체크 상자.</summary>
    public class DarkCheckBox : Control
    {
        bool _checked, _hover;
        public event EventHandler CheckedChanged;

        public DarkCheckBox(string text = "")
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            Text = text;
            Font = Theme.Ui;
            ForeColor = Theme.Text;
            Height = Theme.S(26);
            Cursor = Cursors.Hand;
            TabStop = true;
            AutoFit();
        }

        public void AutoFit() => Width = Theme.S(28) + Theme.Measure(Text, Font).Width + Theme.S(6);

        public bool Checked
        {
            get => _checked;
            set { if (_checked != value) { _checked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); } }
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnClick(EventArgs e) { Focus(); Checked = !Checked; base.OnClick(e); }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override bool IsInputKey(Keys k) => k == Keys.Space || base.IsInputKey(k);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            Theme.HQ(g);
            int bs = Theme.S(16);
            var box = new RectangleF(1, (Height - bs) / 2f, bs, bs);
            if (_checked)
            {
                Theme.FillRound(g, Enabled ? (_hover ? Theme.AccentHover : Theme.Accent) : Theme.TextFaint, box, Theme.S(4));
                var img = Icons.Glyph(G.Check, Theme.S(11), Color.White);
                g.DrawImage(img, box.X + (box.Width - img.Width) / 2, box.Y + (box.Height - img.Height) / 2);
            }
            else
            {
                Theme.FillRound(g, Theme.Input, box, Theme.S(4));
                Theme.DrawRound(g, _hover ? Theme.Lift(Theme.Border, 0.25f) : Theme.Lift(Theme.Border, 0.1f), box, Theme.S(4), 1.2f);
            }
            if (Focused && ShowFocusCues) Theme.DrawRound(g, Theme.AccentHover, RectangleF.Inflate(box, 2, 2), Theme.S(5));
            var tr = new Rectangle(bs + Theme.S(10), 0, Width - bs - Theme.S(10), Height);
            Theme.DrawText(g, Text, Font, Enabled ? ForeColor : Theme.TextFaint, tr);
        }
    }

    /// <summary>라디오 선택지 (설명 줄 포함, 비활성 사유 표시 가능).</summary>
    public class RadioOption : Control
    {
        bool _checked, _hover;
        public string Description = "";
        public object Value;
        public event EventHandler CheckedChanged;

        public RadioOption(string text, string desc, object value)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Text = text;
            Description = desc ?? "";
            Value = value;
            Font = Theme.Ui;
            Height = string.IsNullOrEmpty(Description) ? Theme.S(30) : Theme.S(44);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value) return;
                _checked = value;
                if (value && Parent != null)
                    foreach (Control c in Parent.Controls)
                        if (c is RadioOption r && r != this) r.Checked = false;
                Invalidate();
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnClick(EventArgs e) { if (Enabled) { Focus(); Checked = true; } base.OnClick(e); }
        protected override void OnDoubleClick(EventArgs e) { base.OnDoubleClick(e); if (Enabled) (FindForm()?.AcceptButton as IButtonControl)?.PerformClick(); }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override bool IsInputKey(Keys k) => k == Keys.Space || k == Keys.Up || k == Keys.Down || base.IsInputKey(k);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { Checked = true; e.Handled = true; }
            else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
            {
                Parent?.SelectNextControl(this, e.KeyCode == Keys.Down, true, false, true);
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var bg = Parent?.BackColor ?? Theme.Bg;
            g.Clear(bg);
            Theme.HQ(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            if (_checked) Theme.FillRound(g, Theme.Blend(bg, Theme.Accent, 0.14f), r, Theme.S(6));
            else if (_hover && Enabled) Theme.FillRound(g, Theme.Hover, r, Theme.S(6));
            if (Focused && ShowFocusCues) Theme.DrawRound(g, Theme.Blend(Theme.Accent, bg, 0.3f), r, Theme.S(6));

            int d = Theme.S(16);
            int top = string.IsNullOrEmpty(Description) ? (Height - d) / 2 : Theme.S(7);
            var c = new RectangleF(Theme.S(10), top, d, d);
            var ring = Enabled ? (_checked ? Theme.Accent : Theme.Lift(Theme.Border, 0.2f)) : Theme.TextFaint;
            using (var p = new Pen(ring, 1.6f)) g.DrawEllipse(p, c);
            if (_checked) using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, RectangleF.Inflate(c, -d * 0.27f, -d * 0.27f));

            int x = Theme.S(10) + d + Theme.S(10);
            var fore = Enabled ? Theme.Text : Theme.TextFaint;
            if (string.IsNullOrEmpty(Description))
                Theme.DrawText(g, Text, Font, fore, new Rectangle(x, 0, Width - x - Theme.S(6), Height));
            else
            {
                Theme.DrawText(g, Text, Font, fore, new Rectangle(x, Theme.S(4), Width - x - Theme.S(6), Theme.S(20)));
                Theme.DrawText(g, Description, Theme.Small, Enabled ? Theme.TextDim : Theme.TextFaint, new Rectangle(x, Theme.S(22), Width - x - Theme.S(6), Theme.S(18)));
            }
        }
    }
}
