using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CicodeSim.World;
using Xunit;
using static CicodeSim.Tests.World.WorldFixture;
using JsonValue = CicodeSim.Values.JsonValue;
using Timer = CicodeSim.World.Timer;
using WorldT = CicodeSim.World.World;

namespace CicodeSim.Tests.World
{
    /// <summary>The simulated clock, scenarios
    /// (ctsim-scenario/0) and traces (ctsim-trace/0) on a synthetic install.</summary>
    [Collection("World")]
    public sealed partial class WorldScenarioTests : IDisposable
    {
        private readonly TempDir _tmp = new TempDir();

        public void Dispose() => _tmp.Dispose();

        // ------------------------------------------------------------------ the clock
        [Fact]
        public void ClockFiletimeAndStart()
        {
            var c = new SimClock("2026-10-01T08:00:00Z");
            Assert.Equal(1790841600.0, c.StartUnix);
            Assert.Equal(1790841600.0, SimClock.UnixFromFiletime((long)c.NowFiletime()));
            Assert.Equal((ulong)(1790841600L * 10_000_000 + SimClock.FiletimeUnixOffset), c.NowFiletime());
            Assert.Equal(1790841600L, c.Time32());
            Assert.Equal(SimClock.MonotonicOriginMs, c.MonotonicMs());
            c.Advance(1.25);
            Assert.Equal(1.25, c.Now());
            Assert.Equal(SimClock.MonotonicOriginMs + 1250, c.MonotonicMs());
            Assert.Equal(5.0, SimClock.ParseStart(5.0));
            Assert.Equal(1790841600.0, SimClock.ParseStart("2026-10-01T10:00:00+02:00"));
        }

        [Fact]
        public void ClockTimersInOrder()
        {
            var c = new SimClock(0);
            var log = new List<string>();
            c.CallAt(2.0, () => log.Add("b"));
            c.CallAt(1.0, () => log.Add("a"));
            c.CallAt(2.0, () => log.Add("c")); // same time: the order they were set
            var rep = c.Every(0.75, () =>
                log.Add("tick " + c.Now().ToString(System.Globalization.CultureInfo.InvariantCulture))
            );
            var gone = c.CallLater(1.5, () => log.Add("never"));
            gone.Cancel();
            Assert.Equal(0.75, c.NextDue());
            int fired = c.Advance(2.0);
            Assert.Equal(new[] { "tick 0.75", "a", "tick 1.5", "b", "c" }, log);
            Assert.Equal(5, fired);
            rep.Cancel();
            c.Advance(5);
            Assert.Equal(5, log.Count);
            Assert.Equal(0, c.Pending());
            c.CallAt(1.0, () => log.Add("late")); // a past time fires at the next move
            c.Advance(0);
            Assert.Equal("late", log[log.Count - 1]);
            Assert.Throws<ArgumentException>(() => c.Advance(-1));
            var seen = new List<double>();
            c.CallLater(1.0, () => seen.Add(c.Now()));
            c.Advance(3.0);
            Assert.Equal(new[] { c.Now() - 2.0 }, seen); // a timer runs at ITS time
        }

        [Fact]
        public void ClockModesWithAFakeWall()
        {
            double wall = 100.0;
            var c = new SimClock(0, mode: SimClock.Paused, wall: () => wall, stepSize: 0.5);
            wall += 10;
            Assert.Equal(0, c.Poll());
            Assert.Equal(0.0, c.Now()); // paused: wall time is ignored
            c.Step(3);
            Assert.Equal(1.5, c.Now());
            c.SetMode(SimClock.Stepped);
            c.Step();
            Assert.Equal(2.0, c.Now());
            c.RunMode();
            wall += 2.0;
            c.Poll();
            Assert.Equal(4.0, c.Now(), 9);
            c.SetSpeed(10);
            wall += 1.0;
            c.Poll();
            Assert.Equal(14.0, c.Now(), 9);
            c.CallAt(20.0, () => { });
            Assert.Equal(0.6, c.WallSecondsUntilNext()!.Value, 9);
            Assert.Throws<ArgumentException>(() => c.SetMode("fast"));
            Assert.Throws<ArgumentException>(() => c.SetSpeed(0));
        }

        // ------------------------------------------------------------------ scenarios
        public static JsonValue Scenario(string steps, string extra = "") =>
            JsonValue.Parse(
                "{\"format\": \"ctsim-scenario/0\", \"project\": \"Main\", \"steps\": "
                    + steps
                    + (extra.Length > 0 ? ", " + extra : "")
                    + "}"
            );

        [Fact]
        public void Mulberry32MatchesTheJavascriptReference()
        {
            // node: mulberry32(42) -> 2581720956, 1925393290, 3661312704; (0) -> 1144304738, 1416247
            var r = new Mulberry32(42);
            Assert.Equal(
                new uint[] { 2581720956, 1925393290, 3661312704 },
                new[] { r.Next(), r.Next(), r.Next() }
            );
            r = new Mulberry32(0);
            Assert.Equal(new uint[] { 1144304738, 1416247 }, new[] { r.Next(), r.Next() });
        }

        [Fact]
        public void ScenarioValidation()
        {
            Assert.Throws<FormatException>(() =>
                new CicodeSim.World.Scenario(JsonValue.Parse("{\"format\": \"other\"}"))
            );
            Assert.Throws<FormatException>(() =>
                CicodeSim.World.Scenario.FromJson(Scenario("[{\"at\": 1, \"set\": {}, \"expect\": {}}]"))
            );
            Assert.Throws<FormatException>(() =>
                CicodeSim.World.Scenario.FromJson(Scenario("[{\"set\": {}}]"))
            );
            var sc = CicodeSim.World.Scenario.FromJson(
                Scenario(
                    "[{\"at\": 2, \"set\": {\"a\": 1}}, {\"at\": 1, \"set\": {\"b\": 2}}, {\"at\": 1, \"expect\": {\"b\": 2}}]"
                )
            );
            Assert.Equal(
                new[] { (1.0, "set"), (1.0, "expect"), (2.0, "set") },
                sc.Steps.Select(s => (s.At, s.Kind))
            );
        }

        /// <summary>The scenario of test_scenario_runs_generators_expects_and_traces (also the
        /// differential's first scenario).</summary>
        public const string MainScenarioSteps =
            @"[
            {""at"": 1.0, ""set"": {""Motor_Speed"": 5}},
            {""at"": 1.0, ""expect"": {""Motor_Speed"": 5}},
            {""at"": 1.0, ""ramp"": {""tag"": ""Tank1_Temp"", ""from"": 0, ""to"": 130, ""seconds"": 10, ""every"": 1}},
            {""at"": 6.0, ""expect"": {""Tank1_Temp"": {""value"": 65, ""tolerance"": 0.01}}},
            {""at"": 6.0, ""expect"": {""Tank1_Temp"": 66}},
            {""at"": 7.0, ""sine"": {""tag"": ""Count_Long"", ""min"": 0, ""max"": 100, ""period"": 4, ""every"": 1, ""seconds"": 3}},
            {""at"": 8.0, ""expect"": {""Count_Long"": 100}},
            {""at"": 8.0, ""random"": {""tag"": ""Byte_Val"", ""min"": 0, ""max"": 255, ""every"": 0.5, ""seed"": 42}},
            {""at"": 9.0, ""quality"": {""Motor_Speed"": ""bad_comm_failure""}},
            {""at"": 9.0, ""expect"": {""Motor_Speed"": {""value"": 5, ""quality"": ""bad""}}},
            {""at"": 12.0, ""expect"": {""Tank1_Temp"": 130, ""Nope_Tag"": 1}},
            {""at"": 12.0, ""expectAlarm"": {""ALM_TEMP"": ""on""}},
            {""at"": 12.5, ""ack"": ""ALM_TEMP""},
            {""at"": 12.5, ""expectAlarm"": {""ALM_TEMP"": {""on"": 1, ""ack"": 1}}},
            {""at"": 13.0, ""call"": ""PageDisplay(\""Mixer\"")""},
            {""at"": 13.0, ""expectPage"": {""window"": 0, ""name"": ""Mixer""}}
        ]";

        public const string MainScenarioExtra =
            @"""tags"": {""Pump1_Run"": {""value"": 1}, ""Valve_A"": 1}, ""user"": {""name"": ""Engineer"", ""login"": true}, ""end"": 14.0, ""page"": {""window"": 0, ""name"": ""Overview""}";

        [Fact]
        public void ScenarioRunsGeneratorsExpectsAndTraces()
        {
            var w = MakeWorld(_tmp.Path);
            var buf = new StringWriter();
            w.AttachTrace(new TraceWriter(stream: buf, project: "Main", clock: w.Clock, scenario: "t"));
            w.Start();
            var res = w.RunScenario(Scenario(MainScenarioSteps, MainScenarioExtra));
            var got = res.Expects.Select(e => (e.Target, e.Ok)).ToArray();
            Assert.Equal(
                new[]
                {
                    ("Motor_Speed", true),
                    ("Tank1_Temp", true),
                    ("Tank1_Temp", false),
                    ("Count_Long", true),
                    ("Motor_Speed", true),
                    ("Tank1_Temp", true),
                    ("Nope_Tag", false),
                    ("ALM_TEMP", true),
                    ("ALM_TEMP", true),
                },
                got
            );
            Assert.Equal(14.0, res.End);
            Assert.Equal("Engineer", w.Users.Session.Name);
            Assert.Equal(
                new[] { "page", "call", "expectPage" },
                res.Skipped.Select(s => s.step.Split(' ')[0])
            );
            // the random draws are mulberry32(42) scaled to 0..255
            var r = new Mulberry32(42);
            Assert.Equal("scenario", w.Tags.State(w.Tags.Find("Byte_Val")!).By);
            // the trace
            var (header, events) = TraceWriter.ReadTrace(buf.ToString().Split('\n'));
            Assert.Equal(TraceWriter.Format, header.Get("format")!.Str);
            Assert.Equal("Main", header.Get("project")!.Str);
            var kinds = new HashSet<string>(events.Select(e => e.Get("ev")!.Str));
            Assert.True(new[] { "tagWrite", "expect", "alarm", "skip", "scenario" }.All(kinds.Contains));
            var writes = events
                .Where(e => e.Get("ev")!.Str == "tagWrite" && e.Get("tag")!.Str == "Byte_Val")
                .ToList();
            long r1 = (long)Math.Round(255 * (double)r.Next() / 4294967296.0),
                r2 = (long)Math.Round(255 * (double)r.Next() / 4294967296.0);
            Assert.Equal(new[] { r1, r2 }, writes.Take(2).Select(e => e.Get("value")!.Long));
            Assert.All(writes, e => Assert.Equal("scenario", e.Get("by")!.Str));
            var ramp = events
                .Where(e => e.Get("ev")!.Str == "tagWrite" && e.Get("tag")!.Str == "Tank1_Temp")
                .Select(e => (e.Get("t")!.Double, e.Get("value")!.Double))
                .ToList();
            Assert.Equal((1.0, 0.0), ramp[0]);
            Assert.Equal((6.0, 65.0), ramp[5]);
            Assert.Equal((11.0, 130.0), ramp[ramp.Count - 1]);
            var sine = events
                .Where(e => e.Get("ev")!.Str == "tagWrite" && e.Get("tag")!.Str == "Count_Long")
                .Select(e => e.Get("value")!.Long)
                .ToArray();
            Assert.Equal(new long[] { 50, 100, 50, 0 }, sine);
            var bad = events.Where(e => e.Get("ev")!.Str == "tagWrite" && e.Get("quality") != null).ToList();
            Assert.Equal("Motor_Speed", bad[0].Get("tag")!.Str);
            Assert.Equal("bad_comm_failure", bad[0].Get("quality")!.Str);
            Assert.Equal(
                Enumerable.Range(1, events.Count).Select(i => (long)i),
                events.Select(e => e.Get("n")!.Long)
            );
            var alarmEvents = events
                .Where(e => e.Get("ev")!.Str == "alarm")
                .Select(e => (e.Get("tag")!.Str, e.Get("event")!.Str))
                .ToList();
            Assert.Contains(("ALM_TEMP", "on"), alarmEvents);
            Assert.Contains(("ALM_TEMP", "ack"), alarmEvents);
        }

        /// <summary>An engine that does pages and calls (what the animator / VM will do).</summary>
        private sealed class FakeEngine : IScenarioEngine
        {
            private readonly WorldT _w;
            public readonly Dictionary<long, string> Pages = new Dictionary<long, string>();
            public readonly List<(double, string)> Calls = new List<(double, string)>();
            public int Scans;

            public FakeEngine(WorldT w)
            {
                _w = w;
            }

            public int SetTag(string name, JsonValue? value, JsonValue? quality = null) =>
                _w.Tags.Set(name, value, quality, by: "scenario");

            public object? Input(
                long window,
                long an,
                JsonValue kind,
                JsonValue? key,
                JsonValue? value,
                JsonValue? x,
                JsonValue? y
            ) => throw new NotSimulatedException("no inputs here");

            public object? Call(string expr)
            {
                Calls.Add((_w.Clock.Now(), expr));
                if (expr.StartsWith("PageDisplay", StringComparison.Ordinal))
                    Pages[0] = expr.Split('"')[1];
                return null;
            }

            public object? OpenPage(long window, JsonValue name)
            {
                Pages[window] = name.Str;
                return null;
            }

            public string? Page(long window) => Pages.TryGetValue(window, out var p) ? p : null;

            public void Scan() => Scans++;
        }

        [Fact]
        public void ScenarioWithAnEngine()
        {
            var w = MakeWorld(_tmp.Path);
            var eng = new FakeEngine(w);
            var res = new ScenarioRunner(
                w,
                CicodeSim.World.Scenario.FromJson(
                    Scenario(
                        @"[
                {""at"": 0.0, ""expectPage"": {""window"": 0, ""name"": ""overview""}},
                {""at"": 2.5, ""call"": ""PageDisplay(\""Mixer\"")""},
                {""at"": 2.5, ""expectPage"": {""window"": 0, ""name"": ""Mixer""}},
                {""at"": 3.0, ""input"": {""window"": 0, ""an"": 12, ""kind"": ""click""}}
            ]",
                        @"""page"": {""window"": 0, ""name"": ""Overview""}"
                    )
                ),
                engine: eng
            ).Run();
            Assert.Equal(new[] { true, true }, res.Expects.Select(e => e.Ok));
            Assert.Equal(new[] { (2.5, "PageDisplay(\"Mixer\")") }, eng.Calls);
            Assert.Equal(3, eng.Scans);
            Assert.NotEmpty(res.Skipped);
            Assert.Equal("input AN 12", res.Skipped[0].step);
        }

        // ------------------------------------------------------------------ heavy: R2 projects
        /// <summary>Every TAGREF of the example projects' _LIBRARY.RDB resolves. CTG_SKIP_HEAVY=1
        /// skips it.</summary>
        [CicodeSim.Tests.Vm.HeavyFact]
        public void CompiledTagrefsResolveOnTheExampleProjects()
        {
            var res = new Dictionary<string, JsonValue>();
            foreach (string p in WorldCheck.DefaultProjects)
                res[p] = WorldCheck.CheckOne(p, "self", R2);
            Assert.Equal(559, res["ExampleSA"]["tagrefs"].Long);
            Assert.Equal(0, res["ExampleSA"]["unresolved"].Count);
            Assert.Equal(615, res["Example"]["tagrefs"].Long);
            Assert.Equal(0, res["Example"]["unresolved"].Count);
            WorldT.ClearCaches();
        }

        [Fact]
        public void TraceCompareAndJsonable()
        {
            string p = Path.Combine(_tmp.Path, "a.jsonl");
            var tw = new TraceWriter(p, project: "X", engine: "test 1");
            tw.TagWrite("A", 1.0, "cicode", t: 0.0);
            tw.TagWrite("A", double.PositiveInfinity, "cicode", t: 0.5);
            tw.Page(0, "Mixer", "entry", t: 1.0);
            tw.Close();
            var (header, a) = TraceWriter.ReadTrace(p);
            Assert.Equal("test 1", header.Get("engine")!.Str);
            Assert.Equal("inf", a[1].Get("value")!.Str);
            var b = a.Select(e => JsonValue.Parse(e.Dump())).ToList();
            b[0].Set("t", JsonValue.Of(0.2));
            Assert.Empty(TraceWriter.Compare(a, b)); // within one scan
            b[0].Set("t", JsonValue.Of(0.6));
            b[2].Set("name", JsonValue.Of("Other"));
            var diff = TraceWriter.Compare(a, b);
            Assert.Contains(diff, d => d.Contains("t 0.0 vs 0.6"));
            Assert.Contains(diff, d => d.Contains("name"));
            Assert.Contains(TraceWriter.Compare(a, b.Take(1).ToList()), d => d.Contains("only in the first"));
            Assert.Equal(2, tw.Since(1).Count);
            Assert.Equal(3, tw.Events().Count);
        }
    }
}
