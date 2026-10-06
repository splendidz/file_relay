using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using static FileRelay.L;
namespace FileRelay.UI
{
    public sealed partial class MainForm
    {
        FilePane ActivePane => _activePane ?? _local;

        // ─── 화면 갱신 ─────────────────────────────────────────────────────
        void UpdateAll()
        {
            UpdateStatusBar();
            UpdateInbox();
            UpdateStatusButton();
            UpdateUi();
        }

        void UpdateUi()
        {
            var rp = _remote.Source as RemoteFs;
            bool remoteReady = rp != null && rp.Peer.CanUse && _remote.CurrentPath.Length > 0;
            _tbDownload.Enabled = rp != null && _remote.Selected().Any(i => i.IsDir || i.Kind == FsKind.File);
            _tbUpload.Enabled = remoteReady && _local.Selected().Any(i => i.Kind == FsKind.File || i.Kind == FsKind.Dir || i.Kind == FsKind.Special) && _remote.Perms.Write;
            if (DateTime.Now > _msgUntil)
            {
                string sel = ActivePane.SelectionSummary();
                _stMsg.Text = sel;
                _stMsg.Tag = null;
            }
        }

        void Flash(string text, Color color)
        {
            _stMsg.Text = text;
            _stMsg.Tag = color;
            _msgUntil = DateTime.Now.AddSeconds(6);
            _status.Invalidate();
        }

        void UpdateStatusBar()
        {
            if (_c.Http.Running)
            {
                _stServer.Image = Icons.StatusDot(Presence.Online, Theme.S(14));
                _stServer.Text = T("sb.server", NetUtil.PrimaryIPv4(), _c.Config.Port);
                _stServer.ToolTipText = T("sb.copyAddressTip");
                _stServer.Tag = null;
            }
            else
            {
                _stServer.Image = Icons.Glyph(G.Error, Theme.S(14), Theme.Danger);
                _stServer.Text = T("sb.serverOff", _c.ServerError);
                _stServer.ToolTipText = T("sb.restartServerTip");
                _stServer.Tag = Theme.Danger;
            }
            var all = _c.Peers.All();
            int on = all.Count(p => p.IsOnline && p.State == PeerState.Paired && !p.Blocked);
            _stPeers.Text = T("sb.peersOnline", on, all.Length);
            _stPeers.Image = Icons.Glyph(G.People, Theme.S(14), Theme.TextDim);
            int lim = _c.Config.SpeedLimitKBps;
            _stLimit.Text = lim > 0 ? T("sb.limit", Fmt.Size(lim * 1024L) + "/s") : T("sb.noLimit");
            _stLimit.Image = Icons.Glyph(G.Clock, Theme.S(14), lim > 0 ? Theme.Warn : Theme.TextFaint);
        }

        void UpdateInbox()
        {
            int n = _c.Notes.UnreadCount;
            _tbInbox.Text = n > 0 ? T("main.notesUnread", n) : T("main.notes");
            _tbInbox.Image = Icons.Glyph(n > 0 ? '' : G.Mail, Theme.S(18), n > 0 ? Theme.Accent : Theme.Text);
        }

        void UpdateStatusButton()
        {
            _tbStatus.Text = Fmt.Presence(_c.MyStatus) + (_c.StatusIsAuto ? " " + T("tb.statusAuto") : "");
            _tbStatus.Image = Icons.StatusDot(_c.MyStatus, Theme.S(18));
        }

        void UpdateViewChecks()
        {
            _miHidden.Checked = _c.Config.ShowHiddenFiles;
            _miCompare.Checked = _compare;
            _tbCompare.Checked = _compare;
            _miPeerPanel.Checked = !_splitMain.Panel1Collapsed;
            _miBottom.Checked = !_splitV.Panel2Collapsed;
            _miPause.Checked = _c.Transfers.Paused;
        }

        void UiTick()
        {
            var st = _c.Transfers.Stats();
            if (st.Busy)
            {
                string t = TN("sb.progress", st.Active + st.Queued, Fmt.Size(st.DoneBytes), Fmt.Size(st.TotalBytes));
                if (_c.Transfers.Paused) t = T("sb.paused", t);
                else if (st.Speed > 0) t += $" · {Fmt.Speed(st.Speed)}" + (st.Eta >= 0 ? " · " + T("sb.eta", Fmt.Duration(st.Eta)) : "");
                _stProg.SetValue(st.Percent, true, t);
            }
            else
            {
                string t = st.Failed > 0 ? TN("sb.failed", st.Failed) : _c.Activity.ActiveCount > 0 ? T("sb.peersUsing", _c.Activity.ActiveCount) : T("sb.idle");
                _stProg.SetValue(0, false, t);
            }
            _tbPause.Checked = _c.Transfers.Paused;
            _tbPause.Text = _c.Transfers.Paused ? T("tb.resume") : T("menu.pause");
            _tbPause.Image = Icons.Glyph(_c.Transfers.Paused ? G.Play : G.Pause, Theme.S(18), _c.Transfers.Paused ? Theme.Warn : Theme.Text);
            _miPause.Checked = _c.Transfers.Paused;
            _miPause.Text = _c.Transfers.Paused ? T("menu.resume") : T("menu.pause");
            if (_stMsg.Tag is Color && DateTime.Now > _msgUntil) UpdateUi();
            Tray?.UpdateBadge();
            // 원격 창 머리글의 상대 상태를 최신으로.
            if (_remote.Source is RemoteFs) _remote.UpdateHeader();
        }

        // ─── 피어 ──────────────────────────────────────────────────────────
        void ConnectRemote(Peer p)
        {
            if (p == null) return;
            if (_remote.Source is RemoteFs rf && rf.Peer == p)
            {
                if (!string.IsNullOrEmpty(_remote.LastError) || (_remote.List.ItemCount == 0 && !_remote.Loading)) _remote.RefreshList();
                return;
            }
            string last = _c.State.GetStr("lastRemote." + p.Id, "");
            _remote.SetSource(new RemoteFs(p), last);
            _c.State.Set("lastPeer", p.Id);
            if (_compare) RebuildCompare();
            UpdateUi();
        }

        void OnPeersChanged()
        {
            UpdateStatusBar();
            if (_remote.Source is RemoteFs rf)
            {
                if (!_c.Peers.All().Contains(rf.Peer)) { _remote.SetSource(null, null); }
                else
                {
                    _remote.UpdateHeader();
                    // 오프라인이었다가 접속하면 자동으로 다시 읽는다.
                    if (rf.Peer.CanUse && !_remote.Loading && !string.IsNullOrEmpty(_remote.LastError)) _remote.RefreshList();
                }
            }
            UpdateUi();
        }

        void AddPeer()
        {
            using (var d = new AddPeerDialog())
            {
                var r = d.ShowDialog(this);
                if (r == DialogResult.Retry && d.OpenDiscover) { Discover(); return; }
                if (r == DialogResult.OK && d.Result != null)
                {
                    _peers.Select(d.Result);
                    ConnectRemote(d.Result);
                    Flash(T("mainact.peerAdded", d.Result.DisplayName), Theme.Success);
                }
            }
        }

        void Discover()
        {
            using (var d = new DiscoverDialog()) d.ShowDialog(this);
        }

        public void OpenSettings(int page = 0)
        {
            ShowMain();
            using (var d = new SettingsForm(page)) d.ShowDialog(this);
        }

        void ComposeTo(Peer p)
        {
            if (p != null && !p.CanUse) p = null;
            NoteManager.Compose(p);
        }

        void CopyMyAddress()
        {
            string a = NetUtil.PrimaryIPv4() + ":" + _c.Config.Port;
            try { Clipboard.SetText(a); Flash(T("mainact.addressCopied", a), Theme.Success); } catch { }
        }

        void OnPeerRequest(Peer p)
        {
            Tray?.Balloon(T("mainact.peerRequest.title"), T("mainact.peerRequest.balloon", p.DisplayName, p.Host), NotifyKind.Peer, () => OnPeerRequest(p));
            if (p.State != PeerState.Incoming || !_c.Peers.All().Contains(p)) return;
            ShowMain(false);
            var r = MessageDialog.Show(this, T("mainact.peerRequest.title"), T("mainact.peerRequest.text", p.DisplayName, p.Host, p.Machine),
                MsgKind.Question, null,
                new DlgBtn(T("mainact.accept"), DialogResult.Yes, ButtonKind.Primary), new DlgBtn(T("mainact.decline"), DialogResult.No), new DlgBtn(T("mainact.later"), DialogResult.Ignore));
            if (r == DialogResult.Yes) _c.PeerSvc.AcceptIncoming(p);
            else if (r == DialogResult.No) _c.PeerSvc.RejectIncoming(p);
        }

        void BuildPeerMenu(Peer p, ContextMenuStrip m)
        {
            if (p == null)
            {
                m.Items.Add(Item(T("menu.addPeer"), G.AddFriend, AddPeer, Keys.None, "Ctrl+N"));
                m.Items.Add(Item(T("mainact.discover"), G.Search, Discover));
                m.Items.Add(Item(T("mainact.refreshAll"), G.Refresh, () => _c.PeerSvc.BroadcastPresence()));
                return;
            }
            var open = Item(T("mainact.openShares"), G.Share, () => { ConnectRemote(p); _remote.FocusList(); }, Keys.None, "Enter");
            open.Font = Theme.UiBold;
            m.Items.Add(open);
            var note = Item(T("mainact.sendNote"), G.Note, () => NoteManager.Compose(p), Keys.None, "Ctrl+M");
            note.Enabled = p.CanUse;
            m.Items.Add(note);
            if (p.Unread > 0) m.Items.Add(Item(T("mainact.viewNotes", p.Unread), G.Mail, () => NotesForm.ShowInbox(_c.Notes.All().Where(n => n.Incoming && n.PeerId == p.Id).OrderByDescending(n => n.Time).FirstOrDefault())));
            m.Items.Add(new ToolStripSeparator());
            if (p.State == PeerState.Incoming)
            {
                m.Items.Add(Item(T("mainact.acceptRequest"), G.Accept, () => _c.PeerSvc.AcceptIncoming(p)));
                m.Items.Add(Item(T("mainact.declineRequest"), G.Cancel, () => _c.PeerSvc.RejectIncoming(p)));
                m.Items.Add(new ToolStripSeparator());
            }
            if (p.State == PeerState.Pending || p.State == PeerState.Unpaired)
                m.Items.Add(Item(T("mainact.reconnect"), G.Sync, () => _c.PeerSvc.Reconnect(p)));
            m.Items.Add(Item(T("mainact.refreshStatus"), G.Refresh, () => _c.PeerSvc.PingAsync(p)));
            m.Items.Add(Item(T("mainact.alias"), G.Rename, () =>
            {
                var v = InputDialog.Ask(this, T("mainact.aliasTitle"), T("mainact.aliasPrompt", p.Name), p.Alias);
                if (v == null) return;
                p.Alias = v;
                _c.Peers.Touch(true);
                _remote.UpdateHeader();
            }));
            m.Items.Add(Item(T("mainact.changeAddress"), G.Edit, () =>
            {
                var v = InputDialog.Ask(this, T("mainact.changeAddressTitle"), T("mainact.addressPrompt"), p.Address, false,
                    x => NetUtil.TryParseAddress(x, p.Port, out _, out _) ? null : T("mainact.addressInvalid"));
                if (v == null || !NetUtil.TryParseAddress(v, p.Port, out var h, out var port)) return;
                p.Host = h;
                p.Port = port;
                _c.Peers.Touch(true);
                _c.PeerSvc.PingAsync(p);
            }));
            m.Items.Add(Item(T("mainact.copyAddress"), G.Copy, () => { try { Clipboard.SetText(p.Address); } catch { } }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item(p.Blocked ? T("mainact.unblock") : T("mainact.block"), G.Block, () =>
            {
                if (!p.Blocked && !MessageDialog.Confirm(this, T("mainact.blockTitle", p.DisplayName), T("mainact.blockText"), T("mainact.block"), true)) return;
                _c.PeerSvc.SetBlocked(p, !p.Blocked);
            }));
            var del = Item(T("mainact.removePeer"), G.Delete, () =>
            {
                if (!MessageDialog.Confirm(this, T("mainact.removeTitle", p.DisplayName), T("mainact.removeText"), T("mainact.remove"), true)) return;
                if (_remote.Source is RemoteFs rf && rf.Peer == p) _remote.SetSource(null, null);
                _c.PeerSvc.RemovePeer(p);
            });
            del.Tag = Theme.Danger;
            m.Items.Add(del);
            m.Items.Add(new ToolStripSeparator());
            string info = $"{p.Address}" + (string.IsNullOrEmpty(p.Machine) ? "" : " · " + p.Machine) + (string.IsNullOrEmpty(p.Version) ? "" : " · v" + p.Version);
            m.Items.Add(new ToolStripMenuItem(info) { Enabled = false });
            if (!string.IsNullOrEmpty(p.LastError) && !p.IsOnline) m.Items.Add(new ToolStripMenuItem(Fmt.Truncate(p.LastError, 60)) { Enabled = false });
        }

        void BuildMeMenu(ContextMenuStrip m)
        {
            m.Items.Add(new ToolStripMenuItem(_c.Config.UserName + "  ·  " + NetUtil.PrimaryIPv4() + ":" + _c.Config.Port) { Enabled = false });
            m.Items.Add(new ToolStripSeparator());
            foreach (var p in new[] { Presence.Online, Presence.Away, Presence.Busy })
            {
                var pp = p;
                string desc = p == Presence.Busy ? "  — " + T("mainact.busyDesc") : "";
                m.Items.Add(new ToolStripMenuItem(Fmt.Presence(p) + desc, Icons.StatusDot(p, Theme.S(16)), (s, e) => _c.SetStatus(pp, false)) { Checked = _c.MyStatus == p });
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item(T("mainact.rename"), G.Rename, () =>
            {
                var v = InputDialog.Ask(this, T("set.gen.name"), T("mainact.namePrompt"), _c.Config.UserName, false, x => string.IsNullOrWhiteSpace(x) ? T("name.empty") : null);
                if (v == null) return;
                var cfg = _c.Config.Clone();
                cfg.UserName = v.Trim();
                _c.ApplyConfig(cfg, true);
            }));
            m.Items.Add(Item(T("menu.copyMyAddress"), G.Copy, CopyMyAddress));
            m.Items.Add(Item(T("menu.settings"), G.Settings, () => OpenSettings()));
        }

        void OnStatusChanged()
        {
            // 방해 금지를 풀면 쌓인 쪽지를 띄운다.
            if (_c.MyStatus != Presence.Busy && _c.Config.NotePopup)
                foreach (var n in _c.Notes.All().Where(n => n.Incoming && !n.IsRead).OrderBy(n => n.Time).Take(8)) NoteManager.ShowIncoming(n);
        }

        void OnNoteArrived(Note n)
        {
            var cfg = _c.Config;
            UpdateInbox();
            Tray?.UpdateBadge();
            bool busy = _c.MyStatus == Presence.Busy;
            if (cfg.NotePopup && !busy)
            {
                NoteManager.ShowIncoming(n);
                if (cfg.NoteSound) NoteManager.PlayNoteSound();
            }
            else
            {
                Tray?.Balloon(busy ? T("mainact.noteFromBusy", n.PeerName) : T("mainact.noteFrom", n.PeerName), Fmt.OneLine(n.Text, 200), NotifyKind.Note, () => NoteManager.ShowIncoming(n, true));
            }
        }

        void OnConfigChanged()
        {
            if (_local.Source is LocalFs lf && lf.ShowHidden != _c.Config.ShowHiddenFiles)
            {
                lf.ShowHidden = _c.Config.ShowHiddenFiles;
                _local.RefreshList();
            }
            _local.UpdateHeader();
            _remote.UpdateHeader();
            UpdateStatusBar();
            UpdateViewChecks();
            _peers.Invalidate(true);
        }

        void CheckFirewallAsync()
        {
            int fwPort = _c.Config.Port;
            Task.Run(() => Firewall.Check(fwPort)).ContinueWith(t =>
            {
                if (IsDisposed || t.IsFaulted) return;
                var st = t.Result;
                _stFirewall.Visible = st == Firewall.State.Blocked || st == Firewall.State.NoRule;
                if (st == Firewall.State.Blocked) Log.Warn(Cat.Sys, T("mainact.log.fwBlocked"));
                else if (st == Firewall.State.NoRule) Log.Warn(Cat.Sys, T("mainact.log.fwNoRule"));
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ─── 보기 ──────────────────────────────────────────────────────────
        void ToggleHidden()
        {
            var cfg = _c.Config.Clone();
            cfg.ShowHiddenFiles = !cfg.ShowHiddenFiles;
            _c.ApplyConfig(cfg, true);
        }

        void ToggleCompare()
        {
            _compare = !_compare;
            RebuildCompare();
            UpdateViewChecks();
            Flash(_compare ? T("mainact.compareOn") : T("mainact.compareOff"), Theme.Info);
        }

        void RebuildCompare()
        {
            _cmpLocal = new Dictionary<string, FsItem>(StringComparer.OrdinalIgnoreCase);
            _cmpRemote = new Dictionary<string, FsItem>(StringComparer.OrdinalIgnoreCase);
            bool on = _compare && _remote.Source != null && _local.CurrentPath.Length > 0 && _remote.CurrentPath.Length > 0;
            if (on)
            {
                foreach (var i in _local.AllItems) _cmpLocal[i.Name] = i;
                foreach (var i in _remote.AllItems) _cmpRemote[i.Name] = i;
            }
            _local.List.Invalidate();
            _remote.List.Invalidate();
        }

        CompareMark Compare(FsItem it, Dictionary<string, FsItem> other)
        {
            if (!_compare || _cmpLocal.Count + _cmpRemote.Count == 0) return CompareMark.None;
            if (!other.TryGetValue(it.Name, out var o)) return CompareMark.OnlyHere;
            if (it.IsDir || o.IsDir) return CompareMark.None;
            if (it.Size != o.Size) return CompareMark.DiffSize;
            if (it.MTime > o.MTime.AddSeconds(2)) return CompareMark.NewerHere;
            return CompareMark.None;
        }

        void RefreshAll()
        {
            _local.RefreshList();
            if (_remote.Source != null) _remote.RefreshList();
            _c.PeerSvc.BroadcastPresence();
        }

        void ShowBottom(int tab)
        {
            _splitV.Panel2Collapsed = false;
            _bottom.ShowTab(tab);
            UpdateViewChecks();
        }

        void TogglePause()
        {
            _c.Transfers.SetPaused(!_c.Transfers.Paused);
            UiTick();
        }

        void CancelAll()
        {
            var st = _c.Transfers.Stats();
            if (!st.Busy) return;
            if (!MessageDialog.Confirm(this, T("mainact.cancelAll.title"), TN("mainact.cancelAll.text", st.Active + st.Queued), T("menu.cancelAll"), true)) return;
            _c.Transfers.CancelAll();
        }

        // ─── 전송 ──────────────────────────────────────────────────────────
        void TransferFrom(FilePane src)
        {
            if (src.Local)
            {
                var items = src.Selected().Where(i => i.Kind == FsKind.File || i.Kind == FsKind.Dir || i.Kind == FsKind.Special).ToList();
                if (items.Count == 0) return;
                Upload(items.Select(i => i.Path).ToList(), _remote, _remote.CurrentPath);
            }
            else
            {
                var items = src.Selected().Where(i => i.Kind == FsKind.File || i.Kind == FsKind.Dir || i.Kind == FsKind.Share).ToList();
                if (items.Count == 0 || !(src.Source is RemoteFs rf)) return;
                string dir = _local.CurrentPath.Length > 0 ? _local.CurrentPath : _c.Config.EffectiveDownloadFolder;
                Download(rf.Peer, items, dir);
            }
        }

        void DownloadTo()
        {
            if (!(_remote.Source is RemoteFs rf)) return;
            var items = _remote.Selected().Where(i => i.Kind == FsKind.File || i.Kind == FsKind.Dir || i.Kind == FsKind.Share).ToList();
            if (items.Count == 0) { MessageDialog.Info(this, T("mainact.downloadToTitle"), T("mainact.selectRemoteFirst")); return; }
            var dir = FolderPicker.Pick(this, T("mainact.pickFolder"), _local.CurrentPath.Length > 0 ? _local.CurrentPath : _c.Config.EffectiveDownloadFolder);
            if (dir != null) Download(rf.Peer, items, dir);
        }

        bool CheckPeer(Peer p)
        {
            if (p.CanUse) return true;
            string why = p.Blocked ? T("peer.isBlocked") : p.State != PeerState.Paired ? FilePane.PeerStatusText(p) : T("mainact.offline");
            MessageDialog.Warn(this, T("mainact.cantTransfer", p.DisplayName), why);
            return false;
        }

        void Upload(List<string> localPaths, FilePane remotePane, string remoteDir)
        {
            if (!(remotePane.Source is RemoteFs rf))
            {
                MessageDialog.Info(this, T("tb.upload"), T("mainact.selectPeerFirst"));
                return;
            }
            var peer = rf.Peer;
            if (!CheckPeer(peer)) return;
            if (string.IsNullOrEmpty(remoteDir))
            {
                MessageDialog.Info(this, T("tb.upload"), T("mainact.openRemoteShareFirst"));
                return;
            }
            if (PathEqRemote(remoteDir, remotePane.CurrentPath) && !remotePane.Perms.Write)
            {
                MessageDialog.Warn(this, T("mainact.noWrite.title"), T("mainact.noWrite.text", peer.DisplayName));
                return;
            }
            localPaths = localPaths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (localPaths.Count == 0) return;
            BuildAndQueue(false, peer, remoteDir, ct => JobBuilder.Uploads(peer, localPaths, remoteDir, s => SetBusy(s), ct),
                localPaths.Select(p => Path.GetFileName(p.TrimEnd('\\')) + (Directory.Exists(p) ? "\\" : "")).ToList(),
                T("mainact.peerPc", peer.DisplayName), remoteDir, () => { if (remotePane.Source == rf && PathEqRemote(remotePane.CurrentPath, remoteDir)) remotePane.RefreshList(); });
        }

        static bool PathEqRemote(string a, string b) => string.Equals(PathUtil.Normalize(a), PathUtil.Normalize(b), StringComparison.OrdinalIgnoreCase);

        void Download(Peer peer, List<FsItem> items, string localDir)
        {
            if (!CheckPeer(peer)) return;
            if (string.IsNullOrEmpty(localDir) || !Directory.Exists(localDir))
            {
                MessageDialog.Info(this, T("tb.download"), T("mainact.openLocalFolderFirst"));
                return;
            }
            BuildAndQueue(true, peer, localDir, ct => JobBuilder.Downloads(peer, items, localDir, s => SetBusy(s), ct),
                items.Select(i => i.Name + (i.IsDir ? "\\" : "")).ToList(), T("pane.thisPc"), localDir,
                () => { if (PathUtil.SameLocalPath(_local.CurrentPath, localDir)) _local.RefreshList(); });
        }

        void SetBusy(string text)
        {
            try { BeginInvoke(new Action(() => Flash(text, Theme.Info))); } catch { }
        }

        void BuildAndQueue(bool down, Peer peer, string dstDir, Func<CancellationToken, List<TransferItem>> build, List<string> names, string dstLabel, string dstPath, Action afterDirs)
        {
            string what = down ? T("tb.download") : T("tb.upload");
            Flash(down ? T("mainact.prepDown") : T("mainact.prepUp"), Theme.Info);
            var cts = new CancellationTokenSource();
            Task.Run(() => build(cts.Token)).ContinueWith(t =>
            {
                if (IsDisposed) return;
                if (t.IsFaulted)
                {
                    Flash("", Theme.TextDim);
                    MessageDialog.Error(this, down ? T("mainact.prepFailedDown") : T("mainact.prepFailedUp"), t.Exception.GetBaseException().Message);
                    return;
                }
                var jobs = t.Result;
                afterDirs?.Invoke();
                if (jobs.Count == 0)
                {
                    Flash(T("mainact.noFiles"), Theme.TextDim);
                    return;
                }
                long total = jobs.Sum(j => j.Size);
                if (_c.Config.ConfirmBeforeTransfer)
                {
                    string dst = string.IsNullOrEmpty(dstPath) ? "-" : dstPath;
                    string list = (down ? T("mainact.confirm.dstDown", dst) : T("mainact.confirm.dstUp", dst)) + "\n\n" +
                                  string.Join("\n", names.Take(15)) + (names.Count > 15 ? "\n" + T("mainact.confirm.more", names.Count - 15) : "");
                    string msg = dstLabel + "  ·  " + TN("mainact.confirm.files", jobs.Count) + "  ·  " + Fmt.Size(total);
                    string q = down ? TN("mainact.confirm.titleDown", names.Count) : TN("mainact.confirm.titleUp", names.Count);
                    bool dontAsk;
                    var r = MessageDialog.Show(this, q, msg, MsgKind.Question, list, T("mainact.dontAsk"), out dontAsk,
                        new DlgBtn(what, DialogResult.OK, ButtonKind.Primary), new DlgBtn(T("common.cancel"), DialogResult.Cancel));
                    if (r != DialogResult.OK) { Flash(down ? T("mainact.canceledDown") : T("mainact.canceledUp"), Theme.TextDim); return; }
                    if (dontAsk)
                    {
                        var cfg = _c.Config.Clone();
                        cfg.ConfirmBeforeTransfer = false;
                        _c.ApplyConfig(cfg, true);
                    }
                }
                _c.Transfers.Enqueue(jobs);
                Log.Info(Cat.Xfer, TN("mainact.log.queued", jobs.Count, what, Fmt.Size(total), peer.DisplayName));
                Flash(TN("mainact.queued", jobs.Count, what, Fmt.Size(total)), Theme.Success);
                if (!_splitV.Panel2Collapsed && _bottom.Tabs.Selected > BottomPanel.TabQueue) _bottom.ShowTab(BottomPanel.TabQueue);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Paste(FilePane pane, string targetDir)
        {
            if (pane.Source == null) return;
            if (AppClipboard.HasItems && AppClipboard.Pane != null && AppClipboard.Pane.Local != pane.Local)
            {
                if (AppClipboard.Pane.Local) Upload(AppClipboard.Items.Select(i => i.Path).ToList(), pane, targetDir);
                else if (AppClipboard.Source is RemoteFs rf) Download(rf.Peer, AppClipboard.Items.ToList(), targetDir);
                return;
            }
            System.Collections.Specialized.StringCollection files = null;
            try { if (Clipboard.ContainsFileDropList()) files = Clipboard.GetFileDropList(); } catch { }
            if (files != null && files.Count > 0)
            {
                var list = files.Cast<string>().ToList();
                if (pane.Local)
                {
                    if (string.IsNullOrEmpty(targetDir)) return;
                    ShellOps.CopyOrMove(list, targetDir, false, Handle);
                    pane.RefreshList();
                }
                else Upload(list, pane, targetDir);
                return;
            }
            if (AppClipboard.HasItems && !pane.Local)
                MessageDialog.Info(this, T("menu.paste"), T("mainact.remoteToRemote"));
        }

        void Drop(FilePane pane, IDataObject data, string targetDir)
        {
            if (data.GetDataPresent(FilePane.DragFormat) && FilePane.DragItems != null)
            {
                var items = FilePane.DragItems.ToList();
                var srcPane = pane == _local ? _remote : _local;
                bool fromSelf = (string)data.GetData(FilePane.DragFormat) == pane.GetHashCode().ToString();
                if (fromSelf)
                {
                    if (pane.Local)
                    {
                        ShellOps.CopyOrMove(items.Select(i => i.Path), targetDir, true, Handle);
                        pane.RefreshList();
                    }
                    return;
                }
                if (srcPane.Local && !pane.Local) Upload(items.Select(i => i.Path).ToList(), pane, targetDir);
                else if (!srcPane.Local && pane.Local && srcPane.Source is RemoteFs rf) Download(rf.Peer, items, targetDir);
                return;
            }
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                if (pane.Local)
                {
                    ShellOps.CopyOrMove(files, targetDir, false, Handle);
                    pane.RefreshList();
                }
                else
                {
                    Activate();
                    Upload(files.ToList(), pane, targetDir);
                }
            }
        }

        void OpenFile(FilePane pane, FsItem it)
        {
            if (pane.Local)
            {
                try { ShellOps.Open(it.Path); }
                catch (Exception ex) { MessageDialog.Error(this, T("mainact.cantOpen"), ex.Message, it.Path); }
                return;
            }
            if (!(pane.Source is RemoteFs rf) || !CheckPeer(rf.Peer)) return;
            if (it.Size > 300L * 1024 * 1024 &&
                !MessageDialog.Confirm(this, T("mainact.bigFile.title"), T("mainact.bigFile.text", it.Name, Fmt.Size(it.Size)), T("mainact.bigFile.ok"))) return;
            JobBuilder.SplitRemote(it.Path, out string share, out string rel);
            string safePeer = new string(rf.Peer.DisplayName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray());
            string dir = Path.Combine(_c.Config.EffectiveTempFolder, safePeer, share);
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { MessageDialog.Error(this, T("mainact.cantCreateTemp"), ex.Message, dir); return; }
            var job = new TransferItem
            {
                Dir = TDir.Download,
                PeerId = rf.Peer.Id,
                PeerName = rf.Peer.DisplayName,
                Share = share,
                RemotePath = rel,
                LocalPath = Path.Combine(dir, it.Name),
                Size = it.Size,
                SrcTime = it.MTime,
                OpenAfter = true,
                ForceOverwrite = true,
            };
            _edits.Forget(job.LocalPath);
            _c.Transfers.Enqueue(new[] { job });
            Flash(T("mainact.opening", it.Name), Theme.Info);
        }

        void OnItemFinished(TransferItem it)
        {
            if (it.Status != TStatus.Done) return;
            if (it.Dir == TDir.Download)
            {
                if (it.OpenAfter)
                {
                    try { ShellOps.Open(it.LocalPath); }
                    catch (Exception ex) { MessageDialog.Error(this, T("mainact.cantOpen"), ex.Message, it.LocalPath); }
                    var p = _c.Peers.FindById(it.PeerId);
                    if (p != null) _edits.Watch(it.LocalPath, p, it.Share, it.RemotePath);
                    return;
                }
                if (PathUtil.SameLocalPath(Path.GetDirectoryName(it.LocalPath), _local.CurrentPath)) RefreshSoon(_local);
            }
            else if (_remote.Source is RemoteFs rf && rf.Peer.Id == it.PeerId &&
                     PathEqRemote(PathUtil.Join(it.Share, PathUtil.Parent(it.RemotePath)), _remote.CurrentPath))
                RefreshSoon(_remote);
            if (_compare) RebuildCompare();
        }

        readonly HashSet<FilePane> _refreshPending = new HashSet<FilePane>();

        /// <summary>파일이 하나씩 끝날 때마다 새로 고치지 않도록 0.8초 모아서 한 번.</summary>
        void RefreshSoon(FilePane pane)
        {
            if (!_refreshPending.Add(pane)) return;
            var t = new System.Windows.Forms.Timer { Interval = 800 };
            t.Tick += (s, e) => { t.Dispose(); _refreshPending.Remove(pane); if (!pane.Loading) pane.RefreshList(); };
            t.Start();
        }

        void OnBatchFinished(int done, int failed)
        {
            if (!_c.Config.NotifyTransferComplete) return;
            if (done + failed <= 0) return;
            if (ContainsFocus && Visible && WindowState != FormWindowState.Minimized)
            {
                Flash(failed == 0 ? TN("mainact.batchDone", done) : T("mainact.batchPartial", done, failed), failed == 0 ? Theme.Success : Theme.Warn);
                return;
            }
            Tray?.Balloon(failed == 0 ? T("mainact.batchDoneTitle") : T("mainact.batchPartialTitle"), failed == 0 ? TN("mainact.batchDoneText", done) : T("mainact.batchCounts", done, failed),
                failed == 0 ? NotifyKind.Success : NotifyKind.Warning, () => { ShowMain(); ShowBottom(failed > 0 ? BottomPanel.TabFailed : BottomPanel.TabDone); });
        }

        void OpenTransferResult(TransferItem it)
        {
            if (it.Status == TStatus.Done && it.Dir == TDir.Download && File.Exists(it.LocalPath))
            {
                try { ShellOps.Open(it.LocalPath); } catch { }
            }
            else if (it.Status == TStatus.Failed) MessageDialog.Error(this, T("mainact.xferFailed"), it.Error, it.LocalPath + "\n" + it.RemoteDisplay);
        }

        // ─── 컨텍스트 메뉴 ─────────────────────────────────────────────────
        void BuildPaneMenu(FilePane pane, ContextMenuStrip m, List<FsItem> sel)
        {
            var rf = _remote.Source as RemoteFs;
            bool hasSel = sel.Count > 0;
            bool canOpen = sel.Count == 1;
            if (pane.Local)
            {
                if (hasSel)
                {
                    var open = Item(T("common.open"), G.OpenFile, pane.ActivateSelected, Keys.None, "Enter");
                    open.Font = Theme.UiBold;
                    open.Enabled = canOpen;
                    m.Items.Add(open);
                    if (canOpen && sel[0].Kind == FsKind.File) m.Items.Add(Item(T("mainact.openWith"), G.OpenFile, () => { try { ShellOps.OpenWith(sel[0].Path); } catch { } }));
                    var up = Item(rf != null ? T("mainact.uploadTo", rf.Peer.DisplayName) : T("mainact.uploadNoPeer"), G.Upload, () => TransferFrom(_local), Keys.None, "Ctrl+T");
                    up.Enabled = rf != null && rf.Peer.CanUse && _remote.CurrentPath.Length > 0 && sel.Any(i => !i.IsContainerRoot || i.Kind == FsKind.Special);
                    m.Items.Add(up);
                    m.Items.Add(new ToolStripSeparator());
                    m.Items.Add(Item(T("common.copy"), G.Copy, pane.CopySelected, Keys.None, "Ctrl+C"));
                }
                var paste = Item(T("menu.paste"), G.Paste, () => Paste(pane, pane.CurrentPath), Keys.None, "Ctrl+V");
                paste.Enabled = pane.CurrentPath.Length > 0;
                m.Items.Add(paste);
                m.Items.Add(new ToolStripSeparator());
                var nf = Item(T("common.newFolder"), G.NewFolder, pane.NewFolder, Keys.None, "Ctrl+Shift+N");
                nf.Enabled = pane.CurrentPath.Length > 0;
                m.Items.Add(nf);
                if (hasSel)
                {
                    var rn = Item(T("common.rename"), G.Rename, pane.RenameSelected, Keys.None, "F2");
                    rn.Enabled = canOpen && !sel[0].IsContainerRoot;
                    m.Items.Add(rn);
                    var del = Item(T("mainact.deleteRecycle"), G.Delete, () => pane.DeleteSelected(false), Keys.None, "Del");
                    del.Enabled = sel.Any(i => !i.IsContainerRoot);
                    m.Items.Add(del);
                }
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(Item(T("menu.copyPath"), G.Link, pane.CopyPath));
                m.Items.Add(Item(hasSel ? T("mainact.showInExplorer") : T("mainact.openInExplorer"), G.Explorer, pane.OpenInExplorer));
                m.Items.Add(Item(T("mainact.properties"), G.Info, pane.Properties, Keys.None, "Alt+Enter"));
                m.Items.Add(Item(T("common.refresh"), G.Refresh, pane.RefreshList, Keys.None, "F5"));
                return;
            }
            // 원격
            if (rf == null) return;
            var perms = pane.Perms;
            if (hasSel)
            {
                var open = Item(T("common.open"), G.OpenFile, pane.ActivateSelected, Keys.None, "Enter");
                open.Font = Theme.UiBold;
                open.Enabled = canOpen;
                m.Items.Add(open);
                string ldir = _local.CurrentPath.Length > 0 ? _local.CurrentPath : _c.Config.EffectiveDownloadFolder;
                m.Items.Add(Item(T("mainact.downloadTo", Fmt.Truncate(ldir, 40)), G.Download, () => TransferFrom(_remote), Keys.None, "Ctrl+T"));
                m.Items.Add(Item(T("menu.downloadTo"), G.OpenFolder, DownloadTo));
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(Item(T("common.copy"), G.Copy, pane.CopySelected, Keys.None, "Ctrl+C"));
            }
            var paste2 = Item(T("mainact.pasteUpload"), G.Paste, () => Paste(pane, pane.CurrentPath), Keys.None, "Ctrl+V");
            paste2.Enabled = pane.CurrentPath.Length > 0 && perms.Write;
            m.Items.Add(paste2);
            m.Items.Add(new ToolStripSeparator());
            var nf2 = Item(T("common.newFolder"), G.NewFolder, pane.NewFolder, Keys.None, "Ctrl+Shift+N");
            nf2.Enabled = pane.CurrentPath.Length > 0 && perms.Write;
            m.Items.Add(nf2);
            if (hasSel)
            {
                var rn = Item(T("common.rename"), G.Rename, pane.RenameSelected, Keys.None, "F2");
                rn.Enabled = canOpen && perms.Rename && !sel[0].IsContainerRoot;
                m.Items.Add(rn);
                var del = Item(T("common.delete"), G.Delete, () => pane.DeleteSelected(false), Keys.None, "Del");
                del.Enabled = perms.Delete && sel.Any(i => !i.IsContainerRoot);
                m.Items.Add(del);
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item(T("menu.copyPath"), G.Link, pane.CopyPath));
            m.Items.Add(Item(T("common.refresh"), G.Refresh, pane.RefreshList, Keys.None, "F5"));
            if (pane.CurrentPath.Length > 0)
            {
                var a = perms.Delete ? AccessLevel.Full : perms.Write ? AccessLevel.Write : AccessLevel.Read;
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(new ToolStripMenuItem(T("mainact.myAccess", Fmt.AccessLong(a))) { Enabled = false });
            }
        }

        void BuildTransferMenu(ContextMenuStrip m, List<TransferItem> sel)
        {
            if (sel.Count > 0)
            {
                var one = sel.Count == 1 ? sel[0] : null;
                if (one != null && one.Dir == TDir.Download && one.Status == TStatus.Done)
                {
                    m.Items.Add(Item(T("common.open"), G.OpenFile, () => OpenTransferResult(one)));
                    m.Items.Add(Item(T("mainact.showInFolder"), G.Explorer, () => ShellOps.RevealInExplorer(one.LocalPath)));
                    m.Items.Add(new ToolStripSeparator());
                }
                else if (one != null && one.Dir == TDir.Upload)
                {
                    m.Items.Add(Item(T("mainact.showSource"), G.Explorer, () => ShellOps.RevealInExplorer(one.LocalPath)));
                    m.Items.Add(new ToolStripSeparator());
                }
                var active = sel.Where(i => !i.IsFinished).ToList();
                var retry = sel.Where(i => i.Status == TStatus.Failed || i.Status == TStatus.Canceled).ToList();
                var queued = sel.Where(i => i.Status == TStatus.Queued).ToList();
                if (active.Count > 0) m.Items.Add(Item(T("mainact.cancelN", active.Count), G.Cancel, () => _c.Transfers.Cancel(active)));
                if (queued.Count > 0) m.Items.Add(Item(T("mainact.moveToTop"), G.Up, () => _c.Transfers.Prioritize(queued)));
                if (retry.Count > 0) m.Items.Add(Item(T("mainact.retryN", retry.Count), G.Retry, () => _c.Transfers.Retry(retry)));
                if (one != null && one.Status == TStatus.Failed) m.Items.Add(Item(T("mainact.copyError"), G.Copy, () => { try { Clipboard.SetText(one.Error); } catch { } }));
                var fin = sel.Where(i => i.IsFinished).ToList();
                if (fin.Count > 0) m.Items.Add(Item(T("mainact.removeFromList"), G.Remove, () => _c.Transfers.Remove(i => fin.Contains(i)), Keys.None, "Del"));
                m.Items.Add(new ToolStripSeparator());
            }
            var pause = Item(_c.Transfers.Paused ? T("menu.resume") : T("menu.pause"), _c.Transfers.Paused ? G.Play : G.Pause, TogglePause, Keys.None, "Ctrl+P");
            m.Items.Add(pause);
            m.Items.Add(Item(T("menu.cancelAll"), G.Stop, CancelAll));
            m.Items.Add(Item(T("menu.clearDone"), G.Clear, () => _c.Transfers.Remove(i => i.Status == TStatus.Done || i.Status == TStatus.Skipped)));
            m.Items.Add(Item(T("mainact.retryAllFailed"), G.Retry, () => _c.Transfers.Retry(_c.Transfers.Snapshot().Where(i => i.Status == TStatus.Failed).ToList())));
        }

        /// <summary>
        /// 원격 파일을 '열기'로 받아 편집했을 때, 저장하면 다시 올릴지 묻는다 (FileZilla 의 '보기/편집').
        /// </summary>
        sealed class RemoteEditWatcher : IDisposable
        {
            sealed class Entry
            {
                public string Local, PeerId, Share, Remote;
                public DateTime Stamp;
                public FileSystemWatcher W;
                public bool Asking;
            }

            readonly MainForm _f;
            readonly Dictionary<string, Entry> _map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

            public RemoteEditWatcher(MainForm f) { _f = f; }

            public void Forget(string local)
            {
                if (_map.TryGetValue(local, out var e)) { e.W.Dispose(); _map.Remove(local); }
            }

            public void Watch(string local, Peer p, string share, string remote)
            {
                Forget(local);
                try
                {
                    var e = new Entry { Local = local, PeerId = p.Id, Share = share, Remote = remote, Stamp = File.GetLastWriteTimeUtc(local) };
                    e.W = new FileSystemWatcher(Path.GetDirectoryName(local), Path.GetFileName(local)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size, EnableRaisingEvents = true };
                    e.W.Changed += (s, a) => { try { _f.BeginInvoke(new Action(() => Check(e))); } catch { } };
                    _map[local] = e;
                }
                catch { }
            }

            void Check(Entry e)
            {
                if (e.Asking || !_map.ContainsKey(e.Local)) return;
                DateTime t;
                try { t = File.GetLastWriteTimeUtc(e.Local); } catch { return; }
                if (t <= e.Stamp.AddSeconds(1)) return;
                // 편집기가 아직 쓰는 중일 수 있어 잠깐 기다린다.
                e.Asking = true;
                var timer = new System.Windows.Forms.Timer { Interval = 900 };
                timer.Tick += (s, a) =>
                {
                    timer.Dispose();
                    e.Stamp = File.GetLastWriteTimeUtc(e.Local);
                    var p = AppCore.I.Peers.FindById(e.PeerId);
                    if (p == null) { e.Asking = false; return; }
                    _f.ShowMain(false);
                    bool ok = MessageDialog.Confirm(_f, T("mainact.reupload.title"),
                        T("mainact.reupload.text", Path.GetFileName(e.Local), p.DisplayName, PathUtil.Join(e.Share, e.Remote)), T("mainact.reupload.ok"));
                    e.Asking = false;
                    if (!ok) return;
                    AppCore.I.Transfers.Enqueue(new[]
                    {
                        new TransferItem
                        {
                            Dir = TDir.Upload, PeerId = p.Id, PeerName = p.DisplayName, LocalPath = e.Local,
                            Share = e.Share, RemotePath = e.Remote, ForceOverwrite = true,
                        },
                    });
                };
                timer.Start();
            }

            public void Dispose()
            {
                foreach (var e in _map.Values) e.W.Dispose();
                _map.Clear();
            }
        }
    }
}
