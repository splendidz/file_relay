using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FileRelay.UI;

namespace FileRelay.Tests
{
    /// <summary>
    /// README 스크린샷 — 가상의 동료 PC 들(헤드리스 노드)을 띄워 실제로 연결·전송·쪽지를 주고받으며 화면을 캡처한다.
    ///   FileRelay.Tests readme &lt;작업폴더&gt; &lt;출력폴더&gt; &lt;en|ko&gt; [light|dark]
    /// 피어 주소는 PC 이름(BOB-PC 등)으로 보이고 실제 연결은 127.0.0.1 로 돌린다. 내 IP·PC 이름도 예시 값으로 찍는다.
    /// 캡처한 창들의 화면 위치는 &lt;출력폴더&gt;\layout.json 에 남긴다 (tools/readme_images.py 가 합성할 때 쓴다).
    /// 작업폴더는 비어 있거나 이전 실행이 만든 폴더(.frdemo 표시)여야 한다 — 안에 데모 파일을 만든다.
    /// </summary>
    static class ReadmeShots
    {
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

        const int PortA = 45300;
        const string FakeIp = "192.168.0.10";
        static bool _ko;
        static string _out;
        static MainForm _main;
        static AppCore _core;
        static readonly List<(int Delay, Action Act)> Steps = new List<(int, Action)>();
        static readonly List<string> Layout = new List<string>();

        static string X(string en, string ko) => _ko ? ko : en;

        /// <summary>BOB-PC:45301 같은 주소를 127.0.0.1:45301 로 보낸다 (ApiClient 는 모든 요청을 WebRequest.Create 로 만든다).</summary>
        sealed class Loopback : IWebRequestCreate
        {
            public WebRequest Create(Uri uri) => WebRequest.CreateDefault(new UriBuilder(uri) { Host = "127.0.0.1" }.Uri);
        }

        sealed class Node
        {
            public string Name, Host, Root;
            public int Port;
            public Process Proc;
            public Peer Peer;

            public void Send(string line)
            {
                Proc.StandardInput.WriteLine(line);
                Proc.StandardInput.Flush();
            }

            public void Note(string color, string text) => Send("note " + color + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));
        }

        public static int Run(string work, string outDir, string lang, string theme)
        {
            // FileRelay.exe 는 매니페스트에 dpiAware 가 있다 — 같은 배율로 그려야 실제 화면과 같다 (고배율 화면이면 선명한 큰 캡처).
            SetProcessDPIAware();
            float scale;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = Math.Max(1f, g.DpiX / 96f);
            int S(int v) => (int)Math.Round(v * scale);
            _ko = lang == "ko";
            work = Path.GetFullPath(work);
            _out = Path.GetFullPath(outDir);
            if (Directory.Exists(work))
            {
                if (Directory.EnumerateFileSystemEntries(work).Any() && !File.Exists(Path.Combine(work, ".frdemo")))
                {
                    Console.WriteLine("작업폴더가 비어 있지 않습니다: " + work);
                    return 2;
                }
                Directory.Delete(work, true);
            }
            Directory.CreateDirectory(work);
            File.WriteAllText(Path.Combine(work, ".frdemo"), "FileRelay README demo — safe to delete");
            File.SetAttributes(Path.Combine(work, ".frdemo"), FileAttributes.Hidden);
            Directory.CreateDirectory(_out);
            string nodes = Path.Combine(work, ".nodes");
            Directory.CreateDirectory(nodes);
            File.SetAttributes(nodes, FileAttributes.Hidden | FileAttributes.Directory);

            // ─── 데모 데이터 ───────────────────────────────────────────────
            string me = X("Alice Kim", "김지은");
            var bob = new Node { Name = X("Bob Miller", "박민수"), Host = X("BOB-PC", "PARK-PC"), Port = PortA + 1 };
            var carol = new Node { Name = X("Carol Jones", "이서연"), Host = X("CAROL-LAPTOP", "LEE-LAPTOP"), Port = PortA + 2 };
            var dave = new Node { Name = X("Dave Wilson", "최준호"), Host = "DEV-WS03", Port = PortA + 3 };
            const int OfflinePort = PortA + 9;
            string project = X("Website Redesign", "웹사이트 리뉴얼");
            string design = X("Design Team", "디자인팀");

            // 박민수(Bob) 의 공유: 디자인팀 / 배포 파일 / 사진
            string bShare = Path.Combine(nodes, "B_design"), bRel = Path.Combine(nodes, "B_releases"), bPhoto = Path.Combine(nodes, "B_photos");
            string rp = Path.Combine(bShare, project);
            MakeFile(Path.Combine(rp, X("Brand guide 2026.pdf", "브랜드 가이드 2026.pdf")), 8_806_210, Ago(5, 10, 12));
            MakeFile(Path.Combine(rp, X("Client feedback.docx", "고객 피드백.docx")), 48_310, Ago(0, 11, 40));
            MakeFile(Path.Combine(rp, X("Homepage v3.psd", "메인 시안 v3.psd")), 153_402_880, Ago(0, 10, 5));
            MakeFile(Path.Combine(rp, X("Landing hero.png", "랜딩 히어로.png")), 3_351_904, Ago(0, 9, 52));
            MakeFile(Path.Combine(rp, X("Launch schedule.xlsx", "런칭 일정표.xlsx")), 96_512, Ago(3, 16, 20));
            MakeFile(Path.Combine(rp, X("Product teaser.mp4", "제품 티저 영상.mp4")), 190_812_160, Ago(1, 18, 33));
            MakeFile(Path.Combine(rp, X("Review deck.pptx", "리뷰 발표 자료.pptx")), 13_204_480, Ago(0, 10, 47));
            MakeFile(Path.Combine(rp, X("Icon set.zip", "아이콘 세트.zip")), 25_116_672, Ago(2, 14, 8));
            MakeFile(Path.Combine(rp, "assets", "logo.svg"), 12_288, Ago(4, 9, 30));
            MakeFile(Path.Combine(rp, "exports", "home_1440.png"), 2_048_000, Ago(0, 9, 40));
            Touch(Path.Combine(rp, "assets"), Ago(4, 9, 30));
            Touch(Path.Combine(rp, "exports"), Ago(0, 9, 40));
            Touch(rp, Ago(0, 11, 40));
            foreach (var (d, days) in new[] { (X("Logos", "로고"), 12), (X("Templates", "서식"), 30), (X("Photo shoot", "촬영 원본"), 9) })
            {
                MakeFile(Path.Combine(bShare, d, "_index.txt"), 1_024, Ago(days, 15, 0));
                Touch(Path.Combine(bShare, d), Ago(days, 15, 0));
            }
            MakeFile(Path.Combine(bRel, "FileRelay-1.4.2.zip"), 412_000, Ago(1, 17, 40));
            MakeFile(Path.Combine(bPhoto, "IMG_0412.jpg"), 4_120_000, Ago(6, 12, 0));

            Program.WriteConfig(bob.Root = Path.Combine(nodes, "B"), bob.Name, bob.Port, new List<ShareConfig>
            {
                new ShareConfig { Name = design, Path = bShare, Access = AccessLevel.Read, Description = X("Mockups, logos and templates", "시안, 로고, 서식"),
                                  PeerAccess = new Dictionary<string, AccessLevel>(StringComparer.OrdinalIgnoreCase) { [me] = AccessLevel.Write } },
                new ShareConfig { Name = X("Releases", "배포 파일"), Path = bRel, Access = AccessLevel.Read },
                new ShareConfig { Name = X("Photos", "사진"), Path = bPhoto, Access = AccessLevel.Read },
            });
            Program.WriteConfig(carol.Root = Path.Combine(nodes, "C"), carol.Name, carol.Port, new List<ShareConfig>());
            Program.WriteConfig(dave.Root = Path.Combine(nodes, "D"), dave.Name, dave.Port, new List<ShareConfig>());

            // 내 PC (이 프로세스): 작업 폴더 + 공유 2개
            string local = Path.Combine(work, project);
            MakeFile(Path.Combine(local, X("Brand guide 2026.pdf", "브랜드 가이드 2026.pdf")), 8_806_210, Ago(5, 10, 12));
            MakeFile(Path.Combine(local, X("Client feedback.docx", "고객 피드백.docx")), 41_216, Ago(2, 15, 3));
            MakeFile(Path.Combine(local, X("Launch schedule.xlsx", "런칭 일정표.xlsx")), 96_512, Ago(0, 13, 10));   // 크기 같고 내 쪽이 최신 → 비교에서 파랑
            MakeFile(Path.Combine(local, X("Copy draft.docx", "카피 초안.docx")), 22_016, Ago(0, 13, 30));
            MakeFile(Path.Combine(local, X("Meeting notes.txt", "회의록.txt")), 3_210, Ago(1, 16, 45));
            MakeFile(Path.Combine(local, "assets", "logo.svg"), 12_288, Ago(4, 9, 30));
            Touch(Path.Combine(local, "assets"), Ago(4, 9, 30));
            string team = Path.Combine(work, X("Team Shared", "팀 공용")), drop = Path.Combine(work, X("Inbox", "받은 파일"));
            MakeFile(Path.Combine(team, X("Weekly report.docx", "주간 보고.docx")), 64_000, Ago(3, 17, 0));
            Directory.CreateDirectory(drop);

            string aRoot = Path.Combine(nodes, "A");
            Program.WriteConfig(aRoot, me, PortA, new List<ShareConfig>
            {
                new ShareConfig
                {
                    Name = X("Team Shared", "팀 공용"), Path = team, Access = AccessLevel.Read,
                    Description = X("Weekly reports and shared templates", "주간 보고와 공용 서식"),
                    PeerAccess = new Dictionary<string, AccessLevel>(StringComparer.OrdinalIgnoreCase)
                    {
                        [bob.Name] = AccessLevel.Full,
                        [carol.Name] = AccessLevel.Write,
                        ["MEETING-PC"] = AccessLevel.None,
                    },
                },
                new ShareConfig { Name = X("Inbox", "받은 파일"), Path = drop, Access = AccessLevel.Write, Description = X("Drop files for me here", "저에게 줄 파일은 여기에") },
            }, c =>
            {
                c.Language = lang;
                c.Theme = theme;
                c.NoteSound = false;
                c.DownloadFolder = local;
                c.DefaultConflictAction = "ask";
            });
            Directory.CreateDirectory(Path.Combine(aRoot, "data"));
            File.WriteAllText(Path.Combine(aRoot, "data", "state.json"),
                $"{{ \"window.bounds\": [0, 0, {S(1280)}, {S(780)}], \"lastLocal\": {JsonStr(local)} }}",
                Encoding.UTF8);

            foreach (var n in new[] { bob, carol, dave })
            {
                WebRequest.RegisterPrefix($"http://{n.Host.ToLowerInvariant()}:{n.Port}/", new Loopback());
                n.Proc = StartNode(n.Root);
            }
            WebRequest.RegisterPrefix($"http://meeting-pc:{OfflinePort}/", new Loopback());

            // ─── 노드 A (UI) ───────────────────────────────────────────────
            AppPaths.Init(aRoot);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var ui = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(ui);
            var core = _core = AppCore.Create(ui);
            core.LoadConfig();
            L.Init(core.Config.Language);
            Theme.Init(core.Config.EffectiveUiFont, core.Config.UiFontSize, core.Config.Theme);
            ToolStripManager.Renderer = new DarkRenderer();
            // 내 IP 는 예시 값으로 (NetUtil 이 30초 캐시하는 값을 바꿔 둔다).
            typeof(NetUtil).GetField("_primary", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, FakeIp);
            typeof(NetUtil).GetField("_primaryAt", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, DateTime.UtcNow.AddDays(1));
            core.Start();
            foreach (var n in new[] { bob, carol, dave })
            {
                var r = core.PeerSvc.AddPeer(n.Host, n.Port);
                Console.WriteLine($"add {n.Host}: {(r.Ok ? "ok" : r.Message)}");
                n.Peer = r.Peer;
            }
            core.Peers.Add(new Peer
            {
                Id = "5be0c1d2a3f44e1c9d7a6b8f0e2c4a61", Name = X("Meeting Room", "회의실 PC"), Host = "MEETING-PC", Port = OfflinePort,
                State = PeerState.Paired, AddedAt = DateTime.Now.AddDays(-40), LastSeen = DateTime.Now.AddDays(-2).AddHours(-3),
            });
            carol.Send("status away");
            dave.Send("status busy");

            // 지난 쪽지 (쪽지함에 보일 기록 — 모두 읽음)
            void Old(Node n, bool incoming, string color, string text, DateTime t, DateTime readAt) =>
                core.Notes.Add(new Note { Incoming = incoming, PeerId = n.Peer.Id, PeerName = n.Peer.DisplayName, Color = color, Text = text, Time = t, ReadAt = readAt });
            Old(carol, true, "blue", X("Thanks for the templates!", "서식 보내 주셔서 감사해요!"), Ago(3, 15, 22), Ago(3, 15, 30));
            Old(bob, false, "yellow", X("Could you send me the latest logo files?", "최신 로고 파일 좀 보내 주실 수 있을까요?"), Ago(1, 16, 10), Ago(1, 16, 12));
            Old(dave, true, "green", X("Build 1.4.2 is up in Releases. Please don't use 1.4.1 — it has the export bug.", "1.4.2 빌드를 '배포 파일'에 올렸습니다. 1.4.1 은 내보내기 버그가 있으니 쓰지 말아 주세요."), Ago(1, 17, 42), Ago(1, 17, 45));
            Old(dave, false, "yellow", X("Got it, thanks!", "확인했습니다, 감사합니다!"), Ago(1, 17, 46), Ago(1, 17, 50));

            _main = new MainForm();
            T F<T>(string name) => (T)typeof(MainForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_main);
            void Call(string name, params object[] args) => typeof(MainForm).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public).Invoke(_main, args);
            FilePane Remote() => F<FilePane>("_remote");
            FilePane Local() => F<FilePane>("_local");
            // '종류' 열은 Windows 셸이 주는 이름(OS 언어, 설치된 프로그램에 따라 다름)이라 숨긴다.
            foreach (var pane in new[] { Local(), Remote() })
            {
                var cols = pane.List.Columns;
                cols[0].Width = S(250);
                cols[1].Width = S(82);
                cols[2].Width = 0;
                cols[3].Width = S(136);
            }
            _main.Show();
            _main.Activate();
            void SelectRemote(params string[] names)
            {
                var l = Remote().List;
                l.Focus();
                l.SelectedIndices.Clear();
                bool first = true;
                foreach (var nm in names)
                {
                    int i = l.Data.FindIndex(it => it.Name == nm);
                    if (i < 0) { Console.WriteLine("없음: " + nm); continue; }
                    if (first) l.SelectRow(i); else l.SelectedIndices.Add(i);
                    first = false;
                }
            }
            // 메인 창을 찍기 전에: 방화벽 경고(테스트 exe 라 규칙이 없다)를 숨기고, 내 PC 이름을 예시 값으로.
            void Stage()
            {
                F<ToolStripStatusLabel>("_stFirewall").Visible = false;
                var head = typeof(FilePane).GetField("_head", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Local());
                var sub = typeof(HeaderBar).GetField("_sub", BindingFlags.NonPublic | BindingFlags.Instance);
                sub.SetValue(head, ((string)sub.GetValue(head)).Replace(Environment.MachineName, X("ALICE-PC", "JIEUN-PC")));
                ((Control)head).Invalidate();
            }
            NoteWindow NoteFrom(Node n) => Application.OpenForms.OfType<NoteWindow>().FirstOrDefault(w => w.Note.Incoming && w.Note.PeerId == n.Peer.Id);
            Note sent = null;

            // 피어 선택 → 디자인팀 › 웹사이트 리뉴얼
            Add(2500, () => F<PeerPanel>("_peers").Select(bob.Peer));
            Add(1500, () => Local().Navigate(local));
            Add(300, () => Remote().Navigate(design + "\\" + project));
            // 폴더 비교
            Add(1500, () => Call("ToggleCompare"));
            Add(1200, () => { Remote().List.Focus(); Remote().List.SelectedIndices.Clear(); Stage(); Snap(_main, "compare"); });
            Add(200, () => Call("ToggleCompare"));
            // 내려받기 (속도 제한을 걸어 진행 중인 모습을 찍는다)
            Add(2500, () =>
            {
                core.SetSpeedLimit(12 * 1024);
                SelectRemote(X("Homepage v3.psd", "메인 시안 v3.psd"), X("Product teaser.mp4", "제품 티저 영상.mp4"),
                             X("Review deck.pptx", "리뷰 발표 자료.pptx"), X("Icon set.zip", "아이콘 세트.zip"));
                Call("TransferFrom", Remote());
            });
            Add(9000, () => { Remote().List.Focus(); Stage(); Snap(_main, "main"); });
            Add(200, () => core.SetSpeedLimit(0));
            // 쪽지: 박민수·이서연이 보낸 쪽지가 화면 오른쪽 위에 뜬다
            Add(5000, () => bob.Note("yellow", X(
                "Hi Alice! The final homepage mockups are in Design Team › Website Redesign.\nCould you take a look before the 3 PM review?",
                "지은님, 메인 시안 최종본을 '디자인팀 › 웹사이트 리뉴얼'에 올려 두었어요.\n3시 리뷰 전에 한 번 봐 주실 수 있을까요?")));
            Add(1500, () => carol.Note("green", X("Lunch at 12:30? The new ramen place downstairs!", "점심 12시 반에 같이 드실래요? 1층에 새로 생긴 라멘집이요!")));
            Add(2500, () =>
            {
                _main.Activate();
                Stage();
                Snap(_main, "notes_bg");
                Snap(NoteFrom(bob), "note_bob", false);
                Snap(NoteFrom(carol), "note_carol", false);
            });
            // 답장 쓰기 (받은 쪽지 옆에 열린다)
            Add(300, () =>
            {
                NoteManager.Compose(bob.Peer, NoteFrom(bob).Note);
                var w = Application.OpenForms.OfType<ComposeWindow>().First();
                var body = (RichTextBox)typeof(ComposeWindow).GetField("_body", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(w);
                body.Text = X("On it — I'll leave comments in the feedback doc by 2:30.", "네, 지금 볼게요! 2시 반까지 피드백 문서에 코멘트 남겨 둘게요.");
                body.SelectionStart = body.TextLength;
            });
            Add(1200, () =>
            {
                Snap(NoteFrom(bob), "reply_note", false);
                Snap(Application.OpenForms.OfType<ComposeWindow>().First(), "compose");
            });
            // 쪽지함 — 답장한 쪽지는 읽음, 이서연의 쪽지는 아직 새 쪽지 (쪽지 창을 닫으면 읽음 처리되므로 닫기 전에 찍는다)
            Add(300, () =>
            {
                foreach (var f in Application.OpenForms.OfType<ComposeWindow>().ToList()) f.Close();
                core.MarkNoteRead(NoteFrom(bob).Note);
                NotesForm.ShowInbox(NoteFrom(bob).Note);
            });
            Add(1200, () => SnapForm<NotesForm>("inbox", close: true));
            // 보낸 쪽지 + 읽음 확인
            Add(300, () => Task.Run(() => sent = core.SendNote(bob.Peer, X("Looks great! One thing — could the logo be a little bigger on mobile?", "좋아요! 하나만요 — 모바일에서 로고를 조금 더 크게 해 주실 수 있을까요?"), "blue", "")));
            Add(1500, () => bob.Send("readall"));
            Add(1500, () =>
            {
                NoteManager.CloseAll();
                if (sent != null) NoteManager.ShowIncoming(sent);
            });
            Add(1000, () => { SnapForm<NoteWindow>("note_sent"); NoteManager.CloseAll(); });
            // 피어 메뉴 (오른쪽 클릭)
            Add(300, () =>
            {
                // 노드들이 모두 이 PC 에서 돌아서 상대 PC 이름이 이 PC 이름으로 온다 — 메뉴 머리줄에 보이므로 예시 값으로.
                bob.Peer.Machine = bob.Host;
                var m = new ContextMenuStrip();
                typeof(MainForm).GetMethod("BuildPeerMenu", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_main, new object[] { bob.Peer, m });
                DarkRenderer.Polish(m);
                m.Show(_main, new Point(S(150), S(170)));
                _menu = m;
            });
            Add(800, () => { Snap(_menu, "peer_menu", false); _menu.Close(); });
            // 공유 폴더 편집 (피어별 권한)
            Add(300, () => new ShareEditDialog(core.Config.Shares[0], core.Config.Shares).Show(_main));
            Add(800, () => Application.OpenForms.OfType<ShareEditDialog>().First().ActiveControl = null);   // 폴더 칸 전체 선택 강조 없이
            Add(300, () => SnapForm<ShareEditDialog>("share_edit", close: true));
            // 같은 이름 파일 (충돌 처리)
            Add(300, () =>
            {
                var req = new ConflictRequest
                {
                    Item = new TransferItem
                    {
                        Dir = TDir.Download, PeerName = bob.Peer.DisplayName, LocalPath = Path.Combine(local, X("Client feedback.docx", "고객 피드백.docx")),
                        Size = 48_310, SrcTime = Ago(0, 11, 40),
                    },
                    TargetSize = 41_216, TargetTime = Ago(2, 15, 3), CanOverwrite = true, CanResume = false,
                };
                new ConflictDialog(req).Show(_main);
            });
            Add(1000, () => SnapForm<ConflictDialog>("conflict", close: true));
            Add(300, () =>
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                Layout.Add($"\"screen\": [{wa.X}, {wa.Y}, {wa.Width}, {wa.Height}]");
                File.WriteAllText(Path.Combine(_out, "layout.json"), "{\n  " + string.Join(",\n  ", Layout) + "\n}\n");
                Console.WriteLine("DONE");
                _main.ForceExit();
            });

            int idx = 0;
            var timer = new System.Windows.Forms.Timer { Interval = Steps[0].Delay };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                if (idx >= Steps.Count) return;
                var (delay, act) = Steps[idx++];
                try { act(); } catch (Exception ex) { Console.WriteLine($"step {idx} 오류: {ex.GetBaseException()}"); }
                if (idx < Steps.Count) { timer.Interval = Math.Max(10, Steps[idx].Delay); timer.Start(); }
            };
            timer.Start();
            Application.Run(new ApplicationContext());
            core.Shutdown();
            foreach (var n in new[] { bob, carol, dave })
            {
                try { n.Send("quit"); n.Proc.WaitForExit(5000); } catch { }
                try { if (!n.Proc.HasExited) n.Proc.Kill(); } catch { }
            }
            return 0;
        }

        static ContextMenuStrip _menu;

        static void Add(int delay, Action a) => Steps.Add((delay, a));

        static DateTime Ago(int days, int hour, int minute) => DateTime.Today.AddDays(-days).AddHours(hour).AddMinutes(minute);

        static string JsonStr(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        static void MakeFile(string path, long size, DateTime time)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var fs = new FileStream(path, FileMode.Create)) fs.SetLength(size);
            File.SetLastWriteTime(path, time);
        }

        static void Touch(string dir, DateTime time)
        {
            Directory.CreateDirectory(dir);
            Directory.SetLastWriteTime(dir, time);
        }

        static Process StartNode(string root)
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(typeof(Program).Assembly.Location, $"node \"{root}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                },
            };
            var ready = new ManualResetEventSlim();
            string name = Path.GetFileName(root);
            p.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                Console.WriteLine(name + ": " + e.Data);
                if (e.Data.StartsWith("READY")) ready.Set();
            };
            p.Start();
            p.BeginOutputReadLine();
            if (!ready.Wait(15000)) Console.WriteLine(name + ": 시작 안 됨");
            return p;
        }

        static void SnapForm<T>(string name, bool close = false) where T : Form
        {
            var f = Application.OpenForms.OfType<T>().LastOrDefault();
            if (f == null) { Console.WriteLine("form not found: " + typeof(T).Name); return; }
            Snap(f, name);
            if (close) f.Close();
        }

        /// <summary>창을 PrintWindow 로 찍고, 보이는 테두리(DWM 확장 프레임) 안쪽만 잘라 저장한다.</summary>
        static void Snap(Control c, string name, bool refresh = true)
        {
            if (c == null) { Console.WriteLine("no window: " + name); return; }
            if (refresh) c.Refresh();
            Application.DoEvents();
            var h = c.Handle;
            GetWindowRect(h, out var wr);
            var fr = wr;
            if (DwmGetWindowAttribute(h, 9, out var ext, Marshal.SizeOf(typeof(RECT))) == 0 && ext.R > ext.L) fr = ext;   // DWMWA_EXTENDED_FRAME_BOUNDS
            int w = wr.R - wr.L, hh = wr.B - wr.T;
            if (w <= 0 || hh <= 0) { Console.WriteLine("empty window: " + name); return; }
            using (var full = new Bitmap(w, hh, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(full))
                {
                    var dc = g.GetHdc();
                    PrintWindow(h, dc, 2);
                    g.ReleaseHdc(dc);
                }
                var crop = Rectangle.Intersect(new Rectangle(fr.L - wr.L, fr.T - wr.T, fr.R - fr.L, fr.B - fr.T), new Rectangle(0, 0, w, hh));
                if (crop.Width <= 0 || crop.Height <= 0) crop = new Rectangle(0, 0, w, hh);
                using (var bmp = full.Clone(crop, PixelFormat.Format32bppArgb))
                    bmp.Save(Path.Combine(_out, name + ".png"), ImageFormat.Png);
                Layout.Add($"\"{name}\": [{wr.L + crop.X}, {wr.T + crop.Y}, {crop.Width}, {crop.Height}]");
            }
            Console.WriteLine("shot " + name);
        }
    }
}
