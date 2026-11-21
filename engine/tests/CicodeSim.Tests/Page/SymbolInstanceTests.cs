using System.Linq;
using CicodeSim.Builtins;
using CicodeSim.Page;
using CicodeSim.Sim;
using CicodeSim.Tests.Host;
using CicodeSim.World.Display;
using Xunit;

namespace CicodeSim.Tests.Page
{
    /// <summary>A genie DspSym places (Page/SymbolInstances.cs): the element read from its
    /// library, its compiled rows found by name (R2 ExampleSA, read only).</summary>
    [Collection("ctsim server")]
    public sealed class SymbolInstanceTests
    {
        [Fact]
        public void TheCompiledRowsOfADspSymGenieAreNamedBySha1()
        {
            Assert.Equal(
                "_528676CE3065E3038078B949897138EF77831674",
                LibraryElements.CompiledName("sa_controls.treeviewitem_hd1080")
            );
            Assert.Equal(
                "_528676CE3065E3038078B949897138EF77831674",
                LibraryElements.CompiledName(" SA_Controls.TreeviewItem_HD1080 ")
            );
        }

        [ProjectFact]
        public void TheTreeviewItemElementIsRead()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA))
            {
                var el = sim.Invoke(() =>
                    LibraryElements.Find(sim.World.Unit, "sa_controls.treeviewitem_hd1080")
                );
                Assert.NotNull(el);
                Assert.Equal(942, el!.OriginX);
                Assert.Equal(454, el.OriginY);
                Assert.Equal(8, el.Root.Type);
                Assert.Equal(14, el.Root.Handle);
                var item = Assert.Single(el.Root.Children);
                Assert.Equal("Item", item.Name);
                Assert.Equal(12, item.Handle);
                var names = item.Children.Select(c => c.Name).ToList();
                Assert.Equal(
                    new[] { "Properties", "Label", "ExpandMark", "Icon", "Expander", "Checkbox" },
                    names
                );
                var props = item.Children[0];
                Assert.Equal(0x0F, props.Type);
                Assert.Equal(new[] { 0x1010, 0x100B }, props.Children.Select(c => c.Type).ToArray());
                Assert.True(props.Children.All(c => c.IsInput));
                Assert.Equal(1016, props.Extent.L);
                Assert.Equal(456, props.Extent.T);
                Assert.Null(
                    sim.Invoke(() => LibraryElements.Find(sim.World.Unit, "sa_controls.no_such_genie"))
                );
            }
        }

        /// <summary>One call in window 0 (as a page task's), its result text.</summary>
        private static string Call(Simulation sim, string text)
        {
            CallOutcome? got = null;
            sim.Invoke(() =>
            {
                var t = sim.StartCall(text, o => got = o, noDebug: true, taskName: "test");
                TaskContext.Set(t, 0, -1);
                return 0;
            });
            SimTestUtil.Until(sim, () => got != null, 15000, text);
            Assert.True(got!.State != "halt", text + ": " + got.ErrorText);
            return got.Result;
        }

        [ProjectFact]
        public void DspSymOfATreeviewItemGivesItsMembersNamesMetadataAndRows()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.ExampleSA, clock: "run"))
            {
                Assert.Equal(0, sim.Invoke(() => sim.Pages.ShowAsCicode("Empty", 0))["error"].Long);
                int an = int.Parse(Call(sim, "DspAnNew(100, 200)"));
                Assert.Equal("0", Call(sim, "DspSym(" + an + ", \"sa_controls.treeviewitem_hd1080\", 1, 1)"));
                // the root is the AN (a group of the element's extent), its members are named
                int props = int.Parse(
                    Call(sim, "DspGetAnFromNameRelative(" + an + ", \".Item.Properties\")")
                );
                Assert.True(props > 0 && props != an);
                Assert.Equal(
                    "24",
                    Call(
                        sim,
                        "DspGetMetadataFromNameRelative(" + an + ", \".Item.Properties\", \"TreeItemIndent\")"
                    )
                );
                Assert.Equal(
                    "0",
                    Call(
                        sim,
                        "DspSetMetadataFromNameRelative(" + an + ", \".Item.Properties\", \"Row\", \"3\")"
                    )
                );
                Assert.Equal(
                    "3",
                    Call(sim, "DspGetMetadataFromNameRelative(" + an + ", \".Item.Properties\", \"Row\")")
                );
                // the element's origin (942, 454) is on the AN's point: Properties (1016, 456) -> (174, 202)
                var o = sim.Invoke(() => sim.World.Display.Get(0)!.Page!.Get(props)!);
                Assert.Equal(174, o.Extent.L);
                Assert.Equal(202, o.Extent.T);
                Assert.Equal("Properties", o.Name);
                // the rows join at the next scan, with the element's own pools
                SimTestUtil.Until(
                    sim,
                    () => sim.World.Display.Get(0)!.Page!.Runtime is PageRun r && r.Object(props) != null,
                    10000,
                    "the rows"
                );
                var run = sim.Invoke(() => (PageRun)sim.World.Display.Get(0)!.Page!.Runtime!);
                Assert.Contains(sim.Invoke(() => run.SymbolInstances()), x => x.An == an && x.Members == 16);
                Assert.NotNull(sim.Invoke(() => run.Object(props)!.Program));
                // the element's colour-state array: the members' colour classes read it
                Assert.NotNull(sim.Invoke(() => run.Object(props)!.Drawing?.Colour));
                // a drawing summary that comes later (ctsim/pageModel) keeps the genie placed
                var model = CicodeSim.Page.PageModel.FromJson(
                    CicodeSim.Values.JsonValue.Parse(
                        "{\"format\": \"ctsim-page/0\", \"page\": \"Empty\", \"size\": [800, 600], \"objects\": []}"
                    )
                );
                sim.Invoke(() => sim.Pages.AttachModel(0, model));
                Assert.Equal(
                    props,
                    int.Parse(Call(sim, "DspGetAnFromNameRelative(" + an + ", \".Item.Properties\")"))
                );
                Assert.NotNull(sim.Invoke(() => run.Object(props)!.Drawing?.Colour));
                // the same DspSym again keeps the genie: its members' ANs and metadata
                int nextAn = sim.Invoke(() => sim.World.Display.Get(0)!.Page!.NextDynamicAn);
                Assert.Equal("0", Call(sim, "DspSym(" + an + ", \"sa_controls.treeviewitem_hd1080\", 1, 1)"));
                Assert.Equal(
                    props,
                    int.Parse(Call(sim, "DspGetAnFromNameRelative(" + an + ", \".Item.Properties\")"))
                );
                Assert.Equal(
                    "3",
                    Call(sim, "DspGetMetadataFromNameRelative(" + an + ", \".Item.Properties\", \"Row\")")
                );
                Assert.Equal(nextAn, sim.Invoke(() => sim.World.Display.Get(0)!.Page!.NextDynamicAn));
                // DspAnMove moves the members too
                Assert.Equal("0", Call(sim, "DspAnMove(" + an + ", 110, 230)"));
                Assert.Equal(184, sim.Invoke(() => DisplayAnsAccess.Left(sim, props)));
                // ... and the same DspSym after the move still keeps them where they went
                Assert.Equal("0", Call(sim, "DspSym(" + an + ", \"sa_controls.treeviewitem_hd1080\", 1, 1)"));
                Assert.Equal(
                    props,
                    int.Parse(Call(sim, "DspGetAnFromNameRelative(" + an + ", \".Item.Properties\")"))
                );
                Assert.Equal(184, sim.Invoke(() => DisplayAnsAccess.Left(sim, props)));
                // DspDel: the members go
                Call(sim, "DspDel(" + an + ")");
                Assert.Equal("-1", Call(sim, "DspGetAnFromNameRelative(" + an + ", \".Item.Properties\")"));
                Assert.Empty(sim.Invoke(() => run.SymbolInstances()));
                // an alarm priority's thumbnail genie (type 3, almprior.dbf): its '.Shape'
                int an2 = int.Parse(Call(sim, "DspAnNew(300, 300)"));
                Assert.Equal("0", Call(sim, "DspSym(" + an2 + ", \"1\", 1, 3)"));
                Assert.True(int.Parse(Call(sim, "DspGetAnFromNameRelative(" + an2 + ", \".Shape\")")) > 0);
                // a plain symbol: nothing placed
                int an3 = int.Parse(Call(sim, "DspAnNew(400, 300)"));
                Assert.Equal("0", Call(sim, "DspSym(" + an3 + ", \"sa_icons.no_such_symbol\", 0, 0)"));
                Assert.Equal("-1", Call(sim, "DspGetAnFromNameRelative(" + an3 + ", \".Shape\")"));
            }
        }

        private static class DisplayAnsAccess
        {
            /// <summary>The object's current left edge (DspGetAnLeft) in window 0.</summary>
            public static int Left(Simulation sim, int an)
            {
                var w = sim.World.Display.Get(0)!;
                var o = w.Page!.Get(an)!;
                return o.Extent.L + o.MovedX;
            }
        }
    }
}
