using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace FileRelay.Tests
{
    /// <summary>
    /// 헤드리스 end-to-end 테스트.
    ///   FileRelay.Tests e2e &lt;작업폴더&gt;        — 노드 B 를 별도 프로세스로 띄우고, 이 프로세스(노드 A)에서 시나리오 실행
    ///   FileRelay.Tests node &lt;데이터폴더&gt;      — 헤드리스 노드 (stdin 에 quit 이 오면 종료)
    /// </summary>
    static class Program
    {
        static int _pass, _fail;

        [STAThread]
        static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            if (args.Length >= 3 && args[0] == "ui") return UiShots.Run(args[1], args[2]);
            if (args.Length >= 2 && args[0] == "node") return RunNode(args[1]);
            if (args.Length >= 2 && args[0] == "e2e") return RunE2E(args[1]);
            Console.WriteLine("usage: e2e <workdir> | node <datadir>");
            return 2;
        }

        // ─── 노드 ──────────────────────────────────────────────────────────
        static int RunNode(string dataDir)
        {
            AppPaths.Init(dataDir);
            var core = AppCore.Create(null);
            core.LoadConfig();
            core.Start();
            Console.WriteLine("READY " + core.MyId);
            Console.Out.Flush();
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                // 부모의 콘솔 입력 코드페이지가 UTF-8 이면 첫 줄 앞에 BOM 이 붙어 온다.
                line = line.Trim('﻿', ' ', '\r');
                if (line == "quit") break;
                if (line == "dump")
                {
                    try { core.Peers.SaveNow(); core.Notes.SaveNow(); }
                    catch (Exception ex) { Console.Error.WriteLine(ex); }
                    Console.WriteLine("DUMPED");
                    Console.Out.Flush();
                }
            }
            core.Shutdown();
            return 0;
        }

        static void WriteConfig(string root, string name, int port, List<ShareConfig> shares, Action<AppConfig> tweak = null)
        {
            Directory.CreateDirectory(root);
            var c = new AppConfig
            {
                UserName = name,
                Port = port,
                Shares = shares,
                HeartbeatSeconds = 3,
                AutoAwayMinutes = 0,
                ConfirmBeforeTransfer = false,
                RetryCount = 1,
                DefaultConflictAction = "overwrite",
                RemoteDeleteToRecycleBin = false,
            };
            tweak?.Invoke(c);
            c.Save(Path.Combine(root, "FileRelay.jsonc"));
        }

        // ─── 시나리오 ──────────────────────────────────────────────────────
        static int RunE2E(string work)
        {
            work = Path.GetFullPath(work);
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            int portA = 45311, portB = 45312;

            // 노드 B: 공유 3개 (읽기 전용 / 쓰기 / 모든 권한)
            string bRoot = Path.Combine(work, "B");
            string bPub = Path.Combine(work, "B_share_pub"), bDrop = Path.Combine(work, "B_share_drop"), bFull = Path.Combine(work, "B_share_full");
            foreach (var d in new[] { bPub, bDrop, bFull }) Directory.CreateDirectory(d);
            File.WriteAllBytes(Path.Combine(bPub, "big.bin"), Rand(8 * 1024 * 1024 + 123, 1));
            File.SetLastWriteTime(Path.Combine(bPub, "big.bin"), new DateTime(2024, 5, 6, 7, 8, 9));
            File.WriteAllBytes(Path.Combine(bPub, "huge.bin"), Rand(40 * 1024 * 1024, 2));
            File.WriteAllText(Path.Combine(bPub, "한글 파일.txt"), "안녕하세요 FileRelay", Encoding.UTF8);
            Directory.CreateDirectory(Path.Combine(bPub, "sub", "deeper"));
            File.WriteAllText(Path.Combine(bPub, "sub", "deeper", "x.txt"), "x");
            WriteConfig(bRoot, "노드B", portB, new List<ShareConfig>
            {
                new ShareConfig { Name = "pub", Path = bPub, Access = AccessLevel.Read },
                new ShareConfig { Name = "drop", Path = bDrop, Access = AccessLevel.Write },
                new ShareConfig { Name = "full", Path = bFull, Access = AccessLevel.Full },
                new ShareConfig { Name = "secret", Path = bPub, Access = AccessLevel.None },
            });

            var exe = typeof(Program).Assembly.Location;
            var bp = Process.Start(new ProcessStartInfo(exe, $"node \"{bRoot}\"")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            string ready = bp.StandardOutput.ReadLine();
            Console.WriteLine("B: " + ready);

            // 노드 A (이 프로세스)
            string aRoot = Path.Combine(work, "A");
            WriteConfig(aRoot, "노드A", portA, new List<ShareConfig>());
            AppPaths.Init(aRoot);
            var A = AppCore.Create(null);
            A.LoadConfig();
            A.Start();
            string local = Path.Combine(work, "A_local");
            Directory.CreateDirectory(local);

            try
            {
                Scenarios(A, bp, local, bPub, bDrop, bFull, portB);
            }
            catch (Exception ex)
            {
                Fail("예외: " + ex);
            }
            finally
            {
                try { bp.StandardInput.WriteLine("quit"); bp.WaitForExit(5000); } catch { }
                try { if (!bp.HasExited) bp.Kill(); } catch { }
                A.Shutdown();
            }
            Console.WriteLine($"\n==== PASS {_pass}  FAIL {_fail} ====");
            return _fail == 0 ? 0 : 1;
        }

        static void Scenarios(AppCore A, Process bp, string local, string bPub, string bDrop, string bFull, int portB)
        {
            // 1. 페어링
            var r = A.PeerSvc.AddPeer("127.0.0.1", portB);
            Check(r.Ok && !r.Pending, "피어 추가 (자동 수락)", r.Message);
            var B = r.Peer;
            Check(B != null && B.Name == "노드B" && B.State == PeerState.Paired, "상대 이름/상태 표시", B?.Name + " " + B?.State);
            var ping = ApiClient.Call(B, "POST", "/api/ping");
            Check(ping.B("known"), "상대도 나를 피어로 등록함 (상호 등록)");
            Check(ping.I("shares") == 3, "보이는 공유 수 3 (none 제외)", ping.I("shares").ToString());

            // 1-1. LAN 검색 (UDP)
            var found = Discovery.Search(A, 1500);
            Check(found.Any(f => f.Id == B.Id && f.Name == "노드B" && f.Port == portB), "LAN 검색으로 상대 찾기", string.Join(",", found.Select(f => f.Name + "@" + f.Host + ":" + f.Port)));

            // 2. 공유 목록
            var rfs = new RemoteFs(B);
            var shares = rfs.List("", out _, out _);
            Check(shares.Count == 3 && shares.Any(s => s.Name == "pub" && s.Access == AccessLevel.Read) && shares.Any(s => s.Name == "full" && s.Access == AccessLevel.Full),
                "공유 목록과 권한", string.Join(",", shares.Select(s => s.Name + ":" + s.Access)));
            var pubList = rfs.List("pub", out var pubPerms, out _);
            Check(pubList.Any(i => i.Name == "한글 파일.txt") && !pubPerms.Write, "목록 + 한글 이름 + 읽기 전용 권한");

            // 3. 받기 (8MB) + 타임스탬프
            var big = pubList.First(i => i.Name == "big.bin");
            Run(A, JobBuilder.Downloads(B, new[] { big }, local, null, CancellationToken.None));
            string got = Path.Combine(local, "big.bin");
            Check(File.Exists(got) && Sha(got) == Sha(Path.Combine(bPub, "big.bin")), "받기 무결성 (SHA256)");
            Check(File.GetLastWriteTime(got) == new DateTime(2024, 5, 6, 7, 8, 9), "수정 시각 유지", File.GetLastWriteTime(got).ToString());

            // 4. 이어받기: 앞 3MB 만 있는 .frpart
            File.Delete(got);
            var part = got + PathUtil.PartExt;
            using (var src = File.OpenRead(Path.Combine(bPub, "big.bin")))
            using (var dst = File.Create(part)) { var buf = new byte[3 * 1024 * 1024]; src.Read(buf, 0, buf.Length); dst.Write(buf, 0, buf.Length); }
            var items = Run(A, JobBuilder.Downloads(B, new[] { big }, local, null, CancellationToken.None));
            Check(items[0].Status == TStatus.Done && items[0].Note == "이어받기" && Sha(got) == Sha(Path.Combine(bPub, "big.bin")), "이어받기 (.frpart 에서 계속)", items[0].Note);

            // 5. 같은 파일 다시 받기 — 정책별
            SetPolicy(A, "skip");
            items = Run(A, JobBuilder.Downloads(B, new[] { big }, local, null, CancellationToken.None));
            Check(items[0].Status == TStatus.Skipped, "충돌: 건너뛰기");
            SetPolicy(A, "rename");
            items = Run(A, JobBuilder.Downloads(B, new[] { big }, local, null, CancellationToken.None));
            Check(items[0].Status == TStatus.Done && File.Exists(Path.Combine(local, "big (2).bin")), "충돌: 이름 바꿔 저장", items[0].LocalPath);
            SetPolicy(A, "overwriteIfNewer");
            items = Run(A, JobBuilder.Downloads(B, new[] { big }, local, null, CancellationToken.None));
            Check(items[0].Status == TStatus.Skipped, "충돌: 원본이 최신일 때만 덮어쓰기 → 같으므로 건너뜀");
            SetPolicy(A, "overwrite");

            // 6. 올리기 — 읽기 전용 공유는 거부
            string up = Path.Combine(local, "up.bin");
            File.WriteAllBytes(up, Rand(5 * 1024 * 1024 + 7, 3));
            items = Run(A, JobBuilder.Uploads(B, new[] { up }, "pub", null, CancellationToken.None));
            Check(items[0].Status == TStatus.Failed && items[0].Error.Contains("쓰기 권한"), "읽기 전용 공유에 올리기 거부", items[0].Error);

            // 7. 쓰기 전용(drop): 새 파일은 OK, 덮어쓰기는 거부, 이름 바꿔 저장은 OK
            items = Run(A, JobBuilder.Uploads(B, new[] { up }, "drop", null, CancellationToken.None));
            Check(items[0].Status == TStatus.Done && Sha(Path.Combine(bDrop, "up.bin")) == Sha(up), "쓰기 공유에 올리기 + 무결성");
            items = Run(A, JobBuilder.Uploads(B, new[] { up }, "drop", null, CancellationToken.None));
            Check(items[0].Status == TStatus.Failed && items[0].Error.Contains("권한"), "쓰기 공유: 덮어쓰기 거부", items[0].Error);
            SetPolicy(A, "rename");
            items = Run(A, JobBuilder.Uploads(B, new[] { up }, "drop", null, CancellationToken.None));
            Check(items[0].Status == TStatus.Done && File.Exists(Path.Combine(bDrop, "up (2).bin")), "쓰기 공유: 이름 바꿔 올리기");
            SetPolicy(A, "overwrite");

            // 8. 모든 권한(full): 이어 보내기
            string fullTarget = Path.Combine(bFull, "up.bin");
            using (var src = File.OpenRead(up))
            using (var dst = File.Create(fullTarget + PathUtil.PartExt)) { var buf = new byte[2 * 1024 * 1024]; src.Read(buf, 0, buf.Length); dst.Write(buf, 0, buf.Length); }
            items = Run(A, JobBuilder.Uploads(B, new[] { up }, "full", null, CancellationToken.None));
            Check(items[0].Status == TStatus.Done && items[0].Note == "이어 보내기" && Sha(fullTarget) == Sha(up) && !File.Exists(fullTarget + PathUtil.PartExt), "이어 보내기 (.frpart)", items[0].Note);
            items = Run(A, JobBuilder.Uploads(B, new[] { up }, "full", null, CancellationToken.None));
            Check(items[0].Status == TStatus.Done, "모든 권한: 덮어쓰기");

            // 9. 폴더 올리기 (하위 폴더 + 빈 폴더)
            string tree = Path.Combine(local, "tree");
            for (int i = 0; i < 30; i++)
            {
                string d = Path.Combine(tree, "d" + (i % 3), "e" + (i % 2));
                Directory.CreateDirectory(d);
                File.WriteAllBytes(Path.Combine(d, $"f{i}.dat"), Rand(1000 + i * 37, 100 + i));
            }
            Directory.CreateDirectory(Path.Combine(tree, "empty"));
            File.WriteAllBytes(Path.Combine(tree, "zero.dat"), new byte[0]);
            items = Run(A, JobBuilder.Uploads(B, new[] { tree }, "full", null, CancellationToken.None));
            Check(items.All(i => i.Status == TStatus.Done) && items.Count == 31, "폴더 올리기 (31개 파일)", items.Count(i => i.Status == TStatus.Done) + "/" + items.Count);
            Check(Directory.Exists(Path.Combine(bFull, "tree", "empty")) && File.Exists(Path.Combine(bFull, "tree", "zero.dat")) && new FileInfo(Path.Combine(bFull, "tree", "zero.dat")).Length == 0, "빈 폴더/빈 파일");
            Check(SameTree(tree, Path.Combine(bFull, "tree")), "폴더 내용 일치");

            // 10. 폴더 받기
            string dl2 = Path.Combine(local, "dl2");
            Directory.CreateDirectory(dl2);
            var treeItem = rfs.List("full", out _, out _).First(i => i.Name == "tree");
            items = Run(A, JobBuilder.Downloads(B, new[] { treeItem }, dl2, null, CancellationToken.None));
            Check(items.All(i => i.Status == TStatus.Done) && SameTree(tree, Path.Combine(dl2, "tree")), "폴더 받기 + 내용 일치");

            // 11. 원격 작업 (새 폴더/이름 바꾸기/삭제) + 권한
            rfs.MakeDir("full", "새 폴더");
            Check(Directory.Exists(Path.Combine(bFull, "새 폴더")), "원격 새 폴더 (full)");
            var nf = rfs.List("full", out _, out _).First(i => i.Name == "새 폴더");
            rfs.Rename(nf, "바뀐 이름");
            Check(Directory.Exists(Path.Combine(bFull, "바뀐 이름")), "원격 이름 바꾸기 (full)");
            rfs.Delete(rfs.List("full", out _, out _).Where(i => i.Name == "바뀐 이름").ToList());
            Check(!Directory.Exists(Path.Combine(bFull, "바뀐 이름")), "원격 삭제 (full)");
            ExpectApiError(() => rfs.Rename(rfs.List("drop", out _, out _).First(i => i.Name == "up.bin"), "x.bin"), 403, "쓰기 공유: 이름 바꾸기 거부");
            ExpectApiError(() => rfs.Delete(rfs.List("drop", out _, out _).Where(i => i.Name == "up.bin").ToList()), 403, "쓰기 공유: 삭제 거부");
            ExpectApiError(() => rfs.MakeDir("pub", "nope"), 403, "읽기 전용: 새 폴더 거부");
            ExpectApiError(() => rfs.List("secret", out _, out _), 404, "권한 none 공유는 보이지 않음");

            // 12. 경로 탈출
            ExpectApiError(() => ApiClient.Call(B, "GET", "/api/list" + ApiClient.Q("share", "pub", "path", "..\\..")), 400, "경로 탈출(..) 거부");
            ExpectApiError(() => ApiClient.Call(B, "GET", "/api/stat" + ApiClient.Q("share", "pub", "path", "C:\\Windows")), 400, "절대 경로 거부");
            ExpectApiError(() => ApiClient.Call(B, "GET", "/api/stat" + ApiClient.Q("share", "pub", "path", "big.bin:stream")), 400, "스트림(:) 거부");
            try { var x = ApiServerResolve("D:\\", "a\\b"); Check(x == "D:\\a\\b", "드라이브 루트 공유 경로", x); } catch (Exception ex) { Fail("드라이브 루트 공유 경로: " + ex.Message); }

            // 13. 인증 없는 접근 거부
            try
            {
                var req = (HttpWebRequest)WebRequest.Create($"http://127.0.0.1:{portB}/api/shares");
                req.Proxy = null;
                req.Headers["X-FR-Id"] = "stranger";
                req.GetResponse().Close();
                Fail("모르는 상대의 공유 목록 요청 거부");
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse hr && (int)hr.StatusCode == 403) { Pass("모르는 상대의 공유 목록 요청 거부"); }

            // 14. 쪽지
            var note = A.SendNote(B, "테스트 쪽지입니다\n둘째 줄", "blue", null);
            Check(note != null && A.Notes.All().Any(n => n.Id == note.Id && !n.Incoming), "쪽지 보내기 + 보낸 쪽지함 기록");

            // 15. 속도 제한 + 일시 정지/재개
            A.SetSpeedLimit(8 * 1024);
            var huge = rfs.List("pub", out _, out _).First(i => i.Name == "huge.bin");
            var hj = JobBuilder.Downloads(B, new[] { huge }, local, null, CancellationToken.None);
            A.Transfers.Enqueue(hj);
            Thread.Sleep(1500);
            A.Transfers.SetPaused(true);
            Thread.Sleep(800);
            long partLen = File.Exists(Path.Combine(local, "huge.bin" + PathUtil.PartExt)) ? new FileInfo(Path.Combine(local, "huge.bin" + PathUtil.PartExt)).Length : 0;
            Check(hj[0].Status == TStatus.Queued && partLen > 0 && partLen < huge.Size, "일시 정지 (임시 파일 유지)", $"{hj[0].Status} part={partLen}");
            double kbps = partLen / 1024.0 / 1.5;
            Check(kbps < 8 * 1024 * 1.6, "속도 제한 동작 (~8MB/s)", ((int)kbps) + " KB/s");
            A.SetSpeedLimit(0);
            A.Transfers.SetPaused(false);
            Wait(A, hj);
            Check(hj[0].Status == TStatus.Done && Sha(Path.Combine(local, "huge.bin")) == Sha(Path.Combine(bPub, "huge.bin")), "재개 후 완료 + 무결성", hj[0].StatusText);

            // 16. 취소 (임시 파일 삭제)
            File.Delete(Path.Combine(local, "huge.bin"));
            A.SetSpeedLimit(4 * 1024);
            hj = JobBuilder.Downloads(B, new[] { huge }, local, null, CancellationToken.None);
            A.Transfers.Enqueue(hj);
            Thread.Sleep(1200);
            A.Transfers.Cancel(hj);
            Wait(A, hj);
            Thread.Sleep(300);
            Check(hj[0].Status == TStatus.Canceled && !File.Exists(Path.Combine(local, "huge.bin" + PathUtil.PartExt)), "취소 (임시 파일 삭제)", hj[0].StatusText);
            A.SetSpeedLimit(0);

            // 17. 이름 변경이 상대에게 반영
            var cfg = A.Config.Clone();
            cfg.UserName = "노드A-새이름";
            A.ApplyConfig(cfg, true);
            Thread.Sleep(1500);
            bp.StandardInput.WriteLine("dump");
            bp.StandardOutput.ReadLine();
            string bPeers = File.ReadAllText(Path.Combine(Path.GetDirectoryName(bPub), "B", "data", "peers.json"));
            Check(bPeers.Contains("노드A-새이름"), "내 이름 변경이 상대 목록에 반영");
            string bNotes = File.ReadAllText(Path.Combine(Path.GetDirectoryName(bPub), "B", "data", "notes.json"));
            Check(bNotes.Contains("테스트 쪽지입니다"), "상대 쪽지함에 쪽지 저장");

            // 18. 삭제는 양방향
            A.PeerSvc.RemovePeer(B);
            Thread.Sleep(1500);
            bp.StandardInput.WriteLine("dump");
            bp.StandardOutput.ReadLine();
            bPeers = File.ReadAllText(Path.Combine(Path.GetDirectoryName(bPub), "B", "data", "peers.json"));
            Check(!bPeers.Contains("노드A"), "피어 삭제 시 상대 목록에서도 삭제", bPeers.Length.ToString());

            // 18-1. 데이터 폴더를 다른 PC 로 복사해도 ID 가 겹치지 않음
            string stDir = Path.Combine(local, "statecopy");
            Directory.CreateDirectory(stDir);
            string stFile = Path.Combine(stDir, "state.json");
            File.WriteAllText(stFile, "{\"instanceId\":\"aaaa\",\"instanceMachine\":\"other-pc\"}");
            var s1 = new UiState(); s1.Load(stFile);
            string id1 = s1.InstanceId;
            Check(id1 != "aaaa" && s1.IdRegenerated, "다른 PC 에서 복사된 state.json → 새 ID", id1);
            s1.SaveNow();
            var s2 = new UiState(); s2.Load(stFile);
            Check(s2.InstanceId == id1 && !s2.IdRegenerated, "같은 PC 에서는 ID 유지");
            File.WriteAllText(stFile, "{\"instanceId\":\"bbbb\"}");
            var s3 = new UiState(); s3.Load(stFile);
            Check(s3.InstanceId != "bbbb" && s3.IdRegenerated, "이전 버전 state.json → 새 ID");

            // 18-2. 기본 공유 이름 (드라이브 전체 공유)
            string dn = ShareConfig.DefaultNameFor("C:\\");
            Check(dn.StartsWith("C 드라이브") && PathUtil.ValidateName(dn) == null, "드라이브 전체 공유의 기본 이름", dn);
            Check(ShareConfig.DefaultNameFor("D:\\Work\\Proj\\") == "Proj", "폴더 공유의 기본 이름");

            // 19. JSONC 왕복 (주석 포함 파일 다시 읽기)
            string jsonc = A.Config.ToJsonc();
            var back = AppConfig.FromJson(Json.ParseObject(jsonc));
            Check(back.UserName == "노드A-새이름" && back.Port == A.Config.Port && jsonc.Contains("//"), "JSONC 저장/읽기 왕복");
            var lenient = Json.ParseObject("{ // c\n \"path\": \"D:\\Share\", /* b */ \"a\": [1,2,], }");
            Check(lenient.S("path") == "D:\\Share" && lenient.A("a").Count == 2, "JSONC 관대한 파싱 (주석, trailing comma, 역슬래시)");

            // 20. 다국어
            var koKeys = new HashSet<string>(L.KeysOf("ko"));
            var enKeys = new HashSet<string>(L.KeysOf("en"));
            var onlyKo = koKeys.Except(enKeys).ToList();
            var onlyEn = enKeys.Except(koKeys).ToList();
            Check(koKeys.Count > 500 && onlyKo.Count == 0 && onlyEn.Count == 0, "언어 파일 키 목록 일치 (ko/en)",
                $"ko={koKeys.Count} en={enKeys.Count} onlyKo={string.Join(",", onlyKo.Take(5))} onlyEn={string.Join(",", onlyEn.Take(5))}");
            try
            {
                L.Init("en");
                Check(L.T("common.cancel") == "Cancel" && L.TN("set.nDays", 1) == "1 day" && L.TN("set.nDays", 30) == "30 days", "영어: 문장과 단수/복수",
                    L.T("common.cancel") + " / " + L.TN("set.nDays", 1) + " / " + L.TN("set.nDays", 30));
                Check(ShareConfig.DefaultNameFor("C:\\").StartsWith("Drive C"), "영어: 드라이브 공유 기본 이름", ShareConfig.DefaultNameFor("C:\\"));
                Check(L.T("no.such.key") == "no.such.key", "없는 키는 키 그대로");
                // 상대(B)는 한국어 — 오류는 코드로 오고 내 언어로 보여야 한다.
                try { rfs.MakeDir("pub", "nope"); Fail("영어: 상대 오류 메시지 — 오류가 나야 함"); }
                catch (ApiException ex) { Check(!ex.Message.Any(ch => ch >= '\uAC00' && ch <= '\uD7A3') && ex.Message.Length > 0, "영어: 상대 오류를 내 언어로 표시", ex.Message); }
            }
            finally { L.Init("ko"); }
            Check(L.T("common.cancel") == "취소" && L.TN("set.nDays", 1) == "1일", "한국어로 되돌림");
        }

        static string ApiServerResolve(string root, string rel) => ApiServer.ResolveUnder(root, rel);

        static void SetPolicy(AppCore a, string p)
        {
            var c = a.Config.Clone();
            c.DefaultConflictAction = p;
            a.ApplyConfig(c, false);
        }

        static List<TransferItem> Run(AppCore a, List<TransferItem> jobs)
        {
            a.Transfers.Enqueue(jobs);
            Wait(a, jobs);
            return jobs;
        }

        static void Wait(AppCore a, List<TransferItem> jobs)
        {
            var sw = Stopwatch.StartNew();
            while (jobs.Any(j => !j.IsFinished) && sw.Elapsed.TotalSeconds < 120) Thread.Sleep(50);
            foreach (var j in jobs.Where(j => j.Status == TStatus.Failed)) Console.WriteLine("    (실패 사유) " + j.Name + ": " + j.Error);
        }

        static void ExpectApiError(Action a, int status, string what)
        {
            try { a(); Fail(what + " — 오류가 나야 하는데 성공함"); }
            catch (ApiException ex) { Check(ex.Status == status, what, $"{ex.Status} {ex.Code} {ex.Message}"); }
            catch (Exception ex) { Fail(what + " — " + ex.GetType().Name + ": " + ex.Message); }
        }

        static bool SameTree(string a, string b)
        {
            var fa = Directory.GetFiles(a, "*", SearchOption.AllDirectories).Select(f => f.Substring(a.Length)).OrderBy(x => x).ToList();
            var fb = Directory.GetFiles(b, "*", SearchOption.AllDirectories).Select(f => f.Substring(b.Length)).OrderBy(x => x).ToList();
            if (!fa.SequenceEqual(fb)) { Console.WriteLine($"    files differ: {fa.Count} vs {fb.Count}"); return false; }
            foreach (var f in fa) if (Sha(a + f) != Sha(b + f)) { Console.WriteLine("    content differs: " + f); return false; }
            var da = Directory.GetDirectories(a, "*", SearchOption.AllDirectories).Select(f => f.Substring(a.Length)).OrderBy(x => x);
            var db = Directory.GetDirectories(b, "*", SearchOption.AllDirectories).Select(f => f.Substring(b.Length)).OrderBy(x => x);
            return da.SequenceEqual(db);
        }

        static byte[] Rand(int n, int seed)
        {
            var b = new byte[n];
            new Random(seed).NextBytes(b);
            return b;
        }

        static string Sha(string path)
        {
            using (var s = SHA256.Create())
            using (var f = File.OpenRead(path))
                return BitConverter.ToString(s.ComputeHash(f));
        }

        static void Check(bool ok, string what, string detail = "")
        {
            if (ok) Pass(what);
            else Fail(what + (string.IsNullOrEmpty(detail) ? "" : "  [" + detail + "]"));
        }

        static void Pass(string what) { _pass++; Console.WriteLine("  PASS  " + what); }
        static void Fail(string what) { _fail++; Console.WriteLine("  FAIL  " + what); }
    }
}
