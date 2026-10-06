using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace FileRelay.UI
{
    public enum ButtonKind { Secondary, Primary, Danger, Ghost, Link }

    /// <summary>둥근 모서리 평면 버튼 (아이콘 + 글자). Form.AcceptButton/CancelButton 으로 쓸 수 있다.</summary>
    public class FlatButton : Control, IButtonControl
    {
        bool _hover, _down, _default;
        ButtonKind _kind = ButtonKind.Secondary;
        char _glyph;

        public FlatButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);
            Cursor = Cursors.Hand;
            Font = Theme.Ui;
            ForeColor = Theme.Text;
            Size = new Size(Theme.S(92), Theme.S(32));
            TabStop = true;
        }

        public FlatButton(string text, ButtonKind kind = ButtonKind.Secondary, char glyph = '\0') : this()
        {
            Text = text;
            _kind = kind;
            _glyph = glyph;
            AutoFit();
        }

        public ButtonKind Kind { get => _kind; set { _kind = value; Invalidate(); } }
        public char Glyph { get => _glyph; set { _glyph = value; Invalidate(); } }
        public int Radius { get; set; } = Theme.S(6);
        public bool Toggled { get; set; }
        public Color? GlyphColor { get; set; }
        /// <summary>쪽지 창처럼 배경색이 다른 곳에 놓일 때 버튼 기본색을 덮어쓴다.</summary>
        public Color? CustomBack { get; set; }
        public Color? CustomHover { get; set; }
        public Color? CustomFore { get; set; }

        [DefaultValue(DialogResult.None)]
        public DialogResult DialogResult { get; set; }

        public void NotifyDefault(bool value) { _default = value; Invalidate(); }

        public void PerformClick()
        {
            if (CanSelect || Visible && Enabled) OnClick(EventArgs.Empty);
        }

        public void AutoFit(int minWidth = 0)
        {
            int w = Theme.S(16);
            if (_glyph != '\0') w += Theme.S(16) + (string.IsNullOrEmpty(Text) ? 0 : Theme.S(6));
            if (!string.IsNullOrEmpty(Text)) w += Theme.Measure(Text, Font).Width + Theme.S(8);
            if (string.IsNullOrEmpty(Text) && _glyph != '\0') w = Height;
            Width = Math.Max(w, minWidth);
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        protected override void OnClick(EventArgs e)
        {
            var f = FindForm();
            if (f != null && DialogResult != DialogResult.None) f.DialogResult = DialogResult;
            base.OnClick(e);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override bool IsInputKey(Keys keyData) => keyData == Keys.Enter || keyData == Keys.Space || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { PerformClick(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color parentBg = Parent?.BackColor ?? Theme.Bg;
            g.Clear(parentBg);
            Theme.HQ(g);

            Color back, fore;
            switch (_kind)
            {
                case ButtonKind.Primary:
                    back = _down ? Theme.AccentPressed : _hover ? Theme.AccentHover : Theme.Accent;
                    fore = Color.White;
                    break;
                case ButtonKind.Danger:
                    back = _down ? Theme.Blend(Theme.Danger, Color.Black, 0.15f) : _hover ? Theme.DangerHover : Theme.Danger;
                    fore = Color.White;
                    break;
                case ButtonKind.Ghost:
                    back = _down ? Theme.Pressed : _hover || Toggled ? Theme.Hover : parentBg;
                    if (Toggled) back = _hover ? Theme.Lift(Theme.Selection, 0.06f) : Theme.Selection;
                    fore = Theme.Text;
                    break;
                case ButtonKind.Link:
                    back = parentBg;
                    fore = _hover ? Theme.AccentHover : Theme.Accent;
                    break;
                default:
                    back = _down ? Theme.Pressed : _hover ? Theme.Lift(Theme.Raised, 0.06f) : Theme.Raised;
                    fore = Theme.Text;
                    break;
            }
            if (CustomBack.HasValue && _kind != ButtonKind.Primary)
            {
                back = _down ? Theme.Blend(CustomBack.Value, Color.Black, 0.12f) : _hover ? (CustomHover ?? Theme.Blend(CustomBack.Value, Color.Black, 0.07f)) : CustomBack.Value;
            }
            if (CustomFore.HasValue) fore = CustomFore.Value;
            if (!Enabled)
            {
                back = _kind == ButtonKind.Ghost || _kind == ButtonKind.Link ? parentBg : Theme.Blend(back, parentBg, 0.55f);
                fore = Theme.Blend(fore, parentBg, 0.55f);
            }

            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            if (back != parentBg) Theme.FillRound(g, back, r, Radius);
            if (_kind == ButtonKind.Secondary && Enabled && !CustomBack.HasValue)
                Theme.DrawRound(g, Theme.Lift(Theme.Border, 0.04f), r, Radius);
            if (Focused && ShowFocusCues && _kind != ButtonKind.Link)
                Theme.DrawRound(g, Theme.Blend(Theme.Accent, Color.White, 0.25f), RectangleF.Inflate(r, -1.5f, -1.5f), Radius - 1, 1.5f);
            else if (_default && Enabled && _kind == ButtonKind.Secondary)
                Theme.DrawRound(g, Theme.Accent, r, Radius);

            int gs = Theme.S(16);
            var textSize = string.IsNullOrEmpty(Text) ? Size.Empty : Theme.Measure(Text, Font);
            int contentW = (_glyph != '\0' ? gs : 0) + (_glyph != '\0' && textSize.Width > 0 ? Theme.S(6) : 0) + textSize.Width;
            int x = (Width - contentW) / 2;
            if (_glyph != '\0')
            {
                var img = Icons.Glyph(_glyph, gs, Enabled ? (GlyphColor ?? fore) : fore);
                g.DrawImage(img, x, (Height - gs) / 2);
                x += gs + (textSize.Width > 0 ? Theme.S(6) : 0);
            }
            if (textSize.Width > 0)
            {
                var tr = new Rectangle(x, 0, Width - x, Height);
                TextRenderer.DrawText(g, Text, Font, tr, fore, back, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
        }
    }
}
