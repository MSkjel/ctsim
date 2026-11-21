using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using CicodeSim.Compiled;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Compiler.Syntax;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.Compiler.Codegen
{
    /// <summary>The source compiler, kept warm.</summary>
    public sealed class SourceLibrary
    {
        public CompileUnit Unit { get; private set; }
        public TagTable Tags { get; private set; }
        private readonly Func<CompileUnit> _make;
        private readonly Dictionary<string, string> _overrides;
        private Dictionary<FuncInfo, FunctionCode> _code = new Dictionary<FuncInfo, FunctionCode>();
        private readonly Dictionary<string, Expr?> _defaults = new Dictionary<string, Expr?>(
            StringComparer.Ordinal
        );

        /// <summary>Milliseconds the last front-end compile took (the whole unit).</summary>
        public double FrontEndMs { get; private set; }

        private SourceLibrary(Func<CompileUnit> make, Dictionary<string, string> overrides)
        {
            _make = make;
            _overrides = overrides;
            var sw = Stopwatch.StartNew();
            Unit = Build();
            FrontEndMs = sw.Elapsed.TotalMilliseconds;
            Tags = TagTable.For(Unit);
        }

        /// <summary>The unit of a project (a registered name, or a project folder; see
        /// <see cref="Units.ForProject"/>) with unsaved texts in place of files (path to text).</summary>
        public static SourceLibrary Open(
            string project,
            string? userDir = null,
            IDictionary<string, string>? texts = null,
            Func0Rows? func0 = null
        )
        {
            var o = new Dictionary<string, string>(StringComparer.Ordinal);
            if (texts != null)
                foreach (var kv in texts)
                    o[WinPath.Key(kv.Key)] = kv.Value;
            return new SourceLibrary(() => Units.ForProject(project, userDir, func0), o);
        }

        /// <summary>A unit made by the caller (texts, tests).</summary>
        public static SourceLibrary Of(Func<CompileUnit> make) =>
            new SourceLibrary(make, new Dictionary<string, string>(StringComparer.Ordinal));

        private CompileUnit Build()
        {
            var u = _make();
            if (_overrides.Count > 0)
                foreach (var p in u.Projects)
                    p.Overrides = _overrides;
            return u.Compile();
        }

        /// <summary>The unit's errors (the compiler's view).</summary>
        public List<Diagnostic> Errors() => Unit.Errors();

        /// <summary>The files of the unit (paths, unit order).</summary>
        public IEnumerable<string> Files
        {
            get
            {
                foreach (var f in Unit.Files)
                    yield return f.Path;
            }
        }

        // ----------------------------------------------------------------------- editing
        /// <summary>A file edited (text = the editor's buffer, null = the file on disk again): the
        /// file alone is parsed, resolved and its code generated again when its declarations keep
        /// their shape; otherwise the whole unit is compiled again (the result says which).</summary>
        public FileEdit Edit(string path, string? text = null)
        {
            string key = WinPath.Key(path);
            if (text != null)
                _overrides[key] = text;
            else
                _overrides.Remove(key);
            foreach (var p in Unit.Projects)
                p.Overrides = _overrides.Count > 0 ? _overrides : null;
            var e = Unit.ReplaceFile(path, text);
            if (e.Incremental)
            {
                // the file's functions get new code; the others keep theirs
                var drop = new List<FuncInfo>();
                foreach (var kv in _code)
                    if (WinPath.Key(kv.Key.File) == WinPath.Key(e.File!.Path))
                        drop.Add(kv.Key);
                foreach (var f in drop)
                    _code.Remove(f);
                return e;
            }
            var sw = Stopwatch.StartNew();
            Unit = Build();
            FrontEndMs = sw.Elapsed.TotalMilliseconds;
            Tags = TagTable.For(Unit);
            _code = new Dictionary<FuncInfo, FunctionCode>();
            return e;
        }

        // ------------------------------------------------------------------------ code
        private FunctionCode Code(FuncInfo info, List<(string name, string why)>? failed)
        {
            if (_code.TryGetValue(info, out var c))
            {
                if (_why.TryGetValue(info, out string? w))
                    failed?.Add((info.SymbolName, w));
                return c;
            }
            var stop = Unit.StoppedAt1;
            // the compiler stops at an include-row error or a declaration error (a function
            // defined twice ...): the bodies it never reached are not compiled. Said as that
            // error, not as the 'unknown function' the unresolved body would give
            string? why = Unit.StopReason(info.File);
            if (why == null)
            {
                try
                {
                    c = FunctionGen.Function(Unit, Tags, info, _defaults);
                }
                catch (CodegenException e)
                {
                    why =
                        stop != null
                            ? e.Message + " (the compile stops at " + FirstError(stop) + ")"
                            : e.Message;
                }
            }
            if (why != null)
            {
                failed?.Add((info.SymbolName, why));
                _why[info] = why;
                c = new FunctionCode { Info = info };
                c.Code.Add(Op.End);
            }
            _code[info] = c!;
            return c!;
        }

        // why a function did not compile (a cached result says it again)
        private readonly Dictionary<FuncInfo, string> _why = new Dictionary<FuncInfo, string>();

        internal static string FirstError(SourceFile f)
        {
            foreach (var d in f.SortedPass(1))
                if (d.IsError)
                    return d.ToString();
            return f.Path;
        }

        /// <summary>The reason's tail when the compiled library's code of the name runs instead.</summary>
        public const string RdbRuns = "; its code from the compiled library runs";

        /// <summary>Why a function of the unit did not compile from source, null when it did (or
        /// was not asked for yet).</summary>
        public string? NotCompiledWhy(FuncInfo info) => _why.TryGetValue(info, out string? w) ? w : null;

        /// <summary>The whole library from source, as CtCmp32 lays it out (byte-identical to its
        /// _LIBRARY.RDB for the same sources).</summary>
        public LibraryImage Image()
        {
            var b = new LibraryBuilder(Unit, Tags) { Cache = _code };
            return b.Run();
        }

        /// <summary>
        /// ``baseProgram`` with the functions of ``files`` compiled from source and linked in.
        /// </summary>
        public Program Link(
            Program baseProgram,
            ICollection<string> files,
            List<(string name, string why)>? failed = null
        )
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (string f in files)
                keys.Add(WinPath.Key(f));
            var ov = new Overlay(baseProgram, Unit);
            var names = new LibraryBuilder(Unit, Tags);
            foreach (var info in Unit.Functions)
            {
                if (!keys.Contains(WinPath.Key(info.File)))
                    continue;
                var code = Code(info, failed);
                string? why = NotCompiledWhy(info);
                if (why != null && ov.Has(info.SymbolName))
                {
                    // its compiled code (the RDB's) runs instead: the reason says so (a viewer
                    // shows it; 'not compiled from source' alone reads as a function that fails)
                    failed?.RemoveAll(x => x.name == info.SymbolName);
                    failed?.Add((info.SymbolName, why + RdbRuns));
                    continue;
                }
                byte[] bytes;
                try
                {
                    bytes = LibraryBuilder.Bytes(code, ov);
                }
                catch (CodegenException e)
                {
                    // code the runtime cannot hold (a jump beyond 16 bits, a too long function)
                    why = e.Message;
                    if (ov.Has(info.SymbolName))
                    {
                        failed?.Add((info.SymbolName, why + RdbRuns));
                        continue;
                    }
                    failed?.Add((info.SymbolName, why));
                    code = new FunctionCode { Info = info };
                    code.Code.Add(Op.End);
                    bytes = code.Code.Encode(ov);
                }
                var args = new char[info.ArgTypes.Length];
                for (int i = 0; i < args.Length; i++)
                    args[i] = Types.SigChar(info.ArgTypes[i]);
                var fn = new Function(info.SymbolName, new string(args), info.Result, bytes)
                {
                    MinParam = info.Mandatory,
                    Filename = names.FileName(info),
                    Private = info.Private,
                    PublicKeyword = info.PublicKw,
                    Locals = LibraryBuilder.DebugOrder(code.Locals),
                    TypeWord = info.TypeWord,
                    NotCompiled = why,
                };
                ov.Put(fn);
            }
            return ov.Done();
        }

        /// <summary>Overlay pools: a copy of a compiled program's pools that new code links into.</summary>
        private sealed class Overlay : IPoolOffsets
        {
            private readonly Program _p;
            private readonly List<byte> _text;
            private readonly Dictionary<string, long> _textAt = new Dictionary<string, long>(
                StringComparer.Ordinal
            );
            private readonly Dictionary<TagKey, long> _tagAt = new Dictionary<TagKey, long>();
            private readonly Dictionary<string, long> _lookupAt = new Dictionary<string, long>(
                StringComparer.Ordinal
            );
            private readonly Dictionary<ScaleKey, long> _scaleAt = new Dictionary<ScaleKey, long>();
            private readonly Dictionary<VarRef, Compiled.CodeVar> _var =
                new Dictionary<VarRef, Compiled.CodeVar>();
            private readonly List<byte> _run;
            private readonly Dictionary<VarDecl, (int unit, int address)> _place =
                new Dictionary<VarDecl, (int, int)>();
            private readonly Dictionary<string, int> _fnAt = new Dictionary<string, int>(
                StringComparer.Ordinal
            );

            public Overlay(Program b, CompileUnit unit)
            {
                _p = new Program
                {
                    Source = b.Source + " + source",
                    Functions = new List<Function>(b.Functions),
                    TagRefs = new Dictionary<long, Compiled.TagRef>(b.TagRefs),
                    Scales = new Dictionary<long, Compiled.Scale>(b.Scales),
                    FuncLookups = new Dictionary<long, Compiled.FuncLookup>(b.FuncLookups),
                };
                foreach (var cv in b.CodeVarOrder)
                    _p.AddCodeVar(cv);
                _text = new List<byte>(b.Text);
                int i = 0;
                while (i < b.Text.Length)
                {
                    int e = i;
                    while (e < b.Text.Length && b.Text[e] != 0)
                        e++;
                    string s = Cp1252.Decode(b.Text, i, e - i);
                    if (!_textAt.ContainsKey(s))
                        _textAt[s] = i;
                    i = e + 1;
                }
                foreach (var kv in b.TagRefs)
                {
                    var t = kv.Value;
                    var k = new TagKey(t.Cluster, t.Tag, t.Element, t.Item, (int)t.Percentage);
                    if (!_tagAt.ContainsKey(k))
                        _tagAt[k] = kv.Key;
                }
                foreach (var kv in b.FuncLookups)
                {
                    string k = Lexer.NameKey(kv.Value.Name);
                    if (!_lookupAt.ContainsKey(k))
                        _lookupAt[k] = kv.Key;
                }
                foreach (var kv in b.Scales)
                {
                    var s = kv.Value;
                    var k = new ScaleKey(s.RawZero, s.RawFull, s.EngZero, s.EngFull);
                    if (!_scaleAt.ContainsKey(k))
                        _scaleAt[k] = kv.Key;
                }
                _run = new List<byte>(b.RunImage);
                for (int k = 0; k < _p.Functions.Count; k++)
                {
                    string n = Lexer.NameKey(_p.Functions[k].Name);
                    if (!_fnAt.ContainsKey(n))
                        _fnAt[n] = k;
                }
                int g = 0;
                for (int fi = 0; fi < unit.Files.Count; fi++)
                {
                    int m = 0;
                    foreach (var it in unit.Files[fi].Module.Items)
                        if (it is VarDecl v)
                            _place[v] = v.IsGlobal ? (-1, g++) : (fi, m++);
                }
            }

            public long Text(string s)
            {
                if (_textAt.TryGetValue(s, out long o))
                    return o;
                o = _text.Count;
                _text.AddRange(Cp1252.Encode(s));
                _text.Add(0);
                _textAt[s] = o;
                return o;
            }

            public long Tag(TagKey t)
            {
                if (_tagAt.TryGetValue(t, out long o))
                    return o;
                o = 38L * _p.TagRefs.Count;
                while (_p.TagRefs.ContainsKey(o))
                    o += 38;
                _p.TagRefs[o] = new Compiled.TagRef(o)
                {
                    Cluster = t.Cluster,
                    Tag = t.Tag,
                    Element = t.Element,
                    Item = t.Item,
                    Percentage = t.Percentage,
                    ValueHandle = -1,
                    Lightweight = t.Lightweight,
                };
                _tagAt[t] = o;
                Text(t.Cluster);
                Text(t.Tag);
                Text(t.Element);
                Text(t.Item);
                return o;
            }

            private Compiled.CodeVar Var(VarRef v)
            {
                if (_var.TryGetValue(v, out var cv))
                    return cv;
                var decl = v.Decl as VarDecl;
                var (unitType, addr) = decl != null && _place.TryGetValue(decl, out var pl) ? pl : (-1, 0);
                int raw = FunctionGen.RawType(v.Type);
                int width = LibraryBuilder.BitWidth(raw);
                long count = 1;
                foreach (long d in v.Dims)
                    count *= Math.Max(d, 0);
                string key = Lexer.NameKey(v.Name);
                Compiled.CodeVar? hit = null,
                    byUnit = null;
                foreach (var c in _p.CodeVarOrder)
                {
                    if (Lexer.NameKey(c.Name) != key || c.UnitType != unitType)
                        continue;
                    if (c.UnitAddress == addr)
                    {
                        hit = c;
                        break;
                    }
                    byUnit ??= c;
                }
                hit ??= byUnit;
                byte[] init = InitBytes(decl, raw, width, count);
                if (hit != null && hit.RawType == raw && hit.UnitCount == count && hit.BitWidth == width)
                {
                    // the RDB's slot, the initial value from the source
                    if (decl != null && hit.DataAddress >= 0 && hit.DataAddress + init.Length <= _run.Count)
                        for (int i = 0; i < init.Length; i++)
                            _run[(int)hit.DataAddress + i] = init[i];
                    _var[v] = hit;
                    return hit;
                }
                long off = 65L * _p.CodeVarOrder.Count;
                while (_p.CodeVars.ContainsKey(off))
                    off += 65;
                var n = new Compiled.CodeVar(off, decl != null ? decl.Name : v.Name, raw)
                {
                    BitWidth = width,
                    UnitType = unitType,
                    UnitAddress = addr,
                    UnitCount = count,
                    DataAddress = _run.Count,
                };
                _run.AddRange(init);
                _p.AddCodeVar(n);
                Text(n.Name);
                _var[v] = n;
                return n;
            }

            private static byte[] InitBytes(VarDecl? decl, int raw, int width, long count)
            {
                int esz = width / 8;
                var b = new byte[esz * Math.Max(count, 0)];
                for (int k = 0; k < count; k++)
                {
                    int at = esz * k;
                    Literal? lit = decl != null && k < decl.Init.Count ? decl.Init[k] : null;
                    switch (raw)
                    {
                        case Types.String:
                            if (lit != null)
                            {
                                byte[] s = Cp1252.Encode(
                                    lit.Kind == "STRING" ? lit.SValue ?? ""
                                    : lit.Kind == "REAL" ? FloatText.Shortest(lit.RValue)
                                    : lit.IValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                );
                                Array.Copy(s, 0, b, at, Math.Min(s.Length, 255));
                            }
                            break;
                        case Types.LongReal:
                        case Types.Real:
                            if (lit != null)
                                CodeBuffer.PutI64(
                                    b,
                                    at,
                                    BitConverter.DoubleToInt64Bits(
                                        lit.Kind == "REAL" ? lit.RValue : lit.IValue
                                    )
                                );
                            break;
                        case Types.Object:
                            CodeBuffer.PutI32(b, at, lit != null ? (int)lit.IValue : -1);
                            break;
                        case Types.Timestamp:
                        case Types.Quality:
                            if (lit != null)
                                CodeBuffer.PutI64(b, at, lit.IValue);
                            break;
                        default:
                            if (lit != null)
                                CodeBuffer.PutI32(
                                    b,
                                    at,
                                    lit.Kind == "REAL"
                                        ? LibraryBuilder.Int32Of(lit.RValue)
                                        : unchecked((int)lit.IValue)
                                );
                            break;
                    }
                }
                return b;
            }

            public long CodeVar(VarRef v) => Var(v).Offset;

            public long DataAddress(VarRef v) => Var(v).DataAddress;

            public long FuncLookup(FuncInfo f)
            {
                string k = Lexer.NameKey(f.SymbolName);
                if (_lookupAt.TryGetValue(k, out long o))
                    return o;
                o = 12L * _p.FuncLookups.Count;
                while (_p.FuncLookups.ContainsKey(o))
                    o += 12;
                _p.FuncLookups[o] = new Compiled.FuncLookup(o, f.SymbolName);
                _lookupAt[k] = o;
                Text(f.SymbolName);
                return o;
            }

            public long Scale(ScaleKey s)
            {
                if (_scaleAt.TryGetValue(s, out long o))
                    return o;
                o = 32L * _p.Scales.Count;
                while (_p.Scales.ContainsKey(o))
                    o += 32;
                _p.Scales[o] = new Compiled.Scale(o)
                {
                    RawZero = s.RawZero,
                    RawFull = s.RawFull,
                    EngZero = s.EngZero,
                    EngFull = s.EngFull,
                };
                _scaleAt[s] = o;
                return o;
            }

            /// <summary>The base program has a function of this name.</summary>
            public bool Has(string name) => _fnAt.ContainsKey(Lexer.NameKey(name));

            public void Put(Function fn)
            {
                string k = Lexer.NameKey(fn.Name);
                if (_fnAt.TryGetValue(k, out int at))
                {
                    fn.Index = _p.Functions[at].Index;
                    _p.Functions[at] = fn;
                }
                else
                {
                    fn.Index = _p.Functions.Count;
                    _fnAt[k] = _p.Functions.Count;
                    _p.Functions.Add(fn);
                }
            }

            public Program Done()
            {
                _p.Text = _text.ToArray();
                _p.RunImage = _run.ToArray();
                return _p;
            }
        }
    }
}
