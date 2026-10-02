using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AISpriteAnimation
{
    /// <summary>
    /// Minimal JSON reader used for ComfyUI API responses (no external dependency).
    /// Objects become Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;, numbers double.
    /// </summary>
    public static class MiniJson
    {
        public static object Parse(string json)
        {
            var parser = new Parser(json);
            object value = parser.ParseValue();
            parser.SkipWhitespace();
            if (!parser.AtEnd)
                throw new FormatException($"Unexpected character '{parser.Peek}' at position {parser.Position}.");
            return value;
        }

        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Walks nested dictionaries; returns null if any step is missing.</summary>
        public static object Path(object root, params string[] keys)
        {
            object current = root;
            foreach (string key in keys)
            {
                if (current is Dictionary<string, object> dict && dict.TryGetValue(key, out object next)) current = next;
                else return null;
            }
            return current;
        }

        private sealed class Parser
        {
            private readonly string s;
            private int pos;

            public Parser(string json) { s = json ?? string.Empty; }

            public bool AtEnd => pos >= s.Length;
            public int Position => pos;
            public char Peek => s[pos];

            public void SkipWhitespace()
            {
                while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
            }

            public object ParseValue()
            {
                SkipWhitespace();
                if (AtEnd) throw new FormatException("Unexpected end of JSON.");
                char c = s[pos];
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default: return ParseNumber();
                }
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(s, pos, word, 0, word.Length) != 0)
                    throw new FormatException($"Invalid token at position {pos}.");
                pos += word.Length;
            }

            private Dictionary<string, object> ParseObject()
            {
                var result = new Dictionary<string, object>();
                pos++; // {
                SkipWhitespace();
                if (!AtEnd && s[pos] == '}') { pos++; return result; }
                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || s[pos] != '"') throw new FormatException($"Expected string key at position {pos}.");
                    string key = ParseString();
                    SkipWhitespace();
                    if (AtEnd || s[pos] != ':') throw new FormatException($"Expected ':' at position {pos}.");
                    pos++;
                    result[key] = ParseValue();
                    SkipWhitespace();
                    if (AtEnd) throw new FormatException("Unterminated object.");
                    if (s[pos] == ',') { pos++; continue; }
                    if (s[pos] == '}') { pos++; return result; }
                    throw new FormatException($"Expected ',' or '}}' at position {pos}.");
                }
            }

            private List<object> ParseArray()
            {
                var result = new List<object>();
                pos++; // [
                SkipWhitespace();
                if (!AtEnd && s[pos] == ']') { pos++; return result; }
                while (true)
                {
                    result.Add(ParseValue());
                    SkipWhitespace();
                    if (AtEnd) throw new FormatException("Unterminated array.");
                    if (s[pos] == ',') { pos++; continue; }
                    if (s[pos] == ']') { pos++; return result; }
                    throw new FormatException($"Expected ',' or ']' at position {pos}.");
                }
            }

            private string ParseString()
            {
                var sb = new StringBuilder();
                pos++; // opening quote
                while (true)
                {
                    if (AtEnd) throw new FormatException("Unterminated string.");
                    char c = s[pos++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (AtEnd) throw new FormatException("Unterminated escape.");
                    char e = s[pos++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (pos + 4 > s.Length) throw new FormatException("Bad unicode escape.");
                            sb.Append((char)int.Parse(s.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            pos += 4;
                            break;
                        default: sb.Append(e); break; // " \ /
                    }
                }
            }

            private double ParseNumber()
            {
                int start = pos;
                while (pos < s.Length && "+-0123456789.eE".IndexOf(s[pos]) >= 0) pos++;
                if (start == pos) throw new FormatException($"Unexpected character '{s[pos]}' at position {pos}.");
                return double.Parse(s.Substring(start, pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }
}
