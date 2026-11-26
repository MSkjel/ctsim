using System.Linq;
using CicodeSim.Sim;
using CicodeSim.Values;
using Xunit;

namespace CicodeSim.Tests.Host
{
    /// <summary>ctsim/checkExpression's warnings (Simulation.CheckField): names taken for tags the
    /// project does not define, and fixed text that reaches a page or tag argument (through
    /// Include's PageDisplay, AssWin ...) naming no page / tag of the project.</summary>
    public class CheckFieldTests
    {
        private static JsonValue Check(Simulation sim, string text, string kind = "expression")
        {
            var r = sim.Invoke(() => sim.CheckField(text, kind, ""));
            Assert.True(r["ok"].Bool, r.ToString());
            return r;
        }

        private static string[] Warnings(Simulation sim, string text, string kind = "expression") =>
            Check(sim, text, kind)["warnings"]
                .Items.Select(w =>
                    w["kind"].Str + " " + w["name"].Str + "@" + w["column"].Long + ": " + w["message"].Str
                )
                .ToArray();

        private static string Page(int col, string name) =>
            "page "
            + name
            + "@"
            + col
            + ": the project has no page "
            + name
            + " (the runtime cannot display it)";

        private static string Tag(int col, string name) =>
            "tag "
            + name
            + "@"
            + col
            + ": the project does not define the tag "
            + name
            + " (the runtime raises error 424 for it)";

        [ProjectFact]
        public void FixedPageAndTagNamesThatTheProjectLacksAreWarnings()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example, clock: "paused"))
            {
                // through Include's PageDisplay to _PageDisplay; a page the project has
                Assert.Equal(
                    new[] { Page(13, "NoSuchPage_q") },
                    Warnings(sim, "PageDisplay(\"NoSuchPage_q\")")
                );
                Assert.Empty(Warnings(sim, "PageDisplay(\"!About\")"));
                Assert.Equal(
                    new[] { Page(14, "NoSuchPage_q") },
                    Warnings(sim, "_PageDisplay(\"NoSuchPage_q\")")
                );
                // the identifier and the fixed text say the same
                Assert.Equal(new[] { Tag(9, "NoSuchTag_q") }, Warnings(sim, "TagRead(\"NoSuchTag_q\")"));
                Assert.Equal(new[] { Tag(1, "NoSuchTag_x") }, Warnings(sim, "NoSuchTag_x + 1"));
                Assert.Equal(
                    new[] { Tag(1, "NoSuchTag_x") },
                    Warnings(sim, "NoSuchTag_x + StrToInt(TagRead(\"NoSuchTag_x\"))")
                );
                Assert.Empty(Warnings(sim, "TagRead(\"Agitator_Cmd\")"));
                // in source order
                Assert.Equal(
                    new[] { Tag(1, "NoSuchTag_a"), Tag(32, "NoSuchTag_b"), Tag(50, "NoSuchTag_c") },
                    Warnings(
                        sim,
                        "NoSuchTag_a = StrToInt(TagRead(\"NoSuchTag_b\")) + NoSuchTag_c;",
                        "command"
                    )
                );
                // AssWin: the page to WinNewAt, the tags through AssVarTags to Ass; a ' literal,
                // a dotted name (an equipment: Ass's 289) and a trend tag's name are no tag
                Assert.Equal(
                    new[] { Page(8, "NoSuchPage_w"), Tag(33, "NoSuchTag_r") },
                    Warnings(
                        sim,
                        "AssWin(\"NoSuchPage_w\", 0, 0, 0, \"NoSuchTag_r\", \"'literal\", \"Some.Equip\", \"CPU\")"
                    )
                );
                // a command, in source order
                Assert.Equal(
                    new[] { Tag(10, "NoSuchTag_c"), Page(41, "NoSuchPage_c") },
                    Warnings(sim, "TagWrite(\"NoSuchTag_c\", 1); PageDisplay(\"NoSuchPage_c\");", "command")
                );
                // no lookup of the argument, or no fixed text
                Assert.Empty(Warnings(sim, "PageExists(\"NoSuchPage_z\")"));
                Assert.Empty(Warnings(sim, "MenuGetPageNode(\"NoSuchPage_m\")"));
                Assert.Empty(Warnings(sim, "PageDisplay(\"NoSuch\" + \"Page\")"));
                Assert.Empty(Warnings(sim, "PageDisplay(\"\")"));
                // the name trimmed
                Assert.Equal(
                    new[] { Page(13, "NoSuchPage_t") },
                    Warnings(sim, "PageDisplay(\" NoSuchPage_t \")")
                );
            }
        }

        [ProjectFact]
        public void SimulatingUndefinedTagsLeavesThePageWarnings()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example, clock: "paused"))
            {
                sim.Invoke(() => sim.World.Tags.SimulateUndefined = true);
                Assert.Empty(Warnings(sim, "TagRead(\"NoSuchTag_q\"); NoSuchTag_x = 1;", "command"));
                Assert.Equal(
                    new[] { Page(13, "NoSuchPage_q") },
                    Warnings(sim, "PageDisplay(\"NoSuchPage_q\")")
                );
            }
        }
    }
}
