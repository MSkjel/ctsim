using System.IO;
using CicodeSim.Builtins;
using CicodeSim.Values;
using CicodeSim.World;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>The trend browse.</summary>
    [Collection("World")]
    public sealed class TrendBrowseTests : DataTestBase
    {
        private void Tables()
        {
            Dbf(
                Path.Combine(MainDir, "trend.dbf"),
                new[]
                {
                    "NAME",
                    "EXPR",
                    "TRIG",
                    "SAMPLEPER",
                    "FORMAT",
                    "TYPE",
                    "CLUSTER",
                    "EQUIP",
                    "ITEM",
                    "HISTORIAN",
                    "COMMENT",
                    "TAGGENLINK",
                    "PERIOD",
                },
                new[]
                {
                    "Meter_PV",
                    "Tank1_Temp",
                    "",
                    "1",
                    "",
                    "TRN_PERIODIC",
                    "Cluster1",
                    "Plant.Meter",
                    "PV",
                    "TRUE",
                    "PV",
                    "g1",
                    "24:00:00",
                },
                new[]
                {
                    "Meter_SP",
                    "Tank1_Level",
                    "",
                    "1",
                    "###.##",
                    "TRN_PERIODIC",
                    "Cluster1",
                    "Plant.Meter",
                    "SP",
                    "TRUE",
                    "SP",
                    "g2",
                    "24:00:00",
                },
                new[]
                {
                    "Meter_OP",
                    "Tank1_Temp + Tank1_Level",
                    "",
                    "1",
                    "",
                    "TRN_PERIODIC",
                    "Cluster1",
                    "Plant.Meter",
                    "OP",
                    "",
                    "",
                    "",
                    "",
                },
                new[]
                {
                    "Pump_FB",
                    "Rand(5)",
                    "Pump1_Run",
                    "2",
                    "",
                    "TRN_EVENT",
                    "Cluster1",
                    "Plant.Pump",
                    "FB",
                    "",
                    "",
                    "",
                    "",
                },
                new[]
                {
                    "Tank_Band",
                    "Tank1_Level > 10 AND Tank1_Level < 90",
                    "NOT Pump1_Run",
                    "00:00:00.5",
                    "####.#",
                    "",
                    "",
                    "Plant.Tank",
                    "",
                    "FALSE",
                    "",
                    "",
                    "",
                }
            );
            Dbf(
                Path.Combine(MainDir, "spc.dbf"),
                new[] { "NAME", "EXPR", "SAMPLEPER", "LSL", "USL" },
                new[] { "Weight_SPC", "400 + Tank1_Level", "2", "390", "460" }
            );
        }

        private static long Open(Sim s, string filter, string fields) =>
            Call(s, "TrnBrowseOpen", filter, fields, "").I;

        private static string Field(Sim s, long h, string f) => Call(s, "TrnBrowseGetField", h, f).S;

        [Fact]
        public void TheBrowseFieldsAreTheTrendServersFormat()
        {
            var s = MakeData(Tables);
            // SA_Library Trend.ci _SATrend_GetTrendTag: one equipment item
            long h = Open(s, "CLUSTER=Cluster1;EQUIPMENT=Plant.Meter;ITEM=PV", "TAG");
            Assert.True(h >= 0);
            Assert.Equal(1, Call(s, "TrnBrowseNumRecords", h).I);
            Assert.Equal(0, Call(s, "TrnBrowseFirst", h).I);
            Assert.Equal("Meter_PV", Field(s, h, "TAG"));
            Assert.Equal(0, Call(s, "TrnBrowseClose", h).I);
            // _SATrend_BrowsePens: "ITEM=" (an empty value) gives every trend of the equipment, by name
            h = Open(s, "CLUSTER=Cluster1;EQUIPMENT=Plant.Meter;ITEM=", "TAG,ITEM");
            Assert.Equal(3, Call(s, "TrnBrowseNumRecords", h).I);
            var items = new System.Collections.Generic.List<string>();
            for (long e = Call(s, "TrnBrowseFirst", h).I; e == 0; e = Call(s, "TrnBrowseNext", h).I)
                items.Add(Field(s, h, "TAG") + ":" + Field(s, h, "ITEM"));
            Assert.Equal(new[] { "Meter_OP:OP", "Meter_PV:PV", "Meter_SP:SP" }, items);
            // every field of the format is a filter field; the help's PSI_TYPE / SCALED_TYPE /
            // TAGGENLINK and the left-out RDB fields (TRIG 0x10, PERIOD 0xc, EXPR 0x13, HASH 9) are not
            foreach (string f in Trends.BrowseFields)
                Assert.True(Open(s, f + "=*", "") >= 0, f);
            // a filter or requested field the server's format lacks: its answer 0x19d, raised
            // once it is in (the trend server)
            foreach (
                string f in new[]
                {
                    "PSI_TYPE",
                    "SCALED_TYPE",
                    "TAGGENLINK",
                    "TRIG",
                    "PERIOD",
                    "EXPR",
                    "HASH",
                    "EQUIP",
                }
            )
            {
                var r = Call(s, "TrnBrowseOpen", f + "=x", "", "");
                Assert.True(r.Blocked, f);
                Assert.Equal(-1, r.I);
                Assert.Equal(Codes.BrowseFieldNotFound, r.Error);
                r = Call(s, "TrnBrowseOpen", "", "NAME," + f, "");
                Assert.Equal(-1, r.I);
                Assert.Equal(Codes.BrowseFieldNotFound, r.Error);
            }
            Assert.True(Open(s, "", " name , TAG, comment") >= 0);
            // EXPRESSION / TRIGGER: the one tag of the expression, else ''
            h = Open(s, "", "NAME,EXPRESSION,TRIGGER,FORMAT,HISTORIAN,ITEM,EQUIPMENT");
            var expr = new System.Collections.Generic.Dictionary<string, string>();
            for (long e = Call(s, "TrnBrowseFirst", h).I; e == 0; e = Call(s, "TrnBrowseNext", h).I)
                expr[Field(s, h, "NAME")] =
                    Field(s, h, "EXPRESSION")
                    + "|"
                    + Field(s, h, "TRIGGER")
                    + "|"
                    + Field(s, h, "FORMAT")
                    + "|"
                    + Field(s, h, "HISTORIAN")
                    + "|"
                    + Field(s, h, "ITEM")
                    + "|"
                    + Field(s, h, "EQUIPMENT");
            // FORMAT = the compiled word in decimal ('' 0x200206, '###.##' 0x206), HISTORIAN 0 / 1
            Assert.Equal("Tank1_Temp||2097670|1|PV|Plant.Meter", expr["Meter_PV"]);
            Assert.Equal("Tank1_Level||518|1|SP|Plant.Meter", expr["Meter_SP"]);
            Assert.Equal("||2097670|0|OP|Plant.Meter", expr["Meter_OP"]); // two tags
            Assert.Equal("|Pump1_Run|2097670|0|FB|Plant.Pump", expr["Pump_FB"]); // a function call
            // the operator keywords are no names: one tag (twice) and NOT one tag
            Assert.Equal("Tank1_Level|Pump1_Run|262|0||Plant.Tank", expr["Tank_Band"]);
            // the SPC tags are trends too (_TREND.RDB holds them: Example 298 + 3 records)
            Assert.Equal("Tank1_Level", expr["Weight_SPC"].Split('|')[0]);
            Assert.Equal("2", Call(s, "TrnInfo", "Weight_SPC", 2).S);
            // the compiled record's text: TYPE / AREA / PRIV label
            // numbers, SAMPLEPER seconds "%.7g", SPCFLAG 1 for the SPC tag (Include zoom.ci picks
            // its pens by it), DEADBAND -1 when not set
            h = Open(s, "", "NAME,TYPE,AREA,PRIV,SAMPLEPER,SPCFLAG,DEADBAND");
            var comp = new System.Collections.Generic.Dictionary<string, string>();
            for (long e = Call(s, "TrnBrowseFirst", h).I; e == 0; e = Call(s, "TrnBrowseNext", h).I)
                comp[Field(s, h, "NAME")] =
                    Field(s, h, "TYPE")
                    + "|"
                    + Field(s, h, "AREA")
                    + "|"
                    + Field(s, h, "PRIV")
                    + "|"
                    + Field(s, h, "SAMPLEPER")
                    + "|"
                    + Field(s, h, "SPCFLAG")
                    + "|"
                    + Field(s, h, "DEADBAND");
            Assert.Equal("1|0|0|1|0|-1", comp["Meter_PV"]);
            Assert.Equal("2|0|0|2|0|-1", comp["Pump_FB"]);
            Assert.Equal("1|0|0|0.5|0|-1", comp["Tank_Band"]);
            Assert.Equal("1|0|0|2|1|-1", comp["Weight_SPC"]);
            Assert.Equal(1, Call(s, "TrnBrowseNumRecords", Open(s, "SPCFLAG=1", "NAME")).I);
            Assert.Equal(1, Call(s, "TrnBrowseNumRecords", Open(s, "TYPE=2", "NAME")).I);
        }

        [Fact]
        public void AValueWithoutATermMatchesEveryRecord()
        {
            var s = MakeData(Tables);
            long all = Call(s, "TrnBrowseNumRecords", Open(s, "", "NAME")).I;
            Assert.Equal(6, all);
            Assert.Equal(all, Call(s, "TrnBrowseNumRecords", Open(s, "ITEM=", "NAME")).I);
            Assert.Equal(all, Call(s, "TrnBrowseNumRecords", Open(s, "ITEM= , ;NAME=*", "NAME")).I);
            // a value with a term still filters
            Assert.Equal(2, Call(s, "TrnBrowseNumRecords", Open(s, "ITEM=PV|SP", "NAME")).I);
            // the equipment browse keeps its rule (not read in the runtime yet): "NAME=" is a name ''
            Assert.Equal(
                0,
                Call(s, "EquipBrowseNumRecords", Call(s, "EquipBrowseOpen", "NAME=", "NAME", "").I).I
            );
        }

        [Fact]
        public void TheCatalogueKeepsTheOtherColumnsAsSharedRows()
        {
            var s = MakeData(Tables);
            var tt = TrendTags.Of(s.W);
            var pv = tt.Find("Meter_PV")!;
            var sp = tt.Find("Meter_SP")!;
            Assert.Equal("Plant.Meter", pv.Equip);
            Assert.Equal("PV", pv.Item);
            Assert.Equal("TRUE", pv.Column("HISTORIAN"));
            Assert.Equal("Cluster1", pv.Column("cluster"));
            Assert.Equal("24:00:00", pv.Column("PERIOD"));
            Assert.Equal("", pv.Column("TAGGENLINK")); // not kept
            // the same values: one array
            Assert.Same(pv.Settings, sp.Settings);
            Assert.NotSame(pv.Settings, tt.Find("Pump_FB")!.Settings);
            // TrnInfo reads it: TYPE, SAMPLEPER, the file period
            Assert.Equal("1", Call(s, "TrnInfo", "Meter_PV", 1).S);
            Assert.Equal("2", Call(s, "TrnInfo", "Pump_FB", 1).S);
            Assert.Equal("86400", Call(s, "TrnInfo", "Meter_SP", 9).S);
        }
    }
}
