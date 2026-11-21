using System;
using System.Collections.Generic;
using System.IO;
using CicodeSim.Values;

namespace CicodeSim.Compiled
{
    /// <summary>The file is not a readable RDB.</summary>
    public sealed class RdbError : Exception
    {
        public RdbError(string message)
            : base(message) { }
    }

    public sealed class FieldDesc
    {
        public string Name;
        public int Size;
        public int Offset;
        public int Type;

        public FieldDesc(string name, int size, int offset, int type)
        {
            Name = name;
            Size = size;
            Offset = offset;
            Type = type;
        }
    }

    public sealed class Section
    {
        public string Name = "";
        public int NFields;
        public int RecSize;
        public long Size;
        public long NRec;
        public long Size2;
        public long X;
        public List<FieldDesc> Fields = new List<FieldDesc>();

        /// <summary>File offset of the section head.</summary>
        public int Start;

        /// <summary>File offset of the first record / pool byte.</summary>
        public int Data;
        public bool IsPool => RecSize <= 1;
    }

    /// <summary>Reads compiled .RDB files: _LIBRARY.RDB and page .RDB files.</summary>
    public sealed class RdbFile
    {
        public const int HeaderAt = 0x100;
        public const int DirAt = 0x110;
        public const int DirEntry = 32;
        public const int SectionHead = 52;
        public const int FieldDescSize = 40;

        public const int FText = 1;
        public const int FChars = 2;
        public const int FI16 = 3;
        public const int FI32 = 4;
        public const int FU8 = 8;
        public const int FPtr = 9;
        public const int FExpr = 15;
        public const int FCode = 16;
        public const int FU64 = 27;
        public const int FF64 = 28;
        public const int FTagVar = 47;

        public static bool IsNoText(ulong v) => v == 0xFFFFFFFFUL || v == 0xFFFFFFFFFFFFFFFFUL;

        public byte[] Data;
        public string Path;
        public int Version;
        public long Time;
        public long FileSize;
        public Dictionary<string, Section> Sections = new Dictionary<string, Section>(StringComparer.Ordinal);
        public List<string> Order = new List<string>();
        private byte[]? _text;

        private RdbFile(byte[] data, string path)
        {
            Data = data;
            Path = path;
        }

        public static RdbFile FromBytes(byte[] data, string path = "")
        {
            if (data.Length < DirAt)
                throw new RdbError(
                    $"{(path.Length > 0 ? path : "data")}: too short for an RDB ({data.Length} bytes)"
                );
            var f = new RdbFile(data, path)
            {
                Version = BitConverter.ToUInt16(data, HeaderAt),
                Time = BitConverter.ToUInt32(data, HeaderAt + 6),
                FileSize = BitConverter.ToUInt32(data, HeaderAt + 12),
            };
            int nsec = BitConverter.ToUInt16(data, HeaderAt + 10);
            int o = DirAt;
            for (int k = 0; k < nsec; k++)
            {
                if (o + DirEntry > data.Length)
                    throw new RdbError($"{path}: section directory runs past the end");
                long off = BitConverter.ToUInt32(data, o + 22);
                o += DirEntry;
                if (off + SectionHead > data.Length)
                    throw new RdbError($"{path}: section at 0x{off:x} runs past the end");
                Section sec = f.ReadSection((int)off);
                f.Sections[sec.Name] = sec;
                f.Order.Add(sec.Name);
            }
            return f;
        }

        public static RdbFile Open(string path) => FromBytes(File.ReadAllBytes(path), path);

        private static string Latin1Z(byte[] b, int start, int max)
        {
            int end = start;
            while (end < start + max && end < b.Length && b[end] != 0)
                end++;
            var c = new char[end - start];
            for (int i = 0; i < c.Length; i++)
                c[i] = (char)b[start + i];
            return new string(c);
        }

        private Section ReadSection(int off)
        {
            byte[] b = Data;
            var s = new Section
            {
                Name = Latin1Z(b, off, 32),
                NFields = BitConverter.ToUInt16(b, off + 32),
                RecSize = BitConverter.ToUInt16(b, off + 34),
                Size = BitConverter.ToUInt32(b, off + 36),
                NRec = BitConverter.ToUInt32(b, off + 40),
                Size2 = BitConverter.ToUInt32(b, off + 44),
                X = BitConverter.ToUInt32(b, off + 48),
                Start = off,
            };
            int p = off + SectionHead;
            for (int k = 0; k < s.NFields; k++)
            {
                if (p + FieldDescSize > b.Length)
                    throw new RdbError($"{Path}: field descriptor of {s.Name} runs past the end");
                string fname = Latin1Z(b, p, 32);
                int fsz = BitConverter.ToUInt16(b, p + 32);
                int foff = BitConverter.ToUInt16(b, p + 34);
                int ftype = (int)BitConverter.ToUInt32(b, p + 36);
                s.Fields.Add(new FieldDesc(fname, fsz, foff, ftype));
                p += FieldDescSize;
            }
            long end = p + (s.RecSize <= 1 ? s.NRec : s.NRec * s.RecSize);
            if (end > b.Length)
                throw new RdbError($"{Path}: section {s.Name} runs past the end");
            s.Data = p;
            return s;
        }

        // --- access ---------------------------------------------------------------------
        public bool Has(string name) => Sections.ContainsKey(name);

        public byte[] Pool(string name)
        {
            if (!Sections.TryGetValue(name, out var s))
                return Array.Empty<byte>();
            long n = s.IsPool ? s.NRec : s.NRec * s.RecSize;
            var r = new byte[n];
            Array.Copy(Data, s.Data, r, 0, n);
            return r;
        }

        private byte[] TextPool() => _text ??= Pool("TEXT");

        /// <summary>The TEXT pool string at <paramref name="offset"/> (null for the 'no text'
        /// marker or an offset past the pool).</summary>
        public string? Text(ulong offset)
        {
            if (IsNoText(offset))
                return null;
            byte[] t = TextPool();
            if (offset >= (ulong)t.Length)
                return null;
            return Cp1252.DecodeZ(t, (int)offset);
        }

        /// <summary>(byte offset of the record from the first record, record start in Data):
        /// the offset is what code operands hold (the n-th TAGREF is operand n x 38, the
        /// n-th CODEVAR n x 65, Library DEBUG = n x 12).</summary>
        public IEnumerable<(long Offset, int Start)> RawRecords(string name)
        {
            if (!Sections.TryGetValue(name, out var s) || s.IsPool)
                yield break;
            for (long i = 0; i < s.NRec; i++)
                yield return (i * s.RecSize, (int)(s.Data + i * s.RecSize));
        }

        /// <summary>Every record of <paramref name="name"/> decoded field by field ("_offset" =
        /// its section byte offset, "_index" = its number).</summary>
        public List<Dictionary<string, object?>> Records(string name)
        {
            var output = new List<Dictionary<string, object?>>();
            if (!Sections.TryGetValue(name, out var s) || s.IsPool)
                return output;
            for (long i = 0; i < s.NRec; i++)
            {
                int start = (int)(s.Data + i * s.RecSize);
                var d = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["_offset"] = i * s.RecSize,
                    ["_index"] = i,
                };
                foreach (var fd in s.Fields)
                    d[fd.Name] = DecodeField(fd, start, s.RecSize);
                output.Add(d);
            }
            return output;
        }

        /// <summary>One field of the record at <paramref name="recStart"/>: string (or null),
        /// long, double or byte[] (F_CODE, F_TAGVAR, unknown types).</summary>
        public object? DecodeField(FieldDesc fd, int recStart, int recSize)
        {
            // the slice rec[fd.offset : fd.offset + fd.size] of the record
            int a = recStart + Math.Min(fd.Offset, recSize);
            int b = recStart + Math.Min(fd.Offset + fd.Size, recSize);
            if (b > Data.Length)
                b = Data.Length;
            int len = Math.Max(0, b - a);
            switch (fd.Type)
            {
                case FText:
                    return Text(UInt(a, len));
                case FChars:
                    return Cp1252.DecodeZ(Data, a, len);
                case FI16:
                    return len >= 2 ? (long)BitConverter.ToInt16(Data, a) : 0L;
                case FI32:
                    return len >= 4 ? (long)BitConverter.ToInt32(Data, a) : 0L;
                case FU8:
                    return len >= 1 ? (long)Data[a] : 0L;
                case FPtr:
                case FU64:
                    return (long)UInt(a, len);
                case FF64:
                    return len >= 8 ? BitConverter.ToDouble(Data, a) : 0.0;
                case FExpr:
                    return (long)UInt(a, Math.Min(len, 4));
                default:
                {
                    var r = new byte[len];
                    Array.Copy(Data, a, r, 0, len);
                    return r;
                }
            }
        }

        private ulong UInt(int a, int len)
        {
            if (len >= 8)
                return BitConverter.ToUInt64(Data, a);
            if (len >= 4)
                return BitConverter.ToUInt32(Data, a);
            if (len >= 2)
                return BitConverter.ToUInt16(Data, a);
            return len >= 1 ? Data[a] : 0UL;
        }
    }

    /// <summary>The loaders of library / page RDB files and the _FUNCSYM cross-check.</summary>
    public static class Rdb
    {
        /// <summary>The Library RESULT field = _FUNCSYM TYPE: low 10 bits the return type, 0x400
        /// PRIVATE, 0x800 an explicit PUBLIC keyword.</summary>
        public const long ResultTypeMask = 0x3FF;
        public const long ResultPrivate = 0x400;
        public const long ResultPublic = 0x800;

        /// <summary>Tables whose records carry the animation point in a field named AN.</summary>
        public const string AnField = "AN";

        private static readonly HashSet<string> PoolSections = new HashSet<string>(StringComparer.Ordinal)
        {
            "TEXT",
            "SCALE",
            "RUN",
            "TAGREF",
            "FUNCLOOKUP",
            "CODEVAR",
            "REQUEST",
            "CODE",
            "FUNC",
            "Library",
            "DEBUG",
        };

        internal static long L(Dictionary<string, object?> d, string key, long dflt)
        {
            if (!d.TryGetValue(key, out var v) || v == null)
                return dflt;
            switch (v)
            {
                case long l:
                    return l;
                case double x:
                    return (long)x;
                default:
                    return dflt;
            }
        }

        /// <summary>The field, or dflt when it is missing or 0.</summary>
        internal static long LOr(Dictionary<string, object?> d, string key, long dflt)
        {
            long v = L(d, key, dflt);
            return v == 0 ? dflt : v;
        }

        internal static string S(Dictionary<string, object?> d, string key) =>
            d.TryGetValue(key, out var v) && v is string s ? s : "";

        internal static double F(Dictionary<string, object?> d, string key, double dflt)
        {
            if (!d.TryGetValue(key, out var v) || v == null)
                return dflt;
            return v switch
            {
                double x => x,
                long l => l,
                _ => dflt,
            };
        }

        private static void ProgramPools(RdbFile f, Program prog)
        {
            prog.Text = f.Pool("TEXT");
            prog.RunImage = f.Pool("RUN");
            foreach (var d in f.Records("TAGREF"))
            {
                long off = (long)d["_offset"]!;
                prog.TagRefs[off] = new TagRef(off)
                {
                    Cluster = S(d, "ClusterName"),
                    Tag = S(d, "TagName"),
                    Element = S(d, "TagElement"),
                    Item = S(d, "TagItem"),
                    Percentage = L(d, "Percentage", 0),
                    ValueHandle = L(d, "ValueHandle", 0),
                    Lightweight = L(d, "Lightweight", 0),
                };
            }
            foreach (var d in f.Records("CODEVAR"))
            {
                long off = (long)d["_offset"]!;
                var cv = new CodeVar(off, S(d, "CodeVarRef"), (int)L(d, "RawType", Types.Long))
                {
                    BitWidth = L(d, "BitWidth", 0),
                    LogicalUnit = L(d, "LogicalUnit", 32000),
                    UnitType = L(d, "UnitType", -1),
                    UnitAddress = L(d, "UnitAddress", 0),
                    UnitCount = LOr(d, "UnitCount", 1),
                    DataAddress = L(d, "DataAddress", 0),
                    Quality = (ulong)L(d, "Quality", 0xC0),
                    Error = L(d, "Error", 0),
                    Timestamp = (ulong)L(d, "Timestamp", 0),
                    QualityTimestamp = (ulong)L(d, "QualityTimestamp", 0),
                    ValueTimestamp = (ulong)L(d, "ValueTimestamp", 0),
                };
                prog.AddCodeVar(cv);
            }
            foreach (var d in f.Records("SCALE"))
            {
                long off = (long)d["_offset"]!;
                prog.Scales[off] = new Scale(off)
                {
                    RawZero = F(d, "RawZero", 0.0),
                    RawFull = F(d, "RawFull", 0.0),
                    EngZero = F(d, "EngZero", 0.0),
                    EngFull = F(d, "EngFull", 0.0),
                };
            }
            foreach (var d in f.Records("FUNCLOOKUP"))
            {
                long off = (long)d["_offset"]!;
                prog.FuncLookups[off] = new FuncLookup(off, S(d, "FunctionName"));
            }
        }

        /// <summary>The p-code of the block that starts at <paramref name="offset"/> in the CODE
        /// pool; its u16 length sits just before it.</summary>
        public static byte[] CodeBlockAt(byte[] code, long offset)
        {
            if (offset < 2 || offset > code.Length)
                return Array.Empty<byte>();
            int n = BitConverter.ToUInt16(code, (int)offset - 2);
            int len = (int)Math.Min(n, code.Length - offset);
            var r = new byte[len];
            Array.Copy(code, offset, r, 0, len);
            return r;
        }

        /// <summary>_LIBRARY.RDB as a Program: every Library record with its code, argument
        /// chars, result type, MINPARAM, file name, DEBUG locals; Index = the record number (the
        /// _FUNCSYM.DBF OP_CODE).</summary>
        public static Program LoadLibrary(string path) => LibraryFromRdb(RdbFile.Open(path));

        public static Program LibraryFromRdb(RdbFile f)
        {
            var prog = new Program { Source = f.Path };
            ProgramPools(f, prog);
            byte[] code = f.Pool("CODE");
            var debug = f.Records("DEBUG");
            var debugByOff = new Dictionary<long, int>();
            for (int k = 0; k < debug.Count; k++)
                debugByOff[(long)debug[k]["_offset"]!] = k;
            bool hasDebug = f.Sections.ContainsKey("DEBUG");
            var lib = f.Records("Library");
            for (int i = 0; i < lib.Count; i++)
            {
                var d = lib[i];
                byte[] cf =
                    d.TryGetValue("CODE", out var cfo) && cfo is byte[] cb && cb.Length > 0
                        ? cb
                        : Array.Empty<byte>();
                long coff = cf.Length >= 4 ? BitConverter.ToUInt32(cf, 0) : 0;
                byte[] body = CodeBlockAt(code, coff);
                string name = S(d, "NAME");
                string args = S(d, "ARGS");
                var locs = new List<(int, string)>();
                long cnt = L(d, "DEBUGCNT", 0);
                long dptr = L(d, "DEBUG", 0);
                if (cnt != 0 && hasDebug && debugByOff.TryGetValue(dptr, out int k0))
                {
                    for (int k = k0; k < k0 + cnt && k < debug.Count; k++)
                        locs.Add(((int)L(debug[k], "Offset", 0), S(debug[k], "Name")));
                }
                long res = L(d, "RESULT", Types.Void);
                var fn = new Function(name, args, (int)(res & ResultTypeMask), body)
                {
                    MinParam = (int)L(d, "MINPARAM", 0),
                    Filename = S(d, "FILENAME"),
                    Index = i,
                    Private = (res & ResultPrivate) != 0,
                    PublicKeyword = (res & ResultPublic) != 0,
                    Locals = locs,
                    CodeOffset = coff - 2,
                    CodeField = cf,
                    TypeWord = res,
                };
                prog.Functions.Add(fn);
            }
            return prog;
        }

        /// <summary>A page .RDB as a PageCode: every table's records, each type 15 (expression)
        /// / 16 (command) field that points at code as a PageBlock with the record's AN, and the
        /// page's own pools.</summary>
        public static PageCode LoadPage(string path) => PageFromRdb(RdbFile.Open(path));

        public static PageCode PageFromRdb(RdbFile f)
        {
            string name = f.Path.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(f.Path) : "";
            var pc = new PageCode(name);
            pc.Program.Source = f.Path;
            ProgramPools(f, pc.Program);
            byte[] code = f.Pool("CODE");
            foreach (string sname in f.Order)
            {
                if (PoolSections.Contains(sname))
                    continue;
                Section s = f.Sections[sname];
                if (s.IsPool)
                    continue;
                var recs = f.Records(sname);
                pc.Records[sname] = recs;
                var codeFields = s.Fields.FindAll(fd => fd.Type == RdbFile.FExpr || fd.Type == RdbFile.FCode);
                if (codeFields.Count == 0)
                    continue;
                bool hasAn = s.Fields.Exists(fd => fd.Name == AnField);
                foreach (var r in recs)
                {
                    long an = hasAn ? L(r, AnField, 0) : 0;
                    foreach (var fd in codeFields)
                    {
                        r.TryGetValue(fd.Name, out var v);
                        long? off = PageCodeOffset(fd, v);
                        if (off == null || off < 2 || off > code.Length)
                            continue;
                        pc.Blocks.Add(
                            new PageBlock(
                                sname,
                                (int)(long)r["_index"]!,
                                fd.Name,
                                fd.Type,
                                CodeBlockAt(code, off.Value),
                                an
                            )
                        );
                    }
                }
            }
            return pc;
        }

        /// <summary>The CODE offset a page code field holds, null when it holds no code.</summary>
        public static long? PageCodeOffset(FieldDesc fd, object? value)
        {
            ulong off;
            if (fd.Type == RdbFile.FExpr)
            {
                if (!(value is long l))
                    return null;
                off = (ulong)l;
            }
            else if (value is byte[] b && b.Length >= 4)
                off = BitConverter.ToUInt32(b, 0);
            else
                return null;
            if (RdbFile.IsNoText(off))
                return null;
            return (long)off;
        }

        // --------------------------------------------------------------------- _FUNCSYM.DBF
        /// <summary>_FUNCSYM.DBF rows (NAME, OP_CODE, ARGC, TYPE, RETURNED, MANDATORY), the
        /// compiler's function symbol table next to _LIBRARY.RDB.</summary>
        public static List<Dictionary<string, string>> LoadFuncSym(string path) => Dbf.CitectRows(path);

        /// <summary>(function, what differs) for every _FUNCSYM row that does not agree with its
        /// Library record.</summary>
        public static List<(string Function, string What)> FuncSymMismatches(
            Program prog,
            List<Dictionary<string, string>> rows
        )
        {
            var output = new List<(string, string)>();
            if (rows.Count != prog.Functions.Count)
                output.Add(("", $"{rows.Count} rows, {prog.Functions.Count} functions"));
            foreach (var r in rows)
            {
                string rname = r.TryGetValue("NAME", out var nm) ? nm : "";
                if (
                    !Dbf.ParseInt(r.TryGetValue("OP_CODE", out var opc) ? opc : "", out long k)
                    || !Dbf.ParseIntOrZero(r.TryGetValue("TYPE", out var ty) ? ty : "", out long t)
                )
                {
                    output.Add((rname, "OP_CODE / TYPE not a number"));
                    continue;
                }
                if (k < 0 || k >= prog.Functions.Count)
                {
                    output.Add((rname, $"OP_CODE {k} out of range"));
                    continue;
                }
                var f = prog.Functions[(int)k];
                r.TryGetValue("ARGC", out var argc);
                r.TryGetValue("MANDATORY", out var mand);
                r.TryGetValue("RETURNED", out var ret);
                Dbf.ParseIntOrZero(mand ?? "", out long mandatory);
                (string, bool)[] checks =
                {
                    ("NAME", f.Name == rname && r.ContainsKey("NAME")),
                    ("ARGC", f.Args == (argc ?? "")),
                    ("TYPE", f.TypeWord == t),
                    ("MANDATORY", mandatory == f.MinParam),
                    ("RETURNED", (f.Result != Types.Void) == (ret == "1")),
                };
                foreach (var (what, ok) in checks)
                    if (!ok)
                        output.Add((f.Name, what));
            }
            return output;
        }
    }
}
