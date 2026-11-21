using System;
using System.IO;
using System.Linq;
using CicodeSim.Sim;
using CicodeSim.Tests.Compiler;
using CicodeSim.Tests.Golden;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Host
{
    /// <summary>The variable history, time travel and data breakpoints of the debug target
    /// (IDebugTarget.History ... SetDataBreakpoints): a project compiled from source
    /// in a temporary folder (the R2 System and Include + the folder).</summary>
    public class HistoryTests
    {
        // line numbers are the test's: keep the text as it is
        private const string Code =
            "GLOBAL INT gTotal = 0;\r\n"
            + // 1
            "INT FUNCTION Hist_Add(INT a, INT b)\r\n"
            + // 2
            "\tINT s = a + b;\r\n"
            + // 3
            "\ts = s * 2;\r\n"
            + // 4
            "\tgTotal = gTotal + s;\r\n"
            + // 5
            "\tRETURN s;\r\n"
            + // 6
            "END\r\n"
            + // 7
            "\r\n"
            + // 8
            "INT FUNCTION Hist_Main()\r\n"
            + // 9
            "\tINT x = 1;\r\n"
            + // 10
            "\tx = Hist_Add(x, 2);\r\n"
            + // 11
            "\tx = x + 1;\r\n"
            + // 12
            "\tRETURN x;\r\n"
            + // 13
            "END\r\n"
            + // 14
            "\r\n"
            + // 15
            "INT FUNCTION Hist_Loop()\r\n"
            + // 16
            "\tINT i;\r\n"
            + // 17
            "\tFOR i = 1 TO 200 DO\r\n"
            + // 18
            "\t\tgTotal = gTotal + i;\r\n"
            + // 19
            "\tEND\r\n"
            + // 20
            "\tRETURN gTotal;\r\n"
            + // 21
            "END\r\n"; // 22

        private sealed class Rig : IDisposable
        {
            public readonly string Dir;
            public readonly string File;
            public readonly Simulation Sim;
            public readonly SimEvents Ev;

            public Rig()
            {
                Dir = CodegenReloadTests.NewProject(("hist.ci", Code));
                File = Path.Combine(Dir, "hist.ci");
                Sim = SimTestUtil.Start(Dir, compile: CodeSource.All);
                Ev = new SimEvents(Sim);
                Sim.Debugger.Attach();
            }

            public IDebugTarget D => Sim.Debugger;

            public void Bp(params int[] lines) =>
                Assert.All(
                    D.SetBreakpoints(File, lines.Select(l => new BreakpointRequest { Line = l }).ToList()),
                    b => Assert.True(b.Verified, b.Message)
                );

            public StoppedEventArgs Run(string call)
            {
                Sim.Invoke(() => Sim.StartCall(call, null));
                return Ev.Wait<StoppedEventArgs>();
            }

            public string Local(int task, string name, int frame = 0)
            {
                var f = D.StackTrace(task)[frame];
                var sc = D.Scopes(f.Id).First(s => s.Name == "Locals");
                return D.Variables(sc.VariablesReference)
                    .First(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    .Value;
            }

            public string Global(int task, string name)
            {
                var f = D.StackTrace(task)[0];
                var all = D.Scopes(f.Id)
                    .Where(s => s.Name == "Globals" || s.Name == "Module")
                    .SelectMany(s => D.Variables(s.VariablesReference));
                return all.First(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            }

            public void Dispose()
            {
                Sim.Dispose();
                CodegenReloadTests.Drop(Dir);
            }
        }

        [GoldenFact]
        public void HistoryRecordsEntryValuesAndEveryWrite()
        {
            using (var r = new Rig())
            {
                r.Bp(12);
                var stop = r.Run("Hist_Main()");
                Assert.Equal(StopReason.Breakpoint, stop.Reason);
                var h = r.D.History(stop.Task);
                var writes = h.Where(c => !c.Entry).Select(c => c.Name + "=" + c.New).ToList();
                Assert.Equal(new[] { "x=1", "s=3", "s=6", "gTotal=6", "x=6" }, writes);
                // the parameters' values at the call's entry (Old empty)
                var entry = h.Where(c => c.Entry && c.Function == "Hist_Add")
                    .ToDictionary(c => c.Name, c => c);
                Assert.Equal("1", entry["a"].New);
                Assert.Equal("2", entry["b"].New);
                Assert.Equal("", entry["a"].Old);
                var g = h.Single(c => c.Name == "gTotal");
                Assert.Equal("global", g.Scope);
                Assert.Equal("0", g.Old);
                Assert.Equal(5, g.Line);
                // a call's changes by its id; the frame's CallId names it
                var frames = r.D.StackTrace(stop.Task);
                Assert.True(frames[0].CallId > 0);
                var mine = r
                    .D.History(stop.Task, callId: frames[0].CallId)
                    .Where(c => !c.Entry)
                    .Select(c => c.Name)
                    .ToList();
                Assert.Equal(new[] { "x", "x" }, mine);
                Assert.Equal(
                    new[] { "s=3", "s=6" },
                    r.D.History(stop.Task, "S").Where(c => !c.Entry).Select(c => c.Name + "=" + c.New)
                );
                var st = r.D.HistoryState(stop.Task);
                Assert.Equal(-1, st.Cursor);
                Assert.False(st.Truncated);
                Assert.True(st.Last > st.First);
                r.D.Continue(stop.Task);
                r.Ev.WaitExit(stop.Task);
            }
        }

        [GoldenFact]
        public void StepBackShowsTheStateOfEarlierLinesThenForwardIsLiveAgain()
        {
            using (var r = new Rig())
            {
                r.Bp(12);
                var stop = r.Run("Hist_Main()");
                int t = stop.Task;
                Assert.Equal("6", r.Local(t, "x"));
                // back into the returned call: its frame and values come back
                var m = r.D.StepBack(t);
                Assert.False(m.Live);
                Assert.Equal(6, m.Line);
                Assert.Equal("Hist_Add", m.Function);
                var fr = r.D.StackTrace(t);
                Assert.Equal(new[] { "Hist_Add", "Hist_Main" }, fr.Take(2).Select(f => f.Function));
                Assert.Equal(11, fr[1].Line);
                Assert.Equal("6", r.Local(t, "s"));
                Assert.Equal("1", r.Local(t, "x", 1)); // x = Hist_Add(...) has not happened yet
                Assert.Equal("6", r.Global(t, "gTotal"));
                Assert.Equal(5, r.D.StepBack(t).Line);
                Assert.Equal("0", r.Global(t, "gTotal"));
                Assert.Equal(4, r.D.StepBack(t).Line);
                Assert.Equal("3", r.Local(t, "s"));
                // read-only while not live: SetVariable throws, Evaluate gives a name's old value only
                var f0 = r.D.StackTrace(t)[0];
                var locals = r.D.Scopes(f0.Id).First(s => s.Name == "Locals").VariablesReference;
                Assert.Throws<ArgumentException>(() => r.D.SetVariable(locals, "s", "9"));
                Assert.Equal("3", r.D.Evaluate("s", f0.Id).Result);
                Assert.Equal("", r.D.Evaluate("s", f0.Id).ErrorText);
                Assert.NotEqual("", r.D.Evaluate("s + 1", f0.Id).ErrorText);
                // forward to the stop's own line: live again
                HistoryMove fw;
                int n = 0;
                do
                {
                    fw = r.D.StepForward(t);
                } while (!fw.Live && ++n < 20);
                Assert.True(fw.Live);
                Assert.Equal(12, fw.Line);
                Assert.Equal(-1, r.D.HistoryState(t).Cursor);
                Assert.Equal("6", r.Local(t, "x"));
                // back once more, then Continue makes it live and runs on
                r.D.StepBack(t);
                r.D.Continue(t);
                var exit = r.Ev.WaitExit(t);
                Assert.Equal(TaskState.Stop, exit.State);
            }
        }

        [GoldenFact]
        public void StepBackReachesTheStartOfTheRecording()
        {
            using (var r = new Rig())
            {
                r.Bp(12);
                int t = r.Run("Hist_Main()").Task;
                HistoryMove m;
                int n = 0;
                do
                {
                    m = r.D.StepBack(t);
                } while (!m.AtStart && ++n < 50);
                Assert.True(m.AtStart);
                Assert.Equal(10, m.Line);
                Assert.Equal("Hist_Main", m.Function);
                Assert.Equal("Hist_Main", r.D.StackTrace(t)[0].Function);
                Assert.Equal(10, r.D.StackTrace(t)[0].Line);
                r.D.Live(t);
                Assert.Equal(12, r.D.StackTrace(t)[0].Line);
                r.D.Continue(t);
                r.Ev.WaitExit(t);
            }
        }

        [GoldenFact]
        public void ADataBreakpointStopsAtTheLineAfterTheWrite()
        {
            using (var r = new Rig())
            {
                var res = r.D.SetDataBreakpoints(new[] { new DataBreakpointRequest { Name = "gtotal" } });
                Assert.True(res.Single().Verified);
                var stop = r.Run("Hist_Main()");
                Assert.Equal(StopReason.DataBreakpoint, stop.Reason);
                Assert.Equal(res[0].Id, stop.HitBreakpoints.Single());
                Assert.Equal(6, r.D.StackTrace(stop.Task)[0].Line);
                Assert.Equal("6", r.Global(stop.Task, "gTotal"));
                // a local's, with a condition (evaluated at the next line)
                r.D.SetDataBreakpoints(
                    new[]
                    {
                        new DataBreakpointRequest
                        {
                            Name = "x",
                            Function = "Hist_Main",
                            Condition = "x > 5",
                        },
                    }
                );
                r.D.Continue(stop.Task);
                var s2 = r.Ev.Wait<StoppedEventArgs>(e =>
                    e.Task == stop.Task && e.Reason == StopReason.DataBreakpoint && e != stop
                );
                Assert.Equal(12, r.D.StackTrace(s2.Task)[0].Line); // x = 1 did not hold; x = 6 did
                Assert.Equal("6", r.Local(s2.Task, "x"));
                // Reverse Continue: back to the previous write a data breakpoint watches
                r.D.SetDataBreakpoints(new[] { new DataBreakpointRequest { Name = "gTotal" } });
                var back = r.D.ReverseContinue(s2.Task);
                Assert.Equal(5, back.Line);
                Assert.Equal("Hist_Add", back.Function);
                Assert.Equal("6", r.Global(s2.Task, "gTotal")); // just after the write
                r.D.SetDataBreakpoints(Array.Empty<DataBreakpointRequest>());
                r.D.Continue(s2.Task);
                r.Ev.WaitExit(s2.Task);
            }
        }

        [GoldenFact]
        public void ReverseContinueStopsAtAnEarlierBreakpointLine()
        {
            using (var r = new Rig())
            {
                r.Bp(4, 12);
                var s1 = r.Run("Hist_Main()");
                Assert.Equal(4, r.D.StackTrace(s1.Task)[0].Line);
                r.D.Continue(s1.Task);
                var s2 = r.Ev.Wait<StoppedEventArgs>(e => e != s1);
                Assert.Equal(12, r.D.StackTrace(s2.Task)[0].Line);
                var back = r.D.ReverseContinue(s2.Task);
                Assert.Equal(4, back.Line);
                Assert.Equal("3", r.Local(s2.Task, "s"));
                var start = r.D.ReverseContinue(s2.Task);
                Assert.True(start.AtStart);
                // the start is the call's first line, its entry values there (not the live line)
                Assert.Equal(10, start.Line);
                var f0 = r.D.StackTrace(s2.Task)[0];
                Assert.Equal(("Hist_Main", 10), (f0.Function, f0.Line));
                Assert.Equal(r.D.HistoryState(s2.Task).Cursor, start.Cursor);
                r.D.Continue(s2.Task);
                r.Ev.WaitExit(s2.Task);
            }
        }

        [GoldenFact]
        public void TheRingDropsTheOldestAndDetachEndsTheRecording()
        {
            using (var r = new Rig())
            {
                r.Sim.Invoke(() =>
                {
                    r.Sim.Vm.Recorder!.Capacity = 64;
                    return 0;
                });
                r.Bp(21);
                var stop = r.Run("Hist_Loop()");
                var st = r.D.HistoryState(stop.Task);
                Assert.True(st.Truncated);
                Assert.Equal(64, st.Events);
                Assert.Equal("20100", r.D.History(stop.Task, "gTotal").Last().New);
                r.D.Continue(stop.Task);
                r.Ev.WaitExit(stop.Task);
                r.D.Detach();
                Assert.Null(r.Sim.Invoke(() => r.Sim.Vm.Recorder));
            }
        }
    }
}
