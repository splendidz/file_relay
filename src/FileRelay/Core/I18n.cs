using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FileRelay
{
    /// <summary>
    /// 다국어 문자열 (한국어 / 영어).
    ///  · 언어 파일은 exe 에 내장 (Lang\ko.json, Lang\en.json — 평평한 "키": "문장").
    ///  · exe 옆 lang\xx.json 이 있으면 그 내용으로 덮어쓴다 (번역 수정·새 언어 추가용).
    ///  · 현재 언어에 키가 없으면 한국어로, 그래도 없으면 키 자체를 보여준다.
    ///  · 언어는 시작할 때 한 번 정한다 (바꾸면 다시 시작).
    /// 사용: using static FileRelay.L;  →  T("menu.file"), T("peer.added", name), TN("count.files", n)
    /// </summary>
    public static class L
    {
        public static readonly string[] Supported = { "ko", "en" };

        static Dictionary<string, string> _cur = new Dictionary<string, string>(StringComparer.Ordinal);
        static Dictionary<string, string> _ko = new Dictionary<string, string>(StringComparer.Ordinal);
        static readonly HashSet<string> Missing = new HashSet<string>(StringComparer.Ordinal);
        static readonly object Lock = new object();

        public static string Lang { get; private set; } = "ko";
        public static bool En => Lang == "en";
        public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("ko-KR");

        static L() { Load("ko"); }

        /// <summary>설정 값(auto/ko/en) → 실제 언어.</summary>
        public static string Resolve(string setting)
        {
            if (string.Equals(setting, "ko", StringComparison.OrdinalIgnoreCase)) return "ko";
            if (string.Equals(setting, "en", StringComparison.OrdinalIgnoreCase)) return "en";
            return SystemLang();
        }

        /// <summary>Windows 표시 언어가 한국어면 ko, 아니면 en.</summary>
        public static string SystemLang() =>
            string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "ko", StringComparison.OrdinalIgnoreCase) ? "ko" : "en";

        public static void Init(string setting) => Load(Resolve(setting));

        static void Load(string lang)
        {
            lock (Lock)
            {
                _ko = Read("ko");
                _cur = lang == "ko" ? _ko : Read(lang);
                Lang = lang;
                Culture = CultureInfo.GetCultureInfo(lang == "ko" ? "ko-KR" : "en-US");
                Missing.Clear();
            }
        }

        static Dictionary<string, string> Read(string lang)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                using (var s = typeof(L).Assembly.GetManifestResourceStream("FileRelay.Lang." + lang + ".json"))
                    if (s != null)
                        using (var r = new StreamReader(s, Encoding.UTF8))
                            Merge(d, r.ReadToEnd());
            }
            catch { }
            try
            {
                if (!string.IsNullOrEmpty(AppPaths.ExeDir))
                {
                    string ext = Path.Combine(AppPaths.ExeDir, "lang", lang + ".json");
                    if (File.Exists(ext)) Merge(d, File.ReadAllText(ext, Encoding.UTF8));
                }
            }
            catch { }
            return d;
        }

        static void Merge(Dictionary<string, string> d, string json)
        {
            var o = Json.ParseObject(json);
            foreach (var kv in o)
                if (!kv.Key.StartsWith("_") && kv.Value is string v) d[kv.Key] = v;
        }

        /// <summary>테스트용: 내장 언어 파일의 키 목록.</summary>
        public static ICollection<string> KeysOf(string lang) => Read(lang).Keys;

        public static bool Has(string key) => _cur.ContainsKey(key) || _ko.ContainsKey(key);

        public static string T(string key)
        {
            if (_cur.TryGetValue(key, out var v)) return v;
            lock (Lock)
            {
                if (Missing.Add(key)) System.Diagnostics.Debug.WriteLine("i18n: missing key " + key);
            }
            return _ko.TryGetValue(key, out v) ? v : key;
        }

        public static string T(string key, params object[] args)
        {
            string f = T(key);
            if (args == null || args.Length == 0) return f;
            try { return string.Format(Culture, f, args); }
            catch (FormatException) { return f; }
        }

        /// <summary>
        /// 개수에 따라 단수/복수 문장 ({0} = n). 영어는 key.one / key.other, 한국어는 key.other (없으면 key).
        /// </summary>
        public static string TN(string key, long n, params object[] rest)
        {
            string k = En && n == 1 && Has(key + ".one") ? key + ".one" : Has(key + ".other") ? key + ".other" : key;
            var args = new object[1 + (rest?.Length ?? 0)];
            args[0] = n;
            if (rest != null) Array.Copy(rest, 0, args, 1, rest.Length);
            return T(k, args);
        }
    }

    /// <summary>로그 분류 이름 (현재 언어).</summary>
    public static class Cat
    {
        public static string Sys => L.T("cat.system");
        public static string Peer => L.T("cat.peer");
        public static string Xfer => L.T("cat.transfer");
        public static string Share => L.T("cat.share");
        public static string Note => L.T("cat.note");
        public static string Server => L.T("cat.server");
        public static string Local => L.T("cat.local");
        public static string Remote => L.T("cat.remote");
        public static string Search => L.T("cat.search");
    }
}
