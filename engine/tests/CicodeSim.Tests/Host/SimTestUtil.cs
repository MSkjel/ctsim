using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CicodeSim.Sim;
using CicodeSim.Tests.Vm;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Host
{
    /// <summary>The R2 projects the server tests run (read only: a Simulation never writes
    /// into a project; a trace goes to a temporary file).</summary>
    public static class SimCorpus
    {
        public const string User = @"C:\ProgramData\AVEVA Plant SCADA 2023 R2\User";
        public static readonly string ExampleSA = Path.Combine(User, "ExampleSA");
        public static readonly string Example = Path.Combine(User, "Example");
        public static bool Present =>
            !LocalData.NoInstall
            && File.Exists(Path.Combine(ExampleSA, "_LIBRARY.RDB"))
            && File.Exists(Path.Combine(Example, "_LIBRARY.RDB"));
    }

    /// <summary>A fact that needs the R2 ExampleSA and Example projects.</summary>
    public sealed class ProjectFactAttribute : FactAttribute
    {
        public ProjectFactAttribute()
        {
            if (!SimCorpus.Present)
                Skip = "R2 ExampleSA / Example not installed";
        }

        /// <summary>Local data the test reads (a repository-relative file or folder, see
        /// <see cref="LocalData"/>): the test skips without it.</summary>
        public string? Needs
        {
            get => _needs;
            set
            {
                _needs = value;
                if (Skip == null && value != null && !LocalData.Has(value))
                    Skip = LocalData.Missing(value);
            }
        }

        private string? _needs;
    }

    /// <summary>Events of a simulation's debug target and log, collected for waiting on.</summary>
    public sealed class SimEvents
    {
        private readonly BlockingCollection<object> _q = new BlockingCollection<object>();
        public readonly List<object> All = new List<object>();
        public readonly List<LogEntry> Logs = new List<LogEntry>();

        public SimEvents(Simulation sim)
        {
            var d = sim.Debugger;
            d.Stopped += (s, e) => Add(e);
            d.Continued += (s, e) => Add(e);
            d.Parked += (s, e) => Add(("parked", e));
            d.ErrorReported += (s, e) => Add(e);
            d.TaskStarted += (s, e) => Add(("started", e));
            d.TaskExited += (s, e) => Add(("exited", e));
            d.Output += (s, e) => Add(e);
            sim.Log += e =>
            {
                lock (Logs)
                    Logs.Add(e);
            };
        }

        private void Add(object o)
        {
            lock (All)
                All.Add(o);
            _q.Add(o);
        }

        /// <summary>The next event of type T that matches (earlier non-matching ones are skipped).</summary>
        public T Wait<T>(Func<T, bool>? match = null, int timeoutMs = 15000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                int left = timeoutMs - (int)sw.ElapsedMilliseconds;
                if (left <= 0 || !_q.TryTake(out object? o, left))
                    throw new TimeoutException(
                        "no " + typeof(T).Name + " event in " + timeoutMs + " ms; seen: " + Seen()
                    );
                if (o is T t && (match == null || match(t)))
                    return t;
            }
        }

        /// <summary>The next TaskExited of a task.</summary>
        public TaskEventArgs WaitExit(int task, int timeoutMs = 15000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                int left = timeoutMs - (int)sw.ElapsedMilliseconds;
                if (left <= 0 || !_q.TryTake(out object? o, left))
                    throw new TimeoutException("task " + task + " did not exit; seen: " + Seen());
                if (o is ValueTuple<string, TaskEventArgs> p && p.Item1 == "exited" && p.Item2.Task == task)
                    return p.Item2;
            }
        }

        public string Seen()
        {
            lock (All)
                return string.Join("; ", All.ConvertAll(Describe));
        }

        private static string Describe(object o)
        {
            switch (o)
            {
                case StoppedEventArgs s:
                    return "stopped " + s.Reason + " task " + s.Task + " code " + s.Code;
                case HardwareErrorEventArgs h:
                    return "error " + h.Code + " halting " + h.Halting + " line " + h.Line;
                case ContinuedEventArgs c:
                    return "continued " + c.Task;
                case OutputEventArgs op:
                    return "output " + op.Text.Trim();
                case ValueTuple<string, TaskEventArgs> p:
                    return p.Item1 + " " + p.Item2.Task + " " + p.Item2.Name + " " + p.Item2.HaltReason;
            }
            return o.ToString() ?? "";
        }

        public List<T> Of<T>()
        {
            lock (All)
                return All.FindAll(o => o is T).ConvertAll(o => (T)o);
        }
    }

    public static class SimTestUtil
    {
        public static Simulation Start(
            string project,
            string clock = "paused",
            double speed = 1.0,
            bool startAlarms = false,
            string compile = CodeSource.Changed
        )
        {
            var sim = new Simulation(
                new SimulationOptions
                {
                    ProjectDir = project,
                    Compile = compile,
                    ClockMode = clock,
                    Speed = speed,
                    Start = "2026-10-01T08:00:00Z",
                    StartAlarms = startAlarms,
                    ScanMs = 50,
                    TimeZone = TimeZoneInfo.Utc,
                }
            );
            sim.Start();
            return sim;
        }

        /// <summary>Wait until a condition on the simulation thread holds.</summary>
        public static void Until(
            Simulation sim,
            Func<bool> cond,
            int timeoutMs = 15000,
            string what = "the condition"
        )
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!sim.Invoke(cond))
            {
                if (sw.ElapsedMilliseconds > timeoutMs)
                    throw new TimeoutException(what + " did not hold in " + timeoutMs + " ms");
                Thread.Sleep(10);
            }
        }
    }
}
