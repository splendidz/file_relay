using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using static FileRelay.L;

namespace FileRelay
{
    /// <summary>사람이 읽는 형식(크기, 속도, 시간)으로 바꾸는 도우미.</summary>
    public static class Fmt
    {
        static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

        public static string Size(long n)
        {
            if (n < 0) return "";
            if (n < 1024) return n.ToString(CultureInfo.InvariantCulture) + " B";
            double d = n;
            int i = 0;
            while (d >= 1024 && i < Units.Length - 1) { d /= 1024; i++; }
            string f = d >= 100 ? "0" : d >= 10 ? "0.0" : "0.00";
            return d.ToString(f, CultureInfo.InvariantCulture) + " " + Units[i];
        }

        public static string Speed(double bytesPerSec) => bytesPerSec <= 0 ? "" : Size((long)bytesPerSec) + "/s";

        public static string Duration(double seconds)
        {
            if (seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) return "";
            long s = (long)Math.Ceiling(seconds);
            if (s < 60) return T("fmt.dur.s", s);
            if (s < 3600) return T("fmt.dur.ms", s / 60, s % 60);
            if (s < 86400) return T("fmt.dur.hm", s / 3600, s % 3600 / 60);
            return T("fmt.dur.dh", s / 86400, s % 86400 / 3600);
        }

        public static string Time(DateTime t) =>
            t <= DateTime.MinValue.AddDays(1) ? "" : t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        public static string TimeSec(DateTime t) =>
            t <= DateTime.MinValue.AddDays(1) ? "" : t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        /// <summary>쪽지 머리글용: 오늘이면 "오후 3:12" / "3:12 PM", 아니면 날짜를 붙인다.</summary>
        public static string NoteTime(DateTime t)
        {
            string hm = En
                ? t.ToString("h:mm tt", CultureInfo.GetCultureInfo("en-US"))
                : (t.Hour < 12 ? T("fmt.am") : T("fmt.pm")) + " " + (t.Hour % 12 == 0 ? 12 : t.Hour % 12) + ":" + t.Minute.ToString("00");
            if (t.Date == DateTime.Today) return hm;
            if (t.Date == DateTime.Today.AddDays(-1)) return T("fmt.yesterdayAt", hm);
            if (t.Year == DateTime.Today.Year)
                return En ? t.ToString("MMM d", CultureInfo.GetCultureInfo("en-US")) + ", " + hm : T("fmt.monthDay", t.Month, t.Day) + " " + hm;
            return t.ToString("yyyy-MM-dd ", CultureInfo.InvariantCulture) + hm;
        }

        public static string Relative(DateTime t)
        {
            if (t <= DateTime.MinValue.AddDays(1)) return T("fmt.rel.never");
            var d = DateTime.Now - t;
            if (d.TotalSeconds < 60) return T("fmt.rel.now");
            if (d.TotalMinutes < 60) return TN("fmt.rel.min", (int)d.TotalMinutes);
            if (d.TotalHours < 24) return TN("fmt.rel.hour", (int)d.TotalHours);
            if (d.TotalDays < 2) return T("fmt.rel.yesterday");
            if (d.TotalDays < 30) return TN("fmt.rel.day", (int)d.TotalDays);
            return t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        public static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

        static string AccessKey(AccessLevel a) =>
            a == AccessLevel.Read ? "read" : a == AccessLevel.Write ? "write" : a == AccessLevel.Full ? "full" : "none";

        public static string Access(AccessLevel a) => T("access." + AccessKey(a));

        public static string AccessLong(AccessLevel a) => T("access." + AccessKey(a) + ".long");

        public static string Presence(Presence p)
        {
            switch (p)
            {
                case FileRelay.Presence.Online: return T("presence.online");
                case FileRelay.Presence.Away: return T("presence.away");
                case FileRelay.Presence.Busy: return T("presence.busy");
                default: return T("presence.offline");
            }
        }

        public static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
            return s.Substring(0, Math.Max(0, max - 1)) + "…";
        }

        public static string OneLine(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var t = s.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Trim();
            return Truncate(t, max);
        }
    }

    /// <summary>탐색기와 같은 '자연 정렬' (file2 &lt; file10).</summary>
    public sealed class NaturalComparer : IComparer<string>
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int StrCmpLogicalW(string a, string b);

        public static readonly NaturalComparer Instance = new NaturalComparer();

        public int Compare(string a, string b)
        {
            try { return StrCmpLogicalW(a ?? "", b ?? ""); }
            catch { return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase); }
        }
    }

    public static class Num
    {
        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
        public static long Clamp(long v, long lo, long hi) => v < lo ? lo : v > hi ? hi : v;
        public static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    }

    public static class TimeUtil
    {
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long ToUnixMs(DateTime t)
        {
            if (t <= DateTime.MinValue.AddDays(1)) return 0;
            return (long)(t.ToUniversalTime() - Epoch).TotalMilliseconds;
        }

        public static DateTime FromUnixMs(long ms)
        {
            if (ms <= 0) return DateTime.MinValue;
            try { return Epoch.AddMilliseconds(ms).ToLocalTime(); }
            catch { return DateTime.MinValue; }
        }

        public static DateTime ParseIso(string s)
        {
            if (string.IsNullOrEmpty(s)) return DateTime.MinValue;
            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
                ? (t.Kind == DateTimeKind.Utc ? t.ToLocalTime() : t)
                : DateTime.MinValue;
        }
    }

    /// <summary>원격(공유 내부) 경로와 로컬 경로 도우미. 원격 경로는 '\' 구분, 앞뒤 구분자 없음.</summary>
    public static class PathUtil
    {
        public const string PartExt = ".frpart";

        static readonly char[] Seps = { '\\', '/' };
        static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        public static string[] Segments(string p) =>
            (p ?? "").Split(Seps, StringSplitOptions.RemoveEmptyEntries);

        public static string Normalize(string p) => string.Join("\\", Segments(p));

        public static string Join(string a, string b)
        {
            a = Normalize(a);
            b = Normalize(b);
            if (a.Length == 0) return b;
            if (b.Length == 0) return a;
            return a + "\\" + b;
        }

        public static string Parent(string p)
        {
            var s = Segments(p);
            return s.Length <= 1 ? "" : string.Join("\\", s, 0, s.Length - 1);
        }

        public static string Name(string p)
        {
            var s = Segments(p);
            return s.Length == 0 ? "" : s[s.Length - 1];
        }

        /// <summary>파일/폴더 이름으로 쓸 수 있는지 검사. 문제가 있으면 이유를, 없으면 null.</summary>
        public static string ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return T("name.empty");
            if (name != name.Trim()) return T("name.spaces");
            if (name == "." || name == "..") return T("name.invalid");
            if (name.EndsWith(".")) return T("name.dot");
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return T("name.chars");
            string stem = name.Split('.')[0];
            if (Reserved.Contains(stem)) return T("name.reserved");
            if (name.Length > 240) return T("name.long");
            return null;
        }

        /// <summary>"이름 (2).ext" 형식으로 겹치지 않는 경로를 만든다 (탐색기 방식).</summary>
        public static string UniqueName(string name, Func<string, bool> exists)
        {
            if (!exists(name)) return name;
            string stem = Path.GetFileNameWithoutExtension(name);
            string ext = Path.GetExtension(name);
            for (int i = 2; i < 10000; i++)
            {
                string cand = $"{stem} ({i}){ext}";
                if (!exists(cand)) return cand;
            }
            return stem + " (" + Guid.NewGuid().ToString("N").Substring(0, 6) + ")" + ext;
        }

        public static string UniqueLocalPath(string path)
        {
            string dir = Path.GetDirectoryName(path) ?? "";
            string name = UniqueName(Path.GetFileName(path), n => File.Exists(Path.Combine(dir, n)) || Directory.Exists(Path.Combine(dir, n)));
            return Path.Combine(dir, name);
        }

        public static bool SameLocalPath(string a, string b)
        {
            if (a == null || b == null) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>a 가 b 와 같거나 b 의 하위 경로인지.</summary>
        public static bool IsUnder(string path, string root)
        {
            try
            {
                string p = Path.GetFullPath(path).TrimEnd('\\') + "\\";
                string r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
                return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    public static class Hash
    {
        public static string Short(string s, int len = 12)
        {
            using (var sha = SHA256.Create())
            {
                var b = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                var sb = new StringBuilder();
                foreach (var x in b) sb.Append(x.ToString("x2"));
                return sb.ToString().Substring(0, Math.Min(len, sb.Length));
            }
        }
    }

    public static class Ext
    {
        public static bool Eq(this string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        public static void SafeInvoke(this Action a)
        {
            try { a?.Invoke(); }
            catch (Exception ex) { Log.Error(Cat.Sys, T("log.eventError", ex.Message)); }
        }

        public static List<T> ToListSafe<T>(this IEnumerable<T> e) => e == null ? new List<T>() : e.ToList();

        public static T FirstOr<T>(this IEnumerable<T> e, T def)
        {
            foreach (var x in e) return x;
            return def;
        }
    }
}
