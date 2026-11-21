using System;
using System.Collections.Generic;

namespace CicodeSim.Compiled
{
    /// <summary>The Cicode p-code instruction set.</summary>
    public static class Op
    {
        public const int Stop = 0x00;
        public const int Const = 0x01;
        public const int Load = 0x02;
        public const int Store = 0x03;
        public const int Write = 0x04;
        public const int Func = 0x05;
        public const int Ufunc = 0x06;
        public const int Pop = 0x07;
        public const int Entry = 0x08;
        public const int Convert = 0x09;
        public const int String = 0x0A;
        public const int Branch = 0x0B;
        public const int StringPtr = 0x0C;
        public const int Brfalse = 0x0D;
        public const int Ret = 0x0E;
        public const int StackVar = 0x0F;
        public const int Scale = 0x10;
        public const int UnScale = 0x11;
        public const int Neg = 0x12;
        public const int Fneg = 0x13;
        public const int Not = 0x14;
        public const int Mul = 0x15;
        public const int Div = 0x17;
        public const int Mod = 0x19;
        public const int Add = 0x1B;
        public const int Sub = 0x1E;
        public const int Lt = 0x20;
        public const int Le = 0x23;
        public const int Gt = 0x26;
        public const int Ge = 0x29;
        public const int Eq = 0x2C;
        public const int Ne = 0x2F;
        public const int And = 0x32;
        public const int Or = 0x33;
        public const int Line = 0x34;
        public const int StoreStack = 0x35;
        public const int LoadStack = 0x36;
        public const int Bar = 0x37;
        public const int BitAnd = 0x38;
        public const int BitOr = 0x39;
        public const int BitXor = 0x3A;
        public const int AddrStack = 0x3B;
        public const int AddrCodeVar = 0x3C;
        public const int StoreCodeVar = 0x3D;
        public const int WriteReal = 0x3E;
        public const int RetValue = 0x3F;
        public const int Index = 0x40;
        public const int RawSlider = 0x41;
        public const int AssFmt = 0x42;
        public const int DupEx = 0x43;
        public const int BrTrue = 0x44;
        public const int WriteValue = 0x45;
        public const int StringFmt = 0x46;
        public const int LoadCodeVar = 0x47;
        public const int FuncCallByName = 0x48;
        public const int FuncCallByLookup = 0x49;
        public const int End = 0xFF;

        /// <summary>An internal pseudo opcode for an unknown Const sub-type.</summary>
        public const int UnsupportedConst = 0x100;

        /// <summary>CtCmp32's opcode names.</summary>
        public static readonly string[] Names =
        {
            "Stop",
            "Const",
            "Load",
            "Store",
            "Write",
            "Func",
            "Ufunc",
            "Pop",
            "Entry",
            "Convert",
            "String",
            "Branch",
            "StringPtr",
            "Brfalse",
            "Ret",
            "StackVar",
            "Scale",
            "UnScale",
            "Neg",
            "Fneg",
            "Not",
            "Mul",
            "Fmul",
            "Div",
            "Fdiv",
            "Mod",
            "Fmod",
            "Add",
            "Fadd",
            "Sadd",
            "Sub",
            "Fsub",
            "Lt",
            "Flt",
            "Slt",
            "Le",
            "Fle",
            "Sle",
            "Gt",
            "Fgt",
            "Sgt",
            "Ge",
            "Fge",
            "Sge",
            "Eq",
            "Feq",
            "Seq",
            "Ne",
            "Fne",
            "Sne",
            "And",
            "Or",
            "Line",
            "StoreStack",
            "LoadStack",
            "Bar",
            "BitAnd",
            "BitOr",
            "BitXor",
            "AddrStack",
            "AddrCodeVar",
            "StoreCodeVar",
            "WriteReal",
            "RetValue",
            "Index",
            "RawSlider",
            "AssFmt",
            "DupEx",
            "BrTrue",
            "WriteValue",
            "StringFmt",
            "LoadCodeVar",
            "FuncCallByName",
            "FuncCallByLookup",
        };

        public static string Name(int op)
        {
            if (op == End)
                return "End";
            if (op >= 0 && op < Names.Length)
                return Names[op];
            return "?0x" + op.ToString("x2");
        }

        /// <summary>The opcode with this CtCmp32 name (case-insensitive), -1 when none.</summary>
        public static int ByName(string name)
        {
            if (string.Equals(name, "end", StringComparison.OrdinalIgnoreCase))
                return End;
            for (int k = 0; k < Names.Length; k++)
                if (string.Equals(Names[k], name, StringComparison.OrdinalIgnoreCase))
                    return k;
            return -1;
        }

        /// <summary>Opcodes the interpreter treats as STOP:
        /// 4, 0x10, 0x11, 0x13, the typed F*/S* variants, 0x3e, 0x41,
        /// 0x45. 6 Ufunc and 0 Stop fall to the default case (halt).</summary>
        public static bool IsUnsupported(int op)
        {
            switch (op)
            {
                case 0x04:
                case 0x10:
                case 0x11:
                case 0x13:
                case 0x16:
                case 0x18:
                case 0x1A:
                case 0x1C:
                case 0x1D:
                case 0x1F:
                case 0x21:
                case 0x22:
                case 0x24:
                case 0x25:
                case 0x27:
                case 0x28:
                case 0x2A:
                case 0x2B:
                case 0x2D:
                case 0x2E:
                case 0x30:
                case 0x31:
                case 0x3E:
                case 0x41:
                case 0x45:
                    return true;
            }
            return false;
        }

        /// <summary>Const sub-type -> operand byte count after the sub byte (
        /// case 1); -1 = unknown.</summary>
        public static int ConstSize(int sub)
        {
            switch (sub)
            {
                case 0x01:
                    return 2;
                case 0x02:
                    return 8;
                case 0x04:
                    return 4;
                case 0x07:
                    return 8;
                case 0x08:
                    return 1;
                case 0x0C:
                    return 4;
                case 0x11:
                    return 4; // the pc moves by 4 after
                case 0x13:
                    return 8;
                case 0x14:
                    return 8;
            }
            return -1;
        }

        /// <summary>Fixed operand sizes after the opcode byte, -1 for the variable ones.</summary>
        public static int Fixed(int op)
        {
            switch (op)
            {
                case Load:
                case Store:
                    return 8;
                case Func:
                    return 3;
                case Convert:
                    return 1;
                case Branch:
                case Brfalse:
                case BrTrue:
                case Line:
                    return 2;
                case StoreStack:
                case LoadStack:
                case AddrStack:
                    return 1;
                case Bar:
                    return 4;
                case AddrCodeVar:
                    return 9;
                case StoreCodeVar:
                case LoadCodeVar:
                    return 8;
                case AssFmt:
                    return 4;
                case StringFmt:
                    return 12;
                case FuncCallByName:
                case FuncCallByLookup:
                    return 9;
            }
            return -1;
        }

        /// <summary>The opcodes an Index (0x40) prefix may precede.</summary>
        public static bool IsIndexable(int op) =>
            op == Load || op == Store || op == AddrCodeVar || op == StoreCodeVar || op == LoadCodeVar;
    }

    /// <summary>Code that is not a valid instruction stream (unknown opcode or truncated).</summary>
    public sealed class DecodeError : Exception
    {
        public DecodeError(string message)
            : base(message) { }
    }

    /// <summary>One decoded instruction.</summary>
    public struct Instr
    {
        public int Pc;
        public int Op;
        public int Size;
        public int Sub;
        public int A;
        public int B;
        public long IntValue;
        public double RealValue;
        public ulong U64;
        public long Offset;
        public uint U32;
        public short[]? Shorts;
        public byte[]? Bytes;
    }

    public static class Opcodes
    {
        /// <summary>Byte length of the instruction at <paramref name="pc"/> (Index counts its own
        /// prefix only).</summary>
        public static int Length(byte[] code, int pc)
        {
            int op = code[pc];
            if (op == Op.Const)
            {
                if (pc + 1 >= code.Length)
                    throw new IndexOutOfRangeException("index out of range");
                int sub = code[pc + 1];
                int n = Op.ConstSize(sub);
                if (n < 0)
                    throw new DecodeError($"Const with unknown sub-type 0x{sub:x2} at 0x{pc:x}");
                return 2 + n;
            }
            int f = Op.Fixed(op);
            if (f >= 0)
                return 1 + f;
            if (op == Op.Entry || op == Op.Index)
            {
                if (pc + 1 >= code.Length)
                    throw new IndexOutOfRangeException("index out of range");
                return 2 + 2 * code[pc + 1];
            }
            if (op == Op.StackVar)
            {
                if (pc + 1 >= code.Length)
                    throw new IndexOutOfRangeException("index out of range");
                return 2 + code[pc + 1];
            }
            if (op < Op.Names.Length || op == Op.End)
                return 1;
            throw new DecodeError($"unknown opcode 0x{op:x2} at 0x{pc:x}");
        }

        private static ushort U16(byte[] b, int p) => (ushort)(b[p] | (b[p + 1] << 8));

        private static short I16(byte[] b, int p) => (short)(b[p] | (b[p + 1] << 8));

        private static uint U32(byte[] b, int p) =>
            (uint)(b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24));

        private static ulong U64(byte[] b, int p) => U32(b, p) | ((ulong)U32(b, p + 4) << 32);

        /// <summary>Decode one instruction (see <see cref="Instr"/> for the operands).</summary>
        public static Instr Decode(byte[] code, int pc)
        {
            if (pc < 0 || pc >= code.Length)
                throw new IndexOutOfRangeException("index out of range");
            int op = code[pc];
            int size = Length(code, pc); // a missing sub / count byte throws IndexOutOfRange
            if (pc + size > code.Length)
                throw new DecodeError($"{Op.Name(op)} at 0x{pc:x} runs past the end of the code");
            var ins = new Instr
            {
                Pc = pc,
                Op = op,
                Size = size,
            };
            int a = pc + 1;
            switch (op)
            {
                case Op.Const:
                {
                    int sub = code[a];
                    int p = a + 1;
                    ins.Sub = sub;
                    switch (sub)
                    {
                        case 0x01:
                            ins.IntValue = I16(code, p);
                            break;
                        case 0x02:
                            ins.RealValue = BitConverter.Int64BitsToDouble((long)U64(code, p));
                            break;
                        case 0x04:
                        case 0x0C:
                            ins.IntValue = (int)U32(code, p);
                            break;
                        case 0x08:
                            ins.IntValue = (sbyte)code[p];
                            break;
                        case 0x11:
                            ins.IntValue = U32(code, p);
                            break;
                        default:
                            ins.U64 = U64(code, p);
                            break; // 7 string offset, 0x13 / 0x14 64-bit
                    }
                    break;
                }
                case Op.Load:
                case Op.Store:
                case Op.StoreCodeVar:
                case Op.LoadCodeVar:
                    ins.U64 = U64(code, a);
                    ins.Offset = (long)ins.U64;
                    break;
                case Op.AddrCodeVar:
                    ins.Sub = code[a];
                    ins.U64 = U64(code, a + 1);
                    ins.Offset = (long)ins.U64;
                    break;
                case Op.Func:
                    ins.A = U16(code, a);
                    ins.B = code[a + 2];
                    break;
                case Op.Entry:
                case Op.Index:
                {
                    int n = code[a];
                    var s = new short[n];
                    for (int k = 0; k < n; k++)
                        s[k] = I16(code, a + 1 + 2 * k);
                    ins.Shorts = s;
                    break;
                }
                case Op.Convert:
                case Op.StoreStack:
                case Op.LoadStack:
                case Op.AddrStack:
                    ins.A = code[a];
                    break;
                case Op.Branch:
                case Op.Brfalse:
                case Op.BrTrue:
                    ins.A = a + I16(code, a);
                    break;
                case Op.StackVar:
                {
                    int n = code[a];
                    var t = new byte[n];
                    Array.Copy(code, a + 1, t, 0, n);
                    ins.Bytes = t;
                    break;
                }
                case Op.Line:
                    ins.A = U16(code, a);
                    break;
                case Op.Bar:
                case Op.AssFmt:
                    ins.U32 = U32(code, a);
                    break;
                case Op.StringFmt:
                    ins.U32 = U32(code, a);
                    ins.U64 = U64(code, a + 4);
                    ins.Offset = (long)ins.U64;
                    break;
                case Op.FuncCallByName:
                case Op.FuncCallByLookup:
                    ins.U64 = U64(code, a);
                    ins.Offset = (long)ins.U64;
                    ins.A = code[a + 8];
                    break;
            }
            return ins;
        }

        /// <summary>Linear sweep from <paramref name="start"/> to the end-of-code byte 0xff
        /// (inclusive). Throws DecodeError on an unknown opcode or a stream that runs off the end.</summary>
        public static List<Instr> Instructions(byte[] code, int start = 0)
        {
            var output = new List<Instr>();
            int pc = start;
            int n = code.Length;
            while (pc < n)
            {
                Instr ins = Decode(code, pc);
                output.Add(ins);
                if (ins.Op == Op.End)
                    return output;
                pc += ins.Size;
            }
            throw new DecodeError("code has no end byte (0xff)");
        }

        /// <summary>null when <paramref name="code"/> decodes to exactly its own length ending in
        /// 0xff, else why not.</summary>
        public static string? SweepOk(byte[] code)
        {
            List<Instr> ins;
            try
            {
                ins = Instructions(code);
            }
            catch (DecodeError e)
            {
                return e.Message;
            }
            int end = ins[ins.Count - 1].Pc + 1;
            if (end != code.Length)
                return $"end byte at 0x{ins[ins.Count - 1].Pc:x} but the code is {code.Length} bytes";
            return null;
        }
    }
}
