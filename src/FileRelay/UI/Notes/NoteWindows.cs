using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

using static FileRelay.L;
namespace FileRelay.UI
{
    /// <summary>받은(또는 보낸) 쪽지 한 장 — 스티커 메모처럼 화면에 붙는 창.</summary>
    public sealed class NoteWindow : StickyBase
    {
        public readonly Note Note;
        readonly RichTextBox _body;
        readonly Panel _footer;
        readonly Label _info;
        readonly FlatButton _reply, _copy;
        readonly HeadButton _pin;
        bool _pinned;
        public bool ActivateOnShow;

        public NoteWindow(Note n, bool pinned)
            : base(NotePalette.Get(n.Color, AppCore.I.Config.EffectiveNoteStyle))
        {
            Note = n;
            _pinned = pinned;
            Size = new Size(Theme.S(330), Theme.S(260));
            HeadGlyph = n.Incoming ? G.Mail : G.Send;
            HeadTitle = n.Incoming ? n.PeerName : T("note.to", n.PeerName);
            HeadSub = Fmt.NoteTime(n.Time);
            Text = n.Incoming ? T("note.title.received", n.PeerName) : T("note.title.sent", n.PeerName);

            _pin = AddHeadButton(pinned ? G.Pin : G.Unpin, T("note.pin"), TogglePin);
            _pin.Toggled = pinned;
            AddHeadButton(G.More, T("note.more"), ShowMenu);
            AddHeadButton(G.Close, T("note.closeEsc"), Close);

            _body = MakeBody(true);
            _body.Text = n.Text;
            _body.GotFocus += (s, e) => MarkRead();

            _footer = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(42), BackColor = Pal.Body, Padding = new Padding(Theme.S(6), Theme.S(6), Theme.S(4), Theme.S(4)) };
            _info = new Label { Dock = DockStyle.Fill, ForeColor = Pal.SubText, Font = Theme.Small, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, AutoEllipsis = true };
            _reply = MakeButton(T("note.reply"), G.Reply, true);
            _reply.Dock = DockStyle.Right;
            _reply.Click += (s, e) => Reply();
            _copy = MakeButton("", G.Copy);
            _copy.Width = Theme.S(34);
            _copy.Dock = DockStyle.Right;
            _copy.Click += (s, e) => { try { Clipboard.SetText(Note.Text); _info.Text = T("note.copied"); } catch { } };
            var gap = new Panel { Dock = DockStyle.Right, Width = Theme.S(6), BackColor = Pal.Body };
            _footer.Controls.Add(_info);
            _footer.Controls.Add(_copy);
            _footer.Controls.Add(gap);
            _footer.Controls.Add(_reply);
            if (!n.Incoming) { _reply.Text = T("note.rewrite"); _reply.AutoFit(); }

            var bodyHost = new Panel { Dock = DockStyle.Fill, BackColor = Pal.Body, Padding = new Padding(Theme.S(8), Theme.S(8), Theme.S(4), Theme.S(2)) };
            bodyHost.Controls.Add(_body);
            Controls.Add(bodyHost);
            Controls.Add(_footer);
            UpdateInfo();
            new ToolTip().SetToolTip(_copy, T("note.copyText"));
        }

        protected override bool ShowWithoutActivation => !ActivateOnShow;

        void UpdateInfo()
        {
            if (Note.Incoming)
                _info.Text = string.IsNullOrEmpty(Note.ReplyTo) ? "" : T("note.isReply");
            else
                _info.Text = Note.IsRead ? T("note.readAt", Fmt.NoteTime(Note.ReadAt)) : T("note.notReadYet");
        }

        public override void ApplyPalette(NotePalette p)
        {
            base.ApplyPalette(p);
            _body.BackColor = p.Body;
            _body.ForeColor = p.Text;
            _body.Parent.BackColor = p.Body;
            _footer.BackColor = p.Body;
            foreach (Control c in _footer.Controls) c.BackColor = p.Body;
            _info.ForeColor = p.SubText;
            StyleButton(_reply, true);
            StyleButton(_copy, false);
        }

        void TogglePin()
        {
            _pinned = !_pinned;
            TopMost = _pinned;
            _pin.Toggled = _pinned;
            _pin.Glyph = _pinned ? G.Pin : G.Unpin;
            Invalidate();
        }

        void ShowMenu()
        {
            var m = new ContextMenuStrip();
            m.Items.Add(T("note.copyText"), Icons.Glyph(G.Copy, Theme.TextDim), (s, e) => { try { Clipboard.SetText(Note.Text); } catch { } });
            if (Note.Incoming) m.Items.Add(T("note.replyMenu"), Icons.Glyph(G.Reply, Theme.TextDim), (s, e) => Reply());
            var colors = new ToolStripMenuItem(T("note.color"), Icons.Glyph(G.Color, Theme.TextDim));
            foreach (var key in AppConfig.NoteColors)
            {
                string k = key;
                var it = new ToolStripMenuItem(NotePalette.Get(k, "pastel").DisplayName, NotePalette.Swatch(k, Theme.S(14)));
                it.Click += (s, e) => { Note.Color = k; AppCore.I.Notes.Touch(); ApplyPalette(NotePalette.Get(k, AppCore.I.Config.EffectiveNoteStyle)); };
                colors.DropDownItems.Add(it);
            }
            m.Items.Add(colors);
            m.Items.Add(new ToolStripSeparator());
            var peer = AppCore.I.Peers.FindById(Note.PeerId);
            if (peer != null) m.Items.Add(T("note.viewShares", peer.DisplayName), Icons.Glyph(G.Share, Theme.TextDim), (s, e) => NoteManager.RaiseOpenPeer(peer));
            m.Items.Add(T("note.openNotes"), Icons.Glyph(G.Mail, Theme.TextDim), (s, e) => NoteManager.RaiseOpenInbox());
            m.Items.Add(T("note.closeAll"), Icons.Glyph(G.Clear, Theme.TextDim), (s, e) => NoteManager.CloseAll());
            DarkRenderer.Polish(m);
            m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
            m.Show(this, new Point(Width - Theme.S(160), HeadHeight));
        }

        void Reply()
        {
            MarkRead();
            var peer = AppCore.I.Peers.FindById(Note.PeerId);
            NoteManager.Compose(peer, Note.Incoming ? Note : null, Note.Incoming ? null : Note.Text);
        }

        void MarkRead()
        {
            if (Note.Incoming && !Note.IsRead) AppCore.I.MarkNoteRead(Note);
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            MarkRead();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            MarkRead();
            base.OnFormClosed(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == (Keys.Control | Keys.R)) { Reply(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        public void Refresh2() => UpdateInfo();
    }

    /// <summary>쪽지 쓰기 창.</summary>
    public sealed class ComposeWindow : StickyBase
    {
        readonly RichTextBox _body;
        readonly DarkSelect _to;
        readonly Label _hint;
        readonly FlatButton _send;
        readonly Panel _footer, _toRow;
        readonly Label _toLabel;
        string _color;
        readonly Note _replyTo;
        const int MaxLen = 4000;

        public ComposeWindow(Peer to, Note replyTo, string initialText)
            : base(NotePalette.Get(AppCore.I.Config.NoteColor, AppCore.I.Config.EffectiveNoteStyle))
        {
            _replyTo = replyTo;
            _color = replyTo?.Color ?? AppCore.I.Config.NoteColor;
            if (replyTo != null) Pal = NotePalette.Get(_color, AppCore.I.Config.EffectiveNoteStyle);
            BackColor = Pal.Body;
            Size = new Size(Theme.S(360), Theme.S(320));
            HeadGlyph = replyTo != null ? G.Reply : G.Edit;
            HeadTitle = replyTo != null ? T("note.reply") : T("note.new");
            Text = T("note.composeTitle");
            AddHeadButton(G.Color, T("note.color"), PickColor);
            AddHeadButton(G.Close, T("note.closeEsc"), Close);

            _toRow = new Panel { Dock = DockStyle.Top, Height = Theme.S(44), BackColor = Pal.Body, Padding = new Padding(Theme.S(8), Theme.S(8), Theme.S(6), Theme.S(2)) };
            _toLabel = new Label { Text = T("note.toLabel"), Dock = DockStyle.Left, Width = Theme.S(70), ForeColor = Pal.SubText, TextAlign = ContentAlignment.MiddleLeft };
            _to = new DarkSelect { Dock = DockStyle.Fill, Placeholder = T("note.toPh") };
            _toRow.Controls.Add(_to);
            _toRow.Controls.Add(_toLabel);
            FillRecipients(to);

            _body = MakeBody(false);
            _body.DetectUrls = false;
            _body.TextChanged += (s, e) => UpdateHint();
            _body.KeyDown += (s, e) =>
            {
                // 서식 없는 텍스트만 붙여 넣는다.
                if (e.Control && e.KeyCode == Keys.V || e.Shift && e.KeyCode == Keys.Insert)
                {
                    e.SuppressKeyPress = true;
                    try { if (Clipboard.ContainsText()) _body.SelectedText = Clipboard.GetText(); } catch { }
                }
            };
            if (!string.IsNullOrEmpty(initialText)) _body.Text = initialText;
            var bodyHost = new Panel { Dock = DockStyle.Fill, BackColor = Pal.Body, Padding = new Padding(Theme.S(8), Theme.S(8), Theme.S(4), Theme.S(2)) };
            bodyHost.Controls.Add(_body);

            _footer = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(44), BackColor = Pal.Body, Padding = new Padding(Theme.S(8), Theme.S(6), Theme.S(4), Theme.S(6)) };
            _hint = new Label { Dock = DockStyle.Fill, ForeColor = Pal.SubText, Font = Theme.Small, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, AutoEllipsis = true };
            _send = MakeButton(T("common.send"), G.Send, true);
            _send.Dock = DockStyle.Right;
            _send.Click += (s, e) => Send();
            _footer.Controls.Add(_hint);
            _footer.Controls.Add(_send);

            Controls.Add(bodyHost);
            Controls.Add(_toRow);
            Controls.Add(_footer);
            ApplyPalette(Pal);
            UpdateHint();
        }

        void FillRecipients(Peer preferred)
        {
            _to.Items.Clear();
            var peers = AppCore.I.Peers.All().Where(p => p.State == PeerState.Paired && !p.Blocked)
                .OrderByDescending(p => p.IsOnline).ThenBy(p => p.DisplayName, NaturalComparer.Instance).ToList();
            foreach (var p in peers)
                _to.Add(p, p.DisplayName + (p.IsOnline ? "" : "  (" + T("presence.offline") + ")"), Icons.Avatar(p.DisplayName, p.Id, Theme.S(18), p.Presence, p.State, p.Blocked, Theme.Chrome));
            if (peers.Count(p => p.IsOnline) > 1)
                _to.Add("*", T("note.allOnline", peers.Count(p => p.IsOnline)), Icons.Glyph(G.People, Theme.S(16), Theme.TextDim));
            if (preferred != null) _to.SelectedValue = preferred;
            else
            {
                int i = _to.Items.FindIndex(x => x.Value is Peer p && p.IsOnline);
                _to.SelectedIndex = i;
            }
        }

        public override void ApplyPalette(NotePalette p)
        {
            base.ApplyPalette(p);
            _body.BackColor = p.Body;
            _body.ForeColor = p.Text;
            _body.Parent.BackColor = p.Body;
            _footer.BackColor = p.Body;
            _toRow.BackColor = p.Body;
            _toLabel.ForeColor = p.SubText;
            _toLabel.BackColor = p.Body;
            _hint.ForeColor = p.SubText;
            _hint.BackColor = p.Body;
            _to.BoxColor = p.InputBack;
            _to.BoxBorder = p.Border;
            _to.BoxFore = p.Text;
            _to.Invalidate();
            StyleButton(_send, true);
        }

        void PickColor()
        {
            var m = new ContextMenuStrip();
            foreach (var key in AppConfig.NoteColors)
            {
                string k = key;
                var it = new ToolStripMenuItem(NotePalette.Get(k, "pastel").DisplayName, NotePalette.Swatch(k, Theme.S(14))) { Checked = k == _color };
                it.Click += (s, e) => { _color = k; ApplyPalette(NotePalette.Get(k, AppCore.I.Config.EffectiveNoteStyle)); };
                m.Items.Add(it);
            }
            DarkRenderer.Polish(m);
            m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
            m.Show(this, new Point(Width - Theme.S(150), HeadHeight));
        }

        void UpdateHint(string error = null)
        {
            if (error != null)
            {
                _hint.ForeColor = Pal.Dark ? Theme.DangerHover : Theme.Hex("B42318");
                _hint.Text = error;
                return;
            }
            _hint.ForeColor = Pal.SubText;
            int n = _body.TextLength;
            _hint.Text = (n > MaxLen ? T("note.tooLong", n, MaxLen) : $"{n}/{MaxLen}") + "   ·   " + T("note.sendShortcut");
        }

        void Send()
        {
            string text = _body.Text.TrimEnd();
            if (text.Trim().Length == 0) { UpdateHint(T("note.empty")); _body.Focus(); return; }
            if (text.Length > MaxLen) { UpdateHint(T("note.maxLen", MaxLen)); return; }
            List<Peer> targets;
            if (_to.SelectedValue is Peer p) targets = new List<Peer> { p };
            else if ((_to.SelectedValue as string) == "*") targets = AppCore.I.Peers.All().Where(x => x.CanUse).ToList();
            else { UpdateHint(T("note.noRecipient")); _to.Focus(); return; }
            if (targets.Count == 0) { UpdateHint(T("note.noOnline")); return; }
            var offline = targets.Where(t => !t.IsOnline).ToList();
            if (offline.Count == targets.Count) { UpdateHint(T("note.peerOffline", offline[0].DisplayName)); return; }
            targets = targets.Where(t => t.IsOnline).ToList();

            _send.Enabled = false;
            _body.ReadOnly = true;
            UpdateHint();
            _hint.Text = T("note.sending");
            string color = _color;
            string reply = _replyTo?.Id;
            Task.Run(() =>
            {
                var fails = new List<string>();
                foreach (var t in targets)
                {
                    try { AppCore.I.SendNote(t, text, color, reply); }
                    catch (Exception ex) { fails.Add($"{t.DisplayName}: {ex.Message}"); Log.Warn(Cat.Note, T("note.log.sendFailed", t.DisplayName, ex.Message)); }
                }
                return fails;
            }).ContinueWith(t =>
            {
                if (IsDisposed) return;
                var fails = t.IsFaulted ? new List<string> { t.Exception?.GetBaseException().Message } : t.Result;
                if (fails.Count == 0)
                {
                    NoteManager.RaiseSent(targets.Count == 1 ? T("note.sentTo", targets[0].DisplayName) : TN("note.sentToMany", targets.Count));
                    Close();
                    return;
                }
                _send.Enabled = true;
                _body.ReadOnly = false;
                UpdateHint(T("note.sendFailed", string.Join(" / ", fails)));
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            if (_to.SelectedIndex < 0) _to.Focus(); else _body.Focus();
            _body.SelectionStart = _body.TextLength;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Enter)) { if (_send.Enabled) Send(); return true; }
            if (keyData == Keys.Escape)
            {
                if (_body.TextLength == 0 || MessageDialog.Confirm(this, T("note.discardTitle"), T("note.discardText"), T("common.close"))) Close();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    /// <summary>쪽지 창들을 관리 (표시 위치, 열린 창 목록, 소리).</summary>
    public static class NoteManager
    {
        static readonly List<NoteWindow> Open = new List<NoteWindow>();
        public static event Action<Peer> OpenPeerRequested;
        public static event Action OpenInboxRequested;
        public static event Action<string> Sent;

        public static void RaiseOpenPeer(Peer p) => OpenPeerRequested?.Invoke(p);
        public static void RaiseOpenInbox() => OpenInboxRequested?.Invoke();
        public static void RaiseSent(string msg) => Sent?.Invoke(msg);

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        static extern bool PlaySound(string name, IntPtr mod, uint flags);

        public static void PlayNoteSound()
        {
            try
            {
                // SND_ALIAS | SND_ASYNC | SND_NODEFAULT
                if (!PlaySound("Notification.IM", IntPtr.Zero, 0x00010000 | 0x0001 | 0x0002))
                    System.Media.SystemSounds.Asterisk.Play();
            }
            catch { }
        }

        /// <summary>받은 쪽지를 화면 오른쪽 위에 띄운다 (포커스는 뺏지 않음).</summary>
        public static NoteWindow ShowIncoming(Note n, bool activate = false)
        {
            var existing = Open.FirstOrDefault(w => w.Note.Id == n.Id && !w.IsDisposed);
            if (existing != null)
            {
                if (activate) { existing.Show(); existing.Activate(); }
                return existing;
            }
            var cfg = AppCore.I.Config;
            var w2 = new NoteWindow(n, cfg.NoteTopMost && n.Incoming) { ActivateOnShow = activate };
            w2.SetTopMostAtCreate(cfg.NoteTopMost && n.Incoming);
            var wa = Screen.PrimaryScreen.WorkingArea;
            int k = Open.Count(w => !w.IsDisposed) % 8;
            w2.Location = new Point(wa.Right - w2.Width - Theme.S(24) - k * Theme.S(26), wa.Top + Theme.S(24) + k * Theme.S(30));
            w2.FormClosed += (s, e) => Open.Remove(w2);
            Open.Add(w2);
            w2.Show();
            if (activate) w2.Activate();
            else Native.Flash(w2.Handle, 2);
            return w2;
        }

        public static void Compose(Peer to, Note replyTo = null, string initialText = null)
        {
            var w = new ComposeWindow(to, replyTo, initialText);
            var anchor = Form.ActiveForm;
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Point pos;
            var owner = Open.FirstOrDefault(x => replyTo != null && x.Note.Id == replyTo.Id && !x.IsDisposed);
            if (owner != null)
                pos = new Point(owner.Left - w.Width - Theme.S(12), owner.Top + Theme.S(20));
            else if (anchor != null && !(anchor is StickyBase))
                pos = new Point(anchor.Left + (anchor.Width - w.Width) / 2, anchor.Top + (anchor.Height - w.Height) / 3);
            else
                pos = new Point(wa.Left + (wa.Width - w.Width) / 2, wa.Top + (wa.Height - w.Height) / 3);
            pos.X = Math.Max(wa.Left, Math.Min(pos.X, wa.Right - w.Width));
            pos.Y = Math.Max(wa.Top, Math.Min(pos.Y, wa.Bottom - w.Height));
            w.Location = pos;
            w.Show();
            w.Activate();
        }

        public static void RefreshOpen()
        {
            foreach (var w in Open.ToList()) if (!w.IsDisposed) w.Refresh2();
        }

        public static void CloseAll()
        {
            foreach (var w in Open.ToList()) if (!w.IsDisposed) w.Close();
        }

        public static int OpenCount => Open.Count(w => !w.IsDisposed);
    }
}
