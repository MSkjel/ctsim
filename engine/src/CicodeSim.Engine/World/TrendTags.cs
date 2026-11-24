using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.World
{
    /// <summary>One trend tag of trend.dbf / spc.dbf: the columns a trend object reads (kept
    /// small: a large project has tens of thousands).</summary>
    public sealed class TrendTag
    {
        public string Name = "";

        /// <summary>The trend's expression as configured (EXPR; the Name's own string when equal).</summary>
        public string Expr = "";

        /// <summary>SAMPLEPER in seconds ('hh:mm:ss[.fff]' or seconds; 1 when not set).</summary>
        public double SamplePeriod = 1.0;

        /// <summary>ENG_ZERO / ENG_FULL when configured.</summary>
        public double? EngZero,
            EngFull;
        public string Units = "";
        public string Format = "";
        public string Comment = "";

        /// <summary>The variable tag the expression is when it is one tag (a bare name or an
        /// Equip.Item), "" for any other expression.</summary>
        public string BareTag = "";

        /// <summary>An SPC tag (spc.dbf): its series Tag.X, Tag.R ... are pens of SPC charts.</summary>
        public bool Spc;

        /// <summary>EQUIP, ITEM, FILENAME and TRIG as configured (shared strings).</summary>
        public string Equip = "",
            Item = "",
            FileName = "",
            Trig = "";

        /// <summary>The other catalogue columns, shared between trends with the same values.</summary>
        internal string[] Settings = TrendTags.NoSettings;

        /// <summary>A trend.dbf / spc.dbf column as configured ('' for a column the catalogue
        /// does not keep: TAGGENLINK, EDITCODE, LINKED).</summary>
        public string Column(string name)
        {
            // the browse asks in upper case already: no copy then (tens of thousands of trends a browse on a large project)
            string n = name;
            foreach (char c in name)
                if (c >= 'a' && c <= 'z')
                {
                    n = name.ToUpperInvariant();
                    break;
                }
            switch (n)
            {
                case "NAME":
                    return Name;
                case "EXPR":
                    return Expr;
                case "ENG_UNITS":
                    return Units;
                case "FORMAT":
                    return Format;
                case "COMMENT":
                    return Comment;
                case "EQUIP":
                    return Equip;
                case "ITEM":
                    return Item;
                case "FILENAME":
                    return FileName;
                case "TRIG":
                    return Trig;
            }
            int k = Array.IndexOf(TrendTags.SettingColumns, n);
            return k >= 0 && k < Settings.Length ? Settings[k] : "";
        }
    }

    /// <summary>A set of names compared without case, kept as 32-bit hashes only (far smaller
    /// than the strings on a large project). A hash that collides
    /// only makes a name look like a member (the tag change log then keeps one tag too many).</summary>
    public sealed class NameHashes
    {
        private readonly HashSet<int> _h = new HashSet<int>();

        public int Count => _h.Count;

        /// <summary>FNV-1a over the characters folded as the engine compares names (char by char
        /// to lower case, LowerComparer): a non-ASCII letter written in the other case in an
        /// expression is the same name.</summary>
        public static int Hash(string s) => LowerComparer.Hash(s);

        public void Add(string name) => _h.Add(Hash(name));

        public bool Contains(string name) => _h.Contains(Hash(name));
    }

    /// <summary>The trend tags of a World's unit.</summary>
    public sealed class TrendTags
    {
        private static readonly ConditionalWeakTable<World, TrendTags> All =
            new ConditionalWeakTable<World, TrendTags>();

        private readonly World _world;

        /// <summary>By name without case (no lower-case copy of every name).</summary>
        private readonly Dictionary<string, TrendTag> _byName = new Dictionary<string, TrendTag>(
            StringComparer.OrdinalIgnoreCase
        );
        private readonly List<TrendTag> _list = new List<TrendTag>();
        private NameHashes? _exprTags;
        private bool _loaded;
        private readonly object _lock = new object();

        private static readonly string[] Columns =
        {
            "NAME",
            "EXPR",
            "SAMPLEPER",
            "ENG_ZERO",
            "ENG_FULL",
            "ENG_UNITS",
            "FORMAT",
            "COMMENT",
            "EQUIP",
            "ITEM",
            "FILENAME",
            "TRIG",
        };

        /// <summary>The catalogue columns a trend keeps in its shared <see cref="TrendTag.Settings"/>
        /// (what TrnInfo and the trend browse read besides the named fields).</summary>
        public static readonly string[] SettingColumns =
        {
            "SAMPLEPER",
            "PRIV",
            "AREA",
            "FILES",
            "TIME",
            "PERIOD",
            "TYPE",
            "SPCFLAG",
            "LSL",
            "USL",
            "SUBGRPSIZE",
            "XDOUBLEBAR",
            "RANGE",
            "SDEVIATION",
            "STORMETHOD",
            "CLUSTER",
            "DEADBAND",
            "HISTORIAN",
            "ENG_ZERO",
            "ENG_FULL",
        };

        internal static readonly string[] NoSettings = EmptyRow(SettingColumns.Length);

        private static string[] EmptyRow(int n)
        {
            var r = new string[n];
            for (int i = 0; i < n; i++)
                r[i] = "";
            return r;
        }

        private TrendTags(World w)
        {
            _world = w;
        }

        /// <summary>Simulator only: seed trend history back to the start.</summary>
        public bool SeedHistory;

        /// <summary>The trend tags of <paramref name="w"/> (read at the first use).</summary>
        public static TrendTags Of(World w) => All.GetValue(w, x => new TrendTags(x));

        private static string Key(string name) => CitectText.Lower(CitectText.Trim(name));

        private void Load()
        {
            if (_loaded)
                return;
            lock (_lock)
            {
                if (_loaded)
                    return;
                // streamed (tens of thousands of trends on a large project): no row list; the repeated values
                // (sample periods, scales, units, formats) shared
                var share = new StringInterner();
                var rows = new Dictionary<string, string[]>(StringComparer.Ordinal);
                var key = new System.Text.StringBuilder();
                foreach (var (table, spc) in new[] { ("trend.dbf", false), ("spc.dbf", true) })
                foreach (var (_, path) in _world.Unit.Tables(table, "compile"))
                {
                    int[]? picks = null,
                        setPicks = null;
                    bool isSpc = spc;
                    Dbf.ForEach(path, r =>
                    {
                        if (picks == null)
                        {
                            picks = new int[Columns.Length];
                            for (int c = 0; c < Columns.Length; c++)
                                picks[c] = r.Header.IndexOf(Columns[c]);
                            setPicks = new int[SettingColumns.Length];
                            for (int c = 0; c < SettingColumns.Length; c++)
                                setPicks[c] = r.Header.IndexOf(SettingColumns[c]);
                        }
                        string n = r.Field(picks[0]);
                        if (n.Length == 0 || _byName.ContainsKey(n))
                            return;
                        string F(int c) => r.Field(picks[c], share);
                        string e = r.Field(picks[1]);
                        var t = new TrendTag
                        {
                            Name = n,
                            Expr = e == n ? n : e,
                            SamplePeriod = Seconds(F(2)),
                            EngZero = FloatText.TryParseNumber(F(3), out double z) ? z : (double?)null,
                            EngFull = FloatText.TryParseNumber(F(4), out double f) ? f : (double?)null,
                            Units = F(5),
                            Format = F(6),
                            Comment = r.Field(picks[7]),
                            Spc = isSpc,
                            Equip = F(8),
                            Item = F(9),
                            FileName = F(10),
                            Trig = F(11),
                        };
                        if (t.SamplePeriod <= 0)
                            t.SamplePeriod = 1.0;
                        // the other columns: one shared array per distinct row of values
                        key.Clear();
                        bool any = false;
                        for (int c = 0; c < setPicks!.Length; c++)
                        {
                            string v = r.Field(setPicks[c], share);
                            if (v.Length > 0)
                                any = true;
                            key.Append(v).Append('\u0001');
                        }
                        if (any)
                        {
                            string k = key.ToString();
                            if (!rows.TryGetValue(k, out var row))
                            {
                                row = new string[setPicks.Length];
                                for (int c = 0; c < setPicks.Length; c++)
                                    row[c] = r.Field(setPicks[c], share);
                                rows[k] = row;
                            }
                            t.Settings = row;
                        }
                        _byName[n] = t;
                        _list.Add(t);
                    });
                }
                _loaded = true;
            }
        }

        /// <summary>The trend tags in definition order.</summary>
        public IReadOnlyList<TrendTag> List()
        {
            Load();
            return _list;
        }

        public int Count
        {
            get
            {
                Load();
                return _list.Count;
            }
        }

        /// <summary>A trend tag by name ('Cluster.Tag' allowed, as Builtins.Trends.Find), null when none.</summary>
        public TrendTag? Find(string? name)
        {
            Load();
            string n = CitectText.Trim(name ?? "");
            if (n.Length == 0)
                return null;
            if (_byName.TryGetValue(n, out var t))
                return t;
            int dot = n.IndexOf('.');
            if (dot > 0 && _byName.TryGetValue(n.Substring(dot + 1), out t))
                return t;
            return null;
        }

        /// <summary>The series an SPC trend gives (Include spc.ci: 'Tag.X', 'Tag.XUCL' ... the
        /// mean, range and standard deviation with their control limits).</summary>
        private static readonly HashSet<string> SpcSeries = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase
        )
        {
            "X",
            "XUCL",
            "XLCL",
            "XCL",
            "R",
            "RUCL",
            "RLCL",
            "RCL",
            "S",
            "SUCL",
            "SLCL",
            "SCL",
        };

        /// <summary>A pen naming an SPC series of an SPC tag ('Weight_SPC.XUCL'): the SPC tag,
        /// null otherwise (its series: Builtins.SpcSeries).</summary>
        public TrendTag? SpcTrend(string? name)
        {
            string n = CitectText.Trim(name ?? "");
            int dot = n.LastIndexOf('.');
            if (dot <= 0 || !SpcSeries.Contains(n.Substring(dot + 1)))
                return null;
            Load();
            return _byName.TryGetValue(n.Substring(0, dot), out var t) && t.Spc ? t : null;
        }

        /// <summary>'hh:mm:ss[.fff]' or a number of seconds (0 when neither).</summary>
        public static double Seconds(string text)
        {
            string s = CitectText.Trim(text ?? "");
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

        /// <summary>The variable tag a pen samples.</summary>
        public string? PenTag(string pen)
        {
            var t = Find(pen);
            if (t != null)
            {
                if (t.BareTag.Length == 0 && t.Expr.Length > 0)
                    t.BareTag = SingleTag(t.Expr) ?? "\u0001";
                return t.BareTag == "\u0001" ? null : t.BareTag;
            }
            return SingleTag(pen);
        }

        /// <summary>The variable tag an expression is when it is exactly one name (a tag, a local
        /// variable or an Equip.Item), null otherwise.</summary>
        public string? SingleTag(string expr)
        {
            string e = CitectText.Trim(expr ?? "");
            if (e.Length == 0)
                return null;
            foreach (char c in e)
                if (!IsNameChar(c))
                    return null;
            var d = _world.Tags.Find(e);
            return d?.Name;
        }

        private static bool IsNameStart(char c) =>
            c == '_' || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c >= '\u0080';

        private static bool IsNameChar(char c) => IsNameStart(c) || (c >= '0' && c <= '9') || c == '.';

        /// <summary>The names an expression contains (letters, digits, '_' and '.'; a string
        /// literal skipped), each also without its '.Field' / '.Item' tail.</summary>
        public static IEnumerable<string> Names(string expr)
        {
            int i = 0,
                n = expr.Length;
            while (i < n)
            {
                char c = expr[i];
                if (c == '"')
                {
                    // a string literal ('^' escapes the next character)
                    i++;
                    while (i < n && expr[i] != '"')
                    {
                        if (expr[i] == '^')
                            i++;
                        i++;
                    }
                    i++;
                    continue;
                }
                if (IsNameStart(c))
                {
                    int s = i;
                    while (i < n && IsNameChar(expr[i]))
                        i++;
                    string name = expr.Substring(s, i - s).TrimEnd('.');
                    if (name.Length > 0)
                    {
                        yield return name;
                        int dot = name.IndexOf('.');
                        if (dot > 0)
                            yield return name.Substring(0, dot);
                    }
                    continue;
                }
                if (c >= '0' && c <= '9')
                {
                    while (i < n && (IsNameChar(expr[i])))
                        i++;
                    continue;
                }
                i++;
            }
        }

        // ------------------------------------------------------------------ expression samples
        /// <summary>Samples of the trends whose expression is not one tag (the trend server
        /// evaluates the compiled EXPR every SAMPLEPER; the page animator does it for the trends
        /// a pen shows, from the first time one does: Page.TrendSampler).</summary>
        private readonly Dictionary<string, List<(double T, double V, ulong Q)>> _samples = new Dictionary<
            string,
            List<(double, double, ulong)>
        >(StringComparer.Ordinal);

        /// <summary>Samples kept per expression trend (the older half dropped past it).</summary>
        public const int MaxSamples = 65536;

        /// <summary>The expression trends sampled so far.</summary>
        public int SampledCount => _samples.Count;

        public void RecordSample(string trend, double t, double value, ulong quality)
        {
            string k = Key(trend);
            if (!_samples.TryGetValue(k, out var l))
                _samples[k] = l = new List<(double, double, ulong)>();
            l.Add((t, value, quality));
            if (l.Count > MaxSamples)
                l.RemoveRange(0, l.Count / 2);
        }

        public bool IsSampled(string trend) => _samples.ContainsKey(Key(trend));

        /// <summary>The first sample of an expression trend (see <see cref="SeedHistory"/>);
        /// false when it has none yet.</summary>
        public bool TryFirstSample(string trend, out double value, out ulong quality)
        {
            value = 0;
            quality = 0;
            if (!_samples.TryGetValue(Key(trend), out var l) || l.Count == 0)
                return false;
            value = l[0].V;
            quality = l[0].Q;
            return true;
        }

        /// <summary>The sample of an expression trend at simulated time <paramref name="t"/>: the
        /// last one at or before it; false before the first.</summary>
        public bool TrySampleAt(string trend, double t, out double value, out ulong quality)
        {
            value = 0;
            quality = 0;
            if (!_samples.TryGetValue(Key(trend), out var l) || l.Count == 0)
                return false;
            int lo = 0,
                hi = l.Count - 1,
                at = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (l[mid].T <= t + 1e-9)
                {
                    at = mid;
                    lo = mid + 1;
                }
                else
                    hi = mid - 1;
            }
            if (at < 0)
                return false;
            value = l[at].V;
            quality = l[at].Q;
            return true;
        }

        /// <summary>The names the trend expressions read.</summary>
        public NameHashes ExpressionTags()
        {
            if (_exprTags != null)
                return _exprTags;
            var s = new NameHashes();
            void Add(string expr)
            {
                foreach (string name in Names(expr))
                    s.Add(name);
            }
            if (_loaded)
                foreach (var t in _list)
                    Add(t.Expr);
            else
            {
                // stream the EXPR column only (every row: a trend defined twice adds its other
                // expression's names too, harmless here); the catalogue itself is read when a
                // trend needs it
                foreach (string table in new[] { "trend.dbf", "spc.dbf" })
                foreach (var (_, path) in _world.Unit.Tables(table, "compile"))
                {
                    int ei = -2;
                    Dbf.ForEach(path, r =>
                    {
                        if (ei == -2)
                            ei = r.Header.IndexOf("EXPR");
                        if (ei >= 0)
                            Add(r.Field(ei));
                    });
                }
            }
            _exprTags = s;
            return s;
        }
    }
}
