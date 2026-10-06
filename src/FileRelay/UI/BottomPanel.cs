using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

using static FileRelay.L;
namespace FileRelay.UI
{
    /// <summary>전송 목록 (진행 막대 포함).</summary>
    public sealed class TransferListView : DarkListView
    {
        public List<TransferItem> Rows = new List<TransferItem>();

        public TransferListView()
        {
            Columns.Add(T("common.file"), Theme.S(240));
            Columns.Add(T("bottom.col.dir"), Theme.S(En ? 92 : 70));     // 영어 "Download" 가 들어가도록
            Columns.Add(T("bottom.col.peer"), Theme.S(100));
            Columns.Add(T("common.size"), Theme.S(80), HorizontalAlignment.Right);
            Columns.Add(T("bottom.col.progress"), Theme.S(170));
            Columns.Add(T("bottom.col.speed"), Theme.S(90), HorizontalAlignment.Right);
            Columns.Add(T("bottom.col.status"), Theme.S(230));
            Columns.Add(T("bottom.col.localPath"), Theme.S(280));
            Columns.Add(T("bottom.col.remotePath"), Theme.S(240));
            MultiSelect = true;
            RowHeight = Theme.S(26);
            GetText = (r, c) => r < Rows.Count ? Rows[r].Name : "";
        }

        protected override void DrawCell(Graphics g, int row, int col, Rectangle cell, bool selected, Color back)
        {
            if (row >= Rows.Count) return;
            var it = Rows[row];
            var dim = selected ? Theme.Text : Theme.TextDim;
            var tr = new Rectangle(cell.X + Theme.S(8), cell.Y, cell.Width - Theme.S(14), cell.Height);
            switch (col)
            {
                case 0:
                    {
                        var icon = Icons.ForItem(new FsItem { Name = it.Name, Kind = FsKind.File }, true);
                        int x = cell.X + Theme.S(8);
                        if (icon != null) { g.DrawImage(icon, x, cell.Y + (cell.Height - icon.Height) / 2, icon.Width, icon.Height); x += icon.Width + Theme.S(8); }
                        TextRenderer.DrawText(g, it.Name, Font, new Rectangle(x, cell.Y, cell.Right - x - Theme.S(6), cell.Height), Theme.Text, back, Theme.TextLeft);
                        break;
                    }
                case 1:
                    {
                        bool down = it.Dir == TDir.Download;
                        var c = down ? Theme.Download : Theme.Upload;
                        var img = Icons.Glyph(down ? G.Download : G.Upload, Theme.S(13), c);
                        g.DrawImage(img, cell.X + Theme.S(8), cell.Y + (cell.Height - img.Height) / 2);
                        TextRenderer.DrawText(g, down ? T("bottom.dir.down") : T("bottom.dir.up"), Theme.Small, new Rectangle(cell.X + Theme.S(26), cell.Y, cell.Width - Theme.S(28), cell.Height), c, back, Theme.TextLeft);
                        break;
                    }
                case 2: TextRenderer.DrawText(g, it.PeerName, Font, tr, dim, back, Theme.TextLeft); break;
                case 3: TextRenderer.DrawText(g, Fmt.Size(it.Size), Font, tr, dim, back, Theme.TextRight); break;
                case 4:
                    {
                        Theme.HQ(g);
                        double pct = it.Status == TStatus.Done ? 100 : it.Percent;
                        var bar = new RectangleF(cell.X + Theme.S(8), cell.Y + (cell.Height - Theme.S(6)) / 2f, cell.Width - Theme.S(58), Theme.S(6));
                        Theme.FillRound(g, selected ? Theme.Lift(Theme.Raised, 0.06f) : Theme.Raised, bar, Theme.S(3));
                        Color fc = it.Status == TStatus.Failed ? Theme.Danger : it.Status == TStatus.Done ? Theme.Success :
                                   it.Status == TStatus.Canceled || it.Status == TStatus.Skipped ? Theme.TextFaint :
                                   it.Dir == TDir.Download ? Theme.Download : Theme.Upload;
                        float w = (float)(bar.Width * Math.Max(0, Math.Min(100, pct)) / 100.0);
                        if (w > 0.5f) Theme.FillRound(g, fc, new RectangleF(bar.X, bar.Y, Math.Max(w, Theme.S(6)), bar.Height), Theme.S(3));
                        var pr = new Rectangle((int)bar.Right + Theme.S(4), cell.Y, Theme.S(44), cell.Height);
                        TextRenderer.DrawText(g, ((int)pct) + "%", Theme.Small, pr, dim, back, Theme.TextRight);
                        break;
                    }
                case 5: TextRenderer.DrawText(g, it.Status == TStatus.Active ? Fmt.Speed(it.Speed) : "", Font, tr, dim, back, Theme.TextRight); break;
                case 6:
                    {
                        Color c = it.Status == TStatus.Failed ? Theme.Danger : it.Status == TStatus.Done ? Theme.Success :
                                  it.Status == TStatus.Active ? Theme.Info : dim;
                        string s = it.StatusText;
                        if (it.Status == TStatus.Active && it.Speed > 1 && it.Size > it.Done)
                            s += " · " + T("bottom.timeLeft", Fmt.Duration((it.Size - it.Done) / it.Speed));
                        TextRenderer.DrawText(g, s, Font, tr, c, back, Theme.TextLeft);
                        break;
                    }
                case 7: TextRenderer.DrawText(g, it.LocalPath, Font, tr, dim, back, Theme.TextLeft | TextFormatFlags.PathEllipsis); break;
                case 8: TextRenderer.DrawText(g, it.RemoteDisplay, Font, tr, dim, back, Theme.TextLeft | TextFormatFlags.PathEllipsis); break;
            }
        }
    }

    /// <summary>아래쪽 패널 — 전송 대기열/실패/완료, 내 공유 활동, 로그.</summary>
    public sealed class BottomPanel : Panel
    {
        readonly AppCore _c = AppCore.I;
        public readonly TabStrip Tabs;
        public readonly TransferListView Transfers;
        readonly DarkListView _activity, _log;
        readonly FlowLayoutPanel _tools;
        readonly Timer _timer = new Timer { Interval = 350 };
        readonly FlatButton _pause, _cancel, _retry, _clear, _clearAct, _clearLog, _openLog;
        int _ver = -1, _actVer = -1;
        List<ActivityEntry> _acts = new List<ActivityEntry>();
        readonly List<LogEntry> _logs = new List<LogEntry>();
        readonly List<LogEntry> _pendingLogs = new List<LogEntry>();
        readonly object _logLock = new object();

        public event Action<ContextMenuStrip, List<TransferItem>> BuildTransferMenu;
        public event Action<TransferItem> TransferActivated;

        public const int TabQueue = 0, TabFailed = 1, TabDone = 2, TabActivity = 3, TabLog = 4;

        public BottomPanel()
        {
            BackColor = Theme.Bg;
            var top = new Panel { Dock = DockStyle.Top, Height = Theme.S(38), BackColor = Theme.Panel };
            Tabs = new TabStrip { Dock = DockStyle.Fill, Height = Theme.S(38) };
            Tabs.Add(T("bottom.tab.queue"), G.Sync);
            Tabs.Add(T("bottom.tab.failed"), G.Error);
            Tabs.Add(T("bottom.tab.done"), G.Accept);
            Tabs.Add(T("bottom.tab.activity"), G.Activity);
            Tabs.Add(T("bottom.tab.log"), G.List);
            Tabs.SelectedChanged += (s, e) => OnTab();
            _tools = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = Theme.Panel, Padding = new Padding(0, Theme.S(4), Theme.S(6), 0) };
            _pause = Tool(T("bottom.pause"), G.Pause, () => _c.Transfers.SetPaused(!_c.Transfers.Paused));
            _cancel = Tool(T("bottom.cancelSel"), G.Cancel, () => _c.Transfers.Cancel(SelectedTransfers()));
            _retry = Tool(T("common.retry"), G.Retry, () => _c.Transfers.Retry(SelectedTransfers().Count > 0 ? SelectedTransfers() : _c.Transfers.Snapshot().Where(i => i.Status == TStatus.Failed || i.Status == TStatus.Canceled).ToList()));
            _clear = Tool(T("bottom.clearList"), G.Clear, ClearCurrent);
            _clearAct = Tool(T("bottom.clear"), G.Clear, () => { _c.Activity.ClearFinished(); });
            _clearLog = Tool(T("bottom.clear"), G.Clear, () => { lock (_logLock) _logs.Clear(); _log.SetCount(0); });
            _openLog = Tool(T("bottom.logFolder"), G.OpenFolder, () => ShellOps.OpenFolder(AppPaths.LogDir));
            top.Controls.Add(Tabs);
            top.Controls.Add(_tools);

            Transfers = new TransferListView { Dock = DockStyle.Fill };
            Transfers.EmptyText = T("bottom.emptyQueue");
            Transfers.EmptySub = T("bottom.emptyQueueSub");
            Transfers.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                var m = new ContextMenuStrip();
                BuildTransferMenu?.Invoke(m, SelectedTransfers());
                if (m.Items.Count == 0) { m.Dispose(); return; }
                DarkRenderer.Polish(m);
                m.Closed += (o, a) => BeginInvoke(new Action(m.Dispose));
                m.Show(Transfers, e.Location);
            };
            Transfers.DoubleClick += (s, e) => { var sel = SelectedTransfers(); if (sel.Count == 1) TransferActivated?.Invoke(sel[0]); };
            Transfers.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete) { var sel = SelectedTransfers(); _c.Transfers.Cancel(sel); _c.Transfers.Remove(i => sel.Contains(i)); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.A) { Transfers.SelectAllRows(); e.Handled = true; }
            };

            _activity = new DarkListView { Dock = DockStyle.Fill, Visible = false, MultiSelect = true };
            _activity.Columns.Add(T("bottom.col.time"), Theme.S(80));
            _activity.Columns.Add(T("bottom.col.peer"), Theme.S(110));
            _activity.Columns.Add(T("bottom.col.action"), Theme.S(120));
            _activity.Columns.Add(T("bottom.col.path"), Theme.S(330));
            _activity.Columns.Add(T("common.size"), Theme.S(80), HorizontalAlignment.Right);
            _activity.Columns.Add(T("bottom.col.progress"), Theme.S(80), HorizontalAlignment.Right);
            _activity.Columns.Add(T("bottom.col.status"), Theme.S(160));
            _activity.EmptyText = T("bottom.emptyActivity");
            _activity.EmptySub = T("bottom.emptyActivitySub");
            _activity.GetText = (r, c) =>
            {
                if (r >= _acts.Count) return "";
                var a = _acts[r];
                switch (c)
                {
                    case 0: return a.Time.ToString("HH:mm:ss");
                    case 1: return a.PeerName;
                    case 2: return a.Action;
                    case 3: return a.Path;
                    case 4: return a.Size >= 0 ? Fmt.Size(a.Size) : "";
                    case 5: return a.Size > 0 ? (int)(a.Done * 100.0 / a.Size) + "%" : "";
                    default: return a.Finished ? a.Status : (a.Speed > 0 ? T("bottom.inProgressSpeed", Fmt.Speed(a.Speed)) : T("bottom.inProgress"));
                }
            };
            _activity.GetColor = (r, c) =>
            {
                if (r >= _acts.Count) return null;
                var a = _acts[r];
                if (c == 6) return a.Failed ? Theme.Danger : a.Finished ? Theme.Success : Theme.Info;
                if (c == 2) return a.Kind == "delete" ? Theme.Danger : a.Kind == "upload" ? Theme.Upload : a.Kind == "download" ? Theme.Download : Theme.Text;
                return c == 3 ? Theme.Text : Theme.TextDim;
            };

            _log = new DarkListView { Dock = DockStyle.Fill, Visible = false, MultiSelect = true, RowHeight = Theme.S(22) };
            _log.Columns.Add(T("bottom.col.time"), Theme.S(110));
            _log.Columns.Add(T("bottom.col.category"), Theme.S(70));
            _log.Columns.Add(T("bottom.col.message"), Theme.S(900));
            _log.GetText = (r, c) =>
            {
                LogEntry e;
                lock (_logLock) { if (r >= _logs.Count) return ""; e = _logs[r]; }
                return c == 0 ? e.Time.ToString("HH:mm:ss") : c == 1 ? e.Category : e.Text;
            };
            _log.GetColor = (r, c) =>
            {
                LogEntry e;
                lock (_logLock) { if (r >= _logs.Count) return null; e = _logs[r]; }
                if (c != 2) return Theme.TextFaint;
                switch (e.Level)
                {
                    case LogLevel.Error: return Theme.Danger;
                    case LogLevel.Warn: return Theme.Warn;
                    case LogLevel.Success: return Theme.Success;
                    default: return Theme.TextDim;
                }
            };
            _log.GetIcon = r =>
            {
                LogEntry e;
                lock (_logLock) { if (r >= _logs.Count) return null; e = _logs[r]; }
                switch (e.Level)
                {
                    case LogLevel.Error: return Icons.Glyph(G.Error, Theme.S(12), Theme.Danger);
                    case LogLevel.Warn: return Icons.Glyph(G.Warning, Theme.S(12), Theme.Warn);
                    case LogLevel.Success: return Icons.Glyph(G.Accept, Theme.S(12), Theme.Success);
                    default: return Icons.Glyph(G.Info, Theme.S(12), Theme.TextFaint);
                }
            };
            _log.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.C) { CopyLog(); e.Handled = true; }
                else if (e.Control && e.KeyCode == Keys.A) { _log.SelectAllRows(); e.Handled = true; }
            };
            _log.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                var m = new ContextMenuStrip();
                m.Items.Add(T("common.copy"), Icons.Glyph(G.Copy, Theme.TextDim), (o, a) => CopyLog());
                m.Items.Add(T("bottom.clearAll"), Icons.Glyph(G.Clear, Theme.TextDim), (o, a) => { lock (_logLock) _logs.Clear(); _log.SetCount(0); });
                m.Items.Add(T("bottom.openLogFolder"), Icons.Glyph(G.OpenFolder, Theme.TextDim), (o, a) => ShellOps.OpenFolder(AppPaths.LogDir));
                DarkRenderer.Polish(m);
                m.Closed += (o, a) => BeginInvoke(new Action(m.Dispose));
                m.Show(_log, e.Location);
            };

            Controls.Add(Transfers);
            Controls.Add(_activity);
            Controls.Add(_log);
            Controls.Add(top);

            foreach (var e in Log.Snapshot()) _logs.Add(e);
            Log.Added += e => { lock (_logLock) _pendingLogs.Add(e); };
            _timer.Tick += (s, e) => Tick();
            _timer.Start();
            OnTab();
        }

        FlatButton Tool(string text, char glyph, Action click)
        {
            var b = new FlatButton(text, ButtonKind.Ghost, glyph) { Height = Theme.S(30), CustomBack = Theme.Panel, CustomHover = Theme.Hover, Margin = new Padding(Theme.S(2), 0, 0, 0) };
            b.Font = Theme.Small;
            b.AutoFit();
            b.Click += (s, e) => click();
            _tools.Controls.Add(b);
            return b;
        }

        public void ShowTab(int i) { Tabs.Selected = i; }

        void OnTab()
        {
            int t = Tabs.Selected;
            bool xfer = t <= TabDone;
            Transfers.Visible = xfer;
            _activity.Visible = t == TabActivity;
            _log.Visible = t == TabLog;
            _pause.Visible = _cancel.Visible = t == TabQueue;
            _retry.Visible = t == TabFailed;
            _clear.Visible = t == TabFailed || t == TabDone;
            _clearAct.Visible = t == TabActivity;
            _clearLog.Visible = _openLog.Visible = t == TabLog;
            Transfers.EmptyText = t == TabQueue ? T("bottom.emptyQueue") : t == TabFailed ? T("bottom.emptyFailed") : T("bottom.emptyDone");
            Transfers.EmptySub = t == TabQueue ? T("bottom.emptyQueueSub") : "";
            _ver = -1;
            _actVer = -1;
            Tick();
            if (t == TabLog)
            {
                int n;
                lock (_logLock) n = _logs.Count;
                _log.SetCount(n);
                if (n > 0) _log.EnsureVisible(n - 1);
            }
        }

        public List<TransferItem> SelectedTransfers() =>
            Transfers.SelectedRows().Where(i => i < Transfers.Rows.Count).Select(i => Transfers.Rows[i]).ToList();

        void ClearCurrent()
        {
            int t = Tabs.Selected;
            if (t == TabFailed) _c.Transfers.Remove(i => i.Status == TStatus.Failed || i.Status == TStatus.Canceled);
            else if (t == TabDone) _c.Transfers.Remove(i => i.Status == TStatus.Done || i.Status == TStatus.Skipped);
        }

        void CopyLog()
        {
            var lines = new List<string>();
            lock (_logLock)
                foreach (int i in _log.SelectedRows())
                    if (i < _logs.Count) lines.Add($"{_logs[i].Time:HH:mm:ss} [{_logs[i].Category}] {_logs[i].Text}");
            if (lines.Count > 0) try { Clipboard.SetText(string.Join("\r\n", lines)); } catch { }
        }

        void Tick()
        {
            // 전송
            int v = _c.Transfers.Version;
            var snap = _c.Transfers.Snapshot();
            int q = snap.Count(i => i.Status == TStatus.Queued || i.Status == TStatus.Active);
            int f = snap.Count(i => i.Status == TStatus.Failed || i.Status == TStatus.Canceled);
            int d = snap.Count(i => i.Status == TStatus.Done || i.Status == TStatus.Skipped);
            Tabs.SetBadge(TabQueue, q > 0 ? q.ToString() : "", q > 0 ? Theme.Accent : (Color?)null);
            Tabs.SetBadge(TabFailed, f > 0 ? f.ToString() : "", f > 0 ? Theme.Danger : (Color?)null);
            Tabs.SetBadge(TabDone, d > 0 ? d.ToString() : "");
            _pause.Text = _c.Transfers.Paused ? T("bottom.resume") : T("bottom.pause");
            _pause.Glyph = _c.Transfers.Paused ? G.Play : G.Pause;
            _pause.AutoFit();
            int t = Tabs.Selected;
            if (t <= TabDone && v != _ver)
            {
                _ver = v;
                Func<TransferItem, bool> pred = t == TabQueue ? (Func<TransferItem, bool>)(i => i.Status == TStatus.Queued || i.Status == TStatus.Active)
                    : t == TabFailed ? (i => i.Status == TStatus.Failed || i.Status == TStatus.Canceled)
                    : (i => i.Status == TStatus.Done || i.Status == TStatus.Skipped);
                var keep = new HashSet<TransferItem>(SelectedTransfers());
                var rows = snap.Where(pred).ToList();
                if (t == TabQueue)
                {
                    var order = new Dictionary<TransferItem, int>();
                    for (int k = 0; k < snap.Length; k++) order[snap[k]] = k;
                    rows = rows.OrderBy(i => i.Status == TStatus.Active ? 0 : 1).ThenBy(i => order[i]).ToList();
                }
                else rows = rows.OrderByDescending(i => i.Finished).ToList();
                bool same = rows.Count == Transfers.Rows.Count && rows.SequenceEqual(Transfers.Rows);
                Transfers.Rows = rows;
                if (!same)
                {
                    Transfers.SelectedIndices.Clear();
                    Transfers.SetCount(rows.Count);
                    for (int i = 0; i < rows.Count; i++) if (keep.Contains(rows[i])) Transfers.SelectedIndices.Add(i);
                }
                Transfers.Invalidate();
            }

            // 공유 활동
            int av = _c.Activity.Version;
            int active = _c.Activity.ActiveCount;
            Tabs.SetBadge(TabActivity, active > 0 ? active.ToString() : "", active > 0 ? Theme.Download : (Color?)null);
            if (t == TabActivity && av != _actVer)
            {
                _actVer = av;
                _acts = _c.Activity.Snapshot().OrderByDescending(a => a.Id).ToList();
                _activity.SetCount(_acts.Count);
                _activity.Invalidate();
            }

            // 로그
            List<LogEntry> add = null;
            lock (_logLock)
            {
                if (_pendingLogs.Count > 0)
                {
                    add = new List<LogEntry>(_pendingLogs);
                    _pendingLogs.Clear();
                    _logs.AddRange(add);
                    if (_logs.Count > 4000) _logs.RemoveRange(0, _logs.Count - 4000);
                }
            }
            if (add != null)
            {
                int errs = add.Count(e => e.Level == LogLevel.Error);
                if (errs > 0 && t != TabLog) Tabs.SetBadge(TabLog, "!", Theme.Danger);
                if (t == TabLog)
                {
                    bool atBottom = _log.ItemCount == 0 || IsLastVisible();
                    int n;
                    lock (_logLock) n = _logs.Count;
                    _log.SetCount(n);
                    if (atBottom && n > 0) _log.EnsureVisible(n - 1);
                }
            }
            if (t == TabLog) Tabs.SetBadge(TabLog, "");
        }

        bool IsLastVisible()
        {
            try
            {
                int last = _log.ItemCount - 1;
                var r = _log.GetItemRect(last);
                return r.Top <= _log.ClientSize.Height;
            }
            catch { return true; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
