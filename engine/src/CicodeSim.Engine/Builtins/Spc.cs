using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;
using CicodeSim.World.Display;

namespace CicodeSim.Builtins
{
    /// <summary>SPC settings per tag, changed by the SPC*Set functions.</summary>
    public sealed class SpcSettings
    {
        public sealed class Tag
        {
            public string Name = "";
            public double Lsl,
                Usl;
            public int SubgroupSize = DefaultSubgroupSize;
            public double Mean = Trends.Invalid,
                Range = Trends.Invalid,
                StdDev = Trends.Invalid;
        }

        /// <summary>The subgroup size of an SPC tag with no SUBGRPSIZE (guess: the SPC Tags
        /// form's default, verify).</summary>
        public const int DefaultSubgroupSize = 5;

        private static readonly ConditionalWeakTable<World.World, SpcSettings> All =
            new ConditionalWeakTable<World.World, SpcSettings>();
        private readonly Dictionary<string, Tag> _tags = new Dictionary<string, Tag>(StringComparer.Ordinal);

        private static readonly string[] Columns =
        {
            "NAME",
            "LSL",
            "USL",
            "SUBGRPSIZE",
            "XDOUBLEBAR",
            "RANGE",
            "SDEVIATION",
        };

        private SpcSettings(World.World w)
        {
            foreach (var (_, path) in w.Unit.Tables("spc.dbf", "compile"))
            foreach (string[] r in Dbf.CitectRows(path, Columns))
            {
                string n = CitectText.Trim(r[0]);
                string k = CitectText.Lower(n);
                if (n.Length == 0 || _tags.ContainsKey(k))
                    continue;
                double Num(string s, double d) =>
                    FloatText.TryParseNumber(CitectText.Trim(s), out double v) ? v : d;
                var t = new Tag
                {
                    Name = n,
                    Lsl = Num(r[1], 0),
                    Usl = Num(r[2], 0),
                    Mean = Num(r[4], Trends.Invalid),
                    Range = Num(r[5], Trends.Invalid),
                    StdDev = Num(r[6], Trends.Invalid),
                };
                int size = (int)Num(r[3], DefaultSubgroupSize);
                t.SubgroupSize = size >= 1 && size <= 40 ? size : DefaultSubgroupSize;
                _tags[k] = t;
            }
        }

        public static SpcSettings Of(World.World w) => All.GetValue(w, x => new SpcSettings(x));

        /// <summary>An SPC tag by name ('Cluster.Tag' allowed), null when none.</summary>
        public Tag? Find(string name)
        {
            string n = CitectText.Trim(name ?? "");
            if (_tags.TryGetValue(CitectText.Lower(n), out var t))
                return t;
            int dot = n.IndexOf('.');
            return dot > 0 && _tags.TryGetValue(CitectText.Lower(n.Substring(dot + 1)), out t) ? t : null;
        }
    }

    /// <summary>The SPC functions.</summary>
    public static class Spc
    {
        /// <summary>A pen's SPC series code (pen; maps SPCSetLimit's
        /// types to 3 XUCL, 4 XLCL, 5 XCL, 7 RUCL, 8 RLCL, 9 RCL, 11 SUCL, 12 SLCL, 13 SCL; X, R
        /// and S as 2, 6, 10: guess).</summary>
        internal static int SeriesCode(string pen)
        {
            int dot = pen.LastIndexOf('.');
            switch (dot > 0 ? CitectText.Upper(pen.Substring(dot + 1)) : "")
            {
                case "X":
                    return 2;
                case "XUCL":
                    return 3;
                case "XLCL":
                    return 4;
                case "XCL":
                    return 5;
                case "R":
                    return 6;
                case "RUCL":
                    return 7;
                case "RLCL":
                    return 8;
                case "RCL":
                    return 9;
                case "S":
                    return 10;
                case "SUCL":
                    return 11;
                case "SLCL":
                    return 12;
                case "SCL":
                    return 13;
            }
            return 0;
        }

        private static readonly int[] LimitSeries = { 0, 3, 4, 7, 8, 11, 12, 5, 9, 13 };

        [Builtin(
            "SPCSetLimit",
            Doc = "1192542",
            Effects = "sets an SPC chart's control limit on the client display (the trend at the AN): a manual limit shows as that constant",
            Errors = "274 no window, an AN outside 1..31999 or a type outside 1..9 (raised and returned); 300 no trend at the AN (returned, not raised: the runtime looks the trend up with the trap flag 0)",
            Notes = "Type 1 X-bar UCL, 2 X-bar LCL, 3 range UCL, 4 range LCL, 5 std dev UCL, 6 std dev LCL, 7 X-bar centre, 8 range centre, 9 std dev centre; Setting 0 automatic (the server's calculation, not simulated: no samples), 1 manual (Value). The first pen showing that series takes it; none: 0 and nothing changes."
        )]
        private static Value SPCSetLimit(SimHost h, CicodeTask t, ArgList a)
        {
            long an = A.Short(A.I(a.Get(0))),
                type = A.Short(A.I(a.Get(1)));
            double value = A.R(a.Get(2));
            long setting = A.Short(A.I(a.Get(3)));
            var d = DisplayBuiltins.Of(h);
            var w = d.OpenWindow(d.WindowOf(t));
            if (w == null || an < 1 || an > 31999 || type < 1 || type > 9)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            var tr = w.Page != null ? PageObjectTables.Of(d).Trend(w.Number, w.Page.Name, (int)an) : null;
            // not raised: the code is only returned
            if (tr == null)
                return W.I(TrendObjects.NotFound);
            int code = LimitSeries[type];
            foreach (var p in tr.Pens)
            {
                if (!p.Used || SeriesCode(p.Tag) != code)
                    continue;
                p.SpcManual = setting != 0;
                if (setting != 0)
                    p.SpcLimit = value;
                tr.Touch();
                break;
            }
            return W.I(0);
        }

        // ------------------------------------------------------------------ the server's settings
        /// <summary>A request to the SPC server.</summary>
        private static Value Server(
            SimHost h,
            CicodeTask t,
            string tag,
            string what,
            Func<SpcSettings.Tag, int> body,
            bool raise = true
        )
        {
            void Raise(int c)
            {
                if (raise)
                    W.Raise(t, c);
            }
            if (CitectText.Trim(tag).Length == 0)
            {
                Raise(Codes.NameNotFound);
                return W.I(Codes.NameNotFound);
            }
            if (t.Foreground)
            {
                Raise(Codes.FgBlock);
                return W.I(Codes.FgBlock);
            }
            return W.BlockOneTurn(t, what, () =>
            {
                var s = h.World != null ? SpcSettings.Of(h.World).Find(tag) : null;
                if (s == null)
                {
                    Raise(Codes.NameNotFound);
                    return W.I(Codes.NameNotFound);
                }
                int rc = body(s);
                if (rc != 0)
                    Raise(rc);
                return W.I(rc);
            });
        }

        [Builtin(
            "SPCSubgroupSizeGet",
            Doc = "1192545",
            Blocking = true,
            Errors = "289 an empty tag or no SPC tag of that name (returned, not raised); 282 in foreground code (returned, not raised: the runtime returns the request's code without, unlike the other SPC functions)",
            Notes = "Size (by reference) = the subgroup size the server uses: spc.dbf SUBGRPSIZE (5 when not set: guess), or the last SPCSubgroupSizeSet."
        )]
        private static Value SPCSubgroupSizeGet(SimHost h, CicodeTask t, ArgList a)
        {
            Value r = a.Get(1);
            return Server(
                h,
                t,
                A.S(a.Get(0)),
                "SPCSubgroupSizeGet",
                s =>
                {
                    W.Out(t, r, s.SubgroupSize);
                    return 0;
                },
                raise: false
            );
        }

        [Builtin(
            "SPCSubgroupSizeSet",
            Doc = "1192546",
            Blocking = true,
            Effects = "sets the SPC tag's subgroup size on the server",
            Errors = "289 an empty tag or no SPC tag of that name (raised and returned); 340 a size outside 1..40 (raised and returned); 282 in foreground code (raised and returned)"
        )]
        private static Value SPCSubgroupSizeSet(SimHost h, CicodeTask t, ArgList a)
        {
            long size = A.Short(A.I(a.Get(1)));
            string tag = A.S(a.Get(0));
            if (CitectText.Trim(tag).Length > 0 && (size < 1 || size > 40))
            {
                W.Raise(t, 340);
                return W.I(340);
            }
            return Server(h, t, tag, "SPCSubgroupSizeSet", s =>
            {
                s.SubgroupSize = (int)size;
                return 0;
            });
        }

        [Builtin(
            "SPCSpecLimitGet",
            Doc = "1192543",
            Blocking = true,
            Errors = "289 an empty tag or no SPC tag of that name (raised and returned); 282 in foreground code (raised and returned)",
            Notes = "LSL / USL (by reference): spc.dbf LSL / USL or the last SPCSpecLimitSet."
        )]
        private static Value SPCSpecLimitGet(SimHost h, CicodeTask t, ArgList a)
        {
            Value lr = a.Get(1),
                ur = a.Get(2);
            return Server(h, t, A.S(a.Get(0)), "SPCSpecLimitGet", s =>
            {
                W.Out(t, lr, Value.Real(s.Lsl));
                W.Out(t, ur, Value.Real(s.Usl));
                return 0;
            });
        }

        [Builtin(
            "SPCSpecLimitSet",
            Doc = "1192544",
            Blocking = true,
            Effects = "sets the SPC tag's specification limits on the server",
            Errors = "289 an empty tag or no SPC tag of that name (raised and returned); 274 USL - LSL not above 0 (raised and returned); 282 in foreground code (raised and returned)"
        )]
        private static Value SPCSpecLimitSet(SimHost h, CicodeTask t, ArgList a)
        {
            string tag = A.S(a.Get(0));
            double lsl = A.R(a.Get(1)),
                usl = A.R(a.Get(2));
            if (CitectText.Trim(tag).Length > 0 && !(usl - lsl > 0.0))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            return Server(h, t, tag, "SPCSpecLimitSet", s =>
            {
                s.Lsl = lsl;
                s.Usl = usl;
                return 0;
            });
        }

        [Builtin(
            "SPCProcessXRSGet",
            Doc = "1192540",
            Blocking = true,
            Errors = "289 an empty tag or no SPC tag of that name (raised and returned); 282 in foreground code (raised and returned)",
            Notes = "Mean / Range / StdDev (by reference): the overrides (spc.dbf XDOUBLEBAR / RANGE / SDEVIATION or SPCProcessXRSSet's); TrnGetInvalidValue() for one not set, where the server would give its own calculation (not simulated)."
        )]
        private static Value SPCProcessXRSGet(SimHost h, CicodeTask t, ArgList a)
        {
            Value xr = a.Get(1),
                rr = a.Get(2),
                sr = a.Get(3);
            return Server(h, t, A.S(a.Get(0)), "SPCProcessXRSGet", s =>
            {
                W.Out(t, xr, Value.Real(s.Mean));
                W.Out(t, rr, Value.Real(s.Range));
                W.Out(t, sr, Value.Real(s.StdDev));
                return 0;
            });
        }

        [Builtin(
            "SPCProcessXRSSet",
            Doc = "1192541",
            Blocking = true,
            Effects = "sets the SPC tag's process mean, range and standard deviation overrides on the server",
            Errors = "289 an empty tag or no SPC tag of that name (raised and returned); 274 a range or standard deviation not above 0 and not TrnGetInvalidValue() (raised and returned); 282 in foreground code (raised and returned)",
            Notes = "TrnGetInvalidValue() for a value = no override (the server calculates it)."
        )]
        private static Value SPCProcessXRSSet(SimHost h, CicodeTask t, ArgList a)
        {
            string tag = A.S(a.Get(0));
            double x = A.R(a.Get(1)),
                r = A.R(a.Get(2)),
                sd = A.R(a.Get(3));
            bool Bad(double v) => v <= 0.0 && Trends.Validity(v) != 2;
            if (CitectText.Trim(tag).Length > 0 && (Bad(r) || Bad(sd)))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            return Server(h, t, tag, "SPCProcessXRSSet", s =>
            {
                s.Mean = x;
                s.Range = r;
                s.StdDev = sd;
                return 0;
            });
        }

        // ------------------------------------------------------------------ the client's SPC data
        /// <summary>The page trend pen showing an SPC series of the tag, null when none.</summary>
        private static bool Displayed(SimHost h, string tag)
        {
            if (h.World == null)
                return false;
            var tt = TrendTags.Of(h.World);
            foreach (var tr in PageObjectTables.Of(DisplayBuiltins.Of(h)).Trends)
            foreach (var p in tr.Pens)
                if (p.Used && tt.SpcTrend(p.Tag) is TrendTag s && C.IEq(s.Name, CitectText.Trim(tag)))
                    return true;
            return false;
        }

        /// <summary>The SPC run a page trend shows of the tag: its subgroups up to the pen's end
        /// (the trend's samples, at least <paramref name="atLeast"/>), null when no page trend
        /// shows an SPC series of it.</summary>
        private static SpcSeries.Run? Shown(SimHost h, string tag, int atLeast = 1)
        {
            var w = h.World;
            if (w == null)
                return null;
            var tt = TrendTags.Of(w);
            foreach (var tr in PageObjectTables.Of(DisplayBuiltins.Of(h)).Trends)
            foreach (var p in tr.Pens)
                if (p.Used && tt.SpcTrend(p.Tag) is TrendTag s && C.IEq(s.Name, CitectText.Trim(tag)))
                    return SpcSeries.Of(
                        w,
                        s,
                        SpcSeries.EndAt(w, s, TrendData.EndOf(w, tr, p)),
                        Math.Max(Math.Max(1, tr.Samples), atLeast)
                    );
            return null;
        }

        [Builtin(
            "SPCClientInfo",
            Doc = "1192536",
            Errors = "274 a type outside 1..11 (raised, 0); 289 an empty tag or an SPC tag no page trend shows (raised, 0)",
            Notes = "From the client's cache of a displayed SPC tag: 1 the subgroup size, 3 / 4 / 5 the mean / range / std dev overrides, 6 / 7 the LSL / USL (the server's settings); from the subgroups the page trend shows (SpcSeries): 2 the subgroups with data, 8 Cp, 9 Cpk, 10 skewness, 11 kurtosis (0 when unknown)."
        )]
        private static Value SPCClientInfo(SimHost h, CicodeTask t, ArgList a)
        {
            string tag = A.S(a.Get(0));
            long type = A.Short(A.I(a.Get(1)));
            if (CitectText.Trim(tag).Length == 0)
            {
                W.Raise(t, Codes.NameNotFound);
                return W.R(0);
            }
            if (type < 1 || type > 11)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.R(0);
            }
            var s = h.World != null ? SpcSettings.Of(h.World).Find(tag) : null;
            if (s == null || !Displayed(h, tag))
            {
                W.Raise(t, Codes.NameNotFound);
                return W.R(0);
            }
            double Ov(double v) => Trends.Validity(v) == 2 ? 0.0 : v;
            switch (type)
            {
                case 1:
                    return W.R(s.SubgroupSize);
                case 3:
                    return W.R(Ov(s.Mean));
                case 4:
                    return W.R(Ov(s.Range));
                case 5:
                    return W.R(Ov(s.StdDev));
                case 6:
                    return W.R(s.Lsl);
                case 7:
                    return W.R(s.Usl);
            }
            var run = Shown(h, tag);
            if (run == null)
                return W.R(0);
            return type == 2 ? W.R(run.KnownCount) : W.R(SpcSeries.Capability(run, s.Lsl, s.Usl, (int)type));
        }

        [Builtin(
            "SPCClientTableGet",
            Doc = "",
            Errors = "274 an empty tag, a type outside 1..4, a negative offset or a count outside 1..31999 (raised, 0); 289 an SPC tag no page trend shows, no subgroup with data yet, or type 3 (raised and returned)",
            Notes = "Count values of a displayed SPC tag's subgroups into the REAL array Table starts (Include graph.ci: SPC_TBL_MEAN 1, SPC_TBL_RANGE 2, SPC_TBL_STDDEV 4), from Offset subgroups back from the newest, newest first; Mode as TrnGetTable's DisplayMode: bit 2 oldest first, bit 1 clear writes unknown values as 0 (guess). Returns 0."
        )]
        private static Value SPCClientTableGet(SimHost h, CicodeTask t, ArgList a)
        {
            string tag = A.S(a.Get(0));
            long type = A.Short(A.I(a.Get(1))),
                off = A.Short(A.I(a.Get(2))),
                count = A.Short(A.I(a.Get(3)));
            Value table = a.Get(4);
            long mode = a.Count > 5 ? A.I(a.Get(5)) : 0;
            if (tag.Length == 0 || type < 1 || type > 4 || off < 0 || count < 1 || count >= 32000)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(0);
            }
            var run = type != 3 ? Shown(h, tag, (int)(off + count)) : null;
            if (run == null || run.KnownCount == 0)
            {
                W.Raise(t, Codes.NameNotFound);
                return W.I(Codes.NameNotFound);
            }
            int code =
                type == 1 ? 2
                : type == 2 ? 6
                : 10;
            bool oldestFirst = (mode & 2) != 0,
                keep = (mode & 1) != 0;
            for (int i = 0; i < count; i++)
            {
                int g = run.Groups.Count - 1 - (int)off - i;
                double v = (g >= 0 ? SpcSeries.Value(run, run.Groups[g], code) : null) ?? Trends.Invalid;
                if (!keep && Trends.Validity(v) != 0)
                    v = 0.0;
                Trends.Put(t, table, oldestFirst ? (int)(count - 1 - i) : i, v);
            }
            return W.I(0);
        }

        [Builtin(
            "SPCGetHistogramTable",
            Doc = "1192537",
            Errors = "289 an empty tag, an SPC tag no page trend shows or no sigma yet (raised and returned); 274 a bar count outside 3..100 (raised and returned)",
            Notes = "The frequencies of every sample of the subgroups the page trend shows, around the process mean, into the REAL array: the first below -3 sigma, the last above +3 sigma, the bars between them 6 sigma / (bars - 2) wide (sigma: SpcSeries). Returns 0. The doc says 7..100 bars; the runtime checks 3..100."
        )]
        private static Value SPCGetHistogramTable(SimHost h, CicodeTask t, ArgList a)
        {
            string tag = A.S(a.Get(0));
            long bars = A.Short(A.I(a.Get(1)));
            int rc =
                tag.Length == 0 ? Codes.NameNotFound
                : bars < 3 || bars > 100 ? Codes.InvalidArg
                : 0;
            double[]? hist =
                rc == 0
                    ? (Shown(h, tag) is SpcSeries.Run run ? SpcSeries.Histogram(run, (int)bars) : null)
                    : null;
            if (rc == 0 && hist == null)
                rc = Codes.NameNotFound;
            if (rc != 0)
            {
                W.Raise(t, rc);
                return W.I(rc);
            }
            for (int k = 0; k < hist!.Length; k++)
                Trends.Put(t, a.Get(2), k, hist[k]);
            return W.I(0);
        }

        [Builtin(
            "SPCGetSubgroupTable",
            Doc = "1192538",
            Errors = "289 an empty tag, an SPC tag no page trend shows or a subgroup without data (raised and returned); 274 a negative subgroup (raised and returned)",
            Notes = "Subgroup iSubgroup back from the newest the page trend shows (0 the newest): its samples, then its mean, range and standard deviation, into the REAL array. Returns 0."
        )]
        private static Value SPCGetSubgroupTable(SimHost h, CicodeTask t, ArgList a)
        {
            string tag = A.S(a.Get(0));
            long sub = A.Short(A.I(a.Get(1)));
            int rc =
                tag.Length == 0 ? Codes.NameNotFound
                : sub < 0 ? Codes.InvalidArg
                : 0;
            SpcSeries.Subgroup? g = null;
            if (rc == 0 && Shown(h, tag, (int)sub + 1) is SpcSeries.Run run)
                g = run.Groups[run.Groups.Count - 1 - (int)sub];
            if (rc == 0 && g == null)
                rc = Codes.NameNotFound;
            if (rc != 0)
            {
                W.Raise(t, rc);
                return W.I(rc);
            }
            var table = a.Get(2);
            int k = 0;
            foreach (double v in g!.Samples)
                Trends.Put(t, table, k++, v);
            Trends.Put(t, table, k++, g.Mean);
            Trends.Put(t, table, k++, g.Range);
            Trends.Put(t, table, k, g.StdDev);
            return W.I(0);
        }

        [Builtin(
            "SPCAlarms",
            Doc = "1192535",
            Notes = "ON (1) when one of the AlarmType bits (the Include labels XFreak = 1 ... RBelowLCL = 16384) is on for the SPC tag: SpcSeries.Alarms, over the [SPC]AlarmBufferSize subgroups up to the newest complete one. An unknown tag: 0."
        )]
        private static Value SPCAlarms(SimHost h, CicodeTask t, ArgList a)
        {
            var w = h.World;
            var tt = w != null ? TrendTags.Of(w) : null;
            var spc = tt?.Find(A.S(a.Get(0)));
            if (w == null || spc == null || !spc.Spc)
                return W.I(0);
            int bits = SpcSeries.Alarms(w, spc, (long)w.Clock.NowFiletime());
            return W.I((bits & A.I(a.Get(1))) != 0 ? 1 : 0);
        }
    }
}
