using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FileRelay
{
    public enum LogLevel { Debug, Info, Success, Warn, Error }

    public sealed class LogEntry
    {
        public long Seq;
        public DateTime Time;
        public LogLevel Level;
        public string Category;
        public string Text;
    }

    /// <summary>파일(일자별) + 메모리(최근 N개) 로그. 어느 스레드에서나 호출 가능.</summary>
    public static class Log
    {
        const int MaxMemory = 4000;
        static readonly object Lock = new object();
        static readonly LinkedList<LogEntry> Ring = new LinkedList<LogEntry>();
        static string _dir;
        static StreamWriter _writer;
        static DateTime _day;
        static long _seq;

        /// <summary>새 로그 (호출한 스레드에서 발생 — UI 는 직접 마샬링).</summary>
        public static event Action<LogEntry> Added;

        public static void Init(string dir, int retentionDays)
        {
            _dir = dir;
            try
            {
                Directory.CreateDirectory(dir);
                var limit = DateTime.Today.AddDays(-Math.Max(1, retentionDays));
                foreach (var f in Directory.GetFiles(dir, "FileRelay_*.log"))
                {
                    try { if (File.GetLastWriteTime(f) < limit) File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        public static void Debug(string cat, string text) => Write(LogLevel.Debug, cat, text);
        public static void Info(string cat, string text) => Write(LogLevel.Info, cat, text);
        public static void Success(string cat, string text) => Write(LogLevel.Success, cat, text);
        public static void Warn(string cat, string text) => Write(LogLevel.Warn, cat, text);
        public static void Error(string cat, string text) => Write(LogLevel.Error, cat, text);

        public static void Write(LogLevel level, string cat, string text)
        {
            var e = new LogEntry { Time = DateTime.Now, Level = level, Category = cat ?? "", Text = text ?? "" };
            lock (Lock)
            {
                e.Seq = ++_seq;
                if (level != LogLevel.Debug)
                {
                    Ring.AddLast(e);
                    while (Ring.Count > MaxMemory) Ring.RemoveFirst();
                }
                WriteFile(e);
            }
            if (level == LogLevel.Debug) return;
            try { Added?.Invoke(e); } catch { }
        }

        static void WriteFile(LogEntry e)
        {
            if (_dir == null) return;
            try
            {
                if (_writer == null || _day != e.Time.Date)
                {
                    _writer?.Dispose();
                    _day = e.Time.Date;
                    string path = Path.Combine(_dir, "FileRelay_" + _day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
                    var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
                }
                _writer.WriteLine($"{e.Time:HH:mm:ss.fff} [{LevelTag(e.Level)}] [{e.Category}] {e.Text}");
            }
            catch { }
        }

        public static string LevelTag(LogLevel l)
        {
            switch (l)
            {
                case LogLevel.Debug: return "DBG";
                case LogLevel.Success: return "OK ";
                case LogLevel.Warn: return "WRN";
                case LogLevel.Error: return "ERR";
                default: return "INF";
            }
        }

        public static LogEntry[] Snapshot()
        {
            lock (Lock)
            {
                var a = new LogEntry[Ring.Count];
                Ring.CopyTo(a, 0);
                return a;
            }
        }

        public static void Close()
        {
            lock (Lock)
            {
                try { _writer?.Dispose(); } catch { }
                _writer = null;
            }
        }
    }
}
