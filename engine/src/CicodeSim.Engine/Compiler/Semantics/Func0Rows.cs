using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.Compiler.Semantics
{
    /// <summary>One FUNC0.DBF row as the compiler reads it.</summary>
    public sealed class Func0Row
    {
        public readonly string Name;
        public readonly int Opcode;
        public readonly string RetWord;
        public readonly List<(string kind, bool byRef)> Args;
        public readonly List<string?> Defaults;
        public readonly int Flags;
        public readonly string ArgNames;
        public readonly string Key;
        public readonly bool VarArg;
        public readonly int MinArgs;

        public Func0Row(
            string name,
            int opcode,
            string retWord,
            List<(string, bool)> args,
            List<string?> defaults,
            int flags,
            string argNames = ""
        )
        {
            Name = name;
            Opcode = opcode;
            RetWord = string.IsNullOrEmpty(retWord) ? "INT" : retWord;
            Args = args;
            Defaults = defaults;
            Flags = flags;
            ArgNames = argNames;
            Key = CitectText.Lower(name);
            foreach (var (k, _) in args)
                if (k == "VARARG")
                {
                    VarArg = true;
                    break;
                }
            int n = 0;
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i].Item1 == "VARARG")
                    break;
                string? d = i < defaults.Count ? defaults[i] : null;
                if (d == null)
                    n = i + 1;
            }
            MinArgs = n;
        }

        /// <summary>The return type code (FUNC0 'INT' is the 16-bit Int).</summary>
        public int Result => Func0Rows.TypeWord(RetWord, Types.Int16);

        /// <summary>Most arguments (VARARG: unlimited, -1).</summary>
        public int MaxArgs => VarArg ? -1 : Args.Count;

        public override string ToString() =>
            "Func0Row("
            + Name
            + "#"
            + Opcode.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ")";
    }

    /// <summary>The compiler's built-in table by lower-case name: Bin\FUNC0.DBF rows in file
    /// order, found like <see cref="CicodeSim.Compiled.Func0Table.Load"/>
    /// (a path, else $CICODESIM_FUNC0, else the install's Bin folder; an empty table when none exists).</summary>
    public sealed class Func0Rows
    {
        public readonly List<Func0Row> Rows;
        private readonly Dictionary<string, Func0Row> _byName = new Dictionary<string, Func0Row>(
            StringComparer.Ordinal
        );
        public readonly Dictionary<int, Func0Row> ByOpcode = new Dictionary<int, Func0Row>();

        public Func0Rows(List<Func0Row> rows)
        {
            Rows = rows;
            foreach (var b in rows)
                if (!_byName.ContainsKey(b.Key))
                    _byName[b.Key] = b;
            foreach (var b in rows)
                ByOpcode[b.Opcode] = b;
        }

        public Func0Row? Get(string key) => _byName.TryGetValue(key, out var b) ? b : null;

        public bool Contains(string key) => _byName.ContainsKey(key);

        public int Count => Rows.Count;

        private static readonly Dictionary<string, int> Words = new Dictionary<string, int>(
            StringComparer.Ordinal
        )
        {
            ["INT"] = Types.Int16,
            ["LONG"] = Types.Long,
            ["REAL"] = Types.Real,
            ["STRING"] = Types.String,
            ["OBJECT"] = Types.Object,
            ["TIMESTAMP"] = Types.Timestamp,
            ["QUALITY"] = Types.Quality,
            ["VOID"] = Types.Void,
            ["VARIANT"] = Types.Variant,
            ["ULONG"] = Types.ULong,
            ["DIGITAL"] = Types.Digital,
            ["UINT"] = Types.UInt,
            ["BCD"] = Types.Bcd,
            ["FUNCTION"] = Types.Void,
            ["VARARG"] = Types.VarArg,
        };

        /// <summary>A FUNC0 type word (TYPE / ARGC) as a type code.</summary>
        public static int TypeWord(string word, int dflt) => Words.TryGetValue(word, out int t) ? t : dflt;

        /// <summary>'(a,b,"x,y")' -> ['a', 'b', '"x,y"'] (parentheses and strings respected; the
        /// items are not stripped).</summary>
        public static List<string> SplitList(string text)
        {
            string t = CitectText.Trim(text);
            if (t.StartsWith("(", StringComparison.Ordinal) && t.EndsWith(")", StringComparison.Ordinal))
                t = t.Substring(1, t.Length - 2);
            var output = new List<string>();
            if (CitectText.Trim(t).Length == 0)
                return output;
            var cur = new StringBuilder();
            int depth = 0,
                i = 0;
            while (i < t.Length)
            {
                char c = t[i];
                if (c == '"')
                {
                    int j = t.IndexOf('"', i + 1);
                    if (j < 0)
                        j = t.Length - 1;
                    cur.Append(t, i, j + 1 - i);
                    i = j + 1;
                    continue;
                }
                if (c == '(')
                    depth++;
                else if (c == ')')
                    depth--;
                else if (c == ',' && depth == 0)
                {
                    output.Add(cur.ToString());
                    cur.Clear();
                    i++;
                    continue;
                }
                cur.Append(c);
                i++;
            }
            output.Add(cur.ToString());
            return output;
        }

        /// <summary>One FUNC0.DBF row (null for a blank NAME).</summary>
        public static Func0Row? ParseRow(Func<string, string> get)
        {
            string name = CitectText.Trim(get("NAME"));
            if (name.Length == 0)
                return null;
            string op = CitectText.Trim(get("OP_CODE"));
            if (op.Length == 0)
                op = "-1";
            int opcode = -1;
            long? parsed = CitectText.ParseLong(op);
            if (parsed != null)
                opcode = (int)parsed.Value;
            var args = new List<(string, bool)>();
            foreach (string a in SplitList(get("ARGC")))
            {
                string[] w = SplitWords(CitectText.Trim(a));
                if (w.Length == 0)
                    continue;
                bool byRef = CitectText.Lower(w[0]) == "var";
                string kind = CitectText.Upper(w[w.Length - 1]);
                args.Add((kind, byRef));
            }
            var defaults = new List<string?>();
            foreach (string d0 in SplitList(get("OPTARGS")))
            {
                string d = CitectText.Trim(d0);
                defaults.Add(d.Length > 0 ? d : null);
            }
            while (defaults.Count < args.Count)
                defaults.Add(null);
            string fl = CitectText.Trim(get("FLAGS"));
            if (fl.Length == 0)
                fl = "0";
            long? fv = CitectText.ParseLong(fl);
            int flags = fv != null ? (int)fv.Value : 0;
            return new Func0Row(
                name,
                opcode,
                CitectText.Upper(CitectText.Trim(get("TYPE"))),
                args,
                defaults,
                flags,
                CitectText.Trim(get("ARGNAMES"))
            );
        }

        // the words between runs of blanks
        private static string[] SplitWords(string s)
        {
            var o = new List<string>();
            int i = 0,
                n = s.Length;
            while (i < n)
            {
                while (i < n && CitectText.IsWhite(s[i]))
                    i++;
                int a = i;
                while (i < n && !CitectText.IsWhite(s[i]))
                    i++;
                if (i > a)
                    o.Add(s.Substring(a, i - a));
            }
            return o.ToArray();
        }

        private static readonly Dictionary<string, Func0Rows> Cache = new Dictionary<string, Func0Rows>(
            StringComparer.Ordinal
        );
        private static readonly object Lock = new object();

        /// <summary>Bin\FUNC0.DBF when installed, else the compiler's view of the runtime's own
        /// built-in table (data/builtins_table.json): names, OP_CODEs, argument and result types,
        /// no OPTARGS defaults, so a call must give every argument.</summary>
        public static Func0Rows LoadOrRuntime()
        {
            var t = Load();
            return t.Count > 0 ? t : FromRuntimeTable();
        }

        private static Func0Rows? _runtime;

        public static Func0Rows FromRuntimeTable()
        {
            if (_runtime != null)
                return _runtime;
            var table = CicodeSim.Compiled.Func0Table.Load(func0Path: "\u0001no FUNC0");
            var rows = new List<Func0Row>();
            foreach (var b in table.Entries)
            {
                var args = new List<(string, bool)>();
                foreach (char c in b.Args)
                    args.Add(Kind(c));
                var defaults = new List<string?>();
                foreach (var _ in args)
                    defaults.Add(null);
                rows.Add(new Func0Row(b.Name, b.Index, RetWord(b.Ret), args, defaults, 0));
            }
            return _runtime = new Func0Rows(rows);
        }

        /// <summary>A runtime signature char (chr(0x30 + type)) as FUNC0's (kind, var).</summary>
        private static (string, bool) Kind(char c)
        {
            switch (c)
            {
                case '1':
                    return ("INT", false);
                case '2':
                    return ("REAL", false);
                case '4':
                    return ("LONG", false);
                case '5':
                    return ("LONG", true);
                case '6':
                    return ("REAL", true);
                case '7':
                    return ("STRING", false);
                case '8':
                    return ("STRING", true);
                case '9':
                    return ("FUNCTION", false);
                case '<':
                    return ("OBJECT", false);
                case '=':
                    return ("VARIANT", false);
                case '?':
                    return ("VARIANT", false);
                case '@':
                    return ("VARARG", false);
                case 'C':
                    return ("TIMESTAMP", false);
                case 'D':
                    return ("QUALITY", false);
            }
            return ("INT", false);
        }

        private static string RetWord(int t)
        {
            switch (t)
            {
                case Types.Int16:
                    return "INT";
                case Types.Long:
                    return "LONG";
                case Types.Real:
                    return "REAL";
                case Types.String:
                    return "STRING";
                case Types.Void:
                    return "VOID";
                case Types.Object:
                    return "OBJECT";
                case Types.Variant:
                    return "VARIANT";
                case Types.Timestamp:
                    return "TIMESTAMP";
                case Types.Quality:
                    return "QUALITY";
                case Types.ULong:
                    return "ULONG";
            }
            return "INT";
        }

        /// <summary>FUNC0.DBF of the install (or <paramref name="path"/>); an empty table when none
        /// is found.</summary>
        public static Func0Rows Load(string? path = null)
        {
            string? p = Find(path);
            if (p == null)
                return new Func0Rows(new List<Func0Row>());
            string key = WinPath.Key(p);
            lock (Lock)
            {
                if (Cache.TryGetValue(key, out var hit))
                    return hit;
            }
            var rows = new List<Func0Row>();
            foreach (var r in CicodeSim.Compiled.Dbf.CitectRows(p))
            {
                var b = ParseRow(k => r.TryGetValue(k, out string? v) ? v : "");
                if (b != null)
                    rows.Add(b);
            }
            var t = new Func0Rows(rows);
            lock (Lock)
            {
                if (!Cache.ContainsKey(key))
                    Cache[key] = t;
                return Cache[key];
            }
        }

        /// <summary>FUNC0.DBF: <paramref name="path"/>, else $CICODESIM_FUNC0, else the install's Bin
        /// folders ($CICODESIM_SCADA_BIN, then the AVEVA / Citect install folders), else the default
        /// install path; null when none exists.</summary>
        public static string? Find(string? path = null)
        {
            foreach (string? p in new[] { path, EngineEnv.Get("FUNC0") })
                if (!string.IsNullOrEmpty(p))
                    return File.Exists(p) ? p : null;
            foreach (string d in BinDirs())
            {
                string p = Path.Combine(d, "FUNC0.DBF");
                if (File.Exists(p))
                    return p;
            }
            string def = CicodeSim.Compiled.Func0Table.Func0Default;
            return File.Exists(def) ? def : null;
        }

        private static List<string> BinDirs()
        {
            var o = new List<string>();
            string? env = EngineEnv.Get("SCADA_BIN");
            if (!string.IsNullOrEmpty(env))
            {
                o.Add(env!);
                return o;
            }
            string pf = @"C:\Program Files (x86)";
            foreach (
                var (parent, prefix) in new[]
                {
                    (pf, "AVEVA Plant SCADA"),
                    (pf + @"\AVEVA", "Plant SCADA"),
                    (pf + @"\Schneider Electric", "Citect SCADA"),
                }
            )
            {
                var found = new List<string>();
                try
                {
                    if (!Directory.Exists(parent))
                        continue;
                    foreach (string d in Directory.GetDirectories(parent))
                    {
                        string nm = Path.GetFileName(d);
                        if (
                            nm.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                            && Directory.Exists(Path.Combine(d, "Bin"))
                        )
                            found.Add(Path.Combine(d, "Bin"));
                    }
                }
                catch (Exception e)
                {
                    EngineNotes.Swallowed("Func0Rows.SiblingInstalls", e);
                }
                found.Sort(StringComparer.Ordinal);
                o.AddRange(found);
            }
            return o;
        }
    }
}
