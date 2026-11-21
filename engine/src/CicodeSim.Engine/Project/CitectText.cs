using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace CicodeSim.Project
{
    /// <summary>Text rules for project files and names (Windows-1252, decoded losslessly).</summary>
    public static class CitectText
    {
        /// <summary>A blank as C isspace knows it: space, \t, \n, \v, \f, \r.</summary>
        public static bool IsWhite(char c) => c == ' ' || (c >= '\t' && c <= '\r');

        /// <summary>The text without blanks at either end ("" for null).</summary>
        public static string Trim(string? s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            int a = 0,
                b = s!.Length;
            while (a < b && IsWhite(s[a]))
                a++;
            while (b > a && IsWhite(s[b - 1]))
                b--;
            return a == 0 && b == s.Length ? s : s.Substring(a, b - a);
        }

        public static string TrimStart(string s)
        {
            int a = 0;
            while (a < s.Length && IsWhite(s[a]))
                a++;
            return a == 0 ? s : s.Substring(a);
        }

        public static string TrimEnd(string s)
        {
            int b = s.Length;
            while (b > 0 && IsWhite(s[b - 1]))
                b--;
            return b == s.Length ? s : s.Substring(0, b);
        }

        /// <summary>Null, empty or only blanks.</summary>
        public static bool IsBlank(string? s)
        {
            if (string.IsNullOrEmpty(s))
                return true;
            for (int i = 0; i < s!.Length; i++)
                if (!IsWhite(s[i]))
                    return false;
            return true;
        }

        /// <summary>The text in lower case, character by character ("" for null).</summary>
        public static string Lower(string? s) => string.IsNullOrEmpty(s) ? "" : s!.ToLowerInvariant();

        /// <summary>The text in upper case, character by character: the Windows-1252 case
        /// mapping, so µ stays µ (.NET's invariant casing maps it to Greek capital mu on
        /// net10.0, not on net48) and ß stays ß ("" for null).</summary>
        public static string Upper(string? s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            string u = s!.ToUpperInvariant();
            return u.IndexOf('Μ') >= 0 && s.IndexOf('µ') >= 0 ? KeepMicro(s, u) : u;
        }

        private static string KeepMicro(string s, string u)
        {
            var c = u.ToCharArray();
            for (int i = 0; i < c.Length; i++)
                if (s[i] == 'µ')
                    c[i] = 'µ';
            return new string(c);
        }

        /// <summary>Two names equal without case (<see cref="Lower"/> of both), without
        /// allocating.</summary>
        public static bool SameName(string? a, string? b)
        {
            a ??= "";
            b ??= "";
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                char x = a[i],
                    y = b[i];
                if (x != y && char.ToLowerInvariant(x) != char.ToLowerInvariant(y))
                    return false;
            }
            return true;
        }

        /// <summary>The names sorted by <see cref="Lower"/> (ordinal on the lowered text), equal
        /// ones in their original order.</summary>
        public static List<string> SortIgnoringCase(IEnumerable<string> items)
        {
            var list = new List<(string low, int i, string s)>();
            int k = 0;
            foreach (string s in items)
                list.Add((Lower(s), k++, s));
            list.Sort(
                (a, b) =>
                {
                    int c = string.CompareOrdinal(a.low, b.low);
                    return c != 0 ? c : a.i.CompareTo(b.i);
                }
            );
            var o = new List<string>(list.Count);
            foreach (var t in list)
                o.Add(t.s);
            return o;
        }

        /// <summary>Non-empty and only the ASCII digits 0..9.</summary>
        public static bool IsDigits(string s)
        {
            if (s.Length == 0)
                return false;
            foreach (char c in s)
                if (c < '0' || c > '9')
                    return false;
            return true;
        }

        /// <summary>An integer written as text: blanks around it, an optional sign, then ASCII
        /// decimal digits (leading zeros allowed), or in base 16 hex digits with an optional
        /// 0x prefix; null when the text is anything else.</summary>
        public static BigInteger? ParseInt(string? text, int numberBase = 10)
        {
            if (text == null)
                return null;
            string s = Trim(text);
            int i = 0,
                n = s.Length;
            if (n == 0)
                return null;
            bool neg = false;
            if (s[0] == '+' || s[0] == '-')
            {
                neg = s[0] == '-';
                i = 1;
            }
            if (numberBase == 16 && i + 1 < n && s[i] == '0' && (s[i + 1] == 'x' || s[i + 1] == 'X'))
                i += 2;
            if (i >= n)
                return null;
            BigInteger v = BigInteger.Zero;
            for (; i < n; i++)
            {
                char c = s[i];
                int d;
                if (c >= '0' && c <= '9')
                    d = c - '0';
                else if (numberBase == 16 && c >= 'a' && c <= 'f')
                    d = c - 'a' + 10;
                else if (numberBase == 16 && c >= 'A' && c <= 'F')
                    d = c - 'A' + 10;
                else
                    return null;
                v = v * numberBase + d;
            }
            return neg ? -v : v;
        }

        /// <summary><see cref="ParseInt"/> as a long; null beyond 64 bits.</summary>
        public static long? ParseLong(string? text, int numberBase = 10)
        {
            BigInteger? b = ParseInt(text, numberBase);
            if (b == null)
                return null;
            if (b.Value < long.MinValue || b.Value > long.MaxValue)
                return null;
            return (long)b.Value;
        }

        /// <summary>A name or value quoted for a message: 'text'.</summary>
        public static string Quote(string s) => "'" + s + "'";
    }

    /// <summary>A case-insensitive string comparer by <see cref="CitectText.SameName"/> (no allocation).</summary>
    public sealed class LowerComparer : IEqualityComparer<string>
    {
        public static readonly LowerComparer Instance = new LowerComparer();

        public bool Equals(string? x, string? y) => CitectText.SameName(x, y);

        public int GetHashCode(string obj) => Hash(obj);

        public static int Hash(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                for (int i = 0; i < s.Length; i++)
                {
                    h ^= char.ToLowerInvariant(s[i]);
                    h *= 16777619;
                }
                return (int)h;
            }
        }
    }
}
