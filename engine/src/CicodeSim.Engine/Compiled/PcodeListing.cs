using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CicodeSim.Values;

namespace CicodeSim.Compiled
{
    /// <summary>The listing of compiled Cicode p-code.</summary>
    public static class PcodeListing
    {
        private static readonly Dictionary<int, string> TypeNames = new Dictionary<int, string>
        {
            [0] = "DIGITAL",
            [1] = "INT16",
            [2] = "REAL",
            [3] = "BCD",
            [4] = "INT",
            [5] = "LONGBCD",
            [6] = "LONGREAL",
            [7] = "STRING",
            [8] = "BYTE",
            [9] = "VOID",
            [0xA] = "UINT",
            [0xB] = "REALVALUE",
            [0xC] = "OBJECT",
            [0xD] = "VARIANT",
            [0x10] = "VARARG",
            [0x11] = "TAG",
            [0x12] = "ULONG",
            [0x13] = "TIMESTAMP",
            [0x14] = "QUALITY",
        };

        public static string TypeName(int t) => TypeNames.TryGetValue(t, out var n) ? n : "type" + HexAlt(t);

        /// <summary>0x and the hex digits (-0x for a negative v).</summary>
        private static string HexAlt(long v) =>
            v < 0 ? "-0x" + ((ulong)(-v)).ToString("x") : "0x" + v.ToString("x");

        /// <summary>At least 4 hex digits (a negative v: '-' and at least 3).</summary>
        private static string Hex4(long v)
        {
            if (v >= 0)
                return v.ToString("x4");
            string d = ((ulong)(-v)).ToString("x");
            return "-" + d.PadLeft(3, '0');
        }

        private static string Q(string s) =>
            "\""
            + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n")
            + "\"";

        private static string Hex(ulong v) => v.ToString("x", CultureInfo.InvariantCulture);

        private static string Dec(long v) => v.ToString(CultureInfo.InvariantCulture);

        /// <summary>The resolved operands of one instruction.</summary>
        public static string OperandText(
            in Instr ins,
            Program prog,
            Function? fn = null,
            Func<int, string>? builtinName = null
        )
        {
            switch (ins.Op)
            {
                case Op.Const:
                {
                    int sub = ins.Sub;
                    if (sub == Types.String)
                        return "STRING " + Q(prog.StringAt((long)ins.U64));
                    if (sub == Types.Real)
                        return "REAL " + FloatText.Shortest(ins.RealValue);
                    if (sub == 0x13 || sub == 0x14)
                        return TypeName(sub) + " 0x" + Hex(ins.U64);
                    // sub 1 / 4 / 8 / 0xc / 0x11 are signed / small ints; others print the u64
                    long v =
                        (sub == 0x01 || sub == 0x04 || sub == 0x08 || sub == 0x0C || sub == 0x11)
                            ? ins.IntValue
                            : 0;
                    if (sub == 0x01 || sub == 0x04 || sub == 0x08 || sub == 0x0C || sub == 0x11)
                        return TypeName(sub) + " " + Dec(v);
                    return TypeName(sub) + " " + ins.U64.ToString(CultureInfo.InvariantCulture);
                }
                case Op.Load:
                case Op.Store:
                {
                    if (!prog.TagRefs.TryGetValue(ins.Offset, out var r) || (long)ins.U64 != ins.Offset)
                        return "tagref@" + ins.U64.ToString(CultureInfo.InvariantCulture) + " ?";
                    string s = (r.Cluster.Length > 0 ? r.Cluster + ":" : "") + r.Tag;
                    if (r.Element.Length > 0)
                        s += "." + r.Element;
                    if (r.Item.Length > 0)
                        s += "." + r.Item;
                    return s.Length > 0 ? s : "tagref@" + ins.U64.ToString(CultureInfo.InvariantCulture);
                }
                case Op.LoadCodeVar:
                case Op.StoreCodeVar:
                    return prog.CodeVars.TryGetValue(ins.Offset, out var cv)
                        ? cv.Name
                        : "codevar@" + ins.U64.ToString(CultureInfo.InvariantCulture) + " ?";
                case Op.AddrCodeVar:
                    // the operand is a RUN DataAddress (row 0x3c)
                    return TypeName(ins.Sub)
                        + " "
                        + (
                            ins.U64 <= long.MaxValue && prog.CodeVarAtData(ins.Offset) is CodeVar cv2
                                ? cv2.Name
                                : "data@" + ins.U64.ToString(CultureInfo.InvariantCulture) + " ?"
                        );
                case Op.Func:
                {
                    string nm = builtinName?.Invoke(ins.A) ?? "";
                    return (nm.Length > 0 ? nm : "builtin#" + ins.A) + "(" + ins.B + ")  #" + ins.A;
                }
                case Op.FuncCallByLookup:
                    return (
                            prog.FuncLookups.TryGetValue(ins.Offset, out var fl)
                                ? fl.Name
                                : "lookup@" + ins.U64.ToString(CultureInfo.InvariantCulture) + " ?"
                        )
                        + "("
                        + ins.A
                        + ")";
                case Op.FuncCallByName:
                    return prog.StringAt((long)ins.U64) + "(" + ins.A + ")";
                case Op.Branch:
                case Op.Brfalse:
                case Op.BrTrue:
                    return "-> " + Hex4(ins.A);
                case Op.Entry:
                {
                    var parts = new List<string>();
                    for (int k = 0; k < ins.Shorts!.Length; k++)
                        parts.Add(k + " args -> " + Hex4(ins.Pc + ins.Shorts[k]));
                    return string.Join("  ", parts);
                }
                case Op.Convert:
                    return TypeName(ins.A);
                case Op.StackVar:
                {
                    var parts = new List<string>();
                    foreach (byte t in ins.Bytes!)
                        parts.Add(TypeName(t));
                    return string.Join(", ", parts);
                }
                case Op.Line:
                    return Dec(ins.A);
                case Op.StoreStack:
                case Op.LoadStack:
                case Op.AddrStack:
                {
                    string nm = LocalName(fn, ins.A);
                    return Dec(ins.A) + (nm.Length > 0 ? " (" + nm + ")" : "");
                }
                case Op.Bar:
                {
                    if (ins.U32 == 0xFFFFFFFFu)
                        return "tag scale";
                    if (!prog.Scales.TryGetValue(ins.U32, out var sc))
                        return "scale@" + ins.U32 + " ?";
                    return "eng "
                        + FloatText.Shortest(sc.EngZero)
                        + ".."
                        + FloatText.Shortest(sc.EngFull)
                        + " raw "
                        + FloatText.Shortest(sc.RawZero)
                        + ".."
                        + FloatText.Shortest(sc.RawFull);
                }
                case Op.AssFmt:
                    return "0x" + Hex(ins.U32);
                case Op.StringFmt:
                {
                    uint w = ins.U32;
                    string suf =
                        ins.U64 < (ulong)prog.Text.Length
                            ? prog.StringAt((long)ins.U64)
                            : "@" + ins.U64.ToString(CultureInfo.InvariantCulture);
                    return "0x" + Hex(w) + " " + Q(suf) + "  (" + FormatWordText(w) + ")";
                }
                case Op.Index:
                {
                    var parts = new List<string>();
                    foreach (short d in ins.Shorts!)
                        parts.Add(Dec(d));
                    return string.Join("x", parts);
                }
            }
            return "";
        }

        public static string FormatWordText(uint w)
        {
            uint width = w & 0xFF,
                dec = (w >> 8) & 0xFF,
                fl = w >> 16;
            string s = "w=" + width + " d=" + dec;
            var extra = new List<string>();
            (uint Bit, string Name)[] names =
            {
                (1, "zero"),
                (2, "left"),
                (4, "suffix"),
                (0x10, "exp"),
                (0x20, "0x20"),
            };
            foreach (var (bit, name) in names)
                if ((fl & bit) != 0)
                    extra.Add(name);
            uint rest = fl & ~0x37u;
            if (rest != 0)
                extra.Add("0x" + Hex(rest));
            return s + (extra.Count > 0 ? " " + string.Join(",", extra) : "");
        }

        private static string LocalName(Function? fn, int slot)
        {
            if (fn == null)
                return "";
            foreach (var (off, nm) in fn.Locals)
                if (off == slot)
                    return nm;
            return "";
        }

        /// <summary>One line per instruction: offset, bytes, name, resolved operands.</summary>
        public static List<string> Listing(
            byte[] code,
            Program prog,
            Function? fn = null,
            Func<int, string>? builtinName = null
        )
        {
            var output = new List<string>();
            List<Instr> instrs;
            try
            {
                instrs = Opcodes.Instructions(code);
            }
            catch (DecodeError e)
            {
                output.Add("  !! " + e.Message);
                return output;
            }
            var sb = new StringBuilder();
            foreach (var ins in instrs)
            {
                sb.Clear();
                for (int k = 0; k < ins.Size; k++)
                    sb.Append(code[ins.Pc + k].ToString("x2"));
                string raw = sb.ToString();
                if (raw.Length > 26)
                    raw = raw.Substring(0, 24) + "..";
                string line =
                    "  "
                    + ins.Pc.ToString("x4")
                    + ": "
                    + raw.PadRight(26)
                    + " "
                    + Op.Name(ins.Op).PadRight(16)
                    + " "
                    + OperandText(ins, prog, fn, builtinName);
                output.Add(line.TrimEnd(Blanks));
            }
            return output;
        }

        private static readonly char[] Blanks =
        {
            ' ',
            '\t',
            '\n',
            '\v',
            '\f',
            '\r',
            '\x1c',
            '\x1d',
            '\x1e',
            '\x1f',
            '\x85',
            '\xa0',
        };

        public static string FunctionHeader(Function fn)
        {
            var args = new List<string>();
            foreach (char c in fn.Args)
                args.Add(TypeName(Types.SigType(c)));
            var loc = new List<string>();
            foreach (var (o, n) in fn.Locals)
                loc.Add(o + ":" + n);
            return "FUNCTION "
                + fn.Name
                + "("
                + string.Join(", ", args)
                + ") -> "
                + TypeName(fn.Result)
                + "  min="
                + fn.MinParam
                + " #"
                + fn.Index
                + "\n  ; "
                + fn.Filename
                + "\n  ; locals "
                + string.Join(", ", loc);
        }

        public static Func<int, string> BuiltinNamer()
        {
            try
            {
                var tab = Func0Table.Default;
                return i => tab.Get(i)?.Name ?? "";
            }
            catch (Exception)
            {
                return i => "";
            }
        }

        /// <summary>
        /// The p-code listing of a library or page, as ``ctsim-dev pcode`` prints it.
        /// </summary>
        public static string Run(string path, IList<string> names)
        {
            var low = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in names)
                low.Add(n.ToLowerInvariant());
            var f = RdbFile.Open(path);
            var namer = BuiltinNamer();
            var output = new List<string>();
            if (f.Has("Library") && f.Sections["Library"].NRec != 0)
            {
                var prog = Rdb.LibraryFromRdb(f);
                foreach (var fn in prog.Functions)
                {
                    if (
                        low.Count > 0
                        && !low.Contains(fn.Name.ToLowerInvariant())
                        && !low.Contains(fn.Index.ToString(CultureInfo.InvariantCulture))
                    )
                        continue;
                    output.Add(FunctionHeader(fn));
                    output.AddRange(Listing(fn.Code, prog, fn, namer));
                    output.Add("");
                }
            }
            else
            {
                var pc = Rdb.PageFromRdb(f);
                foreach (var b in pc.Blocks)
                {
                    string label = b.Table + "." + b.Record + "." + b.Field;
                    if (
                        low.Count > 0
                        && !low.Contains(label.ToLowerInvariant())
                        && !low.Contains(b.Table.ToLowerInvariant())
                    )
                        continue;
                    string kind = b.Kind == RdbFile.FExpr ? "expression" : "command";
                    output.Add(label + "  AN " + b.An + "  (" + kind + ")");
                    output.AddRange(Listing(b.Code, pc.Program, null, namer));
                    output.Add("");
                }
            }
            return string.Join("\n", output) + "\n";
        }
    }
}
