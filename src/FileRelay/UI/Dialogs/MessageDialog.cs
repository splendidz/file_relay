using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

using static FileRelay.L;
namespace FileRelay.UI
{
    public enum MsgKind { Info, Success, Warning, Error, Question, Danger }

    public struct DlgBtn
    {
        public string Text;
        public DialogResult Result;
        public ButtonKind Kind;
        public DlgBtn(string text, DialogResult result, ButtonKind kind = ButtonKind.Secondary) { Text = text; Result = result; Kind = kind; }
    }

    /// <summary>다크 메시지 상자 (아이콘, 제목, 본문, 상세, 체크 상자, 사용자 버튼).</summary>
    public sealed class MessageDialog : DialogBase
    {
        public DarkCheckBox Check;

        MessageDialog(string title, string message, MsgKind kind, string detail, string checkText, DlgBtn[] buttons)
        {
            Text = AppInfo.Name;
            int W = Theme.S(480);
            int pad = Theme.S(22);
            int iconSize = Theme.S(30);
            int textLeft = pad + iconSize + Theme.S(16);
            int textW = W - textLeft - pad;

            char glyph; Color color;
            switch (kind)
            {
                case MsgKind.Success: glyph = ''; color = Theme.Success; break;   // Completed
                case MsgKind.Warning: glyph = G.Warning; color = Theme.Warn; break;
                case MsgKind.Error: glyph = ''; color = Theme.Danger; break;      // ErrorBadge
                case MsgKind.Question: glyph = ''; color = Theme.Accent; break;   // Unknown(?)
                case MsgKind.Danger: glyph = G.Delete; color = Theme.Danger; break;
                default: glyph = G.Info; color = Theme.Info; break;
            }
            var icon = new PictureBox { Image = Icons.Glyph(glyph, iconSize, color), Size = new Size(iconSize, iconSize), Location = new Point(pad, Theme.S(20)), BackColor = Color.Transparent };
            Body.Controls.Add(icon);

            int y = Theme.S(18);
            var lt = new Label { Text = title, Font = Theme.Title, ForeColor = Theme.Text, AutoSize = false, UseMnemonic = false, Location = new Point(textLeft, y) };
            lt.Size = new Size(textW, TextRenderer.MeasureText(title, Theme.Title, new Size(textW, 999), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + Theme.S(2));
            Body.Controls.Add(lt);
            y = lt.Bottom + Theme.S(8);

            if (!string.IsNullOrEmpty(message))
            {
                var lm = new Label { Text = message, ForeColor = Theme.TextDim, AutoSize = false, UseMnemonic = false, Location = new Point(textLeft, y) };
                lm.Size = new Size(textW, TextRenderer.MeasureText(message, Theme.Ui, new Size(textW, 9999), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl).Height + Theme.S(4));
                Body.Controls.Add(lm);
                y = lm.Bottom + Theme.S(10);
            }
            if (!string.IsNullOrEmpty(detail))
            {
                int lines = Math.Min(9, detail.Split('\n').Length + 1);
                var tb = new DarkTextBox { Multiline = true, ReadOnly = true, Location = new Point(textLeft, y), Size = new Size(textW, Theme.S(14) + lines * Theme.Small.Height + Theme.S(6)) };
                tb.Box.Font = Theme.Small;
                tb.Box.ForeColor = Theme.TextDim;
                tb.Text = detail.Replace("\r\n", "\n").Replace("\n", "\r\n");
                Body.Controls.Add(tb);
                y = tb.Bottom + Theme.S(10);
            }
            if (!string.IsNullOrEmpty(checkText))
            {
                Check = new DarkCheckBox(checkText) { Location = new Point(textLeft, y) };
                Body.Controls.Add(Check);
                y = Check.Bottom + Theme.S(6);
            }
            y = Math.Max(y, Theme.S(20) + iconSize + Theme.S(10));
            ClientSize = new Size(W, y + Theme.S(10) + Footer.Height);

            if (buttons == null || buttons.Length == 0) buttons = new[] { new DlgBtn(T("common.ok"), DialogResult.OK, ButtonKind.Primary) };
            // buttons 는 화면 표시 순서(왼쪽 → 오른쪽). FlowLayout 이 오른쪽부터 쌓으므로 뒤에서부터 추가.
            for (int i = buttons.Length - 1; i >= 0; i--)
                AddButton(buttons[i].Text, buttons[i].Result, buttons[i].Kind);
            if (buttons.Length > 1)
            {
                // Esc 는 '취소'가 있으면 취소, 없으면 '나중에(Ignore)', 그다음 '아니요'.
                var cancel = buttons.Where(b => b.Result == DialogResult.Cancel)
                    .Concat(buttons.Where(b => b.Result == DialogResult.Ignore))
                    .Concat(buttons.Where(b => b.Result == DialogResult.No)).FirstOrDefault();
                if (cancel.Text != null) CancelButton = ButtonPanel.Controls.OfType<FlatButton>().First(b => b.DialogResult == cancel.Result);
            }
            foreach (Control c in ButtonPanel.Controls)
                if (c is FlatButton fb && (fb.Kind == ButtonKind.Primary || fb.Kind == ButtonKind.Danger)) AcceptButton = fb;
            if (CancelButton == null && buttons.Length == 1) CancelButton = (IButtonControl)ButtonPanel.Controls[0];
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (AcceptButton is Control c) c.Focus();
            if (Owner == null) { TopMost = true; Activate(); }
        }

        public static DialogResult Show(IWin32Window owner, string title, string message, MsgKind kind, string detail,
                                        string checkText, out bool checkState, params DlgBtn[] buttons)
        {
            using (var d = new MessageDialog(title, message, kind, detail, checkText, buttons))
            {
                if (owner == null) d.StartPosition = FormStartPosition.CenterScreen;
                var r = d.ShowDialog(owner);
                checkState = d.Check?.Checked ?? false;
                return r;
            }
        }

        public static DialogResult Show(IWin32Window owner, string title, string message, MsgKind kind = MsgKind.Info, string detail = null, params DlgBtn[] buttons) =>
            Show(owner, title, message, kind, detail, null, out _, buttons);

        public static void Info(IWin32Window owner, string title, string message, string detail = null) =>
            Show(owner, title, message, MsgKind.Info, detail);

        public static void Error(IWin32Window owner, string title, string message, string detail = null) =>
            Show(owner, title, message, MsgKind.Error, detail);

        public static void Warn(IWin32Window owner, string title, string message, string detail = null) =>
            Show(owner, title, message, MsgKind.Warning, detail);

        public static bool Confirm(IWin32Window owner, string title, string message, string okText = null, bool danger = false, string detail = null, MsgKind? kind = null) =>
            Show(owner, title, message, kind ?? (danger ? MsgKind.Danger : MsgKind.Question), detail,
                new DlgBtn(okText ?? T("common.ok"), DialogResult.OK, danger ? ButtonKind.Danger : ButtonKind.Primary),
                new DlgBtn(T("common.cancel"), DialogResult.Cancel)) == DialogResult.OK;
    }

    /// <summary>한 줄 입력 대화상자 (새 폴더, 이름 바꾸기 등).</summary>
    public sealed class InputDialog : DialogBase
    {
        readonly DarkTextBox _box;
        readonly Label _err;
        readonly Func<string, string> _validate;
        readonly bool _selectStem;

        InputDialog(string title, string prompt, string initial, bool selectStem, Func<string, string> validate)
        {
            Text = title;
            _validate = validate;
            _selectStem = selectStem;
            int W = Theme.S(440), pad = Theme.S(22);
            var lt = new Label { Text = title, Font = Theme.Title, ForeColor = Theme.Text, AutoSize = true, Location = new Point(pad, Theme.S(18)), UseMnemonic = false };
            Body.Controls.Add(lt);
            var lp = new Label { Text = prompt, ForeColor = Theme.TextDim, AutoSize = false, UseMnemonic = false, Location = new Point(pad, Theme.S(54)), Size = new Size(W - pad * 2, Theme.S(20)) };
            Body.Controls.Add(lp);
            _box = new DarkTextBox { Location = new Point(pad, Theme.S(78)), Size = new Size(W - pad * 2, Theme.S(34)), Text = initial ?? "" };
            Body.Controls.Add(_box);
            _err = new Label { ForeColor = Theme.Danger, AutoSize = false, UseMnemonic = false, Location = new Point(pad, Theme.S(116)), Size = new Size(W - pad * 2, Theme.S(20)), Font = Theme.Small };
            Body.Controls.Add(_err);
            ClientSize = new Size(W, Theme.S(146) + Footer.Height);
            AddButton(T("common.cancel"), DialogResult.Cancel);
            var ok = AddButton(T("common.ok"), DialogResult.None, ButtonKind.Primary);
            ok.Click += (s, e) => TryAccept();
            AcceptButton = ok;
            _box.Box.TextChanged += (s, e) => _err.Text = "";
        }

        void TryAccept()
        {
            string v = _box.Text.Trim();
            string err = _validate?.Invoke(v);
            if (err != null) { _err.Text = err; _box.Focus(); return; }
            DialogResult = DialogResult.OK;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _box.Focus();
            string t = _box.Text;
            int dot = t.LastIndexOf('.');
            if (_selectStem && dot > 0) _box.Box.Select(0, dot);
            else _box.Box.SelectAll();
        }

        public static string Ask(IWin32Window owner, string title, string prompt, string initial = "", bool selectStem = false, Func<string, string> validate = null)
        {
            using (var d = new InputDialog(title, prompt, initial, selectStem, validate))
            {
                if (owner == null) d.StartPosition = FormStartPosition.CenterScreen;
                return d.ShowDialog(owner) == DialogResult.OK ? d._box.Text.Trim() : null;
            }
        }
    }
}
