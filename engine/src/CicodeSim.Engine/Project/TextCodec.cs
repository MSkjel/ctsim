using System;
using System.Text;

namespace CicodeSim.Project
{
    /// <summary>The text codec for every string in the DBF tables: lossless Windows-1252.</summary>
    public static class TextCodec
    {
        /// <summary>Byte to char, the lossless Windows-1252 table.</summary>
        public static readonly char[] Decode1252 = BuildDecode();

        private static readonly ushort[] EncodeTable = BuildEncode();

        private static char[] BuildDecode()
        {
            var t = new char[256];
            for (int i = 0; i < 256; i++)
                t[i] = (char)i;
            int[] high =
            {
                0x20AC,
                0x0081,
                0x201A,
                0x0192,
                0x201E,
                0x2026,
                0x2020,
                0x2021,
                0x02C6,
                0x2030,
                0x0160,
                0x2039,
                0x0152,
                0x008D,
                0x017D,
                0x008F,
                0x0090,
                0x2018,
                0x2019,
                0x201C,
                0x201D,
                0x2022,
                0x2013,
                0x2014,
                0x02DC,
                0x2122,
                0x0161,
                0x203A,
                0x0153,
                0x009D,
                0x017E,
                0x0178,
            };
            for (int i = 0; i < 32; i++)
                t[0x80 + i] = (char)high[i];
            return t;
        }

        private static ushort[] BuildEncode()
        {
            // char -> byte + 1 (0 = not encodable) for the BMP
            var e = new ushort[0x10000];
            for (int i = 0; i < 256; i++)
                e[Decode1252[i]] = (ushort)(i + 1);
            return e;
        }

        /// <summary>Decode on-disk bytes (never fails: every byte is mapped).</summary>
        public static string Decode(byte[] b) => Decode(b, 0, b.Length);

        public static string Decode(byte[] b, int offset, int count)
        {
            if (count <= 0)
                return "";
            var t = Decode1252;
            char[] c = new char[count];
            for (int i = 0; i < count; i++)
                c[i] = t[b[offset + i]];
            return new string(c);
        }

        public static string DecodeWith(char[] table, byte[] b, int offset, int count)
        {
            if (count <= 0)
                return "";
            char[] c = new char[count];
            for (int i = 0; i < count; i++)
                c[i] = table[b[offset + i]];
            return new string(c);
        }

        /// <summary>Encode text; a character the code page cannot store becomes '?', or throws
        /// with <paramref name="strict"/>.</summary>
        public static byte[] Encode(string s, bool strict = false)
        {
            var o = new byte[s.Length];
            for (int i = 0; i < s.Length; i++)
            {
                int v = EncodeTable[s[i]];
                if (v == 0)
                {
                    if (strict)
                        throw new ArgumentException(
                            $"character U+{(int)s[i]:X4} cannot be stored in Windows-1252"
                        );
                    o[i] = (byte)'?';
                }
                else
                    o[i] = (byte)(v - 1);
            }
            return o;
        }

        public static bool CanEncode(char c) => EncodeTable[c] != 0;

        /// <summary>The whole file as text with every line break ('\r\n' and '\r'
        /// read as '\n').</summary>
        public static string UniversalNewlines(string s)
        {
            if (s.IndexOf('\r') < 0)
                return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\r')
                {
                    sb.Append('\n');
                    if (i + 1 < s.Length && s[i + 1] == '\n')
                        i++;
                }
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
