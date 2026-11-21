using System;
using System.IO;
using System.Linq;
using CicodeSim.Page;
using CicodeSim.Tests.Vm;
using Xunit;

namespace CicodeSim.Tests.Page
{
    /// <summary>The SA trend genie.</summary>
    [Collection("World")]
    public class SaTrendGenieTests
    {
        private static readonly string ExSA = Path.Combine(Corpus.User, "ExampleSA");

        private static PageModel Fixture() =>
            PageModel.Load(
                Path.Combine(AppContext.BaseDirectory, "Page", "Fixtures", "ExampleSA_Others_trend.page.json")
            );

        private static Animator Loaded()
        {
            var a = Animator.ForProject(ExSA);
            var tags = a.World.Tags;
            Assert.Equal(0, tags.Set("Company.Library.Meter.FB", 55));
            Assert.Equal(0, tags.Set("Company.Library.Meter.OP", 60));
            Assert.Equal(0, tags.Set("Company.Library.Meter.PV", 70));
            Assert.Equal(0, tags.Set("Company.Library.Meter.SP", 65));
            Assert.Equal(0, a.Open("Others", 0, Fixture()));
            a.RunFor(0.0);
            // the four pens are set about 15 simulated seconds after the page opens
            a.RunScans(100);
            return a;
        }

        [CorpusFact(Needs = "engine/tests/CicodeSim.Tests/Page/Fixtures/ExampleSA_Others_trend.page.json")]
        public void OthersTrendGenieLoadsTheMeterPensAndTexts()
        {
            var a = Loaded();
            var s = a.Run(0)!.State.Objects;
            var tr = s[210].Trend!;
            Assert.Equal(
                new[] { "SA_Meters_FB", "SA_Meters_OP", "SA_Meters_PV", "SA_Meters_SP" },
                tr.Pens.Select(p => p.Tag).ToArray()
            );
            Assert.Equal(new[] { 1, 2, 3, 4 }, tr.Pens.Select(p => p.Pen).ToArray());
            // TrnSetSpan(StrToTime("01:00:00")) over the drawing's 289 samples
            Assert.Equal(3600.0, tr.Span, 6);
            Assert.Equal(289, tr.Samples);
            Assert.Equal(0, tr.Cursor);
            // the pen labels: the TrendName metadata the load task sets from the ITEM fields
            Assert.Equal("FB", s[8].Text);
            Assert.Equal("OP", s[9].Text);
            Assert.Equal("PV", s[10].Text);
            Assert.Equal("SP", s[15].Text);
            // TrnGetScaleStr(.., 0, 100 / 0, 1): the compiled default format 6.2 and the units
            Assert.Equal("100.00lt/s", s[52].Text);
            Assert.Equal("  0.00lt/s", s[6].Text);
            // TrnGetCursorValue(.., 0):#.## (width 7): the focus pen FB
            Assert.Equal("  55.00", s[28].Text);
            // SATrend_UpdateSpan: 3600 / 6 = 600 s, 'min' and 10 .. 60
            Assert.Equal("min", s[38].Text);
            Assert.Equal(
                new[] { "10", "20", "30", "40", "50", "60" },
                new[] { 193, 194, 195, 198, 208, 209 }.Select(an => s[an].Text).ToArray()
            );
            Assert.DoesNotContain(a.Logs, l => l.StartsWith("engine exception", StringComparison.Ordinal));
        }

        [CorpusFact(Needs = "engine/tests/CicodeSim.Tests/Page/Fixtures/ExampleSA_Others_trend.page.json")]
        public void TheGeniesPenSamplesFollowARamp()
        {
            var a = Loaded();
            var tags = a.World.Tags;
            // a ramp on FB: 0 .. 100, a step a second (the pen samples every 3600 / 289 s)
            double start = a.World.Clock.Now();
            for (int i = 0; i <= 100; i++)
            {
                Assert.Equal(0, tags.Set("Company.Library.Meter.FB", (double)i));
                a.RunFor(1.0);
            }
            var tr = a.Run(0)!.State.Objects[210].Trend!;
            var fb = tr.Pens[0];
            Assert.Equal("SA_Meters_FB", fb.Tag);
            double period = tr.Period;
            Assert.Equal(3600.0 / 289, period, 6);
            // oldest first, the newest at End: each known sample is the ramp's value at its time
            // (the value set at the whole second at or before it), unknown before the history
            int n = fb.Values.Length,
                known = 0;
            for (int k = 0; k < n; k++)
            {
                double at = fb.End - (n - 1 - k) * period;
                double? v = fb.Values[k];
                if (at < start || v == null)
                    continue;
                known++;
                double expect = Math.Min(100.0, Math.Max(0.0, Math.Floor(at - start + 1e-9)));
                Assert.Equal(expect, v!.Value, 6);
            }
            Assert.InRange(known, 6, 10);
            // the other pens keep their constant values
            Assert.All(tr.Pens[3].Values.Where(v => v != null), v => Assert.Equal(65.0, v!.Value, 6));
        }
    }
}
