using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using CicodeSim.Host.Dap;
using CicodeSim.Sim;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Host
{
    internal static partial class DapSessions
    {
        static partial void CreateSession(DapContext context, ref IDapSession? session) => session = new CicodeDapSession(context);
    }
}

namespace CicodeSim.Host.Dap
{
    /// <summary>
    /// The VS Code extension's debugger: DAP over the engine's <see cref="IDebugTarget"/>
    /// (engine/PROTOCOL.md "The debugger"). Conventions are the extension's
    /// real-runtime adapter's (dap/dap/Handlers.cs): the hardwareError exception filter,
    /// exceptionInfo, the hardware error text, a parked task resumed, continue = CONTINUE_ALL.
    ///
    /// Threads: requests come on the connection's reader thread, in order; target events on the
    /// simulation's event thread. Session state is under _lock, sending under _out, never one
    /// inside the other, and neither is held while the target is called (the simulation thread
    /// takes no session lock: the call's end is posted to the event thread). Events wait while
    /// a request is handled, so a response always goes before the events its request causes
    /// (VS Code marks a thread running when the continue response arrives: a stop sent before
    /// it would be lost).
    /// </summary>
    internal sealed partial class CicodeDapSession : IDapSession
    {
        public const string HardwareErrorFilter = "hardwareError";
        public const string PageCodeErrorFilter = "pageCodeError";

        /// <summary>The variables reference of the "Return value" scope (the target's ids stay
        /// below int.MaxValue - 1).</summary>
        public const int ReturnValueRef = int.MaxValue;

        /// <summary>The message of a breakpoint a Run Without Debugging session does not set.</summary>
        public const string NoDebugBreakpoint = "Run Without Debugging: breakpoints are off";

        private readonly DapContext _ctx;
        private readonly Simulation _sim;
        private readonly IDebugTarget _dbg;
        private readonly bool _breakOnErrorBefore;
        private readonly bool _foregroundErrorBreakBefore;

        // ---- session state (under _lock) ----
        private readonly object _lock = new object();
        private bool _initialized;
        private bool _configDone;
        private bool _attach;
        private LaunchArgs? _launch;
        private bool _started;
        private bool _callDone;
        private bool _terminating;
        private bool _terminatedSent;
        /// <summary>launch noDebug (Run Without Debugging): no breakpoint, no stop, every
        /// hardware error only written.</summary>
        private bool _noDebug;
        private string? _returnValue;
        private string _returnType = "";
        /// <summary>Where the launched call halted (from its 'task N halted' log line, which the
        /// call's own ending line replaces).</summary>
        private string? _haltFile;
        private int _haltLine;
        private readonly Dictionary<int, ErrorInfo> _errors = new Dictionary<int, ErrorInfo>();
        /// <summary>Page code tasks the debugger stopped, until they are seen to have ended.</summary>
        private readonly HashSet<int> _heldTasks = new HashSet<int>();
        /// <summary>A task's last hardware error reason from the log text, for the ErrorReported
        /// line that follows it.</summary>
        private readonly Dictionary<int, string> _haltReasons = new Dictionary<int, string>();
        /// <summary>source path as the client sent it -> its breakpoints the target has.</summary>
        private readonly Dictionary<string, List<BpState>> _bps = new Dictionary<string, List<BpState>>(StringComparer.OrdinalIgnoreCase);
        /// <summary>file key (SourceFiles.Key) -> the path the client named the file by.</summary>
        private readonly Dictionary<string, string> _clientPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>compile-time path -> the file on this machine (the path itself when none is found).</summary>
        private readonly Dictionary<string, string> _localPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private CicodeSim.World.TraceWriter? _trace;

        // ---- written on the simulation thread ----
        /// <summary>The launched call's task.</summary>
        private CicodeTask? _call;
        /// <summary>The task whose first stop is reported as the entry stop (0 = none).</summary>
        private int _entryTask;

        private int _closed;

        // ---- sending (under _out) ----
        private readonly object _out = new object();
        private int _hold;
        private readonly List<JsonValue> _held = new List<JsonValue>();

        private sealed class LaunchArgs
        {
            public string Function = "";
            public string Call = "";
            public bool StopOnEntry;
            public bool KeepRunning;
            public bool NoDebug;
        }

        /// <summary>A hardware error a task is stopped on (exceptionInfo), or reported and about
        /// to stop on (the stop comes at its next Line marker).</summary>
        private sealed class ErrorInfo
        {
            public int Code;
            public string Text = "";
            /// <summary>"Thread 5 Hardware error (424) 'Tag not found' ErrPage: 'TagRead' ErrDesc: 'GetTagVal'".</summary>
            public string Announce = "";
            public string ErrPage = "";
            public string ErrDesc = "";
            public string Function = "";
            public string File = "";
            /// <summary>The failing statement.</summary>
            public int Line;
            /// <summary>Where the task stopped (the next statement).</summary>
            public int PauseLine;
            /// <summary>The task is stopped on this error (until it runs on).</summary>
            public bool Stopped;
            /// <summary>The stop is in the failing function: its top frame shows the failing
            /// statement (the real adapter's SetThreadLocation in OnErrorPause).</summary>
            public bool Pin;
        }

        /// <summary>A breakpoint the target has.</summary>
        private sealed class BpState
        {
            public int Id;
            public int Line;
            public string? Condition;
            /// <summary>The condition calls a function (a hover does not run it again).</summary>
            public bool ConditionCalls;
        }

        public CicodeDapSession(DapContext ctx)
        {
            _ctx = ctx;
            _sim = ctx.Simulation;
            _dbg = ctx.Debugger;
            _breakOnErrorBefore = _dbg.Options.BreakOnError;
            _foregroundErrorBreakBefore = _dbg.Options.ForegroundErrorBreak;
        }

        // ------------------------------------------------------------------ messages in
        public void OnMessage(JsonValue msg)
        {
            if (Volatile.Read(ref _closed) != 0 || msg.Get("type")?.Str != "request")
                return;
            string cmd = msg.Get("command")?.Str ?? "";
            var a = msg.Get("arguments");
            if (a == null || a.Kind != JsonKind.Object)
                a = JsonValue.NewObject();
            if (cmd.StartsWith("ctsim/", StringComparison.Ordinal))
            {
                // a ctsim/* request waits while page code holds the simulation: answered off
                // the reader, so the client's continue or step is still read
                Custom(msg);
                return;
            }
            Hold();
            try
            {
                Handle(cmd, msg, a);
            }
            catch (ArgumentException e)
            {
                Fail(msg, e.Message);
            }
            catch (CallTextException e)
            {
                Fail(msg, e.Message);
            }
            catch (FormatException e)
            {
                Fail(msg, e.Message);
            }
            catch (IOException e)
            {
                Fail(msg, e.Message);
            }
            catch (ObjectDisposedException)
            {
                Fail(msg, "the simulation has ended");
            }
            catch (Exception e)
            {
                // every request gets its response: VS Code waits for ever on a missing one
                Fail(msg, cmd + " failed: " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                Unhold();
            }
        }

        private readonly Queue<JsonValue> _custom = new Queue<JsonValue>();
        private bool _customRunning;

        /// <summary>The ctsim/* requests, in their order, on a worker of their own.</summary>
        private void Custom(JsonValue req)
        {
            lock (_custom)
            {
                _custom.Enqueue(req);
                if (_customRunning)
                    return;
                _customRunning = true;
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                for (;;)
                {
                    JsonValue next;
                    lock (_custom)
                    {
                        if (_custom.Count == 0 || Volatile.Read(ref _closed) != 0)
                        {
                            _custom.Clear();
                            _customRunning = false;
                            return;
                        }
                        next = _custom.Dequeue();
                    }
                    string cmd = next.Get("command")?.Str ?? "";
                    try
                    {
                        if (!_ctx.HandleCustom(next))
                            Fail(next, "unsupported request " + Quote(cmd));
                    }
                    catch (ObjectDisposedException)
                    {
                        Fail(next, "the simulation has ended");
                    }
                    catch (Exception e)
                    {
                        Fail(next, cmd + " failed: " + e.GetType().Name + ": " + e.Message);
                    }
                }
            });
        }

        private void Handle(string cmd, JsonValue req, JsonValue a)
        {
            switch (cmd)
            {
                case "initialize": Initialize(req); return;
                case "launch": Launch(req, a); return;
                case "attach": Attach(req, a); return;
                case "configurationDone": ConfigurationDone(req); return;
                case "setBreakpoints": SetBreakpoints(req, a); return;
                case "setExceptionBreakpoints": SetExceptionBreakpoints(req, a); return;
                case "threads": Threads(req); return;
                case "stackTrace": StackTrace(req, a); return;
                case "scopes": Scopes(req, a); return;
                case "variables": Variables(req, a); return;
                case "evaluate": Evaluate(req, a); return;
                case "setVariable": SetVariable(req, a); return;
                case "continue": Continue(req); return;
                case "next": Step(req, a, StepKind.Over); return;
                case "stepIn": Step(req, a, StepKind.Into); return;
                case "stepOut": Step(req, a, StepKind.Out); return;
                case "pause": Pause(req, a); return;
                case "stepBack": Move(req, a, Travel.Back); return;
                case "reverseContinue": Move(req, a, Travel.ReverseContinue); return;
                case "dataBreakpointInfo": DataBreakpointInfo(req, a); return;
                case "setDataBreakpoints": SetDataBreakpoints(req, a); return;
                case "cicode/history": HistoryRequest(req, a); return;
                case "exceptionInfo": ExceptionInfo(req, a); return;
                case "source": Fail(req, "no source: the simulator debugs the .ci files on disk"); return;
                case "terminate": Terminate(req); return;
                case "disconnect": Disconnect(req, a); return;
            }
            if (_ctx.HandleCustom(req))
                return;
            Fail(req, "unsupported request " + Quote(cmd));
        }

        public void OnClosed()
        {
            if (Volatile.Read(ref _closed) != 0)
                return;
            // the client is gone without a disconnect: a launched call ends with its session
            bool kill;
            lock (_lock)
                kill = _launch != null && !_callDone;
            if (kill)
                TryKill();
            // the server has let go of this connection already: a debugger attached since then
            // owns the options
            Shutdown(!_ctx.Server.DebuggerAttached);
        }

        /// <summary>The session ends (once): handlers off, the session's options and trace given back.</summary>
        private void Shutdown(bool restoreOptions)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;
            Unsubscribe();
            if (restoreOptions)
            {
                _dbg.Options.BreakOnError = _breakOnErrorBefore;
                _dbg.Options.ForegroundErrorBreak = _foregroundErrorBreakBefore;
            }
            CloseTrace();
        }

        // ------------------------------------------------------------------ messages out
        private void Hold()
        {
            lock (_out)
                _hold++;
        }

        private void Unhold()
        {
            lock (_out)
            {
                if (--_hold > 0)
                    return;
                _hold = 0;
                foreach (var e in _held)
                    _ctx.Sink.Send(e);
                _held.Clear();
            }
        }

        private void Emit(JsonValue ev)
        {
            lock (_out)
            {
                if (_hold > 0)
                    _held.Add(ev);
                else
                    _ctx.Sink.Send(ev);
            }
        }

        private void Respond(JsonValue req, bool ok, JsonValue? body = null, string? message = null)
        {
            var r = JsonValue.NewObject();
            r.Set("type", "response");
            var seq = req.Get("seq");
            r.Set("request_seq", seq != null && seq.Kind == JsonKind.Int ? seq : JsonValue.Of(0));
            r.Set("success", JsonValue.Of(ok));
            r.Set("command", req.Get("command")?.Str ?? "");
            if (message != null)
                r.Set("message", message);
            r.Set("body", body ?? JsonValue.NewObject());
            lock (_out)
                _ctx.Sink.Send(r);
        }

        private void Fail(JsonValue req, string message) => Respond(req, false, null, message);

        private void Event(string name, JsonValue body) => Emit(SimServer.Event(name, body));

        private void Output(string category, string text, string? file = null, int line = 0)
        {
            var b = JsonValue.NewObject();
            b.Set("category", category);
            b.Set("output", text);
            if (!string.IsNullOrEmpty(file))
            {
                b.Set("source", Source(LocalPath(file!)));
                if (line > 0)
                    b.Set("line", JsonValue.Of(line));
            }
            Event("output", b);
        }

        private void SendTerminated()
        {
            lock (_lock)
            {
                if (_terminatedSent)
                    return;
                _terminatedSent = true;
            }
            Event("terminated", JsonValue.NewObject());
        }

        private void Exited(int code)
        {
            var b = JsonValue.NewObject();
            b.Set("exitCode", JsonValue.Of(code));
            Event("exited", b);
        }

        private bool NoDebug
        {
            get
            {
                lock (_lock)
                    return _noDebug;
            }
        }

        // ------------------------------------------------------------------ initialize
        private void Initialize(JsonValue req)
        {
            var caps = JsonValue.NewObject();
            caps.Set("supportsConfigurationDoneRequest", JsonValue.True);
            caps.Set("supportsConditionalBreakpoints", JsonValue.True);
            caps.Set("supportsHitConditionalBreakpoints", JsonValue.True);
            caps.Set("supportsLogPoints", JsonValue.True);
            caps.Set("supportsEvaluateForHovers", JsonValue.True);
            caps.Set("supportsSetVariable", JsonValue.True);
            caps.Set("supportsExceptionInfoRequest", JsonValue.True);
            caps.Set("supportsTerminateRequest", JsonValue.True);
            caps.Set("supportsDelayedStackTraceLoading", JsonValue.True);
            caps.Set("supportsClipboardContext", JsonValue.True);
            // the simulator records every variable write (revision 17)
            caps.Set("supportsStepBack", JsonValue.True);
            caps.Set("supportsDataBreakpoints", JsonValue.True);
            var filter = JsonValue.NewObject();
            filter.Set("filter", HardwareErrorFilter);
            filter.Set("label", "Cicode hardware errors");
            filter.Set("description", "Stop when a Cicode task raises a hardware error. When off, the error is only written to the Debug Console and the task continues.");
            filter.Set("default", JsonValue.True);
            var page = JsonValue.NewObject();
            page.Set("filter", PageCodeErrorFilter);
            page.Set("label", "Page code hardware errors (simulator)");
            page.Set("description", "With Cicode hardware errors on, also stop page code (object expressions and the functions they call) on its hardware errors; the simulation waits on that page. Off: they are only written to the Debug Console, as the runtime never stops page code.");
            page.Set("default", JsonValue.False);
            var filters = JsonValue.NewArray();
            filters.Add(filter);
            filters.Add(page);
            caps.Set("exceptionBreakpointFilters", filters);
            Respond(req, true, caps);
            bool first;
            lock (_lock)
            {
                first = !_initialized;
                _initialized = true;
            }
            if (!first)
                return;
            // no error stops until setExceptionBreakpoints (VS Code sends it after its breakpoints):
            // an error in that gap is only written, with the filter off it must not stop anything
            _dbg.Options.BreakOnError = false;
            Subscribe();
            Event("initialized", JsonValue.NewObject());
        }

        // ------------------------------------------------------------------ breakpoints
        private void SetBreakpoints(JsonValue req, JsonValue a)
        {
            var src = a.Get("source");
            string path = src != null && src.Kind == JsonKind.Object ? Str(src, "path") : "";
            if (path.Length == 0 && src != null && src.Kind == JsonKind.Object)
                path = Str(src, "name");
            if (path.Length == 0)
                throw new ArgumentException("setBreakpoints needs source.path (the .ci file)");
            var reqs = new List<BreakpointRequest>();
            var bps = a.Get("breakpoints");
            if (bps != null && bps.Kind == JsonKind.Array)
            {
                foreach (var b in bps.Items)
                {
                    if (b.Kind != JsonKind.Object)
                        continue;
                    reqs.Add(new BreakpointRequest
                    {
                        Line = Int(b, "line", 0),
                        Condition = NonEmpty(Str(b, "condition")),
                        HitCondition = NonEmpty(Str(b, "hitCondition")),
                        LogMessage = NonEmpty(Str(b, "logMessage")),
                    });
                }
            }
            else if (a.Get("lines") is JsonValue lines && lines.Kind == JsonKind.Array)
            {
                foreach (var l in lines.Items)
                    if (l.IsNumber)
                        reqs.Add(new BreakpointRequest { Line = (int)l.Double });
            }
            Remember(path);
            // the target gets the breakpoints that can stop: not under Run Without Debugging,
            // not one whose condition does not parse (the target would stop on every hit)
            bool noDebug = NoDebug;
            var refused = new string?[reqs.Count];
            var calls = new bool[reqs.Count];
            var set = new List<BreakpointRequest>();
            for (int i = 0; i < reqs.Count; i++)
            {
                refused[i] = noDebug ? NoDebugBreakpoint : ConditionError(reqs[i].Condition, out calls[i]);
                if (refused[i] == null)
                    set.Add(reqs[i]);
            }
            var res = _dbg.SetBreakpoints(path, set);
            var kept = new List<BpState>();
            var arr = JsonValue.NewArray();
            int k = 0;
            for (int i = 0; i < reqs.Count; i++)
            {
                if (refused[i] != null || k >= res.Count)
                {
                    arr.Add(Unverified(path, reqs[i].Line, refused[i] ?? "not set"));
                    continue;
                }
                var r = res[k++];
                kept.Add(new BpState { Id = r.Id, Line = r.Line, Condition = reqs[i].Condition, ConditionCalls = calls[i] });
                arr.Add(BreakpointJson(r));
            }
            lock (_lock)
            {
                if (kept.Count > 0)
                    _bps[path] = kept;
                else
                    _bps.Remove(path);
            }
            var body = JsonValue.NewObject();
            body.Set("breakpoints", arr);
            Respond(req, true, body);
        }

        private static JsonValue BreakpointJson(BreakpointResult r)
        {
            var o = JsonValue.NewObject();
            o.Set("id", JsonValue.Of(r.Id));
            o.Set("verified", JsonValue.Of(r.Verified));
            o.Set("line", JsonValue.Of(r.Line));
            if (r.Message != null)
                o.Set("message", r.Message);
            if (r.File.Length > 0)
                o.Set("source", Source(r.File));
            return o;
        }

        /// <summary>A breakpoint the target does not have (no id: no later event names it).</summary>
        private static JsonValue Unverified(string path, int line, string message)
        {
            var o = JsonValue.NewObject();
            o.Set("verified", JsonValue.False);
            o.Set("line", JsonValue.Of(line));
            o.Set("message", message);
            o.Set("source", Source(path));
            return o;
        }

        /// <summary>Why a breakpoint condition can never be evaluated (the debugger's expression
        /// parser refuses it, as Handlers.cs does with ValidateCondition), null when it parses.
        /// Names and calls stand for 1 here: what a name is is known only where the task stops.</summary>
        internal static string? ConditionError(string? condition, out bool calls)
        {
            calls = false;
            if (condition == null)
                return null;
            bool call = false;
            var ev = new WatchEval
            {
                Name = (n, i) => Value.Long(1),
                Call = (n, args) =>
                {
                    call = true;
                    return Value.Long(1);
                },
            };
            try
            {
                ev.Evaluate(condition);
            }
            catch (EvalException e)
            {
                return "The condition cannot be parsed: " + e.Message;
            }
            catch (Exception)
            {
                // an operator error on the stand-in values: the text parses
            }
            calls = call;
            return null;
        }

        /// <summary>Run Without Debugging: the breakpoints set before the launch come off the
        /// target and show as unverified.</summary>
        private void DropBreakpoints()
        {
            List<KeyValuePair<string, List<BpState>>> files;
            lock (_lock)
            {
                files = new List<KeyValuePair<string, List<BpState>>>(_bps);
                _bps.Clear();
            }
            foreach (var kv in files)
            {
                _dbg.SetBreakpoints(kv.Key, Array.Empty<BreakpointRequest>());
                foreach (var bp in kv.Value)
                {
                    var o = Unverified(kv.Key, bp.Line, NoDebugBreakpoint);
                    o.Set("id", JsonValue.Of(bp.Id));
                    var b = JsonValue.NewObject();
                    b.Set("reason", "changed");
                    b.Set("breakpoint", o);
                    Event("breakpoint", b);
                }
            }
        }

        private BpState? FindBreakpoint(int id)
        {
            lock (_lock)
                foreach (var list in _bps.Values)
                    foreach (var b in list)
                        if (b.Id == id)
                            return b;
            return null;
        }

        private void SetExceptionBreakpoints(JsonValue req, JsonValue a)
        {
            bool on = false, page = false;
            if (a.Get("filters") is JsonValue f && f.Kind == JsonKind.Array)
                foreach (var x in f.Items)
                    if (x.Kind == JsonKind.String)
                    {
                        on |= x.Str == HardwareErrorFilter;
                        page |= x.Str == PageCodeErrorFilter;
                    }
            if (a.Get("filterOptions") is JsonValue fo && fo.Kind == JsonKind.Array)
                foreach (var x in fo.Items)
                    if (x.Kind == JsonKind.Object)
                    {
                        on |= Str(x, "filterId") == HardwareErrorFilter;
                        page |= Str(x, "filterId") == PageCodeErrorFilter;
                    }
            // Run Without Debugging never stops: an error is only written
            _dbg.Options.BreakOnError = on && !NoDebug;
            _dbg.Options.ForegroundErrorBreak = page && !NoDebug;
            Respond(req, true);
        }

        // ------------------------------------------------------------------ running
        /// <summary>The runtime's CONTINUE_ALL (Handlers.cs OnContinue): every stopped task runs
        /// on, which also re-arms the foreground error report.</summary>
        private void Continue(JsonValue req)
        {
            // the error stops end before the tasks run: a task's next error can be reported (on
            // the event thread) before Continue returns. An error reported but not stopped on
            // yet stays.
            lock (_lock)
            {
                var ended = new List<int>();
                foreach (var kv in _errors)
                    if (kv.Value.Stopped)
                        ended.Add(kv.Key);
                foreach (int t in ended)
                    _errors.Remove(t);
            }
            _dbg.Continue(null);
            var b = JsonValue.NewObject();
            b.Set("allThreadsContinued", JsonValue.True);
            Respond(req, true, b);
        }

        private void Step(JsonValue req, JsonValue a, StepKind kind)
        {
            int task = Int(a, "threadId", 0);
            if (StepInPast(req, task, kind))
                return;
            ErrorInfo? stop;
            lock (_lock)
            {
                // before the task runs (see Continue)
                if (_errors.TryGetValue(task, out stop) && stop.Stopped)
                    _errors.Remove(task);
                else
                    stop = null;
            }
            try
            {
                _dbg.Step(task, kind);
            }
            catch (Exception)
            {
                // the task did not run on: it is still stopped on its error
                if (stop != null)
                    lock (_lock)
                        if (!_errors.ContainsKey(task))
                            _errors[task] = stop;
                throw;
            }
            Respond(req, true);
        }

        private void Pause(JsonValue req, JsonValue a)
        {
            int task = Int(a, "threadId", 0);
            if (NoDebug)
                throw new ArgumentException("Run Without Debugging: a task cannot be paused");
            DebugTaskInfo? info = null;
            foreach (var t in _dbg.Tasks())
                if (t.Id == task)
                    info = t;
            if (info == null)
                throw new ArgumentException("no task " + task.ToString(CultureInfo.InvariantCulture));
            if (info.Foreground && !_dbg.Options.ForegroundCanBreak)
                throw new ArgumentException("task " + task.ToString(CultureInfo.InvariantCulture) + " runs foreground Cicode, which cannot break");
            if (!info.Paused)
                _dbg.Pause(task);
            Respond(req, true);
        }

        // ------------------------------------------------------------------ helpers
        private static string Str(JsonValue a, string key, string def = "") =>
            a.Get(key) is JsonValue v && v.Kind == JsonKind.String ? v.Str : def;

        private static int Int(JsonValue a, string key, int def)
        {
            var v = a.Get(key);
            if (v == null || !v.IsNumber)
                return def;
            double d = v.Double;
            return d >= int.MaxValue ? int.MaxValue : d <= int.MinValue ? int.MinValue : (int)d;
        }

        private static bool Bool(JsonValue a, string key) => a.Get(key) is JsonValue v && v.Kind == JsonKind.Bool && v.Bool;

        private static string? NonEmpty(string s) => s.Length > 0 ? s : null;

        private static string Quote(string s) => CicodeSim.Project.CitectText.Quote(s);

        private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);

        /// <summary>The file name of a path (any separator; never throws on odd characters).</summary>
        private static string FileName(string path)
        {
            string p = path.TrimEnd('\\', '/');
            int s = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
            return s >= 0 ? p.Substring(s + 1) : p;
        }

        private static JsonValue Source(string path)
        {
            var o = JsonValue.NewObject();
            o.Set("name", FileName(path));
            o.Set("path", path);
            return o;
        }

        /// <summary>A file the client named (setBreakpoints, launch 'file'): frames of that file
        /// show the client's path.</summary>
        private void Remember(string path)
        {
            string key = SourceFiles.Key(path);
            if (key.Length == 0 || SourceFiles.IsBare(key))
                return;
            lock (_lock)
                _clientPaths[key] = path;
        }

        /// <summary>The path to show for a compile-time .ci path: the client's own name of that
        /// file, else the file when it exists, else the same file name in the unit's project of
        /// the same folder name (a copied project), else the path itself.</summary>
        private string LocalPath(string file)
        {
            if (file.Length == 0)
                return file;
            string key = SourceFiles.Key(file);
            lock (_lock)
            {
                if (_clientPaths.TryGetValue(key, out var c))
                    return c;
                if (_localPaths.TryGetValue(file, out var l))
                    return l;
            }
            string found = file;
            try
            {
                if (!File.Exists(file))
                {
                    int s = key.LastIndexOf('\\');
                    string folder = s > 0 ? key.Substring(0, s) : "";
                    if (folder.Length > 0)
                        foreach (var p in _sim.World.Unit.Projects)
                        {
                            if (!string.Equals(FileName(p.Dir), folder, StringComparison.OrdinalIgnoreCase))
                                continue;
                            string cand = Path.Combine(p.Dir, FileName(file));
                            if (File.Exists(cand))
                            {
                                found = cand;
                                break;
                            }
                        }
                }
            }
            catch (Exception e) when (e is IOException || e is ArgumentException || e is UnauthorizedAccessException || e is NotSupportedException)
            {
            }
            lock (_lock)
                _localPaths[file] = found;
            return found;
        }

        /// <summary>The real adapter's hardware error line (dap/Ipc/DebugClient.cs LogHwError):
        /// "Cicode hardware error 259 'Array has been overrun' in thread 3, _PltLineStyleGet at
        /// ...\graph.ci:271 [the task halts]".</summary>
        private string HardwareErrorText(int code, string text, int task, string function, string errPage, string file, int line, string? note)
        {
            var sb = new StringBuilder("Cicode hardware error ").Append(code.ToString(CultureInfo.InvariantCulture));
            if (text.Length > 0)
                sb.Append(" '").Append(text).Append('\'');
            sb.Append(" in thread ").Append(Num(task));
            if (function.Length > 0)
                sb.Append(", ").Append(function);
            if (errPage.Length > 0)
                sb.Append(" (").Append(errPage).Append(')');
            if (file.Length > 0)
                sb.Append(" at ").Append(LocalPath(file)).Append(':').Append(Num(line));
            if (note != null)
                sb.Append(" [").Append(note).Append(']');
            return sb.ToString();
        }
    }
}
