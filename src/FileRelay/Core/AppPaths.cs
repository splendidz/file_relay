using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace FileRelay
{
    /// <summary>
    /// 설정/데이터 파일 위치.
    ///  · 실행 파일 폴더에 쓸 수 있으면 '포터블' — FileRelay.jsonc 와 FileRelay_data\ 를 exe 옆에 둔다.
    ///  · 쓸 수 없으면(Program Files 등) %APPDATA%\FileRelay 를 쓴다.
    ///  · --data &lt;폴더&gt; 로 따로 지정할 수 있다 (한 PC 에서 여러 개 실행/테스트용).
    /// </summary>
    public static class AppPaths
    {
        public static string ExePath { get; private set; } = "";
        public static string ExeDir { get; private set; } = "";
        public static string RootDir { get; private set; } = "";
        public static string ConfigFile { get; private set; } = "";
        public static string DataDir { get; private set; } = "";
        public static string CustomDataArg { get; private set; } = "";

        public static string PeersFile => Path.Combine(DataDir, "peers.json");
        public static string NotesFile => Path.Combine(DataDir, "notes.json");
        public static string StateFile => Path.Combine(DataDir, "state.json");
        public static string LogDir => Path.Combine(DataDir, "logs");
        public static string DefaultTempDir => Path.Combine(Path.GetTempPath(), "FileRelay");

        public static void Init(string overrideDir)
        {
            ExePath = Assembly.GetEntryAssembly()?.Location ?? System.Windows.Forms.Application.ExecutablePath;
            ExeDir = Path.GetDirectoryName(ExePath) ?? Environment.CurrentDirectory;

            if (!string.IsNullOrWhiteSpace(overrideDir))
            {
                CustomDataArg = overrideDir;
                RootDir = Path.GetFullPath(overrideDir);
                ConfigFile = Path.Combine(RootDir, "FileRelay.jsonc");
                DataDir = Path.Combine(RootDir, "data");
            }
            else if (File.Exists(Path.Combine(ExeDir, "FileRelay.jsonc")) || IsWritable(ExeDir))
            {
                RootDir = ExeDir;
                ConfigFile = Path.Combine(ExeDir, "FileRelay.jsonc");
                DataDir = Path.Combine(ExeDir, "FileRelay_data");
            }
            else
            {
                RootDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileRelay");
                ConfigFile = Path.Combine(RootDir, "FileRelay.jsonc");
                DataDir = Path.Combine(RootDir, "data");
            }
            Directory.CreateDirectory(RootDir);
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(LogDir);
        }

        static bool IsWritable(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".fr_write_test_" + Guid.NewGuid().ToString("N").Substring(0, 6));
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                // Program Files 아래는 가상화(VirtualStore)될 수 있으니 피한다.
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (PathUtil.IsUnder(dir, pf) || (!string.IsNullOrEmpty(pf86) && PathUtil.IsUnder(dir, pf86))) return false;
                return true;
            }
            catch { return false; }
        }
    }

    public static class KnownFolders
    {
        static readonly Guid DownloadsId = new Guid("374DE290-123F-4565-9164-39C4925E467B");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

        public static string Downloads
        {
            get
            {
                try
                {
                    if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var p) == 0)
                    {
                        string s = Marshal.PtrToStringUni(p);
                        Marshal.FreeCoTaskMem(p);
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }
                catch { }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }
        }

        public static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        public static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }
}
