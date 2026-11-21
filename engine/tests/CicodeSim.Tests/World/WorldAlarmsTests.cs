using System;
using System.Collections.Generic;
using System.Linq;
using CicodeSim.Values;
using CicodeSim.World;
using Xunit;
using static CicodeSim.Tests.World.WorldFixture;
using WorldT = CicodeSim.World.World;

namespace CicodeSim.Tests.World
{
    /// <summary>The alarm server (delays,
    /// deadbands, acknowledge, counts, filters, properties as tags) and the simulated login.</summary>
    [Collection("World")]
    public sealed class WorldAlarmsTests : IDisposable
    {
        private readonly TempDir _tmp = new TempDir();
        private readonly WorldT _w;

        public WorldAlarmsTests()
        {
            _w = MakeWorld(_tmp.Path);
            _w.Start(); // [Alarm]ScanTime 500 ms from param.dbf
        }

        public void Dispose() => _tmp.Dispose();

        private void At(double t) => _w.Clock.AdvanceTo(t);

        private AlarmState St(string tag) => _w.Alarms.State(_w.Alarms.Get(tag)!);

        [Fact]
        public void Definitions()
        {
            var al = _w.Alarms;
            var c = al.Counts();
            Assert.Equal((3, 3, 3, 1), (c["digital"], c["analog"], c["advanced"], c["multi_digital"]));
            var a = al.Get("alm_and")!;
            Assert.Equal(("Pump1_Run", false), (a.Vars[0].Name, a.Vars[0].Negated));
            Assert.Equal(("Lib_Flag", false), (a.Vars[1].Name, a.Vars[1].Negated));
            Assert.Equal((3L, 5L, 2L, 3L), (a.Category, a.Area, a.Priv, al.Priority(a))); // labels resolved
            Assert.Equal(
                ("Pump1_Run", true),
                (al.Get("ALM_NOTPUMP")!.Vars[0].Name, al.Get("ALM_NOTPUMP")!.Vars[0].Negated)
            );
            Assert.Equal(5.0, al.Get("ALM_NOTPUMP")!.Delay);
            Assert.Equal("ALM_PUMP", al.Get("Plant.Tank1.PumpAlarm")!.Tag); // by Equip.Item
            Assert.Equal(3723.0, AlarmDb.ParseDelay("01:02:03"));
            Assert.Equal(0.0, AlarmDb.ParseDelay(""));
            Assert.Equal(0.5, _w.Alarms.ScanTime);
        }

        [Fact]
        public void ScanAndAcknowledgeAllInDefinitionOrder()
        {
            // the tags change in the reverse of the alarms' definition order: the scan still
            // evaluates, and AcknowledgeAll acknowledges, in definition order (kind, then row)
            var pos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in _w.Alarms.All())
                if (!pos.ContainsKey(a.Tag))
                    pos[a.Tag] = pos.Count;
            _w.Tags.Set("Tank1_Temp", 50.0); // inside the limits
            At(0.5); // the first scan evaluates every alarm
            var seen = new List<Transition>();
            _w.Alarms.Listeners.Add(seen.Add);
            _w.Tags.Set("Tank1_Temp", 125.0); // analog ALM_TEMP: High High, no delay
            _w.Tags.Set("Lib_Flag", 1);
            _w.Tags.Set("Pump1_Run", 1); // digital ALM_PUMP, ALM_AND
            At(1.0);
            var on = seen.Where(t => t.Event == "on").Select(t => t.Tag).ToList();
            Assert.Contains("ALM_PUMP", on);
            Assert.Contains("ALM_TEMP", on);
            Assert.True(on.IndexOf("ALM_PUMP") < on.IndexOf("ALM_TEMP"));
            Assert.Equal(on.OrderBy(t => pos[t]).ToList(), on);
            seen.Clear();
            Assert.True(_w.Alarms.AcknowledgeAll() >= 2);
            var acked = seen.Where(t => t.Event == "ack").Select(t => t.Tag).ToList();
            Assert.Contains("ALM_PUMP", acked);
            Assert.Contains("ALM_TEMP", acked);
            Assert.Equal(acked.OrderBy(t => pos[t]).ToList(), acked);
        }

        [Fact]
        public void DigitalOnAckOffAndStates()
        {
            var seen = new List<Transition>();
            _w.Alarms.Listeners.Add(seen.Add);
            _w.Tags.Set("Pump1_Run", 1, by: "scenario");
            At(0.5);
            var s = St("ALM_PUMP");
            Assert.True(s.On && !s.Ack && s.OnTime == 0.5);
            Assert.Equal(257L, _w.Alarms.StateValue(_w.Alarms.Get("ALM_PUMP")!)); // doc: 1068486
            Assert.Contains(seen, t => t.Tag == "ALM_PUMP" && t.Event == "on");
            Assert.Equal(0, _w.Alarms.Acknowledge("ALM_PUMP"));
            Assert.True(s.Ack && s.InList);
            Assert.Equal(1L, _w.Alarms.StateValue(_w.Alarms.Get("ALM_PUMP")!));
            _w.Tags.Set("Pump1_Run", 0);
            At(1.0);
            Assert.True(!s.On && !s.InList); // acknowledged + off = cleared
            // off before an acknowledge = OFF unacknowledged, an acknowledge clears it
            _w.Tags.Set("Pump1_Run", 1);
            At(1.5);
            _w.Tags.Set("Pump1_Run", 0);
            At(2.0);
            Assert.True(s.InList && !s.On && !s.Ack);
            Assert.True(_w.Alarms.Count(12) >= 1 && _w.Alarms.Count(18) >= 1);
            _w.Alarms.Acknowledge("ALM_PUMP");
            Assert.False(s.InList);
        }

        [Fact]
        public void DelayHoldsTheAlarmBack()
        {
            // 'NOT Pump1_Run' with a 5 s delay is true from the first scan (t = 0.5)
            At(5.0);
            Assert.False(St("ALM_NOTPUMP").On);
            At(5.5);
            var s = St("ALM_NOTPUMP");
            Assert.True(s.On && s.OnTime == 0.5); // ON time = when it began
            // a condition that goes away before the delay never alarms
            _w.Tags.Set("Pump1_Run", 1);
            At(6.0);
            _w.Tags.Set("Pump1_Run", 0);
            At(8.0);
            _w.Tags.Set("Pump1_Run", 1);
            At(9.0);
            Assert.False(St("ALM_NOTPUMP").On);
        }

        [Fact]
        public void TwoVariableDigitalNeedsBoth()
        {
            _w.Tags.Set("Pump1_Run", 1);
            At(0.5);
            Assert.False(St("ALM_AND").On);
            _w.Tags.Set("Lib_Flag", 1);
            At(1.0);
            Assert.True(St("ALM_AND").On);
        }

        [Fact]
        public void AnalogLimitsDeadbandAndDelay()
        {
            var tags = _w.Tags;
            tags.Set("Tank1_Temp", 105.0); // above High (100), HDELAY 2 s
            At(0.5);
            var s = St("ALM_TEMP");
            Assert.False(s.On);
            At(2.5);
            Assert.True(s.On && s.Sub == AlarmDb.H && s.State == 12 && s.OnTime == 0.5);
            _w.Alarms.Acknowledge("ALM_TEMP");
            tags.Set("Tank1_Temp", 125.0); // High High replaces High, needs a new ack
            At(3.0);
            Assert.True(s.Sub == AlarmDb.HH && s.State == 14 && !s.Ack);
            tags.Set("Tank1_Temp", 118.0); // inside the deadband (120 - 5)
            At(3.5);
            Assert.Equal(AlarmDb.HH, s.Sub);
            tags.Set("Tank1_Temp", 112.0); // back to High
            At(4.0);
            Assert.Equal(AlarmDb.H, s.Sub);
            tags.Set("Tank1_Temp", 96.0); // High's deadband: still on above 95
            At(4.5);
            Assert.True(s.On && s.Sub == AlarmDb.H);
            tags.Set("Tank1_Temp", 94.0);
            At(5.0);
            Assert.True(!s.On && s.Sub == null);
            tags.Set("Tank1_Temp", 15.0);
            At(5.5);
            Assert.True(s.Sub == AlarmDb.L && s.State == 11);
            tags.Set("Tank1_Temp", 5.0);
            At(6.0);
            Assert.True(s.Sub == AlarmDb.LL && s.State == 13);
            tags.Set("Tank1_Temp", 50.0);
            At(6.5);
            Assert.False(s.On);
        }

        [Fact]
        public void RateAndDeviation()
        {
            var tags = _w.Tags;
            At(0.5);
            tags.Set("Motor_Speed", 10.0); // 10 per 0.5 s > Rate 60 / Period 60 s
            At(1.0);
            var r = St("ALM_RATE");
            Assert.True(r.On && r.Sub == AlarmDb.R && r.State == 10);
            At(1.5); // steady: the rate alarm goes off
            Assert.False(r.On);
            tags.Set("Tank2_Level", 65.0); // setpoint 50 + deviation 10, via Equip.Item
            At(2.0);
            var d = St("ALM_DEV");
            Assert.True(d.Sub == AlarmDb.DVH && d.State == 8);
            tags.Set("Tank2_Level", 45.0);
            At(2.5);
            Assert.False(d.On);
            tags.Set("Tank2_Level", 35.0);
            At(3.0);
            Assert.True(d.Sub == AlarmDb.DVL && d.State == 9);
        }

        [Fact]
        public void AdvancedAndMultiDigital()
        {
            At(0.5);
            Assert.True(St("ADV_NOT").On); // 'NOT Valve_A' with Valve_A = 0
            Assert.True(_w.Alarms.NotEvaluated.Contains("adv_expr"));
            _w.Tags.Set("Pump1_Run", 1);
            At(1.0);
            Assert.True(St("ADV_SIMPLE").On);
            var calls = new List<string>();
            _w.Alarms.SetEvaluator(
                (expr, a) =>
                {
                    calls.Add(expr);
                    return true;
                }
            );
            At(1.5);
            Assert.True(St("ADV_EXPR").On);
            Assert.Equal("Tank1_Level > 80 AND Pump1_Run", calls[calls.Count - 1]);
            // multi-digital: state = C*4 + B*2 + A (doc: 1069236); Pump1_Run is C
            var m = St("ALM_MULTI");
            Assert.True(m.On && m.State == 4);
            var actions = new List<(string, string, string)>();
            _w.Alarms.ActionHook = (a, ev, text) => actions.Add((a.Tag, ev, text));
            _w.Tags.Set("Pump1_Run", 0);
            _w.Tags.Set("Valve_B", 1); // state 2: ALMSTAT2 = 0
            At(2.0);
            Assert.False(m.On);
            Assert.Contains(("ALM_MULTI", "off", "OffMulti()"), actions);
            _w.Tags.Set("Valve_A", 1);
            _w.Tags.Set("Valve_B", 0); // state 1: ALMSTAT1 = 1
            At(2.5);
            Assert.True(m.On && m.State == 1);
            Assert.Contains(("ALM_MULTI", "on", "OnMulti()"), actions);
        }

        [Fact]
        public void CountsFiltersAndActive()
        {
            var al = _w.Alarms;
            var tags = _w.Tags;
            tags.Set("Pump1_Run", 1);
            tags.Set("Valve_A", 1);
            tags.Set("Tank1_Temp", 130.0);
            At(0.5);
            var on = new HashSet<string>(al.All().Where(a => al.PeekState(a)?.On == true).Select(a => a.Tag));
            // ALM_DEV: Tank2_Level 0 is below setpoint 50 - deviation 10; ALM_MULTI state 5 = 0
            Assert.Equal(new HashSet<string> { "ALM_PUMP", "ADV_SIMPLE", "ALM_TEMP", "ALM_DEV" }, on);
            int total = AlarmDb.Kinds.Sum(k => al.Counts()[k]);
            Assert.Equal(10, total);
            Assert.Equal(9, al.Count(4)); // ALM_AND (area 5) hidden: area 0 user
            Assert.True(al.Count(0) == 4 && al.Count(1) == 4 && al.Count(11) == 4 && al.Count(17) == 4);
            al.Acknowledge("ALM_TEMP");
            Assert.Equal((3, 1, 3), (al.Count(1), al.Count(2), al.Count(17)));
            Assert.Equal(1, al.Count(1, "Category=1")); // ALM_PUMP
            Assert.Equal(2, al.Count(0, "Priority=1")); // categories 1 -> priority 1
            Assert.Equal(3, al.Count(0, "Tag=ALM_*"));
            Assert.Equal(1, al.Count(0, "Custom1=east"));
            Assert.Equal(3, al.Count(0, "(Category=1) OR (Category=3)"));
            Assert.Equal(2, al.Count(0, "NOT (Category=1)"));
            Assert.Equal(1, al.Count(0, "Category=1;Tag=ALM_TEMP"));
            Assert.Equal(1, al.Count(0, equipment: "Plant.Tank1")); // bare = 'EQUIPMENT=x*'
            Assert.Equal(2, al.Count(0, equipment: "Plant")); // children too
            Assert.Equal(0, al.Count(0, equipment: "EQUIPMENT=Plant"));
            Assert.Equal(1, al.Count(0, equipment: "PAGE=Detail")); // Tank2's page: ALM_TEMP
            Assert.True(al.Active(0) == 1 && al.Active(1) == 1 && al.Active(3) == 0);
            Assert.Equal(1L, al.Active(2)); // highest unacked priority
            var byP = al.ByPriority(0).ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.Equal(
                new Dictionary<long, int>
                {
                    [1] = 2,
                    [2] = 1,
                    [3] = 1,
                },
                byP
            );
            var byE = al.ByEquipment(0).ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.Equal(new Dictionary<string, int> { ["Plant.Tank1"] = 1, ["Plant.Tank2"] = 1 }, byE);
            al.Disable("ALM_PUMP");
            Assert.True(al.Count(3) == 1 && al.Count(0) == 3 && al.Active(3) == 1);
            tags.Set("Pump1_Run", 0);
            tags.Set("Pump1_Run", 1);
            At(1.0);
            Assert.False(St("ALM_PUMP").On); // disabled: not evaluated
            al.Enable("ALM_PUMP");
            At(1.5);
            Assert.True(St("ALM_PUMP").On && al.Count(3) == 0);
            var rows = al.ActiveList();
            Assert.True(al.Priority(rows[0].a) <= al.Priority(rows[rows.Count - 1].a));
        }

        [Fact]
        public void HardwareAlarms()
        {
            var al = _w.Alarms;
            var hw = al.RaiseHardware(274, page: "Mixer");
            Assert.True(hw.Desc.Length > 0 && al.Count(5) == 1 && al.Count(6) == 1 && al.Active(5) == 1);
            al.RaiseHardware(274, page: "Mixer");
            Assert.True(al.Count(5) == 1 && hw.Count == 2);
            Assert.Equal(1, al.AcknowledgeHardware());
            Assert.True(al.Count(6) == 0 && al.Count(7) == 1);
        }

        [Fact]
        public void AlarmPropertiesAsTags()
        {
            var tags = _w.Tags;
            var al = _w.Alarms;
            var tk = Task();
            tags.Set("Pump1_Run", 1);
            At(0.5);
            var on = tags.Read(WorldValues.Ref("ALM_PUMP.On"), tk);
            Assert.Equal((Types.Long, 1L), (on.T, on.I)); // an integer kind reads as LONG
            Assert.Equal(257L, tags.Read(WorldValues.Ref("Plant.Tank1.PumpAlarm.State"), tk).I);
            Assert.Equal(100.0, tags.Read(WorldValues.Ref("ALM_TEMP.High"), tk).D);
            Assert.Equal(3.4e38, tags.Read(WorldValues.Ref("ALM_RATE.HighHigh"), tk).D); // doc default
            Assert.True(tags.Read(WorldValues.Ref("ALM_PUMP.On", item: "t"), tk).I > 0);
            Assert.Equal(1L, tags.Read(WorldValues.Ref("ALM_PUMP.Category"), tk).I);
            Assert.Equal(0, tags.Write(WorldValues.Ref("ALM_PUMP.Ack"), Long(1), tk));
            Assert.True(St("ALM_PUMP").Ack);
            Assert.Equal(0, tags.Write(WorldValues.Ref("ALM_TEMP.High"), Real(90.0), tk));
            Assert.Equal(90.0, al.Get("ALM_TEMP")!.Limit(AlarmDb.H));
            Assert.Equal(274, tags.Write(WorldValues.Ref("ALM_PUMP.Name"), Str("x"), tk));
            tags.Set("Tank1_Temp", 95.0);
            At(3.5); // HDELAY 2 s
            Assert.Equal(AlarmDb.H, St("ALM_TEMP").Sub);
            Assert.Equal(1L, tags.Read(WorldValues.Ref("ALM_TEMP.H"), tk).I);
        }

        [Fact]
        public void AreaVisibility()
        {
            var al = _w.Alarms;
            var users = _w.Users;
            _w.Tags.Set("Pump1_Run", 1);
            _w.Tags.Set("Lib_Flag", 1);
            At(0.5);
            Assert.True(St("ALM_AND").On); // area 5
            users.LoginAs("nobody", privileges: JsonValue.NewObject()); // area 0 only
            Assert.Equal(0, al.Count(0, "Tag=ALM_AND"));
            Assert.Equal(0, users.Login("Op")); // priv 1 in area 5
            Assert.Equal(1, al.Count(0, "Tag=ALM_AND"));
            users.Logout();
            Assert.Equal(0, al.Count(0, "Tag=ALM_AND"));
        }

        [Fact]
        public void UsersPrivileges()
        {
            var u = _w.Users;
            Assert.True(u.Session.IsDefault && u.GetPriv(1, 0) == 0 && u.UserInfo(0) == "0");
            // privilege 0 asks whether the area is viewable: the default user views area 0 only (doc: 1192340)
            Assert.True(u.GetPriv(0, 0) == 1 && u.GetPriv(0, 3) == 0);
            Assert.Equal(1, u.Login("nobody"));
            Assert.Equal(0, u.Login("Engineer"));
            Assert.True(u.GetPriv(8, 200) == 1 && u.CanView(77)); // global 1..8
            Assert.True(u.UserInfo(1) == "Engineer" && u.UserInfo(2) == "Systems Engineer");
            Assert.True(u.UserInfo(0) == "1" && u.UserInfo(6) == "1");
            _w.Clock.Advance(10);
            u.NoteCommand();
            Assert.Equal("1", u.UserInfo(5));
            Assert.Equal(10, long.Parse(u.UserInfo(4)) - long.Parse(u.UserInfo(3)));
            u.Login("Op"); // PRIV_1 = 5, PRIV_3 = 5,7, ACCESS 6
            Assert.Equal((1, 0, 1), (u.GetPriv(1, 5), u.GetPriv(1, 6), u.GetPriv(1, 0)));
            Assert.Equal((0, 1), (u.GetPriv(2, 5), u.GetPriv(3, 7)));
            Assert.True(u.CanView(6) && u.CanView(7) && u.CanView(0) && !u.CanView(8));
            u.Exclusive = false; // [Privilege]Exclusive 0: hierarchical
            Assert.True(u.GetPriv(2, 5) == 1 && u.GetPriv(4, 5) == 0);
            Assert.True(Users.PrivArgsOk(8, 255) && !Users.PrivArgsOk(9, 0) && !Users.PrivArgsOk(1, 256));
            var privs = JsonValue.NewObject();
            privs.Set("0", JsonValue.Of(8));
            privs.Set("12", JsonValue.Of("1,3"));
            var s = u.LoginAs("Eng2", privileges: privs, areas: JsonValue.Of("20..21"));
            Assert.Equal(new HashSet<int>(Enumerable.Range(1, 8)), s.GlobalPrivs);
            Assert.Equal(new HashSet<int> { 12 }, s.AreaPrivs[3]);
            Assert.True(u.CanView(21));
        }
    }
}
