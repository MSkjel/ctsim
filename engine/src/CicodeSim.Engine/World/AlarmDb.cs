using System;
using System.Collections.Generic;
using System.Globalization;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.World
{
    /// <summary>The simulated alarm server.</summary>
    public sealed class AlarmDb
    {
        public const string Digital = "digital",
            Analog = "analog",
            Advanced = "advanced",
            Timestamped = "timestamped",
            TsDigital = "ts_digital",
            TsAnalog = "ts_analog",
            MultiDigital = "multi_digital",
            ArgyleAnalog = "argyle_analog",
            DoublePoint = "double_point",
            Hardware = "hardware";

        public static readonly string[] Kinds =
        {
            Digital,
            Analog,
            Advanced,
            Timestamped,
            TsDigital,
            TsAnalog,
            MultiDigital,
            ArgyleAnalog,
            DoublePoint,
        };

        public static readonly Dictionary<string, string> TableNames = new Dictionary<string, string>
        {
            [Digital] = "digalm.dbf",
            [Analog] = "anaalm.dbf",
            [Advanced] = "advalm.dbf",
            [Timestamped] = "hresalm.dbf",
            [TsDigital] = "tsdig.dbf",
            [TsAnalog] = "tsana.dbf",
            [MultiDigital] = "argdig.dbf",
            [ArgyleAnalog] = "argana.dbf",
            [DoublePoint] = "dbptalm.dbf",
        };

        /// <summary>AlarmSumType numbers (doc: 1033513); double point: not documented.</summary>
        public static readonly Dictionary<string, int> SumTypes = new Dictionary<string, int>
        {
            [Digital] = 0,
            [Analog] = 1,
            [Advanced] = 2,
            [MultiDigital] = 3,
            [ArgyleAnalog] = 4,
            [Timestamped] = 6,
            [TsDigital] = 8,
            [TsAnalog] = 9,
        };

        private static readonly string[] AnalogKinds = { Analog, TsAnalog, ArgyleAnalog };

        // analog sub-states, highest priority first, and their .State numbers (doc: 1068486)
        public const string HH = "HH",
            LL = "LL",
            H = "H",
            L = "L",
            R = "R",
            DVH = "DVH",
            DVL = "DVL";
        public static readonly string[] AnalogOrder = { HH, LL, H, L, R, DVH, DVL };
        public static readonly Dictionary<string, int> AnalogState = new Dictionary<string, int>
        {
            [DVH] = 8,
            [DVL] = 9,
            [R] = 10,
            [L] = 11,
            [H] = 12,
            [LL] = 13,
            [HH] = 14,
        };
        public const int StateUnack = 256;
        public const int StateDisabled = 512;

        /// <summary>[Alarm]ScanTime / Period defaults (doc: 1057792, 1057785).</summary>
        public const double ScanTimeDefault = 0.5;
        public const double PeriodDefault = 60.0;

        internal static readonly string[] NoCustom = { "", "", "", "", "", "", "", "" };

        // field names built once (not per row)
        private static readonly string[] CustomFields = Names("CUSTOM", 1, 8);
        private static readonly string[] AlmDescFields = Names("ALMDESC", 0, 8);
        private static readonly string[] AlmStatFields = Names("ALMSTAT", 0, 8);
        private static readonly string[] StateNameFields = Names("STATENAME", 0, 8);
        private static readonly string[] StateTypeFields = Names("STATETYPE", 0, 8);

        private static string[] Names(string stem, int first, int n)
        {
            var o = new string[n];
            for (int i = 0; i < n; i++)
                o[i] = stem + (first + i).ToString(CultureInfo.InvariantCulture);
            return o;
        }

        public Unit Unit { get; }
        public TagDb Tags { get; }
        public SimClock Clock { get; }
        public Users? Users { get; }
        public EquipDb? Equipment { get; }

        private readonly Dictionary<string, Dictionary<string, AlarmDef>> _defs =
            new Dictionary<string, Dictionary<string, AlarmDef>>();
        private readonly Dictionary<string, List<AlarmDef>> _defOrder =
            new Dictionary<string, List<AlarmDef>>();
        private StringInterner? _share;
        private Dictionary<string, AlarmDef>? _byTag;
        private AlarmItemIndex? _byItem;
        private readonly Dictionary<string, AlarmState> _states = new Dictionary<string, AlarmState>(
            LowerComparer.Instance
        );
        private readonly List<string> _stateOrder = new List<string>();
        private Dictionary<string, object>? _deps; // AlarmDef or List<AlarmDef>
        private readonly OrderedSet _dirty = new OrderedSet(LowerComparer.Instance);
        private Action? _watch;
        private Dictionary<long, DbfRow>? _categories,
            _priorities,
            _hwMessages;
        public List<HardwareAlarm> HardwareAlarms { get; } = new List<HardwareAlarm>();
        public List<Action<Transition>> Listeners { get; } = new List<Action<Transition>>();

        /// <summary>The advanced-alarm evaluator (the VM): (expr, alarm) to true / false, or null
        /// when it cannot decide.</summary>
        public Func<string, AlarmDef, bool?>? Evaluator { get; private set; }

        /// <summary>The action hook (the VM): (alarm, event, Cicode text).</summary>
        public Action<AlarmDef, string, string>? ActionHook { get; set; }
        public double ScanTime { get; private set; } = ScanTimeDefault;
        public double Period { get; set; } = PeriodDefault;
        private Timer? _timer;
        public OrderedSet NotEvaluated { get; } = new OrderedSet(LowerComparer.Instance);
        public long Scans { get; private set; }
        private readonly OrderedSet _acqErrors = new OrderedSet(LowerComparer.Instance);
        private (Func<string, AlarmDef, bool?>? ev, List<string> keys)? _scanAlways;
        private readonly OrderedSet _listed = new OrderedSet(LowerComparer.Instance);
        private readonly OrderedSet _disabled = new OrderedSet(LowerComparer.Instance);

        /// <summary>Changes whenever a count could change. Read atomically: the server's
        /// alarmsChanged timer reads it off the simulation thread (a long tears on 32 bit).</summary>
        public long Generation
        {
            get => System.Threading.Interlocked.Read(ref _generation);
            private set => System.Threading.Interlocked.Exchange(ref _generation, value);
        }
        private long _generation;
        private readonly Dictionary<(int, string, string, bool, long), int> _countCache =
            new Dictionary<(int, string, string, bool, long), int>();
        private readonly Dictionary<string, AlarmFilter> _filters = new Dictionary<string, AlarmFilter>(
            StringComparer.Ordinal
        );

        public AlarmDb(Unit unit, TagDb tags, SimClock clock, Users? users = null, EquipDb? equipment = null)
        {
            Unit = unit;
            Tags = tags;
            Clock = clock;
            Users = users;
            Equipment = equipment;
        }

        public bool Started => _watch != null;

        private void Changed(AlarmDef a, AlarmState st)
        {
            if (st.InList)
                _listed.Add(a.Tag);
            else
                _listed.Remove(a.Tag);
            if (st.Disabled)
                _disabled.Add(a.Tag);
            else
                _disabled.Remove(a.Tag);
            Generation++;
            if (_countCache.Count > 0)
                _countCache.Clear();
        }

        private AlarmFilter Filter(string text)
        {
            if (!_filters.TryGetValue(text, out AlarmFilter? f))
            {
                if (_filters.Count > 4096)
                    _filters.Clear();
                f = _filters[text] = new AlarmFilter(text);
            }
            return f;
        }

        // ------------------------------------------------------------------ loading
        public static double ParseDelay(string? text)
        {
            string t = CitectText.Trim(text ?? "");
            if (t.Length == 0)
                return 0.0;
            double secs = 0.0;
            var parts = t.Split(':');
            var vals = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!FloatText.TryParseNumber(parts[i], out vals[i]))
                    return 0.0;
            foreach (double p in vals)
                secs = secs * 60 + p;
            return secs;
        }

        internal static double? F(string? text) => TagTypes.Float(text);

        internal static AlarmVar Var(string? text)
        {
            string t = CitectText.Trim(text ?? "");
            if (t.Length == 0)
                return default;
            string head = t.Length >= 4 ? CitectText.Upper(t.Substring(0, 4)) : CitectText.Upper(t);
            if (head == "NOT " || head == "NOT\t")
                return new AlarmVar(CitectText.Trim(t.Substring(4)), true);
            return new AlarmVar(t, false);
        }

        public Dictionary<string, AlarmDef> Defs(string kind)
        {
            if (_defs.TryGetValue(kind, out var have))
                return have;
            var o = new Dictionary<string, AlarmDef>(LowerComparer.Instance);
            var order = new List<AlarmDef>();
            string table = TableNames[kind];
            // equal strings shared over every alarm table of the unit; the interner is dropped
            // once every kind is loaded
            var share = _share ??= new StringInterner();
            foreach (var (p, path) in Unit.Tables(table, "compile"))
            {
                Dbf.ForEach(path, rec =>
                {
                    string tag = CitectText.Trim(rec.Get("TAG", share));
                    if (tag.Length == 0 || o.ContainsKey(tag))
                        return;
                    var a = Make(kind, tag, rec, share, p.Name);
                    o[tag] = a;
                    order.Add(a);
                });
            }
            _defs[kind] = o;
            _defOrder[kind] = order;
            if (_defs.Count == Kinds.Length)
                _share = null;
            return o;
        }

        private AlarmDef Make(string kind, string tag, DbfRecord r, StringInterner share, string project)
        {
            var a = new AlarmDef(kind, tag, project);
            string G(string name) => r.Get(name, share);
            a.Name = G("NAME");
            a.Desc = G("DESC");
            a.Cluster = G("CLUSTER");
            a.Comment = G("COMMENT");
            a.Help = G("HELP");
            a.Equip = G("EQUIP");
            a.Item = G("ITEM");
            a.CategoryText = G("CATEGORY");
            a.Category = Unit.LabelValue(a.CategoryText, 0) ?? 0;
            a.Area = Unit.LabelValue(G("AREA"), 0) ?? 0;
            a.Priv = Unit.LabelValue(G("PRIV"), 0) ?? 0;
            string[]? custom = null;
            for (int i = 0; i < 8; i++)
            {
                string c = G(CustomFields[i]);
                if (c.Length == 0)
                    continue;
                custom ??= (string[])NoCustom.Clone();
                custom[i] = c;
            }
            a.Custom = custom ?? NoCustom;
            a.Delay = ParseDelay(G("DELAY"));
            string paging = CitectText.Upper(CitectText.Trim(G("PAGING")));
            a.Paging = paging == "TRUE" || paging == "1";
            a.PagingGroup = G("PAGINGGRP");
            a.Historian = G("HISTORIAN");
            if (kind == Digital || kind == TsDigital)
                a.Vars = new[] { Var(G("VAR_A")), Var(G("VAR_B")) };
            else if (kind == Timestamped)
                a.Vars = new[] { Var(G("VAR")) };
            else if (kind == MultiDigital)
            {
                a.Vars = new[] { Var(G("VAR_A")), Var(G("VAR_B")), Var(G("VAR_C")) };
                var states = new (string, string)[8];
                for (int i = 0; i < 8; i++)
                    states[i] = (G(AlmDescFields[i]), G(AlmStatFields[i]));
                a.States = states;
                a.OnFunc = G("ONFUNC");
                a.OffFunc = G("OFFFUNC");
            }
            else if (kind == DoublePoint)
            {
                a.Vars = new[] { Var(G("VAR")) };
                var states = new (string, string)[8];
                for (int i = 0; i < 8; i++)
                    states[i] = (G(StateNameFields[i]), G(StateTypeFields[i]));
                a.States = states;
            }
            else if (kind == Advanced)
                a.Expr = CitectText.Trim(G("EXPR"));
            else if (Array.IndexOf(AnalogKinds, kind) >= 0)
            {
                a.Vars = new[] { Var(G("VAR")) };
                a.Setpoint = CitectText.Trim(G("SETPOINT"));
                a.Limits = new Dictionary<string, double?>(StringComparer.Ordinal)
                {
                    [HH] = F(G("HIGHHIGH")),
                    [H] = F(G("HIGH")),
                    [L] = F(G("LOW")),
                    [LL] = F(G("LOWLOW")),
                    ["DEV"] = F(G("DEVIATION")),
                    [R] = F(G("RATE")),
                };
                double? db = F(G("DEADBAND"));
                a.Deadband = db == null || db.Value == 0 ? 0.0 : db.Value;
                double dev = ParseDelay(G("DEVDELAY"));
                a.Delays = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    [HH] = ParseDelay(G("HHDELAY")),
                    [H] = ParseDelay(G("HDELAY")),
                    [L] = ParseDelay(G("LDELAY")),
                    [LL] = ParseDelay(G("LLDELAY")),
                    [DVH] = dev,
                    [DVL] = dev,
                    [R] = 0.0,
                };
                a.Format = G("FORMAT");
            }
            return a;
        }

        /// <summary>Every alarm in kind order, then definition order.</summary>
        public List<AlarmDef> All()
        {
            var o = new List<AlarmDef>();
            foreach (string k in Kinds)
            {
                Defs(k);
                o.AddRange(_defOrder[k]);
            }
            return o;
        }

        private IEnumerable<AlarmDef> AllIter()
        {
            foreach (string k in Kinds)
            {
                Defs(k);
                foreach (var a in _defOrder[k])
                    yield return a;
            }
        }

        public Dictionary<string, int> Counts()
        {
            var c = new Dictionary<string, int>();
            foreach (string k in Kinds)
                c[k] = Defs(k).Count;
            c[Hardware + "_messages"] = HardwareMessages().Count;
            return c;
        }

        private void Index()
        {
            if (_byTag != null)
                return;
            var byTag = new Dictionary<string, AlarmDef>(LowerComparer.Instance);
            var withItems = new List<AlarmDef>();
            int ordinal = 0;
            foreach (var a in AllIter())
            {
                a.Ordinal = ordinal++;
                if (!byTag.ContainsKey(a.Tag))
                    byTag[a.Tag] = a;
                if (a.Equip.Length > 0 && a.Item.Length > 0)
                    withItems.Add(a);
            }
            _byTag = byTag;
            _byItem = new AlarmItemIndex(withItems);
        }

        /// <summary>An alarm by its tag, or by 'Equipment.Item'.</summary>
        public AlarmDef? Get(string? name)
        {
            Index();
            string t = CitectText.Trim(name ?? "");
            if (_byTag!.TryGetValue(t, out var a))
                return a;
            return _byItem!.Find(CitectText.Lower(t));
        }

        private Dictionary<long, DbfRow> NumberedRows(string table, string field)
        {
            var o = new Dictionary<long, DbfRow>();
            foreach (var (_, r) in Unit.Rows(table))
            {
                long? n = Unit.LabelValue(r.Get(field), null);
                if (n != null && !o.ContainsKey(n.Value))
                    o[n.Value] = r;
            }
            return o;
        }

        public Dictionary<long, DbfRow> Categories() =>
            _categories ??= NumberedRows("category.dbf", "CATEGORY");

        public Dictionary<long, DbfRow> Priorities() =>
            _priorities ??= NumberedRows("almprior.dbf", "PRIORITY");

        /// <summary>ERROR code to hardalm.dbf row (the hardware alarm messages, corpus: System).</summary>
        public Dictionary<long, DbfRow> HardwareMessages() =>
            _hwMessages ??= NumberedRows("hardalm.dbf", "ERROR");

        /// <summary>The alarm's priority: its category's PRIORITY (0 = default, doc: 1068445).</summary>
        public long Priority(AlarmDef a) =>
            Categories().TryGetValue(a.Category, out DbfRow? c)
                ? Unit.LabelValue(c.Get("PRIORITY"), 0) ?? 0
                : 0;

        // ------------------------------------------------------------------ state
        public AlarmState State(AlarmDef a)
        {
            if (!_states.TryGetValue(a.Tag, out AlarmState? st))
            {
                st = new AlarmState();
                _states[a.Tag] = st;
                _stateOrder.Add(a.Tag);
            }
            return st;
        }

        public AlarmState? PeekState(AlarmDef a) => _states.TryGetValue(a.Tag, out var st) ? st : null;

        private Dictionary<string, object> DepsMap()
        {
            if (_deps != null)
                return _deps;
            // keyed by the input name itself, compared without case (no lowered copies)
            var deps = new Dictionary<string, object>(LowerComparer.Instance);
            foreach (var a in AllIter())
            {
                foreach (string name in Inputs(a))
                {
                    string key = StripArraySuffix(name);
                    if (key.IndexOf('.') >= 0)
                    {
                        // an Equip.Item: its tag's name
                        string? canon = Tags.Canonical(CitectText.Lower(key));
                        key = canon ?? key;
                    }
                    if (!deps.TryGetValue(key, out object? have))
                        deps[key] = a;
                    else if (have is List<AlarmDef> l)
                        l.Add(a);
                    else
                        deps[key] = new List<AlarmDef> { (AlarmDef)have, a };
                }
            }
            _deps = deps;
            return deps;
        }

        /// <summary>The text without a trailing '[n]' (and a newline after it).</summary>
        private static string StripArraySuffix(string s)
        {
            int end = s.Length;
            if (end > 0 && s[end - 1] == '\n')
                end--; // a final newline after the ']'
            if (end == 0 || s[end - 1] != ']')
                return s;
            int i = end - 2;
            while (i >= 0 && CitectText.IsWhite(s[i]))
                i--;
            int dEnd = i;
            while (i >= 0 && char.IsDigit(s[i]))
                i--;
            if (i == dEnd)
                return s;
            while (i >= 0 && CitectText.IsWhite(s[i]))
                i--;
            if (i < 0 || s[i] != '[')
                return s;
            return s.Substring(0, i) + s.Substring(end);
        }

        private List<string> Inputs(AlarmDef a)
        {
            var names = new List<string>();
            foreach (var v in a.Vars)
                if (v.IsSet)
                    names.Add(v.Name);
            if (a.Kind == Advanced)
            {
                var simple = SimpleTag(a);
                if (simple.IsSet)
                    names.Add(simple.Name);
            }
            if (a.IsAnalog && a.Setpoint.Length > 0 && F(a.Setpoint) == null)
                names.Add(a.Setpoint);
            return names;
        }

        /// <summary>An advanced alarm's expression when it is one defined tag ('NOT tag'); a name
        /// that is no tag (a label, a function-like name) is an expression for the evaluator.</summary>
        private AlarmVar SimpleTag(AlarmDef a)
        {
            var simple = SimpleExpr(a.Expr);
            if (simple.IsSet && Tags.Canonical(simple.Name) == null)
                return default;
            return simple;
        }

        /// <summary>The alarm's input tag names.</summary>
        public List<string> InputsOf(AlarmDef a) => Inputs(a);

        /// <summary>An advanced alarm's one-tag expression, default when none.</summary>
        public AlarmVar SimpleTagOf(AlarmDef a) => SimpleTag(a);

        internal static AlarmVar SimpleExpr(string? expr)
        {
            string e = CitectText.Trim(expr ?? "");
            bool neg = false;
            if (e.Length >= 4 && CitectText.Upper(e.Substring(0, 4)) == "NOT ")
            {
                neg = true;
                e = CitectText.Trim(e.Substring(4));
            }
            if (IsName(e))
            {
                string up = CitectText.Upper(e);
                if (up != "TRUE" && up != "FALSE" && up != "NOT" && up != "AND" && up != "OR")
                    return new AlarmVar(e, neg);
            }
            return default;
        }

        private static bool IsWord(char c)
        {
            if (c == '_')
                return true;
            switch (CharUnicodeInfo.GetUnicodeCategory(c))
            {
                case UnicodeCategory.UppercaseLetter:
                case UnicodeCategory.LowercaseLetter:
                case UnicodeCategory.TitlecaseLetter:
                case UnicodeCategory.ModifierLetter:
                case UnicodeCategory.OtherLetter:
                case UnicodeCategory.DecimalDigitNumber:
                case UnicodeCategory.LetterNumber:
                case UnicodeCategory.OtherNumber:
                    return true;
            }
            return false;
        }

        /// <summary>A plain tag name: an ASCII letter, '_' or '\' first, then word characters
        /// (letters, digits, '_' of any script), '\' and '.', optionally '[digits]' at the end.</summary>
        private static bool IsName(string e)
        {
            if (e.Length == 0)
                return false;
            char c0 = e[0];
            if (!((c0 >= 'A' && c0 <= 'Z') || (c0 >= 'a' && c0 <= 'z') || c0 == '_' || c0 == '\\'))
                return false;
            int i = 1,
                n = e.Length;
            while (i < n && (IsWord(e[i]) || e[i] == '\\' || e[i] == '.'))
                i++;
            if (i == n)
                return true;
            if (e[i] != '[')
                return false;
            int j = i + 1;
            int d0 = j;
            while (j < n && char.IsDigit(e[j]))
                j++;
            return j > d0 && j == n - 1 && e[j] == ']';
        }

        // ------------------------------------------------------------------ the scan
        /// <summary>Scan every [Alarm]ScanTime on the simulated clock and re-evaluate alarms whose
        /// tags changed (subscribes to the tag database).</summary>
        public void Start(double? scanTime = null)
        {
            double st;
            if (scanTime == null)
            {
                long? ms = Unit.LabelValue(Unit.Param("Alarm", "ScanTime", "") ?? "", null);
                st = ms != null && ms.Value != 0 ? ms.Value / 1000.0 : ScanTimeDefault;
            }
            else
                st = scanTime.Value;
            long? per = Unit.LabelValue(Unit.Param("Alarm", "Period", "") ?? "", null);
            if (per != null && per.Value != 0)
                Period = per.Value;
            ScanTime = st;
            if (_watch == null)
            {
                _watch = Tags.Subscribe(OnTag);
                EvaluateAll();
            }
            if (_timer == null)
                _timer = Clock.Every(ScanTime, () => Scan(), label: "alarm scan");
        }

        public void Stop()
        {
            if (_timer != null)
            {
                _timer.Cancel();
                _timer = null;
            }
            if (_watch != null)
            {
                _watch();
                _watch = null;
            }
        }

        private void OnTag(TagChange change)
        {
            if (DepsMap().TryGetValue(change.Name, out object? v))
            {
                if (v is AlarmDef one)
                    _dirty.Add(one.Tag);
                else
                    foreach (var a in (List<AlarmDef>)v)
                        _dirty.Add(a.Tag);
            }
        }

        /// <summary>Mark every alarm with inputs for the next scan.</summary>
        public void EvaluateAll()
        {
            foreach (var a in AllIter())
                if ((a.Kind == Advanced && a.Expr.Length > 0) || Inputs(a).Count > 0)
                    _dirty.Add(a.Tag);
        }

        /// <summary>One alarm server scan: alarms whose tags changed, alarms waiting for a delay,
        /// rate-of-change alarms, and advanced alarms the evaluator answers.</summary>
        public List<Transition> Scan()
        {
            Scans++;
            double now = Clock.Now();
            var keys = new OrderedSet();
            foreach (string k in _dirty.ToList())
                keys.Add(k);
            _dirty.Clear();
            var o = new List<Transition>();
            foreach (string sk in _stateOrder)
            {
                var st = _states[sk];
                if (st.Pending.Count > 0 || (st.DisabledUntil != null && now >= st.DisabledUntil.Value))
                    keys.Add(sk);
            }
            foreach (string k in EveryScan())
                keys.Add(k);
            Index();
            foreach (var a in InDefinitionOrder(keys))
                o.AddRange(Evaluate(a, now));
            return o;
        }

        private static readonly Comparison<AlarmDef> ByOrdinal = (x, y) => x.Ordinal.CompareTo(y.Ordinal);

        /// <summary>The alarms of ``keys`` in definition order, each once.</summary>
        private List<AlarmDef> InDefinitionOrder(OrderedSet keys)
        {
            var defs = new List<AlarmDef>(keys.Count);
            foreach (string k in keys.ToList())
                if (_byTag!.TryGetValue(k, out AlarmDef? a))
                    defs.Add(a);
            defs.Sort(ByOrdinal);
            int n = 0;
            for (int i = 0; i < defs.Count; i++)
                if (n == 0 || !ReferenceEquals(defs[n - 1], defs[i]))
                    defs[n++] = defs[i];
            defs.RemoveRange(n, defs.Count - n);
            return defs;
        }

        /// <summary>Install the advanced-alarm evaluator (the VM); those alarms are then evaluated
        /// every scan.</summary>
        public void SetEvaluator(Func<string, AlarmDef, bool?>? fn)
        {
            Evaluator = fn;
            _scanAlways = null;
            NotEvaluated.Clear();
        }

        /// <summary>For tests: forget the cached every-scan list and the input map (after an
        /// alarm definition changed).</summary>
        public void ResetCaches()
        {
            _scanAlways = null;
            _deps = null;
        }

        private List<string> EveryScan()
        {
            if (_scanAlways == null || !ReferenceEquals(_scanAlways.Value.ev, Evaluator))
            {
                var keys = new List<string>();
                foreach (string k in AnalogKinds)
                {
                    Defs(k);
                    foreach (var a in _defOrder[k])
                        if ((a.Limit(R) ?? 0) > 0)
                            keys.Add(a.Tag);
                }
                if (Evaluator != null)
                {
                    Defs(Advanced);
                    foreach (var a in _defOrder[Advanced])
                        if (a.Expr.Length > 0 && !SimpleTag(a).IsSet)
                            keys.Add(a.Tag);
                }
                _scanAlways = (Evaluator, keys);
            }
            return _scanAlways.Value.keys;
        }

        /// <summary>Evaluate one alarm now (tests, a pushed value).</summary>
        public List<Transition> Evaluate(string tag)
        {
            var a = Get(tag);
            return a != null ? Evaluate(a, Clock.Now()) : new List<Transition>();
        }

        /// <summary>A tag's value as the alarm server reads it: kind 0 none (missing / bad),
        /// 1 number, 2 text.</summary>
        private struct TagValue
        {
            public int Kind;
            public double D;
            public string? S;
        }

        private TagValue ValueOf(string name)
        {
            bool ok = Tags.PeekNumber(name, out double v, out bool isNumber, out ulong q, out RawVal raw);
            if (!ok || (!Quality.IsGood(q) && !Quality.IsUncertain(q)))
                return new TagValue { Kind = 0 };
            if (!isNumber)
                return new TagValue { Kind = 2, S = raw.S ?? "" };
            return new TagValue { Kind = 1, D = v };
        }

        private static bool Truth(in TagValue v)
        {
            if (v.Kind == 2)
            {
                string s = v.S ?? "";
                if (FloatText.TryParseNumber(s, out double d))
                    return d != 0;
                return !CitectText.IsBlank(s);
            }
            return v.D != 0 || double.IsNaN(v.D);
        }

        private void Acq(AlarmDef a, bool bad)
        {
            if (bad)
                _acqErrors.Add(a.Tag);
            else
                _acqErrors.Remove(a.Tag);
        }

        private List<Transition> Evaluate(AlarmDef a, double now)
        {
            _states.TryGetValue(a.Tag, out AlarmState? st);
            if (st != null && st.Disabled)
            {
                if (st.DisabledUntil != null && now >= st.DisabledUntil.Value)
                    return EnableCore(a, st, now, "alarm");
                return new List<Transition>();
            }
            if (a.IsAnalog)
                return EvalAnalog(a, st, now);
            var (cond, state) = Condition(a);
            if (cond == null)
                return new List<Transition>();
            if (st == null)
            {
                // a multi-digital alarm's .State is its tag combination OFF too (doc: 1068486
                // 'state 0B0 for Multi-Digital alarm'), so it keeps one when not in state 000
                if (!cond.Value && !(a.Kind == MultiDigital && state != 0))
                    return new List<Transition>();
                st = State(a);
            }
            return Apply(a, st, now, cond.Value, state, null, a.Delay);
        }

        /// <summary>(ON?, .State) of a non-analog alarm; (null, 0) when it cannot be decided.</summary>
        private (bool? cond, int state) Condition(AlarmDef a)
        {
            if (a.Kind == Digital || a.Kind == TsDigital || a.Kind == Timestamped)
            {
                bool anyGiven = false,
                    ok = true;
                foreach (var v in a.Vars)
                {
                    if (!v.IsSet)
                        continue;
                    anyGiven = true;
                    var val = ValueOf(v.Name);
                    if (val.Kind == 0)
                    {
                        Acq(a, true);
                        return (null, 0);
                    }
                    ok = ok && (v.Negated ? !Truth(val) : Truth(val));
                }
                if (!anyGiven)
                    return (null, 0);
                Acq(a, false);
                return (ok, ok ? 1 : 0);
            }
            if (a.Kind == MultiDigital)
            {
                int n = 0;
                for (int bit = 0; bit < a.Vars.Length; bit++)
                {
                    var v = a.Vars[bit];
                    if (!v.IsSet)
                        continue;
                    var val = ValueOf(v.Name);
                    if (val.Kind == 0)
                    {
                        Acq(a, true);
                        return (null, 0);
                    }
                    bool on = v.Negated ? !Truth(val) : Truth(val);
                    if (on)
                        n |= 1 << bit;
                }
                Acq(a, false);
                return (CitectText.Trim(a.States[n].type) == "1", n);
            }
            if (a.Kind == DoublePoint)
            {
                if (a.Vars.Length == 0 || !a.Vars[0].IsSet)
                    return (null, 0);
                var val = ValueOf(a.Vars[0].Name);
                if (val.Kind == 0)
                    return (null, 0);
                int n = 0;
                if (val.Kind == 1)
                {
                    // a NaN or an infinity names no state: undecided, like a value that cannot be read
                    if (double.IsNaN(val.D) || double.IsInfinity(val.D))
                        return (null, 0);
                    n = (int)((long)Math.Truncate(val.D) & 7);
                }
                return (CitectText.Trim(a.States[n].type) == "2", n);
            }
            if (a.Kind == Advanced)
            {
                var simple = SimpleTag(a);
                if (simple.IsSet)
                {
                    var val = ValueOf(simple.Name);
                    if (val.Kind == 0)
                        return (null, 0);
                    bool on = simple.Negated ? !Truth(val) : Truth(val);
                    return (on, on ? 1 : 0);
                }
                if (Evaluator == null)
                {
                    NotEvaluated.Add(a.Tag);
                    return (null, 0);
                }
                bool? res = Evaluator(a.Expr, a);
                if (res == null)
                    return (null, 0);
                return (res.Value, res.Value ? 1 : 0);
            }
            return (null, 0);
        }

        private List<Transition> Apply(
            AlarmDef a,
            AlarmState st,
            double now,
            bool cond,
            int state,
            string? sub,
            double delay,
            double? since = null
        )
        {
            var o = new List<Transition>();
            string key = sub ?? "on";
            if (cond)
            {
                if (st.On && st.State == state && st.Sub == sub)
                {
                    st.Pending.Remove(key);
                    return o;
                }
                if (!st.Pending.TryGetValue(key, out double start))
                {
                    start = since ?? now;
                    st.Pending[key] = start;
                }
                if (delay > 0 && now - start < delay - 1e-9)
                    return o;
                st.Pending.Remove(key);
                bool wasOn = st.On;
                st.On = true;
                st.Ack = false;
                st.InList = true;
                st.State = state;
                st.Sub = sub;
                st.OnTime = delay > 0 ? start : now;
                st.Time = now;
                st.Transitions++;
                Changed(a, st);
                o.Add(Emit(new Transition(now, a.Tag, a.Kind, wasOn ? "state" : "on", StateValue(a), sub)));
                if (!wasOn)
                    DoAction(a, "on");
            }
            else
            {
                st.Pending.Clear();
                int offState = a.Kind == MultiDigital ? state : 0;
                if (!st.On && st.State != offState)
                {
                    st.State = offState;
                    Changed(a, st);
                }
                if (st.On)
                {
                    st.On = false;
                    st.OffTime = now;
                    st.Time = now;
                    st.Transitions++;
                    st.State = offState;
                    st.Sub = null;
                    st.InList = !st.Ack;
                    Changed(a, st);
                    o.Add(Emit(new Transition(now, a.Tag, a.Kind, "off", StateValue(a))));
                    DoAction(a, "off");
                }
            }
            return o;
        }

        private List<Transition> EvalAnalog(AlarmDef a, AlarmState? st, double now)
        {
            string name = a.Vars.Length > 0 && a.Vars[0].IsSet ? a.Vars[0].Name : "";
            if (name.Length == 0)
                return new List<Transition>();
            var val = ValueOf(name);
            if (val.Kind != 1)
            {
                Acq(a, true);
                return new List<Transition>();
            }
            Acq(a, false);
            double v = val.D;
            double db = a.Deadband;
            string? cur = st?.Sub;
            var conds = new Dictionary<string, bool>(StringComparer.Ordinal);

            bool High(double? limit, bool active) =>
                limit != null && (active ? v > limit.Value - db : v > limit.Value);
            bool Low(double? limit, bool active) =>
                limit != null && (active ? v < limit.Value + db : v < limit.Value);

            conds[HH] = High(a.Limit(HH), cur == HH);
            conds[H] = High(a.Limit(H), cur == H || cur == HH);
            conds[LL] = Low(a.Limit(LL), cur == LL);
            conds[L] = Low(a.Limit(L), cur == L || cur == LL);
            double dev = a.Limit("DEV") ?? 0.0;
            if (dev > 0)
            {
                double? sp = F(a.Setpoint);
                if (sp == null && a.Setpoint.Length > 0)
                {
                    var spv = ValueOf(a.Setpoint);
                    sp = spv.Kind == 1 ? spv.D : (double?)null;
                }
                double spd = sp ?? 0.0;
                conds[DVH] = High(spd + dev, cur == DVH);
                conds[DVL] = Low(spd - dev, cur == DVL);
            }
            double rate = a.Limit(R) ?? 0.0;
            if (st == null)
            {
                bool any = false;
                foreach (bool c in conds.Values)
                    if (c)
                    {
                        any = true;
                        break;
                    }
                if (!any && rate <= 0)
                    return new List<Transition>();
                st = State(a);
            }
            st.Value = v;
            if (rate > 0 && st.LastT != null && now > st.LastT.Value)
            {
                double perS = Math.Abs(v - st.LastValue!.Value) / (now - st.LastT.Value);
                conds[R] = perS > rate / (Period != 0 ? Period : PeriodDefault);
            }
            else if (rate > 0)
                conds[R] = cur == R;
            st.LastValue = v;
            st.LastT = now;
            // each condition's start time; the highest-priority one past its delay wins
            foreach (string sub in AnalogOrder)
            {
                if (conds.TryGetValue(sub, out bool c) && c)
                {
                    if (!st.Pending.ContainsKey(sub))
                        st.Pending[sub] = now;
                }
                else
                    st.Pending.Remove(sub);
            }
            string? chosen = null;
            foreach (string sub in AnalogOrder)
            {
                if (!(conds.TryGetValue(sub, out bool c) && c))
                    continue;
                double since0 = st.Pending[sub];
                // falling back from High High to High (Low Low to Low) needs no new delay: the alarm
                // has been active all along
                if (
                    sub == cur
                    || (st.On && ((sub == H && cur == HH) || (sub == L && cur == LL)))
                    || now - since0 >= a.DelayOf(sub) - 1e-9
                )
                {
                    chosen = sub;
                    break;
                }
            }
            if (chosen == null)
            {
                bool curCond = cur != null && conds.TryGetValue(cur, out bool cc) && cc;
                if (st.On && !curCond)
                {
                    var pend0 = new Dictionary<string, double>(st.Pending, StringComparer.Ordinal);
                    var o0 = Apply(a, st, now, false, 0, null, 0.0);
                    st.Pending = pend0;
                    return o0;
                }
                return new List<Transition>();
            }
            if (chosen == cur && st.On)
                return new List<Transition>();
            double since = st.Pending.TryGetValue(chosen, out double sv) ? sv : now;
            double delay = a.DelayOf(chosen);
            var pend = new Dictionary<string, double>(st.Pending, StringComparer.Ordinal);
            var o = Apply(a, st, now, true, AnalogState[chosen], chosen, 0.0);
            pend.Remove(chosen);
            st.Pending = pend;
            if (o.Count > 0 && delay > 0)
                st.OnTime = since;
            return o;
        }

        private Transition Emit(Transition tr)
        {
            foreach (var fn in Listeners.ToArray())
                fn(tr);
            return tr;
        }

        private void DoAction(AlarmDef a, string ev)
        {
            if (ActionHook == null)
                return;
            var texts = new List<string>();
            if (a.Kind == MultiDigital)
                texts.Add(
                    ev == "on" ? a.OnFunc
                    : ev == "off" ? a.OffFunc
                    : ""
                );
            string field =
                ev == "on" ? "ACTION"
                : ev == "off" ? "OFF_ACTION"
                : "ACK_ACTION";
            texts.Add(Categories().TryGetValue(a.Category, out DbfRow? cat) ? cat.Get(field) : "");
            foreach (string t in texts)
                if (t.Length > 0 && !CitectText.IsBlank(t))
                    ActionHook(a, ev, CitectText.Trim(t));
        }

        // ------------------------------------------------------------------ operator actions
        /// <summary>Acknowledge one alarm (AlarmAckTag): 0, or 424 when there is no such alarm.</summary>
        public int Acknowledge(string tag, string by = "user")
        {
            var a = Get(tag);
            if (a == null)
                return 424;
            _states.TryGetValue(a.Tag, out AlarmState? st);
            double now = Clock.Now();
            if (st == null || !st.InList || st.Ack)
                return 0;
            st.Ack = true;
            st.AckTime = now;
            if (!st.On)
                st.InList = false;
            Changed(a, st);
            Emit(new Transition(now, a.Tag, a.Kind, "ack", StateValue(a), st.Sub, by));
            DoAction(a, "ack");
            return 0;
        }

        /// <summary>Acknowledge every unacknowledged alarm matching the filter; the count.</summary>
        public int AcknowledgeAll(string filterText = "", string by = "user")
        {
            var f = Filter(filterText);
            Index();
            int n = 0;
            foreach (var a in InDefinitionOrder(_listed))
            {
                if (!_states.TryGetValue(a.Tag, out AlarmState? st))
                    continue;
                if (!st.Ack && Visible(a) && f.Match(Getter(a, st)))
                {
                    Acknowledge(a.Tag, by);
                    n++;
                }
            }
            return n;
        }

        /// <summary>Disable an alarm (AlarmDisableTag); until = simulated seconds or null.</summary>
        public int Disable(string tag, double? until = null, string comment = "", string by = "user")
        {
            var a = Get(tag);
            if (a == null)
                return 424;
            var st = State(a);
            double now = Clock.Now();
            st.Disabled = true;
            st.DisabledUntil = until;
            st.Comment = comment;
            st.On = false;
            st.InList = false;
            st.Pending = new Dictionary<string, double>(StringComparer.Ordinal);
            st.State = 0;
            st.Sub = null;
            Changed(a, st);
            Emit(new Transition(now, a.Tag, a.Kind, "disable", StateValue(a), null, by));
            return 0;
        }

        public int Enable(string tag, string by = "user")
        {
            var a = Get(tag);
            if (a == null)
                return 424;
            EnableCore(a, State(a), Clock.Now(), by);
            return 0;
        }

        private List<Transition> EnableCore(AlarmDef a, AlarmState st, double now, string by)
        {
            st.Disabled = false;
            st.DisabledUntil = null;
            Changed(a, st);
            var tr = Emit(new Transition(now, a.Tag, a.Kind, "enable", StateValue(a), null, by));
            _dirty.Add(a.Tag);
            return new List<Transition> { tr };
        }

        /// <summary>Set an alarm's state directly.</summary>
        public int SetState(string tag, AlarmSet s, string by = "scenario")
        {
            var a = Get(tag);
            if (a == null)
                return 424;
            var st = State(a);
            double now = Clock.Now();
            bool wasOn = st.On,
                wasAck = st.Ack,
                wasDisabled = st.Disabled;
            int wasState = st.State;
            int? en = s.State != null ? (int)(s.State.Value & 31) : (int?)null;
            bool on = s.On ?? (en != null ? StateIsOn(a, en.Value) : st.On);
            bool ack = s.Ack
                ?? (
                    s.State != null ? (s.State.Value & StateUnack) == 0
                    : on && !wasOn ? false
                    : st.Ack
                );
            bool disabled = s.Disabled
                ?? (s.State != null ? (s.State.Value & StateDisabled) != 0 : st.Disabled);
            // enabled again with nothing else said: the scan evaluates it from its tags, as
            // AlarmEnableTag does (EnableCore)
            bool reevaluate = wasDisabled && !disabled && s.On == null && s.State == null;
            int state;
            if (en != null)
                state = en.Value;
            else if (disabled)
                state = 0; // as Disable leaves it
            else if (on)
                state =
                    wasOn ? st.State
                    : a.IsAnalog ? AnalogState[H]
                    : 1;
            else
                state = a.Kind == MultiDigital ? st.State : 0;
            st.On = on && !disabled;
            st.Ack = ack;
            st.Disabled = disabled;
            st.DisabledUntil = null;
            st.State = state;
            st.Sub = null;
            if (a.IsAnalog && st.On)
                foreach (var kv in AnalogState)
                    if (kv.Value == state)
                        st.Sub = kv.Key;
            st.InList = !disabled && (st.On || !ack);
            st.Pending = new Dictionary<string, double>(StringComparer.Ordinal);
            bool changed = st.On != wasOn || ack != wasAck || disabled != wasDisabled || state != wasState;
            if (s.OnTime != null)
                st.OnTime = s.OnTime.Value;
            else if (st.On && !wasOn)
                st.OnTime = now;
            if (!st.On && (wasOn || s.Time != null))
                st.OffTime = s.Time ?? now;
            if (ack && !wasAck)
                st.AckTime = now;
            if (s.Time != null)
                st.Time = s.Time.Value;
            else if (changed)
                st.Time = now;
            // a set alarm has a state change behind it: its times show (PropertyValue)
            if (
                changed
                || st.Transitions == 0
                    && (st.On || st.InList || disabled || s.OnTime != null || s.Time != null)
            )
                st.Transitions++;
            if (reevaluate)
                _dirty.Add(a.Tag);
            else
                _dirty.Remove(a.Tag);
            Changed(a, st);
            Emit(new Transition(now, a.Tag, a.Kind, "set", StateValue(a), st.Sub, by));
            return 0;
        }

        /// <summary>Does .State enumeration <paramref name="n"/> mean ON: a multi-digital
        /// alarm's ALMSTATn type 1, a double point alarm's STATETYPEn type 2 (as the scan reads
        /// them), else a nonzero enumeration.</summary>
        private static bool StateIsOn(AlarmDef a, int n)
        {
            if (a.Kind == MultiDigital && n >= 0 && n < a.States.Length)
                return CitectText.Trim(a.States[n].type) == "1";
            if (a.Kind == DoublePoint && n >= 0 && n < a.States.Length)
                return CitectText.Trim(a.States[n].type) == "2";
            return n != 0;
        }

        /// <summary>A hardware alarm (the VM's hardware errors, a driver error): ON
        /// unacknowledged; the same code and name again counts up.</summary>
        public HardwareAlarm RaiseHardware(
            long code,
            string desc = "",
            string name = "Cicode",
            string page = ""
        )
        {
            double now = Clock.Now();
            foreach (var hw0 in HardwareAlarms)
            {
                if (hw0.Code == code && hw0.Name == name && hw0.St.InList)
                {
                    hw0.Count++;
                    hw0.St.Time = now;
                    return hw0;
                }
            }
            string msg = HardwareMessages().TryGetValue(code, out DbfRow? row) ? row.Get("DESC") : "";
            string d =
                desc.Length > 0 ? desc
                : msg.Length > 0 ? msg
                : "Hardware error " + code.ToString(CultureInfo.InvariantCulture);
            var hw = new HardwareAlarm(code, d, name, page);
            hw.St.On = true;
            hw.St.Ack = false;
            hw.St.InList = true;
            hw.St.OnTime = now;
            hw.St.Time = now;
            hw.St.State = 1;
            HardwareAlarms.Add(hw);
            Generation++;
            _countCache.Clear();
            foreach (var fn in Listeners.ToArray())
                fn(
                    new Transition(
                        now,
                        "hardware:" + code.ToString(CultureInfo.InvariantCulture),
                        Hardware,
                        "on",
                        1 + StateUnack
                    )
                );
            return hw;
        }

        /// <summary>Acknowledge every raised hardware alarm (they stay listed while ON, guess).</summary>
        public int AcknowledgeHardware()
        {
            int n = 0;
            foreach (var hw in HardwareAlarms)
            {
                if (hw.St.InList && !hw.St.Ack)
                {
                    hw.St.Ack = true;
                    hw.St.AckTime = Clock.Now();
                    n++;
                }
            }
            Generation++;
            _countCache.Clear();
            return n;
        }

        // ------------------------------------------------------------------ counts
        private Func<string, AlarmFilter.FieldValue> Getter(AlarmDef a, AlarmState? st)
        {
            return fld =>
            {
                string f = CitectText.Upper(fld);
                switch (f)
                {
                    case "TAG":
                        return AlarmFilter.FieldValue.Str(a.Tag);
                    case "NAME":
                        return AlarmFilter.FieldValue.Str(a.Name);
                    case "DESC":
                        return AlarmFilter.FieldValue.Str(a.Desc);
                    case "CATEGORY":
                        return AlarmFilter.FieldValue.Num(a.Category);
                    case "PRIORITY":
                        return AlarmFilter.FieldValue.Num(Priority(a));
                    case "AREA":
                        return AlarmFilter.FieldValue.Num(a.Area);
                    case "PRIV":
                        return AlarmFilter.FieldValue.Num(a.Priv);
                    case "CLUSTER":
                        return AlarmFilter.FieldValue.Str(a.Cluster);
                    case "EQUIPMENT":
                    case "EQUIP":
                        return AlarmFilter.FieldValue.Str(a.Equip);
                    case "ITEM":
                        return AlarmFilter.FieldValue.Str(a.Item);
                    case "COMMENT":
                        return AlarmFilter.FieldValue.Str(a.Comment);
                    case "HELP":
                        return AlarmFilter.FieldValue.Str(a.Help);
                }
                if (
                    f.StartsWith("CUSTOM", StringComparison.Ordinal)
                    && f.Length > 6
                    && CitectText.IsDigits(f.Substring(6))
                )
                {
                    var n = CitectText.ParseInt(f.Substring(6));
                    if (n != null && n.Value >= 1 && n.Value <= 8)
                        return AlarmFilter.FieldValue.Str(a.Custom[(int)n.Value - 1]);
                }
                if (f == "TYPE")
                    return AlarmFilter.FieldValue.Num(SumTypes.TryGetValue(a.Kind, out int t) ? t : -1);
                if (f == "PAGE")
                {
                    var e = Equipment != null && a.Equip.Length > 0 ? Equipment.Get(a.Equip) : null;
                    return AlarmFilter.FieldValue.Str(e != null ? e.Get("PAGE") : "");
                }
                if (f == "STATE")
                    return AlarmFilter.FieldValue.Num(st != null ? StateValue(a) : 0);
                return AlarmFilter.FieldValue.None;
            };
        }

        private bool Visible(AlarmDef a) => Users == null || Users.CanView(Users.ClampInt(a.Area));

        /// <summary>Does an alarm in state ``st`` count for AlarmCount type ``kind``?</summary>
        internal static bool Classify(AlarmState? st, int kind)
        {
            if (st == null)
                return kind == 4 || kind == 9 || kind == 12;
            if (kind == 4 || kind == 9)
                return true;
            if (st.Disabled)
                return kind == 3 || kind == 8;
            bool listed = st.InList,
                on = st.On;
            bool unack = listed && !st.Ack;
            bool offUnack = listed && !on && !st.Ack;
            switch (kind)
            {
                case 0:
                case 5:
                    return listed;
                case 1:
                case 6:
                    return unack;
                case 2:
                case 7:
                    return on && st.Ack;
                case 11:
                case 13:
                    return on;
                case 12:
                    return !on;
                case 14:
                    return offUnack;
                case 17:
                    return on && unack;
                case 18:
                    return offUnack;
            }
            return false;
        }

        private IEnumerable<(AlarmDef a, AlarmState? st)> Candidates(int kind)
        {
            if (kind == 4 || kind == 9 || kind == 12)
            {
                foreach (var a in AllIter())
                {
                    _states.TryGetValue(a.Tag, out AlarmState? st);
                    yield return (a, st);
                }
                yield break;
            }
            Index();
            var keys = kind == 3 || kind == 8 ? _disabled : _listed;
            foreach (string k in keys.ToList())
            {
                if (_byTag!.TryGetValue(k, out AlarmDef? a))
                {
                    _states.TryGetValue(k, out AlarmState? st);
                    yield return (a, st);
                }
            }
        }

        private long SessionKey() => Users?.Serial ?? -1;

        /// <summary>AlarmCount(kind, filter) / AlarmCountEquipment (doc: 1033455, 1033456).
        /// Hardware kinds 5..9, 13, 14 count raised hardware alarms. Results are kept until an
        /// alarm changes state or the user logs in or out.</summary>
        public int Count(int kind = 1, string filterText = "", string equipment = "", bool includeRef = false)
        {
            if (kind >= 5 && kind <= 9 || kind == 13 || kind == 14)
            {
                int hn = 0;
                foreach (var hw in HardwareAlarms)
                    if (Classify(hw.St, kind))
                        hn++;
                return hn;
            }
            var key = (kind, filterText ?? "", equipment ?? "", includeRef, SessionKey());
            if (_countCache.TryGetValue(key, out int hit))
                return hit;
            var f = Filter(filterText ?? "");
            var eq = !string.IsNullOrEmpty(equipment) ? EquipmentFilter(equipment!, includeRef) : null;
            int n = 0;
            foreach (var (a, st) in Candidates(kind))
            {
                if (eq != null && !eq(a))
                    continue;
                if (!Classify(st, kind) || !Visible(a))
                    continue;
                if (!f.Empty && !f.Match(Getter(a, st)))
                    continue;
                n++;
            }
            if (_countCache.Count > 4096)
                _countCache.Clear();
            _countCache[key] = n;
            return n;
        }

        /// <summary>AlarmCountEquipment's equipment filter.</summary>
        private Func<AlarmDef, bool> EquipmentFilter(string text, bool includeRef)
        {
            var equips = new List<string>();
            var pages = new List<string>();
            var refcats = new List<string>();
            foreach (string raw in text.Split(','))
            {
                string part = CitectText.Trim(raw);
                if (part.Length == 0)
                    continue;
                int eqp = part.IndexOf('=');
                string k = CitectText.Upper(CitectText.Trim(eqp >= 0 ? part.Substring(0, eqp) : part));
                string v = eqp >= 0 ? part.Substring(eqp + 1) : "";
                if (eqp < 0)
                    equips.Add(part + "*");
                else if (k == "EQUIPMENT")
                    equips.Add(CitectText.Trim(v));
                else if (k == "PAGE")
                    pages.Add(CitectText.Lower(CitectText.Trim(v)));
                else if (k == "REFCAT")
                    refcats.Add(CitectText.Lower(CitectText.Trim(v)));
            }

            bool EqMatch(string name)
            {
                string low = CitectText.Lower(name);
                foreach (string pat in equips)
                {
                    string p = CitectText.Lower(pat);
                    if (p.EndsWith("*", StringComparison.Ordinal))
                    {
                        if (low.StartsWith(p.Substring(0, p.Length - 1), StringComparison.Ordinal))
                            return true;
                    }
                    else if (low == p)
                        return true;
                }
                return false;
            }

            HashSet<string>? expanded = null;
            if (includeRef && equips.Count > 0)
            {
                expanded = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (_, r) in Unit.Rows("eqref.dbf"))
                {
                    if (!EqMatch(r.Get("EQUIP")))
                        continue;
                    string cat = CitectText.Lower(r.Get("REFCAT"));
                    if (refcats.Count > 0 && !refcats.Exists(c => Wild(cat, c)))
                        continue;
                    expanded.Add(CitectText.Lower(r.Get("REFEQUIP")));
                }
            }

            return a =>
            {
                if (a.Equip.Length == 0)
                    return false;
                if (pages.Count > 0)
                {
                    var e = Equipment?.Get(a.Equip);
                    return e != null && pages.Contains(CitectText.Lower(e.Get("PAGE")));
                }
                // a name may carry its cluster ('Cluster1.Company.*': SA_Include
                // Equipment_GetContext, the equipment tree's alarm counts)
                if (
                    equips.Count > 0
                    && !(
                        EqMatch(a.Equip)
                        || (a.Cluster.Length > 0 && EqMatch(a.Cluster + "." + a.Equip))
                        || (expanded != null && expanded.Contains(CitectText.Lower(a.Equip)))
                    )
                )
                    return false;
                return true;
            };
        }

        /// <summary>AlarmActive(mode) (doc: 1033448): 0 / 1 / 3 to 1 or 0, 2 the highest priority
        /// of the unacknowledged alarms, 5 / 6 hardware counts.</summary>
        public long Active(int mode = 0)
        {
            if (mode == 5 || mode == 6)
                return Count(mode);
            if (mode == 2)
                return HighestPriority(1);
            int kind;
            if (mode == 0)
                kind = 0;
            else if (mode == 1)
                kind = 1;
            else if (mode == 3)
                kind = 3;
            else
                return 0;
            foreach (var (a, st) in Candidates(kind))
                if (Classify(st, kind) && Visible(a))
                    return 1;
            return 0;
        }

        /// <summary>AlarmHighestPriority: the lowest non-zero priority among matching alarms, or 0.</summary>
        public long HighestPriority(
            int kind = 0,
            string filterText = "",
            string equipment = "",
            bool includeRef = false
        )
        {
            var f = Filter(filterText ?? "");
            var eq = !string.IsNullOrEmpty(equipment) ? EquipmentFilter(equipment!, includeRef) : null;
            long best = 0;
            foreach (var (a, st) in Candidates(kind))
            {
                if (!Classify(st, kind) || !Visible(a))
                    continue;
                if (eq != null && !eq(a))
                    continue;
                if (!f.Empty && !f.Match(Getter(a, st)))
                    continue;
                long p = Priority(a);
                if (p != 0 && (best == 0 || p < best))
                    best = p;
            }
            return best;
        }

        /// <summary>AlarmHighestPriority's priority and state.</summary>
        public (long priority, int state) Highest(
            string filterText = "",
            string equipment = "",
            bool includeRef = false,
            bool indicatorOnly = false
        )
        {
            var f = Filter(filterText ?? "");
            var eq = !string.IsNullOrEmpty(equipment) ? EquipmentFilter(equipment!, includeRef) : null;
            long best = 0;
            int state = 0;
            foreach (int kind in new[] { 0, 3 })
            {
                foreach (var (a, st) in Candidates(kind))
                {
                    if (st == null || !Classify(st, kind) || !Visible(a))
                        continue;
                    if (eq != null && !eq(a))
                        continue;
                    if (!f.Empty && !f.Match(Getter(a, st)))
                        continue;
                    long p = Priority(a);
                    if (p == 0)
                        continue;
                    if (indicatorOnly && !ShownOnIndicator(p))
                        continue;
                    int s =
                        kind == 3 ? 4
                        : st.On && !st.Ack ? 1
                        : !st.Ack ? 2
                        : 3;
                    if (best == 0 || p < best)
                    {
                        best = p;
                        state = s;
                    }
                    else if (p == best && s < state)
                        state = s;
                }
                if (best != 0)
                    break;
            }
            return (best, state);
        }

        /// <summary>A category's category.dbf SHOW_ALM ('Show on Active', help 1068445): FALSE (or
        /// 0) keeps its alarms off active alarm lists; empty, TRUE or no row shows them.</summary>
        public bool ShownOnActive(long category) => CategoryFlag(category, "SHOW_ALM");

        /// <summary>A category's SHOW_SUM ('Show on Summary'): FALSE (or 0) keeps its alarms off
        /// summary lists and, help 1180291, the sequence of events.</summary>
        public bool ShownOnSummary(long category) => CategoryFlag(category, "SHOW_SUM");

        private bool CategoryFlag(long category, string field)
        {
            if (!Categories().TryGetValue(category, out DbfRow? r))
                return true;
            string v = CitectText.Upper(CitectText.Trim(r.Get(field)));
            return !(v == "FALSE" || v == "0");
        }

        /// <summary>A priority's almprior.dbf SHOW_AI: FALSE (or 0) hides it from alarm
        /// indicators; empty, TRUE or no row shows it.</summary>
        public bool ShownOnIndicator(long priority)
        {
            if (!Priorities().TryGetValue(priority, out DbfRow? r))
                return true;
            string v = CitectText.Upper(CitectText.Trim(r.Get("SHOW_AI")));
            return !(v == "FALSE" || v == "0");
        }

        private List<KeyValuePair<T, int>> Grouped<T>(int kind, Func<AlarmDef, T> key, Func<T, bool> keep)
            where T : notnull
        {
            var counts = new Dictionary<T, int>();
            var order = new List<T>();
            foreach (var (a, st) in Candidates(kind))
            {
                if (!Classify(st, kind) || !Visible(a))
                    continue;
                T k = key(a);
                if (!keep(k))
                    continue;
                if (!counts.ContainsKey(k))
                {
                    counts[k] = 0;
                    order.Add(k);
                }
                counts[k]++;
            }
            return order.ConvertAll(k => new KeyValuePair<T, int>(k, counts[k]));
        }

        public List<KeyValuePair<long, int>> ByCategory(int kind = 0) =>
            Grouped(kind, a => a.Category, _ => true);

        public List<KeyValuePair<long, int>> ByPriority(int kind = 0) => Grouped(kind, Priority, _ => true);

        public List<KeyValuePair<string, int>> ByEquipment(int kind = 0) =>
            Grouped(kind, a => a.Equip, s => s.Length > 0);

        /// <summary>The alarm list (ON, or OFF unacknowledged), highest priority first, newest
        /// first within a priority (doc: 1068445 display order).</summary>
        public List<(AlarmDef a, AlarmState st)> ActiveList()
        {
            var rows = new List<(AlarmDef a, AlarmState st, long p, int i)>();
            int i = 0;
            foreach (var (a, st) in Candidates(0))
                if (st != null && st.InList && Visible(a))
                    rows.Add((a, st, Priority(a), i++));
            rows.Sort(
                (x, y) =>
                {
                    int c = x.p.CompareTo(y.p);
                    if (c != 0)
                        return c;
                    c = (-x.st.Time).CompareTo(-y.st.Time);
                    return c != 0 ? c : x.i.CompareTo(y.i);
                }
            );
            return rows.ConvertAll(r => (r.a, r.st));
        }

        /// <summary>The alarms a list of type ``kind`` shows, in order.</summary>
        public List<(AlarmDef a, AlarmState? st)> ListOf(
            int kind,
            string filterText = "",
            Func<AlarmDef, AlarmState?, bool>? keep = null
        )
        {
            var f = Filter(filterText ?? "");
            var rows = new List<(AlarmDef a, AlarmState? st, long p, int i)>();
            int i = 0;
            foreach (var (a, st) in Candidates(kind))
            {
                if (!Classify(st, kind) || !Visible(a))
                    continue;
                if (!f.Empty && !f.Match(Getter(a, st)))
                    continue;
                if (keep != null && !keep(a, st))
                    continue;
                rows.Add((a, st, Priority(a), i++));
            }
            bool active = kind == 0 || kind == 1 || kind == 2 || kind == 11 || kind == 17 || kind == 18;
            rows.Sort(
                (x, y) =>
                {
                    int c = active ? x.p.CompareTo(y.p) : 0;
                    if (c != 0)
                        return c;
                    double tx = x.st?.Time ?? double.NegativeInfinity,
                        ty = y.st?.Time ?? double.NegativeInfinity;
                    c = ty.CompareTo(tx);
                    return c != 0 ? c : x.a.Ordinal.CompareTo(y.a.Ordinal);
                }
            );
            return rows.ConvertAll(r => (r.a, r.st));
        }

        /// <summary>The .State property: the state enumeration + the action masks (doc: 1068486).</summary>
        public long StateValue(AlarmDef a)
        {
            if (!_states.TryGetValue(a.Tag, out AlarmState? st))
                return 0;
            long v = st.State;
            if (st.InList && !st.Ack)
                v |= StateUnack;
            if (st.Disabled)
                v |= StateDisabled;
            return v;
        }

        public bool AcqError(AlarmDef a) => _acqErrors.Contains(a.Tag);

        // ------------------------------------------------------------------ properties as tags (doc: 1068485 .. 1068487)
        private static readonly Dictionary<string, int> Props = BuildProps();

        private static Dictionary<string, int> BuildProps()
        {
            var p = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["on"] = Types.Digital,
                ["ack"] = Types.Digital,
                ["acqerr"] = Types.Int16,
                ["category"] = Types.Int16,
                ["combreak"] = Types.Digital,
                ["disabled"] = Types.Digital,
                ["historian"] = Types.Digital,
                ["item"] = Types.String,
                ["millisec"] = Types.Long,
                ["name"] = Types.String,
                ["ontime"] = Types.Long,
                ["paging"] = Types.Digital,
                ["paginggroup"] = Types.String,
                ["priority"] = Types.Int16,
                ["state"] = Types.Int16,
                ["tag"] = Types.String,
                ["time"] = Types.Long,
                ["desc"] = Types.String,
                ["delay"] = Types.Long,
                ["devdelay"] = Types.Long,
                ["deadband"] = Types.Real,
                ["deviation"] = Types.Real,
                ["highhigh"] = Types.Real,
                ["high"] = Types.Real,
                ["lowlow"] = Types.Real,
                ["low"] = Types.Real,
                ["hhdelay"] = Types.Long,
                ["hdelay"] = Types.Long,
                ["ldelay"] = Types.Long,
                ["lldelay"] = Types.Long,
                ["rate"] = Types.Real,
                ["setpoint"] = Types.Real,
                ["value"] = Types.Real,
                ["dvl"] = Types.Digital,
                ["dvh"] = Types.Digital,
                ["r"] = Types.Digital,
                ["l"] = Types.Digital,
                ["h"] = Types.Digital,
                ["ll"] = Types.Digital,
                ["hh"] = Types.Digital,
            };
            for (int i = 1; i <= 8; i++)
                p["custom" + i.ToString(CultureInfo.InvariantCulture)] = Types.String;
            return p;
        }

        /// <summary>The spelling of the help (doc: 1068486), for names in traces and the 'used' list.</summary>
        public static readonly Dictionary<string, string> PropertyNames = BuildPropertyNames();

        private static Dictionary<string, string> BuildPropertyNames()
        {
            var n = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string k in Props.Keys)
                n[k] = char.ToUpperInvariant(k[0]) + k.Substring(1);
            n["acqerr"] = "AcqErr";
            n["combreak"] = "ComBreak";
            n["ontime"] = "OnTime";
            n["paginggroup"] = "PagingGroup";
            n["devdelay"] = "DevDelay";
            n["deadband"] = "DeadBand";
            n["highhigh"] = "HighHigh";
            n["lowlow"] = "LowLow";
            n["hhdelay"] = "HHDelay";
            n["hdelay"] = "HDelay";
            n["ldelay"] = "LDelay";
            n["lldelay"] = "LLDelay";
            n["dvl"] = "DVL";
            n["dvh"] = "DVH";
            n["r"] = "R";
            n["l"] = "L";
            n["h"] = "H";
            n["ll"] = "LL";
            n["hh"] = "HH";
            return n;
        }

        private static readonly HashSet<string> Writable = BuildWritable();

        private static HashSet<string> BuildWritable()
        {
            var w = new HashSet<string>(StringComparer.Ordinal)
            {
                "ack",
                "category",
                "deadband",
                "delay",
                "deviation",
                "disabled",
                "highhigh",
                "high",
                "lowlow",
                "low",
                "hhdelay",
                "hdelay",
                "ldelay",
                "lldelay",
                "devdelay",
                "paging",
                "paginggroup",
                "rate",
            };
            for (int i = 1; i <= 8; i++)
                w.Add("custom" + i.ToString(CultureInfo.InvariantCulture));
            return w;
        }

        private static readonly Dictionary<string, string> LimitKeys = new Dictionary<string, string>(
            StringComparer.Ordinal
        )
        {
            ["highhigh"] = HH,
            ["high"] = H,
            ["low"] = L,
            ["lowlow"] = LL,
            ["deviation"] = "DEV",
            ["rate"] = R,
        };
        private static readonly Dictionary<string, string> DelayKeys = new Dictionary<string, string>(
            StringComparer.Ordinal
        )
        {
            ["hhdelay"] = HH,
            ["hdelay"] = H,
            ["ldelay"] = L,
            ["lldelay"] = LL,
        };

        public static bool IsProperty(string prop) => Props.ContainsKey(prop);

        /// <summary>'Alarm.Prop[.ExtItem]' / 'Equipment.Item.Prop' to (alarm, prop, item) or null.</summary>
        public (AlarmDef alarm, string prop, string item)? ResolveProperty(
            string? name,
            string element = "",
            string item = "",
            string cluster = ""
        )
        {
            var parts = new List<string>((name ?? "").Split('.'));
            string ext = item;
            if (ext.Length == 0 && parts.Count > 2)
            {
                string last = CitectText.Lower(parts[parts.Count - 1]);
                if (last == "v" || last == "vt" || last == "q" || last == "qt" || last == "t")
                {
                    ext = last;
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            if (parts.Count < 2)
                return null;
            string prop = CitectText.Lower(parts[parts.Count - 1]);
            if (!Props.ContainsKey(prop))
                return null;
            var a = Get(string.Join(".", parts.GetRange(0, parts.Count - 1)));
            if (a == null)
                return null;
            if (cluster.Length > 0 && a.Cluster.Length > 0 && !CitectText.SameName(a.Cluster, cluster))
                return null;
            return (a, prop, ext);
        }

        /// <summary>A property read through a TAGREF; integer kinds push as LONG.</summary>
        public Value PropertyValue(AlarmDef a, string prop, string item = "")
        {
            _states.TryGetValue(a.Tag, out AlarmState? st0);
            AlarmState st = st0 ?? new AlarmState();
            int t = Props.TryGetValue(prop, out int pt) ? pt : Types.String;
            if (t == Types.Digital || t == Types.Int16)
                t = Types.Long;
            long iv = 0;
            double dv = 0;
            string sv = "";
            switch (prop)
            {
                case "on":
                    iv = st.On ? 1 : 0;
                    break;
                case "ack":
                    iv = st.Ack ? 1 : 0;
                    break;
                case "acqerr":
                    iv = _acqErrors.Contains(a.Tag) ? 1 : 0;
                    break;
                case "category":
                    iv = a.Category;
                    break;
                case "combreak":
                    iv = 0;
                    break;
                case "disabled":
                    iv = st.Disabled ? 1 : 0;
                    break;
                case "historian":
                {
                    string h = CitectText.Upper(CitectText.Trim(a.Historian));
                    iv = h == "TRUE" || h == "1" ? 1 : 0;
                    break;
                }
                case "item":
                    sv = a.Item;
                    break;
                case "millisec":
                    iv = st.Transitions != 0 ? MsPart(Ft(st.OnTime)) : 0;
                    break;
                case "name":
                    sv = a.Name;
                    break;
                case "ontime":
                    iv = st.Transitions != 0 ? UnixPart(Ft(st.OnTime)) : 0;
                    break;
                case "time":
                    iv = st.Transitions != 0 ? UnixPart(Ft(st.Time)) : 0;
                    break;
                case "paging":
                    iv = a.Paging ? 1 : 0;
                    break;
                case "paginggroup":
                    sv = a.PagingGroup;
                    break;
                case "priority":
                    iv = Priority(a);
                    break;
                case "state":
                    iv = StateValue(a);
                    break;
                case "tag":
                    sv = a.Tag;
                    break;
                case "desc":
                    sv = a.Desc;
                    break;
                case "delay":
                    iv = (long)Math.Truncate(a.Delay);
                    break;
                case "devdelay":
                    iv = (long)Math.Truncate(a.DelayOf(DVH));
                    break;
                case "deadband":
                    dv = a.Deadband == 0 ? 0.0 : a.Deadband;
                    break;
                case "setpoint":
                {
                    double? sp = F(a.Setpoint);
                    dv = sp == null || sp.Value == 0 ? 0.0 : sp.Value;
                    break;
                }
                case "value":
                    dv = st.Value ?? 0.0;
                    break;
                case "dvl":
                case "dvh":
                case "r":
                case "l":
                case "h":
                case "ll":
                case "hh":
                    iv = st.On && CitectText.Lower(st.Sub ?? "") == prop ? 1 : 0;
                    break;
                default:
                    if (prop.StartsWith("custom", StringComparison.Ordinal) && Props.ContainsKey(prop))
                        sv = a.Custom[prop[6] - '1'];
                    else if (DelayKeys.TryGetValue(prop, out string? dk))
                        iv = (long)Math.Truncate(a.DelayOf(dk));
                    else if (LimitKeys.TryGetValue(prop, out string? lk))
                    {
                        double? x = a.Limit(lk);
                        double def =
                            lk == HH || lk == H ? 3.4e38
                            : lk == L || lk == LL ? -3.4e38
                            : 0.0;
                        dv = x ?? def;
                    }
                    break;
            }
            // an alarm that changed at t = 0 (a scenario's first scan) has a time too
            ulong ts = st.Transitions != 0 ? Ft(st.Time) : 0;
            Value val =
                t == Types.Real ? Value.Real(dv, Quality.QualityGood, ts, ts, 0)
                : t == Types.String ? Value.Str(sv, Quality.QualityGood, ts, ts, 0)
                : Value.Int(t, iv, Quality.QualityGood, ts, ts, 0);
            switch (item)
            {
                case "v":
                    return val.Bare(Value.QualityGood);
                case "q":
                    return WorldValues.Quality(Quality.QualityGood);
                case "t":
                case "vt":
                    return WorldValues.Timestamp(ts);
                case "qt":
                    return WorldValues.Timestamp(0);
            }
            return val;
        }

        private ulong Ft(double t) => Clock.FiletimeAt(t);

        /// <summary>Whole Unix seconds of a FILETIME.</summary>
        internal static long UnixPart(ulong ft) =>
            FloorDiv((long)ft - SimClock.FiletimeUnixOffset, 10_000_000);

        /// <summary>The milliseconds within the second of a FILETIME (0..999).</summary>
        internal static long MsPart(ulong ft) =>
            FloorDiv(FloorMod((long)ft - SimClock.FiletimeUnixOffset, 10_000_000), 10_000);

        private static long FloorDiv(long a, long b)
        {
            long q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0)))
                q--;
            return q;
        }

        private static long FloorMod(long a, long b)
        {
            long m = a % b;
            if (m != 0 && ((m < 0) != (b < 0)))
                m += b;
            return m;
        }

        /// <summary>Write an alarm property (doc: 1068487): 0, or 274 for a read-only property or
        /// a value the property cannot take (a number property needs a number, a NaN or an
        /// infinity is no category).</summary>
        public int SetProperty(AlarmDef a, string prop, TagDb.WVal value, string by = "cicode")
        {
            if (!Writable.Contains(prop))
                return 274;
            if (prop == "ack")
                return TruthOf(value) ? Acknowledge(a.Tag, by) : 0;
            if (prop == "disabled")
                return TruthOf(value) ? Disable(a.Tag, by: by) : Enable(a.Tag, by);
            a.Limits ??= new Dictionary<string, double?>(StringComparer.Ordinal);
            a.Delays ??= new Dictionary<string, double>(StringComparer.Ordinal);
            try
            {
                if (prop == "category")
                {
                    double d = NumberOf(value);
                    if (double.IsNaN(d) || double.IsInfinity(d))
                        return 274;
                    a.Category = (long)Math.Truncate(d);
                }
                else if (prop == "deadband")
                    a.Deadband = NumberOf(value);
                else if (prop == "delay")
                    a.Delay = NumberOf(value);
                else if (prop == "devdelay")
                {
                    double d = NumberOf(value);
                    a.Delays[DVH] = d;
                    a.Delays[DVL] = d;
                }
                else if (DelayKeys.TryGetValue(prop, out string? dk))
                    a.Delays[dk] = NumberOf(value);
                else if (LimitKeys.TryGetValue(prop, out string? lk))
                    a.Limits[lk] = NumberOf(value);
                else if (prop == "paging")
                    a.Paging = TruthOf(value);
                else if (prop == "paginggroup")
                    a.PagingGroup = TextOf(value);
                else if (prop.StartsWith("custom", StringComparison.Ordinal))
                {
                    var c = (string[])a.Custom.Clone();
                    string s = TextOf(value);
                    c[prop[6] - '1'] = s.Length > 64 ? s.Substring(0, 64) : s;
                    a.Custom = c;
                }
            }
            catch (FormatException)
            {
                return 274;
            }
            _dirty.Add(a.Tag);
            Generation++; // category / custom fields feed the counts
            _countCache.Clear();
            return 0;
        }

        /// <summary>A written value as a number: a REAL itself, a STRING holding a number
        /// (<see cref="JsonValue.ToNumber"/>), a QUALITY unsigned, an integer; FormatException for
        /// VOID or a string without a number.</summary>
        private static double NumberOf(TagDb.WVal value)
        {
            if (value.IsValue)
            {
                Value v = value.V;
                if (Types.IsRealType(v.T))
                    return v.D;
                if (v.T == Types.String)
                    return JsonValue.Of(v.S ?? "").ToNumber();
                if (v.T == Types.Void)
                    throw new FormatException("VOID is not a number");
                if (v.T == Types.Quality)
                    return unchecked((ulong)v.I);
                return v.I;
            }
            return value.J!.ToNumber();
        }

        /// <summary>A written value as text.</summary>
        private static string TextOf(TagDb.WVal value)
        {
            if (value.IsValue)
            {
                Value v = value.V;
                if (Types.IsRealType(v.T))
                    return FloatText.Shortest(v.D);
                if (v.T == Types.String)
                    return v.S ?? "";
                if (v.T == Types.Void)
                    return "";
                if (v.T == Types.Quality)
                    return unchecked((ulong)v.I).ToString(CultureInfo.InvariantCulture);
                return v.I.ToString(CultureInfo.InvariantCulture);
            }
            JsonValue j = value.J!;
            if (j.Kind == JsonKind.Bool)
                return j.Bool ? "1" : "0";
            if (j.Kind == JsonKind.Null)
                return "";
            return j.Text;
        }

        /// <summary>A written value as a condition: a number is true when nonzero (NaN too), a
        /// string when it holds a nonzero number or, holding none, is not blank; a JSON
        /// array by its first item, another JSON value when it is set.</summary>
        internal static bool TruthOf(TagDb.WVal value)
        {
            if (value.IsValue)
            {
                Value v = value.V;
                if (Types.IsRealType(v.T))
                    return v.D != 0 || double.IsNaN(v.D);
                if (v.T == Types.String)
                    return StrTruth(v.S ?? "");
                if (v.T == Types.Void)
                    return false;
                return v.I != 0;
            }
            return JsonTruth(value.J!);
        }

        private static bool StrTruth(string s) =>
            FloatText.TryParseNumber(s, out double d) ? d != 0 : !CitectText.IsBlank(s);

        internal static bool JsonTruth(JsonValue j)
        {
            switch (j.Kind)
            {
                case JsonKind.String:
                    return StrTruth(j.Str);
                case JsonKind.Array:
                    return j.Count > 0 && JsonTruth(j.Items[0]);
                case JsonKind.Float:
                    return j.FloatValue != 0 || double.IsNaN(j.FloatValue);
                default:
                    return j.IsSet;
            }
        }

        /// <summary>Wildcard match of a REFCAT: '*' may open and / or close the pattern (doc: 1033456).</summary>
        internal static bool Wild(string value, string pattern)
        {
            string p = CitectText.Lower(pattern),
                v = CitectText.Lower(value);
            if (
                p.StartsWith("*", StringComparison.Ordinal)
                && p.EndsWith("*", StringComparison.Ordinal)
                && p.Length > 1
            )
                return v.Contains(p.Substring(1, p.Length - 2));
            if (p.StartsWith("*", StringComparison.Ordinal))
                return v.EndsWith(p.Substring(1), StringComparison.Ordinal);
            if (p.EndsWith("*", StringComparison.Ordinal))
                return v.StartsWith(p.Substring(0, p.Length - 1), StringComparison.Ordinal);
            return v == p;
        }
    }
}

namespace CicodeSim.World
{
    /// <summary>The 'Equipment.Item' index of the alarms: an open-addressing
    /// table over the lower-case 'equip.item' of each alarm, no concatenated copies; the first
    /// alarm of a key wins.</summary>
    internal sealed class AlarmItemIndex
    {
        private readonly AlarmDef?[] _slots;
        private readonly int[] _hashes;

        private static uint Mix(uint h, char c)
        {
            unchecked
            {
                return (h ^ c) * 16777619;
            }
        }

        private static int Hash(AlarmDef a)
        {
            uint h = 2166136261;
            foreach (char c in a.Equip)
                h = Mix(h, char.ToLowerInvariant(c));
            h = Mix(h, '.');
            foreach (char c in a.Item)
                h = Mix(h, char.ToLowerInvariant(c));
            return (int)(h & 0x7FFFFFFF);
        }

        private static int HashLow(string low)
        {
            uint h = 2166136261;
            foreach (char c in low)
                h = Mix(h, c);
            return (int)(h & 0x7FFFFFFF);
        }

        private static bool Is(AlarmDef a, string low)
        {
            string e = a.Equip,
                it = a.Item;
            if (e.Length + 1 + it.Length != low.Length || low[e.Length] != '.')
                return false;
            for (int i = 0; i < e.Length; i++)
                if (char.ToLowerInvariant(e[i]) != low[i])
                    return false;
            for (int i = 0; i < it.Length; i++)
                if (char.ToLowerInvariant(it[i]) != low[e.Length + 1 + i])
                    return false;
            return true;
        }

        public AlarmItemIndex(List<AlarmDef> alarms)
        {
            int cap = 16;
            while (cap < alarms.Count * 2)
                cap <<= 1;
            _slots = new AlarmDef?[cap];
            _hashes = new int[cap];
            int mask = cap - 1;
            foreach (var a in alarms)
            {
                int h = Hash(a),
                    s = h & mask;
                bool dup = false;
                while (_slots[s] != null)
                {
                    var o = _slots[s]!;
                    if (
                        _hashes[s] == h
                        && CitectText.SameName(o.Equip, a.Equip)
                        && CitectText.SameName(o.Item, a.Item)
                    )
                    {
                        dup = true;
                        break;
                    }
                    s = (s + 1) & mask;
                }
                if (dup)
                    continue;
                _slots[s] = a;
                _hashes[s] = h;
            }
        }

        public AlarmDef? Find(string low)
        {
            int h = HashLow(low),
                mask = _slots.Length - 1,
                s = h & mask;
            while (_slots[s] != null)
            {
                if (_hashes[s] == h && Is(_slots[s]!, low))
                    return _slots[s];
                s = (s + 1) & mask;
            }
            return null;
        }
    }
}
