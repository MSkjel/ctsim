using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CicodeSim.Compiled;
using CicodeSim.Values;

namespace CicodeSim.Vm
{
    /// <summary>One prepared instruction: opcode, operands and the index of the next one.</summary>
    internal struct Ins
    {
        public int Op;
        public int A;
        public int B;

        /// <summary>Byte offset of the instruction.</summary>
        public int Pc;

        /// <summary>Index of the instruction that follows it.</summary>
        public int Next;
        public long L;
        public double D;
        public object? O;
    }

    /// <summary>The decoded form of one code byte array (shared by every VM that runs it).</summary>
    internal sealed class Decoded
    {
        public Program Program = null!;
        public Ins[] Ins = null!;
        public int Count;

        /// <summary>Entry targets (argc -> instruction index), null when the code has no Entry at 0.</summary>
        public int[]? Entry;
        public int CallSlots;

        /// <summary>The instruction at byte 0 (a pseudo instruction for empty code).</summary>
        public int Start;

        /// <summary>The Const values (Ins.B indexes them).</summary>
        public Value[] Consts = Array.Empty<Value>();

        /// <summary>A pc that starts no instruction: the task halts.</summary>
        public const int OpBadPc = 0x101;

        /// <summary>Convert 0x11 / Const 0x11, the runtime's in-code tag-reference holder: not
        /// simulated.</summary>
        public const int OpTagRef = 0x102;

        /// <summary>A pc past the decode table: a stack underflow halt.</summary>
        public const int OpOutOfCode = 0x103;
    }

    /// <summary>Decoded code of one function / block: the prepared instructions, the Entry table and the per-VM cache of resolved user calls.</summary>
    public sealed class CodeBlock
    {
        public readonly Program Program;
        public readonly Function? Func;
        public readonly byte[] Code;
        public readonly string Label;
        internal readonly Decoded D;
        internal readonly Ins[] Ins;

        /// <summary>Resolved FuncCallByLookup targets by instruction index.</summary>
        internal Function?[]? Calls;
        internal bool[]? Resolved;

        /// <summary>The variable slot of each LoadCodeVar / StoreCodeVar / Index access (per VM).</summary>
        internal CodeVarSlot?[]? Slots;

        /// <summary>The callee code of each user call site (per VM).</summary>
        internal CodeBlock?[]? CallCode;

        /// <summary>The reference an AddrCodeVar pushes (element 0, one per instruction).</summary>
        internal CodeVarRef?[]? Refs;

        private static readonly ConditionalWeakTable<byte[], Decoded> Cache =
            new ConditionalWeakTable<byte[], Decoded>();

        public CodeBlock(Program program, byte[] code, Function? func = null, string label = "")
        {
            Program = program;
            Func = func;
            Code = code;
            Label = label.Length > 0 ? label : (func != null ? func.Name : "");
            Decoded? d;
            lock (Cache)
            {
                if (!Cache.TryGetValue(code, out d) || d.Program != program)
                {
                    d = Decode(program, code);
                    Cache.Remove(code);
                    Cache.Add(code, d);
                }
            }
            D = d;
            Ins = d.Ins;
        }

        /// <summary>The byte offset of instruction <paramref name="index"/>.</summary>
        public int PcOf(int index) => index >= 0 && index < Ins.Length ? Ins[index].Pc : 0;

        /// <summary>The code starts with an Entry that has at least one target.</summary>
        public bool HasEntry => D.Entry != null && D.Entry.Length > 0;

        public int EntryCount => D.Entry?.Length ?? 0;

        /// <summary>The instruction at byte offset 0.</summary>
        public int Start => D.Start;

        private static Decoded Decode(Program prog, byte[] code)
        {
            var list = new List<Ins>(code.Length / 3 + 4);
            var rawTargets = new List<(int Index, int Target)>();
            var pcIndex = new int[code.Length + 1];
            for (int k = 0; k < pcIndex.Length; k++)
                pcIndex[k] = -1;
            int[]? entryRaw = null;
            int pc = 0,
                n = code.Length;
            var instrs = new List<Instr>();
            while (pc < n)
            {
                Instr ins = Opcodes.Decode(code, pc);
                instrs.Add(ins);
                if (ins.Op == Op.Entry && pc == 0)
                {
                    entryRaw = new int[ins.Shorts!.Length];
                    for (int k = 0; k < entryRaw.Length; k++)
                        entryRaw[k] = ins.Shorts[k];
                }
                if (ins.Op == Op.Index && pc + ins.Size >= n)
                    Opcodes.Decode(code, pc + ins.Size); // the inner op is decoded too (a short one throws)
                if (ins.Op == Op.End)
                    break;
                pc += ins.Size;
            }
            var consts = new List<Value>();
            foreach (var ins in instrs)
            {
                pcIndex[ins.Pc] = list.Count;
                list.Add(Prepare(ins, prog, consts));
            }
            var d = new Decoded
            {
                Program = prog,
                Count = list.Count,
                Consts = consts.ToArray(),
            };
            var pseudo = new Dictionary<int, int>();
            int Resolve(int target)
            {
                int t = target;
                if (t < 0 && t >= -(n + 1))
                    t = n + 1 + t; // counts from the end: no compiler emits one
                if (t < 0 || t > n)
                    return PseudoAt(target, Decoded.OpOutOfCode);
                int idx = pcIndex[t];
                return idx >= 0 ? idx : PseudoAt(target, Decoded.OpBadPc);
            }
            int PseudoAt(int target, int op)
            {
                int key = target * 4 + (op == Decoded.OpOutOfCode ? 1 : 0);
                if (pseudo.TryGetValue(key, out int idx))
                    return idx;
                idx = list.Count;
                list.Add(
                    new Ins
                    {
                        Op = op,
                        Pc = target,
                        Next = idx,
                    }
                );
                pseudo[key] = idx;
                return idx;
            }
            int count = d.Count;
            for (int i = 0; i < count; i++)
            {
                Ins x = list[i];
                Instr src = instrs[i];
                x.Next = Resolve(src.Pc + src.Size);
                switch (x.Op)
                {
                    case Op.Branch:
                    case Op.Brfalse:
                    case Op.BrTrue:
                        // a target outside the code is the not-an-instruction slot at its end
                        x.A = Resolve(src.A >= 0 && src.A < n ? src.A : n);
                        break;
                    case Op.Index:
                        // an unknown inner op continues one byte after it
                        x.B = Resolve(src.Pc + src.Size + 1);
                        break;
                }
                list[i] = x;
            }
            d.Start = Resolve(0);
            if (entryRaw != null)
            {
                var e = new int[entryRaw.Length];
                for (int k = 0; k < e.Length; k++)
                    e[k] = Resolve(entryRaw[k]);
                d.Entry = e;
            }
            d.Ins = list.ToArray();
            return d;
        }

        private static Ins Prepare(in Instr ins, Program prog, List<Value> consts)
        {
            var x = new Ins { Op = ins.Op, Pc = ins.Pc };
            switch (ins.Op)
            {
                case Op.Const:
                {
                    int sub = ins.Sub;
                    if (sub == Types.String)
                    {
                        x.A = Types.String;
                        x.O = StringAt(prog, ins.U64);
                    }
                    else if (sub == 0x01 || sub == 0x04 || sub == 0x08)
                    {
                        x.A = Types.Long;
                        x.L = ins.IntValue;
                    }
                    else if (sub == Types.Real)
                    {
                        x.A = Types.Real;
                        x.D = ins.RealValue;
                    }
                    else if (sub == Types.Object)
                    {
                        x.A = Types.Object;
                        x.L = ins.IntValue;
                    }
                    else if (sub == Types.Timestamp || sub == Types.Quality)
                    {
                        x.A = sub;
                        x.L = (long)ins.U64;
                    }
                    else if (sub == Types.Tag)
                    {
                        x.Op = Decoded.OpTagRef;
                        x.O = "Const TAG";
                        break;
                    }
                    else
                    {
                        x.Op = Op.UnsupportedConst;
                        x.A = sub;
                        break;
                    }
                    Value c;
                    if (x.A == Types.Long)
                        c = Value.Long(x.L);
                    else if (x.A == Types.Real)
                        c = Value.Real(x.D);
                    else if (x.A == Types.String)
                        c = Value.Str((string)x.O!);
                    else if (x.A == Types.Object)
                        c = Value.Object(x.L);
                    else
                        c = Value.Int(x.A, x.L);
                    x.B = consts.Count;
                    consts.Add(c);
                    break;
                }
                case Op.Load:
                case Op.Store:
                {
                    if (!(ins.U64 <= long.MaxValue && prog.TagRefs.TryGetValue(ins.Offset, out var r)))
                        r = new TagRef(ins.Offset) { Tag = "?tagref@" + ins.U64 };
                    x.O = r;
                    break;
                }
                case Op.LoadCodeVar:
                case Op.StoreCodeVar:
                    x.O =
                        ins.U64 <= long.MaxValue && prog.CodeVars.TryGetValue(ins.Offset, out var cv)
                            ? cv
                            : null;
                    x.L = ins.Offset;
                    break;
                case Op.AddrCodeVar:
                    // the operand is the variable's RUN DataAddress
                    // an address no variable starts at gets its own phantom slot,
                    // kept apart from the CODEVAR-offset keys of LoadCodeVar / StoreCodeVar
                    x.O = ins.U64 <= long.MaxValue ? prog.CodeVarAtData(ins.Offset) : null;
                    x.A = ins.Sub;
                    x.L = x.O != null ? ((CodeVar)x.O).Offset : CicodeVm.DataAddressKey(ins.Offset);
                    break;
                case Op.FuncCallByLookup:
                    x.O =
                        ins.U64 <= long.MaxValue && prog.FuncLookups.TryGetValue(ins.Offset, out var fl)
                            ? fl.Name
                            : null;
                    x.A = ins.A;
                    break;
                case Op.FuncCallByName:
                    x.O = StringAt(prog, ins.U64);
                    x.A = ins.A;
                    break;
                case Op.Func:
                    x.A = ins.A;
                    x.B = ins.B;
                    break;
                case Op.Convert:
                    if (ins.A == Types.Tag)
                    {
                        x.Op = Decoded.OpTagRef;
                        x.O = "Convert TAG";
                        break;
                    }
                    x.A = ins.A;
                    break;
                case Op.Branch:
                case Op.Brfalse:
                case Op.BrTrue:
                case Op.StoreStack:
                case Op.LoadStack:
                case Op.AddrStack:
                case Op.Line:
                    x.A = ins.A;
                    break;
                case Op.Bar:
                    if (ins.U32 == 0xFFFFFFFFu)
                        x.B = 2;
                    else if (prog.Scales.TryGetValue(ins.U32, out var sc))
                    {
                        x.B = 1;
                        x.O = sc;
                    }
                    else
                        x.B = 0;
                    break;
                case Op.AssFmt:
                    x.O = ins.U32 < (uint)prog.Text.Length ? prog.StringAt(ins.U32) : "";
                    break;
                case Op.StringFmt:
                    x.L = ins.U32;
                    x.O = ins.U64 < (ulong)prog.Text.Length ? prog.StringAt((long)ins.U64) : "";
                    break;
                case Op.Entry:
                    x.O = ins.Shorts;
                    break;
                case Op.Index:
                    x.O = ins.Shorts;
                    break;
                case Op.StackVar:
                    x.O = ins.Bytes;
                    break;
            }
            return x;
        }

        private static string StringAt(Program prog, ulong offset) =>
            offset < (ulong)prog.Text.Length ? prog.StringAt((long)offset) : "";
    }
}
