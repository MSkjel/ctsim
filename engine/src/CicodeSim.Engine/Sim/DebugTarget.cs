using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;
using CProgram = CicodeSim.Compiled.Program;

namespace CicodeSim.Sim
{
    /// <summary>The module and global variables and tags a library's functions use.</summary>
    public sealed class LibraryVars
    {
        public readonly Dictionary<string, List<CodeVar>> Module = new Dictionary<string, List<CodeVar>>(
            StringComparer.Ordinal
        );
        public readonly List<CodeVar> Globals = new List<CodeVar>();
        public readonly Dictionary<Function, List<TagRef>> Tags = new Dictionary<Function, List<TagRef>>();
        public readonly Dictionary<CodeVar, int[]> Dims = new Dictionary<CodeVar, int[]>();
        public readonly Dictionary<string, List<CodeVar>> ByName = new Dictionary<string, List<CodeVar>>(
            StringComparer.OrdinalIgnoreCase
        );

        /// <summary>The file keys whose functions use each MODULE variable.</summary>
        public readonly Dictionary<CodeVar, HashSet<string>> Files =
            new Dictionary<CodeVar, HashSet<string>>();

        private static readonly ConditionalWeakTable<CProgram, LibraryVars> Cache =
            new ConditionalWeakTable<CProgram, LibraryVars>();

        public static LibraryVars For(CProgram prog) => Cache.GetValue(prog, p => new LibraryVars(p));

        private LibraryVars(CProgram prog)
        {
            var seen = new Dictionary<string, HashSet<CodeVar>>(StringComparer.Ordinal);
            foreach (var fn in prog.Functions)
            {
                List<Instr> ins;
                try
                {
                    ins = Opcodes.Instructions(fn.Code);
                }
                catch (Exception)
                {
                    continue;
                }
                string key = SourceFiles.Key(fn.Filename);
                var tags = new List<TagRef>();
                var tagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int k = 0; k < ins.Count; k++)
                {
                    var x = ins[k];
                    if (x.Op == Op.LoadCodeVar || x.Op == Op.StoreCodeVar || x.Op == Op.AddrCodeVar)
                    {
                        // AddrCodeVar's operand is the variable's RUN DataAddress, the others a
                        // CODEVAR offset (row 0x3c)
                        CodeVar? cv;
                        if (x.Op == Op.AddrCodeVar)
                            cv = x.U64 <= long.MaxValue ? prog.CodeVarAtData(x.Offset) : null;
                        else if (!prog.CodeVars.TryGetValue(x.Offset, out cv))
                            cv = null;
                        if (cv == null)
                            continue;
                        if (
                            k > 0
                            && ins[k - 1].Op == Op.Index
                            && !Dims.ContainsKey(cv)
                            && ins[k - 1].Shorts != null
                        )
                        {
                            var s = ins[k - 1].Shorts!;
                            var d = new int[s.Length];
                            long prod = 1;
                            for (int j = 0; j < s.Length; j++)
                            {
                                d[j] = Math.Max(1, (int)s[j]);
                                prod *= d[j];
                            }
                            if (prod <= Math.Max(1, cv.UnitCount))
                                Dims[cv] = d;
                        }
                        if (cv.UnitType != -1)
                        {
                            if (!seen.TryGetValue(key, out var set))
                                seen[key] = set = new HashSet<CodeVar>();
                            if (set.Add(cv))
                            {
                                if (!Module.TryGetValue(key, out var list))
                                    Module[key] = list = new List<CodeVar>();
                                list.Add(cv);
                            }
                            if (!Files.TryGetValue(cv, out var fs))
                                Files[cv] = fs = new HashSet<string>(StringComparer.Ordinal);
                            fs.Add(key);
                        }
                    }
                    else if (
                        (x.Op == Op.Load || x.Op == Op.Store)
                        && prog.TagRefs.TryGetValue(x.Offset, out var tr)
                    )
                    {
                        if (tagNames.Add(TagName(tr)))
                            tags.Add(tr);
                    }
                }
                Tags[fn] = tags;
            }
            foreach (var cv in prog.CodeVarOrder)
            {
                if (cv.UnitType == -1)
                    Globals.Add(cv);
                if (!ByName.TryGetValue(cv.Name, out var l))
                    ByName[cv.Name] = l = new List<CodeVar>();
                l.Add(cv);
            }
            foreach (var l in Module.Values)
                l.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            Globals.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The dimensions of a variable: [] for a scalar.</summary>
        public int[] DimsOf(CodeVar cv)
        {
            if (Dims.TryGetValue(cv, out var d))
                return d;
            return cv.UnitCount > 1
                ? new[] { (int)Math.Min(cv.UnitCount, int.MaxValue) }
                : Array.Empty<int>();
        }

        /// <summary>'Tag', 'Tag.Element', 'Cluster.Tag.Item' as code names it.</summary>
        public static string TagName(TagRef r)
        {
            string n = r.Tag;
            if (r.Cluster.Length > 0)
                n = r.Cluster + "." + n;
            if (r.Element.Length > 0)
                n += "." + r.Element;
            if (r.Item.Length > 0)
                n += "." + r.Item;
            return n;
        }
    }

    /// <summary>IDebugTarget over a Simulation.</summary>
    public sealed partial class DebugTarget : IDebugTarget
    {
        private readonly Simulation _sim;
        private Stepper S => _sim.Stepper;
        public DebugOptions Options => _sim.Stepper.Options;

        public event EventHandler<StoppedEventArgs>? Stopped;
        public event EventHandler<ContinuedEventArgs>? Continued;
        public event EventHandler<TaskEventArgs>? Parked;
        public event EventHandler<BreakpointEventArgs>? BreakpointChanged;
        public event EventHandler<HardwareErrorEventArgs>? ErrorReported;
        public event EventHandler<TaskEventArgs>? TaskStarted;
        public event EventHandler<TaskEventArgs>? TaskExited;
        public event EventHandler<OutputEventArgs>? Output;
        public event EventHandler? Terminated;

        private sealed class FrameH
        {
            public CicodeTask Task = null!;
            public int Serial;
            public FrameInfo Frame = null!;

            /// <summary>The frame's call (the history), its place in time (-1 live) and whether
            /// it is one of the task's frames now.</summary>
            public long CallId;
            public long View = -1;
            public bool LiveFrame = true;
        }

        private enum VarKind
        {
            Locals,
            Module,
            Globals,
            Tags,
            Array,
        }

        private sealed class VarH
        {
            public VarKind Kind;
            public CicodeTask? Task;
            public int Serial;
            public int AnySerial;
            public FrameInfo? Frame;
            public CodeVarSlot? Slot;
            public CodeVar? Var;
            public int[] Dims = Array.Empty<int>();
            public long[] Prefix = Array.Empty<long>();
            public string Name = "";

            /// <summary>The view's place in time (-1 live), the frame's call and whether it is live.</summary>
            public long View = -1;
            public long CallId;
            public bool LiveFrame = true;
        }

        private readonly Dictionary<CicodeTask, int> _serial = new Dictionary<CicodeTask, int>();
        private int _anySerial;
        private int _nextId = 1;
        private readonly Dictionary<int, FrameH> _frames = new Dictionary<int, FrameH>();
        private readonly Dictionary<int, VarH> _vars = new Dictionary<int, VarH>();

        internal DebugTarget(Simulation sim)
        {
            _sim = sim;
            var s = sim.Stepper;
            s.Stopped += OnStopped;
            s.ErrorReported += OnErrorReported;
            s.Parked += t =>
            {
                Bump(t);
                var a = TaskArgs(t);
                Raise(() => Parked?.Invoke(this, a));
            };
            s.Released += t =>
            {
                Bump(t);
                var a = new ContinuedEventArgs { Task = t.Handle };
                Raise(() => Continued?.Invoke(this, a));
            };
            s.LogPoint += (t, id, msg) =>
            {
                string text = Interpolate(t, msg);
                var a = new OutputEventArgs
                {
                    Category = "console",
                    Text = text + "\n",
                    File = t.Func?.Filename,
                    Line = t.Line,
                };
                Raise(() => Output?.Invoke(this, a));
            };
            s.Condition = Condition;
            sim.TaskStarted += ts =>
                TaskStarted?.Invoke(this, new TaskEventArgs { Task = ts.Handle, Name = ts.Name });
            sim.TaskExited += ts =>
                TaskExited?.Invoke(
                    this,
                    new TaskEventArgs
                    {
                        Task = ts.Handle,
                        Name = ts.Name,
                        State = ts.State,
                        HaltCode = ts.HaltCode,
                        HaltReason = ts.HaltReason,
                    }
                );
            sim.Log += e =>
            {
                if (e.Kind == "hardware")
                    return; // ErrorReported carries those
                Output?.Invoke(
                    this,
                    new OutputEventArgs
                    {
                        Category = e.Level == "error" ? "stderr" : "console",
                        Text = e.Text + "\n",
                        File = e.File.Length > 0 ? e.File : null,
                        Line = e.Line,
                    }
                );
            };
        }

        private void Raise(Action a) => _sim.Events.Post(a);

        private static TaskEventArgs TaskArgs(CicodeTask t) =>
            new TaskEventArgs
            {
                Task = t.Handle,
                Name = t.Name,
                State = t.State,
                HaltCode = t.HaltCode,
                HaltReason = t.HaltReason,
            };

        // ------------------------------------------------------------------ stepper events
        private void OnStopped(CicodeTask t, StopReason reason, int code, string text, int bp)
        {
            Bump(t);
            var a = new StoppedEventArgs
            {
                Task = t.Handle,
                Reason = reason,
                Code = code,
                Text = text,
                HitBreakpoints = bp != 0 ? new[] { bp } : Array.Empty<int>(),
            };
            Raise(() => Stopped?.Invoke(this, a));
        }

        private void OnErrorReported(ErrorReportInfo info)
        {
            var a = new HardwareErrorEventArgs
            {
                Task = info.Task.Handle,
                TaskName = info.Task.Name,
                Code = info.Code,
                Text = info.Text,
                ErrPage = info.ErrPage,
                ErrDesc = info.ErrDesc,
                Function = info.Function,
                File = info.File,
                Line = info.Line,
                Halting = info.Halting,
                Foreground = info.Foreground,
                Notice = info.Notice,
                Announce = info.Notice
                    ? "Thread " + info.Task.Handle.ToString(CultureInfo.InvariantCulture) + " " + info.Text
                    : info.Announce,
            };
            Raise(() => ErrorReported?.Invoke(this, a));
        }

        internal void OnTaskEnded(CicodeTask t)
        {
            _serial.Remove(t);
            Purge(t);
        }

        internal void OnTerminated() => Raise(() => Terminated?.Invoke(this, EventArgs.Empty));

        /// <summary>The library changed (ctsim/reload: Simulation.Reload): breakpoints resolve again.</summary>
        internal void LibraryChanged()
        {
            foreach (var r in S.Rebind())
            {
                var a = new BreakpointEventArgs { Breakpoint = r };
                Raise(() => BreakpointChanged?.Invoke(this, a));
            }
        }

        // ------------------------------------------------------------------ ids
        private void Bump(CicodeTask t)
        {
            _serial[t] = (_serial.TryGetValue(t, out int s) ? s : 0) + 1;
            _anySerial++;
            Purge(t);
        }

        private void Purge(CicodeTask t)
        {
            List<int>? gone = null;
            foreach (var kv in _frames)
                if (kv.Value.Task == t)
                    (gone ??= new List<int>()).Add(kv.Key);
            if (gone != null)
                foreach (int k in gone)
                    _frames.Remove(k);
            gone = null;
            foreach (var kv in _vars)
                if (kv.Value.Task == t || (kv.Value.Task == null && kv.Value.AnySerial != _anySerial))
                    (gone ??= new List<int>()).Add(kv.Key);
            if (gone != null)
                foreach (int k in gone)
                    _vars.Remove(k);
        }

        private int Serial(CicodeTask t) => _serial.TryGetValue(t, out int s) ? s : 0;

        private int NewId()
        {
            int id = _nextId++;
            if (_nextId >= int.MaxValue - 1)
                _nextId = 1;
            return id;
        }

        private FrameH GetFrame(int id)
        {
            if (
                !_frames.TryGetValue(id, out var f)
                || f.Task.Done
                || !f.Task.Pause
                || Serial(f.Task) != f.Serial
            )
                throw new ArgumentException(
                    "frame "
                        + id.ToString(CultureInfo.InvariantCulture)
                        + " is no longer valid (its task has run on)"
                );
            return f;
        }

        private VarH GetVar(int id)
        {
            if (!_vars.TryGetValue(id, out var v))
                throw new ArgumentException(
                    "variable reference " + id.ToString(CultureInfo.InvariantCulture) + " is no longer valid"
                );
            if (v.Task != null)
            {
                if (v.Task.Done || !v.Task.Pause || Serial(v.Task) != v.Serial)
                    throw new ArgumentException(
                        "variable reference "
                            + id.ToString(CultureInfo.InvariantCulture)
                            + " is no longer valid (its task has run on)"
                    );
            }
            else if (v.AnySerial != _anySerial)
                throw new ArgumentException(
                    "variable reference "
                        + id.ToString(CultureInfo.InvariantCulture)
                        + " is no longer valid (a task has run on)"
                );
            return v;
        }

        private int NewVar(VarH v)
        {
            if (v.Task != null)
                v.Serial = Serial(v.Task);
            v.AnySerial = _anySerial;
            int id = NewId();
            _vars[id] = v;
            return id;
        }

        private CicodeTask FindTask(int id)
        {
            if (_sim.Scheduler.Tasks.TryGetValue(id, out var t) && !t.Done)
                return t;
            foreach (var p in S.PausedTasks())
                if (p.Handle == id)
                    return p;
            throw new ArgumentException("no task " + id.ToString(CultureInfo.InvariantCulture));
        }

        // ------------------------------------------------------------------ IDebugTarget
        public IReadOnlyList<DebugTaskInfo> Tasks() =>
            _sim.InvokeDebug(() =>
            {
                var r = new List<DebugTaskInfo>();
                var seen = new HashSet<CicodeTask>();
                _sim.Scheduler.DropEnded();
                foreach (var t in _sim.Scheduler.All)
                    if (!t.Done && !t.NoDebug && seen.Add(t))
                        r.Add(Info(t));
                foreach (var t in S.PausedTasks())
                    if (!t.NoDebug && seen.Add(t))
                        r.Add(Info(t));
                return (IReadOnlyList<DebugTaskInfo>)r;
            });

        private static DebugTaskInfo Info(CicodeTask t) =>
            new DebugTaskInfo
            {
                Id = t.Handle,
                Name = t.Name,
                State = t.StateName,
                Foreground = t.Foreground,
                Paused = t.Pause,
                Parked = t.Pause && t.StopReason == "parked",
                StopReason = t.Pause ? t.StopReason : "",
                Function = t.Func?.Name ?? "",
                Line = t.Line,
            };

        /// <summary>A debugger session starts; the hardware error rule applies until Detach.</summary>
        public void Attach() => _sim.InvokeDebug(() => S.Attach());

        public bool Attached => S.SessionActive;

        public IReadOnlyList<BreakpointResult> SetBreakpoints(
            string file,
            IReadOnlyList<BreakpointRequest> lines
        ) =>
            _sim.InvokeDebug(() =>
            {
                S.Attach();
                return (IReadOnlyList<BreakpointResult>)S.SetBreakpoints(file, lines);
            });

        public void Continue(int? task) =>
            _sim.InvokeDebug(() =>
            {
                S.Attach();
                if (task == null)
                {
                    foreach (var t in S.PausedTasks())
                    {
                        GoLive(t);
                        Bump(t);
                    }
                    S.Continue(null);
                }
                else
                {
                    var t = FindTask(task.Value);
                    GoLive(t);
                    S.Continue(t);
                    Bump(t);
                }
            });

        public void Pause(int task) =>
            _sim.InvokeDebug(() =>
            {
                S.Attach();
                var t = FindTask(task);
                if (!t.Foreground || Options.ForegroundCanBreak)
                    S.RequestPause(t);
            });

        public void Step(int task, StepKind kind) =>
            _sim.InvokeDebug(() =>
            {
                S.Attach();
                var t = FindTask(task);
                if (!t.Pause)
                    throw new ArgumentException(
                        "task " + task.ToString(CultureInfo.InvariantCulture) + " is not stopped"
                    );
                GoLive(t);
                S.Step(t, kind);
                Bump(t);
            });

        public IReadOnlyList<DebugFrame> StackTrace(int task) =>
            _sim.InvokeDebug(() =>
            {
                var t = FindTask(task);
                var r = new List<DebugFrame>();
                var h = Hist(t);
                foreach (var vf in ViewFrames(t, h))
                {
                    var f = vf.Info;
                    int id = NewId();
                    _frames[id] = new FrameH
                    {
                        Task = t,
                        Serial = Serial(t),
                        Frame = f,
                        CallId = vf.CallId,
                        View = h?.Cursor ?? -1,
                        LiveFrame = vf.LiveFrame,
                    };
                    r.Add(
                        new DebugFrame
                        {
                            Id = id,
                            Function = f.Name,
                            File = f.File,
                            Line = f.Line,
                            Pc = f.Pc,
                            Task = t.Handle,
                            CallId = vf.CallId,
                        }
                    );
                }
                return (IReadOnlyList<DebugFrame>)r;
            });

        public IReadOnlyList<DebugScope> Scopes(int frameId) =>
            _sim.InvokeDebug(() =>
            {
                var f = GetFrame(frameId);
                var lib = LibraryVars.For(_sim.Library);
                var r = new List<DebugScope>();
                var hist = Hist(f.Task);
                int locals =
                    f.View >= 0 && hist != null
                        ? ViewLocals(f.Task, hist, f.View, f.CallId, f.Frame, f.LiveFrame).Count
                        : DebugInspect.Locals(f.Task, f.Frame).Count;
                r.Add(Scope("Locals", VarKind.Locals, f, locals));
                string key = SourceFiles.Key(f.Frame.File);
                int module = lib.Module.TryGetValue(key, out var mv) ? mv.Count : 0;
                if (module > 0)
                    r.Add(Scope("Module", VarKind.Module, f, module));
                r.Add(Scope("Globals", VarKind.Globals, f, lib.Globals.Count));
                int tags =
                    f.Frame.Func != null && lib.Tags.TryGetValue(f.Frame.Func, out var tl) ? tl.Count : 0;
                if (tags > 0)
                    r.Add(Scope("Tags", VarKind.Tags, f, tags));
                return (IReadOnlyList<DebugScope>)r;
            });

        private DebugScope Scope(string name, VarKind kind, FrameH f, int count) =>
            new DebugScope
            {
                Name = name,
                VariablesReference = NewVar(
                    new VarH
                    {
                        Kind = kind,
                        Task = f.Task,
                        Frame = f.Frame,
                        View = f.View,
                        CallId = f.CallId,
                        LiveFrame = f.LiveFrame,
                    }
                ),
                NamedVariables = count,
                Expensive = false,
            };

        public IReadOnlyList<DebugVariable> Variables(int variablesReference, int start = 0, int count = 0) =>
            _sim.InvokeDebug(() =>
            {
                var h = GetVar(variablesReference);
                var all = Children(h, start, count);
                return (IReadOnlyList<DebugVariable>)all;
            });

        private List<DebugVariable> Children(VarH h, int start, int count)
        {
            var lib = LibraryVars.For(_sim.Library);
            var r = new List<DebugVariable>();
            if (start < 0)
                start = 0;
            int end = count > 0 ? start + count : int.MaxValue;
            switch (h.Kind)
            {
                case VarKind.Locals:
                {
                    var hist = h.Task != null ? Hist(h.Task) : null;
                    var locals =
                        h.View >= 0 && hist != null
                            ? ViewLocals(h.Task!, hist, h.View, h.CallId, h.Frame!, h.LiveFrame)
                            : DebugInspect.Locals(h.Task!, h.Frame!);
                    for (int i = start; i < locals.Count && i < end; i++)
                    {
                        var (name, _, v) = locals[i];
                        r.Add(
                            new DebugVariable
                            {
                                Name = name,
                                Value = ValueFormat.Display(v),
                                Type = ValueFormat.TypeName(v.T),
                                EvaluateName = name,
                            }
                        );
                    }
                    break;
                }
                case VarKind.Module:
                case VarKind.Globals:
                {
                    List<CodeVar>? list;
                    if (h.Kind == VarKind.Globals)
                        list = lib.Globals;
                    else
                        lib.Module.TryGetValue(SourceFiles.Key(h.Frame!.File), out list);
                    if (list != null)
                        for (int i = start; i < list.Count && i < end; i++)
                            r.Add(VarOf(lib, list[i], h.Task, h.View));
                    break;
                }
                case VarKind.Tags:
                {
                    if (h.Frame?.Func != null && lib.Tags.TryGetValue(h.Frame.Func, out var tags))
                        for (int i = start; i < tags.Count && i < end; i++)
                        {
                            var tr = tags[i];
                            string name = LibraryVars.TagName(tr);
                            var db = _sim.World.Tags;
                            Value v = db.ReadResolved(db.ResolveRef(tr), null, false, "", out int err);
                            var dv = new DebugVariable
                            {
                                Name = name,
                                Value = ValueFormat.Display(v),
                                Type = "tag " + ValueFormat.TypeName(v.T),
                                EvaluateName = name,
                            };
                            if (err != 0)
                                dv.Value +=
                                    " (error "
                                    + err.ToString(CultureInfo.InvariantCulture)
                                    + ": "
                                    + Errors.Text(err)
                                    + ")";
                            dv.ReadOnly = tr.Item.Length > 0 && tr.Item != "v";
                            r.Add(dv);
                        }
                    break;
                }
                case VarKind.Array:
                {
                    int level = h.Prefix.Length;
                    int n = h.Dims[level];
                    for (int i = start; i < n && i < end; i++)
                    {
                        var idx = new long[level + 1];
                        Array.Copy(h.Prefix, idx, level);
                        idx[level] = i;
                        string nm = "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                        string ev = h.Name + Brackets(idx);
                        if (level + 1 < h.Dims.Length)
                        {
                            var sub = new VarH
                            {
                                Kind = VarKind.Array,
                                Task = h.Task,
                                Slot = h.Slot,
                                Var = h.Var,
                                Dims = h.Dims,
                                Prefix = idx,
                                Name = h.Name,
                                View = h.View,
                            };
                            r.Add(
                                new DebugVariable
                                {
                                    Name = nm,
                                    Value = TypeWithDims(h.Slot!.VType, h.Dims, level + 1),
                                    Type = TypeWithDims(h.Slot.VType, h.Dims, level + 1),
                                    VariablesReference = NewVar(sub),
                                    IndexedVariables = h.Dims[level + 1],
                                    EvaluateName = ev,
                                    ReadOnly = true,
                                }
                            );
                        }
                        else
                        {
                            var vh = h.View >= 0 && h.Task != null ? Hist(h.Task) : null;
                            Value v =
                                vh != null
                                    ? ViewCodeVar(vh, h.View, h.Slot!, Flat(h.Dims, idx))
                                    : h.Slot!.Get(Flat(h.Dims, idx), out _);
                            r.Add(
                                new DebugVariable
                                {
                                    Name = nm,
                                    Value = ValueFormat.Display(v),
                                    Type = ValueFormat.TypeName(v.T),
                                    EvaluateName = ev,
                                    ReadOnly = vh != null,
                                }
                            );
                        }
                    }
                    break;
                }
            }
            return r;
        }

        private static string Brackets(long[] idx)
        {
            var sb = new StringBuilder();
            foreach (long i in idx)
                sb.Append('[').Append(i.ToString(CultureInfo.InvariantCulture)).Append(']');
            return sb.ToString();
        }

        private static long Flat(int[] dims, IReadOnlyList<long> idx)
        {
            long acc = 0;
            for (int k = 0; k < dims.Length; k++)
                acc = dims[k] * acc + (k < idx.Count ? idx[k] : 0);
            return acc;
        }

        private static string TypeWithDims(int vt, int[] dims, int from)
        {
            var sb = new StringBuilder(ValueFormat.TypeName(vt));
            for (int k = from; k < dims.Length; k++)
                sb.Append('[').Append(dims[k].ToString(CultureInfo.InvariantCulture)).Append(']');
            return sb.ToString();
        }

        private DebugVariable VarOf(LibraryVars lib, CodeVar cv, CicodeTask? owner, long view = -1)
        {
            var slot = _sim.Vm.Slot(_sim.Library, cv);
            int[] dims = lib.DimsOf(cv);
            var vh = view >= 0 && owner != null ? Hist(owner) : null;
            if (dims.Length > 0)
            {
                var h = new VarH
                {
                    Kind = VarKind.Array,
                    Task = owner,
                    Slot = slot,
                    Var = cv,
                    Dims = dims,
                    Name = cv.Name,
                    View = vh != null ? view : -1,
                };
                string t = TypeWithDims(slot.VType, dims, 0);
                return new DebugVariable
                {
                    Name = cv.Name,
                    Value = t,
                    Type = t,
                    VariablesReference = NewVar(h),
                    IndexedVariables = dims[0],
                    EvaluateName = cv.Name,
                    ReadOnly = true,
                };
            }
            Value v = vh != null ? ViewCodeVar(vh, view, slot, 0) : slot.Get(0, out _);
            return new DebugVariable
            {
                Name = cv.Name,
                Value = ValueFormat.Display(v),
                Type = ValueFormat.TypeName(v.T),
                EvaluateName = cv.Name,
                ReadOnly = vh != null,
            };
        }

        // ------------------------------------------------------------------ evaluate
        public EvaluateResult Evaluate(
            string expression,
            int? frameId,
            EvaluateContext context = EvaluateContext.Repl
        ) =>
            _sim.InvokeDebug(() =>
            {
                var res = new EvaluateResult();
                try
                {
                    FrameH? f = frameId != null ? GetFrame(frameId.Value) : null;
                    if (f != null && f.View >= 0)
                        return EvaluateInView(f, (expression ?? "").Trim());
                    CicodeTask? owner = f?.Task;
                    FrameInfo? frame = f?.Frame;
                    var lib = LibraryVars.For(_sim.Library);
                    string expr = (expression ?? "").Trim();
                    // a bare array name: its elements as children
                    var cvArr = FindVar(lib, expr, frame);
                    if (
                        cvArr != null
                        && lib.DimsOf(cvArr).Length > 0
                        && (frame == null || !IsLocal(owner!, frame, expr))
                    )
                    {
                        var dv = VarOf(lib, cvArr, owner);
                        res.Result = dv.Value;
                        res.Type = dv.Type;
                        res.VariablesReference = dv.VariablesReference;
                        res.IndexedVariables = dv.IndexedVariables;
                        return res;
                    }
                    var ctx = new OpCtx(_sim.World.Clock.NowFiletime(), true);
                    bool hover = context == EvaluateContext.Hover;
                    var deadline = Stopwatch.StartNew();
                    var ev = new WatchEval
                    {
                        Ctx = ctx,
                        Name = (n, idx) => ResolveName(lib, owner, frame, n, idx, hover),
                        Call = hover
                            ? null
                            : (Func<string, List<Value>, Value>)(
                                (n, args) =>
                                    RunCall(
                                        n,
                                        args,
                                        Options.EvaluateTimeoutMs - (int)deadline.ElapsedMilliseconds
                                    )
                            ),
                    };
                    Value v = ev.Evaluate(expr);
                    res.Result = ValueFormat.Display(v);
                    res.Type = ValueFormat.TypeName(v.T);
                    if (ctx.Errors.Count > 0)
                    {
                        res.Error = ctx.Errors[0].Code;
                        res.ErrorText = Errors.Text(res.Error);
                    }
                }
                catch (EvalException e)
                {
                    res.Error = e.Code;
                    res.ErrorText = e.Message;
                }
                catch (ArgumentException e)
                {
                    res.ErrorText = e.Message;
                }
                catch (Exception e)
                {
                    // never thrown out of Evaluate (IDebugTarget): a VM fault in a called function ...
                    res.ErrorText = e.GetType().Name + ": " + e.Message;
                }
                return res;
            });

        private static bool IsLocal(CicodeTask t, FrameInfo frame, string name)
        {
            foreach (var (n, _, _) in DebugInspect.Locals(t, frame))
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>A module / global variable of that name: the frame file's MODULE variable
        /// first, then a GLOBAL, then any.</summary>
        private static CodeVar? FindVar(LibraryVars lib, string name, FrameInfo? frame)
        {
            if (!lib.ByName.TryGetValue(name, out var list) || list.Count == 0)
                return null;
            if (frame != null)
            {
                string key = SourceFiles.Key(frame.File);
                foreach (var cv in list)
                    if (cv.UnitType != -1 && lib.Files.TryGetValue(cv, out var fs) && fs.Contains(key))
                        return cv;
            }
            foreach (var cv in list)
                if (cv.UnitType == -1)
                    return cv;
            return list[0];
        }

        private Value? ResolveName(
            LibraryVars lib,
            CicodeTask? owner,
            FrameInfo? frame,
            string name,
            IReadOnlyList<long> idx,
            bool hover
        )
        {
            if (
                frame != null
                && owner != null
                && idx.Count == 0
                && DebugInspect.TryLocal(owner, frame, name, out var local)
            )
                return local;
            var cv = FindVar(lib, name, frame);
            if (cv != null)
            {
                var slot = _sim.Vm.Slot(_sim.Library, cv);
                int[] dims = lib.DimsOf(cv);
                if (dims.Length == 0)
                {
                    if (idx.Count > 0)
                        throw new EvalException(name + " is not an array");
                    return slot.Get(0, out _);
                }
                if (idx.Count != dims.Length)
                    throw new EvalException(
                        name
                            + " is an array of "
                            + dims.Length.ToString(CultureInfo.InvariantCulture)
                            + " dimension(s): give each index"
                    );
                for (int k = 0; k < dims.Length; k++)
                    if (idx[k] < 0 || idx[k] >= dims[k])
                        throw new EvalException(
                            "index "
                                + idx[k].ToString(CultureInfo.InvariantCulture)
                                + " is outside "
                                + name
                                + " (Array has been overrun)",
                            Errors.Overrun
                        );
                return slot.Get(Flat(dims, idx), out _);
            }
            var db = _sim.World.Tags;
            var r = db.Resolve(name);
            if (r.Found)
            {
                int? index =
                    idx.Count == 1 ? (int?)(int)Math.Max(int.MinValue, Math.Min(int.MaxValue, idx[0])) : null;
                return db.ReadResolved(r, index, false, hover ? "" : "debugger", out _);
            }
            long? label = _sim.World.Unit.LabelValue(name);
            if (label != null && idx.Count == 0)
                return Value.Long((int)label.Value);
            return null;
        }

        /// <summary>Run a call to its end now, on the simulation thread, ignored by the debugger
        /// (no breakpoint, no step, no error stop), within <paramref name="budgetMs"/> of real time.</summary>
        private Value RunCall(string name, List<Value> args, int budgetMs)
        {
            var vm = _sim.Vm;
            CicodeTask task;
            var fn = _sim.Library.Function(name);
            if (fn != null && fn.Private)
                fn = null;
            if (fn != null)
                task = vm.TaskForCall(fn, args, false, null, "evaluate " + fn.Name);
            else
            {
                var b = vm.Table.Lookup(name)
                    ?? throw new EvalException(
                        "no function '" + name + "' (Invalid function)",
                        Errors.InvalidFunction
                    );
                if (args.Count > b.Args.Length && !b.Args.Contains("@"))
                    throw new EvalException(
                        b.Name
                            + " takes "
                            + b.Args.Length.ToString(CultureInfo.InvariantCulture)
                            + " argument(s)",
                        Errors.TooManyArgs
                    );
                if (args.Count < b.MinArgs)
                    throw new EvalException(
                        b.Name
                            + " needs "
                            + b.MinArgs.ToString(CultureInfo.InvariantCulture)
                            + " argument(s)",
                        Errors.TooFewArgs
                    );
                var pools = new Pools();
                var cb = new CodeBuilder(pools);
                for (int k = 0; k < args.Count; k++)
                {
                    if (b.ByRef(k))
                        throw new EvalException(
                            b.Name
                                + " takes argument "
                                + (k + 1).ToString(CultureInfo.InvariantCulture)
                                + " by reference"
                        );
                    Value v = args[k];
                    int t = k < b.Args.Length ? Types.SigType(b.Args[k]) : v.T;
                    if (t == Types.String)
                        cb.ConstStr(Conv.ToStr(v, NoErr.Instance).S ?? "");
                    else if (Types.IsRealType(t))
                        cb.ConstReal(Conv.ToReal(v, NoErr.Instance).D);
                    else if (t == Types.Timestamp || t == Types.Quality)
                        cb.ConstU64(t, unchecked((ulong)v.I));
                    else
                        cb.ConstInt(Conv.ToInt(v, NoErr.Instance).I);
                }
                cb.Func(b.Index, args.Count);
                cb.End();
                task = vm.TaskForBlock(cb.Code(), pools.Fill(new CProgram()), false, "evaluate " + b.Name);
            }
            task.NoDebug = true;
            var sw = Stopwatch.StartNew();
            var clock = _sim.World.Clock;
            while (!task.Done && sw.ElapsedMilliseconds < Math.Max(1, budgetMs))
            {
                if (task.State == TaskState.Wait || task.State == TaskState.Sleep)
                {
                    var w = task.Wait;
                    if (w == null || w.Ready(clock))
                    {
                        vm.Resume(task);
                        continue;
                    }
                    if (clock.Mode == World.SimClock.Run)
                        clock.Poll();
                    Thread.Sleep(1);
                    continue;
                }
                vm.RunSlice(task);
            }
            if (!task.Done)
            {
                task.State = TaskState.Halt;
                task.HaltReason = "evaluation stopped";
                throw new EvalException(
                    "the call did not end in "
                        + Math.Max(1, budgetMs).ToString(CultureInfo.InvariantCulture)
                        + " ms (it waits or loops)"
                );
            }
            if (task.State == TaskState.Halt)
                throw new EvalException(name + " halted: " + task.HaltReason, task.HaltCode);
            if (task.Result == null)
                return Value.Int(Types.Void, 0);
            Value r = task.Result.Value;
            return r.T == Types.Variant ? Value.Deref(r) : r;
        }

        /// <summary>A breakpoint condition, evaluated in the stopping task's current frame
        /// (true = stop; null = it cannot be evaluated: stop).</summary>
        private bool? Condition(CicodeTask t, string expr)
        {
            try
            {
                var lib = LibraryVars.For(_sim.Library);
                var frame = DebugInspect.Top(t);
                var ctx = new OpCtx(_sim.World.Clock.NowFiletime(), true);
                var ev = new WatchEval
                {
                    Ctx = ctx,
                    Name = (n, idx) => ResolveName(lib, t, frame, n, idx, true),
                    Call = (n, args) => RunCall(n, args, Options.EvaluateTimeoutMs),
                };
                Value v = ev.Evaluate(expr);
                if (v.T == Types.String)
                    return !string.IsNullOrEmpty(v.S);
                int truth = Ops.Truth(v, ctx);
                return truth < 0 ? (bool?)null : truth != 0;
            }
            catch (Exception)
            {
                // a condition that cannot be evaluated stops (it runs inside the task's slice:
                // nothing may escape into the interpreter)
                return null;
            }
        }

        /// <summary>A log message with each {expression} replaced by its value (a hover:
        /// names only).</summary>
        private string Interpolate(CicodeTask t, string msg)
        {
            if (msg.IndexOf('{') < 0)
                return msg;
            var sb = new StringBuilder();
            var lib = LibraryVars.For(_sim.Library);
            var frame = DebugInspect.Top(t);
            int i = 0;
            while (i < msg.Length)
            {
                int a = msg.IndexOf('{', i);
                if (a < 0)
                {
                    sb.Append(msg, i, msg.Length - i);
                    break;
                }
                int b = msg.IndexOf('}', a + 1);
                if (b < 0)
                {
                    sb.Append(msg, i, msg.Length - i);
                    break;
                }
                sb.Append(msg, i, a - i);
                string e = msg.Substring(a + 1, b - a - 1);
                try
                {
                    var ev = new WatchEval
                    {
                        Ctx = new OpCtx(_sim.World.Clock.NowFiletime(), true),
                        Name = (n, idx) => ResolveName(lib, t, frame, n, idx, true),
                    };
                    sb.Append(ValueFormat.Display(ev.Evaluate(e)));
                }
                catch (Exception ex)
                {
                    sb.Append("<" + ex.Message + ">");
                }
                i = b + 1;
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ set variable
        public DebugVariable SetVariable(int variablesReference, string name, string value) =>
            _sim.InvokeDebug(() =>
            {
                var h = GetVar(variablesReference);
                if (h.View >= 0)
                    throw NotLive();
                var lib = LibraryVars.For(_sim.Library);
                Value nv = ValueFormat.Parse(value, out bool isStr);
                switch (h.Kind)
                {
                    case VarKind.Locals:
                    {
                        foreach (var (n, _, cur) in DebugInspect.Locals(h.Task!, h.Frame!))
                        {
                            if (!string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                                continue;
                            Value conv = Checked(nv, isStr, cur.T, n);
                            if (!DebugInspect.SetLocal(h.Task!, h.Frame!, n, conv))
                                throw new ArgumentException("the local " + n + " cannot be set now");
                            foreach (var (n2, _, v2) in DebugInspect.Locals(h.Task!, h.Frame!))
                                if (n2 == n)
                                    return new DebugVariable
                                    {
                                        Name = n,
                                        Value = ValueFormat.Display(v2),
                                        Type = ValueFormat.TypeName(v2.T),
                                        EvaluateName = n,
                                    };
                        }
                        throw new ArgumentException("no local variable " + name);
                    }
                    case VarKind.Module:
                    case VarKind.Globals:
                    {
                        List<CodeVar>? list;
                        if (h.Kind == VarKind.Globals)
                            list = lib.Globals;
                        else
                            lib.Module.TryGetValue(SourceFiles.Key(h.Frame!.File), out list);
                        CodeVar? cv = list?.Find(x =>
                            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)
                        );
                        if (cv == null)
                            throw new ArgumentException("no variable " + name + " in this scope");
                        if (lib.DimsOf(cv).Length > 0)
                            throw new ArgumentException(name + " is an array: set its elements");
                        var slot = _sim.Vm.Slot(_sim.Library, cv);
                        int err = slot.Set(0, Checked(nv, isStr, slot.VType, name));
                        if (err != 0)
                            throw new ArgumentException(
                                name + " cannot take " + value + ": " + Errors.Text(err)
                            );
                        return VarOf(lib, cv, h.Task);
                    }
                    case VarKind.Tags:
                    {
                        var db = _sim.World.Tags;
                        var r = db.Resolve(name);
                        if (!r.Found)
                            throw new ArgumentException("no tag " + name);
                        JsonValue j =
                            isStr ? JsonValue.Of(nv.S ?? "")
                            : Types.IsRealType(nv.T) ? JsonValue.Of(nv.D)
                            : JsonValue.Of(nv.I);
                        int err = db.Set(name, j, null, by: "debugger");
                        if (err != 0)
                            throw new ArgumentException(
                                "the tag "
                                    + name
                                    + " cannot take "
                                    + value
                                    + ": error "
                                    + err.ToString(CultureInfo.InvariantCulture)
                                    + " "
                                    + Errors.Text(err)
                            );
                        _sim.FlushSoon();
                        Value v = db.Get(name);
                        return new DebugVariable
                        {
                            Name = name,
                            Value = ValueFormat.Display(v),
                            Type = "tag " + ValueFormat.TypeName(v.T),
                            EvaluateName = name,
                        };
                    }
                    case VarKind.Array:
                    {
                        string s = name.Trim();
                        if (
                            !(
                                s.StartsWith("[", StringComparison.Ordinal)
                                && s.EndsWith("]", StringComparison.Ordinal)
                            )
                            || !long.TryParse(
                                s.Substring(1, s.Length - 2),
                                NumberStyles.Integer,
                                CultureInfo.InvariantCulture,
                                out long i
                            )
                        )
                            throw new ArgumentException("an array element is named [index]: " + name);
                        int level = h.Prefix.Length;
                        if (i < 0 || i >= h.Dims[level])
                            throw new ArgumentException(
                                "index "
                                    + i.ToString(CultureInfo.InvariantCulture)
                                    + " is outside the array (Array has been overrun)"
                            );
                        if (level + 1 < h.Dims.Length)
                            throw new ArgumentException(name + " is a row of the array: set its elements");
                        var idx = new long[level + 1];
                        Array.Copy(h.Prefix, idx, level);
                        idx[level] = i;
                        int err = h.Slot!.Set(
                            Flat(h.Dims, idx),
                            Checked(nv, isStr, h.Slot.VType, h.Name + Brackets(idx))
                        );
                        if (err != 0)
                            throw new ArgumentException(
                                name + " cannot take " + value + ": " + Errors.Text(err)
                            );
                        Value v = h.Slot.Get(Flat(h.Dims, idx), out _);
                        return new DebugVariable
                        {
                            Name = name,
                            Value = ValueFormat.Display(v),
                            Type = ValueFormat.TypeName(v.T),
                            EvaluateName = h.Name + Brackets(idx),
                        };
                    }
                }
                throw new ArgumentException("this variable cannot be set");
            });

        /// <summary>A typed value for a variable of type <paramref name="t"/>; ArgumentException
        /// when it is not of its type.</summary>
        private static Value Checked(Value v, bool isStr, int t, string what)
        {
            if (t == Types.String)
                return isStr ? v : Conv.ToStr(v, NoErr.Instance);
            if (t == Types.Timestamp || t == Types.Quality || t == Types.Object)
            {
                if (isStr || Types.IsRealType(v.T))
                    throw new ArgumentException(
                        what + " is " + ValueFormat.TypeName(t) + ": give an integer"
                    );
                return Value.Int(t, v.I);
            }
            if (isStr)
            {
                string s = v.S ?? "";
                if (!FloatText.TryParseNumber(s.Trim(), out double d))
                    throw new ArgumentException(
                        Project.CitectText.Quote(s)
                            + " is not a number ("
                            + what
                            + " is "
                            + ValueFormat.TypeName(t)
                            + ")"
                    );
                v = Value.Real(d);
            }
            if (Types.IsRealType(t))
                return Conv.ToReal(v, NoErr.Instance);
            if (
                Types.IsRealType(v.T)
                && (v.D > int.MaxValue + 0.5 || v.D < int.MinValue - 0.5 || double.IsNaN(v.D))
            )
                throw new ArgumentException(
                    FloatText.Shortest(v.D) + " is outside the range of " + what + " (INT)"
                );
            return Conv.ToInt(v, NoErr.Instance);
        }

        // ------------------------------------------------------------------ detach
        public void Detach() => _sim.InvokeDebug(DetachHere);

        /// <summary><see cref="Attach"/> for a debugger session that starts now: the number of
        /// detaches so far, which the server gives <see cref="DetachSession"/> when the session's
        /// connection closes.</summary>
        public long AttachSession() =>
            _sim.InvokeDebug(() =>
            {
                S.Attach();
                return _detaches;
            });

        /// <summary>Detach for a closed session, unless another detach ran since it attached.</summary>
        public bool DetachSession(long attached) =>
            _sim.InvokeDebug(() =>
            {
                if (_detaches != attached)
                    return false;
                DetachHere();
                return true;
            });

        private long _detaches;

        private void DetachHere()
        {
            foreach (var t in S.PausedTasks())
                Bump(t);
            S.Detach();
            _frames.Clear();
            _vars.Clear();
            _anySerial++;
            _detaches++;
        }
    }
}
