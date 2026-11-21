using System;
using System.IO;
using System.Linq;
using CicodeSim.Page;
using CicodeSim.Tests.World;
using CicodeSim.Vm;
using Xunit;
using static CicodeSim.Tests.Page.PageObjectTests;
using WorldT = CicodeSim.World.World;

namespace CicodeSim.Tests.Page
{
    /// <summary>Alarm categories on lists and formats.</summary>
    [Collection("World")]
    public class AlarmCategoryTests : IDisposable
    {
        private readonly TempDir _dir = new TempDir();
        private readonly WorldT _world;

        public AlarmCategoryTests()
        {
            WorldT.ClearCaches();
            string main = WorldFixture.MakeInstall(_dir.Path);
            string lib = Path.Combine(Path.GetDirectoryName(main)!, "Lib");
            var cols = WorldFixture.F(
                60,
                "CATEGORY",
                "PRIORITY",
                "SHOW_ALM",
                "SHOW_SUM",
                "DSPFMT",
                "SUMDSPFMT",
                "SOEDSPFMT"
            );
            WorldFixture.WriteDbf(
                Path.Combine(lib, "category.dbf"),
                cols,
                new[]
                {
                    WorldFixture.Row(
                        ("CATEGORY", "1"),
                        ("PRIORITY", "1"),
                        ("SHOW_ALM", "FALSE"),
                        ("SHOW_SUM", "TRUE")
                    ),
                    WorldFixture.Row(
                        ("CATEGORY", "2"),
                        ("PRIORITY", "2"),
                        ("SHOW_SUM", "FALSE"),
                        ("DSPFMT", "{Tag,12}^t{Name,20}"),
                        ("SUMDSPFMT", "{Name,20}{OnTime,8}")
                    ),
                    WorldFixture.Row(("CATEGORY", "_CAT_LBL"), ("PRIORITY", "3")),
                }
            );
            _world = new WorldT(main, start: WorldFixture.DefaultStart);
            _world.Start();
        }

        public void Dispose() => _dir.Dispose();

        private Animator Make()
        {
            var a = new Animator(_world, new CicodeSim.Compiled.Program());
            Assert.Equal(0, a.Open("TestPage", 0, compiled: new PageFixture().Build()));
            return a;
        }

        [Fact]
        public void ActiveListsSkipShowOnActiveFalseAndSummariesShowOnSummaryFalse()
        {
            var a = Make();
            var r = a.Run(0)!;
            Assert.Equal(0, Call(a, "AlarmDsp", 10, 10, 0, "", 0, "").v.I); // active
            Assert.Equal(0, Call(a, "AlarmDsp", 40, 10, 10, "", 0, "").v.I); // summary
            Assert.Equal(0, Call(a, "AlarmDsp", 60, 10, 4, "", 0, "").v.I); // configured
            _world.Tags.Set("Pump1_Run", 1); // ALM_PUMP (category 1) on
            a.RunFor(2.0);
            AlarmListState L(int an) => r.State.Objects[an].AlarmList!;
            Assert.Equal("on", _world.Alarms.PeekState(_world.Alarms.Get("ALM_PUMP")!)!.On ? "on" : "off");
            Assert.DoesNotContain(L(10).Rows, x => x.Tag == "ALM_PUMP");
            Assert.Contains(L(40).Rows, x => x.Tag == "ALM_PUMP");
            // [Alarm]ShowAllConfigured defaults to 1: the configured list keeps every category
            Assert.Contains(L(60).Rows, x => x.Tag == "ALM_PUMP");
            _world.Tags.Set("Pump1_Run", 0); // ALM_NOTPUMP (category 2) after 5 s
            a.RunFor(7.0);
            Assert.True(_world.Alarms.PeekState(_world.Alarms.Get("ALM_NOTPUMP")!)!.On);
            Assert.Contains(L(10).Rows, x => x.Tag == "ALM_NOTPUMP");
            Assert.DoesNotContain(L(40).Rows, x => x.Tag == "ALM_NOTPUMP");
            // the category's own display format
            var row = L(10).Rows.First(x => x.Tag == "ALM_NOTPUMP");
            Assert.Equal(new[] { "Tag", "Name" }, row.Fields.Select(f => f.Key).ToArray());
            Assert.True(_world.Alarms.ShownOnActive(2));
            Assert.False(_world.Alarms.ShownOnActive(1));
            Assert.True(_world.Alarms.ShownOnActive(77)); // no row: shown
            Assert.False(_world.Alarms.ShownOnSummary(2));
        }

        [Fact]
        public void AlarmListCreateWithoutAFormatTakesTheSaDefaultOfItsType()
        {
            var a = Make();
            var r = a.Run(0)!;
            Assert.Equal(0, Call(a, "AlarmListCreate", 20, 0, 1324, 144, 24, 1, "", "", -1, -1).v.I);
            Assert.Equal(0, Call(a, "AlarmListCreate", 100, 15, 1324, 144, 24, 1, "", "", -1, -1).v.I);
            Assert.Equal(0, Call(a, "AlarmListCreate", 200, 10, 1324, 144, 24, 1, "", "", -1, -1).v.I);
            Assert.Equal(0, Call(a, "AlarmListCreate", 300, 3, 1324, 144, 24, 1, "", "", -1, -1).v.I);
            a.RunScans(1);
            string[] Cols(int an) => r.State.Objects[an].AlarmList!.Columns.Select(c => c.Key).ToArray();
            // the live ExampleSA DefaultAlarm_HD1080 list (type 0, no format)
            Assert.Equal(
                new[]
                {
                    "PriorityAndState",
                    "OnDate",
                    "OnTime",
                    "Name",
                    "State",
                    "Cluster",
                    "Equipment",
                    "Item",
                    "UserName",
                    "Comment",
                    "Category",
                },
                Cols(20)
            );
            Assert.Equal(
                new[] { 25, 80, 90, 250, 40, 70, 220, 160, 100, 250, 60 },
                r.State.Objects[20].AlarmList!.Columns.Select(c => c.Value).ToArray()
            );
            Assert.Equal("Date", Cols(100)[1]);
            Assert.Contains("Message", Cols(100));
            Assert.Equal("SumState", Cols(200)[2]);
            Assert.Contains("DisableComment", Cols(300));
        }

        [Fact]
        public void FmtOpenOpensTheCategoryFormats()
        {
            var a = Make();
            a.Run(0);
            var (h, t) = Call(a, "FmtOpen", "2", "", 2);
            Assert.True(h.I >= 0);
            Assert.Equal(0, t.Error);
            Assert.Equal(2, Call(a, "FmtGetFieldCount", h.I).v.I);
            Assert.Equal("Tag", S(Call(a, "FmtGetFieldName", h.I, 0).v));
            Assert.Equal(12, Call(a, "FmtGetFieldWidth", h.I, 0).v.I);
            // the same template again (the library does not close it), 'Cat2' too
            Assert.Equal(h.I, Call(a, "FmtOpen", "2", "", 2).v.I);
            Assert.Equal(h.I, Call(a, "FmtOpen", "Cat2", "", 0).v.I);
            long sum = Call(a, "FmtOpen", "2", "", 3).v.I;
            Assert.True(sum >= 0 && sum != h.I);
            Assert.Equal("Name", S(Call(a, "FmtGetFieldName", sum, 0).v));
            // a category without such a format, or none: -1 without an error
            var (none, tn) = Call(a, "FmtOpen", "1", "", 2);
            Assert.Equal(-1, none.I);
            Assert.Equal(0, tn.Error);
            Assert.Equal(-1, Call(a, "FmtOpen", "2", "", 4).v.I);
            Assert.Equal(-1, Call(a, "FmtOpen", "9", "", 2).v.I);
            Assert.Equal(-1, Call(a, "FmtOpen", "Cat9", "", 0).v.I);
            // closed: opened again as a new template
            Assert.Equal(0, Call(a, "FmtClose", h.I).v.I);
            Assert.True(Call(a, "FmtOpen", "2", "", 2).v.I >= 0);
        }
    }
}
