using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CicodeSim.Compiled;
using CicodeSim.Values;

namespace CicodeSim.Compiler.Codegen
{
    /// <summary>Writes a LibraryImage as _LIBRARY.RDB.</summary>
    public static class RdbWriter
    {
        /// <summary>The creation time text, time_t and the unexplained u32 at 0x102.</summary>
        public struct Stamp
        {
            public string CreationTime;
            public uint TimeT;
            public uint X;

            public static Stamp Now()
            {
                var t = DateTime.Now;
                // asctime: "Thu Jul  2 05:11:45 2026"
                string s =
                    t.ToString("ddd MMM ", CultureInfo.InvariantCulture)
                    + t.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2)
                    + t.ToString(" HH:mm:ss yyyy", CultureInfo.InvariantCulture);
                return new Stamp
                {
                    CreationTime = s,
                    TimeT = (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                    X = 0,
                };
            }

            /// <summary>The stamp of an existing RDB file (to compare the rest byte for byte).</summary>
            public static Stamp Of(byte[] rdb)
            {
                int i = 0;
                string marker = "Creation Time: ";
                string head = Encoding.ASCII.GetString(rdb, 0, Math.Min(0x100, rdb.Length));
                int k = head.IndexOf(marker, StringComparison.Ordinal);
                string ct = "";
                if (k >= 0)
                {
                    i = k + marker.Length;
                    int e = head.IndexOf('\0', i);
                    ct = e > i ? head.Substring(i, e - i) : head.Substring(i);
                }
                return new Stamp
                {
                    CreationTime = ct,
                    X = rdb.Length >= 0x106 ? BitConverter.ToUInt32(rdb, 0x102) : 0,
                    TimeT = rdb.Length >= 0x10A ? BitConverter.ToUInt32(rdb, 0x106) : 0,
                };
            }
        }

        private sealed class Field
        {
            public string Name;
            public int Size,
                Offset,
                Type;

            public Field(string n, int size, int off, int type)
            {
                Name = n;
                Size = size;
                Offset = off;
                Type = type;
            }
        }

        private static readonly Field[] PoolFields = { new Field("TEXT_FIELD", 0, 0, 0) };

        private static readonly Field[] ScaleFields =
        {
            new Field("RawZero", 8, 0, 28),
            new Field("RawFull", 8, 8, 28),
            new Field("EngZero", 8, 16, 28),
            new Field("EngFull", 8, 24, 28),
        };

        private static readonly Field[] TagRefFields =
        {
            new Field("ClusterName", 8, 0, 1),
            new Field("TagName", 8, 8, 1),
            new Field("TagElement", 8, 16, 1),
            new Field("TagItem", 8, 24, 1),
            new Field("Percentage", 1, 32, 8),
            new Field("ValueHandle", 4, 33, 4),
            new Field("Lightweight", 1, 37, 8),
        };

        private static readonly Field[] FuncLookupFields =
        {
            new Field("FunctionName", 8, 0, 1),
            new Field("FunctionHandle", 4, 8, 4),
        };

        private static readonly Field[] CodeVarFields =
        {
            new Field("CodeVarRef", 8, 0, 1),
            new Field("LogicalUnit", 2, 8, 3),
            new Field("RawType", 1, 10, 8),
            new Field("BitWidth", 2, 11, 3),
            new Field("UnitType", 4, 13, 4),
            new Field("UnitAddress", 4, 17, 4),
            new Field("UnitCount", 2, 21, 3),
            new Field("Error", 2, 23, 3),
            new Field("DataAddress", 8, 25, 9),
            new Field("Quality", 8, 33, 27),
            new Field("Timestamp", 8, 41, 27),
            new Field("QualityTimestamp", 8, 49, 27),
            new Field("ValueTimestamp", 8, 57, 27),
        };

        private static readonly Field[] RequestFields = { new Field("Request", 8, 0, 9) };

        private static readonly Field[] LibraryFields =
        {
            new Field("NAME", 8, 0, 1),
            new Field("ARGS", 8, 8, 1),
            new Field("RESULT", 2, 16, 3),
            new Field("CODE", 32, 18, 16),
            new Field("FILENAME", 8, 50, 1),
            new Field("DEBUGCNT", 2, 58, 3),
            new Field("DEBUG", 8, 60, 9),
            new Field("MINPARAM", 2, 68, 3),
        };

        private static readonly Field[] DebugFields =
        {
            new Field("Offset", 4, 0, 4),
            new Field("Name", 8, 4, 1),
        };

        private sealed class Sec
        {
            public string Name = "";
            public Field[] Fields = PoolFields;
            public int RecSize;
            public long NRec;
            public byte[] Data = Array.Empty<byte>();

            /// <summary>Null = no relocation table.</summary>
            public byte[]? Relocs;
        }

        /// <summary>The section bytes of each section in order (head, descriptors, data, relocations).</summary>
        public static List<(string name, byte[] bytes)> Sections(LibraryImage img)
        {
            var secs = new List<Sec>();
            byte[] text = img.TextPool.Bytes();
            secs.Add(
                new Sec
                {
                    Name = "TEXT",
                    RecSize = 1,
                    NRec = text.Length,
                    Data = text,
                }
            );
            var scale = new List<byte>();
            foreach (var s in img.Scales)
            {
                Put64(scale, BitConverter.DoubleToInt64Bits(s.RawZero));
                Put64(scale, BitConverter.DoubleToInt64Bits(s.RawFull));
                Put64(scale, BitConverter.DoubleToInt64Bits(s.EngZero));
                Put64(scale, BitConverter.DoubleToInt64Bits(s.EngFull));
            }
            secs.Add(
                new Sec
                {
                    Name = "SCALE",
                    Fields = ScaleFields,
                    RecSize = 32,
                    NRec = img.Scales.Count,
                    Data = scale.ToArray(),
                }
            );
            secs.Add(
                new Sec
                {
                    Name = "RUN",
                    RecSize = 1,
                    NRec = img.Run.Length,
                    Data = img.Run,
                }
            );
            var tags = new List<byte>();
            foreach (var t in img.TagRefs)
            {
                Put64(tags, img.Text(t.Cluster));
                Put64(tags, img.Text(t.Tag));
                Put64(tags, img.Text(t.Element));
                Put64(tags, img.Text(t.Item));
                tags.Add((byte)t.Percentage);
                Put32(tags, -1);
                tags.Add((byte)t.Lightweight);
            }
            secs.Add(
                new Sec
                {
                    Name = "TAGREF",
                    Fields = TagRefFields,
                    RecSize = 38,
                    NRec = img.TagRefs.Count,
                    Data = tags.ToArray(),
                    Relocs = RecordRelocs(3, img.TagRefs.Count > 0, (0, 0), (8, 0), (16, 0), (24, 0)),
                }
            );
            var fl = new List<byte>();
            foreach (var f in img.FuncLookups)
            {
                Put64(fl, img.Text(f.SymbolName));
                Put32(fl, -1);
            }
            secs.Add(
                new Sec
                {
                    Name = "FUNCLOOKUP",
                    Fields = FuncLookupFields,
                    RecSize = 12,
                    NRec = img.FuncLookups.Count,
                    Data = fl.ToArray(),
                    Relocs = RecordRelocs(4, true, (0, 0)),
                }
            );
            var cv = new List<byte>();
            foreach (var c in img.CodeVars)
            {
                Put64(cv, img.Text(c.Name));
                Put16(cv, 32000);
                cv.Add((byte)c.RawType);
                Put16(cv, c.BitWidth);
                Put32(cv, c.UnitType);
                Put32(cv, c.UnitAddress);
                Put16(cv, c.UnitCount);
                Put16(cv, 0);
                Put64(cv, c.DataAddress);
                Put64(cv, 0xC0);
                Put64(cv, 0);
                Put64(cv, 0);
                Put64(cv, 0);
            }
            secs.Add(
                new Sec
                {
                    Name = "CODEVAR",
                    Fields = CodeVarFields,
                    RecSize = 65,
                    NRec = img.CodeVars.Count,
                    Data = cv.ToArray(),
                    Relocs = RecordRelocs(5, true, (0, 0), (25, 2)),
                }
            );
            var rq = new List<byte>();
            foreach (long r in img.Requests)
                Put64(rq, r);
            secs.Add(
                new Sec
                {
                    Name = "REQUEST",
                    Fields = RequestFields,
                    RecSize = 8,
                    NRec = img.Requests.Count,
                    Data = rq.ToArray(),
                    Relocs = RecordRelocs(6, true, (0, 3)),
                }
            );
            var cr = new List<byte>();
            Put16(cr, 0);
            Put16(cr, img.CodeRelocs.Count);
            Put16(cr, 0);
            Put16(cr, 2 * 7);
            foreach (var (at, sec) in img.CodeRelocs)
                Put32(cr, (int)((uint)at | ((uint)sec << 24)));
            secs.Add(
                new Sec
                {
                    Name = "CODE",
                    RecSize = 1,
                    NRec = img.CodePool.Count,
                    Data = img.CodePool.ToArray(),
                    Relocs = cr.ToArray(),
                }
            );
            secs.Add(
                new Sec
                {
                    Name = "FUNC",
                    RecSize = 1,
                    NRec = img.FuncPool.Count,
                    Data = img.FuncPool.ToArray(),
                }
            );
            var lib = new List<byte>();
            foreach (var r in img.Library)
            {
                Put64(lib, img.Text(r.Name));
                Put64(lib, img.Text(r.Args));
                Put16(lib, r.Result);
                Put64(lib, r.CodeOffset);
                Put64(lib, r.FuncOffset);
                Put64(lib, r.RequestOffset);
                Put64(lib, 0);
                Put64(lib, img.Text(r.Filename));
                Put16(lib, r.DebugCount);
                Put64(lib, r.DebugOffset);
                Put16(lib, r.MinParam);
            }
            secs.Add(
                new Sec
                {
                    Name = "Library",
                    Fields = LibraryFields,
                    RecSize = 70,
                    NRec = img.Library.Count,
                    Data = lib.ToArray(),
                    Relocs = RecordRelocs(
                        9,
                        true,
                        (0, 0),
                        (8, 0),
                        (18, 7),
                        (26, 8),
                        (34, 6),
                        (50, 0),
                        (60, 10)
                    ),
                }
            );
            var dbg = new List<byte>();
            foreach (var (slot, name) in img.Debug)
            {
                Put32(dbg, slot);
                Put64(dbg, img.Text(name));
            }
            secs.Add(
                new Sec
                {
                    Name = "DEBUG",
                    Fields = DebugFields,
                    RecSize = 12,
                    NRec = img.Debug.Count,
                    Data = dbg.ToArray(),
                    Relocs = RecordRelocs(10, true, (4, 0)),
                }
            );
            var o = new List<(string, byte[])>();
            foreach (var s in secs)
                o.Add((s.Name, SectionBytes(s)));
            return o;
        }

        /// <summary>A record section's relocation table: `u16 fields, u16 0, u16 0, u16 2 * own
        /// index`, then per field `offset | target section &lt;&lt; 24` (9.0.1).</summary>
        private static byte[] RecordRelocs(int own, bool any, params (int off, int target)[] fields)
        {
            var b = new List<byte>();
            Put16(b, fields.Length);
            Put16(b, 0);
            Put16(b, 0);
            Put16(b, 2 * own);
            foreach (var (off, t) in fields)
                Put32(b, (int)((uint)off | ((uint)t << 24)));
            return b.ToArray();
        }

        private static byte[] SectionBytes(Sec s)
        {
            var b = new List<byte>();
            PutName(b, s.Name, 32);
            Put16(b, s.Fields.Length);
            Put16(b, s.RecSize);
            long size = 52 + 40L * s.Fields.Length + s.Data.Length;
            Put32(b, (int)size);
            Put32(b, (int)s.NRec);
            Put32(b, s.Relocs != null ? (int)size : 0);
            Put32(b, s.Relocs?.Length ?? 0);
            foreach (var f in s.Fields)
            {
                PutName(b, f.Name, 32);
                Put16(b, f.Size);
                Put16(b, f.Offset);
                Put32(b, f.Type);
            }
            b.AddRange(s.Data);
            if (s.Relocs != null)
                b.AddRange(s.Relocs);
            return b.ToArray();
        }

        /// <summary>The whole file.</summary>
        public static byte[] Write(LibraryImage img, Stamp stamp)
        {
            var secs = Sections(img);
            var b = new List<byte>();
            string head =
                "Copyright AVEVA Group plc and its subsidiaries. All rights reserved\r\n\r\nVersion: 7.0\r\nCreation Time: "
                + stamp.CreationTime;
            b.AddRange(Encoding.ASCII.GetBytes(head));
            while (b.Count < 0x100)
                b.Add(0);
            Put16(b, 7);
            Put32(b, (int)stamp.X);
            Put32(b, (int)stamp.TimeT);
            Put16(b, secs.Count);
            int sizeAt = b.Count;
            Put32(b, 0);
            long off = 0x110 + 32L * secs.Count + 6;
            foreach (var (name, bytes) in secs)
            {
                for (int i = 0; i < 6; i++)
                    b.Add(0);
                PutName(b, name, 14);
                Put16(b, 0xFFFF);
                Put32(b, (int)off);
                Put32(b, bytes.Length);
                Put16(b, 0);
                off += bytes.Length;
            }
            for (int i = 0; i < 6; i++)
                b.Add(0);
            foreach (var (_, bytes) in secs)
                b.AddRange(bytes);
            byte[] all = b.ToArray();
            CodeBuffer.PutI32(all, sizeAt, all.Length);
            return all;
        }

        private static void PutName(List<byte> b, string s, int n)
        {
            byte[] t = Encoding.ASCII.GetBytes(s);
            for (int i = 0; i < n; i++)
                b.Add(i < t.Length ? t[i] : (byte)0);
        }

        private static void Put16(List<byte> b, int v)
        {
            b.Add((byte)v);
            b.Add((byte)(v >> 8));
        }

        private static void Put32(List<byte> b, int v)
        {
            b.Add((byte)v);
            b.Add((byte)(v >> 8));
            b.Add((byte)(v >> 16));
            b.Add((byte)(v >> 24));
        }

        private static void Put64(List<byte> b, long v)
        {
            Put32(b, (int)v);
            Put32(b, (int)(v >> 32));
        }
    }
}
