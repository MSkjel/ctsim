using System.Collections.Generic;
using CicodeSim.Page;
using CicodeSim.World.Display;
using Xunit;

namespace CicodeSim.Tests.Page
{
    /// <summary>The fill colour classes.</summary>
    public class ColourTests
    {
        private static ColourStates States() =>
            new ColourStates
            {
                State0 = new ColourPair(0xC0C0C0, 0xC0C0C0),
                MaxOn = new ColourPair(0xFF0000, 0x000000),
                Sta =
                {
                    ColourPair.Steady(0x111111),
                    ColourPair.Steady(0x222222),
                    ColourPair.Steady(0x333333),
                },
            };

        [Fact]
        public void OnOffAndMultiState()
        {
            var cs = States();
            Assert.Equal(cs.State0, Colours.OnOff(0, cs));
            Assert.Equal(cs.MaxOn, Colours.OnOff(-3, cs));
            Assert.Equal(ColourPair.Steady(0x00FF00), Colours.OnOff(1, null)); // COL_MAXON's default green
            Assert.Null(Colours.OnOff(0, null));
            // A is the low bit: index = A + 2B + 4C; a state without a colour takes the defaults
            Assert.Equal(ColourPair.Steady(0x222222), Colours.MultiState(1, cs));
            Assert.Equal(ColourPair.Steady(0xFF00FF), Colours.MultiState(4, cs));
            Assert.Equal(ColourPair.Steady(0xBFBFBF), Colours.MultiState(7, null));
            Assert.Equal(3, Colours.Conditions(8, 1));
            Assert.Equal(2, Colours.Conditions(4, 5));
            Assert.Equal(3, Colours.Conditions(0, 3));
            Assert.Equal(1, Colours.Conditions(0, 0));
        }

        [Fact]
        public void ArrayIndexClampsWithAnError()
        {
            Assert.Equal(5, Colours.ArrayIndex(5, 256, out bool e));
            Assert.False(e);
            Assert.Equal(0, Colours.ArrayIndex(-1, 256, out e));
            Assert.True(e);
            Assert.Equal(255, Colours.ArrayIndex(300, 256, out e));
            Assert.True(e);
        }

        [Fact]
        public void ThresholdsSortThenTheFirstMatchWins()
        {
            // the defaults: <5 0x0000FF, <15 0x000080, >85 0x800000, >95 0xFF0000
            var sorted = Colours.Sort(Colours.DefaultThresholds);
            Assert.Equal(0x0000FFu, Colours.ThresholdColour(3, sorted)!.Value.On);
            Assert.Equal(0x000080u, Colours.ThresholdColour(10, sorted)!.Value.On);
            Assert.Null(Colours.ThresholdColour(50, sorted)); // none: the static colour
            Assert.Equal(0x800000u, Colours.ThresholdColour(90, sorted)!.Value.On);
            Assert.Equal(0xFF0000u, Colours.ThresholdColour(99, sorted)!.Value.On);
            Assert.Null(Colours.ThresholdColour(85, sorted)); // '>' is strict
            // unsorted input: '<' ascending, '>' descending
            var mixed = new List<Threshold>
            {
                new Threshold(0, 15, ColourPair.Steady(2)),
                new Threshold(1, 85, ColourPair.Steady(3)),
                new Threshold(0, 5, ColourPair.Steady(1)),
                new Threshold(1, 95, ColourPair.Steady(4)),
            };
            var s = Colours.Sort(mixed);
            Assert.Equal(1u, Colours.ThresholdColour(1, s)!.Value.On);
            Assert.Equal(4u, Colours.ThresholdColour(97, s)!.Value.On);
            // at an equal value the inclusive test comes first: <= before <, >= before >
            var eq = Colours.Sort(
                new List<Threshold>
                {
                    new Threshold(2, 10, ColourPair.Steady(7)),
                    new Threshold(0, 10, ColourPair.Steady(8)),
                }
            );
            Assert.Equal(2, eq[0].Cmp);
            Assert.Equal(7u, Colours.ThresholdColour(10, eq)!.Value.On); // 10 <= 10, not < 10
            var ge = Colours.Sort(
                new List<Threshold>
                {
                    new Threshold(3, 50, ColourPair.Steady(5)),
                    new Threshold(1, 50, ColourPair.Steady(6)),
                }
            );
            Assert.Equal(3, ge[0].Cmp);
        }

        /// <summary>Each channel within 1 (the HLS-to-RGB step's m1 / m2 are register arguments
        /// lost: the standard model is a guess, verify).</summary>
        private static void Near(uint want, uint got)
        {
            for (int sh = 0; sh <= 16; sh += 8)
            {
                int a = (int)((want >> sh) & 0xFF),
                    b = (int)((got >> sh) & 0xFF);
                Assert.True(System.Math.Abs(a - b) <= 1, $"0x{want:x6} vs 0x{got:x6}");
            }
        }

        [Fact]
        public void HlsOfThePrimaries()
        {
            // L = (max + min) / 2, S = diff * 255 / sum + 0.5, hue in degrees
            var red = Colours.ToHls(0xFF0000);
            Assert.Equal(0, red.H);
            Assert.Equal(127, red.L);
            Assert.Equal(255, red.S);
            Assert.Equal(120, Colours.ToHls(0x00FF00).H);
            Assert.Equal(240, Colours.ToHls(0x0000FF).H);
            Assert.Equal(60, Colours.ToHls(0xFFFF00).H);
            Assert.Equal(300, Colours.ToHls(0xFF00FF).H);
            // max - min of 2 or less is grey: no hue, S 0
            var g = Colours.ToHls(0x808082);
            Assert.Equal(Colours.NoHue, g.H);
            Assert.Equal(0, g.S);
            Assert.Equal(0x818181u, Colours.FromHls(g));
            foreach (
                uint c in new uint[]
                {
                    0xFF0000,
                    0x00FF00,
                    0x0000FF,
                    0xFFFF00,
                    0x00FFFF,
                    0xFF00FF,
                    0x808080,
                    0xFFFFFF,
                    0x000000,
                    0x336699,
                }
            )
                Near(c, Colours.FromHls(Colours.ToHls(c)));
        }

        [Fact]
        public void GradientInterpolatesInHls()
        {
            // the endpoints: the static colour at 0 %, COL_MAXON at 100 %, clamped beyond
            Near(0xFF0000, Colours.GradientOne(0, 0xFF0000, 0x0000FF));
            Near(0x0000FF, Colours.GradientOne(100, 0xFF0000, 0x0000FF));
            Assert.Equal(
                Colours.GradientOne(100, 0xFF0000, 0x0000FF),
                Colours.GradientOne(150, 0xFF0000, 0x0000FF)
            );
            Assert.Equal(
                Colours.GradientOne(0, 0xFF0000, 0x0000FF),
                Colours.GradientOne(-20, 0xFF0000, 0x0000FF)
            );
            // red (hue 0) to blue (hue 240) passes through green (hue 120): HLS, not RGB
            uint mid = Colours.GradientOne(50, 0xFF0000, 0x0000FF);
            Near(0x00FF00, mid);
            Assert.Equal(120, Colours.ToHls(mid).H);
            // a grey end holds the other end's hue: black to red stays red while L and S rise
            uint dark = Colours.GradientOne(50, 0x000000, 0xFF0000);
            Assert.Equal(0, Colours.ToHls(dark).H);
            Assert.True(((dark >> 16) & 0xFF) > ((dark >> 8) & 0xFF));
            Assert.Equal((dark >> 8) & 0xFF, dark & 0xFF);
            // the on and off halves interpolate each
            var pair = Colours.Gradient(
                50,
                new ColourPair(0xFF0000, 0xFF0000),
                new ColourPair(0x0000FF, 0xFF0000)
            );
            Assert.Equal(mid, pair.On);
            Near(0xFF0000, pair.Off);
        }
    }
}
