using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using CicodeSim.Builtins;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;
using CProgram = CicodeSim.Compiled.Program;
using Rdb = CicodeSim.Compiled.Rdb;
using WorldT = CicodeSim.World.World;
using WQuality = CicodeSim.World.Quality;

namespace CicodeSim.Sim
{
    /// <summary>How a <see cref="Simulation"/> starts.</summary>
    public sealed class SimulationOptions
    {
        /// <summary>The project folder.</summary>
        public string ProjectDir = "";

        /// <summary>The compiled project to run as (the compile unit's root; default: the root
        /// whose include closure holds the project, TagScope.PickRootFor).</summary>
        public string? Root;

        /// <summary>The compiled library (default: the root project's _LIBRARY.RDB).</summary>
        public string? Rdb;

        /// <summary>changed | all | none: which code runs from source (CodeSource.Open):
        /// none = _LIBRARY.RDB, all = every function from source, changed = the RDB with the .ci
        /// files newer than it from source.</summary>
        public string Compile = CodeSource.Changed;

        /// <summary>The simulated wall time at t = 0 (ISO text, Unix seconds, null = now).</summary>
        public object? Start;

        /// <summary>run (the wall clock times Speed) or paused.</summary>
        public string ClockMode = SimClock.Run;
        public double Speed = 1.0;

        /// <summary>The default step of clock step (seconds).</summary>
        public double StepSeconds = 0.25;

        /// <summary>The simulated computer's time zone (default: this machine's).</summary>
        public TimeZoneInfo? TimeZone;

        /// <summary>A ctsim-trace JSONL output (traces/README.md), null = none.</summary>
        public string? TracePath;

        /// <summary>A function started as a task once the simulation runs (the project's
        /// startup code), null = none; "auto" = what the runtime runs at a client's start
        /// (RuntimeStartup: _CitectBegin and the roles' StartupCode, after window 0 shows a page).</summary>
        public string? Startup;

        /// <summary>Real milliseconds between two tag change batches (the page scan, [Page]ScanTime 250 ms).</summary>
        public int ScanMs = 250;

        /// <summary>The debugger's options. Breakpoints in page code stop the scan.</summary>
        public DebugOptions Debug = new DebugOptions { ForegroundCanBreak = true };

        /// <summary>Start the servers' work on the clock: the alarm server's scan and the report
        /// server's schedules.</summary>
        public bool StartAlarms = true;

        /// <summary>The File* built-ins' sandbox: null = this simulation's own folder
        /// under %LOCALAPPDATA%\cicode-sim\sandbox, made at the first write (SimHost.SimulationSandbox).</summary>
        public SandboxOptions? Sandbox;

        /// <summary>The UI of forms and message boxes: null = headless, every dialog cancelled.</summary>
        public IInteractionHost? Interaction;

        /// <summary>Simulator only (ctsim serve --simulate-undefined): a setTag of a
        /// name the project does not define makes a simulated tag for it. Off (default) = the
        /// runtime's behaviour: an undefined name is error 424 / bad quality.</summary>
        public bool SimulateUndefined;

        /// <summary>Simulator only: trends show a pen's first known value back to the start.</summary>
        public bool SeedTrendHistory;

        /// <summary>Simulator only: whether the simulation may load DLLs and reach SQL databases.</summary>
        public Builtins.OutsideMode Dll,
            Sql;
        public List<string> DllAllowed = new List<string>(),
            SqlAllowed = new List<string>();

        /// <summary>Seconds a DLL call may take before the library helper is ended and the call
        /// fails; 0 (default) = no limit, as the runtime (serve --dll-timeout, ctsim/options
        /// dllTimeout).</summary>
        public int DllTimeoutSeconds;
    }

    /// <summary>The compile seam: the source compiler (Compiler.Codegen) makes
    /// 'changed' compile the edited / newer files from source and 'all' everything.</summary>
    public static class CodeSource
    {
        public const string Changed = "changed",
            All = "all",
            None = "none";

        public static bool IsMode(string m) => m == Changed || m == All || m == None;

        /// <summary>The program to run; notes say what was not done.</summary>
        public static CProgram Load(Project.Unit unit, string mode, string? rdbPath, List<string> notes) =>
            Open(unit, mode, rdbPath, notes).Program;

        /// <summary>The program to run, kept up to date by the source compiler.</summary>
        public static Compiler.Codegen.LiveProgram Open(
            Project.Unit unit,
            string mode,
            string? rdbPath,
            List<string> notes
        )
        {
            if (!IsMode(mode))
                throw new ArgumentException(
                    "compile must be changed, all or none: " + CitectSimText.Quote(mode)
                );
            var live = Compiler.Codegen.LiveProgram.Open(unit.RootDir, mode, rdbPath);
            notes.AddRange(live.Notes);
            foreach (var d in live.Errors)
                notes.Add("compile error: " + d);
            foreach (var (fn, why) in live.Failed)
                notes.Add("not compiled from source: " + fn + ": " + why);
            return live;
        }
    }

    internal static class CitectSimText
    {
        public static string Quote(string s) => Project.CitectText.Quote(s);
    }

    /// <summary>A task as viewers see it.</summary>
    public sealed class TaskSummary
    {
        public int Handle;
        public string Name = "";
        public string Function = "";
        public int State;
        public bool Foreground;
        public int HaltCode;
        public string HaltReason = "";
        public bool Paused;
        public string StopReason = "";
        public int Line;
        public string File = "";

        public string StateName => TaskState.Name(State);

        public static TaskSummary Of(CicodeTask t) =>
            new TaskSummary
            {
                Handle = t.Handle,
                Name = t.Name,
                Function = t.Func?.Name ?? "",
                State = t.State,
                Foreground = t.Foreground,
                HaltCode = t.HaltCode,
                HaltReason = t.HaltReason,
                Paused = t.Pause,
                StopReason = t.StopReason,
                Line = t.Line,
                File = t.Func?.Filename ?? "",
            };

        public JsonValue ToJson()
        {
            var o = JsonValue.NewObject();
            o.Set("task", JsonValue.Of(Handle));
            o.Set("name", Name);
            o.Set("function", Function);
            o.Set("state", StateName);
            o.Set("foreground", JsonValue.Of(Foreground));
            if (State == TaskState.Halt)
            {
                o.Set("haltCode", JsonValue.Of(HaltCode));
                o.Set("haltReason", HaltReason);
            }
            if (Paused)
                o.Set("stopReason", StopReason);
            return o;
        }
    }

    /// <summary>A log line for viewers (ctsim/log).</summary>
    public sealed class LogEntry
    {
        /// <summary>error, warning, info.</summary>
        public string Level = "info";

        /// <summary>What the log line is: hardware, task, message and so on.</summary>
        public string Kind = "";
        public string Text = "";
        public string Function = "";
        public string File = "";
        public int Line;
        public int Task;

        /// <summary>A hardware error's code (0 = none).</summary>
        public int Code;

        /// <summary>The built-in call that raised it, with the values it got:
        /// 'TagGetProperty("FIL1_stsWord", "Custom1", 0)' ('' none).</summary>
        public string Call = "";

        /// <summary>The tag the task read last when the error came from a tag read ('' none).</summary>
        public string Tag = "";

        /// <summary>The task's Cicode call stack at the line, innermost first (errors and
        /// warnings of a task; empty otherwise).</summary>
        public List<StackLine> Stack = new List<StackLine>();

        /// <summary>The page code that ran: its window, page, object AN (-1 none) and what it
        /// was (the task's label: 'visible', 'touch.down', 'page.entry' ...).</summary>
        public int Window = -1;
        public string Page = "";
        public int An = -1;
        public string Source = "";

        /// <summary>A hardware error that halts its task.</summary>
        public bool Halts;

        /// <summary>Why the task halts beyond its error code: a function the compile never
        /// reached ('X did not compile: the compile stops at ...'), a call with the wrong number
        /// of arguments; '' when the code says it all.</summary>
        public string HaltReason = "";

        public JsonValue ToJson()
        {
            var o = JsonValue.NewObject();
            o.Set("level", Level);
            o.Set("text", Text);
            if (Halts)
                o.Set("halts", JsonValue.True);
            if (HaltReason.Length > 0)
                o.Set("haltReason", HaltReason);
            if (Kind.Length > 0)
                o.Set("kind", Kind);
            if (Code != 0)
                o.Set("code", JsonValue.Of(Code));
            if (Call.Length > 0)
                o.Set("call", Call);
            if (Tag.Length > 0)
                o.Set("tag", Tag);
            if (Function.Length > 0)
                o.Set("function", Function);
            if (File.Length > 0)
                o.Set("file", File);
            if (Line > 0)
                o.Set("line", JsonValue.Of(Line));
            if (Task > 0)
                o.Set("task", JsonValue.Of(Task));
            if (Stack.Count > 0)
            {
                var a = JsonValue.NewArray();
                foreach (var f in Stack)
                {
                    var x = JsonValue.NewObject();
                    x.Set("function", f.Function);
                    if (f.File.Length > 0)
                        x.Set("file", f.File);
                    if (f.Line > 0)
                        x.Set("line", JsonValue.Of(f.Line));
                    if (f.Args.Count > 0)
                    {
                        var args = JsonValue.NewObject();
                        foreach (var (n, v) in f.Args)
                            args.Set(n, v);
                        x.Set("args", args);
                    }
                    a.Add(x);
                }
                o.Set("stack", a);
            }
            if (Window >= 0)
                o.Set("window", JsonValue.Of(Window));
            if (Page.Length > 0)
                o.Set("page", Page);
            if (An > 0)
                o.Set("an", JsonValue.Of(An));
            if (Source.Length > 0)
                o.Set("source", Source);
            return o;
        }
    }

    /// <summary>One frame of a log line's call stack.</summary>
    public sealed class StackLine
    {
        public string Function = "";
        public string File = "";
        public int Line;

        /// <summary>The function's arguments as they are now (name, value as Cicode text).</summary>
        public List<(string Name, string Value)> Args = new List<(string, string)>();
    }

    /// <summary>A tag's value at a batch (ctsim/tagsChanged, subscribe, tags).</summary>
    public sealed class TagUpdate
    {
        public string Name = "";
        public JsonValue Value = JsonValue.Null;
        public string Quality = "";

        /// <summary>ISO 8601 UTC of the value's timestamp ('' for none).</summary>
        public string Timestamp = "";

        /// <summary>false: no such tag.</summary>
        public bool Defined = true;

        /// <summary>A batch entry read by a subscribed name that is not the tag's plain value
        /// ('Tag.Q', 'Tag.T', 'Tag[3]', see <see cref="Simulation.Watch"/>): that name; null for
        /// the tag's own entry. Not sent (the server names the entry).</summary>
        public string? Asked;

        public JsonValue ToJson()
        {
            var o = JsonValue.NewObject();
            o.Set("name", Name);
            o.Set("value", Value);
            o.Set("quality", Quality);
            if (Timestamp.Length > 0)
                o.Set("timestamp", Timestamp);
            if (!Defined)
                o.Set("defined", JsonValue.False);
            return o;
        }
    }

    /// <summary>One scan's changed tags with the clock at that scan.</summary>
    public sealed class TagBatch
    {
        public IReadOnlyList<TagUpdate> Updates = Array.Empty<TagUpdate>();

        /// <summary>Simulation.ClockState() at the batch.</summary>
        public JsonValue Clock = JsonValue.Null;
    }

    /// <summary>What a call (ctsim/call, a scenario call) gave when its task ended.</summary>
    public sealed class CallOutcome
    {
        public int Task;

        /// <summary>stop, halt, running (wait = false or a timeout).</summary>
        public string State = "";

        /// <summary>The result as ctCicode returns it: the value converted to STRING ('' for none).</summary>
        public string Result = "";
        public JsonValue Value = JsonValue.Null;

        /// <summary>INT, REAL, STRING ... '' for no value.</summary>
        public string Type = "";

        /// <summary>The halt code, else IsError's code at the end (0 = none).</summary>
        public int Error;
        public string ErrorText = "";
        public string HaltReason = "";

        /// <summary>The built-in that is not simulated (the task halted on it), else ''.</summary>
        public string NotSimulated = "";
        public bool TimedOut;

        public JsonValue ToJson()
        {
            var o = JsonValue.NewObject();
            o.Set("task", JsonValue.Of(Task));
            o.Set("state", State);
            o.Set("result", Result);
            o.Set("value", Value);
            o.Set("type", Type);
            o.Set("error", JsonValue.Of(Error));
            if (ErrorText.Length > 0)
                o.Set("errorText", ErrorText);
            if (HaltReason.Length > 0)
                o.Set("haltReason", HaltReason);
            if (NotSimulated.Length > 0)
                o.Set("notSimulated", NotSimulated);
            if (TimedOut)
                o.Set("timedOut", JsonValue.True);
            return o;
        }
    }

    /// <summary>Call text that is not one call of a known function with literal arguments: the
    /// runtime's ctCicode error (292 for a bare expression or an unknown function, 433 / 529
    /// for bad argument text).</summary>
    public sealed class CallTextException : Exception
    {
        public readonly int Code;

        public CallTextException(int code, string message)
            : base(message)
        {
            Code = code;
        }
    }

    /// <summary>One simulated project: its World, VM, scheduler and pages.</summary>
    public sealed class Simulation : IDisposable
    {
        public SimulationOptions Options { get; }
        public WorldT World { get; }
        public CicodeVm Vm { get; }
        public Scheduler Scheduler { get; }
        public Stepper Stepper { get; }
        public SimHost Host { get; }
        public CProgram Library { get; private set; }

        /// <summary>The program and the source compiler behind it (ctsim/reload recompiles a file).</summary>
        public Compiler.Codegen.LiveProgram Live { get; }
        public DebugTarget Debugger { get; }

        /// <summary>The pages: the page animator over this VM, made on the first page request.</summary>
        public SimPages Pages { get; }

        /// <summary>'Start like the runtime' (load {startup: "auto"}): _CitectBegin and the roles'
        /// StartupCode, once window 0 shows a page.</summary>
        public RuntimeStartup Boot { get; }
        public EventPump Events { get; } = new EventPump();
        public string ProjectDir { get; }
        public string ProjectName => World.Project;
        public string Root => World.Root;

        /// <summary>Why <see cref="Root"/>: a Project.RootPick reason (asked, chosen, self, only,
        /// lastDatabase, firstByName, none).</summary>
        public string RootReason => World.Unit.RootReason;

        /// <summary>The roots whose include closure holds the project, by name.</summary>
        public IReadOnlyList<string> RootCandidates => World.Unit.RootCandidates;

        /// <summary>The User folder of the project's install ('' when the project is not in one).</summary>
        public string UserDir { get; }

        /// <summary>The install's data folder (the User folder's parent), ''.</summary>
        public string InstallDir { get; }

        /// <summary>What loading did not do (the compile seam's notes).</summary>
        public List<string> LoadNotes { get; } = new List<string>();
        public DateTime StartedUtc { get; } = DateTime.UtcNow;

        /// <summary>Milliseconds the load took (World + library + VM).</summary>
        public long LoadMs { get; }

        // ---- events (raised on the Events pump thread) ----
        /// <summary>The tags that changed since the last batch, at most one batch per ScanMs.</summary>
        public event Action<TagBatch>? TagsChanged;
        public event Action<LogEntry>? Log;

        /// <summary>A built-in that is not simulated was called: (name, calls so far).</summary>
        public event Action<string, int>? NotSimulated;
        public event Action<TaskSummary>? TaskStarted;
        public event Action<TaskSummary>? TaskExited;

        /// <summary>The clock's mode or speed changed, or a step ended.</summary>
        public event Action<JsonValue>? ClockChanged;

        /// <summary>A scenario run ended (its result).</summary>
        public event Action<JsonValue>? ScenarioFinished;

        /// <summary>The simulation ended (Dispose).</summary>
        public event Action? Terminated;

        /// <summary>A source file was reported changed (ctsim/reload): (file, text or null).</summary>
        public event Action<string, string?>? Reloaded;

        /// <summary>The logged-in user changed (a login, a logout, ctsim/user): UserJson().</summary>
        public event Action<JsonValue>? UserChanged;

        /// <summary>Page code the debugger stopped holds the simulation, or goes on:
        /// {paused, task, function, file, line, reason, window, an, page, source}
        /// (ctsim/debugPaused).</summary>
        public event Action<JsonValue>? DebugPaused;

        private readonly ConcurrentQueue<Action> _commands = new ConcurrentQueue<Action>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);

        /// <summary>The debugger's commands (InvokeDebug): run before the others, and the only
        /// ones run while page code the debugger stopped holds the simulation thread.</summary>
        private readonly ConcurrentQueue<Action> _debugCommands = new ConcurrentQueue<Action>();
        private readonly AutoResetEvent _debugSignal = new AutoResetEvent(false);

        /// <summary>The page code task the debugger holds the simulation in, null = none.</summary>
        private CicodeTask? _heldBy;
        private Thread? _thread;
        private volatile bool _stopping;

        /// <summary>Guards the hand-over of commands at the end: once the loop has set _ended it
        /// runs what was queued and takes nothing more (Post / Invoke then throw), so no Invoke
        /// waits for ever on a command the loop never runs.</summary>
        private readonly object _cmdLock = new object();
        private bool _ended;
        private int _disposed;
        private readonly HashSet<string> _changedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _nsSent = new Dictionary<string, int>(
            StringComparer.Ordinal
        );
        private readonly Dictionary<CicodeTask, List<Action<CicodeTask>>> _onEnd =
            new Dictionary<CicodeTask, List<Action<CicodeTask>>>();
        private readonly Stopwatch _wall = Stopwatch.StartNew();
        private long _lastFlush;
        private bool _flushNow;
        private Scenario? _scenario;
        private ScenarioRunner? _scenarioRun;
        private readonly HashSet<string> _reloaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>tag -> the watched names of it that are read on their own (with a count).</summary>
        private readonly Dictionary<string, Dictionary<string, int>> _watched = new Dictionary<
            string,
            Dictionary<string, int>
        >(StringComparer.OrdinalIgnoreCase);

        /// <summary>Real ms a burst of slices may take before the loop looks at its commands.</summary>
        public const int BurstMs = 20;

        /// <summary>Load the project (World, compiled library, VM) on the calling thread; the
        /// loop starts with <see cref="Start"/>.</summary>
        public Simulation(SimulationOptions options)
        {
            Options = options;
            var sw = Stopwatch.StartNew();
            string dir = Path.GetFullPath(options.ProjectDir).TrimEnd('\\', '/');
            ProjectDir = dir;
            // a folder that does not exist is no project (with the source compiler a folder
            // without _LIBRARY.RDB loads: its code, or none, compiles from source)
            if (!Directory.Exists(dir))
                throw new DirectoryNotFoundException("no project folder: " + dir + " does not exist");
            // the root = the compiled project the runtime runs this project as
            // (TagScope.PickRootFor): asked (--root), else the roots whose include closure holds
            // the project; a project with its own _LIBRARY.RDB is not its own root when another
            // project compiles it (an include project runs as the project that compiles it)
            string? root = string.IsNullOrWhiteSpace(options.Root) ? null : options.Root!.Trim();
            World = new WorldT(dir, root: root, start: options.Start ?? DateTime.UtcNow);
            World.Clock.StepSize = options.StepSeconds;
            Live = CodeSource.Open(World.Unit, options.Compile, options.Rdb, LoadNotes);
            Library = Live.Program;
            Host = new SimHost(
                World,
                new SimHostOptions
                {
                    TimeZone = options.TimeZone,
                    Sandbox = options.Sandbox ?? SimHost.SimulationSandbox(),
                    Interaction = options.Interaction,
                }
            );
            Host.Outside.Dll = options.Dll;
            Host.Outside.Sql = options.Sql;
            Host.Outside.SetAllowed(true, options.DllAllowed);
            Host.Outside.SetAllowed(false, options.SqlAllowed);
            Host.Outside.DllTimeoutMs = options.DllTimeoutSeconds * 1000;
            var config = CodeConfig.FromParams((sec, key, def) => World.Param(sec, key, def) ?? def);
            config.MaxSliceOps = WorldRun.SliceOps;
            Vm = new CicodeVm(Library, WorldBuiltins.Create(Host), World.Tags, World.Clock, null, config);
            Scheduler = new Scheduler(Vm);
            Vm.ErrorReport = OnErrorReport;
            Vm.Log = OnVmLog;
            Stepper = new Stepper(Vm, options.Debug) { SessionActive = false }; // a debugger attaches (DebugTarget.Attach)
            Stepper.RecorderClock = () => World.Clock.NowMs; // (the history's simulated times)
            Debugger = new DebugTarget(this);
            Pages = new SimPages(this);
            Host.ReportSource = () => Pages.CodeUnit();
            Boot = new RuntimeStartup(this);
            // a waiting 'startup auto' runs once window 0 shows a page, however it was shown
            World.Display.PageShown += w => Boot.PageShown(w.Number);
            Scheduler.TaskAdded += t =>
            {
                if (!t.NoDebug)
                {
                    var s = TaskSummary.Of(t);
                    Events.Post(() => Events.Each(TaskStarted, h => ((Action<TaskSummary>)h)(s)));
                }
            };
            Scheduler.TaskEnded += OnTaskEnded;
            World.Tags.Subscribe(ch => _changedTags.Add(ch.Name));
            World.Tags.LockedWrite = OnLockedWrite;
            World.Tags.CodeUsed = OnTagUsed;
            World.Users.Listeners.Add(_ =>
            {
                var body = UserJson();
                Events.Post(() => Events.Each(UserChanged, h => ((Action<JsonValue>)h)(body)));
            });
            if (options.TracePath != null)
                World.OpenTrace(options.TracePath);
            if (options.StartAlarms)
            {
                World.Start();
                Reports.Start(Host, Vm);
            }
            UserDir = FindUserDir(dir);
            InstallDir = UserDir.Length > 0 ? (Path.GetDirectoryName(UserDir) ?? "") : "";
            World.Clock.SetSpeed(options.Speed);
            World.Clock.SetMode(options.ClockMode == "pause" ? SimClock.Paused : options.ClockMode);
            World.Tags.SimulateUndefined = options.SimulateUndefined;
            TrendTags.Of(World).SeedHistory = options.SeedTrendHistory;
            LoadMs = sw.ElapsedMilliseconds;
            Events.Error += OnHandlerError;
        }

        private static string FindUserDir(string projectDir)
        {
            string? parent = Path.GetDirectoryName(projectDir);
            if (parent != null && File.Exists(Path.Combine(parent, "MASTER.DBF")))
                return parent;
            return "";
        }

        // ------------------------------------------------------------------ the thread
        public bool IsSimThread => _thread != null && Thread.CurrentThread == _thread;

        public bool Running => _thread != null && !_stopping;

        /// <summary>Start the loop (and the startup function, if any).</summary>
        public void Start()
        {
            if (_thread != null)
                return;
            foreach (string n in LoadNotes)
                Emit("warning", n, kind: "load");
            _thread = new Thread(Loop) { IsBackground = true, Name = "ctsim-sim " + ProjectName };
            _thread.Start();
            if (!string.IsNullOrEmpty(Options.Startup))
                Post(() =>
                {
                    try
                    {
                        string s = Options.Startup!.Trim();
                        if (string.Equals(s, "auto", StringComparison.OrdinalIgnoreCase))
                        {
                            Boot.Request();
                            return;
                        }
                        StartCall(s.IndexOf('(') >= 0 ? s : s + "()", null);
                    }
                    catch (CallTextException e)
                    {
                        Emit("error", "startup " + Options.Startup + ": " + e.Message, kind: "startup");
                    }
                });
        }

        /// <summary>Run <paramref name="a"/> on the simulation thread (in order; at once when
        /// called on it).</summary>
        public void Post(Action a)
        {
            if (IsSimThread)
            {
                a();
                return;
            }
            Enqueue(a);
        }

        /// <summary>Run <paramref name="a"/> on the simulation thread after the command now
        /// running (queued also when called on that thread: one windowsChanged for the many
        /// window changes of a page open).</summary>
        internal void Defer(Action a) => Enqueue(a);

        private void Enqueue(Action a, bool debug = false)
        {
            lock (_cmdLock)
            {
                if (_ended || Volatile.Read(ref _disposed) != 0)
                    throw new ObjectDisposedException("the simulation has ended");
                (debug ? _debugCommands : _commands).Enqueue(a);
            }
            _signal.Set();
            if (debug)
                _debugSignal.Set();
        }

        /// <summary>Run <paramref name="fn"/> on the simulation thread and wait for its result
        /// (its exception is rethrown here). Before Start it runs on the calling thread.</summary>
        public T Invoke<T>(Func<T> fn, int timeoutMs = Timeout.Infinite)
        {
            if (IsSimThread || _thread == null)
                return fn();
            T result = default!;
            ExceptionDispatchInfo? error = null;
            var done = new ManualResetEventSlim(false);
            Enqueue(() =>
            {
                try
                {
                    result = fn();
                }
                catch (Exception e)
                {
                    error = ExceptionDispatchInfo.Capture(e);
                }
                finally
                {
                    done.Set();
                }
            });
            // not disposed here: after a timeout the command may still run and set it
            if (!done.Wait(timeoutMs))
                throw new TimeoutException(
                    "the simulation did not answer in "
                        + timeoutMs.ToString(CultureInfo.InvariantCulture)
                        + " ms"
                );
            done.Dispose();
            error?.Throw();
            return result;
        }

        public void Invoke(Action a, int timeoutMs = Timeout.Infinite) =>
            Invoke<int>(
                () =>
                {
                    a();
                    return 0;
                },
                timeoutMs
            );

        /// <summary>As <see cref="Invoke{T}"/> for the debugger (DebugTarget): also run while page
        /// code the debugger stopped holds the simulation thread (WaitForegroundStop), when the
        /// other clients' commands wait.</summary>
        public T InvokeDebug<T>(Func<T> fn, int timeoutMs = Timeout.Infinite)
        {
            if (IsSimThread || _thread == null)
                return fn();
            T result = default!;
            ExceptionDispatchInfo? error = null;
            var done = new ManualResetEventSlim(false);
            Enqueue(
                () =>
                {
                    try
                    {
                        result = fn();
                    }
                    catch (Exception e)
                    {
                        error = ExceptionDispatchInfo.Capture(e);
                    }
                    finally
                    {
                        done.Set();
                    }
                },
                debug: true
            );
            if (!done.Wait(timeoutMs))
                throw new TimeoutException(
                    "the simulation did not answer in "
                        + timeoutMs.ToString(CultureInfo.InvariantCulture)
                        + " ms"
                );
            done.Dispose();
            error?.Throw();
            return result;
        }

        public void InvokeDebug(Action a, int timeoutMs = Timeout.Infinite) =>
            InvokeDebug<int>(
                () =>
                {
                    a();
                    return 0;
                },
                timeoutMs
            );

        /// <summary>The page code task the debugger stopped and the simulation waits in, null =
        /// none (any thread: a snapshot).</summary>
        public CicodeTask? HeldBy => Volatile.Read(ref _heldBy);

        /// <summary>Wait inside the scan while the debugger has page code stopped.</summary>
        internal bool WaitForegroundStop(CicodeTask t)
        {
            if (!IsSimThread || _heldBy != null)
                return false;
            var clock = World.Clock;
            bool wasRunning = clock.Mode == SimClock.Run;
            if (wasRunning)
                clock.Pause();
            Volatile.Write(ref _heldBy, t);
            RaiseDebugPaused(t, true);
            try
            {
                while (t.Pause && !t.Done && !_stopping)
                {
                    RunDebugCommands();
                    if (!t.Pause || t.Done || _stopping)
                        break;
                    _debugSignal.WaitOne(50);
                }
            }
            finally
            {
                Volatile.Write(ref _heldBy, null);
                if (wasRunning && clock.Mode == SimClock.Paused)
                    clock.RunMode();
                RaiseDebugPaused(t, false);
            }
            return !t.Pause && !t.Done && !_stopping;
        }

        private void RaiseDebugPaused(CicodeTask t, bool paused)
        {
            var b = JsonValue.NewObject();
            b.Set("paused", JsonValue.Of(paused));
            b.Set("task", JsonValue.Of(t.Handle));
            b.Set("function", t.Func?.Name ?? "");
            b.Set("file", t.Func?.Filename ?? "");
            b.Set("line", JsonValue.Of(t.Line));
            b.Set("reason", t.StopReason ?? "");
            if (t.Data.ContainsKey(CicodeSim.World.Display.TaskContext.WindowKey))
            {
                int w = CicodeSim.World.Display.TaskContext.Window(t, -1);
                b.Set("window", JsonValue.Of(w));
                b.Set("an", JsonValue.Of(CicodeSim.World.Display.TaskContext.An(t)));
                string page = "";
                try
                {
                    page = w >= 0 ? World.Display.Get(w)?.Page?.Name ?? "" : "";
                }
                catch (Exception x) when (!(x is OutOfMemoryException)) { }
                b.Set("page", page);
                b.Set("source", t.Name ?? "");
            }
            Events.Post(() => Events.Each(DebugPaused, h => ((Action<JsonValue>)h)(b)));
        }

        private void RunDebugCommands()
        {
            while (_debugCommands.TryDequeue(out var a))
            {
                try
                {
                    a();
                }
                catch (Exception e)
                {
                    Emit(
                        "error",
                        "a debugger command failed: " + e.GetType().Name + ": " + e.Message,
                        kind: "internal"
                    );
                }
            }
        }

        private void Loop()
        {
            while (!_stopping)
            {
                RunCommands();
                if (_stopping)
                    break;
                var clock = World.Clock;
                bool ran;
                if (_steps.Count > 0)
                {
                    // a clock step moves on by bursts; the commands of other clients run in between
                    Guard(StepSome, "the clock step");
                    ran = true;
                }
                else
                {
                    if (clock.Mode == SimClock.Run)
                        Guard(PollClock, "the clock");
                    ran = RunReady();
                    if (Boot.Boosting)
                        Guard(Boot.Tick, "the startup boost");
                }
                long now = _wall.ElapsedMilliseconds;
                if (_flushNow || now - _lastFlush >= Options.ScanMs)
                    Guard(Flush, "sending the tag changes");
                if (!ran && _commands.IsEmpty)
                {
                    int wait;
                    try
                    {
                        wait = IdleWaitMs();
                    }
                    catch (Exception e)
                    {
                        // never a dead loop thread (that would end the process): log, wait a scan
                        Emit(
                            "error",
                            "the idle wait failed: " + e.GetType().Name + ": " + e.Message,
                            kind: "internal"
                        );
                        wait = Math.Max(1, Options.ScanMs);
                    }
                    _signal.WaitOne(wait);
                }
            }
            // the commands still queued run (their waiters return); later ones are refused
            lock (_cmdLock)
                _ended = true;
            RunCommands();
            // a step in progress answers with what it moved (its client does not wait for a
            // simulation that has ended)
            while (_steps.Count > 0)
                EndStep(_steps.Peek());
        }

        /// <summary>Run mode: move the clock towards the wall time, but never past the next
        /// wake-up of a waiting task: each task wakes at its own simulated time and its code
        /// runs before time moves on (a busy simulation lags the wall, then catches up).</summary>
        private void PollClock()
        {
            var clock = World.Clock;
            long target = clock.WallTargetMs();
            long now = clock.NowMs;
            if (target <= now)
                return;
            long? wake = EarliestWakeMs();
            if (wake != null && wake.Value < target)
                target = Math.Max(wake.Value, now + 1); // at least a ms: a wake-up that is due but not ready never stalls time
            if (target > now)
                clock.AdvanceToMs(target, rebase: false);
        }

        /// <summary>The earliest simulated ms a waiting (not debugger-stopped) task can wake by
        /// time, null when none waits on time.</summary>
        private long? EarliestWakeMs()
        {
            long? best = null;
            foreach (var t in Scheduler.All)
            {
                if (t.Done || t.Pause || (t.State != TaskState.Wait && t.State != TaskState.Sleep))
                    continue;
                long? w = WakeMs(t, out _);
                if (w != null && (best == null || w.Value < best.Value))
                    best = w;
            }
            return best;
        }

        private void RunCommands()
        {
            RunDebugCommands();
            while (_commands.TryDequeue(out var a))
            {
                try
                {
                    a();
                }
                catch (Exception e)
                {
                    Emit(
                        "error",
                        "a command failed: " + e.GetType().Name + ": " + e.Message,
                        kind: "internal"
                    );
                }
            }
        }

        private void Guard(Action a, string what)
        {
            try
            {
                a();
            }
            catch (Exception e)
            {
                Emit("error", what + " failed: " + e.GetType().Name + ": " + e.Message, kind: "internal");
            }
        }

        /// <summary>Run ready tasks for up to BurstMs of real time; false when none was ready.</summary>
        private bool RunReady()
        {
            long start = _wall.ElapsedMilliseconds;
            bool any = false;
            while (true)
            {
                bool stepped = StepOne();
                if (!stepped)
                    break;
                any = true;
                if (_wall.ElapsedMilliseconds - start >= BurstMs || !_commands.IsEmpty)
                    break;
            }
            return any;
        }

        /// <summary>One scheduler step; a VM fault (VmError ...) halts that task, it does not end
        /// the loop or a clock step. False when nothing ran.</summary>
        private bool StepOne()
        {
            try
            {
                return Scheduler.Step();
            }
            catch (Exception e)
            {
                // Vm.Current is restored by now: the task is the one the scheduler stepped. It
                // halts and is dropped at once (TaskEnded: a waiting ctsim/call gets its answer,
                // it never waits on a task left in state Run)
                var t = Scheduler.LastStepped;
                Emit("error", "the VM failed: " + e.GetType().Name + ": " + e.Message, t, "internal");
                if (t != null && !t.Done)
                {
                    t.State = TaskState.Halt;
                    t.HaltReason = "VM error: " + e.GetType().Name + ": " + e.Message;
                    t.Wait = null;
                }
                if (t == null)
                    return false; // not in a task: nothing to halt, do not spin on it
                try
                {
                    Scheduler.DropEnded();
                }
                catch (Exception e2)
                {
                    Emit("error", "dropping the failed task: " + e2.Message, kind: "internal");
                }
                return true; // something ran (the loop goes on with the other tasks)
            }
        }

        /// <summary>How long the idle loop may sleep: until the next wake-up in run mode, a
        /// short poll while some task waits on a condition, else a scan.</summary>
        private int IdleWaitMs()
        {
            var clock = World.Clock;
            int ms = Options.ScanMs;
            bool polls = false;
            long? next = clock.NextDueMs();
            foreach (var t in Scheduler.All)
            {
                if (t.Done || t.Pause || (t.State != TaskState.Wait && t.State != TaskState.Sleep))
                    continue;
                long? w = WakeMs(t, out bool poll);
                polls |= poll;
                if (w != null && (next == null || w.Value < next.Value))
                    next = w;
            }
            if (clock.Mode == SimClock.Run && next != null)
            {
                double? s = clock.WallSecondsUntil(next.Value);
                if (s != null)
                    ms = (int)Math.Min(ms, Math.Ceiling(s.Value * 1000.0));
            }
            if (polls)
                ms = Math.Min(ms, 20);
            return Math.Max(0, Math.Min(ms, Options.ScanMs));
        }

        /// <summary>The simulated ms at which a waiting task can wake (null: only a condition, or
        /// nothing, wakes it).</summary>
        internal long? WakeMs(CicodeTask t, out bool poll)
        {
            poll = false;
            var w = t.Wait;
            if (w == null)
                return World.Clock.NowMs;
            long? at = null;
            if (w.UntilMs.HasValue)
                at = (long)Math.Ceiling(w.UntilMs.Value - SimClock.MonotonicOriginMs);
            if (w.UntilSec.HasValue)
            {
                long ft = Clocks.EpochDiff + w.UntilSec.Value * 10_000_000L - World.Clock.StartFiletime;
                long ms = ft <= 0 ? 0 : (ft + 9_999) / 10_000; // the first ms whose FILETIME reaches the second
                at = at == null ? ms : Math.Max(at.Value, ms);
            }
            if (at == null && w.Poll != null)
                poll = true;
            return at;
        }

        // ------------------------------------------------------------------ events out
        private LogEntry Emit(
            string level,
            string text,
            CicodeTask? task = null,
            string kind = "",
            int code = 0,
            string call = "",
            string tag = "",
            bool halts = false,
            string haltReason = ""
        )
        {
            // (every field is set before the post: the pump's thread serialises the entry)
            var e = new LogEntry
            {
                Level = level,
                Text = text,
                Kind = kind,
                Code = code,
                Call = call,
                Tag = tag,
                Halts = halts,
                HaltReason = haltReason,
            };
            if (task != null)
            {
                e.Task = task.Handle;
                e.Function = task.Func?.Name ?? "";
                e.File = task.Func?.Filename ?? "";
                e.Line = task.Line;
                Where(e, task, level != "info" || kind == "hardware");
            }
            Events.Post(() => DeliverLog(e));
            return e;
        }

        /// <summary>On the event thread: delivering the log line of a failed handler (a handler
        /// that fails on that too is not reported again: no endless error loop).</summary>
        private bool _deliveringHandlerError;

        private void DeliverLog(LogEntry e)
        {
            bool outer = _deliveringHandlerError;
            _deliveringHandlerError = e.Kind == HandlerErrorKind;
            try
            {
                Events.Each(Log, h => ((Action<LogEntry>)h)(e));
            }
            finally
            {
                _deliveringHandlerError = outer;
            }
        }

        private const string HandlerErrorKind = "handler";

        private void OnHandlerError(Exception e)
        {
            if (_deliveringHandlerError)
                return;
            Emit(
                "error",
                "an event handler failed: " + e.GetType().Name + ": " + e.Message,
                kind: HandlerErrorKind
            );
        }

        /// <summary>A log line for every client (any thread).</summary>
        public void Note(string level, string text) => Emit(level, text, kind: "simulator");

        /// <summary>A log line's place: the call stack (when <paramref name="stack"/>) and the
        /// page object whose code the task runs. Never fails (a log line goes out regardless).</summary>
        private void Where(LogEntry e, CicodeTask task, bool stack)
        {
            try
            {
                if (stack)
                    foreach (var f in DebugInspect.Frames(task))
                    {
                        var sl = new StackLine
                        {
                            Function = f.Name,
                            File = f.File,
                            Line = f.Line,
                        };
                        int nargs = f.Func?.Args?.Length ?? 0;
                        if (nargs > 0)
                            foreach (var (name, slot, v) in DebugInspect.Locals(task, f))
                                if (slot < nargs)
                                    sl.Args.Add((name, ValueText(v)));
                        e.Stack.Add(sl);
                        if (e.Stack.Count >= 32)
                            break;
                    }
                if (task.Data.ContainsKey(CicodeSim.World.Display.TaskContext.WindowKey))
                {
                    e.Window = CicodeSim.World.Display.TaskContext.Window(task, -1);
                    e.An = CicodeSim.World.Display.TaskContext.An(task);
                    e.Source = task.Name ?? "";
                    e.Page = e.Window >= 0 ? World.Display.Get(e.Window)?.Page?.Name ?? "" : "";
                }
            }
            catch (Exception x) when (!(x is OutOfMemoryException)) { }
        }

        private void OnVmLog(string text)
        {
            // hardware alarms come with their task through OnErrorReport
            if (
                text.StartsWith("Hardware alarm", StringComparison.Ordinal)
                || text.StartsWith("SUPPRESSED hardware alarm", StringComparison.Ordinal)
            )
                return;
            Emit("info", text, Vm.Current, "message");
        }

        /// <summary>' (why)' when the halt has a reason beyond its code.</summary>
        private static string HaltWhy(CicodeTask task, int code)
        {
            string r = task.HaltReason ?? "";
            if (r.Length == 0 || r == "hardware error " + code.ToString(CultureInfo.InvariantCulture))
                return "";
            return " (" + r + ")";
        }

        private void OnErrorReport(CicodeTask task, int code, bool halting, bool suppressed)
        {
            if (task.NoDebug)
                return;
            // a halting Cicode error is a hardware alarm on the runtime (the SA / Tab_Style
            // Hardware pages list them): ON unacknowledged, the same code again counts up
            // (AlarmDb.RaiseHardware); its ErrPage the built-in it failed in, as the report says
            if (halting && !suppressed)
                RaiseCicodeHardwareAlarm(task, code);
            // foreground code (page expressions, every object every scan): an error ErrSet
            // suppresses is the code's own business (the library's _LibControl_* read missing
            // names on purpose), the others once per text per 10 s of real time (a page whose
            // expressions fail would otherwise send thousands of lines a scan). A background
            // task's error that does not halt it (suppressed, or one the task goes on after):
            // at most BackgroundBurst reports per 10 s (a task that loops over a failing call
            // would pass the outbound limit and the server would drop every viewer; a debugger
            // session that runs a failing call again still sees each); a halting error ends its
            // task and is always reported
            if (task.Foreground && (suppressed || !ReportDue(code, task, 1)))
                return;
            if (!task.Foreground && !halting && !ReportDue(code, task, BackgroundBurst))
                return;
            string page = task.CurrentBuiltin >= 0 ? Vm.Table.Name(task.CurrentBuiltin) : "";
            string text =
                (suppressed ? "SUPPRESSED hardware error (" : "Hardware error (")
                + code.ToString(CultureInfo.InvariantCulture)
                + ") '"
                + Errors.Text(code)
                + "' ErrPage: '"
                + page
                + "' ErrDesc: '"
                + (task.Func?.Name ?? "")
                + "' in task "
                + task.Handle.ToString(CultureInfo.InvariantCulture)
                + " "
                + task.Name
                + (halting ? ": the task halts" + HaltWhy(task, code) : "");
            string call = "",
                tag = "";
            try
            {
                if (page.Length > 0)
                {
                    var sb = new System.Text.StringBuilder(page).Append('(');
                    var args = task.CurrentArgs;
                    for (int k = 0; k < args.Count; k++)
                    {
                        if (k > 0)
                            sb.Append(", ");
                        sb.Append(ValueText(args[k]));
                    }
                    call = sb.Append(')').ToString();
                }
                else if (task.TagError != 0 && task.LastTagRef != null)
                {
                    var r = task.LastTagRef;
                    tag =
                        r.Tag
                        + (r.Item.Length > 0 ? "." + r.Item : "")
                        + (r.Element.Length > 0 ? "." + r.Element : "");
                }
            }
            catch (Exception x) when (!(x is OutOfMemoryException)) { }
            Emit(
                suppressed ? "info"
                    : halting ? "error"
                    : "warning",
                text,
                task,
                "hardware",
                code,
                call,
                tag,
                halts: halting,
                haltReason: halting && HaltWhy(task, code).Length > 0 ? task.HaltReason : ""
            );
        }

        private void RaiseCicodeHardwareAlarm(CicodeTask task, int code)
        {
            try
            {
                string page =
                    task.CurrentBuiltin >= 0 ? Vm.Table.Name(task.CurrentBuiltin) : (task.Func?.Name ?? "");
                World.Alarms.RaiseHardware(code, Errors.Text(code), "Cicode", page);
            }
            catch (Exception x) when (!(x is OutOfMemoryException)) { }
        }

        /// <summary>A value as Cicode text: a string quoted (cut at 200 characters), a number
        /// as written, a reference as 'ref'.</summary>
        internal static string ValueText(in Value v)
        {
            if (Types.IsRealType(v.T))
                return FloatText.Shortest(v.D);
            if (v.T == Types.String)
            {
                string s = v.S ?? "";
                if (s.Length > 200)
                    s = s.Substring(0, 200) + "...";
                return "\"" + s.Replace("\"", "^\"") + "\"";
            }
            if (v.T == Types.Variant)
                return "ref";
            return v.I.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Reports a background task's error that does not halt it may send per 10 s
        /// (by code, function and built-in).</summary>
        internal const int BackgroundBurst = 20;

        private readonly Dictionary<string, (long At, int Count)> _fgReported = new Dictionary<
            string,
            (long, int)
        >(StringComparer.Ordinal);

        /// <summary>An error report is due: fewer than <paramref name="burst"/> reports of it in
        /// the 10 s since its first one (by code, function and the built-in it failed in).</summary>
        private bool ReportDue(int code, CicodeTask task, int burst)
        {
            string key =
                code.ToString(CultureInfo.InvariantCulture)
                + "|"
                + (task.Func?.Name ?? task.Name)
                + "|"
                + (task.CurrentBuiltin >= 0 ? Vm.Table.Name(task.CurrentBuiltin) : "");
            // page code: per object (each object whose expression fails is reported, not only
            // the first of them)
            if (task.Data.ContainsKey(CicodeSim.World.Display.TaskContext.WindowKey))
                key +=
                    "|"
                    + CicodeSim
                        .World.Display.TaskContext.Window(task, -1)
                        .ToString(CultureInfo.InvariantCulture)
                    + "|"
                    + CicodeSim.World.Display.TaskContext.An(task).ToString(CultureInfo.InvariantCulture);
            long now = _wall.ElapsedMilliseconds;
            if (_fgReported.TryGetValue(key, out var r) && now - r.At < 10_000)
            {
                if (r.Count >= burst)
                    return false;
                _fgReported[key] = (r.At, r.Count + 1);
                return true;
            }
            if (_fgReported.Count > 10_000)
                _fgReported.Clear();
            _fgReported[key] = (now, 1);
            return true;
        }

        private void OnTaskEnded(CicodeTask t)
        {
            if (t.State == TaskState.Halt && t.HaltCode == 0 && !t.NoDebug)
                Emit(
                    t.HaltReason.Contains("not simulated") ? "warning" : "error",
                    "task "
                        + t.Handle.ToString(CultureInfo.InvariantCulture)
                        + " "
                        + t.Name
                        + " halted: "
                        + t.HaltReason,
                    t,
                    "task"
                );
            if (!t.NoDebug)
            {
                var s = TaskSummary.Of(t);
                Events.Post(() => Events.Each(TaskExited, h => ((Action<TaskSummary>)h)(s)));
            }
            if (_onEnd.TryGetValue(t, out var list))
            {
                _onEnd.Remove(t);
                foreach (var cb in list)
                {
                    try
                    {
                        cb(t);
                    }
                    catch (Exception e)
                    {
                        Emit("error", "a call completion failed: " + e.Message, kind: "internal");
                    }
                }
            }
            Debugger.OnTaskEnded(t);
        }

        /// <summary>Call <paramref name="cb"/> on the simulation thread when the task ends (at
        /// once when it has).</summary>
        public void WhenEnded(CicodeTask t, Action<CicodeTask> cb)
        {
            if (t.Done && !Scheduler.Tasks.ContainsKey(t.Handle))
            {
                cb(t);
                return;
            }
            if (!_onEnd.TryGetValue(t, out var list))
                _onEnd[t] = list = new List<Action<CicodeTask>>();
            list.Add(cb);
        }

        /// <summary>Send the changed tags (and new not-simulated counts) now.</summary>
        private void Flush()
        {
            _flushNow = false;
            _lastFlush = _wall.ElapsedMilliseconds;
            var alarms = _alarmKeys.Count > 0 ? AlarmChanges() : null;
            if (_changedTags.Count > 0 || alarms != null)
            {
                var list = new List<TagUpdate>(_changedTags.Count);
                if (alarms != null)
                    list.AddRange(alarms);
                string? failed = null;
                foreach (string n in _changedTags)
                {
                    try
                    {
                        list.Add(ReadTag(n, hover: true));
                    }
                    catch (Exception e)
                    {
                        failed ??= n + ": " + e.GetType().Name + ": " + e.Message;
                    }
                }
                // the watched names of those tags that are not their plain value ('Tag.Q' ...):
                // each read by its own name at this scan
                if (_watched.Count > 0)
                    foreach (string n in _changedTags)
                    {
                        if (!_watched.TryGetValue(n, out var names))
                            continue;
                        foreach (string a in names.Keys)
                        {
                            try
                            {
                                var u = ReadTag(a, hover: true);
                                u.Asked = a;
                                list.Add(u);
                            }
                            catch (Exception e)
                            {
                                failed ??= a + ": " + e.GetType().Name + ": " + e.Message;
                            }
                        }
                    }
                _changedTags.Clear();
                if (failed != null)
                    Emit("error", "a changed tag could not be read: " + failed, kind: "internal");
                var batch = new TagBatch { Updates = list, Clock = ClockState() };
                Events.Post(() => Events.Each(TagsChanged, h => ((Action<TagBatch>)h)(batch)));
            }
            foreach (var kv in Vm.NotSimulatedCounts)
            {
                if (_nsSent.TryGetValue(kv.Key, out int sent) && sent == kv.Value)
                    continue;
                _nsSent[kv.Key] = kv.Value;
                string name = kv.Key;
                int count = kv.Value;
                Events.Post(() => Events.Each(NotSimulated, h => ((Action<string, int>)h)(name, count)));
            }
        }

        /// <summary>The watched alarm properties ('Alarm.State' ...) whose value or quality
        /// changed since they were last sent (they change with no tag write: the alarm scan,
        /// an acknowledge), each read by its own name; null when none changed.</summary>
        private List<TagUpdate>? AlarmChanges()
        {
            List<TagUpdate>? o = null;
            foreach (string key in _alarmKeys)
            {
                if (!_watched.TryGetValue(key, out var names))
                    continue;
                foreach (string a in names.Keys)
                {
                    TagUpdate u;
                    try
                    {
                        u = ReadTag(a, hover: true);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    string sig = u.Value.ToString() + "|" + u.Quality;
                    if (_alarmSent.TryGetValue(a, out string? last) && last == sig)
                        continue;
                    _alarmSent[a] = sig;
                    u.Asked = a;
                    (o ??= new List<TagUpdate>()).Add(u);
                }
            }
            return o;
        }

        /// <summary>Watched names that are alarm properties (by the defined name).</summary>
        private readonly HashSet<string> _alarmKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>What each watched alarm property's name last sent (value | quality).</summary>
        private readonly Dictionary<string, string> _alarmSent = new Dictionary<string, string>(
            StringComparer.Ordinal
        );

        /// <summary>Send the batch at the loop's next turn (after a command that changed tags).</summary>
        public void FlushSoon()
        {
            _flushNow = true;
            _signal.Set();
        }

        // ------------------------------------------------------------------ tags
        /// <summary>A tag's value as viewers see it. hover = do not mark it as read by anyone.</summary>
        public TagUpdate ReadTag(string name, bool hover = true)
        {
            var u = new TagUpdate { Name = name };
            var db = World.Tags;
            var r = db.Resolve(name);
            if (!r.Found)
            {
                u.Defined = false;
                u.Quality = WQuality.Name(WQuality.QualityTagNotFound);
                return u;
            }
            if (r.Name.Length > 0)
                u.Name = r.Name;
            Value v = db.ReadResolved(r, null, false, hover ? "" : "viewer", out _);
            u.Value = ValueFormat.Json(v);
            u.Quality = WQuality.Name(v.Q);
            u.Timestamp = ValueFormat.IsoFiletime(v.Ts);
            return u;
        }

        /// <summary>Is this a subscribed name that isn't its tag's plain value?</summary>
        public bool IsOwnRead(string name)
        {
            var r = World.Tags.Resolve(name);
            return r.Found
                && (r.Kind == "alarm" || r.Element.Length > 0 || r.Item.Length > 0 || r.Index != null);
        }

        /// <summary>Count a watcher of an own-read name (see <see cref="IsOwnRead"/>) in or out:
        /// while counted, every batch in which its tag changed also holds an entry read by that
        /// name (<see cref="TagUpdate.Asked"/>). Simulation thread.</summary>
        public void Watch(string name, bool on)
        {
            var r = World.Tags.Resolve(name);
            if (!r.Found || r.Name.Length == 0)
                return;
            if (!_watched.TryGetValue(r.Name, out var names))
            {
                if (!on)
                    return;
                _watched[r.Name] = names = new Dictionary<string, int>(StringComparer.Ordinal);
            }
            names.TryGetValue(name, out int n);
            n += on ? 1 : -1;
            if (n > 0)
                names[name] = n;
            else
            {
                names.Remove(name);
                _alarmSent.Remove(name);
            }
            if (r.Kind == "alarm")
            {
                _alarmKeys.Add(r.Name);
                // the value the subscribe reply carries: no batch repeats it
                if (on && n == 1 && !_alarmSent.ContainsKey(name))
                {
                    var u = ReadTag(name, hover: true);
                    _alarmSent[name] = u.Value.ToString() + "|" + u.Quality;
                }
            }
            if (names.Count == 0)
            {
                _watched.Remove(r.Name);
                _alarmKeys.Remove(r.Name);
            }
        }

        /// <summary>How many own-read names are watched (tests).</summary>
        internal int WatchedCount
        {
            get
            {
                int n = 0;
                foreach (var d in _watched.Values)
                    n += d.Count;
                return n;
            }
        }

        /// <summary>Set a tag as a device would. Returns 0 or the error.</summary>
        public int SetTag(
            string name,
            JsonValue? value,
            JsonValue? quality = null,
            string element = "",
            int? index = null,
            string? type = null
        )
        {
            int err = World.Tags.Set(name, value, quality, by: "user", element: element, index: index);
            if (
                err == TagDb.ErrTagNotFound
                && World.Tags.SimulateUndefined
                && element.Length == 0
                && TagDb.SimulatableName(CicodeSim.Project.CitectText.Trim(name))
            )
            {
                string t = !string.IsNullOrWhiteSpace(type)
                    ? type!.Trim().ToUpperInvariant()
                    : GuessType(name) ?? TagUse.Real;
                var made = World.Tags.AddSimulated(name, t, "simulated: not defined in the project");
                if (made != null)
                {
                    Emit(
                        "info",
                        "simulated tag "
                            + made.Name
                            + " ("
                            + made.TypeName
                            + "): the project does not define it",
                        kind: "simulator"
                    );
                    err = World.Tags.Set(name, value, quality, by: "user", element: element, index: index);
                }
            }
            FlushSoon();
            return err;
        }

        /// <summary>Simulator only: whether trends show a pen's first known value before the
        /// simulation started (TrendTags.SeedHistory). Simulation thread.</summary>
        public bool SeedTrendHistory
        {
            get => TrendTags.Of(World).SeedHistory;
            set
            {
                var tt = TrendTags.Of(World);
                if (tt.SeedHistory == value)
                    return;
                tt.SeedHistory = value;
                Emit(
                    "info",
                    value
                        ? "seeding trend history: a trend shows a pen's first value before the simulation started"
                        : "not seeding trend history: a trend has no samples before the simulation started",
                    kind: "simulator"
                );
                FlushSoon();
            }
        }

        /// <summary>Where the simulation spends its time (ctsim/profile): off until StartProfile.</summary>
        public readonly Profiler Profile = new Profiler();

        /// <summary>Start timing the page expressions, the scans and the tasks' slices (the
        /// hooks on the animator and the scheduler). Simulation thread.</summary>
        public void StartProfile()
        {
            var a = Pages.EnsureAnimator();
            a.EvalTimed = (w, an, cls, ticks) => Profile.Expression(w, an, cls, ticks);
            a.ScanTimed = (w, page, ticks) => Profile.Scan(w, page, ticks);
            Scheduler.SliceTimed = (t, ticks) => Profile.Task(t.Name, ticks);
            Vm.Functions = Profile.Functions;
            Profile.Start();
        }

        /// <summary>Stop timing (the report keeps what was timed). Simulation thread.</summary>
        public void StopProfile()
        {
            var a = Pages.EnsureAnimator();
            a.EvalTimed = null;
            a.ScanTimed = null;
            Scheduler.SliceTimed = null;
            Vm.Functions = null;
            Profile.Stop();
        }

        /// <summary>Simulator only: whether a hardware error in page code stops it
        /// in the debugger (with ForegroundBreak; DebugOptions.ForegroundErrorBreak, off by
        /// default: reported once, the page runs on). Simulation thread.</summary>
        public bool ForegroundErrorBreak
        {
            get => Stepper.Options.ForegroundErrorBreak;
            set
            {
                if (Stepper.Options.ForegroundErrorBreak == value)
                    return;
                Stepper.Options.ForegroundErrorBreak = value;
                Emit(
                    "info",
                    value
                        ? "the debugger stops page code on a hardware error"
                        : "the debugger does not stop page code on a hardware error (it is reported once, the page runs on)",
                    kind: "simulator"
                );
            }
        }

        /// <summary>Simulator only: whether the debugger stops page code.</summary>
        public bool ForegroundBreak
        {
            get => Stepper.Options.ForegroundCanBreak;
            set
            {
                if (Stepper.Options.ForegroundCanBreak == value)
                    return;
                Stepper.Options.ForegroundCanBreak = value;
                Emit(
                    "info",
                    value
                        ? "the debugger stops page code: a breakpoint in an expression or a function it calls holds the simulation"
                        : "the debugger does not stop page code (the runtime's rule: 'Foreground Cicode cannot break')",
                    kind: "simulator"
                );
            }
        }

        /// <summary>Simulator only: whether setTag makes simulated tags for undefined
        /// names, and whether the ones made are found. Simulation thread.</summary>
        public bool SimulateUndefined
        {
            get => World.Tags.SimulateUndefined;
            set
            {
                if (World.Tags.SimulateUndefined == value)
                    return;
                World.Tags.SimulateUndefined = value;
                Emit(
                    "info",
                    value
                        ? "simulating undefined tags: a value set for an undefined name makes a simulated tag"
                        : "not simulating undefined tags: undefined names are error 424 again",
                    kind: "simulator"
                );
                FlushSoon();
            }
        }

        // lower-case tag name -> the type its code suggests (pages seen so far, then the library)
        private readonly Dictionary<string, string> _guesses = new Dictionary<string, string>(
            StringComparer.Ordinal
        );
        private Dictionary<string, string>? _libraryGuesses;

        /// <summary>Remember the types a page's code suggests (ctsim/tags of a page, an open).</summary>
        public void NoteGuesses(Dictionary<string, string> guesses)
        {
            foreach (var kv in guesses)
                if (!_guesses.ContainsKey(kv.Key))
                    _guesses[kv.Key] = kv.Value;
        }

        /// <summary>The type the code suggests for a tag name (TagUse): the pages seen so far and
        /// the pages open now, else the library's functions; null when no code uses it as a value.
        /// Simulation thread.</summary>
        public string? GuessType(string name)
        {
            string key = CicodeSim.Project.CitectText.Trim(name ?? "").ToLowerInvariant();
            if (_guesses.TryGetValue(key, out string? g))
                return g;
            var a = Pages.Animator;
            if (a != null)
                foreach (var w in a.Display.Windows)
                {
                    var run = w.Open ? a.Run(w.Number) : null;
                    if (run == null)
                        continue;
                    NoteGuesses(TagUse.Guess(run.Compiled.Program, TagUse.PageBlocks(run.Compiled), Library));
                }
            if (_guesses.TryGetValue(key, out g))
                return g;
            _libraryGuesses ??= TagUse.GuessLibrary(Library);
            return _libraryGuesses.TryGetValue(key, out g) ? g : null;
        }

        // ------------------------------------------------------------------ calls
        /// <summary>Split call text into the function name and the argument text: one call
        /// 'Name(args)' (an optional ';' after it). CallTextException 292 for anything else (live:
        /// ctCicode refuses bare expressions, literals and labels with 292).</summary>
        public static void SplitCall(string expr, out string name, out string args)
        {
            string s = (expr ?? "").Trim();
            if (s.EndsWith(";", StringComparison.Ordinal))
                s = s.Substring(0, s.Length - 1).TrimEnd();
            int open = s.IndexOf('(');
            if (open <= 0 || !s.EndsWith(")", StringComparison.Ordinal))
                throw new CallTextException(
                    Errors.InvalidFunction,
                    "not a function call: "
                        + Project.CitectText.Quote(s)
                        + " (call text is one call with literal arguments)"
                );
            name = s.Substring(0, open).Trim();
            foreach (char c in name)
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '\\' || c == '@' || c > 0x7f))
                    throw new CallTextException(
                        Errors.InvalidFunction,
                        "not a function name: " + Project.CitectText.Quote(name)
                    );
            // the argument text up to the matching ')' (strings with ^ escapes skipped)
            int depth = 0;
            bool str = false;
            int close = -1;
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (str)
                {
                    if (c == '^' && i + 1 < s.Length)
                    {
                        i++;
                        continue;
                    }
                    if (c == '"')
                        str = false;
                    continue;
                }
                if (c == '"')
                    str = true;
                else if (c == '(')
                    depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        close = i;
                        break;
                    }
                }
            }
            if (close != s.Length - 1)
                throw new CallTextException(
                    Errors.InvalidFunction,
                    "not one function call: " + Project.CitectText.Quote(s)
                );
            args = s.Substring(open + 1, close - open - 1);
        }

        /// <summary>Check a page field against the compiled unit.</summary>
        public JsonValue CheckField(string text, string kind, string project)
        {
            var body = JsonValue.NewObject();
            var errors = JsonValue.NewArray();
            Compiler.Semantics.CompileUnit unit;
            Compiler.Codegen.TagTable tags;
            try
            {
                var src = Live.EnsureSource();
                unit = src.Unit;
                tags = src.Tags;
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                body.Set("ok", JsonValue.False);
                var one = JsonValue.NewObject();
                one.Set("code", JsonValue.Of(0L));
                one.Set("name", JsonValue.Of(""));
                one.Set("message", JsonValue.Of("the project's code could not be read: " + e.Message));
                one.Set("column", JsonValue.Of(0L));
                errors.Add(one);
                body.Set("errors", errors);
                return body;
            }
            var res = unit.CompileField(text ?? "", kind, project.Length > 0 ? project : ProjectName);
            foreach (var d in res.Errors)
            {
                var one = JsonValue.NewObject();
                one.Set("code", JsonValue.Of((long)d.Code));
                one.Set("name", JsonValue.Of(d.Name));
                one.Set("message", JsonValue.Of(d.Message));
                one.Set("column", JsonValue.Of((long)Math.Max(0, d.Col)));
                errors.Add(one);
            }
            var warnings = JsonValue.NewArray();
            if (errors.Count == 0 && res.Node != null)
            {
                // the code generation finds what the parse cannot (a function with the wrong
                // number of arguments); a name it takes for a tag that the project does not
                // define compiles (the runtime looks it up when the page runs: error 424 then)
                // and is a warning here
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    Compiler.Codegen.FunctionCode fc;
                    lock (unit)
                        fc = Compiler.Codegen.FunctionGen.PageField(
                            unit,
                            tags,
                            res.Node,
                            kind == "command"
                                ? Compiler.Codegen.PageFieldKind.Command
                                : Compiler.Codegen.PageFieldKind.Expression
                        );
                    var names = new List<Compiler.Syntax.Name>();
                    Compiler.Semantics.NameArgs.Visit(res.Node, e =>
                    {
                        if (e is Compiler.Syntax.Name n && n.Ref is Compiler.Syntax.TagRefName)
                            names.Add(n);
                    });
                    foreach (var ins in fc.Code.Ins)
                    {
                        if (
                            !(ins.Ref is Compiler.Codegen.TagKey tk)
                            || tk.Tag.Length == 0
                            || !seen.Add(tk.Tag)
                        )
                            continue;
                        if (tags.Lookup(tk.Tag) != null || World.Tags.SimulateUndefined)
                            continue;
                        var at = names.Find(n =>
                            string.Equals(
                                ((Compiler.Syntax.TagRefName)n.Ref!).Name,
                                tk.Tag,
                                StringComparison.OrdinalIgnoreCase
                            )
                        );
                        warnings.Add(UndefinedTag(tk.Tag, at != null ? res.Column(at) : 0));
                    }
                }
                catch (Compiler.Codegen.CodegenException e)
                {
                    var one = JsonValue.NewObject();
                    one.Set("code", JsonValue.Of(0L));
                    one.Set("name", JsonValue.Of(""));
                    one.Set("message", JsonValue.Of(e.Message));
                    one.Set("column", JsonValue.Of(0L));
                    errors.Add(one);
                }
                if (errors.Count == 0)
                    FixedNameWarnings(unit, res.Node, res.Column, seen, warnings);
            }
            // in source order (a stable sort: the code generation's tags come first)
            var ordered = JsonValue.NewArray();
            foreach (var w in System.Linq.Enumerable.OrderBy(warnings.Items, w => w["column"].Long))
                ordered.Add(w);
            body.Set("ok", JsonValue.Of(errors.Count == 0));
            body.Set("errors", errors);
            body.Set("warnings", ordered);
            return body;
        }

        /// <summary>ctsim/checkExpression's warning for a name the project defines no tag of.</summary>
        private static JsonValue UndefinedTag(string name, int column)
        {
            var one = JsonValue.NewObject();
            one.Set("kind", JsonValue.Of("tag"));
            one.Set("name", JsonValue.Of(name));
            one.Set(
                "message",
                JsonValue.Of(
                    "the project does not define the tag " + name + " (the runtime raises error 424 for it)"
                )
            );
            one.Set("column", JsonValue.Of((long)column));
            return one;
        }

        /// <summary>The fixed text of a field that a built-in looks up (NameArgs: a string literal
        /// reaching _PageDisplay's page, TagRead's tag ..., directly or through the functions
        /// passing it on) and the project has no page / tag of: warnings. Compile time only, the
        /// tag found as the runtime's lookup finds it (Ass: its store check's 424).</summary>
        internal void FixedNameWarnings(
            Compiler.Semantics.CompileUnit unit,
            object? code,
            Func<Compiler.Syntax.Node, int> columnOf,
            HashSet<string> seenTags,
            JsonValue warnings
        )
        {
            List<Compiler.Semantics.NameUse> uses;
            lock (unit)
                uses = Compiler.Semantics.NameArgs.For(unit, WorldBuiltins.ArgRole).Literals(code);
            var seenPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var db = World.Tags;
            foreach (var u in uses)
            {
                string name = Project.CitectText.Trim(u.Literal.Value);
                int column = columnOf(u.Literal);
                if (u.Role == Compiler.Semantics.NameRole.Page)
                {
                    if (!seenPages.Add(name) || Pages.PageExists(name))
                        continue;
                    var one = JsonValue.NewObject();
                    one.Set("kind", JsonValue.Of("page"));
                    one.Set("name", JsonValue.Of(name));
                    one.Set(
                        "message",
                        JsonValue.Of("the project has no page " + name + " (the runtime cannot display it)")
                    );
                    one.Set("column", JsonValue.Of((long)column));
                    warnings.Add(one);
                    continue;
                }
                if (db.SimulateUndefined || seenTags.Contains(name))
                    continue;
                bool missing;
                if (string.Equals(u.Primitive, "Ass", StringComparison.OrdinalIgnoreCase))
                    // a ' literal is no tag; a dotted name no tag finds is 289 (an equipment
                    // name: the SA faceplates pass them), no warning; nor a trend tag's name
                    // (Include's AssPopTrend passes its pens to Ass and to the trend)
                    missing =
                        name[0] != '\''
                        && CicodeSim.World.Display.AssRules.StoreCheck(World, name, "", out _, out _) == 424
                        && CicodeSim.World.TrendTags.Of(World).Find(name) == null;
                else
                    missing = !db.NotUsed(() => db.Resolve(name, "", "", "")).Found;
                if (!missing)
                    continue;
                seenTags.Add(name);
                warnings.Add(UndefinedTag(name, column));
            }
        }

        public CicodeTask StartCall(
            string expr,
            Action<CallOutcome>? done,
            bool noDebug = false,
            string? taskName = null,
            int window = -1
        )
        {
            SplitCall(expr, out string name, out string argText);
            CicodeTask task;
            var fn = Library.Function(name);
            if (fn != null && fn.Private)
                fn = null;
            if (fn != null)
            {
                var vals = CallText.Bind(fn, argText, out int err, out string msg);
                if (err == Errors.TypeMismatch || err == Errors.TooFewArgs)
                    throw new CallTextException(err, msg.Length > 0 ? msg : Errors.Text(err));
                if (msg.Length > 0)
                    Emit("info", msg, kind: "simulator");
                task = Vm.TaskForCall(fn, vals, false, null, taskName ?? fn.Name);
            }
            else
            {
                var b = Vm.Table.Lookup(name);
                if (b == null)
                    throw new CallTextException(
                        Errors.InvalidFunction,
                        "no function " + Project.CitectText.Quote(name) + " (Invalid function)"
                    );
                for (int k = 0; k < b.Args.Length; k++)
                    if (b.ByRef(k))
                        throw new CallTextException(
                            Errors.TypeMismatch,
                            b.Name
                                + " takes argument "
                                + (k + 1).ToString(CultureInfo.InvariantCulture)
                                + " by reference: call text cannot give one"
                        );
                var sig = new Compiled.Function(b.Name, b.Args, b.Ret, Array.Empty<byte>())
                {
                    MinParam = b.MinArgs,
                };
                var vals = CallText.Bind(sig, argText, out int err, out string msg);
                if (err == Errors.TypeMismatch || err == Errors.TooFewArgs)
                    throw new CallTextException(err, msg.Length > 0 ? msg : Errors.Text(err));
                var pools = new Pools();
                var cb = new CodeBuilder(pools);
                foreach (var v in vals)
                {
                    if (v.T == Types.String)
                        cb.ConstStr(v.S ?? "");
                    else if (Types.IsRealType(v.T))
                        cb.ConstReal(v.D);
                    else if (v.T == Types.Timestamp || v.T == Types.Quality)
                        cb.ConstU64(v.T, unchecked((ulong)v.I));
                    else
                        cb.ConstInt(v.I);
                }
                cb.Func(b.Index, vals.Count);
                cb.End();
                var prog = pools.Fill(new CProgram());
                task = Vm.TaskForBlock(cb.Code(), prog, false, taskName ?? b.Name);
            }
            task.NoDebug = noDebug;
            if (task.Done)
                throw new CallTextException(
                    task.HaltCode != 0 ? task.HaltCode : Errors.InvalidFunction,
                    task.HaltReason.Length > 0 ? task.HaltReason : "the call cannot run"
                );
            if (window >= 0)
                CicodeSim.World.Display.TaskContext.Set(task, window, -1);
            Scheduler.Add(task);
            if (done != null)
                WhenEnded(task, t => done(Outcome(t)));
            _signal.Set();
            return task;
        }

        /// <summary>What a finished (or still running) call task gave.</summary>
        public CallOutcome Outcome(CicodeTask t)
        {
            var o = new CallOutcome { Task = t.Handle };
            if (!t.Done)
            {
                o.State = "running";
                return o;
            }
            o.State =
                t.State == TaskState.Stop ? "stop"
                : t.State == TaskState.Halt ? "halt"
                : TaskState.Name(t.State).ToLowerInvariant();
            if (t.State == TaskState.Halt)
            {
                o.HaltReason = t.HaltReason;
                if (t.HaltReason.Contains("not simulated"))
                {
                    int a = t.HaltReason.IndexOf("function ", StringComparison.Ordinal);
                    int b = t.HaltReason.IndexOf(" is not simulated", StringComparison.Ordinal);
                    o.NotSimulated = a >= 0 && b > a ? t.HaltReason.Substring(a + 9, b - a - 9) : "";
                }
            }
            o.Error = t.State == TaskState.Halt && t.HaltCode != 0 ? t.HaltCode : t.Error;
            if (o.Error != 0)
                o.ErrorText = Errors.Text(o.Error);
            if (t.State == TaskState.Stop && t.Result.HasValue && t.Result.Value.T != Types.Void)
            {
                Value v = t.Result.Value;
                if (v.T == Types.Variant)
                    v = Value.Deref(v);
                o.Value = ValueFormat.Json(v);
                o.Type = ValueFormat.TypeName(v.T);
                o.Result = Conv.ToStr(v, NoErr.Instance).S ?? "";
            }
            return o;
        }

        // ------------------------------------------------------------------ the clock
        // ------------------------------------------------------------------ the simulated user
        /// <summary>The name ctsim/user {mode: "full"} logs in as.</summary>
        public const string FullAccessUser = "(full access)";

        /// <summary>{mode: "full" | "none" | "user", name, fullName, roles}: who is logged in
        /// ("full" = the simulator's every-privilege user, "none" = nobody: the runtime's default
        /// user, area 0 and privilege 0 only).</summary>
        public JsonValue UserJson()
        {
            var s = World.Users.Session;
            var o = JsonValue.NewObject();
            string mode =
                s.IsDefault ? "none"
                : s.Name == FullAccessUser ? "full"
                : "user";
            o.Set("mode", mode);
            o.Set("name", mode == "none" ? "" : s.Name);
            // a full name is a display string: its '@(native)' texts as the window shows them
            o.Set("fullName", mode == "none" ? "" : World.Display.Language.Localise(s.FullName));
            var roles = JsonValue.NewArray();
            foreach (string r in s.Roles)
                roles.Add(JsonValue.Of(r));
            o.Set("roles", roles);
            return o;
        }

        // ------------------------------------------------------------------ tags code used
        private sealed class Used
        {
            public string Page = "";
            public readonly HashSet<string> Seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Order = new List<string>();

            /// <summary>Parallel to Order: the function that first used the name ('' = the
            /// page's own expression or command), its file and line.</summary>
            public readonly List<(string Function, string File, int Line)> By =
                new List<(string, string, int)>();
        }

        /// <summary>The tag names each window's code used at run time (its page's expressions,
        /// commands and the functions they call, the tasks it started), first use first.</summary>
        private readonly Dictionary<int, Used> _used = new Dictionary<int, Used>();
        private const int UsedMax = 20000;

        /// <summary>TagDb.Used: the task running now names its window (TaskContext).</summary>
        private void OnTagUsed(string name)
        {
            var t = Vm.Current;
            if (t == null || !t.Data.ContainsKey(CicodeSim.World.Display.TaskContext.WindowKey))
                return;
            int w = CicodeSim.World.Display.TaskContext.Window(t, -1);
            if (w < 0)
                return;
            string page = World.Display.Get(w)?.Page?.Name ?? "";
            if (
                !_used.TryGetValue(w, out var u)
                || !string.Equals(u.Page, page, StringComparison.OrdinalIgnoreCase)
            )
            {
                u = new Used { Page = page };
                _used[w] = u;
            }
            if (u.Order.Count < UsedMax && u.Seen.Add(name))
            {
                u.Order.Add(name);
                u.By.Add((t.Func?.Name ?? "", t.Func?.Filename ?? "", t.Func != null ? t.Line : 0));
            }
        }

        /// <summary>ctsim/usedTags {window, from}: {window, page, count, names, by} (the names the
        /// window's code used, from index <paramref name="from"/> on; by[i] = the function, file
        /// and line that first used names[i]).</summary>
        public JsonValue UsedTagsJson(int window, int from)
        {
            var b = JsonValue.NewObject();
            b.Set("window", JsonValue.Of(window));
            string page = World.Display.Get(window)?.Page?.Name ?? "";
            b.Set("page", page);
            var arr = JsonValue.NewArray();
            var by = JsonValue.NewArray();
            int count = 0;
            if (
                _used.TryGetValue(window, out var u)
                && string.Equals(u.Page, page, StringComparison.OrdinalIgnoreCase)
            )
            {
                count = u.Order.Count;
                for (int k = Math.Max(0, from); k < count; k++)
                {
                    arr.Add(JsonValue.Of(u.Order[k]));
                    var (fn, file, line) = u.By[k];
                    var o = JsonValue.NewObject();
                    o.Set("function", fn);
                    o.Set("file", file);
                    o.Set("line", JsonValue.Of(line));
                    by.Add(o);
                }
            }
            b.Set("count", JsonValue.Of(count));
            b.Set("names", arr);
            b.Set("by", by);
            return b;
        }

        /// <summary>ctsim/params: {serial, params: [{section, name, value, default, source,
        /// reads, writes, users}]} (the parameters Cicode read or wrote, first-read order).</summary>
        public JsonValue ParamsJson()
        {
            var b = JsonValue.NewObject();
            var arr = JsonValue.NewArray();
            var d = CicodeSim.Builtins.ProcessData.Existing(Host);
            b.Set("serial", JsonValue.Of(d?.ParamSerial ?? 0));
            if (d != null)
                foreach (var u in d.ParamOrder)
                {
                    var o = JsonValue.NewObject();
                    o.Set("section", u.Section);
                    o.Set("name", u.Name);
                    o.Set("value", u.Value);
                    o.Set("default", u.Default);
                    o.Set("source", u.Source);
                    o.Set("reads", JsonValue.Of(u.Reads));
                    o.Set("writes", JsonValue.Of(u.Writes));
                    var us = JsonValue.NewArray();
                    foreach (string w in u.Users)
                        us.Add(JsonValue.Of(w));
                    o.Set("users", us);
                    arr.Add(o);
                }
            b.Set("params", arr);
            return b;
        }

        /// <summary>ctsim/setParam {section, name, value}: the value every later ParameterGet of
        /// it reads (the process's copy of citect.ini, as ParameterPut; '' = back to the
        /// project's value). Returns ParamsJson().</summary>
        public JsonValue SetParam(string section, string name, string value)
        {
            if (section.Length == 0 || name.Length == 0)
                throw new ArgumentException("setParam needs a section and a name");
            var d = CicodeSim.Builtins.ProcessData.Of(Host);
            d.Ini.Put(CicodeSim.Builtins.IniFiles.CitectIniKey, section, name, value);
            var u = d.UseOf(section, name);
            if (value.Length > 0)
            {
                u.Value = value;
                u.Source = "simulator";
            }
            else if (u.Source == "simulator")
                u.Source = "";
            d.ParamSerial++;
            return ParamsJson();
        }

        /// <summary>ctsim/users: {user: UserJson(), users: [{name, fullName, roles}]} (the
        /// project's users.dbf over the unit).</summary>
        public JsonValue UsersJson()
        {
            var arr = JsonValue.NewArray();
            var defs = new List<UserDef>(World.Users.UserDefs().Values);
            defs.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));
            foreach (var u in defs)
            {
                var o = JsonValue.NewObject();
                o.Set("name", u.Name);
                o.Set("fullName", World.Display.Language.Localise(u.FullName));
                var roles = JsonValue.NewArray();
                foreach (string r in u.Roles)
                    roles.Add(JsonValue.Of(r));
                o.Set("roles", roles);
                arr.Add(o);
            }
            var b = JsonValue.NewObject();
            b.Set("user", UserJson());
            b.Set("users", arr);
            return b;
        }

        /// <summary>ctsim/user: log in as full access, log out, or log in as a user.</summary>
        public JsonValue SetUser(JsonValue a)
        {
            string mode = (a.Get("mode")?.Str ?? "").Trim().ToLowerInvariant();
            switch (mode)
            {
                case "full":
                    World.Users.LoginAs(
                        FullAccessUser,
                        "Full access (simulator)",
                        JsonValue.Of("1..8"),
                        JsonValue.Of("0..255")
                    );
                    break;
                case "none":
                    World.Users.Logout();
                    break;
                case "user":
                    string name = a.Get("name")?.Str ?? "";
                    if (World.Users.Login(name) != 0)
                        throw new ArgumentException("no user '" + name + "' in the project's users");
                    break;
                default:
                    throw new ArgumentException("user mode must be 'full', 'none' or 'user'");
            }
            return UserJson();
        }

        // ------------------------------------------------------------------ locks
        private readonly Dictionary<string, double> _lockNoted = new Dictionary<string, double>(
            StringComparer.OrdinalIgnoreCase
        );

        /// <summary>A write a tag lock ignored: logged, at most once per tag every 10 s.</summary>
        private void OnLockedWrite(string tag, string by)
        {
            double now = Environment.TickCount / 1000.0;
            if (_lockNoted.TryGetValue(tag, out double last) && now - last < 10.0)
                return;
            _lockNoted[tag] = now;
            Note("info", tag + " is locked: a write by " + by + " was ignored");
        }

        public JsonValue ClockState()
        {
            var c = World.Clock;
            var o = JsonValue.NewObject();
            o.Set("mode", c.Mode == SimClock.Run ? "run" : "paused");
            o.Set("speed", JsonValue.Of(c.Speed));
            o.Set("ms", JsonValue.Of(c.NowMs));
            o.Set("t", JsonValue.Of(c.Now()));
            o.Set("time", ValueFormat.IsoFiletime(c.NowFiletime()));
            o.Set("start", ValueFormat.IsoFiletime((ulong)c.StartFiletime));
            o.Set("stepSize", JsonValue.Of(c.StepSize));
            return o;
        }

        private void ClockEvent()
        {
            var s = ClockState();
            Events.Post(() => Events.Each(ClockChanged, h => ((Action<JsonValue>)h)(s)));
        }

        public void SetClockMode(string mode)
        {
            string m =
                mode == "pause" || mode == "paused" ? SimClock.Paused
                : mode == "run" ? SimClock.Run
                : throw new ArgumentException(
                    "clock mode must be run or pause: " + Project.CitectText.Quote(mode)
                );
            World.Clock.SetMode(m);
            // 'run' ends the steps in progress where they are (each answers with what it moved)
            while (m == SimClock.Run && _steps.Count > 0)
                StepSome();
            ClockEvent();
            _signal.Set();
        }

        public void SetSpeed(double speed)
        {
            World.Clock.SetSpeed(speed);
            ClockEvent();
        }

        /// <summary>A clock step in progress (<see cref="BeginStep"/>).</summary>
        private sealed class StepState
        {
            public long Target,
                Start;
            public Stopwatch Watch = new Stopwatch();
            public int BudgetMs;
            public Action<long>? Done;
        }

        private readonly Queue<StepState> _steps = new Queue<StepState>();

        /// <summary>A clock step that doesn't hold the simulation.</summary>
        public void BeginStep(long ms, Action<long> done, int budgetMs = 30_000)
        {
            var clock = World.Clock;
            if (clock.Mode != SimClock.Paused)
                clock.SetMode(SimClock.Paused);
            _steps.Enqueue(
                new StepState
                {
                    Target = Math.Max(0, ms),
                    BudgetMs = budgetMs,
                    Done = done,
                    Start = -1,
                }
            );
            _signal.Set();
        }

        /// <summary>One burst of the step in front (BurstMs of ready tasks, then time to the next
        /// wake-up); the step ends at its target or its budget.</summary>
        private void StepSome()
        {
            var st = _steps.Peek();
            var clock = World.Clock;
            if (st.Start < 0)
            {
                st.Start = clock.NowMs;
                st.Target += clock.NowMs;
                st.Watch.Start();
            }
            bool end;
            try
            {
                end =
                    clock.Mode != SimClock.Paused
                    || st.Watch.ElapsedMilliseconds >= st.BudgetMs
                    || StepBurst(st.Target, st.Watch, st.BudgetMs);
            }
            catch (Exception e)
            {
                // a burst that fails ends the step where it is (else it would fail again on every
                // turn of the loop and never answer)
                Emit(
                    "error",
                    "the clock step failed: " + e.GetType().Name + ": " + e.Message,
                    kind: "internal"
                );
                end = true;
            }
            if (!end)
                return;
            EndStep(st);
        }

        /// <summary>A step ends: the tag batch and the clock event go, its client gets what it moved.</summary>
        private void EndStep(StepState st)
        {
            _steps.Dequeue();
            var clock = World.Clock;
            try
            {
                Flush();
                ClockEvent();
            }
            catch (Exception e)
            {
                Emit("error", "ending a clock step failed: " + e.Message, kind: "internal");
            }
            long moved = st.Start < 0 ? 0 : clock.NowMs - st.Start;
            try
            {
                st.Done?.Invoke(moved);
            }
            catch (Exception e)
            {
                Emit("error", "a clock step's answer failed: " + e.Message, kind: "internal");
            }
        }

        /// <summary>One outer turn of a step: the ready tasks for a burst, then the clock to the
        /// next wake-up (never past <paramref name="target"/>). True when the target is reached.</summary>
        private bool StepBurst(long target, Stopwatch sw, int budgetMs)
        {
            var clock = World.Clock;
            long burst = sw.ElapsedMilliseconds;
            bool busy = false;
            while (StepOne())
            {
                if (sw.ElapsedMilliseconds >= budgetMs)
                    break;
                if (sw.ElapsedMilliseconds - burst >= BurstMs)
                {
                    busy = true;
                    break;
                }
            }
            if (clock.NowMs >= target)
                return true;
            long next = target;
            long? due = clock.NextDueMs();
            if (due != null && due.Value < next)
                next = Math.Max(due.Value, clock.NowMs);
            foreach (var t in Scheduler.All)
            {
                if (t.Done || t.Pause || (t.State != TaskState.Wait && t.State != TaskState.Sleep))
                    continue;
                long? w = WakeMs(t, out bool poll);
                if (poll && w == null)
                    w = clock.NowMs + clock.StepMs; // a condition: look again a step later
                if (w != null && w.Value < next)
                    next = Math.Max(w.Value, clock.NowMs + 1);
            }
            // a task still ready after a burst never sleeps (WHILE TRUE DO ... END): time moves
            // on by the real time it ran, as the runtime's time slices let it
            if (busy)
                next = Math.Min(next, clock.NowMs + Math.Max(1, sw.ElapsedMilliseconds - burst));
            Guard(() => clock.AdvanceToMs(Math.Max(next, clock.NowMs)), "the clock");
            return false;
        }

        /// <summary>Pause, then move simulated time on by ``ms``, running tasks as they wake.</summary>
        public long StepClock(long ms, int budgetMs = 30_000)
        {
            var clock = World.Clock;
            if (clock.Mode != SimClock.Paused)
                clock.SetMode(SimClock.Paused);
            long target = clock.NowMs + Math.Max(0, ms);
            long start = clock.NowMs;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < budgetMs)
                if (StepBurst(target, sw, budgetMs))
                    break;
            Flush();
            ClockEvent();
            return clock.NowMs - start;
        }

        // ------------------------------------------------------------------ scenarios
        public Scenario LoadScenario(JsonValue? data, string? path)
        {
            var sc =
                data != null && data.Kind == JsonKind.Object
                    ? Scenario.FromJson(data)
                    : Scenario.Load(
                        path ?? throw new ArgumentException("scenario load needs a path or a scenario")
                    );
            _scenario = sc;
            return sc;
        }

        public bool ScenarioRunning => _scenarioRun?.Running == true;

        /// <summary>Save the simulation's state as a scenario.</summary>
        public JsonValue SaveScenario()
        {
            var o = JsonValue.NewObject();
            o.Set("format", "ctsim-scenario/0");
            o.Set("project", ProjectName);
            var clock = World.Clock;
            o.Set(
                "description",
                "The simulation's state at "
                    + ValueFormat.IsoFiletime(clock.NowFiletime())
                    + " (t = "
                    + clock.Now().ToString("0.###", CultureInfo.InvariantCulture)
                    + " s)"
            );
            var c = JsonValue.NewObject();
            c.Set("start", ValueFormat.IsoFiletime(clock.NowFiletime()));
            c.Set("speed", JsonValue.Of(clock.Speed));
            o.Set("clock", c);
            var page = World.Display.Windows[0].Page;
            if (page != null)
            {
                var p = JsonValue.NewObject();
                p.Set("window", JsonValue.Of(0));
                p.Set("name", page.Name);
                o.Set("page", p);
            }
            var tags = JsonValue.NewObject();
            foreach (string n in World.Tags.ChangesSince(0))
            {
                var u = ReadTag(n, hover: true);
                if (!u.Defined || u.Value.Kind == JsonKind.Array || u.Value.Kind == JsonKind.Object)
                    continue;
                var e = JsonValue.NewObject();
                e.Set("value", u.Value);
                e.Set("quality", u.Quality);
                tags.Set(u.Name, e);
            }
            o.Set("tags", tags);
            o.Set("steps", JsonValue.NewArray());
            return o;
        }

        public void RunScenario()
        {
            var sc = _scenario
                ?? throw new InvalidOperationException("no scenario loaded (scenario load first)");
            _scenarioRun?.Stop();
            var run = new ScenarioRunner(World, sc, new SimScenarioEngine(this), World.Trace);
            _scenarioRun = run;
            run.Schedule(res =>
            {
                var j = ScenarioResultJson(res);
                j.Set("description", sc.Description); // (which scenario ended)
                Events.Post(() => Events.Each(ScenarioFinished, h => ((Action<JsonValue>)h)(j)));
            });
            FlushSoon();
        }

        public void StopScenario()
        {
            var run = _scenarioRun;
            if (run == null)
                return;
            bool was = run.Running;
            run.Stop();
            if (was)
            {
                var j = ScenarioResultJson(run.Result);
                j.Set("stopped", JsonValue.True);
                j.Set("description", run.Scenario.Description);
                Events.Post(() => Events.Each(ScenarioFinished, h => ((Action<JsonValue>)h)(j)));
            }
        }

        public static JsonValue ScenarioResultJson(ScenarioResult r)
        {
            var o = JsonValue.NewObject();
            o.Set("passed", JsonValue.Of(r.Passed));
            o.Set("failed", JsonValue.Of(r.Failed.Count));
            o.Set("skipped", JsonValue.Of(r.Skipped.Count));
            o.Set("errors", JsonValue.Of(r.Errors.Count));
            o.Set("end", JsonValue.Of(r.End));
            var fails = JsonValue.NewArray();
            foreach (var e in r.Failed)
            {
                var f = JsonValue.NewObject();
                f.Set("t", JsonValue.Of(e.T));
                f.Set("kind", e.Kind);
                f.Set("target", e.Target);
                f.Set("want", e.Want);
                f.Set("got", e.Got);
                fails.Add(f);
            }
            o.Set("failures", fails);
            var errs = JsonValue.NewArray();
            foreach (var (t, step, code, reason) in r.Errors)
            {
                var f = JsonValue.NewObject();
                f.Set("t", JsonValue.Of(t));
                f.Set("step", step);
                f.Set("code", code);
                f.Set("reason", reason);
                errs.Add(f);
            }
            o.Set("errorList", errs);
            return o;
        }

        // ------------------------------------------------------------------ source changes
        /// <summary>A .ci file changed (ctsim/reload): saved (text null) or an unsaved buffer (text);
        /// compiled again by the source compiler.</summary>
        public JsonValue Reload(string file, string? text)
        {
            _reloaded.Add(file);
            var o = JsonValue.NewObject();
            o.Set("accepted", JsonValue.True);
            o.Set("file", file);
            // the source compiler: the file compiled again (alone when its
            // declarations keep their shape), the new program used by every call from now on; a
            // task already running keeps the code it runs
            Compiler.Codegen.LiveProgram.ReloadResult r;
            try
            {
                r = Live.Reload(file, text);
            }
            catch (Exception e)
                when (e is IOException
                    || e is UnauthorizedAccessException
                    || e is Compiler.Codegen.CodegenException
                )
            {
                r = new Compiler.Codegen.LiveProgram.ReloadResult { Reason = e.Message };
            }
            if (r.Compiled)
            {
                // the module / global variables keep their values (one storage for the old code
                // still running and the new code), the new ones start from their initialisers
                var old = Library;
                Library = Live.Program;
                Vm.Library = Library;
                Vm.ShareVariables(old, Library);
                RebindEventHandlers(old, Library);
                // breakpoints resolve again (a line may have gained or lost its code)
                Debugger.LibraryChanged();
                // page rows compile against the new declarations
                Pages.LibraryChanged();
            }
            o.Set("compiled", JsonValue.Of(r.Compiled));
            o.Set("incremental", JsonValue.Of(r.Incremental));
            o.Set("ms", JsonValue.Of(Math.Round(r.Ms, 1)));
            if (r.Reason.Length > 0)
                o.Set("reason", r.Reason);
            var errs = JsonValue.NewArray();
            foreach (var d in r.Errors)
            {
                var e = JsonValue.NewObject();
                e.Set("code", d.Name);
                e.Set("line", JsonValue.Of(d.Line));
                e.Set("column", JsonValue.Of(d.Col));
                e.Set("message", d.Message + (d.Context.Length > 0 ? " {" + d.Context + "}" : ""));
                errs.Add(e);
            }
            o.Set("errors", errs);
            if (r.Compiled)
            {
                Emit(
                    "info",
                    "reload "
                        + file
                        + ": compiled"
                        + (r.Incremental ? "" : " (the whole unit)")
                        + " in "
                        + r.Ms.ToString("0.0", CultureInfo.InvariantCulture)
                        + " ms",
                    kind: "reload"
                );
                // a function the code generator could not compile runs as an empty body: say so
                foreach (var (fn, why) in Live.Failed)
                    Emit(
                        "warning",
                        "reload " + file + ": not compiled from source: " + fn + ": " + why,
                        kind: "reload"
                    );
            }
            else
                Emit(
                    r.Errors.Count > 0 ? "error" : "warning",
                    "reload " + file + ": not compiled: " + r.Reason,
                    kind: "reload"
                );
            Events.Post(() => Events.Each(Reloaded, h => ((Action<string, string?>)h)(file, text)));
            return o;
        }

        /// <summary>The files reported changed since the start.</summary>
        public IReadOnlyCollection<string> ReloadedFiles => _reloaded;

        /// <summary>Point event handlers at the same functions after a reload.</summary>
        private void RebindEventHandlers(CProgram from, CProgram to)
        {
            var ev = Host.Events;
            for (int k = 0; k < ev.Length; k++)
            {
                if (ev[k] < 0)
                    continue;
                Compiled.Function? f = null;
                foreach (var x in from.Functions)
                    if (x.Index == ev[k])
                    {
                        f = x;
                        break;
                    }
                if (f == null)
                    continue;
                ev[k] = Follow(f, to)?.Index ?? -1;
            }
            var data = ProcessData.Existing(Host);
            if (data != null)
                foreach (var m in data.MapStore.ByName.Values)
                    if (m.OnClose != null)
                        m.OnClose = Follow(m.OnClose, to);
        }

        /// <summary>The function in ``to`` that matches ``f`` from the replaced library.</summary>
        internal static Compiled.Function? Follow(Compiled.Function f, CProgram to)
        {
            Compiled.Function? sameFileName = null,
                sameName = null;
            string file = Project.WinPath.BaseName(f.Filename),
                folder = Project.WinPath.BaseName(Project.WinPath.DirName(f.Filename));
            foreach (var x in to.Functions)
            {
                if (!string.Equals(x.Name, f.Name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(x.Filename, f.Filename, StringComparison.OrdinalIgnoreCase))
                    return x;
                if (
                    sameFileName == null
                    && string.Equals(
                        Project.WinPath.BaseName(x.Filename),
                        file,
                        StringComparison.OrdinalIgnoreCase
                    )
                    && string.Equals(
                        Project.WinPath.BaseName(Project.WinPath.DirName(x.Filename)),
                        folder,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    sameFileName = x;
                if (sameName == null && !x.Private)
                    sameName = x;
            }
            return sameFileName ?? (f.Private ? null : sameName);
        }

        // ------------------------------------------------------------------ info
        public JsonValue Info()
        {
            var o = JsonValue.NewObject();
            o.Set("project", ProjectDir);
            o.Set("projectName", ProjectName);
            o.Set("root", Root);
            o.Set("rootReason", RootReason);
            var roots = JsonValue.NewArray();
            foreach (string r in RootCandidates)
                roots.Add(JsonValue.Of(r));
            o.Set("roots", roots);
            o.Set("rootDir", World.Unit.RootDir);
            // the projects the simulation reads, in its search order (pages, functions, tags)
            var unit = JsonValue.NewArray();
            foreach (var p in World.Unit.Search)
            {
                var x = JsonValue.NewObject();
                x.Set("name", p.Name);
                x.Set("dir", p.Dir);
                unit.Add(x);
            }
            o.Set("unit", unit);
            o.Set("user", UserDir);
            o.Set("install", InstallDir);
            o.Set("functions", JsonValue.Of(Library.Functions.Count));
            o.Set("compile", Options.Compile);
            var notes = JsonValue.NewArray();
            foreach (string n in LoadNotes)
                notes.Add(JsonValue.Of(n));
            o.Set("notes", notes);
            o.Set("loadMs", JsonValue.Of(LoadMs));
            return o;
        }

        public List<TaskSummary> TaskList()
        {
            Scheduler.DropEnded();
            var r = new List<TaskSummary>();
            foreach (var t in Scheduler.All)
                if (!t.Done && !t.NoDebug)
                    r.Add(TaskSummary.Of(t));
            return r;
        }

        // ------------------------------------------------------------------ ending
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _stopping = true;
            _signal.Set();
            if (_thread != null && !IsSimThread)
                _thread.Join(10_000);
            try
            {
                _scenarioRun?.Stop();
            }
            catch (Exception) { }
            try
            {
                Pages.Dispose();
            }
            catch (Exception) { }
            try
            {
                Host.ShutdownOutside();
            }
            catch (Exception) { }
            try
            {
                World.Trace?.Close();
            }
            catch (Exception) { }
            Events.Post(() => Events.Each(Terminated, h => ((Action)h)()));
            Debugger.OnTerminated();
            Events.Dispose();
        }
    }

    /// <summary>The simulation as a scenario's engine: tags by 'scenario', calls as tasks (not
    /// waited for), pages and inputs through the simulation's pages (SimPages).</summary>
    public sealed class SimScenarioEngine : IScenarioEngine
    {
        private readonly Simulation _sim;

        public SimScenarioEngine(Simulation sim)
        {
            _sim = sim;
        }

        public int SetTag(string name, JsonValue? value, JsonValue? quality = null) =>
            _sim.World.Tags.Set(name, value, quality, by: "scenario");

        public object? Input(
            long window,
            long an,
            JsonValue kind,
            JsonValue? key,
            JsonValue? value,
            JsonValue? x,
            JsonValue? y
        )
        {
            var a = JsonValue.NewObject();
            a.Set("an", JsonValue.Of(an));
            a.Set("kind", kind.Kind == JsonKind.String ? kind : JsonValue.Of(kind.Text));
            if (key != null)
                a.Set("key", key);
            if (value != null)
                a.Set("value", value);
            if (x != null)
                a.Set("x", x);
            if (y != null)
                a.Set("y", y);
            try
            {
                return _sim.Pages.Input((int)window, a).Get("ran")?.Bool;
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
            {
                throw new NotSimulatedException("input: " + e.Message);
            }
        }

        public object? Call(string expr)
        {
            try
            {
                return _sim.StartCall(expr, null).Handle;
            }
            catch (CallTextException e)
            {
                throw new NotSimulatedException("call " + expr + ": " + e.Message);
            }
        }

        public object? OpenPage(long window, JsonValue name)
        {
            int err = _sim.Pages.OpenForScenario((int)window, name.Text);
            if (err != 0)
                throw new NotSimulatedException(
                    "page "
                        + name.Text
                        + ": error "
                        + err.ToString(CultureInfo.InvariantCulture)
                        + " "
                        + SimPages.OpenError(err)
                );
            return err;
        }

        public string? Page(long window) => _sim.World.Display.OpenWindow((int)window)?.Page?.Name;

        public void Scan() { }
    }
}
