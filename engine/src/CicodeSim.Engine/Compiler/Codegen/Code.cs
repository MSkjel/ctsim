using System;
using System.Collections.Generic;
using CicodeSim.Compiled;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Compiler.Syntax;

namespace CicodeSim.Compiler.Codegen
{
    /// <summary>
    /// A tag reference as CtCmp32 stores it: one record per distinct (cluster, tag, element, item,
    /// percentage).
    /// </summary>
    public sealed class TagKey : IEquatable<TagKey>
    {
        public readonly string Cluster;
        public readonly string Tag;
        public readonly string Element;
        public readonly string Item;
        public readonly int Percentage;
        private readonly string _key;

        public TagKey(string cluster, string tag, string element, string item, int percentage = 0)
        {
            Cluster = cluster;
            Tag = tag;
            Element = element;
            Item = item;
            Percentage = percentage;
            _key =
                Lexer.NameKey(cluster)
                + "\u0001"
                + Lexer.NameKey(tag)
                + "\u0001"
                + Lexer.NameKey(element)
                + "\u0001"
                + Lexer.NameKey(item)
                + "\u0001"
                + percentage;
        }

        /// <summary>0 for the items vt and qt, else 1 (`?Tag?.Field.QT` 0, `.Q` / `.T` 1).</summary>
        public int Lightweight => Lexer.NameKey(Item) == "vt" || Lexer.NameKey(Item) == "qt" ? 0 : 1;

        public bool Equals(TagKey? o) => o != null && o._key == _key;

        public override bool Equals(object? obj) => obj is TagKey k && Equals(k);

        public override int GetHashCode() => _key.GetHashCode();

        public override string ToString() =>
            (Cluster.Length > 0 ? Cluster + ":" : "")
            + Tag
            + (Element.Length > 0 ? "." + Element : "")
            + (Item.Length > 0 ? "." + Item : "")
            + (Percentage != 0 ? " %" : "");
    }

    /// <summary>A Bar range (SCALE record, four doubles).</summary>
    public readonly struct ScaleKey : IEquatable<ScaleKey>
    {
        public readonly double RawZero,
            RawFull,
            EngZero,
            EngFull;

        public ScaleKey(double rz, double rf, double ez, double ef)
        {
            RawZero = rz;
            RawFull = rf;
            EngZero = ez;
            EngFull = ef;
        }

        public bool Equals(ScaleKey o) =>
            BitConverter.DoubleToInt64Bits(RawZero) == BitConverter.DoubleToInt64Bits(o.RawZero)
            && BitConverter.DoubleToInt64Bits(RawFull) == BitConverter.DoubleToInt64Bits(o.RawFull)
            && BitConverter.DoubleToInt64Bits(EngZero) == BitConverter.DoubleToInt64Bits(o.EngZero)
            && BitConverter.DoubleToInt64Bits(EngFull) == BitConverter.DoubleToInt64Bits(o.EngFull);

        public override bool Equals(object? obj) => obj is ScaleKey k && Equals(k);

        public override int GetHashCode() => (RawZero, RawFull, EngZero, EngFull).GetHashCode();
    }

    /// <summary>A branch target inside one code buffer.</summary>
    public sealed class CodeLabel
    {
        /// <summary>Instruction index the label stands before (-1 until placed).</summary>
        public int At = -1;
    }

    /// <summary>One symbolic instruction, before the pools give it an offset.</summary>
    public sealed class CodeIns
    {
        public int Op;

        /// <summary>Const sub type / Convert type / stack slot / AddrCodeVar raw type / argc.</summary>
        public int A;

        /// <summary>Func opcode / Line number / StringFmt word / Const int value.</summary>
        public long L;
        public double D;
        public object? Ref;
        public CodeLabel? Target;
        public CodeLabel[]? Targets;
        public short[]? Shorts;
        public byte[]? Bytes;

        /// <summary>Byte offset (layout).</summary>
        public int Pc;

        /// <summary>A pseudo opcode: <see cref="Bytes"/> copied as they are.</summary>
        public const int Raw = -1;

        public CodeIns(int op)
        {
            Op = op;
        }

        /// <summary>Encoded size in bytes.</summary>
        public int Size
        {
            get
            {
                switch (Op)
                {
                    case Raw:
                        return Bytes!.Length;
                    case Compiled.Op.Const:
                        return 2 + Compiled.Op.ConstSize(A);
                    case Compiled.Op.Entry:
                        return 2 + 2 * Targets!.Length;
                    case Compiled.Op.Index:
                        return 2 + 2 * Shorts!.Length;
                    case Compiled.Op.StackVar:
                        return 2 + Bytes!.Length;
                }
                int f = Compiled.Op.Fixed(Op);
                return f >= 0 ? 1 + f : 1;
            }
        }
    }

    /// <summary>The pool offsets a code buffer's operands take (a library or a page).</summary>
    public interface IPoolOffsets
    {
        long Text(string s);
        long Tag(TagKey t);
        long CodeVar(VarRef v);

        /// <summary>The RUN-image DataAddress of a code variable (AddrCodeVar).</summary>
        long DataAddress(VarRef v);
        long FuncLookup(FuncInfo f);
        long Scale(ScaleKey s);
    }

    /// <summary>The instructions of one function or page block, then laid out to bytes.</summary>
    public sealed class CodeBuffer
    {
        public readonly List<CodeIns> Ins = new List<CodeIns>();

        public CodeIns Add(int op)
        {
            var i = new CodeIns(op);
            Ins.Add(i);
            return i;
        }

        public CodeLabel NewLabel() => new CodeLabel();

        public void Place(CodeLabel l) => l.At = Ins.Count;

        /// <summary>Byte offsets of every instruction; returns the total size.</summary>
        public int Layout()
        {
            int pc = 0;
            foreach (var i in Ins)
            {
                i.Pc = pc;
                pc += i.Size;
            }
            return pc;
        }

        private int PcOf(CodeLabel l, int size)
        {
            if (l.At < 0)
                throw new InvalidOperationException("label not placed");
            return l.At < Ins.Count ? Ins[l.At].Pc : size;
        }

        /// <summary>A jump distance as the i16 the runtime reads (Branch / Brfalse / BrTrue / Entry);
        /// a function too big for it is not compiled rather than given a wrong jump.</summary>
        private static short Jump(int delta)
        {
            if (delta < short.MinValue || delta > short.MaxValue)
                throw new CodegenException(
                    "a jump of "
                        + delta.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " bytes: the code is too big for the runtime's 16-bit jumps"
                );
            return (short)delta;
        }

        /// <summary>The largest code a function's u16 length word in the CODE pool holds.</summary>
        public const int MaxFunctionBytes = 0xFFFF;

        /// <summary>The bytes, operands resolved through <paramref name="pools"/>.</summary>
        public byte[] Encode(IPoolOffsets pools)
        {
            int size = Layout();
            var b = new byte[size];
            foreach (var i in Ins)
            {
                int p = i.Pc;
                if (i.Op == CodeIns.Raw)
                {
                    Array.Copy(i.Bytes!, 0, b, p, i.Bytes!.Length);
                    continue;
                }
                b[p] = (byte)i.Op;
                switch (i.Op)
                {
                    case Op.Const:
                        b[p + 1] = (byte)i.A;
                        switch (i.A)
                        {
                            case 0x04:
                            case 0x0C:
                                PutI32(b, p + 2, (int)i.L);
                                break;
                            case 0x02:
                                PutI64(b, p + 2, BitConverter.DoubleToInt64Bits(i.D));
                                break;
                            case 0x07:
                                PutI64(b, p + 2, pools.Text((string)i.Ref!));
                                break;
                            case 0x13:
                            case 0x14:
                                PutI64(b, p + 2, i.L);
                                break;
                            case 0x01:
                                PutI16(b, p + 2, (short)i.L);
                                break;
                            case 0x08:
                                b[p + 2] = (byte)i.L;
                                break;
                            case 0x11:
                                PutI32(b, p + 2, (int)i.L);
                                break;
                        }
                        break;
                    case Op.Load:
                    case Op.Store:
                        PutI64(b, p + 1, pools.Tag((TagKey)i.Ref!));
                        break;
                    case Op.LoadCodeVar:
                    case Op.StoreCodeVar:
                        PutI64(b, p + 1, pools.CodeVar((VarRef)i.Ref!));
                        break;
                    case Op.AddrCodeVar:
                        b[p + 1] = (byte)i.A;
                        PutI64(b, p + 2, pools.DataAddress((VarRef)i.Ref!));
                        break;
                    case Op.Func:
                        PutI16(b, p + 1, (short)i.L);
                        b[p + 3] = (byte)i.A;
                        break;
                    case Op.FuncCallByLookup:
                        PutI64(b, p + 1, pools.FuncLookup((FuncInfo)i.Ref!));
                        b[p + 9] = (byte)i.A;
                        break;
                    case Op.Entry:
                        b[p + 1] = (byte)i.Targets!.Length;
                        for (int k = 0; k < i.Targets.Length; k++)
                            PutI16(b, p + 2 + 2 * k, Jump(PcOf(i.Targets[k], size) - p));
                        break;
                    case Op.Index:
                        b[p + 1] = (byte)i.Shorts!.Length;
                        for (int k = 0; k < i.Shorts.Length; k++)
                            PutI16(b, p + 2 + 2 * k, i.Shorts[k]);
                        break;
                    case Op.StackVar:
                        b[p + 1] = (byte)i.Bytes!.Length;
                        Array.Copy(i.Bytes, 0, b, p + 2, i.Bytes.Length);
                        break;
                    case Op.Convert:
                    case Op.StoreStack:
                    case Op.LoadStack:
                    case Op.AddrStack:
                        b[p + 1] = (byte)i.A;
                        break;
                    case Op.Branch:
                    case Op.Brfalse:
                    case Op.BrTrue:
                        PutI16(b, p + 1, Jump(PcOf(i.Target!, size) - (p + 1)));
                        break;
                    case Op.Line:
                        PutI16(b, p + 1, (short)i.L);
                        break;
                    case Op.Bar:
                        PutI32(b, p + 1, i.Ref is ScaleKey sk ? (int)pools.Scale(sk) : -1);
                        break;
                    case Op.AssFmt:
                        PutI32(b, p + 1, (int)pools.Text((string)i.Ref!));
                        break;
                    case Op.StringFmt:
                        PutI32(b, p + 1, (int)i.L);
                        PutI64(b, p + 5, pools.Text((string)i.Ref!));
                        break;
                }
            }
            return b;
        }

        internal static void PutI16(byte[] b, int p, short v)
        {
            b[p] = (byte)v;
            b[p + 1] = (byte)(v >> 8);
        }

        internal static void PutI32(byte[] b, int p, int v)
        {
            b[p] = (byte)v;
            b[p + 1] = (byte)(v >> 8);
            b[p + 2] = (byte)(v >> 16);
            b[p + 3] = (byte)(v >> 24);
        }

        internal static void PutI64(byte[] b, int p, long v)
        {
            PutI32(b, p, (int)v);
            PutI32(b, p + 4, (int)(v >> 32));
        }
    }
}
