using System;
using System.Collections.Generic;
using CicodeSim.Compiled;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Compiler.Syntax;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.Compiler.Codegen
{
    /// <summary>The TEXT pool: NUL-terminated Windows-1252 strings, deduplicated by their exact
    /// bytes, each placed at its first insertion; offset 0 = "".</summary>
    public sealed class TextPool
    {
        private readonly List<byte> _bytes = new List<byte>();
        private readonly Dictionary<string, long> _at = new Dictionary<string, long>(StringComparer.Ordinal);
        public readonly List<string> Order = new List<string>();

        public TextPool()
        {
            Add("");
        }

        /// <summary>A string; its spelling becomes the first spelling of its name for <see cref="AddName"/>
        /// (any string counts, not only names).</summary>
        public long Add(string s)
        {
            if (_at.TryGetValue(s, out long o))
                return o;
            o = _bytes.Count;
            _at[s] = o;
            string k = Lexer.NameKey(s);
            if (!_firstSpelling.ContainsKey(k))
                _firstSpelling[k] = s;
            Order.Add(s);
            _bytes.AddRange(Cp1252.Encode(s));
            _bytes.Add(0);
            return o;
        }

        private readonly Dictionary<string, string> _firstSpelling = new Dictionary<string, string>(
            StringComparer.Ordinal
        );

        /// <summary>An identifier, reusing the first spelling already in the pool.</summary>
        public string AddName(string s)
        {
            string k = Lexer.NameKey(s);
            if (_firstSpelling.TryGetValue(k, out string? first))
                return first;
            Add(s);
            return s;
        }

        public long Offset(string s) =>
            _at.TryGetValue(s, out long o) ? o : throw new KeyNotFoundException("TEXT has no \"" + s + "\"");

        public bool Has(string s) => _at.ContainsKey(s);

        public byte[] Bytes() => _bytes.ToArray();
    }

    /// <summary>One CODEVAR record (65 bytes).</summary>
    public sealed class CodeVarRec
    {
        public VarRef Var = null!;
        public string Name = "";
        public int RawType;
        public int BitWidth;
        public int UnitType;
        public int UnitAddress;
        public int UnitCount;
        public long DataAddress;
        public int Index;
        public long Offset => 65L * Index;
    }

    /// <summary>One Library record (70 bytes).</summary>
    public sealed class LibraryRec
    {
        public FuncInfo Info = null!;
        public string Name = "";
        public string Args = "";
        public int Result;
        public long CodeOffset;
        public long FuncOffset;
        public long RequestOffset;
        public string Filename = "";
        public int DebugCount;
        public long DebugOffset;
        public int MinParam;
        public byte[] Code = Array.Empty<byte>();
        public FunctionCode Gen = null!;
    }

    /// <summary>A compiled library as CtCmp32 lays it out: the pools in its record order, every
    /// function's code, the per-function lists (REQUEST, FUNC, DEBUG) and the CODE relocations.</summary>
    public sealed class LibraryImage : IPoolOffsets
    {
        public readonly TextPool TextPool = new TextPool();
        public readonly List<TagKey> TagRefs = new List<TagKey>();
        public readonly Dictionary<TagKey, int> TagIndex = new Dictionary<TagKey, int>();
        public readonly List<ScaleKey> Scales = new List<ScaleKey>();
        public readonly Dictionary<ScaleKey, int> ScaleIndex = new Dictionary<ScaleKey, int>();
        public readonly List<CodeVarRec> CodeVars = new List<CodeVarRec>();
        public readonly Dictionary<VarRef, CodeVarRec> CodeVarOf = new Dictionary<VarRef, CodeVarRec>();
        public byte[] Run = Array.Empty<byte>();
        public readonly List<FuncInfo> FuncLookups = new List<FuncInfo>();
        public readonly Dictionary<FuncInfo, int> FuncLookupIndex = new Dictionary<FuncInfo, int>();
        public readonly List<long> Requests = new List<long> { -1 };
        public readonly List<byte> CodePool = new List<byte>();
        public readonly List<byte> FuncPool = new List<byte> { 0xFF, 0xFF };
        public readonly List<LibraryRec> Library = new List<LibraryRec>();
        public readonly List<(int slot, string name)> Debug = new List<(int, string)>();

        /// <summary>CODE relocations: (byte offset in the CODE pool, target section index).</summary>
        public readonly List<(long at, int section)> CodeRelocs = new List<(long, int)>();

        /// <summary>Functions the generator could not compile: (name, why).</summary>
        public readonly List<(string name, string why)> Failed = new List<(string, string)>();

        public long Text(string s) => TextPool.Offset(s);

        public long Tag(TagKey t) => 38L * TagIndex[t];

        public long CodeVar(VarRef v) => CodeVarOf[v].Offset;

        public long DataAddress(VarRef v) => CodeVarOf[v].DataAddress;

        public long FuncLookup(FuncInfo f) => 12L * FuncLookupIndex[f];

        public long Scale(ScaleKey s) => 32L * ScaleIndex[s];

        /// <summary>The library as the VM loads it (what <see cref="Rdb.LibraryFromRdb"/> gives
        /// for the written file): the same pools and offsets, Locals in DEBUG order.</summary>
        private string? NotCompiledWhy(string name)
        {
            foreach (var (n, why) in Failed)
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                    return why;
            return null;
        }

        public Program ToProgram(string source = "")
        {
            var prog = new Program
            {
                Source = source,
                Text = TextPool.Bytes(),
                RunImage = Run,
            };
            for (int i = 0; i < TagRefs.Count; i++)
            {
                var t = TagRefs[i];
                long off = 38L * i;
                prog.TagRefs[off] = new Compiled.TagRef(off)
                {
                    Cluster = t.Cluster,
                    Tag = t.Tag,
                    Element = t.Element,
                    Item = t.Item,
                    Percentage = t.Percentage,
                    ValueHandle = -1,
                    Lightweight = t.Lightweight,
                };
            }
            foreach (var c in CodeVars)
            {
                prog.AddCodeVar(
                    new Compiled.CodeVar(c.Offset, c.Name, c.RawType)
                    {
                        BitWidth = c.BitWidth,
                        UnitType = c.UnitType,
                        UnitAddress = c.UnitAddress,
                        UnitCount = c.UnitCount,
                        DataAddress = c.DataAddress,
                    }
                );
            }
            for (int i = 0; i < Scales.Count; i++)
            {
                var s = Scales[i];
                prog.Scales[32L * i] = new Compiled.Scale(32L * i)
                {
                    RawZero = s.RawZero,
                    RawFull = s.RawFull,
                    EngZero = s.EngZero,
                    EngFull = s.EngFull,
                };
            }
            for (int i = 0; i < FuncLookups.Count; i++)
                prog.FuncLookups[12L * i] = new Compiled.FuncLookup(12L * i, FuncLookups[i].SymbolName);
            for (int i = 0; i < Library.Count; i++)
            {
                var r = Library[i];
                var cf = new byte[32];
                CodeBuffer.PutI64(cf, 0, r.CodeOffset);
                CodeBuffer.PutI64(cf, 8, r.FuncOffset);
                CodeBuffer.PutI64(cf, 16, r.RequestOffset);
                var locs = new List<(int, string)>();
                if (r.DebugCount > 0)
                {
                    int k0 = (int)(r.DebugOffset / 12);
                    for (int k = k0; k < k0 + r.DebugCount; k++)
                        locs.Add(Debug[k]);
                }
                prog.Functions.Add(
                    new Function(r.Name, r.Args, r.Result & 0x3FF, r.Code)
                    {
                        MinParam = r.MinParam,
                        Filename = r.Filename,
                        Index = i,
                        Private = (r.Result & 0x400) != 0,
                        PublicKeyword = (r.Result & 0x800) != 0,
                        Locals = locs,
                        CodeOffset = r.CodeOffset - 2,
                        CodeField = cf,
                        TypeWord = r.Result,
                        NotCompiled = NotCompiledWhy(r.Name),
                    }
                );
            }
            return prog;
        }
    }

    /// <summary>Builds a whole library from a compiled front-end unit, as CtCmp32 does.</summary>
    public sealed class LibraryBuilder
    {
        private readonly CompileUnit _unit;
        private readonly TagTable _tags;
        private readonly LibraryImage _img = new LibraryImage();
        private readonly HashSet<VarRef> _declared = new HashSet<VarRef>();
        private readonly Dictionary<string, Expr?> _defaults = new Dictionary<string, Expr?>(
            StringComparer.Ordinal
        );
        private readonly Dictionary<FunctionDef, FuncInfo> _infoOf = new Dictionary<FunctionDef, FuncInfo>();
        private readonly Dictionary<FuncInfo, FunctionCode> _code = new Dictionary<FuncInfo, FunctionCode>();

        /// <summary>Per file (the unit's Files index) its MODULE declarations' running index.</summary>
        private readonly Dictionary<VarDecl, (int unit, int address)> _place =
            new Dictionary<VarDecl, (int, int)>();

        /// <summary>Precompiled function code (an incremental build reuses unchanged files').</summary>
        public Func<FuncInfo, FunctionCode?>? Reuse;

        /// <summary>Code kept between builds (an incremental compile): used when there, filled with
        /// what this build generates.</summary>
        public Dictionary<FuncInfo, FunctionCode>? Cache;

        public LibraryBuilder(CompileUnit unit, TagTable? tags = null)
        {
            _unit = unit;
            _tags = tags ?? TagTable.For(unit);
        }

        public static LibraryImage Build(CompileUnit unit, TagTable? tags = null) =>
            new LibraryBuilder(unit, tags).Run();

        public LibraryImage Run()
        {
            foreach (var fi in _unit.Functions)
                _infoOf[fi.Node] = fi;
            PlaceVariables();
            Pass1();
            Pass2();
            RunImage();
            return _img;
        }

        /// <summary>UnitType / UnitAddress of every declared variable: MODULE = (the file's unit
        /// number, its index among the file's non-GLOBAL declarations), GLOBAL = (-1, its index
        /// among the unit's GLOBALs in compile order).</summary>
        private void PlaceVariables()
        {
            int g = 0;
            for (int fi = 0; fi < _unit.Files.Count; fi++)
            {
                int m = 0;
                foreach (var it in _unit.Files[fi].Module.Items)
                {
                    if (!(it is VarDecl v))
                        continue;
                    if (v.IsGlobal)
                        _place[v] = (-1, g++);
                    else
                        _place[v] = (fi, m++); // System\citect.ci is 0, Include's first file 1
                }
            }
        }

        // ----------------------------------------------------------------------------- pass 1
        private void Pass1()
        {
            foreach (var f in _unit.Files)
            {
                foreach (var it in f.Module.Items)
                {
                    if (it is VarDecl v)
                    {
                        if (v.Var == null)
                            continue;
                        _declared.Add(v.Var);
                        if (v.Init.Count > 0)
                            NewCodeVar(v.Var);
                        continue;
                    }
                    if (!(it is FunctionDef fd) || !_infoOf.TryGetValue(fd, out var info))
                        continue;
                    FunctionCode? code = Reuse?.Invoke(info);
                    if (code == null && Cache != null)
                        Cache.TryGetValue(info, out code);
                    string? stopped = code == null ? _unit.StopReason(info.File) : null;
                    bool placeholder = false;
                    if (stopped != null)
                    {
                        // the compile stops before this body (an include-row or declaration error):
                        // not compiled, said as that error, not as its unresolved names
                        _img.Failed.Add((info.SymbolName, stopped));
                        code = new FunctionCode { Info = info };
                        code.Code.Add(Op.End);
                        placeholder = true;
                    }
                    if (code == null)
                    {
                        try
                        {
                            code = FunctionGen.Function(_unit, _tags, info, _defaults);
                        }
                        catch (CodegenException e)
                        {
                            _img.Failed.Add(
                                (info.SymbolName, e.Message + (e.Line > 0 ? " (line " + e.Line + ")" : ""))
                            );
                            code = new FunctionCode { Info = info };
                            code.Code.Add(Op.End);
                            placeholder = true;
                        }
                    }
                    _code[info] = code;
                    // (a placeholder is not kept: the next build says again why, or compiles it)
                    if (Cache != null && !placeholder)
                        Cache[info] = code;
                    foreach (var ev in code.Events)
                    {
                        if (ev.Kind == 0)
                        {
                            var vr = ev.Var!;
                            if (_img.CodeVarOf.ContainsKey(vr))
                                continue;
                            if (vr.Kind == "global" && !_declared.Contains(vr))
                                continue; // declared later: nothing yet
                            NewCodeVar(vr);
                        }
                        else if (ev.Kind == 1)
                        {
                            var callee = ev.Callee!;
                            if (callee.Index <= info.Index)
                                NewFuncLookup(callee);
                        }
                        else
                            NewScale(ev.Tag!);
                    }
                }
            }
        }

        private void NewCodeVar(VarRef v)
        {
            if (_img.CodeVarOf.ContainsKey(v))
                return;
            var decl = v.Decl as VarDecl;
            var (unitType, addr) = decl != null && _place.TryGetValue(decl, out var pl) ? pl : (-1, 0);
            long count = 1;
            foreach (long d in v.Dims)
                count *= Math.Max(d, 0);
            int raw = FunctionGen.RawType(v.Type);
            var rec = new CodeVarRec
            {
                Var = v,
                Name = decl != null ? decl.Name : v.Name,
                RawType = raw,
                BitWidth = BitWidth(raw),
                UnitType = unitType,
                UnitAddress = addr,
                UnitCount = (int)count,
                Index = _img.CodeVars.Count,
            };
            long da = 0;
            if (_img.CodeVars.Count > 0)
            {
                var last = _img.CodeVars[_img.CodeVars.Count - 1];
                da = last.DataAddress + (long)last.BitWidth / 8 * last.UnitCount;
            }
            rec.DataAddress = da;
            _img.CodeVars.Add(rec);
            _img.CodeVarOf[v] = rec;
            rec.Name = _img.TextPool.AddName(rec.Name);
        }

        /// <summary>BitWidth by RawType: INT 32, STRING 2048, REAL 64, TIMESTAMP 64, OBJECT 32,
        /// QUALITY 64 (assumed).</summary>
        public static int BitWidth(int raw)
        {
            switch (raw)
            {
                case Types.String:
                    return 2048;
                case Types.LongReal:
                case Types.Real:
                case Types.Timestamp:
                case Types.Quality:
                    return 64;
            }
            return 32;
        }

        private void NewFuncLookup(FuncInfo f)
        {
            if (_img.FuncLookupIndex.ContainsKey(f))
                return;
            _img.FuncLookupIndex[f] = _img.FuncLookups.Count;
            _img.FuncLookups.Add(f);
            _img.TextPool.Add(f.SymbolName);
        }

        private void NewTag(TagKey t)
        {
            if (_img.TagIndex.ContainsKey(t))
                return;
            _img.TagIndex[t] = _img.TagRefs.Count;
            _img.TagRefs.Add(t);
            _img.TextPool.Add(t.Cluster);
            _img.TextPool.Add(t.Tag);
            _img.TextPool.Add(t.Element);
            _img.TextPool.Add(t.Item);
        }

        // ----------------------------------------------------------------------------- pass 2
        private void Pass2()
        {
            var text = _img.TextPool;
            foreach (var info in _unit.Functions)
            {
                if (!_code.TryGetValue(info, out var code))
                    continue;
                var loads = new SortedSet<long>();
                foreach (var ins in code.Code.Ins)
                {
                    switch (ins.Op)
                    {
                        case Op.FuncCallByLookup:
                            NewFuncLookup((FuncInfo)ins.Ref!);
                            break;
                        case Op.Const:
                            if (ins.A == Types.String)
                                text.Add((string)ins.Ref!);
                            break;
                        case Op.StringFmt:
                        case Op.AssFmt:
                            text.Add((string)ins.Ref!);
                            break;
                        case Op.Load:
                        case Op.Store:
                            NewTag((TagKey)ins.Ref!);
                            if (ins.Op == Op.Load)
                                loads.Add(_img.Tag((TagKey)ins.Ref!));
                            break;
                        case Op.LoadCodeVar:
                        case Op.StoreCodeVar:
                        case Op.AddrCodeVar:
                            NewCodeVar((VarRef)ins.Ref!); // a GLOBAL only named before its declaration
                            break;
                    }
                }
                // the bytes (the pools hold every operand now); code the runtime cannot hold (a
                // jump beyond 16 bits, more than the u16 length word) is not compiled
                byte[] bytes;
                try
                {
                    bytes = Bytes(code);
                }
                catch (CodegenException e)
                {
                    _img.Failed.Add((info.SymbolName, e.Message));
                    code = new FunctionCode { Info = info };
                    code.Code.Add(Op.End);
                    loads.Clear();
                    bytes = Bytes(code);
                }
                var rec = new LibraryRec
                {
                    Info = info,
                    Name = info.SymbolName,
                    Gen = code,
                    Result = info.TypeWord,
                    MinParam = info.Mandatory,
                    Filename = FileName(info),
                };
                var args = new char[info.ArgTypes.Length];
                for (int i = 0; i < args.Length; i++)
                    args[i] = Types.SigChar(info.ArgTypes[i]);
                rec.Args = new string(args);
                text.Add(rec.Name);
                text.Add(rec.Filename);
                var dbg = DebugOrder(code.Locals);
                foreach (var (_, nm) in dbg)
                    text.Add(nm);
                text.Add(rec.Args);
                // REQUEST: the distinct TAGREF offsets the function loads, ascending, then -1
                rec.RequestOffset = 8L * _img.Requests.Count;
                foreach (long o in loads)
                    _img.Requests.Add(o);
                _img.Requests.Add(-1);
                // DEBUG
                rec.DebugCount = dbg.Count;
                rec.DebugOffset = dbg.Count > 0 ? 12L * _img.Debug.Count : -1;
                _img.Debug.AddRange(dbg);
                // FUNC: the distinct callee indexes in parse pre-order, then 0xFFFF
                if (code.Callees.Count > 0)
                {
                    rec.FuncOffset = _img.FuncPool.Count;
                    foreach (var c in code.Callees)
                    {
                        _img.FuncPool.Add((byte)c.Index);
                        _img.FuncPool.Add((byte)(c.Index >> 8));
                    }
                    _img.FuncPool.Add(0xFF);
                    _img.FuncPool.Add(0xFF);
                }
                // CODE
                rec.Code = bytes;
                _img.CodePool.Add((byte)bytes.Length);
                _img.CodePool.Add((byte)(bytes.Length >> 8));
                rec.CodeOffset = _img.CodePool.Count;
                foreach (var ins in code.Code.Ins)
                {
                    long at = rec.CodeOffset + ins.Pc;
                    switch (ins.Op)
                    {
                        case Op.Const:
                            if (ins.A == Types.String)
                                _img.CodeRelocs.Add((at + 2, 0));
                            break;
                        case Op.Load:
                        case Op.Store:
                            _img.CodeRelocs.Add((at + 1, 3));
                            break;
                        case Op.LoadCodeVar:
                        case Op.StoreCodeVar:
                            _img.CodeRelocs.Add((at + 1, 5));
                            break;
                        case Op.AddrCodeVar:
                            _img.CodeRelocs.Add((at + 2, 2));
                            break;
                        case Op.StringFmt:
                            _img.CodeRelocs.Add((at + 5, 0));
                            break;
                        case Op.FuncCallByLookup:
                            _img.CodeRelocs.Add((at + 1, 4));
                            break;
                    }
                }
                _img.CodePool.AddRange(bytes);
                _img.Library.Add(rec);
            }
        }

        /// <summary>A function's bytes; more than its u16 length word holds is not compiled.</summary>
        internal static byte[] Bytes(FunctionCode code, IPoolOffsets pools)
        {
            byte[] b = code.Code.Encode(pools);
            if (b.Length > CodeBuffer.MaxFunctionBytes)
                throw new CodegenException(
                    "the code is "
                        + b.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " bytes: more than the 65,535 a function's length word holds"
                );
            return b;
        }

        private byte[] Bytes(FunctionCode code) => Bytes(code, _img);

        /// <summary>FILENAME = the project's MASTER.DBF PATH + the file name as enumerated.</summary>
        internal string FileName(FuncInfo info)
        {
            foreach (var f in _unit.Files)
            {
                if (f.Path != info.File)
                    continue;
                string? path = ProjectPath(f.Project);
                if (path == null)
                    return f.Path;
                return path.EndsWith("\\", StringComparison.Ordinal) ? path + f.Name : path + "\\" + f.Name;
            }
            return info.File;
        }

        private readonly Dictionary<SourceProject, string?> _paths = new Dictionary<SourceProject, string?>();

        /// <summary>The PATH column of the project's MASTER.DBF row, as written there.</summary>
        private string? ProjectPath(SourceProject p)
        {
            if (_paths.TryGetValue(p, out var s))
                return s;
            s = null;
            if (!string.IsNullOrEmpty(p.Folder))
            {
                string? user = WinPath.DirName(WinPath.Clean(p.Folder!).TrimEnd('\\', '/'));
                string? master = user != null ? Unit0.Find(user, "master.dbf") : null;
                if (master != null)
                    foreach (var r in Unit0.Rows(master, new[] { "NAME", "PATH" }))
                        if (CitectText.Lower(CitectText.Trim(r[0])) == p.Key)
                        {
                            string path = CitectText.Trim(r[1]);
                            if (path.Length > 0)
                                s = path;
                            break;
                        }
                if (s == null)
                    s = WinPath.Clean(p.Folder!).TrimEnd('\\') + "\\";
            }
            _paths[p] = s;
            return s;
        }

        // ------------------------------------------------------------------------------ DEBUG
        /// <summary>The DEBUG order inside a function: ascending buckets of CtCmp32's 1021-bucket
        /// hash of the name (ELF hash over toupper bytes, bytes >= 0x80 sign-extended), equal buckets
        /// in slot order.</summary>
        public static List<(int slot, string name)> DebugOrder(List<(int slot, string name)> locals)
        {
            var keyed = new List<(uint b, int slot, string name)>(locals.Count);
            foreach (var (slot, name) in locals)
                keyed.Add((Bucket(name), slot, name));
            keyed.Sort((a, b) => a.b != b.b ? a.b.CompareTo(b.b) : a.slot.CompareTo(b.slot));
            var o = new List<(int, string)>(keyed.Count);
            foreach (var k in keyed)
                o.Add((k.slot, k.name));
            return o;
        }

        public static uint Bucket(string name)
        {
            uint h = 0;
            foreach (byte c in Cp1252.Encode(name))
            {
                int v = c < 0x80 ? (c >= 'a' && c <= 'z' ? c - 32 : c) : c - 256;
                h = (h << 4) + (uint)v;
                uint g = h & 0xF0000000u;
                if (g != 0)
                {
                    h ^= g >> 24;
                    h &= ~g;
                }
            }
            return h % 1021;
        }

        // ------------------------------------------------------------------------------ SCALE
        /// <summary>The distinct scales of the tags in the order the parser reads their names
        /// (pass 1); tags with no scale field and degenerate ranges skipped.</summary>
        private void NewScale(TagRow row)
        {
            var s = row.Scale();
            if (s == null)
                return;
            var sk = s.Value;
            if (sk.RawZero == sk.RawFull || sk.EngZero == sk.EngFull)
                return;
            if (_img.ScaleIndex.ContainsKey(sk))
                return;
            _img.ScaleIndex[sk] = _img.Scales.Count;
            _img.Scales.Add(sk);
        }

        // -------------------------------------------------------------------------------- RUN
        /// <summary>The RUN image: each record's initial value at its DataAddress (STRING = the
        /// bytes + NUL in a 256-byte slot, INT i32, REAL f64, TIMESTAMP i64; uninitialised zero,
        /// OBJECT -1).</summary>
        private void RunImage()
        {
            long size = 0;
            if (_img.CodeVars.Count > 0)
            {
                var last = _img.CodeVars[_img.CodeVars.Count - 1];
                size = last.DataAddress + (long)last.BitWidth / 8 * last.UnitCount;
            }
            var run = new byte[size];
            foreach (var cv in _img.CodeVars)
            {
                var decl = cv.Var.Decl as VarDecl;
                int esz = cv.BitWidth / 8;
                for (int k = 0; k < cv.UnitCount; k++)
                {
                    long at = cv.DataAddress + (long)esz * k;
                    Literal? lit = decl != null && k < decl.Init.Count ? decl.Init[k] : null;
                    switch (cv.RawType)
                    {
                        case Types.String:
                            if (lit != null)
                            {
                                byte[] s = Cp1252.Encode(
                                    lit.Kind == "STRING" ? lit.SValue ?? "" : LiteralText(lit)
                                );
                                Array.Copy(s, 0, run, at, Math.Min(s.Length, 255));
                            }
                            break;
                        case Types.LongReal:
                        case Types.Real:
                            if (lit != null)
                                CodeBuffer.PutI64(
                                    run,
                                    (int)at,
                                    BitConverter.DoubleToInt64Bits(
                                        lit.Kind == "REAL" ? lit.RValue : lit.IValue
                                    )
                                );
                            break;
                        case Types.Object:
                            CodeBuffer.PutI32(run, (int)at, lit != null ? (int)lit.IValue : -1);
                            break;
                        case Types.Timestamp:
                        case Types.Quality:
                            if (lit != null)
                                CodeBuffer.PutI64(run, (int)at, lit.IValue);
                            break;
                        default:
                            if (lit != null)
                                CodeBuffer.PutI32(
                                    run,
                                    (int)at,
                                    lit.Kind == "REAL" ? Int32Of(lit.RValue) : unchecked((int)lit.IValue)
                                );
                            break;
                    }
                }
            }
            _img.Run = run;
        }

        /// <summary>A REAL initialiser of an INT variable as an i32, as x86 truncates it.</summary>
        internal static int Int32Of(double d) =>
            double.IsNaN(d) || d >= 2147483648.0 || d <= -2147483649.0 ? int.MinValue : (int)d;

        private static string LiteralText(Literal l) =>
            l.Kind == "REAL"
                ? FloatText.Shortest(l.RValue)
                : l.IValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
