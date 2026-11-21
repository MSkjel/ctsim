using System;
using System.Collections.Generic;
using CicodeSim.World.Display;

namespace CicodeSim.Page
{
    /// <summary>The fill colour classes' arithmetic over the colour-state array.</summary>
    public static class Colours
    {
        /// <summary>COL_MAXON's default: green (doc: 1175478).</summary>
        public const uint DefaultMaxOn = 0x00FF00;

        /// <summary>A Multi-state state without a stored colour (ctgedit: fillcolour.STATE_DEFAULTS
        /// = CtDraw): green, yellow, red, cyan, magenta, white, blue, grey.</summary>
        public static readonly uint[] StateDefaults =
        {
            0x00FF00,
            0xFFFF00,
            0xFF0000,
            0x00FFFF,
            0xFF00FF,
            0xFFFFFF,
            0x0000FF,
            0xBFBFBF,
        };

        /// <summary>The thresholds a record without stored ones shows (ctgedit:
        /// fillcolour.DEFAULT_THRESHOLDS = CtDraw).</summary>
        public static readonly Threshold[] DefaultThresholds =
        {
            new Threshold(0, 5, ColourPair.Steady(0x0000FF)),
            new Threshold(0, 15, ColourPair.Steady(0x000080)),
            new Threshold(1, 85, ColourPair.Steady(0x800000)),
            new Threshold(1, 95, ColourPair.Steady(0xFF0000)),
        };

        /// <summary>On / Off: value != 0 = the COL_MAXON pair, else the
        /// static pair (null when the drawing gives none).</summary>
        public static ColourPair? OnOff(long v, ColourStates? cs)
        {
            if (v != 0)
                return cs?.MaxOn ?? ColourPair.Steady(DefaultMaxOn);
            return cs?.State0;
        }

        /// <summary>The Multi-state colour of state <paramref name="index"/>: COL_STA_index, else
        /// the state default.</summary>
        public static ColourPair MultiState(int index, ColourStates? cs)
        {
            if (cs != null && index >= 0 && index < cs.Sta.Count)
                return cs.Sta[index];
            return ColourPair.Steady(StateDefaults[index & 7]);
        }

        /// <summary>The number of Multi-state conditions: log2 of the record's state count when it
        /// has one, else the last condition expression present (A..E), at least 1.</summary>
        public static int Conditions(int stateCount, int lastPresent)
        {
            if (stateCount > 1)
            {
                int n = 0;
                while ((1 << n) < stateCount && n < 5)
                    n++;
                return Math.Max(1, n);
            }
            return Math.Max(1, lastPresent);
        }

        /// <summary>An array index as the colour / text arrays clamp it: &lt; 0 -&gt; 0, &gt;= count -&gt; the last, both an error.</summary>
        public static int ArrayIndex(long v, int count, out bool error)
        {
            error = false;
            if (v < 0)
            {
                error = true;
                return 0;
            }
            if (count > 0 && v >= count)
            {
                error = true;
                return count - 1;
            }
            return (int)v;
        }

        /// <summary>The thresholds in the order the class checks them (a
        /// pairwise exchange sort: '&lt;' / '&lt;=' ascending, '&gt;' / '&gt;=' descending, and at an
        /// equal value the inclusive test first: '&lt;=' before '&lt;', '&gt;=' before '&gt;').</summary>
        public static List<Threshold> Sort(IReadOnlyList<Threshold> input)
        {
            var e = new List<Threshold>(input);
            int n = e.Count;
            for (int i = 0; i < n - 1; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    Threshold a = e[i],
                        b = e[j];
                    bool swap;
                    if (a.Cmp == b.Cmp)
                        swap = a.Cmp == 0 || a.Cmp == 2 ? b.Percent < a.Percent : a.Percent < b.Percent;
                    else
                        switch (a.Cmp)
                        {
                            case 0:
                                swap = !(a.Percent < b.Percent) && b.Cmp == 2;
                                break;
                            case 1:
                                swap = !(b.Percent < a.Percent) && b.Cmp == 3;
                                break;
                            case 2:
                                swap = !(a.Percent <= b.Percent) && b.Cmp == 0;
                                break;
                            case 3:
                                swap = !(b.Percent <= a.Percent) && b.Cmp == 1;
                                break;
                            default:
                                swap = false;
                                break;
                        }
                    if (swap)
                    {
                        e[i] = b;
                        e[j] = a;
                    }
                }
            }
            return e;
        }

        /// <summary>The first threshold the value meets (0 v &lt; t, 1 v &gt; t,
        /// 2 v &lt;= t, 3 v &gt;= t), null when none (the static colour).</summary>
        public static ColourPair? ThresholdColour(double v, IReadOnlyList<Threshold> sorted)
        {
            foreach (var t in sorted)
            {
                bool hit;
                switch (t.Cmp)
                {
                    case 0:
                        hit = v < t.Percent;
                        break;
                    case 1:
                        hit = t.Percent <= v && v != t.Percent;
                        break;
                    case 2:
                        hit = !(t.Percent < v);
                        break;
                    case 3:
                        hit = !(v < t.Percent);
                        break;
                    default:
                        continue;
                }
                if (hit)
                    return t.Colour;
            }
            return null;
        }

        // ------------------------------------------------------------------ gradient (HLS)
        /// <summary>Hue, lightness, saturation as the runtime keeps them: hue in degrees
        /// 0..359, 0x1000 = undefined (grey); L and S 0..255.</summary>
        public struct Hls
        {
            public int H,
                L,
                S;
        }

        public const int NoHue = 0x1000;

        /// <summary>RGB (0x00RRGGBB) to HLS: a colour whose max - min is at
        /// most 2 is grey (hue undefined, S 0); L = (max + min) / 2; S = diff * 255 / sum (or
        /// / (511 - sum) when L &gt; 127) + 0.5; hue = 60 x the sector, + 360 when negative, + 0.5.</summary>
        public static Hls ToHls(uint rgb)
        {
            int r = (int)((rgb >> 16) & 0xFF),
                g = (int)((rgb >> 8) & 0xFF),
                b = (int)(rgb & 0xFF);
            int max = Math.Max(r, Math.Max(g, b)),
                min = Math.Min(r, Math.Min(g, b));
            var h = new Hls { H = NoHue, S = 0 };
            int diff = max - min;
            if (max == min || Math.Abs(diff) <= 2)
            {
                h.L = (min + max) >> 1;
                return h;
            }
            int sum = max + min;
            h.L = (sum >> 1) & 0xFFFF;
            if (h.L > 0x7f)
                sum = 0x1ff - max - min;
            h.S = (int)(diff * 255.0 / sum + 0.5) & 0xff;
            double hue;
            if (r == max)
                hue = (double)(g - b) / diff;
            else if (g == max)
                hue = (double)(b - r) / diff + 2.0;
            else
                hue = (double)(r - g) / diff + 4.0;
            hue *= 60.0;
            if (hue < 0.0)
                hue += 360.0;
            h.H = (short)(int)(hue + 0.5);
            return h;
        }

        /// <summary>HLS to RGB.</summary>
        public static uint FromHls(Hls c)
        {
            if (c.H == NoHue || c.S == 0)
            {
                uint l = (uint)(c.L & 0xFF);
                return (l << 16) | (l << 8) | l;
            }
            int hue = c.H;
            while (hue > 359)
                hue -= 360;
            while (hue < 0)
                hue += 360;
            double L = c.L / 255.0,
                S = c.S / 255.0;
            double m2 = c.L <= 127 ? L * (1.0 + S) : L + S - L * S;
            double m1 = 2.0 * L - m2;
            int ch(double h)
            {
                if (h > 360.0)
                    h -= 360.0;
                else if (h < 0.0)
                    h += 360.0;
                double v;
                if (h < 60.0)
                    v = (m2 - m1) * h / 60.0 + m1;
                else if (h < 180.0)
                    v = m2;
                else if (h < 240.0)
                    v = (m2 - m1) * (240.0 - h) / 60.0 + m1;
                else
                    v = m1;
                return (int)(v * 255.0 + 0.5) & 0xFF;
            }
            int r = ch(hue + 120.0),
                g = ch(hue),
                b = ch(hue - 120.0);
            return ((uint)r << 16) | ((uint)g << 8) | (uint)b;
        }

        /// <summary>Round half away from zero to an int, as the gradient channels are
        /// (+/-0.5 then truncation).</summary>
        private static int RoundAway(double v) => (int)(v >= 0.0 ? v + 0.5 : v - 0.5);

        /// <summary>One colour of the gradient: each HLS channel linear from <paramref name="lo"/>
        /// (at 0 %) to <paramref name="hi"/> (at 100 %), the percent clamped to 0..100; a grey end
        /// holds the other end's hue.</summary>
        public static uint GradientOne(double percent, uint lo, uint hi)
        {
            double p =
                percent > 100.0 ? 100.0
                : percent < 0.0 || double.IsNaN(percent) ? 0.0
                : percent;
            Hls a = ToHls(lo),
                b = ToHls(hi);
            int ha = a.H,
                hb = b.H;
            var r = new Hls();
            if (ha == NoHue && hb == NoHue)
                r.H = NoHue;
            else
            {
                if (ha == NoHue)
                    ha = hb;
                if (hb == NoHue)
                    hb = ha;
                r.H = RoundAway(ha + (hb - ha) * p / 100.0);
            }
            r.L = RoundAway(a.L + (b.L - a.L) * p / 100.0);
            r.S = RoundAway(a.S + (b.S - a.S) * p / 100.0);
            return FromHls(r);
        }

        /// <summary>The gradient pair at a percent: the on halves and the off halves each.</summary>
        public static ColourPair Gradient(double percent, ColourPair lo, ColourPair hi) =>
            new ColourPair(GradientOne(percent, lo.On, hi.On), GradientOne(percent, lo.Off, hi.Off));
    }
}
