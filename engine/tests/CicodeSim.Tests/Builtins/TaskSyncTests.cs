using System;
using System.Linq;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>Task, semaphore and queue built-ins.</summary>
    [Collection("World")]
    public sealed class TaskSyncTests : BuiltinTestBase
    {
        // ------------------------------------------------------------------ call text
        private static Function Fn(string args, int minParam = -1) =>
            new Function("F", args, Types.Void, new byte[] { 0xff })
            {
                MinParam = minParam < 0 ? args.Length : minParam,
            };

        [Fact]
        public void CallTextLiteralsConvertToTheParameterTypes()
        {
            // call text: strings with ^ escapes, a name as its text, -n, real to INT truncates
            var v = CallText.Bind(Fn("474274"), "12, \"a^\"b\", -3.9, 2.5e1, Tag_1, 7", out int err);
            Assert.Equal(0, err);
            Assert.Equal(12, v[0].I);
            Assert.Equal("a\"b", v[1].S);
            Assert.Equal(-3, v[2].I);
            Assert.Equal(25.0, v[3].D);
            Assert.Equal("Tag_1", v[4].S);
            Assert.Equal(7, v[5].I);
        }

        [Fact]
        public void CallTextNumbersForStringsAndBadNumbers()
        {
            var v = CallText.Bind(Fn("77"), "42, 1.23456789", out int err);
            Assert.Equal(0, err);
            Assert.Equal("42", v[0].S);
            Assert.Equal("1.2346", v[1].S); // _gcvt(x, 5)
            CallText.Bind(Fn("4"), "abc", out err);
            Assert.Equal(Errors.TypeMismatch, err); // 0x1b1: number type parameter expected
            CallText.Bind(Fn("44", 2), "1", out err);
            Assert.Equal(Errors.TooFewArgs, err); // 0x211
            v = CallText.Bind(Fn("44", 1), "1", out err);
            Assert.Equal(0, err);
            Assert.Single(v); // the callee's default for the rest
            CallText.Bind(Fn("4"), "1, 2", out err);
            Assert.Equal(Errors.TooManyArgs, err); // 0x206, the call is still made
            v = CallText.Bind(Fn("44"), "1,,", out err);
            Assert.Equal(0, v[1].I); // an empty argument is 0 / ''
        }

        // ------------------------------------------------------------------ TaskNew & co
        private Sim TaskProgram() =>
            Make(pb =>
            {
                // Worker(INT n): Count_Long = n; Sleep(1); Count_Long = n + 1
                pb.Function("Worker", "4")
                    .Begin()
                    .LoadStack(0)
                    .Store("Count_Long")
                    .ConstInt(1)
                    .Func("Sleep", 1)
                    .Op("Pop")
                    .LoadStack(0)
                    .ConstInt(1)
                    .Op("Add")
                    .Store("Count_Long")
                    .Ret()
                    .End();
                Ret(pb, "Start", "TaskNew", "Worker", "41", 0);
                Ret(pb, "StartSingle", "TaskNew", "Worker", "5", 8);
                Ret(pb, "StartBad", "TaskNew", "NoSuchFn", "", 0);
                Ret(pb, "StartBadArgs", "TaskNew", "Worker", "abc", 0);
                Ret(pb, "Hnd", "TaskHnd", "Worker");
                Ret(pb, "Me", "TaskHnd", "");
                pb.Function("Echo", "7", Types.String)
                    .Begin()
                    .LoadStack(0)
                    .ConstStr("!")
                    .Op("Add")
                    .RetValue()
                    .End();
                Ret(pb, "CallEcho", "TaskCall", "Echo", "\"hi\"");
                Ret(pb, "CallVoid", "TaskCall", "Worker", "3");
                Ret(pb, "CallNone", "TaskCall", "Nope", "");
            });

        [Fact]
        public void TaskNewRunsTheFunctionAsANewTask()
        {
            var s = TaskProgram();
            var r = s.Call("Start");
            Assert.Equal("stop", r.Outcome);
            long h = r.Task.Result!.Value.I;
            Assert.True(h > 0);
            s.Run.Scheduler.Run();
            Assert.Equal(41, s.W.Tags.Get("Count_Long").I);
            var w = s.Run.Scheduler.Tasks[(int)h];
            Assert.Equal("Worker", w.Name);
            Assert.Equal(TaskState.Sleep, w.State);
            Assert.Equal(h, s.Result("Hnd").I); // TaskHnd(name)
            s.W.Clock.Advance(2);
            s.Run.Scheduler.Run();
            Assert.Equal(42, s.W.Tags.Get("Count_Long").I);
            Assert.True(w.Done);
            Assert.Equal(-1, s.Result("Hnd").I);
        }

        [Fact]
        public void TaskNewMode8IsSingleInstance()
        {
            var s = TaskProgram();
            Assert.True(s.Result("Start").I > 0);
            Assert.Equal(-1, s.Result("StartSingle").I); // a task 'Worker' lives: -1
            s.Run.Scheduler.Run();
            s.W.Clock.Advance(2);
            s.Run.Scheduler.Run();
            Assert.True(s.Result("StartSingle").I > 0);
        }

        [Fact]
        public void TaskNewOfAnUnknownFunctionOrBadTextIsMinusOne()
        {
            var s = TaskProgram();
            var r = s.Call("StartBad");
            Assert.Equal(-1, r.Task.Result!.Value.I);
            Assert.Equal(0, r.Task.Error); // no error code (null pointer)
            Assert.Equal(-1, s.Result("StartBadArgs").I);
        }

        [Fact]
        public void TaskNewExChecksTheRateAndTasksKeepTheirCluster()
        {
            var s = Make(pb =>
            {
                // Clu(): Name_Str = TaskCluster()
                pb.Function("Clu").Func("TaskCluster", 0).Store("Name_Str").Ret().End();
                // Spawn(): TaskNew("Clu", "", 0) without a cluster
                pb.Function("Spawn")
                    .ConstStr("Clu")
                    .ConstStr("")
                    .ConstInt(0)
                    .Func("TaskNew", 3)
                    .Op("Pop")
                    .Ret()
                    .End();
                Ret(pb, "ex", "TaskNewEx", "Clu", "", 0, 500, "C1");
                Ret(pb, "exRate0", "TaskNewEx", "Clu", "", 0, 0, "");
                Ret(pb, "exRateDefault", "TaskNewEx", "Clu", "", 0, -1, "");
                Ret(pb, "exCluster", "TaskNewEx", "Clu", "", 0, 500, "Nope");
                Ret(pb, "exSpawn", "TaskNewEx", "Spawn", "", 0, -1, "c1");
                Ret(pb, "own", "TaskCluster");
            });
            CicodeSim.Tests.World.WorldFixture.WriteDbf(
                System.IO.Path.Combine(Tmp.Path, "inst", "User", "Main", "cluster.dbf"),
                new[] { ("NAME", 16) },
                new[] { CicodeSim.Tests.World.WorldFixture.Row(("NAME", "C1")) }
            );
            Assert.True(s.Result("ex").I > 0);
            s.Run.Scheduler.Run();
            Assert.Equal("C1", s.W.Tags.Get("Name_Str").S);
            var r = s.Call("exRate0");
            Assert.Equal(-1, r.Task.Result!.Value.I);
            Assert.Equal(Errors.InvalidArg, r.Task.Error);
            Assert.True(s.Result("exRateDefault").I > 0); // -1 = the caller's rate
            s.Run.Scheduler.Run();
            Assert.Equal("", s.W.Tags.Get("Name_Str").S);
            Assert.Equal(Errors.InvalidArg, s.Call("exCluster").Task.Error);
            Assert.True(s.Result("exSpawn").I > 0);
            s.Run.Scheduler.Run();
            Assert.Equal("c1", s.W.Tags.Get("Name_Str").S); // inherited from its starter
            Assert.Equal("", S(s.Result("own")));
        }

        [Fact]
        public void TaskCallWaitsAndReturnsTheResultAsText()
        {
            var s = TaskProgram();
            Assert.Equal("hi!", S(s.Result("CallEcho")));
            var r = s.Call("CallVoid");
            Assert.Equal("stop", r.Outcome);
            Assert.Equal("", S(r.Task.Result!.Value)); // VOID: ''
            Assert.Equal(4, s.W.Tags.Get("Count_Long").I); // it waited for the Sleep(1)
            Assert.True(r.SimSeconds >= 1.0);
            r = s.Call("CallNone");
            Assert.Equal(Errors.InvalidFunction, r.Task.Error);
        }

        [Fact]
        public void TaskCallAndTaskNewCallABuiltIn()
        {
            // help 1194741 calls a built-in (StrFill); Tab_Style's alarm table calls
            // TaskCall("AlarmCountList", "21")
            var s = Make(pb =>
            {
                Ret(pb, "left", "TaskCall", "StrLeft", "\"abcdef\", 3");
                Ret(pb, "fill", "TaskCall", "StrFill", "\"ab\", 5");
                Ret(pb, "bad", "TaskCall", "StrLeft", "\"abcdef\"");
                Ret(pb, "start", "TaskNew", "StrLeft", "\"abcdef\", 3", 0);
                Ret(pb, "none", "TaskNew", "NoSuchBuiltin", "", 0);
            });
            Assert.Equal("abc", S(s.Result("left")));
            Assert.Equal(5, S(s.Result("fill")).Length);
            var r = s.Call("bad");
            Assert.Equal(Errors.TooFewArgs, r.Task.Error);
            Assert.True(s.Result("start").I > 0);
            Assert.Equal(-1, s.Result("none").I);
        }

        [Fact]
        public void TaskCallInForegroundIs282()
        {
            var s = TaskProgram();
            var t = s.Foreground("CallEcho");
            Assert.Equal(TaskState.Stop, t.State);
            Assert.Equal(Errors.FgBlock, t.Error);
            Assert.Equal("", t.Result!.Value.S);
        }

        [Fact]
        public void TaskKillSuspendResumeAndSignals()
        {
            var s = Make(pb =>
            {
                // Loop(): WHILE 1 DO Count_Long = Count_Long + 1; SleepMS(100) END
                pb.Function("Loop")
                    .Label("top")
                    .Load("Count_Long")
                    .ConstInt(1)
                    .Op("Add")
                    .Store("Count_Long")
                    .ConstInt(100)
                    .Func("SleepMS", 1)
                    .Op("Pop")
                    .Branch("top")
                    .End();
                pb.Function("Kill", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("TaskKill", 1)
                    .RetValue()
                    .End();
                pb.Function("Susp", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("TaskSuspend", 1)
                    .RetValue()
                    .End();
                pb.Function("Res", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("TaskResume", 1)
                    .RetValue()
                    .End();
                pb.Function("SetSig", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .ConstInt(1)
                    .Func("TaskSetSignal", 2)
                    .RetValue()
                    .End();
                pb.Function("GetSig", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("TaskGetSignal", 1)
                    .RetValue()
                    .End();
            });
            var loop = s.Run.Scheduler.Spawn("Loop");
            s.Run.Scheduler.Run();
            s.W.Clock.Advance(0.25);
            s.Run.Scheduler.Run();
            long c0 = s.W.Tags.Get("Count_Long").I;
            Assert.True(c0 >= 2);
            Assert.Equal(0, s.Result("Susp", loop.Handle).I);
            s.W.Clock.Advance(1);
            s.Run.Scheduler.Run();
            Assert.Equal(c0, s.W.Tags.Get("Count_Long").I); // suspended: no turns
            Assert.Equal(0, s.Result("Res", loop.Handle).I);
            s.Run.Scheduler.Run();
            Assert.Equal(c0 + 1, s.W.Tags.Get("Count_Long").I); // resumed: its Sleep ended early
            Assert.Equal(0, s.Result("GetSig", loop.Handle).I);
            Assert.Equal(0, s.Result("SetSig", loop.Handle).I);
            Assert.Equal(1, s.Result("GetSig", loop.Handle).I);
            Assert.Equal(0, s.Result("Kill", loop.Handle).I);
            Assert.True(loop.Done);
            Assert.Equal(Codes.BadHandle, s.Result("Kill", loop.Handle).I);
            Assert.Equal(Codes.BadHandle, s.Result("Susp", 0).I); // handle 0: 269
            Assert.Equal(Codes.BadHandle, s.Result("SetSig", 9999).I);
            Assert.Equal(0, s.Result("GetSig", 9999).I);
        }

        // ------------------------------------------------------------------ semaphores
        private Sim SemProgram() =>
            Make(pb =>
            {
                Ret(pb, "Open0", "SemOpen", "S1", 0);
                Ret(pb, "Open1", "SemOpen", "S1", 1);
                Ret(pb, "Open2", "SemOpen", "S1", 2);
                Ret(pb, "OpenBadMode", "SemOpen", "S1", 3);
                Ret(pb, "OpenBadName", "SemOpen", "S 1", 1);
                // Take(INT secs): h = SemOpen("S1", 2); r = SemWait(h, secs); RETURN r
                pb.Function("Take", "4", Types.Long)
                    .Begin()
                    .ConstStr("S1")
                    .ConstInt(2)
                    .Func("SemOpen", 2)
                    .LoadStack(0)
                    .Func("SemWait", 2)
                    .RetValue()
                    .End();
                // Hold(INT secs): take S1, sleep secs, signal; returns the signal's result
                pb.Function("Hold", "4", Types.Long)
                    .Begin()
                    .StackVar(Types.Long)
                    .ConstStr("S1")
                    .ConstInt(2)
                    .Func("SemOpen", 2)
                    .StoreStack(1)
                    .LoadStack(1)
                    .ConstInt(-1)
                    .Func("SemWait", 2)
                    .Op("Pop")
                    .LoadStack(0)
                    .Func("Sleep", 1)
                    .Op("Pop")
                    .LoadStack(1)
                    .Func("SemSignal", 1)
                    .RetValue()
                    .End();
                // HoldAndDie(): take S1 and return without signalling
                pb.Function("HoldAndDie")
                    .ConstStr("S1")
                    .ConstInt(2)
                    .Func("SemOpen", 2)
                    .ConstInt(-1)
                    .Func("SemWait", 2)
                    .Op("Pop")
                    .Ret()
                    .End();
                // Waiter(STRING id, INT delayMs): SleepMS(delay); SemWait(S1, -1); Name_Str = Name_Str + id; SemSignal
                pb.Function("Waiter", "74")
                    .Begin()
                    .StackVar(Types.Long)
                    .ConstStr("S1")
                    .ConstInt(2)
                    .Func("SemOpen", 2)
                    .StoreStack(2)
                    .LoadStack(1)
                    .Func("SleepMS", 1)
                    .Op("Pop")
                    .LoadStack(2)
                    .ConstInt(-1)
                    .Func("SemWait", 2)
                    .Op("Pop")
                    .Load("Name_Str")
                    .LoadStack(0)
                    .Op("Add")
                    .Store("Name_Str")
                    .LoadStack(2)
                    .Func("SemSignal", 1)
                    .Op("Pop")
                    .Ret()
                    .End();
                pb.Function("Signal", "", Types.Long)
                    .ConstStr("S1")
                    .ConstInt(0)
                    .Func("SemOpen", 2)
                    .Func("SemSignal", 1)
                    .RetValue()
                    .End();
                pb.Function("Close", "", Types.Long)
                    .ConstStr("S1")
                    .ConstInt(0)
                    .Func("SemOpen", 2)
                    .Func("SemClose", 1)
                    .RetValue()
                    .End();
                Ret(pb, "WaitBad", "SemWait", 77, 0);
                // Recursive(): take S1 twice, signal once: still owned (another Take(0) times out)
                pb.Function("Recursive", "", Types.Long)
                    .StackVar(Types.Long)
                    .ConstStr("S1")
                    .ConstInt(2)
                    .Func("SemOpen", 2)
                    .StoreStack(0)
                    .LoadStack(0)
                    .ConstInt(0)
                    .Func("SemWait", 2)
                    .Op("Pop")
                    .LoadStack(0)
                    .ConstInt(0)
                    .Func("SemWait", 2)
                    .Op("Pop")
                    .LoadStack(0)
                    .Func("SemSignal", 1)
                    .RetValue()
                    .End();
            });

        [Fact]
        public void SemOpenModes()
        {
            var s = SemProgram();
            Assert.Equal(-1, s.Result("Open0").I); // mode 0: none yet
            long a = s.Result("Open2").I; // mode 2: created
            Assert.Equal(0, a); // handles from 0
            Assert.Equal(a, s.Result("Open0").I);
            Assert.Equal(a, s.Result("Open2").I);
            Assert.Equal(1, s.Result("Open1").I); // mode 1 always creates
            var r = s.Call("OpenBadMode");
            Assert.Equal(-1, r.Task.Result!.Value.I);
            Assert.Equal(Codes.InvalidArg, r.Task.Error);
            r = s.Call("OpenBadName");
            Assert.Equal(-1, r.Task.Result!.Value.I);
            Assert.Equal(Codes.InvalidArg, r.Task.Error);
        }

        [Fact]
        public void SemWaitTakesSignalsAndTimesOut()
        {
            var s = SemProgram();
            Assert.Equal(0, s.Result("Take", 0).I); // free: taken at once
            Assert.Equal(Codes.NoPrivilege, s.Result("Signal").I); // another task is not the owner (276)
            // the owner ended without signalling: the next SemWait gets 297 'owner died'
            Assert.Equal(Codes.SemOwnerDied, s.Result("Take", 0).I);
            var holder = s.Run.Scheduler.Spawn("Hold", new[] { Value.Long(5) });
            s.Run.Scheduler.Run();
            Assert.Equal(TaskState.Sleep, holder.State);
            Assert.Equal(Codes.SemTimeout, s.Result("Take", 0).I); // Timeout 0: no wait, 298
            var r = s.Call("Take", 2);
            Assert.Equal(Codes.SemTimeout, r.Task.Result!.Value.I); // 2 s timeout while held for 5
            Assert.InRange(r.SimSeconds, 2.0, 3.0);
            r = s.Call("Take", -1);
            Assert.Equal(0, r.Task.Result!.Value.I); // handed over by Hold's SemSignal
            Assert.Equal(0, holder.Result!.Value.I);
            Assert.Equal(Codes.BadHandle, s.Result("WaitBad").I);
        }

        [Fact]
        public void SemaphoreIsRecursiveForItsOwner()
        {
            var s = SemProgram();
            Assert.Equal(0, s.Result("Recursive").I);
            // the task ended owning it once more: the owner died
            Assert.Equal(Codes.SemOwnerDied, s.Result("Take", 0).I);
        }

        [Fact]
        public void SemaphoreWakesWaitersFirstInFirstOut()
        {
            var s = SemProgram();
            s.W.Tags.Set("Name_Str", "");
            var holder = s.Run.Scheduler.Spawn("Hold", new[] { Value.Long(3) });
            // created in the order A, B, C; they reach SemWait in the order C, A, B
            s.Run.Scheduler.Spawn("Waiter", new[] { Value.Str("A"), Value.Long(500) });
            s.Run.Scheduler.Spawn("Waiter", new[] { Value.Str("B"), Value.Long(1000) });
            s.Run.Scheduler.Spawn("Waiter", new[] { Value.Str("C"), Value.Long(100) });
            for (int i = 0; i < 40; i++)
            {
                s.Run.Scheduler.Run();
                s.W.Clock.Advance(0.25);
            }
            s.Run.Scheduler.Run();
            Assert.True(holder.Done);
            Assert.Equal("CAB", s.W.Tags.Get("Name_Str").S);
        }

        [Fact]
        public void SemWaitInForegroundIs282AndCloseReleasesWaiters()
        {
            var s = Make(pb =>
            {
                pb.Function("FgTake", "", Types.Long)
                    .ConstStr("S1")
                    .ConstInt(2)
                    .Func("SemOpen", 2)
                    .ConstInt(-1)
                    .Func("SemWait", 2)
                    .RetValue()
                    .End();
                pb.Function("Take", "", Types.Long)
                    .ConstStr("S1")
                    .ConstInt(2)
                    .Func("SemOpen", 2)
                    .ConstInt(-1)
                    .Func("SemWait", 2)
                    .RetValue()
                    .End();
                pb.Function("Close", "", Types.Long)
                    .ConstStr("S1")
                    .ConstInt(0)
                    .Func("SemOpen", 2)
                    .Func("SemClose", 1)
                    .RetValue()
                    .End();
                pb.Function("Hold")
                    .ConstStr("S1")
                    .ConstInt(2)
                    .Func("SemOpen", 2)
                    .ConstInt(-1)
                    .Func("SemWait", 2)
                    .Op("Pop")
                    .ConstInt(10)
                    .Func("Sleep", 1)
                    .Op("Pop")
                    .Ret()
                    .End();
            });
            var holder = s.Run.Scheduler.Spawn("Hold");
            s.Run.Scheduler.Run();
            var fg = s.Foreground("FgTake");
            Assert.Equal(TaskState.Stop, fg.State);
            Assert.Equal(Codes.FgBlock, fg.Result!.Value.I); // returned, the task goes on
            var waiter = s.Run.Scheduler.Spawn("Take");
            s.Run.Scheduler.Run();
            Assert.Equal(TaskState.Wait, waiter.State);
            Assert.Equal(0, s.Result("Close").I);
            s.Run.Scheduler.Run();
            Assert.Equal(TaskState.Stop, waiter.State);
            Assert.Equal(Codes.SemOwnerDied, waiter.Result!.Value.I); // released with 0x129
            Assert.False(holder.Done);
        }

        [Fact]
        public void IncludeCriticalSectionRunsOnSemaphores()
        {
            // Include's EnterCriticalSection: SemOpen(name, 2) + SemWait(h, -1); Leave: SemOpen(name, 0) + SemSignal
            var s = Make(pb =>
            {
                pb.Function("Enter", "7", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .ConstInt(2)
                    .Func("SemOpen", 2)
                    .ConstInt(-1)
                    .Func("SemWait", 2)
                    .RetValue()
                    .End();
                pb.Function("Leave", "7")
                    .Begin()
                    .LoadStack(0)
                    .ConstInt(0)
                    .Func("SemOpen", 2)
                    .Func("SemSignal", 1)
                    .Op("Pop")
                    .Ret()
                    .End();
                // Section(id): Enter("CS"); Name_Str = Name_Str + id; Sleep(1); Name_Str = Name_Str + id; Leave("CS")
                pb.Function("Section", "7")
                    .Begin()
                    .ConstStr("CS")
                    .Call("Enter", 1)
                    .Op("Pop")
                    .Load("Name_Str")
                    .LoadStack(0)
                    .Op("Add")
                    .Store("Name_Str")
                    .ConstInt(1)
                    .Func("Sleep", 1)
                    .Op("Pop")
                    .Load("Name_Str")
                    .LoadStack(0)
                    .Op("Add")
                    .Store("Name_Str")
                    .ConstStr("CS")
                    .Call("Leave", 1)
                    .Ret()
                    .End();
            });
            s.W.Tags.Set("Name_Str", "");
            s.Run.Scheduler.Spawn("Section", new[] { Value.Str("1") });
            s.Run.Scheduler.Spawn("Section", new[] { Value.Str("2") });
            for (int i = 0; i < 20; i++)
            {
                s.Run.Scheduler.Run();
                s.W.Clock.Advance(0.25);
            }
            Assert.Equal("1122", s.W.Tags.Get("Name_Str").S); // never interleaved
        }

        // ------------------------------------------------------------------ queues
        private Sim QueProgram() =>
            Make(pb =>
            {
                Ret(pb, "Open", "QueOpen", "Q1", 2);
                Ret(pb, "Open0", "QueOpen", "Q1", 0);
                Ret(pb, "OpenBad", "QueOpen", "Q1", 3);
                pb.Function("Write", "47", Types.Long)
                    .Begin()
                    .ConstStr("Q1")
                    .ConstInt(2)
                    .Func("QueOpen", 2)
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("QueWrite", 3)
                    .RetValue()
                    .End();
                // Read(INT mode): QueRead(h, Type, Str, mode); RETURN rc : Type : Str as text
                pb.Function("Read", "4", Types.String)
                    .Begin()
                    .StackVar(Types.Long, Types.String, Types.Long)
                    .ConstStr("Q1")
                    .ConstInt(2)
                    .Func("QueOpen", 2)
                    .AddrStack(1)
                    .AddrStack(2)
                    .LoadStack(0)
                    .Func("QueRead", 4)
                    .StoreStack(3)
                    .LoadStack(3)
                    .Convert(Types.String)
                    .ConstStr(":")
                    .Op("Add")
                    .LoadStack(1)
                    .Convert(Types.String)
                    .Op("Add")
                    .ConstStr(":")
                    .Op("Add")
                    .LoadStack(2)
                    .Op("Add")
                    .RetValue()
                    .End();
                // Peek(INT type, STRING str, INT mode)
                pb.Function("Peek", "474", Types.String)
                    .Begin()
                    .StackVar(Types.Long)
                    .ConstStr("Q1")
                    .ConstInt(2)
                    .Func("QueOpen", 2)
                    .AddrStack(0)
                    .AddrStack(1)
                    .LoadStack(2)
                    .Func("QuePeek", 4)
                    .StoreStack(3)
                    .LoadStack(3)
                    .Convert(Types.String)
                    .ConstStr(":")
                    .Op("Add")
                    .LoadStack(0)
                    .Convert(Types.String)
                    .Op("Add")
                    .ConstStr(":")
                    .Op("Add")
                    .LoadStack(1)
                    .Op("Add")
                    .RetValue()
                    .End();
                pb.Function("Len", "", Types.Long)
                    .ConstStr("Q1")
                    .ConstInt(2)
                    .Func("QueOpen", 2)
                    .Func("QueLength", 1)
                    .RetValue()
                    .End();
                pb.Function("Close", "", Types.Long)
                    .ConstStr("Q1")
                    .ConstInt(0)
                    .Func("QueOpen", 2)
                    .Func("QueClose", 1)
                    .RetValue()
                    .End();
                pb.Function("Resume", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("TaskResume", 1)
                    .RetValue()
                    .End();
            });

        [Fact]
        public void QueueWriteReadPeekLength()
        {
            var s = QueProgram();
            Assert.Equal(-1, s.Result("Open0").I);
            long h = s.Result("Open").I;
            Assert.Equal(h, s.Result("Open0").I);
            Assert.Equal(-1, s.Result("OpenBad").I);
            Assert.Equal(0, s.Result("Write", 1, "one").I);
            Assert.Equal(0, s.Result("Write", 2, "two").I);
            Assert.Equal(0, s.Result("Write", 3, "Three").I);
            Assert.Equal(3, s.Result("Len").I);
            Assert.Equal("0:2:two", S(s.Result("Peek", 2, "", 2))); // by number
            Assert.Equal("0:3:Three", S(s.Result("Peek", 0, "three", 1))); // by string, case-insensitive
            Assert.Equal("296:0:three", S(s.Result("Peek", 0, "three", 4))); // case-sensitive: none
            Assert.Equal("0:2:two", S(s.Result("Peek", 1, "", 16))); // offset 1
            Assert.Equal("0:2:two", S(s.Result("Peek", 2, "", 2 + 8))); // + 8 removes it
            Assert.Equal(2, s.Result("Len").I);
            Assert.Equal("0:1:one", S(s.Result("Read", 0)));
            Assert.Equal("0:3:Three", S(s.Result("Read", 0)));
            Assert.Equal("296:0:", S(s.Result("Read", 0))); // empty, no wait
        }

        [Fact]
        public void QueReadBlocksUntilAWrite()
        {
            var s = QueProgram();
            s.Result("Open");
            var reader = s.Run.Scheduler.Spawn("Read", new[] { Value.Long(1) });
            s.Run.Scheduler.Run();
            Assert.Equal(TaskState.Wait, reader.State);
            Assert.Equal(0, s.Result("Write", 7, "seven").I);
            s.Run.Scheduler.Run();
            Assert.Equal("0:7:seven", reader.Result!.Value.S);
            Assert.Equal(0, s.Result("Len").I); // handed to the reader, not queued
            // a second blocked reader of a queue without mode 4: 282
            var r1 = s.Run.Scheduler.Spawn("Read", new[] { Value.Long(1) });
            s.Run.Scheduler.Run();
            Assert.Equal("282:0:", S(s.Result("Read", 1)));
            Assert.Equal(0, s.Result("Close").I);
            s.Run.Scheduler.Run();
            Assert.Equal("0:0:", r1.Result!.Value.S); // QueClose: 0, Type 0, Str ''
        }

        [Fact]
        public void QueReadResumedWithoutAnElementReturns0()
        {
            // the runtime returns 0 when it blocks; only QueWrite / QueClose set the result again,
            // so a reader TaskResume wakes returns 0 with Type and Str as they were
            var s = QueProgram();
            s.Result("Open");
            var reader = s.Run.Scheduler.Spawn("Read", new[] { Value.Long(1) });
            s.Run.Scheduler.Run();
            Assert.Equal(TaskState.Wait, reader.State);
            Assert.Equal(0, s.Result("Resume", reader.Handle).I);
            s.Run.Scheduler.Run();
            Assert.Equal("0:0:", reader.Result!.Value.S);
            Assert.Equal(0, s.Result("Write", 7, "seven").I);
            Assert.Equal(1, s.Result("Len").I); // no reader waits any more: queued
        }

        [Fact]
        public void QueReadInForegroundIs282()
        {
            var s = QueProgram();
            s.Result("Open");
            var t = s.Run.Vm.TaskForCall("Read", new[] { Value.Long(1) }, foreground: true);
            s.Run.Vm.Run(t);
            Assert.Equal("282:0:", t.Result!.Value.S);
        }
    }
}
