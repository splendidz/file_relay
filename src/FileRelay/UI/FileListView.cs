using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

using static FileRelay.L;
namespace FileRelay.UI
{
    public enum CompareMark { None, OnlyHere, DiffSize, NewerHere, OlderHere }

    /// <summary>파일 목록 (가상 모드). 폴더 먼저, 자연 정렬, 드라이브 사용량 막대, 비교 표시, 끌어놓기 대상 강조.</summary>
    public sealed class FileListView : DarkListView
    {
        public List<FsItem> Data = new List<FsItem>();
        public bool IsLocal = true;
        public Func<FsItem, CompareMark> CompareOf;
        int _dropTarget = -1;

        public FileListView()
        {
            Columns.Add(T("common.name"), Theme.S(300));
            Columns.Add(T("common.size"), Theme.S(96), HorizontalAlignment.Right);
            Columns.Add(T("common.type"), Theme.S(150));
            Columns.Add(T("common.modified"), Theme.S(140));
            SortColumn = 0;
            RowHeight = Theme.S(26);
            MultiSelect = true;
            LabelEdit = false;
            AllowDrop = true;
            ColumnClick += (s, e) =>
            {
                if (SortColumn == e.Column) SortDescending = !SortDescending;
                else { SortColumn = e.Column; SortDescending = e.Column == 1 || e.Column == 3; }
                var keep = SelectedItemsList();
                ApplySort();
                RestoreSelection(keep);
            };
            SearchForVirtualItem += (s, e) =>
            {
                if (string.IsNullOrEmpty(e.Text) || Data.Count == 0) return;
                int start = Math.Max(0, e.StartIndex);
                for (int k = 0; k < Data.Count; k++)
                {
                    int i = (start + k) % Data.Count;
                    if (Data[i].Name.StartsWith(e.Text, StringComparison.CurrentCultureIgnoreCase)) { e.Index = i; return; }
                }
            };
            GetText = (r, c) => r < Data.Count ? Data[r].Name : "";
        }

        public int DropTarget
        {
            get => _dropTarget;
            set
            {
                if (_dropTarget == value) return;
                InvalidateRow(_dropTarget);
                _dropTarget = value;
                InvalidateRow(_dropTarget);
            }
        }

        public void SetItems(List<FsItem> items, ICollection<string> selectNames = null, string focusName = null)
        {
            SelectedIndices.Clear();
            Data = items;
            ApplySort();
            if (selectNames != null && selectNames.Count > 0) RestoreSelection(selectNames, focusName);
            else if (Data.Count > 0 && focusName != null)
            {
                int i = Data.FindIndex(x => x.Name.Eq(focusName));
                if (i >= 0) SelectRow(i);
            }
            if (Data.Count > 0 && SelectedIndices.Count == 0 && Focused)
            {
                try { FocusedItem = Items[0]; } catch { }
            }
            if (Data.Count > 0) { try { EnsureVisible(Math.Max(0, FocusedRow)); } catch { } }
        }

        public List<string> SelectedItemsList()
        {
            var l = new List<string>();
            foreach (int i in SelectedIndices) if (i < Data.Count) l.Add(Data[i].Name);
            return l;
        }

        void RestoreSelection(ICollection<string> names, string focus = null)
        {
            SelectedIndices.Clear();
            var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            int first = -1;
            for (int i = 0; i < Data.Count; i++)
                if (set.Contains(Data[i].Name)) { SelectedIndices.Add(i); if (first < 0) first = i; }
            int f = focus != null ? Data.FindIndex(x => x.Name.Eq(focus)) : first;
            if (f >= 0)
            {
                try { FocusedItem = Items[f]; } catch { }
                EnsureVisible(f);
            }
        }

        static int KindOrder(FsKind k)
        {
            switch (k)
            {
                case FsKind.Special: return 0;
                case FsKind.Drive: return 1;
                case FsKind.Share: return 2;
                case FsKind.Dir: return 3;
                default: return 4;
            }
        }

        public void ApplySort()
        {
            int col = SortColumn;
            bool desc = SortDescending;
            var nc = NaturalComparer.Instance;
            Comparison<FsItem> cmp = (a, b) =>
            {
                int k = KindOrder(a.Kind).CompareTo(KindOrder(b.Kind));
                if (k != 0) return k;
                if (a.Kind == FsKind.Special || a.Kind == FsKind.Drive) return 0;   // 원래 순서 유지
                int r;
                switch (col)
                {
                    case 1: r = a.Size.CompareTo(b.Size); break;
                    case 2: r = string.Compare(Icons.TypeName(a), Icons.TypeName(b), StringComparison.CurrentCultureIgnoreCase); break;
                    case 3: r = a.MTime.CompareTo(b.MTime); break;
                    default: r = 0; break;
                }
                if (r == 0) r = nc.Compare(a.Name, b.Name);
                return desc ? -r : r;
            };
            // 안정 정렬 (드라이브 순서 유지)
            var indexed = new List<KeyValuePair<int, FsItem>>();
            for (int i = 0; i < Data.Count; i++) indexed.Add(new KeyValuePair<int, FsItem>(i, Data[i]));
            indexed.Sort((x, y) => { int c = cmp(x.Value, y.Value); return c != 0 ? c : x.Key.CompareTo(y.Key); });
            Data = indexed.ConvertAll(kv => kv.Value);
            SetCount(Data.Count);
            Invalidate();
        }

        protected override void DrawRowOverlay(Graphics g, int index, Rectangle row, bool selected)
        {
            if (index != _dropTarget) return;
            Theme.HQ(g);
            var r = new RectangleF(row.X + 1.5f, row.Y + 1.5f, Math.Min(row.Width, ClientSize.Width) - 3, row.Height - 3);
            using (var b = new SolidBrush(Color.FromArgb(40, Theme.Accent))) g.FillRectangle(b, r);
            Theme.DrawRound(g, Theme.Accent, r, Theme.S(4), 1.5f);
        }

        protected override void DrawCell(Graphics g, int row, int col, Rectangle cell, bool selected, Color back)
        {
            if (row >= Data.Count) return;
            var it = Data[row];
            var mark = CompareOf?.Invoke(it) ?? CompareMark.None;
            Color fore = it.Hidden ? Theme.TextFaint : Theme.Text;
            if (!selected)
            {
                if (mark == CompareMark.OnlyHere) fore = Theme.Success;
                else if (mark == CompareMark.DiffSize) fore = Theme.Warn;
                else if (mark == CompareMark.NewerHere) fore = Theme.Info;
            }
            switch (col)
            {
                case 0:
                    {
                        int x = cell.X + Theme.S(8);
                        var icon = Icons.ForItem(it, IsLocal);
                        if (icon != null)
                        {
                            int isz = Math.Min(icon.Height, cell.Height - Theme.S(4));
                            int iw = icon.Width * isz / Math.Max(1, icon.Height);
                            if (it.Hidden)
                            {
                                var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.5f };
                                using (var ia = new System.Drawing.Imaging.ImageAttributes())
                                {
                                    ia.SetColorMatrix(cm);
                                    g.DrawImage(icon, new Rectangle(x, cell.Y + (cell.Height - isz) / 2, iw, isz), 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, ia);
                                }
                            }
                            else g.DrawImage(icon, x, cell.Y + (cell.Height - isz) / 2, iw, isz);
                            x += iw + Theme.S(8);
                        }
                        var tr = new Rectangle(x, cell.Y, cell.Right - x - Theme.S(6), cell.Height);
                        TextRenderer.DrawText(g, it.Name, Font, tr, fore, back, Theme.TextLeft);
                        break;
                    }
                case 1:
                    {
                        if (it.Kind == FsKind.File)
                        {
                            DrawRightText(g, Fmt.Size(it.Size), cell, selected ? Theme.Text : Theme.TextDim, back);
                        }
                        else if ((it.Kind == FsKind.Drive || it.Kind == FsKind.Share) && it.Total > 0)
                        {
                            // 사용량 막대 + 여유 공간
                            double used = 1.0 - (double)it.Free / it.Total;
                            var bar = new RectangleF(cell.X + Theme.S(8), cell.Bottom - Theme.S(8), cell.Width - Theme.S(16), Theme.S(4));
                            Theme.HQ(g);
                            Theme.FillRound(g, Theme.Raised, bar, Theme.S(2));
                            var fill = new RectangleF(bar.X, bar.Y, (float)(bar.Width * Math.Max(0, Math.Min(1, used))), bar.Height);
                            Theme.FillRound(g, used > 0.9 ? Theme.Danger : Theme.Accent, fill, Theme.S(2));
                            var tr = new Rectangle(cell.X + Theme.S(4), cell.Y - Theme.S(3), cell.Width - Theme.S(10), cell.Height);
                            TextRenderer.DrawText(g, T("list.free", Fmt.Size(it.Free)), Theme.Small, tr, Theme.TextDim, back, Theme.TextRight);
                        }
                        break;
                    }
                case 2:
                    {
                        string t = Icons.TypeName(it);
                        var tr = new Rectangle(cell.X + Theme.S(8), cell.Y, cell.Width - Theme.S(14), cell.Height);
                        Color c = selected ? Theme.Text : Theme.TextDim;
                        if (it.Kind == FsKind.Share && !selected)
                            c = it.Access == AccessLevel.Full ? Theme.Warn : it.Access == AccessLevel.Write ? Theme.Info : Theme.TextDim;
                        TextRenderer.DrawText(g, t, Font, tr, c, back, Theme.TextLeft);
                        break;
                    }
                case 3:
                    {
                        var tr = new Rectangle(cell.X + Theme.S(8), cell.Y, cell.Width - Theme.S(14), cell.Height);
                        TextRenderer.DrawText(g, Fmt.Time(it.MTime), Font, tr, selected ? Theme.Text : Theme.TextDim, back, Theme.TextLeft);
                        break;
                    }
            }
        }

        void DrawRightText(Graphics g, string s, Rectangle cell, Color c, Color back)
        {
            var tr = new Rectangle(cell.X + Theme.S(4), cell.Y, cell.Width - Theme.S(12), cell.Height);
            TextRenderer.DrawText(g, s, Font, tr, c, back, Theme.TextRight);
        }

        /// <summary>마우스 위치의 행 (없으면 -1).</summary>
        public int RowAt(Point client)
        {
            try { return GetItemAt(Theme.S(4), client.Y)?.Index ?? -1; } catch { return -1; }
        }
    }
}
