using System;
using System.Collections.Generic;

namespace CicodeSim.Vm
{
    /// <summary>Why a task stopped in the debugger (DAP 'stopped' reasons).</summary>
    public enum StopReason
    {
        Breakpoint,
        Step,
        Pause,
        Exception,
        Entry,

        /// <summary>A watched variable was written (SetDataBreakpoints).</summary>
        DataBreakpoint,
    }

    public enum StepKind
    {
        Into,
        Over,
        Out,
    }

    /// <summary>Where an expression is evaluated from (DAP evaluate 'context').</summary>
    public enum EvaluateContext
    {
        /// <summary>A watch expression: names and one call with literal arguments.</summary>
        Watch,

        /// <summary>A hover: names only, never a call, nothing changes (no tag is marked as read).</summary>
        Hover,

        /// <summary>The debug console: names and one call with literal arguments.</summary>
        Repl,

        /// <summary>Copy value: as Watch.</summary>
        Clipboard,
    }

    /// <summary>A task as the debugger lists it (DAP threads).</summary>
    public sealed class DebugTaskInfo
    {
        /// <summary>The task handle (what TaskHnd returns): stable while the task lives; a handle
        /// is used again only after the task ended (past 0x7FFF handles start again at 1).</summary>
        public int Id;
        public string Name = "";

        /// <summary>Run, Wait, Sleep, Halt, Stop, Ready, RunExec, Dead (TaskState.Names).</summary>
        public string State = "";
        public bool Foreground;

        /// <summary>Stopped by the debugger (a breakpoint, step, pause or error stop), or parked.</summary>
        public bool Paused;

        /// <summary>Parked by a step off its outermost function: paused with no Stopped event
        /// (the runtime's behaviour); Continue releases it and it ends.</summary>
        public bool Parked;

        /// <summary>breakpoint / step / pause / exception / parked, '' when running.</summary>
        public string StopReason = "";

        /// <summary>The function it is in (the innermost frame), '' when none.</summary>
        public string Function = "";
        public int Line;
    }

    public sealed class BreakpointRequest
    {
        public int Line;

        /// <summary>A Cicode expression; null = always.</summary>
        public string? Condition;

        /// <summary>'n' or '== n' (the n-th hit), '&gt;= n', '&gt; n', '&lt; n', '&lt;= n',
        /// '% n' (every n-th hit).</summary>
        public string? HitCondition;

        /// <summary>A log point: the message is written to Output (with {expression} parts
        /// evaluated as hovers) and the task does not stop.</summary>
        public string? LogMessage;
    }

    public sealed class BreakpointResult
    {
        public int Id;
        public bool Verified;

        /// <summary>The line the breakpoint really sits on (the next Line marker).</summary>
        public int Line;

        /// <summary>Why it is not verified (or null).</summary>
        public string? Message;

        /// <summary>The file as the client named it in SetBreakpoints.</summary>
        public string File = "";

        /// <summary>The library's file key it matched ('folder\name', SourceFiles.Key), '' when none.</summary>
        internal string Key = "";
    }

    public sealed class DebugFrame
    {
        /// <summary>Valid while the task stays stopped at this stop (see IDebugTarget).</summary>
        public int Id;
        public string Function = "";

        /// <summary>The .ci file (the compiled library's full compile-time path). EMPTY for code
        /// with no source file (a page expression, run-time call text).</summary>
        public string File = "";
        public int Line;

        /// <summary>The byte offset in the function's p-code.</summary>
        public int Pc;
        public int Task;

        /// <summary>The call this frame is (unique across the tasks; History's callId), 0 when
        /// no history is recorded.</summary>
        public long CallId;
    }

    /// <summary>A variable's change in a task's history (IDebugTarget.History): a write, or with
    /// Old empty its value when its call started (a parameter, a local's initial value).</summary>
    public sealed class VariableChange
    {
        public long Seq;
        public int Task;
        public long CallId;
        public string Function = "";

        /// <summary>"local", "module" or "global".</summary>
        public string Scope = "local";
        public string Name = "";

        /// <summary>The array element (flat index), -1 none.</summary>
        public long Index = -1;

        /// <summary>'' for an entry value.</summary>
        public string Old = "";
        public string New = "";
        public string Type = "";
        public bool Entry;
        public int Line;

        /// <summary>Simulated seconds since the start.</summary>
        public double Time;
    }

    /// <summary>A task's history (IDebugTarget.HistoryState).</summary>
    public sealed class HistoryInfo
    {
        public long First;
        public long Last;

        /// <summary>The view's place (a Seq), -1 = live.</summary>
        public long Cursor = -1;
        public int Events;

        /// <summary>Older events were dropped (the ring is full).</summary>
        public bool Truncated;
    }

    /// <summary>Where a time travel move put a task's view (IDebugTarget.StepBack ...).</summary>
    public sealed class HistoryMove
    {
        /// <summary>A Seq, -1 = live.</summary>
        public long Cursor = -1;

        /// <summary>The view is at the start of the recording (nothing earlier).</summary>
        public bool AtStart;

        /// <summary>The view is live again.</summary>
        public bool Live;
        public int Line;
        public string Function = "";
        public string File = "";
    }

    /// <summary>Stop when a variable is written (IDebugTarget.SetDataBreakpoints).</summary>
    public sealed class DataBreakpointRequest
    {
        public string Name = "";

        /// <summary>The function of a local; null = a module or global variable.</summary>
        public string? Function;

        /// <summary>An array element (flat index), -1 = any.</summary>
        public long Index = -1;
        public string? Condition;
        public string? HitCondition;
    }

    public sealed class DebugScope
    {
        /// <summary>Locals, Module, Globals, Tags.</summary>
        public string Name = "";
        public int VariablesReference;
        public bool Expensive;
        public int NamedVariables;
        public int IndexedVariables;
    }

    public sealed class DebugVariable
    {
        public string Name = "";
        public string Value = "";

        /// <summary>INT, REAL, STRING, OBJECT, TIMESTAMP, QUALITY, an array 'INT[8]', a tag
        /// 'tag INT' ...</summary>
        public string Type = "";

        /// <summary>Non-zero: the variable has children (an array: its elements or rows).</summary>
        public int VariablesReference;
        public int NamedVariables;

        /// <summary>The number of elements (rows) of an array; Variables pages them.</summary>
        public int IndexedVariables;

        /// <summary>What Evaluate takes to read this variable again ('LineStyle[3]').</summary>
        public string EvaluateName = "";

        /// <summary>SetVariable refuses it (a tag element that is not writable, an array row).</summary>
        public bool ReadOnly;
    }

    public sealed class EvaluateResult
    {
        public string Result = "";
        public string Type = "";
        public int VariablesReference;
        public int NamedVariables;
        public int IndexedVariables;

        /// <summary>A hardware error the evaluation raised (0 = none).</summary>
        public int Error;

        /// <summary>Why the evaluation failed ('' = it did not): the error's title, an unknown
        /// name, a call refused in a hover, the time budget ...</summary>
        public string ErrorText = "";
    }

    public sealed class StoppedEventArgs : EventArgs
    {
        public int Task;
        public StopReason Reason;

        /// <summary>The hardware error code for an error stop (Exception, or Breakpoint when the
        /// line after the error has one), else 0.</summary>
        public int Code;
        public string Text = "";
        public IReadOnlyList<int> HitBreakpoints = Array.Empty<int>();
        public bool AllTasksStopped;
    }

    public sealed class ContinuedEventArgs : EventArgs
    {
        /// <summary>The task that runs on.</summary>
        public int Task;
        public bool AllTasks;
    }

    public sealed class TaskEventArgs : EventArgs
    {
        public int Task;
        public string Name = "";

        /// <summary>TaskExited: the final state (TaskState Stop / Halt / Dead) and the halt code.</summary>
        public int State;
        public int HaltCode;

        /// <summary>TaskExited: why it halted ('' for a stop): 'hardware error 259', 'built-in
        /// function X is not simulated', 'Halt()' ...</summary>
        public string HaltReason = "";
    }

    public sealed class BreakpointEventArgs : EventArgs
    {
        /// <summary>The breakpoint with its new state (same Id).</summary>
        public BreakpointResult Breakpoint = new BreakpointResult();

        /// <summary>changed (DAP breakpoint event reason).</summary>
        public string Reason = "changed";
    }

    /// <summary>A hardware error report (the runtime's 0x100B announce + 0x1002 line + 0x100E
    /// code / DESC / ERRPAGE / ERRDESC; live: conformance/runtime/debugger/*.json).</summary>
    public sealed class HardwareErrorEventArgs : EventArgs
    {
        public int Task;
        public string TaskName = "";
        public int Code;

        /// <summary>DESC: the error's title ('Array has been overrun').</summary>
        public string Text = "";

        /// <summary>ERRPAGE: the built-in that raised it ('TagRead'), '' for the VM's own errors.</summary>
        public string ErrPage = "";

        /// <summary>ERRDESC: the Cicode function it happened in.</summary>
        public string ErrDesc = "";
        public string Function = "";

        /// <summary>The .ci file, may be empty.</summary>
        public string File = "";
        public int Line;

        /// <summary>The task halts: it ends now (a TaskExited follows), there is no stop.</summary>
        public bool Halting;

        /// <summary>Foreground code: the report only, once until the next Continue(null).</summary>
        public bool Foreground;

        /// <summary>Notice 343 'Foreground Cicode cannot break' (a breakpoint hit in foreground
        /// code, once a session), not an error of the code.</summary>
        public bool Notice;

        /// <summary>"Thread 3 Hardware error (259) 'Array has been overrun' ErrPage: '' ErrDesc: '_PltLineStyleGet'".</summary>
        public string Announce = "";
    }

    public sealed class OutputEventArgs : EventArgs
    {
        /// <summary>console, stdout, stderr (DAP output categories).</summary>
        public string Category = "console";
        public string Text = "";
        public string? File;
        public int Line;
    }

    /// <summary>Debugger behaviour; the defaults are the real runtime's (the extension's
    /// dap/PROTOCOL.md, live). Deviations are explicit options. A change takes effect at once,
    /// also while tasks run (the next Line marker / error reads it).</summary>
    public sealed class DebugOptions
    {
        /// <summary>A step releases every other paused task.</summary>
        public bool StepReleasesOthers = true;

        /// <summary>Stepping off the outermost function parks the task with no event.</summary>
        public bool ParkOnStepOffOutermost = true;

        /// <summary>A hardware error is reported: a halting one, then the task ends; a
        /// non-halting one, then the task stops at its next Line marker (DAP exception filter
        /// hardwareError; the runtime's BreakOnError, set by SESSION_START).</summary>
        public bool BreakOnError = true;

        /// <summary>Foreground code (page expressions) cannot break (notice 343). True = it
        /// breaks like background code (a simulator deviation).</summary>
        public bool ForegroundCanBreak;

        /// <summary>With ForegroundCanBreak, a hardware error in foreground code stops it too.</summary>
        public bool ForegroundErrorBreak;

        /// <summary>The real time an Evaluate may take before it gives up (ms).</summary>
        public int EvaluateTimeoutMs = 2000;
    }

    /// <summary>The engine side of a Cicode debugger, shared with the VS Code extension.</summary>
    public interface IDebugTarget
    {
        DebugOptions Options { get; }

        IReadOnlyList<DebugTaskInfo> Tasks();

        /// <summary>Replace the breakpoints of one .ci file (lines as VS Code shows them).</summary>
        IReadOnlyList<BreakpointResult> SetBreakpoints(string file, IReadOnlyList<BreakpointRequest> lines);

        /// <summary>Continue one task, or every paused task (null).</summary>
        void Continue(int? task);

        /// <summary>Stop a task at its next Line marker (a foreground task cannot be paused).</summary>
        void Pause(int task);

        void Step(int task, StepKind kind);

        /// <summary>The task's frames, innermost first.</summary>
        IReadOnlyList<DebugFrame> StackTrace(int task);

        IReadOnlyList<DebugScope> Scopes(int frameId);

        /// <summary>The children of a scope or a variable; an array's elements from
        /// <paramref name="start"/>, at most <paramref name="count"/> of them (0 = all).</summary>
        IReadOnlyList<DebugVariable> Variables(int variablesReference, int start = 0, int count = 0);

        /// <summary>Evaluate an expression in a frame.</summary>
        EvaluateResult Evaluate(
            string expression,
            int? frameId,
            EvaluateContext context = EvaluateContext.Repl
        );

        /// <summary>Change a variable (the simulator can, the runtime debugger cannot). Throws
        /// ArgumentException with a message for the user (no such variable, a value that is not
        /// of its type, a read-only one).</summary>
        DebugVariable SetVariable(int variablesReference, string name, string value);

        /// <summary>The debugger leaves: every breakpoint is cleared and every stopped task runs
        /// on (the runtime's SESSION_STOP); the simulation goes on.</summary>
        void Detach();

        // ---- variable history and time travel (a simulator feature) ----------------
        /// <summary>A task's variable changes, oldest first.</summary>
        IReadOnlyList<VariableChange> History(int task, string? name = null, long callId = -1, int max = 500);

        HistoryInfo HistoryState(int task);

        /// <summary>A stopped task's view one line back (the previous Line marker it ran, in any
        /// call): StackTrace / Scopes / Variables / Evaluate answer as of then (read-only) until
        /// Live, a Continue or a Step of the task. ArgumentException when it is not stopped.</summary>
        HistoryMove StepBack(int task);

        /// <summary>One line forward; past the last, live again.</summary>
        HistoryMove StepForward(int task);

        /// <summary>Back to the previous line with a breakpoint, or the previous write a data
        /// breakpoint watches; else the start of the recording.</summary>
        HistoryMove ReverseContinue(int task);

        /// <summary>The view live again.</summary>
        void Live(int task);

        /// <summary>Replace the data breakpoints: a watched variable's write stops the writing
        /// task at its next line (StopReason.DataBreakpoint, the id in HitBreakpoints).</summary>
        IReadOnlyList<BreakpointResult> SetDataBreakpoints(IReadOnlyList<DataBreakpointRequest> list);

        event EventHandler<StoppedEventArgs>? Stopped;

        /// <summary>Tasks run on other than by the client's Continue / Step of them (a step of
        /// another task released them).</summary>
        event EventHandler<ContinuedEventArgs>? Continued;

        /// <summary>A step left the outermost function: the task is parked, with no Stopped.</summary>
        event EventHandler<TaskEventArgs>? Parked;
        event EventHandler<BreakpointEventArgs>? BreakpointChanged;
        event EventHandler<HardwareErrorEventArgs>? ErrorReported;
        event EventHandler<TaskEventArgs>? TaskStarted;
        event EventHandler<TaskEventArgs>? TaskExited;
        event EventHandler<OutputEventArgs>? Output;
        event EventHandler? Terminated;
    }
}
