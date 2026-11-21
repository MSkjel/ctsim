using System;
using System.Collections.Generic;
using System.IO;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.World;
using Xunit;
using static CicodeSim.Tests.World.WorldFixture;
using Quality = CicodeSim.World.Quality;
using WorldT = CicodeSim.World.World;

namespace CicodeSim.Tests.World
{
    /// <summary>Quality layout, the compile unit,
    /// the tag database (synthetic install) and a read-only check on the real R2 ExampleSA.</summary>
    [Collection("World")]
    public sealed class WorldTagsTests : IDisposable
    {
        private readonly TempDir _tmp = new TempDir();
        private readonly WorldT _w;

        public WorldTagsTests()
        {
            _w = MakeWorld(_tmp.Path);
        }

        public void Dispose() => _tmp.Dispose();

        // ------------------------------------------------------------------ quality layout
        [Fact]
        public void QualityLayoutIsClientDlls()
        {
            // QualityCreate general << 6 | substatus << 2 | limit, byte 1
            // extended, byte 2 override / control inhibit, high dword the error code
            ulong q = Quality.Make(
                Quality.Bad,
                Quality.BadCommFailure,
                Quality.LimitedLow,
                Quality.ExtCommBad,
                true,
                true,
                0x42
            );
            Assert.Equal(0x19UL, q & 0xFF);
            Assert.Equal(0x07UL, (q >> 8) & 0xFF);
            Assert.Equal(0x03UL, (q >> 16) & 0xFF);
            Assert.Equal(0x42, Quality.ErrorCode(q));
            Assert.Equal(0x19, Quality.Opc(q));
            Assert.Equal(
                (0, 6, 1, 7),
                (Quality.General(q), Quality.Substatus(q), Quality.Limit(q), Quality.Extended(q))
            );
            Assert.True(Quality.IsOverride(q) && Quality.IsControlInhibit(q) && Quality.IsBad(q));
            Assert.Equal(0xC0UL, Quality.QualityGood);
            Assert.True(Quality.IsGood(0xC0));
            Assert.True(
                Quality.IsUncertain(Quality.Make(Quality.Uncertain))
                    && !Quality.IsBad(Quality.Make(Quality.Uncertain))
            );
            Assert.Equal(0x18UL, Quality.Parse("bad_comm_failure"));
            Assert.Equal(0x40UL, Quality.Parse("0x40"));
            Assert.Equal(0xC0UL, Quality.Parse((string?)null));
            Assert.Equal("bad_comm_failure", Quality.Name(0x18));
            Assert.Throws<FormatException>(() => Quality.Parse("splendid"));
        }

        // ------------------------------------------------------------------ the unit
        [Fact]
        public void CompileUnitOrderAndSearchOrder()
        {
            // Include first, the include projects post-order, the root last
            Assert.Equal(new[] { "Include", "Lib", "Main" }, _w.Unit.Names());
            Assert.Equal("Main", _w.Unit.Search[0].Name);
            Assert.Equal(3L, _w.Unit.LabelValue("_CAT_LBL"));
            Assert.Equal(7L, _w.Unit.LabelValue("7"));
            Assert.Equal("500", _w.Param("alarm", "scantime"));
        }

        // ------------------------------------------------------------------ definitions
        [Fact]
        public void DefinitionsTypesRangesAndProperties()
        {
            var t = _w.Tags;
            var lvl = t.Find("tank1_level")!;
            Assert.Equal("Tank1_Level", lvl.Name);
            Assert.Equal(Types.Int16, lvl.Type);
            Assert.True(lvl.Scaled);
            Assert.Equal(Types.Real, lvl.ValueType);
            Assert.Equal((0.0, 100.0), lvl.CheckRange());
            Assert.Equal("%", lvl.Property("Units"));
            Assert.Equal("levelsensor", lvl.Property("Custom1"));
            Assert.Equal("2", lvl.Property("Type"));
            Assert.Equal("Plant.Tank1", lvl.Property("Equipment"));
            Assert.Equal(Types.Real, t.Find("Motor_Speed")!.Type); // 'Real' case-insensitive
            Assert.Equal(Types.Int16, t.Find("Untyped")!.Type); // doc: 1064875 no type = INT
            Assert.Equal(new[] { 10 }, t.Find("Arr_Real")!.Dims);
            Assert.Equal("10", t.Find("Arr_Real")!.Property("ArraySize"));
            Assert.Equal(new[] { 4, 3 }, t.Find("LocArr")!.Dims);
            Assert.Equal("local", t.Find("LocArr")!.Kind);
            Assert.Equal(Types.Int16, t.Find("LocUntyped")!.Type);
            Assert.False(t.Find("Pump1_Run")!.Scaled); // DIGITAL never scaled
            Assert.Null(t.Find("nope"));
            Assert.Equal(6 | 1 << 8, TagTypes.FormatWord("####.#"));
            Assert.Equal(9 | 2 << 8 | 1 << 16 | 1 << 17 | 1 << 18, TagTypes.FormatWord("#0-###.##EU"));
            Assert.Equal((6, 2, 0), TagTypes.FormatInfo("3.2"));
            // TagInfo (doc: 1192741): 1 units, 4 / 5 eng range, 10 raw type, 14 unit count
            Assert.Equal(("%", "0", "100", "1"), (lvl.Info(1), lvl.Info(4), lvl.Info(5), lvl.Info(10)));
            Assert.Equal("10", t.Find("Arr_Real")!.Info(14));
            Assert.Null(lvl.Info(9));
            Assert.Equal("", t.Find("LocInt")!.Info(13));
            Assert.Equal("####.##", t.Find("LocInt")!.DisplayFormat);
            Assert.Equal("####.#", t.Find("Motor_Speed")!.DisplayFormat);
            var c = t.Counts();
            Assert.Equal((13, 4, 4), (c["variables"], c["locals"], c["equipment_items"]));
        }

        [Fact]
        public void ResolutionForms()
        {
            var t = _w.Tags;
            var r = t.Resolve("Plant.Tank1.Level"); // Equip.Item (doc: 1064785)
            Assert.Equal(("variable", "equipment", "Tank1_Level"), (r.Kind, r.Via, r.Name));
            r = t.Resolve("Tank1_Level.Field.vt"); // extensions written in the name
            Assert.Equal(("Tank1_Level", "field", "vt"), (r.Name, r.Element, r.Item));
            r = t.Resolve("Plant.Tank1.Level.q");
            Assert.Equal(("Tank1_Level", "q"), (r.Name, r.Item));
            r = t.Resolve("Arr_Real[3]");
            Assert.Equal(3, r.Index);
            Assert.Equal("Arr_Real", r.Name);
            Assert.True(t.Resolve("Lib_Flag", cluster: "C1").Found);
            Assert.False(t.Resolve("Lib_Flag", cluster: "C2").Found);
            Assert.False(t.Resolve("Tank1_Level", element: "bogus").Found);
            Assert.Equal("Tank1_Temp", t.Canonical("plant.tank1.temp"));
        }

        // ------------------------------------------------------------------ reading
        [Fact]
        public void ReadInitialValuesItemsAndUnknown()
        {
            var t = _w.Tags;
            ulong start = (ulong)SimClock.FiletimeFromUnix(_w.Clock.StartUnix);
            var v = t.Read(Ref("Tank1_Level"), Task());
            Assert.Equal((Types.Real, 0.0, 0xC0UL, start, start, start), (v.T, v.D, v.Q, v.Ts, v.Vts, v.Qts));
            // the runtime pushes every integer tag type as LONG (4)
            Assert.Equal(Types.Long, t.Read(Ref("Pump1_Run"), Task()).T);
            Assert.Equal(Types.Long, t.Read(Ref("Byte_Val"), Task()).T);
            Assert.Equal("", t.Read(Ref("Name_Str"), Task()).S);
            var loc = t.Read(Ref("LocInt"), Task());
            Assert.Equal((Types.Long, 0L, 0UL), (loc.T, loc.I, loc.Ts)); // locals: no timestamps
            // items: GOOD quality and zero timestamps (doc: 1064782)
            var vt = t.Read(Ref("Tank1_Level", item: "vt"), Task());
            Assert.Equal((Types.Timestamp, (long)start, 0xC0UL, 0UL), (vt.T, vt.I, vt.Q, vt.Ts));
            var q = t.Read(Ref("Tank1_Level", item: "q"), Task());
            Assert.Equal((Types.Quality, 0xC0L), (q.T, q.I));
            // a lightweight TAGREF carries no timestamps
            Assert.Equal(0UL, t.Read(Ref("Tank1_Level", lightweight: 1), Task()).Ts);
            // unknown: bad quality + 424 (hostapi, doc: 1130208)
            var tk = Task();
            v = t.Read(Ref("NoSuchTag"), tk);
            Assert.Equal(424, tk.TagError);
            Assert.True(Quality.IsBad(v.Q));
            Assert.Equal(Quality.ExtInvalidTag, Quality.Extended(v.Q));
            Assert.Equal((Types.Real, 0.0), (v.T, v.D)); // the runtime's no-subscription branch
            // local variable extras read bad, refuse writes (doc: 1130161)
            Assert.True(Quality.IsBad(t.Read(Ref("LocInt", element: "override"), Task()).Q));
            Assert.Equal(274, t.Write(Ref("LocInt", element: "override"), Long(1), Task()));
        }

        [Fact]
        public void UsedListsReadsInFirstReadOrder()
        {
            var t = _w.Tags;
            foreach (string name in new[] { "Pump1_Run", "Plant.Tank1.Level", "Pump1_Run", "LocStr" })
                t.Read(Ref(name), Task());
            t.Get("Valve_A"); // a viewer read without 'by' is not 'used'
            t.Get("Valve_B", by: "page");
            Assert.Equal(new[] { "Pump1_Run", "Tank1_Level", "LocStr", "Valve_B" }, t.Used);
            Assert.Contains(new KeyValuePair<string, string>("Valve_B", "page"), t.UsedBy());
        }

        // ------------------------------------------------------------------ writing
        [Fact]
        public void WriteTimestampsAndValid()
        {
            var t = _w.Tags;
            var clock = _w.Clock;
            clock.Advance(2.0);
            Assert.Equal(0, t.Write(Ref("Motor_Speed"), Real(12.5), Task()));
            var w1 = t.Read(Ref("Motor_Speed"), Task());
            ulong ft = (ulong)SimClock.FiletimeFromUnix(clock.StartUnix + 2.0);
            Assert.Equal(
                (12.5, ft, ft, (ulong)SimClock.FiletimeFromUnix(clock.StartUnix)),
                (w1.D, w1.Ts, w1.Vts, w1.Qts)
            );
            clock.Advance(1.0);
            t.Write(Ref("Motor_Speed"), Real(12.5), Task()); // same value: VT stays
            var w2 = t.Read(Ref("Motor_Speed"), Task());
            Assert.Equal((ulong)SimClock.FiletimeFromUnix(clock.StartUnix + 3.0), w2.Ts);
            Assert.Equal(ft, w2.Vts);
            clock.Advance(1.0);
            t.Set("Motor_Speed", 13, quality: "bad_comm_failure", by: "scenario");
            var w3 = t.Read(Ref("Motor_Speed"), Task());
            Assert.Equal(0x18UL, w3.Q);
            Assert.Equal(w3.Ts, w3.Qts);
            var valid = t.Read(Ref("Motor_Speed", element: "valid"), Task()); // the last good value
            Assert.Equal((12.5, 0xC0UL), (valid.D, valid.Q));
            Assert.Equal("scenario", t.State(t.Find("Motor_Speed")!).By);
        }

        [Fact]
        public void TypeConversionOnWrite()
        {
            var t = _w.Tags;
            t.Write(Ref("Count_Long"), Real(2.5), Task());
            Assert.Equal(3L, t.Get("Count_Long").I); // REAL -> INT half away from zero
            t.Write(Ref("Count_Long"), Real(-2.5), Task());
            Assert.Equal(-3L, t.Get("Count_Long").I);
            t.Write(Ref("Pump1_Run"), Long(7), Task());
            Assert.Equal(1L, t.Get("Pump1_Run").I);
            t.Write(Ref("Name_Str"), Real(1.5), Task());
            Assert.Equal("1.5", t.Get("Name_Str").S);
            t.Write(Ref("Name_Str"), Str("a\u0001b" + new string('x', 300)), Task());
            string s = t.Get("Name_Str").S!;
            Assert.StartsWith("a b", s);
            Assert.Equal(255, s.Length); // doc: 1064761, STRING_MAX
            t.Write(Ref("Motor_Speed"), Real(0.1), Task());
            Assert.Equal(0.1, t.Get("Motor_Speed").D, 7); // a 4-byte REAL
            Assert.NotEqual(0.1, t.Get("Motor_Speed").D);
            Assert.Equal(433, t.Write(Ref("Count_Long"), Str("12abc"), Task()));
            t.Write(Ref("Count_Long"), Str("1,5"), Task()); // ',' read as '.'
            Assert.Equal(2L, t.Get("Count_Long").I);
        }

        [Fact]
        public void ScaleCheckAndScaling()
        {
            var t = _w.Tags;
            var sc = Task(TaskFlags.WriteLocal | TaskFlags.ScaleCheck);
            Assert.Equal(0, t.Write(Ref("Tank1_Level"), Real(50.0), sc));
            var st = t.State(t.Find("Tank1_Level")!);
            Assert.Equal(16000L, st.Field.IntValue);
            Assert.Equal(50.0, t.Get("Tank1_Level").D); // raw INT, eng REAL
            t.Write(Ref("Tank1_Level"), Real(33.33333), sc);
            Assert.Equal(33.334375, t.Get("Tank1_Level").D, 9); // raw quantised
            Assert.Equal(257, t.Write(Ref("Tank1_Level"), Real(101.0), sc));
            Assert.Equal(33.334375, t.Get("Tank1_Level").D, 9); // nothing written
            var noCheck = Task(TaskFlags.WriteLocal);
            Assert.Equal(0, t.Write(Ref("Tank1_Level"), Real(150.0), noCheck));
            Assert.Equal(32767L, st.Field.IntValue); // limited to INT
            Assert.Equal(257, t.Write(Ref("Byte_Val"), Long(300), sc)); // the type's range
            t.Write(Ref("Byte_Val"), Long(300), noCheck);
            Assert.Equal(255L, t.Get("Byte_Val").I);
        }

        [Fact]
        public void WriteLocalOffArrivesLater()
        {
            var t = _w.Tags;
            var tk = Task(TaskFlags.ScaleCheck); // WriteLocal off
            Assert.Equal(0, t.Write(Ref("Motor_Speed"), Real(5.0), tk));
            Assert.Equal(0.0, t.Get("Motor_Speed").D);
            Assert.NotEqual(0L, t.Get("Motor_Speed", element: "status").I & 0x2); // write pending
            _w.Clock.Advance(t.WriteDelay);
            Assert.Equal(5.0, t.Get("Motor_Speed").D);
            Assert.Equal(0x4L, t.Get("Motor_Speed", element: "status").I & 0x6); // write ok
        }

        [Fact]
        public void ElementsOverrideAndControlMode()
        {
            var t = _w.Tags;
            t.Set("Tank1_Temp", 40.0, by: "scenario");
            Assert.Equal(274, t.Write(Ref("Tank1_Temp", element: "status"), Long(1), Task()));
            Assert.Equal(274, t.Write(Ref("Tank1_Temp", element: "valid"), Long(1), Task()));
            // '.v' writes the value, the other items are refused
            Assert.Equal(0, t.Write(Ref("Tank1_Temp", item: "v"), Real(40.0), Task()));
            Assert.Equal(274, t.Write(Ref("Tank1_Temp", item: "q"), Real(1.0), Task()));
            Assert.Equal(0x2009, t.Write(Ref("Tank1_Temp", item: "q"), WorldValues.Quality(0xC0), Task()));
            Assert.Equal(0x2009, t.Write(Ref("Tank1_Temp", item: "vt"), WorldValues.Timestamp(5), Task()));
            Assert.Equal(274, t.Write(Ref("Tank1_Temp", item: "t"), Long(5), Task()));
            // override mode 1 copies the field; an unqualified read then gives the override
            Assert.Equal(0, t.Write(Ref("Tank1_Temp", element: "overridemode"), Long(1), Task()));
            t.Write(Ref("Tank1_Temp", element: "override"), Real(99.0), Task());
            t.Set("Tank1_Temp", 41.0, by: "scenario");
            var v = t.Read(Ref("Tank1_Temp"), Task());
            Assert.Equal(99.0, v.D);
            Assert.True(Quality.IsOverride(v.Q));
            Assert.Equal(41.0, t.Read(Ref("Tank1_Temp", element: "field"), Task()).D);
            // mode 4 tracks Valid and refuses override writes (doc: 1064783)
            t.Write(Ref("Tank1_Temp", element: "overridemode"), Long(4), Task());
            Assert.Equal(41.0, t.Read(Ref("Tank1_Temp"), Task()).D);
            Assert.Equal(274, t.Write(Ref("Tank1_Temp", element: "override"), Real(1.0), Task()));
            Assert.Equal(274, t.Write(Ref("Tank1_Temp", element: "overridemode"), Long(9), Task()));
            t.Write(Ref("Tank1_Temp", element: "overridemode"), Long(0), Task());
            // control inhibit forbids Field writes (doc: 1064782)
            t.Write(Ref("Tank1_Temp", element: "controlmode"), Long(1), Task());
            Assert.Equal(274, t.Write(Ref("Tank1_Temp"), Real(1.0), Task()));
            Assert.True(Quality.IsControlInhibit(t.Read(Ref("Tank1_Temp", element: "field"), Task()).Q));
        }

        [Fact]
        public void Arrays()
        {
            var t = _w.Tags;
            Assert.Equal(0, t.Write(Ref("Arr_Real"), Real(2.0), Task(), index: 3));
            Assert.Equal(2.0, t.Read(Ref("Arr_Real"), Task(), index: 3).D);
            Assert.Equal(2.0, t.Read(Ref("Arr_Real[3]"), Task()).D);
            var tk = Task();
            // the runtime logs '%s[%i]' for the subscription's 0x103 = 259
            Assert.True(Quality.IsBad(t.Read(Ref("Arr_Real"), tk, index: 10).Q));
            Assert.Equal(259, tk.TagError);
            Assert.Equal(259, t.Write(Ref("Arr_Real"), Real(1.0), Task(), index: 10));
            Assert.Equal(259, t.Write(Ref("Arr_Real"), Real(1.0), Task(TaskFlags.ScaleCheck), index: -1)); // also on a delayed write
            Assert.Equal(259, t.Write(Ref("Arr_Real", element: "override"), Real(1.0), Task(), index: 12));
            Assert.Equal(0, t.Write(Ref("LocArr"), Long(9), Task(), index: 11));
            Assert.Equal(9L, t.Get("LocArr", index: 11).I);
        }

        [Fact]
        public void ChangeNotificationWithSerials()
        {
            var t = _w.Tags;
            var seen = new List<TagChange>();
            var off = t.Subscribe(seen.Add);
            long s0 = t.Serial;
            t.Set("Pump1_Run", 1, by: "user");
            t.Write(Ref("Plant.Tank1.Level"), Real(25.0), Task());
            Assert.Equal(2, seen.Count);
            Assert.Equal(("Pump1_Run", "user", "1"), (seen[0].Name, seen[0].By, seen[0].Value.Dump()));
            Assert.Equal(("Tank1_Level", "cicode", "25.0"), (seen[1].Name, seen[1].By, seen[1].Value.Dump()));
            Assert.Equal(seen[0].Serial + 1, seen[1].Serial);
            Assert.Equal(new[] { "Pump1_Run", "Tank1_Level" }, t.ChangesSince(s0));
            Assert.Equal(new[] { "Tank1_Level" }, t.ChangesSince(seen[0].Serial));
            off();
            t.Set("Pump1_Run", 0);
            Assert.Equal(2, seen.Count);
            var p = t.Peek("Pump1_Run");
            Assert.Equal(("0", 0xC0UL, true), (p.value.Dump(), p.quality, p.defined));
            var pb = t.Peek("Valve_B");
            Assert.Equal(("0", 0xC0UL, true), (pb.value.Dump(), pb.quality, pb.defined));
            Assert.False(t.Peek("Nope").defined);
        }

        // ------------------------------------------------------------------ the real install
        [CicodeSim.Tests.Vm.CorpusFact]
        public void ExampleSaWorldReadsItsTags()
        {
            string dir = Path.Combine(R2, "ExampleSA");
            var w = new WorldT(dir, start: 0);
            Assert.Equal(
                new[]
                {
                    "System",
                    "Include",
                    "SA_Controls",
                    "SA_Include",
                    "SA_Library",
                    "IndustrialGraphics_Include",
                    "ExampleSA",
                },
                w.Unit.Names()
            );
            var r = w.Tags.Resolve("Company.TopMilk.Bottling.Capper.Running", item: "vt");
            Assert.Equal(("variable", "equipment", "TopMilk_Capper_Running"), (r.Kind, r.Via, r.Name));
            Assert.Equal("local", w.Tags.Resolve("SA_DisableLabel").Kind); // SA_Library locvar
            Assert.Equal(Types.Real, w.Tags.Read(Ref("TopMilk_LS20_PV"), Task()).T);
        }
    }
}
