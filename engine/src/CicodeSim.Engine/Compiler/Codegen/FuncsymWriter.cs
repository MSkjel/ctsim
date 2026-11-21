using System;
using System.Collections.Generic;
using System.Text;
using CicodeSim.Values;

namespace CicodeSim.Compiler.Codegen
{
    /// <summary>Writes _FUNCSYM.DBF as the compiler does.</summary>
    public static class FuncsymWriter
    {
        private static readonly (string name, int size)[] Fields =
        {
            ("NAME", 251),
            ("OP_CODE", 5),
            ("ARGC", 35),
            ("TYPE", 5),
            ("RETURNED", 5),
            ("MANDATORY", 5),
        };

        /// <summary>The file for the image's functions (Library order) dated <paramref name="date"/>.</summary>
        public static byte[] Write(LibraryImage img, DateTime date)
        {
            var rows = new List<List<KeyValuePair<string, string>>>(img.Library.Count);
            foreach (var r in img.Library)
                rows.Add(r.Info.FuncsymRow());
            return Write(rows, date);
        }

        public static byte[] Write(IList<List<KeyValuePair<string, string>>> rows, DateTime date)
        {
            int recLen = 1;
            foreach (var (_, size) in Fields)
                recLen += size;
            int head = 32 + 32 * Fields.Length + 2;
            var b = new byte[head + recLen * rows.Count + 1];
            b[0] = 0x03;
            b[1] = (byte)(date.Year - 1900);
            b[2] = (byte)date.Month;
            b[3] = (byte)date.Day;
            CodeBuffer.PutI32(b, 4, rows.Count);
            b[8] = (byte)head;
            b[9] = (byte)(head >> 8);
            b[10] = (byte)recLen;
            b[11] = (byte)(recLen >> 8);
            int at = 32;
            foreach (var (name, size) in Fields)
            {
                byte[] n = Encoding.ASCII.GetBytes(name);
                Array.Copy(n, 0, b, at, n.Length);
                b[at + 11] = (byte)'C';
                b[at + 16] = (byte)size;
                at += 32;
            }
            b[at] = 0x0D;
            at = head;
            foreach (var row in rows)
            {
                b[at] = (byte)' ';
                int p = at + 1;
                foreach (var (name, size) in Fields)
                {
                    string v = "";
                    foreach (var kv in row)
                        if (kv.Key == name)
                        {
                            v = kv.Value;
                            break;
                        }
                    byte[] t = Cp1252.Encode(v);
                    for (int i = 0; i < size; i++)
                        b[p + i] = i < t.Length ? t[i] : (byte)' ';
                    p += size;
                }
                at += recLen;
            }
            b[at] = 0x1A;
            return b;
        }

        /// <summary>The compile date of an existing _FUNCSYM.DBF (to compare the rest byte for byte).</summary>
        public static DateTime DateOf(byte[] dbf) =>
            dbf.Length >= 4 && dbf[2] >= 1 && dbf[2] <= 12 && dbf[3] >= 1 && dbf[3] <= 31
                ? new DateTime(1900 + dbf[1], dbf[2], dbf[3])
                : new DateTime(2000, 1, 1);
    }
}
