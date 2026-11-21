using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.World;
using Xunit;
using static CicodeSim.Tests.World.WorldFixture;
using Quality = CicodeSim.World.Quality;
using WorldT = CicodeSim.World.World;

namespace CicodeSim.Tests.World
{
    /// <summary>Tag, clock, user and alarm rules of the world, one test
    /// each.</summary>
    [Collection("World")]
    public sealed class WorldReviewTests : IDisposable
    {
        private readonly TempDir _tmp = new TempDir();

        public void Dispose() => _tmp.Dispose();

        private WorldT NewWorld() => MakeWorld(_tmp.Path);

        // ------------------------------------------------------------------ tags
        [Fact]
        public void PartialRangesScaleEachEndOnItsOwn()
        {
            // REAL tags that store RAW_ZERO = ENG_ZERO = '0' and blank full scales: each end
            // defaults on its own (doc: 1064875), so they are not scaled
            var d = new TagDef("ZoneID", "variable", "p", "REAL", rawZero: 0.0, engZero: 0.0);
            Assert.False(d.Scaled);
            Assert.Equal((0.0, 3.4e38), d.RawRange());
            Assert.Equal("0.0", TagDb.EngJson(d, RawVal.Real(0.0)).Dump());
            d = new TagDef("R", "variable", "p", "REAL", engZero: 0.0); // only ENG_ZERO
            Assert.False(d.Scaled);
            d = new TagDef("E", "variable", "p", "REAL", engZero: 0.0, engFull: 1500.0);
            Assert.False(d.Scaled);
            Assert.Equal((0.0, 1500.0), d.CheckRange());
            // an integer tag keeps the doc's type-range default for a missing raw range
            d = new TagDef("I", "variable", "p", "INT", engZero: 0.0, engFull: 1100.0);
            Assert.True(d.Scaled);
            Assert.Equal((-32768.0, 32767.0), d.RawRange());
        }

        [Fact]
        public void StringTagAddressLengthIsNotAnArray()
        {
            // doc: 1064766: a STRING tag's 'V500[4]' is the registers of one string
            Assert.Empty(TagTypes.DimsFromAddress("400769[50]", "STRING"));
            Assert.Equal(new[] { 5 }, TagTypes.DimsFromAddress("V500[5]", "INT"));
            Assert.Equal(new[] { 10 }, TagTypes.DimsFromAddress("R3!A[10]", "REAL"));
        }

        [Fact]
        public void ReadTypesAreTheLoads()
        {
            // the runtime pushes integer kinds as LONG, unsigned as ULONG
            var t = NewWorld().Tags;
            foreach (string name in new[] { "Pump1_Run", "Byte_Val", "Count_Long", "Untyped", "LocInt" })
                Assert.Equal(Types.Long, t.Read(Ref(name), Task()).T);
            Assert.Equal(Types.Real, t.Read(Ref("Motor_Speed"), Task()).T);
            Assert.Equal(Types.String, t.Read(Ref("Name_Str"), Task()).T);
            Assert.Equal(Types.Real, t.Read(Ref("Tank1_Level"), Task()).T); // scaled INT
            Assert.Equal(Types.Long, t.Read(Ref("Tank1_Temp", element: "overridemode"), Task()).T);
            Assert.Equal(Types.ULong, new TagDef("U", "variable", "p", "ULONG").ValueType);
        }

        [Fact]
        public void StoreRefusesTypesItCannotWrite()
        {
            // the runtime writes REAL, LONG, STRING, ULONG, TIMESTAMP, QUALITY only
            var t = NewWorld().Tags;
            foreach (
                var bad in new[]
                {
                    Value.Int(Types.Int16, 1),
                    Value.Int(Types.Digital, 1),
                    Value.Int(Types.Object, 5),
                    WorldValues.Void,
                }
            )
                Assert.Equal(274, t.Write(Ref("Count_Long"), bad, Task()));
            Assert.Equal(0L, t.Get("Count_Long").I);
            Assert.Equal(0, t.Write(Ref("Count_Long"), Long(5), Task()));
            // the item check comes first, the tag lookup last
            Assert.Equal(274, t.Write(Ref("NoSuchTag", item: "q"), Real(1.0), Task()));
            Assert.Equal(274, t.Write(Ref("NoSuchTag"), Value.Int(Types.Object, 1), Task()));
            Assert.Equal(424, t.Write(Ref("NoSuchTag"), Long(1), Task()));
        }

        [Fact]
        public void NonFiniteWritesDoNotCrash()
        {
            var t = NewWorld().Tags;
            var nc = Task(TaskFlags.WriteLocal);
            Assert.Equal(434, t.Write(Ref("Count_Long"), Real(double.PositiveInfinity), nc));
            Assert.Equal(434, t.Write(Ref("Pump1_Run"), Real(double.NaN), nc));
            Assert.Equal(257, t.Write(Ref("Count_Long"), Real(double.PositiveInfinity), Task()));
            Assert.Equal(274, t.Write(Ref("Tank1_Temp", element: "overridemode"), Real(double.NaN), Task()));
            Assert.Equal(0, t.Write(Ref("Motor_Speed"), Real(double.PositiveInfinity), nc));
            Assert.Equal(TagTypes.Float32Max, t.Get("Motor_Speed").D);
        }

        [Fact]
        public void EmptyStringWritesZero()
        {
            // atol = 0, a NUL first character is no type mismatch
            var t = NewWorld().Tags;
            t.Write(Ref("Count_Long"), Long(7), Task());
            Assert.Equal(0, t.Write(Ref("Count_Long"), Str(""), Task()));
            Assert.Equal(0L, t.Get("Count_Long").I);
            Assert.Equal(0, t.Write(Ref("Count_Long"), Str("  "), Task()));
            Assert.Equal(433, t.Write(Ref("Count_Long"), Str("x"), Task()));
        }

        private WorldT WorldWithLocalReal()
        {
            string main = MakeInstall(_tmp.Path);
            string lib = Path.Combine(Path.GetDirectoryName(main)!, "Lib");
            WriteDbf(
                Path.Combine(lib, "locvar.dbf"),
                Locvar,
                new[]
                {
                    Row(("NAME", "LocReal"), ("TYPE", "REAL")),
                    Row(("NAME", "LocInt"), ("TYPE", "INT")),
                    Row(("NAME", "LocBig"), ("TYPE", "STRING"), ("ASIZE", "200,100")),
                }
            );
            WorldT.ClearCaches();
            return new WorldT(main, start: DefaultStart);
        }

        [Fact]
        public void LocalRealIsADouble()
        {
            // doc: 1064776 a local variable's REAL is 8 bytes, -1.7e308 .. 1.7e308
            var w = WorldWithLocalReal();
            var d = w.Tags.Find("LocReal")!;
            Assert.Equal("local", d.Kind);
            Assert.Equal(64, d.BitWidth);
            Assert.Equal("64", d.Info(11));
            Assert.Equal(0, w.Tags.Write(Ref("LocReal"), Real(0.1), Task()));
            Assert.Equal(0.1, w.Tags.Get("LocReal").D); // no 4-byte rounding
            Assert.Equal(0, w.Tags.Write(Ref("LocReal"), Real(1e300), Task()));
            Assert.Equal(1e300, w.Tags.Get("LocReal").D);
            // a variable tag's REAL stays a 4-byte float
            w.Tags.Write(Ref("Motor_Speed"), Real(0.1), Task());
            Assert.NotEqual(0.1, w.Tags.Get("Motor_Speed").D);
        }

        [Fact]
        public void ArrayElementWritesAreInPlace()
        {
            // An include project fills STRING arrays of 200 x 100 elements: a write never copies the whole array
            var w = WorldWithLocalReal();
            var tr = w.OpenTrace();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 20_000; i++)
                Assert.Equal(0, w.Tags.Write(Ref("LocBig"), Str("r" + i), Task(), index: i));
            Assert.True(sw.Elapsed.TotalSeconds < 5.0);
            Assert.Equal("r19999", w.Tags.Get("LocBig", index: 19_999).S);
            Assert.Equal("r7", w.Tags.Get("LocBig", element: "valid", index: 7).S);
            var last = tr.Events().Last();
            Assert.Equal(("LocBig[19999]", "r19999"), (last.Get("tag")!.Str, last.Get("value")!.Str));
        }

        [Fact]
        public void OverrideBitAndDynamicOverridePeek()
        {
            var t = NewWorld().Tags;
            t.Set("Tank1_Temp", 40.0);
            t.Write(Ref("Tank1_Temp", element: "overridemode"), Long(3), Task());
            t.Write(Ref("Tank1_Temp", element: "override"), Real(77.0), Task());
            // the Override element's quality has the override bit in an override mode (doc: 1064783)
            Assert.True(Quality.IsOverride(t.Read(Ref("Tank1_Temp", element: "override"), Task()).Q));
            Assert.Equal("77.0", t.Peek("Tank1_Temp").value.Dump());
            // mode 4 tracks Valid: what the alarm server reads too
            t.Write(Ref("Tank1_Temp", element: "overridemode"), Long(4), Task());
            t.Set("Tank1_Temp", 45.0);
            Assert.Equal(45.0, t.Read(Ref("Tank1_Temp"), Task()).D);
            Assert.Equal("45.0", t.Peek("Tank1_Temp").value.Dump());
            t.Write(Ref("Tank1_Temp", element: "overridemode"), Long(0), Task());
            Assert.False(Quality.IsOverride(t.Read(Ref("Tank1_Temp", element: "override"), Task()).Q));
        }

        [Fact]
        public void WritePendingStaysWhileAWriteIsOnItsWay()
        {
            var w = NewWorld();
            var t = w.Tags;
            var late = Task(TaskFlags.ScaleCheck); // WriteLocal off
            t.Write(Ref("Motor_Speed"), Real(1.0), late);
            w.Clock.Advance(0.1);
            t.Write(Ref("Motor_Speed"), Real(2.0), late);
            w.Clock.Advance(0.2); // the first one arrived
            Assert.Equal(1.0, t.Get("Motor_Speed").D);
            Assert.Equal(0x6L, t.Get("Motor_Speed", element: "status").I & 0x6); // ok + still pending
            w.Clock.Advance(0.1);
            Assert.Equal(2.0, t.Get("Motor_Speed").D);
            Assert.Equal(0x4L, t.Get("Motor_Speed", element: "status").I & 0x6);
        }

        [Fact]
        public void TagRefMemoIsByIdentity()
        {
            var w = NewWorld();
            var r = Ref("Tank1_Level");
            w.Tags.Read(r, Task());
            Assert.Same(w.Tags.ResolveRef(r), w.Tags.ResolveRef(r));
        }

        [Fact]
        public void UserSetRefusesItemsOtherThanV()
        {
            var t = NewWorld().Tags;
            Assert.Equal(274, t.Set("Tank1_Temp.q", 1));
            Assert.Equal(0.0, t.Get("Tank1_Temp").D);
            Assert.Equal(0, t.Set("Tank1_Temp.v", 12.0));
            Assert.Equal(12.0, t.Get("Tank1_Temp").D);
        }

        // ------------------------------------------------------------------ clock
        [Fact]
        public void FiletimeIsExactAtEveryMillisecond()
        {
            var c = new SimClock("2026-10-01T08:00:00Z");
            ulong b = c.NowFiletime();
            for (int ms = 1; ms < 100_000; ms += 7)
                Assert.Equal((ulong)ms * 10_000UL, c.FiletimeAt(ms / 1000.0) - b);
            c.Advance(0.001);
            Assert.Equal(10_000UL, c.NowFiletime() - b);
        }

        [Fact]
        public void PeriodicTimersDoNotDrift()
        {
            var c = new SimClock(0);
            var hits = new List<double>();
            c.Every(0.1, () => hits.Add(c.Now()));
            c.Advance(1000.0);
            Assert.Equal(10_000, hits.Count);
            Assert.Equal(1000.0, hits[hits.Count - 1], 9);
            var c2 = new SimClock(0); // 3 * 0.1 > 0.3 in floats: due times round
            var hits2 = new List<double>();
            c2.Every(0.1, () => hits2.Add(c2.Now()));
            c2.AdvanceTo(0.3);
            Assert.Equal(new[] { 0.1, 0.2, 0.3 }, hits2);
        }

        [Fact]
        public void ClockCountsWholeMillisecondsWithNoDrift()
        {
            // the clock counts integer milliseconds, so the waits the scheduler computes in
            // seconds (wake time - now) add up exactly
            var c = new SimClock("2026-10-01T08:00:00.3Z");
            for (int i = 0; i < 11; i++)
                c.Advance((c.Time32() + 1) - c.Unix()); // Sleep(1): wake at the next whole second
            Assert.Equal(10_700, c.NowMs);
            Assert.Equal(10.7, c.Now());
            var d = new SimClock(0);
            for (int i = 0; i < 110; i++)
            {
                double until = d.MonotonicMs() + 100; // SleepMS(100)
                d.Advance((until - d.MonotonicMs()) / 1000.0);
            }
            Assert.Equal(11.0, d.Now());
            for (int i = 0; i < 1000; i++)
                d.Advance(0.1);
            Assert.Equal(111.0, d.Now());
            Assert.Equal(SimClock.MonotonicOriginMs + 111_000, d.MonotonicMs());
            // seconds round to the nearest millisecond; a repeating period is at least 1 ms
            d.Advance(0.0004);
            Assert.Equal(111_000, d.NowMs);
            d.Advance(0.0015);
            Assert.Equal(111_002, d.NowMs);
            int n = 0;
            d.Every(0.0001, () => n++);
            d.AdvanceMs(10);
            Assert.Equal(10, n);
        }

        [Fact]
        public void ReentrantAdvanceNeverMovesTimeBack()
        {
            var c = new SimClock(0);
            c.CallAt(0.5, () => c.Advance(2.0));
            c.AdvanceTo(1.0);
            Assert.Equal(2.5, c.Now(), 9);
        }

        [Fact]
        public void SetStartOnlyBeforeTheClockRuns()
        {
            var c = new SimClock("2026-01-01T00:00:00Z");
            c.SetStart("2026-10-01T08:00:00Z");
            Assert.Equal(SimClock.ParseStart("2026-10-01T08:00:00Z"), c.StartUnix);
            c.Advance(1);
            Assert.Throws<InvalidOperationException>(() => c.SetStart(0));
        }

        [Fact]
        public void ScenarioAppliesItsClockStart()
        {
            var w = MakeWorld(_tmp.Path, start: "2020-01-01T00:00:00Z");
            var sc = JsonValue.Parse(
                @"{""format"": ""ctsim-scenario/0"", ""clock"": {""start"": ""2026-10-01T08:00:00Z""},
                ""steps"": [{""at"": 1.0, ""set"": {""Pump1_Run"": 1}}]}"
            );
            w.RunScenario(sc);
            Assert.Equal(SimClock.ParseStart("2026-10-01T08:00:00Z"), w.Clock.StartUnix);
            var st = w.Tags.State(w.Tags.Find("Pump1_Run")!);
            Assert.Equal(w.Clock.FiletimeAt(1.0), st.Field.T);
        }

        [Fact]
        public void BadGeneratorsAreReportedAndRampTakesUntil()
        {
            var w = NewWorld();
            var sc = JsonValue.Parse(
                @"{""format"": ""ctsim-scenario/0"", ""steps"": [
                {""at"": 0.0, ""sine"": {""tag"": ""Motor_Speed"", ""period"": 0}},
                {""at"": 0.0, ""random"": {""tag"": ""Motor_Speed"", ""every"": 0}},
                {""at"": 0.0, ""ramp"": {""tag"": ""Tank1_Temp"", ""from"": 0, ""to"": 10, ""until"": 1.0}},
                {""at"": 0.5, ""expect"": {""Tank1_Temp"": {""value"": 5.0, ""tolerance"": 0.01}}},
                {""at"": 1.0, ""expect"": {""Tank1_Temp"": 10.0}}]}"
            );
            var res = w.RunScenario(sc);
            Assert.Equal(2, res.Errors.Count);
            Assert.Empty(res.Failed);
            Assert.Equal(2, res.Passed);
        }

        // ------------------------------------------------------------------ users
        [Fact]
        public void SessionSerialChangesWithEveryLogin()
        {
            var u = NewWorld().Users;
            var seen = new HashSet<long> { u.Serial };
            for (int i = 0; i < 20; i++)
            {
                u.LoginAs("x", privileges: JsonValue.NewObject());
                seen.Add(u.Serial);
                u.Logout();
                seen.Add(u.Serial);
            }
            Assert.Equal(41, seen.Count);
        }

        [Fact]
        public void RoleAreasMayNameGroups()
        {
            // corpus: Example's roles have ACCESS 'Plantwide', a groups.dbf group of areas 1, 2; a
            // group may name another group (doc: 1095086)
            string main = MakeInstall(_tmp.Path);
            var groups = F(
                30,
                new[] { "NAME" }.Concat(Enumerable.Range(1, 10).Select(i => $"ASS{i}")).ToArray()
            );
            WriteDbf(
                Path.Combine(main, "groups.dbf"),
                groups,
                new[]
                {
                    Row(("NAME", "Plantwide"), ("ASS1", "1"), ("ASS2", "2"), ("ASS3", "Upper")),
                    Row(("NAME", "Upper"), ("ASS1", "10..12"), ("ASS2", "Plantwide")),
                }
            );
            WriteDbf(
                Path.Combine(main, "roles.dbf"),
                Roles,
                new[] { Row(("NAME", "Op"), ("ACCESS", "Plantwide"), ("PRIV_1", "Upper")) }
            );
            WriteDbf(
                Path.Combine(main, "users.dbf"),
                F(40, "NAME", "FULL_NAME", "ROLES", "TYPE"),
                new[] { Row(("NAME", "Op"), ("ROLES", "Op")) }
            );
            WorldT.ClearCaches();
            var w = new WorldT(main, start: 0);
            var role = w.Users.Roles()["op"];
            Assert.Equal(new HashSet<int> { 1, 2, 10, 11, 12 }, role.ViewAreas);
            Assert.Equal(new HashSet<int> { 1, 2, 10, 11, 12 }, role.AreaPrivs[1]);
            Assert.Equal(0, w.Users.Login("Op"));
            Assert.True(w.Users.CanView(11) && !w.Users.CanView(3));
        }

        // ------------------------------------------------------------------ alarms
        private WorldT Running()
        {
            var w = NewWorld();
            w.Start();
            return w;
        }

        [Fact]
        public void Count12CountsEveryOffAlarm()
        {
            var w = Running();
            var al = w.Alarms;
            w.Tags.Set("Pump1_Run", 1);
            w.Clock.AdvanceTo(0.5);
            int on = al.Count(11),
                configured = al.Count(4),
                disabled = al.Count(3);
            Assert.True(on > 0);
            Assert.Equal(configured - on - disabled, al.Count(12));
            Assert.True(al.Count(18) == 0 && 0 < al.Count(12)); // 12 is not 'unacknowledged OFF' (18)
            al.Disable("ALM_TEMP");
            Assert.Equal(al.Count(4) - al.Count(11) - al.Count(3), al.Count(12));
        }

        [Fact]
        public void PropertyWriteRefreshesCachedCounts()
        {
            var w = Running();
            w.Tags.Set("Pump1_Run", 1);
            w.Clock.AdvanceTo(0.5);
            Assert.Equal(0, w.Alarms.Count(0, "Category=9"));
            Assert.Equal(0, w.Tags.Write(Ref("ALM_PUMP.Category"), Long(9), Task()));
            Assert.Equal(1, w.Alarms.Count(0, "Category=9"));
        }

        [Fact]
        public void MultiDigitalStateWhileOff()
        {
            var w = Running();
            w.Tags.Set("Valve_B", 1); // state 2 = 0B0: ALMSTAT2 = 0 (not an alarm)
            w.Clock.AdvanceTo(0.5);
            var a = w.Alarms.Get("ALM_MULTI")!;
            Assert.False(w.Alarms.State(a).On);
            Assert.Equal(2L, w.Alarms.StateValue(a)); // doc: 1068486 '2 - State 0B0'
        }

        [Fact]
        public void AdvancedNameThatIsNotATagGoesToTheEvaluator()
        {
            var w = Running();
            var al = w.Alarms;
            var a = al.Get("ADV_SIMPLE")!;
            a.Expr = "SomeLabel"; // one name, but no tag of that name
            al.ResetCaches();
            var seen = new List<string>();
            al.SetEvaluator(
                (expr, alarm) =>
                {
                    seen.Add(expr);
                    return true;
                }
            );
            w.Clock.AdvanceTo(0.5);
            Assert.Contains("SomeLabel", seen);
            Assert.True(al.State(a).On);
        }

        [Fact]
        public void AlarmOnAtT0HasItsTimes()
        {
            var w = Running();
            var al = w.Alarms;
            w.Tags.Set("Pump1_Run", 1);
            al.Scan(); // a scenario's scan at t = 0
            var a = al.Get("ALM_PUMP")!;
            Assert.True(al.State(a).On);
            Assert.Equal(0.0, al.State(a).OnTime);
            long start = (long)w.Clock.StartUnix;
            var tk = Task();
            Assert.Equal(start, w.Tags.Read(Ref("ALM_PUMP.OnTime"), tk).I);
            Assert.Equal(start, w.Tags.Read(Ref("ALM_PUMP.Time"), tk).I);
            Assert.Equal(w.Clock.FiletimeAt(0.0), w.Tags.Read(Ref("ALM_PUMP.On"), tk).Ts);
        }

        [Fact]
        public void MillisecIsTheWallTimes()
        {
            var w = MakeWorld(_tmp.Path, start: 1_790_000_000.75); // a start with a fraction
            w.Start();
            w.Tags.Set("Pump1_Run", 1);
            w.Clock.AdvanceTo(0.5); // ON at t = 0.5 = .25 of the next second
            var tk = Task();
            Assert.Equal(250L, w.Tags.Read(Ref("ALM_PUMP.Millisec"), tk).I);
            Assert.Equal(1_790_000_001L, w.Tags.Read(Ref("ALM_PUMP.OnTime"), tk).I);
        }

        [Fact]
        public void LabelValuesWithLeadingZeros()
        {
            var w = NewWorld();
            Assert.Equal(8L, w.Unit.LabelValue("08"));
            Assert.Equal(-7L, w.Unit.LabelValue("-07"));
            Assert.Equal(31L, w.Unit.LabelValue("0x1F"));
            Assert.Null(w.Unit.LabelValue("1.5e400"));
        }
    }
}
