using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using FileRelay.UI;

using static FileRelay.L;
namespace FileRelay
{
    static class Program
    {
        static Mutex _mutex;
        static MainForm _main;
        static volatile bool _exiting;
        static DateTime _lastErrorBox = DateTime.MinValue;

        /// <summary>
        /// 인자:
        ///   --data &lt;폴더&gt;   설정/데이터 폴더 지정 (한 PC 에서 여러 개 실행할 때)
        ///   --tray            창 없이 트레이에서 시작
        ///   --wait-pid &lt;n&gt;  (다시 시작용) 이전 프로세스가 끝날 때까지 기다림
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            // exe 하나만 배포해도(.config 없이) 긴 경로(260자 초과)를 쓸 수 있게 — 다른 IO 보다 먼저.
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);

            string data = null;
            bool tray = false;
            int waitPid = 0;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if ((a == "--data" || a == "-d") && i + 1 < args.Length) data = args[++i];
                else if (a == "--tray" || a == "--minimized") tray = true;
                else if (a == "--wait-pid" && i + 1 < args.Length) int.TryParse(args[++i], out waitPid);
            }
            if (waitPid > 0)
            {
                try { Process.GetProcessById(waitPid).WaitForExit(20000); } catch { }
            }

            AppPaths.Init(data);
            string key = Hash.Short(AppPaths.ConfigFile.ToLowerInvariant(), 16);
            _mutex = new Mutex(true, "Local\\FileRelay_" + key, out bool created);
            var activate = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\FileRelay_Activate_" + key);
            if (!created)
            {
                // 이미 실행 중 — 기존 창을 앞으로.
                activate.Set();
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => HandleError(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => HandleError(e.ExceptionObject as Exception);

            var ui = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(ui);
            var core = AppCore.Create(ui);
            L.Init("auto");                     // 설정을 읽기 전 — 설정 파일 오류 메시지는 Windows 언어로
            core.LoadConfig();
            L.Init(core.Config.Language);
            Theme.Init(core.Config.EffectiveUiFont, core.Config.UiFontSize, core.Config.Theme);
            ToolStripManager.Renderer = new DarkRenderer();

            if (string.IsNullOrWhiteSpace(core.Config.UserName))
            {
                var cfg = core.Config;
                FirstRunDialog.Draft draft = null;
                while (true)
                {
                    using (var d = new FirstRunDialog(cfg, draft))
                    {
                        var r = d.ShowDialog();
                        if (r == DialogResult.Retry)
                        {
                            // 언어를 바꿨다 — 그 언어와 글꼴로 다시 연다.
                            cfg = d.Result;
                            draft = d.Next;
                            L.Init(cfg.Language);
                            Theme.Init(cfg.EffectiveUiFont, cfg.UiFontSize, cfg.Theme);
                            continue;
                        }
                        if (r != DialogResult.OK) return;
                        core.ApplyFirstRun(d.Result);
                        break;
                    }
                }
            }

            core.Start();
            _main = new MainForm();
            var h = _main.Handle;   // 숨긴 채 시작해도 Invoke 가 되도록 핸들을 만든다.

            var listener = new Thread(() =>
            {
                while (!_exiting)
                {
                    try
                    {
                        if (!activate.WaitOne(1000)) continue;
                        if (_exiting) break;
                        _main.BeginInvoke(new Action(() => _main.ShowMain()));
                    }
                    catch { break; }
                }
            }) { IsBackground = true, Name = "activate" };
            listener.Start();

            if (!(tray || core.Config.StartMinimized)) _main.Show();
            if (!string.IsNullOrEmpty(core.ConfigError))
                _main.BeginInvoke(new Action(() =>
                {
                    _main.ShowMain();
                    MessageDialog.Error(_main, T("app.err.configTitle"), T("app.err.configText"), core.ConfigError);
                }));

            Application.Run(new ApplicationContext());

            _exiting = true;
            core.Shutdown();
            try { _mutex.ReleaseMutex(); } catch { }
        }

        public static void Restart()
        {
            var args = new List<string>();
            if (!string.IsNullOrEmpty(AppPaths.CustomDataArg)) args.Add("--data \"" + AppPaths.RootDir + "\"");
            args.Add("--wait-pid " + Process.GetCurrentProcess().Id);
            try
            {
                Process.Start(new ProcessStartInfo(AppPaths.ExePath, string.Join(" ", args)) { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                MessageDialog.Error(_main, T("app.err.restart"), ex.Message);
                return;
            }
            _main?.ForceExit();
        }

        static void HandleError(Exception ex)
        {
            if (ex == null) return;
            Log.Error(Cat.Sys, T("app.log.unexpected", ex));
            if ((DateTime.Now - _lastErrorBox).TotalSeconds < 5 || _exiting) return;
            _lastErrorBox = DateTime.Now;
            try
            {
                MessageDialog.Error(_main != null && _main.Visible ? _main : null, T("app.err.unexpected"),
                    T("app.err.unexpectedText", ex.Message), ex.ToString());
            }
            catch { }
        }
    }
}
