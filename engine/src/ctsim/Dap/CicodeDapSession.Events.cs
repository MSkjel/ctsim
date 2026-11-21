using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using CicodeSim.Sim;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Host.Dap
{
    /// <summary>The target's events as DAP events (raised on the simulation's event thread).</summary>
    internal sealed partial class CicodeDapSession
    {
        private void Subscribe()
        {
            _dbg.Stopped += OnStopped;
            _dbg.Continued += OnContinued;
            _dbg.Parked += OnParked;
            _dbg.BreakpointChanged += OnBreakpointChanged;
            _dbg.ErrorReported += OnErrorReported;
            _dbg.TaskStarted += OnTaskStarted;
            _dbg.TaskExited += OnTaskExited;
            _dbg.Output += OnOutput;
            _dbg.Terminated += OnTerminated;
            _sim.Log += OnLog;
            _sim.DebugPaused += OnDebugPaused;
        }

        private void Unsubscribe()
        {
            _dbg.Stopped -= OnStopped;
            _dbg.Continued -= OnContinued;
            _dbg.Parked -= OnParked;
            _dbg.BreakpointChanged -= OnBreakpointChanged;
            _dbg.ErrorReported -= OnErrorReported;
            _dbg.TaskStarted -= OnTaskStarted;
            _dbg.TaskExited -= OnTaskExited;
            _dbg.Output -= OnOutput;
            _dbg.Terminated -= OnTerminated;
            _sim.Log -= OnLog;
            _sim.DebugPaused -= OnDebugPaused;
        }

        private bool Closed => Volatile.Read(ref _closed) != 0;

        private void OnStopped(object? sender, StoppedEventArgs e)
        {
            if (Closed)
                return;
            DropViews();
            string reason;
            switch (e.Reason)
            {
                case StopReason.Breakpoint: reason = "breakpoint"; break;
                case StopReason.DataBreakpoint: reason = "data breakpoint"; break;
                case StopReason.Step: reason = "step"; break;
                case StopReason.Exception: reason = "exception"; break;
                case StopReason.Entry: reason = "entry"; break;
                default: reason = "pause"; break;
            }
            // stopOnEntry is a pause asked for before the call's first slice
            bool first = Interlocked.CompareExchange(ref _entryTask, 0, e.Task) == e.Task;
            if (first && e.Reason == StopReason.Pause)
                reason = "entry";
            var b = JsonValue.NewObject();
            b.Set("reason", reason);
            b.Set("threadId", JsonValue.Of(e.Task));
            b.Set("allThreadsStopped", JsonValue.Of(e.AllTasksStopped));
            if (e.HitBreakpoints.Count > 0)
            {
                var ids = JsonValue.NewArray();
                foreach (int id in e.HitBreakpoints)
                    ids.Add(JsonValue.Of(id));
                b.Set("hitBreakpointIds", ids);
            }
            if (e.Code != 0)
            {
                // a non-halting hardware error: stopped alive at the statement after it
                DebugFrame? top = null;
                try
                {
                    var frames = _dbg.StackTrace(e.Task);
                    if (frames.Count > 0)
                        top = frames[0];
                }
                catch (Exception x) when (x is ArgumentException || x is ObjectDisposedException)
                {
                }
                string announce;
                lock (_lock)
                {
                    if (!_errors.TryGetValue(e.Task, out var info) || info.Code != e.Code)
                        _errors[e.Task] = info = new ErrorInfo { Code = e.Code };
                    if (e.Text.Length > 0)
                        info.Text = e.Text;
                    info.PauseLine = top?.Line ?? 0;
                    info.Stopped = true;
                    // the stop is in the function that failed, not in a caller it returned to
                    info.Pin = top != null && info.Line > 0 && info.File.Length > 0 &&
                               string.Equals(top.Function, info.Function, StringComparison.OrdinalIgnoreCase) &&
                               string.Equals(top.File, info.File, StringComparison.OrdinalIgnoreCase);
                    announce = info.Announce;
                }
                b.Set("description", e.Reason == StopReason.Exception ? "Cicode hardware error" : "Breakpoint, after Cicode hardware error " + Num(e.Code));
                b.Set("text", announce.Length > 0 ? announce : "Hardware error (" + Num(e.Code) + ") '" + e.Text + "'");
            }
            else if (e.Reason == StopReason.Breakpoint && e.HitBreakpoints.Count > 0)
                ConditionFailed(e.Task, e.HitBreakpoints[0]);
            Event("stopped", b);
        }

        /// <summary>The target stops on a breakpoint whose condition fails to evaluate (an
        /// unknown name ...): say why on the console. The condition is read again as a hover,
        /// which resolves names as the condition did; one with a call is not run twice.</summary>
        private void ConditionFailed(int task, int id)
        {
            var bp = FindBreakpoint(id);
            if (bp == null || bp.Condition == null || bp.ConditionCalls)
                return;
            try
            {
                var frames = _dbg.StackTrace(task);
                if (frames.Count == 0)
                    return;
                var r = _dbg.Evaluate(bp.Condition, frames[0].Id, EvaluateContext.Hover);
                // a value (also one with an operator error) is what the condition gave
                if (r.Result.Length > 0 || (r.Error == 0 && r.ErrorText.Length == 0))
                    return;
                string why = r.ErrorText.Length > 0 ? r.ErrorText : "error " + Num(r.Error) + " " + Errors.Title(r.Error);
                Output("console", "Breakpoint condition " + Quote(bp.Condition) + " cannot be evaluated (" + why + "): the task stops there\n",
                       frames[0].File, frames[0].Line);
            }
            catch (Exception x) when (x is ArgumentException || x is ObjectDisposedException)
            {
                // the task has run on meanwhile
            }
        }

        private void OnContinued(object? sender, ContinuedEventArgs e)
        {
            if (Closed)
                return;
            DropViews();
            lock (_lock)
            {
                if (e.AllTasks)
                    _errors.Clear();
                else
                    _errors.Remove(e.Task);
            }
            var b = JsonValue.NewObject();
            b.Set("threadId", JsonValue.Of(e.Task));
            b.Set("allThreadsContinued", JsonValue.Of(e.AllTasks));
            Event("continued", b);
        }

        /// <summary>A step left the outermost function: the runtime parks the task with no
        /// event; the real adapter resumes it so it ends (DebugClient.cs OnStepProbeStack).</summary>
        private void OnParked(object? sender, TaskEventArgs e)
        {
            if (Closed)
                return;
            try
            {
                _dbg.Continue(e.Task);
            }
            catch (Exception x) when (x is ArgumentException || x is ObjectDisposedException)
            {
            }
        }

        private void OnBreakpointChanged(object? sender, BreakpointEventArgs e)
        {
            if (Closed)
                return;
            var b = JsonValue.NewObject();
            b.Set("reason", e.Reason);
            b.Set("breakpoint", BreakpointJson(e.Breakpoint));
            Event("breakpoint", b);
        }

        private void OnErrorReported(object? sender, HardwareErrorEventArgs e)
        {
            if (Closed)
                return;
            string fn = e.ErrDesc.Length > 0 ? e.ErrDesc : e.Function;
            if (e.Notice)
            {
                // notice 343: a breakpoint hit by foreground code, reported once a session
                Output("console", "[Cicode] " + e.Text + ": thread " + Num(e.Task) + (fn.Length > 0 ? ", " + fn : "") +
                       (e.File.Length > 0 ? " at " + LocalPath(e.File) + ":" + Num(e.Line) : "") +
                       " (foreground Cicode is never suspended; the runtime reports this once per session)\n", e.File, e.Line);
                return;
            }
            // page code stops on its errors only with both options (the pageCodeError filter)
            bool neverStops = e.Foreground && !(_dbg.Options.ForegroundCanBreak && _dbg.Options.ForegroundErrorBreak);
            if (!e.Halting && !neverStops)
                lock (_lock)
                    _errors[e.Task] = new ErrorInfo
                    {
                        Code = e.Code, Text = e.Text, Announce = e.Announce, ErrPage = e.ErrPage, ErrDesc = fn, Function = e.Function,
                        File = e.File, Line = e.Line,
                    };
            string? note = e.Halting ? "the task halts"
                : !neverStops ? null
                : _dbg.Options.ForegroundCanBreak ? "page code runs on; the 'Page code hardware errors' filter stops it"
                : "foreground Cicode is never suspended";
            string? reason;
            lock (_lock)
                if (_haltReasons.TryGetValue(e.Task, out reason))
                    _haltReasons.Remove(e.Task);
            Output("stderr", HardwareErrorText(e.Code, e.Text, e.Task, fn, e.ErrPage, e.File, e.Line, note) + "\n" +
                             (reason != null ? "  " + reason + "\n" : ""), e.File, e.Line);
        }


        /// <summary>When the target reports nothing (the hardwareError filter off, Run Without
        /// Debugging, or no debugger attached) the error is still written to the console and
        /// the task runs on (the real adapter's 'log only').</summary>
        private void OnLog(LogEntry e)
        {
            if (Closed || e.Kind != "hardware" || e.Level == "info")
                return;
            string? reason = e.HaltReason.Length > 0 ? e.HaltReason : null;
            if (_dbg.Options.BreakOnError && _sim.Debugger.Attached)
            {
                // ErrorReported writes the error (it comes after this line); the reason a task
                // halts beyond its code (a function that did not compile) is only on the log entry
                if (reason != null)
                    lock (_lock)
                        _haltReasons[e.Task] = reason;
                return;
            }
            if (e.Code == 0)
            {
                Output("stderr", e.Text + "\n" + (reason != null ? "  " + reason + "\n" : ""), e.File, e.Line);
                return;
            }
            // from the entry's fields (its text may change): the built-in that raised it is the
            // name of its call ('TagGetProperty("X", "Custom1", 0)')
            int paren = e.Call.IndexOf('(');
            string errPage = paren > 0 ? e.Call.Substring(0, paren).Trim() : e.Call;
            Output("stderr", HardwareErrorText(e.Code, Errors.Title(e.Code), e.Task, e.Function, errPage, e.File, e.Line, e.Halts ? "the task halts" : null) + "\n" +
                             (reason != null ? "  " + reason + "\n" : ""), e.File, e.Line);
        }

        private void OnTaskStarted(object? sender, TaskEventArgs e)
        {
            if (Closed)
                return;
            var b = JsonValue.NewObject();
            b.Set("reason", "started");
            b.Set("threadId", JsonValue.Of(e.Task));
            Event("thread", b);
        }

        private void OnTaskExited(object? sender, TaskEventArgs e)
        {
            if (Closed)
                return;
            lock (_lock)
                _errors.Remove(e.Task);
            Interlocked.CompareExchange(ref _entryTask, 0, e.Task);
            var b = JsonValue.NewObject();
            b.Set("reason", "exited");
            b.Set("threadId", JsonValue.Of(e.Task));
            Event("thread", b);
        }

        /// <summary>Page code the debugger stopped holds the simulation (ForegroundCanBreak). Its
        /// task sends no TaskStarted / TaskExited: once it is released and has ended, the thread
        /// is said to have exited, so the client does not keep it. A normal command runs after
        /// the scan, so after the code ended or stopped again (a step: then the next release
        /// checks).</summary>
        private void OnDebugPaused(JsonValue e)
        {
            if (Closed || !(e.Get("task") is JsonValue tv) || !tv.IsNumber)
                return;
            int task = (int)tv.Long;
            if (e.Get("paused") is JsonValue p && p.Kind == JsonKind.Bool && p.Bool)
            {
                lock (_lock)
                    _heldTasks.Add(task);
                return;
            }
            lock (_lock)
                if (!_heldTasks.Contains(task))
                    return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool alive;
                try
                {
                    alive = _sim.Invoke(() =>
                    {
                        foreach (var t in _dbg.Tasks())
                            if (t.Id == task)
                                return true;
                        return false;
                    });
                }
                catch (Exception x) when (x is ObjectDisposedException || x is TimeoutException || x is InvalidOperationException)
                {
                    return;
                }
                if (alive || Closed)
                    return;
                lock (_lock)
                {
                    if (!_heldTasks.Remove(task))
                        return;
                    _errors.Remove(task);
                }
                var b = JsonValue.NewObject();
                b.Set("reason", "exited");
                b.Set("threadId", JsonValue.Of(task));
                Event("thread", b);
            });
        }

        private void OnOutput(object? sender, OutputEventArgs e)
        {
            if (Closed || IsCallHaltLog(e))
                return;
            Output(e.Category, e.Text, e.File, e.Line);
        }

        /// <summary>The simulation's 'task N fn halted: reason' line of the launched call: the
        /// call's own ending line says it, once, at this place (it comes first: the log line is
        /// posted before the call's end).</summary>
        private bool IsCallHaltLog(OutputEventArgs e)
        {
            var t = Volatile.Read(ref _call);
            if (t == null || !e.Text.StartsWith("task " + Num(t.Handle) + " ", StringComparison.Ordinal) ||
                e.Text.IndexOf(" halted: ", StringComparison.Ordinal) < 0)
                return false;
            lock (_lock)
            {
                if (_callDone)
                    return false;
                _haltFile = e.File;
                _haltLine = e.Line;
            }
            return true;
        }

        private void OnTerminated(object? sender, EventArgs e)
        {
            if (Closed)
                return;
            SendTerminated();
        }
    }
}
