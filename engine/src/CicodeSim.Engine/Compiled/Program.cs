using System;
using System.Collections.Generic;
using CicodeSim.Values;

namespace CicodeSim.Compiled
{
    /// <summary>A tag / Equip.Item reference used by code (TAGREF section).</summary>
    public sealed class TagRef
    {
        public long Offset;
        public string Cluster = "";
        public string Tag = "";

        /// <summary>Tag extension: Value, Q, T, Field ...</summary>
        public string Element = "";

        /// <summary>Equipment item.</summary>
        public string Item = "";
        public long Percentage;
        public long ValueHandle;
        public long Lightweight;

        public TagRef(long offset)
        {
            Offset = offset;
        }
    }

    /// <summary>A MODULE / GLOBAL variable (CODEVAR section). Lives in a virtual I/O unit
    /// (LogicalUnit 32000) and carries quality + timestamps like a tag; its initial value is in
    /// the RUN image at DataAddress.</summary>
    public sealed class CodeVar
    {
        public long Offset;
        public string Name;

        /// <summary>Types.Long / String / Real / Object / Timestamp / Quality.</summary>
        public int RawType;

        /// <summary>STRING = 2048 (256 bytes).</summary>
        public long BitWidth;
        public long LogicalUnit = 32000;

        /// <summary>-1 GLOBAL, else a per-file id.</summary>
        public long UnitType = -1;
        public long UnitAddress;

        /// <summary>Array element count.</summary>
        public long UnitCount = 1;

        /// <summary>Array dimensions (row-major), from the Index prefix / source.</summary>
        public int[] Dims = Array.Empty<int>();
        public long DataAddress;
        public ulong Quality = 0xC0;
        public long Error;
        public ulong Timestamp;
        public ulong QualityTimestamp;
        public ulong ValueTimestamp;

        public CodeVar(long offset, string name, int rawType)
        {
            Offset = offset;
            Name = name;
            RawType = rawType;
        }
    }

    /// <summary>A (expr)^(min,max) / Bar range (SCALE section, four f64).</summary>
    public sealed class Scale
    {
        public long Offset;
        public double RawZero;
        public double RawFull = 100.0;
        public double EngZero;
        public double EngFull = 100.0;

        public Scale(long offset)
        {
            Offset = offset;
        }
    }

    /// <summary>A late-bound user call target (FUNCLOOKUP section); resolved by name on first
    /// call.</summary>
    public sealed class FuncLookup
    {
        public long Offset;
        public string Name;

        public FuncLookup(long offset, string name)
        {
            Offset = offset;
            Name = name;
        }
    }

    /// <summary>One compiled Cicode function (Library record + its CODE bytes).</summary>
    public sealed class Function
    {
        /// <summary>As stored: PRIVATE functions are 'name@file@project'.</summary>
        public string Name;

        /// <summary>One type char per argument (Types.SigChar).</summary>
        public string Args;

        /// <summary>Return type code (Types.Void for none).</summary>
        public int Result;

        /// <summary>The p-code (without the u16 length prefix), ends with 0xff.</summary>
        public byte[] Code;

        /// <summary>Arguments without a default.</summary>
        public int MinParam;

        /// <summary>Full .ci path.</summary>
        public string Filename = "";

        /// <summary>_FUNCSYM OP_CODE (function handle).</summary>
        public int Index = -1;
        public bool Private;

        /// <summary>Declared with an explicit PUBLIC (_FUNCSYM flag 0x800).</summary>
        public bool PublicKeyword;

        /// <summary>DEBUG: (stack slot, name).</summary>
        public List<(int Slot, string Name)> Locals = new List<(int, string)>();

        /// <summary>Offset of the u16 length in the CODE section (on disk).</summary>
        public long CodeOffset = -1;

        /// <summary>The Library CODE field bytes (32).</summary>
        public byte[]? CodeField;

        /// <summary>The Library RESULT field = _FUNCSYM TYPE word.</summary>
        public long TypeWord = -1;

        /// <summary>Not null: the function did not compile from source and there is no compiled
        /// code for it either; a call halts with this reason (the simulator's, not the runtime's:
        /// the runtime has no library at all while the project does not compile).</summary>
        public string? NotCompiled;

        /// <summary>The VM's decoded form of <see cref="Code"/> (filled on first call).</summary>
        internal object? Decoded;

        public Function(string name, string args, int result, byte[] code)
        {
            Name = name;
            Args = args;
            Result = result;
            Code = code;
        }

        public string Key => Name.ToLowerInvariant();
    }

    /// <summary>A compiled library: every function of one compile unit + the pools its code
    /// points into. Code operands are byte offsets into the pools, as on disk.</summary>
    public sealed class Program
    {
        public byte[] Text = Array.Empty<byte>();
        public List<Function> Functions = new List<Function>();
        public Dictionary<long, TagRef> TagRefs = new Dictionary<long, TagRef>();

        /// <summary>Insertion order = record order.</summary>
        public Dictionary<long, CodeVar> CodeVars = new Dictionary<long, CodeVar>();
        public List<CodeVar> CodeVarOrder = new List<CodeVar>();
        public Dictionary<long, Scale> Scales = new Dictionary<long, Scale>();
        public Dictionary<long, FuncLookup> FuncLookups = new Dictionary<long, FuncLookup>();
        public byte[] RunImage = Array.Empty<byte>();

        /// <summary>The .RDB path, or 'source:&lt;project&gt;' for the compiler.</summary>
        public string Source = "";

        private Dictionary<string, Function>? _byName;
        private int _byNameCount = -1;
        private Dictionary<long, CodeVar>? _byData;
        private int _byDataCount = -1;

        /// <summary>The variable at RUN-image address ``dataAddress``, or null.</summary>
        public CodeVar? CodeVarAtData(long dataAddress)
        {
            var d = _byData;
            if (d == null || _byDataCount != CodeVarOrder.Count)
            {
                d = new Dictionary<long, CodeVar>();
                foreach (var cv in CodeVarOrder)
                    if (!d.ContainsKey(cv.DataAddress))
                        d[cv.DataAddress] = cv;
                _byData = d;
                _byDataCount = CodeVarOrder.Count;
            }
            return d.TryGetValue(dataAddress, out var r) ? r : null;
        }

        public void AddCodeVar(CodeVar cv)
        {
            _byData = null;
            if (!CodeVars.ContainsKey(cv.Offset))
                CodeVarOrder.Add(cv);
            else
                CodeVarOrder[CodeVarOrder.FindIndex(x => x.Offset == cv.Offset)] = cv;
            CodeVars[cv.Offset] = cv;
        }

        /// <summary>The NUL-terminated pool string at <paramref name="offset"/> (lossless
        /// Windows-1252); "" past the end.</summary>
        public string StringAt(long offset)
        {
            if (offset < 0 || offset >= Text.Length)
                return "";
            return Cp1252.DecodeZ(Text, (int)offset);
        }

        /// <summary>Look a function up as the runtime does: a PRIVATE 'name@file@project' of the
        /// caller's file first, then the public name; case-insensitive.</summary>
        public Function? Function(string name, string? file = null, string? project = null)
        {
            var idx = Index();
            string low = name.ToLowerInvariant();
            if (!string.IsNullOrEmpty(file) && !string.IsNullOrEmpty(project))
            {
                if (
                    idx.TryGetValue(
                        low + "@" + file!.ToLowerInvariant() + "@" + project!.ToLowerInvariant(),
                        out var p
                    )
                )
                    return p;
            }
            return idx.TryGetValue(low, out var f) ? f : null;
        }

        private Dictionary<string, Function> Index()
        {
            var c = _byName;
            if (c == null || _byNameCount != Functions.Count)
            {
                c = new Dictionary<string, Function>(StringComparer.Ordinal);
                foreach (var f in Functions)
                    c[f.Key] = f; // a later duplicate wins
                _byName = c;
                _byNameCount = Functions.Count;
            }
            return c;
        }
    }

    /// <summary>One expression / command of a compiled page (a page .RDB record field of type
    /// 15 = expression or 16 = command).</summary>
    public sealed class PageBlock
    {
        /// <summary>PGTEXT, PGSYM, PGNUM, PGBAR, PGSTR, PGADV, PGBUTTON ...</summary>
        public string Table;
        public int Record;
        public string Field;

        /// <summary>15 expression, 16 command.</summary>
        public int Kind;
        public byte[] Code;

        /// <summary>The record's animation point, when the table has one.</summary>
        public long An;

        public PageBlock(string table, int record, string field, int kind, byte[] code, long an)
        {
            Table = table;
            Record = record;
            Field = field;
            Kind = kind;
            Code = code;
            An = an;
        }
    }

    /// <summary>A compiled page: its records' code blocks + the page's own pools.</summary>
    public sealed class PageCode
    {
        public string Page;
        public List<PageBlock> Blocks = new List<PageBlock>();

        /// <summary>The page's TEXT / TAGREF / SCALE ...</summary>
        public Program Program = new Program();

        /// <summary>Every table's raw records.</summary>
        public Dictionary<string, List<Dictionary<string, object?>>> Records = new Dictionary<
            string,
            List<Dictionary<string, object?>>
        >(StringComparer.Ordinal);

        public PageCode(string page)
        {
            Page = page;
        }
    }
}
