using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

using static FileRelay.L;
namespace FileRelay.UI
{
    /// <summary>트레이 아이콘 — 메뉴, 알림 풍선, 안 읽은 쪽지 배지.</summary>
    public sealed class TrayController : IDisposable
    {
        readonly NotifyIcon _icon;
        readonly ContextMenuStrip _menu;
        readonly MainForm _main;
        readonly AppCore _c = AppCore.I;
        Action _balloonAction;
        Icon _current;
        string _badgeKey = "";

        public TrayController(MainForm main)
        {
            _main = main;
            _menu = new ContextMenuStrip();
            _menu.Opening += (s, e) => BuildMenu();
            _icon = new NotifyIcon
            {
                Icon = Icons.App(16),
                Text = "FileRelay",
                Visible = true,
                ContextMenuStrip = _menu,
            };
            _icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) _main.ShowMain(); };
            _icon.BalloonTipClicked += (s, e) => { var a = _balloonAction; _balloonAction = null; if (a != null) a(); else _main.ShowMain(); };
            _icon.BalloonTipClosed += (s, e) => _balloonAction = null;
            UpdateBadge();
        }

        public void Balloon(string title, string text, NotifyKind kind, Action onClick = null)
        {
            ToolTipIcon icon;
            switch (kind)
            {
                case NotifyKind.Error: icon = ToolTipIcon.Error; break;
                case NotifyKind.Warning: icon = ToolTipIcon.Warning; break;
                default: icon = ToolTipIcon.Info; break;
            }
            _balloonAction = onClick;
            try { _icon.ShowBalloonTip(5000, Fmt.Truncate(title, 60), Fmt.Truncate(string.IsNullOrEmpty(text) ? " " : text, 250), icon); }
            catch { }
        }

        public void UpdateBadge()
        {
            int unread = _c.Notes.UnreadCount;
            bool serverDown = !_c.Http.Running;
            string key = unread + "|" + serverDown;
            if (key != _badgeKey)
            {
                _badgeKey = key;
                var old = _current;
                _current = Icons.Tray(unread > 0 || serverDown, serverDown ? Theme.Danger : Theme.Accent);
                _icon.Icon = _current;
                if (old != null) { Native.DestroyIcon(old.Handle); old.Dispose(); }
            }
            var st = _c.Transfers?.Stats();
            int online = _c.Peers.All().Count(p => p.IsOnline && p.State == PeerState.Paired);
            string tip = $"FileRelay · {_c.Config.UserName} ({Fmt.Presence(_c.MyStatus)})\n" + TN("tray.tip.online", online);
            if (st != null && st.Busy) tip += " · " + T("tray.tip.transfer", (int)st.Percent);
            if (unread > 0) tip += "\n" + TN("tray.tip.unread", unread);
            if (serverDown) tip = T("tray.tip.serverDown");
            try { _icon.Text = Fmt.Truncate(tip, 63); } catch { }
        }

        void BuildMenu()
        {
            _menu.Items.Clear();
            var open = new ToolStripMenuItem(T("tray.open"), Icons.AppBitmap(Theme.S(16)), (s, e) => _main.ShowMain()) { Font = Theme.UiBold };
            _menu.Items.Add(open);
            _menu.Items.Add(new ToolStripSeparator());

            var status = new ToolStripMenuItem(T("tray.myStatus", Fmt.Presence(_c.MyStatus)), Icons.StatusDot(_c.MyStatus, Theme.S(16)));
            foreach (var p in new[] { Presence.Online, Presence.Away, Presence.Busy })
            {
                var pp = p;
                var it = new ToolStripMenuItem(Fmt.Presence(p), Icons.StatusDot(p, Theme.S(16)), (s, e) => _c.SetStatus(pp, false)) { Checked = _c.MyStatus == p };
                status.DropDownItems.Add(it);
            }
            _menu.Items.Add(status);

            var note = new ToolStripMenuItem(T("tray.sendNote"), Icons.Glyph(G.Note, Theme.TextDim));
            var peers = _c.Peers.All().Where(p => p.CanUse).OrderBy(p => p.DisplayName, NaturalComparer.Instance).ToList();
            if (peers.Count == 0) note.DropDownItems.Add(new ToolStripMenuItem(T("tray.noPeers")) { Enabled = false });
            foreach (var p in peers)
            {
                var pp = p;
                note.DropDownItems.Add(p.DisplayName, Icons.Avatar(p.DisplayName, p.Id, Theme.S(18), p.Presence, p.State, p.Blocked, Theme.Chrome), (s, e) => NoteManager.Compose(pp));
            }
            _menu.Items.Add(note);
            int unread = _c.Notes.UnreadCount;
            _menu.Items.Add(new ToolStripMenuItem(T("tray.notes") + (unread > 0 ? $"  ({unread})" : ""), Icons.Glyph(G.Mail, Theme.TextDim), (s, e) => NotesForm.ShowInbox()));
            if (NoteManager.OpenCount > 0)
                _menu.Items.Add(T("tray.closeNotes"), Icons.Glyph(G.Clear, Theme.TextDim), (s, e) => NoteManager.CloseAll());
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(T("tray.settings"), Icons.Glyph(G.Settings, Theme.TextDim), (s, e) => _main.OpenSettings());
            _menu.Items.Add(T("common.exit"), Icons.Glyph(G.Power, Theme.TextDim), (s, e) => _main.ExitApp());
            DarkRenderer.Polish(_menu);
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
            if (_current != null) { Native.DestroyIcon(_current.Handle); _current.Dispose(); }
        }
    }
}
