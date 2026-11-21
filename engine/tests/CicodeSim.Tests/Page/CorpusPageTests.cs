using System;
using System.IO;
using CicodeSim.Page;
using CicodeSim.Tests.Vm;
using CicodeSim.Values;
using CicodeSim.World.Display;
using Xunit;

namespace CicodeSim.Tests.Page
{
    /// <summary>The animator on compiled R2 pages (read only) with the hand-written ctsim-page/0
    /// fixtures of Page/Fixtures (their rects and colours are made up, not read from
    /// the drawings: the derived numbers are checked against them).</summary>
    [Collection("World")]
    public class CorpusPageTests
    {
        private static readonly string ExSA = Path.Combine(Corpus.User, "ExampleSA");
        private static readonly string Ex = Path.Combine(Corpus.User, "Example");

        private static PageModel Fixture(string name) =>
            PageModel.Load(Path.Combine(AppContext.BaseDirectory, "Page", "Fixtures", name));

        [CorpusFact(Needs = "engine/tests/CicodeSim.Tests/Page/Fixtures/ExampleSA_Others.page.json")]
        public void OthersRotatesTheCompassNeedleByWindPV()
        {
            var a = Animator.ForProject(ExSA);
            Assert.Equal(0, a.World.Tags.Set("Company.Library.WindCompass.WindPV", 90));
            Assert.Equal(0, a.Open("Others", 0, Fixture("ExampleSA_Others.page.json")));
            var s = a.Run(0)!.State.Objects;
            // AN 106: ROT = WindPV ^(0, 360) over 0 .. 360 degrees (Load; Bar 0..360)
            Assert.Equal(90.0, s[106].Rotate!.Value, 9);
            // Express centre of (1180, 380, 1221, 461): 41 / 2, 81 / 2
            Assert.Equal(new[] { 1200.0, 420.0 }, s[106].RotateOrigin);
            // AN 126: a constant 240 over 0..360, the pivot at the bottom (ROTYORG 1)
            Assert.Equal(240.0, s[126].Rotate!.Value, 9);
            Assert.Equal(new[] { 620.0, 380.0 }, s[126].RotateOrigin);
            // AN 83: a constant 225 (Bar 0..100 clamps it to 100 %) over 45..225: at maximum
            Assert.Equal(225.0, s[83].Rotate!.Value, 9);
            a.World.Tags.Set("Company.Library.WindCompass.WindPV", 225);
            a.RunScans(1);
            Assert.Equal(225.0, s[106].Rotate!.Value, 9);
            // the vertex objects: three vertices each
            Assert.Equal(3, s[347].VertexOffsets!.Count);
            Assert.Equal(3, s[390].VertexOffsets!.Count);
            // the page opened without an engine exception
            Assert.DoesNotContain(a.Logs, l => l.StartsWith("engine exception", StringComparison.Ordinal));
        }

        [CorpusFact(Needs = "engine/tests/CicodeSim.Tests/Page/Fixtures/Example_Dynamic_1.page.json")]
        public void Dynamic1GradientFillLevelAndCustomPivot()
        {
            if (!File.Exists(Path.Combine(Ex, "_LIBRARY.RDB")))
                return;
            var a = Animator.ForProject(Ex);
            var tags = a.World.Tags;
            // the Percentage loads (TAGREF Percentage 1) read the percent of the tag's range
            (double z, double f) Range(string tag) => tags.Find(tag)!.EngRange();
            var (hz, hf) = Range("H_Fill");
            var (vz, vf) = Range("V_Fill");
            tags.Set("H_Fill", hf);
            tags.Set("V_Fill", vz + (vf - vz) / 2);
            Assert.Equal(0, a.Open("Dynamic_1", 0, Fixture("Example_Dynamic_1.page.json")));
            var s = a.Run(0)!.State.Objects;
            // AN 527: gradient at 100 % = COL_MAXON (blue), within the HLS rounding
            uint c = (uint)s[527].FillColour!.Value;
            Assert.True((c & 0xFF) >= 0xFE && (c >> 8) == 0, $"0x{c:x6}");
            // AN 518: fill level half way
            Assert.Equal(0.5, s[518].FillLevel!.Value, 6);
            // AN 520: Custom rotation axis (-20, -20) from the centre of (300, 200, 361, 251)
            Assert.Equal(new[] { 300.0 + 30 - 20, 200.0 + 25 - 20 }, s[520].RotateOrigin);
            tags.Set("H_Fill", hz);
            a.RunScans(1);
            c = (uint)s[527].FillColour!.Value;
            Assert.True((c >> 16) >= 0xFE && (c & 0xFFFF) == 0, $"0x{c:x6}");
            Assert.DoesNotContain(a.Logs, l => l.StartsWith("engine exception", StringComparison.Ordinal));
        }

        /// <summary>Every ExampleSA page opens and scans with the World's default tags without
        /// an engine exception (the acceptance sweep, ctsim-dev page-sweep).</summary>
        [CorpusFact]
        public void EveryExampleSAPageOpensAndScans()
        {
            if (Environment.GetEnvironmentVariable("CTG_SKIP_HEAVY") == "1")
                return;
            var a = Animator.ForProject(ExSA);
            int opened = 0;
            foreach (var p in a.Display.Catalogue!.Pages)
            {
                Assert.Equal(0, a.Open(p.Name, 0));
                a.RunScans(2, 0);
                opened++;
            }
            Assert.Equal(157, opened);
            Assert.DoesNotContain(a.Logs, l => l.StartsWith("engine exception", StringComparison.Ordinal));
        }
    }
}
