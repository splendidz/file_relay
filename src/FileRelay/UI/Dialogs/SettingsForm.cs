using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Windows.Forms;

using static FileRelay.L;
namespace FileRelay.UI
{
    /// <summary>왼쪽 탐색 목록 (설정 창 페이지 선택).</summary>
    sealed class NavList : BufferedPanel
    {
        public readonly List<(string Text, char Glyph)> Items = new List<(string, char)>();
        int _sel, _hot = -1;
        public event EventHandler SelectedChanged;

        public NavList()
        {
            BackColor = Theme.Panel;
            Cursor = Cursors.Hand;
        }

        public int Selected
        {
            get => _sel;
            set { if (value != _sel && value >= 0 && value < Items.Count) { _sel = value; Invalidate(); SelectedChanged?.Invoke(this, EventArgs.Empty); } }
        }

        Rectangle ItemRect(int i) => new Rectangle(Theme.S(10), Theme.S(14) + i * Theme.S(40), Width - Theme.S(20), Theme.S(36));

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = -1;
            for (int i = 0; i < Items.Count; i++) if (ItemRect(i).Contains(e.Location)) h = i;
            if (h != _hot) { _hot = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = -1; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            for (int i = 0; i < Items.Count; i++) if (ItemRect(i).Contains(e.Location)) Selected = i;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            Theme.HQ(g);
            for (int i = 0; i < Items.Count; i++)
            {
                var r = ItemRect(i);
                bool sel = i == _sel;
                if (sel) Theme.FillRound(g, Theme.Selection, r, Theme.S(6));
                else if (i == _hot) Theme.FillRound(g, Theme.Hover, r, Theme.S(6));
                if (sel) Theme.FillRound(g, Theme.Accent, new RectangleF(r.X, r.Y + Theme.S(9), Theme.S(3), r.Height - Theme.S(18)), Theme.S(2));
                var img = Icons.Glyph(Items[i].Glyph, Theme.S(16), sel ? Theme.Accent : Theme.TextDim);
                g.DrawImage(img, r.X + Theme.S(14), r.Y + (r.Height - img.Height) / 2);
                Theme.DrawText(g, Items[i].Text, sel ? Theme.UiBold : Theme.Ui, sel ? Theme.Text : Theme.TextDim, new Rectangle(r.X + Theme.S(42), r.Y, r.Width - Theme.S(46), r.Height));
            }
            using (var p = new Pen(Theme.BorderSoft)) g.DrawLine(p, Width - 1, 0, Width - 1, Height);
        }
    }

    /// <summary>설정 창 — FileRelay.jsonc 를 편집한다 (저장 시 즉시 적용).</summary>
    public sealed class SettingsForm : DarkForm
    {
        readonly AppCore _c = AppCore.I;
        readonly AppConfig _cfg;
        readonly NavList _nav;
        readonly Panel _host;
        readonly List<Panel> _pages = new List<Panel>();
        readonly Label _err;

        // 일반
        DarkTextBox _name, _port, _key, _bootstrap;
        ToggleSwitch _autoAccept, _discoverable, _startWin, _startMin, _confirmExit;
        // 공유
        DarkListView _shareList;
        ToggleSwitch _shareHidden, _recycle;
        DarkTextBox _serveLimit;
        List<ShareConfig> _shares;
        // 전송
        DarkTextBox _dlFolder, _tmpFolder, _limit;
        DarkSelect _concurrent, _retry, _conflict;
        ToggleSwitch _confirmXfer, _preserve, _notifyXfer, _showHidden;
        // 쪽지
        ToggleSwitch _notePopup, _noteSound, _noteTop, _readReceipt;
        DarkSelect _noteStyle, _noteColor, _noteKeep;
        // 알림
        ToggleSwitch _notifyOnline, _notifyRemote;
        DarkSelect _away;
        // 고급
        DarkSelect _hb, _font, _fontSize, _logKeep, _theme, _lang;

        public SettingsForm(int page = 0)
        {
            _cfg = _c.Config.Clone();
            _shares = _cfg.Shares.Select(s => s.Clone()).ToList();
            Text = T("set.title");
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            Size = new Size(Theme.S(900), Theme.S(680));
            MinimumSize = new Size(Theme.S(760), Theme.S(520));

            _nav = new NavList { Dock = DockStyle.Left, Width = Theme.S(200) };
            _nav.Items.Add((T("set.nav.general"), G.Contact));
            _nav.Items.Add((T("set.nav.shares"), G.Share));
            _nav.Items.Add((T("set.nav.transfer"), G.Sync));
            _nav.Items.Add((T("set.nav.notes"), G.Note));
            _nav.Items.Add((T("set.nav.notify"), G.Bell));
            _nav.Items.Add((T("set.nav.advanced"), G.Settings));
            _nav.SelectedChanged += (s, e) => ShowPage(_nav.Selected);

            _host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };

            var footer = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(60), BackColor = Theme.Panel, Padding = new Padding(Theme.S(16), Theme.S(14), Theme.S(16), Theme.S(14)) };
            footer.Paint += (s, e) => { using (var p = new Pen(Theme.BorderSoft)) e.Graphics.DrawLine(p, 0, 0, footer.Width, 0); };
            var save = new FlatButton(T("common.save"), ButtonKind.Primary, G.Save) { Dock = DockStyle.Right };
            save.AutoFit(Theme.S(96));
            save.Click += (s, e) => Save();
            var cancel = new FlatButton(T("common.cancel"), ButtonKind.Secondary) { Dock = DockStyle.Right, DialogResult = DialogResult.Cancel };
            cancel.AutoFit(Theme.S(88));
            var gap = new Panel { Dock = DockStyle.Right, Width = Theme.S(8), BackColor = Theme.Panel };
            _err = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Danger, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, AutoEllipsis = true };
            footer.Controls.Add(_err);
            footer.Controls.Add(cancel);
            footer.Controls.Add(gap);
            footer.Controls.Add(save);
            AcceptButton = null;
            CancelButton = cancel;

            Controls.Add(_host);
            Controls.Add(_nav);
            Controls.Add(footer);

            BuildGeneral();
            BuildShares();
            BuildTransfer();
            BuildNotes();
            BuildNotify();
            BuildAdvanced();
            _nav.Selected = Math.Max(0, Math.Min(page, _pages.Count - 1));
            ShowPage(_nav.Selected);
        }

        // ─── 페이지 틀 ─────────────────────────────────────────────────────
        Panel NewPage(string title, string subtitle)
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true, Padding = new Padding(Theme.S(28), Theme.S(8), Theme.S(28), Theme.S(24)), Visible = false };
            Theme.ApplyControlTheme(page);
            var head = new BufferedPanel { Dock = DockStyle.Top, Height = Theme.S(64) };
            head.Paint += (s, e) =>
            {
                e.Graphics.Clear(Theme.Bg);
                Theme.DrawText(e.Graphics, title, Theme.Big, Theme.Text, new Rectangle(0, Theme.S(10), head.Width, Theme.S(30)));
                Theme.DrawText(e.Graphics, subtitle, Theme.Small, Theme.TextFaint, new Rectangle(0, Theme.S(40), head.Width, Theme.S(20)));
            };
            Add(page, head);
            _pages.Add(page);
            _host.Controls.Add(page);
            return page;
        }

        static void Add(Panel page, Control c)
        {
            page.Controls.Add(c);
            c.BringToFront();
        }

        static void Section(Panel page, string text) => Add(page, new SectionTitle(text));

        static T Row<T>(Panel page, string title, string desc, T editor, int width = 0) where T : Control
        {
            Add(page, new SettingRow(title, desc, editor, width));
            return editor;
        }

        static ToggleSwitch Toggle(Panel page, string title, string desc, bool value)
        {
            var t = new ToggleSwitch { Checked = value };
            Row(page, title, desc, t);
            return t;
        }

        static DarkTextBox TextRow(Panel page, string title, string desc, string value, int width = 260, string placeholder = "")
        {
            var tb = new DarkTextBox { Text = value ?? "", Placeholder = placeholder };
            Row(page, title, desc, tb, Theme.S(width));
            return tb;
        }

        static DarkTextBox Number(Panel page, string title, string desc, int value, int width = 120)
        {
            var tb = TextRow(page, title, desc, value.ToString(), width);
            tb.Box.KeyPress += (s, e) => { if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true; };
            return tb;
        }

        static DarkSelect SelectRow(Panel page, string title, string desc, IEnumerable<(object, string)> items, object value, int width = 220)
        {
            var s = new DarkSelect();
            foreach (var (v, t) in items) s.Add(v, t);
            s.SelectedValue = value;
            if (s.SelectedIndex < 0 && s.Items.Count > 0) s.SelectedIndex = 0;
            Row(page, title, desc, s, Theme.S(width));
            return s;
        }

        DarkTextBox PathRow(Panel page, string title, string desc, string value, string placeholder)
        {
            var holder = new Panel { Height = Theme.S(34), BackColor = Theme.Bg };
            var tb = new DarkTextBox { Text = value ?? "", Placeholder = placeholder, Dock = DockStyle.Fill };
            var browse = new FlatButton("", ButtonKind.Secondary, G.OpenFolder) { Dock = DockStyle.Right, Width = Theme.S(38) };
            var gap = new Panel { Dock = DockStyle.Right, Width = Theme.S(6), BackColor = Theme.Bg };
            browse.Click += (s, e) =>
            {
                var p = FolderPicker.Pick(this, title, string.IsNullOrWhiteSpace(tb.Text) ? null : tb.Text);
                if (p != null) tb.Text = p;
            };
            holder.Controls.Add(tb);
            holder.Controls.Add(gap);
            holder.Controls.Add(browse);
            Row(page, title, desc, holder, Theme.S(380));
            return tb;
        }

        void ShowPage(int i)
        {
            for (int k = 0; k < _pages.Count; k++) _pages[k].Visible = k == i;
            _pages[i].BringToFront();
        }

        // ─── 페이지들 ─────────────────────────────────────────────────────
        void BuildGeneral()
        {
            var p = NewPage(T("set.nav.general"), T("set.gen.sub"));
            Section(p, T("set.gen.me"));
            _name = TextRow(p, T("set.gen.name"), T("set.gen.nameDesc"), _cfg.UserName, 260, T("set.gen.namePh"));
            var addr = new Label { Text = string.Join("   ", NetUtil.LocalIPv4().Take(4)), ForeColor = Theme.Text, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Height = Theme.S(30), UseMnemonic = false };
            Row(p, T("set.gen.ip"), T("set.gen.ipDesc"), addr, Theme.S(320));

            Section(p, T("set.gen.network"));
            _port = Number(p, T("set.gen.port"), T("set.gen.portDesc"), _cfg.Port);
            _key = TextRow(p, T("set.gen.key"), T("set.gen.keyDesc"), _cfg.NetworkKey, 220, T("set.gen.keyPh"));
            _autoAccept = Toggle(p, T("set.gen.autoAccept"), T("set.gen.autoAcceptDesc"), _cfg.AutoAcceptPeers);
            _discoverable = Toggle(p, T("set.gen.discoverable"), T("set.gen.discoverableDesc"), _cfg.Discoverable);
            _bootstrap = new DarkTextBox { Multiline = true, Height = Theme.S(70), Text = string.Join("\r\n", _cfg.BootstrapPeers) };
            Row(p, T("set.gen.bootstrap"), T("set.gen.bootstrapDesc"), _bootstrap, Theme.S(260));

            Section(p, T("set.gen.startExit"));
            _startWin = Toggle(p, T("set.gen.startWin"), T("set.gen.startWinDesc"), _cfg.StartWithWindows);
            _startMin = Toggle(p, T("set.gen.startMin"), T("set.gen.startMinDesc"), _cfg.StartMinimized);
            _confirmExit = Toggle(p, T("set.gen.confirmExit"), T("set.gen.confirmExitDesc"), _cfg.ConfirmOnExit);
        }

        void BuildShares()
        {
            var p = NewPage(T("set.nav.shares"), T("set.sh.sub"));
            Section(p, T("set.sh.list"));
            var bar = new Panel { Dock = DockStyle.Top, Height = Theme.S(44), BackColor = Theme.Bg };
            var add = new FlatButton(T("set.share.add"), ButtonKind.Primary, G.Add);
            add.AutoFit();
            var edit = new FlatButton(T("set.sh.edit"), ButtonKind.Secondary, G.Edit);
            edit.AutoFit();
            var del = new FlatButton(T("common.delete"), ButtonKind.Secondary, G.Delete);
            del.AutoFit();
            var toggle = new FlatButton(T("set.sh.toggle"), ButtonKind.Secondary, G.Pause);
            toggle.AutoFit();
            int x = 0;
            foreach (var b in new[] { add, edit, del, toggle }) { b.Location = new Point(x, Theme.S(4)); bar.Controls.Add(b); x += b.Width + Theme.S(8); }
            Add(p, bar);

            _shareList = new DarkListView { Dock = DockStyle.Top, Height = Theme.S(220), RowHeight = Theme.S(34), MultiSelect = false, RowBack = Theme.Panel, BackColor = Theme.Panel };
            _shareList.Columns.Add(T("set.sh.colName"), Theme.S(150));
            _shareList.Columns.Add(T("set.share.folder"), Theme.S(250));
            _shareList.Columns.Add(T("set.sh.colAccess"), Theme.S(110));
            _shareList.Columns.Add(T("set.sh.colPeers"), Theme.S(70), HorizontalAlignment.Center);
            _shareList.GetText = (r, c) =>
            {
                var s = _shares[r];
                switch (c)
                {
                    case 0: return s.Name;
                    case 1: return s.Path;
                    case 2: return s.Enabled ? Fmt.Access(s.Access) : T("set.sh.disabled");
                    default: return s.PeerAccess.Count == 0 ? "—" : TN("set.share.peerCount", s.PeerAccess.Count);
                }
            };
            _shareList.GetColor = (r, c) =>
            {
                var s = _shares[r];
                if (!s.Enabled) return Theme.TextFaint;
                if (c == 1 && !Directory.Exists(s.Path)) return Theme.Danger;
                if (c == 2) return s.Access == AccessLevel.Full ? Theme.Warn : s.Access == AccessLevel.Write ? Theme.Info : Theme.TextDim;
                return c == 0 ? Theme.Text : Theme.TextDim;
            };
            _shareList.GetIcon = r => Icons.ForItem(new FsItem { Kind = FsKind.Share, Name = _shares[r].Name }, true);
            _shareList.EmptyText = T("set.sh.empty");
            _shareList.EmptySub = T("set.sh.emptySub");
            _shareList.DoubleClick += (s, e) => EditShare();
            Add(p, _shareList);
            RefreshShares();

            add.Click += (s, e) =>
            {
                using (var d = new ShareEditDialog(null, _shares))
                    if (d.ShowDialog(this) == DialogResult.OK) { _shares.Add(d.Result); RefreshShares(); _shareList.SelectRow(_shares.Count - 1); }
            };
            edit.Click += (s, e) => EditShare();
            del.Click += (s, e) =>
            {
                int i = _shareList.SelectedRows().FirstOr(-1);
                if (i < 0) return;
                if (!MessageDialog.Confirm(this, T("set.share.removeTitle"), T("set.share.removeText", _shares[i].Name), T("common.delete"), true)) return;
                _shares.RemoveAt(i);
                RefreshShares();
            };
            toggle.Click += (s, e) =>
            {
                int i = _shareList.SelectedRows().FirstOr(-1);
                if (i < 0) return;
                _shares[i].Enabled = !_shares[i].Enabled;
                _shareList.Invalidate();
            };

            var hint = new NoteLabel(T("set.sh.levels")) { Dock = DockStyle.Top, Height = Theme.S(34), TextAlign = ContentAlignment.MiddleLeft };
            Add(p, hint);

            Section(p, T("set.sh.options"));
            _shareHidden = Toggle(p, T("set.sh.hidden"), T("set.sh.hiddenDesc"), _cfg.ShareHiddenFiles);
            _recycle = Toggle(p, T("set.sh.recycle"), T("set.sh.recycleDesc"), _cfg.RemoteDeleteToRecycleBin);
            _serveLimit = Number(p, T("set.sh.serveLimit"), T("set.sh.serveLimitDesc"), _cfg.ServeSpeedLimitKBps);
        }

        void EditShare()
        {
            int i = _shareList.SelectedRows().FirstOr(-1);
            if (i < 0) return;
            using (var d = new ShareEditDialog(_shares[i], _shares))
                if (d.ShowDialog(this) == DialogResult.OK) { _shares[i] = d.Result; RefreshShares(); }
        }

        void RefreshShares()
        {
            _shareList.SetCount(_shares.Count);
            _shareList.Invalidate();
        }

        void BuildTransfer()
        {
            var p = NewPage(T("set.nav.transfer"), T("set.xf.sub"));
            Section(p, T("set.share.folder"));
            _dlFolder = PathRow(p, T("set.xf.dlFolder"), T("set.xf.dlFolderDesc"), _cfg.DownloadFolder, KnownFolders.Downloads);
            _tmpFolder = PathRow(p, T("set.xf.temp"), T("set.xf.tempDesc"), _cfg.TempFolder, AppPaths.DefaultTempDir);
            _showHidden = Toggle(p, T("set.xf.showHidden"), "", _cfg.ShowHiddenFiles);

            Section(p, T("set.xf.speed"));
            _concurrent = SelectRow(p, T("set.xf.concurrent"), T("set.xf.concurrentDesc"), Enumerable.Range(1, 8).Select(n => ((object)n, TN("set.xfer.nFiles", n))), _cfg.MaxConcurrentTransfers, 140);
            _limit = Number(p, T("set.xf.limit"), T("set.xf.limitDesc"), _cfg.SpeedLimitKBps);
            _retry = SelectRow(p, T("set.xf.retry"), T("set.xf.retryDesc"), Enumerable.Range(0, 6).Select(n => ((object)n, n == 0 ? T("set.xfer.noRetry") : TN("set.xfer.nTimes", n))), _cfg.RetryCount, 140);

            Section(p, T("set.xf.behavior"));
            _conflict = SelectRow(p, T("set.xf.conflict"), T("set.xf.conflictDesc"),
                AppConfig.ConflictActions.Select(k => ((object)k, AppConfig.ConflictText(k))), _cfg.DefaultConflictAction, 260);
            _confirmXfer = Toggle(p, T("set.xf.confirm"), T("set.xf.confirmDesc"), _cfg.ConfirmBeforeTransfer);
            _preserve = Toggle(p, T("set.xf.preserve"), T("set.xf.preserveDesc"), _cfg.PreserveTimestamps);
            _notifyXfer = Toggle(p, T("set.xf.notify"), T("set.xf.notifyDesc"), _cfg.NotifyTransferComplete);
        }

        void BuildNotes()
        {
            var p = NewPage(T("set.nav.notes"), T("set.nt.sub"));
            Section(p, T("set.nt.receiving"));
            _notePopup = Toggle(p, T("set.nt.popup"), T("set.nt.popupDesc"), _cfg.NotePopup);
            _noteSound = Toggle(p, T("set.nt.sound"), "", _cfg.NoteSound);
            _noteTop = Toggle(p, T("set.nt.topmost"), T("set.nt.topmostDesc"), _cfg.NoteTopMost);
            _readReceipt = Toggle(p, T("set.nt.receipt"), T("set.nt.receiptDesc"), _cfg.SendReadReceipt);
            _noteStyle = SelectRow(p, T("set.nt.style"), "", new[] { ((object)"auto", T("set.nt.styleAuto")), ("pastel", T("set.nt.stylePastel")), ("dark", T("set.nt.styleDark")) }, _cfg.NoteStyle, 220);

            Section(p, T("set.nt.sending"));
            _noteColor = new DarkSelect();
            foreach (var key in AppConfig.NoteColors)
                _noteColor.Add(key, NotePalette.Get(key, "pastel").DisplayName, NotePalette.Swatch(key, Theme.S(14)));
            _noteColor.SelectedValue = _cfg.NoteColor;
            Row(p, T("set.nt.color"), T("set.nt.colorDesc"), _noteColor, Theme.S(200));
            _noteKeep = SelectRow(p, T("set.nt.keep"), "", new[] { ((object)30, TN("set.nDays", 30)), (90, TN("set.nDays", 90)), (180, TN("set.nDays", 180)), (365, T("set.oneYear")), (0, T("set.forever")) }, _cfg.NoteHistoryDays, 140);
        }

        void BuildNotify()
        {
            var p = NewPage(T("set.nav.notify"), T("set.nf.sub"));
            Section(p, T("set.nf.tray"));
            _notifyOnline = Toggle(p, T("set.nf.online"), T("set.nf.onlineDesc"), _cfg.NotifyPeerOnline);
            _notifyRemote = Toggle(p, T("set.nf.remote"), T("set.nf.remoteDesc"), _cfg.NotifyRemoteActivity);
            var always = new Label { Text = T("set.nf.always"), ForeColor = Theme.TextFaint, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Height = Theme.S(28) };
            Row(p, T("set.nf.autoAdded"), T("set.nf.autoAddedDesc"), always, Theme.S(120));

            Section(p, T("presence.away"));
            _away = SelectRow(p, T("set.nf.autoAway"), T("set.nf.autoAwayDesc"),
                new[] { ((object)0, T("set.off")), (5, TN("set.nMinutes", 5)), (10, TN("set.nMinutes", 10)), (15, TN("set.nMinutes", 15)), (30, TN("set.nMinutes", 30)), (60, T("set.oneHour")) }, _cfg.AutoAwayMinutes, 160);
        }

        void BuildAdvanced()
        {
            var p = NewPage(T("set.nav.advanced"), T("set.adv.sub"));
            Section(p, T("set.adv.display"));
            _lang = SelectRow(p, T("set.adv.language"), T("set.adv.restartNeeded"),
                new[] { ((object)"auto", T("set.adv.langAuto")), ("ko", "한국어"), ("en", "English") }, _cfg.Language, 220);
            _theme = SelectRow(p, T("set.adv.theme"), T("set.adv.restartNeeded"), new[] { ((object)"light", T("set.adv.themeLight")), ("dark", T("set.adv.themeDark")) }, _cfg.Theme, 180);
            var fonts = new List<(object, string)> { ("auto", T("set.adv.fontAuto")) };
            using (var ifc = new InstalledFontCollection())
            {
                var names = new HashSet<string>(ifc.Families.Select(f => f.Name));
                foreach (var f in new[] { "Malgun Gothic", "Segoe UI", "NanumGothic", "나눔고딕", "Pretendard", "Noto Sans KR", "Gulim", "Dotum" })
                    if (names.Contains(f)) fonts.Add((f, f));
                if (!fonts.Any(x => (string)x.Item1 == _cfg.UiFont)) fonts.Add((_cfg.UiFont, _cfg.UiFont));
            }
            _font = SelectRow(p, T("set.adv.font"), T("set.adv.restartNeeded"), fonts, _cfg.UiFont, 220);
            _fontSize = SelectRow(p, T("set.adv.fontSize"), "", new[] { 8f, 8.5f, 9f, 9.5f, 10f, 10.5f, 11f, 12f }.Select(v => ((object)v, v + " pt")), _cfg.UiFontSize, 140);

            Section(p, T("set.gen.network"));
            _hb = SelectRow(p, T("set.adv.heartbeat"), T("set.adv.heartbeatDesc"), new[] { ((object)5, TN("set.nSeconds", 5)), (10, TN("set.nSeconds", 10)), (15, TN("set.nSeconds", 15)), (30, TN("set.nSeconds", 30)), (60, T("set.oneMinute")) }, _cfg.HeartbeatSeconds, 140);
            var fw = new FlatButton(T("set.adv.firewallBtn"), ButtonKind.Secondary, G.Shield);
            fw.AutoFit();
            fw.Click += (s, e) => Firewall.AddRuleInteractive(this);
            Row(p, T("set.adv.firewall"), T("set.adv.firewallDesc"), fw);

            Section(p, T("set.adv.files"));
            _logKeep = SelectRow(p, T("set.adv.logKeep"), "", new[] { ((object)7, TN("set.nDays", 7)), (30, TN("set.nDays", 30)), (90, TN("set.nDays", 90)), (365, T("set.oneYear")) }, _cfg.LogRetentionDays, 140);
            var openCfg = new FlatButton(T("set.adv.openConfig"), ButtonKind.Secondary, G.Edit);
            openCfg.AutoFit();
            openCfg.Click += (s, e) => OpenConfigFile();
            Row(p, T("set.adv.config"), AppPaths.ConfigFile, openCfg);
            var openData = new FlatButton(T("set.adv.openData"), ButtonKind.Secondary, G.OpenFolder);
            openData.AutoFit();
            openData.Click += (s, e) => ShellOps.OpenFolder(AppPaths.DataDir);
            Row(p, T("set.adv.data"), T("set.adv.dataDesc"), openData);
        }

        public static void OpenConfigFile()
        {
            try
            {
                if (!File.Exists(AppPaths.ConfigFile)) AppCore.I.SaveConfig();
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + AppPaths.ConfigFile + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Warn(Cat.Sys, T("log.openConfigFailed", ex.Message)); }
        }

        // ─── 저장 ──────────────────────────────────────────────────────────
        void Save()
        {
            var c = _cfg;
            c.UserName = _name.Text.Trim();
            int.TryParse(_port.Text, out c.Port);
            c.NetworkKey = _key.Text.Trim();
            c.AutoAcceptPeers = _autoAccept.Checked;
            c.Discoverable = _discoverable.Checked;
            c.BootstrapPeers = _bootstrap.Text.Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            c.StartWithWindows = _startWin.Checked;
            c.StartMinimized = _startMin.Checked;
            c.ConfirmOnExit = _confirmExit.Checked;

            c.Shares = _shares.Select(s => s.Clone()).ToList();
            c.ShareHiddenFiles = _shareHidden.Checked;
            c.RemoteDeleteToRecycleBin = _recycle.Checked;
            int.TryParse(_serveLimit.Text, out c.ServeSpeedLimitKBps);

            c.DownloadFolder = _dlFolder.Text.Trim();
            c.TempFolder = _tmpFolder.Text.Trim();
            c.ShowHiddenFiles = _showHidden.Checked;
            c.MaxConcurrentTransfers = (int)_concurrent.SelectedValue;
            int.TryParse(_limit.Text, out c.SpeedLimitKBps);
            c.RetryCount = (int)_retry.SelectedValue;
            c.DefaultConflictAction = (string)_conflict.SelectedValue;
            c.ConfirmBeforeTransfer = _confirmXfer.Checked;
            c.PreserveTimestamps = _preserve.Checked;
            c.NotifyTransferComplete = _notifyXfer.Checked;

            c.NotePopup = _notePopup.Checked;
            c.NoteSound = _noteSound.Checked;
            c.NoteTopMost = _noteTop.Checked;
            c.SendReadReceipt = _readReceipt.Checked;
            c.NoteStyle = (string)_noteStyle.SelectedValue;
            c.NoteColor = (string)_noteColor.SelectedValue ?? "yellow";
            c.NoteHistoryDays = (int)_noteKeep.SelectedValue;

            c.NotifyPeerOnline = _notifyOnline.Checked;
            c.NotifyRemoteActivity = _notifyRemote.Checked;
            c.AutoAwayMinutes = (int)_away.SelectedValue;

            c.HeartbeatSeconds = (int)_hb.SelectedValue;
            string oldFont = _c.Config.UiFont;
            float oldSize = _c.Config.UiFontSize;
            string oldTheme = _c.Config.Theme;
            string oldLang = _c.Config.Language;
            c.Theme = (string)_theme.SelectedValue ?? "light";
            c.Language = (string)_lang.SelectedValue ?? "auto";
            c.UiFont = (string)_font.SelectedValue;
            c.UiFontSize = Convert.ToSingle(_fontSize.SelectedValue);
            c.LogRetentionDays = (int)_logKeep.SelectedValue;

            var errs = c.Validate();
            foreach (var s in c.Shares.Where(s => s.Enabled && !Directory.Exists(s.Path)))
                errs.Add(T("set.err.shareFolderMissing", s.Name, s.Path));
            if (errs.Count > 0)
            {
                _err.Text = errs[0];
                MessageDialog.Warn(this, T("set.err.title"), T("set.err.text"), string.Join("\n", errs));
                return;
            }
            _c.ApplyConfig(c.Clone(), true);
            DialogResult = DialogResult.OK;
            if (oldFont != c.UiFont || Math.Abs(oldSize - c.UiFontSize) > 0.01f || oldTheme != c.Theme || oldLang != c.Language)
            {
                if (MessageDialog.Confirm(this, T("set.restart.title"), T("set.restart.text"), T("set.restart.now")))
                    Program.Restart();
            }
        }
    }

    /// <summary>공유 폴더 하나 편집 (이름, 경로, 기본 권한, 피어별 권한).</summary>
    public sealed class ShareEditDialog : DialogBase
    {
        readonly DarkTextBox _name, _path, _desc;
        readonly DarkSelect _access, _peerPick, _peerAccess;
        readonly ToggleSwitch _enabled;
        readonly DarkListView _rules;
        readonly List<KeyValuePair<string, AccessLevel>> _ruleList;
        readonly List<ShareConfig> _all;
        readonly ShareConfig _orig;
        public ShareConfig Result;

        public ShareEditDialog(ShareConfig share, List<ShareConfig> all)
        {
            _orig = share;
            _all = all;
            var s = share?.Clone() ?? new ShareConfig { Access = AccessLevel.Read };
            Text = share == null ? T("set.share.add") : T("set.share.edit");
            int W = Theme.S(600), pad = Theme.S(22), y = Theme.S(16), lw = Theme.S(110), fw = W - pad * 2 - lw;
            Body.Controls.Add(new Label { Text = Text, Font = Theme.Title, ForeColor = Theme.Text, AutoSize = true, Location = new Point(pad, y) });
            y += Theme.S(42);

            Label L(string t, int yy) { var l = new Label { Text = t, ForeColor = Theme.TextDim, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Location = new Point(pad, yy), Size = new Size(lw, Theme.S(34)) }; Body.Controls.Add(l); return l; }

            L(T("set.share.folder"), y);
            _path = new DarkTextBox { Location = new Point(pad + lw, y), Size = new Size(fw - Theme.S(44), Theme.S(34)), Text = s.Path, Placeholder = T("set.share.pathPh") };
            var browse = new FlatButton("", ButtonKind.Secondary, G.OpenFolder) { Location = new Point(pad + lw + fw - Theme.S(38), y), Size = new Size(Theme.S(38), Theme.S(34)) };
            browse.Click += (o, e) =>
            {
                var p = FolderPicker.Pick(this, T("set.share.pick"), _path.Text);
                if (p == null) return;
                // 이름이 비었거나 이전 폴더로 자동으로 붙인 이름이면 새 폴더 기준으로 다시 붙인다.
                string oldDefault = string.IsNullOrWhiteSpace(_path.Text) ? "" : ShareConfig.DefaultNameFor(_path.Text);
                bool autoName = string.IsNullOrWhiteSpace(_name.Text) || _name.Text.Trim() == oldDefault || _name.Text.Trim() == T("share.default");
                _path.Text = p;
                if (autoName) _name.Text = ShareConfig.DefaultNameFor(p);
            };
            Body.Controls.Add(_path);
            Body.Controls.Add(browse);
            y += Theme.S(44);

            L(T("set.sh.colName"), y);
            _name = new DarkTextBox { Location = new Point(pad + lw, y), Size = new Size(Theme.S(220), Theme.S(34)), Text = s.Name, Placeholder = T("set.share.namePh") };
            Body.Controls.Add(_name);
            Body.Controls.Add(new Label { Text = T("set.share.enabled"), ForeColor = Theme.TextDim, AutoSize = true, Location = new Point(pad + lw + Theme.S(250), y + Theme.S(8)) });
            _enabled = new ToggleSwitch { Location = new Point(pad + lw + Theme.S(290), y + Theme.S(6)), Checked = s.Enabled };
            Body.Controls.Add(_enabled);
            y += Theme.S(44);

            L(T("set.share.desc"), y);
            _desc = new DarkTextBox { Location = new Point(pad + lw, y), Size = new Size(fw, Theme.S(34)), Text = s.Description, Placeholder = T("set.share.descPh") };
            Body.Controls.Add(_desc);
            y += Theme.S(44);

            L(T("set.sh.colAccess"), y);
            _access = AccessSelect(s.Access, true);
            _access.Location = new Point(pad + lw, y);
            _access.Size = new Size(fw, Theme.S(34));
            Body.Controls.Add(_access);
            y += Theme.S(52);

            Body.Controls.Add(new Label { Text = T("set.share.perPeer"), Font = Theme.UiBold, ForeColor = Theme.Text, AutoSize = true, Location = new Point(pad, y) });
            var nl = new NoteLabel(T("set.share.perPeerDesc")) { Location = new Point(pad, y + Theme.S(22)) };
            nl.FitHeight(W - pad * 2);
            Body.Controls.Add(nl);
            y = nl.Bottom + Theme.S(6);

            _peerPick = new DarkSelect { Location = new Point(pad, y), Size = new Size(Theme.S(220), Theme.S(34)), Placeholder = T("set.share.pickPeer") };
            foreach (var p in AppCore.I.Peers.All().OrderBy(p => p.DisplayName, NaturalComparer.Instance))
                _peerPick.Add(string.IsNullOrEmpty(p.Name) ? p.Host : p.Name, $"{p.DisplayName}  ({p.Host})", Icons.Avatar(p.DisplayName, p.Id, Theme.S(18), p.Presence, p.State, p.Blocked, Theme.Chrome, false));
            _peerPick.Add("__custom__", T("set.share.custom"), Icons.Glyph(G.Edit, Theme.S(14), Theme.TextDim));
            _peerAccess = AccessSelect(AccessLevel.Full, false);
            _peerAccess.Location = new Point(pad + Theme.S(228), y);
            _peerAccess.Size = new Size(Theme.S(150), Theme.S(34));
            var addRule = new FlatButton(T("common.add"), ButtonKind.Secondary, G.Add) { Location = new Point(pad + Theme.S(386), y), Height = Theme.S(34) };
            addRule.AutoFit();
            var delRule = new FlatButton(T("common.delete"), ButtonKind.Ghost, G.Delete) { Location = new Point(addRule.Right + Theme.S(6), y), Height = Theme.S(34) };
            delRule.AutoFit();
            Body.Controls.Add(_peerPick);
            Body.Controls.Add(_peerAccess);
            Body.Controls.Add(addRule);
            Body.Controls.Add(delRule);
            y += Theme.S(42);

            _ruleList = s.PeerAccess.ToList();
            _rules = new DarkListView { Location = new Point(pad, y), Size = new Size(W - pad * 2, Theme.S(130)), RowHeight = Theme.S(28), RowBack = Theme.Panel, BackColor = Theme.Panel };
            _rules.Columns.Add(T("set.share.colPeer"), Theme.S(300));
            _rules.Columns.Add(T("set.share.colAccess"), Theme.S(200));
            _rules.GetText = (r, c) => c == 0 ? _ruleList[r].Key : Fmt.Access(_ruleList[r].Value);
            _rules.GetColor = (r, c) => c == 1 ? AccessColor(_ruleList[r].Value) : Theme.Text;
            _rules.EmptyText = T("set.share.rulesEmpty");
            _rules.SetCount(_ruleList.Count);
            Body.Controls.Add(_rules);
            y = _rules.Bottom + Theme.S(10);

            addRule.Click += (o, e) =>
            {
                var key = _peerPick.SelectedValue as string;
                if (key == null) { _peerPick.Focus(); return; }
                if (key == "__custom__")
                {
                    key = InputDialog.Ask(this, T("set.share.specify"), T("set.share.specifyPrompt"), "", false, v => string.IsNullOrWhiteSpace(v) ? T("common.enterValue") : null);
                    if (key == null) return;
                }
                var acc = (AccessLevel)_peerAccess.SelectedValue;
                _ruleList.RemoveAll(kv => kv.Key.Eq(key));
                _ruleList.Add(new KeyValuePair<string, AccessLevel>(key, acc));
                _rules.SetCount(_ruleList.Count);
                _rules.Invalidate();
            };
            delRule.Click += (o, e) =>
            {
                foreach (int i in _rules.SelectedRows().OrderByDescending(i => i)) _ruleList.RemoveAt(i);
                _rules.SelectedIndices.Clear();
                _rules.SetCount(_ruleList.Count);
            };

            ClientSize = new Size(W, y + Footer.Height);
            AddButton(T("common.cancel"), DialogResult.Cancel);
            var ok = AddButton(T("common.ok"), DialogResult.None, ButtonKind.Primary);
            ok.Click += (o, e) => Accept();
            AcceptButton = ok;
        }

        static Color AccessColor(AccessLevel a) =>
            a == AccessLevel.Full ? Theme.Warn : a == AccessLevel.Write ? Theme.Info : a == AccessLevel.None ? Theme.Danger : Theme.TextDim;

        static DarkSelect AccessSelect(AccessLevel value, bool longText)
        {
            var s = new DarkSelect();
            foreach (var a in new[] { AccessLevel.Read, AccessLevel.Write, AccessLevel.Full, AccessLevel.None })
                s.Add(a, longText ? Fmt.AccessLong(a) : Fmt.Access(a), Icons.Glyph(a == AccessLevel.None ? G.Hide : a == AccessLevel.Read ? G.View : a == AccessLevel.Write ? G.Upload : G.Admin, Theme.S(14), AccessColor(a)));
            s.SelectedValue = value;
            return s;
        }

        void Accept()
        {
            string name = _name.Text.Trim(), path = _path.Text.Trim().Replace('/', '\\');
            string err = PathUtil.ValidateName(name);
            if (err != null) { MessageDialog.Warn(this, T("set.sh.colName"), err); _name.Focus(); return; }
            if (_all.Any(x => x != _orig && x.Name.Eq(name))) { MessageDialog.Warn(this, T("set.sh.colName"), T("set.share.dup")); _name.Focus(); return; }
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) { MessageDialog.Warn(this, T("set.share.folder"), T("set.share.fullPath")); _path.Focus(); return; }
            if (!Directory.Exists(path)) { MessageDialog.Warn(this, T("set.share.folder"), T("set.share.noFolder", path)); _path.Focus(); return; }
            var sys = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (PathUtil.IsUnder(path, sys) || path.TrimEnd('\\').Length <= 2)
            {
                if (!MessageDialog.Confirm(this, T("set.share.broadTitle"), T("set.share.broadText", path), T("set.share.shareIt"), true)) return;
            }
            Result = new ShareConfig
            {
                Name = name,
                Path = path,
                Description = _desc.Text.Trim(),
                Access = (AccessLevel)_access.SelectedValue,
                Enabled = _enabled.Checked,
            };
            foreach (var kv in _ruleList) Result.PeerAccess[kv.Key] = kv.Value;
            DialogResult = DialogResult.OK;
        }
    }
}
