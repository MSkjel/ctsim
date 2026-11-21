using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CicodeSim.Compiled;
using CicodeSim.Values;

namespace CicodeSim.Compiler.Codegen
{
    /// <summary>Instructions with their operands resolved through the pools (strings, tags, code
    /// variables by offset and AddrCodeVar by RUN DataAddress, built-ins, callees, scales), so code
    /// from two different pool layouts compares.</summary>
    public static class NormalCode
    {
        public static List<string> Of(byte[] code, Program prog, Dictionary<long, CodeVar>? byData = null)
        {
            byData ??= ByDataAddress(prog);
            var o = new List<string>();
            List<Instr> ins;
            try
            {
                ins = Opcodes.Instructions(code);
            }
            catch (Exception e)
            {
                o.Add("!! " + e.Message);
                return o;
            }
            foreach (var i in ins)
                o.Add(i.Pc.ToString("x4", CultureInfo.InvariantCulture) + " " + One(i, prog, byData));
            return o;
        }

        public static Dictionary<long, CodeVar> ByDataAddress(Program prog)
        {
            var d = new Dictionary<long, CodeVar>();
            foreach (var cv in prog.CodeVarOrder)
                if (!d.ContainsKey(cv.DataAddress))
                    d[cv.DataAddress] = cv;
            return d;
        }

        private static string Q(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\')
                    sb.Append('\\').Append(c);
                else if (c < ' ' || c > '~')
                    sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else
                    sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        private static string Cv(CodeVar? cv, long raw) =>
            cv == null
                ? "?@" + raw.ToString(CultureInfo.InvariantCulture)
                : cv.Name
                    + "@"
                    + cv.UnitType.ToString(CultureInfo.InvariantCulture)
                    + ":"
                    + cv.UnitAddress.ToString(CultureInfo.InvariantCulture);

        public static string One(in Instr i, Program prog, Dictionary<long, CodeVar> byData)
        {
            string n = Op.Name(i.Op);
            switch (i.Op)
            {
                case Op.Const:
                    switch (i.Sub)
                    {
                        case 0x07:
                            return n + " S " + Q(prog.StringAt((long)i.U64));
                        case 0x02:
                            return n
                                + " R "
                                + BitConverter
                                    .DoubleToInt64Bits(i.RealValue)
                                    .ToString("x16", CultureInfo.InvariantCulture);
                        case 0x13:
                        case 0x14:
                            return n
                                + " "
                                + i.Sub.ToString("x", CultureInfo.InvariantCulture)
                                + " "
                                + i.U64.ToString(CultureInfo.InvariantCulture);
                    }
                    return n
                        + " "
                        + i.Sub.ToString("x", CultureInfo.InvariantCulture)
                        + " "
                        + i.IntValue.ToString(CultureInfo.InvariantCulture);
                case Op.Load:
                case Op.Store:
                    if (prog.TagRefs.TryGetValue(i.Offset, out var t))
                        return n
                            + " "
                            + Q(t.Cluster)
                            + "|"
                            + Q(t.Tag)
                            + "|"
                            + Q(t.Element)
                            + "|"
                            + Q(t.Item)
                            + "|"
                            + t.Percentage
                            + "|"
                            + t.Lightweight;
                    return n + " ?" + i.Offset;
                case Op.LoadCodeVar:
                case Op.StoreCodeVar:
                    return n
                        + " "
                        + Cv(prog.CodeVars.TryGetValue(i.Offset, out var cv) ? cv : null, i.Offset);
                case Op.AddrCodeVar:
                    return n
                        + " "
                        + i.Sub.ToString("x", CultureInfo.InvariantCulture)
                        + " "
                        + Cv(byData.TryGetValue(i.Offset, out var cv2) ? cv2 : null, i.Offset);
                case Op.Func:
                    return n
                        + " "
                        + i.A.ToString(CultureInfo.InvariantCulture)
                        + " "
                        + i.B.ToString(CultureInfo.InvariantCulture);
                case Op.FuncCallByLookup:
                    return n
                        + " "
                        + (prog.FuncLookups.TryGetValue(i.Offset, out var fl) ? fl.Name : "?" + i.Offset)
                        + " "
                        + i.A;
                case Op.FuncCallByName:
                    return n + " " + prog.StringAt((long)i.U64) + " " + i.A;
                case Op.Branch:
                case Op.Brfalse:
                case Op.BrTrue:
                    return n + " " + i.A.ToString("x4", CultureInfo.InvariantCulture);
                case Op.Entry:
                case Op.Index:
                    return n
                        + " "
                        + string.Join(
                            ",",
                            Array.ConvertAll(i.Shorts!, s => s.ToString(CultureInfo.InvariantCulture))
                        );
                case Op.StackVar:
                    return n + " " + BitConverter.ToString(i.Bytes!);
                case Op.Convert:
                case Op.StoreStack:
                case Op.LoadStack:
                case Op.AddrStack:
                case Op.Line:
                    return n + " " + i.A.ToString(CultureInfo.InvariantCulture);
                case Op.Bar:
                    if (i.U32 == 0xFFFFFFFFu)
                        return n + " tag";
                    if (prog.Scales.TryGetValue(i.U32, out var sc))
                        return n
                            + " "
                            + FloatText.Shortest(sc.RawZero)
                            + ","
                            + FloatText.Shortest(sc.RawFull)
                            + ","
                            + FloatText.Shortest(sc.EngZero)
                            + ","
                            + FloatText.Shortest(sc.EngFull);
                    return n + " ?" + i.U32;
                case Op.AssFmt:
                    return n + " " + Q(prog.StringAt(i.U32));
                case Op.StringFmt:
                    return n
                        + " "
                        + i.U32.ToString("x", CultureInfo.InvariantCulture)
                        + " "
                        + Q(prog.StringAt((long)i.U64));
            }
            return n;
        }
    }

    /// <summary>A compiled library compared with CtCmp32's _LIBRARY.RDB: per function the resolved
    /// instructions and the Library fields, per section the bytes.</summary>
    public sealed class LibraryReport
    {
        public int Functions;
        public int Exact;

        /// <summary>Equal except Line operands (source drift).</summary>
        public int ExactButLines;
        public int HeaderDiffs;
        public readonly List<(int index, string name, string what)> Mismatches =
            new List<(int, string, string)>();
        public readonly List<(string section, bool same, string detail)> Sections =
            new List<(string, bool, string)>();
        public readonly List<string> Orders = new List<string>();

        /// <summary>Functions with equal code whose Library fields differ.</summary>
        public readonly List<(int index, string name, string what)> HeaderOnly =
            new List<(int, string, string)>();
        public bool FileSame;
        public string FileDetail = "";

        /// <summary>_FUNCSYM.DBF next to the RDB written again (its date kept): null = no such file.</summary>
        public bool? FuncsymSame;
        public string FuncsymDetail = "";
        public int CountMismatch;

        public string Summary()
        {
            var sb = new StringBuilder();
            sb.Append(Functions).Append(" functions: ").Append(Exact).Append(" exact");
            if (ExactButLines > 0)
                sb.Append(", ").Append(ExactButLines).Append(" equal except Line numbers");
            sb.Append(", ").Append(Functions - Exact - ExactButLines).Append(" different");
            if (HeaderDiffs > 0)
                sb.Append("; ").Append(HeaderDiffs).Append(" Library field differences");
            return sb.ToString();
        }
    }

    public static class LibraryCheck
    {
        /// <summary>Compare <paramref name="mine"/> (a compiled image, its written bytes) with the
        /// RDB file <paramref name="rdbPath"/>.</summary>
        public static LibraryReport Compare(LibraryImage mine, string rdbPath, int keep = 20)
        {
            var rep = new LibraryReport();
            byte[] theirsBytes = System.IO.File.ReadAllBytes(rdbPath);
            var theirsFile = RdbFile.FromBytes(theirsBytes, rdbPath);
            var theirs = Rdb.LibraryFromRdb(theirsFile);
            var ours = mine.ToProgram("source");
            var tData = NormalCode.ByDataAddress(theirs);
            var oData = NormalCode.ByDataAddress(ours);
            int n = Math.Max(theirs.Functions.Count, ours.Functions.Count);
            rep.Functions = theirs.Functions.Count;
            if (theirs.Functions.Count != ours.Functions.Count)
                rep.CountMismatch = ours.Functions.Count - theirs.Functions.Count;
            var theirsByName = new Dictionary<string, Function>(StringComparer.Ordinal);
            foreach (var f in theirs.Functions)
                theirsByName[f.Name] = f;
            for (int k = 0; k < ours.Functions.Count; k++)
            {
                var o = ours.Functions[k];
                Function? t =
                    k < theirs.Functions.Count && theirs.Functions[k].Name == o.Name
                        ? theirs.Functions[k]
                        : (theirsByName.TryGetValue(o.Name, out var tf) ? tf : null);
                if (t == null)
                {
                    if (rep.Mismatches.Count < keep)
                        rep.Mismatches.Add((k, o.Name, "not in the RDB"));
                    continue;
                }
                var a = NormalCode.Of(t.Code, theirs, tData);
                var b = NormalCode.Of(o.Code, ours, oData);
                string? hdr = HeaderDiff(t, o);
                if (hdr != null)
                    rep.HeaderDiffs++;
                if (Same(a, b))
                {
                    rep.Exact++;
                    if (hdr != null && rep.HeaderOnly.Count < keep)
                        rep.HeaderOnly.Add((k, o.Name, hdr));
                    continue;
                }
                if (SameButLines(a, b))
                {
                    rep.ExactButLines++;
                    continue;
                }
                if (rep.Mismatches.Count < keep)
                    rep.Mismatches.Add(
                        (k, o.Name, FirstDiff(a, b) + (hdr != null ? "; header: " + hdr : ""))
                    );
            }
            // sections, byte for byte (the written file with the RDB's own time stamps)
            byte[] written = RdbWriter.Write(mine, RdbWriter.Stamp.Of(theirsBytes));
            var ourFile = RdbFile.FromBytes(written, "written");
            foreach (string name in theirsFile.Order)
            {
                var ts = theirsFile.Sections[name];
                if (!ourFile.Sections.TryGetValue(name, out var os))
                {
                    rep.Sections.Add((name, false, "missing"));
                    continue;
                }
                byte[] tb = Slice(theirsBytes, ts.Start, (int)(ts.Size + ts.X));
                byte[] ob = Slice(written, os.Start, (int)(os.Size + os.X));
                int d = FirstByteDiff(tb, ob);
                rep.Sections.Add(
                    (
                        name,
                        d < 0,
                        d < 0
                            ? (tb.Length + " bytes")
                            : $"{tb.Length} vs {ob.Length} bytes, first difference at +0x{d:x}"
                                + SectionHint(name, ts, os, d, theirsFile, ourFile)
                    )
                );
            }
            // the pool orders, item by item
            rep.Orders.Add(OrderDiff("TEXT", Strings(theirs.Text), mine.TextPool.Order));
            var tfl = new List<string>();
            foreach (var d in theirsFile.Records("FUNCLOOKUP"))
                tfl.Add(d["FunctionName"] as string ?? "");
            rep.Orders.Add(OrderDiff("FUNCLOOKUP", tfl, mine.FuncLookups.ConvertAll(f => f.SymbolName)));
            rep.Orders.Add(
                OrderDiff(
                    "CODEVAR",
                    theirs.CodeVarOrder.ConvertAll(c => c.Name + "@" + c.UnitType + ":" + c.UnitAddress),
                    mine.CodeVars.ConvertAll(c => c.Name + "@" + c.UnitType + ":" + c.UnitAddress)
                )
            );
            var ttg = new List<string>();
            foreach (var d in theirsFile.Records("TAGREF"))
                ttg.Add((d["TagName"] as string ?? "") + "." + (d["TagItem"] as string ?? ""));
            rep.Orders.Add(OrderDiff("TAGREF", ttg, mine.TagRefs.ConvertAll(t => t.Tag + "." + t.Item)));
            var tsc = new List<string>();
            foreach (var d in theirsFile.Records("SCALE"))
                tsc.Add(
                    FloatText.Shortest((double)d["RawZero"]!)
                        + ","
                        + FloatText.Shortest((double)d["RawFull"]!)
                        + ","
                        + FloatText.Shortest((double)d["EngZero"]!)
                        + ","
                        + FloatText.Shortest((double)d["EngFull"]!)
                );
            rep.Orders.Add(
                OrderDiff(
                    "SCALE",
                    tsc,
                    mine.Scales.ConvertAll(s =>
                        FloatText.Shortest(s.RawZero)
                        + ","
                        + FloatText.Shortest(s.RawFull)
                        + ","
                        + FloatText.Shortest(s.EngZero)
                        + ","
                        + FloatText.Shortest(s.EngFull)
                    )
                )
            );
            int fd = FirstByteDiff(theirsBytes, written);
            rep.FileSame = fd < 0;
            rep.FileDetail =
                fd < 0
                    ? theirsBytes.Length + " bytes, identical"
                    : $"{theirsBytes.Length} vs {written.Length} bytes, first difference at 0x{fd:x}";
            string fsym = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(rdbPath) ?? "",
                "_FUNCSYM.DBF"
            );
            if (System.IO.File.Exists(fsym))
            {
                byte[] td = System.IO.File.ReadAllBytes(fsym);
                byte[] md = FuncsymWriter.Write(mine, FuncsymWriter.DateOf(td));
                int dd = FirstByteDiff(td, md);
                rep.FuncsymSame = dd < 0;
                rep.FuncsymDetail =
                    dd < 0
                        ? td.Length + " bytes, identical"
                        : $"{td.Length} vs {md.Length} bytes, first difference at 0x{dd:x}";
            }
            return rep;
        }

        private static List<string> Strings(byte[] pool)
        {
            var o = new List<string>();
            int i = 0;
            while (i < pool.Length)
            {
                int e = i;
                while (e < pool.Length && pool[e] != 0)
                    e++;
                o.Add(Cp1252.Decode(pool, i, e - i));
                i = e + 1;
            }
            return o;
        }

        private static string OrderDiff(string what, List<string> a, List<string> b)
        {
            int n = Math.Min(a.Count, b.Count);
            for (int i = 0; i < n; i++)
            {
                if (a[i] == b[i])
                    continue;
                var sb = new StringBuilder();
                sb.Append(what)
                    .Append(": ")
                    .Append(a.Count)
                    .Append(" vs ")
                    .Append(b.Count)
                    .Append(" items, first difference at ")
                    .Append(i);
                sb.Append("\n        RDB : ");
                for (int k = Math.Max(0, i - 2); k < Math.Min(a.Count, i + 5); k++)
                    sb.Append(k == i ? ">" : "").Append(a[k]).Append(" | ");
                sb.Append("\n        ours: ");
                for (int k = Math.Max(0, i - 2); k < Math.Min(b.Count, i + 5); k++)
                    sb.Append(k == i ? ">" : "").Append(b[k]).Append(" | ");
                return sb.ToString();
            }
            return what
                + ": "
                + (
                    a.Count == b.Count
                        ? "same order, " + a.Count + " items"
                        : a.Count + " vs " + b.Count + " items"
                );
        }

        private static string SectionHint(string name, Section ts, Section os, int d, RdbFile tf, RdbFile of)
        {
            int dataRel = d - (ts.Data - ts.Start);
            if (dataRel < 0)
                return " (head)";
            if (!ts.IsPool && ts.RecSize > 0)
            {
                long rec = dataRel / ts.RecSize;
                if (rec < ts.NRec)
                    return $" (record {rec}, field offset {dataRel % ts.RecSize})";
                return " (relocation table)";
            }
            if (dataRel >= ts.NRec)
                return " (relocation table)";
            return $" (pool offset {dataRel})";
        }

        private static byte[] Slice(byte[] b, int start, int len)
        {
            len = Math.Max(0, Math.Min(len, b.Length - start));
            var r = new byte[len];
            Array.Copy(b, start, r, 0, len);
            return r;
        }

        private static int FirstByteDiff(byte[] a, byte[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
                if (a[i] != b[i])
                    return i;
            return a.Length == b.Length ? -1 : n;
        }

        private static bool Same(List<string> a, List<string> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        private static string NoLine(string s)
        {
            int k = s.IndexOf(' ');
            string body = k >= 0 ? s.Substring(k + 1) : s;
            return body.StartsWith("Line ", StringComparison.Ordinal) ? "Line" : s;
        }

        private static bool SameButLines(List<string> a, List<string> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
                if (NoLine(a[i]) != NoLine(b[i]))
                    return false;
            return true;
        }

        /// <summary>The instruction without its pc and branch target (to find where the code
        /// itself first differs when a size difference moves every later target).</summary>
        private static string Shape(string s)
        {
            int k = s.IndexOf(' ');
            string body = k >= 0 ? s.Substring(k + 1) : s;
            if (
                body.StartsWith("Branch ", StringComparison.Ordinal)
                || body.StartsWith("Brfalse ", StringComparison.Ordinal)
                || body.StartsWith("BrTrue ", StringComparison.Ordinal)
                || body.StartsWith("Entry ", StringComparison.Ordinal)
            )
                return body.Substring(0, body.IndexOf(' '));
            return body;
        }

        public static string FirstDiff(List<string> a, List<string> b)
        {
            int n = Math.Min(a.Count, b.Count);
            for (int i = 0; i < n; i++)
                if (Shape(a[i]) != Shape(b[i]))
                    return $"instruction {i}: RDB [{a[i]}] ours [{b[i]}]" + Context(a, b, i);
            for (int i = 0; i < n; i++)
                if (a[i] != b[i])
                    return $"instruction {i}: RDB [{a[i]}] ours [{b[i]}]" + Context(a, b, i);
            return $"{a.Count} vs {b.Count} instructions" + (n > 0 ? Context(a, b, n - 1) : "");
        }

        private static string Context(List<string> a, List<string> b, int i)
        {
            var sb = new StringBuilder();
            sb.Append("\n        RDB : ");
            for (int k = Math.Max(0, i - 3); k < Math.Min(a.Count, i + 4); k++)
                sb.Append(k == i ? ">" : "").Append(a[k]).Append(" | ");
            sb.Append("\n        ours: ");
            for (int k = Math.Max(0, i - 3); k < Math.Min(b.Count, i + 4); k++)
                sb.Append(k == i ? ">" : "").Append(b[k]).Append(" | ");
            return sb.ToString();
        }

        private static string? HeaderDiff(Function t, Function o)
        {
            var d = new List<string>();
            if (t.Args != o.Args)
                d.Add($"ARGS {t.Args}/{o.Args}");
            if (t.TypeWord != o.TypeWord)
                d.Add($"RESULT {t.TypeWord}/{o.TypeWord}");
            if (t.MinParam != o.MinParam)
                d.Add($"MINPARAM {t.MinParam}/{o.MinParam}");
            if (t.Filename != o.Filename)
                d.Add($"FILENAME {t.Filename}/{o.Filename}");
            string tl = string.Join(",", t.Locals.ConvertAll(x => x.Slot + ":" + x.Name));
            string ol = string.Join(",", o.Locals.ConvertAll(x => x.Slot + ":" + x.Name));
            if (tl != ol)
                d.Add($"DEBUG {tl}/{ol}");
            return d.Count > 0 ? string.Join("; ", d) : null;
        }
    }
}
