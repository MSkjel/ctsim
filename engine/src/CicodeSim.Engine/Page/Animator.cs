using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World.Display;
using CProgram = CicodeSim.Compiled.Program;
using WorldT = CicodeSim.World.World;

namespace CicodeSim.Page
{
    /// <summary>A page event the animator records (the trace's `page` events).</summary>
    public sealed class PageEvent
    {
        public double T;
        public int Window;
        public string Page = "";
        public string Event = "";
        public string Detail = "";

        public override string ToString() =>
            $"{T.ToString("F3", CultureInfo.InvariantCulture)} w{Window} {Page} {Event}{(Detail.Length > 0 ? " " + Detail : "")}";
    }

    /// <summary>What the animator counted per class: evaluations, the expressions a built-in
    /// stopped as not simulated (by function) and the error codes the classes saw.</summary>
    public sealed class ClassStats
    {
        public long Objects;
        public long Evaluations;
        public Dictionary<string, long> NotSimulated = new Dictionary<string, long>(StringComparer.Ordinal);
        public Dictionary<int, long> Errors = new Dictionary<int, long>();
        public long Halted;

        /// <summary>Why the other halts happened (the halt reason, its first 80 characters).</summary>
        public Dictionary<string, long> HaltReasons = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>The halts by halt code (EvalResult.HaltCode; the not-simulated stops are
        /// counted in NotSimulated, not here).</summary>
        public Dictionary<int, long> HaltCodes = new Dictionary<int, long>();
    }

    /// <summary>The page animator and display client core.</summary>
    public sealed partial class Animator : IPageController, IPageRuntime, ISymbolPlacer, IDisposable
    {
        public readonly WorldT World;
        public readonly DisplayState Display;
        public readonly CicodeVm Vm;
        public readonly Scheduler Scheduler;
        public readonly PageTags Tags;
        public readonly KeyTable Keys;
        public readonly SimHost? Host;

        /// <summary>The library the VM runs (Vm.Library: on a simulation's VM it follows a reload).</summary>
        public CProgram Library => Vm.Library;
        public readonly Dictionary<string, ClassStats> Stats = new Dictionary<string, ClassStats>(
            StringComparer.Ordinal
        );
        public readonly List<PageEvent> Events = new List<PageEvent>();
        public readonly List<string> Logs = new List<string>();

        /// <summary>Every page change of every window: (window, the changed objects).</summary>
        public event Action<PageState, List<ObjectState>>? PageChanged;

        /// <summary>Supplies the drawing summary of a page when the page is opened without one
        /// (a viewer's reader); null = the Display's host, else none.</summary>
        public Func<PageRecord, PageDrawing?>? DrawingSource;

        /// <summary>Supplies a compiled page by name before its .RDB is read.</summary>
        public Func<string, CompiledPage?>? CompiledSource;

        /// <summary>Instructions per foreground slice before 285: the simulated clock stands still
        /// while code runs, so the slice is counted (200 x [Code]TimeSlicePage ms, the background
        /// 20000 per 100 ms of WorldRun.SliceOps scaled).</summary>
        public int ForegroundOps;

        private World.Timer? _flash,
            _anim;

        /// <summary>The touch repeat timer: ONE per process (timer, count, limit), null
        /// when none runs.</summary>
        private RepeatTimer? _repeat;
        private Dictionary<long, (long, long, long, long, long, long)>? _prioColours;
        private TrendSampler? _sampler;

        /// <summary>The trend server's sampling of the expression trends the shown trends' pens
        /// name.</summary>
        public TrendSampler Sampler => _sampler ??= new TrendSampler(this);

        private sealed class RepeatTimer
        {
            public PageRun Run = null!;

            /// <summary>The pressed object: its touch records get the repeat events.</summary>
            public int An;
            public World.Timer? Timer;
            public int Count,
                Limit;
        }

        public Animator(WorldT world, CProgram library, IBuiltins? builtins = null, CodeConfig? config = null)
        {
            World = world;
            Display = world.Display;
            if (config == null)
                config = CodeConfig.FromParams((sec, key, def) => world.Param(sec, key, def) ?? def);
            // the simulated clock stands still while code runs: without an instruction count a
            // loop in page code (foreground: 285 after the slice) would never end
            if (config.MaxSliceOps <= 0)
                config.MaxSliceOps = 200 * Math.Max(1, config.TimeSlicePage);
            ForegroundOps = config.MaxSliceOps;
            Tags = new PageTags(world.Tags, Display);
            var reg = builtins ?? WorldBuiltins.Create(world);
            Host = WorldBuiltins.HostOf(reg);
            Vm = new CicodeVm(library, reg, Tags, world.Clock, Tags, config);
            Scheduler = new Scheduler(Vm);
            Keys = KeyTable.FromUnit(world.Unit);
            TakeOver();
        }

        /// <summary>An animator over the VM that already runs the World's Cicode.</summary>
        public Animator(WorldT world, CicodeVm vm, Scheduler scheduler)
        {
            if (vm.Config.MaxSliceOps <= 0)
                throw new ArgumentException(
                    "the VM counts no instructions per slice (CodeConfig.MaxSliceOps 0): on the simulated clock a loop in page code would never end",
                    nameof(vm)
                );
            World = world;
            Display = world.Display;
            // the host's instructions per ms of its background slice, over the foreground slice:
            // the simulation's 20,000 per 100 ms gives 100,000 for TimeSlicePage 500, the count
            // the other constructor uses (RunToEnd applies it to foreground runs only)
            var vc = vm.Config;
            ForegroundOps = (int)
                Math.Max(
                    1,
                    Math.Min(
                        int.MaxValue,
                        (long)vc.MaxSliceOps * Math.Max(1, vc.TimeSlicePage) / Math.Max(1, vc.TimeSlice)
                    )
                );
            Tags = new PageTags(world.Tags, Display);
            vm.Tags = Tags;
            vm.Associations = Tags;
            Host = WorldBuiltins.HostOf(vm.Builtins);
            Vm = vm;
            Scheduler = scheduler;
            Keys = KeyTable.FromUnit(world.Unit);
            TakeOver();
        }

        /// <summary>One animator drives a World's display (its windows, flash phase, repeat
        /// timer): a new one takes over and the previous one's timers stop (else both toggle the
        /// flash phase and the clock's timers keep the old one alive).</summary>
        private void TakeOver()
        {
            if (Display.Controller is Animator prev && prev != this)
                prev.Dispose();
            Display.Controller = this;
            // the sequence of events and alarm summary the alarm lists of types 10, 15 and 16
            // show, from now on
            AlarmEventLog.Attach(World);
            StartTimers();
        }

        private bool _disposed;

        /// <summary>Stop this animator's timers (flash, symbol animation, touch repeat, the page
        /// scans of its pages) and release the World's display. The pages stay as they are (no
        /// page event runs).</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _flash?.Cancel();
            _anim?.Cancel();
            _sampler?.Dispose();
            StopRepeat();
            foreach (var w in Display.Windows)
                if (w.Page?.Runtime is PageRun r && r.Animator == this)
                {
                    r.Closed = true;
                    r.ScanTimer?.Cancel();
                }
            if (Display.Controller == this)
                Display.Controller = null;
        }

        /// <summary>An animator for a compiled project folder.</summary>
        public static Animator ForProject(
            string projectDir,
            string? root = null,
            object? start = null,
            string compile = Compiler.Codegen.LiveProgram.None,
            List<string>? notes = null,
            CProgram? library = null
        )
        {
            string dir = projectDir.TrimEnd('\\', '/');
            var world = new WorldT(dir, root: root ?? Path.GetFileName(dir), start: start);
            CProgram prog;
            if (library != null)
                prog = library;
            else if (compile == Compiler.Codegen.LiveProgram.None)
            {
                string lib = Path.Combine(dir, "_LIBRARY.RDB");
                prog = File.Exists(lib) ? Rdb.LoadLibrary(lib) : new CProgram();
            }
            else
            {
                if (!Compiler.Codegen.LiveProgram.IsMode(compile))
                    throw new ArgumentException("compile must be changed, all or none: " + compile);
                var live = Compiler.Codegen.LiveProgram.Open(world.Unit.RootDir, compile);
                if (notes != null)
                {
                    if (compile == Compiler.Codegen.LiveProgram.All)
                        notes.Add(
                            "every function from source: "
                                + live.Program.Functions.Count.ToString(CultureInfo.InvariantCulture)
                                + " functions of "
                                + live.SourceFiles.Count.ToString(CultureInfo.InvariantCulture)
                                + " files (front end "
                                + live.FrontEndMs.ToString("0", CultureInfo.InvariantCulture)
                                + " ms, code "
                                + live.CodeMs.ToString("0", CultureInfo.InvariantCulture)
                                + " ms)"
                        );
                    notes.AddRange(live.Notes);
                    foreach (var d in live.Errors)
                        notes.Add("compile error: " + d);
                    foreach (var (fn, why) in live.Failed)
                        notes.Add("not compiled from source: " + fn + ": " + why);
                }
                prog = live.Program;
            }
            world.Start();
            return new Animator(world, prog);
        }

        private void StartTimers()
        {
            var cfg = Display.Config;
            _flash = World.Clock.Every(
                cfg.FlashTime / 1000.0,
                () =>
                {
                    Display.FlashPhase ^= 1;
                    foreach (var w in Display.Windows)
                        if (w.Open && w.Page?.Runtime is PageRun r && !r.Closed)
                            r.Emit();
                },
                label: "flash"
            );
            _anim = World.Clock.Every(
                cfg.AnmDelay / 1000.0,
                () =>
                {
                    foreach (var w in Display.Windows)
                        if (w.Open && w.Page?.Runtime is PageRun r)
                            foreach (var o in r.Objects)
                                if (o.IsSymbolSet && o.TxtMode == 3)
                                    o.AnimTick = true;
                },
                label: "animate"
            );
        }

        // ================================================================== bookkeeping
        public void LogLine(string text)
        {
            Logs.Add(text);
            if (Logs.Count > 10000)
                Logs.RemoveRange(0, 5000);
        }

        /// <summary>A hardware alarm the animation classes raise (logged and counted).</summary>
        internal void HardwareAlarm(int code, string text)
        {
            LogLine("hardware alarm " + code.ToString(CultureInfo.InvariantCulture) + ": " + text);
            var s = StatsOf("hardware_alarm");
            s.Errors.TryGetValue(code, out long n);
            s.Errors[code] = n + 1;
        }

        /// <summary>Record a page event; the list keeps the last 5,000 to 10,000 (every command
        /// task adds one: a held repeat at RPT_RATE 100 adds ten a second).</summary>
        internal void Trace(int window, string page, string ev, string detail = "")
        {
            Events.Add(
                new PageEvent
                {
                    T = World.Clock.Now(),
                    Window = window,
                    Page = page,
                    Event = ev,
                    Detail = detail,
                }
            );
            if (Events.Count > 10000)
                Events.RemoveRange(0, 5000);
        }

        public ClassStats StatsOf(string cls)
        {
            if (!Stats.TryGetValue(cls, out var s))
                Stats[cls] = s = new ClassStats();
            return s;
        }

        internal void Count(string cls, EvalResult r)
        {
            var s = StatsOf(cls);
            s.Evaluations++;
            if (!string.IsNullOrEmpty(r.NotSimulated))
            {
                s.NotSimulated.TryGetValue(r.NotSimulated, out long n);
                s.NotSimulated[r.NotSimulated] = n + 1;
            }
            else if (r.Halted)
            {
                s.Halted++;
                string why = r.HaltReason ?? "";
                if (why.Length > 80)
                    why = why.Substring(0, 80);
                s.HaltReasons.TryGetValue(why, out long hn);
                s.HaltReasons[why] = hn + 1;
                s.HaltCodes.TryGetValue(r.HaltCode, out long hc);
                s.HaltCodes[r.HaltCode] = hc + 1;
            }
            if (r.Error != 0)
            {
                s.Errors.TryGetValue(r.Error, out long e);
                s.Errors[r.Error] = e + 1;
            }
        }

        internal void Changed(PageRun run, List<ObjectState> changed) =>
            PageChanged?.Invoke(run.State, changed);

        /// <summary>A page expression that halted (any reason, the not-simulated stops too):
        /// the class, the AN and the halted task (its frames still show where). For tools (ctsim
        /// page-sweep / page-run name the place); no behaviour.</summary>
        public event Action<string, int, CicodeTask>? EvalHalted;

        internal void NoteEvalHalt(string cls, int an, CicodeTask t) => EvalHalted?.Invoke(cls, an, t);

        /// <summary>Where a halted task stopped: 'function line n' of its innermost frame with a
        /// function (else the code block's label), the frames outwards after ' &lt; '.</summary>
        public static string HaltPlace(CicodeTask t)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var f in DebugInspect.Frames(t))
            {
                if (sb.Length > 0)
                    sb.Append(" < ");
                sb.Append(f.Name.Length > 0 ? f.Name : "?");
                if (f.Line > 0)
                    sb.Append(" line ").Append(f.Line.ToString(CultureInfo.InvariantCulture));
                if (sb.Length > 200)
                    break;
            }
            return sb.ToString();
        }

        /// <summary>The built-in a halted task stopped at as not simulated, "" for another halt.</summary>
        public static string NotSimulatedName(CicodeTask t)
        {
            const string pre = "built-in function ",
                post = " is not simulated";
            string r = t.HaltReason;
            if (r.StartsWith(pre, StringComparison.Ordinal) && r.EndsWith(post, StringComparison.Ordinal))
                return r.Substring(pre.Length, r.Length - pre.Length - post.Length);
            return "";
        }

        // ================================================================== running code
        /// <summary>A foreground evaluation the debugger stopped: the host waits (serving the
        /// debugger) until the task is released, true, or gives up, false (the code halts).
        /// Null = no waiting: such a stop halts the code.</summary>
        public Func<CicodeTask, bool>? ForegroundStopped;

        /// <summary>The profiler's hooks (ctsim/profile): an expression's (window, AN, class,
        /// Stopwatch ticks), a scan's (window, page, ticks); null = not timed.</summary>
        public Action<int, int, string, long>? EvalTimed;
        public Action<int, string, long>? ScanTimed;

        /// <summary>Run a code block in the foreground, to its end.</summary>
        public CicodeTask RunForeground(byte[] code, CProgram program, int window, int an, string label)
        {
            var t = Vm.TaskForBlock(code, program, true, label);
            TaskContext.Set(t, window, an);
            RunToEnd(t);
            return t;
        }

        private void RunToEnd(CicodeTask t)
        {
            // the foreground slice's instruction count (the VM has one count for every task: on a
            // VM shared with a simulation it is the background's)
            var cfg = Vm.Config;
            int saved = cfg.MaxSliceOps;
            cfg.MaxSliceOps = ForegroundOps;
            try
            {
                RunSlices(t);
            }
            finally
            {
                cfg.MaxSliceOps = saved;
            }
        }

        private void RunSlices(CicodeTask t)
        {
            int n = 0;
            while (!t.Done && n < 64)
            {
                if (t.Pause && t.ParkForeground && t.State == TaskState.Wait)
                {
                    // the debugger stopped it (DebugOptions.ForegroundCanBreak, a simulator
                    // deviation): the host waits here, inside the scan, until it is released
                    var hold = ForegroundStopped;
                    if (hold == null || !hold(t))
                    {
                        Vm.Halt(t, "foreground code stopped by the debugger");
                        break;
                    }
                    Vm.Resume(t);
                    n = 0; // the slices stepped meanwhile do not count
                    continue;
                }
                if (t.State == TaskState.Wait || t.State == TaskState.Sleep)
                {
                    // foreground code that waits: the runtime refuses it (282); here a stuck wait
                    Vm.Halt(t, "foreground code cannot wait");
                    break;
                }
                try
                {
                    Vm.RunSlice(t);
                }
                catch (Exception e) when (!(e is OutOfMemoryException) && !(e is StackOverflowException))
                {
                    t.State = TaskState.Halt;
                    t.HaltReason = "engine exception: " + e.GetType().Name + ": " + e.Message;
                    LogLine("engine exception in " + t.Name + ": " + e);
                    break;
                }
                n++;
            }
            if (!t.Done)
                Vm.Halt(t, "foreground code did not end");
        }

        /// <summary>Start a command as a new task: page-bound ones go with the page
        /// (mode 0x11 / 1 | 4), input commands survive it (0x50 | 4).</summary>
        public CicodeTask? StartTask(
            byte[] code,
            CProgram program,
            int window,
            int an,
            string label,
            PageRun? pageBound
        )
        {
            CicodeTask t;
            try
            {
                t = Vm.TaskForBlock(code, program, false, label);
                TaskContext.Set(t, window, an);
                Scheduler.Add(t);
            }
            catch (HardwareError e)
            {
                LogLine(label + ": " + e.Message);
                return null;
            }
            pageBound?.Tasks.Add(t);
            Trace(
                window,
                pageBound?.Page.Name ?? "",
                "taskStart",
                label + " " + t.Handle.ToString(CultureInfo.InvariantCulture)
            );
            return t;
        }

        /// <summary>Run the handler of an OnEvent type (page events 3..10) to its
        /// end as foreground code; its INT result, 0 without a handler.</summary>
        public long RaiseEvent(int type, int window)
        {
            if (Host == null)
                return 0;
            long hnd = Host.EventHandler(type);
            if (hnd < 0)
                return 0;
            var fn = Events_ByHandle(hnd);
            if (fn == null)
                return 0;
            var t = Vm.TaskForCall(fn, Array.Empty<Value>(), true, null, fn.Name);
            TaskContext.Set(t, window, -1);
            RunToEnd(t);
            return t.State == TaskState.Stop && t.Result != null ? A.I(t.Result.Value) : 0;
        }

        private Function? Events_ByHandle(long hnd)
        {
            var fs = Vm.Library.Functions;
            if (hnd >= 0 && hnd < fs.Count && fs[(int)hnd].Index == hnd)
                return fs[(int)hnd];
            foreach (var f in fs)
                if (f.Index == hnd)
                    return f;
            return null;
        }

        /// <summary>A project function the runtime calls by name when it is defined and not
        /// PRIVATE (_PageStart / _PageStop / _PageBack, lookup).</summary>
        public bool CallProjectFunction(string name, int window)
        {
            var fn = Vm.Library.Function(name);
            if (fn == null || fn.Private)
                return false;
            var t = Vm.TaskForCall(fn, Array.Empty<Value>(), true, null, fn.Name);
            TaskContext.Set(t, window, -1);
            RunToEnd(t);
            return true;
        }

        // ================================================================== time
        /// <summary>Simulated seconds until the first waiting task can wake, null when none
        /// waits on the clock (as WorldRun's).</summary>
        private double? NextTaskWake()
        {
            double? best = null;
            foreach (var t in Scheduler.All)
            {
                if (t.Done || (t.State != TaskState.Wait && t.State != TaskState.Sleep))
                    continue;
                var w = t.Wait;
                double? dt = null;
                if (w == null)
                    dt = 0.001;
                else
                {
                    double d = 0.0;
                    bool timed = false;
                    if (w.UntilMs.HasValue)
                    {
                        d = Math.Max(d, (w.UntilMs.Value - World.Clock.MonotonicMs()) / 1000.0);
                        timed = true;
                    }
                    if (w.UntilSec.HasValue)
                    {
                        d = Math.Max(d, w.UntilSec.Value - Clocks.UnixSeconds(World.Clock));
                        timed = true;
                    }
                    if (!timed && w.Poll != null)
                    {
                        d = World.Clock.StepSize;
                        timed = true;
                    }
                    if (timed)
                        dt = Math.Max(d, 0.001);
                }
                if (dt != null && (best == null || dt.Value < best.Value))
                    best = dt;
            }
            return best;
        }

        /// <summary>Run the ready tasks (at most <paramref name="maxSlices"/> slices: a busy
        /// background loop cannot hold simulated time).</summary>
        public long RunTasks(long maxSlices = 20000)
        {
            long n = 0;
            while (n < maxSlices)
            {
                bool stepped;
                try
                {
                    stepped = Scheduler.Step();
                }
                catch (Exception e) when (!(e is OutOfMemoryException) && !(e is StackOverflowException))
                {
                    LogLine("engine exception in a task: " + e);
                    break;
                }
                if (!stepped)
                    break;
                n++;
            }
            return n;
        }

        /// <summary>Advance simulated time by <paramref name="seconds"/>: page scans, flash,
        /// symbol animation and touch repeat timers fire in time order, tasks run in between.</summary>
        public void RunFor(double seconds)
        {
            var clock = World.Clock;
            double end = clock.Now() + Math.Max(0.0, seconds);
            int guard = 0;
            double slice = Math.Max(1, Vm.Config.TimeSlice) / 1000.0;
            while (guard++ < 10_000_000)
            {
                RunTasks(SlicesPerTimeSlice);
                double? due = clock.NextDue();
                double? wake = NextTaskWake();
                double? next = due;
                if (wake != null)
                {
                    double tw = clock.Now() + wake.Value;
                    if (next == null || tw < next.Value)
                        next = tw;
                }
                // a task still ready (it kept using its slices) costs a time slice
                if (Scheduler.QueueLength > 0)
                {
                    double tb = clock.Now() + slice;
                    if (next == null || tb < next.Value)
                        next = tb;
                }
                if (next == null || next.Value > end)
                    break;
                clock.AdvanceTo(Math.Max(next.Value, clock.Now()));
            }
            if (clock.Now() < end)
                clock.AdvanceTo(end);
            RunTasks(SlicesPerTimeSlice);
        }

        /// <summary>
        /// Background slices per [Code]TimeSlice of simulated time while a task stays ready.
        /// </summary>
        public int SlicesPerTimeSlice = 5;

        /// <summary>Run until window <paramref name="window"/> has scanned <paramref name="scans"/>
        /// more times (at most 1000 simulated seconds).</summary>
        public void RunScans(int scans, int window = 0)
        {
            var run = Run(window);
            if (run == null || scans <= 0)
                return;
            long target = run.Scans + scans;
            double limit = World.Clock.Now() + 1000.0;
            while (run.Scans < target && !run.Closed && World.Clock.Now() < limit)
            {
                double step = Math.Max(0.001, (run.NextDue - World.Clock.Now()));
                RunFor(step);
            }
        }

        // ================================================================== windows and pages
        public PageRun? Run(int window) => Display.OpenWindow(window)?.Page?.Runtime as PageRun;

        public PageState? State(int window) => Run(window)?.State;

        /// <summary>The page's compiled file: the catalogue's .RDB of the run project.</summary>
        private CompiledPage? Compile(string name, PageRecord rec, out int error)
        {
            error = 0;
            string path = rec.RdbPath.Length > 0 ? rec.RdbPath : Display.Catalogue?.RdbPath(name) ?? "";
            if (path.Length == 0 || !File.Exists(path))
            {
                error = 261;
                return null;
            }
            try
            {
                return CompiledPage.Load(path);
            }
            catch (Exception e) when (e is RdbError || e is IOException)
            {
                LogLine("page " + name + ": " + e.Message);
                error = 393;
                return null;
            }
        }

        /// <summary>Display a page in a window.</summary>
        public int Open(
            string page,
            int window = 0,
            PageModel? model = null,
            bool pushHistory = true,
            CompiledPage? compiled = null
        )
        {
            page = CitectText.Trim(page ?? "");
            if (page.Length == 0)
                return 274;
            var w = Display.Get(window);
            if (w == null)
                return 274;
            if (w.InAnimation || w.InComms)
                return 517;
            if (_nesting > Display.Config.MaxRecursion)
                return 318;
            _nesting++;
            try
            {
                PageRecord? rec = Display.Host?.FindPage(page, "") ?? Display.Catalogue?.Record(page);
                CompiledPage? cp = compiled ?? CompiledSource?.Invoke(page);
                int err = 0;
                if (cp == null)
                {
                    if (rec == null)
                    {
                        string p = Display.Catalogue?.RdbPath(page) ?? "";
                        rec =
                            p.Length > 0 && File.Exists(p)
                                ? new PageRecord { Name = page, RdbPath = p }
                                : null;
                    }
                    if (rec == null)
                        return File.Exists(Display.Catalogue?.RdbPath(page) ?? "") ? 265 : 261;
                    cp = Compile(page, rec, out err);
                    if (cp == null)
                        return err;
                    if (cp.Control != null)
                    {
                        cp.Control.Comment = rec.Comment;
                        cp.Control.Parent = rec.Parent;
                        rec = cp.Control;
                    }
                }
                else
                    rec = cp.Control ?? rec ?? new PageRecord { Name = cp.Name };
                if (cp.Control == null && compiled == null)
                    return 265;
                if (!Display.AreaOk(rec!.Areas))
                    return 277;
                // 2. close the old page
                if (w.Page != null)
                    Close(window, freeChildren: true);
                w.Open = true;
                // 3..5 the window record
                var inst = new PageInstance(rec) { Name = rec.Name.Length > 0 ? rec.Name : page };
                var drawing = model?.Drawing ?? DrawingSource?.Invoke(rec) ?? Display.Host?.LoadDrawing(rec);
                inst.Take(drawing);
                CicodeSim.World.Display.CicodeExtents.StaticTexts(Display, inst);
                w.Page = inst;
                w.ScanTime = rec.ScanTime > 0 ? Math.Min(rec.ScanTime, 60000) : Display.Config.ScanTime;
                w.IgnoreValueQuality = rec.IgnoreVq >= 0 ? rec.IgnoreVq : Display.Config.IgnoreValueQuality;
                w.Shows = 0;
                w.ClearVars();
                w.KeyBuffer.Clear();
                w.CommandCursor = -1;
                w.Cluster = rec.Cluster;
                w.ResetForPage(); // what Cicode drew on the old page goes
                // 7. associations: the window's pending ones, then the page's declared defaults
                Display.ClaimForPage(w, inst.Associations);
                Tags.Serial++;
                // 8. the object data
                var run = new PageRun(this, w, inst, cp);
                inst.Runtime = run;
                foreach (var o in run.Objects)
                {
                    var ao = inst.Ensure(o.An);
                    if (ao.Type == 0)
                        ao.Type = o.Type;
                    if (o.Comment.Length > 0 && ao.Tip.Length == 0)
                        ao.Tip = o.Comment;
                }
                foreach (var k in run.Keys)
                    if (k.An > 0)
                        inst.Ensure(k.An).HasKeyCommand = true;
                foreach (var t in run.Touches.Keys)
                    inst.Ensure(t).HasTouch = true;
                CountClasses(run);
                // 10. history
                if (pushHistory)
                {
                    if (w.HistoryPos < w.History.Count - 1)
                        w.History.RemoveRange(w.HistoryPos + 1, w.History.Count - w.HistoryPos - 1);
                    w.History.Add(inst.Name);
                    while (w.History.Count > Math.Max(Display.Config.MaxList, Display.Config.MaxLast))
                        w.History.RemoveAt(0);
                    w.HistoryPos = w.History.Count - 1;
                }
                Display.Host?.Shown(w.Number, inst);
                Display.Host?.WindowChanged(w);
                Trace(w.Number, inst.Name, "open");
                var program = cp.Program;
                void Start()
                {
                    if (run.Closed || w.Page != inst)
                        return;
                    // 11. _PageStart, OnEvent 8 "Page open", OnEvent 4 "Page user open"
                    CallProjectFunction("_PageStart", w.Number);
                    RaiseEvent(8, w.Number);
                    RaiseEvent(4, w.Number);
                    // 12. ENTRY_FUNC as a new page task (mode 0x11: page-bound, no wait for data)
                    Trace(w.Number, inst.Name, "entry");
                    if (run.EntryCode != null)
                        run.EntryTask = StartTask(run.EntryCode, program, w.Number, -1, "page.entry", run);
                    // 14. the ReadyCicodeDelay countdown, 13. the first scan now (every
                    // subscription has data in the simulator: the subscription hook runs it inside the
                    // open, before the entry task ran a statement: guess; verify)
                    w.ReadyCountdown = Display.Config.ReadyCicodeDelay;
                    ScheduleScan(run, now: true);
                }
                // a page Cicode displays without a drawing summary: a viewer that supplies
                // drawings may be asked for it first; the page then starts (its open events, entry
                // code, scans) when the drawing comes (Release) or the host lets it go
                // (simulator only: the runtime reads its drawing file)
                if (drawing == null && _cicodeOpens > 0 && WantDrawing != null && WantDrawing(w.Number, inst))
                {
                    run.PendingStart = Start;
                    return 0;
                }
                Start();
                return 0;
            }
            finally
            {
                _nesting--;
            }
        }

        private int _nesting;

        /// <summary>Opens the display built-ins asked for (PageDisplay, WinNew, AssPopUp, the
        /// history) under way: only those may wait for a drawing.</summary>
        private int _cicodeOpens;

        /// <summary>Asked when Cicode displays a page that has no drawing summary: true = hold its
        /// start until <see cref="Release"/> (the host asks a viewer for the drawing and calls
        /// Release when it came or after a while).</summary>
        public Func<int, PageInstance, bool>? WantDrawing;

        /// <summary>Start the held page of a window (its open events, entry code, first scan):
        /// false when the window shows no held page (<paramref name="page"/> = the instance the
        /// hold was for, null any).</summary>
        public bool Release(int window, PageInstance? page = null)
        {
            var run = Run(window);
            if (run == null || run.PendingStart == null || run.Closed)
                return false;
            if (page != null && !ReferenceEquals(run.Page, page))
                return false;
            var s = run.PendingStart;
            run.PendingStart = null;
            s();
            Released?.Invoke(window);
            return true;
        }

        /// <summary>A held page started (<see cref="Release"/>): its window. A waiting 'startup
        /// auto' runs once window 0's page has started (RuntimeStartup), not while it waits for
        /// its drawing.</summary>
        public event Action<int>? Released;

        private void CountClasses(PageRun run)
        {
            foreach (var o in run.Objects)
            {
                if (o.Vis != null)
                    StatsOf("visibility").Objects++;
                if (o.Dis != null)
                    StatsOf("disable").Objects++;
                if (o.Mvx != null || o.Mvy != null)
                    StatsOf("movement").Objects++;
                if (o.Szx != null || o.Szy != null)
                    StatsOf("scaling").Objects++;
                if (o.Rot != null)
                    StatsOf("rotation").Objects++;
                if (o.SliderX || o.SliderY || o.SliderRot)
                    StatsOf("slider").Objects++;
                if (o.HasColour)
                    StatsOf(
                        o.ColMode switch
                        {
                            0 => "fill_colour_onoff",
                            1 => "fill_colour_multi",
                            2 => "fill_colour_array",
                            3 => "fill_colour_threshold",
                            _ => "fill_colour_gradient",
                        }
                    ).Objects++;
                if (o.Fil != null)
                    StatsOf("fill_level").Objects++;
                if (o.HasText)
                    StatsOf(
                        o.TxtMode switch
                        {
                            0 => "display_onoff",
                            1 => "display_multi",
                            2 => "display_array",
                            3 => "display_numeric",
                            _ => "display_string",
                        }
                    ).Objects++;
                if (o.HasSymbolSet)
                    StatsOf(
                        o.TxtMode switch
                        {
                            0 => "symbolset_onoff",
                            1 => "symbolset_multi",
                            2 => "symbolset_array",
                            _ => "symbolset_animated",
                        }
                    ).Objects++;
                if (o.Vertices.Count > 0)
                    StatsOf("vertex").Objects++;
                if (o.Indicator != null)
                    StatsOf("alarm_indicator").Objects++;
                if (o.Legacy != null)
                    StatsOf(LegacyClass(o.Legacy.Table)).Objects++;
                if (o.Table == "PGBUTTON")
                    StatsOf("button").Objects++;
            }
            StatsOf("cicode_object").Objects += run.CicodeObjects.Count;
            StatsOf("trend").Objects += run.Trends.Count;
            foreach (var l in run.Touches.Values)
                StatsOf("touch").Objects += l.Count;
            StatsOf("keyboard").Objects += run.Keys.Count;
        }

        /// <summary>The class name of a V3 / V4 table.</summary>
        internal static string LegacyClass(string table) =>
            table switch
            {
                "PGNUM" => "legacy_number",
                "PGSTR" => "legacy_string",
                "PGANSYM" => "legacy_animated_symbol",
                "PGSYM" => "legacy_symbol",
                _ => "legacy_text",
            };

        private void ScheduleScan(PageRun run, bool now = false)
        {
            var clock = World.Clock;
            if (now)
            {
                run.NextDue = clock.Now();
                DoScan(run);
                return;
            }
        }

        private void DoScan(PageRun run)
        {
            if (run.Closed)
                return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                run.Scan();
                long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                run.ScanTicks += dt;
                run.TimedScans++;
                if (dt > run.MaxScanTicks)
                    run.MaxScanTicks = dt;
                if (run.ScanCosts.Count < 4096)
                    run.ScanCosts.Add(dt);
            }
            catch (Exception e) when (!(e is OutOfMemoryException) && !(e is StackOverflowException))
            {
                LogLine("engine exception in the scan of " + run.Page.Name + ": " + e);
                StatsOf("engine").Halted++;
            }
            if (run.Closed)
                return;
            // next due = previous due + scan time; in the past -> now + scan time (head)
            var clock = World.Clock;
            double period = Math.Max(1, run.Window.ScanTime) / 1000.0;
            double next = run.NextDue + period;
            if (next <= clock.Now())
                next = clock.Now() + period;
            run.NextDue = next;
            run.ScanTimer = clock.CallAt(
                next,
                () => DoScan(run),
                "scan w" + run.Window.Number.ToString(CultureInfo.InvariantCulture)
            );
        }

        /// <summary>Close the page of a window: _PageStop,
        /// OnEvent 5 then 9, EXIT_FUNC synchronously in the foreground, page-child windows freed,
        /// the entry / ready and page-bound tasks killed.</summary>
        public int Close(int window, bool freeChildren = true)
        {
            var w = Display.Get(window);
            if (w?.Page == null)
                return 0;
            var run = w.Page.Runtime as PageRun;
            string name = w.Page.Name;
            // a page still waiting for its drawing starts first: the runtime always ran a page's
            // open events and entry code before its close (no _PageStop without _PageStart)
            if (run != null && run.PendingStart != null && !run.Closed)
            {
                var start = run.PendingStart;
                run.PendingStart = null;
                start();
            }
            CallProjectFunction("_PageStop", window);
            RaiseEvent(5, window);
            RaiseEvent(9, window);
            if (run?.ExitCode != null)
            {
                Trace(window, name, "exit");
                RunForeground(run.ExitCode, run.Compiled.Program, window, -1, "page.exit");
            }
            else
                Trace(window, name, "exit");
            if (freeChildren)
                foreach (var c in Display.Windows)
                    if (c.Open && c.Parent == window && (c.Mode & 1) != 0 && c.Number != window)
                        FreeWindow(c.Number);
            if (run != null)
            {
                run.Closed = true;
                run.ScanTimer?.Cancel();
                foreach (var t in run.Tasks)
                    if (!t.Done)
                        Vm.Halt(t, "page closed");
                if (_repeat != null && _repeat.Run == run)
                    StopRepeat();
            }
            Display.OnPageClosing(w, w.Page); // Array*ByAn arrays freed
            // the page's trend objects and alarm lists go with it
            PageObjectTables.Of(Display).PageClosed(window, name);
            w.ResetForPage();
            w.Page = null;
            w.Associations.Clear();
            Tags.Serial++;
            Display.Host?.WindowChanged(w);
            return 0;
        }

        /// <summary>Open a page in a new window (WinNew / WinNewAt core): the
        /// lowest free window, -1 when none is free or the page cannot be shown.</summary>
        public int NewWindow(
            string page,
            int x = 0,
            int y = 0,
            int mode = 0,
            int parent = -1,
            PageModel? model = null
        )
        {
            if (Display.OpenCount() >= Display.Windows.Length)
                return -1;
            int n = Display.FreeWindow();
            if (n < 0)
                return -1;
            var w = Display.Windows[n];
            w.Open = true;
            Display.Opened(w);
            w.X = x;
            w.Y = y;
            w.Mode = mode & 0x7fffffff;
            w.Parent = parent;
            w.History.Clear();
            w.HistoryPos = -1;
            int err = Open(page, n, model);
            if (err != 0)
            {
                w.Open = false;
                w.Parent = -1;
                return -1;
            }
            return n;
        }

        /// <summary>Free a window: its page closes, the window and its window children go.</summary>
        public int FreeWindow(int window)
        {
            var w = Display.Get(window);
            if (w == null || !w.Open)
                return 256;
            foreach (var c in Display.Windows)
                if (c.Open && c.Parent == window && c.Number != window)
                    FreeWindow(c.Number);
            Close(window, freeChildren: false);
            w.Open = window == 0 && Display.OpenCount() == 1;
            w.Parent = -1;
            w.Mode = 0;
            w.History.Clear();
            w.HistoryPos = -1;
            Display.Host?.WindowChanged(w);
            return 0;
        }

        /// <summary>The page history: back (-n) or forward (+n) without dropping entries
        /// (PageBack / PageForward, doc: 1188739); 274 outside the history.</summary>
        public int History(int window, int delta)
        {
            var w = Display.OpenWindow(window);
            if (w == null)
                return 269;
            int pos = w.HistoryPos + delta;
            if (pos < 0 || pos >= w.History.Count)
                return 274;
            int err = Open(w.History[pos], window, pushHistory: false);
            if (err == 0)
                w.HistoryPos = pos;
            return err;
        }

        /// <summary>Window activation: ACT_FUNC on activation, DEACT_FUNC
        /// on deactivation, as page tasks.</summary>
        public void Activate(int window, bool active)
        {
            var w = Display.OpenWindow(window);
            var run = Run(window);
            if (w == null || run == null || w.Active == active)
                return;
            w.Active = active;
            Trace(window, run.Page.Name, active ? "activate" : "deactivate");
            byte[]? code = active ? run.ActivateCode : run.DeactivateCode;
            if (code != null)
                StartTask(
                    code,
                    run.Compiled.Program,
                    window,
                    -1,
                    active ? "page.activate" : "page.deactivate",
                    run
                );
        }

        // ================================================================== inputs
        /// <summary>The pointer moved: the window's mouse (DspGetMouseOver, tool tips) and the
        /// command cursor = the innermost object under it the cursor may stop on (a keyboard or
        /// touch command, KeyAllowCursor: AnObject.CursorAllowed) (doc: 1106241).</summary>
        public void Mouse(int window, int x, int y)
        {
            var w = Display.OpenWindow(window);
            if (w == null)
                return;
            w.MouseX = x;
            w.MouseY = y;
            if (w.Page == null)
                return;
            int best = -1;
            long area = long.MaxValue;
            foreach (var o in w.Page.Objects.Values)
            {
                if (!o.CursorAllowed(w.Page) || o.Drawing?.Rect == null)
                    continue;
                var r = o.Drawing.Rect.Value;
                if (!r.ContainsInclusive(x, y))
                    continue;
                long a = (long)(r.Width + 1) * (r.Height + 1);
                if (a < area)
                {
                    area = a;
                    best = o.An;
                }
            }
            if (best > 0)
                w.CommandCursor = best;
        }

        public enum TouchKind
        {
            Down,
            Up,
            DoubleClick,
        }

        /// <summary>Does the object take input (shown, enabled, user has access)?</summary>
        private bool TakesInput(PageRun run, int an)
        {
            var o = run.Object(an);
            if (o == null)
            {
                var seen = new HashSet<int>();
                for (
                    var d = run.Page.Get(an);
                    d != null && d.Parent > 0 && seen.Add(d.An);
                    d = run.Page.Get(d.Parent)
                )
                    if ((o = run.Object(d.Parent)) != null)
                        break;
                if (o == null)
                    return true;
            }
            for (var x = o; x != null; x = x.Parent)
                if (!x.Shown || x.Disabled || !x.AreaOk || !x.PrivOk)
                    return false;
            return true;
        }

        /// <summary>A left mouse button on an object.</summary>
        public bool Touch(int window, int an, TouchKind kind)
        {
            var run = Run(window);
            if (run == null)
                return false;
            if (kind == TouchKind.Up && _repeat != null)
                StopRepeat();
            if (!TakesInput(run, an))
                return false;
            bool ran = false;
            // the pointer is on the object: the command cursor is there before its commands run
            // (the runtime's mouse move over it put it there), so AssMetadata(-2)
            // and KeyGetCursor() in a touch command name the clicked object
            if (run.Touches.ContainsKey(an))
                run.Window.CommandCursor = an;
            if (run.Touches.TryGetValue(an, out var list))
                foreach (var t in list)
                {
                    if (!t.TakesLeftButton)
                        continue;
                    bool did = false;
                    switch (kind)
                    {
                        case TouchKind.Down:
                            if (t.Repeat != null)
                                StartRepeat(run, t);
                            if (t.Down != null)
                                did = Command(run, t.Down, an, "touch.down", t.LogDown, t.Program);
                            break;
                        case TouchKind.Up:
                            if (t.Up != null)
                                did = Command(run, t.Up, an, "touch.up", t.LogUp, t.Program);
                            break;
                        case TouchKind.DoubleClick:
                            if (t.Dbl != null)
                                did = Command(run, t.Dbl, an, "touch.dblclick", t.LogDbl, t.Program);
                            break;
                    }
                    if (did)
                    {
                        ran = true;
                        break;
                    }
                }
            // the mouse keys of the command cursor (KEY_LBTN_CMD_DN / _UP, labels.dbf), then the
            // plain mouse key (KEY_LBUTTON_DN / _UP / _DBL) for a sequence named with that
            var w = run.Window;
            int plain =
                kind == TouchKind.Down ? KeyTable.LButtonDown
                : kind == TouchKind.Up ? KeyTable.LButtonUp
                : KeyTable.LButtonDbl;
            if (kind != TouchKind.DoubleClick && run.Keys.Exists(k => k.An == an))
            {
                w.CommandCursor = an;
                ran |= MouseKeys(
                    window,
                    kind == TouchKind.Down ? KeyTable.LButtonCmdDown : KeyTable.LButtonCmdUp,
                    plain
                );
            }
            else if (!ran)
                ran |= PageMouseKey(window, plain);
            return ran;
        }

        /// <summary>A mouse key on the command cursor's object.</summary>
        public bool MouseKeys(int window, int cursorCode, int plainCode)
        {
            bool ran = cursorCode > 0 && KeyCode(window, cursorCode);
            var w = Display.OpenWindow(window);
            if (!ran && plainCode > 0 && (w == null || w.KeyBuffer.Count == 0))
                ran = KeyCode(window, plainCode);
            return ran;
        }

        /// <summary>A mouse key where no object with keyboard commands is: the page's and the
        /// project's (system) keyboard commands only (a page RBUTTON_UP), not the commands of an
        /// object the cursor was left on.</summary>
        public bool PageMouseKey(int window, int plainCode)
        {
            var w = Display.OpenWindow(window);
            if (w == null || plainCode <= 0)
                return false;
            int cur = w.CommandCursor;
            w.CommandCursor = 0;
            try
            {
                return KeyCode(window, plainCode);
            }
            finally
            {
                if (w.CommandCursor == 0)
                    w.CommandCursor = cur;
            }
        }

        /// <summary>A button Cicode drew on the AN (DspButton / DspButtonFn: the window's drawn
        /// output of kind 'button'), else null.</summary>
        public static DrawCommand? DrawnButton(PageRun run, int an)
        {
            var c = an > 0 ? run.Window.DrawnAt(an) : null;
            return c != null && c.Kind == "button" ? c : null;
        }

        /// <summary>A left mouse button on a button Cicode drew.</summary>
        public bool PressDrawnButton(int window, int an, TouchKind kind)
        {
            var run = Run(window);
            if (run == null)
                return false;
            var b = DrawnButton(run, an);
            if (b == null || !TakesInput(run, an))
                return false;
            run.Window.CommandCursor = an;
            bool ran = false;
            if (kind != TouchKind.Up)
                ran |= DrawnButtonPart(run, b, true);
            if (kind != TouchKind.Down)
                ran |= DrawnButtonPart(run, b, false);
            return ran;
        }

        private bool DrawnButtonPart(PageRun run, DrawCommand b, bool down)
        {
            JsonValue? fn = null,
                key = null;
            foreach (var kv in b.Args)
            {
                if (kv.Key == (down ? "downFunction" : "upFunction"))
                    fn = kv.Value;
                if (kv.Key == (down ? "downKey" : "upKey"))
                    key = kv.Value;
            }
            if (fn != null)
            {
                Function? f = null;
                if (fn.Kind == JsonKind.String && fn.Str.Trim().Length > 0)
                    f = Vm.Library.Function(fn.Str.Trim());
                else if (fn.IsNumber && fn.Long > 0)
                    f = Events_ByHandle(fn.Long);
                if (f == null)
                    return false;
                StatsOf("touch").Evaluations++;
                try
                {
                    var t = Vm.TaskForCall(f, Array.Empty<Value>(), false, null, f.Name);
                    TaskContext.Set(t, run.Window.Number, b.An);
                    Scheduler.Add(t);
                    Trace(
                        run.Window.Number,
                        run.Page.Name,
                        "taskStart",
                        (down ? "button.down " : "button.up ")
                            + t.Handle.ToString(CultureInfo.InvariantCulture)
                    );
                    return true;
                }
                catch (HardwareError e)
                {
                    LogLine("button " + f.Name + ": " + e.Message);
                    return false;
                }
            }
            if (key != null && key.IsNumber && key.Long != 0)
                return KeyCode(run.Window.Number, (int)key.Long);
            return false;
        }

        private bool Command(
            PageRun run,
            byte[] code,
            int an,
            string label,
            string log,
            CProgram? program = null
        )
        {
            if (log.Length > 0)
                LogLine("MSG_LOG AN " + an.ToString(CultureInfo.InvariantCulture) + ": " + log);
            StatsOf(label.StartsWith("key", StringComparison.Ordinal) ? "keyboard" : "touch").Evaluations++;
            return StartTask(code, program ?? run.Compiled.Program, run.Window.Number, an, label, null)
                != null;
        }

        /// <summary>Start the repeat timer of a press.</summary>
        private void StartRepeat(PageRun run, TouchCommand t)
        {
            var (period, _) = Formulas.RepeatSchedule(t.RptRate);
            int limit = 1000 / period;
            int count = 0;
            if (_repeat != null)
            {
                if (_repeat.Run == run && _repeat.An == t.An)
                    return;
                count = _repeat.Count < _repeat.Limit ? 0 : limit;
                _repeat.Timer?.Cancel();
            }
            var rt = new RepeatTimer
            {
                Run = run,
                An = t.An,
                Limit = limit,
                Count = count,
            };
            rt.Timer = World.Clock.Every(period / 1000.0, () => RepeatTick(rt), label: "repeat");
            _repeat = rt;
        }

        private void RepeatTick(RepeatTimer rt)
        {
            if (rt != _repeat)
                return;
            // count up to the limit, then every tick sends the repeat event
            // (the key code | 0x8000) to the pressed object
            if (rt.Count < rt.Limit)
            {
                rt.Count++;
                return;
            }
            if (rt.Run.Closed)
            {
                StopRepeat();
                return;
            }
            // a hidden or disabled object takes no input: the repeat waits (doc: 1172179, 1172175)
            if (!TakesInput(rt.Run, rt.An) || !rt.Run.Touches.TryGetValue(rt.An, out var list))
                return;
            foreach (var t in list)
                if (t.TakesLeftButton && t.Repeat != null)
                {
                    Command(rt.Run, t.Repeat, rt.An, "touch.repeat", t.LogRepeat, t.Program);
                    break;
                }
        }

        private void StopRepeat()
        {
            _repeat?.Timer?.Cancel();
            _repeat = null;
        }

        /// <summary>A key typed into a window (a keys.dbf name, one character or a code).</summary>
        public bool Key(int window, string key)
        {
            int code = Keys.Parse(key);
            return code >= 0 && KeyCode(window, code);
        }

        /// <summary>Append a key code to the window's key command line and run any complete match.</summary>
        public bool KeyCode(int window, int code)
        {
            var run = Run(window);
            var w = Display.OpenWindow(window);
            if (run == null || w == null || w.KeyBuffer.Count >= CicodeSim.World.Display.Window.KeyLineMax)
                return false;
            w.KeyBuffer.Add(code);
            int sep = Display.Config.KeySeparator;
            var scope = new List<KeyCommand>();
            int cur = w.CommandCursor;
            var chain = new List<int>();
            var start = cur > 0 ? run.Object(cur) : null;
            if (cur > 0 && start == null)
            {
                // the cursor on an object without animation of its own (a touch command's text):
                // it and its drawing parents up to the nearest animated group, whose chain follows
                // (the group's keyboard commands still answer, doc 1106241: two cursors)
                chain.Add(cur);
                var seen = new HashSet<int> { cur };
                for (
                    var d = run.Page.Get(cur);
                    d != null && d.Parent > 0 && seen.Add(d.Parent);
                    d = run.Page.Get(d.Parent)
                )
                {
                    if ((start = run.Object(d.Parent)) != null)
                        break;
                    chain.Add(d.Parent);
                }
            }
            for (var o = start; o != null; o = o.Parent)
                chain.Add(o.An);
            // the page's commands, then those KeySetSeq added while this page is shown
            var all = new List<KeyCommand>(run.Keys);
            w.RuntimeKeys.RemoveAll(r => r.Page != w.Page);
            foreach (var r in w.RuntimeKeys)
                all.Add(
                    new KeyCommand
                    {
                        An = r.An,
                        Sequence = r.Sequence,
                        Codes = Keys.Sequence(r.Sequence),
                        Function = r.Function,
                    }
                );
            foreach (int an in chain)
            foreach (var k in all)
                if (k.An == an)
                    scope.Add(k);
            foreach (var k in all)
                if (k.An == 0)
                    scope.Add(k);
            bool prefix = false;
            foreach (var k in scope)
            {
                var m = KeyMatch.Match(k.Codes, w.KeyBuffer, sep, out string[] args);
                if (m == KeyMatch.Result.Complete)
                {
                    w.LastKeyLine.Clear();
                    w.LastKeyLine.AddRange(w.KeyBuffer); // KeyReplay
                    w.KeyBuffer.Clear();
                    if (k.Area != 0 && !Display.AreaOk(k.Area) || !Display.PrivOk(k.Priv, k.Area))
                        return false;
                    if (k.An > 0 && !TakesInput(run, k.An))
                        return false;
                    for (int i = 0; i < 8 && i < Vm.GlbStr.Length; i++)
                        Vm.GlbStr[i] = args[i];
                    if (k.Function >= 0)
                    {
                        var fn = Events_ByHandle(k.Function);
                        if (fn == null)
                            return false;
                        StatsOf("keyboard").Evaluations++;
                        var ft = Vm.TaskForCall(fn, Array.Empty<Value>(), false, null, "key " + k.Sequence);
                        TaskContext.Set(ft, window, k.An > 0 ? k.An : cur);
                        Scheduler.Add(ft);
                        return true;
                    }
                    if (k.Cmd == null)
                        return false;
                    if (k.MsgLog.Length > 0)
                        LogLine("MSG_LOG key " + k.Sequence + ": " + k.MsgLog);
                    StatsOf("keyboard").Evaluations++;
                    return StartTask(
                            k.Cmd,
                            k.Program ?? run.Compiled.Program,
                            window,
                            k.An > 0 ? k.An : cur,
                            "key " + k.Sequence,
                            null
                        ) != null;
                }
                if (m == KeyMatch.Result.Prefix)
                    prefix = true;
            }
            if (!prefix)
                w.KeyBuffer.Clear();
            return false;
        }

        /// <summary>A slider drag.</summary>
        public bool Drag(int window, int an, double? dx, double? dy, double? angle, bool release)
        {
            var run = Run(window);
            if (run == null)
                return false;
            var o = run.Object(an);
            if (o == null || !TakesInput(run, an))
                return false;
            bool any = false;
            void Write((byte[]? Read, byte[]? Write) sl, int mode, double pct, string label)
            {
                bool continuous = (mode & 0x1000) != 0;
                if (sl.Write == null || (!continuous && !release))
                    return;
                Vm.GlbStr[0] = Crt.Sprintf("%e", PrintfArg.Float(pct));
                StatsOf("slider").Evaluations++;
                RunForeground(sl.Write, o.Program ?? run.Compiled.Program, window, an, label);
                any = true;
            }
            var s = o.State;
            double magX = o.Drawing?.MagnitudeX ?? 1.0,
                magY = o.Drawing?.MagnitudeY ?? 1.0;
            if (dx != null && o.SliderX)
            {
                double off = dx.Value / (magX == 0 ? 1 : magX);
                double pct = Formulas.SliderPercent(off, -o.MvxMin, o.MvxMax);
                o.SliderActiveX = !release;
                double shown = Formulas.MoveX(pct, o.MvxMin, o.MvxMax);
                s.Move = new[] { shown * magX, s.Move != null ? s.Move[1] : 0.0 };
                Write(o.MvxSl, o.MvxSlMode, pct, "slider.x");
            }
            if (dy != null && o.SliderY)
            {
                double off = dy.Value / (magY == 0 ? 1 : magY);
                double pct = Formulas.SliderPercent(off, o.MvyMin, -o.MvyMax);
                o.SliderActiveY = !release;
                double shown = Formulas.MoveY(pct, o.MvyMin, o.MvyMax);
                s.Move = new[] { s.Move != null ? s.Move[0] : 0.0, shown * magY };
                Write(o.MvySl, o.MvySlMode, pct, "slider.y");
            }
            if (angle != null && o.SliderRot)
            {
                double pct = Formulas.SliderPercent(angle.Value, -o.RotMin, o.RotMax);
                o.SliderActiveRot = !release;
                s.Rotate = Formulas.RotateDegrees(pct, o.RotMin, o.RotMax);
                Write(o.RotSl, o.RotSlMode, pct, "slider.rotate");
            }
            if (release)
            {
                o.SliderActiveX = o.SliderActiveY = o.SliderActiveRot = false;
            }
            run.Emit();
            return any;
        }

        // ------------------------------------------------------------------ IPageController
        int IPageController.Open(string page, int window, bool pushHistory)
        {
            _cicodeOpens++;
            try
            {
                return Open(page, window, null, pushHistory);
            }
            finally
            {
                _cicodeOpens--;
            }
        }

        int IPageController.NewWindow(string page, int x, int y, int mode, int parent)
        {
            _cicodeOpens++;
            try
            {
                return NewWindow(page, x, y, mode, parent);
            }
            finally
            {
                _cicodeOpens--;
            }
        }

        int IPageController.FreeWindow(int window) => FreeWindow(window);

        int IPageController.History(int window, int delta)
        {
            _cicodeOpens++;
            try
            {
                return History(window, delta);
            }
            finally
            {
                _cicodeOpens--;
            }
        }

        // ------------------------------------------------------------------ IPageRuntime
        bool IPageRuntime.KeyPress(int window, int code) => KeyCode(window, code);

        void IPageRuntime.AssociationsChanged(int window)
        {
            Tags.Serial++;
            // the host's windowsChanged carries the associations (a viewer re-asks its tags)
            var w = Display.OpenWindow(window);
            if (w != null)
                Display.Host?.WindowChanged(w);
        }

        AnMotion? IPageRuntime.Motion(int window, int an)
        {
            var o = Run(window)?.Object(an);
            if (o == null)
                return null;
            var s = o.State;
            var m = new AnMotion
            {
                Sx = 1.0,
                Sy = 1.0,
                Hidden = !o.Shown,
                Priv = o.Priv,
                Area = o.Area,
            };
            if (s.Move != null)
            {
                m.Dx = s.Move[0];
                m.Dy = s.Move.Length > 1 ? s.Move[1] : 0.0;
            }
            if (s.Scale != null)
            {
                m.Sx = s.Scale[0];
                m.Sy = s.Scale.Length > 1 ? s.Scale[1] : 1.0;
            }
            if (s.ScaleOrigin != null)
            {
                m.Ox = s.ScaleOrigin[0];
                m.Oy = s.ScaleOrigin.Length > 1 ? s.ScaleOrigin[1] : 0.0;
            }
            return m;
        }

        List<AnExpression>? IPageRuntime.Expressions(int window, int an)
        {
            var run = Run(window);
            var o = run?.Object(an);
            if (run == null || o == null)
                return null;
            var l = new List<AnExpression>();
            void Add(string ctx, byte[]? code)
            {
                if (code != null)
                    l.Add(
                        new AnExpression
                        {
                            Context = ctx,
                            Code = code,
                            Program = o.Program ?? run.Compiled.Program,
                        }
                    );
            }
            Add("Visibility", o.Vis);
            Add("Disable", o.Dis);
            Add("Move X", o.Mvx);
            Add("Move Y", o.Mvy);
            Add("Size X", o.Szx);
            Add("Size Y", o.Szy);
            Add("Rotation", o.Rot);
            Add("Slider X", o.MvxSl.Read);
            Add("Slider Y", o.MvySl.Read);
            Add("Slider Rotational", o.RotSl.Read);
            for (int i = 0; i < o.Col.Length; i++)
                Add("Fill Color", o.Col[i]);
            Add("Fill Color", o.ColArr);
            Add("Fill Color", o.ColExp);
            Add("Fill Level", o.Fil);
            for (int i = 0; i < o.Txt.Length; i++)
                Add("Display Value", o.Txt[i]);
            Add("Display Value", o.TxtStr);
            Add("Symbol Set", o.SetArr);
            foreach (var v in o.Vertices)
                Add("Vertex", v.Expr);
            if (o.Legacy != null)
                Add("Expression", o.Legacy.Expr);
            return l.Count > 0 ? l : null;
        }

        // ================================================================== data the classes read
        /// <summary>The alarm indicator's colour pair for a priority and state.</summary>
        internal (long fg, long bg)? PriorityColours(long priority, int state)
        {
            if (_prioColours == null)
            {
                _prioColours = new Dictionary<long, (long, long, long, long, long, long)>();
                foreach (var (_, r) in World.Unit.Rows("almprior.dbf"))
                {
                    long? p = World.Unit.LabelValue(r.Get("PRIORITY"));
                    if (p == null || _prioColours.ContainsKey(p.Value))
                        continue;
                    long u = World.Unit.LabelValue(r.Get("UNACKONFG")) ?? 0xFF0000;
                    long a = World.Unit.LabelValue(r.Get("ACKONFG")) ?? u;
                    long off = World.Unit.LabelValue(r.Get("UNACKOFFFG")) ?? a;
                    _prioColours[p.Value] = (
                        u,
                        World.Unit.LabelValue(r.Get("UNACKONBG")) ?? u,
                        a,
                        World.Unit.LabelValue(r.Get("ACKONBG")) ?? a,
                        off,
                        World.Unit.LabelValue(r.Get("UNACKOFFBG")) ?? off
                    );
                }
            }
            if (!_prioColours.TryGetValue(priority, out var c))
                return null;
            return state == 1 ? (c.Item1, c.Item2)
                : state == 2 ? (c.Item5, c.Item6)
                : (c.Item3, c.Item4);
        }
    }
}
