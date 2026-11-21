using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CicodeSim.Builtins;
using CicodeSim.Tests.Vm;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>Format templates, error / trace built-ins, paths, local text, events, TableMath,
    /// and real compiled functions that run end to end.</summary>
    [Collection("World")]
    public sealed class FormatMiscTests : BuiltinTestBase
    {
        private static (string, string, object[]) C(string fn, string b, params object[] args) =>
            (fn, b, args);

        [Fact]
        public void FormatTemplateParsing()
        {
            // no width = the length of the '{...}' text; L R N Z
            var f = FmtTemplate.Parse("F", "{Name}{Desc,20}");
            Assert.Equal(6, f.Fields[0].Width);
            Assert.Equal(20, f.Fields[1].Width);
            Assert.Equal(6, f.Fields[1].Offset);
            f.Fields[0].Data = "CV101";
            f.Fields[1].Data = "Raw Coal Conveyor";
            Assert.Equal("CV101 Raw Coal Conveyor   ", f.ToStr());
            f = FmtTemplate.Parse("G", "<{A,4,R}|{B,3,Z}|{C,5,N}>");
            f.Fields[0].Data = "ab";
            f.Fields[1].Data = "7";
            f.Fields[2].Data = "x";
            Assert.Equal("<  ab|007|x>", f.ToStr());
            Assert.True(FmtTemplate.Valid("{a}{b,3}{c,3,r}"));
            Assert.False(FmtTemplate.Valid("{,3}"));
            Assert.False(FmtTemplate.Valid("{a,x}"));
            Assert.False(FmtTemplate.Valid("{a,3,q}"));
            Assert.False(FmtTemplate.Valid("{a,3,l,1}"));
            // StrToFmt: by offsets; a 'none' field stops at the template character after it
            f = FmtTemplate.Parse("H", "{A,5,N} {B,4}");
            f.FromStr("ab cdefgh");
            Assert.Equal("ab", f.Fields[0].Data);
            Assert.Equal("cdef", f.Fields[1].Data);
        }

        [Fact]
        public void FormatBuiltins()
        {
            var s = Make(pb =>
            {
                Ret(pb, "open", "FmtOpen", "MyFmt", "{Name}{Desc,20}", 1);
                Ret(pb, "open0", "FmtOpen", "MyFmt", "", 0);
                Ret(pb, "openBad", "FmtOpen", "Bad", "{x,y}", 1);
                pb.Function("set", "477", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .LoadStack(2)
                    .Func("FmtSetField", 3)
                    .RetValue()
                    .End();
                pb.Function("get", "47", Types.String)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("FmtGetField", 2)
                    .RetValue()
                    .End();
                pb.Function("str", "4", Types.String)
                    .Begin()
                    .LoadStack(0)
                    .Func("FmtToStr", 1)
                    .RetValue()
                    .End();
                pb.Function("parse", "47", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("StrToFmt", 2)
                    .RetValue()
                    .End();
                pb.Function("count", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("FmtGetFieldCount", 1)
                    .RetValue()
                    .End();
                pb.Function("name", "44", Types.String)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("FmtGetFieldName", 2)
                    .RetValue()
                    .End();
                pb.Function("width", "44", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("FmtGetFieldWidth", 2)
                    .RetValue()
                    .End();
                pb.Function("hnd", "47", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("FmtFieldHnd", 2)
                    .RetValue()
                    .End();
                pb.Function("close", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("FmtClose", 1)
                    .RetValue()
                    .End();
                pb.Function("setH", "447", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .LoadStack(2)
                    .Func("FmtSetFieldHnd", 3)
                    .RetValue()
                    .End();
                pb.Function("getH", "44", Types.String)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("FmtGetFieldHnd", 2)
                    .RetValue()
                    .End();
            });
            long h = s.Result("open").I;
            Assert.Equal(0, h);
            Assert.Equal(h, s.Result("open0").I);
            var r = s.Call("openBad");
            Assert.Equal(Codes.InvalidArg, r.Task.Error);
            Assert.Equal(Codes.InvalidArg, r.Task.Result!.Value.I); // the runtime returns 0x112 as the handle
            Assert.Equal(0, s.Result("set", h, "Name", "CV101").I);
            Assert.Equal(0, s.Result("set", h, "desc", "Raw Coal Conveyor").I);
            Assert.Equal(Codes.FieldNotFound, s.Result("set", h, "Nope", "x").I);
            Assert.Equal("CV101 Raw Coal Conveyor   ", S(s.Result("str", h)));
            Assert.Equal(0, s.Result("parse", h, "AB123 Some text").I);
            Assert.Equal("Some text", S(s.Result("get", h, "Desc")));
            Assert.Equal(2, s.Result("count", h).I);
            Assert.Equal("Desc", S(s.Result("name", h, 1)));
            Assert.Equal(20, s.Result("width", h, 1).I);
            Assert.Equal(1, s.Result("hnd", h, "DESC").I);
            Assert.Equal(0, s.Result("setH", h, 0, "LONGNAME").I); // cut to the field's width 6
            Assert.Equal("LONGNA", S(s.Result("getH", h, 0)));
            Assert.Equal(Codes.FieldNotFound, s.Result("setH", h, 5, "x").I);
            Assert.Equal(Codes.FieldNotFound, s.Call("getH", h, 2).Task.Error);
            Assert.Equal(0, s.Result("close", h).I);
            Assert.Equal(Codes.BadHandle, s.Result("close", h).I);
            Assert.Equal(Codes.BadHandle, s.Call("str", h).Task.Error);
        }

        [Fact]
        public void ErrorTraceAndStringHelpers()
        {
            var s = Make(pb =>
            {
                Ret(pb, "trace", "TraceMsg", "hello @(Motor Overload)");
                Ret(pb, "log", "ErrLog", "logged");
                Ret(pb, "sethw", "ErrSetHw", 4, 273, 0);
                Ret(pb, "gethw", "ErrGetHw", 4, 0);
                Ret(pb, "gethw2", "ErrGetHw", 5, 0);
                Ret(pb, "local", "StrToLocalText", "@(Motor Overload) at @(Pump^,1)");
                Ret(pb, "path", "PathToStr", "[RUN]:test.txt");
                Ret(pb, "plain", "PathToStr", "c:\\x.txt");
                Ret(pb, "width", "StrCalcWidth", "abcd", -1);
                Ret(pb, "info", "ErrInfo", 0);
                Ret(pb, "com", "ErrCom");
                Ret(pb, "trunc", "StrTruncFont", "abcdef", "Arial", 24, 0);
                Ret(pb, "truncAll", "StrTruncFontHnd", "abcdef", 1, -1, 0);
                Ret(pb, "trace0", "CodeTrace", -2, 3);
                Ret(pb, "traceBad", "CodeTrace", 999, 3);
            });
            Assert.Equal(0, s.Result("trace").I);
            Assert.Contains("TraceMsg: hello Motor Overload", s.Log);
            Assert.Equal(0, s.Result("log").I);
            Assert.Contains("ErrLog: logged", s.Log);
            Assert.Equal(0, s.Result("sethw").I);
            Assert.Equal(273, s.Result("gethw").I);
            Assert.Equal(0, s.Result("gethw2").I);
            Assert.Equal("Motor Overload at Pump,1", S(s.Result("local")));
            Assert.Equal(Path.Combine(s.W.Unit.RootDir, "test.txt"), S(s.Result("path")));
            Assert.Equal("c:\\x.txt", S(s.Result("plain")));
            Assert.Equal(32, s.Result("width").I);
            Assert.Equal("0", S(s.Result("info")));
            Assert.Equal(0, s.Result("com").I);
            Assert.Equal("abc", S(s.Result("trunc"))); // 8 pixels a character
            Assert.Equal("abcdef", S(s.Result("truncAll")));
            Assert.Equal(0, s.Result("trace0").I);
            Assert.Equal(3, s.Host.TraceDefault);
            Assert.Equal(Codes.BadHandle, s.Result("traceBad").I);
        }

        /// <summary>Every world built-in called with its mandatory and with all of its
        /// arguments, once with zero / empty values and once with 1 / "1": no .NET exception
        /// escapes, and the task ends or waits.</summary>
        [Fact]
        public void EveryWorldBuiltinSurvivesPlainArguments()
        {
            var table = CicodeSim.Compiled.Func0Table.Default;
            var calls = new List<(string Fn, string Name)>();
            var s = Make(pb =>
            {
                int k = 0;
                foreach (var (info, _) in WorldBuiltins.Declared())
                {
                    var b = table.Lookup(info.Name)!;
                    foreach (int n in new[] { b.MinArgs, b.NArgs })
                    foreach (int pass in new[] { 0, 1 })
                    {
                        var args = new List<object>();
                        for (int i = 0; i < n; i++)
                        {
                            int ty = Types.SigType(b.Args[i]);
                            if (ty == Types.String || ty == Types.Variant || ty == Types.Tag)
                                args.Add(pass == 0 ? "" : "1");
                            else if (Types.IsRealType(ty))
                                args.Add(pass == 0 ? 0.0 : 1.0);
                            else if (ty == Types.Quality || ty == Types.Timestamp)
                                args.Add(Value.Int(ty, pass));
                            else
                                args.Add(pass);
                        }
                        string fn = "smoke" + k++;
                        Ret(pb, fn, b.Name, args.ToArray());
                        calls.Add((fn, b.Name + "/" + n + "/" + pass));
                    }
                }
            });
            var failed = new List<string>();
            foreach (var (fn, name) in calls)
            {
                try
                {
                    var r = s.Run.Call(fn, null, maxSeconds: 5, maxSlices: 5000);
                    if (
                        !r.Task.Done
                        && !r.Waited
                        && r.Task.State != TaskState.Wait
                        && r.Task.State != TaskState.Sleep
                    )
                        failed.Add(name + ": " + r.Task.State);
                }
                catch (Exception e)
                {
                    failed.Add(name + ": " + e.GetType().Name + " " + e.Message);
                }
            }
            Assert.True(failed.Count == 0, string.Join("\n", failed));
            Assert.Equal(4 * WorldBuiltins.Declared().Count, calls.Count);
        }

        [Fact]
        public void QualityToStrFollowsTheHelpExamples()
        {
            Value Q(ulong q) => Value.Int(Types.Quality, unchecked((long)q));
            var s = Make(pb =>
            {
                // QualityCreate(QUAL_GOOD, 0, 0, 0, 1, 1, 0) part -1.
                Ret(pb, "full", "QualityToStr", Q(0xC0 | 0x30000), -1, 0);
                // GOOD + QUAL_GOOD_LOCAL_OVERRIDE (6) part 1.
                Ret(pb, "ovr", "QualityToStr", Q(0xC0 | 6 << 2), 1, 0);
                Ret(pb, "low", "QualityToStr", Q(0xC1), 2, 0);
                Ret(pb, "cfg", "QualityToStr", Q(1 << 2), -1, 0);
                Ret(pb, "cfgShort", "QualityToStr", Q(1 << 2 | 0x10000), -2, 1);
                Ret(pb, "uncr", "QualityToStr", Q(0x40 | 0x0800), 0, 0);
                Ret(pb, "ext", "QualityToStr", Q(0x40 | 0x0800), 3, 0);
                Ret(pb, "subn", "QualityToStr", Q(0x40 | 6 << 2), 1, 0);
                Ret(pb, "both", "QualityToStr", Q(0x40 | 4 << 2 | 0x10000), -1, 0);
                Ret(pb, "gen2", "QualityToStr", Q(0x80), 0, 0);
                Ret(pb, "badExt", "QualityToStr", Q(0xC0 | 0x1500), 3, 0);
                Ret(pb, "badPart", "QualityToStr", Q(0xC0), 6, 0);
                Ret(pb, "noFlag", "QualityToStr", Q(0xC0), 4, 0);
            });
            Assert.Equal("Good [Override] [Control Inhibit]", S(s.Result("full")));
            Assert.Equal("Overridden", S(s.Result("ovr")));
            Assert.Equal("Below Low", S(s.Result("low")));
            Assert.Equal("Bad - Configuration Error", S(s.Result("cfg")));
            Assert.Equal("Bad - Configuration Error", S(s.Result("cfgShort")));
            Assert.Equal("Uncertain", S(s.Result("uncr")));
            Assert.Equal("Tag Address Out Of Range", S(s.Result("ext")));
            Assert.Equal("Subnormal", S(s.Result("subn")));
            Assert.Equal("Uncertain [Override] - Sensor Not Accurate", S(s.Result("both")));
            Assert.Equal(":2:", S(s.Result("gen2")));
            Assert.Equal("", S(s.Result("badExt")));
            Assert.Equal("", S(s.Result("badPart")));
            Assert.Equal("", S(s.Result("noFlag")));
        }

        [Fact]
        public void EventHandlersAndTableMath()
        {
            var s = Make(pb =>
            {
                pb.Pools.CodeVar("Arr", Types.Real, 5, new List<double> { 10, 15, 50, 100, 200 });
                pb.Function("Handler", "", Types.Long)
                    .Load("Count_Long")
                    .ConstInt(1)
                    .Op("Add")
                    .Store("Count_Long")
                    .ConstInt(7)
                    .RetValue()
                    .End();
                Ret(pb, "on", "OnEvent", 5, "Handler");
                Ret(pb, "off", "OnEvent", 5, "0");
                Ret(pb, "onBad", "OnEvent", 300, "Handler");
                Ret(pb, "get", "GetEvent", 5);
                Ret(pb, "set6", "SetEvent", 6, 0);
                Ret(pb, "get6", "GetEvent", 6);
                Ret(pb, "setBad", "SetEvent", 6, 9999);
                Ret(pb, "setType", "SetEvent", 256, 0);
                pb.Function("chain", "", Types.Long)
                    .ConstInt(5)
                    .Func("GetEvent", 1)
                    .Func("ChainEvent", 1)
                    .RetValue()
                    .End();
                Ret(pb, "call", "CallEvent", 0, 5);
                pb.Function("math", "44", Types.Real)
                    .Begin()
                    .AddrVar("Arr", 6)
                    .ConstInt(5)
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("TableMath", 4)
                    .RetValue()
                    .End();
                pb.Function("math1", "", Types.Real)
                    .AddrVar("Arr", 6)
                    .ConstInt(1)
                    .ConstInt(0)
                    .ConstInt(0)
                    .Func("TableMath", 4)
                    .RetValue()
                    .End();
                double invalid = BitConverter.Int64BitsToDouble(unchecked((long)0xFFFFBBBBFFFFBBBBUL));
                double gated = BitConverter.Int64BitsToDouble(unchecked((long)0xFFFFAAAAFFFFAAAAUL));
                pb.Pools.CodeVar("Bad", Types.Real, 3, new List<double> { invalid, gated, invalid });
                pb.Function("mathBad", "4", Types.Real)
                    .Begin()
                    .AddrVar("Bad", 6)
                    .ConstInt(3)
                    .LoadStack(0)
                    .ConstInt(1)
                    .Func("TableMath", 4)
                    .RetValue()
                    .End();
            });
            s.W.Tags.Set("Count_Long", 0);
            Assert.Equal(-1, s.Result("get").I);
            Assert.Equal(0, s.Result("on").I);
            Assert.Equal(0, s.Result("get").I); // the handler's function handle
            Assert.Equal(7, s.Result("chain").I);
            Assert.Equal(7, s.Result("call").I);
            Assert.Equal(2, s.W.Tags.Get("Count_Long").I);
            Assert.Equal(Codes.InvalidArg, s.Result("onBad").I);
            Assert.Equal(0, s.Result("set6").I);
            Assert.Equal(0, s.Result("get6").I); // function handle 0
            Assert.Equal(Codes.InvalidArg, s.Result("setBad").I);
            Assert.Equal(Codes.InvalidArg, s.Result("setType").I);
            Assert.Equal(0, s.Result("off").I);
            Assert.Equal(-1, s.Result("get").I);
            Assert.Equal(0, s.Result("call").I);
            Assert.Equal(10.0, s.Result("math", 0, 0).D);
            Assert.Equal(200.0, s.Result("math", 1, 0).D);
            Assert.Equal(75.0, s.Result("math", 2, 0).D); // the help's example
            Assert.Equal(375.0, s.Result("math", 4, 0).D);
            Assert.Equal(
                Math.Sqrt((10 * 10 + 15 * 15 + 50 * 50 + 100 * 100 + 200 * 200 - 375.0 * 375 / 5) / 4),
                s.Result("math", 3, 0).D,
                9
            );
            var r = s.Call("math1");
            Assert.Equal(Codes.InvalidArg, r.Task.Error); // Size below 2
            // Mode 1 with nothing left: the invalid marker itself, every command
            for (int cmd = 0; cmd <= 4; cmd++)
                Assert.Equal(
                    0xFFFFBBBBFFFFBBBBUL,
                    (ulong)BitConverter.DoubleToInt64Bits(s.Result("mathBad", cmd).D)
                );
        }

        [Fact]
        public void BehaviourJsonIsCurrent()
        {
            // builtins/behaviour.json is generated from the registries and [Builtin] attributes
            // (ctsim-dev builtins-behaviour --out builtins/behaviour.json)
            string path = Path.Combine(RepoRoot(), "builtins", "behaviour.json");
            string have = File.ReadAllText(path).Replace("\r\n", "\n");
            Assert.Equal(WorldBuiltins.BehaviourJson(), have);
            // and it is JSON in the README's shape
            var root = (Dictionary<string, object?>)Json.Parse(have)!;
            Assert.Equal("ctsim-behaviour/0", root["format"]);
            var fns = (Dictionary<string, object?>)root["functions"]!;
            foreach (var kv in fns)
            {
                var e = (Dictionary<string, object?>)kv.Value!;
                Assert.Contains((string)e["status"]!, new[] { "implemented", "stub", "refused" });
                foreach (string list in new[] { "effects", "errors", "cases", "doc" })
                    if (e.TryGetValue(list, out object? l))
                    {
                        Assert.IsType<List<object?>>(l);
                        // an item is never cut inside its parentheses ('(returned; guess ...)')
                        foreach (object? item in (List<object?>)l!)
                        {
                            string x = (string)item!;
                            Assert.True(
                                x.Split('(').Length == x.Split(')').Length,
                                kv.Key + " " + list + ": " + x
                            );
                        }
                    }
                // README: a stub returns `returns` (a value: a number, or "" for a STRING stub; no description in words)
                if (e.TryGetValue("returns", out object? ret))
                    Assert.False(ret is string rs && rs.Length > 0, kv.Key + ": returns " + ret);
            }
            Assert.True(fns.ContainsKey("SemOpen") && fns.ContainsKey("QualityToStr"));
        }

        [Fact]
        public void AttributeListsSplitOutsideParentheses()
        {
            Assert.Equal(
                new[] { "274 an empty tag (returned)", "424 no such alarm (returned; guess on the code)" },
                WorldBuiltins.SplitItems(
                    "274 an empty tag (returned); 424 no such alarm (returned; guess on the code)"
                )
            );
            Assert.Equal(
                new[] { "a", "b (c (d; e); f)", "g;h" },
                WorldBuiltins.SplitItems(" a ; b (c (d; e); f); ; g;h")
            );
            Assert.Empty(WorldBuiltins.SplitItems("  "));
        }

        [Fact]
        public void EveryWorldBuiltinIsAFunc0Name()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (info, _) in WorldBuiltins.Declared())
            {
                Assert.NotNull(CicodeSim.Compiled.Func0Table.Default.Lookup(info.Name));
                Assert.True(names.Add(info.Name), "twice: " + info.Name);
                Assert.Contains(info.Status, new[] { "implemented", "stub", "refused" });
            }
            var reg = WorldBuiltins.Create((CicodeSim.World.World?)null);
            foreach (string n in names)
                Assert.True(reg.IsImplemented(n), n);
            // the default registry stays without them (the pure built-ins only)
            Assert.False(Registry.Default().IsImplemented("SemOpen"));
            Assert.False(Registry.Default().IsImplemented("TimeCurrent"));
        }

        private static string RepoRoot()
        {
            string d = AppContext.BaseDirectory;
            while (d != null && !File.Exists(Path.Combine(d, "LICENSE")))
                d = Path.GetDirectoryName(d)!;
            return d ?? throw new DirectoryNotFoundException("repo root");
        }

        // ------------------------------------------------------------------ compiled code
        [CorpusFact]
        public void ExampleSaCapFillRunsPastSemOpenToItsEnd()
        {
            // ExampleSA Bottling.ci ProdLine_SimulateCapFill: SleepMS loop, Include's
            // EnterCriticalSection (SemOpen + SemWait), TagWrite, LeaveCriticalSection (SemSignal)
            var run = WorldRun.ForProject(Corpus.ExSA, null, "2026-10-01T08:00:00Z");
            var r = run.Call("ProdLine_SimulateCapFill");
            Assert.Equal("stop", r.Outcome);
            Assert.InRange(r.SimSeconds, 10.9, 11.1); // 100 x SleepMS(100) + 2 x SleepMS(500)
            var last = r
                .Changes.Skip(r.Changes.Count - 4)
                .Select(c => c.Name + "=" + c.Value.Dump())
                .ToList();
            Assert.Equal(
                new[]
                {
                    "TopMilk_LS20_PV=100.0",
                    "TopMilk_CNV02_Position=0.0",
                    "TopMilk_CNV02_Running=0",
                    "TopMilk_CNV02_Stopped=1",
                },
                last
            );
            // with the default registry it stops at SemOpen (not simulated there)
            run = WorldRun.ForProject(Corpus.ExSA, null, "2026-10-01T08:00:00Z");
            run.Vm.Builtins = Registry.Default();
            r = run.Call("ProdLine_SimulateCapFill");
            Assert.Equal("not simulated", r.Outcome);
            Assert.Contains("SemOpen", r.Task.HaltReason);
        }

        [CorpusFact]
        public void IncludeQueryDevNameRunsOnSemaphores()
        {
            // Include citect.ci GetQueryDevName: EnterCriticalSection, SemOpen(name, 0) probes,
            // LeaveCriticalSection; MODULE counter
            var run = WorldRun.ForProject(Corpus.ExSA, null, "2026-10-01T08:00:00Z");
            var r = run.Call("GetQueryDevName");
            Assert.Equal("stop", r.Outcome);
            Assert.Equal("Query0", r.Task.Result!.Value.S);
            r = run.Call("GetQueryDevName");
            Assert.Equal("Query1", r.Task.Result!.Value.S);
        }
    }
}
