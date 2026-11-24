using System;
using System.Collections.Generic;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;

namespace CicodeSim.Builtins
{
    /// <summary>Trend tags and history without a page.</summary>
    public static class Trends
    {
        public static readonly double Invalid = BitConverter.Int64BitsToDouble(
            unchecked((long)0xFFFFBBBBFFFFBBBBUL)
        );
        public static readonly double Gated = BitConverter.Int64BitsToDouble(
            unchecked((long)0xFFFFAAAAFFFFAAAAUL)
        );

        /// <summary>[Trend]MaxRequestLength's default (doc 1193855).</summary>
        public const int MaxRequestLength = 4000;

        /// <summary>A trend tag by name, or null.</summary>
        internal static TrendTag? Find(SimHost h, string tag) =>
            h.World == null ? null : TrendTags.Of(h.World).Find(tag);

        private static string Get(TrendTag r, string f) => r.Column(f);

        /// <summary>'hh:mm:ss[.fff]' or a plain number of seconds (0 when neither).</summary>
        internal static double Seconds(string text)
        {
            string s = CitectText.Trim(text);
            if (s.Length == 0)
                return 0;
            if (s.IndexOf(':') < 0)
                return FloatText.TryParseNumber(s, out double d) ? d : 0;
            double total = 0;
            foreach (string part in s.Split(':'))
            {
                if (!FloatText.TryParseNumber(CitectText.Trim(part), out double x))
                    return 0;
                total = total * 60 + x;
            }
            return total;
        }

        private static string Num(double d) =>
            d == Math.Floor(d) && Math.Abs(d) < 1e15 ? C.Inv((long)d) : FloatText.Shortest(d);

        /// <summary>The sample period in seconds (1 when not set).</summary>
        internal static double SamplePeriod(TrendTag r) => r.SamplePeriod > 0 ? r.SamplePeriod : 1.0;

        private static string Info(SimHost h, TrendTag r, long type)
        {
            var w = h.World!;
            switch (type)
            {
                case 1:
                {
                    string ty = Get(r, "TYPE");
                    long? v = ty.Length == 0 ? 1 : w.Unit.LabelValue(ty, null);
                    // Include's labels (corpus: TRN_PERIODIC 1, TRN_EVENT 2, TRN_PERIODIC_EVENT 3)
                    // when the unit has none
                    if (v == null)
                        v =
                            C.IEq(ty, "TRN_PERIODIC") ? 1
                            : C.IEq(ty, "TRN_EVENT") ? 2
                            : C.IEq(ty, "TRN_PERIODIC_EVENT") ? 3
                            : (long?)null;
                    return v != null ? C.Inv(v.Value) : ty;
                }
                case 2:
                    return Num(SamplePeriod(r));
                case 3:
                {
                    string f = Get(r, "FILENAME");
                    int slash = Math.Max(f.LastIndexOf('\\'), f.LastIndexOf('/'));
                    int dot = f.LastIndexOf('.');
                    return dot > slash ? f.Substring(0, dot) : f;
                }
                case 4:
                    return C.Inv(w.Unit.LabelValue(Get(r, "AREA"), 0) ?? 0);
                case 5:
                    return C.Inv(w.Unit.LabelValue(Get(r, "PRIV"), 0) ?? 0);
                case 6:
                    return "0";
                case 7:
                    return Get(r, "ENG_UNITS");
                case 8:
                    return Get(r, "STORMETHOD").IndexOf("8", StringComparison.Ordinal) >= 0 ? "8" : "2";
                case 9:
                {
                    string p = CitectText.Upper(Get(r, "PERIOD"));
                    if (
                        p.StartsWith("MONTH", StringComparison.Ordinal)
                        || p.StartsWith("YEAR", StringComparison.Ordinal)
                    )
                        return "0";
                    if (
                        p.StartsWith("DAY", StringComparison.Ordinal)
                        || p.StartsWith("DAI", StringComparison.Ordinal)
                    )
                        return "86400";
                    if (p.StartsWith("WEEK", StringComparison.Ordinal))
                        return "604800";
                    return Num(Seconds(p));
                }
            }
            return "";
        }

        [Builtin(
            "TrnInfo",
            Doc = "1193858",
            Blocking = true,
            Errors = "289 an empty tag (raised, '',); 274 a type outside 1..9 (raised, '',); 289 an unknown trend (raised, ''; guess: the trend server's answer is not read); 282 in foreground code (raised, '')",
            Notes = "trend.dbf of the unit: 1 TYPE as its label number (TRN_PERIODIC 1, TRN_EVENT 2, TRN_PERIODIC_EVENT 3; empty = 1), 2 SAMPLEPER in seconds, 3 FILENAME without its extension (an unset file name '': the default path is not simulated), 4 AREA, 5 PRIV, 6 the event number 0, 7 ENG_UNITS, 8 STORMETHOD 8 (floating point) or 2 (scaled), 9 the file PERIOD in seconds (0 for monthly / yearly; verify the empty default). Waits one turn (the trend server's request)."
        )]
        private static Value TrnInfo(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("TrnInfo");
            string tag = A.S(a.Get(0));
            long type = A.Short(A.I(a.Get(1)));
            // an empty tag 0x121, then a type outside 1..9 0x112, then the request
            // (CodeBlock -1 in foreground code is 0x11a)
            if (tag.Length == 0 || type < 1 || type > 9)
            {
                W.Raise(t, tag.Length == 0 ? Codes.NameNotFound : Codes.InvalidArg);
                return W.Empty;
            }
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.Empty;
            }
            return W.BlockOneTurn(t, "TrnInfo", () =>
            {
                var r = Find(h, tag);
                if (r == null)
                {
                    W.Raise(t, Codes.NameNotFound);
                    return W.Empty;
                }
                return W.S(C.Cut(Info(h, r, type)));
            });
        }

        /// <summary>The trend's engineering range: ENG_ZERO / ENG_FULL of trend.dbf, else its
        /// expression's tag range.</summary>
        private static (double Lo, double Hi) Scale(SimHost h, TrendTag r)
        {
            var w = h.World!;
            bool lo = FloatText.TryParseNumber(Get(r, "ENG_ZERO"), out double z);
            bool hi = FloatText.TryParseNumber(Get(r, "ENG_FULL"), out double f);
            if (lo && hi)
                return (z, f);
            var def = w.Tags.Find(Get(r, "EXPR")) ?? w.Tags.FindItem(Get(r, "EXPR"));
            var (ez, ef) = def != null ? def.EngRange() : (0.0, 100.0);
            return (lo ? z : ez, hi ? f : ef);
        }

        [Builtin(
            "TrnGetDefScale",
            Doc = "1193839",
            Blocking = true,
            Errors = "289 an unknown trend (returned); 282 in foreground code (raised and returned)",
            Notes = "ENG_ZERO / ENG_FULL of trend.dbf, else the expression's variable tag's engineering range (0..100 when it is not a tag), into the two REAL references; 0. Waits one turn."
        )]
        private static Value TrnGetDefScale(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("TrnGetDefScale");
            string tag = A.S(a.Get(0));
            Value lo = a.Get(1),
                hi = a.Get(2);
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.I(Codes.FgBlock);
            }
            return W.BlockOneTurn(t, "TrnGetDefScale", () =>
            {
                var r = Find(h, tag);
                if (r == null)
                    return W.I(Codes.NameNotFound);
                var (z, f) = Scale(h, r);
                W.Out(t, lo, Value.Real(z));
                W.Out(t, hi, Value.Real(f));
                return W.I(0);
            });
        }

        [Builtin(
            "TrnGetInvalidValue",
            Doc = "1193844",
            Notes = "The double with the bits 0xFFFFBBBB_FFFFBBBB (a NaN; the VM keeps the bits)."
        )]
        private static Value TrnGetInvalidValue(SimHost h, CicodeTask t, ArgList a) => W.R(Invalid);

        [Builtin(
            "TrnGetGatedValue",
            Doc = "1193843",
            Notes = "The double with the bits 0xFFFFAAAA_FFFFAAAA (a NaN)."
        )]
        private static Value TrnGetGatedValue(SimHost h, CicodeTask t, ArgList a) => W.R(Gated);

        /// <summary>1 gated, 2 invalid (both markers' halves, or any NaN), 0 valid.</summary>
        internal static long Validity(double v)
        {
            long bits = BitConverter.DoubleToInt64Bits(v);
            int lo = (int)bits,
                hi = (int)(bits >> 32);
            if (lo == -0x5556 && hi == -0x5556)
                return 1;
            if (lo == -0x4445 && hi == -0x4445)
                return 2;
            return double.IsNaN(v) ? 2 : 0;
        }

        [Builtin(
            "TrnIsValidValue",
            Doc = "1193859",
            Notes = "0 a valid value, 1 the gated marker, 2 the invalid marker or any other NaN."
        )]
        private static Value TrnIsValidValue(SimHost h, CicodeTask t, ArgList a) =>
            W.I(Validity(A.R(a.Get(0))));

        /// <summary>The trend server's browse fields.</summary>
        internal static readonly string[] BrowseFields =
        {
            // the TREND table's fields, in its order
            "CLUSTER",
            "NAME",
            "AREA",
            "PRIV",
            "SAMPLEPER",
            "TYPE",
            "ENG_UNITS",
            "FORMAT",
            "FILENAME",
            "FILES",
            "TIME",
            "COMMENT",
            "STORMETHOD",
            "SPCFLAG",
            "LSL",
            "USL",
            "SUBGRPSIZE",
            "XDOUBLEBAR",
            "RANGE",
            "SDEVIATION",
            "DEADBAND",
            "EQUIPMENT",
            "ITEM",
            "HISTORIAN",
            // the trend server's own
            "RAW_ZERO",
            "RAW_FULL",
            "ENG_ZERO",
            "ENG_FULL",
            "EXPRESSION",
            "TRIGGER",
            "ACQERROR",
            // NAME's other name
            "TAG",
        };

        /// <summary>The Cicode operator keywords: not tag names (a tag cannot be called AND).</summary>
        private static readonly HashSet<string> OperatorWords = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase
        )
        {
            "AND",
            "OR",
            "NOT",
            "MOD",
            "BITAND",
            "BITOR",
            "BITXOR",
        };

        /// <summary>The one tag an expression names, or ''.</summary>
        private static string OneTag(SimHost h, string expr)
        {
            if (expr.Length == 0 || expr.IndexOf('(') >= 0)
                return "";
            string? one = null;
            foreach (string n in TrendTags.Names(expr))
            {
                if (OperatorWords.Contains(n))
                    continue;
                if (one == null)
                    one = n;
                else if (
                    !(
                        one.StartsWith(n, StringComparison.OrdinalIgnoreCase)
                        && one.Length > n.Length
                        && one[n.Length] == '.'
                    ) && !C.IEq(one, n)
                )
                    return ""; // a second name (Names also gives a dotted name without its tail)
            }
            if (one == null)
                return "";
            var w = h.World!;
            return w.Tags.Find(one) != null || w.Tags.FindItem(one) != null ? one : "";
        }

        /// <summary>A float field as the trend server sends it ("%.7g").</summary>
        private static string Float7g(double v) => Printf.Format("%.7g", PrintfArg.Float((float)v));

        /// <summary>A trend browse field of a trend, or null if it isn't a field.</summary>
        private static string? BrowseField(SimHost h, TrendTag r, string f)
        {
            switch (f)
            {
                case "TAG":
                case "NAME":
                    return r.Name;
                case "EQUIPMENT":
                    return r.Equip;
                case "ITEM":
                    return r.Item;
                case "EXPRESSION":
                    return OneTag(h, r.Expr);
                case "TRIGGER":
                    return OneTag(h, r.Trig);
                case "RAW_ZERO":
                case "RAW_FULL":
                case "ACQERROR":
                    return "";
                case "TYPE":
                    return Info(h, r, 1);
                case "AREA":
                    return Info(h, r, 4);
                case "PRIV":
                    return Info(h, r, 5);
                case "SAMPLEPER":
                    return Float7g(SamplePeriod(r));
                case "SPCFLAG":
                    return r.Spc ? "1" : "0";
                case "FORMAT":
                    return C.Inv((long)(int)Compiler.Codegen.FormatWords.OfTagFormat(r.Format, out _));
                case "HISTORIAN":
                {
                    string v = r.Column("HISTORIAN");
                    if (v.Length == 0)
                        return "0";
                    long? n = h.World!.Unit.LabelValue(v, null);
                    return C.Inv(n ?? (C.IEq(v, "TRUE") ? 1 : 0));
                }
                case "DEADBAND":
                {
                    string v = r.Column("DEADBAND");
                    return v.Length == 0 ? "-1"
                        : FloatText.TryParseNumber(v, out double d) ? Float7g(d)
                        : v;
                }
            }
            return Array.IndexOf(BrowseFields, f) >= 0 ? r.Column(f) : null;
        }

        /// <summary>Every name of the list is a field of the trend server's format.</summary>
        private static bool AllFields(HashSet<string>? names)
        {
            if (names != null)
                foreach (string n in names)
                    if (Array.IndexOf(BrowseFields, n) < 0)
                        return false;
            return true;
        }

        [Builtin(
            "TrnBrowseOpen",
            Doc = "1193813",
            Blocking = true,
            Errors = "282 in foreground code (raised, -1); 413 a requested or filter field the trend server's format lacks (raised, -1, once the trend server's answer is in)",
            Notes = "The trend tags of the unit (trend.dbf, then spc.dbf, as _TREND.RDB holds them) with the trend server's fields: the TREND table's (CLUSTER, NAME, AREA, PRIV, SAMPLEPER, TYPE, ENG_UNITS, FORMAT, FILENAME, FILES, TIME, COMMENT, STORMETHOD, SPCFLAG, LSL, USL, SUBGRPSIZE, XDOUBLEBAR, RANGE, SDEVIATION, DEADBAND, EQUIPMENT, ITEM, HISTORIAN; the compiled record's text: TYPE / AREA / PRIV numbers, SAMPLEPER seconds, SPCFLAG 1 for an SPC tag, FORMAT the compiled word in decimal) and its own (RAW_ZERO, RAW_FULL, ENG_ZERO, ENG_FULL, EXPRESSION and TRIGGER: the one tag of the expression, ACQERROR); TAG is NAME. The Browse filter rules (a value without a field is NAME; an empty value matches every record), sorted by NAME."
        )]
        private static Value TrnBrowseOpen(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("TrnBrowseOpen");
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.I(-1);
            }
            var fl = Browse.Compile(A.S(a.Get(0)), "NAME", noTermMatchesAll: true);
            string fields = a.Count > 1 ? A.S(a.Get(1)) : "";
            // the server looks every requested field, then every filter field, up in its format
            // (an empty name or TAG is NAME) and answers 0x19d for one it lacks
            if (!AllFields(Browse.FieldSet(fields)) || !Browse.FieldsKnown(fl, BrowseFields))
                return W.BlockOneTurn(t, "TrnBrowseOpen", () =>
                {
                    W.Raise(t, Codes.BrowseFieldNotFound);
                    return W.I(-1);
                });
            var cl = Browse.FieldSet(a.Count > 2 ? A.S(a.Get(2)) : "");
            // the session holds indexes into the World's catalogue, not copies
            var all = TrendTags.Of(h.World).List();
            Func<int, string, string?> get = (i, f) => BrowseField(h, all[i], f);
            var m = new List<int>();
            for (int i = 0; i < all.Count; i++)
            {
                string c = all[i].Column("CLUSTER");
                if (cl != null && c.Length > 0 && !cl.Contains(c.ToUpperInvariant()))
                    continue;
                int k = i;
                if (fl.Groups.Count == 0 || Browse.Match(fl, f => get(k, f)))
                    m.Add(i);
            }
            return Browse.Open(h, t, "Trn", Browse.Sorted(m, get, Browse.SortSpec("", "NAME")), get, fields);
        }

        [Builtin(
            "TrnBrowseFirst",
            Doc = "1193809",
            Blocking = true,
            Errors = "410 bad session; 412 no records; 282 in foreground code (returned)"
        )]
        private static Value TrnBrowseFirst(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "Trn", 0, true);

        [Builtin(
            "TrnBrowseNext",
            Doc = "1193811",
            Blocking = true,
            Errors = "410 bad session; 412 past the last record; 282 in foreground code (returned)"
        )]
        private static Value TrnBrowseNext(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "Trn", 1, true);

        [Builtin(
            "TrnBrowsePrev",
            Doc = "1193814",
            Blocking = true,
            Errors = "410 bad session; 412 before the first record; 282 in foreground code (returned)"
        )]
        private static Value TrnBrowsePrev(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "Trn", -1, true);

        [Builtin(
            "TrnBrowseGetField",
            Doc = "1193810",
            Errors = "410 bad session; 412 no current record; 413 unknown field (raised, '' returned)"
        )]
        private static Value TrnBrowseGetField(SimHost h, CicodeTask t, ArgList a) =>
            Browse.GetField(h, t, a, "Trn");

        [Builtin("TrnBrowseNumRecords", Doc = "1193812", Errors = "410 bad session (raised, -1)")]
        private static Value TrnBrowseNumRecords(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Count(h, t, a, "Trn");

        [Builtin("TrnBrowseClose", Doc = "1193808", Errors = "410 bad session (returned)")]
        private static Value TrnBrowseClose(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Close(h, t, a, "Trn");

        /// <summary>Write element <paramref name="k"/> of the REAL table a reference starts (a
        /// module / global array element: the following elements; a local: element 0 only).</summary>
        internal static bool Put(CicodeTask t, in Value table, int k, double v)
        {
            if (table.O is CodeVarRef cr)
                return cr.Slot.Set(cr.Element + k, Value.Real(v)) == 0;
            if (k == 0)
            {
                W.Out(t, table, Value.Real(v));
                return true;
            }
            return false;
        }

        [Builtin(
            "TrnGetTable",
            Doc = "1193855",
            Blocking = true,
            Errors = "274 an empty tag, a negative Time or Period, a Length of 32000+ (raised, 0) or above 4000 ([Trend]MaxRequestLength) (raised, 0); 289 an unknown trend (raised, 0); 282 in foreground code (raised, 0)",
            Notes = "Length samples Period seconds apart (0 = the trend's sample period) ending at Time + Milliseconds (0 = now), newest first (DisplayMode + 2: oldest first), from the World's tag change log of the trend's expression (a variable tag or Equip.Item; another expression gives invalid samples): each sample is the value at that time (step; the condense / stretch modes are not simulated). Times before the log knows the value, and bad-quality values, are TrnGetInvalidValue(); DisplayMode bit 1 clear writes them as 0. Fills the REAL array the Table argument starts (a local variable: its one element); returns Length. Waits one turn."
        )]
        private static Value TrnGetTable(SimHost h, CicodeTask t, ArgList a)
        {
            var w = h.World;
            if (w == null)
                return Tags.NoWorld("TrnGetTable");
            string tag = A.S(a.Get(0));
            long time = A.I(a.Get(1));
            double period = A.R(a.Get(2));
            long length = A.UShort(A.I(a.Get(3)));
            Value table = a.Get(4);
            long mode = A.I(a.Get(5));
            long ms = a.Count > 6 ? A.I(a.Get(6)) : 0;
            if (tag.Length == 0 || time < 0 || !(period >= 0.0) || length >= 32000)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(0);
            }
            if (length > MaxRequestLength)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(0);
            }
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.I(0);
            }
            var hist = TagHistory.Attach(w);
            return W.BlockOneTurn(t, "TrnGetTable", () =>
            {
                var r = Find(h, tag);
                if (r == null)
                {
                    W.Raise(t, Codes.NameNotFound);
                    return W.I(0);
                }
                double p = period > 0 ? period : SamplePeriod(r);
                string expr = Get(r, "EXPR");
                var def = w.Tags.Find(expr) ?? w.Tags.FindItem(expr);
                double end = time == 0 ? w.Clock.Now() : time + ms / 1000.0 - w.Clock.StartUnix;
                bool oldestFirst = (mode & 2) != 0,
                    keep = (mode & 1) != 0;
                for (int i = 0; i < length; i++)
                {
                    double at = end - i * p;
                    double v = Invalid;
                    // (a sample Cicode wrote with TrnSetTable first)
                    if (TrendArchive.TryWritten(h, tag, at + w.Clock.StartUnix, p, out double written))
                    {
                        Put(t, table, oldestFirst ? (int)(length - 1 - i) : i, written);
                        continue;
                    }
                    if (
                        def != null
                        && hist.TryValueAt(def.Name, at, out double x, out ulong q)
                        && (q & 0xC0) != 0
                    )
                        v = x;
                    if (!keep && Validity(v) != 0)
                        v = 0.0;
                    int k = oldestFirst ? (int)(length - 1 - i) : i;
                    Put(t, table, k, v);
                }
                return W.I(length);
            });
        }

        // ------------------------------------------------------------------ not simulated
        private const long SchdDay = 864000000000L;

        /// <summary>(from, to) FILETIME of a scheduler browse: Start (default today at
        /// midnight, UTC) and Duration (default 86400 s).</summary>
        private static (long, long) SchdWindow(SimHost h, in ArgList a, int k)
        {
            long now = (long)h.World!.Clock.NowFiletime();
            long from = k < a.Count && !a.Get(k).IsNothing ? (long)A.U64(a.Get(k)) : now / SchdDay * SchdDay;
            long dur = k + 1 < a.Count && !a.Get(k + 1).IsNothing ? A.I(a.Get(k + 1)) : 86400;
            return (from, from + Math.Max(0, dur) * 10_000_000L);
        }

        private static string SchdArg(in ArgList a, int k) =>
            k < a.Count && !a.Get(k).IsNothing ? A.S(a.Get(k)) : "";

        [Builtin(
            "SchdOpen",
            Doc = "1189313",
            Blocking = true,
            Errors = "282 in foreground code (raised, -1); 274 no world (raised, -1)",
            Notes = "The occurrences of the equipment's items in the simulation's scheduler (World.EquipScheduler), by start; times as FILETIME numbers."
        )]
        private static Value SchdOpen(SimHost h, CicodeTask t, ArgList a)
        {
            if (t.Foreground || h.World == null)
            {
                W.Raise(t, t.Foreground ? Codes.FgBlock : Codes.InvalidArg);
                return W.I(-1);
            }
            string eq = SchdArg(a, 0);
            var (from, to) = SchdWindow(h, a, 1);
            var rows = CicodeSim
                .World.EquipScheduler.Of(h.World)
                .Rows(from, to, it => string.Equals(it.Equipment, eq, StringComparison.OrdinalIgnoreCase));
            return Browse.OpenRows(
                h,
                t,
                "Schd",
                rows,
                Browse.Compile(SchdArg(a, 3), "STATE"),
                SchdArg(a, 4),
                new List<(string, bool)>()
            );
        }

        // the scheduler browse sessions (the shared browse functions ..)
        // over the simulation's own scheduler (World.EquipScheduler)
        [Builtin(
            "SchdFirst",
            Doc = "1189309",
            Errors = "410 bad session (returned)",
            Notes = "The sessions SchdOpen opens (World.EquipScheduler)."
        )]
        private static Value SchdFirst(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "Schd", 0, false);

        [Builtin("SchdNext", Doc = "1189311", Errors = "410 bad session (returned)")]
        private static Value SchdNext(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "Schd", 1, false);

        [Builtin("SchdPrev", Doc = "1189314", Errors = "410 bad session (returned)")]
        private static Value SchdPrev(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "Schd", -1, false);

        [Builtin("SchdGetField", Doc = "1189310", Errors = "410 bad session (raised, '')")]
        private static Value SchdGetField(SimHost h, CicodeTask t, ArgList a) =>
            Browse.GetField(h, t, a, "Schd");

        [Builtin("SchdNumRecords", Doc = "1189312", Errors = "410 bad session (raised, -1)")]
        private static Value SchdNumRecords(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Count(h, t, a, "Schd");

        [Builtin("SchdClose", Doc = "1189301", Errors = "410 bad session (returned)")]
        private static Value SchdClose(SimHost h, CicodeTask t, ArgList a) => Browse.Close(h, t, a, "Schd");

        [Builtin(
            "SchdConfigFirst",
            Doc = "1189303",
            Errors = "410 bad session (returned)",
            Notes = "The sessions SchdConfigOpen opens (World.EquipScheduler)."
        )]
        private static Value SchdConfigFirst(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "SchdConfig", 0, false);

        [Builtin("SchdConfigNext", Doc = "1189305", Errors = "410 bad session (returned)")]
        private static Value SchdConfigNext(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "SchdConfig", 1, false);

        [Builtin("SchdConfigPrev", Doc = "1189308", Errors = "410 bad session (returned)")]
        private static Value SchdConfigPrev(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "SchdConfig", -1, false);

        [Builtin("SchdConfigGetField", Doc = "1189304", Errors = "410 bad session (raised, '')")]
        private static Value SchdConfigGetField(SimHost h, CicodeTask t, ArgList a) =>
            Browse.GetField(h, t, a, "SchdConfig");

        [Builtin("SchdConfigNumRecords", Doc = "1189306", Errors = "410 bad session (raised, -1)")]
        private static Value SchdConfigNumRecords(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Count(h, t, a, "SchdConfig");

        [Builtin("SchdConfigClose", Doc = "1189302", Errors = "410 bad session (returned)")]
        private static Value SchdConfigClose(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Close(h, t, a, "SchdConfig");

        [Builtin(
            "SchdConfigOpen",
            Doc = "1189307",
            Errors = "274 no world (raised, -1)",
            Notes = "The occurrences of every item in the simulation's scheduler (World.EquipScheduler) in the window, by start; times as FILETIME numbers."
        )]
        private static Value SchdConfigOpen(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            var (from, to) = SchdWindow(h, a, 0);
            var rows = CicodeSim.World.EquipScheduler.Of(h.World).Rows(from, to, _ => true);
            return Browse.OpenRows(
                h,
                t,
                "SchdConfig",
                rows,
                Browse.Compile(SchdArg(a, 2), "EQUIPMENT"),
                SchdArg(a, 3),
                new List<(string, bool)>()
            );
        }
    }
}
