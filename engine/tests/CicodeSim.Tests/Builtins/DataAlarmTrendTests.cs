using System;
using System.Collections.Generic;
using System.IO;
using CicodeSim.Builtins;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>Alarm queries, trend tags and history.</summary>
    [Collection("World")]
    public sealed class DataAlarmTrendTests : DataTestBase
    {
        private void Categories() =>
            Dbf(
                Path.Combine(LibDir, "category.dbf"),
                new[]
                {
                    "CATEGORY",
                    "PRIORITY",
                    "ACTION",
                    "OFF_ACTION",
                    "ACK_ACTION",
                    "DSPFMT",
                    "SUMDSPFMT",
                    "SOEDSPFMT",
                },
                new[] { "0", "0", "", "", "", "{Tag,20}{Name,30}", "{Desc,40}", "{Time,12}" },
                new[] { "1", "1", "Beep(1)", "", "", "{Tag,10}", "", "" },
                new[] { "2", "2", "", "", "", "", "", "" },
                new[] { "_CAT_LBL", "3", "", "", "Acked()", "", "", "" }
            );

        // ------------------------------------------------------------------ counts
        [Fact]
        public void CountsAreTheAlarmServers()
        {
            var s = MakeData();
            s.W.Tags.Set("Pump1_Run", 1);
            s.W.Tags.Set("Tank1_Temp", 125.0);
            s.W.RunFor(1);
            var db = s.W.Alarms;
            foreach (int type in new[] { 0, 1, 2, 3, 4, 11, 12, 17, 18 })
            {
                Assert.Equal(db.Count(type), Call(s, "AlarmCount", type).I);
                Assert.Equal(
                    db.Count(type, "Category=1"),
                    Call(s, "AlarmCount", type, "Category=1", 30, 1).I
                );
            }
            Assert.Equal(db.Count(1), Call(s, "AlarmCount").I); // the default type 1
            var r = Call(s, "AlarmCount", 10);
            Assert.Equal(-1, r.I);
            Assert.Equal(Codes.InvalidArg, r.Error);
            r = Call(s, "AlarmCount", 1, "", 30, 0); // CachedMode 0: one turn
            Assert.True(r.Blocked);
            Assert.Equal(db.Count(1), r.I);
            r = CallFg(s, "AlarmCount", 1, "", 30, 0);
            Assert.Equal(-1, r.I);
            Assert.Equal(Codes.FgBlock, r.Error);
            Assert.False(CallFg(s, "AlarmCount", 1).Blocked); // cached: never blocks
            foreach (int mode in new[] { 0, 1, 2, 3, 5, 6 })
                Assert.Equal(db.Active(mode), Call(s, "AlarmActive", mode).I);
            // equipment
            Assert.Equal(
                db.Count(0, "", "Plant.Tank1", true),
                Call(s, "AlarmCountEquipment", 0, "Plant.Tank1").I
            );
            Assert.Equal(
                db.Count(1, "", "EQUIPMENT=Plant*", false),
                Call(s, "AlarmCountEquipment", 1, "EQUIPMENT=Plant*", "", 30, 1, 0).I
            );
            Assert.True(Call(s, "AlarmCountEquipment", 0, "Plant").I >= 1);
            // a cluster-qualified name (SA Equipment_GetContext: the equipment tree's counts)
            db.Get("ALM_PUMP")!.Cluster = "Cluster1";
            Assert.Equal(1, Call(s, "AlarmCountEquipment", 0, "EQUIPMENT=Cluster1.Plant.*", "", 30, 1, 0).I);
            Assert.Equal(
                1,
                Call(s, "AlarmCountEquipment", 0, "EQUIPMENT=CLUSTER1.Plant.Tank1", "", 30, 1, 0).I
            );
            Assert.Equal(0, Call(s, "AlarmCountEquipment", 0, "EQUIPMENT=Cluster2.Plant.*", "", 30, 1, 0).I);
        }

        [Fact]
        public void HighestPriorityAndItsState()
        {
            var s = MakeData();
            s.W.Tags.Set("Pump1_Run", 1); // ALM_PUMP: category 1, priority 1
            s.W.RunFor(1);
            Assert.Equal(1, Call(s, "AlarmHighestPriority", 0).I);
            Assert.Equal(1, Call(s, "AlarmHighestPriority", 1).I); // ON unacknowledged
            Assert.Equal((1 << 8) | 1, Call(s, "AlarmHighestPriority", 2).I);
            Assert.Equal(0, Call(s, "AlarmHighestPriority", 3).I);
            Assert.Equal(0, Call(s, "AlarmAckTag", "ALM_PUMP").I);
            Assert.Equal(3, Call(s, "AlarmHighestPriority", 1, "Plant.Tank1").I); // ON acknowledged
            Assert.Equal(Codes.InvalidArg, Call(s, "AlarmHighestPriority", 7).Error);
        }

        [Fact]
        public void HighestPriorityIncludesReferencesAndLeavesOutHiddenPriorities()
        {
            // Plant.Tank1 references Plant.Tank2 (eqref.dbf); priority 1 has SHOW_AI FALSE
            var s = MakeData(() =>
            {
                Dbf(
                    Path.Combine(MainDir, "eqref.dbf"),
                    new[] { "EQUIP", "REFEQUIP", "REFITEM", "ORDER" },
                    new[] { "Plant.Tank1", "Plant.Tank2", "Level", "1" }
                );
                Dbf(
                    Path.Combine(MainDir, "almprior.dbf"),
                    new[] { "PRIORITY", "UNACKONFG", "SHOW_AI" },
                    new[] { "1", "0x00FF0000", "FALSE" },
                    new[] { "2", "0x00FFFF00", "TRUE" }
                );
            });
            s.W.Tags.Set("Tank1_Temp", 125.0); // ALM_TEMP (Plant.Tank2, priority 1) HH
            s.W.RunFor(3);
            Assert.True(s.W.Alarms.State(s.W.Alarms.Get("ALM_TEMP")!).On);
            // IncludingReference defaults to 1 (doc: 1033485), as AlarmCountEquipment's
            Assert.Equal(1, Call(s, "AlarmHighestPriority", 0, "EQUIPMENT=Plant.Tank1").I);
            Assert.Equal(0, Call(s, "AlarmHighestPriority", 0, "EQUIPMENT=Plant.Tank1", "", 30, 1, 0).I);
            Assert.Equal(1, Call(s, "AlarmHighestPriority", 1, "EQUIPMENT=Plant.Tank1").I);
            // modes 4..6 leave out Show on Indicator FALSE priorities
            Assert.Equal(0, Call(s, "AlarmHighestPriority", 4, "EQUIPMENT=Plant.Tank1").I);
            Assert.Equal(0, Call(s, "AlarmHighestPriority", 6, "EQUIPMENT=Plant.Tank2").I);
            Assert.Equal((1 << 8) | 1, Call(s, "AlarmHighestPriority", 2, "EQUIPMENT=Plant.Tank2").I);
            // the page alarm indicator's query is the same routine
            Assert.Equal((0L, 0), s.W.Alarms.Highest("", "EQUIPMENT=Plant.Tank1", true, indicatorOnly: true));
            Assert.Equal((1L, 1), s.W.Alarms.Highest("", "EQUIPMENT=Plant.Tank1", true));
            // only a disabled alarm: its priority, state 4
            Assert.Equal(0, s.W.Alarms.Disable("ALM_TEMP"));
            Assert.Equal((4 << 8) | 1, Call(s, "AlarmHighestPriority", 2, "EQUIPMENT=Plant.Tank2").I);
        }

        [Fact]
        public void TagActionsAckClearDisableEnable()
        {
            var s = MakeData();
            s.W.Tags.Set("Pump1_Run", 1);
            s.W.RunFor(1);
            var db = s.W.Alarms;
            var a = db.Get("ALM_PUMP")!;
            var r = Call(s, "AlarmAckTag", "ALM_PUMP", ""); // background: one turn
            Assert.True(r.Blocked);
            Assert.Equal(0, r.I);
            Assert.True(db.State(a).Ack);
            Assert.False(CallFg(s, "AlarmAckTag", "Plant.Tank1.PumpAlarm").Blocked); // Equipment.Item, foreground: at once
            Assert.Equal(Codes.TagNotFound, Call(s, "AlarmAckTag", "NoSuchAlarm").I);
            Assert.Equal(Codes.InvalidArg, Call(s, "AlarmAckTag", "").I);
            Assert.Equal(Codes.InvalidArg, Call(s, "AlarmAckTag", " ").I); // ack: ' ' is no tag
            // disable until a time, enable with acknowledge
            long until = s.W.Clock.Time32() + 60;
            Assert.Equal(0, Call(s, "AlarmDisableTag", "ALM_PUMP", "", until, "shelved").I);
            Assert.True(db.State(a).Disabled);
            Assert.Equal(1, Call(s, "AlarmCount", 3).I);
            Assert.Equal(
                Codes.InvalidArg,
                Call(s, "AlarmDisableTag", "ALM_PUMP", "", 0, new string('c', 201)).I
            );
            Assert.Equal(0, Call(s, "AlarmEnableTag", "ALM_PUMP", "", 1).I);
            Assert.False(db.State(a).Disabled);
            // clear: an acknowledged OFF alarm leaves the list
            s.W.Tags.Set("Pump1_Run", 0);
            s.W.RunFor(1);
            Assert.Equal(0, Call(s, "AlarmClearTag", " ").I); // ' ' every alarm
            Assert.False(db.State(a).InList);
        }

        [Fact]
        public void CategoryFormats()
        {
            var s = MakeData(Categories);
            Assert.Equal("{Tag,10}", Call(s, "AlarmCatGetFormat", 1).S);
            Assert.Equal("{Desc,40}", Call(s, "AlarmCatGetFormat", 1, 1).S); // empty: category 0's
            Assert.Equal("{Time,12}", Call(s, "AlarmCatGetFormat", 2, 2).S);
            Assert.Equal("{Tag,20}{Name,30}", Call(s, "AlarmCatGetFormat", 99).S); // undefined category
            Assert.Equal("", Call(s, "AlarmCatGetFormat", 1, 3).S);
        }

        // ------------------------------------------------------------------ browse, records
        [Fact]
        public void AlarmBrowseAndRecords()
        {
            var s = MakeData();
            s.W.Tags.Set("Pump1_Run", 1);
            s.W.RunFor(1);
            var r = CallFg(s, "AlmBrowseOpen", "", "", "", 0);
            Assert.Equal(-1, r.I);
            Assert.Equal(Codes.FgBlock, r.Error);
            r = Call(
                s,
                "AlmBrowseOpen",
                "Category=1",
                "TAG,NAME,STATE,CATEGORY,PRIORITY,EQUIPMENT,CUSTOM1",
                "",
                0
            );
            Assert.True(r.Blocked);
            long h = r.I;
            Assert.True(Call(s, "AlmBrowseNumRecords", h).I >= 2); // ALM_PUMP, ALM_TEMP, ALM_RATE (category 1)
            var tags = new List<string>();
            for (long st = Call(s, "AlmBrowseFirst", h).I; st == 0; st = Call(s, "AlmBrowseNext", h).I)
            {
                tags.Add(Call(s, "AlmBrowseGetField", h, "TAG").S);
                Assert.Equal("1", Call(s, "AlmBrowseGetField", h, "CATEGORY").S);
            }
            Assert.Contains("ALM_PUMP", tags);
            Assert.DoesNotContain("ALM_NOTPUMP", tags); // category 2
            Assert.Equal(0, Call(s, "AlmBrowseClose", h).I);
            // a field outside the session's fields: 413
            h = Call(s, "AlmBrowseOpen", "ALM_PUMP", "TAG", "", 0).I;
            Assert.Equal(1, Call(s, "AlmBrowseNumRecords", h).I);
            Call(s, "AlmBrowseFirst", h);
            Assert.Equal("ALM_PUMP", Call(s, "AlmBrowseGetField", h, "tag").S);
            Assert.Equal(413, Call(s, "AlmBrowseGetField", h, "NAME").Error);
            // AlmTags: the same alarms
            h = Call(s, "AlmTagsOpen", "ALM_PUMP", "", "").I;
            Call(s, "AlmTagsFirst", h);
            Assert.Equal("east", Call(s, "AlmTagsGetField", h, "CUSTOM1").S);
            Assert.Equal("Plant.Tank1", Call(s, "AlmTagsGetField", h, "EQUIPMENT").S);
            Assert.Equal("ON", Call(s, "AlmTagsGetField", h, "STATE").S);
            // actions at the cursor
            Assert.Equal(0, Call(s, "AlmTagsAck", h).I);
            Assert.True(s.W.Alarms.State(s.W.Alarms.Get("ALM_PUMP")!).Ack);
            Assert.Equal(0, Call(s, "AlmTagsDisable", h, 0, "x").I);
            Assert.True(s.W.Alarms.State(s.W.Alarms.Get("ALM_PUMP")!).Disabled);
            Assert.Equal(0, Call(s, "AlmTagsEnable", h, 0).I);
            Assert.False(s.W.Alarms.State(s.W.Alarms.Get("ALM_PUMP")!).Disabled);
            Assert.Equal(Codes.BrowseSessionNotFound, Call(s, "AlmBrowseAck", 9999).I);
            long hb = Call(s, "AlmBrowseOpen", "ALM_PUMP", "", "", 0).I;
            Assert.Equal(Codes.BrowseEof, Call(s, "AlmBrowseAck", hb).I); // no current record yet
            // records
            long rec = Call(s, "AlarmFirstTagRec", "ALM_PUMP", " ", " ").I;
            Assert.Equal(s.W.Alarms.Get("ALM_PUMP")!.Ordinal, rec);
            Assert.Equal("Pump", Call(s, "AlarmGetFieldRec", rec, "Name").S);
            Assert.Equal("1", Call(s, "AlarmGetFieldRec", rec, "Priority").S);
            Assert.Equal(rec, Call(s, "AlarmFirstTagRec", "plant.tank1.pumpalarm", " ", " ").I);
            long all0 = Call(s, "AlarmFirstTagRec", " ", " ", " ").I;
            Assert.Equal(0, all0);
            Assert.Equal(1, Call(s, "AlarmNextTagRec", all0, " ", " ", " ").I);
            Assert.Equal(-1, Call(s, "AlarmFirstTagRec", "NoSuch", " ", " ").I);
            r = Call(s, "AlarmGetFieldRec", 99999, "Name");
            Assert.Equal(Codes.InvalidArg, r.Error);
            Assert.Equal(-1, CallFg(s, "AlarmFirstTagRec", "ALM_PUMP", " ", " ").I);
        }

        [Fact]
        public void FilterEditSessionsOnNamedFilters()
        {
            var s = MakeData();
            Assert.Equal(-1, Call(s, "AlarmFilterEditOpen", "21").I); // an AN: no alarm list here
            Assert.Equal(0, Call(s, "AlarmFilterOpen", "MyFilter", 1, 0).I);
            Assert.Equal(Codes.InvalidArg, Call(s, "AlarmFilterOpen", "MyFilter", 1, 0).I);
            Assert.Equal(0, Call(s, "AlarmFilterOpen", "MyFilter", 2, 0).I);
            long h = Call(s, "AlarmFilterEditOpen", "myfilter", 0).I;
            Assert.True(h >= 0);
            // the help example (doc 1069687)
            Assert.Equal(0, Call(s, "AlarmFilterEditSet", h, "Tag=Dig*;Category=1;Area=1;").I);
            Assert.Equal(0, Call(s, "AlarmFilterEditAppend", h, "Priority<20").I);
            Assert.Equal("Tag=Dig*;", Call(s, "AlarmFilterEditFirst", h).S);
            Assert.Equal("Category=1;", Call(s, "AlarmFilterEditNext", h).S);
            Assert.Equal("Priority<20;", Call(s, "AlarmFilterEditLast", h).S);
            Assert.Equal("Area=1;", Call(s, "AlarmFilterEditPrev", h).S);
            Assert.Equal(1, Call(s, "AlarmFilterEditHasField", h, "Tag").I);
            Assert.Equal(0, Call(s, "AlarmFilterEditHasField", h, "Desc").I);
            Assert.Equal(1, Call(s, "AlarmFilterEditHasField", h, "Desc,Area").I);
            // Set, then Append pieces (doc 1069680)
            Call(s, "AlarmFilterEditSet", h, "Tag");
            Call(s, "AlarmFilterEditAppend", h, "=");
            Call(s, "AlarmFilterEditAppend", h, "ALM_P*");
            var r = Call(s, "AlarmFilterEditCommit", h);
            Assert.True(r.Blocked);
            Assert.Equal(0, r.I);
            Assert.Equal("Tag=ALM_P*;", Call(s, "AlarmFilterEditFirst", h).S);
            // the named filter counts like its text
            s.W.Tags.Set("Pump1_Run", 1);
            s.W.RunFor(1);
            Assert.Equal(s.W.Alarms.Count(4, "Tag=ALM_P*"), Call(s, "AlarmCount", 4, "MyFilter").I);
            // an invalid filter: HasField 0, Commit refuses
            Call(s, "AlarmFilterEditSet", h, "NewCar=Fantastic;");
            Assert.Equal(0, Call(s, "AlarmFilterEditHasField", h, "NewCar").I);
            Assert.Equal(Codes.InvalidArg, Call(s, "AlarmFilterEditCommit", h).I);
            Assert.Equal(0, Call(s, "AlarmFilterEditClose", h).I);
            Assert.Equal(Codes.BadHandle, Call(s, "AlarmFilterEditClose", h).I);
            Assert.Equal(Codes.BadHandle, Call(s, "AlarmFilterEditSet", h, "x").I);
            Assert.Equal("", Call(s, "AlarmFilterEditFirst", h).S);
            Assert.Equal(0, Call(s, "AlarmFilterClose", "MyFilter").I);
            Assert.Equal(Codes.InvalidArg, Call(s, "AlarmFilterClose", "MyFilter").I);
        }

        // ------------------------------------------------------------------ trends
        private void TrendTable() =>
            Dbf(
                Path.Combine(MainDir, "trend.dbf"),
                new[]
                {
                    "NAME",
                    "EXPR",
                    "SAMPLEPER",
                    "AREA",
                    "PRIV",
                    "ENG_UNITS",
                    "FILENAME",
                    "PERIOD",
                    "TYPE",
                    "STORMETHOD",
                    "ENG_ZERO",
                    "ENG_FULL",
                    "CLUSTER",
                },
                new[]
                {
                    "TrnTemp",
                    "Tank1_Temp",
                    "00:00:01",
                    "3",
                    "2",
                    "degC",
                    "[DATA]:TrnTemp.HST",
                    "24:00:00",
                    "TRN_PERIODIC",
                    "Floating Point (8-byte samples)",
                    "",
                    "",
                    "",
                },
                new[]
                {
                    "TrnScaled",
                    "Tank1_Level",
                    "00:00:02.5",
                    "",
                    "",
                    "%",
                    "",
                    "Monthly",
                    "TRN_EVENT",
                    "Scaled (2-byte samples)",
                    "10",
                    "90",
                    "",
                },
                new[] { "TrnExpr", "Tank1_Temp * 2", "1", "", "", "", "", "", "", "", "", "", "" }
            );

        [Fact]
        public void InvalidAndGatedMarkersAreBitExact()
        {
            var s = MakeData();
            Assert.Equal(
                unchecked((long)0xFFFFBBBBFFFFBBBBUL),
                BitConverter.DoubleToInt64Bits(Call(s, "TrnGetInvalidValue").D)
            );
            Assert.Equal(
                unchecked((long)0xFFFFAAAAFFFFAAAAUL),
                BitConverter.DoubleToInt64Bits(Call(s, "TrnGetGatedValue").D)
            );
            Assert.Equal(2, Call(s, "TrnIsValidValue", Trends.Invalid).I);
            Assert.Equal(1, Call(s, "TrnIsValidValue", Trends.Gated).I);
            Assert.Equal(2, Call(s, "TrnIsValidValue", double.NaN).I); // any other NaN
            Assert.Equal(0, Call(s, "TrnIsValidValue", 12.5).I);
            // the bits survive the VM: REAL f() RETURN TrnGetInvalidValue(); and TrnIsValidValue of it
            var s2 = MakeData(build: pb =>
            {
                pb.Function("inv", "", Types.Real).Func("TrnGetInvalidValue", 0).RetValue().End();
                pb.Function("ok", "", Types.Long)
                    .Func("TrnGetInvalidValue", 0)
                    .Func("TrnIsValidValue", 1)
                    .RetValue()
                    .End();
            });
            Assert.Equal(
                unchecked((long)0xFFFFBBBBFFFFBBBBUL),
                BitConverter.DoubleToInt64Bits(s2.Result("inv").D)
            );
            Assert.Equal(2, s2.Result("ok").I);
        }

        [Fact]
        public void TrnInfoDefScaleAndBrowse()
        {
            var s = MakeData(TrendTable);
            Assert.Equal("1", Call(s, "TrnInfo", "TrnTemp", 1).S); // TRN_PERIODIC
            Assert.Equal("2", Call(s, "TrnInfo", "TrnScaled", 1).S); // TRN_EVENT
            Assert.Equal("1", Call(s, "TrnInfo", "trntemp", 2).S);
            Assert.Equal("2.5", Call(s, "TrnInfo", "TrnScaled", 2).S);
            Assert.Equal("[DATA]:TrnTemp", Call(s, "TrnInfo", "TrnTemp", 3).S);
            Assert.Equal("3", Call(s, "TrnInfo", "TrnTemp", 4).S);
            Assert.Equal("2", Call(s, "TrnInfo", "TrnTemp", 5).S);
            Assert.Equal("degC", Call(s, "TrnInfo", "TrnTemp", 7).S);
            Assert.Equal("8", Call(s, "TrnInfo", "TrnTemp", 8).S);
            Assert.Equal("2", Call(s, "TrnInfo", "TrnScaled", 8).S);
            Assert.Equal("86400", Call(s, "TrnInfo", "TrnTemp", 9).S);
            Assert.Equal("0", Call(s, "TrnInfo", "TrnScaled", 9).S); // monthly
            Assert.Equal("1", Call(s, "TrnInfo", "Cluster1.TrnTemp", 1).S); // Cluster.Tag
            var r = Call(s, "TrnInfo", "TrnTemp", 10);
            Assert.Equal("", r.S);
            Assert.Equal(Codes.InvalidArg, r.Error); // a type outside 1..9: 274
            Assert.Equal(Codes.NameNotFound, Call(s, "TrnInfo", "", 1).Error); // an empty tag: 289
            Assert.Equal(Codes.NameNotFound, Call(s, "TrnInfo", "", 0).Error); // the empty tag is tested first
            Assert.Equal(Codes.NameNotFound, Call(s, "TrnInfo", "NoTrend", 1).Error);
            Assert.Equal(Codes.FgBlock, CallFg(s, "TrnInfo", "TrnTemp", 1).Error);
            // scales: trend.dbf, else the tag's range
            Value lo = Ref(Value.Real(0)),
                hi = Ref(Value.Real(0));
            Assert.Equal(0, Call(s, "TrnGetDefScale", "TrnScaled", lo, hi).I);
            Assert.Equal(10.0, Held(lo).D);
            Assert.Equal(90.0, Held(hi).D);
            Assert.Equal(0, Call(s, "TrnGetDefScale", "TrnTemp", lo, hi).I);
            Assert.Equal(0.0, Held(lo).D);
            Assert.Equal(150.0, Held(hi).D); // Tank1_Temp 0..150
            Assert.Equal(Codes.NameNotFound, Call(s, "TrnGetDefScale", "NoTrend", lo, hi).I);
            // browse
            long h = Call(s, "TrnBrowseOpen", "NAME=Trn*;EXPRESSION=Tank1_Temp*", "NAME,EXPRESSION", "").I;
            Assert.Equal(2, Call(s, "TrnBrowseNumRecords", h).I);
            Call(s, "TrnBrowseFirst", h);
            Assert.Equal("TrnExpr", Call(s, "TrnBrowseGetField", h, "NAME").S); // by name
            Call(s, "TrnBrowseNext", h);
            Assert.Equal("TrnTemp", Call(s, "TrnBrowseGetField", h, "NAME").S);
            Assert.Equal(413, Call(s, "TrnBrowseGetField", h, "TAG").Error); // not among the session's fields
            // the trend server's answer 0x19d, raised by the open
            Assert.Equal(Codes.BrowseFieldNotFound, Call(s, "TrnBrowseOpen", "BOGUS=1", "", "").Error);
        }

        private void EquipTrendTable() =>
            Dbf(
                Path.Combine(MainDir, "trend.dbf"),
                new[] { "NAME", "EXPR", "SAMPLEPER", "CLUSTER", "EQUIP", "ITEM" },
                new[] { "Meter_PV", "Tank1_Level", "1", "Cluster1", "Plant.Meter", "PV" },
                new[] { "Meter_OP", "Tank1_Temp", "1", "Cluster1", "Plant.Meter", "OP" },
                new[] { "Other_PV", "Tank1_Temp", "1", "Cluster1", "Plant.Other", "PV" }
            );

        [Fact]
        public void TrnBrowseFiltersByItem()
        {
            // ITEM (trend.dbf's column) is a browse field, though the help's list leaves
            // it out: the SA trend genies' pens come from SA_Library Trend.ci's
            // TrnBrowseOpen("CLUSTER=..;EQUIPMENT=..;ITEM=..", "TAG"), and the live runtime draws
            // them (conformance/pages/ExampleSA/Meters/meter: the Analyzer trend's pen is
            // Company.Library.Meter.PV)
            var s = MakeData(EquipTrendTable);
            long h = Call(s, "TrnBrowseOpen", "CLUSTER=Cluster1;EQUIPMENT=Plant.Meter;ITEM=PV", "TAG", "").I;
            Assert.True(h >= 0);
            Assert.Equal(1, Call(s, "TrnBrowseNumRecords", h).I);
            Assert.Equal(0, Call(s, "TrnBrowseFirst", h).I);
            Assert.Equal("Meter_PV", Call(s, "TrnBrowseGetField", h, "TAG").S);
            Assert.Equal(0, Call(s, "TrnBrowseClose", h).I);
            // ITEM as a column of the session too (SA Trend.ci _SATrend_BrowsePens: "TAG,ITEM")
            h = Call(s, "TrnBrowseOpen", "EQUIPMENT=Plant.Meter", "TAG,ITEM", "").I;
            Assert.Equal(2, Call(s, "TrnBrowseNumRecords", h).I);
            Call(s, "TrnBrowseFirst", h);
            Assert.Equal("Meter_OP", Call(s, "TrnBrowseGetField", h, "TAG").S); // by name
            Assert.Equal("OP", Call(s, "TrnBrowseGetField", h, "ITEM").S);
            // an unknown field is still refused (the server's 0x19d)
            Assert.Equal(Codes.BrowseFieldNotFound, Call(s, "TrnBrowseOpen", "ITEMS=PV", "", "").Error);
        }

        [Fact]
        public void TrnGetTableSamplesTheTagHistory()
        {
            // a module REAL array of 12, TrnGetTable over it, and a reader of one element
            var s = MakeData(TrendTable, pb =>
            {
                pb.Pools.CodeVar("Tab", Types.Real, 12);
                pb.Function("get", "4", Types.Long)
                    .Begin()
                    .ConstStr("TrnTemp")
                    .ConstInt(0)
                    .ConstReal(1.0)
                    .ConstInt(12)
                    .AddrVar("Tab", 6)
                    .LoadStack(0)
                    .ConstInt(0)
                    .ConstStr("")
                    .Func("TrnGetTable", 8)
                    .RetValue()
                    .End();
                pb.Function("expr", "", Types.Long)
                    .ConstStr("TrnExpr")
                    .ConstInt(0)
                    .ConstReal(1.0)
                    .ConstInt(3)
                    .AddrVar("Tab", 6)
                    .ConstInt(1)
                    .ConstInt(0)
                    .ConstStr("")
                    .Func("TrnGetTable", 8)
                    .RetValue()
                    .End();
                pb.Function("bad", "", Types.Long)
                    .ConstStr("TrnTemp")
                    .ConstInt(0)
                    .ConstReal(1.0)
                    .ConstInt(4001)
                    .AddrVar("Tab", 6)
                    .ConstInt(1)
                    .ConstInt(0)
                    .ConstStr("")
                    .Func("TrnGetTable", 8)
                    .RetValue()
                    .End();
                pb.Function("at", "4", Types.Real)
                    .Begin()
                    .LoadStack(0)
                    .Index(12)
                    .LoadVar("Tab")
                    .RetValue()
                    .End();
            });
            s.W.Tags.Set("Tank1_Temp", 10.0);
            s.W.RunFor(5);
            s.W.Tags.Set("Tank1_Temp", 20.0);
            s.W.RunFor(5);
            s.W.Tags.Set("Tank1_Temp", 30.0);
            s.W.RunFor(0.5);
            // newest first, 1 s apart from now (t = 10.5): 30, 20 x 5, 10 x 5, then before the start
            var r = s.Call("get", 1); // keep invalid samples
            Assert.Equal("stop", r.Outcome);
            Assert.Equal(12, r.Task.Result!.Value.I);
            double[] want = { 30, 20, 20, 20, 20, 20, 10, 10, 10, 10, 10 };
            for (int i = 0; i < want.Length; i++)
                Assert.Equal(want[i], s.Result("at", i).D);
            Assert.Equal(2, Trends.Validity(s.Result("at", 11).D)); // t = -0.5: invalid
            r = s.Call("get", 0); // invalid -> 0
            Assert.Equal(0.0, s.Result("at", 11).D);
            r = s.Call("get", 3); // oldest first
            Assert.Equal(30.0, s.Result("at", 11).D);
            Assert.Equal(10.0, s.Result("at", 1).D);
            // an expression that is not a tag: invalid samples
            Assert.Equal(3, s.Result("expr").I);
            Assert.Equal(2, Trends.Validity(s.Result("at", 0).D));
            // a length above [Trend]MaxRequestLength: 274, 0
            r = s.Call("bad");
            Assert.Equal(0, r.Task.Result!.Value.I);
            Assert.Equal(Codes.InvalidArg, r.Task.Error);
            Assert.Equal(Codes.FgBlock, s.Foreground("expr").Error); // blocking: refused in foreground
        }

        [Fact]
        public void EventSamplesWrittenAreReadBack()
        {
            // (EnergyManagement.ci: TrnEventSetTable of one sample; export.ci reads events back)
            var s = MakeData(TrendTable, pb =>
            {
                // (values 1.5, 2.5, 3.5 at the times 100, 200, 300)
                pb.Pools.CodeVar("Vals", Types.Real, count: 4, init: new object[] { 1.5, 2.5, 3.5, 0.0 });
                pb.Pools.CodeVar("Times", Types.Long, count: 4, init: new object[] { 100, 200, 300, 0 });
                pb.Pools.CodeVar("OutV", Types.Real, 4);
                pb.Pools.CodeVar("OutT", Types.Long, 4);
                pb.Function("set", "", Types.Long)
                    .ConstStr("TrnTemp")
                    .ConstInt(0)
                    .ConstInt(0)
                    .ConstInt(3)
                    .AddrVar("Vals", 6)
                    .AddrVar("Times", 4)
                    .Func("TrnEventSetTable", 6)
                    .RetValue()
                    .End();
                pb.Function("get", "4", Types.Long)
                    .Begin()
                    .ConstStr("TrnTemp")
                    .ConstInt(0)
                    .ConstInt(0)
                    .ConstInt(4)
                    .AddrVar("OutV", 6)
                    .AddrVar("OutT", 4)
                    .LoadStack(0)
                    .Func("TrnEventGetTable", 7)
                    .RetValue()
                    .End();
                pb.Function("v", "4", Types.Real)
                    .Begin()
                    .LoadStack(0)
                    .Index(4)
                    .LoadVar("OutV")
                    .RetValue()
                    .End();
                pb.Function("t", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Index(4)
                    .LoadVar("OutT")
                    .RetValue()
                    .End();
            });
            var r = s.Call("set");
            Assert.Equal("stop", r.Outcome);
            Assert.Equal(3, r.Task.Result!.Value.I);
            // the last 4 events, newest first (event 3 = 3.5); the 4th was never written
            r = s.Call("get", 1);
            Assert.Equal(4, r.Task.Result!.Value.I);
            Assert.Equal(3.5, s.Result("v", 0).D);
            Assert.Equal(300, s.Result("t", 0).I);
            Assert.Equal(1.5, s.Result("v", 2).D);
            Assert.Equal(2, Trends.Validity(s.Result("v", 3).D)); // (never written: invalid)
            s.Call("get", 3); // oldest first
            Assert.Equal(1.5, s.Result("v", 1).D);
            Assert.Equal(3.5, s.Result("v", 3).D);
            Assert.Equal(Codes.FgBlock, s.Foreground("set").Error); // blocking: refused in foreground
        }

        [Fact]
        public void TheSimulationsSchedulerKeepsItsItems()
        {
            // the simulation's own scheduler (World.EquipScheduler): ExampleSA's
            // BuildDefaultSchedules adds its shifts through these and must not stop on them
            var s = MakeData();
            long day = 864000000000L;
            long start = 133000000000000000L / day * day; // a midnight (UTC)
            Value Ts(long ft) => Value.Int(Types.Timestamp, ft);
            var r = Call(
                s,
                "ScheduleItemAdd",
                "Cluster1",
                "Equip",
                "Active",
                Ts(start + day / 4),
                Ts(start + day / 2),
                "Shift"
            );
            Assert.Equal(0, r.Error);
            long id = r.I;
            Assert.True(id > 0);
            Assert.Equal(
                0,
                Call(s, "ScheduleItemSetRepeat", "Cluster1", id, 4, 1, 0, 127, -1, -1, 0, 0, 0, 0, "").I
            );
            Assert.Equal(
                Codes.InvalidArg,
                Call(s, "ScheduleItemSetRepeat", "Cluster1", id + 99, 4, 1, 0, 127, -1, -1, 0, 0, 0, 0, "").I
            );
            // three days: the daily item's three occurrences
            r = Call(s, "SchdConfigOpen", Ts(start), 3 * 86400, "", "", "");
            Assert.Equal(0, r.Error);
            long hs = r.I;
            Assert.True(hs >= 0);
            Assert.Equal(3, Call(s, "SchdConfigNumRecords", hs).I);
            Assert.Equal(0, Call(s, "SchdConfigFirst", hs).I);
            Assert.Equal(id.ToString(), Call(s, "SchdConfigGetField", hs, "ID").V.S);
            Assert.Equal(
                (start + day / 4).ToString(),
                Call(s, "SchdConfigGetField", hs, "OCCURRENCESTART").V.S
            );
            Assert.Equal(0, Call(s, "SchdConfigClose", hs).I);
            // one equipment's occurrences (SchdOpen); another equipment has none
            r = Call(s, "SchdOpen", "equip", Ts(start), 86400, "", "STATE", "");
            Assert.Equal(1, Call(s, "SchdNumRecords", r.I).I);
            Assert.Equal(0, Call(s, "SchdFirst", r.I).I);
            Assert.Equal("Active", Call(s, "SchdGetField", r.I, "STATE").V.S);
            r = Call(s, "SchdOpen", "Other", Ts(start), 86400, "", "", "");
            Assert.Equal(0, Call(s, "SchdNumRecords", r.I).I);
            Assert.Equal(Codes.FgBlock, CallFg(s, "SchdOpen", "Equip", Ts(start), 0, "", "", "").Error);
            Assert.Equal(0, Call(s, "ScheduleItemDelete", "Cluster1", id).I);
            r = Call(s, "SchdConfigOpen", Ts(start), 3 * 86400, "", "", "");
            Assert.Equal(0, Call(s, "SchdConfigNumRecords", r.I).I);
            Assert.Equal(
                0,
                Call(s, "TrnEventSetTable", "T", 0, 0, 1, Ref(Value.Real(0)), Ref(Value.Long(0)), "").I
            );
            Assert.Equal(Codes.BrowseSessionNotFound, Call(s, "SchdFirst", -1).I); // a bad handle
        }
    }
}
