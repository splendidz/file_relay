using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FileRelay.UI
{
    /// <summary>
    /// 완전 오너드로우 다크 ListView (Details + VirtualMode).
    ///  · 머리글/행/선택/호버를 직접 그린다. 머리글 오른쪽 빈 영역은 HeaderPainter 가 칠한다.
    ///  · 데이터는 GetText/GetColor/GetIcon 대리자 또는 DrawCell 재정의로 공급.
    ///  · 항목이 없을 때 EmptyText 를 가운데에 표시.
    /// </summary>
    public class DarkListView : ListView
    {
        int _hot = -1;
        int _rowHeight;
        HeaderPainter _hdr;
        string _emptyText = "";

        public Func<int, int, string> GetText;
        public Func<int, int, Color?> GetColor;
        public Func<int, Image> GetIcon;
        public int SortColumn = -1;
        public bool SortDescending;
        public Color RowBack = Theme.Bg;
        public string EmptySub = "";

        public DarkListView()
        {
            OwnerDraw = true;
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
            DoubleBuffered = true;
            BorderStyle = BorderStyle.None;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui;
            HeaderStyle = ColumnHeaderStyle.Clickable;
            AllowColumnReorder = false;
            VirtualMode = true;
            RowHeight = Theme.S(26);
        }

        public int RowHeight
        {
            get => _rowHeight;
            set
            {
                _rowHeight = value;
                SmallImageList = new ImageList { ImageSize = new Size(1, Math.Max(16, value)) };
            }
        }

        public string EmptyText
        {
            get => _emptyText;
            set { if (_emptyText != value) { _emptyText = value ?? ""; Invalidate(); } }
        }

        public int ItemCount => VirtualMode ? VirtualListSize : Items.Count;

        public void SetCount(int n)
        {
            if (!VirtualMode) return;
            if (VirtualListSize == n) { Invalidate(); return; }
            // 줄어들 때 범위 밖 선택/포커스가 남으면 예외가 나므로 먼저 정리.
            if (n < VirtualListSize)
            {
                var keep = new List<int>();
                foreach (int i in SelectedIndices) if (i < n) keep.Add(i);
                if (keep.Count != SelectedIndices.Count)
                {
                    SelectedIndices.Clear();
                    foreach (int i in keep) SelectedIndices.Add(i);
                }
            }
            if (_hot >= n) _hot = -1;
            VirtualListSize = n;
            Invalidate();
        }

        public List<int> SelectedRows()
        {
            var l = new List<int>();
            foreach (int i in SelectedIndices) l.Add(i);
            l.Sort();
            return l;
        }

        public void SelectRow(int i, bool clear = true, bool focus = true)
        {
            if (i < 0 || i >= ItemCount) return;
            if (clear) SelectedIndices.Clear();
            SelectedIndices.Add(i);
            if (focus)
            {
                try { FocusedItem = Items[i]; } catch { }
            }
            EnsureVisible(i);
        }

        public void SelectAllRows()
        {
            if (ItemCount == 0) return;
            var lvi = new Native.LVITEM { mask = 0x8, state = 0x2, stateMask = 0x2 }; // LVIF_STATE, LVIS_SELECTED
            Native.SendMessage(Handle, Native.LVM_SETITEMSTATE, (IntPtr)(-1), ref lvi);
        }

        public int FocusedRow
        {
            get
            {
                try { return FocusedItem?.Index ?? -1; } catch { return -1; }
            }
        }

        protected override void OnRetrieveVirtualItem(RetrieveVirtualItemEventArgs e)
        {
            string t = "";
            try { t = GetText?.Invoke(e.ItemIndex, 0) ?? ""; } catch { }
            // 가상 모드에서는 열 수만큼 하위 항목이 있어야 한다 (그리기는 직접 하므로 빈 값).
            var item = new ListViewItem(t);
            for (int i = 1; i < Columns.Count; i++) item.SubItems.Add("");
            e.Item = item;
            base.OnRetrieveVirtualItem(e);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetWindowTheme(Handle, Theme.IsDark ? "DarkMode_Explorer" : "Explorer", null);
            var h = Native.SendMessage(Handle, Native.LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
            if (h != IntPtr.Zero)
            {
                Native.SetWindowTheme(h, Theme.IsDark ? "DarkMode_ItemsView" : "ItemsView", null);
                _hdr = new HeaderPainter(this);
                _hdr.AssignHandle(h);
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            _hdr?.ReleaseHandle();
            _hdr = null;
            base.OnHandleDestroyed(e);
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int idx = -1;
            try { idx = GetItemAt(Theme.S(4), e.Y)?.Index ?? -1; } catch { }
            if (idx != _hot)
            {
                InvalidateRow(_hot);
                _hot = idx;
                InvalidateRow(_hot);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            InvalidateRow(_hot);
            _hot = -1;
        }

        public void InvalidateRow(int i)
        {
            if (i < 0 || i >= ItemCount) return;
            try
            {
                var r = GetItemRect(i);
                r.X = 0;
                r.Width = ClientSize.Width;
                Invalidate(r);
            }
            catch { }
        }

        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            var g = e.Graphics;
            var r = e.Bounds;
            using (var b = new SolidBrush(Theme.Panel)) g.FillRectangle(b, r);
            using (var p = new Pen(Theme.BorderSoft))
            {
                g.DrawLine(p, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
                g.DrawLine(p, r.Right - 1, r.Top + Theme.S(6), r.Right - 1, r.Bottom - Theme.S(6));
            }
            bool sorted = e.ColumnIndex == SortColumn;
            int arrowW = sorted ? Theme.S(14) : 0;
            var tr = new Rectangle(r.X + Theme.S(8), r.Y, r.Width - Theme.S(14) - arrowW, r.Height);
            var flags = e.Header.TextAlign == HorizontalAlignment.Right ? Theme.TextRight :
                        e.Header.TextAlign == HorizontalAlignment.Center ? Theme.TextCenter : Theme.TextLeft;
            TextRenderer.DrawText(g, e.Header.Text, Font, tr, sorted ? Theme.Text : Theme.TextDim, Theme.Panel, flags);
            if (sorted && r.Width > Theme.S(30))
            {
                var img = Icons.Glyph(SortDescending ? G.ChevronDown : '', Theme.S(10), Theme.TextDim);
                int ax = e.Header.TextAlign == HorizontalAlignment.Right ? r.X + Theme.S(4) : r.Right - Theme.S(16);
                if (e.Header.TextAlign == HorizontalAlignment.Right) tr.X += 0;
                g.DrawImage(img, ax, r.Y + (r.Height - img.Height) / 2);
            }
        }

        protected virtual Color RowBackFor(int index) => RowBack;

        protected override void OnDrawItem(DrawListViewItemEventArgs e)
        {
            if (View != View.Details) { e.DrawDefault = true; return; }
            int index = e.ItemIndex;
            bool sel;
            try { sel = e.Item.Selected; } catch { sel = false; }
            bool hot = index == _hot;
            var g = e.Graphics;
            var row = e.Bounds;
            row.Width = Math.Max(row.Width, ClientSize.Width - row.X);
            Color back = sel ? (ContainsFocus ? Theme.Selection : Theme.SelectionInactive) : hot ? Theme.Hover : RowBackFor(index);
            using (var b = new SolidBrush(back)) g.FillRectangle(b, row);
            if (sel && ContainsFocus && e.Item.Focused)
            {
                using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, row.X, row.Y + Theme.S(4), Theme.S(2), row.Height - Theme.S(8));
            }
            int x = e.Bounds.X;
            for (int c = 0; c < Columns.Count; c++)
            {
                int w = Columns[c].Width;
                if (x + w > 0 && x < ClientSize.Width)
                    DrawCell(g, index, c, new Rectangle(x, row.Y, w, row.Height), sel, back);
                x += w;
            }
            DrawRowOverlay(g, index, row, sel);
        }

        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e) { }

        protected virtual void DrawRowOverlay(Graphics g, int index, Rectangle row, bool selected) { }

        protected virtual void DrawCell(Graphics g, int row, int col, Rectangle cell, bool selected, Color back)
        {
            string text = "";
            try { text = GetText?.Invoke(row, col) ?? ""; } catch { }
            Color fore = Theme.Text;
            try { fore = GetColor?.Invoke(row, col) ?? (col == 0 ? Theme.Text : Theme.TextDim); } catch { }
            int x = cell.X + Theme.S(8);
            if (col == 0 && GetIcon != null)
            {
                Image img = null;
                try { img = GetIcon(row); } catch { }
                if (img != null)
                {
                    g.DrawImage(img, x, cell.Y + (cell.Height - img.Height) / 2, img.Width, img.Height);
                    x += img.Width + Theme.S(8);
                }
            }
            var align = Columns[col].TextAlign;
            var flags = align == HorizontalAlignment.Right ? Theme.TextRight : align == HorizontalAlignment.Center ? Theme.TextCenter : Theme.TextLeft;
            var tr = new Rectangle(x, cell.Y, cell.Right - x - Theme.S(8), cell.Height);
            TextRenderer.DrawText(g, text, Font, tr, fore, back, flags);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NMHDR { public IntPtr hwndFrom; public IntPtr idFrom; public int code; }

        [StructLayout(LayoutKind.Sequential)]
        struct NMCUSTOMDRAW
        {
            public NMHDR hdr;
            public int dwDrawStage;
            public IntPtr hdc;
            public Native.RECT rc;
            public IntPtr dwItemSpec;
            public uint uItemState;
            public IntPtr lItemlParam;
        }

        const int OCM_NOTIFY = 0x204E, NM_CUSTOMDRAW = -12, CDDS_ITEMPREPAINT = 0x10001;
        const int CDIS_SELECTED = 0x1, CDIS_FOCUS = 0x10, CDIS_HOT = 0x40;
        static readonly int StageOffset = Marshal.OffsetOf(typeof(NMCUSTOMDRAW), "dwDrawStage").ToInt32();
        static readonly int StateOffset = Marshal.OffsetOf(typeof(NMCUSTOMDRAW), "uItemState").ToInt32();

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == OCM_NOTIFY && m.LParam != IntPtr.Zero)
            {
                var hdr = (NMHDR)Marshal.PtrToStructure(m.LParam, typeof(NMHDR));
                if (hdr.code == NM_CUSTOMDRAW && hdr.hwndFrom == Handle)
                {
                    base.WndProc(ref m);   // 여기서 OnDrawItem 이 우리 방식으로 행을 그린다
                    // Windows 'Explorer' 테마는 그 위에 자체 선택/호버/포커스 표시를 반투명으로 덧그린다.
                    // 항목 상태 플래그를 지워서 덧그리지 않게 한다 (선택 여부는 직접 조회하므로 영향 없음).
                    int stage = Marshal.ReadInt32(m.LParam, StageOffset);
                    if ((stage & CDDS_ITEMPREPAINT) == CDDS_ITEMPREPAINT)
                    {
                        int st = Marshal.ReadInt32(m.LParam, StateOffset);
                        Marshal.WriteInt32(m.LParam, StateOffset, st & ~(CDIS_SELECTED | CDIS_FOCUS | CDIS_HOT));
                    }
                    return;
                }
            }
            base.WndProc(ref m);
            if (m.Msg != Native.WM_PAINT) return;
            // 다크 테마는 마지막 행 아래 빈 영역에 열 구분선을 그린다 — 배경색으로 덮는다.
            int bottom = HeaderHeight();
            try { if (ItemCount > 0) bottom = Math.Max(bottom, GetItemRect(ItemCount - 1).Bottom); } catch { }
            if (bottom < ClientSize.Height)
                using (var g = CreateGraphics())
                using (var b = new SolidBrush(BackColor))
                    g.FillRectangle(b, 0, bottom, ClientSize.Width, ClientSize.Height - bottom);
            if (ItemCount == 0 && (!string.IsNullOrEmpty(_emptyText)))
            {
                using (var g = CreateGraphics())
                {
                    int top = HeaderHeight();
                    var area = new Rectangle(0, top, ClientSize.Width, Math.Max(0, ClientSize.Height - top));
                    if (area.Height < Theme.S(30)) return;
                    var r1 = new Rectangle(area.X + Theme.S(12), area.Y + area.Height / 2 - Theme.S(26), area.Width - Theme.S(24), Theme.S(26));
                    TextRenderer.DrawText(g, _emptyText, Font, r1, Theme.TextDim, BackColor, Theme.TextCenter);
                    if (!string.IsNullOrEmpty(EmptySub))
                    {
                        var r2 = new Rectangle(area.X + Theme.S(12), r1.Bottom, area.Width - Theme.S(24), Theme.S(22));
                        TextRenderer.DrawText(g, EmptySub, Theme.Small, r2, Theme.TextFaint, BackColor, Theme.TextCenter);
                    }
                }
            }
        }

        public int HeaderHeight()
        {
            if (_hdr == null || _hdr.Handle == IntPtr.Zero || HeaderStyle == ColumnHeaderStyle.None) return 0;
            Native.GetClientRect(_hdr.Handle, out var rc);
            return rc.Bottom - rc.Top;
        }

        /// <summary>머리글 오른쪽 빈 영역을 다크로 칠한다.</summary>
        sealed class HeaderPainter : NativeWindow
        {
            readonly DarkListView _lv;
            public HeaderPainter(DarkListView lv) { _lv = lv; }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg != Native.WM_PAINT) return;
                try
                {
                    int count = (int)Native.SendMessage(Handle, Native.HDM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
                    int right = 0;
                    if (count > 0)
                    {
                        var rc = new Native.RECT();
                        Native.SendMessage(Handle, Native.HDM_GETITEMRECT, (IntPtr)(count - 1), ref rc);
                        right = rc.Right;
                    }
                    Native.GetClientRect(Handle, out var cr);
                    if (right < cr.Right)
                    {
                        using (var g = Graphics.FromHwnd(Handle))
                        using (var b = new SolidBrush(Theme.Panel))
                        using (var p = new Pen(Theme.BorderSoft))
                        {
                            g.FillRectangle(b, right, 0, cr.Right - right, cr.Bottom);
                            g.DrawLine(p, right, cr.Bottom - 1, cr.Right, cr.Bottom - 1);
                        }
                    }
                }
                catch { }
            }
        }
    }
}
