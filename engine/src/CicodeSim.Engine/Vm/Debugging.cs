using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using CicodeSim.Compiled;
using CicodeSim.Values;

namespace CicodeSim.Vm
{
    /// <summary>One frame of a task as the debugger sees it: the function, its .ci file, the
    /// current line (the last Line marker), the frame base (local slot 0) and the pc.</summary>
    public sealed class FrameInfo
    {
        public Function? Func;
        public string Name = "";
        public string File = "";
        public int Line;
        public int Base;
        public int Pc;

        /// <summary>0 = the innermost frame.</summary>
        public int Depth;
    }

    /// <summary>How the debugger names a .ci file.</summary>
    public static class SourceFiles
    {
        /// <summary>'folder\name' in lower case ('' for no file; '\name' when the path has no
        /// folder).</summary>
        public static string Key(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return "";
            string p = path!.TrimEnd('\\', '/', ' ');
            int s = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
            string name = s >= 0 ? p.Substring(s + 1) : p;
            string folder = "";
            if (s > 0)
            {
                string dir = p.Substring(0, s).TrimEnd('\\', '/');
                int s2 = Math.Max(dir.LastIndexOf('\\'), dir.LastIndexOf('/'));
                folder = s2 >= 0 ? dir.Substring(s2 + 1) : dir;
                if (folder.EndsWith(":", StringComparison.Ordinal))
                    folder = "";
            }
            return (folder + "\\" + name).ToLowerInvariant();
        }

        /// <summary>The file name part of a key.</summary>
        public static string NameOf(string key)
        {
            int s = key.LastIndexOf('\\');
            return s >= 0 ? key.Substring(s + 1) : key;
        }

        /// <summary>A key without a folder ('\name').</summary>
        public static bool IsBare(string key) => key.Length > 0 && key[0] == '\\';

        private static readonly ConditionalWeakTable<Program, Index> Indexes =
            new ConditionalWeakTable<Program, Index>();

        /// <summary>A program's functions by file key (built once per program).</summary>
        public sealed class Index
        {
            public readonly Dictionary<string, List<Function>> ByKey = new Dictionary<string, List<Function>>(
                StringComparer.Ordinal
            );
            public readonly Dictionary<string, List<string>> KeysByName = new Dictionary<
                string,
                List<string>
            >(StringComparer.Ordinal);
            public readonly Dictionary<Function, string> KeyOf = new Dictionary<Function, string>();

            internal Index(Program prog)
            {
                foreach (var fn in prog.Functions)
                {
                    string k = Key(fn.Filename);
                    KeyOf[fn] = k;
                    if (k.Length == 0)
                        continue;
                    if (!ByKey.TryGetValue(k, out var list))
                    {
                        ByKey[k] = list = new List<Function>();
                        string n = NameOf(k);
                        if (!KeysByName.TryGetValue(n, out var keys))
                            KeysByName[n] = keys = new List<string>();
                        keys.Add(k);
                    }
                    list.Add(fn);
                }
            }

            /// <summary>The library's key for a file the client names: the same folder + name,
            /// or for a bare name the one file of that name (null when none or several).</summary>
            public string? Match(string file, out string message)
            {
                message = "";
                string k = Key(file);
                if (k.Length == 0)
                {
                    message = "no file";
                    return null;
                }
                if (!IsBare(k))
                {
                    if (ByKey.ContainsKey(k))
                        return k;
                    message = "not in the compiled project: no function of " + Display(k) + " was compiled";
                    return null;
                }
                string n = NameOf(k);
                if (KeysByName.TryGetValue(n, out var keys))
                {
                    if (keys.Count == 1)
                        return keys[0];
                    message =
                        n
                        + " is in several projects ("
                        + string.Join(", ", keys.ConvertAll(Display))
                        + "): give its folder";
                    return null;
                }
                message = "not in the compiled project: no function of " + n + " was compiled";
                return null;
            }
        }

        /// <summary>'Folder\Name' as the key holds it (lower case).</summary>
        public static string Display(string key) => key.TrimStart('\\');

        public static Index For(Program prog) => Indexes.GetValue(prog, p => new Index(p));
    }

    /// <summary>The debugger seam of the VM: frames from the task's call stack and Line markers,
    /// locals from the DEBUG section, breakpoint line lookup, and the Stepper that drives the
    /// line hook (breakpoints, pause, step into / over / out). An IDebugTarget is built on these.</summary>
    public static class DebugInspect
    {
        /// <summary>The task's frames, innermost first (the bottom frame of a call task, the
        /// VM's return stub, is left out).</summary>
        public static List<FrameInfo> Frames(CicodeTask t)
        {
            var r = new List<FrameInfo>();
            r.Add(Make(t.Func, t.Code, t.Line, t.Base, t.Pc, 0));
            for (int k = t.Frames.Count - 1; k >= 0; k--)
            {
                var f = t.Frames[k];
                if (f.Func == null && f.Code != null && f.Code.Label == "<return>")
                    continue;
                r.Add(Make(f.Func, f.Code, f.Line, f.Base, f.Code?.PcOf(f.Ip) ?? 0, r.Count));
            }
            return r;
        }

        /// <summary>The task's innermost frame (Frames' first) alone.</summary>
        public static FrameInfo Top(CicodeTask t) => Make(t.Func, t.Code, t.Line, t.Base, t.Pc, 0);

        private static FrameInfo Make(Function? fn, CodeBlock? code, int line, int bas, int pc, int depth) =>
            new FrameInfo
            {
                Func = fn,
                Name = fn?.Name ?? code?.Label ?? "",
                File = fn?.Filename ?? "",
                Line = line,
                Base = bas,
                Pc = pc,
                Depth = depth,
            };

        /// <summary>The named locals of a frame in slot order (the arguments, then the locals as
        /// declared; the DEBUG section keeps another order) with their current values; a
        /// slot that is not on the stack yet is left out.</summary>
        public static List<(string Name, int Slot, Value Value)> Locals(CicodeTask t, FrameInfo frame)
        {
            var r = new List<(string Name, int Slot, Value Value)>();
            if (frame.Func == null)
                return r;
            foreach (var (slot, name) in frame.Func.Locals)
            {
                int i = frame.Base + slot;
                if (i >= 0 && i < t.Sp)
                    r.Add((name, slot, t.Stack[i]));
            }
            r.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            return r;
        }

        /// <summary>A named local's value (Locals' first of that name, case aside) without the
        /// list: false when the frame has none on the stack.</summary>
        public static bool TryLocal(CicodeTask t, FrameInfo frame, string name, out Value value)
        {
            value = default;
            if (frame.Func == null)
                return false;
            int best = int.MaxValue;
            foreach (var (slot, n) in frame.Func.Locals)
            {
                int i = frame.Base + slot;
                if (
                    slot < best
                    && i >= 0
                    && i < t.Sp
                    && string.Equals(n, name, StringComparison.OrdinalIgnoreCase)
                )
                {
                    best = slot;
                    value = t.Stack[i];
                }
            }
            return best != int.MaxValue;
        }

        /// <summary>Set a local by name; the value is converted to the slot's type like a store
        /// through a reference (the slot keeps its quality). False when there is no such local.</summary>
        public static bool SetLocal(CicodeTask t, FrameInfo frame, string name, Value value)
        {
            if (frame.Func == null)
                return false;
            foreach (var (slot, nm) in frame.Func.Locals)
            {
                if (!string.Equals(nm, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                int i = frame.Base + slot;
                if (i < 0 || i >= t.Sp)
                    return false;
                t.Vm.AssignRef(t.RefTo(i), value);
                return true;
            }
            return false;
        }

        /// <summary>Every Line marker of a function, in code order.</summary>
        public static List<int> LineMarkers(Function fn)
        {
            var r = new List<int>();
            try
            {
                foreach (var ins in Opcodes.Instructions(fn.Code))
                    if (ins.Op == Op.Line)
                        r.Add(ins.A);
            }
            catch (DecodeError) { }
            return r;
        }

        /// <summary>The line a breakpoint on ``line`` actually sits on.</summary>
        public static int ResolveLine(Program prog, string file, int line)
        {
            var ix = SourceFiles.For(prog);
            string? key = ix.Match(file, out _);
            return key == null ? -1 : ResolveKeyLine(ix, key, line);
        }

        /// <summary>ResolveLine for a library file key.</summary>
        public static int ResolveKeyLine(SourceFiles.Index ix, string key, int line)
        {
            int best = -1;
            int inside = -1;
            if (!ix.ByKey.TryGetValue(key, out var fns))
                return -1;
            foreach (var fn in fns)
            {
                var marks = LineMarkers(fn);
                if (marks.Count == 0)
                    continue;
                int lo = int.MaxValue,
                    hi = int.MinValue,
                    next = -1;
                foreach (int m in marks)
                {
                    if (m < lo)
                        lo = m;
                    if (m > hi)
                        hi = m;
                    if (m >= line && (next < 0 || m < next))
                        next = m;
                }
                if (next < 0)
                    continue;
                if (lo <= line && line <= hi && (inside < 0 || next < inside))
                    inside = next;
                if (best < 0 || next < best)
                    best = next;
            }
            return inside >= 0 ? inside : best;
        }
    }

    /// <summary>A hardware error as the runtime's debugger reports it (live:
    /// conformance/runtime/debugger/*.json): the announce text (0x100B), the error line (0x1002),
    /// the code with DESC / ERRPAGE / ERRDESC (0x100E).</summary>
    public sealed class ErrorReportInfo
    {
        public CicodeTask Task = null!;
        public int Code;

        /// <summary>The error's title (DESC: 'Array has been overrun').</summary>
        public string Text = "";

        /// <summary>ERRPAGE: the built-in that raised it ('TagRead'), '' for the VM's own.</summary>
        public string ErrPage = "";

        /// <summary>ERRDESC: the Cicode function it happened in.</summary>
        public string ErrDesc = "";
        public string Function = "";
        public string File = "";
        public int Line;

        /// <summary>The task halts now (it ends with no stop); false = it runs on and stops at
        /// its next Line marker.</summary>
        public bool Halting;

        /// <summary>Foreground code: only the report (once until the next continue-all).</summary>
        public bool Foreground;

        /// <summary>Not an error of the code: notice 343 'Foreground Cicode cannot break' for a
        /// breakpoint hit in foreground code (once a session).</summary>
        public bool Notice;

        /// <summary>The announce line the runtime sends (0x100B): "Thread 3 Hardware error (259)
        /// 'Array has been overrun' ErrPage: '' ErrDesc: '_PltLineStyleGet'".</summary>
        public string Announce =>
            "Thread "
            + Task.Handle.ToString(CultureInfo.InvariantCulture)
            + " Hardware error ("
            + Code.ToString(CultureInfo.InvariantCulture)
            + ") '"
            + Text
            + "' ErrPage: '"
            + ErrPage
            + "' ErrDesc: '"
            + ErrDesc
            + "'";
    }

    /// <summary>Breakpoints, pause and stepping on the VM's line hook.</summary>
    public sealed class Stepper
    {
        public readonly CicodeVm Vm;
        public readonly DebugOptions Options;

        /// <summary>file key -> breakpoint lines -> breakpoint id.</summary>
        private readonly Dictionary<string, Dictionary<int, int>> _breakpoints = new Dictionary<
            string,
            Dictionary<int, int>
        >(StringComparer.Ordinal);

        /// <summary>What the client asked for, per file it named (re-resolved when the library changes).</summary>
        private readonly Dictionary<string, List<(BreakpointRequest Req, BreakpointResult Res)>> _requests =
            new Dictionary<string, List<(BreakpointRequest, BreakpointResult)>>(
                StringComparer.OrdinalIgnoreCase
            );
        private readonly Dictionary<int, BreakpointRequest> _byId = new Dictionary<int, BreakpointRequest>();
        private readonly Dictionary<int, long> _hits = new Dictionary<int, long>();
        private readonly Dictionary<Function, Dictionary<int, int>?> _fnMap =
            new Dictionary<Function, Dictionary<int, int>?>();

        // the last MapFor answer (most lines run in the same function as the line before)
        private Function? _lastFn;
        private Dictionary<int, int>? _lastMap;

        // the hit conditions, parsed once
        private readonly Dictionary<string, HitRule> _hitRules = new Dictionary<string, HitRule>();
        private readonly HashSet<CicodeTask> _pauseRequested = new HashSet<CicodeTask>();

        /// <summary>The tasks this Stepper stopped and has not released (also ones outside the
        /// VM's Scheduler: a foreground evaluation with ForegroundCanBreak).</summary>
        private readonly HashSet<CicodeTask> _stopped = new HashSet<CicodeTask>();
        private int _nextId = 1;
        private bool _pauseAll;

        /// <summary>Page-code hardware errors reported since the last continue.</summary>
        private readonly HashSet<(int, string, string, int)> _foregroundErrorsReported =
            new HashSet<(int, string, string, int)>();
        private const int ForegroundErrorsMax = 1000;
        private bool _foregroundBreakWarned;
        private Program? _indexed;

        /// <summary>A debugger session is attached.</summary>
        public bool SessionActive = true;

        /// <summary>Evaluates a breakpoint condition in the stopped task (true = stop). Without
        /// one, or when it returns null, the breakpoint always stops.</summary>
        public Func<CicodeTask, string, bool?>? Condition;

        /// <summary>A task stopped: (task, reason, error code, text, breakpoint id or 0).</summary>
        public event Action<CicodeTask, StopReason, int, string, int>? Stopped;

        /// <summary>A hardware error or notice was reported (see the class remarks).</summary>
        public event Action<ErrorReportInfo>? ErrorReported;

        /// <summary>A step left the outermost function: the task is parked with no stop (the
        /// runtime sends nothing; DebugTaskInfo.Parked shows it).</summary>
        public event Action<CicodeTask>? Parked;

        /// <summary>A stopped task runs on other than by Continue / Step of that task (a step of
        /// another task released it).</summary>
        public event Action<CicodeTask>? Released;

        /// <summary>A breakpoint with a log message was hit: (task, breakpoint id, message).</summary>
        public event Action<CicodeTask, int, string>? LogPoint;

        public Stepper(CicodeVm vm, DebugOptions? options = null)
        {
            Vm = vm;
            Options = options ?? new DebugOptions();
            vm.LineHook = OnLine;
            var previous = vm.ErrorReport;
            vm.ErrorReport = (task, code, halting, suppressed) =>
            {
                previous?.Invoke(task, code, halting, suppressed);
                OnError(task, code, halting, suppressed);
            };
        }

        // ------------------------------------------------------------------ breakpoints
        private SourceFiles.Index Index()
        {
            var ix = SourceFiles.For(Vm.Library);
            if (!ReferenceEquals(_indexed, Vm.Library))
            {
                _indexed = Vm.Library;
                _fnMap.Clear();
                _lastFn = null;
            }
            return ix;
        }

        /// <summary>Replace the breakpoints of a file: per requested line (verified, actual line, id).</summary>
        public List<BreakpointResult> SetBreakpoints(string file, IReadOnlyList<BreakpointRequest> lines)
        {
            var ix = Index();
            if (_requests.TryGetValue(file, out var old))
                foreach (var (_, res) in old)
                {
                    _byId.Remove(res.Id);
                    _hits.Remove(res.Id);
                }
            var mine = new List<(BreakpointRequest, BreakpointResult)>();
            var r = new List<BreakpointResult>();
            string? key = ix.Match(file, out string why);
            foreach (var req in lines)
            {
                var res = new BreakpointResult { Id = _nextId++, File = file };
                Bind(ix, key, why, req, res);
                mine.Add((req, res));
                r.Add(res);
                _byId[res.Id] = req;
            }
            if (lines.Count == 0)
                _requests.Remove(file);
            else
                _requests[file] = mine;
            Rebuild();
            return r;
        }

        private static void Bind(
            SourceFiles.Index ix,
            string? key,
            string why,
            BreakpointRequest req,
            BreakpointResult res
        )
        {
            int actual = key == null ? -1 : DebugInspect.ResolveKeyLine(ix, key, req.Line);
            res.Verified = actual >= 0;
            res.Line = actual >= 0 ? actual : req.Line;
            res.Key = key ?? "";
            res.Message =
                actual >= 0 ? null
                : key == null ? why
                : "no Cicode code on or after this line";
        }

        private void Rebuild()
        {
            _breakpoints.Clear();
            _fnMap.Clear();
            _lastFn = null;
            foreach (var list in _requests.Values)
            foreach (var (_, res) in list)
            {
                if (!res.Verified || res.Key.Length == 0)
                    continue;
                if (!_breakpoints.TryGetValue(res.Key, out var map))
                    _breakpoints[res.Key] = map = new Dictionary<int, int>();
                if (!map.ContainsKey(res.Line))
                    map[res.Line] = res.Id;
            }
        }

        /// <summary>Resolve every breakpoint again (the library changed); the ones whose
        /// verification, line or message changed (they keep their ids).</summary>
        public List<BreakpointResult> Rebind()
        {
            var ix = Index();
            var changed = new List<BreakpointResult>();
            foreach (var kv in _requests)
            {
                string? key = ix.Match(kv.Key, out string why);
                foreach (var (req, res) in kv.Value)
                {
                    bool v = res.Verified;
                    int l = res.Line;
                    string? m = res.Message;
                    Bind(ix, key, why, req, res);
                    if (v != res.Verified || l != res.Line || m != res.Message)
                        changed.Add(res);
                }
            }
            Rebuild();
            return changed;
        }

        /// <summary>Every breakpoint result (all files).</summary>
        public List<BreakpointResult> Breakpoints()
        {
            var r = new List<BreakpointResult>();
            foreach (var list in _requests.Values)
            foreach (var (_, res) in list)
                r.Add(res);
            return r;
        }

        public int BreakpointCount => _byId.Count;

        /// <summary>A line breakpoint sits on line <paramref name="line"/> of function
        /// <paramref name="fn"/> (the time travel's Reverse Continue).</summary>
        public bool HasBreakpointAt(Function fn, int line)
        {
            var m = MapFor(fn);
            return m != null && m.ContainsKey(line);
        }

        private Dictionary<int, int>? MapFor(Function fn)
        {
            if (_breakpoints.Count == 0)
                return null;
            if (!ReferenceEquals(_indexed, Vm.Library))
                Index();
            if (ReferenceEquals(fn, _lastFn))
                return _lastMap;
            if (!_fnMap.TryGetValue(fn, out var m))
            {
                string key = SourceFiles.Key(fn.Filename);
                _breakpoints.TryGetValue(key, out m);
                _fnMap[fn] = m;
            }
            _lastFn = fn;
            _lastMap = m;
            return m;
        }

        // ------------------------------------------------------------------ running on
        public void RequestPause(CicodeTask task) => _pauseRequested.Add(task);

        /// <summary>Pause the next background task that reaches a Line marker (the runtime's
        /// BREAK 0x102F); armed until a task stops.</summary>
        public void RequestPauseAny() => _pauseAll = true;

        /// <summary>Let a stopped task run on (all paused tasks when null; that also re-arms the
        /// foreground error report).</summary>
        public void Continue(CicodeTask? task)
        {
            if (task != null)
            {
                Release(task);
                return;
            }
            _foregroundErrorsReported.Clear();
            foreach (var t in PausedTasks())
                Release(t);
        }

        private void Release(CicodeTask t)
        {
            _stopped.Remove(t);
            t.Step = StepMode.None;
            t.StopReason = "";
            t.Pause = false;
            t.ParkForeground = false;
        }

        /// <summary>The tasks stopped or parked by the debugger.</summary>
        public List<CicodeTask> PausedTasks()
        {
            _stopped.RemoveWhere(t => t.Done || !t.Pause);
            var r = new List<CicodeTask>();
            var s = Vm.Scheduler;
            if (s != null)
                foreach (var t in s.All)
                    if (t.Pause && !t.Done)
                        r.Add(t);
            foreach (var t in _stopped)
                if (t.Pause && !r.Contains(t))
                    r.Add(t);
            return r;
        }

        /// <summary>Step a stopped task; by default the other paused tasks run on too.</summary>
        public void Step(CicodeTask task, StepKind kind)
        {
            if (Options.StepReleasesOthers)
                foreach (var t in PausedTasks())
                    if (t != task)
                    {
                        Release(t);
                        Released?.Invoke(t);
                    }
            task.Step =
                kind == StepKind.Into ? StepMode.Into
                : kind == StepKind.Over ? StepMode.Over
                : StepMode.Out;
            task.StepDepth = task.Depth;
            task.StopReason = "";
            task.Pause = false;
            task.ParkForeground = false;
            _stopped.Remove(task);
        }

        /// <summary>The debugger leaves (the runtime's SESSION_STOP): every breakpoint is cleared,
        /// every stopped task runs on, pending pauses and error stops are dropped, the
        /// once-a-session notices are re-armed, and the error rule is off until the next Attach.</summary>
        public void Detach()
        {
            _requests.Clear();
            _byId.Clear();
            _hits.Clear();
            Rebuild();
            _pauseRequested.Clear();
            _pauseAll = false;
            _foregroundErrorsReported.Clear();
            _foregroundBreakWarned = false;
            var s = Vm.Scheduler;
            if (s != null)
                foreach (var t in s.All)
                {
                    t.PendingErrorStop = 0;
                    t.PendingDataStop = 0;
                    t.Step = StepMode.None;
                    // (no debugger, no history: the tasks let theirs go)
                    t.HistOwner = null;
                    t.Hist = null;
                }
            foreach (var t in PausedTasks())
                Release(t);
            SessionActive = false;
            // no debugger: no history, no data breakpoints
            _dataBps.Clear();
            Vm.Recorder = null;
        }

        /// <summary>A debugger session starts (the runtime's SESSION_START): the hardware error
        /// rule applies from now on.</summary>
        public void Attach()
        {
            SessionActive = true;
            // the variable history: recorded while a debugger is attached
            if (Vm.Recorder == null)
            {
                Vm.Recorder = new Recorder(RecorderClock ?? (() => 0L));
                WatchWrites();
            }
        }

        /// <summary>The simulated ms the history records (the simulation's clock).</summary>
        public Func<long>? RecorderClock;

        // ------------------------------------------------------------------ data breakpoints
        private sealed class DataBp
        {
            public int Id;
            public DataBreakpointRequest Req = null!;
            public long Hits;
        }

        private readonly List<DataBp> _dataBps = new List<DataBp>();

        public IReadOnlyList<DataBreakpointRequest> DataBreakpoints()
        {
            var r = new List<DataBreakpointRequest>();
            foreach (var d in _dataBps)
                r.Add(d.Req);
            return r;
        }

        /// <summary>The recorder reports writes only while there are data breakpoints.</summary>
        private void WatchWrites()
        {
            if (Vm.Recorder != null)
                Vm.Recorder.Written = _dataBps.Count > 0 ? OnWritten : (WrittenHandler?)null;
        }

        /// <summary>Replace the data breakpoints (verified: a name is given).</summary>
        public List<BreakpointResult> SetDataBreakpoints(IReadOnlyList<DataBreakpointRequest> list)
        {
            _dataBps.Clear();
            var r = new List<BreakpointResult>();
            foreach (var q in list)
            {
                int id = _nextId++;
                bool ok = !string.IsNullOrWhiteSpace(q.Name);
                if (ok)
                    _dataBps.Add(new DataBp { Id = id, Req = q });
                r.Add(
                    new BreakpointResult
                    {
                        Id = id,
                        Verified = ok,
                        Message = ok ? null : "a data breakpoint needs a variable name",
                    }
                );
            }
            WatchWrites();
            return r;
        }

        /// <summary>The data breakpoint a history event's write matches, null when none.</summary>
        public int MatchData(in HistoryEvent e)
        {
            foreach (var d in _dataBps)
                if (Matches(d.Req, e))
                    return d.Id;
            return 0;
        }

        private static bool Matches(DataBreakpointRequest q, in HistoryEvent e)
        {
            if (e.Kind == HistoryKind.Local)
            {
                if (
                    q.Function == null
                    || e.Func == null
                    || !string.Equals(q.Function, e.Func.Name, StringComparison.OrdinalIgnoreCase)
                )
                    return false;
                return string.Equals(
                        q.Name,
                        Recorder.LocalName(e.Func, e.Slot),
                        StringComparison.OrdinalIgnoreCase
                    ) && !e.Entry;
            }
            if (e.Kind == HistoryKind.CodeVar && e.Var != null)
            {
                if (q.Function != null)
                    return false;
                if (!string.Equals(q.Name, e.Var.Cv.Name, StringComparison.OrdinalIgnoreCase))
                    return false;
                return q.Index < 0 || q.Index == e.Index;
            }
            return false;
        }

        private void OnWritten(CicodeTask task, in HistoryEvent e)
        {
            // (the condition and the hit count at the next line: the task's state is whole there)
            if (_dataBps.Count == 0 || task.NoDebug || !SessionActive || task.PendingDataStop != 0)
                return;
            foreach (var d in _dataBps)
                if (Matches(d.Req, e))
                {
                    task.PendingDataStop = d.Id;
                    return;
                }
        }

        /// <summary>A pending data breakpoint stops the task: its condition holds and its hit
        /// count matches.</summary>
        private bool DataStopHolds(CicodeTask task, int id)
        {
            var d = _dataBps.Find(x => x.Id == id);
            if (d == null)
                return false;
            if (
                !string.IsNullOrEmpty(d.Req.Condition)
                && Condition != null
                && Condition(task, d.Req.Condition!) == false
            )
                return false;
            d.Hits++;
            return string.IsNullOrEmpty(d.Req.HitCondition) || HitHolds(d.Req.HitCondition!, d.Hits);
        }

        private void Stop(CicodeTask task, StopReason reason, int bp, int code = 0, string text = "")
        {
            task.Step = StepMode.None;
            task.Pause = true;
            // only reached for foreground code with ForegroundCanBreak: the VM parks it too
            task.ParkForeground = task.Foreground;
            task.StopReason = reason.ToString().ToLowerInvariant();
            _stopped.Add(task);
            _pauseAll = false;
            Stopped?.Invoke(task, reason, code, text, bp);
        }

        // ------------------------------------------------------------------ hardware errors
        private void OnError(CicodeTask task, int code, bool halting, bool suppressed)
        {
            if (task.NoDebug || suppressed || code == 0 || !Options.BreakOnError || !SessionActive)
                return;
            // page code stops on its hardware errors only when asked (ForegroundErrorBreak);
            // else the runtime's rule: reported once, the page runs on
            bool fg = task.Foreground && (!Options.ForegroundCanBreak || !Options.ForegroundErrorBreak);
            if (fg)
            {
                var key = (
                    code,
                    task.Func?.Name ?? task.Code?.Label ?? "",
                    task.Func?.Filename ?? "",
                    task.Line
                );
                if (
                    _foregroundErrorsReported.Count >= ForegroundErrorsMax
                    || !_foregroundErrorsReported.Add(key)
                )
                    return;
            }
            var info = Report(task, code, halting);
            info.Foreground = task.Foreground;
            ErrorReported?.Invoke(info);
            if (!halting && !fg)
                task.PendingErrorStop = code;
        }

        private ErrorReportInfo Report(CicodeTask task, int code, bool halting)
        {
            string page = "";
            if (task.CurrentBuiltin >= 0)
            {
                try
                {
                    page = Vm.Builtins?.Name(task.CurrentBuiltin) ?? Vm.Table.Name(task.CurrentBuiltin);
                }
                catch (Exception)
                {
                    page = Vm.Table.Name(task.CurrentBuiltin);
                }
            }
            return new ErrorReportInfo
            {
                Task = task,
                Code = code,
                Text = Errors.Text(code),
                ErrPage = page,
                ErrDesc = task.Func?.Name ?? "",
                Function = task.Func?.Name ?? task.Code?.Label ?? "",
                File = task.Func?.Filename ?? "",
                Line = task.Line,
                Halting = halting,
            };
        }

        // ------------------------------------------------------------------ the line hook
        /// <summary>The line hook: Line markers (task.AtReturn false) and returns into a caller
        /// (AtReturn true, with the caller's line).</summary>
        public void OnLine(CicodeTask task, int line)
        {
            if (task.NoDebug || !SessionActive)
                return;
            if (task.Foreground && !Options.ForegroundCanBreak)
            {
                // notice 343 'Foreground Cicode cannot break' for a breakpoint hit, once a session
                if (!task.AtReturn && !_foregroundBreakWarned && task.Func != null)
                {
                    var fm = MapFor(task.Func);
                    if (fm != null && fm.ContainsKey(line))
                    {
                        _foregroundBreakWarned = true;
                        var info = Report(task, Errors.FgCannotBreak, false);
                        info.Foreground = true;
                        info.Notice = true;
                        info.Text = "Foreground Cicode cannot break";
                        ErrorReported?.Invoke(info);
                    }
                }
                return;
            }
            if (
                (_pauseRequested.Count > 0 && _pauseRequested.Remove(task))
                || (_pauseAll && !task.Foreground && !task.AtReturn)
            )
            {
                task.PendingErrorStop = 0;
                Stop(task, StopReason.Pause, 0);
                return;
            }
            int depth = task.Depth;
            if (task.AtReturn && task.Func == null && depth == 0)
            {
                // the outermost function returned into the VM's return stub
                task.PendingErrorStop = 0;
                if (task.Step != StepMode.None)
                {
                    // a step off the outermost function: the runtime parks the task with no event.
                    // Not page code (ForegroundCanBreak): parked, it would hold the whole
                    // simulation with nothing shown; the expression ends and the scan goes on
                    task.Step = StepMode.None;
                    if (Options.ParkOnStepOffOutermost && !task.Foreground)
                    {
                        task.Pause = true;
                        task.ParkForeground = task.Foreground;
                        task.StopReason = "parked";
                        _stopped.Add(task);
                        Parked?.Invoke(task);
                    }
                }
                return;
            }
            Dictionary<int, int>? map = task.Func != null && !task.AtReturn ? MapFor(task.Func) : null;
            int bpId = 0;
            if (map != null)
                map.TryGetValue(line, out bpId);
            if (task.PendingDataStop != 0 && !task.AtReturn)
            {
                // the statement after a watched variable's write
                int id = task.PendingDataStop;
                task.PendingDataStop = 0;
                if (DataStopHolds(task, id))
                {
                    Stop(task, StopReason.DataBreakpoint, id);
                    return;
                }
            }
            if (task.PendingErrorStop != 0 && !task.AtReturn)
            {
                // the statement after a non-halting error: stopped there, alive (0x1002; 0x1003
                // when the line has a breakpoint)
                int code = task.PendingErrorStop;
                task.PendingErrorStop = 0;
                if (bpId != 0)
                    Stop(task, StopReason.Breakpoint, bpId, code, Errors.Text(code));
                else
                    Stop(task, StopReason.Exception, 0, code, Errors.Text(code));
                return;
            }
            switch (task.Step)
            {
                case StepMode.Into:
                    Stop(task, StopReason.Step, 0);
                    return;
                case StepMode.Over:
                    if (depth < task.StepDepth || (depth == task.StepDepth && !task.AtReturn))
                    {
                        Stop(task, StopReason.Step, 0);
                        return;
                    }
                    break;
                case StepMode.Out:
                    if (depth < task.StepDepth)
                    {
                        Stop(task, StopReason.Step, 0);
                        return;
                    }
                    break;
            }
            if (bpId == 0)
                return;
            var req = _byId.TryGetValue(bpId, out var rq) ? rq : null;
            if (
                req != null
                && !string.IsNullOrEmpty(req.Condition)
                && Condition != null
                && Condition(task, req.Condition!) == false
            )
                return;
            long hits = _hits.TryGetValue(bpId, out long h) ? h + 1 : 1;
            _hits[bpId] = hits;
            if (req != null && !string.IsNullOrEmpty(req.HitCondition) && !HitHolds(req.HitCondition!, hits))
                return;
            if (req != null && !string.IsNullOrEmpty(req.LogMessage))
            {
                LogPoint?.Invoke(task, bpId, req.LogMessage!);
                return;
            }
            Stop(task, StopReason.Breakpoint, bpId);
        }

        /// <summary>A hit condition: 'n' or '== n' (the n-th hit), '&gt;= n', '&gt; n', '&lt; n',
        /// '&lt;= n', '% n' (every n-th); anything else always matches.</summary>
        public static bool HitMatches(string cond, long hits) => HitRule.Parse(cond).Matches(hits);

        private bool HitHolds(string cond, long hits)
        {
            if (!_hitRules.TryGetValue(cond, out var rule))
                _hitRules[cond] = rule = HitRule.Parse(cond);
            return rule.Matches(hits);
        }

        /// <summary>A parsed hit condition (see <see cref="HitMatches"/>).</summary>
        private readonly struct HitRule
        {
            private static readonly string[] Ops = { "==", ">=", "<=", ">", "<", "%", "=" };
            private readonly string _op;
            private readonly long _n;
            private readonly bool _any;

            private HitRule(string op, long n, bool any)
            {
                _op = op;
                _n = n;
                _any = any;
            }

            public static HitRule Parse(string cond)
            {
                string c = cond.Trim();
                string op = "==";
                foreach (string o in Ops)
                    if (c.StartsWith(o, StringComparison.Ordinal))
                    {
                        op = o == "=" ? "==" : o;
                        c = c.Substring(o.Length).Trim();
                        break;
                    }
                bool ok = long.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n);
                return new HitRule(op, n, !ok);
            }

            public bool Matches(long hits)
            {
                if (_any)
                    return true;
                switch (_op)
                {
                    case ">=":
                        return hits >= _n;
                    case "<=":
                        return hits <= _n;
                    case ">":
                        return hits > _n;
                    case "<":
                        return hits < _n;
                    case "%":
                        return _n > 0 && hits % _n == 0;
                    default:
                        return hits == _n;
                }
            }
        }
    }
}
