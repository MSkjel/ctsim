using System;
using System.Collections.Generic;
using System.IO;
using CicodeSim.Builtins;
using CicodeSim.Values;
using CicodeSim.Vm;
using CProgram = CicodeSim.Compiled.Program;
using Rdb = CicodeSim.Compiled.Rdb;

namespace CicodeSim.World
{
    /// <summary>The VM over a World.</summary>
    public sealed class WorldRun
    {
        public World World { get; }
        public CicodeVm Vm { get; }
        public Scheduler Scheduler { get; }
        public CProgram Library { get; }

        /// <summary>Instructions per slice that count as a used-up slice: the simulated clock
        /// stands still while code runs, so loops yield by count (as ctsim-dev run does).</summary>
        public const int SliceOps = 20000;

        public WorldRun(World world, CProgram library, IBuiltins? builtins = null, CodeConfig? config = null)
        {
            World = world;
            Library = library;
            if (config == null)
            {
                config = CodeConfig.FromParams((sec, key, def) => world.Param(sec, key, def) ?? def);
                config.MaxSliceOps = SliceOps;
            }
            // the World's built-ins (tags, equipment, time, tasks ... over this World);
            // pass Registry.Default() for the pure ones only (the golden world sweep does)
            Vm = new CicodeVm(
                library,
                builtins ?? WorldBuiltins.Create(world),
                world.Tags,
                world.Clock,
                null,
                config
            );
            Scheduler = new Scheduler(Vm);
        }

        /// <summary>A World of ``projectDir``, run as the compiled project ``root``.</summary>
        public static WorldRun ForProject(
            string projectDir,
            string? root = null,
            object? start = null,
            string? rdb = null
        )
        {
            string dir = projectDir.TrimEnd('\\', '/');
            var world = new World(dir, root: root ?? Path.GetFileName(dir), start: start);
            var lib = Rdb.LoadLibrary(rdb ?? Path.Combine(dir, "_LIBRARY.RDB"));
            world.Start();
            return new WorldRun(world, lib);
        }

        /// <summary>Arguments from text by the function's signature (ctsim-dev run's rule: an INT
        /// argument that parses is a LONG, a REAL one a REAL, everything else a STRING).</summary>
        public static List<Value> ParseArgs(Compiled.Function? fn, IReadOnlyList<string> args)
        {
            var vals = new List<Value>();
            for (int k = 0; k < args.Count; k++)
            {
                string a = args[k];
                int t = fn != null && k < fn.Args.Length ? Types.SigType(fn.Args[k]) : Types.String;
                if (
                    t == Types.Long
                    && long.TryParse(
                        a,
                        System.Globalization.NumberStyles.AllowLeadingSign,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out long l
                    )
                )
                    vals.Add(Value.Long((int)l));
                else if (
                    t == Types.Real
                    && double.TryParse(
                        a,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double d
                    )
                )
                    vals.Add(Value.Real(d));
                else
                    vals.Add(Value.Str(a));
            }
            return vals;
        }

        /// <summary>Call ``function`` as a background task and run until it ends.</summary>
        public RunResult Call(
            string function,
            IReadOnlyList<Value>? args = null,
            double maxSeconds = 60.0,
            long maxSlices = 100000
        )
        {
            var changes = new List<TagChange>();
            Action unsubscribe = World.Tags.Subscribe(ch =>
            {
                if (ch.By == "cicode")
                    changes.Add(ch);
            });
            double t0 = World.Clock.Now();
            var task = Scheduler.Spawn(function, args ?? Array.Empty<Value>());
            long slices = 0;
            bool waited = false;
            try
            {
                while (!task.Done && slices < maxSlices)
                {
                    if (Scheduler.Step())
                    {
                        slices++;
                        continue;
                    }
                    double? dt = SecondsToNextWake();
                    if (dt == null || World.Clock.Now() - t0 + dt.Value > maxSeconds)
                    {
                        waited = task.State == TaskState.Wait || task.State == TaskState.Sleep;
                        break;
                    }
                    World.Clock.Advance(dt.Value);
                }
                if (World.Tags.WriteDelay > 0 && World.Tags.PendingWrites() > 0)
                    World.Clock.Advance(World.Tags.WriteDelay);
            }
            finally
            {
                unsubscribe();
            }
            return new RunResult(task, waited, slices, maxSlices, World.Clock.Now() - t0, changes);
        }

        /// <summary>Simulated seconds until the first waiting task of the scheduler can wake (the
        /// called one or a task an earlier call left running, each at its own time), or null when
        /// nothing on the clock wakes any of them.</summary>
        private double? SecondsToNextWake()
        {
            double? best = null;
            foreach (var t in Scheduler.All)
            {
                if (t.Done)
                    continue;
                double? dt = SecondsToWake(t);
                if (dt != null && (best == null || dt.Value < best.Value))
                    best = dt;
            }
            return best;
        }

        /// <summary>Simulated seconds until the waiting task can wake (at least 1 ms), or null
        /// when nothing on the clock wakes it (a poll gets the world's step).</summary>
        private double? SecondsToWake(CicodeTask task)
        {
            var w = task.Wait;
            if (task.State != TaskState.Wait && task.State != TaskState.Sleep)
                return null;
            if (w == null)
                return 0.001;
            double dt = 0.0;
            bool timed = false;
            if (w.UntilMs.HasValue)
            {
                dt = Math.Max(dt, (w.UntilMs.Value - World.Clock.MonotonicMs()) / 1000.0);
                timed = true;
            }
            if (w.UntilSec.HasValue)
            {
                dt = Math.Max(dt, w.UntilSec.Value - Clocks.UnixSeconds(World.Clock));
                timed = true;
            }
            if (!timed)
            {
                if (w.Poll == null)
                    return null;
                dt = World.Clock.StepSize;
            }
            return Math.Max(dt, 0.001);
        }
    }

    /// <summary>The outcome of WorldRun.Call: the task (state, halt code, result ...), the
    /// slices run, the simulated seconds that passed and the tag changes the code made.</summary>
    public sealed class RunResult
    {
        public CicodeTask Task { get; }

        /// <summary>The task was still waiting when the time budget ran out.</summary>
        public bool Waited { get; }
        public long Slices { get; }
        public long MaxSlices { get; }
        public double SimSeconds { get; }
        public List<TagChange> Changes { get; }

        public RunResult(
            CicodeTask task,
            bool waited,
            long slices,
            long maxSlices,
            double simSeconds,
            List<TagChange> changes
        )
        {
            Task = task;
            Waited = waited;
            Slices = slices;
            MaxSlices = maxSlices;
            SimSeconds = simSeconds;
            Changes = changes;
        }

        /// <summary>stop / halt &lt;code&gt; / not simulated / waits / still running (ctsim-dev run's words).</summary>
        public string Outcome
        {
            get
            {
                var t = Task;
                if (Waited)
                    return "waits";
                if (t.State == TaskState.Stop)
                    return "stop";
                if (t.State == TaskState.Halt)
                    return t.HaltReason.Contains("not simulated")
                        ? "not simulated"
                        : "halt " + t.HaltCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!t.Done && Slices >= MaxSlices)
                    return "still running";
                return "state " + t.State.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }
}
