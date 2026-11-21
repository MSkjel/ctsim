using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Compiler.Syntax;
using CicodeSim.Project;

namespace CicodeSim.Host
{
    /// <summary>Commands of the source front end: parse, funcsym, units, speed.</summary>
    internal static class FrontendCommands
    {
        internal const string DefaultUser = @"C:\ProgramData\AVEVA Plant SCADA 2023 R2\User";

        internal static string UserDir()
        {
            string? u = EngineEnv.Get("CICODE_USER");
            return string.IsNullOrEmpty(u) ? DefaultUser : u!;
        }

        internal static bool Handles(string cmd) =>
            cmd == "parse"
            || cmd == "funcsym"
            || cmd == "units"
            || cmd == "speed"
            || cmd == "fields"
            || cmd == "probes";

        internal const string Usage =
            "parse <file.ci> [--alone] [--ast] [--user dir] [--root project]\n"
            + "funcsym [UserDir | project dir] [project ...]\n"
            + "units [UserDir] [--no-tags]\n"
            + "speed [UserDir]\n"
            + "fields [UserDir | project dir] [project ...]\n"
            + "probes [cases dir] [--list] [--json out.json] [filter ...]";

        internal static int Dispatch(string[] args)
        {
            var rest = new List<string>(args);
            string cmd = rest[0];
            rest.RemoveAt(0);
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 1 << 16)
            {
                AutoFlush = false,
                NewLine = "\n",
            };
            try
            {
                switch (cmd)
                {
                    case "parse":
                        return Parse(rest, stdout);
                    case "funcsym":
                        return Funcsym(rest, stdout);
                    case "units":
                        return Units(rest, stdout);
                    case "speed":
                        return Speed(rest, stdout);
                    case "fields":
                        return Fields(rest, stdout);
                    case "probes":
                        return FrontendProbes.Run(rest, stdout);
                }
                return 2;
            }
            finally
            {
                stdout.Flush();
            }
        }

        private static string? Opt(List<string> args, string name)
        {
            int k = args.IndexOf(name);
            if (k < 0 || k + 1 >= args.Count)
                return null;
            string v = args[k + 1];
            args.RemoveRange(k, 2);
            return v;
        }

        private static bool Flag(List<string> args, string name)
        {
            int k = args.IndexOf(name);
            if (k < 0)
                return false;
            args.RemoveAt(k);
            return true;
        }

        /// <summary>A User folder (holds MASTER.DBF), or a project folder whose parent is one: (user
        /// dir, project names given by the folder).</summary>
        private static (string user, List<string> names) UserAndProjects(List<string> args)
        {
            string user = UserDir();
            var names = new List<string>();
            foreach (string a in args)
            {
                if (Directory.Exists(a))
                {
                    if (Unit0Find(a, "master.dbf") != null)
                        user = a;
                    else
                    {
                        string full = WinPath.Full(a);
                        user = WinPath.DirName(full);
                        names.Add(WinPath.BaseName(full));
                    }
                }
                else
                    names.Add(a);
            }
            return (user, names);
        }

        private static string? Unit0Find(string folder, string lower)
        {
            var names = WinPath.ListDir(folder);
            if (names == null)
                return null;
            foreach (string fn in names)
                if (CitectText.Lower(fn) == lower)
                    return WinPath.Join(folder, fn);
            return null;
        }

        private static string F2(double s) => s.ToString("0.00", CultureInfo.InvariantCulture);

        private static string F1(double s) => s.ToString("0.0", CultureInfo.InvariantCulture);

        // ---------------------------------------------------------------------------- parse
        /// <summary>parse &lt;file.ci&gt;: the file's diagnostics as JSON. A file inside a registered
        /// project is compiled in that project's unit (its labels, functions, tags); --alone parses
        /// it on its own (no labels).</summary>
        internal static int Parse(List<string> args, TextWriter o)
        {
            bool alone = Flag(args, "--alone");
            bool ast = Flag(args, "--ast");
            string? userOpt = Opt(args, "--user");
            string? rootOpt = Opt(args, "--root");
            if (args.Count < 1)
            {
                Console.Error.WriteLine(
                    "usage: ctsim-dev parse <file.ci> [--alone] [--ast] [--user dir] [--root project]"
                );
                return 2;
            }
            string path = WinPath.Full(args[0]);
            if (!File.Exists(path))
            {
                Console.Error.WriteLine("no such file: " + path);
                return 1;
            }
            List<Diagnostic> diags;
            Module? mod = null;
            string unitName = "";
            CompileUnit? unit = null;
            SourceFile? sf = null;
            if (!alone)
            {
                string projDir = WinPath.DirName(path);
                string user = userOpt ?? WinPath.DirName(projDir);
                var reg = new Registry(user);
                SourceProject? proj = null;
                foreach (var p in reg.All)
                    if (p.Folder != null && WinPath.Key(p.Folder) == WinPath.Key(projDir))
                    {
                        proj = p;
                        break;
                    }
                if (proj != null)
                {
                    // the file's own project as the root, or a project that includes it (--root)
                    string root = rootOpt ?? proj.Name;
                    var (order, odiags) = reg.CompileOrder(root);
                    unit = new CompileUnit(order, null, odiags).Compile(stop: false);
                    foreach (var f in unit.Files)
                        if (WinPath.Key(f.Path) == WinPath.Key(path))
                        {
                            sf = f;
                            break;
                        }
                    if (unit.OrderDiags.Exists(d => d.Code == 4016 || d.Code == 4017))
                    {
                        // the unit does not compile (an include project not found, a cycle): what
                        // the compiler reports is that, not the file's messages
                        unitName = root;
                        diags = new List<Diagnostic>(unit.OrderDiags);
                        WriteParse(o, path, unitName, diags);
                        if (ast)
                            foreach (string ln in AstDump.Lines(null, "ast"))
                                o.WriteLine(ln);
                        return 0;
                    }
                    else if (sf != null)
                        unitName = root;
                    else if (rootOpt != null)
                    {
                        Console.Error.WriteLine(
                            "ctsim-dev parse: " + path + " is not compiled in the unit of " + rootOpt
                        );
                        return 1;
                    }
                }
                else if (rootOpt != null)
                {
                    Console.Error.WriteLine(
                        "ctsim-dev parse: " + path + " is not in a project registered in " + user
                    );
                    return 1;
                }
            }
            if (sf != null)
            {
                mod = sf.Module;
                diags = new List<Diagnostic>(sf.SortedPass(1));
                diags.AddRange(sf.SortedPass(2));
            }
            else
            {
                string text = Lexer.SourceText(File.ReadAllBytes(path));
                mod = Parser.ParseSource(text, null, path, "");
                var f = new SourceFile(
                    new SourceProject("", null, new List<KeyValuePair<string, string>>()),
                    WinPath.BaseName(path),
                    path,
                    mod
                );
                diags = new List<Diagnostic>(f.SortedPass(1));
                diags.AddRange(f.SortedPass(2));
            }
            WriteParse(o, path, unitName, diags);
            if (ast)
                foreach (string ln in AstDump.Lines(mod, "ast"))
                    o.WriteLine(ln);
            return 0;
        }

        /// <summary>The parse command's JSON line; unit null when the file was parsed on its own.</summary>
        private static void WriteParse(TextWriter o, string path, string unitName, List<Diagnostic> diags)
        {
            var sb = new StringBuilder();
            sb.Append("{\"file\":");
            AstDump.Str(sb, path);
            sb.Append(",\"unit\":");
            AstDump.Str(sb, unitName.Length > 0 ? unitName : null);
            sb.Append(",\"diagnostics\":[");
            for (int i = 0; i < diags.Count; i++)
            {
                var d = diags[i];
                if (i > 0)
                    sb.Append(',');
                sb.Append("{\"code\":")
                    .Append(d.Code.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"name\":");
                AstDump.Str(sb, d.Name);
                sb.Append(",\"line\":").Append(d.Line.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"col\":").Append(d.Col.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"context\":");
                AstDump.Str(sb, d.Context);
                sb.Append(",\"message\":");
                AstDump.Str(sb, d.Message);
                sb.Append('}');
            }
            sb.Append("]}");
            o.WriteLine(sb.ToString());
        }

        // -------------------------------------------------------------------------- funcsym
        private static bool SameRow(DbfRow want, List<KeyValuePair<string, string>> got)
        {
            if (want.FieldCount != got.Count)
                return false;
            var names = want.Header.Names;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < names.Length; i++)
                seen.Add(names[i]);
            if (seen.Count != got.Count)
                return false;
            foreach (var kv in got)
                if (!want.Has(kv.Key) || want.Get(kv.Key) != kv.Value)
                    return false;
            return true;
        }

        /// <summary>funcsym: the source-built signature table vs every compiled _FUNCSYM.DBF (rows
        /// equal, in order).</summary>
        private static int Funcsym(List<string> args, TextWriter o)
        {
            var (user, names) = UserAndProjects(args);
            var reg = new Registry(user);
            if (names.Count == 0)
            {
                var all = new List<string>();
                foreach (var p in reg.All)
                    if (p.Folder != null && File.Exists(Path.Combine(p.Folder, "_FUNCSYM.DBF")))
                        all.Add(p.Name);
                all.Sort(StringComparer.Ordinal);
                names = all;
            }
            int totalOk = 0;
            foreach (string root in names)
            {
                var p = reg.Get(root);
                if (p == null || p.Folder == null)
                {
                    o.WriteLine(root + ": not registered");
                    continue;
                }
                string path = Path.Combine(p.Folder, "_FUNCSYM.DBF");
                var sw = Stopwatch.StartNew();
                var unit = CompileUnit.ForProject(root, user).Compile();
                double dt = sw.Elapsed.TotalSeconds;
                var want = Dbf.CitectRows(path);
                var got = unit.Funcsym();
                int equal = 0;
                for (int i = 0; i < Math.Min(want.Count, got.Count); i++)
                    if (SameRow(want[i], got[i]))
                        equal++;
                long newest = 0;
                foreach (var pr in unit.Projects)
                foreach (var (_, fp) in pr.CiFiles())
                {
                    var st = WinPath.Stat(fp);
                    if (st != null && st.Value.mtime > newest)
                        newest = st.Value.mtime;
                }
                var fst = WinPath.Stat(path);
                bool stale = fst != null && newest > fst.Value.mtime;
                var errs = unit.Errors();
                bool exact = equal == want.Count && want.Count == got.Count;
                if (exact)
                    totalOk++;
                o.WriteLine(
                    $"{root}: built {got.Count} rows, _FUNCSYM {want.Count} rows, equal in order {equal}"
                        + (exact ? "  EXACT" : "")
                        + (stale ? "  (stale: sources newer than _FUNCSYM)" : "")
                        + $"  errors {errs.Count}  {F2(dt)} s"
                );
                if (!exact)
                {
                    var wn = new Dictionary<string, DbfRow>(StringComparer.Ordinal);
                    foreach (var r in want)
                        wn[CitectText.Lower(r.Get("NAME"))] = r;
                    var gn = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var r in got)
                        gn.Add(CitectText.Lower(r[0].Value));
                    int onlyBuilt = 0,
                        onlyWant = 0;
                    foreach (string k in gn)
                        if (!wn.ContainsKey(k))
                            onlyBuilt++;
                    foreach (string k in wn.Keys)
                        if (!gn.Contains(k))
                            onlyWant++;
                    o.WriteLine($"    only built {onlyBuilt}, only _FUNCSYM {onlyWant}");
                    int shown = 0;
                    for (int i = 0; i < Math.Min(want.Count, got.Count) && shown < 5; i++)
                    {
                        if (SameRow(want[i], got[i]))
                            continue;
                        shown++;
                        var sb = new StringBuilder();
                        foreach (var kv in got[i])
                            sb.Append(kv.Key).Append('=').Append(kv.Value).Append(' ');
                        o.WriteLine(
                            $"    row {i}: _FUNCSYM {want[i].Get("NAME")} {want[i].Get("ARGC")} {want[i].Get("TYPE")} | built {sb}"
                        );
                    }
                }
                for (int i = 0; i < Math.Min(5, errs.Count); i++)
                    o.WriteLine("     " + errs[i]);
            }
            o.WriteLine($"exact tables: {totalOk}/{names.Count}");
            return totalOk == names.Count ? 0 : 1;
        }

        // ---------------------------------------------------------------------------- units
        /// <summary>units: every registered project compiled as the root of its unit. A ROOT is a
        /// project no other project includes (what Studio compiles); a library compiled on its own
        /// may legitimately fail where it uses its includers' functions or labels.</summary>
        private static int Units(List<string> args, TextWriter o)
        {
            bool useTags = !Flag(args, "--no-tags");
            var (user, _) = UserAndProjects(args);
            var reg = new Registry(user);
            var included = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in reg.All)
                if (p.Folder != null && Directory.Exists(p.Folder))
                    foreach (string n in p.IncludeRows())
                        included.Add(CitectText.Lower(CitectText.Trim(n)));
            var clean = new HashSet<string>(StringComparer.Ordinal);
            var allfiles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in reg.All)
            foreach (var (_, fp) in p.CiFiles())
                allfiles.Add(WinPath.CaseKey(fp));
            int badRoots = 0;
            var sw = Stopwatch.StartNew();
            var projects = new List<SourceProject>(reg.All);
            projects = SortByLowerName(projects);
            foreach (var p in projects)
            {
                if (p.Folder == null || !Directory.Exists(p.Folder))
                    continue;
                var (order, diags) = reg.CompileOrder(p.Name);
                var unit = new CompileUnit(order, null, diags, useTags).Compile();
                var errs = unit.Errors();
                if (errs.Count == 0)
                    foreach (var f in unit.Files)
                        clean.Add(WinPath.CaseKey(f.Path));
                var warns = new List<Diagnostic>();
                foreach (var d in unit.CompilerView())
                    if (!d.IsError)
                        warns.Add(d);
                bool root = !included.Contains(p.Key) && p.Key != "include" && p.Key != "system";
                if (errs.Count > 0 && root)
                    badRoots++;
                var codes = new List<string>();
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var d in warns)
                {
                    if (!counts.ContainsKey(d.Name))
                    {
                        counts[d.Name] = 0;
                        codes.Add(d.Name);
                    }
                    counts[d.Name]++;
                }
                var cs = new StringBuilder("{");
                for (int i = 0; i < codes.Count; i++)
                    cs.Append(i > 0 ? ", " : "")
                        .Append('\'')
                        .Append(codes[i])
                        .Append("': ")
                        .Append(counts[codes[i]]);
                cs.Append('}');
                o.WriteLine(
                    $"{p.Name}{(root ? " (root)" : "")}: {unit.Projects.Count} projects, {unit.Files.Count} files, "
                        + $"{unit.Functions.Count} functions, errors {errs.Count}, warnings {cs}"
                );
                for (int i = 0; i < Math.Min(6, errs.Count); i++)
                    o.WriteLine("     " + errs[i]);
            }
            int inClean = 0;
            foreach (string f in allfiles)
                if (clean.Contains(f))
                    inClean++;
            o.WriteLine(
                $"roots with errors: {badRoots}; .ci files in an error-free unit {inClean}/{allfiles.Count}; "
                    + $"{F1(sw.Elapsed.TotalSeconds)} s"
            );
            return badRoots == 0 ? 0 : 1;
        }

        internal static List<SourceProject> SortByLowerName(List<SourceProject> ps)
        {
            var idx = new List<(string k, int i, SourceProject p)>();
            for (int i = 0; i < ps.Count; i++)
                idx.Add((CitectText.Lower(ps[i].Name), i, ps[i]));
            idx.Sort(
                (a, b) =>
                {
                    int c = string.CompareOrdinal(a.k, b.k);
                    return c != 0 ? c : a.i.CompareTo(b.i);
                }
            );
            var o = new List<SourceProject>();
            foreach (var t in idx)
                o.Add(t.p);
            return o;
        }

        // ---------------------------------------------------------------------------- speed
        /// <summary>speed: lex + parse every top-level .ci file of the User folder (with its own
        /// project's unit's labels).</summary>
        private static int Speed(List<string> args, TextWriter o)
        {
            var (user, _) = UserAndProjects(args);
            int rounds = 1;
            string? r = Opt(args, "--rounds");
            if (r != null)
                rounds = int.Parse(r, CultureInfo.InvariantCulture);
            var reg = new Registry(user);
            var jobs = new List<(string fp, SourceProject p, LabelTable lab)>();
            foreach (var p in SortByLowerName(new List<SourceProject>(reg.All)))
            {
                if (p.Folder == null || p.CiFiles().Count == 0)
                    continue;
                var (order, _) = reg.CompileOrder(p.Name);
                var u = new CompileUnit(order, null, null, false);
                u.LoadLabels();
                foreach (var (_, fp) in p.CiFiles())
                    jobs.Add((fp, p, u.Labels));
            }
            for (int round = 0; round < rounds; round++)
            {
                var sw = Stopwatch.StartNew();
                var texts = new List<(string fp, SourceProject p, LabelTable lab, string text)>();
                foreach (var (fp, p, lab) in jobs)
                    texts.Add((fp, p, lab, p.Text(WinPath.BaseName(fp), fp)));
                double t1 = sw.Elapsed.TotalSeconds;
                long lines = 0;
                foreach (var t in texts)
                {
                    int c = 1;
                    foreach (char ch in t.text)
                        if (ch == '\n')
                            c++;
                    lines += c;
                }
                var lexed = new List<(string fp, SourceProject p, LexResult lex)>();
                double t1b = sw.Elapsed.TotalSeconds;
                foreach (var (fp, p, lab, text) in texts)
                    lexed.Add((fp, p, Lexer.Tokenize(text, lab, fp, "ci", p.Name)));
                double t2 = sw.Elapsed.TotalSeconds;
                int errors = 0;
                foreach (var (fp, p, lex) in lexed)
                {
                    var m = new Parser(lex, fp, p.Name).ParseModule();
                    foreach (var d in m.Diagnostics)
                        if (d.IsError)
                            errors++;
                }
                double t3 = sw.Elapsed.TotalSeconds;
                o.WriteLine(
                    $"{texts.Count} files, {lines} lines: read {F2(t1)} s, lex {F2(t2 - t1b)} s, "
                        + $"parse {F2(t3 - t2)} s, total {F2(t3 - (t1b - t1))} s; syntax errors {errors}"
                );
            }
            return 0;
        }

        // --------------------------------------------------------------------------- fields
        internal static readonly (string table, string kind, string[] cols)[] PageFields =
        {
            (
                "pgdynobj.dbf",
                "expression",
                new[]
                {
                    "SZX",
                    "SZY",
                    "ROT",
                    "MVX",
                    "MVY",
                    "VISIBLE",
                    "COL_A",
                    "COL_B",
                    "COL_C",
                    "COL_D",
                    "COL_E",
                    "TXT_A",
                    "TXT_B",
                    "TXT_C",
                    "TXT_D",
                    "TXT_E",
                    "TXT_STR",
                    "FIL",
                    "DISABLED",
                }
            ),
            ("pgadv.dbf", "command", new[] { "CMD" }),
            ("pgbutton.dbf", "command", new[] { "CMD" }),
            ("pgkey.dbf", "command", new[] { "CMD" }),
            ("pgtouch.dbf", "command", new[] { "CMD", "DOWN_CMD", "UP_CMD", "REPEAT_CMD" }),
        };

        /// <summary>(record number from 1, deleted, {FIELD: value}) of every row, deleted ones too
        /// (errlog.DBF E_REC counts them).</summary>
        internal static IEnumerable<(int rec, bool deleted, Dictionary<string, string> row)> DbfRecords(
            string path
        )
        {
            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (Exception)
            {
                yield break;
            }
            if (data.Length < 32)
                yield break;
            uint nrec = BitConverter.ToUInt32(data, 4);
            int hlen = BitConverter.ToUInt16(data, 8);
            int rlen = BitConverter.ToUInt16(data, 10);
            var fields = new List<(string name, int pos, int len)>();
            int off = 32,
                pos = 1;
            while (off + 32 <= hlen && off < data.Length && data[off] != 0x0D)
            {
                int z = 0;
                while (z < 11 && off + z < data.Length && data[off + z] != 0)
                    z++;
                var nb = new StringBuilder();
                for (int i = 0; i < z; i++)
                    nb.Append(data[off + i] < 0x80 ? (char)data[off + i] : '\uFFFD');
                string name = CitectText.Upper(CitectText.Trim(nb.ToString()));
                int ln = off + 16 < data.Length ? data[off + 16] : 0;
                fields.Add((name, pos, ln));
                pos += ln;
                off += 32;
            }
            for (long k = 0; k < nrec; k++)
            {
                long o = hlen + k * rlen;
                if (o + rlen > data.Length)
                    yield break;
                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (name, a, ln) in fields)
                {
                    int start = (int)o + Math.Min(a, rlen);
                    int n = Math.Max(0, Math.Min(ln, rlen - a));
                    row[name] = CitectText.Trim(TextCodec.Decode(data, start, n));
                }
                yield return ((int)(k + 1), rlen > 0 && data[o] == (byte)'*', row);
            }
        }

        private static readonly Regex ErrlogCode = new Regex(
            @"\((\w\d+)\)\s*$",
            RegexOptions.CultureInvariant
        );

        /// <summary>fields: every page field of a project's pg*.DBF tables through CompileField,
        /// against the page messages of its errlog.DBF (the last compile's): per (table, record,
        /// field) the same codes in the same order.</summary>
        private static int Fields(List<string> args, TextWriter o)
        {
            var (user, names) = UserAndProjects(args);
            if (names.Count == 0)
                names = new List<string> { "Example" };
            var reg = new Registry(user);
            int bad = 0;
            foreach (string root in names)
            {
                var p = reg.Get(root);
                if (p == null || p.Folder == null)
                {
                    o.WriteLine(root + ": not registered");
                    bad++;
                    continue;
                }
                string? log = Unit0Find(p.Folder, "errlog.dbf");
                var recorded = new Dictionary<(string, int, string), List<string>>();
                var recordedOrder = new List<(string, int, string)>();
                (string t, int rec, string page)? cut = null;
                if (log != null)
                {
                    foreach (var (_, _, r) in DbfRecords(log))
                    {
                        string t = CitectText.Lower(WinPath.BaseName(Get(r, "E_TABLE")));
                        if (!IsPageTable(t))
                            continue;
                        var m = ErrlogCode.Match(Get(r, "E_ERROR"));
                        if (!m.Success)
                            continue;
                        string recs = Get(r, "E_REC");
                        int rec = recs.Length > 0 ? (int)(CitectText.ParseLong(recs) ?? 0) : 0;
                        var key = (t, rec, Get(r, "E_FIELD"));
                        if (!recorded.TryGetValue(key, out var lst))
                        {
                            recorded[key] = lst = new List<string>();
                            recordedOrder.Add(key);
                        }
                        lst.Add(m.Groups[1].Value);
                        cut = (t, rec, Get(r, "E_PAGE"));
                    }
                }
                var cutTables = new HashSet<string>(StringComparer.Ordinal);
                if (cut != null)
                    foreach (var k in recordedOrder)
                        cutTables.Add(k.Item1);
                var sw = Stopwatch.StartNew();
                var unit = CompileUnit.Build(root, user);
                int n = 0,
                    same = 0,
                    after = 0;
                var diff =
                    new List<(
                        (string, int, string) key,
                        string page,
                        List<string> want,
                        List<string> got,
                        string text
                    )>();
                var oursTotal = new Dictionary<string, int>(StringComparer.Ordinal);
                var oursOrder = new List<string>();
                var seen = new HashSet<(string, int, string)>();
                foreach (var (table, kind, cols) in PageFields)
                {
                    string? path = Unit0Find(p.Folder, table);
                    if (path == null)
                        continue;
                    foreach (var (recno, deleted, row) in DbfRecords(path))
                    {
                        if (deleted)
                            continue;
                        foreach (string col in cols)
                        {
                            string text = row.TryGetValue(col, out string? tv) ? tv : "";
                            if (text.Length == 0)
                                continue;
                            n++;
                            var res = unit.CompileField(
                                text,
                                kind,
                                root,
                                table + ":" + recno.ToString(CultureInfo.InvariantCulture) + ":" + col
                            );
                            var got = new List<string>();
                            foreach (var d in res.Diagnostics)
                            {
                                got.Add(d.Name);
                                if (!oursTotal.ContainsKey(d.Name))
                                {
                                    oursTotal[d.Name] = 0;
                                    oursOrder.Add(d.Name);
                                }
                                oursTotal[d.Name]++;
                            }
                            var key = (table, recno, col);
                            seen.Add(key);
                            var want = recorded.TryGetValue(key, out var w) ? w : new List<string>();
                            string page = row.TryGetValue("PAGE", out string? pg) ? pg : "";
                            if (SameList(got, want))
                                same++;
                            else if (
                                cut != null
                                && page == cut.Value.page
                                && want.Count == 0
                                && (
                                    (table == cut.Value.t && recno > cut.Value.rec)
                                    || !cutTables.Contains(table)
                                )
                            )
                                after++;
                            else
                                diff.Add((key, page, want, got, text));
                        }
                    }
                }
                int unseen = 0,
                    recMsgs = 0;
                foreach (var k in recordedOrder)
                {
                    if (!seen.Contains(k))
                        unseen++;
                    recMsgs += recorded[k].Count;
                }
                var ot = new StringBuilder("{");
                for (int i = 0; i < oursOrder.Count; i++)
                    ot.Append(i > 0 ? ", " : "")
                        .Append('\'')
                        .Append(oursOrder[i])
                        .Append("': ")
                        .Append(oursTotal[oursOrder[i]]);
                ot.Append('}');
                o.WriteLine(
                    $"{root}: {n} page fields, the same messages {same}, different {diff.Count}, "
                        + $"ours only after the compile's last message (in {(cut != null ? cut.Value.page : "no page")}) {after}; "
                        + $"errlog page messages {recMsgs} on {recordedOrder.Count} fields ({unseen} not found in the tables), "
                        + $"ours {ot}; {F1(sw.Elapsed.TotalSeconds)} s"
                );
                for (int i = 0; i < Math.Min(8, diff.Count); i++)
                {
                    var x = diff[i];
                    string txt = x.text.Length > 90 ? x.text.Substring(0, 90) : x.text;
                    o.WriteLine(
                        $"    {x.key} {x.page}: errlog [{string.Join(", ", x.want)}] ours [{string.Join(", ", x.got)}] | {txt}"
                    );
                }
                if (diff.Count > 0)
                    bad++;
            }
            return bad == 0 ? 0 : 1;
        }

        private static bool IsPageTable(string t)
        {
            foreach (var pf in PageFields)
                if (pf.table == t)
                    return true;
            return false;
        }

        private static string Get(Dictionary<string, string> r, string k) =>
            r.TryGetValue(k, out string? v) ? v : "";

        private static bool SameList(List<string> a, List<string> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        // ------------------------------------------------------------------- units and fields
        /// <summary>Every registered project of the User folder with a folder (by lower-case name,
        /// or the named ones by key), compiled as the root of its unit with the tag tables: the
        /// units the golden tests freeze.</summary>
        internal static IEnumerable<(SourceProject Project, CompileUnit Unit)> CiUnits(
            string user,
            ICollection<string> only
        )
        {
            var reg = new Registry(user);
            foreach (var p in SortByLowerName(new List<SourceProject>(reg.All)))
            {
                if (p.Folder == null || !Directory.Exists(p.Folder))
                    continue;
                if (only.Count > 0 && !only.Contains(p.Key))
                    continue;
                var (order, diags) = reg.CompileOrder(p.Name);
                yield return (p, new CompileUnit(order, null, diags, true).Compile());
            }
        }

        /// <summary>Every Cicode field of the pg*.DBF page tables of the named projects (default
        /// Example, ExampleSA, Test), compiled against the project's unit: the project, where
        /// (table:record:column) and the result.</summary>
        internal static IEnumerable<(string Root, string Where, FieldResult Result)> FieldResults(
            string user,
            IList<string> projects
        )
        {
            var reg = new Registry(user);
            var names = new List<string>(projects);
            if (names.Count == 0)
                names.AddRange(new[] { "Example", "ExampleSA" });
            foreach (string root in names)
            {
                var p = reg.Get(root);
                if (p == null || p.Folder == null)
                    continue;
                var unit = CompileUnit.Build(root, user);
                foreach (var (table, kind, cols) in PageFields)
                {
                    string? path = Unit0Find(p.Folder, table);
                    if (path == null)
                        continue;
                    foreach (var (recno, deleted, row) in DbfRecords(path))
                    {
                        if (deleted)
                            continue;
                        foreach (string col in cols)
                        {
                            string text = row.TryGetValue(col, out string? tv) ? tv : "";
                            if (text.Length == 0)
                                continue;
                            string where =
                                table + ":" + recno.ToString(CultureInfo.InvariantCulture) + ":" + col;
                            yield return (root, where, unit.CompileField(text, kind, root, where));
                        }
                    }
                }
            }
        }
    }
}
