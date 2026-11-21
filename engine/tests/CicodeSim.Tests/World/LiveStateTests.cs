using System;
using System.Collections.Generic;
using System.Linq;
using CicodeSim.World;
using Xunit;
using static CicodeSim.Tests.World.WorldFixture;
using JsonValue = CicodeSim.Values.JsonValue;
using WorldT = CicodeSim.World.World;

namespace CicodeSim.Tests.World
{
    /// <summary>Alarm states and equipment runtime fields set directly (AlarmDb.SetState,
    /// EquipDb.SetRuntime, LiveState): a live page case's frame, a scenario's setAlarm /
    /// setEquipment.</summary>
    [Collection("World")]
    public sealed class LiveStateTests : IDisposable
    {
        private readonly TempDir _tmp = new TempDir();
        private readonly WorldT _w;

        public LiveStateTests()
        {
            _w = MakeWorld(_tmp.Path);
            _w.Start();
        }

        public void Dispose() => _tmp.Dispose();

        private AlarmState St(string tag) => _w.Alarms.State(_w.Alarms.Get(tag)!);

        private AlarmSet Spec(string json) => AlarmSet.FromJson(JsonValue.Parse(json), _w.Clock);

        [Fact]
        public void AnOffUnacknowledgedAlarmHoldsAgainstItsTags()
        {
            // live: TopMilk_AG01_PV_HH OFF unacknowledged (State 256) while its tag reads 0 (the
            // alarm server keeps its alarms across restarts); the engine's scan leaves it so
            _w.Tags.Set("Pump1_Run", 0);
            Assert.Equal(
                0,
                _w.Alarms.SetState(
                    "ALM_PUMP",
                    Spec("{\"on\": 0, \"ack\": 0, \"disabled\": 0, \"state\": 256}")
                )
            );
            _w.Clock.AdvanceTo(1.0); // two alarm scans
            var st = St("ALM_PUMP");
            Assert.False(st.On);
            Assert.False(st.Ack);
            Assert.True(st.InList);
            Assert.Equal(256, _w.Alarms.StateValue(_w.Alarms.Get("ALM_PUMP")!));
            Assert.Equal(1, _w.Alarms.Count(0, "TAG=ALM_PUMP"));
            Assert.Equal(1, _w.Alarms.HighestPriority(0)); // category 1, priority 1
            // the alarm's input changes: the scan takes it on from there
            _w.Tags.Set("Pump1_Run", 1);
            _w.Clock.AdvanceTo(1.5);
            Assert.True(St("ALM_PUMP").On);
            Assert.Equal(257, _w.Alarms.StateValue(_w.Alarms.Get("ALM_PUMP")!));
        }

        [Fact]
        public void TheStateValueGivesEnumerationAndMasks()
        {
            var a = _w.Alarms;
            Assert.Equal(0, a.SetState("ALM_TEMP", Spec("{\"state\": 270}"))); // 14 HH + 256
            var st = St("ALM_TEMP");
            Assert.True(st.On);
            Assert.False(st.Ack);
            Assert.Equal("HH", st.Sub);
            Assert.Equal(1, a.PropertyValue(a.Get("ALM_TEMP")!, "hh").I);
            Assert.Equal(0, a.SetState("ALM_NOTPUMP", Spec("{\"state\": 512}")));
            Assert.True(St("ALM_NOTPUMP").Disabled);
            Assert.False(St("ALM_NOTPUMP").InList);
            Assert.Equal(1, a.Count(3));
            Assert.Equal(512, a.StateValue(a.Get("ALM_NOTPUMP")!));
            // a multi-digital alarm: state 4 = C00, ALMSTAT4 = 1 (an alarm state)
            Assert.Equal(0, a.SetState("ALM_MULTI", Spec("{\"state\": 260}")));
            Assert.True(St("ALM_MULTI").On);
            Assert.Equal(4, St("ALM_MULTI").State);
            Assert.Equal(424, a.SetState("NO_SUCH_ALARM", Spec("\"on\"")));
            Assert.Equal(0, a.SetState("Plant.Tank1.PumpAlarm", Spec("\"on_ack\""))); // by Equipment.Item
            Assert.True(St("ALM_PUMP").On && St("ALM_PUMP").Ack);
        }

        [Fact]
        public void WordsAndTimes()
        {
            var a = _w.Alarms;
            var def = a.Get("ALM_PUMP")!;
            Assert.Equal(
                0,
                a.SetState(
                    "ALM_PUMP",
                    Spec("{\"on\": 1, \"ack\": 0, \"onTime\": \"2026-10-01T07:59:00Z\", \"time\": 2}")
                )
            );
            Assert.Equal(-60.0, St("ALM_PUMP").OnTime, 6);
            Assert.Equal(2.0, St("ALM_PUMP").Time, 6);
            // OnTime as Unix seconds (the start is 2026-10-01T08:00:00Z)
            Assert.Equal(1790841540L, a.PropertyValue(def, "ontime").I);
            var j = LiveState.AlarmJson(_w, def);
            Assert.Equal(1, j.Get("on")!.ToInteger());
            Assert.Equal("2026-10-01T07:59:00.000Z", j.Get("onTime")!.Str);
            a.SetState("ALM_PUMP", Spec("\"off_unack\""));
            Assert.Equal(256, a.StateValue(def));
            a.SetState("ALM_PUMP", Spec("\"off\""));
            Assert.Equal(0, a.StateValue(def));
            Assert.False(St("ALM_PUMP").InList);
            a.SetState("ALM_PUMP", Spec("\"disabled\""));
            Assert.Equal(512, a.StateValue(def));
            Assert.Throws<FormatException>(() => Spec("\"maybe\""));
            Assert.Throws<FormatException>(() => Spec("{\"on\": 1, \"colour\": 2}"));
            Assert.Throws<FormatException>(() => Spec("{\"on\": \"yes\"}"));
            Assert.Throws<FormatException>(() => Spec("[1]"));
        }

        [Fact]
        public void AFrameSetsItsAlarmsAndEveryOtherCheckedOneNormal()
        {
            // a page case frame lists the alarms that are not normal; the others it checked are
            // off, acknowledged and enabled, whatever the engine's own scan would make of them
            _w.Tags.Set("Pump1_Run", 1);
            _w.Clock.AdvanceTo(0.5);
            Assert.True(St("ALM_PUMP").On);
            var frame = JsonValue.Parse(
                "{\"ALM_TEMP\": {\"on\": 0, \"ack\": 0, \"disabled\": 0, \"state\": 256},"
                    + " \"ALM_GONE\": {\"error\": 268435889}}"
            );
            var (n, problems) = LiveState.SetAlarms(
                _w,
                frame,
                "live",
                new[] { "ALM_PUMP", "ALM_TEMP", "ALM_AND" }
            );
            Assert.Equal(3, n);
            Assert.Empty(problems);
            Assert.False(St("ALM_PUMP").On);
            Assert.True(St("ALM_PUMP").Ack);
            Assert.Equal(256, _w.Alarms.StateValue(_w.Alarms.Get("ALM_TEMP")!));
            _w.Clock.AdvanceTo(1.0);
            Assert.False(St("ALM_PUMP").On); // not evaluated again: its tag did not change
            var (_, p2) = LiveState.SetAlarms(
                _w,
                JsonValue.Parse("{\"NOPE\": \"on\", \"ALM_PUMP\": {\"on\": [1]}}"),
                "live"
            );
            Assert.Equal(new[] { "NOPE: no such alarm" }, p2.Take(1));
            Assert.Equal(2, p2.Count);
        }

        [Fact]
        public void EnablingASetDisabledAlarmEvaluatesItFromItsTags()
        {
            _w.Tags.Set("Pump1_Run", 1);
            _w.Clock.AdvanceTo(0.5);
            Assert.True(St("ALM_PUMP").On);
            // disabled: off, enumeration 0, as AlarmDisableTag leaves it
            _w.Alarms.SetState("ALM_PUMP", Spec("\"disabled\""));
            Assert.Equal(512, _w.Alarms.StateValue(_w.Alarms.Get("ALM_PUMP")!) & ~256);
            Assert.False(St("ALM_PUMP").On);
            // enabled with nothing else said: the next scan takes it from its tag (ON again),
            // as AlarmEnableTag does; its tag did not change meanwhile
            _w.Alarms.SetState("ALM_PUMP", Spec("{\"disabled\": 0}"));
            Assert.False(St("ALM_PUMP").On);
            _w.Clock.AdvanceTo(1.0);
            Assert.True(St("ALM_PUMP").On);
            // enabled and set off: it holds
            _w.Alarms.SetState("ALM_PUMP", Spec("\"disabled\""));
            _w.Alarms.SetState("ALM_PUMP", Spec("\"off\""));
            _w.Clock.AdvanceTo(1.5);
            Assert.False(St("ALM_PUMP").On);
            Assert.Throws<FormatException>(() => Spec("{\"state\": 1e30}"));
        }

        [Fact]
        public void TheAlarmSummaryFollowsASetState()
        {
            // an alarm list of the summary types reads the event log's summary: a set alarm that
            // is listed has an entry with its own times
            var log = CicodeSim.Builtins.AlarmEventLog.Attach(_w);
            _w.Alarms.SetState("ALM_PUMP", Spec("{\"state\": 256, \"onTime\": 1.5, \"time\": 2.5}"));
            var e = log.Summary.Single(x => x.Def.Tag == "ALM_PUMP");
            Assert.Equal((1.5, 2.5, -1.0), (e.OnT, e.OffT, e.AckT));
            Assert.Equal("set", log.Events[log.Events.Count - 1].Event);
            _w.Clock.AdvanceTo(3.0);
            _w.Alarms.SetState("ALM_PUMP", Spec("\"off\""));
            Assert.Equal(3.0, e.AckT);
            Assert.Single(log.Summary, x => x.Def.Tag == "ALM_PUMP");
            // ALM_TEMP went ON at the first scan (Tank1_Temp 0 is below LOWLOW): set off, its
            // entry closes
            var temp = log.Summary.Single(x => x.Def.Tag == "ALM_TEMP");
            Assert.Equal((-1.0, -1.0), (temp.OffT, temp.AckT));
            _w.Alarms.SetState("ALM_TEMP", Spec("\"off\""));
            Assert.Equal((3.0, 3.0), (temp.OffT, temp.AckT));
            _w.Alarms.SetState("ALM_AND", Spec("\"off\"")); // never listed: no entry
            Assert.DoesNotContain(log.Summary, x => x.Def.Tag == "ALM_AND");
        }

        [Fact]
        public void EquipmentRuntimeFields()
        {
            var eq = _w.Equipment;
            var t1 = eq.Get("Plant.Tank1")!;
            // not SCHEDULED: 'Unknown' for all three (live); scheduled: MODE / DRMODE 0, STATE
            // the DEFSTATE (no scheduler)
            Assert.Equal(
                new[] { ("MODE", "Unknown"), ("STATE", "Unknown"), ("DRMODE", "Unknown") },
                eq.RuntimeOf(t1)
            );
            Assert.Equal(
                new[] { ("MODE", "0"), ("STATE", "Idle"), ("DRMODE", "0") },
                eq.RuntimeOf(eq.Get("Plant.Tank2")!)
            );
            var (n, problems) = LiveState.SetEquipment(
                _w,
                JsonValue.Parse(
                    "{\"Plant.Tank1\": {\"MODE\": \"0\", \"STATE\": \"Active\", \"DRMODE\": \"0\"},"
                        + " \"Plant.Tank2\": {\"mode\": 2, \"STATE\": {\"error\": 5}},"
                        + " \"Plant.Nope\": {\"STATE\": \"x\"}, \"Plant\": {\"PAGE\": \"y\"}}"
                )
            );
            Assert.Equal(4, n);
            Assert.Equal("Active", eq.Runtime(t1, "STATE"));
            Assert.Equal("2", eq.Runtime(eq.Get("Plant.Tank2")!, "MODE"));
            Assert.Equal(
                new[]
                {
                    "Plant.Nope: no such equipment",
                    "Plant.PAGE: not a runtime field (MODE, STATE, DRMODE)",
                },
                problems
            );
            Assert.Equal(new[] { ("MODE", "0"), ("STATE", "Active"), ("DRMODE", "0") }, eq.RuntimeOf(t1));
            Assert.Equal(274, eq.SetRuntime("Plant.Tank1", "TYPE", "x"));
            Assert.Equal(289, eq.SetRuntime("Plant.Nope", "MODE", "x"));
        }

        [Fact]
        public void ScenarioStepsSetAlarmAndSetEquipment()
        {
            var res = _w.RunScenario(
                WorldScenarioTests.Scenario(
                    @"[
                {""at"": 1.0, ""setAlarm"": {""ALM_PUMP"": ""on"", ""ALM_TEMP"": {""state"": 268}}},
                {""at"": 1.0, ""expectAlarm"": {""ALM_PUMP"": {""on"": 1, ""ack"": 0}, ""ALM_TEMP"": {""state"": 268}}},
                {""at"": 2.0, ""setEquipment"": {""Plant.Tank1"": {""MODE"": ""2"", ""STATE"": ""Standby""}}},
                {""at"": 2.0, ""setAlarm"": {""NO_SUCH"": ""on"", ""ALM_AND"": {""ack"": ""x""}}},
                {""at"": 2.0, ""setEquipment"": {""Plant.Nope"": {""MODE"": ""2""}, ""Plant"": {""TYPE"": ""t""}}},
                {""at"": 2.0, ""setAlarm"": ""ALM_PUMP""}
            ]"
                )
            );
            Assert.True(res.Expects.All(e => e.Ok));
            Assert.Equal(2, res.Expects.Count);
            Assert.Equal("Standby", _w.Equipment.Runtime(_w.Equipment.Get("Plant.Tank1")!, "STATE"));
            Assert.Equal(
                new[] { "no-alarm", "bad-step", "no-equipment", "bad-step", "bad-step" },
                res.Errors.Select(e => e.code)
            );
            Assert.Equal("setAlarm NO_SUCH", res.Errors[0].step);
            Assert.Equal("setEquipment Plant.Nope", res.Errors[2].step);
        }

        [Fact]
        public void ScenarioStepSetServerTakesAServerOfflineAndBack()
        {
            var res = _w.RunScenario(
                WorldScenarioTests.Scenario(
                    @"[
                {""at"": 1.0, ""setServer"": {""IOServer1"": false, ""Other"": ""down""}}
            ]"
                )
            );
            Assert.False(CicodeSim.World.ServerStates.Of(_w).IsOnline("ioserver1", _w.Clock.Now()));
            Assert.Equal(new[] { "bad-step" }, res.Errors.Select(e => e.code));
            Assert.Equal("setServer Other", res.Errors[0].step);
            _w.RunScenario(
                WorldScenarioTests.Scenario(@"[{""at"": 0.5, ""setServer"": {""IOServer1"": true}}]")
            );
            Assert.True(CicodeSim.World.ServerStates.Of(_w).IsOnline("IOServer1", _w.Clock.Now()));
        }
    }
}
