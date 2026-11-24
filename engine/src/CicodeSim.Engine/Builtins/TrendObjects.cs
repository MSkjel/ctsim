using System;
using System.Collections.Generic;
using System.Globalization;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;
using CicodeSim.World.Display;

namespace CicodeSim.Builtins
{
    /// <summary>Trend objects on a page, and the trend client's time and sample arithmetic.</summary>
    public static class TrendData
    {
        public const long Tick = 10_000_000;

        /// <summary>a period below 5000 ticks (0.5 ms) is 'not set'.</summary>
        public const long MinPeriod = 5000;

        /// <summary>The sample period in ticks: the one set, else the smallest SAMPLEPER of the
        /// trend's pens (an SPC series pen's: its subgroup's time; with a period under 0.5 ms),
        /// else 1 s (no pen: guess).</summary>
        public static long PeriodTicks(World.World w, PageTrend t)
        {
            if (t.PeriodTicks >= MinPeriod)
                return t.PeriodTicks;
            long best = 0;
            var tt = TrendTags.Of(w);
            foreach (var p in t.Pens)
            {
                if (!p.Used)
                    continue;
                var r = tt.Find(p.Tag);
                var spc = r == null ? tt.SpcTrend(p.Tag) : null;
                long per =
                    spc != null
                        ? SpcSeries.SubgroupTicks(w, spc)
                        : (long)Math.Round((r?.SamplePeriod ?? 1.0) * Tick);
                if (per >= MinPeriod && (best == 0 || per < best))
                    best = per;
            }
            return best > 0 ? best : Tick;
        }

        /// <summary>The span in ticks: the one TrnSetSpan set, else samples x period.</summary>
        public static long SpanTicks(World.World w, PageTrend t) =>
            t.SpanTicks > 0 ? t.SpanTicks : (long)Math.Max(1, t.Samples) * PeriodTicks(w, t);

        /// <summary>The FILETIME of a pen's newest sample: in historical mode the end the pen was
        /// set to, else now on the period's grid (the trend server samples on multiples of the
        /// period: guess).</summary>
        public static long EndOf(World.World w, PageTrend t, TrendPen p)
        {
            if (t.Historical && p.End > 0)
                return p.End;
            long now = (long)w.Clock.NowFiletime();
            long per = PeriodTicks(w, t);
            return per > 0 ? now / per * per : now;
        }

        /// <summary>The FILETIME of sample <paramref name="index"/> back from the newest (end - period x index).</summary>
        public static long SampleTime(World.World w, PageTrend t, TrendPen p, int index) =>
            EndOf(w, t, p) - PeriodTicks(w, t) * index;

        /// <summary>The value a pen held at FILETIME <paramref name="ft"/>, null when it is not
        /// known (no history then, bad quality, an expression trend no one sampled). An SPC
        /// series pen: a manual control limit is a constant line, else the series
        /// (SpcSeries).</summary>
        public static double? ValueAt(World.World w, TrendPen p, long ft)
        {
            if (!p.Used)
                return null;
            var tt = TrendTags.Of(w);
            if (tt.Find(p.Tag) == null && tt.SpcTrend(p.Tag) is TrendTag spc)
                return p.SpcManual ? p.SpcLimit : SpcSeries.ValueAt(w, spc, Spc.SeriesCode(p.Tag), ft);
            return TrendValueAt(w, p.Tag, ft);
        }

        /// <summary>The value trend <paramref name="trend"/> held at FILETIME
        /// <paramref name="ft"/>, null when it is not known.</summary>
        public static double? TrendValueAt(World.World w, string trend, long ft)
        {
            var tt = TrendTags.Of(w);
            double t = w.Clock.TOfFiletime((ulong)ft);
            // simulator only (ctsim/options seedTrendHistory): the time before the simulation
            // started (and before the first known value) holds the first known value, so a
            // trend shows its whole span at once; the runtime shows the trend archive there
            bool seed = tt.SeedHistory;
            if (t < 0)
            {
                if (!seed)
                    return null;
                t = 0;
            }
            string? tag = tt.PenTag(trend);
            double v;
            ulong q;
            if (tag != null)
            {
                var hist = TagHistory.Attach(w);
                if (!hist.TryValueAt(tag, t, out v, out q) && !(seed && hist.TryEarliest(tag, out v, out q)))
                    return null;
            }
            else
            {
                var r = tt.Find(trend);
                if (r == null)
                    return null;
                if (
                    !tt.TrySampleAt(r.Name, t, out v, out q)
                    && !(seed && tt.TryFirstSample(r.Name, out v, out q))
                )
                    return null;
            }
            if ((q & 0xC0) == 0 || double.IsNaN(v))
                return null;
            return v;
        }

        /// <summary>The pen's samples oldest first (Samples of them, the newest at the end), null
        /// where unknown.</summary>
        public static double?[] Samples(World.World w, PageTrend t, TrendPen p)
        {
            int n = Math.Max(1, t.Samples);
            var o = new double?[n];
            if (!p.Used)
                return o;
            long end = EndOf(w, t, p),
                per = PeriodTicks(w, t);
            var tt = TrendTags.Of(w);
            if (!p.SpcManual && tt.Find(p.Tag) == null && tt.SpcTrend(p.Tag) is TrendTag spc)
            {
                // an SPC series: each sample a subgroup, the limits over the subgroups shown
                var s = SpcSeries.Samples(w, spc, Spc.SeriesCode(p.Tag), end, n);
                Array.Copy(s, 0, o, n - s.Length, s.Length);
                return o;
            }
            for (int i = 0; i < n; i++)
                o[n - 1 - i] = ValueAt(w, p, end - per * i);
            return o;
        }

        /// <summary>The Cicode time (Unix seconds) of a FILETIME, truncated.</summary>
        public static long Unix(long ft) =>
            (long)Math.Floor((ft - SimClock.FiletimeUnixOffset) / (double)Tick);

        /// <summary>The milliseconds of a FILETIME.</summary>
        public static long Millis(long ft)
        {
            long r = (ft - SimClock.FiletimeUnixOffset) % Tick;
            if (r < 0)
                r += Tick;
            return r / 10_000;
        }

        /// <summary>A pen's default scale: trend.dbf ENG_ZERO / ENG_FULL, else its expression
        /// tag's range, else 0 .. 100.</summary>
        public static (float lo, float hi) DefaultScale(World.World w, string trend)
        {
            var tt = TrendTags.Of(w);
            var r = tt.Find(trend);
            double? lo = r?.EngZero,
                hi = r?.EngFull;
            if (lo == null || hi == null)
            {
                string? tag = tt.PenTag(trend);
                var d = tag != null ? w.Tags.Find(tag) : null;
                var (ez, ef) = d != null ? d.EngRange() : (0.0, 100.0);
                lo ??= ez;
                hi ??= ef;
            }
            return ((float)lo.Value, (float)hi.Value);
        }

        /// <summary>The format the compiler gives a trend without a FORMAT (width 6, 2 decimals).</summary>
        public const int CompiledWidth = 6,
            CompiledDecimals = 2;

        /// <summary>A trend's format width and decimals.</summary>
        public static (int width, int decimals) Format(World.World w, string trend)
        {
            var tt = TrendTags.Of(w);
            var r = tt.Find(trend);
            string f = r?.Format ?? "";
            if (f.Length == 0)
            {
                string? tag = tt.PenTag(trend);
                f = tag != null ? w.Tags.Find(tag)?.Format ?? "" : "";
                if (f.Length == 0 && r != null)
                    return (CompiledWidth, CompiledDecimals);
            }
            int width = 0,
                dec = 0;
            bool point = false,
                any = false;
            foreach (char c in f)
            {
                if (c == '#' || c == '0')
                {
                    width++;
                    any = true;
                    if (point)
                        dec++;
                }
                else if (c == '.')
                {
                    width++;
                    point = true;
                }
                else if (any)
                    break;
            }
            return any ? (width, dec) : (5, 1);
        }

        /// <summary>Set a pen of a trend: '!' clears it; a trend tag takes its
        /// scale, units, format and comment and its tag is logged from now on.</summary>
        public static void SetPen(World.World w, PageTrend t, int k, string tag)
        {
            var p = t.Pens[k];
            string n = CitectText.Trim(tag);
            if (n.StartsWith("!", StringComparison.Ordinal) || n.Length == 0)
            {
                t.Pens[k] = new TrendPen();
                t.Touch();
                return;
            }
            if (p.Used && string.Equals(p.Tag, n, StringComparison.OrdinalIgnoreCase))
                return;
            var np = new TrendPen
            {
                Tag = n,
                End = p.End,
                DisplayMode = p.DisplayMode,
            };
            var (lo, hi) = DefaultScale(w, n);
            np.Lo = np.DefLo = lo;
            np.Hi = np.DefHi = hi;
            (np.Width, np.Decimals) = Format(w, n);
            var tt = TrendTags.Of(w);
            var r = tt.Find(n);
            string units = r != null && r.Units.Length > 0 ? r.Units : "";
            if (units.Length == 0)
            {
                string? et = tt.PenTag(n);
                units = et != null ? w.Tags.Find(et)?.EngUnits ?? "" : "";
            }
            np.Units = units.Length > 7 ? units.Substring(0, 7) : units;
            np.Comment = r?.Comment ?? "";
            t.Pens[k] = np;
            string? tg = tt.PenTag(n);
            if (tg != null)
                TagHistory.Attach(w).Watch(tg);
            t.Touch();
        }

        /// <summary>sprintf("%W.Df") of a value; '&lt;na&gt;' for the invalid marker, '&lt;gated&gt;'
        /// for the gated one; the units appended when <paramref name="units"/>.</summary>
        public static string Text(double v, TrendPen p, bool units)
        {
            long v2 = Trends.Validity(v);
            if (v2 == 1)
                return "<gated>";
            if (v2 == 2)
                return "<na>";
            string s = Printf.Format(
                "%" + C.Inv(p.Width) + "." + C.Inv(p.Decimals) + "f",
                PrintfArg.Float(v)
            );
            if (s.Length > 0x4f)
                s = s.Substring(0, 0x4f);
            return units ? s + p.Units : s;
        }
    }

    /// <summary>The Trn* functions of a trend object on a page.</summary>
    public static class TrendObjects
    {
        public const int NotFound = 300,
            PenNotFound = 301,
            NoCursor = 0x155;

        private static DisplayState D(SimHost h) => DisplayBuiltins.Of(h);

        private static PageObjectTables Tables(SimHost h) => PageObjectTables.Of(D(h));

        /// <summary>The task's window with a page shown, null when none (no page: the trend
        /// lookups fail).</summary>
        private static Window? Win(SimHost h, CicodeTask t) => D(h).OpenWindow(D(h).WindowOf(t));

        private static bool HasWindow(SimHost h, CicodeTask t) => Win(h, t) != null;

        /// <summary>The trend an AN names in the task's window (-2 = TrnSelect's), null with
        /// <paramref name="err"/> 300.</summary>
        internal static PageTrend? Find(SimHost h, CicodeTask t, long an, out int err)
        {
            err = 0;
            var tb = Tables(h);
            if (an == -2)
            {
                if (tb.Selected != null && tb.Trends.Contains(tb.Selected))
                    return tb.Selected;
                err = NotFound;
                return null;
            }
            var w = Win(h, t);
            var tr = w?.Page != null ? tb.Trend(w.Number, w.Page.Name, (int)an) : null;
            if (tr == null)
                err = NotFound;
            return tr;
        }

        /// <summary>The trends a setter's AN names: -1 every trend of the page, else Find's.</summary>
        private static List<PageTrend> Targets(SimHost h, CicodeTask t, long an, out int err)
        {
            err = 0;
            if (an == -1)
            {
                var w = Win(h, t);
                return w?.Page != null ? Tables(h).TrendsOf(w.Number, w.Page.Name) : new List<PageTrend>();
            }
            var tr = Find(h, t, an, out err);
            return tr != null ? new List<PageTrend> { tr } : new List<PageTrend>();
        }

        private static bool GetterAn(long an) => an >= 1 && an <= 31999;

        private static bool SetterAn(long an) => an >= -2 && an <= 31999;

        private static World.World Wd(SimHost h) => h.World!;

        /// <summary>Run a getter under ErrSet(1), as the runtime does.</summary>
        private static Value Getter(CicodeTask t, Func<Value> body)
        {
            t.ErrSetLevel++;
            try
            {
                return body();
            }
            finally
            {
                if (t.ErrSetLevel > 0)
                    t.ErrSetLevel--;
            }
        }

        /// <summary>The trend and pen of a getter (its checks done), null with the error raised.</summary>
        private static (PageTrend? tr, TrendPen? pen) GetPen(
            SimHost h,
            CicodeTask t,
            long an,
            long pen,
            bool penRequired = true
        )
        {
            var tr = Find(h, t, an, out int err);
            if (tr == null)
            {
                W.Raise(t, err);
                return (null, null);
            }
            var p = tr.Pen(pen);
            if (penRequired && (p == null || !p.Used))
            {
                W.Raise(t, PenNotFound);
                return (tr, null);
            }
            return (tr, p);
        }

        private static long Pen(ArgList a, int k) => a.Count > k ? A.Short(A.I(a.Get(k))) : 0;

        private static long An(ArgList a) => A.Short(A.I(a.Get(0)));

        // ================================================================== pens
        [Builtin(
            "TrnGetPen",
            Doc = "1193847",
            Errors = "274 no window, an AN outside 1..31999 or a pen outside 0..8 (raised, ''); 300 no trend at the AN; 301 an unused pen (raised, '')",
            Notes = "The pen's trend tag; Mode 1 prefixes the trend's cluster ('Cluster.Tag') when it has one. Runs under ErrSet(1) (never halts)."
        )]
        private static Value TrnGetPen(SimHost h, CicodeTask t, ArgList a) => Getter(t, () =>
            {
                long an = An(a),
                    pen = Pen(a, 1);
                bool cl = a.Count > 2 && A.I(a.Get(2)) != 0;
                if (!HasWindow(h, t) || !GetterAn(an) || pen < 0 || pen > 8)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.Empty;
                }
                var (tr, p) = GetPen(h, t, an, pen);
                if (p == null)
                    return W.Empty;
                string s = cl && tr!.Cluster.Length > 0 ? tr.Cluster + "." + p.Tag : p.Tag;
                return W.S(C.Cut(s));
            });

        [Builtin(
            "TrnSetPen",
            Doc = "1193870",
            Blocking = true,
            Effects = "sets the pen's trend tag (its scale, units and format from trend.dbf); the tag's changes are logged from now on",
            Errors = "274 no window, an AN outside -2..31999 or a pen outside -2..8 (raised and returned); 300 no trend at the AN (raised and returned); 282 in foreground code (raised and returned: the request blocks, CodeBlock -1)",
            Notes = "AN -1 = every trend of the page, -2 = TrnSelect's trend; Pen -1 = every pen, -2 = the first free pen, 0 = the focus pen; a tag starting with '!' clears the pen. A TrnSelect trend for a page not shown yet stores the pen at once; else the request to the trend server waits one turn."
        )]
        private static Value TrnSetPen(SimHost h, CicodeTask t, ArgList a)
        {
            long an = An(a),
                pen = Pen(a, 1);
            string tag = A.S(a.Get(2));
            if (!HasWindow(h, t) || !SetterAn(an) || pen < -2 || pen > 8)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            var ts = Targets(h, t, an, out int err);
            if (err != 0)
            {
                W.Raise(t, err);
                return W.I(err);
            }
            void Apply()
            {
                var w = Wd(h);
                foreach (var tr in ts)
                {
                    if (pen == -1)
                    {
                        for (int k = 0; k < tr.Pens.Length; k++)
                            TrendData.SetPen(w, tr, k, tag);
                        continue;
                    }
                    int idx;
                    if (pen == -2)
                    {
                        idx = Array.FindIndex(tr.Pens, x => !x.Used);
                        if (idx < 0)
                            continue;
                    }
                    else
                        idx = pen == 0 ? tr.Focus : (int)pen - 1;
                    TrendData.SetPen(w, tr, idx, tag);
                }
            }
            bool pending = ts.Count > 0 && ts.TrueForAll(x => x.Pending);
            if (pending || ts.Count == 0)
            {
                Apply();
                return W.I(0);
            }
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.I(Codes.FgBlock);
            }
            return W.BlockOneTurn(t, "TrnSetPen", () =>
            {
                Apply();
                return W.I(0);
            });
        }

        [Builtin(
            "TrnGetPenNo",
            Doc = "1193850",
            Errors = "274 no window or an AN outside 1..31999 (raised, 0); 300 no trend at the AN (raised, 0)",
            Notes = "The number (1..8) of the pen showing the trend tag (compared without case), 0 when none."
        )]
        private static Value TrnGetPenNo(SimHost h, CicodeTask t, ArgList a) => Getter(t, () =>
            {
                long an = An(a);
                string tag = CitectText.Trim(A.S(a.Get(1)));
                if (!HasWindow(h, t) || !GetterAn(an))
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.I(0);
                }
                var tr = Find(h, t, an, out int err);
                if (tr == null)
                {
                    W.Raise(t, err);
                    return W.I(0);
                }
                for (int k = 0; k < tr.Pens.Length; k++)
                    if (tr.Pens[k].Used && C.IEq(tr.Pens[k].Tag, tag))
                        return W.I(k + 1);
                return W.I(0);
            });

        [Builtin(
            "TrnGetPenFocus",
            Doc = "1193849",
            Errors = "274 no window or an AN outside 1..31999 (raised, -1); 300 no trend at the AN (raised, -1)",
            Notes = "The focus pen 1..8."
        )]
        private static Value TrnGetPenFocus(SimHost h, CicodeTask t, ArgList a)
        {
            long an = An(a);
            if (!HasWindow(h, t) || !GetterAn(an))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            var tr = Find(h, t, an, out int err);
            if (tr == null)
            {
                W.Raise(t, err);
                return W.I(-1);
            }
            return W.I(tr.Focus + 1);
        }

        [Builtin(
            "TrnSetPenFocus",
            Doc = "1193871",
            Effects = "moves the trend's focus pen",
            Errors = "274 no window, an AN outside -2..31999 or a pen outside -4..8 (raised, -1); 300 no trend at the AN (raised, -1)",
            Notes = "Returns the old focus pen 1..8. Pen 1..8 that pen (used or not), 0 no change, -4 the next pen, -3 the previous pen (wrapping), -2 the next pen with a tag, -1 the previous one with a tag."
        )]
        private static Value TrnSetPenFocus(SimHost h, CicodeTask t, ArgList a)
        {
            long an = An(a),
                pen = Pen(a, 1);
            if (!HasWindow(h, t) || !SetterAn(an) || pen < -4 || pen > 8)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            var tr = Find(h, t, an, out int err);
            if (tr == null)
            {
                W.Raise(t, err);
                return W.I(-1);
            }
            int old = tr.Focus,
                n = tr.Pens.Length;
            switch (pen)
            {
                case 0:
                    break;
                case -4:
                    tr.Focus = tr.Focus == n - 1 ? 0 : tr.Focus + 1;
                    break;
                case -3:
                    tr.Focus = tr.Focus == 0 ? n - 1 : tr.Focus - 1;
                    break;
                case -2:
                    for (int k = 1, f = tr.Focus; k <= n; k++)
                    {
                        int c = (f + k) % n;
                        if (tr.Pens[c].Used)
                        {
                            tr.Focus = c;
                            break;
                        }
                    }
                    break;
                case -1:
                    for (int k = 1, f = tr.Focus; k <= n; k++)
                    {
                        int c = ((f - k) % n + n) % n;
                        if (tr.Pens[c].Used)
                        {
                            tr.Focus = c;
                            break;
                        }
                    }
                    break;
                default:
                    tr.Focus = (int)pen - 1;
                    break;
            }
            if (tr.Focus != old)
                tr.Touch();
            return W.I(old + 1);
        }

        [Builtin(
            "TrnGetPenComment",
            Doc = "1193848",
            Errors = "274 no window, an AN outside 1..31999, a pen outside 0..8, no trend at the AN or an unused pen (raised, '': the runtime raises 0x112 for both)",
            Notes = "The pen's trend.dbf COMMENT."
        )]
        private static Value TrnGetPenComment(SimHost h, CicodeTask t, ArgList a) => Getter(t, () =>
            {
                long an = An(a),
                    pen = Pen(a, 1);
                if (!HasWindow(h, t) || !GetterAn(an) || pen < 0 || pen > 8)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.Empty;
                }
                var tr = Find(h, t, an, out _);
                var p = tr?.Pen(pen);
                if (p == null || !p.Used)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.Empty;
                }
                return W.S(C.Cut(p.Comment, 0xfe));
            });

        [Builtin(
            "TrnGetUnits",
            Doc = "1193857",
            Errors = "274 no window, an AN outside 1..31999 or a pen outside 0..8 (raised, ''); 300 no trend at the AN (raised, '')",
            Notes = "The pen's engineering units (trend.dbf ENG_UNITS, else its tag's), at most 7 characters; '' for an unused pen."
        )]
        private static Value TrnGetUnits(SimHost h, CicodeTask t, ArgList a) => Getter(t, () =>
            {
                long an = An(a),
                    pen = Pen(a, 1);
                if (!HasWindow(h, t) || !GetterAn(an) || pen < 0 || pen > 8)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.Empty;
                }
                var (_, p) = GetPen(h, t, an, pen, penRequired: false);
                return W.S(p != null && p.Used ? p.Units : "");
            });

        [Builtin(
            "TrnGetFormat",
            Doc = "1193842",
            Errors = "274 no window, an AN outside 1..31999 or a pen outside 0..8 (raised and returned); 300 no trend at the AN; 301 an unused pen (returned in historical mode)",
            Notes = "Width and Decimals of the pen's format (trend.dbf FORMAT, else its tag's, else for a trend tag the compiled default 6 and 2 (_TREND.RDB 0x00200206); 5 and 1 for a pen that is no trend and has none, and on an error)."
        )]
        private static Value TrnGetFormat(SimHost h, CicodeTask t, ArgList a) => Getter(t, () =>
            {
                long an = An(a),
                    pen = Pen(a, 1);
                Value wr = a.Get(2),
                    dr = a.Get(3);
                W.Out(t, wr, 5);
                W.Out(t, dr, 1);
                if (!HasWindow(h, t) || !GetterAn(an) || pen < 0 || pen > 8)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.I(Codes.InvalidArg);
                }
                var tr = Find(h, t, an, out int err);
                if (tr == null)
                {
                    W.Raise(t, err);
                    return W.I(err);
                }
                var p = tr.Pen(pen);
                if (p == null || !p.Used)
                    return W.I(tr.Historical ? PenNotFound : 0);
                W.Out(t, wr, p.Width);
                W.Out(t, dr, p.Decimals);
                return W.I(0);
            });

        // ================================================================== period, span, time
        [Builtin(
            "TrnGetPeriod",
            Doc = "1193851",
            Errors = "274 no window or an AN outside 1..31999 (raised, 0); 300 no trend at the AN (raised, 0)",
            Notes = "The sample period in seconds (10^7): the one set, else the smallest SAMPLEPER of the pens (1 s without pens: guess)."
        )]
        private static Value TrnGetPeriod(SimHost h, CicodeTask t, ArgList a) => Getter(t, () =>
            {
                long an = An(a);
                if (!HasWindow(h, t) || !GetterAn(an))
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.R(0);
                }
                var tr = Find(h, t, an, out int err);
                if (tr == null)
                {
                    W.Raise(t, err);
                    return W.R(0);
                }
                return W.R(TrendData.PeriodTicks(Wd(h), tr) / (double)TrendData.Tick);
            });

        [Builtin(
            "TrnSetPeriod",
            Doc = "1193872",
            Effects = "sets the trend's sample period and span = samples x period",
            Errors = "274 no window, an AN outside -2..31999 or a negative period (raised and returned); 300 no trend at the AN (raised, returned); 301 no pen to take a period from (returned)",
            Notes = "AN -1 = every trend of the page. A period under 0.5 ms takes the smallest SAMPLEPER of the pens."
        )]
        private static Value TrnSetPeriod(SimHost h, CicodeTask t, ArgList a)
        {
            long an = An(a);
            double period = A.R(a.Get(1));
            if (!HasWindow(h, t) || !SetterAn(an) || !(period >= 0.0))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            var ts = Targets(h, t, an, out int err);
            if (err != 0)
            {
                W.Raise(t, err);
                return W.I(err);
            }
            int rc = 0;
            foreach (var tr in ts)
            {
                long ticks = (long)(period * TrendData.Tick);
                if (ticks < TrendData.MinPeriod)
                {
                    if (tr.PenCount == 0)
                    {
                        rc = PenNotFound;
                        continue;
                    }
                    ticks = 0;
                }
                tr.PeriodTicks = ticks;
                tr.SpanTicks = 0;
                tr.Touch();
            }
            return W.I(rc);
        }

        [Builtin(
            "TrnGetSpan",
            Doc = "1193854",
            Errors = "274 no window or an AN outside 1..31999 (raised, 0); 300 no trend at the AN (raised, 0)",
            Notes = "The span in whole seconds (10^7, integer division)."
        )]
        private static Value TrnGetSpan(SimHost h, CicodeTask t, ArgList a) => Getter(t, () =>
            {
                long an = An(a);
                if (!HasWindow(h, t) || !GetterAn(an))
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.I(0);
                }
                var tr = Find(h, t, an, out int err);
                if (tr == null)
                {
                    W.Raise(t, err);
                    return W.I(0);
                }
                return W.I(TrendData.SpanTicks(Wd(h), tr) / TrendData.Tick);
            });

        [Builtin(
            "TrnSetSpan",
            Doc = "1193874",
            Effects = "sets the trend's span and period = span / samples",
            Errors = "274 no window, an AN outside -2..31999 or a span below 1 (raised and returned); 300 no trend at the AN (raised, returned)",
            Notes = "AN -1 = every trend of the page. The span is kept as given; the period is span x 10^7 / samples ticks (64-bit integer division)."
        )]
        private static Value TrnSetSpan(SimHost h, CicodeTask t, ArgList a)
        {
            long an = An(a);
            long span = A.I(a.Get(1));
            if (!HasWindow(h, t) || !SetterAn(an) || span < 1)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            var ts = Targets(h, t, an, out int err);
            if (err != 0)
            {
                W.Raise(t, err);
                return W.I(err);
            }
            foreach (var tr in ts)
            {
                long ticks = span * TrendData.Tick;
                if (tr.SpanTicks == ticks)
                    continue;
                tr.SpanTicks = ticks;
                long per = ticks / Math.Max(1, tr.Samples);
                tr.PeriodTicks = per >= TrendData.MinPeriod ? per : 0;
                tr.Touch();
            }
            return W.I(0);
        }

        /// <summary>the time of a percent of the span back from the pen's end.</summary>
        private static long TimeAt(SimHost h, PageTrend tr, TrendPen p, long pct)
        {
            var w = Wd(h);
            long end = TrendData.EndOf(w, tr, p);
            if (end <= SimClock.FiletimeUnixOffset)
                return 0;
            return end - TrendData.SpanTicks(w, tr) * pct / 100;
        }

        private static Value TimeGetter(SimHost h, CicodeTask t, ArgList a, bool ms) => Getter(t, () =>
            {
                long an = An(a),
                    pen = Pen(a, 1),
                    pct = a.Count > 2 ? A.UShort(A.I(a.Get(2))) : 0;
                if (!HasWindow(h, t) || !GetterAn(an) || pen < 0 || pen > 8 || pct > 100)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.I(0);
                }
                var tr = Find(h, t, an, out int err);
                if (tr == null)
                {
                    W.Raise(t, err);
                    return W.I(0);
                }
                var p = tr.Pen(pen);
                if (p == null || !p.Used)
                    return W.I(0);
                long ft = TimeAt(h, tr, p, pct);
                if (ft == 0)
                    return W.I(0);
                return W.I(ms ? TrendData.Millis(ft) : TrendData.Unix(ft));
            });

        [Builtin(
            "TrnGetTime",
            Doc = "1193856",
            Errors = "274 no window, an AN outside 1..31999, a pen outside 0..8 or a percent above 100 (raised, 0); 300 no trend at the AN (raised, 0)",
            Notes = "The time (seconds since 1970) Percent of the span back from the pen's newest sample (0 % = the newest); 0 for an unused pen. In real time the newest sample is now on the period's grid (guess); in historical mode the end TrnSetTime / TrnScroll set."
        )]
        private static Value TrnGetTime(SimHost h, CicodeTask t, ArgList a) => TimeGetter(h, t, a, false);

        [Builtin(
            "TrnGetMSTime",
            Doc = "1193846",
            Errors = "274 no window, an AN outside 1..31999, a pen outside 0..8 or a percent above 100 (raised, 0); 300 no trend at the AN (raised, 0)",
            Notes = "The milliseconds (0..999) of TrnGetTime's time."
        )]
        private static Value TrnGetMSTime(SimHost h, CicodeTask t, ArgList a) => TimeGetter(h, t, a, true);

        [Builtin(
            "TrnSetTime",
            Doc = "1193876",
            Effects = "a time puts the trend in historical mode with the pen's newest sample at that time; 0 puts it back in real time",
            Errors = "274 no window, an AN outside -2..31999, a pen outside -1..8 or a time not 0 and outside 1980-01-01 .. 2038-01-01 (raised and returned); 300 no trend at the AN (returned); 301 an unused pen in historical mode (returned)",
            Notes = "Pen -1 = every pen, 0 = the focus pen."
        )]
        private static Value TrnSetTime(SimHost h, CicodeTask t, ArgList a)
        {
            long an = An(a),
                pen = Pen(a, 1),
                time = A.I(a.Get(2));
            bool okTime = time == 0 || (time > 0x12cea5ff && time < 0x7fe6c600);
            if (!okTime || !HasWindow(h, t) || !SetterAn(an) || pen < -1 || pen > 8 || time < 0)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            var tr = an == -1 ? null : Find(h, t, an, out int e2);
            var ts =
                an == -1 ? Targets(h, t, an, out _)
                : tr != null ? new List<PageTrend> { tr }
                : new List<PageTrend>();
            if (an != -1 && tr == null)
            {
                W.Raise(t, NotFound);
                return W.I(NotFound);
            }
            int rc = 0;
            foreach (var x in ts)
            {
                if (time == 0)
                {
                    if (x.Historical)
                    {
                        x.Historical = false;
                        x.Touch();
                    }
                    continue;
                }
                long ft = time * TrendData.Tick + SimClock.FiletimeUnixOffset;
                if (pen == -1)
                {
                    foreach (var p in x.Pens)
                        if (p.Used)
                            p.End = ft;
                }
                else
                {
                    var p = x.Pen(pen);
                    if (p == null || !p.Used)
                    {
                        rc = PenNotFound;
                        continue;
                    }
                    p.End = ft;
                }
                x.Historical = true;
                x.Touch();
            }
            return W.I(rc);
        }

        [Builtin(
            "TrnScroll",
            Doc = "1193864",
            Effects = "moves every pen's end time and puts the trend in historical mode",
            Errors = "274 no window, an AN outside -2..31999, a pen outside -1..8 or a mode without bit 1 or 2 (raised and returned); 300 no trend at the AN (returned)",
            Notes = "Mode bit 1: nScroll is a percent of the span; bit 2: a number of samples (period x nScroll). A positive scroll moves later. The pen argument is not used (the runtime moves every pen)."
        )]
        private static Value TrnScroll(SimHost h, CicodeTask t, ArgList a)
        {
            long an = An(a),
                pen = Pen(a, 1),
                n = A.Short(A.I(a.Get(2))),
                mode = a.Count > 3 ? A.I(a.Get(3)) : 0;
            if (!HasWindow(h, t) || !SetterAn(an) || pen < -1 || pen > 8 || (mode & 3) == 0)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            var ts = Targets(h, t, an, out int err);
            if (err != 0)
            {
                W.Raise(t, err);
                return W.I(err);
            }
            var w = Wd(h);
            foreach (var tr in ts)
                Scroll(w, tr, n, mode);
            return W.I(0);
        }

        internal static void Scroll(World.World w, PageTrend tr, long n, long mode)
        {
            long delta =
                (mode & 1) != 0 ? TrendData.SpanTicks(w, tr) * n / 100
                : (mode & 2) != 0 ? TrendData.PeriodTicks(w, tr) * n
                : 0;
            foreach (var p in tr.Pens)
                if (p.Used)
                    p.End = TrendData.EndOf(w, tr, p) + delta;
            tr.Historical = true;
            tr.Touch();
        }

        [Builtin(
            "TrnGetMode",
            Doc = "1193845",
            Errors = "274 no window, an AN outside 1..31999 or a pen outside 0..8 (raised, 0); 300 no trend at the AN (raised, 0)",
            Notes = "1 in historical mode (4), 0 in real time."
        )]
        private static Value TrnGetMode(SimHost h, CicodeTask t, ArgList a) => Getter(t, () =>
            {
                long an = An(a),
                    pen = Pen(a, 1);
                if (!HasWindow(h, t) || !GetterAn(an) || pen < 0 || pen > 8)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.I(0);
                }
                var tr = Find(h, t, an, out int err);
                if (tr == null)
                {
                    W.Raise(t, err);
                    return W.I(0);
                }
                return W.I(tr.Historical ? 1 : 0);
            });

        // ================================================================== scale
        [Builtin(
            "TrnGetScale",
            Doc = "1193852",
            Errors = "274 no window, an AN outside 1..31999, a pen outside 0..8 or a percent above 100 (raised, 0); 300 no trend at the AN (raised, 0)",
            Notes = "lo + (hi - lo) x Percent / 100 of the pen's scale (floats); 0 for an unused pen."
        )]
        private static Value TrnGetScale(SimHost h, CicodeTask t, ArgList a) => Getter(t, () =>
            {
                long an = An(a),
                    pen = Pen(a, 1),
                    pct = a.Count > 2 ? A.UShort(A.I(a.Get(2))) : 0;
                if (!HasWindow(h, t) || !GetterAn(an) || pen < 0 || pen > 8 || pct > 100)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.R(0);
                }
                var tr = Find(h, t, an, out int err);
                if (tr == null)
                {
                    W.Raise(t, err);
                    return W.R(0);
                }
                var p = tr.Pen(pen);
                if (p == null || !p.Used)
                    return W.R(0);
                return W.R(ScaleAt(p, pct));
            });

        internal static double ScaleAt(TrendPen p, long pct) =>
            ((double)p.Hi - p.Lo) * (int)(short)pct / 100.0 + p.Lo;

        [Builtin(
            "TrnGetScaleStr",
            Doc = "1193853",
            Errors = "274 no window, an AN outside 1..31999, a pen outside 0..8 or a percent above 100 (raised, '<na>'); 300 no trend at the AN (raised, '<na>')",
            Notes = "TrnGetScale's value as sprintf('%W.Df') with the pen's width and decimals; Mode not 0 appends the pen's units (no blank between: the runtime copies them after the number). An unused pen: '<na>'. The task's error is cleared first (IsError is 0 after a call that succeeds; 0), and an error of the call gives '<na>'."
        )]
        private static Value TrnGetScaleStr(SimHost h, CicodeTask t, ArgList a) => Getter(t, () =>
            {
                long an = An(a),
                    pen = Pen(a, 1),
                    pct = a.Count > 2 ? A.UShort(A.I(a.Get(2))) : 0;
                bool units = a.Count > 3 && A.I(a.Get(3)) != 0;
                // the runtime clears the task's error before its checks (outside the error event)
                // and answers '<na>' for any error the call raised
                t.Error = 0;
                if (!HasWindow(h, t) || !GetterAn(an) || pen < 0 || pen > 8 || pct > 100)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.S("<na>");
                }
                var tr = Find(h, t, an, out int err);
                if (tr == null)
                {
                    W.Raise(t, err);
                    return W.S("<na>");
                }
                var p = tr.Pen(pen);
                if (p == null || !p.Used)
                    return W.S("<na>");
                return W.S(TrendData.Text(ScaleAt(p, pct), p, units));
            });

        [Builtin(
            "TrnSetScale",
            Doc = "1193873",
            Effects = "changes the pen's scale",
            Errors = "274 no window, an AN outside -2..31999, a pen outside -1..8 or a percent not 0, 100, -1 or -2 (raised and returned); 300 no trend at the AN (raised, returned); 301 an unused pen in historical mode (returned)",
            Notes = "Percent 0 sets the bottom (when below the top), 100 the top (when above the bottom), -2 the default scale (trend.dbf ENG_ZERO / ENG_FULL, else the tag's range), -1 the samples' minimum and maximum (autoscale). Pen -1 = every pen; AN -1 = every trend of the page."
        )]
        private static Value TrnSetScale(SimHost h, CicodeTask t, ArgList a)
        {
            long an = An(a),
                pen = Pen(a, 1),
                pct = A.Short(A.I(a.Get(2)));
            double scale = A.R(a.Get(3));
            if (
                !HasWindow(h, t)
                || !SetterAn(an)
                || pen < -1
                || pen > 8
                || !(pct == 0 || pct == 100 || pct == -1 || pct == -2)
            )
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            var ts = Targets(h, t, an, out int err);
            if (err != 0)
            {
                W.Raise(t, err);
                return W.I(err);
            }
            int rc = 0;
            var w = Wd(h);
            foreach (var tr in ts)
            {
                var pens = new List<TrendPen>();
                if (pen == -1)
                {
                    foreach (var p in tr.Pens)
                        if (p.Used)
                            pens.Add(p);
                }
                else
                {
                    var p = tr.Pen(pen);
                    if (p == null || !p.Used)
                    {
                        if (tr.Historical)
                            rc = PenNotFound;
                        continue;
                    }
                    pens.Add(p);
                }
                foreach (var p in pens)
                {
                    float f = (float)scale;
                    switch (pct)
                    {
                        case 0:
                            if (scale < p.Hi)
                                p.Lo = f;
                            break;
                        case 100:
                            if (scale > p.Lo)
                                p.Hi = f;
                            break;
                        case -2:
                            p.Lo = p.DefLo;
                            p.Hi = p.DefHi;
                            break;
                        case -1:
                        {
                            double lo = double.MaxValue,
                                hi = double.MinValue;
                            foreach (var v in TrendData.Samples(w, tr, p))
                                if (v != null)
                                {
                                    lo = Math.Min(lo, v.Value);
                                    hi = Math.Max(hi, v.Value);
                                }
                            if (lo <= hi)
                            {
                                p.Lo = (float)lo;
                                p.Hi = (float)hi;
                            }
                            break;
                        }
                    }
                }
                tr.Touch();
            }
            return W.I(rc);
        }

        // ================================================================== display mode
        [Builtin(
            "TrnGetDisplayMode",
            Doc = "1193840",
            Errors = "274 no window, an AN outside -2..31999 or a pen outside 0..8 (raised, 0); 300 no trend at the AN (raised, 0); 301 an unused pen in historical mode (raised, 0)",
            Notes = "The pen's display mode (pen; 0 until TrnSetDisplayMode: guess for the default)."
        )]
        private static Value TrnGetDisplayMode(SimHost h, CicodeTask t, ArgList a) => Getter(t, () =>
            {
                long an = An(a),
                    pen = Pen(a, 1);
                if (!HasWindow(h, t) || !SetterAn(an) || pen < 0 || pen > 8)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.I(0);
                }
                var tr = Find(h, t, an, out int err);
                if (tr == null)
                {
                    W.Raise(t, err);
                    return W.I(0);
                }
                var p = tr.Pen(pen);
                if (p == null || !p.Used)
                {
                    if (tr.Historical)
                        W.Raise(t, PenNotFound);
                    return W.I(0);
                }
                return W.I(p.DisplayMode);
            });

        [Builtin(
            "TrnSetDisplayMode",
            Doc = "1193868",
            Effects = "sets the pen's display mode",
            Errors = "274 no window, an AN outside -2..31999, a pen outside -1..8 or a mode with (mode & 0x7c) >= 0x10 or (mode & 0xf80) >= 0x180 (raised and returned); 300 no trend at the AN (raised, returned); 301 an unused pen in historical mode (raised, returned)",
            Notes = "Bit 0x2000000 is dropped and bit 0x100000 keeps the pen's own. Pen -1 = every pen."
        )]
        private static Value TrnSetDisplayMode(SimHost h, CicodeTask t, ArgList a)
        {
            long an = An(a),
                pen = Pen(a, 1),
                mode = A.I(a.Get(2));
            if (
                !HasWindow(h, t)
                || !SetterAn(an)
                || pen < -1
                || pen > 8
                || (mode & 0x7c) >= 0x10
                || (mode & 0xf80) >= 0x180
            )
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            var tr = Find(h, t, an, out int err);
            if (tr == null)
            {
                W.Raise(t, err);
                return W.I(err);
            }
            mode &= 0xfdffffff;
            void Set(TrendPen p)
            {
                long m = mode ^ ((p.DisplayMode ^ mode) & 0x100000);
                if (p.DisplayMode != m)
                {
                    p.DisplayMode = m;
                    tr.Touch();
                }
            }
            if (pen == -1)
            {
                foreach (var p in tr.Pens)
                    if (p.Used)
                        Set(p);
                return W.I(0);
            }
            var pp = tr.Pen(pen);
            if (pp == null || !pp.Used)
            {
                if (!tr.Historical)
                    return W.I(0);
                W.Raise(t, PenNotFound);
                return W.I(PenNotFound);
            }
            Set(pp);
            return W.I(0);
        }

        // ================================================================== cursor and samples
        [Builtin(
            "TrnGetCursorPos",
            Doc = "1193835",
            Errors = "274 no window or an AN outside 1..31999 (raised, -1); 300 no trend at the AN (raised, -1)",
            Notes = "The cursor in samples back from the newest one, -1 when it is off."
        )]
        private static Value TrnGetCursorPos(SimHost h, CicodeTask t, ArgList a) => Getter(t, () =>
            {
                long an = An(a);
                if (!HasWindow(h, t) || !GetterAn(an))
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.I(-1);
                }
                var tr = Find(h, t, an, out int err);
                if (tr == null)
                {
                    W.Raise(t, err);
                    return W.I(-1);
                }
                return W.I(tr.Cursor);
            });

        [Builtin(
            "TrnSetCursorPos",
            Doc = "1193867",
            Effects = "moves the trend's cursor",
            Errors = "274 no window or an AN outside -2..31999 (raised and returned); 300 no trend at the AN (raised, returned)",
            Notes = "Position = samples back from the newest one; a negative position or one past the samples puts it in the middle (samples / 2). AN -1 = every trend of the page."
        )]
        private static Value TrnSetCursorPos(SimHost h, CicodeTask t, ArgList a)
        {
            long an = An(a),
                pos = A.UShort(A.I(a.Get(1)));
            if (!HasWindow(h, t) || !SetterAn(an))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            var ts = Targets(h, t, an, out int err);
            if (err != 0)
            {
                W.Raise(t, err);
                return W.I(err);
            }
            foreach (var tr in ts)
            {
                int n = Math.Max(1, tr.Samples);
                int c = pos > 0x7fff || pos >= n ? (short)(n >> 1) : (int)pos;
                if (tr.Cursor != c)
                {
                    tr.Cursor = c;
                    tr.Touch();
                }
            }
            return W.I(0);
        }

        [Builtin(
            "TrnSetCursor",
            Doc = "1193866",
            Effects = "moves or removes the trend's cursor; past an end the trend scrolls",
            Errors = "274 no window or an AN outside -2..31999 (raised and returned); 300 no trend at the AN (raised, returned)",
            Notes = "0 removes the cursor; a move n puts it n samples later (an off cursor starts in the middle); past either end it goes to the middle and the trend scrolls one percent of the span (guess for the mode). AN -1 = every trend of the page."
        )]
        private static Value TrnSetCursor(SimHost h, CicodeTask t, ArgList a)
        {
            long an = An(a),
                move = A.Short(A.I(a.Get(1)));
            if (!HasWindow(h, t) || !SetterAn(an))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            var ts = Targets(h, t, an, out int err);
            if (err != 0)
            {
                W.Raise(t, err);
                return W.I(err);
            }
            foreach (var tr in ts)
            {
                if (move == 0)
                {
                    if (tr.Cursor >= 0)
                    {
                        tr.Cursor = -1;
                        tr.Touch();
                    }
                    continue;
                }
                int n = Math.Max(1, tr.Samples),
                    half = n >> 1;
                int c = (tr.Cursor < 0 ? half : tr.Cursor) - (int)move;
                if (c > n - 1 || c < 0)
                {
                    Scroll(Wd(h), tr, c < 0 ? 1 : -1, 1);
                    c = half;
                }
                tr.Cursor = c;
                tr.Touch();
            }
            return W.I(0);
        }

        private static double? SampleBack(SimHost h, PageTrend tr, TrendPen p, int index) =>
            TrendData.ValueAt(Wd(h), p, TrendData.SampleTime(Wd(h), tr, p, index));

        [Builtin(
            "TrnGetCursorValue",
            Doc = "1193837",
            Errors = "274 no window, an AN outside 1..31999 or a pen outside 0..8 (raised, 0); 300 no trend at the AN (raised, 0); 341 the cursor is off (returned as 0, not raised)",
            Notes = "The pen's sample at the cursor; TrnGetInvalidValue() where the history has none."
        )]
        private static Value TrnGetCursorValue(SimHost h, CicodeTask t, ArgList a) => Getter(t, () =>
            {
                long an = An(a),
                    pen = Pen(a, 1);
                if (!HasWindow(h, t) || !GetterAn(an) || pen < 0 || pen > 8)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.R(0);
                }
                var tr = Find(h, t, an, out int err);
                if (tr == null)
                {
                    W.Raise(t, err);
                    return W.R(0);
                }
                var p = tr.Pen(pen);
                if (tr.Cursor < 0 || p == null || !p.Used)
                    return W.R(0);
                return W.R(SampleBack(h, tr, p, tr.Cursor) ?? Trends.Invalid);
            });

        [Builtin(
            "TrnGetCursorValueStr",
            Doc = "1193838",
            Errors = "274 no window, an AN outside 1..31999 or a pen outside 0..8 (raised, '<na>'); 300 no trend at the AN (raised, ''); 341 the cursor is off (raised, '')",
            Notes = "The cursor value formatted as TrnGetScaleStr (Mode not 0 appends the units); '<na>' for an invalid sample or an unused pen, '<gated>' for a gated one."
        )]
        private static Value TrnGetCursorValueStr(SimHost h, CicodeTask t, ArgList a) => Getter(t, () =>
            {
                long an = An(a),
                    pen = Pen(a, 1);
                bool units = a.Count > 2 && A.I(a.Get(2)) != 0;
                if (!HasWindow(h, t) || !GetterAn(an) || pen < 0 || pen > 8)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.S("<na>");
                }
                var tr = Find(h, t, an, out int err);
                if (tr == null)
                {
                    W.Raise(t, err);
                    return W.Empty;
                }
                // the cursor off: the runtime's code 0x155, trapped (unlike TrnGetCursorValue's)
                if (tr.Cursor < 0)
                {
                    W.Raise(t, NoCursor);
                    return W.Empty;
                }
                var p = tr.Pen(pen);
                if (p == null || !p.Used)
                    return W.S("<na>");
                return W.S(TrendData.Text(SampleBack(h, tr, p, tr.Cursor) ?? Trends.Invalid, p, units));
            });

        private static Value CursorTime(SimHost h, CicodeTask t, ArgList a, bool ms) => Getter(t, () =>
            {
                long an = An(a),
                    pen = Pen(a, 1);
                if (!HasWindow(h, t) || !GetterAn(an) || pen < 0 || pen > 8)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.I(0);
                }
                var tr = Find(h, t, an, out int err);
                if (tr == null)
                {
                    W.Raise(t, err);
                    return W.I(0);
                }
                var p = tr.Pen(pen);
                if (tr.Cursor < 0 || p == null || !p.Used)
                    return W.I(0);
                long ft = TrendData.SampleTime(Wd(h), tr, p, tr.Cursor);
                return W.I(ms ? TrendData.Millis(ft) : TrendData.Unix(ft));
            });

        [Builtin(
            "TrnGetCursorTime",
            Doc = "1193836",
            Errors = "274 no window, an AN outside 1..31999 or a pen outside 0..8 (raised, 0); 300 no trend at the AN (raised, 0)",
            Notes = "The time (seconds since 1970) of the sample at the cursor (the pen's end - period x position); 0 when the cursor is off."
        )]
        private static Value TrnGetCursorTime(SimHost h, CicodeTask t, ArgList a) =>
            CursorTime(h, t, a, false);

        [Builtin(
            "TrnGetCursorMSTime",
            Doc = "1193834",
            Errors = "274 no window, an AN outside 1..31999 or a pen outside 0..8 (raised, 0); 300 no trend at the AN (raised, 0)",
            Notes = "The milliseconds of TrnGetCursorTime's time."
        )]
        private static Value TrnGetCursorMSTime(SimHost h, CicodeTask t, ArgList a) =>
            CursorTime(h, t, a, true);

        private static Value BufTime(SimHost h, CicodeTask t, ArgList a, bool ms) => Getter(t, () =>
            {
                long an = An(a),
                    pen = Pen(a, 1),
                    index = A.Short(A.I(a.Get(2)));
                if (!HasWindow(h, t) || !GetterAn(an) || pen < 0 || pen > 8)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.I(0);
                }
                var tr = Find(h, t, an, out int err);
                if (tr == null)
                {
                    W.Raise(t, err);
                    return W.I(0);
                }
                var p = tr.Pen(pen);
                if (index < 0 || index >= tr.Samples || p == null || !p.Used)
                    return W.I(0);
                long ft = TrendData.SampleTime(Wd(h), tr, p, (int)index);
                return W.I(ms ? TrendData.Millis(ft) : TrendData.Unix(ft));
            });

        [Builtin(
            "TrnGetBufTime",
            Doc = "1193830",
            Errors = "274 no window, an AN outside 1..31999 or a pen outside 0..8 (raised, 0); 300 no trend at the AN (raised, 0)",
            Notes = "The time of sample Index back from the newest (0 = the newest); 0 outside 0 .. samples - 1."
        )]
        private static Value TrnGetBufTime(SimHost h, CicodeTask t, ArgList a) => BufTime(h, t, a, false);

        [Builtin(
            "TrnGetBufMSTime",
            Doc = "",
            Errors = "274 no window, an AN outside 1..31999 or a pen outside 0..8 (raised, 0); 300 no trend at the AN (raised, 0)",
            Notes = "The milliseconds of TrnGetBufTime's time."
        )]
        private static Value TrnGetBufMSTime(SimHost h, CicodeTask t, ArgList a) => BufTime(h, t, a, true);

        [Builtin(
            "TrnGetBufValue",
            Doc = "1193831",
            Errors = "274 no window, an AN outside 1..31999 or a pen outside 0..8 (raised, 0); 300 no trend at the AN (trapped, 0); 257 an index outside -1 .. samples - 1 (trapped, 0)",
            Notes = "The pen's sample Index back from the newest (0 = the newest; -1 = the newest too: guess); TrnGetInvalidValue() where the history has none."
        )]
        private static Value TrnGetBufValue(SimHost h, CicodeTask t, ArgList a) => Getter(t, () =>
            {
                long an = An(a),
                    pen = Pen(a, 1),
                    index = A.Short(A.I(a.Get(2)));
                if (!HasWindow(h, t) || !GetterAn(an) || pen < 0 || pen > 8)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.R(0);
                }
                var tr = Find(h, t, an, out int err);
                if (tr == null)
                {
                    W.Raise(t, err);
                    return W.R(0);
                }
                if (index < -1 || index >= tr.Samples)
                {
                    W.Raise(t, Codes.OutOfRange);
                    return W.R(0);
                }
                var p = tr.Pen(pen);
                if (p == null || !p.Used)
                    return W.R(0);
                return W.R(SampleBack(h, tr, p, (int)Math.Max(0, index)) ?? Trends.Invalid);
            });

        // ------------------------------------------------------------------ event trends
        private static Value EventZero(SimHost h, CicodeTask t, ArgList a) => Getter(t, () =>
            {
                long an = An(a),
                    pen = Pen(a, 1);
                if (!HasWindow(h, t) || !GetterAn(an) || pen < 0 || pen > 8)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.I(0);
                }
                if (Find(h, t, an, out int err) == null)
                    W.Raise(t, err);
                return W.I(0);
            });

        [Builtin(
            "TrnGetEvent",
            Doc = "1193841",
            Errors = "274 no window, an AN outside 1..31999, a pen outside 0..8 or a percent above 100 (raised, 0); 300 no trend at the AN (raised, 0)",
            Notes = "Event numbers come from event trends, which the simulator does not keep: 0 (the runtime's answer for a pen with no event number)."
        )]
        private static Value TrnGetEvent(SimHost h, CicodeTask t, ArgList a)
        {
            if (a.Count > 2 && A.UShort(A.I(a.Get(2))) > 100)
                return Getter(t, () =>
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.I(0);
                });
            return EventZero(h, t, a);
        }

        [Builtin(
            "TrnGetCursorEvent",
            Doc = "1193833",
            Errors = "274 no window, an AN outside 1..31999 or a pen outside 0..8 (raised, 0); 300 no trend at the AN (raised, 0)",
            Notes = "0: only an event pen (pen 2) has event numbers, and the simulator keeps none."
        )]
        private static Value TrnGetCursorEvent(SimHost h, CicodeTask t, ArgList a) => EventZero(h, t, a);

        [Builtin(
            "TrnGetBufEvent",
            Doc = "1193829",
            Errors = "274 no window, an AN outside 1..31999 or a pen outside 0..8 (raised, 0); 300 no trend at the AN (raised, 0)",
            Notes = "0 (no event trends)."
        )]
        private static Value TrnGetBufEvent(SimHost h, CicodeTask t, ArgList a) => EventZero(h, t, a);

        [Builtin(
            "TrnSetEvent",
            Doc = "1193869",
            Errors = "274 no window, an AN outside -2..31999, a pen outside -1..8 or a negative event (raised and returned); 300 no trend at the AN (raised, returned)",
            Notes = "Event trends are not simulated: the trend is found and nothing changes; 0."
        )]
        private static Value TrnSetEvent(SimHost h, CicodeTask t, ArgList a)
        {
            long an = An(a),
                pen = Pen(a, 1),
                ev = A.I(a.Get(2));
            if (!HasWindow(h, t) || !SetterAn(an) || pen < -1 || pen > 8 || ev < 0)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            Targets(h, t, an, out int err);
            if (err != 0)
            {
                W.Raise(t, err);
                return W.I(err);
            }
            return W.I(0);
        }

        // ================================================================== trend objects
        [Builtin(
            "TrnNew",
            Doc = "1193860",
            Effects = "makes a trend object at the AN of the current window's page with the given pens, or keeps the one there",
            Errors = "274 no window or an AN outside 1..31999 (raised and returned)",
            Notes = "The trend definition (a trend symbol 'lib.name' or number) gives the trend's samples in the runtime; the simulator takes them from the AN's drawing when it is a trend, else 100 (guess). An empty Trend does nothing (the runtime returns 0 for a definition it cannot find). Tag1..Tag8 are the pens ('' = none), ClusterName their cluster. A trend object already at the AN with the same definition, or with none yet (a PGTREND trend before its first TrnNew), is kept as it is, pens included, and the Tags are not applied (only an object of another definition is freed, and made again; verify that the tags are unused then): SA_Library Trend.ci calls TrnNew before each of its TrnSetPen calls."
        )]
        private static Value TrnNew(SimHost h, CicodeTask t, ArgList a)
        {
            long an = An(a);
            var w = Win(h, t);
            if (w == null || !GetterAn(an))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            string sym = CitectText.Trim(A.S(a.Get(1)));
            if (sym.Length == 0 || w.Page == null)
                return W.I(0);
            string cluster = a.Count > 10 ? CitectText.Trim(A.S(a.Get(10))) : "";
            var tb = Tables(h);
            var old = tb.Trend(w.Number, w.Page.Name, (int)an);
            // the AN's trend object is freed and made again only when
            // its definition is set and another one; else it stays and takes the definition
            if (old != null && (old.Symbol.Length == 0 || C.IEq(old.Symbol, sym)))
            {
                old.Symbol = sym;
                if (old.Cluster.Length == 0)
                    old.Cluster = cluster;
                return W.I(0);
            }
            var tr = Make(w, (int)an);
            if (old != null)
            {
                tb.Trends.Remove(old);
                if (tb.Selected == old)
                    tb.Selected = tr;
            }
            tr.Symbol = sym;
            tr.Cluster = cluster;
            tb.Trends.Add(tr);
            for (int k = 0; k < 8; k++)
            {
                string tag = a.Count > 2 + k ? CitectText.Trim(A.S(a.Get(2 + k))) : "";
                if (tag.Length > 0)
                    TrendData.SetPen(Wd(h), tr, k, tag);
            }
            return W.I(0);
        }

        /// <summary>The trend object for a PGTREND row of a page being shown.</summary>
        public static PageTrend Open(
            World.World world,
            Window win,
            int an,
            IList<string> pens,
            string cluster,
            bool periodic,
            int trendNo
        )
        {
            var tb = PageObjectTables.Of(world.Display);
            string page = win.Page?.Name ?? "";
            tb.Trends.RemoveAll(x =>
                !x.Pending && x.Window == win.Number && x.An == an && C.IEq(x.Page, page)
            );
            var made = Make(win, an);
            var tr = tb.Adopt(win.Number, page, an);
            if (tr != null)
            {
                tr.Samples = made.Samples;
                tr.PixelsPerSample = made.PixelsPerSample;
                tr.PenColours.Clear();
                tr.PenColours.AddRange(made.PenColours);
            }
            else
            {
                tr = made;
                tb.Trends.Add(tr);
            }
            tr.TrendNo = trendNo;
            tr.Periodic = periodic;
            if (tr.Cluster.Length == 0)
                tr.Cluster = cluster;
            if (tr.PenCount == 0)
                for (int k = 0; k < 8 && k < pens.Count; k++)
                    if (pens[k].Length > 0)
                        TrendData.SetPen(world, tr, k, pens[k]);
            return tr;
        }

        /// <summary>A new trend object for an AN of a window's page: its samples and pen colours
        /// from the drawing when the AN is a trend (tc 0x1003).</summary>
        public static PageTrend Make(Window w, int an)
        {
            var tr = new PageTrend
            {
                Window = w.Number,
                Page = w.Page?.Name ?? "",
                An = an,
            };
            var d = w.Page?.Drawing?.Get(an);
            if (d?.Trend != null)
            {
                if (d.Trend.Samples > 0)
                    tr.Samples = d.Trend.Samples;
                if (d.Trend.PixelsPerSample > 0)
                    tr.PixelsPerSample = d.Trend.PixelsPerSample;
                tr.PenColours.AddRange(d.Trend.Pens);
            }
            return tr;
        }

        [Builtin(
            "TrnDelete",
            Doc = "1193817",
            Effects = "removes the trend object (its pens cleared)",
            Errors = "274 no window or an AN outside 1..31999 (trapped, returned); 300 no trend at the AN (trapped, returned)"
        )]
        private static Value TrnDelete(SimHost h, CicodeTask t, ArgList a)
        {
            long an = An(a);
            if (!HasWindow(h, t) || !GetterAn(an))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            var tr = Find(h, t, an, out int err);
            if (tr == null)
            {
                W.Raise(t, err);
                return W.I(err);
            }
            var tb = Tables(h);
            tb.Trends.Remove(tr);
            if (tb.Selected == tr)
                tb.Selected = null;
            return W.I(0);
        }

        [Builtin(
            "TrnSelect",
            Doc = "1193865",
            Effects = "selects (making it when needed) the trend at the AN of a page in a window: later calls with AN -2 act on it; the page takes it when it is shown",
            Errors = "274 an AN not -3 and outside 1..31999, or a window below -3 / -1 (raised and returned)",
            Notes = "Window -3 = the current window, -2 = the next window displayed; AN -3 = the first trend on the page. A trend of a page not shown yet holds its pens until the page shows them (they replace the page's own pens: doc)."
        )]
        private static Value TrnSelect(SimHost h, CicodeTask t, ArgList a)
        {
            long win = A.Short(A.I(a.Get(0)));
            string page = CitectText.Trim(A.S(a.Get(1)));
            long an = A.Short(A.I(a.Get(2)));
            string cluster = a.Count > 3 ? CitectText.Trim(A.S(a.Get(3))) : "";
            if (!(an == -3 || GetterAn(an)))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            if (win == -3)
                win = D(h).WindowOf(t);
            if (!(win >= 0 || win == -2))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            var tb = Tables(h);
            PageTrend? tr = null;
            if (win >= 0)
                tr = tb.Trend((int)win, page, (int)an)
                    ?? tb.Trends.Find(x => x.Pending && x.Window == win && x.An == an && C.IEq(x.Page, page));
            if (tr == null)
            {
                tr = new PageTrend
                {
                    Window = (int)win,
                    Page = page,
                    An = (int)an,
                    Pending = true,
                };
                var w = D(h).OpenWindow((int)win);
                if (w?.Page != null && C.IEq(w.Page.Name, page))
                {
                    var made = Make(w, (int)an);
                    made.Pending = false;
                    tr = made;
                }
                tb.Trends.Add(tr);
            }
            tr.Cluster = cluster;
            tb.Selected = tr;
            return W.I(0);
        }

        [Builtin(
            "TrnEcho",
            Doc = "1193819",
            Errors = "274 no window or an AN outside 1..31999 (raised, 0)",
            Notes = "Mode 0 holds the trend's redraw (a count, 0 returned); another mode releases one hold and returns 1 when the last was released (the trend redraws), else 0. No trend: 0."
        )]
        private static Value TrnEcho(SimHost h, CicodeTask t, ArgList a)
        {
            long an = An(a),
                mode = A.Short(A.I(a.Get(1)));
            if (!HasWindow(h, t) || !GetterAn(an))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(0);
            }
            var tr = Find(h, t, an, out int err);
            if (tr == null)
            {
                W.Raise(t, err);
                return W.I(0);
            }
            if (mode == 0)
            {
                tr.EchoHold++;
                return W.I(0);
            }
            if (tr.EchoHold > 0 && --tr.EchoHold == 0)
            {
                tr.Touch();
                return W.I(1);
            }
            return W.I(0);
        }

        [Builtin(
            "TrnGetCluster",
            Doc = "1193832",
            Errors = "274 no window or an AN outside -2..31999 (raised, ''); 300 no trend at the AN (raised, '')",
            Notes = "The cluster the trend's pens are on: the one TrnNew / TrnSelect gave, else the unit's first cluster (the cluster its trend server is in)."
        )]
        private static Value TrnGetCluster(SimHost h, CicodeTask t, ArgList a)
        {
            long an = An(a);
            if (!HasWindow(h, t) || !SetterAn(an))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.Empty;
            }
            var tr = Find(h, t, an, out int err);
            if (tr == null)
            {
                W.Raise(t, err);
                return W.Empty;
            }
            if (tr.Cluster.Length > 0)
                return W.S(C.Cut(tr.Cluster));
            foreach (string c in Wd(h).Unit.Clusters())
                return W.S(C.Cut(c));
            return W.Empty;
        }

        [Builtin(
            "TrnClientInfo",
            Doc = "1193815",
            Errors = "274 no window, an AN outside 1..31999 or a type outside 1..2 (raised; Error set); 300 no trend at the AN (raised; Error set)",
            Notes = "Type 1: the trend's samples as text; type 2: the trend's load state ('1' once its pens have data, '-1' before: guess). Error (by reference) = 0 or the error. Data is ignored."
        )]
        private static Value TrnClientInfo(SimHost h, CicodeTask t, ArgList a) => Getter(t, () =>
            {
                long an = An(a),
                    type = A.Short(A.I(a.Get(2)));
                Value er = a.Count > 4 ? a.Get(4) : Value.Nothing;
                W.Out(t, er, 0);
                if (!HasWindow(h, t) || !GetterAn(an) || type < 1 || type > 2)
                {
                    W.Out(t, er, Codes.InvalidArg);
                    W.Raise(t, Codes.InvalidArg);
                    return W.Empty;
                }
                var tr = Find(h, t, an, out int err);
                if (tr == null)
                {
                    W.Out(t, er, err);
                    W.Raise(t, err);
                    return W.Empty;
                }
                return W.S(
                    type == 1 ? C.Inv(tr.Samples)
                    : tr.PenCount > 0 ? "1"
                    : "-1"
                );
            });

        [Builtin(
            "TrnFlush",
            Doc = "1193828",
            Errors = "274 an empty tag (raised and returned)",
            Notes = "Asks the trend server to write its buffers to disk; the simulated server has no files: 0."
        )]
        private static Value TrnFlush(SimHost h, CicodeTask t, ArgList a)
        {
            if (CitectText.Trim(A.S(a.Get(0))).Length == 0)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            return W.I(0);
        }

        // ================================================================== markers
        [Builtin(
            "DspMarkerNew",
            Doc = "1104703",
            Effects = "adds a marker to the trend",
            Errors = "274 no window, an AN outside 1..31999 or a mode other than 0 / 1 (raised, -1) (guess for the mode); 300 no trend at the AN (raised, -1) (guess)",
            Notes = "Mode 0 a vertical marker, 1 a horizontal one, in Color (no flashing). The handle is the trend's marker number, 0..9 in the order they were made (guess); a trend with 10 markers: -1. Not shown until DspMarkerMove places it. Markers stay drawn over the trend's updates (the runtime erases them unless the trend paint event, OnEvent 22, draws them again: the simulator raises no paint event) (guess)."
        )]
        private static Value DspMarkerNew(SimHost h, CicodeTask t, ArgList a)
        {
            long an = An(a);
            if (an == -2)
                an = TaskContext.An(t);
            long mode = A.I(a.Get(1));
            if (!HasWindow(h, t) || !GetterAn(an) || (mode != 0 && mode != 1))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            var tr = Find(h, t, an, out int err);
            if (tr == null)
            {
                W.Raise(t, err);
                return W.I(-1);
            }
            if (tr.Markers.Count >= PageTrend.MaxMarkers)
                return W.I(-1);
            tr.Markers.Add(new TrendMarker { Horizontal = mode == 1, Colour = Plots.Colour(A.I(a.Get(2))) });
            return W.I(tr.Markers.Count - 1);
        }

        [Builtin(
            "DspMarkerMove",
            Doc = "1104702",
            Effects = "places the trend's marker",
            Errors = "274 no window or an AN outside 1..31999 (raised and returned); 300 no trend at the AN (raised and returned) (guess); 269 no such marker (raised and returned) (guess)",
            Notes = "A vertical marker at Offset samples from the trend's left edge (0..the trend's samples), a horizontal one at Offset / 32000 of the trend's height from its bottom (0..32000); an offset outside is kept in the range (guess). The viewer gets the placed markers in the trend's state."
        )]
        private static Value DspMarkerMove(SimHost h, CicodeTask t, ArgList a)
        {
            long an = An(a);
            if (an == -2)
                an = TaskContext.An(t);
            if (!HasWindow(h, t) || !GetterAn(an))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            var tr = Find(h, t, an, out int err);
            if (tr == null)
            {
                W.Raise(t, err);
                return W.I(err);
            }
            long hm = A.I(a.Get(1));
            if (hm < 0 || hm >= tr.Markers.Count)
            {
                W.Raise(t, Codes.BadHandle);
                return W.I(Codes.BadHandle);
            }
            var m = tr.Markers[(int)hm];
            long max = m.Horizontal ? 32000 : Math.Max(1, tr.Samples);
            int off = (int)Math.Max(0, Math.Min(max, A.I(a.Get(2))));
            if (m.Offset != off)
            {
                m.Offset = off;
                tr.Touch();
            }
            return W.I(0);
        }

        // ================================================================== the archive (not simulated)
    }
}
