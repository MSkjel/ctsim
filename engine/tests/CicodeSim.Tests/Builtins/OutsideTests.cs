using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>The outside-world stubs: each returns what its
    /// behaviour.json entry says, raises what the runtime raises for 'not there', counts as not
    /// simulated and goes to the trace as a 'stub' event; none touches the real system.</summary>
    [Collection("World")]
    public sealed class OutsideTests : BuiltinTestBase
    {
        private static Value Raw(Registry reg, CicodeTask t, string name, params Value[] args)
        {
            var b = Func0Table.Default.Lookup(name)!;
            Value r = reg.Call(b.Index, t, ArgList.Of(args));
            if (r.IsBlock)
            {
                // a blocking stub answers at the task's next turn
                var w = t.Wait!;
                t.Wait = null;
                r = w.Result!() ?? Value.Nothing;
            }
            return r;
        }

        private Sim Plain() =>
            Make(pb =>
            {
                pb.Function("noop").Ret().End();
                pb.Function("dll", "", Types.Long)
                    .ConstStr("shell32.dll")
                    .ConstStr("ShellExecuteA")
                    .ConstStr("JJCCCCJ")
                    .Func("DLLOpen", 3)
                    .RetValue()
                    .End();
                pb.Function("dllbad", "", Types.Long)
                    .ConstStr("x.dll")
                    .ConstStr("F")
                    .ConstStr("JX")
                    .Func("DLLOpen", 3)
                    .RetValue()
                    .End();
                pb.Function("sql", "", Types.Long).ConstStr("DSN=x").Func("SQLConnect", 1).RetValue().End();
                pb.Function("sqlmsg", "", Types.String).ConstInt(0).Func("SQLErrMsg", 1).RetValue().End();
                pb.Function("ctl", "", Types.Long)
                    .ConstStr("X.Y")
                    .ConstStr("n")
                    .ConstInt(0)
                    .ConstInt(0)
                    .ConstInt(1)
                    .ConstInt(1)
                    .ConstStr("e")
                    .Func("CreateControlObject", 7)
                    .Op("Pop")
                    .ConstInt(5)
                    .RetValue()
                    .End();
                pb.Function("exec", "", Types.Long)
                    .ConstStr("notepad.exe")
                    .ConstInt(1)
                    .Func("Exec", 2)
                    .RetValue()
                    .End();
            });

        [Fact]
        public void EveryStubReturnsItsBehaviourEntryAndCountsAsNotSimulated()
        {
            var s = Plain();
            var reg = (Registry)s.Run.Vm.Builtins!;
            var task = s.Run.Vm.TaskForCall("noop", Array.Empty<Value>());
            var root = (Dictionary<string, object?>)Json.Parse(WorldBuiltins.BehaviourJson())!;
            var fns = (Dictionary<string, object?>)root["functions"]!;
            int n = 0;
            var bad = new List<string>();
            foreach (var (info, fn) in WorldBuiltins.Declared())
            {
                if (
                    info.Status != "stub"
                    || info.Returns.Length == 0
                    || fn.Method.DeclaringType != typeof(Outside)
                )
                    continue;
                var e = (Dictionary<string, object?>)fns[Func0Table.Default.Lookup(info.Name)!.Name]!;
                object? want = e["returns"];
                task.Error = 0;
                task.Unwinding = false;
                task.State = TaskState.Run;
                Value r = Raw(reg, task, info.Name);
                n++;
                bool ok = want switch
                {
                    null => r.T == Types.Variant && r.O == null,
                    string ws => r.T == Types.String && (r.S ?? "") == ws,
                    _ => r.PayloadIsInt
                        && r.I == Convert.ToInt64(want, System.Globalization.CultureInfo.InvariantCulture),
                };
                if (!ok)
                    bad.Add(
                        info.Name
                            + ": want "
                            + (want ?? "null")
                            + ", got type "
                            + r.T
                            + " "
                            + (r.T == Types.String ? r.S : r.I.ToString())
                    );
                Assert.True(
                    s.Run.Vm.NotSimulatedCounts.TryGetValue(info.Name, out int c) && c >= 1,
                    info.Name + " not counted"
                );
            }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
            Assert.True(n > 0, "No external-service stubs were discovered.");
            Assert.Equal(n, s.Run.Vm.NotSimulatedCounts.Count);
        }

        [Fact]
        public void DdeConversationSharesItemsAndKeepsItsOwnLineCursor()
        {
            var sim = Plain();
            var reg = (Registry)sim.Run.Vm.Builtins!;
            var task = sim.Run.Vm.TaskForCall("noop", Array.Empty<Value>());
            Value Call(string name, params Value[] args) => Raw(reg, task, name, args);
            Value S(string text) => Value.Str(text);
            var app = S("SheetApp");
            var doc = S("Sheet1");
            var item = S("R1C1");
            Assert.Equal(-1, Call("DDEhInitiate", app, doc).I);
            Call("ParameterPut", S("SimulatedDDE"), app, S("1"));
            var h = Call("DDEhInitiate", app, doc);
            var second = Call("DDEhInitiate", app, doc);
            Assert.NotEqual(h.I, second.I);
            Assert.Equal(0, Call("DDEhSetMode", h, Value.Long(2)).I);
            Assert.Equal(0, Call("DDEhWriteLn", h, item, S("a,b")).I);
            Assert.Equal(0, Call("DDEhWriteLn", h, item, S("c,d")).I);
            Assert.Equal("", Call("DDERead", app, doc, item).S);
            Assert.Equal(0, Call("DDEhWriteLn", h, item, S("")).I);
            Assert.Equal("a,b\r\nc,d", Call("DDEhRequest", second, item).S);
            Assert.Equal("a,b", Call("DDEhReadLn", second, item).S);
            Assert.Equal("a,b", Call("DDEhReadLn", h, item).S);
            Assert.Equal("c,d", Call("DDEhReadLn", second, item).S);
            Assert.Equal("", Call("DDEhReadLn", second, item).S);
            Assert.Equal("next", Call("DDEWrite", app, doc, item, S("next")).S);
            Assert.Equal("next", Call("DDEhRequest", h, item).S);
            Assert.Equal(0, Call("DDEhPoke", h, item, S("last")).I);
            Assert.Equal("last", Call("DDERead", app, doc, item).S);
            Assert.Equal("", Call("DDEhRequest", h, S("unknown")).S);
            Assert.Equal(0x4009, Call("DDEhGetLastError", h).I);
            Assert.Equal(0, Call("DDEhExecute", h, S("refresh")).I);
            Assert.Contains(sim.Log, line => line.Contains("SheetApp|Sheet1: refresh"));
            Assert.Equal(1, Call("DDEExec", app, S("refresh")).I);
            Assert.Equal(0, Call("DDEhTerminate", h).I);
            Assert.Equal(269, Call("DDEhTerminate", h).I);
        }

        [Fact]
        public void DdePostPublishesToTheSimulationsDataTopic()
        {
            var sim = Plain();
            var reg = (Registry)sim.Run.Vm.Builtins!;
            var task = sim.Run.Vm.TaskForCall("noop", Array.Empty<Value>());
            Value Call(string name, params string[] args) =>
                Raw(reg, task, name, args.Select(arg => Value.Str(arg)).ToArray());
            Assert.Equal("", Call("DDEPost", "Tank", "10").S);
            Call("ParameterPut", "DDE", "Enable", "1");
            Assert.Equal("10", Call("DDEPost", "Tank", "10").S);
            Assert.Equal("10", Call("DDERead", "Citect", "Data", "TANK").S);
            Assert.Equal("11", Call("DDEPost", "tank", "11").S);
            Assert.Equal("11", Call("DDERead", "CITECT", "DATA", "Tank").S);
            Assert.Equal("", Call("DDERead", "Citect", "Data", "unknown").S);
            Assert.Equal("", Call("DDERead", "Excel", "Sheet1", "Tank").S);
            Assert.Equal(289, task.Error);
        }

        [Fact]
        public void VirtualSerialPortReadsThePeerAndWakesOnClose()
        {
            var sim = Plain();
            var reg = (Registry)sim.Run.Vm.Builtins!;
            var t = sim.Run.Vm.TaskForCall("noop", Array.Empty<Value>());
            Value Call(string name, params Value[] args) =>
                reg.Call(Func0Table.Default.Lookup(name)!.Index, t, ArgList.Of(args));
            Value V(string text) => Value.Str(text);
            Value N(int n) => Value.Long(n);
            Value Ref(Value v) => Value.Variant(new ValueRef(v));
            Assert.Equal(-1, Call("ComOpen", V("A"), N(1)).I);
            Call("ParameterPut", V("SimulatedSerial"), V("A"), V("B"));
            Call("ParameterPut", V("SimulatedSerial"), V("B"), V("A"));
            var send = Call("ComOpen", V("a"), N(1));
            var receive = Call("ComOpen", V("B"), N(1));
            Assert.True(send.I >= 0 && receive.I >= 0);
            Assert.Equal(-1, Call("ComOpen", V("A"), N(1)).I);
            var length = Ref(N(3));
            var buffer = Ref(V("A"));
            Call("StrSetChar", buffer, N(2), N(66));
            Assert.Equal(0, Call("ComWrite", send, buffer, length, N(0)).I);
            Assert.Equal(0, Value.Deref(length).I);
            var two = Ref(N(2));
            Assert.Equal(0, Call("ComRead", receive, buffer, two, N(0)).I);
            Assert.Equal("A\0", Value.Deref(buffer).S);
            Assert.Equal(0, Call("StrGetChar", buffer, N(1)).I);
            Assert.Equal(2, Value.Deref(two).I);
            Assert.Equal(0, Call("ComRead", receive, buffer, two, N(0)).I);
            Assert.Equal("B", Value.Deref(buffer).S);
            Assert.Equal(1, Value.Deref(two).I);
            Assert.True(Call("ComRead", receive, buffer, two, N(1)).IsBlock);
            var timeout = t.Wait!;
            Assert.False(timeout.Poll!());
            sim.W.Clock.AdvanceMs(1000);
            Assert.True(timeout.Poll!());
            Assert.Equal(7, timeout.Result!()!.Value.I);
            Assert.Equal(7, t.Error);
            Assert.Equal(0, Value.Deref(two).I);
            t.Wait = null;
            two = Ref(N(2));
            Assert.True(Call("ComRead", receive, buffer, two, N(-1)).IsBlock);
            var pending = t.Wait!;
            Assert.False(pending.Poll!());
            t.Wait = null;
            Assert.Equal(0, Call("ComClose", receive).I);
            Assert.True(pending.Poll!());
            Assert.Equal(Codes.OutOfRange, pending.Result!()!.Value.I);
            Assert.Equal(Codes.BadHandle, Call("ComReset", receive).I);
        }

        [Fact]
        public void TheFamiliesFailTheWayTheRuntimeFailsWithoutTheThing()
        {
            var s = Plain();
            var r = s.Call("dll");
            Assert.Equal(-1, r.Task.Result!.Value.I);
            Assert.Equal(289, r.Task.Error); // LoadLibrary fails
            Assert.Equal(274, s.Call("dllbad").Task.Error); // a bad type letter
            r = s.Call("sql"); // blocks one turn, then 307
            Assert.Equal("stop", r.Outcome);
            Assert.Equal(-1, r.Task.Result!.Value.I);
            Assert.Equal(307, r.Task.Error);
            Assert.Equal("SQL Error", S(s.Result("sqlmsg")));
            r = s.Call("ctl"); // raise(354, 1) halts
            Assert.Equal(TaskState.Halt, r.Task.State);
            Assert.Equal(354, r.Task.HaltCode);
            Assert.Equal(0, s.Result("exec").I); // never runs a program
            Assert.Contains(s.Log, l => l.Contains("Exec(\"notepad.exe\")") && l.Contains("not simulated"));
            Assert.Equal(1, s.Run.Vm.NotSimulatedCounts["Exec"]);
        }

        /// <summary>The Windows functions SA_Include opens that the simulator answers itself
        /// (Builtins.WinApi): TrendPage.ci's GetCurrentProcessId, Workspace.ci's LockWindowUpdate
        /// and WinUtil cursors; nothing native is called, any other function stays not loaded.</summary>
        [Fact]
        public void TheLibrarysWindowsHelpersAreStoodInFor()
        {
            var s = Plain();
            var reg = (Registry)s.Run.Vm.Builtins!;
            var task = s.Run.Vm.TaskForCall("noop", Array.Empty<Value>());
            long Open(string dll, string fn, string types) =>
                Raw(reg, task, "DLLOpen", Value.Str(dll), Value.Str(fn), Value.Str(types)).I;
            long pid = Open("Kernel32.dll", "GetCurrentProcessId", "J");
            Assert.True(pid >= 0);
            Assert.Equal(
                System.Diagnostics.Process.GetCurrentProcess().Id,
                Raw(reg, task, "DLLCallEx", Value.Long(pid)).I
            );
            Assert.Equal(0, Raw(reg, task, "DLLClose", Value.Long(pid)).I);
            long lw = Open("User32.dll", "LockWindowUpdate", "AJ");
            Assert.Equal("1", Raw(reg, task, "DLLCall", Value.Long(lw), Value.Str("123")).S);
            long wc = Open(
                @"C:\Program Files (x86)\AVEVA Plant SCADA\Bin\WinUtil.DLL",
                "WinUtil_WaitCursor",
                "JJ"
            );
            Assert.Equal("0", Raw(reg, task, "DLLCall", Value.Long(wc), Value.Str("5")).S);
            Assert.Equal(-1, Open("User32.dll", "GetDC", "JJ"));
            Assert.Equal(-1, Open("Kernel32.dll", "ExitProcess", "JJ"));
            // (only the functions it does not answer are logged as not simulated)
            Assert.DoesNotContain(s.Log, l =>
                l.Contains("GetCurrentProcessId") || l.Contains("LockWindowUpdate")
            );
            Assert.Contains(s.Log, l => l.Contains("\"GetDC\"") && l.Contains("not simulated"));
        }

        [Fact]
        public void StubsGoToTheTrace()
        {
            var s = Plain();
            var sw = new StringWriter();
            s.W.OpenTrace(stream: sw);
            s.Call("exec");
            s.W.Trace!.Flush();
            string t = sw.ToString();
            Assert.Contains("\"ev\":\"stub\"", t);
            Assert.Contains("\"fn\":\"Exec\"", t);
            Assert.Contains("notepad.exe", t);
            Assert.Contains("\"returned\":0", t);
        }

        [Fact]
        public void StubsSurviveForegroundCode()
        {
            // a blocking stub in foreground code refuses (282) like the runtime, never waits
            var s = Plain();
            var t = s.Run.Vm.TaskForCall("sql", Array.Empty<Value>(), foreground: true);
            s.Run.Vm.Run(t);
            Assert.Equal(-1, t.Result!.Value.I);
            Assert.Equal(282, t.Error);
        }
    }
}
