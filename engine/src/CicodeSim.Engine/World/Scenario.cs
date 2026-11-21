using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.World
{
    /// <summary>An engine cannot do this step (the runner reports it as skipped).</summary>
    public sealed class NotSimulatedException : Exception
    {
        public NotSimulatedException(string message)
            : base(message) { }
    }

    /// <summary>What the runner needs from the simulator beyond the world (the VM / animator plug
    /// in here). Every method may throw <see cref="NotSimulatedException"/>.</summary>
    public interface IScenarioEngine
    {
        int SetTag(string name, JsonValue? value, JsonValue? quality = null);
        object? Input(
            long window,
            long an,
            JsonValue kind,
            JsonValue? key,
            JsonValue? value,
            JsonValue? x,
            JsonValue? y
        );
        object? Call(string expr);
        object? OpenPage(long window, JsonValue name);
        string? Page(long window);
        void Scan();
    }

    /// <summary>The engine of a bare World: tags only (input / call / pages are not simulated).</summary>
    public sealed class WorldEngine : IScenarioEngine
    {
        private readonly World _world;

        public WorldEngine(World world)
        {
            _world = world;
        }

        public int SetTag(string name, JsonValue? value, JsonValue? quality = null) =>
            _world.Tags.Set(name, value, quality, by: "scenario");

        public object? Input(
            long window,
            long an,
            JsonValue kind,
            JsonValue? key,
            JsonValue? value,
            JsonValue? x,
            JsonValue? y
        ) => throw new NotSimulatedException("input needs the page animator");

        public object? Call(string expr) => throw new NotSimulatedException("call needs the Cicode VM");

        public object? OpenPage(long window, JsonValue name) =>
            throw new NotSimulatedException("pages need the page animator");

        public string? Page(long window) => throw new NotSimulatedException("pages need the page animator");

        public void Scan() { }
    }

    /// <summary>The 32-bit generator the random step uses (Tommy Ettinger's mulberry32): the
    /// same sequence in every engine.</summary>
    public sealed class Mulberry32
    {
        private uint _state;

        public Mulberry32(uint seed)
        {
            _state = seed;
        }

        /// <summary>A seed's low 32 bits (two's complement: -1 is 0xFFFFFFFF).</summary>
        public static Mulberry32 FromSeed(BigInteger seed)
        {
            var m = BigInteger.One << 32;
            var r = seed % m;
            if (r.Sign < 0)
                r += m;
            return new Mulberry32((uint)r);
        }

        public uint Next()
        {
            unchecked
            {
                _state += 0x6D2B79F5;
                uint z = _state;
                z = (z ^ (z >> 15)) * (z | 1);
                z ^= z + (z ^ (z >> 7)) * (z | 61);
                return z ^ (z >> 14);
            }
        }
    }

    public sealed class Step
    {
        public double At;
        public string Kind = "";
        public JsonValue Arg = JsonValue.Null;
        public int Index;
        public JsonValue Raw = JsonValue.Null;
    }

    public sealed class ExpectResult
    {
        public double T;
        public string Target = "";
        public JsonValue Want = JsonValue.Null;
        public JsonValue Got = JsonValue.Null;
        public bool Ok;
        public string Kind = "expect";
    }

    public sealed class ScenarioResult
    {
        public List<ExpectResult> Expects { get; } = new List<ExpectResult>();
        public List<(double t, string step, string reason)> Skipped { get; } =
            new List<(double, string, string)>();

        /// <summary>The steps that could not run: (t, step, code, message), code as the README's
        /// table (bad-start, bad-step, tag-error, no-user, no-alarm).</summary>
        public List<(double t, string step, string code, string reason)> Errors { get; } =
            new List<(double, string, string, string)>();
        public double End;

        public int Passed
        {
            get
            {
                int n = 0;
                foreach (var e in Expects)
                    if (e.Ok)
                        n++;
                return n;
            }
        }

        public List<ExpectResult> Failed => Expects.FindAll(e => !e.Ok);
        public bool Ok => Failed.Count == 0 && Errors.Count == 0;
    }

    /// <summary>A parsed scenario.</summary>
    public sealed class Scenario
    {
        public const string Format = "ctsim-scenario/0";

        /// <summary>[Page]ScanTime default (doc: 1118065).</summary>
        public const double ScanDefault = 0.25;

        public static readonly string[] Actions =
        {
            "set",
            "quality",
            "ramp",
            "sine",
            "random",
            "square",
            "input",
            "call",
            "page",
            "openPage",
            "ack",
            "disable",
            "enable",
            "login",
            "logout",
            "setAlarm",
            "setEquipment",
            "setServer",
        };
        public static readonly string[] Expects = { "expect", "expectPage", "expectAlarm" };

        public JsonValue Data { get; }
        public string Path { get; }
        public JsonValue Project { get; }
        public JsonValue Description { get; }
        public JsonValue? User { get; }
        public JsonValue Clock { get; }
        public JsonValue? PageSpec { get; }
        public JsonValue Tags { get; }
        public double? End { get; }
        public List<Step> Steps { get; }

        public Scenario(JsonValue data, string path = "")
        {
            var fmt = data.Get("format");
            if (fmt == null || fmt.Kind != JsonKind.String || fmt.Str != Format)
                throw new FormatException(
                    $"not a {Format} scenario: {(fmt == null ? "no 'format'" : fmt.Dump())}"
                );
            Data = data;
            Path = path;
            Project = data.Get("project") ?? JsonValue.Of("");
            Description = data.Get("description") ?? JsonValue.Of("");
            User = data.Get("user");
            var clock = data.Get("clock");
            Clock = clock != null && clock.IsSet ? clock : JsonValue.NewObject();
            PageSpec = data.Get("page");
            var tags = data.Get("tags");
            Tags = tags != null && tags.IsSet ? tags : JsonValue.NewObject();
            End = data.Has("end") ? data["end"].ToNumber() : (double?)null;
            var steps = data.Get("steps");
            Steps = ParseSteps(steps != null && steps.IsSet ? steps : JsonValue.NewArray());
        }

        public static Scenario Load(string path) =>
            new Scenario(JsonValue.Parse(File.ReadAllText(path, Encoding.UTF8)), path);

        public static Scenario FromJson(JsonValue data) => new Scenario(data);

        public static bool IsAction(string k) => Array.IndexOf(Actions, k) >= 0;

        public static bool IsExpect(string k) => Array.IndexOf(Expects, k) >= 0;

        private static List<Step> ParseSteps(JsonValue items)
        {
            var o = new List<Step>();
            int i = 0;
            foreach (var item in items.Items)
            {
                if (!item.Has("at"))
                    throw new FormatException($"step {i}: no 'at'");
                double at = item["at"].ToNumber();
                if (at < 0)
                    throw new FormatException($"step {i}: 'at' < 0");
                var kinds = new List<string>();
                foreach (var kv in item.Pairs)
                    if (kv.Key != "at")
                        kinds.Add(kv.Key);
                var known = kinds.FindAll(k => IsAction(k) || IsExpect(k));
                if (known.Count != 1)
                {
                    var all = new List<string>(Actions);
                    all.AddRange(Expects);
                    string got = kinds.Count == 0 ? "none" : string.Join(", ", kinds);
                    throw new FormatException(
                        $"step {i}: one action or expectation expected ({string.Join(", ", all)}), got {got}"
                    );
                }
                o.Add(
                    new Step
                    {
                        At = at,
                        Kind = known[0],
                        Arg = item[known[0]],
                        Index = i,
                        Raw = item,
                    }
                );
                i++;
            }
            o.Sort(
                (a, b) =>
                {
                    int c = a.At.CompareTo(b.At);
                    return c != 0 ? c : a.Index.CompareTo(b.Index);
                }
            );
            return o;
        }
    }

    /// <summary>Runs a <see cref="Scenario"/> on a World: moves the clock to each step time
    /// (which fires the generators, the alarm scan and any other timer on the way), runs the
    /// time's actions in order, lets the engine scan, then checks the time's expectations.</summary>
    public sealed class ScenarioRunner
    {
        private readonly World _world;
        private readonly Scenario _sc;
        private readonly IScenarioEngine _engine;

        /// <summary>The scenario it runs.</summary>
        public Scenario Scenario => _sc;
        private readonly TraceWriter? _trace;
        public ScenarioResult Result { get; } = new ScenarioResult();
        private readonly List<Timer> _generators = new List<Timer>();

        public ScenarioRunner(
            World world,
            Scenario scenario,
            IScenarioEngine? engine = null,
            TraceWriter? trace = null
        )
        {
            _world = world;
            _sc = scenario;
            _engine = engine ?? new WorldEngine(world);
            _trace = trace ?? world.Trace;
        }

        private double Now() => _world.Clock.Now();

        private void Skip(string step, string reason)
        {
            double t = Now();
            Result.Skipped.Add((t, step, reason));
            _trace?.Skip(step, reason, t);
        }

        private void Error(string step, string code, string reason)
        {
            double t = Now();
            Result.Errors.Add((t, step, code, reason));
            _trace?.ScenarioError(step, code, reason, t);
        }

        private void SetTag(string name, JsonValue? value, JsonValue? quality = null)
        {
            int err;
            try
            {
                err = _engine.SetTag(name, value, quality);
            }
            catch (NotSimulatedException e)
            {
                Skip($"set {name}", e.Message);
                return;
            }
            if (err != 0)
                Error($"set {name}", "tag-error", $"error {err}");
        }

        private static string Str(JsonValue? v) => v == null ? "" : v.Text;

        // ------------------------------------------------------------------ setup
        public void Setup()
        {
            var sc = _sc;
            var w = _world;
            string? startError = null;
            var start = sc.Clock.Get("start");
            if (start != null && !start.IsNull)
            {
                // the README's clock.start: the simulated wall time at t = 0
                try
                {
                    w.Clock.SetStart(start);
                }
                catch (Exception e)
                    when (e is InvalidOperationException || e is FormatException || e is ArgumentException)
                {
                    startError = e.Message;
                }
            }
            _trace?.Event(
                "scenario",
                null,
                null,
                ("event", "start"),
                ("path", sc.Path),
                ("project", sc.Project),
                ("start", w.Clock.StartUnix)
            );
            if (startError != null)
                Error("clock.start", "bad-start", startError);
            if (sc.Clock.Has("speed"))
                w.Clock.SetSpeed(sc.Clock["speed"].ToNumber());
            if (sc.User != null && sc.User.IsSet)
            {
                var u = sc.User;
                if (u.Get("login")?.IsSet == true)
                {
                    if (w.Users.Login(Str(u.Get("name") ?? JsonValue.Of(""))) != 0)
                        Error("user", "no-user", $"no user {CitectText.Quote(Str(u.Get("name")))}");
                }
                else
                {
                    var roles = new List<string>();
                    var rv = u.Get("roles");
                    if (rv != null && rv.Kind == JsonKind.Array)
                        foreach (var r in rv.Items)
                            roles.Add(r.Text);
                    else if (rv != null && rv.Kind == JsonKind.String)
                        foreach (char c in rv.Str)
                            roles.Add(c.ToString());
                    w.Users.LoginAs(
                        Str(u.Get("name") ?? JsonValue.Of("User")),
                        Str(u.Get("fullName") ?? JsonValue.Of("")),
                        u.Get("privileges"),
                        u.Get("areas"),
                        roles
                    );
                }
            }
            foreach (var kv in sc.Tags.Pairs)
            {
                if (kv.Value.Kind == JsonKind.Object)
                    SetTag(kv.Key, kv.Value.Get("value"), kv.Value.Get("quality"));
                else
                    SetTag(kv.Key, kv.Value);
            }
            if (sc.PageSpec != null && sc.PageSpec.IsSet)
                OpenPage(sc.PageSpec);
        }

        private void OpenPage(JsonValue spec)
        {
            try
            {
                _engine.OpenPage((long)(spec.Get("window") ?? JsonValue.Of(0)).ToInteger(), spec["name"]);
            }
            catch (NotSimulatedException e)
            {
                Skip($"page {Str(spec.Get("name"))}", e.Message);
            }
        }

        // ------------------------------------------------------------------ running
        public ScenarioResult Run()
        {
            var clock = _world.Clock;
            Setup();
            var steps = _sc.Steps;
            int i = 0;
            while (i < steps.Count)
            {
                double at = steps[i].At;
                var group = new List<Step>();
                while (i < steps.Count && steps[i].At == at)
                    group.Add(steps[i++]);
                if (at > clock.Now())
                    clock.AdvanceTo(at);
                foreach (var st in group)
                    if (!Scenario.IsExpect(st.Kind))
                        Action(st);
                DoScan();
                foreach (var st in group)
                    if (Scenario.IsExpect(st.Kind))
                        Expect(st);
            }
            double? end = _sc.End;
            if (end != null && end.Value > clock.Now())
            {
                clock.AdvanceTo(end.Value);
                DoScan();
            }
            Result.End = clock.Now();
            _trace?.Event(
                "scenario",
                null,
                null,
                ("event", "end"),
                ("passed", Result.Passed),
                ("failed", Result.Failed.Count),
                ("skipped", Result.Skipped.Count)
            );
            return Result;
        }

        private readonly List<Timer> _stepTimers = new List<Timer>();

        /// <summary>The scenario runs on the clock (<see cref="Schedule"/>) and has not finished.</summary>
        public bool Running { get; private set; }

        /// <summary>Run the scenario on a clock something else moves.</summary>
        public void Schedule(Action<ScenarioResult>? finished = null)
        {
            var clock = _world.Clock;
            Setup();
            Running = true;
            long t0 = clock.NowMs;
            var steps = _sc.Steps;
            var groups = new List<List<Step>>();
            int i = 0;
            while (i < steps.Count)
            {
                double at = steps[i].At;
                var g = new List<Step>();
                while (i < steps.Count && steps[i].At == at)
                    g.Add(steps[i++]);
                groups.Add(g);
            }
            double? end = _sc.End;
            void Finish()
            {
                if (!Running)
                    return;
                Running = false;
                Result.End = clock.Now();
                _trace?.Event(
                    "scenario",
                    null,
                    null,
                    ("event", "end"),
                    ("passed", Result.Passed),
                    ("failed", Result.Failed.Count),
                    ("skipped", Result.Skipped.Count)
                );
                finished?.Invoke(Result);
            }
            bool endAfter = end != null && (groups.Count == 0 || end.Value > groups[groups.Count - 1][0].At);
            for (int k = 0; k < groups.Count; k++)
            {
                var g = groups[k];
                bool last = k == groups.Count - 1;
                _stepTimers.Add(
                    clock.CallAtMs(
                        t0 + Math.Max(0, SimClock.ToMs(g[0].At)),
                        () =>
                        {
                            foreach (var st in g)
                                if (!Scenario.IsExpect(st.Kind))
                                    Action(st);
                            DoScan();
                            foreach (var st in g)
                                if (Scenario.IsExpect(st.Kind))
                                    Expect(st);
                            if (last && !endAfter)
                                Finish();
                        },
                        "scenario step"
                    )
                );
            }
            if (endAfter)
                _stepTimers.Add(
                    clock.CallAtMs(
                        t0 + Math.Max(0, SimClock.ToMs(end!.Value)),
                        () =>
                        {
                            DoScan();
                            Finish();
                        },
                        "scenario end"
                    )
                );
            if (groups.Count == 0 && !endAfter)
                Finish();
        }

        /// <summary>Stop a scheduled scenario: its pending steps and its generators are cancelled.</summary>
        public void Stop()
        {
            foreach (var tm in _stepTimers)
                tm.Cancel();
            _stepTimers.Clear();
            foreach (var tm in _generators)
                tm.Cancel();
            if (Running)
            {
                Running = false;
                Result.End = _world.Clock.Now();
                _trace?.Event(
                    "scenario",
                    null,
                    null,
                    ("event", "stopped"),
                    ("passed", Result.Passed),
                    ("failed", Result.Failed.Count),
                    ("skipped", Result.Skipped.Count)
                );
            }
        }

        private void DoScan()
        {
            var alarms = _world.Alarms;
            if (alarms.Started)
                alarms.Scan();
            try
            {
                _engine.Scan();
            }
            catch (NotSimulatedException) { }
        }

        private void Action(Step st)
        {
            string k = st.Kind;
            JsonValue a = st.Arg;
            var w = _world;
            if (k == "set")
            {
                foreach (var kv in a.Pairs)
                {
                    if (kv.Value.Kind == JsonKind.Object)
                        SetTag(kv.Key, kv.Value.Get("value"), kv.Value.Get("quality"));
                    else
                        SetTag(kv.Key, kv.Value);
                }
            }
            else if (k == "quality")
            {
                foreach (var kv in a.Pairs)
                    SetTag(kv.Key, null, kv.Value);
            }
            else if (k == "ramp" || k == "sine" || k == "random" || k == "square")
            {
                string? why = null;
                try
                {
                    StartGenerator(k, a);
                }
                catch (Exception e)
                    when (e is FormatException || e is OverflowException || e is ArgumentException)
                {
                    why = e.Message;
                }
                // a bad generator is reported like any failed step, the run goes on
                if (why != null)
                {
                    string what = a.Kind == JsonKind.Object ? Str(a.Get("tag")) : a.Text;
                    Error(what.Length > 0 ? $"{k} {what}" : k, "bad-step", why);
                }
            }
            else if (k == "input")
            {
                try
                {
                    _engine.Input(
                        (long)(a.Get("window") ?? JsonValue.Of(0)).ToInteger(),
                        (long)(a.Get("an") ?? JsonValue.Of(0)).ToInteger(),
                        a.Get("kind") ?? JsonValue.Of("click"),
                        a.Get("key"),
                        a.Get("value"),
                        a.Get("x"),
                        a.Get("y")
                    );
                }
                catch (NotSimulatedException e)
                {
                    Skip($"input AN {Str(a.Get("an"))}", e.Message);
                }
            }
            else if (k == "call")
            {
                try
                {
                    _engine.Call(a.Text);
                }
                catch (NotSimulatedException e)
                {
                    Skip($"call {a.Text}", e.Message);
                }
            }
            else if (k == "page" || k == "openPage")
                OpenPage(a);
            else if (k == "ack")
            {
                var names =
                    a.Kind == JsonKind.Array ? new List<JsonValue>(a.Items) : new List<JsonValue> { a };
                foreach (var n in names)
                {
                    if (CitectText.Lower(n.Text) == "all")
                        w.Alarms.AcknowledgeAll(by: "scenario");
                    else if (w.Alarms.Acknowledge(n.Text, by: "scenario") != 0)
                        Error($"ack {n.Text}", "no-alarm", "no such alarm");
                }
            }
            else if (k == "disable" || k == "enable")
            {
                var names =
                    a.Kind == JsonKind.Array ? new List<JsonValue>(a.Items) : new List<JsonValue> { a };
                foreach (var n in names)
                {
                    int r =
                        k == "disable"
                            ? w.Alarms.Disable(n.Text, by: "scenario")
                            : w.Alarms.Enable(n.Text, by: "scenario");
                    if (r != 0)
                        Error($"{k} {n.Text}", "no-alarm", "no such alarm");
                }
            }
            else if (k == "setServer")
            {
                // {server: online} (World.ServerStates): ServerIsOnline answers 0 while it is off
                if (a.Kind != JsonKind.Object)
                {
                    Error(k, "bad-step", "the step's value must be an object, not " + a.Dump());
                    return;
                }
                foreach (var kv in a.Pairs)
                {
                    if (kv.Value.Kind != JsonKind.Bool)
                    {
                        Error(
                            $"setServer {kv.Key}",
                            "bad-step",
                            "online is true or false, not " + kv.Value.Dump()
                        );
                        continue;
                    }
                    ServerStates.Of(w).SetOnline(kv.Key, kv.Value.Bool);
                }
            }
            else if (k == "setAlarm" || k == "setEquipment")
            {
                // {alarm: state} / {equipment: {MODE / STATE / DRMODE: value}} (LiveState)
                if (a.Kind != JsonKind.Object)
                {
                    Error(k, "bad-step", "the step's value must be an object, not " + a.Dump());
                    return;
                }
                var (_, problems) =
                    k == "setAlarm" ? LiveState.SetAlarms(w, a, "scenario") : LiveState.SetEquipment(w, a);
                foreach (string pr in problems)
                {
                    int colon = pr.IndexOf(": ", StringComparison.Ordinal);
                    string what = colon > 0 ? pr.Substring(0, colon) : pr,
                        why = colon > 0 ? pr.Substring(colon + 2) : pr;
                    string code =
                        why == "no such alarm" ? "no-alarm"
                        : why == "no such equipment" ? "no-equipment"
                        : "bad-step";
                    Error($"{k} {what}", code, why);
                }
            }
            else if (k == "login")
            {
                if (w.Users.Login(a.Text) != 0)
                    Error("login", "no-user", $"no user {CitectText.Quote(a.Text)}");
            }
            else if (k == "logout")
                w.Users.Logout();
        }

        // ------------------------------------------------------------------ generators
        private void StartGenerator(string kind, JsonValue a)
        {
            var clock = _world.Clock;
            double t0 = clock.Now();
            if (a.Kind != JsonKind.Object)
                throw new FormatException("the step's value must be an object, not " + a.Dump());
            JsonValue nameV = a.Get("tag") ?? throw new FormatException("no 'tag'");
            string name = nameV.Text;
            double every = (a.Get("every") ?? JsonValue.Of(Scenario.ScanDefault)).ToNumber();
            if (!(every > 0))
                throw new FormatException("'every' must be above 0");
            JsonValue? until = a.Get("until"),
                seconds = a.Get("seconds");
            double? stop =
                until != null && !until.IsNull ? until.ToNumber()
                : seconds != null && !seconds.IsNull ? t0 + seconds.ToNumber()
                : (double?)null;
            Func<double, double> value;
            if (kind == "ramp")
            {
                double lo = (a.Get("from") ?? JsonValue.Of(0.0)).ToNumber(),
                    hi = (a.Get("to") ?? JsonValue.Of(0.0)).ToNumber();
                double dur = stop != null ? FloatOrder.Max(0.0, stop.Value - t0) : 0.0; // seconds or until
                stop = t0 + dur;
                value = t =>
                    dur <= 0 ? hi : lo + (hi - lo) * FloatOrder.Min(1.0, FloatOrder.Max(0.0, (t - t0) / dur));
            }
            else if (kind == "sine")
            {
                double lo = (a.Get("min") ?? JsonValue.Of(0.0)).ToNumber(),
                    hi = (a.Get("max") ?? JsonValue.Of(1.0)).ToNumber();
                double period = (a.Get("period") ?? JsonValue.Of(10.0)).ToNumber();
                if (!(period > 0))
                    throw new FormatException("'period' must be above 0");
                value = t =>
                {
                    double mid = (lo + hi) / 2.0,
                        amp = (hi - lo) / 2.0;
                    return mid + amp * CMath.Sin(2.0 * Math.PI * (t - t0) / period);
                };
            }
            else if (kind == "square")
            {
                // min for the first part of each period, max for the rest (duty = the max part):
                // a pulse, an on / off digital (min 0, max 1), a step that comes back
                double lo = (a.Get("min") ?? JsonValue.Of(0.0)).ToNumber(),
                    hi = (a.Get("max") ?? JsonValue.Of(1.0)).ToNumber();
                double period = (a.Get("period") ?? JsonValue.Of(2.0)).ToNumber();
                double duty = (a.Get("duty") ?? JsonValue.Of(0.5)).ToNumber();
                if (!(period > 0))
                    throw new FormatException("'period' must be above 0");
                if (!(duty >= 0 && duty <= 1))
                    throw new FormatException("'duty' must be 0 to 1");
                value = t =>
                {
                    double ph = (t - t0) % period;
                    if (ph < 0)
                        ph += period;
                    return ph < period * (1.0 - duty) - 1e-9 ? lo : hi;
                };
            }
            else
            {
                double lo = (a.Get("min") ?? JsonValue.Of(0.0)).ToNumber(),
                    hi = (a.Get("max") ?? JsonValue.Of(1.0)).ToNumber();
                var rnd = Mulberry32.FromSeed((a.Get("seed") ?? JsonValue.Of(0)).ToInteger());
                value = t => lo + (hi - lo) * (rnd.Next() / 4294967296.0);
            }
            Timer? timer = null;
            double? stopF = stop;

            void Tick()
            {
                double t = clock.Now();
                bool done = stopF != null && t >= stopF.Value - 1e-9;
                SetTag(name, JsonValue.Of(value(done && kind == "ramp" ? stopF!.Value : t)));
                if (done && timer != null)
                    timer.Cancel();
            }

            Tick();
            if (stopF == null || stopF.Value > t0 + 1e-9)
            {
                timer = clock.Every(every, Tick, label: $"{kind} {name}");
                _generators.Add(timer);
            }
        }

        // ------------------------------------------------------------------ expectations
        private void Expect(Step st)
        {
            var w = _world;
            double t = Now();
            if (st.Kind == "expect")
            {
                foreach (var kv in st.Arg.Pairs)
                {
                    string name = kv.Key;
                    JsonValue spec = kv.Value;
                    JsonValue want = spec;
                    double tol = 0.0;
                    JsonValue? wq = null;
                    if (spec.Kind == JsonKind.Object)
                    {
                        want = spec.Get("value") ?? JsonValue.Null;
                        tol = (spec.Get("tolerance") ?? JsonValue.Of(0.0)).ToNumber();
                        wq = spec.Get("quality");
                        if (wq != null && wq.IsNull)
                            wq = null;
                    }
                    Value got = w.Tags.Get(name);
                    bool found = w.Tags.Resolve(name).Found;
                    JsonValue gotJ = JsonValue.From(WorldValues.Payload(got));
                    bool ok = found && (want.IsNull || Matches(gotJ, want, tol));
                    if (wq != null)
                        ok = ok && QualityMatches(got.Q, wq);
                    Record(
                        new ExpectResult
                        {
                            T = t,
                            Target = name,
                            Want = spec,
                            Got = found ? gotJ : JsonValue.Null,
                            Ok = ok,
                        },
                        Quality.Name(got.Q)
                    );
                }
            }
            else if (st.Kind == "expectPage")
            {
                JsonValue spec = st.Arg;
                string? gotPage;
                try
                {
                    gotPage = _engine.Page((long)(spec.Get("window") ?? JsonValue.Of(0)).ToInteger());
                }
                catch (NotSimulatedException e)
                {
                    Skip($"expectPage {Str(spec.Get("name"))}", e.Message);
                    return;
                }
                bool ok =
                    CitectText.Lower(gotPage ?? "")
                    == CitectText.Lower((spec.Get("name") ?? JsonValue.Of("")).Text);
                Record(
                    new ExpectResult
                    {
                        T = t,
                        Target = "window " + Str(spec.Get("window") ?? JsonValue.Of(0)),
                        Want = spec.Get("name") ?? JsonValue.Null,
                        Got = JsonValue.Of(gotPage),
                        Ok = ok,
                        Kind = "expectPage",
                    }
                );
            }
            else if (st.Kind == "expectAlarm")
            {
                foreach (var kv in st.Arg.Pairs)
                {
                    string tag = kv.Key;
                    JsonValue spec = kv.Value;
                    var a = w.Alarms.Get(tag);
                    if (a == null)
                    {
                        Record(
                            new ExpectResult
                            {
                                T = t,
                                Target = tag,
                                Want = spec,
                                Got = JsonValue.Null,
                                Ok = false,
                                Kind = "expectAlarm",
                            }
                        );
                        continue;
                    }
                    var s = w.Alarms.State(a);
                    var got = JsonValue.NewObject();
                    got.Set("on", JsonValue.Of(s.On ? 1 : 0));
                    got.Set("ack", JsonValue.Of(s.Ack ? 1 : 0));
                    got.Set("disabled", JsonValue.Of(s.Disabled ? 1 : 0));
                    got.Set("state", JsonValue.Of(w.Alarms.StateValue(a)));
                    if (spec.Kind == JsonKind.String)
                        spec = AlarmWord(CitectText.Lower(spec.Str));
                    bool ok = true;
                    foreach (var kv2 in spec.Pairs)
                    {
                        var have = got.Get(kv2.Key) ?? JsonValue.Of(-1);
                        if (have.ToInteger() != kv2.Value.ToInteger())
                        {
                            ok = false;
                            break;
                        }
                    }
                    Record(
                        new ExpectResult
                        {
                            T = t,
                            Target = tag,
                            Want = spec,
                            Got = got,
                            Ok = ok,
                            Kind = "expectAlarm",
                        }
                    );
                }
            }
        }

        private static JsonValue AlarmWord(string word)
        {
            var o = JsonValue.NewObject();
            switch (word)
            {
                case "off":
                    o.Set("on", JsonValue.Of(0));
                    break;
                case "ack":
                    o.Set("ack", JsonValue.Of(1));
                    break;
                case "unack":
                    o.Set("ack", JsonValue.Of(0));
                    break;
                case "disabled":
                    o.Set("disabled", JsonValue.Of(1));
                    break;
                default:
                    o.Set("on", JsonValue.Of(1));
                    break; // 'on' and anything unknown
            }
            return o;
        }

        private void Record(ExpectResult r, string? quality = null)
        {
            Result.Expects.Add(r);
            if (_trace == null)
                return;
            var extra = new List<(string, object?)>();
            if (r.Kind != "expect")
                extra.Add(("kind", r.Kind));
            if (!string.IsNullOrEmpty(quality) && quality != "good")
                extra.Add(("quality", quality));
            _trace.Expect(r.Target, r.Want, r.Got, r.Ok, r.T, extra.ToArray());
        }

        /// <summary>'good' / 'bad' / 'uncertain' compare the general part; any other name or number
        /// the whole quality.</summary>
        internal static bool QualityMatches(ulong q, JsonValue want)
        {
            if (want.Kind == JsonKind.String)
            {
                string w = CitectText.Lower(CitectText.Trim(want.Str));
                if (w == "good")
                    return Quality.General(q) == Quality.Good;
                if (w == "bad")
                    return Quality.General(q) == Quality.Bad;
                if (w == "uncertain")
                    return Quality.General(q) == Quality.Uncertain;
            }
            return Quality.Parse(want) == q;
        }

        internal static bool Matches(JsonValue got, JsonValue want, double tol)
        {
            if (want.Kind == JsonKind.Object)
                return false;
            bool gotNum = got.Kind == JsonKind.Int || got.Kind == JsonKind.Float || got.Kind == JsonKind.Bool;
            bool wantNum = want.Kind == JsonKind.Int || want.Kind == JsonKind.Float;
            if (gotNum && wantNum)
            {
                double w = want.ToNumber();
                return Math.Abs(got.ToNumber() - w) <= tol + 1e-9 * FloatOrder.Max(1.0, Math.Abs(w));
            }
            if (want.Kind == JsonKind.Bool)
                return got.IsSet == want.Bool;
            if (gotNum && want.Kind == JsonKind.String)
            {
                if (!FloatText.TryParseNumber(want.Str, out double w))
                    return false;
                return Math.Abs(got.ToNumber() - w) <= tol;
            }
            return got.Text == want.Text;
        }

        /// <summary>Load (a path / JSON / Scenario) and run a scenario on a world.</summary>
        public static ScenarioResult RunScenario(
            World world,
            object scenario,
            IScenarioEngine? engine = null,
            TraceWriter? trace = null
        )
        {
            Scenario sc = scenario switch
            {
                string path => Scenario.Load(path),
                JsonValue j => Scenario.FromJson(j),
                Scenario s => s,
                _ => throw new ArgumentException("a scenario path, JSON or Scenario"),
            };
            return new ScenarioRunner(world, sc, engine, trace).Run();
        }
    }
}
