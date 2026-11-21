using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Compiler.Syntax;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.Host
{
    /// <summary>The front end against the recorded CtCmp32 verdicts of the probe corpus.</summary>
    internal static class FrontendProbes
    {
        /// <summary>The probe workspace (CICODESIM_PROBES): cases\, conf_run\fresh.jsonl,
        /// rtproj\CiSimRT.</summary>
        internal static readonly string Root = Environment.GetEnvironmentVariable("CICODESIM_PROBES")
            is { Length: > 0 } r
            ? r
            : "probes";
        internal static string Cases = Path.Combine(Root, "cases");
        internal static readonly string Fresh = Path.Combine(Root, "conf_run", "fresh.jsonl");
        internal static readonly string Export = FindExport();

        /// <summary>conformance\compile of the repository this ctsim or test assembly was built in
        /// (a worktree, or the submodule in an app, reads its own copy).</summary>
        private static string FindExport()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                string p = Path.Combine(d.FullName, "conformance", "compile");
                if (Directory.Exists(p) && File.Exists(Path.Combine(d.FullName, "engine", "CicodeSim.sln")))
                    return p;
            }
            return Path.Combine("conformance", "compile");
        }

        private static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.Ordinal)
        {
            "harness",
            "refs_fixture",
            "_privtest",
        };
        private static readonly HashSet<string> Baseline = new HashSet<string>(StringComparer.Ordinal)
        {
            "E2079",
            "E2117",
            "W1001",
            "F4028",
            "W1018",
        };

        // ------------------------------------------------------------------------ text helpers
        /// <summary>The lines of a text: each ends at \r\n, \n or \r (a final line break makes no
        /// empty line).</summary>
        internal static List<string> SplitLines(string s)
        {
            var o = new List<string>();
            int i = 0,
                n = s.Length,
                start = 0;
            while (i < n)
            {
                char c = s[i];
                if (c != '\n' && c != '\r')
                {
                    i++;
                    continue;
                }
                o.Add(s.Substring(start, i - start));
                if (c == '\r' && i + 1 < n && s[i + 1] == '\n')
                    i++;
                i++;
                start = i;
            }
            if (start < n)
                o.Add(s.Substring(start));
            return o;
        }

        private static readonly HashSet<char> Cp1252Special = BuildSpecial();

        private static HashSet<char> BuildSpecial()
        {
            var h = new HashSet<char>();
            for (int b = 0x80; b < 0xA0; b++)
            {
                if (b == 0x81 || b == 0x8D || b == 0x8F || b == 0x90 || b == 0x9D)
                    continue;
                h.Add(TextCodec.Decode1252[b]);
            }
            return h;
        }

        /// <summary>What the probe recorder writes: the lines + CRLF, cp1252 with '?' for what it cannot hold,
        /// read back the way the compiler reads it.</summary>
        internal static string DiskText(List<string> lines)
        {
            var sb = new StringBuilder();
            for (int k = 0; k < lines.Count; k++)
            {
                if (k > 0)
                    sb.Append("\r\n");
                string ln = lines[k];
                for (int i = 0; i < ln.Length; i++)
                {
                    char c = ln[i];
                    if (c < 0x80 || (c >= 0xA0 && c <= 0xFF) || Cp1252Special.Contains(c))
                        sb.Append(c);
                    else
                    {
                        sb.Append('?');
                        if (char.IsHighSurrogate(c) && i + 1 < ln.Length && char.IsLowSurrogate(ln[i + 1]))
                            i++;
                    }
                }
            }
            sb.Append("\r\n");
            string s = sb.ToString();
            // the input ends at a NUL or Ctrl-Z (source_text)
            return Lexer.CutInput(s);
        }

        /// <summary>The words between runs of blanks.</summary>
        private static List<string> SplitWs(string s)
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
            return o;
        }

        private static bool IsWord(char c)
        {
            if (c == '_' || char.IsLetterOrDigit(c))
                return true;
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            return cat == UnicodeCategory.LetterNumber || cat == UnicodeCategory.OtherNumber;
        }

        private static int SkipSpace(string s, int i)
        {
            while (i < s.Length && CitectText.IsWhite(s[i]))
                i++;
            return i;
        }

        /// <summary>'^////\s*KEY:' at the start of a line: the offset after the colon, -1 when it is
        /// not there.</summary>
        private static int Marker(string ln, string key)
        {
            if (!ln.StartsWith("////", StringComparison.Ordinal))
                return -1;
            int i = SkipSpace(ln, 4);
            if (string.CompareOrdinal(ln, i, key, 0, key.Length) != 0)
                return -1;
            i += key.Length;
            if (i >= ln.Length || ln[i] != ':')
                return -1;
            return i + 1;
        }

        /// <summary>^(\w+(?:\s*\([^)]*\))?)\s*=\s*(.*)$ from <paramref name="i"/>: (name, expr).</summary>
        private static bool LabelSpec(string s, int i, out string name, out string expr)
        {
            name = expr = "";
            int a = i;
            while (i < s.Length && IsWord(s[i]))
                i++;
            if (i == a)
                return false;
            int afterWord = i;
            int j = SkipSpace(s, i);
            int end = afterWord;
            if (j < s.Length && s[j] == '(')
            {
                int k = s.IndexOf(')', j + 1);
                if (k >= 0)
                {
                    int q = SkipSpace(s, k + 1);
                    if (q < s.Length && s[q] == '=')
                        end = k + 1;
                }
            }
            int e = SkipSpace(s, end);
            if (e >= s.Length || s[e] != '=')
                return false;
            name = s.Substring(a, end - a);
            expr = CitectText.Trim(s.Substring(SkipSpace(s, e + 1)));
            return true;
        }

        internal sealed class OrderedFiles
        {
            public readonly List<string> Keys = new List<string>();
            public readonly Dictionary<string, List<string>> Map = new Dictionary<string, List<string>>(
                StringComparer.Ordinal
            );

            public void Set(string k, List<string> v)
            {
                if (!Map.ContainsKey(k))
                    Keys.Add(k);
                Map[k] = v;
            }

            public List<KeyValuePair<string, string>> Texts()
            {
                var o = new List<KeyValuePair<string, string>>();
                foreach (string k in Keys)
                    o.Add(new KeyValuePair<string, string>(k, DiskText(Map[k])));
                return o;
            }
        }

        internal static (
            List<KeyValuePair<string, string>> files,
            List<KeyValuePair<string, string>> labels
        ) SplitSingle(string text)
        {
            var files = new OrderedFiles();
            var labels = new List<KeyValuePair<string, string>>();
            string cur = "probe.ci";
            var buf = new List<string>();
            foreach (string ln in SplitLines(text))
            {
                int m = Marker(ln, "FILE");
                if (m >= 0)
                {
                    int a = SkipSpace(ln, m);
                    int b = a;
                    while (b < ln.Length && !CitectText.IsWhite(ln[b]))
                        b++;
                    if (b > a && SkipSpace(ln, b) == ln.Length)
                    {
                        if (buf.Count > 0 || cur != "probe.ci")
                            files.Set(cur, buf);
                        cur = ln.Substring(a, b - a);
                        buf = new List<string>();
                        continue;
                    }
                }
                m = Marker(ln, "LABEL");
                if (m >= 0 && LabelSpec(ln, SkipSpace(ln, m), out string nm, out string ex))
                {
                    labels.Add(new KeyValuePair<string, string>(nm, ex));
                    continue;
                }
                buf.Add(ln);
            }
            files.Set(cur, buf);
            return (files.Texts(), labels);
        }

        internal sealed class MultiSpec
        {
            public readonly OrderedFiles Files = new OrderedFiles();
            public readonly List<KeyValuePair<string, string>> Labels =
                new List<KeyValuePair<string, string>>();
            public readonly List<string> Includes = new List<string>();
            public readonly List<KeyValuePair<string, string>> Locvars =
                new List<KeyValuePair<string, string>>();
        }

        private static readonly string[] Marks =
        {
            "PROJECT",
            "INCLUDE",
            "FILE",
            "LABEL",
            "COMPILE",
            "LOCVAR",
        };

        internal static (
            Dictionary<string, MultiSpec> projects,
            List<string> order,
            List<string> targets
        ) SplitMulti(string text)
        {
            var projects = new Dictionary<string, MultiSpec>(StringComparer.Ordinal);
            var order = new List<string>();
            var targets = new List<string>();
            string? cur = null,
                curfile = null;
            var buf = new List<string>();

            void Flush()
            {
                if (cur != null && curfile != null)
                    projects[cur].Files.Set(curfile, buf);
            }

            foreach (string ln in SplitLines(text))
            {
                string? kind = null;
                int m = -1;
                foreach (string k in Marks)
                {
                    m = Marker(ln, k);
                    if (m >= 0)
                    {
                        kind = k;
                        break;
                    }
                }
                if (kind == null)
                {
                    buf.Add(ln);
                    continue;
                }
                string arg = CitectText.Trim(ln.Substring(m));
                if (kind == "COMPILE")
                {
                    targets.Add(arg);
                    continue;
                }
                if (kind == "PROJECT")
                {
                    Flush();
                    cur = arg;
                    curfile = null;
                    buf = new List<string>();
                    if (!projects.ContainsKey(cur))
                        projects[cur] = new MultiSpec();
                    order.Add(cur);
                    continue;
                }
                if (cur == null)
                    continue;
                if (kind == "FILE")
                {
                    Flush();
                    curfile = arg;
                    buf = new List<string>();
                }
                else if (kind == "INCLUDE")
                    projects[cur].Includes.Add(arg);
                else if (kind == "LABEL")
                {
                    if (LabelSpec(arg, 0, out string nm, out string ex))
                        projects[cur].Labels.Add(new KeyValuePair<string, string>(nm, ex));
                }
                else if (kind == "LOCVAR")
                {
                    var parts = SplitWs(arg);
                    if (parts.Count > 0)
                        projects[cur]
                            .Locvars.Add(
                                new KeyValuePair<string, string>(parts[0], parts.Count > 1 ? parts[1] : "")
                            );
                }
            }
            Flush();
            if (targets.Count == 0 && order.Count > 0)
                targets.Add(order[0]);
            return (projects, order, targets);
        }

        internal static string RegName(string name) =>
            name.StartsWith("=", StringComparison.Ordinal) ? name.Substring(1) : "CiP_" + name;

        // -------------------------------------------------------------------------------- rig
        internal sealed class Outcome
        {
            public bool Ok;
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Warnings = new List<string>();
            public readonly List<(string code, string file, int line, string context)> Detail =
                new List<(string, string, int, string)>();
            public readonly List<Diagnostic> View = new List<Diagnostic>();
        }

        /// <summary>The probe rig: real System + Include, virtual probe projects.</summary>
        internal sealed class Rig
        {
            private readonly SourceProject? _system;
            private readonly SourceProject? _include;
            public readonly Dictionary<string, Module> Cache = new Dictionary<string, Module>(
                StringComparer.Ordinal
            );
            private readonly Func0Rows _func0 = Func0Rows.Load();

            public Rig(string user)
            {
                var real = new Registry(user);
                _system = real.Get("System");
                _include = real.Get("Include");
            }

            private Registry NewRegistry()
            {
                var reg = new Registry(null);
                if (_system != null)
                    reg.Add(new SourceProject(_system.Name, _system.Folder));
                if (_include != null)
                    reg.Add(new SourceProject(_include.Name, _include.Folder));
                return reg;
            }

            public Dictionary<string, Outcome> RunSingle(string text)
            {
                var (files, labels) = SplitSingle(text);
                var reg = NewRegistry();
                reg.Add(
                    new SourceProject(
                        "CiProbe",
                        null,
                        files,
                        labels,
                        new List<string>(),
                        new List<KeyValuePair<string, string>>(),
                        new Dictionary<string, int>()
                    )
                );
                return new Dictionary<string, Outcome> { [""] = Compile("CiProbe", reg) };
            }

            public Dictionary<string, Outcome> RunMulti(string text)
            {
                var (projects, order, targets) = SplitMulti(text);
                var reg = NewRegistry();
                foreach (string p in order)
                {
                    var spec = projects[p];
                    var incl = new List<string>();
                    foreach (string inc in spec.Includes)
                    {
                        if (inc.StartsWith("=*", StringComparison.Ordinal))
                            continue; // xprobe: a deleted INCLUDE.DBF row
                        incl.Add(RegName(inc));
                    }
                    reg.Add(
                        new SourceProject(
                            RegName(p),
                            null,
                            spec.Files.Texts(),
                            spec.Labels,
                            incl,
                            spec.Locvars,
                            new Dictionary<string, int>()
                        )
                    );
                }
                var o = new Dictionary<string, Outcome>(StringComparer.Ordinal);
                foreach (string t in targets)
                    o[RegName(t)] = Compile(RegName(t), reg);
                return o;
            }

            private Outcome Compile(string root, Registry reg)
            {
                var (order, diags) = reg.CompileOrder(root);
                var unit = new CompileUnit(order, _func0, diags, false, Cache);
                unit.Compile();
                var res = new Outcome();
                foreach (var d in unit.CompilerView())
                {
                    if (Baseline.Contains(d.Name))
                        continue;
                    res.View.Add(d);
                    if (d.IsError)
                        res.Errors.Add(d.Name);
                    else
                        res.Warnings.Add(d.Name);
                    res.Detail.Add((d.Name, WinPath.BaseName(d.File), d.Line, d.Context));
                }
                res.Ok = res.Errors.Count == 0;
                return res;
            }
        }

        // --------------------------------------------------------------------------- verdicts
        internal sealed class Entry
        {
            public string Path = "";
            public string Source = "";
            public readonly List<string> TargetOrder = new List<string>();
            public readonly Dictionary<string, Dictionary<string, object?>> Verdicts = new Dictionary<
                string,
                Dictionary<string, object?>
            >(StringComparer.Ordinal);

            public void Set(string target, Dictionary<string, object?> v)
            {
                if (!Verdicts.ContainsKey(target))
                    TargetOrder.Add(target);
                Verdicts[target] = v;
            }

            public bool Multi
            {
                get
                {
                    foreach (string t in TargetOrder)
                        if (t.Length > 0)
                            return true;
                    return false;
                }
            }
        }

        private static string Norm(string p) => WinPath.Key(p);

        /// <summary>os.walk top-down: (dirpath, dirnames, filenames) in listdir order; the caller
        /// may empty dirnames to prune.</summary>
        private static IEnumerable<(string dp, List<string> dns, List<string> fns)> Walk(string top)
        {
            var names = WinPath.ListDir(top);
            if (names == null)
                yield break;
            var dns = new List<string>();
            var fns = new List<string>();
            foreach (string n in names)
            {
                if (Directory.Exists(Path.Combine(top, n)))
                    dns.Add(n);
                else
                    fns.Add(n);
            }
            yield return (top, dns, fns);
            foreach (string d in new List<string>(dns))
            {
                if (!dns.Contains(d))
                    continue;
                foreach (var x in Walk(WinPath.Join(top, d)))
                    yield return x;
            }
        }

        private static string RelPath(string path, string start)
        {
            string a = WinPath.Key(path),
                b = WinPath.Key(start);
            string full = WinPath.Full(path);
            if (a.StartsWith(b + "\\", StringComparison.Ordinal))
                return full.Substring(WinPath.Full(start).Length + 1);
            return path;
        }

        /// <summary>norm path to path of every probe .ci under the cases folder (SkipDirs and the
        /// 'projects' / 'dump' / 'ext' copy folders left out).</summary>
        internal static Dictionary<string, string> ProbeFiles()
        {
            var o = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (dp, dns, fns) in Walk(Cases))
            {
                string rel = RelPath(dp, Cases);
                string[] segs = dp == Cases ? new[] { "." } : rel.Split('\\');
                string top = segs[0];
                bool skip = SkipDirs.Contains(top);
                for (int i = 1; i < segs.Length && !skip; i++)
                    if (
                        segs[i] == "projects"
                        || segs[i] == "dump"
                        || segs[i] == "ext"
                        || segs[i] == "__pycache__"
                        || segs[i] == "vfy"
                    )
                        skip = true;
                if (skip)
                {
                    dns.Clear();
                    continue;
                }
                foreach (string fn in fns)
                {
                    if (CitectText.Lower(fn).EndsWith(".ci", StringComparison.Ordinal))
                    {
                        string p = WinPath.Join(dp, fn);
                        o[Norm(p)] = p;
                    }
                }
            }
            return o;
        }

        private static Dictionary<string, Dictionary<string, Dictionary<string, object?>>> Stored(
            Dictionary<string, string> probes,
            Dictionary<string, List<string>> targetOrder
        )
        {
            var o = new Dictionary<string, Dictionary<string, Dictionary<string, object?>>>(
                StringComparer.Ordinal
            );
            var files = new List<(long mt, int i, string p)>();
            int idx = 0;
            foreach (var (dp, _, fns) in Walk(Cases))
            foreach (string fn in fns)
                if (
                    fn.EndsWith(".json", StringComparison.Ordinal)
                    || fn.EndsWith(".jsonl", StringComparison.Ordinal)
                )
                {
                    string p = WinPath.Join(dp, fn);
                    var st = WinPath.Stat(p);
                    files.Add((st?.mtime ?? 0, idx++, p));
                }
            files.Sort(
                (a, b) =>
                {
                    int c = a.mt.CompareTo(b.mt);
                    return c != 0 ? c : a.i.CompareTo(b.i);
                }
            );
            foreach (var (_, _, src) in files)
            {
                string text;
                try
                {
                    text = Encoding.UTF8.GetString(File.ReadAllBytes(src));
                }
                catch (Exception)
                {
                    continue;
                }
                string d = WinPath.DirName(src);
                foreach (string raw in SplitLines(TextCodec.UniversalNewlines(text)))
                {
                    string ln = CitectText.Trim(raw);
                    if (!ln.StartsWith("{", StringComparison.Ordinal))
                        continue;
                    object? obj;
                    try
                    {
                        obj = Json.Parse(ln);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (
                        !(obj is Dictionary<string, object?> ob)
                        || !ob.TryGetValue("probe", out var pr)
                        || !ob.ContainsKey("errors")
                    )
                        continue;
                    string probe = pr as string ?? (pr?.ToString() ?? "");
                    string target = ob.TryGetValue("compile", out var cv) && cv is string cs ? cs : "";
                    foreach (string c in new[] { probe, WinPath.Join(d, probe), WinPath.Join(Cases, probe) })
                    {
                        string k = Norm(c);
                        if (probes.ContainsKey(k))
                        {
                            if (!o.TryGetValue(k, out var m))
                            {
                                o[k] = m = new Dictionary<string, Dictionary<string, object?>>(
                                    StringComparer.Ordinal
                                );
                                targetOrder[k] = new List<string>();
                            }
                            if (!m.ContainsKey(target))
                                targetOrder[k].Add(target);
                            m[target] = ob;
                            break;
                        }
                    }
                }
            }
            return o;
        }

        private static byte[]? TextKey(string p)
        {
            try
            {
                byte[] b = File.ReadAllBytes(p);
                var o = new List<byte>(b.Length);
                for (int i = 0; i < b.Length; i++)
                {
                    if (b[i] == '\r' && i + 1 < b.Length && b[i + 1] == '\n')
                        continue;
                    o.Add(b[i]);
                }
                int n = o.Count;
                while (n > 0 && o[n - 1] == '\n')
                    n--;
                return o.GetRange(0, n).ToArray();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private sealed class BytesKey : IEqualityComparer<byte[]>
        {
            public bool Equals(byte[]? a, byte[]? b)
            {
                if (a == null || b == null)
                    return a == b;
                if (a.Length != b.Length)
                    return false;
                for (int i = 0; i < a.Length; i++)
                    if (a[i] != b[i])
                        return false;
                return true;
            }

            public int GetHashCode(byte[] a)
            {
                unchecked
                {
                    int h = (int)2166136261;
                    foreach (byte x in a)
                        h = (h ^ x) * 16777619;
                    return h;
                }
            }
        }

        /// <summary>norm probe path to its entry; a single-project probe has the one target ''.
        /// Fresh verdicts win over stored ones.</summary>
        internal static Dictionary<string, Entry> LoadVerdicts()
        {
            var probes = ProbeFiles();
            var targetOrder = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var stored = Stored(probes, targetOrder);
            var o = new Dictionary<string, Entry>(StringComparer.Ordinal);
            var keyOrder = new List<string>();

            void Put(string k, Entry e)
            {
                if (!o.ContainsKey(k))
                    keyOrder.Add(k);
                o[k] = e;
            }

            foreach (var kv in stored)
            {
                var e = new Entry { Path = probes[kv.Key], Source = "stored" };
                foreach (string t in targetOrder[kv.Key])
                    e.Set(t, kv.Value[t]);
                Put(kv.Key, e);
            }
            if (Directory.Exists(Export))
            {
                foreach (var (dp, _, fns) in Walk(Export))
                {
                    foreach (string fn in fns)
                    {
                        if (!fn.EndsWith(".json", StringComparison.Ordinal) || fn == "index.json")
                            continue;
                        string rel = RelPath(
                            WinPath.Join(dp, fn.Substring(0, fn.Length - 5) + ".ci"),
                            Export
                        );
                        string k = Norm(WinPath.Join(Cases, rel));
                        if (!probes.ContainsKey(k))
                            continue;
                        object? obj;
                        try
                        {
                            obj = Json.Parse(
                                Encoding.UTF8.GetString(File.ReadAllBytes(WinPath.Join(dp, fn)))
                            );
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        if (!(obj is Dictionary<string, object?> ob))
                            continue;
                        var e = new Entry { Path = probes[k], Source = "export" };
                        if (ob.TryGetValue("compiles", out var cs) && cs is List<object?> lst)
                        {
                            foreach (var c in lst)
                                if (c is Dictionary<string, object?> cd)
                                    e.Set(
                                        cd.TryGetValue("compile", out var cn) ? cn as string ?? "" : "",
                                        cd
                                    );
                        }
                        else
                            e.Set("", ob);
                        Put(k, e);
                    }
                }
            }
            if (File.Exists(Fresh))
            {
                var extra = new List<(string k, Dictionary<string, object?> v)>();
                string all = Encoding.UTF8.GetString(File.ReadAllBytes(Fresh));
                foreach (string ln in TextCodec.UniversalNewlines(all).Split('\n'))
                {
                    object? obj;
                    try
                    {
                        obj = Json.Parse(ln);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (!(obj is Dictionary<string, object?> v))
                        continue;
                    string cse = v.TryGetValue("case", out var cv) ? cv as string ?? "" : "";
                    string k = Norm(WinPath.Join(Cases, cse));
                    if (probes.ContainsKey(k))
                    {
                        var e = new Entry { Path = probes[k], Source = "fresh" };
                        e.Set("", v);
                        Put(k, e);
                    }
                    else if (File.Exists(k))
                        extra.Add((k, v));
                }
                if (extra.Count > 0)
                {
                    // the fresh rig also compiled single files of the copy folders alone: those whose
                    // text is no counted probe are verdicts of their own
                    var seen = new HashSet<byte[]>(new BytesKey());
                    foreach (string p in probes.Values)
                    {
                        var tk = TextKey(p);
                        if (tk != null)
                            seen.Add(tk);
                    }
                    foreach (var (k, v) in extra)
                    {
                        var tk = TextKey(k);
                        if (tk != null && seen.Add(tk))
                        {
                            var e = new Entry { Path = k, Source = "fresh-extra" };
                            e.Set("", v);
                            Put(k, e);
                        }
                    }
                }
            }
            var ordered = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (string k in keyOrder)
                ordered[k] = o[k];
            return ordered;
        }

        private static List<string> Codes(
            Dictionary<string, object?> verdict,
            string key,
            out List<(string code, string file, long line)> at
        )
        {
            var o = new List<string>();
            at = new List<(string, string, long)>();
            if (verdict.TryGetValue(key, out var e) && e is List<object?> lst)
            {
                foreach (var x in lst)
                {
                    if (!(x is Dictionary<string, object?> d))
                        continue;
                    string code = d.TryGetValue("code", out var c) ? c as string ?? "" : "";
                    if (Baseline.Contains(code))
                        continue;
                    o.Add(code);
                    string file = d.TryGetValue("file", out var f) ? f as string ?? "" : "";
                    long line = d.TryGetValue("line", out var l) && l is long ll ? ll : 0;
                    at.Add((code, CitectText.Lower(WinPath.BaseName(file)), line));
                }
            }
            return o;
        }

        private static bool Same(List<string> a, List<string> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        private static List<string> Sorted(List<string> a)
        {
            var o = new List<string>(a);
            o.Sort(StringComparer.Ordinal);
            return o;
        }

        private static List<(string key, Entry e)> SortedEntries(Dictionary<string, Entry> verdicts)
        {
            var items = new List<(string key, Entry e, string low, int i)>();
            int idx = 0;
            foreach (var kv in verdicts)
                items.Add((kv.Key, kv.Value, CitectText.Lower(kv.Value.Path), idx++));
            items.Sort(
                (a, b) =>
                {
                    int c = string.CompareOrdinal(a.low, b.low);
                    return c != 0 ? c : a.i.CompareTo(b.i);
                }
            );
            var o = new List<(string, Entry)>();
            foreach (var t in items)
                o.Add((t.key, t.e));
            return o;
        }

        private static string? ReadProbe(string path)
        {
            try
            {
                string t = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
                return TextCodec.UniversalNewlines(t);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>probes [cases dir] [--list] [--json out.json] [filter ...]: probes with a
        /// verdict, accept / reject agreement, first-error-code agreement, and the disagreements
        /// grouped by (recorded first code, our first code).</summary>
        internal static int Run(List<string> args, TextWriter o)
        {
            bool asList = args.Remove("--list");
            string? outJson = null;
            int ji = args.IndexOf("--json");
            if (ji >= 0 && ji + 1 < args.Count)
            {
                outJson = args[ji + 1];
                args.RemoveRange(ji, 2);
            }
            if (args.Count > 0 && Directory.Exists(args[0]))
            {
                Cases = WinPath.Full(args[0]);
                args.RemoveAt(0);
            }
            var filters = new List<string>();
            foreach (string a in args)
                filters.Add(CitectText.Lower(a));
            var verdicts = LoadVerdicts();
            var rig = new Rig(FrontendCommands.UserDir());
            var stats = new Dictionary<string, int>(StringComparer.Ordinal);
            void Inc(string k) => stats[k] = (stats.TryGetValue(k, out int v) ? v : 0) + 1;
            int S(string k) => stats.TryGetValue(k, out int v) ? v : 0;
            var groups = new Dictionary<(string, string), List<string>>();
            var groupOrder = new List<(string, string)>();
            var results = new StringBuilder();
            int nresults = 0;
            var sw = Stopwatch.StartNew();
            foreach (var (_, e) in SortedEntries(verdicts))
            {
                string path = e.Path;
                string rel = RelPath(path, Cases);
                if (filters.Count > 0)
                {
                    bool hit = false;
                    foreach (string f in filters)
                        if (CitectText.Lower(rel).Contains(f))
                        {
                            hit = true;
                            break;
                        }
                    if (!hit)
                        continue;
                }
                string? text = ReadProbe(path);
                if (text == null)
                {
                    Inc("unreadable");
                    continue;
                }
                bool multi = text.Contains("//// PROJECT:");
                Dictionary<string, Outcome> ours;
                try
                {
                    ours = multi ? rig.RunMulti(text) : rig.RunSingle(text);
                }
                catch (Exception exc)
                {
                    Inc("crash");
                    var gk = ("CRASH", exc.GetType().Name);
                    if (!groups.ContainsKey(gk))
                    {
                        groups[gk] = new List<string>();
                        groupOrder.Add(gk);
                    }
                    groups[gk].Add(rel);
                    continue;
                }
                foreach (string target in e.TargetOrder)
                {
                    var rec = e.Verdicts[target];
                    Outcome? mine = Mine(ours, target, multi);
                    if (mine == null)
                    {
                        Inc("no_target");
                        continue;
                    }
                    var recErr = Codes(rec, "errors", out var recAt);
                    bool recOk = recErr.Count == 0;
                    Inc("compiles");
                    bool sameOk = recOk == mine.Ok;
                    Inc(sameOk ? "agree" : "disagree");
                    string rFirst = recErr.Count > 0 ? recErr[0] : "OK";
                    string mFirst = mine.Errors.Count > 0 ? mine.Errors[0] : "OK";
                    if (!recOk && !mine.Ok)
                    {
                        Inc(rFirst == mFirst ? "first_code_same" : "first_code_diff");
                        if (new HashSet<string>(recErr).SetEquals(mine.Errors))
                            Inc("code_set_same");
                        if (Same(Sorted(recErr), Sorted(mine.Errors)))
                            Inc("code_list_same");
                        if (Same(recErr, mine.Errors))
                        {
                            Inc("code_order_same");
                            var ourAt = new List<(string, string, long)>();
                            foreach (var (c, f, ln, _) in mine.Detail)
                                if (c[0] != 'W')
                                    ourAt.Add((c, CitectText.Lower(f), ln));
                            bool linesSame = recAt.Count == ourAt.Count;
                            for (int i = 0; linesSame && i < recAt.Count; i++)
                                linesSame = recAt[i].Equals(ourAt[i]);
                            if (linesSame)
                                Inc("lines_same");
                        }
                    }
                    if (rFirst != mFirst)
                    {
                        var gk = (rFirst, mFirst);
                        if (!groups.ContainsKey(gk))
                        {
                            groups[gk] = new List<string>();
                            groupOrder.Add(gk);
                        }
                        groups[gk].Add(rel + (target.Length > 0 ? " [" + target + "]" : ""));
                    }
                    var recWarn = Codes(rec, "warnings", out _);
                    if (!recOk && !mine.Ok && Same(Sorted(recWarn), Sorted(mine.Warnings)))
                        Inc("reject_warnings_same");
                    if (recOk && mine.Ok)
                    {
                        Inc("both_accept");
                        if (Same(Sorted(recWarn), Sorted(mine.Warnings)))
                            Inc("warnings_same");
                    }
                    nresults++;
                    if (outJson != null)
                    {
                        results.Append(nresults > 1 ? ",\n" : "");
                        results.Append("{\"case\":");
                        AstDump.Str(results, rel);
                        results.Append(",\"target\":");
                        AstDump.Str(results, target);
                        results
                            .Append(",\"recorded\":[")
                            .Append(string.Join(",", Quoted(recErr)))
                            .Append("],\"ours\":[")
                            .Append(string.Join(",", Quoted(mine.Errors)))
                            .Append("],\"agree\":")
                            .Append(sameOk ? "true" : "false")
                            .Append('}');
                    }
                }
            }
            double dt = sw.Elapsed.TotalSeconds;
            o.WriteLine(
                $"compiles with a verdict: {S("compiles")} ({nresults} results, {S("crash")} crashes, "
                    + $"{S("no_target")} without a matching target), {dt.ToString("0.0", CultureInfo.InvariantCulture)} s"
            );
            o.WriteLine($"accept / reject agree: {S("agree")}, disagree: {S("disagree")}");
            int both = S("first_code_same") + S("first_code_diff");
            o.WriteLine(
                $"both reject: {both}, same first error code: {S("first_code_same")}, same error code set: "
                    + $"{S("code_set_same")}, same codes with counts: {S("code_list_same")}, same codes in the same order: "
                    + $"{S("code_order_same")} (and the same files and lines: {S("lines_same")}), the same warnings: "
                    + $"{S("reject_warnings_same")}"
            );
            o.WriteLine(
                $"both accept: {S("both_accept")}, same warning codes (with counts): {S("warnings_same")}"
            );
            o.WriteLine("first-code differences (recorded -> ours): count");
            var gl = new List<((string, string) k, int n, int i)>();
            for (int i = 0; i < groupOrder.Count; i++)
                gl.Add((groupOrder[i], groups[groupOrder[i]].Count, i));
            gl.Sort((a, b) => a.n != b.n ? b.n.CompareTo(a.n) : a.i.CompareTo(b.i));
            foreach (var (k, n, _) in gl)
            {
                o.Write($"  {k.Item1} -> {k.Item2}: {n}");
                if (asList)
                {
                    var cs = groups[k];
                    o.Write("\n      " + string.Join("\n      ", cs.GetRange(0, Math.Min(40, cs.Count))));
                }
                o.WriteLine();
            }
            if (outJson != null)
                File.WriteAllText(outJson, "{\"results\":[\n" + results + "\n]}\n", new UTF8Encoding(false));
            return 0;
        }

        private static IEnumerable<string> Quoted(List<string> xs)
        {
            foreach (string x in xs)
                yield return Json.Quote(x);
        }

        /// <summary>Our outcome for a recorded target: a multi-project probe's target by its
        /// name, its registered name (CiP_ prefix) or as it is; a single probe's one outcome.</summary>
        internal static Outcome? Mine(Dictionary<string, Outcome> ours, string target, bool multi)
        {
            Outcome? mine;
            if (multi)
                ours.TryGetValue(target, out mine);
            else
                ours.TryGetValue("", out mine);
            if (mine == null && multi)
            {
                if (!ours.TryGetValue(RegName(target), out mine))
                    ours.TryGetValue(target, out mine);
            }
            return mine;
        }

        /// <summary>The recorded error codes of a verdict (without the rig's own messages).</summary>
        internal static List<string> RecordedErrors(Dictionary<string, object?> verdict) =>
            Codes(verdict, "errors", out _);

        /// <summary>One probe with a verdict compiled by the rig: its entry, path below the cases
        /// folder, text (null when unreadable), the exception type when the rig crashed, and each
        /// compiled target's outcome.</summary>
        internal sealed class ProbeRun
        {
            public Entry Entry = null!;
            public string Rel = "";
            public string? Text;
            public string? Crash;
            public Dictionary<string, Outcome>? Ours;
            public bool Multi;
        }

        /// <summary>Every probe with a verdict (sorted by path) compiled by the rig: the golden
        /// tests (Golden/ProbeGoldenTests).</summary>
        internal static IEnumerable<ProbeRun> ProbeRuns(string user)
        {
            var verdicts = LoadVerdicts();
            var rig = new Rig(user);
            foreach (var (_, e) in SortedEntries(verdicts))
            {
                var r = new ProbeRun
                {
                    Entry = e,
                    Rel = RelPath(e.Path, Cases),
                    Text = ReadProbe(e.Path),
                };
                if (r.Text != null)
                {
                    r.Multi = r.Text.Contains("//// PROJECT:");
                    try
                    {
                        r.Ours = r.Multi ? rig.RunMulti(r.Text) : rig.RunSingle(r.Text);
                    }
                    catch (Exception exc)
                    {
                        r.Crash = exc.GetType().Name;
                    }
                }
                yield return r;
            }
        }
    }
}
