using System;
using System.Collections.Generic;
using System.Globalization;
using CicodeSim.Project;
using CicodeSim.World;

namespace CicodeSim.Builtins
{
    /// <summary>The SPC series of an SPC tag (SPC Formulas and Constants, doc 1217198):
    /// subgroups of SubgroupSize samples a sample period apart, on the subgroup period's grid;
    /// each subgroup's mean, range and standard deviation; over a run of subgroups the process
    /// values, the control limits (Control Chart Line Constants, doc 1217199) and the SPC alarms
    /// (doc 1217197).</summary>
    public static class SpcSeries
    {
        /// <summary>A2, D3, D4, B3, B4 by subgroup size 1..25.</summary>
        private static readonly double[][] Constants =
        {
            new[] { 2.660, 0, 3.267, 0, 3.267 },
            new[] { 1.880, 0, 3.267, 0, 3.267 },
            new[] { 1.023, 0, 2.574, 0, 2.568 },
            new[] { 0.729, 0, 2.282, 0, 2.266 },
            new[] { 0.577, 0, 2.114, 0, 2.089 },
            new[] { 0.483, 0, 2.004, 0.030, 1.970 },
            new[] { 0.419, 0.076, 1.924, 0.118, 1.882 },
            new[] { 0.373, 0.136, 1.864, 0.185, 1.815 },
            new[] { 0.337, 0.184, 1.816, 0.239, 1.761 },
            new[] { 0.308, 0.223, 1.777, 0.284, 1.716 },
            new[] { 0.285, 0.256, 1.744, 0.321, 1.679 },
            new[] { 0.266, 0.283, 1.717, 0.354, 1.646 },
            new[] { 0.249, 0.307, 1.693, 0.382, 1.618 },
            new[] { 0.235, 0.328, 1.672, 0.406, 1.594 },
            new[] { 0.223, 0.347, 1.653, 0.428, 1.572 },
            new[] { 0.212, 0.363, 1.637, 0.448, 1.552 },
            new[] { 0.203, 0.378, 1.622, 0.466, 1.534 },
            new[] { 0.194, 0.391, 1.608, 0.482, 1.518 },
            new[] { 0.187, 0.403, 1.597, 0.497, 1.503 },
            new[] { 0.180, 0.415, 1.585, 0.510, 1.490 },
            new[] { 0.173, 0.425, 1.575, 0.523, 1.477 },
            new[] { 0.167, 0.434, 1.566, 0.534, 1.466 },
            new[] { 0.162, 0.443, 1.557, 0.545, 1.455 },
            new[] { 0.157, 0.451, 1.548, 0.555, 1.445 },
            new[] { 0.153, 0.459, 1.541, 0.565, 1.435 },
        };

        /// <summary>d2 of one sample a subgroup: the process standard deviation is then the mean
        /// moving range over d2.</summary>
        private const double D2One = 1.128;

        /// <summary>[SPC]AlarmBufferSize's default: the subgroups of the alarm calculations.</summary>
        public const int DefaultAlarmBuffer = 39;

        public sealed class Subgroup
        {
            public long End;
            public double[] Samples = Array.Empty<double>();
            public double Mean,
                Range,
                StdDev;
        }

        /// <summary>A run of subgroups and its process values (invalid when no subgroup is
        /// known) and control limits.</summary>
        public sealed class Run
        {
            public int Size = 1;

            /// <summary>The subgroups oldest first; null where a sample is unknown.</summary>
            public readonly List<Subgroup?> Groups = new List<Subgroup?>();
            public double Mean = Trends.Invalid,
                Range = Trends.Invalid,
                StdDev = Trends.Invalid,
                Sigma = Trends.Invalid;
            public double XUcl = Trends.Invalid,
                XLcl = Trends.Invalid,
                RUcl = Trends.Invalid,
                RLcl = Trends.Invalid,
                SUcl = Trends.Invalid,
                SLcl = Trends.Invalid;

            public bool Known => Trends.Validity(Mean) == 0;

            public int KnownCount
            {
                get
                {
                    int n = 0;
                    foreach (var g in Groups)
                        if (g != null)
                            n++;
                    return n;
                }
            }
        }

        /// <summary>The samples in a subgroup (the SPC tag's SUBGRPSIZE, 1..25).</summary>
        public static int Size(World.World w, TrendTag tag)
        {
            int n = SpcSettings.Of(w).Find(tag.Name)?.SubgroupSize ?? SpcSettings.DefaultSubgroupSize;
            return Math.Max(1, Math.Min(25, n));
        }

        /// <summary>The time between samples in 100 ns ticks (SAMPLEPER, 1 s when none).</summary>
        public static long SampleTicks(TrendTag tag) =>
            Math.Max(TrendData.MinPeriod, (long)Math.Round(Trends.SamplePeriod(tag) * TrendData.Tick));

        /// <summary>The time a subgroup takes: its samples x the sample period.</summary>
        public static long SubgroupTicks(World.World w, TrendTag tag) => SampleTicks(tag) * Size(w, tag);

        /// <summary>The end of the newest subgroup complete at FILETIME <paramref name="ft"/> (on
        /// the subgroup period's grid, guess).</summary>
        public static long EndAt(World.World w, TrendTag tag, long ft)
        {
            long g = SubgroupTicks(w, tag);
            return ft / g * g;
        }

        /// <summary>The subgroup ending at <paramref name="end"/>: its samples a sample period
        /// apart, null when one is unknown ([SPC]PartialSubgroup 0: an incomplete subgroup is not
        /// used). One sample a subgroup: its range is the moving range from the previous
        /// subgroup (<paramref name="previous"/>, invalid without one) and its standard deviation
        /// that of the two samples (guess).</summary>
        public static Subgroup? At(World.World w, TrendTag tag, long end, Subgroup? previous)
        {
            int n = Size(w, tag);
            long per = SampleTicks(tag);
            var xs = new double[n];
            for (int j = 0; j < n; j++)
            {
                double? v = TrendData.TrendValueAt(w, tag.Name, end - (n - 1 - j) * per);
                if (v == null)
                    return null;
                xs[j] = v.Value;
            }
            var g = new Subgroup { End = end, Samples = xs };
            double sum = 0,
                lo = xs[0],
                hi = xs[0];
            foreach (double x in xs)
            {
                sum += x;
                lo = Math.Min(lo, x);
                hi = Math.Max(hi, x);
            }
            g.Mean = sum / n;
            if (n == 1)
            {
                g.Range = previous != null ? Math.Abs(xs[0] - previous.Samples[0]) : Trends.Invalid;
                g.StdDev = previous != null ? g.Range / Math.Sqrt(2.0) : Trends.Invalid;
                return g;
            }
            g.Range = hi - lo;
            double ss = 0;
            foreach (double x in xs)
                ss += (x - g.Mean) * (x - g.Mean);
            g.StdDev = Math.Sqrt(ss / (n - 1));
            return g;
        }

        /// <summary><paramref name="count"/> subgroups ending at <paramref name="newestEnd"/>
        /// with the process values over them: the means of the subgroups' means, ranges and
        /// standard deviations, each replaced by the SPC tag's override when it has one (the
        /// XDOUBLEBAR / RANGE / SDEVIATION settings, SPCProcessXRSSet), and the control limits
        /// from them.</summary>
        public static Run Of(World.World w, TrendTag tag, long newestEnd, int count)
        {
            int n = Size(w, tag);
            long g = SubgroupTicks(w, tag);
            var run = new Run { Size = n };
            Subgroup? prev = n == 1 ? At(w, tag, newestEnd - count * g, null) : null;
            for (int i = count - 1; i >= 0; i--)
            {
                var sg = At(w, tag, newestEnd - i * g, prev);
                run.Groups.Add(sg);
                prev = sg;
            }
            double Mean(Func<Subgroup, double> f)
            {
                double s = 0;
                int k = 0;
                foreach (var x in run.Groups)
                    if (x != null && Trends.Validity(f(x)) == 0)
                    {
                        s += f(x);
                        k++;
                    }
                return k > 0 ? s / k : Trends.Invalid;
            }
            run.Mean = Mean(x => x.Mean);
            run.Range = Mean(x => x.Range);
            run.StdDev = Mean(x => x.StdDev);
            var set = SpcSettings.Of(w).Find(tag.Name);
            if (set != null)
            {
                if (Trends.Validity(set.Mean) == 0)
                    run.Mean = set.Mean;
                if (Trends.Validity(set.Range) == 0)
                    run.Range = set.Range;
                if (Trends.Validity(set.StdDev) == 0)
                    run.StdDev = set.StdDev;
            }
            run.Sigma = n == 1 ? run.Range / D2One : run.StdDev;
            var k = Constants[n - 1];
            if (Trends.Validity(run.Mean) == 0 && Trends.Validity(run.Range) == 0)
            {
                run.XUcl = run.Mean + k[0] * run.Range;
                run.XLcl = run.Mean - k[0] * run.Range;
            }
            if (Trends.Validity(run.Range) == 0)
            {
                run.RUcl = k[2] * run.Range;
                run.RLcl = k[1] * run.Range;
            }
            if (Trends.Validity(run.StdDev) == 0)
            {
                run.SUcl = k[4] * run.StdDev;
                run.SLcl = k[3] * run.StdDev;
            }
            return run;
        }

        /// <summary>The value of series <paramref name="code"/> (Spc.SeriesCode: 2 X, 3 XUCL,
        /// 4 XLCL, 5 XCL, 6 R, 7 RUCL, 8 RLCL, 9 RCL, 10 S, 11 SUCL, 12 SLCL, 13 SCL) for subgroup
        /// <paramref name="sg"/> of <paramref name="run"/>; null when unknown.</summary>
        public static double? Value(Run run, Subgroup? sg, int code)
        {
            double v = code switch
            {
                2 => sg?.Mean ?? Trends.Invalid,
                3 => run.XUcl,
                4 => run.XLcl,
                5 => run.Mean,
                6 => sg?.Range ?? Trends.Invalid,
                7 => run.RUcl,
                8 => run.RLcl,
                9 => run.Range,
                10 => sg?.StdDev ?? Trends.Invalid,
                11 => run.SUcl,
                12 => run.SLcl,
                13 => run.StdDev,
                _ => Trends.Invalid,
            };
            return Trends.Validity(v) == 0 ? v : (double?)null;
        }

        /// <summary>A pen's samples (<paramref name="count"/> of them, oldest first) of an SPC
        /// series: each sample a subgroup, the limits over the subgroups shown.</summary>
        public static double?[] Samples(World.World w, TrendTag tag, int code, long newestEnd, int count)
        {
            var run = Of(w, tag, EndAt(w, tag, newestEnd), Math.Max(1, count));
            var o = new double?[run.Groups.Count];
            for (int i = 0; i < o.Length; i++)
                o[i] = Value(run, run.Groups[i], code);
            return o;
        }

        /// <summary>An SPC series' value at FILETIME <paramref name="ft"/>: the subgroup complete
        /// then, the limits over the [SPC]AlarmBufferSize subgroups up to it.</summary>
        public static double? ValueAt(World.World w, TrendTag tag, int code, long ft)
        {
            var run = Of(w, tag, EndAt(w, tag, ft), AlarmBuffer(w));
            return Value(run, run.Groups.Count > 0 ? run.Groups[run.Groups.Count - 1] : null, code);
        }

        // ------------------------------------------------------------------ alarms
        /// <summary>The SPC alarm types (the Include labels XFreak = 1 ... RBelowLCL = 16384), with
        /// the [SPC] parameter of their consecutive points and its default.</summary>
        private static readonly (int Bit, string Param, int Default)[] AlarmTypes =
        {
            (1, "XFreak", 1),
            (2, "XOutsideCL", 3),
            (4, "XAboveUCL", 3),
            (8, "XBelowLCL", 3),
            (16, "XOutsideWL", 5),
            (32, "XUptrend", 5),
            (64, "XDownTrend", 5),
            (128, "XGradualUp", 8),
            (256, "XGradualDown", 8),
            (512, "XErratic", 3),
            (1024, "XStratification", 15),
            (2048, "XMixture", 8),
            (4096, "ROutsideCL", 3),
            (8192, "RAboveUCL", 3),
            (16384, "RBelowLCL", 3),
        };

        /// <summary>[SPC]AlarmBufferSize (1..200).</summary>
        public static int AlarmBuffer(World.World w) =>
            IniInt(w, "AlarmBufferSize", DefaultAlarmBuffer, 1, 200);

        private static int IniInt(World.World w, string name, int dflt, int lo, int hi)
        {
            string s = CitectText.Trim(w.Param("SPC", name) ?? "");
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? Math.Max(lo, Math.Min(hi, v))
                : dflt;
        }

        /// <summary>The alarm bits on now, over the [SPC]AlarmBufferSize subgroups up to the newest
        /// complete one: each type's [SPC] parameter of consecutive points (from the newest) all
        /// meeting its condition (doc 1217197). Sigma is a third of the distance from the centre
        /// line to the upper control limit; XFreak a point beyond 2 sigma, XOutsideWL beyond the
        /// alert limits (2 sigma), XStratification within 1 sigma, XMixture outside it; XErratic a
        /// change from the previous point greater than the distance between the control limits;
        /// trends strictly rising or falling (the conditions' details: guess).</summary>
        public static int Alarms(World.World w, TrendTag tag, long now)
        {
            var run = Of(w, tag, EndAt(w, tag, now), AlarmBuffer(w));
            if (!run.Known)
                return 0;
            var x = new List<double>();
            var r = new List<double>();
            foreach (var g in run.Groups)
            {
                x.Add(g?.Mean ?? Trends.Invalid);
                r.Add(g?.Range ?? Trends.Invalid);
            }
            double cl = run.Mean,
                sigma = Trends.Validity(run.XUcl) == 0 ? (run.XUcl - cl) / 3.0 : Trends.Invalid;
            bool Ok(double v) => Trends.Validity(v) == 0;
            bool Last(List<double> s, int k, Func<int, bool> cond)
            {
                if (k < 1 || s.Count < k)
                    return false;
                for (int i = s.Count - k; i < s.Count; i++)
                    if (!Ok(s[i]) || !cond(i))
                        return false;
                return true;
            }
            int bits = 0;
            foreach (var (bit, param, dflt) in AlarmTypes)
            {
                int k = IniInt(w, param, dflt, 1, 20);
                bool on = bit switch
                {
                    1 => Ok(sigma) && Last(x, k, i => Math.Abs(x[i] - cl) > 2 * sigma),
                    2 => Ok(run.XUcl) && Last(x, k, i => x[i] > run.XUcl || x[i] < run.XLcl),
                    4 => Ok(run.XUcl) && Last(x, k, i => x[i] > run.XUcl),
                    8 => Ok(run.XLcl) && Last(x, k, i => x[i] < run.XLcl),
                    16 => Ok(sigma) && Last(x, k, i => Math.Abs(x[i] - cl) > 2 * sigma),
                    32 => Last(x, k, i => i > 0 && Ok(x[i - 1]) && x[i] > x[i - 1]),
                    64 => Last(x, k, i => i > 0 && Ok(x[i - 1]) && x[i] < x[i - 1]),
                    128 => Last(x, k, i => x[i] > cl),
                    256 => Last(x, k, i => x[i] < cl),
                    512 => Ok(run.XUcl)
                        && Last(x, k, i =>
                            i > 0 && Ok(x[i - 1]) && Math.Abs(x[i] - x[i - 1]) > run.XUcl - run.XLcl
                        ),
                    1024 => Ok(sigma) && Last(x, k, i => Math.Abs(x[i] - cl) <= sigma),
                    2048 => Ok(sigma) && Last(x, k, i => Math.Abs(x[i] - cl) > sigma),
                    4096 => Ok(run.RUcl) && Last(r, k, i => r[i] > run.RUcl || r[i] < run.RLcl),
                    8192 => Ok(run.RUcl) && Last(r, k, i => r[i] > run.RUcl),
                    16384 => Ok(run.RLcl) && Last(r, k, i => r[i] < run.RLcl),
                    _ => false,
                };
                if (on)
                    bits |= bit;
            }
            return bits;
        }

        // ------------------------------------------------------------------ capability
        /// <summary>Cp, Cpk, skewness and kurtosis of a run (SPCClientInfo 8..11): Cp = (USL - LSL)
        /// / 6 sigma, Cpk = the nearer specification limit's distance from the mean / 3 sigma;
        /// skewness and kurtosis the third and fourth moments of every sample over the standard
        /// deviation's third and fourth powers (kurtosis 3 for a normal distribution). 0 when
        /// unknown (guess).</summary>
        public static double Capability(Run run, double lsl, double usl, int type)
        {
            if (!run.Known)
                return 0;
            if (type == 8 || type == 9)
            {
                if (!(run.Sigma > 0))
                    return 0;
                return type == 8
                    ? (usl - lsl) / (6 * run.Sigma)
                    : Math.Min(usl - run.Mean, run.Mean - lsl) / (3 * run.Sigma);
            }
            var all = new List<double>();
            foreach (var g in run.Groups)
                if (g != null)
                    all.AddRange(g.Samples);
            if (all.Count < 2)
                return 0;
            double m = 0;
            foreach (double v in all)
                m += v;
            m /= all.Count;
            double m2 = 0,
                m3 = 0,
                m4 = 0;
            foreach (double v in all)
            {
                double d = v - m;
                m2 += d * d;
                m3 += d * d * d;
                m4 += d * d * d * d;
            }
            m2 /= all.Count;
            m3 /= all.Count;
            m4 /= all.Count;
            if (!(m2 > 0))
                return 0;
            return type == 10 ? m3 / Math.Pow(m2, 1.5) : m4 / (m2 * m2);
        }

        /// <summary>The histogram of every sample of a run around the process mean (doc 1192537):
        /// <paramref name="bars"/> counts, the first below -3 sigma, the last above +3 sigma, the
        /// others the bars between them; null when the run has no sigma.</summary>
        public static double[]? Histogram(Run run, int bars)
        {
            if (!run.Known || !(run.Sigma > 0) || bars < 3)
                return null;
            var o = new double[bars];
            double lo = run.Mean - 3 * run.Sigma,
                width = 6 * run.Sigma / (bars - 2);
            foreach (var g in run.Groups)
            {
                if (g == null)
                    continue;
                foreach (double v in g.Samples)
                {
                    int k =
                        v < lo ? 0
                        : v > run.Mean + 3 * run.Sigma ? bars - 1
                        : 1 + Math.Min(bars - 3, (int)((v - lo) / width));
                    o[k]++;
                }
            }
            return o;
        }
    }
}
