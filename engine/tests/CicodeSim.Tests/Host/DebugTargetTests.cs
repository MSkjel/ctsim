using System;
using System.IO;
using System.Linq;
using CicodeSim.Sim;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Host
{
    /// <summary>The debug target over a Simulation of the R2 Example project: a breakpoint on a
    /// real compiled function line (Simulatr.ci Temp_Profile_Sim -> Ramp_Temp_Profile), step
    /// into / over / out, locals, module variables, evaluate, set variable, detach.</summary>
    public class DebugTargetTests
    {
        private static string Simulatr => Path.Combine(SimCorpus.Example, "Simulatr.ci");

        [ProjectFact]
        public void BreakpointStepsAndLocalsOnARealExampleFunction()
        {
            // paused: no simulated time passes while the test looks (SysTime stands still); the
            // ramp's SleepMS(500) loop runs on a clock step
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                var ev = new SimEvents(sim);
                var d = sim.Debugger;
                // Simulatr.ci line 274 'Ramp_Temp_Profile( 6, 100);' in Temp_Profile_Sim; a path of
                // another install names the same file: project folder + file name
                var bp = d.SetBreakpoints(
                        @"C:\ProgramData\AVEVA Plant SCADA 2023\User\EXAMPLE\simulatr.ci",
                        new[] { new BreakpointRequest { Line = 273 } }
                    )
                    .Single();
                Assert.True(bp.Verified, bp.Message);
                Assert.Equal(274, bp.Line); // the next Line marker
                var task = sim.Invoke(() => sim.StartCall("Temp_Profile_Sim()", null));
                var stop = ev.Wait<StoppedEventArgs>();
                Assert.Equal(StopReason.Breakpoint, stop.Reason);
                Assert.Equal(new[] { bp.Id }, stop.HitBreakpoints.ToArray());
                Assert.Equal(task.Handle, stop.Task);
                var info = d.Tasks().Single(t => t.Id == task.Handle);
                Assert.True(
                    info.Paused && !info.Parked && info.Function == "Temp_Profile_Sim" && info.Line == 274
                );
                var frames = d.StackTrace(stop.Task);
                Assert.Equal("Temp_Profile_Sim", frames[0].Function);
                Assert.Equal(274, frames[0].Line);
                Assert.Equal(Simulatr, frames[0].File, ignoreCase: true);
                // the module variable the function set at line 272
                var scopes = d.Scopes(frames[0].Id);
                Assert.Equal(new[] { "Locals", "Module", "Globals" }, scopes.Select(s => s.Name).ToArray()); // no tag in Temp_Profile_Sim
                var module = d.Variables(scopes[1].VariablesReference);
                Assert.Equal("1", module.Single(v => v.Name == "Temp_Profile_Running").Value);
                Assert.Equal(
                    "1",
                    d.Evaluate("Temp_Profile_Running", frames[0].Id, EvaluateContext.Hover).Result
                );

                // step into Ramp_Temp_Profile( 6, 100)
                d.Step(stop.Task, StepKind.Into);
                stop = ev.Wait<StoppedEventArgs>();
                Assert.Equal(StopReason.Step, stop.Reason);
                frames = d.StackTrace(stop.Task);
                Assert.Equal(
                    new[] { "Ramp_Temp_Profile", "Temp_Profile_Sim" },
                    frames.Select(f => f.Function).ToArray()
                );
                Assert.Equal(290, frames[0].Line); // the function's first Line marker (its header line)
                var tags = d.Variables(
                    d.Scopes(frames[0].Id).First(s => s.Name == "Tags").VariablesReference
                );
                Assert.Equal("Temp_Profile", tags.Single().Name);
                var locals = d.Variables(
                    d.Scopes(frames[0].Id).First(s => s.Name == "Locals").VariablesReference
                );
                Assert.Equal(
                    new[] { "rRampTime", "RampTo", "iStart", "iTimeExp", "iRampValue", "iRampFrom" },
                    locals.Select(v => v.Name).ToArray()
                );
                Assert.Equal("6", locals[0].Value);
                Assert.Equal("REAL", locals[0].Type);
                Assert.Equal("100", locals[1].Value);
                Assert.Equal("INT", locals[1].Type);
                // paged
                var page = d.Variables(
                    d.Scopes(frames[0].Id).First(s => s.Name == "Locals").VariablesReference,
                    1,
                    2
                );
                Assert.Equal(new[] { "RampTo", "iStart" }, page.Select(v => v.Name).ToArray());

                // step over three times: 295 (iStart = SysTime()), 297, then 298 with rRampTime = 6000
                d.Step(stop.Task, StepKind.Over);
                stop = ev.Wait<StoppedEventArgs>();
                Assert.Equal(295, d.StackTrace(stop.Task)[0].Line);
                d.Step(stop.Task, StepKind.Over);
                stop = ev.Wait<StoppedEventArgs>();
                Assert.Equal(297, d.StackTrace(stop.Task)[0].Line);
                d.Step(stop.Task, StepKind.Over);
                stop = ev.Wait<StoppedEventArgs>();
                frames = d.StackTrace(stop.Task);
                Assert.Equal(298, frames[0].Line);
                var e = d.Evaluate("rRampTime / 1000 + RampTo", frames[0].Id, EvaluateContext.Watch);
                Assert.Equal("", e.ErrorText);
                Assert.Equal("106", e.Result);
                Assert.Contains(
                    "a hover reads names only",
                    d.Evaluate("IntToStr(5)", frames[0].Id, EvaluateContext.Hover).ErrorText
                );
                Assert.Equal("\"5\"", d.Evaluate("IntToStr(5)", frames[0].Id, EvaluateContext.Repl).Result);
                Assert.Contains("unknown name", d.Evaluate("NoSuchName", frames[0].Id).ErrorText);
                // the simulator can change a variable: RampTo 100 -> 50
                var lref = d.Scopes(frames[0].Id).First(s => s.Name == "Locals").VariablesReference;
                var set = d.SetVariable(lref, "RampTo", "50");
                Assert.Equal("50", set.Value);
                Assert.Throws<ArgumentException>(() => d.SetVariable(lref, "RampTo", "\"abc\""));
                Assert.Throws<ArgumentException>(() => d.SetVariable(lref, "NoSuchLocal", "1"));

                // step out: back in Temp_Profile_Sim at the call's line (the ramp's loop runs out first)
                d.Step(stop.Task, StepKind.Out);
                Assert.Equal(7000, sim.Invoke(() => sim.StepClock(7000)));
                stop = ev.Wait<StoppedEventArgs>();
                frames = d.StackTrace(stop.Task);
                Assert.Equal("Temp_Profile_Sim", frames[0].Function);
                Assert.Equal(274, frames[0].Line);
                Assert.Single(frames);
                var tp = d.Evaluate("Temp_Profile", frames[0].Id);
                Assert.True(
                    tp.ErrorText == "" && int.Parse(tp.Result.Split(' ')[0]) > 0,
                    "Temp_Profile = " + tp.Result + " " + tp.ErrorText
                ); // the ramp wrote the tag

                // detach: breakpoints cleared, the task runs on to its end
                d.Detach();
                sim.Invoke(() => sim.StepClock(60_000)); // the rest of Temp_Profile_Sim: SleepMS(6000) ...
                var exit = ev.WaitExit(task.Handle);
                Assert.Equal(TaskState.Stop, exit.State);
            }
        }

        [ProjectFact]
        public void ConditionsLogPointsAndUnverifiedFiles()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                var ev = new SimEvents(sim);
                var d = sim.Debugger;
                // Ramp_Temp_Profile line 304 (Temp_Profile = iRampFrom + iRampValue) in the loop: the loop branches past
                // the WHILE line 302 marker, so 302 is hit once
                d.SetBreakpoints(
                    Simulatr,
                    new[]
                    {
                        new BreakpointRequest { Line = 304, Condition = "iRampValue > 50" },
                        new BreakpointRequest
                        {
                            Line = 295,
                            LogMessage = "ramp to {RampTo} in {rRampTime} s",
                        },
                    }
                );
                var none = d.SetBreakpoints(
                        @"C:\x\NoSuchProject\Simulatr.ci",
                        new[] { new BreakpointRequest { Line = 10 } }
                    )
                    .Single();
                Assert.False(none.Verified);
                Assert.Contains("not in the compiled project", none.Message);
                var task = sim.Invoke(() => sim.StartCall("Ramp_Temp_Profile(5, 100)", null));
                sim.Invoke(() => sim.StepClock(6000));
                var log = ev.Wait<OutputEventArgs>(o => o.Text.StartsWith("ramp", StringComparison.Ordinal));
                Assert.Equal("ramp to 100 in 5 s\n", log.Text);
                var stop = ev.Wait<StoppedEventArgs>();
                var frames = d.StackTrace(stop.Task);
                Assert.Equal(304, frames[0].Line);
                int v = int.Parse(d.Evaluate("iRampValue", frames[0].Id).Result);
                Assert.True(v > 50, "iRampValue " + v);
                d.SetBreakpoints(Simulatr, Array.Empty<BreakpointRequest>());
                d.Continue(null);
                sim.Invoke(() => sim.StepClock(6000));
                Assert.Equal(TaskState.Stop, ev.WaitExit(task.Handle).State);
            }
        }
    }
}
