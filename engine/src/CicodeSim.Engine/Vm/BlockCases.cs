using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Values;

namespace CicodeSim.Vm
{
    /// <summary>One self-contained p-code block case from the golden data.</summary>
    public static class BlockCases
    {
        private sealed class Tags : ITagAccess, ITagScale
        {
            public Value Read(TagRef r, CicodeTask task) => Value.Long(0);

            public int Write(TagRef r, Value value, CicodeTask task) => 0;

            public bool TryScale(TagRef? r, CicodeTask task, out double engZero, out double engFull)
            {
                engZero = 10.0;
                engFull = 50.0;
                return true;
            }
        }

        private static byte[] Hex(string s)
        {
            var b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = byte.Parse(
                    s.Substring(2 * i, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture
                );
            return b;
        }

        private static long L(object? o) =>
            o is long l ? l : Convert.ToInt64(o, CultureInfo.InvariantCulture);

        private static double D(object? o) =>
            o is double d ? d : Convert.ToDouble(o, CultureInfo.InvariantCulture);

        /// <summary>A value as the block cases encode it: {t, v, q, ts, vts, qts}, a REAL as
        /// {"f": its shortest round-trip text}, a reference as "&lt;ref&gt;", TIMESTAMP /
        /// QUALITY unsigned.</summary>
        public static void WriteValue(StringBuilder sb, Value? rv)
        {
            if (!rv.HasValue)
            {
                sb.Append("null");
                return;
            }
            Value v = rv.Value;
            sb.Append("{\"t\":").Append(v.T.ToString(CultureInfo.InvariantCulture)).Append(",\"v\":");
            if (Types.IsRealType(v.T))
            {
                sb.Append("{\"f\":");
                Json.WriteString(sb, FloatText.Shortest(v.D));
                sb.Append('}');
            }
            else if (v.T == Types.String)
                Json.WriteString(sb, v.S);
            else if (v.T == Types.Variant && v.O != null)
                sb.Append("\"<ref>\"");
            else if (v.T == Types.Timestamp || v.T == Types.Quality)
                sb.Append(unchecked((ulong)v.I).ToString(CultureInfo.InvariantCulture));
            else
                sb.Append(v.I.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"q\":").Append(v.Q.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"ts\":").Append(v.Ts.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"vts\":").Append(v.Vts.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"qts\":").Append(v.Qts.ToString(CultureInfo.InvariantCulture));
            sb.Append('}');
        }

        private static string I(long v) => v.ToString(CultureInfo.InvariantCulture);

        /// <summary>Run one case (a parsed JSON object); the result JSON.</summary>
        public static string Run(Dictionary<string, object?> c, IBuiltins builtins)
        {
            var prog = new Program { Text = Hex((string)c["text"]!) };
            foreach (var o in (List<object?>)c["scales"]!)
            {
                var l = (List<object?>)o!;
                long off = L(l[0]);
                prog.Scales[off] = new Scale(off)
                {
                    RawZero = D(l[1]),
                    RawFull = D(l[2]),
                    EngZero = D(l[3]),
                    EngFull = D(l[4]),
                };
            }
            if (c.TryGetValue("run", out var runHex) && runHex is string rh)
                prog.RunImage = Hex(rh);
            if (c.TryGetValue("cvs", out var cvsObj) && cvsObj is List<object?> cvs)
            {
                foreach (var o in cvs)
                {
                    var d = (Dictionary<string, object?>)o!;
                    prog.AddCodeVar(
                        new CodeVar(L(d["off"]), (string)d["name"]!, (int)L(d["rt"]))
                        {
                            BitWidth = L(d["bw"]),
                            UnitCount = L(d["n"]),
                            DataAddress = L(d["addr"]),
                            Quality = (ulong)L(d["q"]),
                            Timestamp = (ulong)L(d["ts"]),
                            ValueTimestamp = (ulong)L(d["vts"]),
                            QualityTimestamp = (ulong)L(d["qts"]),
                        }
                    );
                }
            }
            var cfg = new CodeConfig { MaxSliceOps = 20000, IgnoreCase = L(c["ic"]) != 0 };
            var vm = new CicodeVm(prog, builtins, new Tags(), new ManualClock(tickMs: 1.0), null, cfg);
            var sb = new StringBuilder(256);
            sb.Append('{');
            try
            {
                var t = vm.TaskForBlock(Hex((string)c["code"]!), prog, L(c["fg"]) != 0);
                int k = 0;
                while (!t.Done && k < 20)
                {
                    if (t.State == TaskState.Wait || t.State == TaskState.Sleep)
                        break;
                    vm.RunSlice(t);
                    k++;
                }
                sb.Append("\"state\":").Append(I(t.State));
                sb.Append(",\"code\":").Append(I(t.HaltCode));
                sb.Append(",\"reason\":");
                Json.WriteString(sb, t.HaltReason);
                sb.Append(",\"pc\":").Append(I(t.Pc));
                sb.Append(",\"instr\":").Append(I(t.Instructions));
                sb.Append(",\"error\":").Append(I(t.Error));
                sb.Append(",\"expr\":").Append(I(t.ExprError));
                sb.Append(",\"errset\":").Append(I(t.ErrSetLevel));
                sb.Append(",\"stack\":[");
                for (int i = 0; i < t.Sp; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    WriteValue(sb, t.Stack[i]);
                }
                sb.Append("],\"result\":");
                WriteValue(sb, t.Result);
                sb.Append(",\"glb\":[").Append(I(vm.GlbInt[0])).Append(',');
                Json.WriteString(sb, FloatText.Shortest(vm.GlbFloat[0]));
                sb.Append(',').Append(I(vm.GlbBool[0])).Append(',');
                Json.WriteString(sb, vm.GlbStr[0]);
                sb.Append("],\"slots\":[");
                bool first = true;
                foreach (var cv in prog.CodeVarOrder)
                {
                    if (!first)
                        sb.Append(',');
                    first = false;
                    var sl = vm.Slot(prog, cv);
                    sb.Append('[');
                    for (int e = 0; e < sl.Count; e++)
                    {
                        if (e > 0)
                            sb.Append(',');
                        WriteValue(sb, sl.Get(e, out _));
                    }
                    sb.Append(']');
                }
                sb.Append(']');
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                sb.Append("\"exc\":");
                Json.WriteString(sb, e.GetType().Name + ": " + e.Message);
            }
            sb.Append('}');
            return sb.ToString();
        }
    }
}
