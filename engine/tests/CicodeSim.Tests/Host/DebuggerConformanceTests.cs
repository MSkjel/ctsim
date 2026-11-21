using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CicodeSim.Sim;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Host
{
    /// <summary>The recorded runtime cases of conformance/runtime (ctsim-runtime/0, live) on the
    /// engine: the two debugger event sequences and TaskNew's argument check, each in a
    /// Simulation of ExampleSA (where they were recorded) through its IDebugTarget.</summary>
    public class DebuggerConformanceTests
    {
        private static string RepoRoot()
        {
            string d = AppContext.BaseDirectory;
            while (d != null && !File.Exists(Path.Combine(d, "LICENSE")))
                d = Path.GetDirectoryName(d)!;
            return d ?? throw new DirectoryNotFoundException("repo root");
        }

        private static JsonValue Case(string rel) =>
            JsonValue.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "conformance", "runtime", rel)));

        /// <summary>The thread number of an announce replaced by T.</summary>
        private static string NoThread(string s) => Regex.Replace(s, @"^Thread \S+ ", "Thread T ");

        [ProjectFact]
        public void HaltingErrorIsReportedThenTheTaskEnds()
        {
            var c = Case("debugger/hwerror_259_array_overrun_halts.json");
            Assert.Equal("ctsim-runtime/0", c["format"].Str);
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            {
                var ev = new SimEvents(sim);
                sim.Debugger.Attach(); // the setup: a debugger attached
                Assert.True(sim.Debugger.Options.BreakOnError); // the setup: BreakOnError=1, ErrSet level 0
                var caller = sim.Invoke(() => sim.StartCall(c["trigger"].Str, null));
                var err = ev.Wait<HardwareErrorEventArgs>();
                var ipc = c["ipc"].Items;
                // 0x100B announce, 0x1002 line, 0x100E code + DESC / ERRPAGE / ERRDESC
                Assert.Equal(NoThread(ipc[0]["text"].Str), NoThread(err.Announce));
                Assert.Equal((int)ipc[1]["line"].Long, err.Line);
                Assert.Equal((int)ipc[2]["code"].Long, err.Code);
                Assert.Equal(ipc[2]["desc"].Str, err.Text);
                Assert.Equal(ipc[2]["errpage"].Str.Trim(), err.ErrPage);
                Assert.Equal(ipc[2]["errdesc"].Str, err.ErrDesc);
                Assert.EndsWith("graph.ci", err.File, StringComparison.OrdinalIgnoreCase);
                Assert.True(err.Halting);
                // then nothing: the task halts at once (suspended false), no stop
                var exit = ev.WaitExit(err.Task);
                Assert.Equal(TaskState.Halt, exit.State);
                Assert.Equal(259, exit.HaltCode);
                Assert.False(c["suspended"].Bool);
                sim.Events.Flush();
                Assert.Empty(ev.Of<StoppedEventArgs>());
                Assert.DoesNotContain(sim.Debugger.Tasks(), t => t.Id == err.Task);
                // the control: TaskNew("_PltLineStyleGet", "1", 0) gives a handle and no events
                int before = ev.Of<HardwareErrorEventArgs>().Count;
                var ok = sim.Invoke(() => sim.StartCall("TaskNew(\"_PltLineStyleGet\", \"1\", 0)", null));
                SimTestUtil.Until(sim, () => ok.Done && sim.Scheduler.Tasks.Count == 0);
                Assert.True(ok.Result!.Value.I > 0);
                sim.Events.Flush();
                Assert.Equal(before, ev.Of<HardwareErrorEventArgs>().Count);
            }
        }

        [ProjectFact]
        public void NonHaltingErrorStopsAtTheNextStatement()
        {
            var c = Case("debugger/hwerror_424_in_builtin_continues.json");
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            {
                var ev = new SimEvents(sim);
                sim.Debugger.Attach(); // the setup: a debugger attached
                sim.Invoke(() => sim.StartCall(c["trigger"].Str, null));
                var err = ev.Wait<HardwareErrorEventArgs>();
                var ipc = c["ipc"].Items;
                Assert.Equal(NoThread(ipc[0]["text"].Str), NoThread(err.Announce));
                Assert.Equal((int)ipc[1]["line"].Long, err.Line); // 23
                Assert.Equal(424, err.Code);
                Assert.Equal(ipc[2]["desc"].Str, err.Text);
                Assert.Equal(ipc[2]["errpage"].Str, err.ErrPage); // TagRead
                Assert.Equal(ipc[2]["errdesc"].Str, err.ErrDesc); // GetTagVal
                Assert.False(err.Halting);
                // the task is suspended alive at the next statement (0x1002 line 24)
                var stop = ev.Wait<StoppedEventArgs>();
                Assert.Equal(StopReason.Exception, stop.Reason);
                Assert.Equal(424, stop.Code);
                Assert.True(c["suspended"].Bool);
                var frames = sim.Debugger.StackTrace(stop.Task);
                Assert.Equal("GetTagVal", frames[0].Function);
                Assert.Equal((int)ipc[3]["line"].Long, frames[0].Line); // 24
                // locals readable while stopped
                var scopes = sim.Debugger.Scopes(frames[0].Id);
                var locals = sim.Debugger.Variables(scopes.First(s => s.Name == "Locals").VariablesReference);
                Assert.Equal(
                    "\"NoSuchTagXyz\"",
                    locals.First(v => v.Name.Equals("sTag", StringComparison.OrdinalIgnoreCase)).Value
                );
                // a step lands on a later line; continue resumes and the task ends
                sim.Debugger.Step(stop.Task, StepKind.Over);
                var step = ev.Wait<StoppedEventArgs>();
                Assert.Equal(StopReason.Step, step.Reason);
                Assert.True(sim.Debugger.StackTrace(step.Task)[0].Line > 24);
                Assert.Throws<ArgumentException>(() => sim.Debugger.Scopes(frames[0].Id)); // ids die when the task runs on
                sim.Debugger.Continue(step.Task);
                var exit = ev.WaitExit(step.Task);
                Assert.Equal(TaskState.Stop, exit.State);
            }
        }

        [ProjectFact]
        public void ErrSetAboveZeroReportsNothing()
        {
            // the same 424 inside ErrSet(1): the runtime logs 'SUPPRESSED hardware alarm', a
            // debugger gets nothing (only an error at level 0 reaches it)
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            {
                var ev = new SimEvents(sim);
                sim.Debugger.Attach(); // the setup: a debugger attached
                var t = sim.Invoke(() =>
                {
                    var task = sim.Vm.TaskForCall("GetTagVal", new[] { Value.Str("NoSuchTagXyz") });
                    task.ErrSetLevel = 1;
                    sim.Scheduler.Add(task);
                    return task;
                });
                ev.WaitExit(t.Handle);
                sim.Events.Flush();
                Assert.Empty(ev.Of<HardwareErrorEventArgs>());
                Assert.Empty(ev.Of<StoppedEventArgs>());
                Assert.Contains(ev.Logs, l =>
                    l.Text.StartsWith("SUPPRESSED hardware error (424)", StringComparison.Ordinal)
                );
            }
        }

        [ProjectFact]
        public void WithNoDebuggerANonHaltingErrorDoesNotStopTheTask()
        {
            // BreakOnError is the debugger session's: with no debugger attached (a viewer-only
            // server, ctgedit's Simulate tab) the 424 of TagRead is logged and the task runs on
            // to its end (GetTagVal returns "0"); it is never parked waiting for a Continue
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            {
                var ev = new SimEvents(sim);
                Assert.False(sim.Debugger.Attached);
                var done = new System.Threading.ManualResetEventSlim(false);
                CallOutcome? outcome = null;
                sim.Invoke(() => sim.StartCall("GetTagVal(\"NoSuchTagXyz\")", o =>
                    {
                        outcome = o;
                        done.Set();
                    }));
                Assert.True(done.Wait(10000), "the task was stopped with no debugger to continue it");
                Assert.Equal("stop", outcome!.State);
                Assert.Equal("0", outcome.Result);
                sim.Events.Flush();
                Assert.Empty(ev.Of<HardwareErrorEventArgs>());
                Assert.Empty(ev.Of<StoppedEventArgs>());
                Assert.Contains(ev.Logs, l =>
                    l.Text.StartsWith("Hardware error (424)", StringComparison.Ordinal)
                );
                // attached, the same call stops at the next statement; detached again, it runs on
                sim.Debugger.Attach();
                var t = sim.Invoke(() => sim.StartCall("GetTagVal(\"NoSuchTagXyz\")", null));
                Assert.Equal(StopReason.Exception, ev.Wait<StoppedEventArgs>().Reason);
                sim.Debugger.Detach();
                Assert.Equal(TaskState.Stop, ev.WaitExit(t.Handle).State);
                Assert.False(sim.Debugger.Attached);
            }
        }

        [ProjectFact]
        public void TaskNewChecksTheArgumentTypesItself()
        {
            // conformance/runtime/ctapi/tasknew_int_param_non_numeric.json: -1, the kernel
            // message, no task, no hardware error, no debugger event
            var c = Case("ctapi/tasknew_int_param_non_numeric.json");
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            {
                var ev = new SimEvents(sim);
                var done = new System.Threading.ManualResetEventSlim(false);
                CallOutcome? outcome = null;
                sim.Invoke(() => sim.StartCall(c["call"].Str, o =>
                    {
                        outcome = o;
                        done.Set();
                    }));
                Assert.True(done.Wait(10000));
                Assert.Equal("stop", outcome!.State);
                Assert.Equal(c["result"].Str, outcome.Result);
                Assert.Equal(0, outcome.Error);
                sim.Events.Flush();
                Assert.Contains(ev.Logs, l => l.Text == c["kernelOutput"].Str);
                Assert.Empty(ev.Of<HardwareErrorEventArgs>());
                Assert.Empty(ev.Of<StoppedEventArgs>());
                Assert.Single(
                    ev.All.FindAll(o => o is ValueTuple<string, TaskEventArgs> p && p.Item1 == "started")
                ); // the call's own task only
                // the control: a handle
                CallOutcome? ok = null;
                var done2 = new System.Threading.ManualResetEventSlim(false);
                sim.Invoke(() => sim.StartCall(c["control"]["call"].Str, o =>
                    {
                        ok = o;
                        done2.Set();
                    }));
                Assert.True(done2.Wait(10000));
                Assert.True(long.Parse(ok!.Result) > 0);
            }
        }
    }
}
