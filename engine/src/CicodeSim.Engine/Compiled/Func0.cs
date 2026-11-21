using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using CicodeSim.Values;

namespace CicodeSim.Compiled
{
    /// <summary>One built-in: the runtime table's entry merged with its FUNC0.DBF row.</summary>
    public sealed class Builtin
    {
        public int Index;

        /// <summary>The runtime table's name (what a call resolves to).</summary>
        public string Name = "";

        /// <summary>Runtime argument type chars.</summary>
        public string Args = "";

        /// <summary>Runtime return type code.</summary>
        public int Ret;

        /// <summary>The function's address in the runtime.</summary>
        public string Address = "";
        public string Func0Name = "";
        public string Func0Type = "";
        public string[] Func0Args = Array.Empty<string>();

        /// <summary>FUNC0 OPTARGS per argument ('' = mandatory).</summary>
        public string[] OptArgs = Array.Empty<string>();
        public string ArgNames = "";
        public string Flags = "";

        public int NArgs => Args.Length;

        /// <summary>Arguments without a default (FUNC0 OPTARGS).</summary>
        public int MinArgs
        {
            get
            {
                int n = Args.Length;
                while (n > 0 && n - 1 < OptArgs.Length && OptArgs[n - 1] != "")
                    n--;
                return n;
            }
        }

        public bool ReturnsInt16 => Ret == Types.Int16;

        /// <summary>Argument i is passed by reference ('var' in FUNC0, chars 5 / 6 / 8).</summary>
        public bool ByRef(int i)
        {
            if (i >= Args.Length)
                return false;
            int t = Types.SigType(Args[i]);
            return t == Types.LongBcd || t == Types.LongReal || t == Types.Byte;
        }
    }

    /// <summary>The built-in function table: FUNC0.DBF plus the runtime's own table.</summary>
    public sealed class Func0Table
    {
        public const string Func0Default = @"C:\Program Files (x86)\AVEVA Plant SCADA\Bin\FUNC0.DBF";

        public readonly List<Builtin> Entries;
        private readonly Dictionary<int, Builtin> _byIndex = new Dictionary<int, Builtin>();
        private readonly Dictionary<string, Builtin> _byName = new Dictionary<string, Builtin>(
            StringComparer.Ordinal
        );

        public Func0Table(List<Builtin> entries)
        {
            Entries = entries;
            foreach (var b in entries)
                _byIndex[b.Index] = b;
            foreach (var b in entries)
                AddName(b.Name, b);
            foreach (var b in entries)
                if (b.Func0Name.Length > 0)
                    AddName(b.Func0Name, b);
        }

        private void AddName(string n, Builtin b)
        {
            string k = n.ToLowerInvariant();
            if (!_byName.ContainsKey(k))
                _byName[k] = b;
        }

        public int Count => Entries.Count;

        public Builtin? Get(int index) => _byIndex.TryGetValue(index, out var b) ? b : null;

        public Builtin? Lookup(string name) =>
            _byName.TryGetValue(name.ToLowerInvariant(), out var b) ? b : null;

        public string Name(int index) => _byIndex.TryGetValue(index, out var b) ? b.Name : "builtin#" + index;

        /// <summary>The embedded runtime table (always) merged with FUNC0.DBF when it can be read
        /// (<paramref name="func0Path"/>, else $CICODESIM_FUNC0, else the install's Bin folder).</summary>
        public static Func0Table Load(string? func0Path = null, string? tableJson = null)
        {
            string text = tableJson ?? EmbeddedText("builtins_table.json");
            var rows = (List<object?>)Json.Parse(text)!;
            var f0 = new Dictionary<long, Dictionary<string, string>>();
            string? path = func0Path;
            if (string.IsNullOrEmpty(path))
                path = EngineEnv.Get("FUNC0");
            if (string.IsNullOrEmpty(path))
                path = Func0Default;
            if (File.Exists(path))
            {
                foreach (var r in Dbf.CitectRows(path!))
                {
                    if (r.TryGetValue("OP_CODE", out var op) && Dbf.ParseInt(op, out long k))
                        f0[k] = r;
                }
            }
            var entries = new List<Builtin>();
            foreach (var o in rows)
            {
                var r = (Dictionary<string, object?>)o!;
                int idx = (int)(long)r["i"]!;
                f0.TryGetValue(idx, out var d);
                string G(string key) => d != null && d.TryGetValue(key, out var v) ? v : "";
                entries.Add(
                    new Builtin
                    {
                        Index = idx,
                        Name = (string)r["name"]!,
                        Args = (string)r["args"]!,
                        Ret = (int)(long)r["ret"]!,
                        Address = r.TryGetValue("fn", out var fn) && fn is string s ? s : "",
                        Func0Name = G("NAME"),
                        Func0Type = G("TYPE"),
                        Func0Args = SplitArgs(G("ARGC")),
                        OptArgs = SplitArgs(G("OPTARGS")),
                        ArgNames = G("ARGNAMES"),
                        Flags = G("FLAGS"),
                    }
                );
            }
            return new Func0Table(entries);
        }

        private static Func0Table? _table;
        private static readonly object TableLock = new object();

        /// <summary>The process-wide table (loaded once).</summary>
        public static Func0Table Default
        {
            get
            {
                var t = _table;
                if (t != null)
                    return t;
                lock (TableLock)
                {
                    return _table ??= Load();
                }
            }
        }

        /// <summary>'(STRING, var LONG)' -> ['STRING', 'var LONG']; commas inside quotes kept.</summary>
        public static string[] SplitArgs(string s)
        {
            s = Dbf.Trim(s);
            if (s.StartsWith("(", StringComparison.Ordinal) && s.EndsWith(")", StringComparison.Ordinal))
                s = s.Substring(1, s.Length - 2);
            if (Dbf.Trim(s).Length == 0)
                return Array.Empty<string>();
            var output = new List<string>();
            var cur = new StringBuilder();
            bool q = false;
            foreach (char ch in s)
            {
                if (ch == '"')
                    q = !q;
                if (ch == ',' && !q)
                {
                    output.Add(Dbf.Trim(cur.ToString()));
                    cur.Clear();
                }
                else
                    cur.Append(ch);
            }
            output.Add(Dbf.Trim(cur.ToString()));
            return output.ToArray();
        }

        /// <summary>A 16-bit built-in result as the VM pushes it: (int)(short)v.</summary>
        public static long SignExtend16(long v) => (short)(v & 0xFFFF);

        private static readonly Dictionary<string, int> WordTypes = new Dictionary<string, int>(
            StringComparer.Ordinal
        )
        {
            ["INT"] = Types.Int16,
            ["LONG"] = Types.Long,
            ["REAL"] = Types.Real,
            ["STRING"] = Types.String,
            ["VOID"] = Types.Void,
            ["OBJECT"] = Types.Object,
            ["VARIANT"] = Types.Variant,
            ["TIMESTAMP"] = Types.Timestamp,
            ["QUALITY"] = Types.Quality,
            ["FUNCTION"] = Types.Void,
            ["VARARG"] = Types.VarArg,
        };

        private static readonly Dictionary<string, int> VarTypes = new Dictionary<string, int>(
            StringComparer.Ordinal
        )
        {
            ["LONG"] = Types.LongBcd,
            ["REAL"] = Types.LongReal,
            ["STRING"] = Types.Byte,
            ["INT"] = Types.LongBcd,
        };

        /// <summary>A FUNC0 type word ('INT', 'var LONG', 'VAR STRING' ...) as a type code.</summary>
        public static int WordType(string word)
        {
            string w = Dbf.Trim(word);
            if (w.ToLowerInvariant().StartsWith("var ", StringComparison.Ordinal))
                return VarTypes.TryGetValue(Dbf.Trim(w.Substring(4)).ToUpperInvariant(), out int v)
                    ? v
                    : Types.Variant;
            return WordTypes.TryGetValue(w.ToUpperInvariant(), out int t) ? t : Types.Void;
        }

        internal static string EmbeddedText(string name)
        {
            var asm = typeof(Func0Table).GetTypeInfo().Assembly;
            using (var st = asm.GetManifestResourceStream("CicodeSim.Data." + name))
            {
                if (st == null)
                    throw new FileNotFoundException("embedded resource missing: " + name);
                using (var rd = new StreamReader(st, new UTF8Encoding(false)))
                    return rd.ReadToEnd();
            }
        }
    }
}
