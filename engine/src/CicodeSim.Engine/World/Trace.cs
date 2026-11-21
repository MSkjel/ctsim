using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.World
{
    /// <summary>Writes the ctsim-trace/0 trace format.</summary>
    public sealed class TraceWriter : IDisposable
    {
        public const string Format = "ctsim-trace/0";
        public static readonly string[] Kinds =
        {
            "tagWrite",
            "call",
            "stub",
            "error",
            "taskStart",
            "taskEnd",
            "window",
            "page",
            "print",
            "expect",
            "alarm",
            "skip",
            "scenario",
            "scenarioError",
        };

        private readonly SimClock? _clock;
        private readonly object _lock = new object();
        private readonly LinkedList<JsonValue> _ring = new LinkedList<JsonValue>();
        private readonly int _ringMax;
        public long Count { get; private set; }
        private TextWriter? _out;
        private readonly bool _own;
        public JsonValue Header { get; }

        public static string EngineName() => "cicode-sim " + EngineInfo.Version;

        public TraceWriter(
            string? path = null,
            TextWriter? stream = null,
            string project = "",
            string scenario = "",
            string? engine = null,
            SimClock? clock = null,
            int ring = 10_000,
            JsonValue? extra = null
        )
        {
            _clock = clock;
            _ringMax = Math.Max(1, ring);
            if (path != null)
            {
                stream = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" };
                _own = true;
            }
            _out = stream;
            Header = JsonValue.NewObject();
            Header.Set("format", JsonValue.Of(Format));
            Header.Set("engine", JsonValue.Of(engine ?? EngineName()));
            Header.Set("project", JsonValue.Of(project));
            Header.Set("scenario", JsonValue.Of(scenario));
            if (clock != null)
                Header.Set("start", JsonValue.Of(clock.StartUnix));
            if (extra != null && extra.Kind == JsonKind.Object)
                foreach (var kv in extra.Pairs)
                    Header.Set(kv.Key, Jsonable(kv.Value));
            Write(Header);
        }

        private void Write(JsonValue obj)
        {
            if (_out != null)
            {
                _out.Write(obj.Dump());
                _out.Write('\n');
            }
        }

        /// <summary>A value as JSON (trace.jsonable): a Value as its payload, lists and objects
        /// recursively, non-finite doubles as text ('inf', '-inf', 'nan').</summary>
        public static JsonValue Jsonable(object? v)
        {
            if (v is JsonValue j)
            {
                switch (j.Kind)
                {
                    case JsonKind.Float:
                        return double.IsNaN(j.FloatValue) || double.IsInfinity(j.FloatValue)
                            ? JsonValue.Of(FloatText.Shortest(j.FloatValue))
                            : j;
                    case JsonKind.Array:
                    {
                        var a = JsonValue.NewArray();
                        foreach (var x in j.Items)
                            a.Add(Jsonable(x));
                        return a;
                    }
                    case JsonKind.Object:
                    {
                        var o = JsonValue.NewObject();
                        foreach (var kv in j.Pairs)
                            o.Set(kv.Key, Jsonable(kv.Value));
                        return o;
                    }
                    default:
                        return j;
                }
            }
            if (v is double d)
                return double.IsNaN(d) || double.IsInfinity(d)
                    ? JsonValue.Of(FloatText.Shortest(d))
                    : JsonValue.Of(d);
            if (v is Value val)
                return Jsonable(WorldValues.Payload(val));
            return Jsonable(JsonValue.From(v));
        }

        /// <summary>Write one event; returns it (with its serial n).</summary>
        public JsonValue Event(
            string ev,
            double? t = null,
            object? task = null,
            params (string key, object? value)[] fields
        )
        {
            double tt = t ?? (_clock != null ? _clock.Now() : 0.0);
            lock (_lock)
            {
                Count++;
                var obj = JsonValue.NewObject();
                obj.Set("ev", JsonValue.Of(ev));
                obj.Set("t", JsonValue.Of(FloatText.Round(tt, 6)));
                obj.Set("task", Jsonable(task));
                obj.Set("n", JsonValue.Of(Count));
                foreach (var (k, v) in fields)
                    obj.Set(k, Jsonable(v));
                _ring.AddLast(obj);
                while (_ring.Count > _ringMax)
                    _ring.RemoveFirst();
                Write(obj);
                return obj;
            }
        }

        // ------------------------------------------------------------------ the README's kinds
        public JsonValue TagWrite(
            string tag,
            object? value,
            string by,
            string? quality = null,
            double? t = null,
            object? task = null
        )
        {
            if (!string.IsNullOrEmpty(quality) && quality != "good")
                return Event(
                    "tagWrite",
                    t,
                    task,
                    ("tag", tag),
                    ("value", value),
                    ("by", by),
                    ("quality", quality)
                );
            return Event("tagWrite", t, task, ("tag", tag), ("value", value), ("by", by));
        }

        public JsonValue Call(string fn, IEnumerable<object?> args, double? t = null, object? task = null) =>
            Event("call", t, task, ("fn", fn), ("args", JsonValue.From(args)));

        public JsonValue Stub(
            string fn,
            IEnumerable<object?> args,
            object? returned,
            double? t = null,
            object? task = null
        ) => Event("stub", t, task, ("fn", fn), ("args", JsonValue.From(args)), ("returned", returned));

        public JsonValue Error(
            long code,
            string message = "",
            string fn = "",
            string file = "",
            long line = 0,
            double? t = null,
            object? task = null
        ) =>
            Event(
                "error",
                t,
                task,
                ("code", code),
                ("message", message),
                ("fn", fn),
                ("file", file),
                ("line", line)
            );

        public JsonValue TaskStart(string fn, double? t = null, object? task = null) =>
            Event("taskStart", t, task, ("fn", fn));

        public JsonValue TaskEnd(string fn, string reason, double? t = null, object? task = null) =>
            Event("taskEnd", t, task, ("fn", fn), ("reason", reason));

        public JsonValue Window(
            string op,
            long window,
            string page = "",
            double? t = null,
            object? task = null
        ) => Event("window", t, task, ("op", op), ("window", window), ("page", page));

        public JsonValue Page(long window, string name, string ev, double? t = null, object? task = null) =>
            Event("page", t, task, ("window", window), ("name", name), ("event", ev));

        public JsonValue Print(string text, double? t = null, object? task = null) =>
            Event("print", t, task, ("text", text));

        public JsonValue Expect(
            string tag,
            object? want,
            object? got,
            bool ok,
            double? t = null,
            params (string, object?)[] extra
        )
        {
            var f = new List<(string, object?)> { ("tag", tag), ("want", want), ("got", got), ("ok", ok) };
            f.AddRange(extra);
            return Event("expect", t, null, f.ToArray());
        }

        public JsonValue Alarm(
            string tag,
            string ev,
            long state,
            string by = "alarm",
            double? t = null,
            params (string, object?)[] extra
        )
        {
            var f = new List<(string, object?)> { ("tag", tag), ("event", ev), ("state", state), ("by", by) };
            f.AddRange(extra);
            return Event("alarm", t, null, f.ToArray());
        }

        public JsonValue Skip(object? step, string reason, double? t = null) =>
            Event("skip", t, null, ("step", step), ("reason", reason));

        /// <summary>A scenario step that could not run: its code (scenarios/README.md) and a plain
        /// message.</summary>
        public JsonValue ScenarioError(string step, string code, string message, double? t = null) =>
            Event("scenarioError", t, null, ("step", step), ("code", code), ("message", message));

        // ------------------------------------------------------------------ the viewer side
        /// <summary>The kept events with a serial above n.</summary>
        public List<JsonValue> Since(long n)
        {
            lock (_lock)
            {
                var o = new List<JsonValue>();
                foreach (var e in _ring)
                    if (e.Get("n")!.Long > n)
                        o.Add(e);
                return o;
            }
        }

        public List<JsonValue> Events()
        {
            lock (_lock)
                return new List<JsonValue>(_ring);
        }

        public void Flush() => _out?.Flush();

        public void Close()
        {
            if (_own && _out != null)
                _out.Dispose();
            else
                _out?.Flush();
            _out = null;
        }

        public void Dispose() => Close();

        // ------------------------------------------------------------------ reading / comparing
        /// <summary>(header, events) of a trace file. FormatException for a file that is not
        /// ctsim-trace/0.</summary>
        public static (JsonValue header, List<JsonValue> events) ReadTrace(string path) =>
            ReadTrace(CitectIni.SplitLines(File.ReadAllText(path, Encoding.UTF8)));

        public static (JsonValue header, List<JsonValue> events) ReadTrace(IEnumerable<string> source)
        {
            var lines = new List<string>();
            foreach (string ln in source)
                if (!CitectText.IsBlank(ln))
                    lines.Add(ln);
            if (lines.Count == 0)
                throw new FormatException("empty trace");
            JsonValue header = JsonValue.Parse(lines[0]);
            var fmt = header.Get("format");
            if (fmt == null || fmt.Kind != JsonKind.String || fmt.Str != Format)
                throw new FormatException(
                    $"not a {Format} trace: {(fmt == null ? "no 'format'" : fmt.Dump())}"
                );
            var events = new List<JsonValue>();
            for (int i = 1; i < lines.Count; i++)
                events.Add(JsonValue.Parse(lines[i]));
            return (header, events);
        }

        /// <summary>The fields that identify an event when two traces are compared (t, task and n
        /// are not compared).</summary>
        public static readonly Dictionary<string, string[]> Keys = new Dictionary<string, string[]>
        {
            ["tagWrite"] = new[] { "tag", "value", "by" },
            ["call"] = new[] { "fn", "args" },
            ["stub"] = new[] { "fn", "args", "returned" },
            ["error"] = new[] { "code", "fn" },
            ["taskStart"] = new[] { "fn" },
            ["taskEnd"] = new[] { "fn", "reason" },
            ["window"] = new[] { "op", "window", "page" },
            ["page"] = new[] { "window", "name", "event" },
            ["print"] = new[] { "text" },
            ["expect"] = new[] { "tag", "want", "ok" },
            ["alarm"] = new[] { "tag", "event" },
            ["scenarioError"] = new[] { "step", "code" },
        };

        /// <summary>Two event fields are the same: numbers within 1e-9 relative when either is
        /// a double (a field missing on one side is not), the rest equal by value.</summary>
        private static bool Same(JsonValue? a, JsonValue? b)
        {
            bool fa = a != null && a.Kind == JsonKind.Float,
                fb = b != null && b.Kind == JsonKind.Float;
            if (fa || fb)
            {
                if (a == null || b == null || !a.IsNumber || !b.IsNumber)
                    return false;
                double x = a.ToNumber(),
                    y = b.ToNumber();
                return Math.Abs(x - y) <= 1e-9 * FloatOrder.Max(1.0, Math.Abs(x));
            }
            if (a == null || b == null)
                return a == null && b == null;
            return a.ValueEquals(b);
        }

        private static string Show(JsonValue? v) => v == null ? "(missing)" : v.Dump();

        /// <summary>An event's t as seconds (0 when it is missing or no number).</summary>
        private static double Seconds(JsonValue? t) => t != null && t.IsNumber ? t.ToNumber() : 0.0;

        /// <summary>Differences between two event lists (README: events matched in order per
        /// kind, t may differ by one scan). Kinds without a key list are not compared.</summary>
        public static List<string> Compare(
            IList<JsonValue> a,
            IList<JsonValue> b,
            double scan = 0.25,
            IEnumerable<string>? kinds = null
        )
        {
            var o = new List<string>();
            var want = new HashSet<string>(kinds ?? Keys.Keys);
            var byA = new Dictionary<string, List<JsonValue>>();
            var byB = new Dictionary<string, List<JsonValue>>();
            foreach (var e in a)
            {
                var ev = e.Get("ev");
                if (ev != null && ev.Kind == JsonKind.String && want.Contains(ev.Str))
                {
                    if (!byA.TryGetValue(ev.Str, out var l))
                        byA[ev.Str] = l = new List<JsonValue>();
                    l.Add(e);
                }
            }
            foreach (var e in b)
            {
                var ev = e.Get("ev");
                if (ev != null && ev.Kind == JsonKind.String && want.Contains(ev.Str))
                {
                    if (!byB.TryGetValue(ev.Str, out var l))
                        byB[ev.Str] = l = new List<JsonValue>();
                    l.Add(e);
                }
            }
            var all = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string k in byA.Keys)
                all.Add(k);
            foreach (string k in byB.Keys)
                all.Add(k);
            foreach (string ev in all)
            {
                var xs = byA.TryGetValue(ev, out var lx) ? lx : new List<JsonValue>();
                var ys = byB.TryGetValue(ev, out var ly) ? ly : new List<JsonValue>();
                for (int i = 0; i < Math.Max(xs.Count, ys.Count); i++)
                {
                    if (i >= xs.Count)
                    {
                        o.Add($"{ev} #{i}: only in the second trace: {ys[i].Dump()}");
                        continue;
                    }
                    if (i >= ys.Count)
                    {
                        o.Add($"{ev} #{i}: only in the first trace: {xs[i].Dump()}");
                        continue;
                    }
                    JsonValue x = xs[i],
                        y = ys[i];
                    if (Keys.TryGetValue(ev, out string[]? ks))
                        foreach (string k in ks)
                            if (!Same(x.Get(k), y.Get(k)))
                                o.Add($"{ev} #{i}: {k} {Show(x.Get(k))} != {Show(y.Get(k))}");
                    double tx = Seconds(x.Get("t")),
                        ty = Seconds(y.Get("t"));
                    if (Math.Abs(tx - ty) > scan + 1e-9)
                        o.Add($"{ev} #{i}: t {Show(x.Get("t"))} vs {Show(y.Get("t"))}");
                }
            }
            return o;
        }
    }
}
