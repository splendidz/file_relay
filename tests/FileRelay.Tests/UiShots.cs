using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using FileRelay.UI;

namespace FileRelay.Tests
{
    /// <summary>
    /// 실제 UI 를 같은 프로세스에서 띄워 상태별 화면을 캡처한다 (시각 점검용).
    ///   FileRelay.Tests ui &lt;데이터폴더&gt; &lt;출력폴더&gt;
    /// </summary>
    static class UiShots
    {
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

        static string _out;
        static MainForm _main;
        static readonly List<(int Delay, Action Act)> Steps = new List<(int, Action)>();

        public static int Run(string dataDir, string outDir)
        {
            _out = Path.GetFullPath(outDir);
            Directory.CreateDirectory(_out);
            AppPaths.Init(dataDir);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var ui = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(ui);
            var core = AppCore.Create(ui);
            core.LoadConfig();
            L.Init(core.Config.Language);
            Theme.Init(core.Config.EffectiveUiFont, core.Config.UiFontSize, core.Config.Theme);
            ToolStripManager.Renderer = new DarkRenderer();
            core.Start();
            _main = new MainForm();
            _main.Show();

            Peer B() => core.Peers.All().FirstOrDefault();
            T F<T>(string name) => (T)typeof(MainForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_main);
            void Call(string name, params object[] args) => typeof(MainForm).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public).Invoke(_main, args);

            Add(3500, () => Snap(_main, "01_start"));
            Add(200, () => { var p = B(); F<PeerPanel>("_peers").Select(p); });
            Add(1800, () => Snap(_main, "02_peer_selected"));
            Add(200, () => F<FilePane>("_remote").Navigate("설계 문서"));
            Add(1200, () => { var r = F<FilePane>("_remote"); r.List.Focus(); r.List.SelectRow(2); r.List.SelectedIndices.Add(4); });
            Add(600, () => Snap(_main, "03_remote_browse"));
            Add(200, () => Call("ToggleCompare"));
            Add(600, () => Snap(_main, "04_compare"));
            Add(200, () => Call("ToggleCompare"));
            // 전송 (확인 창)
            Add(200, () => { core.SetSpeedLimit(3 * 1024); var r = F<FilePane>("_remote"); r.List.SelectedIndices.Clear(); r.List.SelectRow(r.List.Data.FindIndex(i => i.Name == "배포 패키지.zip")); Call("TransferFrom", r); });
            Add(1500, () => SnapTop<MessageDialog>("05_confirm", DialogResult.OK));
            Add(2500, () => Snap(_main, "06_transferring"));
            // 컨텍스트 메뉴
            Add(200, () =>
            {
                var r = F<FilePane>("_remote");
                var m = new ContextMenuStrip();
                typeof(MainForm).GetMethod("BuildPaneMenu", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_main, new object[] { r, m, r.Selected() });
                DarkRenderer.Polish(m);
                m.Show(r.List, new Point(200, 120));
                _menu = m;
            });
            Add(700, () => { SnapHandle(_menu.Handle, "07_context_menu"); _menu.Close(); });
            Add(200, () =>
            {
                var m = new ContextMenuStrip();
                typeof(MainForm).GetMethod("BuildPeerMenu", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_main, new object[] { B(), m });
                DarkRenderer.Polish(m);
                m.Show(_main, new Point(100, 260));
                _menu = m;
            });
            Add(700, () => { SnapHandle(_menu.Handle, "08_peer_menu"); _menu.Close(); });
            // 쪽지
            Add(200, () =>
            {
                var n = new Note { Incoming = true, PeerId = B().Id, PeerName = B().DisplayName, Color = "yellow", Text = "길동씨, 오후 3시 회의 자료 '설계 문서' 공유 폴더에 올려두었어요.\n확인 부탁드립니다 🙂\nhttp://intranet/wiki/meeting" };
                core.Notes.Add(n);
                NoteManager.ShowIncoming(n, true);
            });
            Add(900, () => SnapForm<NoteWindow>("09_note_dark"));
            Add(200, () =>
            {
                var c = core.Config.Clone(); c.NoteStyle = "pastel"; core.ApplyConfig(c, false);
                var n = new Note { Incoming = true, PeerId = B().Id, PeerName = B().DisplayName, Color = "green", Text = "점심 같이 하실래요? 12시에 1층 로비에서 봬요!" };
                core.Notes.Add(n);
                NoteManager.ShowIncoming(n, true);
            });
            Add(900, () => SnapForm<NoteWindow>("10_note_pastel", last: true));
            Add(200, () => { var c = core.Config.Clone(); c.NoteStyle = "dark"; core.ApplyConfig(c, false); NoteManager.Compose(B()); });
            Add(900, () => SnapForm<ComposeWindow>("11_compose"));
            Add(200, () => { foreach (var f in Application.OpenForms.OfType<ComposeWindow>().ToList()) f.Close(); NotesForm.ShowInbox(); });
            Add(1000, () => SnapForm<NotesForm>("12_inbox"));
            Add(200, () => { foreach (var f in Application.OpenForms.OfType<NotesForm>().ToList()) f.Close(); NoteManager.CloseAll(); });
            // 설정 창 페이지
            for (int pg = 0; pg < 6; pg++)
            {
                int page = pg;
                Add(300, () => { var s = new SettingsForm(page); s.Show(_main); });
                Add(900, () => { var s = Application.OpenForms.OfType<SettingsForm>().First(); Snap(s, "13_settings_" + page); s.Close(); });
            }
            Add(200, () => { var s = Application.OpenForms.OfType<SettingsForm>().FirstOrDefault(); s?.Close(); new ShareEditDialog(core.Config.Shares[0], core.Config.Shares).Show(_main); });
            Add(800, () => SnapForm<ShareEditDialog>("14_share_edit", close: true));
            Add(200, () => new AddPeerDialog("192.168.0.15").Show(_main));
            Add(800, () => SnapForm<AddPeerDialog>("15_add_peer", close: true));
            Add(200, () =>
            {
                var req = new ConflictRequest
                {
                    Item = new TransferItem { Dir = TDir.Download, PeerName = "김철수", LocalPath = @"C:\work\예산안_v3.xlsx", Size = 96500, SrcTime = DateTime.Now.AddHours(-1) },
                    TargetSize = 88000, TargetTime = DateTime.Now.AddDays(-2), CanOverwrite = true, CanResume = false,
                };
                new ConflictDialog(req).Show(_main);
            });
            Add(800, () => SnapForm<ConflictDialog>("16_conflict", close: true));
            Add(200, () => new FirstRunDialog(new AppConfig()).Show(_main));
            Add(800, () => SnapForm<FirstRunDialog>("17_first_run", close: true));
            Add(200, () => new ShortcutsDialog().Show(_main));
            Add(800, () => SnapForm<ShortcutsDialog>("18_shortcuts", close: true));
            Add(200, () => F<BottomPanel>("_bottom").ShowTab(BottomPanel.TabLog));
            Add(800, () => Snap(_main, "19_log_tab"));
            Add(200, () => { F<BottomPanel>("_bottom").ShowTab(BottomPanel.TabQueue); core.SetSpeedLimit(0); });
            Add(4000, () => { F<BottomPanel>("_bottom").ShowTab(BottomPanel.TabDone); });
            Add(600, () => Snap(_main, "20_done_tab"));
            Add(300, () => _main.Close());
            Add(900, () => SnapTop<MessageDialog>("21_close_ask", DialogResult.Cancel));
            Add(300, () => Snap(_main, "22_after_cancel"));
            Add(300, () => { Console.WriteLine("DONE"); _main.ForceExit(); });

            int idx = 0;
            var timer = new System.Windows.Forms.Timer { Interval = 50 };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                if (idx >= Steps.Count) return;
                var (delay, act) = Steps[idx++];
                try { act(); } catch (Exception ex) { Console.WriteLine($"step {idx} 오류: {ex.GetBaseException().Message}"); }
                if (idx < Steps.Count) { timer.Interval = Math.Max(10, Steps[idx].Delay); timer.Start(); }
            };
            timer.Interval = Steps[0].Delay;
            timer.Start();
            Application.Run(new ApplicationContext());
            core.Shutdown();
            return 0;
        }

        static ContextMenuStrip _menu;

        static void Add(int delay, Action a) => Steps.Add((delay, a));

        static void SnapForm<T>(string name, bool close = false, bool last = false) where T : Form
        {
            var forms = Application.OpenForms.OfType<T>().ToList();
            var f = last ? forms.LastOrDefault() : forms.FirstOrDefault();
            if (f == null) { Console.WriteLine("form not found: " + typeof(T).Name); return; }
            Snap(f, name);
            if (close) f.Close();
        }

        static void SnapTop<T>(string name, DialogResult result) where T : Form
        {
            var f = Application.OpenForms.OfType<T>().LastOrDefault();
            if (f == null) { Console.WriteLine("dialog not found: " + typeof(T).Name); return; }
            Snap(f, name);
            f.DialogResult = result;
        }

        static void Snap(Form f, string name)
        {
            f.Refresh();
            Application.DoEvents();
            SnapHandle(f.Handle, name);
        }

        static void SnapHandle(IntPtr h, string name)
        {
            GetWindowRect(h, out var r);
            int w = r.R - r.L, hh = r.B - r.T;
            if (w <= 0 || hh <= 0) { Console.WriteLine("empty window: " + name); return; }
            using (var bmp = new Bitmap(w, hh, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    var dc = g.GetHdc();
                    PrintWindow(h, dc, 2);
                    g.ReleaseHdc(dc);
                }
                string path = Path.Combine(_out, name + ".png");
                bmp.Save(path, ImageFormat.Png);
                Console.WriteLine("shot " + path);
            }
        }
    }
}
