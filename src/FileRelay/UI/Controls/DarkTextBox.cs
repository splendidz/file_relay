using System;
using System.Drawing;
using System.Windows.Forms;

namespace FileRelay.UI
{
    /// <summary>테두리/포커스 강조가 있는 다크 입력 칸 (내부 TextBox 는 Box 로 접근).</summary>
    public class DarkTextBox : Control
    {
        public readonly TextBox Box;
        char _glyph;
        string _placeholder = "";
        bool _hover;
        public Color BoxColor = Theme.Input;
        public Color BorderColor = Theme.Border;

        public DarkTextBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = BoxColor,
                ForeColor = Theme.Text,
                Font = Theme.Ui,
            };
            Controls.Add(Box);
            Box.GotFocus += (s, e) => Invalidate();
            Box.LostFocus += (s, e) => Invalidate();
            Box.MouseEnter += (s, e) => { _hover = true; Invalidate(); };
            Box.MouseLeave += (s, e) => { _hover = false; Invalidate(); };
            Box.HandleCreated += (s, e) => ApplyCue();
            Height = Theme.S(32);
            Font = Theme.Ui;
            Cursor = Cursors.IBeam;
            TabStop = false;
        }

        public override string Text { get => Box.Text; set => Box.Text = value ?? ""; }

        public new event EventHandler TextChanged
        {
            add => Box.TextChanged += value;
            remove => Box.TextChanged -= value;
        }

        public char Glyph { get => _glyph; set { _glyph = value; PerformLayout(); Invalidate(); } }

        public string Placeholder
        {
            get => _placeholder;
            set { _placeholder = value ?? ""; ApplyCue(); }
        }

        public bool ReadOnly { get => Box.ReadOnly; set { Box.ReadOnly = value; } }

        public bool Multiline
        {
            get => Box.Multiline;
            set
            {
                Box.Multiline = value;
                Box.ScrollBars = value ? ScrollBars.Vertical : ScrollBars.None;
                Box.AcceptsReturn = value;
                if (value) Theme.ApplyControlTheme(Box);
                PerformLayout();
            }
        }

        public bool Password { set => Box.UseSystemPasswordChar = value; }

        /// <summary>오른쪽에 붙는 보조 버튼 등을 위한 여백.</summary>
        public int RightInset { get; set; }

        public void SetColors(Color box, Color fore, Color border)
        {
            BoxColor = box;
            BorderColor = border;
            Box.BackColor = box;
            Box.ForeColor = fore;
            Invalidate();
        }

        void ApplyCue()
        {
            if (Box.IsHandleCreated && !Box.Multiline)
                Native.SendMessage(Box.Handle, Native.EM_SETCUEBANNER, (IntPtr)1, _placeholder);
        }

        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Box.Font = Font; PerformLayout(); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int padX = Theme.S(10);
            int left = padX + (_glyph != '\0' ? Theme.S(22) : 0);
            if (Box.Multiline)
            {
                Box.SetBounds(left, Theme.S(7), Math.Max(10, Width - left - Theme.S(4) - RightInset), Math.Max(10, Height - Theme.S(12)));
            }
            else
            {
                int h = Box.PreferredHeight;
                Box.SetBounds(left, (Height - h) / 2 + 1, Math.Max(10, Width - left - padX - RightInset), h);
            }
        }

        protected override void OnClick(EventArgs e) { base.OnClick(e); Box.Focus(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Box.Focus(); }

        public new bool Focus() => Box.Focus();

        public void SelectAll() => Box.SelectAll();

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            Theme.HQ(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.FillRound(g, BoxColor, r, Theme.S(6));
            bool focus = Box.Focused;
            var bc = focus ? Theme.Accent : _hover ? Theme.Lift(BorderColor, 0.12f) : BorderColor;
            Theme.DrawRound(g, bc, r, Theme.S(6), focus ? 1.4f : 1f);
            if (_glyph != '\0')
            {
                var img = Icons.Glyph(_glyph, Theme.S(14), Theme.TextFaint);
                g.DrawImage(img, Theme.S(10), (Height - img.Height) / 2);
            }
        }
    }
}
