using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CicodeSim.Compiled;
using CicodeSim.Host;
using CicodeSim.Values;
using Xunit;
using CProgram = CicodeSim.Compiled.Program;
using Obj = CicodeSim.Tests.Golden.GoldenJson.Obj;

namespace CicodeSim.Tests.Golden
{
    /// <summary>The compiled code of the shipped libraries, frozen.</summary>
    public class LibraryGoldenTests
    {
        private const string SweepFile = "library_sweep.jsonl.gz";
        private const string PcodeFile = "pcode.jsonl.gz";

        // ------------------------------------------------------------------ the library sweep
        internal static string SweepLine(VmCommands.LibraryRun r)
        {
            var o = new Obj()
                .Add("lib", r.Lib)
                .Add("index", r.Fn.Index)
                .Add("fn", r.Fn.Name)
                .Add("set", r.Set);
            if (r.Task == null)
                o.Add("outcome", r.Outcome).Add("err", r.Err);
            else
                GoldenRecords.AddRun(o, r.Task, r.Outcome, VmCommands.NotSimulatedName(r.Task));
            return GoldenJson.Write(o);
        }

        private static string SweepKey(string lib, int index, int set) =>
            lib
            + "|"
            + index.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "|"
            + set.ToString(System.Globalization.CultureInfo.InvariantCulture);

        [GoldenFact]
        [Trait("Golden", "light")]
        public void LibrarySweepMatchesTheGolden()
        {
            var got = new List<(string, string)>();
            foreach (string lib in VmCommands.Libraries(GoldenFiles.User))
            foreach (var r in VmCommands.LibraryRuns(lib, every: true))
                got.Add((SweepKey(r.Lib, r.Fn.Index, r.Set), SweepLine(r)));
            if (GoldenFiles.WriteIfAsked(SweepFile, got.Select(x => x.Item2)))
                return;
            var want = GoldenFiles
                .ReadLines(SweepFile)
                .Select(l => (GoldenRecords.KeyOf(l, "lib", "index", "set"), l))
                .ToList();
            GoldenFiles.AssertSame("library sweep (lib|function index|argument set)", want, got);
        }

        // ------------------------------------------------------------------ the instructions
        /// <summary>A function as golden lines: its header (index, name, argument types, result
        /// type, minimum arguments, source file, locals), then one line per instruction: pc, size,
        /// the bytes, the opcode name and the operands resolved through the program's pools.</summary>
        public static List<string> FunctionLines(CProgram prog, Function fn, Func<int, string> builtinName)
        {
            var lines = new List<string>();
            var args = new List<object?>();
            foreach (char c in fn.Args)
                args.Add(Types.SigType(c));
            var locals = new List<object?>();
            foreach (var (slot, name) in fn.Locals)
                locals.Add(new object?[] { slot, name });
            lines.Add(
                GoldenJson.Write(
                    new object?[]
                    {
                        "fn",
                        fn.Index,
                        fn.Name,
                        args,
                        fn.Result,
                        fn.MinParam,
                        fn.Filename,
                        locals,
                    }
                )
            );
            List<Instr> instrs;
            try
            {
                instrs = Opcodes.Instructions(fn.Code);
            }
            catch (DecodeError e)
            {
                lines.Add(GoldenJson.Write(new object?[] { "error", e.Message }));
                return lines;
            }
            var hex = new StringBuilder();
            foreach (var ins in instrs)
            {
                hex.Clear();
                for (int k = 0; k < ins.Size; k++)
                    hex.Append(
                        fn.Code[ins.Pc + k].ToString("x2", System.Globalization.CultureInfo.InvariantCulture)
                    );
                lines.Add(
                    GoldenJson.Write(
                        new object?[]
                        {
                            ins.Pc,
                            ins.Size,
                            hex.ToString(),
                            Op.Name(ins.Op),
                            Operands(ins, prog, fn, builtinName),
                        }
                    )
                );
            }
            return lines;
        }

        private static string LocalName(Function fn, int slot)
        {
            foreach (var (off, nm) in fn.Locals)
                if (off == slot)
                    return nm;
            return "";
        }

        private static List<object?> Operands(
            in Instr ins,
            CProgram prog,
            Function fn,
            Func<int, string> builtinName
        )
        {
            var o = new List<object?>();
            switch (ins.Op)
            {
                case Op.Const:
                {
                    int sub = ins.Sub;
                    o.Add(sub);
                    if (sub == Types.String)
                        o.Add(prog.StringAt((long)ins.U64));
                    else if (sub == Types.Real)
                        o.Add(ins.RealValue);
                    else if (sub == 0x01 || sub == 0x04 || sub == 0x08 || sub == 0x0C || sub == 0x11)
                        o.Add(ins.IntValue);
                    else
                        o.Add(ins.U64);
                    break;
                }
                case Op.Load:
                case Op.Store:
                    if (prog.TagRefs.TryGetValue(ins.Offset, out var r) && (long)ins.U64 == ins.Offset)
                        o.AddRange(new object?[] { r.Cluster, r.Tag, r.Element, r.Item });
                    else
                        o.AddRange(new object?[] { "?", ins.U64 });
                    break;
                case Op.LoadCodeVar:
                case Op.StoreCodeVar:
                    if (prog.CodeVars.TryGetValue(ins.Offset, out var cv))
                        o.Add(cv.Name);
                    else
                        o.AddRange(new object?[] { "?", ins.U64 });
                    break;
                case Op.AddrCodeVar:
                    // the operand is a RUN DataAddress (row 0x3c)
                    o.Add(ins.Sub);
                    if (ins.U64 <= long.MaxValue && prog.CodeVarAtData(ins.Offset) is CodeVar cv2)
                        o.Add(cv2.Name);
                    else
                        o.AddRange(new object?[] { "?", ins.U64 });
                    break;
                case Op.Func:
                    o.AddRange(new object?[] { ins.A, ins.B, builtinName(ins.A) });
                    break;
                case Op.FuncCallByLookup:
                    if (prog.FuncLookups.TryGetValue(ins.Offset, out var fl))
                        o.AddRange(new object?[] { fl.Name, ins.A });
                    else
                        o.AddRange(new object?[] { "?", ins.U64, ins.A });
                    break;
                case Op.FuncCallByName:
                    o.AddRange(new object?[] { prog.StringAt((long)ins.U64), ins.A });
                    break;
                case Op.Branch:
                case Op.Brfalse:
                case Op.BrTrue:
                case Op.Convert:
                case Op.Line:
                    o.Add(ins.A);
                    break;
                case Op.Entry:
                    foreach (short d in ins.Shorts!)
                        o.Add(ins.Pc + d);
                    break;
                case Op.StackVar:
                    foreach (byte t in ins.Bytes!)
                        o.Add((int)t);
                    break;
                case Op.StoreStack:
                case Op.LoadStack:
                case Op.AddrStack:
                    o.AddRange(new object?[] { ins.A, LocalName(fn, ins.A) });
                    break;
                case Op.Bar:
                    if (ins.U32 == 0xFFFFFFFFu)
                        o.Add("tag");
                    else if (!prog.Scales.TryGetValue(ins.U32, out var sc))
                        o.AddRange(new object?[] { "?", ins.U32 });
                    else
                        o.AddRange(new object?[] { sc.EngZero, sc.EngFull, sc.RawZero, sc.RawFull });
                    break;
                case Op.AssFmt:
                    o.Add(ins.U32);
                    break;
                case Op.StringFmt:
                    o.Add(ins.U32);
                    if (ins.U64 < (ulong)prog.Text.Length)
                        o.Add(prog.StringAt((long)ins.U64));
                    else
                        o.AddRange(new object?[] { "@", ins.U64 });
                    break;
                case Op.Index:
                    foreach (short d in ins.Shorts!)
                        o.Add((int)d);
                    break;
            }
            return o;
        }

        [GoldenFact]
        [Trait("Golden", "light")]
        public void InstructionsMatchTheGolden()
        {
            var namer = PcodeListing.BuiltinNamer();
            var got = new List<(string, string)>();
            var progs = new Dictionary<string, CProgram>(StringComparer.Ordinal);
            foreach (string path in VmCommands.Libraries(GoldenFiles.User))
            {
                string lib = Path.GetFileName(Path.GetDirectoryName(path)!);
                var prog = Rdb.LoadLibrary(path);
                progs[lib] = prog;
                foreach (var fn in prog.Functions)
                {
                    var lines = FunctionLines(prog, fn, namer);
                    using var d = new GoldenJson.Digest();
                    foreach (string l in lines)
                        d.Add(l);
                    string key =
                        lib + "|" + fn.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    got.Add(
                        (
                            key,
                            GoldenJson.Write(
                                new Obj()
                                    .Add("lib", lib)
                                    .Add("index", fn.Index)
                                    .Add("fn", fn.Name)
                                    .Add("n", lines.Count - 1)
                                    .Add("h", d.Hex())
                            )
                        )
                    );
                }
            }
            if (GoldenFiles.WriteIfAsked(PcodeFile, got.Select(x => x.Item2)))
                return;
            var want = GoldenFiles
                .ReadLines(PcodeFile)
                .Select(l => (GoldenRecords.KeyOf(l, "lib", "index"), l))
                .ToList();
            GoldenFiles.AssertSame(
                "instructions (lib|function index: digest of the decoded function)",
                want,
                got,
                key =>
                {
                    string[] p = key.Split('|');
                    if (!progs.TryGetValue(p[0], out var prog))
                        return "";
                    var fn = prog.Functions.FirstOrDefault(f =>
                        f.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) == p[1]
                    );
                    if (fn == null)
                        return "";
                    var lines = FunctionLines(prog, fn, namer);
                    return "  the function now (first 60 of "
                        + lines.Count
                        + " lines):\n    "
                        + string.Join("\n    ", lines.Take(60));
                }
            );
        }
    }
}
