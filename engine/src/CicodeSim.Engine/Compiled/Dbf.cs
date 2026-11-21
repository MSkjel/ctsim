using System;
using System.Collections.Generic;
using System.IO;
using CicodeSim.Values;

namespace CicodeSim.Compiled
{
    /// <summary>Reads the project dBASE tables the way compiled code takes them.</summary>
    public static class Dbf
    {
        public static List<Dictionary<string, string>> CitectRows(string path)
        {
            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                return new List<Dictionary<string, string>>();
            }
            catch (UnauthorizedAccessException)
            {
                return new List<Dictionary<string, string>>();
            }
            return CitectRows(data);
        }

        public static List<Dictionary<string, string>> CitectRows(byte[] data)
        {
            var rows = new List<Dictionary<string, string>>();
            int n = data.Length;
            if (n < 33)
                return rows;
            long nrec = BitConverter.ToUInt32(data, 4);
            int hdrSize = BitConverter.ToUInt16(data, 8);
            int recSize = BitConverter.ToUInt16(data, 10);
            if (recSize <= 0 || hdrSize < 33 || hdrSize > n)
                return rows;
            var names = new List<string>();
            var offsets = new List<int>();
            var lengths = new List<int>();
            int off = 32,
                pos = 1;
            while (off + 32 <= hdrSize && data[off] != 0x0D)
            {
                names.Add(Trim(Cp1252.DecodeZ(data, off, 11)));
                int ln = data[off + 16];
                offsets.Add(pos);
                lengths.Add(ln);
                pos += ln;
                off += 32;
            }
            if (names.Count == 0 || pos > recSize)
                return rows;
            long count = Math.Min(nrec, (n - hdrSize) / recSize);
            for (long i = 0; i < count; i++)
            {
                int o = (int)(hdrSize + i * recSize);
                if (data[o] == 0x2A)
                    continue;
                var d = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int k = 0; k < names.Count; k++)
                    d[names[k]] = Trim(Cp1252.Decode(data, o + offsets[k], lengths[k]));
                rows.Add(d);
            }
            return rows;
        }

        /// <summary>A blank as C isspace knows it: space, \t, \n, \v, \f, \r.</summary>
        public static bool IsBlank(char c) => c == ' ' || (c >= '\t' && c <= '\r');

        /// <summary>The text without blanks at either end.</summary>
        public static string Trim(string s)
        {
            int a = 0,
                b = s.Length;
            while (a < b && IsBlank(s[a]))
                a++;
            while (b > a && IsBlank(s[b - 1]))
                b--;
            return a == 0 && b == s.Length ? s : s.Substring(a, b - a);
        }

        /// <summary>A decimal integer field: blanks around it, an optional sign, ASCII digits;
        /// its low 64 bits. False for anything else.</summary>
        public static bool ParseInt(string s, out long v)
        {
            v = 0;
            if (!ParseBig(s, out System.Numerics.BigInteger acc))
                return false;
            v = (long)(ulong)(acc & ulong.MaxValue);
            return true;
        }

        /// <summary>A decimal integer field with its whole (unbounded) value. False for anything
        /// else.</summary>
        public static bool ParseBig(string s, out System.Numerics.BigInteger value)
        {
            value = System.Numerics.BigInteger.Zero;
            string t = Trim(s);
            int i = 0;
            bool neg = false;
            if (i < t.Length && (t[i] == '+' || t[i] == '-'))
            {
                neg = t[i] == '-';
                i++;
            }
            if (i >= t.Length)
                return false;
            System.Numerics.BigInteger acc = System.Numerics.BigInteger.Zero;
            for (; i < t.Length; i++)
            {
                char c = t[i];
                if (c < '0' || c > '9')
                    return false;
                acc = acc * 10 + (c - '0');
            }
            value = neg ? -acc : acc;
            return true;
        }

        /// <summary><see cref="ParseInt"/>, with an empty field read as 0.</summary>
        public static bool ParseIntOrZero(string s, out long v)
        {
            if (string.IsNullOrEmpty(s))
            {
                v = 0;
                return true;
            }
            return ParseInt(s, out v);
        }
    }
}
