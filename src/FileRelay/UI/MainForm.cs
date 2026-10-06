using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

using static FileRelay.L;
namespace FileRelay.UI
{
    /// <summary>
    /// 메인 창: 메뉴, 툴바, [피어 목록 | 로컬 창 | 원격 창], 아래 전송/활동/로그, 상태 표시줄.
    /// 창을 닫아도(설정에 따라) 트레이에서 계속 실행된다.
    /// </summary>
    public sealed partial class MainForm : DarkForm, IMessageFilter
    {
        readonly AppCore _c = AppCore.I;
        MenuStrip _menu;
        ToolStrip _tool;
        StatusStrip _status;
        SplitContainer _splitMain, _splitV, _splitPanes;
        PeerPanel _peers;
        FilePane _local, _remote, _activePane;
        BottomPanel _bottom;
        ToolStripStatusLabel _stServer, _stPeers, _stFirewall, _stMsg, _stLimit;
        ToolStripProgress _stProg;
        ToolStripButton _tbPause, _tbInbox, _tbCompare, _tbDownload, _tbUpload;
        ToolStripDropDownButton _tbStatus;
        ToolStripMenuItem _miHidden, _miCompare, _miPause, _miPeerPanel, _miBottom;
        readonly Timer _uiTimer = new Timer { Interval = 500 };
        public TrayController Tray;
        bool _reallyClosing, _trayHintShown, _compare;
        DateTime _msgUntil = DateTime.MinValue;
        Dictionary<string, FsItem> _cmpLocal = new Dictionary<string, FsItem>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, FsItem> _cmpRemote = new Dictionary<string, FsItem>(StringComparer.OrdinalIgnoreCase);
        RemoteEditWatcher _edits;

        public MainForm()
        {
            Text = "FileRelay";
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.Manual;
            MinimumSize = new Size(Theme.S(900), Theme.S(560));
            Size = new Size(Theme.S(1360), Theme.S(860));
            KeyPreview = false;
            BackColor = Theme.Bg;

            BuildLayout();
            BuildMenu();
            BuildToolbar();
            BuildStatusBar();
            Controls.Add(_splitMain);
            Controls.Add(_tool);
            Controls.Add(_menu);
            Controls.Add(_status);
            MainMenuStrip = _menu;

            WireEvents();
            Tray = new TrayController(this);
            _edits = new RemoteEditWatcher(this);
            RestoreWindow();
            Application.AddMessageFilter(this);
            _uiTimer.Tick += (s, e) => UiTick();
            _uiTimer.Start();
        }

        protected override Color CaptionColor => Theme.Chrome;

        // ─── 배치 ──────────────────────────────────────────────────────────
        void BuildLayout()
        {
            _peers = new PeerPanel { Dock = DockStyle.Fill };
            _local = new FilePane(true) { Dock = DockStyle.Fill };
            _remote = new FilePane(false) { Dock = DockStyle.Fill };
            _local.Other = _remote;
            _remote.Other = _local;
            _bottom = new BottomPanel { Dock = DockStyle.Fill };

            _splitPanes = NewSplit(Orientation.Vertical);
            _splitPanes.Panel1.Controls.Add(_local);
            _splitPanes.Panel2.Controls.Add(_remote);

            _splitV = NewSplit(Orientation.Horizontal);
            _splitV.FixedPanel = FixedPanel.Panel2;
            _splitV.Panel1.Controls.Add(_splitPanes);
            _splitV.Panel2.Controls.Add(_bottom);

            _splitMain = NewSplit(Orientation.Vertical);
            _splitMain.FixedPanel = FixedPanel.Panel1;
            _splitMain.Panel1.Controls.Add(_peers);
            _splitMain.Panel2.Controls.Add(_splitV);
            _splitMain.Dock = DockStyle.Fill;
        }

        static SplitContainer NewSplit(Orientation o)
        {
            var s = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = o,
                BackColor = Theme.Splitter,
                SplitterWidth = Theme.S(4),
                TabStop = false,
            };
            s.Panel1.BackColor = Theme.Bg;
            s.Panel2.BackColor = Theme.Bg;
            return s;
        }

        // ─── 메뉴 ──────────────────────────────────────────────────────────
        ToolStripMenuItem Item(string text, char glyph, Action click, Keys keys = Keys.None, string keyText = null)
        {
            var it = new ToolStripMenuItem(text, glyph == '\0' ? null : Icons.Glyph(glyph, Theme.TextDim));
            if (click != null) it.Click += (s, e) => click();
            if (keys != Keys.None) it.ShortcutKeys = keys;
            if (keyText != null) it.ShortcutKeyDisplayString = keyText;
            return it;
        }

        void BuildMenu()
        {
            _menu = new MenuStrip { BackColor = Theme.Chrome, ForeColor = Theme.Text, Padding = new Padding(Theme.S(6), Theme.S(2), 0, Theme.S(2)), Font = Theme.Ui };

            var file = new ToolStripMenuItem(T("menu.file"));
            file.DropDownItems.AddRange(new ToolStripItem[]
            {
                Item(T("menu.addPeer"), G.AddFriend, AddPeer, Keys.Control | Keys.N),
                Item(T("menu.discover"), G.Search, Discover),
                new ToolStripSeparator(),
                Item(T("menu.compose"), G.Note, () => ComposeTo(_peers.SelectedPeer), Keys.Control | Keys.M),
                Item(T("main.notes"), G.Mail, () => NotesForm.ShowInbox(), Keys.Control | Keys.Shift | Keys.M),
                new ToolStripSeparator(),
                Item(T("menu.settings"), G.Settings, () => OpenSettings(), Keys.Control | Keys.Oemcomma, "Ctrl+,"),
                Item(T("set.adv.openConfig"), G.Edit, SettingsForm.OpenConfigFile),
                Item(T("set.adv.openData"), G.OpenFolder, () => ShellOps.OpenFolder(AppPaths.DataDir)),
                new ToolStripSeparator(),
                Item(T("menu.hideToTray"), G.Minimize, HideToTray),
                Item(T("menu.restart"), G.Refresh, () => Program.Restart()),
                Item(T("common.exit"), G.Power, ExitApp, Keys.Control | Keys.Q),
            });

            var edit = new ToolStripMenuItem(T("menu.edit"));
            edit.DropDownItems.AddRange(new ToolStripItem[]
            {
                Item(T("common.copy"), G.Copy, () => ActivePane?.CopySelected(), Keys.None, "Ctrl+C"),
                Item(T("menu.paste"), G.Paste, () => { var p = ActivePane; if (p != null) Paste(p, p.CurrentPath); }, Keys.None, "Ctrl+V"),
                new ToolStripSeparator(),
                Item(T("common.newFolder"), G.NewFolder, () => ActivePane?.NewFolder(), Keys.None, "Ctrl+Shift+N"),
                Item(T("common.rename"), G.Rename, () => ActivePane?.RenameSelected(), Keys.None, "F2"),
                Item(T("common.delete"), G.Delete, () => ActivePane?.DeleteSelected(false), Keys.None, "Del"),
                new ToolStripSeparator(),
                Item(T("menu.selectAll"), G.SelectAll, () => ActivePane?.List.SelectAllRows(), Keys.None, "Ctrl+A"),
                Item(T("menu.copyPath"), G.Link, () => ActivePane?.CopyPath()),
            });

            var view = new ToolStripMenuItem(T("menu.view"));
            _miHidden = Item(T("set.xf.showHidden"), G.View, ToggleHidden);
            _miCompare = Item(T("menu.compare"), G.Compare, ToggleCompare);
            _miPeerPanel = Item(T("menu.peerList"), G.People, () => { _splitMain.Panel1Collapsed = !_splitMain.Panel1Collapsed; UpdateViewChecks(); });
            _miBottom = Item(T("menu.bottomPanel"), G.List, () => { _splitV.Panel2Collapsed = !_splitV.Panel2Collapsed; UpdateViewChecks(); });
            view.DropDownItems.AddRange(new ToolStripItem[]
            {
                Item(T("common.refresh"), G.Refresh, RefreshAll, Keys.None, "F5"),
                new ToolStripSeparator(),
                _miHidden, _miCompare,
                new ToolStripSeparator(),
                _miPeerPanel, _miBottom,
                new ToolStripSeparator(),
                Item(T("menu.queue"), G.Sync, () => ShowBottom(BottomPanel.TabQueue)),
                Item(T("menu.activity"), G.Activity, () => ShowBottom(BottomPanel.TabActivity)),
                Item(T("menu.log"), G.List, () => ShowBottom(BottomPanel.TabLog)),
            });

            var go = new ToolStripMenuItem(T("menu.go"));
            go.DropDownItems.AddRange(new ToolStripItem[]
            {
                Item(T("menu.back"), G.Back, () => ActivePane?.GoBack(), Keys.None, "Alt+←"),
                Item(T("menu.forward"), G.Forward, () => ActivePane?.GoForward(), Keys.None, "Alt+→"),
                Item(T("menu.up"), G.Up, () => ActivePane?.GoUp(), Keys.None, "Alt+↑"),
                new ToolStripSeparator(),
                Item(T("menu.addressBar"), G.Edit, () => ActivePane?.FocusAddress(), Keys.None, "Ctrl+L"),
                Item(T("menu.filter"), G.Filter, () => ActivePane?.FocusFilter(), Keys.None, "Ctrl+F"),
                new ToolStripSeparator(),
                Item(T("menu.peerList"), G.People, () => _peers.FocusList(), Keys.Control | Keys.D1, "Ctrl+1"),
                Item(T("menu.localPane"), G.PC, () => _local.FocusList(), Keys.Control | Keys.D2, "Ctrl+2"),
                Item(T("menu.remotePane"), G.Network, () => _remote.FocusList(), Keys.Control | Keys.D3, "Ctrl+3"),
                new ToolStripSeparator(),
                Item(T("fs.desktop"), G.Home, () => { _local.Navigate(KnownFolders.Desktop); _local.FocusList(); }),
                Item(T("fs.downloads"), G.Download, () => { _local.Navigate(KnownFolders.Downloads); _local.FocusList(); }),
                Item(T("pane.thisPc"), G.PC, () => { _local.Navigate(""); _local.FocusList(); }),
            });

            var xfer = new ToolStripMenuItem(T("menu.transfer"));
            _miPause = Item(T("menu.pause"), G.Pause, TogglePause, Keys.Control | Keys.P);
            var limit = new ToolStripMenuItem(T("menu.speedLimit"), Icons.Glyph(G.Clock, Theme.TextDim));
            limit.DropDownOpening += (s, e) => FillLimitMenu(limit.DropDownItems);
            limit.DropDownItems.Add("-");
            xfer.DropDownItems.AddRange(new ToolStripItem[]
            {
                Item(T("menu.download"), G.Download, () => TransferFrom(_remote), Keys.None, "Ctrl+T"),
                Item(T("menu.upload"), G.Upload, () => TransferFrom(_local), Keys.None, "Ctrl+T"),
                Item(T("menu.downloadTo"), G.OpenFolder, DownloadTo),
                new ToolStripSeparator(),
                _miPause,
                Item(T("menu.cancelAll"), G.Stop, CancelAll),
                Item(T("menu.retryFailed"), G.Retry, () => _c.Transfers.Retry(_c.Transfers.Snapshot().Where(i => i.Status == TStatus.Failed).ToList())),
                Item(T("menu.clearDone"), G.Clear, () => _c.Transfers.Remove(i => i.Status == TStatus.Done || i.Status == TStatus.Skipped)),
                new ToolStripSeparator(),
                limit,
            });

            var peer = new ToolStripMenuItem(T("menu.peer"));
            peer.DropDownOpening += (s, e) =>
            {
                peer.DropDownItems.Clear();
                var tmp = new ContextMenuStrip();
                BuildPeerMenu(_peers.SelectedPeer, tmp);
                foreach (var it in tmp.Items.Cast<ToolStripItem>().ToList()) peer.DropDownItems.Add(it);
                tmp.Dispose();
                DarkRenderer.Polish(peer.DropDown);
            };
            peer.DropDownItems.Add("-");

            var tools = new ToolStripMenuItem(T("menu.tools"));
            tools.DropDownItems.AddRange(new ToolStripItem[]
            {
                Item(T("menu.firewall"), G.Shield, () => { Firewall.AddRuleInteractive(this); CheckFirewallAsync(); }),
                Item(T("menu.copyMyAddress"), G.Copy, CopyMyAddress),
                new ToolStripSeparator(),
                Item(T("menu.openLogFolder"), G.OpenFolder, () => ShellOps.OpenFolder(AppPaths.LogDir)),
                Item(T("menu.openTempFolder"), G.OpenFolder, () => { System.IO.Directory.CreateDirectory(_c.Config.EffectiveTempFolder); ShellOps.OpenFolder(_c.Config.EffectiveTempFolder); }),
            });

            var help = new ToolStripMenuItem(T("menu.help"));
            help.DropDownItems.AddRange(new ToolStripItem[]
            {
                Item(T("menu.shortcuts"), G.Help, () => { using (var d = new ShortcutsDialog()) d.ShowDialog(this); }, Keys.F1),
                Item(T("menu.about"), G.Info, () => { using (var d = new AboutDialog()) d.ShowDialog(this); }),
            });

            _menu.Items.AddRange(new ToolStripItem[] { file, edit, view, go, xfer, peer, tools, help });
            DarkRenderer.Polish(_menu);
        }

        void FillLimitMenu(ToolStripItemCollection items)
        {
            items.Clear();
            int cur = _c.Config.SpeedLimitKBps;
            foreach (var (kb, text) in new[] { (0, T("menu.unlimited")), (512, "512 KB/s"), (1024, "1 MB/s"), (2048, "2 MB/s"), (5120, "5 MB/s"), (10240, "10 MB/s"), (51200, "50 MB/s") })
            {
                int k = kb;
                items.Add(new ToolStripMenuItem(text, null, (s, e) => _c.SetSpeedLimit(k)) { Checked = cur == k });
            }
            items.Add(new ToolStripSeparator());
            items.Add(new ToolStripMenuItem(T("menu.limitCustom"), null, (s, e) =>
            {
                var v = InputDialog.Ask(this, T("menu.speedLimit"), T("menu.limitPrompt"), cur.ToString(), false, x => int.TryParse(x, out int n) && n >= 0 ? null : T("menu.limitInvalid"));
                if (v != null) _c.SetSpeedLimit(int.Parse(v));
            }));
        }

        // ─── 툴바 ──────────────────────────────────────────────────────────
        ToolStripButton Tb(string text, char glyph, string tip, Action click)
        {
            var b = new ToolStripButton(text, Icons.Glyph(glyph, Theme.S(18), Theme.Text))
            {
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                ToolTipText = tip,
                Padding = new Padding(Theme.S(4), 0, Theme.S(4), 0),
                Margin = new Padding(Theme.S(1), Theme.S(2), Theme.S(1), Theme.S(2)),
                AutoToolTip = false,
            };
            b.Click += (s, e) => click();
            return b;
        }

        void BuildToolbar()
        {
            _tool = new ToolStrip
            {
                BackColor = Theme.Chrome,
                ForeColor = Theme.Text,
                GripStyle = ToolStripGripStyle.Hidden,
                ImageScalingSize = new Size(Theme.S(18), Theme.S(18)),
                Padding = new Padding(Theme.S(8), Theme.S(4), Theme.S(8), Theme.S(4)),
                Font = Theme.Ui,
                CanOverflow = true,
                AutoSize = false,
                Height = Theme.S(46),
                ShowItemToolTips = true,
            };
            _tbDownload = Tb(T("tb.download"), G.Download, T("tb.downloadTip"), () => TransferFrom(_remote));
            _tbUpload = Tb(T("tb.upload"), G.Upload, T("tb.uploadTip"), () => TransferFrom(_local));
            _tbDownload.Image = Icons.Glyph(G.Download, Theme.S(18), Theme.Download);
            _tbUpload.Image = Icons.Glyph(G.Upload, Theme.S(18), Theme.Upload);
            _tbCompare = Tb(T("tb.compare"), G.Compare, T("tb.compareTip"), ToggleCompare);
            _tbPause = Tb(T("menu.pause"), G.Pause, T("tb.pauseTip"), TogglePause);
            _tbInbox = Tb(T("main.notes"), G.Mail, T("tb.notesTip"), () => NotesForm.ShowInbox());

            _tbStatus = new ToolStripDropDownButton(T("presence.online"), Icons.StatusDot(Presence.Online, Theme.S(18)))
            {
                Alignment = ToolStripItemAlignment.Right,
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                ToolTipText = T("tb.myStatus"),
                Margin = new Padding(Theme.S(1), Theme.S(2), Theme.S(1), Theme.S(2)),
            };
            _tbStatus.DropDownOpening += (s, e) =>
            {
                _tbStatus.DropDownItems.Clear();
                foreach (var p in new[] { Presence.Online, Presence.Away, Presence.Busy })
                {
                    var pp = p;
                    string desc = p == Presence.Busy ? "  " + T("tb.busyDesc") : "";
                    _tbStatus.DropDownItems.Add(new ToolStripMenuItem(Fmt.Presence(p) + desc, Icons.StatusDot(p, Theme.S(16)), (o, a) => _c.SetStatus(pp, false)) { Checked = _c.MyStatus == p });
                }
                DarkRenderer.Polish(_tbStatus.DropDown);
            };
            _tbStatus.DropDownItems.Add("-");
            var settings = Tb("", G.Settings, T("tb.settingsTip"), () => OpenSettings());
            settings.Alignment = ToolStripItemAlignment.Right;
            settings.DisplayStyle = ToolStripItemDisplayStyle.Image;

            _tool.Items.AddRange(new ToolStripItem[]
            {
                Tb(T("tb.addPeer"), G.AddFriend, T("tb.addPeerTip"), AddPeer),
                Tb(T("tb.find"), G.Search, T("tb.findTip"), Discover),
                new ToolStripSeparator(),
                _tbDownload, _tbUpload,
                Tb(T("common.refresh"), G.Refresh, T("tb.refreshTip"), RefreshAll),
                _tbCompare,
                new ToolStripSeparator(),
                _tbPause,
                Tb(T("menu.cancelAll"), G.Stop, T("tb.cancelAllTip"), CancelAll),
                new ToolStripSeparator(),
                Tb(T("tb.compose"), G.Note, T("tb.composeTip"), () => ComposeTo(_peers.SelectedPeer)),
                _tbInbox,
                settings,
                _tbStatus,
            });
        }

        // ─── 상태 표시줄 ───────────────────────────────────────────────────
        void BuildStatusBar()
        {
            _status = new StatusStrip { BackColor = Theme.Chrome, ForeColor = Theme.TextDim, SizingGrip = false, Font = Theme.Small, Padding = new Padding(Theme.S(8), 0, Theme.S(8), 0) };
            _status.Height = Theme.S(28);
            _stServer = new ToolStripStatusLabel { ToolTipText = T("sb.copyAddressTip"), IsLink = false };
            _stServer.Click += (s, e) =>
            {
                if (_c.Http.Running) { CopyMyAddress(); return; }
                _c.StartServer();
                if (_c.Http.Running) _c.Disc.Start(_c.Config.Port);
            };
            _stPeers = new ToolStripStatusLabel { Margin = new Padding(Theme.S(12), 0, 0, 0) };
            _stFirewall = new ToolStripStatusLabel(T("sb.firewall"), Icons.Glyph(G.Shield, Theme.S(14), Theme.Warn)) { Visible = false, Tag = Theme.Warn, Margin = new Padding(Theme.S(12), 0, 0, 0) };
            _stFirewall.Click += (s, e) => { Firewall.AddRuleInteractive(this); CheckFirewallAsync(); };
            _stMsg = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(Theme.S(16), 0, 0, 0) };
            _stLimit = new ToolStripStatusLabel { ToolTipText = T("sb.limitTip") };
            _stLimit.Click += (s, e) =>
            {
                var m = new ContextMenuStrip();
                FillLimitMenu(m.Items);
                DarkRenderer.Polish(m);
                m.Closed += (o, a) => BeginInvoke(new Action(m.Dispose));
                m.Show(_status, new Point(_stLimit.Bounds.X, -m.PreferredSize.Height));
            };
            _stProg = new ToolStripProgress { Width = Theme.S(330) };
            _status.Items.AddRange(new ToolStripItem[] { _stServer, _stPeers, _stFirewall, _stMsg, _stLimit, _stProg });
        }

        // ─── 이벤트 연결 ───────────────────────────────────────────────────
        void WireEvents()
        {
            _peers.PeerSelected += p => ConnectRemote(p);
            _peers.PeerActivated += p => { ConnectRemote(p); _remote.FocusList(); };
            _peers.BuildPeerMenu += BuildPeerMenu;
            _peers.AddRequested += AddPeer;
            _peers.BuildMeMenu += BuildMeMenu;

            foreach (var pane in new[] { _local, _remote })
            {
                pane.TransferRequested += TransferFrom;
                pane.OpenFileRequested += OpenFile;
                pane.PasteRequested += Paste;
                pane.DropRequested += Drop;
                pane.StateChanged += p => UpdateUi();
                pane.PaneActivated += p => { _activePane = p; UpdateUi(); };
                pane.Navigated += p => { if (_compare) RebuildCompare(); };
                pane.BuildMenu += BuildPaneMenu;
            }
            _local.CompareOf = it => Compare(it, _cmpRemote);
            _remote.CompareOf = it => Compare(it, _cmpLocal);
            _bottom.BuildTransferMenu += BuildTransferMenu;
            _bottom.TransferActivated += it => OpenTransferResult(it);

            _c.PeersChanged += OnPeersChanged;
            _c.NoteArrived += OnNoteArrived;
            _c.NotesChanged += () => { Tray?.UpdateBadge(); UpdateInbox(); NoteManager.RefreshOpen(); };
            _c.Notify += (t, x, k, a) => Tray?.Balloon(t, x, k, a);
            _c.PeerRequest += OnPeerRequest;
            _c.ConfigChanged += OnConfigChanged;
            _c.ServerStateChanged += () => { UpdateStatusBar(); Tray?.UpdateBadge(); };
            _c.StatusChanged += () => { UpdateStatusButton(); Tray?.UpdateBadge(); OnStatusChanged(); };

            _c.Transfers.AskConflict = req =>
            {
                if (IsDisposed) return null;
                try { return (ConflictRequest)Invoke(new Func<ConflictRequest>(() => { ShowMain(false); return ConflictDialog.Ask(this, req); })); }
                catch { return null; }
            };
            _c.Transfers.ItemFinished += it => { try { BeginInvoke(new Action(() => OnItemFinished(it))); } catch { } };
            _c.Transfers.BatchFinished += (b, done, failed) => { try { BeginInvoke(new Action(() => OnBatchFinished(done, failed))); } catch { } };

            NoteManager.OpenPeerRequested += p => { ShowMain(); _peers.Select(p); ConnectRemote(p); };
            NoteManager.OpenInboxRequested += () => NotesForm.ShowInbox();
            NoteManager.Sent += msg => Flash(msg, Theme.Success);
        }

        // ─── 창 상태 ───────────────────────────────────────────────────────
        void RestoreWindow()
        {
            var st = _c.State;
            var b = st.GetInts("window.bounds");
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            if (b.Count == 4)
            {
                var r = new Rectangle(b[0], b[1], b[2], b[3]);
                if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r)) && r.Width >= MinimumSize.Width / 2) Bounds = r;
                else CenterOn(area);
            }
            else CenterOn(area);
            if (st.GetBool("window.max")) WindowState = FormWindowState.Maximized;
            _compare = st.GetBool("view.compare");
            _splitMain.Panel1Collapsed = st.GetBool("view.hidePeers");
            _splitV.Panel2Collapsed = st.GetBool("view.hideBottom");
            _local.List.SortColumn = st.GetInt("sort.local.col", 0);
            _local.List.SortDescending = st.GetBool("sort.local.desc");
            _remote.List.SortColumn = st.GetInt("sort.remote.col", 0);
            _remote.List.SortDescending = st.GetBool("sort.remote.desc");
            RestoreColumns(_local.List, "cols.local");
            RestoreColumns(_remote.List, "cols.remote");
            RestoreColumns(_bottom.Transfers, "cols.transfers");
            // 방향 열은 언어마다 최소 폭이 다르다 (한국어로 쓰던 폭이면 영어 "Download" 가 잘린다).
            int dirMin = Theme.S(En ? 92 : 70);
            if (_bottom.Transfers.Columns[1].Width < dirMin) _bottom.Transfers.Columns[1].Width = dirMin;
        }

        void CenterOn(Rectangle area)
        {
            int w = Math.Min(Width, area.Width - Theme.S(40)), h = Math.Min(Height, area.Height - Theme.S(40));
            Bounds = new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
        }

        void RestoreColumns(ListView lv, string key)
        {
            var w = _c.State.GetInts(key);
            for (int i = 0; i < Math.Min(w.Count, lv.Columns.Count); i++)
                if (w[i] >= Theme.S(30)) lv.Columns[i].Width = w[i];
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            var st = _c.State;
            try
            {
                int pw = st.GetInt("split.peers", Theme.S(270));
                _splitMain.SplitterDistance = Num.Clamp(pw, Theme.S(200), Math.Max(Theme.S(200), _splitMain.Width / 2));
                int bh = st.GetInt("split.bottom", Theme.S(230));
                _splitV.SplitterDistance = Num.Clamp(_splitV.Height - bh, Theme.S(200), Math.Max(Theme.S(200), _splitV.Height - Theme.S(110)));
                double ratio = st.GetInt("split.panes.permille", 500) / 1000.0;
                _splitPanes.SplitterDistance = Num.Clamp((int)(_splitPanes.Width * ratio), Theme.S(260), Math.Max(Theme.S(260), _splitPanes.Width - Theme.S(260)));
            }
            catch { }
            _splitMain.Panel1MinSize = Theme.S(200);
            _splitPanes.Panel1MinSize = Theme.S(260);
            _splitPanes.Panel2MinSize = Theme.S(260);
            _splitV.Panel2MinSize = Theme.S(110);
            OnConfigChanged();
            string startLocal = st.GetStr("lastLocal", "");
            if (string.IsNullOrEmpty(startLocal) || !System.IO.Directory.Exists(startLocal)) startLocal = _c.Config.EffectiveDownloadFolder;
            _local.SetSource(new LocalFs { ShowHidden = _c.Config.ShowHiddenFiles }, startLocal);
            _remote.SetSource(null, null);
            var lastPeer = _c.Peers.FindById(st.GetStr("lastPeer", ""));
            if (lastPeer != null) { _peers.Select(lastPeer); ConnectRemote(lastPeer); }
            _activePane = _local;
            _local.FocusList();
            UpdateViewChecks();
            UpdateAll();
            CheckFirewallAsync();
            if (_c.Notes.UnreadCount > 0 && _c.MyStatus != Presence.Busy && _c.Config.NotePopup)
                foreach (var n in _c.Notes.All().Where(n => n.Incoming && !n.IsRead).OrderBy(n => n.Time).Take(5)) NoteManager.ShowIncoming(n);
        }

        void SaveWindow()
        {
            var st = _c.State;
            var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            st.SetInts("window.bounds", new[] { r.X, r.Y, r.Width, r.Height });
            st.Set("window.max", WindowState == FormWindowState.Maximized);
            try
            {
                if (!_splitMain.Panel1Collapsed) st.Set("split.peers", (long)_splitMain.SplitterDistance);
                if (!_splitV.Panel2Collapsed) st.Set("split.bottom", (long)(_splitV.Height - _splitV.SplitterDistance));
                if (_splitPanes.Width > 0) st.Set("split.panes.permille", (long)(_splitPanes.SplitterDistance * 1000L / _splitPanes.Width));
            }
            catch { }
            st.Set("view.compare", _compare);
            st.Set("view.hidePeers", _splitMain.Panel1Collapsed);
            st.Set("view.hideBottom", _splitV.Panel2Collapsed);
            st.Set("sort.local.col", (long)_local.List.SortColumn);
            st.Set("sort.local.desc", _local.List.SortDescending);
            st.Set("sort.remote.col", (long)_remote.List.SortColumn);
            st.Set("sort.remote.desc", _remote.List.SortDescending);
            st.SetInts("cols.local", _local.List.Columns.Cast<ColumnHeader>().Select(c => c.Width));
            st.SetInts("cols.remote", _remote.List.Columns.Cast<ColumnHeader>().Select(c => c.Width));
            st.SetInts("cols.transfers", _bottom.Transfers.Columns.Cast<ColumnHeader>().Select(c => c.Width));
            if (_remote.Source is RemoteFs rf) st.Set("lastPeer", rf.Peer.Id);
        }

        // ─── 보이기 / 숨기기 / 종료 ────────────────────────────────────────
        public void ShowMain(bool activate = true)
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = _c.State.GetBool("window.max") ? FormWindowState.Maximized : FormWindowState.Normal;
            if (activate)
            {
                Activate();
                Native.SetForegroundWindow(Handle);
            }
        }

        void HideToTray()
        {
            SaveWindow();
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                Tray?.Balloon(T("main.trayHint.title"), T("main.trayHint.text"), NotifyKind.Info);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_reallyClosing && e.CloseReason == CloseReason.UserClosing)
            {
                // 창을 닫을 때마다 묻는다 (기본: 트레이에서 계속 실행). 닫기 처리 중에 다시 Close 를 부르지 않도록 지연 실행.
                e.Cancel = true;
                BeginInvoke(new Action(AskOnClose));
                return;
            }
            SaveWindow();
            base.OnFormClosing(e);
        }

        bool _askingClose;

        void AskOnClose()
        {
            if (_askingClose || _reallyClosing) return;
            _askingClose = true;
            try
            {
                var st = _c.Transfers.Stats();
                string msg = T("main.close.text");
                if (st.Busy)
                    msg += "\n\n" + TN("main.close.busy", st.Active + st.Queued);
                var r = MessageDialog.Show(this, T("main.close.title"), msg, MsgKind.Question, null,
                    new DlgBtn(T("main.close.keep"), DialogResult.Yes, ButtonKind.Primary),
                    new DlgBtn(T("main.close.exit"), DialogResult.No),
                    new DlgBtn(T("common.cancel"), DialogResult.Cancel));
                if (r == DialogResult.Yes) HideToTray();
                else if (r == DialogResult.No) ForceExit();
            }
            finally { _askingClose = false; }
        }

        bool ConfirmExit()
        {
            var st = _c.Transfers.Stats();
            if (st.Busy)
                return MessageDialog.Confirm(this, T("main.exitBusy.title"), TN("main.exitBusy.text", st.Active + st.Queued), T("common.exit"), true);
            if (_c.Config.ConfirmOnExit)
                return MessageDialog.Confirm(this, T("main.exit.title"), T("main.exit.text"), T("common.exit"), false, null, MsgKind.Question);
            return true;
        }

        /// <summary>확인 없이 종료 (다시 시작 등).</summary>
        public void ForceExit()
        {
            _reallyClosing = true;
            ExitApp();
        }

        public void ExitApp()
        {
            if (!_reallyClosing)
            {
                if (!Visible) ShowMain();
                if (!ConfirmExit()) return;
            }
            _reallyClosing = true;
            SaveWindow();
            NoteManager.CloseAll();
            Tray?.Dispose();
            Tray = null;
            Application.RemoveMessageFilter(this);
            Close();
            Application.ExitThread();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            // 창이 실제로 닫히면 트레이까지 정리하고 메시지 루프를 끝낸다.
            NoteManager.CloseAll();
            Tray?.Dispose();
            Tray = null;
            Application.RemoveMessageFilter(this);
            Application.ExitThread();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_QUERYENDSESSION) { _reallyClosing = true; SaveWindow(); }
            base.WndProc(ref m);
        }

        // ─── 마우스 뒤로/앞으로 버튼 ───────────────────────────────────────
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != Native.WM_XBUTTONUP && m.Msg != Native.WM_XBUTTONDOWN) return false;
            var ctl = Control.FromChildHandle(m.HWnd);
            while (ctl != null && !(ctl is FilePane)) ctl = ctl.Parent;
            if (!(ctl is FilePane pane) || pane.FindForm() != this) return false;
            if (m.Msg == Native.WM_XBUTTONDOWN) return true;
            int btn = (int)(((long)m.WParam >> 16) & 0xFFFF);
            if (btn == 1) pane.GoBack();
            else if (btn == 2) pane.GoForward();
            return true;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.F5:
                    RefreshAll();
                    return true;
                case Keys.Control | Keys.Shift | Keys.F:
                    Discover();
                    return true;
                case Keys.Escape:
                    if (_peers.ContainsFocus) { _local.FocusList(); return true; }
                    break;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _uiTimer.Dispose(); _edits?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
