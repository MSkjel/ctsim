using System;
using System.Collections.Generic;
using CicodeSim.Compiled;
using CicodeSim.Values;

namespace CicodeSim.Vm
{
    /// <summary>TEXT / TAGREF / CODEVAR / SCALE / FUNCLOOKUP of one program, laid out like the
    /// RDB (record sizes 38 / 65 / 32 / 12; TEXT starts with an empty string). The p-code
    /// assembler for tests, hand-written conformance cases and the compiler's back end.</summary>
    public sealed class Pools
    {
        public readonly List<byte> Text = new List<byte> { 0 };
        private readonly Dictionary<string, long> _texts = new Dictionary<string, long>(
            StringComparer.Ordinal
        )
        {
            [""] = 0,
        };
        public readonly Dictionary<long, TagRef> TagRefs = new Dictionary<long, TagRef>();
        private readonly Dictionary<(string, string, string, string), long> _tags =
            new Dictionary<(string, string, string, string), long>();
        public readonly List<CodeVar> CodeVars = new List<CodeVar>();
        internal readonly Dictionary<string, long> Cvs = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>Variable name to its RUN DataAddress (the AddrCodeVar operand, row 0x3c).</summary>
        internal readonly Dictionary<string, long> CvData = new Dictionary<string, long>(
            StringComparer.Ordinal
        );
        public readonly List<byte> Run = new List<byte>();
        public readonly Dictionary<long, Scale> Scales = new Dictionary<long, Scale>();
        public readonly Dictionary<long, FuncLookup> FuncLookups = new Dictionary<long, FuncLookup>();
        private readonly Dictionary<string, long> _fl = new Dictionary<string, long>(StringComparer.Ordinal);

        public long String(string s)
        {
            if (_texts.TryGetValue(s, out long off))
                return off;
            off = Text.Count;
            Text.AddRange(Cp1252.Encode(s));
            Text.Add(0);
            _texts[s] = off;
            return off;
        }

        public long Tag(string name, string element = "", string item = "", string cluster = "")
        {
            var key = (cluster, name, element, item);
            if (_tags.TryGetValue(key, out long off))
                return off;
            off = 38L * TagRefs.Count;
            TagRefs[off] = new TagRef(off)
            {
                Cluster = cluster,
                Tag = name,
                Element = element,
                Item = item,
            };
            _tags[key] = off;
            return off;
        }

        /// <summary>A module / global variable; init is one value or one per element.</summary>
        public long CodeVar(
            string name,
            int rawType = Types.Long,
            int count = 1,
            object? init = null,
            int unitType = -1
        )
        {
            string low = name.ToLowerInvariant();
            if (Cvs.TryGetValue(low, out long off0))
                return off0;
            long off = 65L * CodeVars.Count;
            int width =
                rawType == Types.String ? 2048
                : (
                    rawType == Types.LongReal
                    || rawType == Types.Real
                    || rawType == Types.Timestamp
                    || rawType == Types.Quality
                )
                    ? 64
                : 32;
            int size = width / 8;
            long addr = Run.Count;
            var data = new byte[size * count];
            var vals = new List<object?>();
            if (init is System.Collections.IList list)
                foreach (var o in list)
                    vals.Add(o);
            else if (init != null)
                for (int k = 0; k < count; k++)
                    vals.Add(init);
            for (int k = 0; k < Math.Min(count, vals.Count); k++)
            {
                int o = k * size;
                object? v = vals[k];
                if (rawType == Types.String)
                {
                    byte[] b = Cp1252.Encode(
                        Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? ""
                    );
                    Array.Copy(b, 0, data, o, Math.Min(b.Length, 255));
                }
                else if (rawType == Types.Real || rawType == Types.LongReal)
                    Array.Copy(
                        BitConverter.GetBytes(
                            Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture)
                        ),
                        0,
                        data,
                        o,
                        8
                    );
                else if (rawType == Types.Timestamp || rawType == Types.Quality)
                    Array.Copy(
                        BitConverter.GetBytes(
                            Convert.ToUInt64(v, System.Globalization.CultureInfo.InvariantCulture)
                        ),
                        0,
                        data,
                        o,
                        8
                    );
                else
                    Array.Copy(
                        BitConverter.GetBytes(
                            Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture)
                        ),
                        0,
                        data,
                        o,
                        4
                    );
            }
            Run.AddRange(data);
            CodeVars.Add(
                new CodeVar(off, name, rawType)
                {
                    BitWidth = width,
                    UnitType = unitType,
                    UnitCount = count,
                    DataAddress = addr,
                }
            );
            Cvs[low] = off;
            CvData[low] = addr;
            return off;
        }

        public long Scale(double engZero, double engFull, double rawZero = 0.0, double rawFull = 32000.0)
        {
            long off = 32L * Scales.Count;
            Scales[off] = new Scale(off)
            {
                RawZero = rawZero,
                RawFull = rawFull,
                EngZero = engZero,
                EngFull = engFull,
            };
            return off;
        }

        public long Lookup(string name)
        {
            string low = name.ToLowerInvariant();
            if (_fl.TryGetValue(low, out long off))
                return off;
            off = 12L * FuncLookups.Count;
            FuncLookups[off] = new FuncLookup(off, name);
            _fl[low] = off;
            return off;
        }

        public Program Fill(Program prog)
        {
            prog.Text = Text.ToArray();
            prog.TagRefs = new Dictionary<long, TagRef>(TagRefs);
            foreach (var cv in CodeVars)
                prog.AddCodeVar(cv);
            prog.Scales = new Dictionary<long, Scale>(Scales);
            prog.FuncLookups = new Dictionary<long, FuncLookup>(FuncLookups);
            prog.RunImage = Run.ToArray();
            return prog;
        }
    }

    /// <summary>Emits one code block. Jumps take label names, resolved by Code().</summary>
    public class CodeBuilder
    {
        public readonly Pools Pools;
        protected readonly List<byte> Buf = new List<byte>();
        private readonly Dictionary<string, int> _labels = new Dictionary<string, int>(
            StringComparer.Ordinal
        );
        private readonly List<(int Pos, string Label)> _fixups = new List<(int, string)>();
        private (int Start, List<string> Labels)? _entryFix;

        public CodeBuilder(Pools pools)
        {
            Pools = pools;
        }

        // --- raw ---------------------------------------------------------------------------
        public CodeBuilder Emit(params object[] parts)
        {
            foreach (var p in parts)
            {
                if (p is byte[] b)
                    Buf.AddRange(b);
                else
                    Buf.Add(
                        (byte)System.Convert.ToInt32(p, System.Globalization.CultureInfo.InvariantCulture)
                    );
            }
            return this;
        }

        private CodeBuilder Bytes(int op, params byte[][] rest)
        {
            Buf.Add((byte)op);
            foreach (var r in rest)
                Buf.AddRange(r);
            return this;
        }

        private static byte[] B(byte v) => new[] { v };

        public CodeBuilder Label(string name)
        {
            _labels[name] = Buf.Count;
            return this;
        }

        public int Pc => Buf.Count;

        // --- instructions ------------------------------------------------------------------
        /// <summary>An operand-less opcode by its CtCmp32 name (Add, Pop, RetValue, End ...).</summary>
        public CodeBuilder Op(string name)
        {
            int op = Compiled.Op.ByName(name);
            if (op < 0)
                throw new KeyNotFoundException(name);
            Buf.Add((byte)op);
            return this;
        }

        public CodeBuilder End()
        {
            Buf.Add(Compiled.Op.End);
            return this;
        }

        public CodeBuilder ConstInt(long v) =>
            Bytes(Compiled.Op.Const, B(0x04), BitConverter.GetBytes((int)v));

        public CodeBuilder ConstReal(double v) => Bytes(Compiled.Op.Const, B(0x02), BitConverter.GetBytes(v));

        public CodeBuilder ConstStr(string s) =>
            Bytes(Compiled.Op.Const, B(0x07), BitConverter.GetBytes((ulong)Pools.String(s)));

        public CodeBuilder ConstObj(int v) => Bytes(Compiled.Op.Const, B(0x0C), BitConverter.GetBytes(v));

        public CodeBuilder ConstU64(int t, ulong v) =>
            Bytes(Compiled.Op.Const, B((byte)t), BitConverter.GetBytes(v));

        public CodeBuilder ConstSub(int sub, byte[] raw) => Bytes(Compiled.Op.Const, B((byte)sub), raw);

        public CodeBuilder Convert(int t) => Bytes(Compiled.Op.Convert, B((byte)t));

        public CodeBuilder Line(int n) => Bytes(Compiled.Op.Line, BitConverter.GetBytes((ushort)n));

        public CodeBuilder LoadStack(int slot) => Bytes(Compiled.Op.LoadStack, B((byte)slot));

        public CodeBuilder StoreStack(int slot) => Bytes(Compiled.Op.StoreStack, B((byte)slot));

        public CodeBuilder AddrStack(int slot) => Bytes(Compiled.Op.AddrStack, B((byte)slot));

        public CodeBuilder StackVar(params int[] types)
        {
            var t = new byte[types.Length];
            for (int k = 0; k < t.Length; k++)
                t[k] = (byte)types[k];
            return Bytes(Compiled.Op.StackVar, B((byte)types.Length), t);
        }

        /// <summary>Func by built-in index or name (the runtime table / FUNC0 name).</summary>
        public CodeBuilder Func(string name, int argc)
        {
            var b = Func0Table.Default.Lookup(name) ?? throw new KeyNotFoundException(name);
            return Func(b.Index, argc);
        }

        public CodeBuilder Func(int index, int argc) =>
            Bytes(Compiled.Op.Func, BitConverter.GetBytes((ushort)index), B((byte)argc));

        public CodeBuilder Call(string name, int argc) =>
            Bytes(
                Compiled.Op.FuncCallByLookup,
                BitConverter.GetBytes((ulong)Pools.Lookup(name)),
                B((byte)argc)
            );

        public CodeBuilder CallByName(string name, int argc) =>
            Bytes(
                Compiled.Op.FuncCallByName,
                BitConverter.GetBytes((ulong)Pools.String(name)),
                B((byte)argc)
            );

        private CodeBuilder Jump(int op, string label)
        {
            Buf.Add((byte)op);
            _fixups.Add((Buf.Count, label));
            Buf.Add(0);
            Buf.Add(0);
            return this;
        }

        public CodeBuilder Branch(string label) => Jump(Compiled.Op.Branch, label);

        public CodeBuilder BrFalse(string label) => Jump(Compiled.Op.Brfalse, label);

        public CodeBuilder BrTrue(string label) => Jump(Compiled.Op.BrTrue, label);

        public CodeBuilder Load(string tag, string element = "") =>
            Bytes(Compiled.Op.Load, BitConverter.GetBytes((ulong)Pools.Tag(tag, element)));

        public CodeBuilder Store(string tag, string element = "") =>
            Bytes(Compiled.Op.Store, BitConverter.GetBytes((ulong)Pools.Tag(tag, element)));

        public CodeBuilder LoadVar(string name) =>
            Bytes(Compiled.Op.LoadCodeVar, BitConverter.GetBytes((ulong)Pools.Cvs[name.ToLowerInvariant()]));

        public CodeBuilder StoreVar(string name) =>
            Bytes(Compiled.Op.StoreCodeVar, BitConverter.GetBytes((ulong)Pools.Cvs[name.ToLowerInvariant()]));

        public CodeBuilder AddrVar(string name, int sub) =>
            Bytes(
                Compiled.Op.AddrCodeVar,
                B((byte)sub),
                BitConverter.GetBytes((ulong)Pools.CvData[name.ToLowerInvariant()])
            );

        public CodeBuilder Index(params int[] dims)
        {
            var b = new List<byte> { (byte)dims.Length };
            foreach (int d in dims)
                b.AddRange(BitConverter.GetBytes((short)d));
            return Bytes(Compiled.Op.Index, b.ToArray());
        }

        public CodeBuilder Bar(long scaleOffset) =>
            Bytes(Compiled.Op.Bar, BitConverter.GetBytes((uint)(scaleOffset & 0xFFFFFFFF)));

        public CodeBuilder AssFmt(string name) =>
            Bytes(Compiled.Op.AssFmt, BitConverter.GetBytes((uint)Pools.String(name)));

        public CodeBuilder StringFmt(uint word, string suffix = "") =>
            Bytes(
                Compiled.Op.StringFmt,
                BitConverter.GetBytes(word),
                BitConverter.GetBytes((ulong)Pools.String(suffix))
            );

        /// <summary>Entry (0x08) with one target per argument count 0..n (labels).</summary>
        public CodeBuilder Entry(params string[] labels)
        {
            int start = Buf.Count;
            Buf.Add(Compiled.Op.Entry);
            Buf.Add((byte)labels.Length);
            for (int k = 0; k < labels.Length; k++)
            {
                Buf.Add(0);
                Buf.Add(0);
            }
            _entryFix = (start, new List<string>(labels));
            return this;
        }

        public CodeBuilder Ret()
        {
            Buf.Add(Compiled.Op.Ret);
            return this;
        }

        public CodeBuilder RetValue()
        {
            Buf.Add(Compiled.Op.RetValue);
            return this;
        }

        public byte[] Code()
        {
            var b = Buf.ToArray();
            foreach (var (pos, lab) in _fixups)
            {
                short rel = (short)(_labels[lab] - pos);
                b[pos] = (byte)rel;
                b[pos + 1] = (byte)(rel >> 8);
            }
            if (_entryFix.HasValue)
            {
                var (start, labs) = _entryFix.Value;
                for (int k = 0; k < labs.Count; k++)
                {
                    short rel = (short)(_labels[labs[k]] - start);
                    b[start + 2 + 2 * k] = (byte)rel;
                    b[start + 3 + 2 * k] = (byte)(rel >> 8);
                }
            }
            return b;
        }
    }

    public sealed class FunctionBuilder : CodeBuilder
    {
        public readonly string Name;
        public readonly string Args;
        public readonly int Result;
        public readonly int MinParam;
        public readonly string File;
        public readonly List<(int Slot, string Name)> Locals = new List<(int, string)>();

        public FunctionBuilder(Pools pools, string name, string args, int result, int minParam, string file)
            : base(pools)
        {
            Name = name;
            Args = args;
            Result = result;
            MinParam = minParam;
            File = file;
        }

        /// <summary>The Entry table of a function with arguments and no defaults: every argument
        /// count goes to the body (the compiler emits an Entry whenever there are arguments).</summary>
        public FunctionBuilder Begin()
        {
            if (Args.Length > 0)
            {
                var labels = new string[Args.Length + 1];
                for (int k = 0; k < labels.Length; k++)
                    labels[k] = "_body";
                Entry(labels);
                Label("_body");
            }
            return this;
        }
    }

    /// <summary>Several functions sharing one set of pools: Build gives the Program.</summary>
    public sealed class ProgramBuilder
    {
        public readonly Pools Pools = new Pools();
        public readonly List<FunctionBuilder> Functions = new List<FunctionBuilder>();

        public FunctionBuilder Function(
            string name,
            string args = "",
            int result = Types.Void,
            int minParam = -1,
            string file = "test.ci"
        )
        {
            var f = new FunctionBuilder(
                Pools,
                name,
                args,
                result,
                minParam < 0 ? args.Length : minParam,
                file
            );
            Functions.Add(f);
            return f;
        }

        /// <summary>A code block outside any function (a page expression / command).</summary>
        public CodeBuilder Block() => new CodeBuilder(Pools);

        public Program Build()
        {
            var prog = new Program { Source = "asm" };
            for (int k = 0; k < Functions.Count; k++)
            {
                var f = Functions[k];
                prog.Functions.Add(
                    new Function(f.Name, f.Args, f.Result, f.Code())
                    {
                        MinParam = f.MinParam,
                        Filename = f.File,
                        Index = k,
                        Private = f.Name.Contains("@"),
                        Locals = new List<(int, string)>(f.Locals),
                    }
                );
            }
            return Pools.Fill(prog);
        }
    }
}
