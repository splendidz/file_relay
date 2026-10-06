using System;
using System.Drawing;
using System.Windows.Forms;

namespace FileRelay.UI
{
    /// <summary>켜기/끄기 스위치 (부드러운 애니메이션).</summary>
    public class ToggleSwitch : Control
    {
        bool _checked, _hover;
        float _pos;          // 0 = 꺼짐, 1 = 켜짐
        readonly Timer _anim = new Timer { Interval = 15 };

        public event EventHandler CheckedChanged;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Size = new Size(Theme.S(42), Theme.S(22));
            Cursor = Cursors.Hand;
            TabStop = true;
            _anim.Tick += (s, e) =>
            {
                float target = _checked ? 1 : 0;
                _pos += (target - _pos) * 0.35f;
                if (Math.Abs(target - _pos) < 0.02f) { _pos = target; _anim.Stop(); }
                Invalidate();
            };
        }

        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value) return;
                _checked = value;
                if (IsHandleCreated && Visible) _anim.Start();
                else { _pos = value ? 1 : 0; Invalidate(); }
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _anim.Dispose();
            base.Dispose(disposing);
        }

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
            float h = Height - 2, w = Width - 2;
            var r = new RectangleF(1, 1, w, h);
            var offBack = _hover ? Theme.Lift(Theme.Raised, 0.06f) : Theme.Raised;
            var onBack = _hover ? Theme.AccentHover : Theme.Accent;
            var back = Theme.Blend(offBack, onBack, _pos);
            if (!Enabled) back = Theme.Blend(back, Theme.Bg, 0.5f);
            Theme.FillRound(g, back, r, h / 2);
            if (_pos < 0.5f) Theme.DrawRound(g, Theme.Lift(Theme.Border, 0.1f), r, h / 2);
            if (Focused && ShowFocusCues) Theme.DrawRound(g, Theme.AccentHover, RectangleF.Inflate(r, 0.5f, 0.5f), h / 2, 1.2f);
            float d = h - Theme.S(6);
            float x = 1 + Theme.S(3) + (w - d - Theme.S(6)) * _pos;
            var knob = Theme.Blend(Theme.TextDim, Color.White, _pos);
            using (var b = new SolidBrush(Enabled ? knob : Theme.TextFaint)) g.FillEllipse(b, x, 1 + Theme.S(3), d, d);
        }
    }
}
