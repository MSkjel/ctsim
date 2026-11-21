using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Vm
{
    /// <summary>Compiled Cicode on disk: the RDB reader, the instruction table, the
    /// p-code lister and real compiled functions run in the VM (R2 corpus, read only).</summary>
    public class RdbTests
    {
        [CorpusFact]
        public void LibrarySectionsAndRecords()
        {
            var f = RdbFile.Open(Corpus.Lib);
            Assert.Equal(7, f.Version);
            foreach (
                var name in new[]
                {
                    "TEXT",
                    "SCALE",
                    "RUN",
                    "TAGREF",
                    "FUNCLOOKUP",
                    "CODEVAR",
                    "CODE",
                    "Library",
                    "DEBUG",
                }
            )
                Assert.True(f.Has(name), name);
            var library = Corpus.Library;
            Assert.Equal(f.Sections["Library"].NRec, library.Functions.Count);
            var fn = library.Function("StrToBool")!;
            Assert.True(fn.Args == "7" && fn.Result == Types.Long && fn.MinParam == 1);
            Assert.EndsWith("Include\\String.ci", fn.Filename);
            Assert.Equal(
                new HashSet<string> { "sVal", "sValUC", "bVal", "iErrLevel" },
                new HashSet<string>(fn.Locals.Select(l => l.Name))
            );
            Assert.True(fn.Code[fn.Code.Length - 1] == Op.End && Opcodes.SweepOk(fn.Code) == null);
            var priv = library.Functions.Where(x => x.Private).ToList();
            Assert.NotEmpty(priv);
            Assert.All(priv, x => Assert.Equal(2, x.Name.Count(c => c == '@')));
        }

        [CorpusFact]
        public void LibraryAgreesWithFuncSym()
        {
            // every _FUNCSYM.DBF row = its Library record: NAME (PRIVATE as name@file@project),
            // OP_CODE, ARGC, TYPE (return type | 0x400 private | 0x800 public), MANDATORY, RETURNED
            var library = Corpus.Library;
            var rows = Dbf.CitectRows(Path.Combine(Corpus.ExSA, "_FUNCSYM.DBF"));
            Assert.Equal(library.Functions.Count, rows.Count);
            foreach (var r in rows)
            {
                var f = library.Functions[int.Parse(r["OP_CODE"])];
                Assert.Equal(r["NAME"], f.Name);
                Assert.Equal(r["ARGC"], f.Args);
                long type = long.Parse(r["TYPE"]);
                Assert.Equal(type, f.TypeWord);
                Assert.Equal(type & 0x3FF, f.Result);
                Assert.Equal((type & 0x400) != 0, f.Private);
                Assert.Equal(r["MANDATORY"].Length > 0 ? int.Parse(r["MANDATORY"]) : 0, f.MinParam);
                Assert.Equal(r["RETURNED"] == "1", f.Result != Types.Void);
            }
            Assert.Empty(Rdb.FuncSymMismatches(library, rows));
        }

        [CorpusFact]
        public void EveryFunctionDecodes()
        {
            foreach (var fn in Corpus.Library.Functions)
                Assert.True(Opcodes.SweepOk(fn.Code) == null, fn.Name);
        }

        [CorpusFact]
        public void CodeVarsAndRunImage()
        {
            var library = Corpus.Library;
            var cv = library.CodeVarOrder.First(c => c.Name == "l_sActionFunction");
            Assert.True(cv.RawType == Types.String && cv.BitWidth == 2048);
            var m = new CicodeVm(library, Registry.Default(), null, new ManualClock());
            Assert.Equal("Function@ActionObject", m.Variable("l_sActionFunction")!.Values[0]);
        }

        [CorpusFact]
        public void PageBlocksDecode()
        {
            var pc = Rdb.LoadPage(Corpus.Page);
            Assert.Equal("WTP_INTAKE_HD1080", pc.Page);
            Assert.NotEmpty(pc.Blocks);
            Assert.All(pc.Blocks, b => Assert.Null(Opcodes.SweepOk(b.Code)));
            Assert.Contains(pc.Blocks, b => b.Kind == RdbFile.FExpr);
            Assert.All(pc.Blocks.Where(b => b.Table == "PGDYNOBJ"), b => Assert.True(b.An > 0));
            Assert.True(pc.Records.ContainsKey("PGDYNOBJ") && pc.Program.TagRefs.Count > 0);
        }

        [CorpusFact]
        public void ListsCompiledCode()
        {
            string output = PcodeListing.Run(Corpus.Lib, new[] { "StrToBool" });
            Assert.Contains("FUNCTION StrToBool(STRING) -> INT", output);
            Assert.Contains("Const            STRING \"TRUE\"", output);
            Assert.Contains("StrUpper(1)  #39", output);
            Assert.Contains("LoadStack        1 (sValUC)", output);
            output = PcodeListing.Run(Corpus.Page, new[] { "PGDYNOBJ" });
            Assert.Contains("Load             WTP_T201_LT", output);
        }

        [Fact]
        public void Func0TableEntries()
        {
            var tab = Func0Table.Default;
            Assert.Equal(1147, tab.Count);
            var b = tab.Lookup("StrMid")!;
            Assert.Equal((34, "711", Types.Int16 + 6), (b.Index, b.Args, b.Ret));
            Assert.True(tab.Lookup("IsError")!.ReturnsInt16);
            Assert.True(tab.Lookup("StrWord")!.ByRef(0));
            Assert.True(Func0Table.SignExtend16(0xFFFF) == -1 && Func0Table.SignExtend16(0x7FFF) == 32767);
            if (tab.Lookup("RealToStr")!.Func0Args.Length > 0) // FUNC0.DBF installed
                Assert.Equal(3, tab.Lookup("RealToStr")!.MinArgs);
        }

        [CorpusFact]
        public void CompiledFunctionsRun()
        {
            // real CtCmp32 output from the R2 libraries that only uses pure built-ins, with the
            // results the .ci sources compute
            var m = new CicodeVm(Corpus.Library, Registry.Default(), null, new ManualClock());
            Assert.Equal(1, m.Call("StrToBool", "TRUE")!.Value.I);
            Assert.Equal(0, m.Call("StrToBool", "false")!.Value.I);
            Assert.Equal(1, m.Call("StrToBool", "12")!.Value.I);
            Assert.Equal(0, m.Call("StrToBool", "abc")!.Value.I); // StrToInt's 274 seen by IsError
            Assert.Equal("Foo", m.Call("Callback_GetFuncName", "Foo(1,2)")!.Value.S);
            Assert.Equal("1,2", m.Call("Callback_GetParams", "Foo(1,2)")!.Value.S);
            Assert.Equal("c", m.Call("MenupathGetName", "a.b.c")!.Value.S);
            Assert.Equal(1, m.Call("StrEquals", "abc", "ABC")!.Value.I);
            Assert.Equal(-3, m.Call("Floor", -2.7)!.Value.I);
            Assert.Equal("Pump1", m.Call("_Faceplate_GetEquipmentShortName", "Plant.Area.Pump1")!.Value.S);
        }

        [CorpusFact]
        public void InterpreterRuns()
        {
            var m = new CicodeVm(Corpus.Library, Registry.Default(), null, new ManualClock());
            long n = 0;
            var sw = Stopwatch.StartNew();
            for (int k = 0; k < 300; k++)
            {
                var t = m.TaskForCall("Callback_GetParams", new[] { Value.Str("SomeFunction(1,2,3)") });
                m.Run(t);
                n += t.Instructions;
            }
            Assert.True(n > 0 && sw.Elapsed.TotalMilliseconds > 0);
        }

        [HeavyFact]
        public void AllR2LibrariesDecode()
        {
            // every function of every R2 library: no unknown opcodes, no desyncs
            long total = 0;
            foreach (var d in Directory.GetDirectories(Corpus.User))
            {
                string p = Path.Combine(d, "_LIBRARY.RDB");
                if (!File.Exists(p))
                    continue;
                var prog = Rdb.LoadLibrary(p);
                foreach (var fn in prog.Functions)
                    Assert.True(Opcodes.SweepOk(fn.Code) == null, p + " " + fn.Name);
                total += prog.Functions.Count;
            }
            Assert.True(total > 1000, "only " + total + " functions in the R2 libraries");
        }

        private sealed class PageTags : ITagAccess
        {
            private readonly Dictionary<string, object> _values;

            public PageTags(Dictionary<string, object> values)
            {
                _values = values;
            }

            public Value Read(TagRef r, CicodeTask task)
            {
                if (!_values.TryGetValue(r.Tag, out var v))
                {
                    task.TagError = Errors.TagNotFound;
                    return Value.Long(0, 0);
                }
                return CicodeVm.ValueOf(v);
            }

            public int Write(TagRef r, Value value, CicodeTask task) => 0;
        }

        [CorpusFact]
        public void PageExpressionsRun()
        {
            // WTP_INTAKE_HD1080's compiled expressions, run in the foreground against the project
            // library: a TXT_STR display (StringFmt 0x200206 = '%6.2f') and a VISIBLE test
            var pc = Rdb.LoadPage(Corpus.Page);
            var tags = new PageTags(
                new Dictionary<string, object> { ["WTP_T201_LT"] = 12.5, ["WTP_T201_LSL"] = 0 }
            );
            var m = new CicodeVm(Corpus.Library, Registry.Default(), tags, new ManualClock());
            var txt = pc.Blocks.First(b =>
                b.Table == "PGDYNOBJ" && b.Field == "TXT_STR" && b.Code[0] == 0x02 && b.Code[9] == 0x46
            );
            var t = m.Evaluate(txt.Code, pc.Program);
            Assert.True(t.State == TaskState.Stop && t.Result!.Value.S == " 12.50");
            var vis = pc.Blocks.First(b =>
                b.Table == "PGDYNOBJ"
                && b.Field == "VISIBLE"
                && b.Code[0] == 0x02
                && b.Code[9] == 0x01
                && b.Code[10] == 0x04
            );
            t = m.Evaluate(vis.Code, pc.Program);
            Assert.True(t.State == TaskState.Stop && t.Result!.Value.T == Types.Long);
        }
    }
}
