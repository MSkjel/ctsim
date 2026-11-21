using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace CicodeSim.Tests.Golden
{
    /// <summary>The golden files and the corpus they were made from.</summary>
    public static class GoldenFiles
    {
        public const string User = @"C:\ProgramData\AVEVA Plant SCADA 2023 R2\User";

        public static bool CorpusPresent =>
            !CicodeSim.Tests.Vm.LocalData.NoInstall
            && Directory.Exists(User)
            && File.Exists(Path.Combine(User, "MASTER.DBF"));

        public static bool Writing => Environment.GetEnvironmentVariable("CICODESIM_GOLDEN_WRITE") == "1";

        private static string SourceDir([CallerFilePath] string here = "") =>
            Path.Combine(Path.GetDirectoryName(here)!, "Data");

        /// <summary>Where a golden file is read: the source tree, else the copy in the test output.</summary>
        public static string ReadPath(string name)
        {
            string src = Path.Combine(SourceDir(), name);
            if (File.Exists(src))
                return src;
            return Path.Combine(AppContext.BaseDirectory, "Golden", "Data", name);
        }

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        /// <summary>The golden file this thread read last (AssertSame names its inputs).</summary>
        [ThreadStatic]
        private static string? _lastGolden;

        public static IEnumerable<string> ReadLines(string name)
        {
            _lastGolden = name;
            return ReadLinesOf(name);
        }

        private static IEnumerable<string> ReadLinesOf(string name)
        {
            string p = ReadPath(name);
            Assert.True(File.Exists(p), "golden file missing: " + p);
            using var fs = File.OpenRead(p);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var rd = new StreamReader(gz, Utf8);
            string? line;
            while ((line = rd.ReadLine()) != null)
                yield return line;
        }

        /// <summary>Write a golden file (gzip, '\n' after every line) into the source tree.</summary>
        public static void WriteLines(string name, IEnumerable<string> lines)
        {
            string dir = SourceDir();
            Directory.CreateDirectory(dir);
            string p = Path.Combine(dir, name);
            string tmp = p + ".tmp";
            using (var fs = File.Create(tmp))
            using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            using (var w = new StreamWriter(gz, Utf8) { NewLine = "\n" })
            {
                foreach (string l in lines)
                {
                    if (l.IndexOf('\n') >= 0)
                        throw new ArgumentException("a golden line holds a newline");
                    w.Write(l);
                    w.Write('\n');
                }
            }
            if (File.Exists(p))
                File.Delete(p);
            File.Move(tmp, p);
        }

        /// <summary>Write the file when CICODESIM_GOLDEN_WRITE=1 (and fail the test on purpose),
        /// else return false.</summary>
        public static bool WriteIfAsked(string name, IEnumerable<string> lines)
        {
            _lastGolden = name;
            if (!Writing)
                return false;
            WriteLines(name, lines);
            WriteInputs(name);
            Assert.Fail(
                "CICODESIM_GOLDEN_WRITE=1: wrote " + Path.Combine(SourceDir(), name) + " (not compared)"
            );
            return true;
        }

        /// <summary>Compare keyed golden lines with computed ones: every key in both, the same
        /// text. The message names each differing key and shows both lines (the first
        /// <paramref name="show"/>), then counts.</summary>
        public static void AssertSame(
            string what,
            IList<(string key, string line)> want,
            IList<(string key, string line)> got,
            Func<string, string>? detail = null,
            int show = 12
        )
        {
            var w = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var (k, l) in want)
            {
                if (!w.TryGetValue(k, out var lst))
                {
                    w[k] = lst = new List<string>();
                    order.Add(k);
                }
                lst.Add(l);
            }
            var g = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var gorder = new List<string>();
            foreach (var (k, l) in got)
            {
                if (!g.TryGetValue(k, out var lst))
                {
                    g[k] = lst = new List<string>();
                    gorder.Add(k);
                }
                lst.Add(l);
            }
            var report = new StringBuilder();
            int differ = 0,
                missing = 0,
                extra = 0;
            foreach (string k in order)
            {
                if (!g.TryGetValue(k, out var gl))
                {
                    if (missing++ < show)
                        report
                            .Append("missing now: ")
                            .Append(k)
                            .Append("\n  golden ")
                            .Append(string.Join("\n  golden ", w[k]))
                            .Append('\n');
                    continue;
                }
                var wl = w[k];
                if (Same(wl, gl))
                    continue;
                if (differ++ < show)
                {
                    report.Append("differs: ").Append(k).Append('\n');
                    foreach (string l in wl)
                        report.Append("  golden ").Append(l).Append('\n');
                    foreach (string l in gl)
                        report.Append("  now    ").Append(l).Append('\n');
                    if (wl.Count == 1 && gl.Count == 1)
                        FieldDiff(report, wl[0], gl[0]);
                    if (detail != null)
                        report.Append(detail(k)).Append('\n');
                }
            }
            foreach (string k in gorder)
            {
                if (w.ContainsKey(k))
                    continue;
                if (extra++ < show)
                    report
                        .Append("new now: ")
                        .Append(k)
                        .Append("\n  now    ")
                        .Append(string.Join("\n  now    ", g[k]))
                        .Append('\n');
            }
            string? golden = _lastGolden;
            if (differ + missing + extra == 0)
            {
                // CICODESIM_GOLDEN_PIN=1: record the inputs of a golden that holds (one written
                // without an input record)
                if (golden != null && Environment.GetEnvironmentVariable("CICODESIM_GOLDEN_PIN") == "1")
                    WriteInputs(golden);
                return;
            }
            string changed = golden == null ? "" : ChangedInputs(golden);
            string head =
                changed.Length > 0
                    ? "INPUT CHANGED (the R2 projects this golden was written from changed since: "
                        + changed
                        + "; check whether that explains the difference before calling it a regression)\n"
                    : "";
            Assert.Fail(
                $"{head}{what}: {differ} differ, {missing} missing now, {extra} new (golden {want.Count} lines, now {got.Count})\n{report}"
            );
        }

        // -- what a golden was written from ---------------------------------------------------
        // The goldens are computed from the installed R2 projects, which change; a golden's
        // Data/<name>.inputs.json keeps one digest per project (its files' names, sizes and
        // times) so a failure can say the input changed.

        private static readonly string[] InputExtensions =
        {
            ".ci",
            ".dbf",
            ".ndx",
            ".ctg",
            ".ctm",
            ".ctl",
            ".ctt",
            ".rdb",
            ".ini",
            ".cit",
            ".xml",
            ".pal",
            ".frm",
        };

        /// <summary>project folder name -> digest of its input files (sorted relative path, size,
        /// write time).</summary>
        public static SortedDictionary<string, string> InputDigests()
        {
            var o = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(User))
                return o;
            foreach (string dir in Directory.GetDirectories(User))
            {
                var rows = new List<string>();
                try
                {
                    foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        if (Array.IndexOf(InputExtensions, ext) < 0)
                            continue;
                        var fi = new FileInfo(f);
                        rows.Add(
                            f.Substring(dir.Length).ToLowerInvariant()
                                + "|"
                                + fi.Length
                                + "|"
                                + fi.LastWriteTimeUtc.Ticks
                        );
                    }
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                rows.Sort(StringComparer.Ordinal);
                using var sha = System.Security.Cryptography.SHA1.Create();
                byte[] h = sha.ComputeHash(Utf8.GetBytes(string.Join("\n", rows)));
                o[Path.GetFileName(dir)] = BitConverter.ToString(h).Replace("-", "").ToLowerInvariant();
            }
            return o;
        }

        private static string InputsPath(string name) => Path.Combine(SourceDir(), name + ".inputs.json");

        /// <summary>Record what golden <paramref name="name"/> is written from (now).</summary>
        public static void WriteInputs(string name)
        {
            var obj = CicodeSim.Values.JsonValue.NewObject();
            foreach (var kv in InputDigests())
                obj.Set(kv.Key, kv.Value);
            File.WriteAllText(InputsPath(name), obj.Dump() + "\n", Utf8);
        }

        /// <summary>The projects whose inputs differ from golden <paramref name="name"/>'s record
        /// ('' when there is no record or nothing changed).</summary>
        public static string ChangedInputs(string name)
        {
            string p = InputsPath(name);
            if (!File.Exists(p))
                return "";
            CicodeSim.Values.JsonValue rec;
            try
            {
                rec = CicodeSim.Values.JsonValue.Parse(File.ReadAllText(p, Utf8));
            }
            catch (FormatException)
            {
                return "";
            }
            var now = InputDigests();
            var changed = new List<string>();
            foreach (var kv in rec.Pairs)
                if (!now.TryGetValue(kv.Key, out var d) || d != kv.Value.Str)
                    changed.Add(kv.Key);
            foreach (var kv in now)
                if (!rec.Has(kv.Key))
                    changed.Add(kv.Key + " (new)");
            return string.Join(", ", changed);
        }

        /// <summary>For two JSON object lines: the fields that differ (dotted paths into nested
        /// objects), golden value then the value now, so a long record shows what changed.</summary>
        private static void FieldDiff(StringBuilder report, string golden, string now)
        {
            CicodeSim.Values.JsonValue a,
                b;
            try
            {
                a = CicodeSim.Values.JsonValue.Parse(golden);
                b = CicodeSim.Values.JsonValue.Parse(now);
            }
            catch (FormatException)
            {
                return;
            }
            var diffs = new List<string>();
            Walk("", a, b, diffs);
            if (diffs.Count == 0)
                return;
            report
                .Append("  fields ")
                .Append(string.Join("; ", diffs.Count > 8 ? diffs.GetRange(0, 8) : diffs));
            if (diffs.Count > 8)
                report.Append("; ... ").Append(diffs.Count - 8).Append(" more");
            report.Append('\n');
        }

        private static void Walk(
            string path,
            CicodeSim.Values.JsonValue? a,
            CicodeSim.Values.JsonValue? b,
            List<string> diffs
        )
        {
            const CicodeSim.Values.JsonKind Obj = CicodeSim.Values.JsonKind.Object;
            if (a != null && b != null && a.Kind == Obj && b.Kind == Obj && path.Split('.').Length < 4)
            {
                var keys = new List<string>();
                foreach (var kv in a.Pairs)
                    keys.Add(kv.Key);
                foreach (var kv in b.Pairs)
                    if (!a.Has(kv.Key))
                        keys.Add(kv.Key);
                foreach (string k in keys)
                    Walk(path.Length == 0 ? k : path + "." + k, a.Get(k), b.Get(k), diffs);
                return;
            }
            string sa = a == null ? "(none)" : a.Dump(),
                sb = b == null ? "(none)" : b.Dump();
            if (sa == sb)
                return;
            static string Cut(string s) => s.Length > 80 ? s.Substring(0, 77) + "..." : s;
            diffs.Add((path.Length == 0 ? "(value)" : path) + ": " + Cut(sa) + " -> " + Cut(sb));
        }

        private static bool Same(List<string> a, List<string> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                    return false;
            return true;
        }
    }

    /// <summary>A golden fact over the R2 corpus: skipped when the User folder is not installed
    /// (the extension's Linux CI), and, when heavy, with CTG_SKIP_HEAVY=1. Each golden
    /// test also carries the trait Golden = light | heavy (dotnet test --filter "Golden!=heavy").</summary>
    public sealed class GoldenFactAttribute : FactAttribute
    {
        public GoldenFactAttribute(bool heavy = false)
        {
            if (!GoldenFiles.CorpusPresent)
                Skip = "the R2 corpus (" + GoldenFiles.User + ") is not installed";
            else if (heavy && Environment.GetEnvironmentVariable("CTG_SKIP_HEAVY") == "1")
                Skip = "corpus-wide golden check (CTG_SKIP_HEAVY=1)";
        }
    }
}
