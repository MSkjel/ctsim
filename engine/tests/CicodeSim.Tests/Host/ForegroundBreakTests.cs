using System;
using System.Linq;
using CicodeSim.Sim;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Host
{
    /// <summary>Page code the debugger stops.</summary>
    public class ForegroundBreakTests
    {
        private static int LineOf(string text)
        {
            var lines = System.IO.File.ReadAllLines(System.IO.Path.Combine(SimCorpus.Example, "Alarms.ci"));
            for (int k = 0; k < lines.Length; k++)
                if (lines[k].Contains(text))
                    return k + 1;
            throw new InvalidOperationException("Alarms.ci has no line with " + text);
        }

        [ProjectFact]
        public void ABreakpointInAFunctionAnExpressionCallsHoldsTheSimulation()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                var ev = new SimEvents(sim);
                var paused = new System.Collections.Concurrent.ConcurrentQueue<JsonValue>();
                sim.DebugPaused += b => paused.Enqueue(b);
                Assert.True(sim.Invoke(() => sim.ForegroundBreak));
                sim.Debugger.Attach();
                int line = LineOf("AlmFilter = \"Equipment=\" + EquipName;");
                string file = System.IO.Path.Combine(SimCorpus.Example, "Alarms.ci");
                var bp = sim.Debugger.SetBreakpoints(file, new[] { new BreakpointRequest { Line = line } });
                Assert.True(bp.Single().Verified);
                // the page opens and its first scan evaluates the expressions: posted, the
                // simulation thread is held in the scan (an Invoke would wait for the Continue)
                sim.Post(() => sim.Pages.OpenForScenario(0, "Mixer"));
                var stop = ev.Wait<StoppedEventArgs>();
                Assert.Equal(StopReason.Breakpoint, stop.Reason);
                SpinUntil(() => sim.HeldBy != null); // (Stopped is raised as it parks)
                Assert.Equal(stop.Task, sim.HeldBy!.Handle);
                // the debugger's requests are served while the scan waits
                var frames = sim.Debugger.StackTrace(stop.Task);
                Assert.Equal("AlarmCountByEquipment", frames[0].Function, ignoreCase: true);
                Assert.Equal(line, frames[0].Line);
                var scopes = sim.Debugger.Scopes(frames[0].Id);
                Assert.NotEmpty(scopes);
                // ... the other clients' commands wait for it
                Assert.Throws<TimeoutException>(() => sim.Invoke(() => 0, 300));
                // the viewers were told, with the page object
                SpinUntil(() => !paused.IsEmpty);
                paused.TryPeek(out var first);
                Assert.True(first!["paused"].Bool);
                Assert.Equal(0, (int)first["window"].Long);
                Assert.True(first["an"].Long > 0);
                Assert.Equal("Mixer", first["page"].Str, ignoreCase: true);
                // a step stays in the held scan, on the next line
                sim.Debugger.Step(stop.Task, StepKind.Over);
                var step = ev.Wait<StoppedEventArgs>(s => s.Reason == StopReason.Step);
                Assert.Equal(line + 1, sim.Debugger.StackTrace(step.Task)[0].Line);
                // no breakpoint any more, a step out of the function: back in the expression the
                // stop ends (page code is not parked as the runtime parks a task stepped off its
                // outermost function: that would hold the simulation with nothing shown), the
                // scan goes on and the simulation answers again
                sim.Debugger.SetBreakpoints(file, Array.Empty<BreakpointRequest>());
                sim.Debugger.Step(step.Task, StepKind.Out);
                SimTestUtil.Until(sim, () =>
                    sim.HeldBy == null && sim.World.Display.OpenWindow(0)?.Page != null
                );
                SpinUntil(() => paused.Any(b => !b["paused"].Bool));
                sim.Events.Flush();
                Assert.DoesNotContain(ev.Of<ValueTuple<string, TaskEventArgs>>(), p => p.Item1 == "parked");
            }
        }

        [ProjectFact]
        public void ContinueEndsTheHold()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                var ev = new SimEvents(sim);
                sim.Debugger.Attach();
                int line = LineOf("AlmFilter = \"Equipment=\" + EquipName;");
                string file = System.IO.Path.Combine(SimCorpus.Example, "Alarms.ci");
                sim.Debugger.SetBreakpoints(file, new[] { new BreakpointRequest { Line = line } });
                sim.Post(() => sim.Pages.OpenForScenario(0, "Mixer"));
                ev.Wait<StoppedEventArgs>();
                SpinUntil(() => sim.HeldBy != null);
                sim.Debugger.SetBreakpoints(file, Array.Empty<BreakpointRequest>());
                sim.Debugger.Continue(null);
                SimTestUtil.Until(sim, () =>
                    sim.HeldBy == null && sim.World.Display.OpenWindow(0)?.Page != null
                );
            }
        }

        [ProjectFact]
        public void WithForegroundBreakOffPageCodeKeepsTheRuntimesRule()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                var ev = new SimEvents(sim);
                sim.Invoke(() => sim.ForegroundBreak = false);
                sim.Debugger.Attach();
                int line = LineOf("AlmFilter = \"Equipment=\" + EquipName;");
                string file = System.IO.Path.Combine(SimCorpus.Example, "Alarms.ci");
                sim.Debugger.SetBreakpoints(file, new[] { new BreakpointRequest { Line = line } });
                sim.Invoke(() => sim.Pages.OpenForScenario(0, "Mixer"));
                // notice 343 'Foreground Cicode cannot break', once; no stop, nothing held
                var notice = ev.Wait<HardwareErrorEventArgs>(e => e.Code == 343);
                Assert.Equal("Foreground Cicode cannot break", notice.Text);
                sim.Events.Flush();
                Assert.Empty(ev.Of<StoppedEventArgs>());
                Assert.Null(sim.HeldBy);
            }
        }

        [ProjectFact]
        public void AHardwareErrorInPageCodeDoesNotHoldTheSimulationUnlessAsked()
        {
            // ExampleSA's Mixing_L3: its expressions raise hardware errors at
            // once (SA's DspAnGetMetadata 289 in a visibility expression)
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            {
                var ev = new SimEvents(sim);
                sim.Debugger.Attach();
                Assert.True(sim.Invoke(() => sim.ForegroundBreak));
                Assert.False(sim.Invoke(() => sim.ForegroundErrorBreak));
                Assert.True(sim.Stepper.Options.BreakOnError);
                sim.Post(() => sim.Pages.OpenForScenario(0, "Mixing_L3"));
                SimTestUtil.Until(sim, () => sim.World.Display.OpenWindow(0)?.Page != null, what: "the page");
                sim.Events.Flush();
                Assert.Null(sim.HeldBy);
                Assert.DoesNotContain(ev.Of<StoppedEventArgs>(), s => s.Task == -1);
                // asked for: the page's first hardware error stops its code
                sim.Invoke(() => sim.ForegroundErrorBreak = true);
                sim.Post(() => sim.Pages.OpenForScenario(0, "Mixing_L3"));
                var stop = ev.Wait<StoppedEventArgs>(s => s.Reason == StopReason.Exception);
                SpinUntil(() => sim.HeldBy != null);
                Assert.True(sim.HeldBy!.Foreground);
                // (a change takes effect at once: the held code runs on to its end)
                sim.Stepper.Options.ForegroundErrorBreak = false;
                sim.Debugger.Continue(null);
                SimTestUtil.Until(sim, () => sim.HeldBy == null, what: "the end of the hold");
            }
        }

        private static void SpinUntil(Func<bool> cond, int timeoutMs = 5000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!cond())
            {
                if (sw.ElapsedMilliseconds > timeoutMs)
                    throw new TimeoutException("the event did not come");
                System.Threading.Thread.Sleep(10);
            }
        }
    }
}
