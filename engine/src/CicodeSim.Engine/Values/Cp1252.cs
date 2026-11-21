using System.Text;

namespace CicodeSim.Values
{
    /// <summary>Lossless Windows-1252.</summary>
    public static class Cp1252
    {
        private static readonly char[] Table = BuildTable();
        private static readonly byte[] Reverse = BuildReverse();

        private static char[] BuildTable()
        {
            var t = new char[256];
            for (int i = 0; i < 256; i++)
                t[i] = (char)i;
            ushort[] hi =
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
                t[0x80 + i] = (char)hi[i];
            return t;
        }

        private static byte[] BuildReverse()
        {
            // 0 = not encodable (except for the NUL character itself)
            var r = new byte[0x10000];
            for (int i = 0; i < 256; i++)
                r[Table[i]] = (byte)i;
            return r;
        }

        /// <summary>The character of one byte.</summary>
        public static char Char(byte b) => Table[b];

        /// <summary>Decode bytes (never fails: every byte is mapped).</summary>
        public static string Decode(byte[] b, int start, int count)
        {
            if (count <= 0)
                return "";
            var c = new char[count];
            for (int i = 0; i < count; i++)
                c[i] = Table[b[start + i]];
            return new string(c);
        }

        public static string Decode(byte[] b) => Decode(b, 0, b.Length);

        /// <summary>Decode up to the first NUL (or the end).</summary>
        public static string DecodeZ(byte[] b, int start, int max = int.MaxValue)
        {
            int end = start;
            int lim = max == int.MaxValue ? b.Length : System.Math.Min(b.Length, start + max);
            while (end < lim && b[end] != 0)
                end++;
            return Decode(b, start, end - start);
        }

        /// <summary>True when the character has a byte in the code page.</summary>
        public static bool CanEncode(char c) => c == '\0' || Reverse[c] != 0;

        /// <summary>The byte of one character; '?' (0x3F) for a character the code page cannot
        /// store.</summary>
        public static byte Byte(char c)
        {
            if (c == '\0')
                return 0;
            byte b = Reverse[c];
            return b != 0 ? b : (byte)0x3F;
        }

        /// <summary>Encode with '?' for characters the code page cannot store.</summary>
        public static byte[] Encode(string s)
        {
            var b = new byte[s.Length];
            for (int i = 0; i < s.Length; i++)
                b[i] = Byte(s[i]);
            return b;
        }

        /// <summary>True when every character is ASCII.</summary>
        public static bool IsAscii(string s)
        {
            foreach (char c in s)
                if (c > 0x7F)
                    return false;
            return true;
        }

        /// <summary>An Encoding object for stream readers / writers.</summary>
        public static Encoding Encoding { get; } = new Cp1252Encoding();

        private sealed class Cp1252Encoding : Encoding
        {
            public override string WebName => "windows-1252-lossless";

            public override int GetByteCount(char[] chars, int index, int count) => count;

            public override int GetBytes(
                char[] chars,
                int charIndex,
                int charCount,
                byte[] bytes,
                int byteIndex
            )
            {
                for (int i = 0; i < charCount; i++)
                    bytes[byteIndex + i] = Byte(chars[charIndex + i]);
                return charCount;
            }

            public override int GetCharCount(byte[] bytes, int index, int count) => count;

            public override int GetChars(
                byte[] bytes,
                int byteIndex,
                int byteCount,
                char[] chars,
                int charIndex
            )
            {
                for (int i = 0; i < byteCount; i++)
                    chars[charIndex + i] = Table[bytes[byteIndex + i]];
                return byteCount;
            }

            public override int GetMaxByteCount(int charCount) => charCount;

            public override int GetMaxCharCount(int byteCount) => byteCount;
        }
    }
}
