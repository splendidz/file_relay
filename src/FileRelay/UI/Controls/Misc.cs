using System;
using System.Drawing;
using System.Windows.Forms;

namespace FileRelay.UI
{
    /// <summary>깜빡임 없는 패널 (직접 그리는 영역용).</summary>
    public class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }
    }

    /// <summary>한 줄 구분선.</summary>
    public class Divider : Control
    {
        public bool Vertical;

        public Divider(bool vertical = false)
        {
            Vertical = vertical;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Theme.BorderSoft;
            if (vertical) Width = 1; else Height = 1;
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e) => e.Graphics.Clear(BackColor);
    }

    /// <summary>제목 + 부제 + 왼쪽 이미지 + 오른쪽 '칩' 을 그리는 머리글 막대.</summary>
    public class HeaderBar : BufferedPanel
    {
        string _title = "", _sub = "", _chip = "";
        Image _image;
        Color _chipColor = Theme.Raised, _chipFore = Theme.TextDim;
        public bool BottomLine = true;

        public HeaderBar()
        {
            BackColor = Theme.Panel;
            Height = Theme.S(44);
        }

        public void Set(string title, string sub, Image image = null)
        {
            if (_title == (title ?? "") && _sub == (sub ?? "") && ReferenceEquals(_image, image)) return;
            _title = title ?? "";
            _sub = sub ?? "";
            _image = image;
            Invalidate();
        }

        public void SetChip(string text, Color back, Color fore)
        {
            if (_chip == (text ?? "") && _chipColor == back && _chipFore == fore) return;
            _chip = text ?? "";
            _chipColor = back;
            _chipFore = fore;
            Invalidate();
        }

        public int ChipRightInset { get; set; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            Theme.HQ(g);
            int x = Theme.S(12);
            if (_image != null)
            {
                g.DrawImage(_image, x, (Height - _image.Height) / 2, _image.Width, _image.Height);
                x += _image.Width + Theme.S(10);
            }
            int chipW = 0;
            if (!string.IsNullOrEmpty(_chip))
            {
                var cs = Theme.Measure(_chip, Theme.Small);
                chipW = cs.Width + Theme.S(18);
                var cr = new Rectangle(Width - chipW - Theme.S(12) - ChipRightInset, (Height - Theme.S(22)) / 2, chipW, Theme.S(22));
                Theme.FillRound(g, _chipColor, cr, Theme.S(11));
                Theme.DrawText(g, _chip, Theme.Small, _chipFore, cr, Theme.TextCenter);
                chipW += Theme.S(16) + ChipRightInset;
            }
            int avail = Width - x - chipW - Theme.S(8);
            if (string.IsNullOrEmpty(_sub))
            {
                Theme.DrawText(g, _title, Theme.UiBold, Theme.Text, new Rectangle(x, 0, avail, Height));
            }
            else
            {
                int lh = Theme.UiBold.Height;
                int sh = Theme.Small.Height;
                int top = (Height - lh - sh) / 2;
                Theme.DrawText(g, _title, Theme.UiBold, Theme.Text, new Rectangle(x, top, avail, lh));
                Theme.DrawText(g, _sub, Theme.Small, Theme.TextDim, new Rectangle(x, top + lh, avail, sh));
            }
            if (BottomLine)
                using (var p = new Pen(Theme.BorderSoft)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }
    }

    /// <summary>상태 표시줄용 얇은 진행 막대 + 글자.</summary>
    public class ToolStripProgress : ToolStripItem
    {
        double _value;
        bool _active;

        public ToolStripProgress()
        {
            AutoSize = false;
            Width = Theme.S(260);
        }

        public void SetValue(double percent, bool active, string text)
        {
            _value = Math.Max(0, Math.Min(100, percent));
            _active = active;
            Text = text;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.HQ(g);
            int barW = Theme.S(110);
            var bar = new RectangleF(Theme.S(4), (Height - Theme.S(6)) / 2f, barW, Theme.S(6));
            if (_active)
            {
                Theme.FillRound(g, Theme.Raised, bar, Theme.S(3));
                var fill = new RectangleF(bar.X, bar.Y, (float)(bar.Width * _value / 100.0), bar.Height);
                if (fill.Width > 1) Theme.FillRound(g, Theme.Accent, fill, Theme.S(3));
            }
            var tr = new Rectangle((int)bar.Right + Theme.S(8), 0, Width - (int)bar.Right - Theme.S(8), Height);
            if (!_active) tr = new Rectangle(Theme.S(4), 0, Width - Theme.S(4), Height);
            TextRenderer.DrawText(g, Text ?? "", Theme.Small, tr, _active ? Theme.Text : Theme.TextDim, Theme.TextLeft);
        }
    }

    /// <summary>설정 화면의 한 줄: 제목 + 설명 (왼쪽), 컨트롤 (오른쪽).</summary>
    public class SettingRow : BufferedPanel
    {
        public readonly Control Editor;
        readonly string _title, _desc;
        readonly int _editorWidth;

        public SettingRow(string title, string desc, Control editor, int editorWidth = 0)
        {
            _title = title;
            _desc = desc ?? "";
            Editor = editor;
            _editorWidth = editorWidth;
            BackColor = Theme.Bg;
            Height = string.IsNullOrEmpty(_desc) ? Theme.S(46) : Theme.S(58);
            Controls.Add(editor);
            Dock = DockStyle.Top;
        }

        bool _sizing;
        const TextFormatFlags DescFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;

        int DescHeight(int width) =>
            string.IsNullOrEmpty(_desc) ? 0 : TextRenderer.MeasureText(_desc, Theme.Small, new Size(Math.Max(60, width), int.MaxValue), DescFlags).Height;

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Editor == null) return;
            int w = _editorWidth > 0 ? _editorWidth : Editor.Width;
            Editor.Width = w;
            int textW = Width - w - Theme.S(20);
            int need = string.IsNullOrEmpty(_desc)
                ? Theme.S(46)
                : Theme.S(24) + Theme.Ui.Height + Theme.S(3) + DescHeight(textW);
            need = Math.Max(need, Editor.Height + Theme.S(16));
            if (!_sizing && Width > 0 && need != Height)
            {
                _sizing = true;
                Height = need;
                _sizing = false;
            }
            Editor.Location = new Point(Width - w - Theme.S(4), (Height - Editor.Height) / 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            int right = (Editor?.Left ?? Width) - Theme.S(16);
            int lh = Theme.Ui.Height;
            if (string.IsNullOrEmpty(_desc))
                Theme.DrawText(g, _title, Theme.Ui, Theme.Text, new Rectangle(0, 0, right, Height));
            else
            {
                int dh = DescHeight(right);
                int top = (Height - lh - dh - Theme.S(3)) / 2;
                Theme.DrawText(g, _title, Theme.Ui, Theme.Text, new Rectangle(0, top, right, lh));
                TextRenderer.DrawText(g, _desc, Theme.Small, new Rectangle(0, top + lh + Theme.S(3), right, dh + Theme.S(2)), Theme.TextFaint, DescFlags);
            }
            using (var p = new Pen(Theme.BorderSoft)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }
    }

    /// <summary>설정 화면의 구역 제목.</summary>
    public class SectionTitle : BufferedPanel
    {
        readonly string _text;

        public SectionTitle(string text)
        {
            _text = text;
            Height = Theme.S(40);
            Dock = DockStyle.Top;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            Theme.DrawText(e.Graphics, _text, Theme.SmallBold, Theme.Accent, new Rectangle(0, Theme.S(14), Width, Height - Theme.S(14)));
        }
    }

    /// <summary>여러 줄 안내 문구.</summary>
    public class NoteLabel : Label
    {
        public NoteLabel(string text, Color? color = null)
        {
            Text = text;
            ForeColor = color ?? Theme.TextDim;
            BackColor = Color.Transparent;
            Font = Theme.Small;
            AutoSize = false;
            UseMnemonic = false;
        }

        public void FitHeight(int width)
        {
            Width = width;
            var sz = TextRenderer.MeasureText(Text, Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            Height = sz.Height + Theme.S(4);
        }
    }
}
