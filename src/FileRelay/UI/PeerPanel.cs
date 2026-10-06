using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

using static FileRelay.L;
namespace FileRelay.UI
{
    /// <summary>
    /// 왼쪽 피어 목록 — 아바타 + 상태 배지(온라인/자리 비움/방해 금지/오프라인/대기/차단),
    /// 온라인·오프라인 묶음 머리글, 안 읽은 쪽지 배지, 아래쪽 내 프로필 카드.
    /// </summary>
    public sealed class PeerPanel : Panel
    {
        readonly AppCore _c = AppCore.I;
        readonly PeerListBox _list;
        readonly DarkTextBox _search;
        readonly HeaderBar _head;
        readonly MeCard _me;
        readonly FlatButton _add;

        public event Action<Peer> PeerSelected;
        public event Action<Peer> PeerActivated;
        public event Action<Peer, ContextMenuStrip> BuildPeerMenu;
        public event Action AddRequested;
        public event Action<ContextMenuStrip> BuildMeMenu;

        public PeerPanel()
        {
            BackColor = Theme.Panel;
            _head = new HeaderBar { Dock = DockStyle.Top, Height = Theme.S(46), ChipRightInset = Theme.S(34) };
            _add = new FlatButton("", ButtonKind.Ghost, G.AddFriend) { Size = new Size(Theme.S(32), Theme.S(32)), CustomBack = Theme.Panel, CustomHover = Theme.Hover };
            _add.Click += (s, e) => AddRequested?.Invoke();
            new ToolTip().SetToolTip(_add, T("peers.addTip"));
            _head.Controls.Add(_add);
            _head.Resize += (s, e) => _add.Location = new Point(_head.Width - _add.Width - Theme.S(8), (_head.Height - _add.Height) / 2);

            var searchHost = new Panel { Dock = DockStyle.Top, Height = Theme.S(46), BackColor = Theme.Panel, Padding = new Padding(Theme.S(10), Theme.S(6), Theme.S(10), Theme.S(8)) };
            _search = new DarkTextBox { Dock = DockStyle.Fill, Glyph = G.Search, Placeholder = T("peers.searchPh") };
            _search.TextChanged += (s, e) => Rebuild(true);
            _search.Box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Enter) { _list.Focus(); _list.SelectFirstPeer(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Escape) { _search.Text = ""; _list.Focus(); e.SuppressKeyPress = true; }
            };
            searchHost.Controls.Add(_search);

            _list = new PeerListBox { Dock = DockStyle.Fill };
            _list.SelectedPeerChanged += p => PeerSelected?.Invoke(p);
            _list.DoubleClick += (s, e) => { if (_list.SelectedPeer != null) PeerActivated?.Invoke(_list.SelectedPeer); };
            _list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && _list.SelectedPeer != null) { PeerActivated?.Invoke(_list.SelectedPeer); e.Handled = true; }
                else if (e.KeyCode == Keys.Apps || e.KeyCode == Keys.F10 && e.Shift) { ShowPeerMenu(new Point(Theme.S(30), Theme.S(30))); e.Handled = true; }
            };
            _list.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                int i = _list.ItemAt(e.Location);
                if (i >= 0 && _list.Items[i] is Peer) _list.SelectedIndex = i;
                ShowPeerMenu(e.Location);
            };

            _me = new MeCard { Dock = DockStyle.Bottom };
            _me.Click += (s, e) =>
            {
                var m = new ContextMenuStrip();
                BuildMeMenu?.Invoke(m);
                DarkRenderer.Polish(m);
                m.Closed += (o, a) => BeginInvoke(new Action(m.Dispose));
                m.Show(_me, new Point(Theme.S(8), -m.PreferredSize.Height));
            };

            Controls.Add(_list);
            Controls.Add(searchHost);
            Controls.Add(_head);
            Controls.Add(_me);

            _c.PeersChanged += () => Rebuild(false);
            _c.StatusChanged += () => _me.Invalidate();
            _c.ConfigChanged += () => _me.Invalidate();
            _c.ServerStateChanged += () => _me.Invalidate();
            Rebuild(true);
        }

        public Peer SelectedPeer => _list.SelectedPeer;
        public void FocusList() { _list.Focus(); if (_list.SelectedPeer == null) _list.SelectFirstPeer(); }
        public void FocusSearch() => _search.Focus();

        public void Select(Peer p) => _list.SelectPeer(p);

        void ShowPeerMenu(Point at)
        {
            var m = new ContextMenuStrip();
            BuildPeerMenu?.Invoke(_list.SelectedPeer, m);
            if (m.Items.Count == 0) { m.Dispose(); return; }
            DarkRenderer.Polish(m);
            m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
            m.Show(_list, at);
        }

        public void Rebuild(bool force)
        {
            var all = _c.Peers.All();
            int online = all.Count(p => p.IsOnline && p.State == PeerState.Paired && !p.Blocked);
            _head.Set(T("peers.title"), all.Length == 0 ? T("peers.none") : T("peers.summary", online, all.Length), Icons.Glyph(G.People, Theme.S(22), Theme.Accent));
            string q = _search.Text.Trim();
            var vis = all.Where(p => q.Length == 0 || p.DisplayName.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                                     p.Host.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (p.Name ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
            _list.SetPeers(vis, force, q.Length > 0);
            _me.Invalidate();
        }

        /// <summary>피어 목록 상자 (오너드로우, 묶음 머리글 포함).</summary>
        sealed class PeerListBox : ListBox
        {
            int _hot = -1;
            string _sig = "";
            bool _guard;
            Peer _lastSelected;
            public event Action<Peer> SelectedPeerChanged;

            public PeerListBox()
            {
                DrawMode = DrawMode.OwnerDrawVariable;
                BorderStyle = BorderStyle.None;
                BackColor = Theme.Panel;
                ForeColor = Theme.Text;
                Font = Theme.Ui;
                IntegralHeight = false;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
                Theme.ApplyControlTheme(this);
            }

            public Peer SelectedPeer => SelectedItem as Peer;

            /// <summary>
            /// 좌표의 항목 인덱스 (없으면 -1). ListBox.IndexFromPoint 는 목록이 비어 있으면
            /// -1 이 아니라 65535(0xFFFF)를 돌려주므로 범위와 실제 영역을 다시 확인한다.
            /// </summary>
            public int ItemAt(Point p)
            {
                int n = Items.Count;
                if (n == 0) return -1;
                int i = IndexFromPoint(p);
                if (i < 0 || i >= n) return -1;
                return GetItemRectangle(i).Contains(p) ? i : -1;
            }

            public void SelectFirstPeer()
            {
                for (int i = 0; i < Items.Count; i++) if (Items[i] is Peer) { SelectedIndex = i; return; }
            }

            public void SelectPeer(Peer p)
            {
                int i = Items.IndexOf(p);
                if (i >= 0) SelectedIndex = i;
            }

            static int Group(Peer p) =>
                p.Blocked ? 3 : p.State == PeerState.Incoming ? 0 : p.IsOnline && p.State == PeerState.Paired ? 1 : 2;

            public void SetPeers(List<Peer> peers, bool force, bool searching)
            {
                var ordered = peers.OrderBy(Group).ThenBy(p => p.DisplayName, NaturalComparer.Instance).ToList();
                var items = new List<object>();
                foreach (var g in ordered.GroupBy(Group))
                {
                    string title = g.Key == 0 ? T("peers.grp.requests") : g.Key == 1 ? T("presence.online") : g.Key == 2 ? T("peers.grp.offline") : T("peers.grp.blocked");
                    items.Add(title + "  " + g.Count());
                    items.AddRange(g);
                }
                string sig = string.Join("|", items.Select(x => x is Peer p ? p.GetHashCode().ToString() : (string)x));
                if (!force && sig == _sig) { Invalidate(); return; }
                _sig = sig;
                var sel = SelectedPeer ?? _lastSelected;
                int top = TopIndex;
                _guard = true;
                _hot = -1;
                BeginUpdate();
                Items.Clear();
                foreach (var x in items) Items.Add(x);
                if (sel != null && Items.Contains(sel)) SelectedItem = sel;
                try { TopIndex = Math.Min(top, Math.Max(0, Items.Count - 1)); } catch { }
                EndUpdate();
                _guard = false;
                if (items.Count == 0) Invalidate();
            }

            protected override void OnMeasureItem(MeasureItemEventArgs e)
            {
                e.ItemHeight = e.Index >= 0 && e.Index < Items.Count && Items[e.Index] is string ? Theme.S(30) : Theme.S(54);
            }

            protected override void OnSelectedIndexChanged(EventArgs e)
            {
                if (!_guard && SelectedIndex >= 0 && Items[SelectedIndex] is string)
                {
                    // 머리글은 선택되지 않게 다음 피어로.
                    int i = SelectedIndex;
                    _guard = true;
                    int dir = _lastSelected != null && Items.IndexOf(_lastSelected) > i ? -1 : 1;
                    int j = i + dir;
                    while (j >= 0 && j < Items.Count && !(Items[j] is Peer)) j += dir;
                    if (j < 0 || j >= Items.Count) { j = i - dir; while (j >= 0 && j < Items.Count && !(Items[j] is Peer)) j -= dir; }
                    SelectedIndex = j >= 0 && j < Items.Count ? j : -1;
                    _guard = false;
                }
                base.OnSelectedIndexChanged(e);
                if (_guard) return;
                var p = SelectedPeer;
                if (p != _lastSelected)
                {
                    _lastSelected = p;
                    SelectedPeerChanged?.Invoke(p);
                }
                Invalidate();
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                int i = ItemAt(e.Location);
                if (i != _hot)
                {
                    if (_hot >= 0 && _hot < Items.Count) Invalidate(GetItemRectangle(_hot));
                    _hot = i;
                    if (_hot >= 0) Invalidate(GetItemRectangle(_hot));
                }
                Cursor = i >= 0 && Items[i] is Peer ? Cursors.Hand : Cursors.Default;
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (_hot >= 0 && _hot < Items.Count) Invalidate(GetItemRectangle(_hot));
                _hot = -1;
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                using (var b = new SolidBrush(BackColor)) e.Graphics.FillRectangle(b, ClientRectangle);
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg == Native.WM_PAINT && Items.Count == 0)
                {
                    using (var g = CreateGraphics())
                    {
                        var r = new Rectangle(Theme.S(16), Theme.S(30), Width - Theme.S(32), Theme.S(120));
                        TextRenderer.DrawText(g, T("peers.empty"), Theme.Small, r, Theme.TextFaint, BackColor,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
                    }
                }
            }

            protected override void OnDrawItem(DrawItemEventArgs e)
            {
                if (e.Index < 0 || e.Index >= Items.Count) return;
                var g = e.Graphics;
                Theme.HQ(g);
                var r = e.Bounds;
                using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, r);
                var item = Items[e.Index];
                if (item is string head)
                {
                    Theme.DrawText(g, head.ToUpperInvariant(), Theme.SmallBold, Theme.TextFaint, new Rectangle(r.X + Theme.S(16), r.Y + Theme.S(6), r.Width - Theme.S(20), r.Height - Theme.S(6)));
                    return;
                }
                var p = (Peer)item;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                var card = new Rectangle(r.X + Theme.S(6), r.Y + Theme.S(2), r.Width - Theme.S(12), r.Height - Theme.S(4));
                if (sel) Theme.FillRound(g, Focused ? Theme.Selection : Theme.SelectionInactive, card, Theme.S(8));
                else if (e.Index == _hot) Theme.FillRound(g, Theme.Hover, card, Theme.S(8));
                int av = Theme.S(36);
                var bgForRing = sel ? (Focused ? Theme.Selection : Theme.SelectionInactive) : e.Index == _hot ? Theme.Hover : BackColor;
                var img = Icons.Avatar(p.DisplayName, string.IsNullOrEmpty(p.Id) ? p.Host : p.Id, av, p.Presence, p.State, p.Blocked, bgForRing);
                g.DrawImage(img, card.X + Theme.S(8), card.Y + (card.Height - av) / 2);
                int x = card.X + Theme.S(8) + av + Theme.S(10);
                int badgeW = 0;
                if (p.Unread > 0)
                {
                    string t = p.Unread > 99 ? "99+" : p.Unread.ToString();
                    var ts = Theme.Measure(t, Theme.SmallBold);
                    badgeW = Math.Max(Theme.S(20), ts.Width + Theme.S(10));
                    var br = new Rectangle(card.Right - badgeW - Theme.S(10), card.Y + (card.Height - Theme.S(20)) / 2, badgeW, Theme.S(20));
                    Theme.FillRound(g, Theme.Accent, br, Theme.S(10));
                    Theme.DrawText(g, t, Theme.SmallBold, Color.White, br, Theme.TextCenter);
                    badgeW += Theme.S(14);
                }
                int w = card.Right - x - Theme.S(8) - badgeW;
                bool dim = !p.IsOnline || p.State != PeerState.Paired || p.Blocked;
                string name = p.DisplayName;
                if (!string.IsNullOrEmpty(p.Alias) && !string.IsNullOrEmpty(p.Name) && p.Alias != p.Name) name += "  (" + p.Name + ")";
                int lh = Theme.UiBold.Height, sh = Theme.Small.Height;
                int top = card.Y + (card.Height - lh - sh - Theme.S(2)) / 2;
                Theme.DrawText(g, name, Theme.UiBold, dim ? Theme.TextDim : Theme.Text, new Rectangle(x, top, w, lh));
                Color subColor = p.Blocked || p.State == PeerState.Unpaired ? Theme.Danger :
                                 p.State == PeerState.Pending || p.State == PeerState.Incoming ? Theme.Warn :
                                 p.IsOnline ? Theme.PresenceColor(p.Presence) : Theme.TextFaint;
                string status = FilePane.PeerStatusText(p);
                var ss = Theme.Measure(status, Theme.Small);
                Theme.DrawText(g, status, Theme.Small, subColor, new Rectangle(x, top + lh + Theme.S(2), Math.Min(w, ss.Width + Theme.S(4)), sh));
                if (ss.Width + Theme.S(10) < w)
                    Theme.DrawText(g, "· " + p.Host, Theme.Small, Theme.TextFaint, new Rectangle(x + ss.Width + Theme.S(6), top + lh + Theme.S(2), w - ss.Width - Theme.S(6), sh));
            }
        }

        /// <summary>아래쪽 내 프로필 카드 (클릭 → 상태/이름 메뉴).</summary>
        sealed class MeCard : BufferedPanel
        {
            bool _hover;

            public MeCard()
            {
                Height = Theme.S(64);
                BackColor = Theme.Panel;
                Cursor = Cursors.Hand;
            }

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var c = AppCore.I;
                var g = e.Graphics;
                g.Clear(BackColor);
                Theme.HQ(g);
                using (var p = new Pen(Theme.BorderSoft)) g.DrawLine(p, 0, 0, Width, 0);
                var card = new Rectangle(Theme.S(6), Theme.S(6), Width - Theme.S(12), Height - Theme.S(12));
                if (_hover) Theme.FillRound(g, Theme.Hover, card, Theme.S(8));
                int av = Theme.S(36);
                var img = Icons.Avatar(c.Config.UserName, c.MyId, av, c.MyStatus, PeerState.Paired, false, _hover ? Theme.Hover : BackColor);
                g.DrawImage(img, card.X + Theme.S(8), card.Y + (card.Height - av) / 2);
                int x = card.X + Theme.S(8) + av + Theme.S(10);
                int w = card.Right - x - Theme.S(28);
                int lh = Theme.UiBold.Height, sh = Theme.Small.Height;
                int top = card.Y + (card.Height - lh - sh - Theme.S(2)) / 2;
                Theme.DrawText(g, c.Config.UserName + "  (" + T("common.me") + ")", Theme.UiBold, Theme.Text, new Rectangle(x, top, w, lh));
                string st = c.Http.Running ? Fmt.Presence(c.MyStatus) + (c.StatusIsAuto ? " " + T("peers.auto") : "") : T("peers.serverOff");
                var ss = Theme.Measure(st, Theme.Small);
                Theme.DrawText(g, st, Theme.Small, c.Http.Running ? Theme.PresenceColor(c.MyStatus) : Theme.Danger, new Rectangle(x, top + lh + Theme.S(2), ss.Width + Theme.S(4), sh));
                Theme.DrawText(g, "· " + NetUtil.PrimaryIPv4() + ":" + c.Config.Port, Theme.Small, Theme.TextFaint, new Rectangle(x + ss.Width + Theme.S(6), top + lh + Theme.S(2), w - ss.Width - Theme.S(6), sh));
                var chev = Icons.Glyph('', Theme.S(12), Theme.TextDim);
                g.DrawImage(chev, card.Right - Theme.S(22), card.Y + (card.Height - chev.Height) / 2);
            }
        }
    }
}
