using System.IO;
using CicodeSim.Page;
using CicodeSim.Values;
using Xunit;

namespace CicodeSim.Tests.Page
{
    /// <summary>How long a recorded case's page had been open.</summary>
    public class LiveCaseLeadTests
    {
        private static string? Root()
        {
            string? d = System.AppContext.BaseDirectory;
            while (d != null && !File.Exists(Path.Combine(d, "LICENSE")))
                d = Path.GetDirectoryName(d);
            return d;
        }

        private static readonly string Cases = Path.Combine(
            Root() ?? "",
            "conformance",
            "pages",
            "ExampleSA"
        );

        private static double Lead(string c) => LiveCase.CaseLead(Path.Combine(Cases, c));

        [Fact]
        public void APagesLaterCasesRanLongerInTheSameWindow()
        {
            if (!Directory.Exists(Cases))
                return;
            // the first (or only) case of its page's window
            Assert.Equal(0.0, Lead(Path.Combine("Others", "wind_compass")));
            Assert.Equal(0.0, Lead(Path.Combine("Meters", "meter")));
            Assert.Equal(0.0, Lead(Path.Combine("Filling_L3", "tank20")));
            Assert.Equal(0.0, Lead(Path.Combine("Overview_L2", "sequence")));
            // after the page's first case (the recordings: 14 to 23 s)
            Assert.InRange(Lead(Path.Combine("Others", "polar_star")), 10.0, 30.0);
            Assert.InRange(Lead(Path.Combine("Filling_L3", "batch_id")), 10.0, 30.0);
            Assert.InRange(Lead(Path.Combine("Overview_L2", "recipe_text")), 10.0, 30.0);
        }

        [Fact]
        public void ACaseRecordedAloneLaterHasNoLead()
        {
            using var dir = new CicodeSim.Tests.World.TempDir();
            void Case(string name, string recorded, double lastAt)
            {
                string d = Path.Combine(dir.Path, "Page", name);
                Directory.CreateDirectory(d);
                File.WriteAllText(
                    Path.Combine(d, "case.json"),
                    "{\"recorded\": \""
                        + recorded
                        + "\", \"frames\": [{\"name\": \"f0\", \"at\": 1.5}, {\"name\": \"f1\", \"at\": "
                        + lastAt.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "}]}"
                );
            }
            Case("a", "2026-10-01T15:40:56-07:00", 14.5);
            Case("b", "2026-10-01T15:41:07-07:00", 10.0); // started 0.5 s after a ended
            Case("c", "2026-10-02T09:00:00-07:00", 5.0); // re-recorded alone the next day
            Assert.Equal(0.0, LiveCase.CaseLead(Path.Combine(dir.Path, "Page", "a")));
            Assert.Equal(15.5, LiveCase.CaseLead(Path.Combine(dir.Path, "Page", "b")), 6);
            Assert.Equal(0.0, LiveCase.CaseLead(Path.Combine(dir.Path, "Page", "c")));
            var f = JsonValue.Parse("{\"at\": 2.5}");
            Assert.Equal(LiveCase.RecordedLead + 15.5 + 2.5, LiveCase.SettleOf(f, 15.5), 9);
            Assert.Equal(LiveCase.RecordedLead, LiveCase.SettleOf(JsonValue.Parse("{}")), 9);
        }
    }
}
