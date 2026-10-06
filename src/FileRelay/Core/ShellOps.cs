using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace FileRelay
{
    /// <summary>탐색기 기능 (휴지통 삭제, 복사, 속성, 열기) 래퍼.</summary>
    public static class ShellOps
    {
        const uint FO_MOVE = 1, FO_COPY = 2, FO_DELETE = 3;
        const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040,
                     FOF_NOERRORUI = 0x0400, FOF_NOCONFIRMMKDIR = 0x0200, FOF_RENAMEONCOLLISION = 0x0008;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        static string Multi(IEnumerable<string> paths)
        {
            var sb = new StringBuilder();
            foreach (var p in paths) sb.Append(p).Append('\0');
            sb.Append('\0');
            return sb.ToString();
        }

        /// <summary>휴지통으로 보낸다. silent=true 면 어떤 창도 띄우지 않는다 (서버 스레드용).</summary>
        public static bool Recycle(IEnumerable<string> paths, IntPtr owner, bool silent, out bool aborted)
        {
            var op = new SHFILEOPSTRUCT
            {
                hwnd = owner,
                wFunc = FO_DELETE,
                pFrom = Multi(paths),
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | (silent ? FOF_SILENT | FOF_NOERRORUI : 0)),
            };
            int r = SHFileOperation(ref op);
            aborted = op.fAnyOperationsAborted;
            return r == 0 && !aborted;
        }

        /// <summary>영구 삭제 (휴지통 없이). 탐색기 진행 창 사용.</summary>
        public static bool DeletePermanent(IEnumerable<string> paths, IntPtr owner)
        {
            var op = new SHFILEOPSTRUCT
            {
                hwnd = owner,
                wFunc = FO_DELETE,
                pFrom = Multi(paths),
                fFlags = FOF_NOCONFIRMATION,
            };
            return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
        }

        /// <summary>탐색기 방식 복사/이동 (진행 창, 충돌 창 포함) — 로컬 → 로컬 붙여넣기용.</summary>
        public static bool CopyOrMove(IEnumerable<string> paths, string destDir, bool move, IntPtr owner)
        {
            var list = paths.ToList();
            bool samePlace = list.Count > 0 && list.All(p => PathUtil.SameLocalPath(Path.GetDirectoryName(p.TrimEnd('\\')), destDir));
            var op = new SHFILEOPSTRUCT
            {
                hwnd = owner,
                wFunc = move ? FO_MOVE : FO_COPY,
                pFrom = Multi(list),
                pTo = destDir + "\0\0",
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR | (samePlace && !move ? FOF_RENAMEONCOLLISION : 0)),
            };
            return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpVerb;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpParameters;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

        public static void ShowProperties(string path, IntPtr owner)
        {
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf(typeof(SHELLEXECUTEINFO)),
                fMask = 0x0000000C, // SEE_MASK_INVOKEIDLIST
                hwnd = owner,
                lpVerb = "properties",
                lpFile = path,
                nShow = 5,
            };
            ShellExecuteEx(ref info);
        }

        public static void Open(string path)
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        public static void OpenWith(string path)
        {
            Process.Start(new ProcessStartInfo("rundll32.exe", "shell32.dll,OpenAs_RunDLL " + path) { UseShellExecute = false });
        }

        public static void RevealInExplorer(string path)
        {
            if (File.Exists(path) || Directory.Exists(path) && !IsDriveRoot(path))
                Process.Start("explorer.exe", "/select,\"" + path + "\"");
            else if (Directory.Exists(path))
                Process.Start("explorer.exe", "\"" + path + "\"");
        }

        public static void OpenFolder(string path)
        {
            if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
        }

        static bool IsDriveRoot(string p) => p.TrimEnd('\\').Length == 2 && p[1] == ':';

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetDiskFreeSpaceEx(string dir, out ulong freeAvail, out ulong total, out ulong totalFree);

        /// <summary>폴더가 속한 볼륨의 사용 가능 공간 (-1 = 알 수 없음).</summary>
        public static long FreeSpace(string dir, out long total)
        {
            total = -1;
            try
            {
                string d = dir;
                while (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) d = Path.GetDirectoryName(d);
                if (string.IsNullOrEmpty(d)) return -1;
                if (!d.EndsWith("\\")) d += "\\";
                if (GetDiskFreeSpaceEx(d, out ulong free, out ulong tot, out _))
                {
                    total = (long)tot;
                    return (long)free;
                }
            }
            catch { }
            return -1;
        }

        public static long FreeSpace(string dir) => FreeSpace(dir, out _);
    }
}
