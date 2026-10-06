using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

using static FileRelay.L;
namespace FileRelay.UI
{
    /// <summary>피어 추가 (IP/PC 이름 입력).</summary>
    public sealed class AddPeerDialog : DialogBase
    {
        readonly DarkTextBox _addr, _port;
        readonly Label _status;
        readonly PictureBox _statusIcon;
        readonly FlatButton _ok, _pending;
        readonly AppCore _c = AppCore.I;
        public Peer Result;
        public bool OpenDiscover;

        public AddPeerDialog(string initial = "")
        {
            Text = T("pdlg.add.title");
            int W = Theme.S(500), pad = Theme.S(22), y = Theme.S(18);
            Body.Controls.Add(new PictureBox { Image = Icons.Glyph(G.AddFriend, Theme.S(28), Theme.Accent), Size = new Size(Theme.S(28), Theme.S(28)), Location = new Point(pad, y + Theme.S(2)) });
            int tl = pad + Theme.S(42);
            Body.Controls.Add(new Label { Text = T("pdlg.add.title"), Font = Theme.Title, ForeColor = Theme.Text, AutoSize = true, Location = new Point(tl, y) });
            y += Theme.S(34);
            var desc = new NoteLabel(T("pdlg.add.desc")) { Location = new Point(tl, y) };
            desc.FitHeight(W - tl - pad);
            Body.Controls.Add(desc);
            y = desc.Bottom + Theme.S(14);

            Body.Controls.Add(new Label { Text = T("pdlg.address"), ForeColor = Theme.TextDim, AutoSize = true, Location = new Point(tl, y) });
            Body.Controls.Add(new Label { Text = T("set.gen.port"), ForeColor = Theme.TextDim, AutoSize = true, Location = new Point(W - pad - Theme.S(96), y) });
            y += Theme.S(22);
            _addr = new DarkTextBox { Location = new Point(tl, y), Size = new Size(W - tl - pad - Theme.S(106), Theme.S(34)), Placeholder = T("pdlg.add.addrPh"), Glyph = G.Network };
            _addr.Text = initial;
            _port = new DarkTextBox { Location = new Point(W - pad - Theme.S(96), y), Size = new Size(Theme.S(96), Theme.S(34)), Text = _c.Config.Port.ToString() };
            _port.Box.KeyPress += (s, e) => { if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true; };
            Body.Controls.Add(_addr);
            Body.Controls.Add(_port);
            y = _addr.Bottom + Theme.S(12);

            _statusIcon = new PictureBox { Size = new Size(Theme.S(16), Theme.S(16)), Location = new Point(tl, y + Theme.S(2)) };
            _status = new Label { ForeColor = Theme.TextDim, AutoSize = false, UseMnemonic = false, Location = new Point(tl + Theme.S(22), y), Size = new Size(W - tl - pad - Theme.S(22), Theme.S(40)) };
            Body.Controls.Add(_statusIcon);
            Body.Controls.Add(_status);
            y = _status.Bottom + Theme.S(4);

            string mine = string.Join(", ", NetUtil.LocalIPv4().Take(3).Select(ip => ip + ":" + _c.Config.Port));
            var myCard = new BufferedPanel { Location = new Point(tl, y), Size = new Size(W - tl - pad, Theme.S(40)) };
            var copy = new FlatButton(T("common.copy"), ButtonKind.Ghost, G.Copy) { Height = Theme.S(28) };
            copy.AutoFit();
            copy.Location = new Point(myCard.Width - copy.Width - Theme.S(6), Theme.S(6));
            copy.CustomBack = Theme.Panel;
            copy.Click += (s, e) => { try { Clipboard.SetText(NetUtil.PrimaryIPv4() + ":" + _c.Config.Port); copy.Text = T("pdlg.copied"); } catch { } };
            myCard.Controls.Add(copy);
            myCard.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.Clear(Theme.Bg);
                Theme.HQ(g);
                Theme.FillRound(g, Theme.Panel, new RectangleF(0, 0, myCard.Width - 1, myCard.Height - 1), Theme.S(8));
                Theme.DrawText(g, T("pdlg.myAddress"), Theme.Small, Theme.TextFaint, new Rectangle(Theme.S(12), 0, Theme.S(60), myCard.Height));
                Theme.DrawText(g, mine, Theme.Ui, Theme.Text, new Rectangle(Theme.S(64), 0, myCard.Width - Theme.S(150), myCard.Height));
            };
            Body.Controls.Add(myCard);
            y = myCard.Bottom + Theme.S(14);
            ClientSize = new Size(W, y + Footer.Height);

            var disc = new FlatButton(T("pdlg.findOnNetwork"), ButtonKind.Ghost, G.Search);
            disc.AutoFit();
            disc.Click += (s, e) => { OpenDiscover = true; DialogResult = DialogResult.Retry; };
            FooterLeft.Controls.Add(disc);

            AddButton(T("common.cancel"), DialogResult.Cancel);
            _ok = AddButton(T("common.add"), DialogResult.None, ButtonKind.Primary, G.Add);
            _ok.Click += (s, e) => Run();
            AcceptButton = _ok;
            _pending = AddButton(T("pdlg.addPending"), DialogResult.None, ButtonKind.Secondary);
            _pending.Visible = false;
            _pending.Click += (s, e) => AddPending();
        }

        void SetStatus(string text, Color color, char glyph)
        {
            _status.Text = text;
            _status.ForeColor = color;
            _statusIcon.Image = glyph == '\0' ? null : Icons.Glyph(glyph, Theme.S(16), color);
        }

        bool Parse(out string host, out int port)
        {
            int.TryParse(_port.Text, out int defPort);
            if (defPort <= 0) defPort = _c.Config.Port;
            if (!NetUtil.TryParseAddress(_addr.Text, defPort, out host, out port))
            {
                SetStatus(T("pdlg.badAddress"), Theme.Danger, G.Error);
                _addr.Focus();
                return false;
            }
            if (NetUtil.IsLocalAddress(host) && port == _c.Config.Port)
            {
                SetStatus(T("peer.selfAddress"), Theme.Danger, G.Error);
                return false;
            }
            return true;
        }

        void Run()
        {
            if (!Parse(out string host, out int port)) return;
            _ok.Enabled = _addr.Enabled = _port.Enabled = false;
            _pending.Visible = false;
            SetStatus(T("pdlg.connecting", host, port), Theme.TextDim, G.Sync);
            Task.Run(() => _c.PeerSvc.AddPeer(host, port)).ContinueWith(t =>
            {
                if (IsDisposed) return;
                _ok.Enabled = _addr.Enabled = _port.Enabled = true;
                var r = t.IsFaulted ? new AddResult { Message = t.Exception?.GetBaseException().Message } : t.Result;
                if (r.Ok)
                {
                    Result = r.Peer;
                    if (r.Pending)
                    {
                        MessageDialog.Info(this, T("pdlg.waitTitle"), T("pdlg.waitText", r.Peer.DisplayName));
                    }
                    DialogResult = DialogResult.OK;
                    return;
                }
                if (r.Unreachable)
                {
                    SetStatus(T("pdlg.unreachable", r.Message), Theme.Warn, G.Warning);
                    _pending.Visible = true;
                    _pending.AutoFit(Theme.S(88));
                }
                else SetStatus(r.Message, Theme.Danger, G.Error);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void AddPending()
        {
            if (!Parse(out string host, out int port)) return;
            Result = _c.PeerSvc.AddPending(host, port);
            DialogResult = DialogResult.OK;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _addr.Focus();
        }
    }

    /// <summary>LAN 검색 결과에서 피어 추가.</summary>
    public sealed class DiscoverDialog : DialogBase
    {
        readonly DarkListView _list;
        readonly Label _status;
        readonly FlatButton _add, _again;
        List<Discovery.Found> _found = new List<Discovery.Found>();
        readonly AppCore _c = AppCore.I;
        public int Added;

        public DiscoverDialog()
        {
            Text = T("pdlg.disc.title");
            int W = Theme.S(620), pad = Theme.S(22), y = Theme.S(18);
            Body.Controls.Add(new Label { Text = T("pdlg.disc.title"), Font = Theme.Title, ForeColor = Theme.Text, AutoSize = true, Location = new Point(pad, y) });
            y += Theme.S(34);
            var desc = new NoteLabel(T("pdlg.disc.desc")) { Location = new Point(pad, y) };
            desc.FitHeight(W - pad * 2);
            Body.Controls.Add(desc);
            y = desc.Bottom + Theme.S(10);

            _list = new DarkListView { Location = new Point(pad, y), Size = new Size(W - pad * 2, Theme.S(240)), MultiSelect = true, RowHeight = Theme.S(30) };
            _list.Columns.Add(T("common.name"), Theme.S(170));
            _list.Columns.Add(T("pdlg.address"), Theme.S(150));
            _list.Columns.Add(T("pdlg.pcName"), Theme.S(130));
            _list.Columns.Add(T("pdlg.status"), Theme.S(110));
            _list.GetText = (r, c) =>
            {
                var f = _found[r];
                switch (c)
                {
                    case 0: return f.Name;
                    case 1: return f.Host + ":" + f.Port;
                    case 2: return f.Machine;
                    default: return Known(f) ? T("pdlg.disc.known") : Fmt.Presence(AccessParse.ParsePresence(f.Status));
                }
            };
            _list.GetColor = (r, c) => Known(_found[r]) ? Theme.TextFaint : c == 0 ? Theme.Text : Theme.TextDim;
            _list.GetIcon = r => Icons.Avatar(_found[r].Name, _found[r].Id, Theme.S(22), AccessParse.ParsePresence(_found[r].Status), PeerState.Paired, false, Theme.Bg);
            _list.DoubleClick += (s, e) => AddSelected();
            _list.SelectedIndexChanged += (s, e) => UpdateButtons();
            _list.VirtualItemsSelectionRangeChanged += (s, e) => UpdateButtons();
            Body.Controls.Add(_list);
            y = _list.Bottom + Theme.S(8);
            _status = new Label { ForeColor = Theme.TextDim, AutoSize = false, Location = new Point(pad, y), Size = new Size(W - pad * 2, Theme.S(22)) };
            Body.Controls.Add(_status);
            y = _status.Bottom + Theme.S(6);
            ClientSize = new Size(W, y + Footer.Height);

            _again = new FlatButton(T("pdlg.disc.again"), ButtonKind.Ghost, G.Refresh);
            _again.AutoFit();
            _again.Click += (s, e) => Search();
            FooterLeft.Controls.Add(_again);
            AddButton(T("common.close"), DialogResult.Cancel);
            _add = AddButton(T("common.add"), DialogResult.None, ButtonKind.Primary, G.Add);
            _add.Click += (s, e) => AddSelected();
            AcceptButton = _add;
            UpdateButtons();
        }

        bool Known(Discovery.Found f) => _c.Peers.FindById(f.Id) != null;

        void UpdateButtons()
        {
            _add.Enabled = _list.SelectedIndices.Count > 0 && _list.SelectedRows().Any(i => i < _found.Count && !Known(_found[i]));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Search();
        }

        void Search()
        {
            _again.Enabled = false;
            _status.Text = T("pdlg.disc.searchingStatus");
            _list.EmptyText = T("pdlg.disc.searching");
            Task.Run(() => Discovery.Search(_c, 2000)).ContinueWith(t =>
            {
                if (IsDisposed) return;
                _again.Enabled = true;
                _found = t.IsFaulted ? new List<Discovery.Found>() : t.Result;
                _list.SetCount(_found.Count);
                _list.EmptyText = T("pdlg.disc.none");
                _list.EmptySub = T("pdlg.disc.noneSub");
                _status.Text = t.IsFaulted ? T("pdlg.disc.failed", t.Exception?.GetBaseException().Message) : TN("pdlg.disc.found", _found.Count);
                UpdateButtons();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void AddSelected()
        {
            var sel = _list.SelectedRows().Where(i => i < _found.Count).Select(i => _found[i]).Where(f => !Known(f)).ToList();
            if (sel.Count == 0) return;
            _add.Enabled = false;
            _status.Text = TN("pdlg.disc.adding", sel.Count);
            Task.Run(() => sel.Select(f => _c.PeerSvc.AddPeer(f.Host, f.Port)).ToList()).ContinueWith(t =>
            {
                if (IsDisposed) return;
                var res = t.IsFaulted ? new List<AddResult>() : t.Result;
                Added += res.Count(r => r.Ok);
                var fails = res.Where(r => !r.Ok).Select(r => r.Message).ToList();
                _status.Text = fails.Count == 0 ? TN("pdlg.disc.added", res.Count) : T("pdlg.disc.partial", string.Join(" / ", fails));
                _list.Invalidate();
                UpdateButtons();
                if (fails.Count == 0) DialogResult = DialogResult.OK;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
