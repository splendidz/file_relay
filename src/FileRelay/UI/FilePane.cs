using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

using static FileRelay.L;
namespace FileRelay.UI
{
    /// <summary>앱 내부 클립보드 (Ctrl+C 한 항목과 출처).</summary>
    public static class AppClipboard
    {
        public static FilePane Pane;
        public static FsSource Source;
        public static List<FsItem> Items = new List<FsItem>();
        public static bool HasItems => Items.Count > 0 && Source != null;

        public static void Clear() { Pane = null; Source = null; Items = new List<FsItem>(); }
    }

    /// <summary>
    /// 파일 창 하나 (로컬 또는 원격 피어).
    ///  · 뒤로/앞으로/상위/새로 고침, 주소 표시줄(최근·즐겨찾기), 필터
    ///  · 탐색기 단축키, 마우스 뒤로/앞으로 버튼(MainForm 의 메시지 필터가 전달)
    ///  · 끌어서 놓기, 컨텍스트 메뉴, 비동기 목록 읽기
    /// </summary>
    public sealed class FilePane : Panel
    {
        public const string DragFormat = "FileRelay.PaneItems";

        public readonly bool Local;
        public FsSource Source { get; private set; }
        public string CurrentPath { get; private set; } = "";
        public Perms Perms { get; private set; }
        public long Free { get; private set; } = -1;
        public bool Loading { get; private set; }
        public string LastError { get; private set; } = "";
        public readonly FileListView List;
        public FilePane Other;
        public Func<FsItem, CompareMark> CompareOf { set => List.CompareOf = value; }
        public IReadOnlyList<FsItem> AllItems => _all;

        readonly HeaderBar _head;
        readonly FlatButton _back, _fwd, _up, _refresh, _hist, _star;
        readonly DarkTextBox _addr, _filter;
        readonly Label _status, _freeLabel;
        readonly List<string> _backStack = new List<string>(), _fwdStack = new List<string>();
        List<FsItem> _all = new List<FsItem>();
        int _navToken;
        readonly ToolTip _tips = new ToolTip();

        public event Action<FilePane> TransferRequested;
        public event Action<FilePane, FsItem> OpenFileRequested;
        public event Action<FilePane, string> PasteRequested;
        public event Action<FilePane, IDataObject, string> DropRequested;
        public event Action<FilePane> StateChanged;
        public event Action<FilePane> PaneActivated;
        public event Action<FilePane> Navigated;
        public event Action<FilePane, ContextMenuStrip, List<FsItem>> BuildMenu;

        public FilePane(bool local)
        {
            Local = local;
            BackColor = Theme.Bg;
            Padding = Padding.Empty;

            _head = new HeaderBar { Dock = DockStyle.Top, Height = Theme.S(46) };

            var nav = new Panel { Dock = DockStyle.Top, Height = Theme.S(46), BackColor = Theme.Panel, Padding = new Padding(Theme.S(8), Theme.S(6), Theme.S(8), Theme.S(8)) };
            nav.Paint += (s, e) => { using (var p = new Pen(Theme.BorderSoft)) e.Graphics.DrawLine(p, 0, nav.Height - 1, nav.Width, nav.Height - 1); };
            _back = NavButton(G.Back, T("pane.tip.back"), GoBack);
            _fwd = NavButton(G.Forward, T("pane.tip.forward"), GoForward);
            _up = NavButton(G.Up, T("pane.tip.up"), GoUp);
            _refresh = NavButton(G.Refresh, T("pane.tip.refresh"), RefreshList);
            _addr = new DarkTextBox { Dock = DockStyle.Fill, Placeholder = local ? T("pane.thisPc") : T("pane.shareList"), RightInset = Theme.S(56) };
            _addr.Box.KeyDown += AddrKeyDown;
            _addr.Box.GotFocus += (s, e) => BeginInvoke(new Action(() => _addr.Box.SelectAll()));
            _star = new FlatButton("", ButtonKind.Ghost, G.Star) { Size = new Size(Theme.S(26), Theme.S(24)), CustomBack = Theme.Input, CustomHover = Theme.Hover };
            _star.Click += (s, e) => ToggleBookmark();
            _hist = new FlatButton("", ButtonKind.Ghost, G.ChevronDown) { Size = new Size(Theme.S(26), Theme.S(24)), CustomBack = Theme.Input, CustomHover = Theme.Hover };
            _hist.Click += (s, e) => ShowHistoryMenu();
            _addr.Controls.Add(_star);
            _addr.Controls.Add(_hist);
            _addr.Resize += (s, e) =>
            {
                _hist.Location = new Point(_addr.Width - _hist.Width - Theme.S(4), (_addr.Height - _hist.Height) / 2);
                _star.Location = new Point(_hist.Left - _star.Width, _hist.Top);
            };
            _tips.SetToolTip(_star, T("pane.tip.star"));
            _tips.SetToolTip(_hist, T("pane.tip.history"));
            _filter = new DarkTextBox { Dock = DockStyle.Right, Width = Theme.S(150), Glyph = G.Filter, Placeholder = T("pane.filterPh") };
            _filter.TextChanged += (s, e) => ApplyFilter(true);
            _filter.Box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { _filter.Text = ""; List.Focus(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Down) { List.Focus(); if (List.ItemCount > 0 && List.SelectedIndices.Count == 0) List.SelectRow(0); e.SuppressKeyPress = true; }
            };
            var gap1 = new Panel { Dock = DockStyle.Left, Width = Theme.S(6), BackColor = Theme.Panel };
            var gap2 = new Panel { Dock = DockStyle.Right, Width = Theme.S(6), BackColor = Theme.Panel };
            nav.Controls.Add(_addr);
            nav.Controls.Add(gap1);
            nav.Controls.Add(_refresh);
            nav.Controls.Add(_up);
            nav.Controls.Add(_fwd);
            nav.Controls.Add(_back);
            nav.Controls.Add(gap2);
            nav.Controls.Add(_filter);
            foreach (var b in new[] { _back, _fwd, _up, _refresh }) { b.Dock = DockStyle.Left; }

            List = new FileListView { Dock = DockStyle.Fill, IsLocal = local };
            List.KeyDown += ListKeyDown;
            List.DoubleClick += (s, e) => ActivateSelected();
            List.SelectedIndexChanged += (s, e) => OnSelection();
            List.VirtualItemsSelectionRangeChanged += (s, e) => OnSelection();
            List.GotFocus += (s, e) => PaneActivated?.Invoke(this);
            List.MouseUp += ListMouseUp;
            List.ItemDrag += ListItemDrag;
            List.DragEnter += ListDragOver;
            List.DragOver += ListDragOver;
            List.DragLeave += (s, e) => List.DropTarget = -1;
            List.DragDrop += ListDragDrop;

            var footer = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(28), BackColor = Theme.Panel, Padding = new Padding(Theme.S(10), 0, Theme.S(10), 0) };
            footer.Paint += (s, e) => { using (var p = new Pen(Theme.BorderSoft)) e.Graphics.DrawLine(p, 0, 0, footer.Width, 0); };
            _status = new Label { Dock = DockStyle.Fill, ForeColor = Theme.TextDim, Font = Theme.Small, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, AutoEllipsis = true };
            _freeLabel = new Label { Dock = DockStyle.Right, Width = Theme.S(170), ForeColor = Theme.TextFaint, Font = Theme.Small, TextAlign = ContentAlignment.MiddleRight };
            footer.Controls.Add(_status);
            footer.Controls.Add(_freeLabel);

            Controls.Add(List);
            Controls.Add(footer);
            Controls.Add(nav);
            Controls.Add(_head);
            UpdateNavButtons();
        }

        FlatButton NavButton(char glyph, string tip, Action click)
        {
            var b = new FlatButton("", ButtonKind.Ghost, glyph) { Width = Theme.S(32), Height = Theme.S(32), CustomBack = Theme.Panel, CustomHover = Theme.Hover };
            b.Click += (s, e) => click();
            _tips.SetToolTip(b, tip);
            return b;
        }

        // ─── 소스 / 머리글 ────────────────────────────────────────────────
        public void SetSource(FsSource src, string path)
        {
            Source = src;
            _backStack.Clear();
            _fwdStack.Clear();
            _all = new List<FsItem>();
            List.SetItems(new List<FsItem>());
            CurrentPath = "";
            _filter.Text = "";
            UpdateHeader();
            if (src == null)
            {
                List.EmptyText = T("pane.pickPeer");
                List.EmptySub = T("pane.pickPeerSub");
                _addr.Text = "";
                UpdateStatus();
                UpdateNavButtons();
                return;
            }
            Navigate(path ?? "", false);
        }

        public void UpdateHeader()
        {
            if (Local)
            {
                _head.Set(T("pane.thisPc"), Environment.MachineName + " · " + AppCore.I.Config.UserName, Icons.Glyph(G.PC, Theme.S(26), Theme.Accent));
                _head.SetChip("", Theme.Raised, Theme.TextDim);
                return;
            }
            if (!(Source is RemoteFs rf))
            {
                _head.Set(T("pane.remote"), T("pane.selectPeer"), Icons.Glyph(G.Network, Theme.S(26), Theme.TextFaint));
                _head.SetChip("", Theme.Raised, Theme.TextDim);
                return;
            }
            var p = rf.Peer;
            string sub = p.Address + " · " + PeerStatusText(p);
            _head.Set(p.DisplayName, sub, Icons.Avatar(p.DisplayName, p.Id, Theme.S(30), p.Presence, p.State, p.Blocked, Theme.Panel));
            if (CurrentPath.Length == 0 || Loading) _head.SetChip("", Theme.Raised, Theme.TextDim);
            else
            {
                var a = Perms.Delete ? AccessLevel.Full : Perms.Write ? AccessLevel.Write : AccessLevel.Read;
                Color c = a == AccessLevel.Full ? Theme.Warn : a == AccessLevel.Write ? Theme.Info : Theme.TextDim;
                _head.SetChip(Fmt.Access(a), Theme.Blend(Theme.Panel, c, 0.18f), c);
            }
        }

        public static string PeerStatusText(Peer p)
        {
            if (p.Blocked) return T("pane.st.blocked");
            switch (p.State)
            {
                case PeerState.Pending: return T("pane.st.pending");
                case PeerState.Incoming: return T("pane.st.incoming");
                case PeerState.Unpaired: return T("pane.st.unpaired");
            }
            return p.IsOnline ? Fmt.Presence(p.Presence) : T("pane.st.offlineSince", Fmt.Relative(p.LastSeen));
        }

        // ─── 탐색 ──────────────────────────────────────────────────────────
        public void Navigate(string path, bool addHistory = true, string focusName = null, ICollection<string> keepSel = null)
        {
            if (Source == null) return;
            path = path ?? "";
            int token = ++_navToken;
            var src = Source;
            string from = CurrentPath;
            Loading = true;
            List.EmptyText = T("pane.loading");
            List.EmptySub = "";
            _refresh.Enabled = false;
            UpdateStatus();
            Task.Run(() =>
            {
                var items = src.List(path, out var perms, out var free);
                return (items, perms, free);
            }).ContinueWith(t =>
            {
                if (IsDisposed || token != _navToken || src != Source) return;
                Loading = false;
                _refresh.Enabled = true;
                if (t.IsFaulted)
                {
                    var ex = t.Exception.GetBaseException();
                    LastError = ex.Message;
                    Log.Warn(Local ? Cat.Local : Cat.Remote, T("pane.log.openFailed", src.RootName, path, ex.Message));
                    if (_all.Count == 0 && path.Length > 0 && !addHistory && from.Length == 0 && !(ex is ApiException ae && ae.IsNetwork))
                    {
                        // 처음 열 때 마지막 위치가 없어졌으면 맨 위(내 PC / 공유 목록)로.
                        Navigate("", false);
                        return;
                    }
                    if (path != CurrentPath || _all.Count == 0)
                    {
                        // 처음 열기 실패면 목록을 비우고 이유를 보여준다. 이동 실패면 이전 위치 유지.
                        if (_all.Count == 0 || CurrentPath == path)
                        {
                            List.SetItems(new List<FsItem>());
                            bool offline = ex is ApiException oe && (oe.Code == "offline" || oe.IsNetwork);
                            List.EmptyText = offline ? T("pane.cantConnect") : T("pane.cantOpen");
                            List.EmptySub = ex.Message;
                            CurrentPath = path;
                            _addr.Text = src.Display(path);
                        }
                        else
                        {
                            _status.Text = T("pane.cantOpenMsg", ex.Message);
                            _status.ForeColor = Theme.Danger;
                            _addr.Text = src.Display(CurrentPath);
                            System.Media.SystemSounds.Beep.Play();
                            UpdateNavButtons();
                            return;
                        }
                    }
                    UpdateHeader();
                    UpdateNavButtons();
                    StateChanged?.Invoke(this);
                    return;
                }
                LastError = "";
                var (items, perms, free) = t.Result;
                bool sameDir = PathEq(path, from);
                if (addHistory && !sameDir)
                {
                    _backStack.Add(from);
                    if (_backStack.Count > 100) _backStack.RemoveAt(0);
                    _fwdStack.Clear();
                }
                // 상위로 올라가면 방금 있던 폴더를 선택해 준다 (탐색기 방식).
                if (focusName == null && !sameDir && from.Length > 0 && PathEq(src.Parent(from) ?? "\u0001", path))
                    focusName = Local ? Path.GetFileName(from.TrimEnd('\\')) is string fn && fn.Length > 0 ? fn : from.TrimEnd('\\') : PathUtil.Name(from);
                if (focusName == null && !sameDir && from.Length > 0 && path.Length == 0 && Local)
                    focusName = items.FirstOrDefault(i => PathEq(i.Path, from) || (i.Kind == FsKind.Drive && from.StartsWith(i.Path, StringComparison.OrdinalIgnoreCase)))?.Name;
                CurrentPath = path;
                Perms = perms;
                Free = free;
                _all = items;
                if (!sameDir) _filter.Text = "";
                SetAddress(src.Display(path));
                if (keepSel == null && sameDir) keepSel = List.SelectedItemsList();
                ApplyFilter(false, keepSel, focusName);
                string emptyMain = items.Count == 0 ? (path.Length == 0 && !Local ? T("pane.noShares") : T("pane.emptyFolder")) : "";
                List.EmptyText = emptyMain;
                List.EmptySub = path.Length == 0 && !Local && items.Count == 0 ? T("pane.noSharesSub") : "";
                _status.ForeColor = Theme.TextDim;
                RememberRecent(path);
                UpdateHeader();
                UpdateNavButtons();
                UpdateStatus();
                Navigated?.Invoke(this);
                StateChanged?.Invoke(this);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>주소 표시줄 — 포커스가 없으면 경로의 끝(현재 폴더 이름)이 보이게.</summary>
        void SetAddress(string text)
        {
            _addr.Text = text;
            if (!_addr.Box.Focused && _addr.Box.IsHandleCreated)
            {
                _addr.Box.Select(_addr.Box.TextLength, 0);
                _addr.Box.ScrollToCaret();
            }
        }

        static bool PathEq(string a, string b) => string.Equals((a ?? "").TrimEnd('\\'), (b ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        public void RefreshList()
        {
            if (Source == null) return;
            Navigate(CurrentPath, false, null, List.SelectedItemsList());
        }

        public void GoBack()
        {
            if (_backStack.Count == 0 || Source == null) return;
            string p = _backStack[_backStack.Count - 1];
            _backStack.RemoveAt(_backStack.Count - 1);
            _fwdStack.Add(CurrentPath);
            Navigate(p, false, Local ? Path.GetFileName(CurrentPath.TrimEnd('\\')) : PathUtil.Name(CurrentPath));
        }

        public void GoForward()
        {
            if (_fwdStack.Count == 0 || Source == null) return;
            string p = _fwdStack[_fwdStack.Count - 1];
            _fwdStack.RemoveAt(_fwdStack.Count - 1);
            _backStack.Add(CurrentPath);
            Navigate(p, false);
        }

        public void GoUp()
        {
            if (Source == null) return;
            var parent = Source.Parent(CurrentPath);
            if (parent == null) return;
            Navigate(parent);
        }

        void UpdateNavButtons()
        {
            _back.Enabled = _backStack.Count > 0 && Source != null;
            _fwd.Enabled = _fwdStack.Count > 0 && Source != null;
            _up.Enabled = Source != null && Source.Parent(CurrentPath) != null;
            _refresh.Enabled = Source != null && !Loading;
            _addr.Enabled = _filter.Enabled = Source != null;
            bool marked = Source != null && CurrentPath.Length > 0 && Bookmarks().Any(b => PathEq(b, CurrentPath));
            _star.Glyph = marked ? G.StarFill : G.Star;
            _star.GlyphColor = marked ? Theme.Warn : (Color?)null;
            _star.Visible = Source != null && CurrentPath.Length > 0;
        }

        public void ActivateSelected()
        {
            var it = FocusedOrFirstSelected();
            if (it == null) return;
            if (it.IsDir) Navigate(it.Path);
            else OpenFileRequested?.Invoke(this, it);
        }

        FsItem FocusedOrFirstSelected()
        {
            int f = List.FocusedRow;
            if (f >= 0 && f < List.Data.Count && List.SelectedIndices.Contains(f)) return List.Data[f];
            var sel = Selected();
            return sel.Count > 0 ? sel[0] : null;
        }

        public List<FsItem> Selected() =>
            List.SelectedRows().Where(i => i < List.Data.Count).Select(i => List.Data[i]).ToList();

        // ─── 필터 / 상태 ───────────────────────────────────────────────────
        void ApplyFilter(bool keepSelection, ICollection<string> sel = null, string focus = null)
        {
            string f = _filter.Text.Trim();
            List<FsItem> items;
            if (f.Length == 0) items = new List<FsItem>(_all);
            else if (f.IndexOf('*') >= 0 || f.IndexOf('?') >= 0)
            {
                var rx = new Regex("^" + Regex.Escape(f).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);
                items = _all.Where(i => rx.IsMatch(i.Name)).ToList();
            }
            else items = _all.Where(i => i.Name.IndexOf(f, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
            if (keepSelection && sel == null) sel = List.SelectedItemsList();
            List.SetItems(items, sel, focus);
            if (f.Length > 0 && items.Count == 0) { List.EmptyText = T("pane.filterNoMatch"); List.EmptySub = T("pane.filterClear"); }
            UpdateStatus();
        }

        void OnSelection()
        {
            UpdateStatus();
            StateChanged?.Invoke(this);
        }

        public void UpdateStatus()
        {
            if (Source == null) { _status.Text = ""; _freeLabel.Text = ""; return; }
            if (Loading) { _status.Text = T("pane.loading"); return; }
            int dirs = List.Data.Count(i => i.IsDir), files = List.Data.Count - dirs;
            string s = TN("pane.nFolders", dirs) + ", " + TN("pane.nFiles", files);
            if (_filter.Text.Trim().Length > 0) s += "  " + T("pane.filterCount", List.Data.Count, _all.Count);
            var sel = Selected();
            if (sel.Count > 0)
            {
                long bytes = sel.Where(i => !i.IsDir).Sum(i => i.Size);
                s += "   ·   " + TN("pane.nSelected", sel.Count) + (bytes > 0 ? $" ({Fmt.Size(bytes)})" : "");
            }
            _status.Text = s;
            _freeLabel.Text = Free >= 0 ? T("pane.free", Fmt.Size(Free)) : "";
        }

        public string SelectionSummary()
        {
            var sel = Selected();
            if (sel.Count == 0) return "";
            long bytes = sel.Where(i => !i.IsDir).Sum(i => i.Size);
            return TN("pane.selSummary", sel.Count, Local ? Cat.Local : Cat.Remote) + (bytes > 0 ? $" · {Fmt.Size(bytes)}" : "");
        }

        // ─── 키보드 ────────────────────────────────────────────────────────
        void AddrKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                if (Source == null) return;
                string target = Source.ParseInput(_addr.Text, CurrentPath);
                Navigate(target);
                List.Focus();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                _addr.Text = Source?.Display(CurrentPath) ?? "";
                List.Focus();
            }
        }

        void ListKeyDown(object sender, KeyEventArgs e)
        {
            bool handled = true;
            switch (e.KeyData)
            {
                case Keys.Enter: ActivateSelected(); break;
                case Keys.Back: GoUp(); break;
                case Keys.F2: RenameSelected(); break;
                case Keys.Delete: DeleteSelected(false); break;
                case Keys.Shift | Keys.Delete: DeleteSelected(true); break;
                case Keys.Control | Keys.A: List.SelectAllRows(); break;
                case Keys.Control | Keys.C: CopySelected(); break;
                case Keys.Control | Keys.V: PasteRequested?.Invoke(this, CurrentPath); break;
                case Keys.Control | Keys.Shift | Keys.N: NewFolder(); break;
                case Keys.Alt | Keys.Enter: Properties(); break;
                case Keys.Control | Keys.T:
                case Keys.Control | Keys.Enter: TransferRequested?.Invoke(this); break;
                case Keys.Escape:
                    if (_filter.Text.Length > 0) _filter.Text = "";
                    else List.SelectedIndices.Clear();
                    break;
                case Keys.Apps:
                case Keys.Shift | Keys.F10:
                    {
                        int f = List.FocusedRow;
                        var pt = f >= 0 ? new Point(Theme.S(40), List.GetItemRect(f).Bottom) : new Point(Theme.S(40), Theme.S(40));
                        ShowMenu(pt);
                        break;
                    }
                default: handled = false; break;
            }
            if (handled) { e.Handled = true; e.SuppressKeyPress = true; }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Alt | Keys.Left: GoBack(); return true;
                case Keys.Alt | Keys.Right: GoForward(); return true;
                case Keys.Alt | Keys.Up: GoUp(); return true;
                case Keys.F5:
                case Keys.Control | Keys.R: RefreshList(); return true;
                case Keys.Control | Keys.L:
                case Keys.Alt | Keys.D:
                case Keys.F4: FocusAddress(); return true;
                case Keys.Control | Keys.F: _filter.Focus(); _filter.SelectAll(); return true;
                case Keys.Tab:
                case Keys.Shift | Keys.Tab:
                    if (List.Focused && Other != null) { Other.FocusList(); return true; }
                    break;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        public void FocusAddress()
        {
            _addr.Focus();
            _addr.SelectAll();
        }

        public void FocusList()
        {
            List.Focus();
            if (List.ItemCount > 0 && List.SelectedIndices.Count == 0)
            {
                int f = List.FocusedRow;
                List.SelectRow(f >= 0 ? f : 0);
            }
        }

        public void FocusFilter() { _filter.Focus(); _filter.SelectAll(); }

        // ─── 파일 작업 ─────────────────────────────────────────────────────
        public void CopySelected()
        {
            var sel = Selected().Where(i => i.Kind != FsKind.Special || Local).ToList();
            if (sel.Count == 0) return;
            AppClipboard.Pane = this;
            AppClipboard.Source = Source;
            AppClipboard.Items = sel;
            if (Local)
            {
                // 탐색기에도 붙여 넣을 수 있게 Windows 클립보드에도 올린다.
                try
                {
                    var sc = new StringCollection();
                    sc.AddRange(sel.Select(i => i.Path).ToArray());
                    Clipboard.SetFileDropList(sc);
                }
                catch { }
            }
            Log.Info(Local ? Cat.Local : Cat.Remote, TN("pane.log.copied", sel.Count));
        }

        public void NewFolder()
        {
            if (Source == null) return;
            if (CurrentPath.Length == 0 || !Perms.Write)
            {
                MessageDialog.Warn(FindForm(), T("common.newFolder"), CurrentPath.Length == 0 ? T("pane.cantMkdirHere") : T("pane.noWrite"));
                return;
            }
            string dir = CurrentPath;
            string name = InputDialog.Ask(FindForm(), T("common.newFolder"), T("pane.folderName"), PathUtil.UniqueName(T("common.newFolder"), n => _all.Any(i => i.Name.Eq(n))), false, PathUtil.ValidateName);
            if (name == null) return;
            var src = Source;
            Task.Run(() => src.MakeDir(dir, name)).ContinueWith(t =>
            {
                if (IsDisposed) return;
                if (t.IsFaulted) { MessageDialog.Error(FindForm(), T("pane.mkdirFailed"), t.Exception.GetBaseException().Message); return; }
                Log.Success(Local ? Cat.Local : Cat.Remote, T("pane.log.newFolder", name));
                if (src == Source && PathEq(dir, CurrentPath)) Navigate(CurrentPath, false, name, new[] { name });
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        public void RenameSelected()
        {
            var sel = Selected();
            if (sel.Count != 1 || Source == null) return;
            var it = sel[0];
            if (it.IsContainerRoot) return;
            if (!Perms.Rename) { MessageDialog.Warn(FindForm(), T("common.rename"), T("pane.noRename")); return; }
            string name = InputDialog.Ask(FindForm(), T("common.rename"), T("pane.newName"), it.Name, !it.IsDir, v =>
            {
                var err = PathUtil.ValidateName(v);
                if (err != null) return err;
                if (!v.Eq(it.Name) && _all.Any(i => i.Name.Eq(v))) return T("err.exists_name");
                return null;
            });
            if (name == null || name == it.Name) return;
            var src = Source;
            string dir = CurrentPath;
            Task.Run(() => src.Rename(it, name)).ContinueWith(t =>
            {
                if (IsDisposed) return;
                if (t.IsFaulted) { MessageDialog.Error(FindForm(), T("pane.renameFailed"), t.Exception.GetBaseException().Message); return; }
                Log.Success(Local ? Cat.Local : Cat.Remote, T("pane.log.renamed", it.Name, name));
                if (src == Source && PathEq(dir, CurrentPath)) Navigate(CurrentPath, false, name, new[] { name });
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        public void DeleteSelected(bool permanent)
        {
            var sel = Selected().Where(i => !i.IsContainerRoot).ToList();
            if (sel.Count == 0 || Source == null) return;
            var owner = FindForm();
            string list = string.Join("\n", sel.Take(12).Select(i => (i.IsDir ? "📁 " : "") + i.Name)) + (sel.Count > 12 ? "\n" + T("pane.andMore", sel.Count - 12) : "");
            if (Local)
            {
                string msg = permanent ? T("pane.delPermMsg") : T("pane.delRecycleMsg");
                if (!MessageDialog.Confirm(owner, TN("pane.delTitle", sel.Count), msg, permanent ? T("pane.deletePerm") : T("common.delete"), true, list)) return;
                var paths = sel.Select(i => i.Path).ToList();
                bool ok = permanent ? ShellOps.DeletePermanent(paths, owner.Handle) : ShellOps.Recycle(paths, owner.Handle, false, out _);
                Log.Info(Cat.Local, (permanent ? TN("pane.log.deletedPerm", sel.Count) : TN("pane.log.recycled", sel.Count)) + (ok ? "" : " " + T("pane.log.partial")));
                RefreshList();
                return;
            }
            if (!Perms.Delete) { MessageDialog.Warn(owner, T("common.delete"), T("pane.noDelete")); return; }
            var rf = (RemoteFs)Source;
            if (!MessageDialog.Confirm(owner, TN("pane.delRemoteTitle", sel.Count, rf.Peer.DisplayName),
                    T("pane.delRemoteMsg"), T("common.delete"), true, list)) return;
            Task.Run(() => rf.Delete(sel)).ContinueWith(t =>
            {
                if (IsDisposed) return;
                if (t.IsFaulted) MessageDialog.Error(owner, T("pane.deleteFailed"), t.Exception.GetBaseException().Message);
                else Log.Success(Cat.Remote, TN("pane.log.remoteDeleted", sel.Count, rf.Peer.DisplayName) + (t.Result != null && t.Result.B("recycled") ? " " + T("pane.log.theirRecycle") : ""));
                if (rf == Source) RefreshList();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        public void Properties()
        {
            if (!Local) return;
            var sel = Selected();
            string path = sel.Count > 0 ? sel[0].Path : CurrentPath;
            if (!string.IsNullOrEmpty(path)) ShellOps.ShowProperties(path, FindForm().Handle);
        }

        public void CopyPath()
        {
            var sel = Selected();
            var lines = sel.Count > 0 ? sel.Select(i => Local ? i.Path : PathUtil.Join(@"\\" + ((RemoteFs)Source).Peer.DisplayName, i.Path)) : new[] { Local ? CurrentPath : Source?.Display(CurrentPath) ?? "" };
            try { Clipboard.SetText(string.Join("\r\n", lines)); } catch { }
        }

        public void OpenInExplorer()
        {
            if (!Local) return;
            var sel = Selected();
            if (sel.Count > 0) ShellOps.RevealInExplorer(sel[0].Path);
            else if (CurrentPath.Length > 0) ShellOps.OpenFolder(CurrentPath);
        }

        // ─── 컨텍스트 메뉴 ─────────────────────────────────────────────────
        void ListMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            int row = List.RowAt(e.Location);
            if (row < 0) List.SelectedIndices.Clear();
            else if (!List.SelectedIndices.Contains(row)) List.SelectRow(row);
            ShowMenu(e.Location);
        }

        void ShowMenu(Point at)
        {
            if (Source == null) return;
            var m = new ContextMenuStrip();
            BuildMenu?.Invoke(this, m, Selected());
            if (m.Items.Count == 0) { m.Dispose(); return; }
            DarkRenderer.Polish(m);
            m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
            m.Show(List, at);
        }

        // ─── 끌어서 놓기 ───────────────────────────────────────────────────
        void ListItemDrag(object sender, ItemDragEventArgs e)
        {
            var sel = Selected().Where(i => i.Kind == FsKind.File || i.Kind == FsKind.Dir || (!Local && i.Kind == FsKind.Share)).ToList();
            if (sel.Count == 0) return;
            var data = new DataObject();
            data.SetData(DragFormat, this.GetHashCode().ToString());
            if (Local)
            {
                var sc = new StringCollection();
                sc.AddRange(sel.Select(i => i.Path).ToArray());
                data.SetFileDropList(sc);
            }
            DragItems = sel;
            try { List.DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move); }
            finally { DragItems = null; }
        }

        /// <summary>이 창에서 끌고 있는 항목 (같은 프로세스 안에서 전달).</summary>
        public static List<FsItem> DragItems;

        string DropDirAt(Point client, out int row)
        {
            row = List.RowAt(client);
            if (row >= 0 && row < List.Data.Count && List.Data[row].IsDir && (List.Data[row].Kind != FsKind.Share || !Local))
                return List.Data[row].Path;
            row = -1;
            return CurrentPath;
        }

        void ListDragOver(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.None;
            if (Source == null || Loading) { List.DropTarget = -1; return; }
            var pt = List.PointToClient(new Point(e.X, e.Y));
            string dir = DropDirAt(pt, out int row);
            bool fromPane = e.Data.GetDataPresent(DragFormat);
            bool fromSelf = fromPane && (string)e.Data.GetData(DragFormat) == GetHashCode().ToString();
            bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
            bool ok;
            if (fromSelf) ok = row >= 0 && Local && !(DragItems?.Any(i => PathEq(i.Path, dir)) ?? false);   // 같은 창: 하위 폴더로 로컬 이동/복사만
            else if (fromPane) ok = true;
            else ok = files;
            if (ok && dir.Length == 0) ok = false;                       // 루트(내 PC / 공유 목록) 에는 놓을 수 없음
            if (ok && !Local && !Perms.Write && row < 0) ok = false;
            List.DropTarget = ok ? row : -1;
            if (ok) e.Effect = DragDropEffects.Copy;
            // 가장자리에 가까우면 스크롤
            if (pt.Y < List.HeaderHeight() + Theme.S(16)) ScrollBy(-1);
            else if (pt.Y > List.ClientSize.Height - Theme.S(16)) ScrollBy(1);
        }

        void ScrollBy(int dir)
        {
            int top = List.TopItem?.Index ?? 0;
            int target = Math.Max(0, Math.Min(List.ItemCount - 1, top + dir));
            if (List.ItemCount > 0) List.EnsureVisible(dir < 0 ? target : Math.Min(List.ItemCount - 1, target + List.ClientSize.Height / Math.Max(1, List.RowHeight)));
        }

        void ListDragDrop(object sender, DragEventArgs e)
        {
            var pt = List.PointToClient(new Point(e.X, e.Y));
            string dir = DropDirAt(pt, out _);
            List.DropTarget = -1;
            if (dir.Length == 0) return;
            DropRequested?.Invoke(this, e.Data, dir);
        }

        // ─── 최근 위치 / 즐겨찾기 ──────────────────────────────────────────
        string StateKey(string kind) => kind + "." + (Local ? "local" : (Source as RemoteFs)?.Peer.Id ?? "none");

        List<string> Bookmarks() => AppCore.I.State.GetStrs(StateKey("bookmarks"));

        void RememberRecent(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var l = AppCore.I.State.GetStrs(StateKey("recent"));
            l.RemoveAll(x => PathEq(x, path));
            l.Insert(0, path);
            if (l.Count > 12) l.RemoveRange(12, l.Count - 12);
            AppCore.I.State.SetStrs(StateKey("recent"), l);
            AppCore.I.State.Set(Local ? "lastLocal" : "lastRemote." + ((RemoteFs)Source).Peer.Id, path);
        }

        void ToggleBookmark()
        {
            if (Source == null || CurrentPath.Length == 0) return;
            var l = Bookmarks();
            if (l.Any(b => PathEq(b, CurrentPath))) l.RemoveAll(b => PathEq(b, CurrentPath));
            else l.Add(CurrentPath);
            AppCore.I.State.SetStrs(StateKey("bookmarks"), l);
            UpdateNavButtons();
        }

        void ShowHistoryMenu()
        {
            if (Source == null) return;
            var m = new ContextMenuStrip();
            var bm = Bookmarks();
            m.Items.Add(new ToolStripLabel(T("pane.favorites")) { ForeColor = Theme.TextFaint, Font = Theme.SmallBold });
            if (bm.Count == 0) m.Items.Add(new ToolStripMenuItem(T("pane.noFavorites")) { Enabled = false });
            foreach (var b in bm)
            {
                string p = b;
                m.Items.Add(Source.Display(p), Icons.Glyph(G.StarFill, Theme.S(14), Theme.Warn), (s, e) => Navigate(p));
            }
            var recent = AppCore.I.State.GetStrs(StateKey("recent"));
            if (recent.Count > 0)
            {
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(new ToolStripLabel(T("pane.recent")) { ForeColor = Theme.TextFaint, Font = Theme.SmallBold });
                foreach (var r in recent)
                {
                    string p = r;
                    m.Items.Add(Source.Display(p), Icons.Glyph(G.Recent, Theme.S(14), Theme.TextDim), (s, e) => Navigate(p));
                }
            }
            if (Local)
            {
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(T("fs.desktop"), Icons.Glyph(G.Home, Theme.S(14), Theme.TextDim), (s, e) => Navigate(KnownFolders.Desktop));
                m.Items.Add(T("fs.downloads"), Icons.Glyph(G.Download, Theme.S(14), Theme.TextDim), (s, e) => Navigate(KnownFolders.Downloads));
                m.Items.Add(T("pane.thisPc"), Icons.Glyph(G.PC, Theme.S(14), Theme.TextDim), (s, e) => Navigate(""));
            }
            DarkRenderer.Polish(m);
            m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
            m.Show(_addr, new Point(0, _addr.Height + Theme.S(2)));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tips.Dispose();
            base.Dispose(disposing);
        }
    }
}
