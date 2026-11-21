using System;
using System.Collections.Generic;
using CicodeSim.Compiled;
using CicodeSim.World.Display;

namespace CicodeSim.Page
{
    /// <summary>A compiled page as the animator reads it.</summary>
    public sealed class CompiledPage
    {
        public string Name;
        public string Path = "";
        public Program Program = new Program();
        public byte[] Code = Array.Empty<byte>();
        public Dictionary<string, List<Dictionary<string, object?>>> Tables = new Dictionary<
            string,
            List<Dictionary<string, object?>>
        >(StringComparer.Ordinal);
        public PageRecord? Control;

        public CompiledPage(string name)
        {
            Name = name;
        }

        public static CompiledPage Load(string path) => FromRdb(RdbFile.Open(path));

        public static CompiledPage FromRdb(RdbFile f)
        {
            var pc = Rdb.PageFromRdb(f);
            var cp = new CompiledPage(pc.Page)
            {
                Path = f.Path,
                Program = pc.Program,
                Code = f.Pool("CODE"),
                Tables = pc.Records,
                Control = PageRecord.FromRdb(f),
            };
            if (cp.Control != null && cp.Control.Name.Length > 0)
                cp.Name = cp.Control.Name;
            return cp;
        }

        public List<Dictionary<string, object?>> Rows(string table) =>
            Tables.TryGetValue(table, out var r) ? r : new List<Dictionary<string, object?>>();

        /// <summary>The code block at a CODE offset, null for none (0xFFFFFFFF, out of the pool,
        /// or a block that is only End: the runtime runs nothing for code whose first byte is
        /// 0xff).</summary>
        public byte[]? CodeAt(long offset)
        {
            if (offset < 2 || offset > Code.Length || offset == 0xFFFFFFFFL)
                return null;
            byte[] b = Rdb.CodeBlockAt(Code, offset);
            if (b.Length == 0 || b[0] == 0xFF)
                return null;
            return b;
        }

        /// <summary>The code of an expression or command field of a record.</summary>
        public byte[]? FieldCode(Dictionary<string, object?> rec, string field)
        {
            if (!rec.TryGetValue(field, out var v) || v == null)
                return null;
            switch (v)
            {
                case long l:
                    return CodeAt(l);
                case byte[] b when b.Length >= 4:
                    return CodeAt(BitConverter.ToUInt32(b, 0));
            }
            return null;
        }

        /// <summary>A slider tag field (type 23): its read and write code.</summary>
        public (byte[]? Read, byte[]? Write) SliderCode(Dictionary<string, object?> rec, string field)
        {
            if (!rec.TryGetValue(field, out var v) || !(v is byte[] b) || b.Length < 8)
                return (null, null);
            return (CodeAt(BitConverter.ToUInt32(b, 0)), CodeAt(BitConverter.ToUInt32(b, 4)));
        }

        public static int Int(Dictionary<string, object?> rec, string field, int dflt = 0)
        {
            if (!rec.TryGetValue(field, out var v) || v == null)
                return dflt;
            return v switch
            {
                long l => (int)l,
                double d => (int)d,
                byte[] b when b.Length >= 2 => BitConverter.ToInt16(b, 0),
                string s => int.TryParse(
                    s.Trim(),
                    System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int x
                )
                    ? x
                    : dflt,
                _ => dflt,
            };
        }

        public static string Str(Dictionary<string, object?> rec, string field) =>
            rec.TryGetValue(field, out var v) && v is string s ? s : "";

        /// <summary>A drawing type code as PGDYNOBJ TYPE holds it ("0x0005").</summary>
        public static int TypeCode(string text)
        {
            text = (text ?? "").Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(2);
            return int.TryParse(
                text,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out int t
            )
                ? t
                : 0;
        }

        // ------------------------------------------------------------------ building (tests)
        /// <summary>Append a code block (u16 length + code) to the CODE pool; its offset.</summary>
        public long AddCode(byte[] code)
        {
            var n = new byte[Code.Length + 2 + code.Length];
            Array.Copy(Code, n, Code.Length);
            if (Code.Length == 0)
            {
                // offset 0 is never a block (CodeAt needs 2 bytes before it): a pad word
                n = new byte[4 + code.Length];
                n[2] = (byte)(code.Length & 0xFF);
                n[3] = (byte)(code.Length >> 8);
                Array.Copy(code, 0, n, 4, code.Length);
                Code = n;
                return 4;
            }
            int at = Code.Length;
            n[at] = (byte)(code.Length & 0xFF);
            n[at + 1] = (byte)(code.Length >> 8);
            Array.Copy(code, 0, n, at + 2, code.Length);
            Code = n;
            return at + 2;
        }

        /// <summary>A command field value (16 bytes, the code offset first).</summary>
        public static byte[] Command(long offset)
        {
            var b = new byte[16];
            BitConverter.GetBytes((uint)offset).CopyTo(b, 0);
            return b;
        }

        /// <summary>A slider field value (8 bytes: read, write offsets).</summary>
        public static byte[] Slider(long read, long write)
        {
            var b = new byte[8];
            BitConverter.GetBytes((uint)read).CopyTo(b, 0);
            BitConverter.GetBytes((uint)write).CopyTo(b, 4);
            return b;
        }

        public void AddRow(string table, Dictionary<string, object?> row)
        {
            if (!Tables.TryGetValue(table, out var rows))
                Tables[table] = rows = new List<Dictionary<string, object?>>();
            rows.Add(row);
        }
    }
}
