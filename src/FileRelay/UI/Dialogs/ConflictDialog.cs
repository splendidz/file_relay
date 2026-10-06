using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

using static FileRelay.L;
namespace FileRelay.UI
{
    /// <summary>전송 충돌(같은 이름 존재) 처리 선택 — FileZilla 방식.</summary>
    public sealed class ConflictDialog : DialogBase
    {
        static string _last = "overwrite";
        readonly ConflictRequest _req;
        readonly DarkCheckBox _all;
        readonly Panel _opts;

        public ConflictDialog(ConflictRequest req)
        {
            _req = req;
            var it = req.Item;
            Text = T("cdlg.title");
            int W = Theme.S(520), pad = Theme.S(22), y = Theme.S(18);
            var icon = new PictureBox { Image = Icons.Glyph(G.Warning, Theme.S(28), Theme.Warn), Size = new Size(Theme.S(28), Theme.S(28)), Location = new Point(pad, y + Theme.S(2)) };
            Body.Controls.Add(icon);
            int tl = pad + Theme.S(42);
            Body.Controls.Add(new Label { Text = T("cdlg.heading"), Font = Theme.Title, ForeColor = Theme.Text, AutoSize = true, Location = new Point(tl, y) });
            y += Theme.S(32);
            Body.Controls.Add(new Label { Text = it.Name, Font = Theme.UiBold, ForeColor = Theme.Accent, AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Location = new Point(tl, y), Size = new Size(W - tl - pad, Theme.S(22)) });
            y += Theme.S(30);

            bool down = it.Dir == TDir.Download;
            string srcWho = down ? it.PeerName : T("pane.thisPc");
            string dstWho = down ? T("pane.thisPc") : it.PeerName;
            var card = new BufferedPanel { Location = new Point(pad, y), Size = new Size(W - pad * 2, Theme.S(72)), BackColor = Theme.Panel };
            card.Paint += (s, e) =>
            {
                var g = e.Graphics;
                Theme.HQ(g);
                g.Clear(Theme.Bg);
                Theme.FillRound(g, Theme.Panel, new RectangleF(0, 0, card.Width - 1, card.Height - 1), Theme.S(8));
                int rowH = Theme.S(28), x0 = Theme.S(14), x1 = Theme.S(150);
                bool newer = it.SrcTime > req.TargetTime.AddSeconds(2);
                bool older = it.SrcTime < req.TargetTime.AddSeconds(-2);
                DrawRow(g, Theme.S(8), T("cdlg.source", srcWho), it.Size, it.SrcTime, newer ? T("cdlg.newer") : null, Theme.Success, x0, x1, rowH);
                DrawRow(g, Theme.S(8) + rowH, T("cdlg.target", dstWho), req.TargetSize, req.TargetTime, older ? T("cdlg.newer") : null, Theme.Warn, x0, x1, rowH);
            };
            Body.Controls.Add(card);
            y = card.Bottom + Theme.S(14);

            _opts = new Panel { Location = new Point(pad, y), Size = new Size(W - pad * 2, 10), BackColor = Theme.Bg };
            string noPerm = T("cdlg.noPerm");
            AddOpt("overwrite", T("conflict.overwrite"), T("cdlg.overwriteDesc"), req.CanOverwrite, noPerm);
            AddOpt("overwriteIfNewer", T("conflict.overwriteIfNewer"), T("cdlg.newerDesc"), req.CanOverwrite, noPerm);
            AddOpt("overwriteIfDifferentSize", T("conflict.overwriteIfDifferentSize"), T("cdlg.sizeDesc"), req.CanOverwrite, noPerm);
            AddOpt("resume", T("conflict.resume"), T("cdlg.resumeDesc", Fmt.Size(req.TargetSize)),
                req.CanResume && req.CanOverwrite, !req.CanOverwrite ? noPerm : T("cdlg.resumeOnlySmaller"));
            AddOpt("rename", T("conflict.rename"), T("cdlg.renameDesc", PathUtil.UniqueName(it.Name, n => n == it.Name)), true, "");
            AddOpt("skip", T("conflict.skip"), T("cdlg.skipDesc"), true, "");
            int oy = 0;
            foreach (Control c in _opts.Controls) { c.Location = new Point(0, oy); c.Width = _opts.Width; oy += c.Height + Theme.S(2); }
            _opts.Height = oy;
            Body.Controls.Add(_opts);
            y = _opts.Bottom + Theme.S(10);

            _all = new DarkCheckBox(T("cdlg.applyAll")) { Location = new Point(pad, y) };
            Body.Controls.Add(_all);
            y = _all.Bottom + Theme.S(8);
            ClientSize = new Size(W, y + Theme.S(8) + Footer.Height);

            var pick = _opts.Controls.OfType<RadioOption>().FirstOrDefault(r => (string)r.Value == _last && r.Enabled)
                       ?? _opts.Controls.OfType<RadioOption>().First(r => r.Enabled && (string)r.Value == (req.CanOverwrite ? "overwrite" : "rename"));
            pick.Checked = true;

            var cancelAll = new FlatButton(T("cdlg.cancelAll"), ButtonKind.Ghost, G.Cancel);
            cancelAll.AutoFit();
            cancelAll.Click += (s, e) => { _req.CancelAll = true; DialogResult = DialogResult.Abort; };
            FooterLeft.Controls.Add(cancelAll);
            AddButton(T("common.skip"), DialogResult.Ignore);
            var ok = AddButton(T("common.ok"), DialogResult.OK, ButtonKind.Primary);
            AcceptButton = ok;
            CancelButton = null;
        }

        void DrawRow(Graphics g, int top, string who, long size, DateTime t, string tag, Color tagColor, int x0, int x1, int h)
        {
            Theme.DrawText(g, who, Theme.Ui, Theme.TextDim, new Rectangle(x0, top, x1 - x0, h));
            string info = Fmt.Size(size) + "   ·   " + Fmt.TimeSec(t);
            Theme.DrawText(g, info, Theme.Ui, Theme.Text, new Rectangle(x1, top, Theme.S(260), h));
            if (tag != null)
            {
                var ts = Theme.Measure(tag, Theme.Small);
                var r = new Rectangle(x1 + Theme.S(262), top + (h - Theme.S(20)) / 2, ts.Width + Theme.S(14), Theme.S(20));
                Theme.FillRound(g, Theme.Blend(Theme.Panel, tagColor, 0.25f), r, Theme.S(10));
                Theme.DrawText(g, tag, Theme.Small, tagColor, r, Theme.TextCenter);
            }
        }

        void AddOpt(string key, string text, string desc, bool enabled, string disabledReason)
        {
            var r = new RadioOption(text, enabled ? desc : disabledReason, key) { Enabled = enabled };
            _opts.Controls.Add(r);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Ignore; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _opts.Controls.OfType<RadioOption>().FirstOrDefault(r => r.Checked)?.Focus();
            Native.Flash(Handle);
        }

        public static ConflictRequest Ask(IWin32Window owner, ConflictRequest req)
        {
            using (var d = new ConflictDialog(req))
            {
                var r = d.ShowDialog(owner);
                if (req.CancelAll || r == DialogResult.Abort) { req.CancelAll = true; return req; }
                if (r == DialogResult.OK)
                {
                    req.Answer = (string)d._opts.Controls.OfType<RadioOption>().First(o => o.Checked).Value;
                    _last = req.Answer;
                }
                else req.Answer = "skip";
                req.ApplyAll = d._all.Checked;
                return req;
            }
        }
    }
}
