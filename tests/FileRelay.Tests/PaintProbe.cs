using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// 왼쪽 피어 목록이 쓸데없이 다시 그려지지 않는지 센다 (깜박임 회귀 점검).
    ///   FileRelay.Tests paint &lt;작업폴더&gt; [초]
    /// 온라인 피어 1 + 연결되지 않는 피어 1 을 두고 하트비트(3초)가 여러 번 도는 동안 피어 목록 창이 받은
    /// 배경 지우기 / 줄 그리기 횟수를 센다 (변화가 없으면 0 이어야 한다). 이어서 한 피어의 값만 바꿔
    /// 그 줄만 다시 그려지는지 보고, 화면을 &lt;작업폴더&gt;\shots 에 캡처한다.
    /// </summary>
    static class PaintProbe
    {
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);

        sealed class Counter : NativeWindow
        {
            public int Erase, Paint, DrawItem;

            public void Reset() => Erase = Paint = DrawItem = 0;

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x0014) Erase++;            // WM_ERASEBKGND
                else if (m.Msg == 0x000F) Paint++;       // WM_PAINT
                else if (m.Msg == 0x202B) DrawItem++;    // WM_REFLECT + WM_DRAWITEM
                base.WndProc(ref m);
            }

            public override string ToString() => $"WM_ERASEBKGND={Erase} WM_PAINT={Paint} DrawItem={DrawItem}";
        }

        public static int Run(string work, int seconds)
        {
            work = Path.GetFullPath(work);
            if (Directory.Exists(work)) Directory.Delete(work, true);
            string shots = Path.Combine(work, "shots");
            Directory.CreateDirectory(shots);
            int portA = 45331, portB = 45332;
            string aRoot = Path.Combine(work, "A"), bRoot = Path.Combine(work, "B");
            Program.WriteConfig(bRoot, "노드B", portB, new List<ShareConfig>());
            Program.WriteConfig(aRoot, "노드A", portA, new List<ShareConfig>());

            var bp = Process.Start(new ProcessStartInfo(typeof(Program).Assembly.Location, $"node \"{bRoot}\"")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            Console.WriteLine("B: " + bp.StandardOutput.ReadLine());

            AppPaths.Init(aRoot);
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
            var B = core.PeerSvc.AddPeer("127.0.0.1", portB).Peer;
            core.Peers.Add(new Peer { Name = "꺼진 PC", Host = "127.0.0.1", Port = 45339, State = PeerState.Paired });

            var main = new MainForm();
            main.Show();
            var panel = (PeerPanel)typeof(MainForm).GetField("_peers", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(main);
            var list = (Control)typeof(PeerPanel).GetField("_list", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel);
            var search = (Control)typeof(PeerPanel).GetField("_search", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel);
            var split = (SplitContainer)typeof(MainForm).GetField("_splitMain", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(main);
            int events = 0;
            core.PeersChanged += () => events++;
            var counter = new Counter();
            bool ok = true;

            // 강제로 다시 그리지 않고 지금 화면 그대로 찍는다 (부분 갱신이 제대로 됐는지 보려고).
            void Snap(string name)
            {
                Application.DoEvents();
                using (var bmp = new Bitmap(main.Width, main.Height, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        var dc = g.GetHdc();
                        PrintWindow(main.Handle, dc, 2);
                        g.ReleaseHdc(dc);
                    }
                    bmp.Save(Path.Combine(shots, name + ".png"), ImageFormat.Png);
                }
            }

            var steps = new Queue<(int Delay, Action Act)>();
            void Next()
            {
                if (steps.Count == 0) { main.Close(); Application.ExitThread(); return; }
                var (delay, act) = steps.Dequeue();
                var t = new System.Windows.Forms.Timer { Interval = delay };
                t.Tick += (s, e) => { t.Dispose(); act(); Next(); };
                t.Start();
            }

            // 연결/첫 하트비트가 끝날 때까지 기다린 뒤부터 센다.
            steps.Enqueue((6000, () => { counter.AssignHandle(list.Handle); counter.Reset(); events = 0; }));
            steps.Enqueue((seconds * 1000, () =>
            {
                Console.WriteLine($"{seconds}s idle: PeersChanged={events} {counter}");
                ok &= counter.Erase == 0 && counter.DrawItem == 0;
            }));
            // 한 피어의 안 읽은 쪽지 수만 바뀜 → 그 줄만 다시 그려야 한다.
            steps.Enqueue((100, () => { counter.Reset(); B.Unread = 3; core.Peers.Touch(false); }));
            steps.Enqueue((1500, () =>
            {
                Console.WriteLine($"one row changed: {counter}");
                ok &= counter.DrawItem == 1;
                Snap("1_unread_badge");
            }));
            steps.Enqueue((100, () => panel.Select(B)));
            steps.Enqueue((800, () => Snap("2_selected")));
            steps.Enqueue((100, () => search.Text = "zzzz"));
            steps.Enqueue((800, () => Snap("3_no_match")));
            steps.Enqueue((100, () => search.Text = ""));
            steps.Enqueue((800, () => Snap("4_cleared")));
            // 목록 폭이 바뀌면 오른쪽 배지가 새 위치에 다시 그려져야 한다.
            steps.Enqueue((100, () => split.SplitterDistance += Theme.S(90)));
            steps.Enqueue((800, () => Snap("5_wider")));
            Next();
            Application.Run();

            counter.ReleaseHandle();
            try { bp.StandardInput.WriteLine("quit"); bp.WaitForExit(5000); } catch { }
            try { if (!bp.HasExited) bp.Kill(); } catch { }
            core.Shutdown();
            Console.WriteLine(ok ? "PAINT OK" : "PAINT FAIL");
            return ok ? 0 : 1;
        }
    }
}
