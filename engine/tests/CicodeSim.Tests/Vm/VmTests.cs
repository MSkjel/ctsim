using System;
using System.Collections.Generic;
using System.Linq;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;
using static CicodeSim.Tests.Vm.VmTestUtil;

namespace CicodeSim.Tests.Vm
{
    /// <summary>The Cicode VM, one opcode or rule at a time.</summary>
    public class VmTests
    {
        // ------------------------------------------------------------------ Const
        [Fact]
        public void ConstSubtypes()
        {
            Assert.Equal(-7, ValueOf(b => b.ConstInt(-7)).I);
            var r = ValueOf(b => b.ConstReal(2.5));
            Assert.Equal((Types.Real, 2.5), (r.T, r.D));
            var s = ValueOf(b => b.ConstStr("abc"));
            Assert.Equal((Types.String, "abc", Value.QualityGood, 0UL), (s.T, s.S, s.Q, s.Ts));
            Assert.Equal(Types.Object, ValueOf(b => b.ConstObj(-1)).T);
            Assert.Equal(123, ValueOf(b => b.ConstU64(Types.Timestamp, 123)).I);
            // sub-types 1 (i16) and 8 (i8) push type 4 (case 1 sub 1 / 8)
            r = ValueOf(b => b.ConstSub(0x01, new byte[] { 0xFF, 0xFF }));
            Assert.Equal((Types.Long, -1L), (r.T, r.I));
            r = ValueOf(b => b.ConstSub(0x08, new byte[] { 0x80 }));
            Assert.Equal((Types.Long, -128L), (r.T, r.I));
        }

        // ------------------------------------------------------------------ Convert
        [Fact]
        public void ConvertOpcodeTargets()
        {
            Assert.Equal(3, ValueOf(b => b.ConstReal(2.5).Convert(Types.Long)).I);
            Assert.Equal("5", ValueOf(b => b.ConstInt(5).Convert(Types.String)).S);
            Assert.Equal(1.5, ValueOf(b => b.ConstStr("1,5").Convert(Types.Real)).D);
            var r = ValueOf(b => b.ConstInt(1).Func("ErrSet", 1).ConstInt(5).Convert(Types.Object));
            Assert.True(r.T == Types.Object && r.I == -1 && r.Q == 0); // 433, quality cleared
            // TIMESTAMP target only checks the type: value kept, 433 raised (halt flag)
            var t = Block(b => b.ConstInt(5).Convert(Types.Timestamp));
            Assert.True(t.State == TaskState.Halt && t.HaltCode == 433);
        }

        [Fact]
        public void ConvertErrorHaltsOnlyAtErrSetLevel0()
        {
            var t = Block(b => b.ConstStr("abc").Convert(Types.Long));
            Assert.True(t.State == TaskState.Halt && t.HaltCode == 433);
            t = Block(b => b.ConstInt(1).Func("ErrSet", 1).ConstStr("abc").Convert(Types.Long));
            Assert.True(t.State == TaskState.Stop && t.Result!.Value.I == 0 && t.Error == 433);
        }

        [Fact]
        public void HaltOnErrorOff()
        {
            var cfg = new CodeConfig { HaltOnError = false };
            var t = Block(b => b.ConstReal(1e12).Convert(Types.Long), config: cfg);
            Assert.True(t.State == TaskState.Stop && t.Result!.Value.I == 0 && t.Error == 434);
        }

        // ------------------------------------------------------------------ arithmetic
        [Fact]
        public void AddIntWrapsAndMergesTime()
        {
            var r = ValueOf(b => b.ConstInt(1000000001).ConstInt(2000000002).Op("Add"));
            Assert.Equal(-1294967293, r.I); // doc 1130171 example
            Assert.True(r.Ts == Now && r.Vts == Now); // a zero timestamp counts as now
        }

        [Fact]
        public void DivByZero273()
        {
            var t = Block(b => b.ConstInt(1).Func("ErrSet", 1).ConstInt(7).ConstInt(0).Op("Div"));
            Assert.True(t.Result!.Value.T == Types.Long && t.Result.Value.I == 0);
            Assert.True(t.Error == 273 && t.ExprError == 273);
            t = Block(b => b.ConstInt(7).ConstInt(0).Op("Div"));
            Assert.True(t.State == TaskState.Halt && t.HaltCode == 273);
        }

        [Fact]
        public void ModAndNegAndNot()
        {
            Assert.Equal(-1, ValueOf(b => b.ConstInt(-7).ConstInt(2).Op("Mod")).I);
            var r = ValueOf(b => b.ConstReal(7.5).ConstInt(2).Op("Mod"));
            Assert.Equal((Types.Real, 1.5), (r.T, r.D));
            Assert.Equal(-5, ValueOf(b => b.ConstInt(5).Op("Neg")).I);
            Assert.Equal(1, ValueOf(b => b.ConstReal(0.4).Op("Not")).I); // 0.4 rounds to 0
        }

        [Fact]
        public void AndOrDoNotShortCircuit()
        {
            // both operands are computed (here: both calls run)
            var pb = new ProgramBuilder();
            pb.Pools.CodeVar("n", Types.Long, init: 0);
            var f = pb.Function("Bump", "", Types.Long);
            f.LoadVar("n").ConstInt(1).Op("Add").StoreVar("n").ConstInt(0).RetValue().End();
            var b = pb.Block();
            b.Call("Bump", 0).Call("Bump", 0).Op("And").End();
            byte[] code = b.Code();
            var prog = pb.Build();
            var m = MakeVm(prog);
            var t = m.TaskForBlock(code, prog);
            m.Run(t);
            Assert.Equal(0, t.Result!.Value.I);
            Assert.Equal(2L, m.Variable("n")!.Values[0]);
        }

        [Fact]
        public void CompareIgnoreCaseTaskFlag()
        {
            Assert.Equal(1, ValueOf(b => b.ConstStr("abc").ConstStr("ABC").Op("Eq")).I);
            var r = ValueOf(b =>
                b.ConstInt(3)
                    .ConstInt(0)
                    .Func("CodeSetMode", 2)
                    .Op("Pop")
                    .ConstStr("abc")
                    .ConstStr("ABC")
                    .Op("Eq")
            );
            Assert.Equal(0, r.I);
        }

        [Fact]
        public void BrfalseTestsTheDouble()
        {
            // IF 0.4 is TRUE (Brfalse tests != 0.0) while 0.4 AND 1 is FALSE
            Assert.Equal(
                1,
                ValueOf(b =>
                    b.ConstReal(0.4)
                        .BrFalse("no")
                        .ConstInt(1)
                        .Branch("end")
                        .Label("no")
                        .ConstInt(0)
                        .Label("end")
                ).I
            );
            Assert.Equal(0, ValueOf(b => b.ConstReal(0.4).ConstInt(1).Op("And")).I);
        }

        [Fact]
        public void BrfalseOnStringIs433()
        {
            var t = Block(b => b.ConstStr("x").BrFalse("l").Label("l"));
            Assert.True(t.State == TaskState.Halt && t.HaltCode == 433);
        }

        // ------------------------------------------------------------------ functions
        [Fact]
        public void EntryDefaultArgumentsAndRetValue()
        {
            // Entry + 7: the caller's argc selects the entry point; missing trailing
            // arguments get their defaults computed by the callee
            var pb = new ProgramBuilder();
            var f = pb.Function("Sum", "44", Types.Long, minParam: 1);
            f.Entry("e0", "e1", "e2");
            f.Label("e0");
            f.Label("e1");
            f.ConstInt(100).Convert(Types.Long); // the default of b
            f.Label("e2");
            f.LoadStack(0).LoadStack(1).Op("Add").Convert(Types.Long).RetValue().End();
            var prog = pb.Build();
            var m = MakeVm(prog);
            Assert.Equal(3, m.Call("Sum", 1, 2)!.Value.I);
            Assert.Equal(105, m.Call("Sum", 5)!.Value.I);
            var t = m.TaskForCall("Sum", new Value[0]);
            m.Run(t);
            Assert.True(t.State == TaskState.Halt && t.HaltCode == 529); // too few arguments
            t = m.TaskForCall("Sum", new[] { Value.Long(1), Value.Long(2), Value.Long(3) });
            m.Run(t);
            Assert.True(t.State == TaskState.Halt && t.HaltCode == 518); // too many
        }

        [Fact]
        public void CallByLookupChecks()
        {
            var pb = new ProgramBuilder();
            var f = pb.Function("One", "", Types.Long);
            f.ConstInt(1).RetValue().End();
            var b = pb.Block();
            b.Call("Missing", 0).End();
            byte[] code = b.Code();
            var b2 = pb.Block();
            b2.ConstInt(5).Call("One", 1).End();
            byte[] code2 = b2.Code();
            var prog = pb.Build();
            var m = MakeVm(prog);
            var t = m.TaskForBlock(code, prog);
            m.Run(t);
            Assert.True(t.State == TaskState.Halt && t.HaltCode == 292 && t.Error == 292);
            t = m.TaskForBlock(code2, prog);
            m.Run(t);
            Assert.True(t.State == TaskState.Halt && t.HaltCode == 518);
        }

        [Fact]
        public void VoidFunctionRetAndStackVarDefaults()
        {
            // StackVar gives REAL 0.0, INT 0, STRING ''; Ret returns nothing
            var pb = new ProgramBuilder();
            pb.Pools.CodeVar("out", Types.String, init: "");
            var f = pb.Function("Locals", "", Types.Void);
            f.StackVar(Types.Real, Types.Long, Types.String);
            f.LoadStack(0)
                .Convert(Types.String)
                .ConstStr("|")
                .Op("Add")
                .LoadStack(1)
                .Convert(Types.String)
                .Op("Add")
                .LoadStack(2)
                .Op("Add")
                .StoreVar("out")
                .Ret()
                .End();
            var prog = pb.Build();
            var m = MakeVm(prog);
            Assert.Null(m.Call("Locals"));
            Assert.Equal("0|0", m.Variable("out")!.Values[0]);
        }

        [Fact]
        public void StackOverflow295()
        {
            var pb = new ProgramBuilder();
            var f = pb.Function("Deep", "", Types.Long);
            f.Call("Deep", 0).RetValue().End();
            var prog = pb.Build();
            var m = MakeVm(prog, config: new CodeConfig { Stack = 16 });
            var t = m.TaskForCall("Deep", new Value[0]);
            m.Run(t);
            Assert.True(t.State == TaskState.Halt && t.HaltCode == 295);
            Assert.Equal(15, t.Frames.Count); // depth < [Code]Stack - 1
        }

        [Fact]
        public void ValueStackOverflow295()
        {
            var t = Block(
                b =>
                {
                    for (int k = 0; k < 20; k++)
                        b.ConstInt(1);
                },
                config: new CodeConfig { Stack = 8 }
            );
            Assert.True(t.State == TaskState.Halt && t.HaltCode == 295 && t.Sp == 7);
        }

        // ------------------------------------------------------------------ statements
        [Fact]
        public void ForLoopShapeReevaluatesLimit()
        {
            // FOR i = a TO b: i = a; Ltop: i; b; Le; Brfalse Lend; body; i = i + 1; Branch Ltop.
            // The TO expression is evaluated every iteration (here it shrinks).
            var pb = new ProgramBuilder();
            pb.Pools.CodeVar("lim", Types.Long, init: 10);
            var f = pb.Function("Loop", "", Types.Long);
            f.StackVar(Types.Long, Types.Long); // 0 i, 1 sum
            f.ConstInt(1).StoreStack(0);
            f.Label("top");
            f.LoadStack(0).LoadVar("lim").Op("Le").BrFalse("end");
            f.LoadStack(1).LoadStack(0).Op("Add").StoreStack(1);
            f.LoadVar("lim").ConstInt(1).Op("Sub").StoreVar("lim");
            f.LoadStack(0).ConstInt(1).Op("Add").StoreStack(0);
            f.Branch("top");
            f.Label("end");
            f.LoadStack(1).RetValue().End();
            var prog = pb.Build();
            var m = MakeVm(prog);
            Assert.Equal(1 + 2 + 3 + 4 + 5, m.Call("Loop")!.Value.I); // i <= lim with lim 10, 9, 8 ...
            var pb2 = new ProgramBuilder();
            f = pb2.Function("Zero", "", Types.Long);
            f.StackVar(Types.Long, Types.Long);
            f.ConstInt(10).StoreStack(0);
            f.Label("top");
            f.LoadStack(0).ConstInt(1).Op("Le").BrFalse("end");
            f.ConstInt(1).StoreStack(1);
            f.Branch("top");
            f.Label("end");
            f.LoadStack(1).RetValue().End();
            Assert.Equal(0, MakeVm(pb2.Build()).Call("Zero")!.Value.I); // FOR i = 10 TO 1 runs 0 times
        }

        /// <summary>SELECT CASE s / CASE 1 / CASE 2, 3 / CASE 5 TO 9 / CASE IS &lt; 0 / CASE
        /// ELSE, with the exact shapes of .</summary>
        private static void Select(ProgramBuilder pb, string name)
        {
            var f = pb.Function(name, "4", Types.Long);
            f.Entry("e0", "e1");
            f.Label("e0");
            f.ConstInt(0);
            f.Label("e1");
            f.StackVar(Types.Long); // slot 1 = result
            f.LoadStack(0); // the selector, pushed once
            f.ConstInt(1).Op("DupEx").Op("Eq").BrFalse("c2");
            f.ConstInt(10).StoreStack(1).Branch("done");
            f.Label("c2");
            f.ConstInt(2).Op("DupEx").Op("Eq").BrTrue("b2");
            f.ConstInt(3).Op("DupEx").Op("Eq").BrFalse("c3");
            f.Label("b2");
            f.ConstInt(20).StoreStack(1).Branch("done");
            f.Label("c3");
            f.ConstInt(5).Op("DupEx").Op("Le").BrFalse("c4");
            f.ConstInt(9).Op("DupEx").Op("Ge").BrTrue("b3");
            f.Branch("c4");
            f.Label("b3");
            f.ConstInt(30).StoreStack(1).Branch("done");
            f.Label("c4");
            f.ConstInt(0).Op("DupEx").Op("Gt").BrFalse("else");
            f.ConstInt(40).StoreStack(1).Branch("done");
            f.Label("else");
            f.ConstInt(50).StoreStack(1);
            f.Label("done");
            f.Op("Pop").LoadStack(1).RetValue().End();
        }

        [Fact]
        public void SelectCaseShapes()
        {
            var pb = new ProgramBuilder();
            Select(pb, "Sel");
            var m = MakeVm(pb.Build());
            var want = new Dictionary<int, long>
            {
                [1] = 10,
                [2] = 20,
                [3] = 20,
                [4] = 50,
                [5] = 30,
                [9] = 30,
                [10] = 50,
                [-3] = 40,
            };
            foreach (var kv in want)
                Assert.Equal(kv.Value, m.Call("Sel", kv.Key)!.Value.I);
        }

        // ------------------------------------------------------------------ variables
        [Fact]
        public void CodeVarRunImageAndStoreWithQuality()
        {
            var pb = new ProgramBuilder();
            pb.Pools.CodeVar("s", Types.String, init: "Function@ActionObject");
            pb.Pools.CodeVar("r", Types.LongReal, init: 2.5);
            pb.Pools.CodeVar("arr", Types.Long, count: 6, init: new object[] { 0, 1, 2, 3, 4, 5 });
            var f = pb.Function("Get", "", Types.String);
            f.LoadVar("s").LoadVar("r").Convert(Types.String).Op("Add").RetValue().End();
            var m = MakeVm(pb.Build());
            Assert.Equal("Function@ActionObject2.5", m.Call("Get")!.Value.S);
        }

        [Fact]
        public void IndexTwoDimsRowMajorAndBounds()
        {
            // Index: n INT indices, row-major acc = dim * acc + idx, 0 <= i < dim else 259
            var pb = new ProgramBuilder();
            pb.Pools.CodeVar("arr", Types.Long, count: 6, init: new object[] { 0, 1, 2, 3, 4, 5 });
            var f = pb.Function("At", "44", Types.Long).Begin();
            f.LoadStack(0).LoadStack(1).Index(2, 3).LoadVar("arr").RetValue().End();
            var g = pb.Function("Put", "444", Types.Void).Begin();
            g.LoadStack(2).LoadStack(0).LoadStack(1).Index(2, 3).StoreVar("arr").Ret().End();
            var m = MakeVm(pb.Build());
            Assert.Equal(5, m.Call("At", 1, 2)!.Value.I);
            Assert.Equal(1, m.Call("At", 0, 1)!.Value.I);
            m.Call("Put", 1, 0, 77);
            Assert.Equal(new object?[] { 0L, 1L, 2L, 77L, 4L, 5L }, m.Variable("arr")!.Values);
            var t = m.TaskForCall("At", new[] { Value.Long(2), Value.Long(0) });
            m.Run(t);
            Assert.True(t.State == TaskState.Halt && t.HaltCode == 259);
        }

        [Fact]
        public void AddrStackByReferenceStrWord()
        {
            // AddrStack (0x3b) passes a local by reference: StrWord takes the first word and leaves
            // the rest in the variable
            var pb = new ProgramBuilder();
            var f = pb.Function("Words", "7", Types.String).Begin();
            f.StackVar(Types.String);
            f.AddrStack(0)
                .Func("StrWord", 1)
                .StoreStack(1)
                .LoadStack(1)
                .ConstStr("|")
                .Op("Add")
                .LoadStack(0)
                .Op("Add")
                .RetValue()
                .End();
            var m = MakeVm(pb.Build());
            Assert.Equal("one| two three", m.Call("Words", "  one two three")!.Value.S);
        }

        [Fact]
        public void AddrCodeVarByReference()
        {
            var pb = new ProgramBuilder();
            pb.Pools.CodeVar("g", Types.String, init: "alpha beta");
            var f = pb.Function("W", "", Types.String);
            f.AddrVar("g", 7).Func("StrWord", 1).RetValue().End();
            var m = MakeVm(pb.Build());
            Assert.Equal("alpha", m.Call("W")!.Value.S);
            Assert.Equal(" beta", m.Variable("g")!.Values[0]);
        }

        // ------------------------------------------------------------------ tags
        [Fact]
        public void LoadStoreAndWriteLocalFlag()
        {
            var tags = new FakeTags(("Level", Value.Real(40.0, 0x40, 5, 6, 7)));
            var t = Block(b => b.Load("Level").ConstInt(10).Op("Add").Store("Out").Load("Out"), tags);
            Assert.True(t.Result!.Value.D == 50.0 && t.Result.Value.Q == 0x40); // bad-ish quality propagates
            Assert.Equal(("Out", Types.Real, (object?)50.0, true), tags.Writes[0]);
        }

        [Fact]
        public void LoadErrorSetsOpErrorAndSkipsStore()
        {
            var tags = new FakeTags();
            var t = Block(b => b.Load("Nope").Store("Out"), tags, fg: false);
            Assert.Equal(TaskState.Stop, t.State);
            Assert.Empty(tags.Writes); // a pending op error skips the write
            Assert.True(t.Error == 424 && t.ExprError == 424);
        }

        [Fact]
        public void HaltOnInvalidTagData()
        {
            var tags = new FakeTags();
            var cfg = new CodeConfig { HaltOnInvalidTagData = true };
            var t = Block(b => b.Load("Nope"), tags, cfg, fg: false);
            Assert.True(t.State == TaskState.Halt && t.HaltCode == 450);
            t = Block(b => b.Load("Nope"), tags, cfg, fg: true);
            Assert.Equal(TaskState.Stop, t.State); // never in the foreground
        }

        [Fact]
        public void BarScaleAndRangeError()
        {
            // Bar: percent of the SCALE range, clamped to 0..100; out of range makes a following
            // Store raise 257 under ScaleCheck
            var pb = new ProgramBuilder();
            long sc = pb.Pools.Scale(0.0, 500.0);
            var b = pb.Block();
            b.ConstReal(250.0).Bar(sc).End();
            byte[] code = b.Code();
            var b2 = pb.Block();
            b2.ConstReal(600.0).Bar(sc).Store("Out").End();
            byte[] code2 = b2.Code();
            var prog = pb.Build();
            var tags = new FakeTags();
            var m = MakeVm(prog, tags);
            var t = m.TaskForBlock(code, prog);
            m.Run(t);
            Assert.Equal(50.0, t.Result!.Value.D);
            t = m.TaskForBlock(code2, prog, false);
            m.Run(t);
            Assert.True(tags.Writes.Count == 0 && t.Error == 257 && t.State == TaskState.Stop);
        }

        // ------------------------------------------------------------------ formats
        [Fact]
        public void StringFmtAndAssFmtString()
        {
            Assert.Equal("42", ValueOf(b => b.ConstInt(42).StringFmt(0x1, "")).S);
            Assert.Equal("  3.14", ValueOf(b => b.ConstReal(3.14159).StringFmt(0x0206, "")).S);
            Assert.Equal("3.0 m%", ValueOf(b => b.ConstReal(3.0).StringFmt(0x040103, "m%")).S);
            // AssFmt with no association: '%6.2f' and expression error 323, then String formats
            var t = Block(b => b.ConstReal(7.0).AssFmt("?5?").Op("String"));
            Assert.True(t.Result!.Value.S == "  7.00" && t.ExprError == 323);
        }

        // ------------------------------------------------------------------ built-in calls
        [Fact]
        public void BuiltinInt16ReturnAndQualityMerge()
        {
            var tags = new FakeTags(("T", Value.Str("abc", 0x40, 5, 0, 0)));
            var r = ValueOf(b => b.Load("T").Func("StrLength", 1), tags);
            Assert.True(r.T == Types.Long && r.I == 3 && r.Q == 0x40 && r.Ts == Now);
            // StrSearch returns 0xffff for 'not found': INT is 16 bits, sign-extended
            r = ValueOf(b => b.ConstInt(0).ConstStr("abc").ConstStr("z").Func("StrSearch", 3));
            Assert.Equal(-1, r.I);
        }

        [Fact]
        public void NotSimulatedBuiltinHaltsWithItsName()
        {
            var t = Block(b => b.ConstStr("Mixer").ConstStr("").Func("_PageGoto", 2));
            Assert.True(t.State == TaskState.Halt && t.HaltReason.Contains("_PageGoto"));
        }

        // ------------------------------------------------------------------ slices / tasks
        [Fact]
        public void BackgroundPreemptAndForeground285()
        {
            // the clock is checked every ~200 instructions; a background task is
            // re-queued (Ready), a foreground task gets 285 and halts
            var pb = new ProgramBuilder();
            var f = pb.Function("Spin", "", Types.Void);
            f.Label("top").ConstInt(1).BrFalse("end").Branch("top").Label("end").Ret().End();
            var prog = pb.Build();
            var clock = new ManualClock(Now, tickMs: 60.0);
            var m = MakeVm(prog, clock: clock);
            var t = m.TaskForCall("Spin", new Value[0]);
            Assert.Equal(TaskState.Ready, m.RunSlice(t));
            Assert.True(199 <= t.Instructions && t.Instructions < 1000);
            t = m.TaskForCall("Spin", new Value[0], foreground: true);
            m.RunSlice(t);
            Assert.True(t.State == TaskState.Halt && t.HaltCode == 285);
        }

        [Fact]
        public void SchedulerRoundRobinAndSleep()
        {
            var pb = new ProgramBuilder();
            pb.Pools.CodeVar("log", Types.String, init: "");
            foreach (string name in new[] { "A", "B" })
            {
                var f = pb.Function(name, "", Types.Void);
                f.LoadVar("log").ConstStr(name).Op("Add").StoreVar("log");
                f.ConstInt(1).Func("Sleep", 1).Op("Pop");
                f.LoadVar("log").ConstStr(name.ToLowerInvariant()).Op("Add").StoreVar("log");
                f.Ret().End();
            }
            var prog = pb.Build();
            var clock = new ManualClock(Now);
            var m = MakeVm(prog, clock: clock);
            var sch = new Scheduler(m);
            var a = sch.Spawn("A");
            var bt = sch.Spawn("B");
            sch.Run();
            Assert.Equal("AB", m.Variable("log")!.Values[0]);
            Assert.True(a.State == TaskState.Sleep && bt.State == TaskState.Sleep);
            clock.Advance(1000);
            sch.Run();
            Assert.Equal("ABab", m.Variable("log")!.Values[0]);
            Assert.True(a.Done && bt.Done);
        }

        [Fact]
        public void SleepInForeground282()
        {
            var t = Block(b => b.ConstInt(1).Func("Sleep", 1));
            Assert.True(t.State == TaskState.Halt && t.HaltCode == 282);
        }

        [Fact]
        public void LineHookAndPause()
        {
            var pb = new ProgramBuilder();
            var f = pb.Function("L", "", Types.Long);
            f.Line(10).ConstInt(1).Line(11).RetValue().End();
            var prog = pb.Build();
            var m = MakeVm(prog);
            var seen = new List<int>();
            m.LineHook = (task, line) =>
            {
                seen.Add(line);
                if (line == 11 && !task.Data.ContainsKey("paused_once"))
                {
                    task.Data["paused_once"] = true;
                    task.Pause = true;
                }
            };
            var sch = new Scheduler(m);
            var t = sch.Spawn("L");
            sch.Run();
            Assert.True(t.State == TaskState.Wait && t.Line == 11);
            t.Pause = false;
            sch.Run();
            Assert.True(t.Done && t.Result!.Value.I == 1 && seen[0] == 10 && seen[1] == 11);
        }

        [Fact]
        public void ErrorHookHandlesError()
        {
            var pb = new ProgramBuilder();
            var b = pb.Block();
            b.ConstInt(1).ConstInt(0).Op("Div").End();
            byte[] code = b.Code();
            var prog = pb.Build();
            var m = MakeVm(prog);
            var seen = new List<int>();
            m.ErrorHook = (task, c) =>
            {
                seen.Add(c);
                return true;
            };
            var t = m.TaskForBlock(code, prog);
            m.Run(t);
            Assert.True(t.State == TaskState.Stop && seen.Count == 1 && seen[0] == 273);
        }

        [Fact]
        public void UnsupportedAndStopOpcodesHalt()
        {
            var t = Block(b => b.Emit(0x16)); // Fmul: STOP in the interpreter
            Assert.Equal(TaskState.Halt, t.State);
            t = Block(b => b.Emit(0x00)); // Stop
            Assert.Equal(TaskState.Halt, t.State);
        }

        // ------------------------------------------------------------------ every binary op
        public static IEnumerable<object[]> BinaryCases()
        {
            string[] names =
            {
                "Add",
                "Sub",
                "Mul",
                "Div",
                "Mod",
                "Lt",
                "Le",
                "Gt",
                "Ge",
                "Eq",
                "Ne",
                "And",
                "Or",
                "BitAnd",
                "BitOr",
                "BitXor",
            };
            object[][] pairs =
            {
                new object[] { 7, 3 },
                new object[] { -7, 2 },
                new object[] { 7.5, 2.0 },
                new object[] { 3, 2.5 },
                new object[] { "12", 3 },
                new object[] { "ab", "AB" },
            };
            foreach (var p in pairs)
            foreach (var n in names)
                yield return new[] { n, p[0], p[1] };
        }

        private static void Push(CodeBuilder b, object x)
        {
            switch (x)
            {
                case string s:
                    b.ConstStr(s);
                    break;
                case double d:
                    b.ConstReal(d);
                    break;
                default:
                    b.ConstInt(Convert.ToInt64(x));
                    break;
            }
        }

        [Theory]
        [MemberData(nameof(BinaryCases))]
        public void EveryBinaryOpcodeMatchesOps(string name, object a, object b)
        {
            // the VM's inline fast paths and its slow paths give what Ops.Binary gives
            var t = Block(
                bld =>
                {
                    bld.ConstInt(1).Func("ErrSet", 1);
                    Push(bld, a);
                    Push(bld, b);
                    bld.Op(name);
                },
                fg: false
            );
            Assert.True(t.State == TaskState.Stop, t.HaltReason);
            var c = new OpCtx(Now);
            var want = Ops.Binary(Op.ByName(name), CicodeVm.ValueOf(a), CicodeVm.ValueOf(b), c);
            Assert.True(
                want.Equals(t.Result!.Value.WithQ(want.Q, want.Ts, want.Vts, want.Qts)),
                $"{want} vs {t.Result}"
            );
        }

        [Fact]
        public void FuncCallByNameAndStringPtr()
        {
            var pb = new ProgramBuilder();
            var f = pb.Function("Two", "", Types.Long);
            f.ConstInt(2).RetValue().End();
            var b = pb.Block();
            b.CallByName("two", 0).End();
            byte[] code = b.Code();
            var prog = pb.Build();
            var m = MakeVm(prog);
            var t = m.TaskForBlock(code, prog);
            m.Run(t);
            Assert.Equal(2, t.Result!.Value.I);
            Assert.Equal(" 2.5", ValueOf(bb => bb.ConstReal(2.5).ConstStr("%4.1f").Op("StringPtr")).S);
        }

        [Fact]
        public void StoreScaleCheckOffSkipsWriteSilently()
        {
            var pb = new ProgramBuilder();
            long sc = pb.Pools.Scale(0.0, 100.0);
            var b = pb.Block();
            b.ConstInt(1)
                .ConstInt(0)
                .Func("CodeSetMode", 2)
                .Op("Pop")
                .ConstReal(150.0)
                .Bar(sc)
                .Store("Out")
                .End();
            byte[] code = b.Code();
            var prog = pb.Build();
            var tags = new FakeTags();
            var m = MakeVm(prog, tags);
            var t = m.TaskForBlock(code, prog, false);
            m.Run(t);
            Assert.True(t.State == TaskState.Stop && tags.Writes.Count == 0);
        }

        private sealed class ArrayTags : FakeTags, ITagElementAccess
        {
            public Value ReadElement(TagRef r, long index, CicodeTask task) => Value.Long(100 + index);

            public int WriteElement(TagRef r, long index, Value value, CicodeTask task) => 0;
        }

        [Fact]
        public void IndexTagElementLoadHasNoBoundsCheck()
        {
            var t = Block(b => b.ConstInt(9).Index(2).Load("Arr"), new ArrayTags());
            Assert.True(t.State == TaskState.Stop && t.Result!.Value.I == 109);
            t = Block(b => b.ConstInt(9).ConstInt(0).Index(2, 2).Load("Arr"), new ArrayTags());
            Assert.True(t.State == TaskState.Halt && t.HaltCode == 259);
        }

        [Fact]
        public void AddrCodeVarRealSub2Is256()
        {
            var pb = new ProgramBuilder();
            pb.Pools.CodeVar("r", Types.LongReal, init: 1.0);
            var b = pb.Block();
            b.AddrVar("r", 2).End();
            byte[] code = b.Code();
            var prog = pb.Build();
            var m = MakeVm(prog);
            var t = m.TaskForBlock(code, prog);
            m.Run(t);
            Assert.True(t.State == TaskState.Halt && t.HaltCode == 256);
        }

        [Fact]
        public void TaskForCallConvertsPlainArguments()
        {
            var pb = new ProgramBuilder();
            var f = pb.Function("Echo", "2", Types.Real).Begin();
            f.LoadStack(0).RetValue().End();
            var m = MakeVm(pb.Build());
            var r = m.Call("Echo", 3)!.Value;
            Assert.Equal((Types.Real, 3.0), (r.T, r.D));
        }

        [Fact]
        public void RealClockRuns()
        {
            var pb = new ProgramBuilder();
            var f = pb.Function("One", "", Types.Long);
            f.ConstInt(1).RetValue().End();
            var m = new CicodeVm(pb.Build(), Registry.Default());
            Assert.Equal(1, m.Call("One")!.Value.I);
        }

        // ------------------------------------------------------------------ edge cases
        private sealed class ElementTags : FakeTags, ITagElementAccess
        {
            public readonly List<string> Calls = new List<string>();

            public ElementTags(params (string, object)[] v)
                : base(v) { }

            public Value ReadElement(TagRef r, long index, CicodeTask task)
            {
                Calls.Add(FormattableString.Invariant($"read {r.Tag} {index}"));
                return Value.Long(100 + index);
            }

            public int WriteElement(TagRef r, long index, Value value, CicodeTask task)
            {
                Calls.Add(FormattableString.Invariant($"write {r.Tag} {index} {value.I}"));
                return 0;
            }
        }

        [Fact]
        public void IndexedTagAccessPassesTheIndexThrough()
        {
            // a 1-dimension tag array index is passed unchecked (here -1) and a
            // bounds error passes -1; neither falls back to the plain tag
            var tags = new ElementTags(("Arr", 0));
            var t = Block(b => b.ConstInt(-1).Index(10).Load("Arr"), tags);
            Assert.True(t.Result!.Value.I == 99 && tags.Calls.SequenceEqual(new[] { "read Arr -1" }));
            tags = new ElementTags(("Arr", 0));
            Block(b => b.ConstInt(5).ConstInt(-1).Index(10).Store("Arr").ConstInt(0), tags);
            Assert.True(tags.Calls.SequenceEqual(new[] { "write Arr -1 5" }) && tags.Writes.Count == 0);
            tags = new ElementTags(("Arr", 0));
            t = Block(
                b => b.ConstInt(1).Func("ErrSet", 1).ConstInt(1).ConstInt(9).Index(2, 3).Load("Arr"),
                tags
            );
            Assert.True(t.Error == 259 && tags.Calls.SequenceEqual(new[] { "read Arr -1" }));
            // an indexed Store whose bounds check failed writes nothing
            tags = new ElementTags(("Arr", 0));
            t = Block(
                b =>
                    b.ConstInt(1)
                        .Func("ErrSet", 1)
                        .ConstInt(5)
                        .ConstInt(1)
                        .ConstInt(9)
                        .Index(2, 3)
                        .Store("Arr")
                        .ConstInt(0),
                tags
            );
            Assert.True(t.Error == 259 && tags.Calls.Count == 0 && tags.Writes.Count == 0);
            // plain Load / Store still go to Read / Write
            tags = new ElementTags(("Arr", 7));
            t = Block(b => b.Load("Arr").Store("Out").Load("Arr"), tags);
            Assert.True(t.Result!.Value.I == 7 && tags.Calls.Count == 0);
            Assert.Equal(
                ("Out", Types.Long, (object?)7L),
                (tags.Writes[0].Tag, tags.Writes[0].T, tags.Writes[0].V)
            );
        }

        private static CicodeTask BlockingBlock(
            BuiltinImpl impl,
            string name = "StrLength",
            int argc = 1,
            Action<CodeBuilder>? push = null
        )
        {
            var pb = new ProgramBuilder();
            var b = pb.Block();
            b.ConstInt(5);
            if (push != null)
                push(b);
            else
                b.ConstStr("abc");
            b.Func(name, argc).End();
            byte[] code = b.Code();
            var prog = pb.Build();
            var m = new CicodeVm(
                prog,
                Registry.Default(new Dictionary<string, BuiltinImpl> { [name] = impl }),
                null,
                new ManualClock(Now)
            );
            var t = m.TaskForBlock(code, prog, false);
            var s = new Scheduler(m);
            s.Add(t);
            s.Run();
            return t;
        }

        [Fact]
        public void BlockedBuiltinResultIsConvertedLikeADirectReturn()
        {
            // a BLOCKed built-in pushes, when its task wakes, exactly what a direct return pushes:
            // its declared return type with the merged quality; no result = the default; a VOID
            // built-in nothing; a NULL string 530
            var t = BlockingBlock((task, a) => Value.Block); // StrLength: INT
            Assert.True(
                t.State == TaskState.Stop
                    && t.StackValues().Select(x => x.I).SequenceEqual(new long[] { 5, 0 })
            );
            t = BlockingBlock(
                (task, a) =>
                {
                    task.Wait = new Wait(TaskState.Wait) { Result = () => Value.Long(0x18003) };
                    return Value.Block;
                }
            );
            Assert.True(
                t.State == TaskState.Stop && t.Result!.Value.T == Types.Long && t.Result.Value.I == -32765
            ); // 16 bits
            t = BlockingBlock(
                (task, a) =>
                {
                    task.Wait = new Wait(TaskState.Wait) { Result = () => Value.Real(2.5) };
                    return Value.Block;
                },
                "Abs",
                push: b => b.ConstReal(-2.5)
            );
            Assert.True(
                t.State == TaskState.Stop && t.Result!.Value.T == Types.Real && t.Result.Value.D == 2.5
            );
            t = BlockingBlock(
                (task, a) =>
                {
                    task.Wait = new Wait(TaskState.Wait) { Result = () => Value.Long(99) };
                    return Value.Block;
                },
                "ErrSet",
                1,
                b => b.ConstInt(0)
            );
            Assert.True(
                t.State == TaskState.Stop && t.StackValues().Select(x => x.I).SequenceEqual(new long[] { 5 })
            );
            t = BlockingBlock(
                (task, a) =>
                {
                    task.Wait = new Wait(TaskState.Wait) { Result = () => null };
                    return Value.Block;
                },
                "StrUpper"
            );
            Assert.True(t.State == TaskState.Halt && t.HaltCode == 530);
        }

        [Fact]
        public void SchedulerDropsEndedTasks()
        {
            // ended tasks leave Scheduler.Tasks (the caller keeps the task); a waiting one stays
            var pb = new ProgramBuilder();
            pb.Function("Quick", "", Types.Long).ConstInt(1).RetValue().End();
            pb.Function("Nap", "", Types.Void).ConstInt(5).Func("Sleep", 1).Op("Pop").Ret().End();
            var m = MakeVm(pb.Build());
            var s = new Scheduler(m);
            var quick = Enumerable.Range(0, 120).Select(_ => s.Spawn("Quick")).ToList();
            var nap = s.Spawn("Nap");
            s.Run();
            Assert.All(quick, t => Assert.True(t.State == TaskState.Stop && t.Result!.Value.I == 1));
            Assert.True(s.Tasks.Values.SequenceEqual(new[] { nap }) && nap.State == TaskState.Sleep);
            // the thread limit counts live tasks only
            m.Config.Threads = 5;
            for (int k = 0; k < 4; k++)
                s.Spawn("Quick");
            Assert.Throws<HardwareError>(() => s.Spawn("Quick"));
            s.Run();
            Assert.True(s.Tasks.Values.SequenceEqual(new[] { nap }));
        }

        [Fact]
        public void BlockCacheIsKeyedByContentAndBounded()
        {
            var pb = new ProgramBuilder();
            var b = pb.Block();
            b.ConstInt(2).ConstInt(3).Op("Add").End();
            byte[] code = b.Code();
            var prog = pb.Build();
            var m = MakeVm(prog);
            for (int k = 0; k < 50; k++)
            {
                var t = m.TaskForBlock((byte[])code.Clone(), prog);
                m.Run(t);
                Assert.Equal(5, t.Result!.Value.I);
            }
            Assert.Equal(1, m.BlockCount);
            m.BlockCache = 16;
            for (int k = 0; k < 40; k++)
            {
                var bb = pb.Block();
                bb.ConstInt(k).End();
                var t = m.TaskForBlock(bb.Code(), prog);
                m.Run(t);
                Assert.Equal(k, t.Result!.Value.I);
            }
            Assert.True(m.BlockCount <= 16);
        }

        [Fact]
        public void BadCodeHaltsTheTaskInsteadOfRaising()
        {
            var t = Block(b => b.Emit(0x0B, new byte[] { 0x03, 0x00 }).ConstInt(7)); // Branch to 0x04: inside Const
            Assert.True(t.State == TaskState.Halt && t.HaltReason.Contains("starts no instruction"));
            t = Block(b => b.Emit(0x0B, new byte[] { 0x00, 0x80 })); // Branch far before the code
            Assert.True(t.State == TaskState.Halt && t.HaltReason.Contains("starts no instruction"));
            var pb = new ProgramBuilder();
            pb.Function("Broken", "", Types.Long).Emit(0x80).End(); // not an opcode
            var blk = pb.Block();
            blk.Call("Broken", 0).End();
            byte[] code = blk.Code();
            var prog = pb.Build();
            var m = MakeVm(prog);
            t = m.TaskForBlock(code, prog);
            m.Run(t);
            Assert.True(t.State == TaskState.Halt && t.HaltReason.Contains("invalid code"));
        }

        [Fact]
        public void ConstSub0x11HasA4ByteOperand()
        {
            var code = new byte[] { 0x01, 0x11, 1, 2, 3, 4, 0xFF };
            Assert.Null(Opcodes.SweepOk(code));
            var ins = Opcodes.Instructions(code);
            Assert.True(ins.Select(i => i.Op).SequenceEqual(new[] { Op.Const, Op.End }));
            Assert.Equal((0x11, 0x04030201L), (ins[0].Sub, ins[0].IntValue));
        }

        [Fact]
        public void TagReferenceConvertAndConstAreNotSimulated()
        {
            var tags = new FakeTags(("T", 5));
            var t = Block(b => b.Load("T").Convert(Types.Tag), tags);
            Assert.True(t.State == TaskState.Halt && t.HaltCode == 0 && t.Error == 0);
            Assert.True(t.HaltReason.Contains("not simulated") && t.HaltReason.Contains("Convert TAG"));
            t = Block(b => b.ConstSub(Types.Tag, new byte[4]));
            Assert.True(t.State == TaskState.Halt && t.HaltReason.Contains("not simulated"));
        }

        private sealed class ScaledTags : FakeTags, ITagScale
        {
            public ScaledTags(params (string, object)[] v)
                : base(v) { }

            public bool TryScale(TagRef? r, CicodeTask task, out double engZero, out double engFull)
            {
                engZero = 0.0;
                engFull = 500.0;
                return true;
            }
        }

        [Fact]
        public void BarWithTheTagRangeSetsTheOpError()
        {
            // Bar with no SCALE (operand -1) uses the last tag's range
            // out of range writes the op error 257, so a following Store raises it
            // under ScaleCheck exactly like the SCALE form; in range clears it
            var pb = new ProgramBuilder();
            var b = pb.Block();
            b.Load("Level").Bar(0xFFFFFFFF).Store("Out").Load("Level").Bar(0xFFFFFFFF).End();
            byte[] code = b.Code();
            var prog = pb.Build();
            var tags = new ScaledTags(("Level", 600.0));
            var m = MakeVm(prog, tags);
            var t = m.TaskForBlock(code, prog, false);
            m.Run(t);
            Assert.True(tags.Writes.Count == 0 && t.Error == 257 && t.Result!.Value.D == 100.0);
            tags = new ScaledTags(("Level", 250.0));
            m = MakeVm(prog, tags);
            t = m.TaskForBlock(code, prog, false);
            m.Run(t);
            Assert.True(
                tags.Writes.Select(w => w.V).SequenceEqual(new object?[] { 50.0 })
                    && t.Error == 0
                    && t.OpError == 0
            );
        }
    }
}
