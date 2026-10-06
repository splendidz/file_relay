using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using static FileRelay.L;
namespace FileRelay
{
    /// <summary>JSON 객체 (키 순서 무관, 대소문자 구분).</summary>
    public sealed class JObj : Dictionary<string, object>
    {
        public JObj() : base(StringComparer.Ordinal) { }
    }

    /// <summary>
    /// 의존성 없는 JSON / JSONC 파서와 직렬화기.
    ///  · 주석(// 한 줄, /* 블록 */)과 trailing comma 를 허용한다 (설정 파일용).
    ///  · 알 수 없는 이스케이프(\S 등)는 그대로 둔다 — 사람이 "D:\Share" 처럼 써도 읽히게.
    ///  · 객체 → JObj, 배열 → List&lt;object&gt;, 정수 → long, 실수 → double.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            var r = new Reader(text ?? "");
            r.Ws();
            if (r.Eof) return null;
            var v = r.Value();
            r.Ws();
            if (!r.Eof) throw r.Err(T("json.trailing"));
            return v;
        }

        public static JObj ParseObject(string text)
        {
            return Parse(text) as JObj ?? throw new FormatException(T("json.notObject"));
        }

        sealed class Reader
        {
            readonly string _s;
            int _i;

            public Reader(string s) { _s = s; }
            public bool Eof => _i >= _s.Length;

            public FormatException Err(string msg)
            {
                int line = 1, col = 1;
                for (int k = 0; k < _i && k < _s.Length; k++)
                {
                    if (_s[k] == '\n') { line++; col = 1; } else col++;
                }
                return new FormatException(T("json.error", line, col, msg));
            }

            public void Ws()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\uFEFF') { _i++; continue; }
                    if (c == '/' && _i + 1 < _s.Length)
                    {
                        if (_s[_i + 1] == '/')
                        {
                            _i += 2;
                            while (_i < _s.Length && _s[_i] != '\n') _i++;
                            continue;
                        }
                        if (_s[_i + 1] == '*')
                        {
                            int e = _s.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                            if (e < 0) throw Err(T("json.unclosedComment"));
                            _i = e + 2;
                            continue;
                        }
                    }
                    break;
                }
            }

            public object Value()
            {
                if (Eof) throw Err(T("json.valueExpected"));
                char c = _s[_i];
                switch (c)
                {
                    case '{': return Obj();
                    case '[': return Arr();
                    case '"': return Str();
                    case 't': Word("true"); return true;
                    case 'f': Word("false"); return false;
                    case 'n': Word("null"); return null;
                }
                if (c == '-' || (c >= '0' && c <= '9')) return Num();
                throw Err(T("json.unexpected", c));
            }

            void Word(string w)
            {
                if (_i + w.Length > _s.Length || string.CompareOrdinal(_s, _i, w, 0, w.Length) != 0)
                    throw Err(T("json.expected", w));
                _i += w.Length;
            }

            JObj Obj()
            {
                var o = new JObj();
                _i++;
                while (true)
                {
                    Ws();
                    if (Eof) throw Err(T("json.objUnclosed"));
                    if (_s[_i] == '}') { _i++; return o; }
                    if (_s[_i] != '"') throw Err(T("json.keyQuote"));
                    string key = Str();
                    Ws();
                    if (Eof || _s[_i] != ':') throw Err(T("json.expected", ":"));
                    _i++;
                    Ws();
                    o[key] = Value();
                    Ws();
                    if (Eof) throw Err(T("json.objUnclosed"));
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == '}') { _i++; return o; }
                    throw Err(T("json.commaOrBrace"));
                }
            }

            List<object> Arr()
            {
                var a = new List<object>();
                _i++;
                while (true)
                {
                    Ws();
                    if (Eof) throw Err(T("json.arrUnclosed"));
                    if (_s[_i] == ']') { _i++; return a; }
                    a.Add(Value());
                    Ws();
                    if (Eof) throw Err(T("json.arrUnclosed"));
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == ']') { _i++; return a; }
                    throw Err(T("json.commaOrBracket"));
                }
            }

            string Str()
            {
                _i++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (Eof) throw Err(T("json.strUnclosed"));
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c == '\n') throw Err(T("json.strNewline"));
                    if (c != '\\') { sb.Append(c); continue; }
                    if (Eof) throw Err(T("json.strUnclosed"));
                    char e = _s[_i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > _s.Length) throw Err(T("json.badEscape"));
                            if (!int.TryParse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp))
                                throw Err(T("json.badEscape"));
                            sb.Append((char)cp);
                            _i += 4;
                            break;
                        default:
                            // "D:\Share" 처럼 역슬래시 하나로 쓴 경로를 관대하게 받아준다.
                            sb.Append('\\').Append(e);
                            break;
                    }
                }
            }

            object Num()
            {
                int st = _i;
                if (_s[_i] == '-') _i++;
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') _i++;
                    else break;
                }
                string t = _s.Substring(st, _i - st);
                if (t.IndexOfAny(new[] { '.', 'e', 'E' }) < 0 &&
                    long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l))
                    return l;
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    return d;
                throw Err(T("json.badNumber", t));
            }
        }

        // ─── 직렬화 ─────────────────────────────────────────────────────────
        public static string Serialize(object value, bool indent = false)
        {
            var sb = new StringBuilder();
            Write(sb, value, indent, 0);
            return sb.ToString();
        }

        public static string Quote(string s)
        {
            var sb = new StringBuilder();
            Quote(sb, s);
            return sb.ToString();
        }

        static void Quote(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        static void NewLine(StringBuilder sb, bool indent, int depth)
        {
            if (!indent) return;
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        static void Write(StringBuilder sb, object v, bool indent, int depth)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: Quote(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case DateTime dt: Quote(sb, dt.ToString("o", CultureInfo.InvariantCulture)); return;
                case Enum e: Quote(sb, e.ToString()); return;
                case double d:
                    sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "0" : d.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case float f:
                    sb.Append(float.IsNaN(f) || float.IsInfinity(f) ? "0" : f.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); return;
                case int _:
                case long _:
                case short _:
                case byte _:
                case uint _:
                case ulong _:
                case ushort _:
                case sbyte _:
                    sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                    return;
                case IDictionary<string, object> dict:
                    {
                        sb.Append('{');
                        bool first = true;
                        foreach (var kv in dict)
                        {
                            if (!first) sb.Append(',');
                            first = false;
                            NewLine(sb, indent, depth + 1);
                            Quote(sb, kv.Key);
                            sb.Append(indent ? ": " : ":");
                            Write(sb, kv.Value, indent, depth + 1);
                        }
                        if (!first) NewLine(sb, indent, depth);
                        sb.Append('}');
                        return;
                    }
                case IDictionary nd:
                    {
                        sb.Append('{');
                        bool first = true;
                        foreach (DictionaryEntry kv in nd)
                        {
                            if (!first) sb.Append(',');
                            first = false;
                            NewLine(sb, indent, depth + 1);
                            Quote(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                            sb.Append(indent ? ": " : ":");
                            Write(sb, kv.Value, indent, depth + 1);
                        }
                        if (!first) NewLine(sb, indent, depth);
                        sb.Append('}');
                        return;
                    }
                case IEnumerable en:
                    {
                        sb.Append('[');
                        bool first = true;
                        foreach (var item in en)
                        {
                            if (!first) sb.Append(',');
                            first = false;
                            NewLine(sb, indent, depth + 1);
                            Write(sb, item, indent, depth + 1);
                        }
                        if (!first) NewLine(sb, indent, depth);
                        sb.Append(']');
                        return;
                    }
                default:
                    Quote(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
                    return;
            }
        }
    }

    /// <summary>JObj 값 읽기 도우미 — 타입이 조금 달라도(문자열 숫자 등) 관대하게 읽는다.</summary>
    public static class J
    {
        public static bool Has(this IDictionary<string, object> o, string k) => o != null && o.ContainsKey(k);

        public static string S(this IDictionary<string, object> o, string k, string def = "")
        {
            if (o == null || !o.TryGetValue(k, out var v) || v == null) return def;
            return v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static long L(this IDictionary<string, object> o, string k, long def = 0)
        {
            if (o == null || !o.TryGetValue(k, out var v) || v == null) return def;
            switch (v)
            {
                case long l: return l;
                case int i: return i;
                case double d: return (long)d;
                case bool b: return b ? 1 : 0;
                case string s:
                    return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long r) ? r : def;
            }
            return def;
        }

        public static int I(this IDictionary<string, object> o, string k, int def = 0)
        {
            long v = o.L(k, def);
            return v > int.MaxValue ? int.MaxValue : v < int.MinValue ? int.MinValue : (int)v;
        }

        public static double D(this IDictionary<string, object> o, string k, double def = 0)
        {
            if (o == null || !o.TryGetValue(k, out var v) || v == null) return def;
            switch (v)
            {
                case double d: return d;
                case long l: return l;
                case string s:
                    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) ? r : def;
            }
            return def;
        }

        public static bool B(this IDictionary<string, object> o, string k, bool def = false)
        {
            if (o == null || !o.TryGetValue(k, out var v) || v == null) return def;
            switch (v)
            {
                case bool b: return b;
                case long l: return l != 0;
                case double d: return d != 0;
                case string s:
                    if (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" || s.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
                    if (s.Equals("false", StringComparison.OrdinalIgnoreCase) || s == "0" || s.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
                    return def;
            }
            return def;
        }

        public static JObj O(this IDictionary<string, object> o, string k)
        {
            if (o == null || !o.TryGetValue(k, out var v)) return null;
            return v as JObj;
        }

        public static List<object> A(this IDictionary<string, object> o, string k)
        {
            if (o == null || !o.TryGetValue(k, out var v)) return null;
            return v as List<object>;
        }

        public static List<string> StrList(this IDictionary<string, object> o, string k)
        {
            var res = new List<string>();
            var a = o.A(k);
            if (a == null) return res;
            foreach (var x in a)
                if (x != null) res.Add(x as string ?? Convert.ToString(x, CultureInfo.InvariantCulture));
            return res;
        }
    }
}
