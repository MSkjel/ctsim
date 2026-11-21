using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using CicodeSim.Values;

namespace CicodeSim.Sim
{
    /// <summary>Where a simulation spends its time (ctsim/profile).</summary>
    public sealed class Profiler
    {
        private sealed class Cell
        {
            public long Count;
            public long Ticks;
            public long MaxTicks;
        }

        private readonly Dictionary<(int Window, int An, string Class), Cell> _expr =
            new Dictionary<(int, int, string), Cell>();
        private readonly Dictionary<(int Window, string Page), Cell> _scans =
            new Dictionary<(int, string), Cell>();
        private readonly Dictionary<string, Cell> _tasks = new Dictionary<string, Cell>(
            StringComparer.OrdinalIgnoreCase
        );
        private readonly Dictionary<int, string> _pages = new Dictionary<int, string>();
        private long _since;

        /// <summary>Each Cicode and built-in function's time: the VM's hook.</summary>
        public readonly Vm.FunctionProfile Functions = new Vm.FunctionProfile();

        public bool Running { get; private set; }

        public void Start()
        {
            if (!Running)
                _since = Stopwatch.GetTimestamp();
            Running = true;
        }

        public void Stop() => Running = false;

        public void Reset()
        {
            _expr.Clear();
            _scans.Clear();
            _tasks.Clear();
            Functions.Reset();
            _since = Stopwatch.GetTimestamp();
        }

        private static void Add(Cell c, long ticks)
        {
            c.Count++;
            c.Ticks += ticks;
            if (ticks > c.MaxTicks)
                c.MaxTicks = ticks;
        }

        public void Expression(int window, int an, string cls, long ticks)
        {
            var k = (window, an, cls ?? "");
            if (!_expr.TryGetValue(k, out var c))
                _expr[k] = c = new Cell();
            Add(c, ticks);
        }

        public void Scan(int window, string page, long ticks)
        {
            _pages[window] = page ?? "";
            var k = (window, page ?? "");
            if (!_scans.TryGetValue(k, out var c))
                _scans[k] = c = new Cell();
            Add(c, ticks);
        }

        public void Task(string name, long ticks)
        {
            string k = string.IsNullOrEmpty(name) ? "(task)" : name;
            if (!_tasks.TryGetValue(k, out var c))
                _tasks[k] = c = new Cell();
            Add(c, ticks);
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private static JsonValue Times(Cell c)
        {
            var o = JsonValue.NewObject();
            o.Set("count", JsonValue.Of(c.Count));
            o.Set("ms", JsonValue.Of(Math.Round(Ms(c.Ticks), 3)));
            o.Set("avgUs", JsonValue.Of(c.Count > 0 ? Math.Round(Ms(c.Ticks) * 1000.0 / c.Count, 1) : 0.0));
            o.Set("maxUs", JsonValue.Of(Math.Round(Ms(c.MaxTicks) * 1000.0, 1)));
            return o;
        }

        /// <summary>{running, seconds (real time profiled), scans: [{window, page, count, ms, avgUs,
        /// maxUs}], expressions: [{window, page, an, class, ...}] (the <paramref name="top"/>
        /// costliest), tasks: [{name, ...}] (the costliest), expressionsTotal, tasksTotal}.</summary>
        public JsonValue Report(int top = 100, bool tree = false)
        {
            var b = JsonValue.NewObject();
            b.Set("running", JsonValue.Of(Running));
            b.Set(
                "seconds",
                JsonValue.Of(Math.Round((Stopwatch.GetTimestamp() - _since) / (double)Stopwatch.Frequency, 3))
            );
            var scans = JsonValue.NewArray();
            foreach (var kv in _scans.OrderByDescending(x => x.Value.Ticks))
            {
                var o = Times(kv.Value);
                o.Set("window", JsonValue.Of(kv.Key.Window));
                o.Set("page", kv.Key.Page);
                scans.Add(o);
            }
            b.Set("scans", scans);
            var ex = JsonValue.NewArray();
            foreach (var kv in _expr.OrderByDescending(x => x.Value.Ticks).Take(Math.Max(1, top)))
            {
                var o = Times(kv.Value);
                o.Set("window", JsonValue.Of(kv.Key.Window));
                o.Set("page", _pages.TryGetValue(kv.Key.Window, out var p) ? p : "");
                o.Set("an", JsonValue.Of(kv.Key.An));
                o.Set("class", kv.Key.Class);
                ex.Add(o);
            }
            b.Set("expressions", ex);
            b.Set("expressionsTotal", JsonValue.Of(_expr.Count));
            var ts = JsonValue.NewArray();
            foreach (var kv in _tasks.OrderByDescending(x => x.Value.Ticks).Take(Math.Max(1, top)))
            {
                var o = Times(kv.Value);
                o.Set("name", kv.Key);
                ts.Add(o);
            }
            b.Set("tasks", ts);
            b.Set("tasksTotal", JsonValue.Of(_tasks.Count));
            b.Set("functions", Functions.Report(top, out int nf));
            b.Set("functionsTotal", JsonValue.Of(nf));
            if (tree)
                b.Set("tree", Functions.TreeReport());
            return b;
        }
    }
}
