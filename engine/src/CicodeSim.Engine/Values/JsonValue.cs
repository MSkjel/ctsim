using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using CicodeSim.Project;

// The engine's one JSON implementation: JsonValue below; Json (Values/Json.cs) is its
// plain-object facade.
namespace CicodeSim.Values
{
    public enum JsonKind
    {
        Null,
        Bool,
        Int,
        Float,
        String,
        Array,
        Object,
    }

    /// <summary>A JSON value, with NaN and Infinity read and written as numbers.</summary>
    public sealed class JsonValue
    {
        public JsonKind Kind { get; }
        private readonly bool _b;
        private readonly long _i;
        private readonly BigInteger? _big;
        private readonly double _d;
        private readonly string? _s;
        private readonly List<JsonValue>? _arr;
        private readonly List<KeyValuePair<string, JsonValue>>? _obj;
        private Dictionary<string, int>? _index;

        public static readonly JsonValue Null = new JsonValue(JsonKind.Null);
        public static readonly JsonValue True = new JsonValue(true);
        public static readonly JsonValue False = new JsonValue(false);

        private JsonValue(JsonKind k)
        {
            Kind = k;
        }

        private JsonValue(bool b)
        {
            Kind = JsonKind.Bool;
            _b = b;
        }

        private JsonValue(long i)
        {
            Kind = JsonKind.Int;
            _i = i;
        }

        private JsonValue(BigInteger b)
        {
            Kind = JsonKind.Int;
            if (b >= long.MinValue && b <= long.MaxValue)
                _i = (long)b;
            else
                _big = b;
        }

        private JsonValue(double d)
        {
            Kind = JsonKind.Float;
            _d = d;
        }

        private JsonValue(string s)
        {
            Kind = JsonKind.String;
            _s = s;
        }

        private JsonValue(List<JsonValue> a)
        {
            Kind = JsonKind.Array;
            _arr = a;
        }

        private JsonValue(List<KeyValuePair<string, JsonValue>> o)
        {
            Kind = JsonKind.Object;
            _obj = o;
        }

        public static JsonValue Of(bool b) => b ? True : False;

        public static JsonValue Of(long i) => new JsonValue(i);

        public static JsonValue Of(ulong u) =>
            u <= long.MaxValue ? new JsonValue((long)u) : new JsonValue((BigInteger)u);

        public static JsonValue Of(BigInteger b) => new JsonValue(b);

        public static JsonValue Of(double d) => new JsonValue(d);

        public static JsonValue Of(string? s) => s == null ? Null : new JsonValue(s);

        public static JsonValue Array(IEnumerable<JsonValue> items) =>
            new JsonValue(new List<JsonValue>(items));

        public static JsonValue NewArray() => new JsonValue(new List<JsonValue>());

        public static JsonValue NewObject() => new JsonValue(new List<KeyValuePair<string, JsonValue>>());

        /// <summary>A JSON value of a .NET value (null, bool, integers, double, string,
        /// JsonValue, Value: its payload, lists and string-keyed dictionaries).</summary>
        public static JsonValue From(object? v)
        {
            switch (v)
            {
                case null:
                    return Null;
                case JsonValue j:
                    return j;
                case bool b:
                    return Of(b);
                case int i:
                    return Of(i);
                case long l:
                    return Of(l);
                case ulong u:
                    return Of(u);
                case uint ui:
                    return Of((long)ui);
                case short sh:
                    return Of(sh);
                case byte by:
                    return Of(by);
                case BigInteger bi:
                    return Of(bi);
                case double d:
                    return Of(d);
                case float f:
                    return Of((double)f);
                case string s:
                    return Of(s);
                case Value val:
                    return From(CicodeSim.World.WorldValues.Payload(val));
                case System.Collections.IDictionary dict:
                {
                    var o = NewObject();
                    foreach (System.Collections.DictionaryEntry e in dict)
                        o.Set(Convert.ToString(e.Key, CultureInfo.InvariantCulture) ?? "", From(e.Value));
                    return o;
                }
                case System.Collections.IEnumerable seq:
                {
                    var a = NewArray();
                    foreach (object? x in seq)
                        a.Add(From(x));
                    return a;
                }
                default:
                    return Of(Convert.ToString(v, CultureInfo.InvariantCulture));
            }
        }

        // ------------------------------------------------------------------ access
        public bool IsNull => Kind == JsonKind.Null;
        public bool Bool => _b;
        public bool IsNumber => Kind == JsonKind.Int || Kind == JsonKind.Float;
        public bool FitsLong => Kind == JsonKind.Int && _big == null;
        public long Long => _i;
        public BigInteger BigValue => _big ?? _i;
        public double Double =>
            Kind == JsonKind.Float ? _d
            : Kind == JsonKind.Int ? (_big != null ? FloatText.FromInteger(_big.Value) : _i)
            : Kind == JsonKind.Bool ? (_b ? 1 : 0)
            : 0;
        public double FloatValue => _d;
        public string Str => _s ?? "";
        public IReadOnlyList<JsonValue> Items =>
            (IReadOnlyList<JsonValue>?)_arr ?? System.Array.Empty<JsonValue>();
        public IReadOnlyList<KeyValuePair<string, JsonValue>> Pairs =>
            (IReadOnlyList<KeyValuePair<string, JsonValue>>?)_obj
            ?? System.Array.Empty<KeyValuePair<string, JsonValue>>();
        public int Count => _arr?.Count ?? _obj?.Count ?? 0;

        public void Add(JsonValue v) => _arr!.Add(v);

        public void Set(string key, JsonValue v)
        {
            Index();
            if (_index!.TryGetValue(key, out int i))
                _obj![i] = new KeyValuePair<string, JsonValue>(key, v);
            else
            {
                _index[key] = _obj!.Count;
                _obj.Add(new KeyValuePair<string, JsonValue>(key, v));
            }
        }

        public void Set(string key, object? v) => Set(key, From(v));

        private void Index()
        {
            if (_index != null)
                return;
            _index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < _obj!.Count; i++)
                _index[_obj[i].Key] = i;
        }

        public bool Has(string key)
        {
            if (Kind != JsonKind.Object)
                return false;
            Index();
            return _index!.ContainsKey(key);
        }

        /// <summary>The value of a key (null when absent or not an object).</summary>
        public JsonValue? Get(string key)
        {
            if (Kind != JsonKind.Object)
                return null;
            Index();
            return _index!.TryGetValue(key, out int i) ? _obj![i].Value : null;
        }

        public JsonValue this[string key] =>
            Get(key) ?? throw new KeyNotFoundException("no " + CitectText.Quote(key));

        /// <summary>True for a value that is set: true, a nonzero number, a non-empty string,
        /// array or object (null, false, 0 and empty values are not set).</summary>
        public bool IsSet
        {
            get
            {
                switch (Kind)
                {
                    case JsonKind.Null:
                        return false;
                    case JsonKind.Bool:
                        return _b;
                    case JsonKind.Int:
                        return _big != null || _i != 0;
                    case JsonKind.Float:
                        return _d != 0;
                    case JsonKind.String:
                        return _s!.Length > 0;
                    default:
                        return Count > 0;
                }
            }
        }

        /// <summary>The JSON type name ('null', 'boolean', 'integer', 'number', 'string',
        /// 'array', 'object'), for messages.</summary>
        public string KindName =>
            Kind switch
            {
                JsonKind.Null => "null",
                JsonKind.Bool => "boolean",
                JsonKind.Int => "integer",
                JsonKind.Float => "number",
                JsonKind.String => "string",
                JsonKind.Array => "array",
                _ => "object",
            };

        // ------------------------------------------------------------------ conversions
        /// <summary>The value as a double.</summary>
        public double ToNumber()
        {
            switch (Kind)
            {
                case JsonKind.Float:
                    return _d;
                case JsonKind.Int:
                    return _big != null ? FloatText.FromInteger(_big.Value) : _i;
                case JsonKind.Bool:
                    return _b ? 1.0 : 0.0;
                case JsonKind.String:
                    if (FloatText.TryParseNumber(_s, out double d))
                        return d;
                    break;
            }
            throw new FormatException("not a number: " + Dump());
        }

        /// <summary>The value as an integer.</summary>
        public BigInteger ToInteger()
        {
            switch (Kind)
            {
                case JsonKind.Int:
                    return BigValue;
                case JsonKind.Bool:
                    return _b ? 1 : 0;
                case JsonKind.Float:
                    if (double.IsInfinity(_d))
                        throw new OverflowException("not an integer: " + Dump());
                    if (double.IsNaN(_d))
                        break;
                    return new BigInteger(Math.Truncate(_d));
                case JsonKind.String:
                {
                    var b = CitectText.ParseInt(_s, 10);
                    if (b != null)
                        return b.Value;
                    break;
                }
            }
            throw new FormatException("not an integer: " + Dump());
        }

        /// <summary>The value as text for a name or a message: a string as it is, anything else
        /// as its JSON text.</summary>
        public string Text => Kind == JsonKind.String ? _s! : Dump();

        /// <summary>Equal values: numbers by their exact value (1 == 1.0), the rest by kind and
        /// content (true is no number; objects compare by key, not key order).</summary>
        public bool ValueEquals(JsonValue o)
        {
            bool numA = Kind == JsonKind.Int || Kind == JsonKind.Float;
            bool numB = o.Kind == JsonKind.Int || o.Kind == JsonKind.Float;
            if (numA && numB)
                return JsonNumbers.Eq(this, o);
            if (Kind != o.Kind)
                return false;
            switch (Kind)
            {
                case JsonKind.Null:
                    return true;
                case JsonKind.Bool:
                    return _b == o._b;
                case JsonKind.String:
                    return _s == o._s;
                case JsonKind.Array:
                    if (_arr!.Count != o._arr!.Count)
                        return false;
                    for (int i = 0; i < _arr.Count; i++)
                        if (!_arr[i].ValueEquals(o._arr[i]))
                            return false;
                    return true;
                default:
                    if (_obj!.Count != o._obj!.Count)
                        return false;
                    foreach (var kv in _obj)
                    {
                        var other = o.Get(kv.Key);
                        if (other == null || !kv.Value.ValueEquals(other))
                            return false;
                    }
                    return true;
            }
        }

        internal BigInteger AsBig() => Kind == JsonKind.Bool ? (_b ? 1 : 0) : BigValue;

        // ------------------------------------------------------------------ writing
        /// <summary>The compact JSON text (no blanks between the parts).</summary>
        public string Dump()
        {
            var sb = new StringBuilder();
            WriteJson(sb, compact: true);
            return sb.ToString();
        }

        /// <summary>The JSON text with ', ' between items and ': ' after keys.</summary>
        public string DumpSpaced()
        {
            var sb = new StringBuilder();
            WriteJson(sb, compact: false);
            return sb.ToString();
        }

        /// <summary>Compact strict RFC 8259 JSON.</summary>
        public string DumpStrict()
        {
            var sb = new StringBuilder();
            WriteJson(sb, compact: true, strict: true);
            return sb.ToString();
        }

        /// <summary>Append the compact strict text (<see cref="DumpStrict"/>).</summary>
        public void WriteStrict(StringBuilder sb) => WriteJson(sb, compact: true, strict: true);

        private void WriteJson(StringBuilder sb, bool compact, bool strict = false)
        {
            switch (Kind)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    break;
                case JsonKind.Bool:
                    sb.Append(_b ? "true" : "false");
                    break;
                case JsonKind.Int:
                    sb.Append(BigValue.ToString(CultureInfo.InvariantCulture));
                    break;
                case JsonKind.Float:
                    if (double.IsNaN(_d))
                        sb.Append(strict ? "\"NaN\"" : "NaN");
                    else if (double.IsPositiveInfinity(_d))
                        sb.Append(strict ? "\"Infinity\"" : "Infinity");
                    else if (double.IsNegativeInfinity(_d))
                        sb.Append(strict ? "\"-Infinity\"" : "-Infinity");
                    else
                        sb.Append(FloatText.Shortest(_d));
                    break;
                case JsonKind.String:
                    WriteJsonString(sb, _s!);
                    break;
                case JsonKind.Array:
                    sb.Append('[');
                    for (int i = 0; i < _arr!.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(compact ? "," : ", ");
                        _arr[i].WriteJson(sb, compact, strict);
                    }
                    sb.Append(']');
                    break;
                default:
                    sb.Append('{');
                    for (int i = 0; i < _obj!.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(compact ? "," : ", ");
                        WriteJsonString(sb, _obj[i].Key);
                        sb.Append(compact ? ":" : ": ");
                        _obj[i].Value.WriteJson(sb, compact, strict);
                    }
                    sb.Append('}');
                    break;
            }
        }

        /// <summary>A string as JSON text (non-ASCII characters as they are).</summary>
        public static void WriteJsonString(StringBuilder sb, string s) => WriteJsonString(sb, s, false);

        /// <summary>A string as JSON text; with <paramref name="ensureAscii"/> every character
        /// outside ' '..'~' as a \u escape.</summary>
        public static void WriteJsonString(StringBuilder sb, string s, bool ensureAscii)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    default:
                        if (c < 0x20 || (ensureAscii && c > 0x7E))
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        public override string ToString() => Dump();

        /// <summary>The value as plain .NET objects: Dictionary&lt;string, object?&gt; (key order
        /// kept), List&lt;object?&gt;, string, long (an integer that fits; a bigger one as a
        /// double), double, bool or null (what the Json facade's Parse returns).</summary>
        public object? ToPlain()
        {
            switch (Kind)
            {
                case JsonKind.Null:
                    return null;
                case JsonKind.Bool:
                    return _b;
                case JsonKind.Int:
                    return _big == null ? (object)_i : FloatText.FromInteger(_big.Value);
                case JsonKind.Float:
                    return _d;
                case JsonKind.String:
                    return _s!;
                case JsonKind.Array:
                {
                    var a = new List<object?>(_arr!.Count);
                    foreach (var v in _arr)
                        a.Add(v.ToPlain());
                    return a;
                }
                default:
                {
                    var d = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var kv in _obj!)
                        d[kv.Key] = kv.Value.ToPlain();
                    return d;
                }
            }
        }

        // ------------------------------------------------------------------ parsing
        /// <summary>The deepest nesting Parse accepts.</summary>
        public const int MaxDepth = 512;

        /// <summary>The most digits Parse turns into an integer (more is a FormatException): it
        /// bounds the cost, BigInteger.Parse is superlinear in the digit count.</summary>
        public const int MaxIntDigits = 4300;

        /// <summary>Parse a JSON text.</summary>
        public static JsonValue Parse(string text)
        {
            var p = new Parser(text);
            p.Ws();
            var v = p.Value();
            p.Ws();
            if (p.Pos != text.Length)
                throw p.Error("text after the value");
            return v;
        }

        private sealed class Parser
        {
            private readonly string _t;
            public int Pos;
            private int _depth;

            public Parser(string t)
            {
                _t = t;
            }

            public FormatException Error(string what) =>
                new FormatException($"JSON: {what} at character {Pos}");

            public void Ws()
            {
                while (
                    Pos < _t.Length
                    && (_t[Pos] == ' ' || _t[Pos] == '\t' || _t[Pos] == '\n' || _t[Pos] == '\r')
                )
                    Pos++;
            }

            public JsonValue Value()
            {
                if (Pos >= _t.Length)
                    throw Error("a value expected");
                char c = _t[Pos];
                switch (c)
                {
                    case '{':
                    case '[':
                        if (++_depth > MaxDepth)
                            throw Error("nesting deeper than 512");
                        var r = c == '{' ? Obj() : Arr();
                        _depth--;
                        return r;
                    case '"':
                        return Of(Str());
                }
                if (Match("null"))
                    return Null;
                if (Match("true"))
                    return True;
                if (Match("false"))
                    return False;
                if (Match("NaN"))
                    return Of(FloatText.QuietNaN); // C's NAN, 0x7FF8000000000000
                if (Match("Infinity"))
                    return Of(double.PositiveInfinity);
                if (Match("-Infinity"))
                    return Of(double.NegativeInfinity);
                return Num();
            }

            private bool Match(string w)
            {
                if (string.CompareOrdinal(_t, Pos, w, 0, w.Length) == 0)
                {
                    Pos += w.Length;
                    return true;
                }
                return false;
            }

            private JsonValue Obj()
            {
                Pos++;
                var o = NewObject();
                Ws();
                if (Pos < _t.Length && _t[Pos] == '}')
                {
                    Pos++;
                    return o;
                }
                while (true)
                {
                    Ws();
                    if (Pos >= _t.Length || _t[Pos] != '"')
                        throw Error("a key (a string) expected");
                    string k = Str();
                    Ws();
                    if (Pos >= _t.Length || _t[Pos] != ':')
                        throw Error("':' expected");
                    Pos++;
                    Ws();
                    o.Set(k, Value());
                    Ws();
                    if (Pos < _t.Length && _t[Pos] == ',')
                    {
                        Pos++;
                        continue;
                    }
                    if (Pos < _t.Length && _t[Pos] == '}')
                    {
                        Pos++;
                        return o;
                    }
                    throw Error("',' or the end expected");
                }
            }

            private JsonValue Arr()
            {
                Pos++;
                var a = NewArray();
                Ws();
                if (Pos < _t.Length && _t[Pos] == ']')
                {
                    Pos++;
                    return a;
                }
                while (true)
                {
                    Ws();
                    a.Add(Value());
                    Ws();
                    if (Pos < _t.Length && _t[Pos] == ',')
                    {
                        Pos++;
                        continue;
                    }
                    if (Pos < _t.Length && _t[Pos] == ']')
                    {
                        Pos++;
                        return a;
                    }
                    throw Error("',' or the end expected");
                }
            }

            private string Str()
            {
                Pos++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (Pos >= _t.Length)
                        throw Error("unterminated string");
                    char c = _t[Pos++];
                    if (c == '"')
                        return sb.ToString();
                    if (c == '\\')
                    {
                        if (Pos >= _t.Length)
                            throw Error("unterminated string");
                        char e = _t[Pos++];
                        switch (e)
                        {
                            case '"':
                                sb.Append('"');
                                break;
                            case '\\':
                                sb.Append('\\');
                                break;
                            case '/':
                                sb.Append('/');
                                break;
                            case 'b':
                                sb.Append('\b');
                                break;
                            case 'f':
                                sb.Append('\f');
                                break;
                            case 'n':
                                sb.Append('\n');
                                break;
                            case 'r':
                                sb.Append('\r');
                                break;
                            case 't':
                                sb.Append('\t');
                                break;
                            case 'u':
                                // exactly 4 hex digits
                                if (Pos + 4 > _t.Length)
                                    throw Error("a \\u escape needs 4 hex digits");
                                int u = 0;
                                for (int k = 0; k < 4; k++)
                                {
                                    char h = _t[Pos + k];
                                    int dv =
                                        h >= '0' && h <= '9' ? h - '0'
                                        : h >= 'a' && h <= 'f' ? h - 'a' + 10
                                        : h >= 'A' && h <= 'F' ? h - 'A' + 10
                                        : -1;
                                    if (dv < 0)
                                        throw Error("a \\u escape needs 4 hex digits");
                                    u = u * 16 + dv;
                                }
                                sb.Append((char)u);
                                Pos += 4;
                                break;
                            default:
                                throw Error("unknown escape");
                        }
                    }
                    else if (c < 0x20)
                        throw Error("a control character in a string");
                    else
                        sb.Append(c);
                }
            }

            private JsonValue Num()
            {
                int start = Pos;
                if (Pos < _t.Length && _t[Pos] == '-')
                    Pos++;
                if (Pos >= _t.Length)
                    throw Error("a value expected");
                if (_t[Pos] == '0')
                    Pos++;
                else if (_t[Pos] >= '1' && _t[Pos] <= '9')
                    while (Pos < _t.Length && char.IsDigit(_t[Pos]) && _t[Pos] < 128)
                        Pos++;
                else
                {
                    Pos = start;
                    throw Error("a value expected");
                }
                bool isFloat = false;
                if (
                    Pos < _t.Length
                    && _t[Pos] == '.'
                    && Pos + 1 < _t.Length
                    && _t[Pos + 1] >= '0'
                    && _t[Pos + 1] <= '9'
                )
                {
                    isFloat = true;
                    Pos++;
                    while (Pos < _t.Length && _t[Pos] >= '0' && _t[Pos] <= '9')
                        Pos++;
                }
                if (Pos < _t.Length && (_t[Pos] == 'e' || _t[Pos] == 'E'))
                {
                    int save = Pos;
                    Pos++;
                    if (Pos < _t.Length && (_t[Pos] == '+' || _t[Pos] == '-'))
                        Pos++;
                    if (Pos < _t.Length && _t[Pos] >= '0' && _t[Pos] <= '9')
                    {
                        isFloat = true;
                        while (Pos < _t.Length && _t[Pos] >= '0' && _t[Pos] <= '9')
                            Pos++;
                    }
                    else
                        Pos = save;
                }
                string num = _t.Substring(start, Pos - start);
                if (!isFloat)
                {
                    int digits = num.Length - (num[0] == '-' ? 1 : 0);
                    if (digits > MaxIntDigits)
                        throw new FormatException(
                            "JSON: an integer of "
                                + digits.ToString(CultureInfo.InvariantCulture)
                                + " digits (at most "
                                + MaxIntDigits.ToString(CultureInfo.InvariantCulture)
                                + ") at character "
                                + start.ToString(CultureInfo.InvariantCulture)
                        );
                    if (digits <= 18)
                        return Of(
                            long.Parse(num, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
                        );
                    return Of(BigInteger.Parse(num, CultureInfo.InvariantCulture));
                }
                double d = FloatText.ParseDecimal(num, 0, num.Length);
                return Of(d);
            }
        }
    }

    /// <summary>Comparisons of JSON numbers and of an integer with a double by their exact
    /// values (never through a rounded double).</summary>
    public static class JsonNumbers
    {
        /// <summary>a == b for two numeric JSON values.</summary>
        public static bool Eq(JsonValue a, JsonValue b)
        {
            if (a.Kind == JsonKind.Float && b.Kind == JsonKind.Float)
                return a.FloatValue == b.FloatValue;
            if (a.Kind == JsonKind.Float)
                return Cmp(b.AsBig(), a.FloatValue) == 0;
            if (b.Kind == JsonKind.Float)
                return Cmp(a.AsBig(), b.FloatValue) == 0;
            return a.AsBig() == b.AsBig();
        }

        /// <summary>sign(i - d) exactly; 2 when d is NaN (every comparison false).</summary>
        public static int Cmp(BigInteger i, double d)
        {
            if (double.IsNaN(d))
                return 2;
            if (double.IsPositiveInfinity(d))
                return -1;
            if (double.IsNegativeInfinity(d))
                return 1;
            double fl = Math.Floor(d);
            BigInteger f = new BigInteger(fl);
            int c = i.CompareTo(f);
            if (c != 0)
                return c;
            return d > fl ? -1 : 0;
        }

        /// <summary>sign(i - d) for a long.</summary>
        public static int Cmp(long i, double d)
        {
            if (double.IsNaN(d))
                return 2;
            if (d >= 9.2233720368547758e18)
                return -1;
            if (d < -9.2233720368547758e18)
                return 1;
            double fl = Math.Floor(d);
            long f = (long)fl;
            if (i != f)
                return i < f ? -1 : 1;
            return d > fl ? -1 : 0;
        }
    }
}
