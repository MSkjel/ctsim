using System;
using System.Collections.Generic;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>A built-in implementation: (task, arguments) -&gt; the raw result (a value whose
    /// payload the VM converts to the declared return type, or Value.Nothing).</summary>
    public delegate Value BuiltinImpl(CicodeTask task, ArgList args);

    /// <summary>The simulator's built-in functions, registered by name and called by FUNC0 index.</summary>
    public sealed class Registry : IBuiltins
    {
        private static readonly object Lock = new object();
        private static Dictionary<string, (BuiltinImpl Fn, string Source)>? _impls;
        private static List<string>? _order;

        public readonly Func0Table Table;
        private readonly BuiltinImpl?[] _byIndex;
        public readonly Dictionary<string, int> Calls = new Dictionary<string, int>(StringComparer.Ordinal);

        public Registry(Func0Table? table = null, IDictionary<string, BuiltinImpl>? overrides = null)
        {
            Load();
            Table = table ?? Func0Table.Default;
            int max = -1;
            foreach (var b in Table.Entries)
                if (b.Index > max)
                    max = b.Index;
            _byIndex = new BuiltinImpl?[max + 1];
            foreach (string low in _order!)
                Install(low, _impls![low].Fn);
            if (overrides != null)
                foreach (var kv in overrides)
                    Install(kv.Key.ToLowerInvariant(), kv.Value);
        }

        private static List<string>? _duplicates;

        /// <summary>Names the built-in modules registered more than once (the last one wins;
        /// a test keeps this empty so a second registration is never an accident).</summary>
        internal static List<string> Duplicates()
        {
            Load();
            return new List<string>(_duplicates!);
        }

        private static void Load()
        {
            if (_impls != null)
                return;
            lock (Lock)
            {
                if (_impls != null)
                    return;
                var d = new Dictionary<string, (BuiltinImpl, string)>(StringComparer.Ordinal);
                var order = new List<string>();
                var dup = new List<string>();
                void Add(string name, string source, BuiltinImpl fn)
                {
                    string low = name.ToLowerInvariant();
                    if (!d.ContainsKey(low))
                        order.Add(low);
                    else
                        dup.Add(name + " (" + d[low].Item2 + ", then " + source + ")");
                    d[low] = (fn, source);
                }
                // maths, state, strings in this order: a name registered twice keeps its first
                // position and its last implementation
                Maths.Register(Add);
                State.Register(Add);
                Strings.Register(Add);
                _order = order;
                _duplicates = dup;
                _impls = d;
            }
        }

        private void Install(string low, BuiltinImpl fn)
        {
            var b = Table.Lookup(low);
            if (b != null && b.Index >= 0 && b.Index < _byIndex.Length)
                _byIndex[b.Index] = fn;
        }

        public void Register(string name, BuiltinImpl fn) => Install(name.ToLowerInvariant(), fn);

        public List<string> Implemented()
        {
            var r = new List<string>();
            for (int i = 0; i < _byIndex.Length; i++)
                if (_byIndex[i] != null && Table.Get(i) != null)
                    r.Add(Table.Get(i)!.Name);
            r.Sort(StringComparer.Ordinal);
            return r;
        }

        public bool IsImplemented(string name)
        {
            var b = Table.Lookup(name);
            return b != null && b.Index < _byIndex.Length && _byIndex[b.Index] != null;
        }

        public bool IsIndexImplemented(int index) =>
            index >= 0 && index < _byIndex.Length && _byIndex[index] != null;

        // --- IBuiltins -------------------------------------------------------------------------
        public Value Call(int index, CicodeTask task, ArgList args)
        {
            BuiltinImpl? fn = index >= 0 && index < _byIndex.Length ? _byIndex[index] : null;
            if (fn == null)
                return Value.NotSimulated(Name(index), index);
            return fn(task, args);
        }

        public string Name(int index) => Table.Name(index);

        /// <summary>Where a built-in's behaviour comes from (fact tag).</summary>
        public static string Source(string name)
        {
            Load();
            return _impls!.TryGetValue(name.ToLowerInvariant(), out var e) ? e.Source : "";
        }

        /// <summary>The pure and interpreter-state built-ins (strings, maths, error state, globals).</summary>
        public static Registry Default(IDictionary<string, BuiltinImpl>? overrides = null) =>
            new Registry(null, overrides);
    }

    /// <summary>Argument access for built-in implementations (C-side views of VM values).</summary>
    public static class A
    {
        public const string Spaces = " \t\n\v\f\r";

        public static Value Arg(ArgList a, int k) => a.Get(k);

        /// <summary>The contents of a Cicode byte buffer, including embedded NULs.</summary>
        public static string Buffer(in Value v) => Value.Deref(v).S ?? "";

        /// <summary>A string argument as the C code sees it (up to the first NUL).</summary>
        public static string S(in Value v)
        {
            if (v.IsNothing)
                return "";
            if (v.T == Types.String)
                return CutAtNul(v.S ?? "");
            Value d = v.T == Types.Variant ? Value.Deref(v) : v;
            string x = d.T == Types.String ? (d.S ?? "") : Conv.ValueText(d, out _);
            return CutAtNul(x);
        }

        // the last strings known to hold no NUL (a loop passes the same string again and
        // again; strings are immutable, so a stale entry is still true)
        private static string? _noNul0,
            _noNul1;

        /// <summary>The string up to its first NUL (the C view of a buffer).</summary>
        public static string CutAtNul(string x)
        {
            if (ReferenceEquals(x, _noNul0) || ReferenceEquals(x, _noNul1))
                return x;
            int k = x.IndexOf('\0');
            if (k >= 0)
                return x.Substring(0, k);
            if (x.Length > 16)
            {
                _noNul1 = _noNul0;
                _noNul0 = x;
            }
            return x;
        }

        private static readonly string[] OneChar = BuildOneChar();

        private static string[] BuildOneChar()
        {
            var a = new string[256];
            for (int c = 0; c < 256; c++)
                a[c] = ((char)c).ToString();
            return a;
        }

        /// <summary>s.Substring(start, len) with the one-character strings shared.</summary>
        public static string Sub(string s, int start, int len)
        {
            if (len == 1)
            {
                char c = s[start];
                return c < 256 ? OneChar[c] : c.ToString();
            }
            return s.Substring(start, len);
        }

        /// <summary>An INT / LONG argument (32-bit).</summary>
        public static long I(in Value v0)
        {
            if (v0.T == Types.Long)
                return (int)v0.I;
            if (v0.IsNothing)
                return 0;
            Value v = v0.T == Types.Variant ? Value.Deref(v0) : v0;
            if (Types.TypeClass(v.T) == Types.ClassInt)
                return (int)v.I;
            if (Types.IsRealType(v.T))
                return Conv.RoundHalfAway(v.D, out long r) ? r : 0;
            if (v.T == Types.String)
                return Conv.StrToInt(v.S, out _);
            return v.PayloadIsInt ? (int)v.I : 0;
        }

        /// <summary>A REAL argument.</summary>
        public static double R(in Value v0)
        {
            if (v0.T == Types.Real)
                return v0.D;
            if (v0.IsNothing)
                return 0.0;
            Value v = v0.T == Types.Variant ? Value.Deref(v0) : v0;
            if (Types.IsRealType(v.T))
                return v.D;
            if (Types.TypeClass(v.T) == Types.ClassInt)
                return (int)v.I;
            if (v.T == Types.String)
                return Conv.ParseReal(v.S, out _);
            return 0.0;
        }

        /// <summary>A QUALITY / TIMESTAMP argument (64-bit).</summary>
        public static ulong U64(in Value v0)
        {
            if (v0.IsNothing)
                return 0;
            Value v = v0.T == Types.Variant ? Value.Deref(v0) : v0;
            return v.PayloadIsInt ? (ulong)v.I : 0;
        }

        /// <summary>The low 16 bits as a signed C short.</summary>
        public static long Short(long x) => (short)(x & 0xFFFF);

        public static long UShort(long x) => x & 0xFFFF;

        /// <summary>_strupr in the C locale: only a..z change.</summary>
        public static string CUpper(string x)
        {
            char[]? c = null;
            for (int i = 0; i < x.Length; i++)
            {
                char ch = x[i];
                if (ch >= 'a' && ch <= 'z')
                {
                    c ??= x.ToCharArray();
                    c[i] = (char)(ch - 32);
                }
            }
            return c == null ? x : new string(c);
        }

        public static string CLower(string x)
        {
            char[]? c = null;
            for (int i = 0; i < x.Length; i++)
            {
                char ch = x[i];
                if (ch >= 'A' && ch <= 'Z')
                {
                    c ??= x.ToCharArray();
                    c[i] = (char)(ch + 32);
                }
            }
            return c == null ? x : new string(c);
        }

        public static bool IsSpace(char c) => Spaces.IndexOf(c) >= 0;

        public static string SkipSpace(string x) => Conv.SkipSpace(x);

        public static string Cap(string x) =>
            x.Length > Types.StringMax ? x.Substring(0, Types.StringMax) : x;

        /// <summary>s from index a to b (clamped; a negative index counts from the end).</summary>
        public static string Slice(string s, long a, long b)
        {
            long n = s.Length;
            if (a < 0)
            {
                a += n;
                if (a < 0)
                    a = 0;
            }
            if (b < 0)
            {
                b += n;
                if (b < 0)
                    b = 0;
            }
            if (a > n)
                a = n;
            if (b > n)
                b = n;
            return b <= a ? "" : s.Substring((int)a, (int)(b - a));
        }

        /// <summary>s from index a to its end.</summary>
        public static string From(string s, long a) => Slice(s, a, s.Length);

        /// <summary>s up to index b.</summary>
        public static string To(string s, long b) => Slice(s, 0, b);

        /// <summary>Write a by-reference argument.</summary>
        public static void Assign(CicodeTask task, Value r, Value value) => task.Vm.AssignRef(r, value);

        public static void Assign(CicodeTask task, Value r, string value) =>
            task.Vm.AssignRef(r, Value.Str(value));

        public static void Assign(CicodeTask task, Value r, long value) =>
            task.Vm.AssignRef(r, Value.Long((int)value));

        public static Value Int(long v) => Value.Long(v);

        public static Value Real(double v) => Value.Real(v);

        public static Value Str(string v) => Value.Str(v);
    }
}
