using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

using static FileRelay.L;
namespace FileRelay.UI
{
    /// <summary>쪽지함 — 받은/보낸 쪽지 기록.</summary>
    public sealed class NotesForm : DarkForm
    {
        static NotesForm _inst;
        readonly AppCore _c = AppCore.I;
        readonly TabStrip _tabs;
        readonly DarkTextBox _search;
        readonly DarkListView _list;
        readonly RichTextBox _preview;
        readonly HeaderBar _prevHead;
        readonly FlatButton _reply, _open, _del, _readAll, _newNote;
        List<Note> _rows = new List<Note>();

        public static void ShowInbox(Note select = null)
        {
            if (_inst == null || _inst.IsDisposed) _inst = new NotesForm();
            _inst.Show();
            if (_inst.WindowState == FormWindowState.Minimized) _inst.WindowState = FormWindowState.Normal;
            _inst.Activate();
            if (select != null) _inst.SelectNote(select);
        }

        NotesForm()
        {
            Text = T("notes.title");
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(Theme.S(820), Theme.S(580));
            MinimumSize = new Size(Theme.S(560), Theme.S(380));

            var top = new Panel { Dock = DockStyle.Top, Height = Theme.S(48), BackColor = Theme.Panel };
            _tabs = new TabStrip { Dock = DockStyle.Fill, Height = Theme.S(48) };
            _tabs.Add(T("notes.tab.received"), G.Mail);
            _tabs.Add(T("notes.tab.sent"), G.Send);
            _tabs.SelectedChanged += (s, e) => Reload();
            _search = new DarkTextBox { Dock = DockStyle.Right, Width = Theme.S(220), Glyph = G.Search, Placeholder = T("notes.searchPh") };
            var searchHost = new Panel { Dock = DockStyle.Right, Width = Theme.S(236), BackColor = Theme.Panel, Padding = new Padding(0, Theme.S(8), Theme.S(12), Theme.S(8)) };
            searchHost.Controls.Add(_search);
            _search.Dock = DockStyle.Fill;
            _search.TextChanged += (s, e) => Reload();
            top.Controls.Add(_tabs);
            top.Controls.Add(searchHost);

            var bar = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(52), BackColor = Theme.Panel, Padding = new Padding(Theme.S(12), Theme.S(10), Theme.S(12), Theme.S(10)) };
            bar.Paint += (s, e) => { using (var p = new Pen(Theme.BorderSoft)) e.Graphics.DrawLine(p, 0, 0, bar.Width, 0); };
            _newNote = Btn(T("notes.new"), G.Edit, ButtonKind.Primary);
            _reply = Btn(T("notes.reply"), G.Reply);
            _open = Btn(T("notes.popOut"), G.Note);
            _readAll = Btn(T("notes.readAll"), G.Accept);
            _del = Btn(T("common.delete"), G.Delete);
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Panel, WrapContents = false };
            foreach (var b in new[] { _newNote, _reply, _open, _readAll, _del }) { b.Margin = new Padding(0, 0, Theme.S(8), 0); flow.Controls.Add(b); }
            bar.Controls.Add(flow);
            _newNote.Click += (s, e) => NoteManager.Compose(null);
            _reply.Click += (s, e) => Reply();
            _open.Click += (s, e) => { foreach (var n in Selected()) NoteManager.ShowIncoming(n, true); };
            _readAll.Click += (s, e) => { foreach (var n in _c.Notes.All().Where(n => n.Incoming && !n.IsRead)) _c.MarkNoteRead(n); };
            _del.Click += (s, e) => Delete();

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Theme.BorderSoft, SplitterWidth = Theme.S(4), FixedPanel = FixedPanel.Panel2 };
            split.Panel1.BackColor = Theme.Bg;
            split.Panel2.BackColor = Theme.Bg;

            _list = new DarkListView { Dock = DockStyle.Fill, MultiSelect = true, RowHeight = Theme.S(34) };
            _list.Columns.Add(T("notes.col.peer"), Theme.S(170));
            _list.Columns.Add(T("notes.col.text"), Theme.S(360));
            _list.Columns.Add(T("notes.col.time"), Theme.S(150));
            _list.Columns.Add(T("notes.col.status"), Theme.S(90));
            _list.GetText = (r, c) =>
            {
                var n = _rows[r];
                switch (c)
                {
                    case 0: return n.PeerName;
                    case 1: return Fmt.OneLine(n.Text, 200);
                    case 2: return Fmt.NoteTime(n.Time);
                    default: return n.Incoming ? (n.IsRead ? T("notes.st.read") : T("notes.st.new")) : (n.IsRead ? T("notes.st.readByPeer") : T("notes.st.unread"));
                }
            };
            _list.GetColor = (r, c) =>
            {
                var n = _rows[r];
                bool unread = n.Incoming && !n.IsRead;
                if (c == 3) return unread ? Theme.Accent : n.IsRead ? Theme.Success : Theme.TextFaint;
                return unread || c == 0 ? Theme.Text : Theme.TextDim;
            };
            _list.GetIcon = r =>
            {
                var n = _rows[r];
                var p = _c.Peers.FindById(n.PeerId);
                return Icons.Avatar(n.PeerName, string.IsNullOrEmpty(n.PeerId) ? n.PeerName : n.PeerId, Theme.S(24), p?.Presence ?? Presence.Offline, PeerState.Paired, false, Theme.Bg, p != null);
            };
            _list.EmptyText = T("notes.empty");
            _list.SelectedIndexChanged += (s, e) => ShowPreview();
            _list.VirtualItemsSelectionRangeChanged += (s, e) => ShowPreview();
            _list.DoubleClick += (s, e) => { foreach (var n in Selected()) NoteManager.ShowIncoming(n, true); };
            _list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete) { Delete(); e.Handled = true; }
                else if (e.KeyCode == Keys.Enter) { foreach (var n in Selected()) NoteManager.ShowIncoming(n, true); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.R) { Reply(); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.A) { _list.SelectAllRows(); e.Handled = true; }
            };
            split.Panel1.Controls.Add(_list);

            _prevHead = new HeaderBar { Dock = DockStyle.Top, Height = Theme.S(46) };
            _preview = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Bg, ForeColor = Theme.Text, Font = new Font(Theme.FontName, Theme.Ui.Size + 1f), DetectUrls = true };
            Theme.ApplyControlTheme(_preview);
            _preview.LinkClicked += (s, e) => { try { ShellOps.Open(e.LinkText); } catch { } };
            var prevHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.S(16), Theme.S(10), Theme.S(12), Theme.S(8)), BackColor = Theme.Bg };
            prevHost.Controls.Add(_preview);
            split.Panel2.Controls.Add(prevHost);
            split.Panel2.Controls.Add(_prevHead);

            Controls.Add(split);
            Controls.Add(top);
            Controls.Add(bar);
            Shown += (s, e) => { try { split.SplitterDistance = Math.Max(Theme.S(120), split.Height - Theme.S(190)); } catch { } };

            _c.NotesChanged += OnNotesChanged;
            FormClosed += (s, e) => _c.NotesChanged -= OnNotesChanged;
            Reload();
        }

        FlatButton Btn(string text, char glyph, ButtonKind kind = ButtonKind.Secondary)
        {
            var b = new FlatButton(text, kind, glyph);
            b.AutoFit();
            return b;
        }

        void OnNotesChanged()
        {
            if (IsDisposed) return;
            Reload();
        }

        void Reload()
        {
            var keep = Selected().Select(n => n.Id).ToList();
            bool inbox = _tabs.Selected == 0;
            string q = _search.Text.Trim();
            var all = _c.Notes.All();
            _rows = all.Where(n => n.Incoming == inbox)
                .Where(n => q.Length == 0 || n.PeerName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || n.Text.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(n => n.Time).ToList();
            int unread = all.Count(n => n.Incoming && !n.IsRead);
            _tabs.SetBadge(0, unread > 0 ? unread.ToString() : "", unread > 0 ? Theme.Accent : (Color?)null);
            _tabs.SetBadge(1, "");
            _list.SelectedIndices.Clear();
            _list.SetCount(_rows.Count);
            _list.Columns[0].Text = inbox ? T("notes.col.from") : T("notes.col.to");
            for (int i = 0; i < _rows.Count; i++) if (keep.Contains(_rows[i].Id)) _list.SelectedIndices.Add(i);
            _list.Invalidate();
            ShowPreview();
        }

        List<Note> Selected() => _list == null ? new List<Note>() : _list.SelectedRows().Where(i => i < _rows.Count).Select(i => _rows[i]).ToList();

        void SelectNote(Note n)
        {
            _tabs.Selected = n.Incoming ? 0 : 1;
            int i = _rows.IndexOf(n);
            if (i >= 0) _list.SelectRow(i);
        }

        void ShowPreview()
        {
            var sel = Selected();
            _reply.Enabled = sel.Count == 1 && sel[0].Incoming;
            _open.Enabled = _del.Enabled = sel.Count > 0;
            if (sel.Count != 1)
            {
                _prevHead.Set(sel.Count == 0 ? T("notes.selectHint") : TN("notes.selected", sel.Count), "");
                _preview.Text = "";
                return;
            }
            var n = sel[0];
            var p = _c.Peers.FindById(n.PeerId);
            _prevHead.Set(n.Incoming ? T("notes.from", n.PeerName) : T("notes.to", n.PeerName),
                Fmt.TimeSec(n.Time) + (n.Incoming ? "" : n.IsRead ? "  ·  " + T("notes.readAt", Fmt.NoteTime(n.ReadAt)) : "  ·  " + T("notes.notReadYet")),
                Icons.Avatar(n.PeerName, string.IsNullOrEmpty(n.PeerId) ? n.PeerName : n.PeerId, Theme.S(30), p?.Presence ?? Presence.Offline, PeerState.Paired, false, Theme.Panel, p != null));
            _preview.Text = n.Text;
            if (n.Incoming && !n.IsRead && ContainsFocus) _c.MarkNoteRead(n);
        }

        void Reply()
        {
            var sel = Selected();
            if (sel.Count != 1 || !sel[0].Incoming) return;
            _c.MarkNoteRead(sel[0]);
            NoteManager.Compose(_c.Peers.FindById(sel[0].PeerId), sel[0]);
        }

        void Delete()
        {
            var sel = Selected();
            if (sel.Count == 0) return;
            if (!MessageDialog.Confirm(this, TN("notes.delTitle", sel.Count), TN("notes.delText", sel.Count), T("common.delete"), true)) return;
            _c.Notes.Remove(sel);
            foreach (var p in _c.Peers.All()) p.Unread = _c.Notes.UnreadFrom(p.Id);
            _c.Peers.Touch(false);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == (Keys.Control | Keys.F)) { _search.Focus(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
