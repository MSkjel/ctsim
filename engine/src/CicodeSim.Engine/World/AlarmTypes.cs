using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.World
{
    /// <summary>A set that iterates in insertion order (deterministic from run to run).</summary>
    public sealed class OrderedSet : IEnumerable<string>
    {
        private readonly Dictionary<string, int> _pos;
        private readonly List<string?> _items = new List<string?>();
        private int _dead;

        public OrderedSet(IEqualityComparer<string>? comparer = null)
        {
            _pos = new Dictionary<string, int>(comparer ?? StringComparer.Ordinal);
        }

        public int Count => _pos.Count;

        public bool Add(string k)
        {
            if (_pos.ContainsKey(k))
                return false;
            _pos[k] = _items.Count;
            _items.Add(k);
            return true;
        }

        public bool Remove(string k)
        {
            if (!_pos.TryGetValue(k, out int i))
                return false;
            _pos.Remove(k);
            _items[i] = null;
            if (++_dead > 32 && _dead * 2 > _items.Count)
                Compact();
            return true;
        }

        public bool Contains(string k) => _pos.ContainsKey(k);

        public void Clear()
        {
            _pos.Clear();
            _items.Clear();
            _dead = 0;
        }

        private void Compact()
        {
            var keep = new List<string?>(_pos.Count);
            foreach (string? s in _items)
                if (s != null)
                {
                    _pos[s] = keep.Count;
                    keep.Add(s);
                }
            _items.Clear();
            _items.AddRange(keep);
            _dead = 0;
        }

        /// <summary>A copy of the members, in insertion order.</summary>
        public List<string> ToList()
        {
            var o = new List<string>(_pos.Count);
            foreach (string? s in _items)
                if (s != null)
                    o.Add(s);
            return o;
        }

        public IEnumerator<string> GetEnumerator() => ToList().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A variable of an alarm: 'NOT Tag' = (Tag, negated); default = none.</summary>
    public readonly struct AlarmVar
    {
        public string Name { get; }
        public bool Negated { get; }

        public AlarmVar(string name, bool negated)
        {
            Name = name;
            Negated = negated;
        }

        public bool IsSet => Name != null;
    }

    /// <summary>One configured alarm.</summary>
    public sealed class AlarmDef
    {
        public string Kind { get; }
        public string Tag { get; }
        public string Project { get; }

        /// <summary>The position in definition order (AlarmDb.All(): kind order, then row
        /// order), set when the alarm server indexes its alarms; the scan and AcknowledgeAll
        /// order.</summary>
        public int Ordinal { get; internal set; }
        public string Name = "",
            Desc = "",
            Cluster = "",
            Comment = "",
            Help = "",
            Equip = "",
            Item = "";
        public string Expr = "";
        public string CategoryText = "";
        public long Category,
            Area,
            Priv;
        public double Delay;
        public AlarmVar[] Vars = Array.Empty<AlarmVar>();
        public string[] Custom = AlarmDb.NoCustom;
        public bool Paging;

        // the fields most alarms leave empty live in one object made at the first non-empty value
        // (saves memory on large projects)
        private sealed class Extra
        {
            public string Setpoint = "",
                Format = "",
                OnFunc = "",
                OffFunc = "",
                PagingGroup = "",
                Historian = "";
            public Dictionary<string, double?>? Limits;
            public double Deadband;
            public Dictionary<string, double>? Delays;
            public (string desc, string type)[] States = Array.Empty<(string, string)>();
        }

        private Extra? _x;
        private Extra X => _x ??= new Extra();

        public string Setpoint
        {
            get => _x?.Setpoint ?? "";
            set
            {
                if (value.Length > 0 || _x != null)
                    X.Setpoint = value;
            }
        }
        public string Format
        {
            get => _x?.Format ?? "";
            set
            {
                if (value.Length > 0 || _x != null)
                    X.Format = value;
            }
        }
        public string OnFunc
        {
            get => _x?.OnFunc ?? "";
            set
            {
                if (value.Length > 0 || _x != null)
                    X.OnFunc = value;
            }
        }
        public string OffFunc
        {
            get => _x?.OffFunc ?? "";
            set
            {
                if (value.Length > 0 || _x != null)
                    X.OffFunc = value;
            }
        }
        public string PagingGroup
        {
            get => _x?.PagingGroup ?? "";
            set
            {
                if (value.Length > 0 || _x != null)
                    X.PagingGroup = value;
            }
        }
        public string Historian
        {
            get => _x?.Historian ?? "";
            set
            {
                if (value.Length > 0 || _x != null)
                    X.Historian = value;
            }
        }

        /// <summary>Analog only (null = none set): HH H L LL DEV R.</summary>
        public Dictionary<string, double?>? Limits
        {
            get => _x?.Limits;
            set
            {
                if (value != null || _x != null)
                    X.Limits = value;
            }
        }
        public double Deadband
        {
            get => _x?.Deadband ?? 0.0;
            set
            {
                if (value != 0 || _x != null || double.IsNaN(value))
                    X.Deadband = value;
            }
        }

        /// <summary>Analog only: HH H L LL DVH DVL R.</summary>
        public Dictionary<string, double>? Delays
        {
            get => _x?.Delays;
            set
            {
                if (value != null || _x != null)
                    X.Delays = value;
            }
        }
        public (string desc, string type)[] States
        {
            get => _x?.States ?? Array.Empty<(string, string)>();
            set
            {
                if (value.Length > 0 || _x != null)
                    X.States = value;
            }
        }

        public AlarmDef(string kind, string tag, string project = "")
        {
            Kind = kind;
            Tag = tag;
            Project = project;
        }

        /// <summary>The lower-case tag; the server's own sets key on the tag itself, compared
        /// without case, to keep no lowered copies.</summary>
        public string Key => CitectText.Lower(Tag);
        public bool IsAnalog =>
            Kind == AlarmDb.Analog || Kind == AlarmDb.TsAnalog || Kind == AlarmDb.ArgyleAnalog;

        public double? Limit(string k) =>
            _x?.Limits != null && _x.Limits.TryGetValue(k, out double? v) ? v : null;

        public double DelayOf(string k) =>
            _x?.Delays != null && _x.Delays.TryGetValue(k, out double v) ? v : 0.0;

        public override string ToString() => $"AlarmDef({Kind} {Tag})";
    }

    /// <summary>The state of one alarm (or one raised hardware alarm).</summary>
    public sealed class AlarmState
    {
        public bool On;
        public bool Ack = true;
        public bool InList; // ON, or OFF unacknowledged
        public bool Disabled;
        public int State; // .State enumeration (without the mask bits)
        public string? Sub;
        public double OnTime,
            OffTime,
            AckTime,
            Time; // simulated seconds
        public Dictionary<string, double> Pending = new Dictionary<string, double>(StringComparer.Ordinal);
        public double? Value;
        public double? LastValue;
        public double? LastT;
        public string Comment = "";
        public double? DisabledUntil;
        public long Transitions;
    }

    /// <summary>One alarm state change (listeners, traces).</summary>
    public sealed class Transition
    {
        public double T { get; }
        public string Tag { get; }
        public string Kind { get; }
        public string Event { get; } // on | off | ack | clear | state | disable | enable
        public long State { get; }
        public string? Sub { get; }
        public string By { get; }

        public Transition(
            double t,
            string tag,
            string kind,
            string ev,
            long state,
            string? sub = null,
            string by = "alarm"
        )
        {
            T = t;
            Tag = tag;
            Kind = kind;
            Event = ev;
            State = state;
            Sub = sub;
            By = by;
        }
    }

    /// <summary>A raised hardware alarm (the runtime raises them).</summary>
    public sealed class HardwareAlarm
    {
        public long Code;
        public string Desc;
        public string Name;
        public string Page;
        public int Count = 1;
        public AlarmState St = new AlarmState();

        public HardwareAlarm(long code, string desc, string name = "Cicode", string page = "")
        {
            Code = code;
            Desc = desc;
            Name = name;
            Page = page;
        }
    }

    /// <summary>An alarm filter expression.</summary>
    public sealed class AlarmFilter
    {
        private static readonly string S = TagTypes.BlankClass;
        private static readonly Regex TokenRx = new Regex(
            $"[{S}]*(\\(|\\)|;|<>|<=|>=|=|<|>|''(?:[^']|'(?!'))*''|\"[^\"]*\"|[^{S}()=<>;]+)",
            RegexOptions.CultureInvariant
        );

        internal abstract class Node { }

        internal sealed class All : Node
        {
            public List<Node> Items = new List<Node>();
        }

        internal sealed class Bin : Node
        {
            public string Op = "";
            public Node A = null!,
                B = null!;
        }

        internal sealed class Not : Node
        {
            public Node A = null!;
        }

        internal sealed class Cmp : Node
        {
            public string Field = "",
                Op = "",
                Want = "";
            public bool WantIsNumber;
            public double WantNumber;
        }

        public string Text { get; }
        private readonly List<string> _toks = new List<string>();
        private int _i;
        internal Node? Tree { get; }
        public bool Empty => Tree == null;

        public AlarmFilter(string? text)
        {
            Text = text ?? "";
            foreach (Match m in TokenRx.Matches(Text))
                if (m.Groups[1].Value.Length > 0)
                    _toks.Add(m.Groups[1].Value);
            Tree = _toks.Count > 0 ? ParseList() : null;
        }

        private string? Peek() => _i < _toks.Count ? _toks[_i] : null;

        private string? Next()
        {
            string? t = Peek();
            _i++;
            return t;
        }

        private Node ParseList()
        {
            var items = new List<Node> { ParseOr() };
            while (Peek() == ";")
            {
                Next();
                if (Peek() == null)
                    break;
                items.Add(ParseOr());
            }
            if (items.Count == 1)
                return items[0];
            var all = new All();
            all.Items.AddRange(items);
            return all;
        }

        private Node ParseOr()
        {
            Node left = ParseAnd();
            while (true)
            {
                string up = CitectText.Upper(Peek() ?? "");
                if (up != "OR" && up != "XOR" && up != "EQV")
                    break;
                string op = CitectText.Lower(Next()!);
                left = new Bin
                {
                    Op = op,
                    A = left,
                    B = ParseAnd(),
                };
            }
            return left;
        }

        private Node ParseAnd()
        {
            Node left = ParseNot();
            while (CitectText.Upper(Peek() ?? "") == "AND")
            {
                Next();
                var all = new All();
                all.Items.Add(left);
                all.Items.Add(ParseNot());
                left = all;
            }
            return left;
        }

        private Node ParseNot()
        {
            if (CitectText.Upper(Peek() ?? "") == "NOT")
            {
                Next();
                return new Not { A = ParseNot() };
            }
            if (Peek() == "(")
            {
                Next();
                Node inner = ParseOr();
                if (Peek() == ")")
                    Next();
                return inner;
            }
            string fld = Next() ?? "";
            string? op = Peek();
            string val;
            if (op == "=" || op == "<>" || op == "<" || op == "<=" || op == ">" || op == ">=")
            {
                Next();
                val = Next() ?? "";
            }
            else
            {
                op = "=";
                val = fld;
                fld = "TAG";
            }
            if (
                val.StartsWith("''", StringComparison.Ordinal)
                && val.EndsWith("''", StringComparison.Ordinal)
                && val.Length >= 4
            )
                val = val.Substring(2, val.Length - 4);
            else if (
                val.StartsWith("\"", StringComparison.Ordinal)
                && val.EndsWith("\"", StringComparison.Ordinal)
                && val.Length >= 2
            )
                val = val.Substring(1, val.Length - 2);
            var c = new Cmp
            {
                Field = CitectText.Upper(fld),
                Op = op!,
                Want = val,
            };
            c.WantIsNumber = FloatText.TryParseNumber(val, out c.WantNumber);
            return c;
        }

        /// <summary>A field's value: a number (integer fields), a string, or null (no such field).</summary>
        public readonly struct FieldValue
        {
            public readonly bool IsNull,
                IsNumber;
            public readonly long N;
            public readonly string? S;

            private FieldValue(bool isNull, bool isNumber, long n, string? s)
            {
                IsNull = isNull;
                IsNumber = isNumber;
                N = n;
                S = s;
            }

            public static readonly FieldValue None = new FieldValue(true, false, 0, null);

            public static FieldValue Num(long n) => new FieldValue(false, true, n, null);

            public static FieldValue Str(string s) => new FieldValue(false, false, 0, s);
        }

        public bool Match(Func<string, FieldValue> get) => Tree == null || Eval(Tree, get);

        private static bool Eval(Node node, Func<string, FieldValue> get)
        {
            switch (node)
            {
                case All a:
                    foreach (Node n in a.Items)
                        if (!Eval(n, get))
                            return false;
                    return true;
                case Bin b:
                    if (b.Op == "or")
                        return Eval(b.A, get) || Eval(b.B, get);
                    if (b.Op == "xor")
                        return Eval(b.A, get) != Eval(b.B, get);
                    return Eval(b.A, get) == Eval(b.B, get); // eqv
                case Not nt:
                    return !Eval(nt.A, get);
            }
            var c = (Cmp)node;
            FieldValue have = get(c.Field);
            if (have.IsNull)
                return c.Op == "<>";
            if (have.IsNumber)
            {
                if (!c.WantIsNumber)
                    return c.Op == "<>";
                int k = JsonNumbers.Cmp(have.N, c.WantNumber); // 2 = NaN: every comparison false
                switch (c.Op)
                {
                    case "=":
                        return k == 0;
                    case "<>":
                        return k != 0;
                    case "<":
                        return k == -1;
                    case "<=":
                        return k == -1 || k == 0;
                    case ">":
                        return k == 1;
                    default:
                        return k == 1 || k == 0;
                }
            }
            string h = CitectText.Lower(have.S ?? ""),
                w = CitectText.Lower(c.Want);
            int star = w.IndexOf('*');
            if (star >= 0 && (c.Op == "=" || c.Op == "<>"))
            {
                string pre = w.Substring(0, star),
                    post = w.Substring(star + 1);
                bool hit =
                    h.StartsWith(pre, StringComparison.Ordinal)
                    && h.EndsWith(post, StringComparison.Ordinal)
                    && h.Length >= pre.Length + post.Length;
                return c.Op == "=" ? hit : !hit;
            }
            int cmp = string.CompareOrdinal(h, w);
            switch (c.Op)
            {
                case "=":
                    return cmp == 0;
                case "<>":
                    return cmp != 0;
                case "<":
                    return cmp < 0;
                case "<=":
                    return cmp <= 0;
                case ">":
                    return cmp > 0;
                default:
                    return cmp >= 0;
            }
        }
    }
}
