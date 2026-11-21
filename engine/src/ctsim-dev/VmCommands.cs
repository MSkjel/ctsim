using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;
using CProgram = CicodeSim.Compiled.Program;

namespace CicodeSim.Host
{
    /// <summary>The compiled-code commands: pcode, check, run, sweep and bench.</summary>
    internal static class VmCommands
    {
        public const string DefaultUser = @"C:\ProgramData\AVEVA Plant SCADA 2023 R2\User";

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private static TextWriter Output(string? path)
        {
            if (path == null)
            {
                var o = new StreamWriter(Console.OpenStandardOutput(), Utf8)
                {
                    AutoFlush = false,
                    NewLine = "\n",
                };
                return o;
            }
            return new StreamWriter(path, false, Utf8) { NewLine = "\n" };
        }

        internal static string? TakeOption(List<string> args, string name) => CliArgs.TakeOption(args, name);

        internal static bool TakeFlag(List<string> args, string name) => CliArgs.TakeFlag(args, name);

        /// <summary>The libraries under a User folder (user\*\_LIBRARY.RDB), sorted by path.</summary>
        public static List<string> Libraries(string user)
        {
            var r = new List<string>();
            if (!Directory.Exists(user))
                return r;
            foreach (var d in Directory.GetDirectories(user))
            {
                string p = Path.Combine(d, "_LIBRARY.RDB");
                if (File.Exists(p))
                    r.Add(FileCase(d, "_LIBRARY.RDB"));
            }
            r.Sort(StringComparer.Ordinal);
            return r;
        }

        private static string FileCase(string dir, string name)
        {
            foreach (var f in Directory.GetFiles(dir))
                if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase))
                    return f;
            return Path.Combine(dir, name);
        }

        // ------------------------------------------------------------------ pcode
        public static int PcodeListing(List<string> args)
        {
            string? outPath = TakeOption(args, "--out");
            if (args.Count < 1)
            {
                Console.Error.WriteLine(
                    "usage: ctsim-dev pcode <file.RDB> [function | table | table.record.field ...] [--out file]"
                );
                return 2;
            }
            string text = CicodeSim.Compiled.PcodeListing.Run(args[0], args.Skip(1).ToList());
            using (var w = Output(outPath))
                w.Write(text);
            return 0;
        }

        // ------------------------------------------------------------------ check
        public static int Check(List<string> args)
        {
            var pages = new List<string>();
            int pk = args.IndexOf("--pages");
            if (pk >= 0)
            {
                pages.AddRange(args.Skip(pk + 1));
                args.RemoveRange(pk, args.Count - pk);
            }
            string user = args.Count > 0 ? args[0] : DefaultUser;
            var sw = Stopwatch.StartNew();
            var ops = new Dictionary<int, long>();
            long nf = 0,
                nbad = 0,
                nsym = 0,
                nmis = 0;
            var libs = Libraries(user);
            foreach (var p in libs)
            {
                var prog = Rdb.LoadLibrary(p);
                var bad = new List<(string, string)>();
                foreach (var fn in prog.Functions)
                {
                    string? why = Opcodes.SweepOk(fn.Code);
                    if (why != null)
                    {
                        bad.Add((fn.Name, why));
                        continue;
                    }
                    foreach (var ins in Opcodes.Instructions(fn.Code))
                        ops[ins.Op] = (ops.TryGetValue(ins.Op, out long c) ? c : 0) + 1;
                }
                string sym = Path.Combine(Path.GetDirectoryName(p)!, "_FUNCSYM.DBF");
                var mism = new List<(string, string)>();
                if (File.Exists(sym))
                {
                    var rows = Rdb.LoadFuncSym(sym);
                    nsym += rows.Count;
                    mism = Rdb.FuncSymMismatches(prog, rows);
                }
                nf += prog.Functions.Count;
                nbad += bad.Count;
                nmis += mism.Count;
                string proj = Path.GetFileName(Path.GetDirectoryName(p)!);
                foreach (var x in bad.Take(3).Concat(mism.Take(3)))
                    Console.WriteLine($"  !! {proj} ({x.Item1}, {x.Item2})");
            }
            Console.WriteLine(
                $"libraries {libs.Count}  functions {nf}  decode problems {nbad}  _FUNCSYM rows {nsym}  mismatches {nmis}  ({sw.Elapsed.TotalSeconds:F1} s)"
            );
            Console.WriteLine(
                "opcodes used: "
                    + string.Join(
                        ", ",
                        ops.OrderBy(kv => kv.Key).Select(kv => Op.Name(kv.Key) + " " + kv.Value)
                    )
            );
            long pbad = 0;
            foreach (var proj in pages)
            {
                var pops = new Dictionary<int, long>();
                var (np, nb, badp) = CheckPages(Path.Combine(user, proj), pops);
                pbad += badp.Count;
                foreach (var x in badp.Take(5))
                    Console.WriteLine("  !! " + x);
                Console.WriteLine($"{proj}: pages {np}  blocks {nb}  decode problems {badp.Count}");
                Console.WriteLine(
                    "  opcodes used: "
                        + string.Join(
                            ", ",
                            pops.OrderBy(kv => kv.Key).Select(kv => Op.Name(kv.Key) + " " + kv.Value)
                        )
                );
            }
            return nbad != 0 || nmis != 0 || pbad != 0 ? 1 : 0;
        }

        /// <summary>The page .RDB files of a project (*.RDB sorted by path, no '_' names, with
        /// CODE and without Library).</summary>
        public static IEnumerable<(string Path, RdbFile? File, string? Error)> PageFiles(string projectDir)
        {
            if (!Directory.Exists(projectDir))
                yield break;
            var files = Directory
                .GetFiles(projectDir)
                .Where(f => f.EndsWith(".RDB", StringComparison.OrdinalIgnoreCase))
                .ToList();
            files.Sort(StringComparer.Ordinal);
            foreach (var p in files)
            {
                if (Path.GetFileName(p).ToUpperInvariant().StartsWith("_", StringComparison.Ordinal))
                    continue;
                RdbFile? f = null;
                string? err = null;
                try
                {
                    f = RdbFile.Open(p);
                }
                catch (IOException e)
                {
                    err = e.Message;
                }
                catch (RdbError e)
                {
                    err = e.Message;
                }
                if (f == null)
                {
                    yield return (p, null, err);
                    continue;
                }
                if (!f.Has("CODE") || f.Has("Library"))
                    continue;
                yield return (p, f, null);
            }
        }

        private static (int Pages, int Blocks, List<string> Bad) CheckPages(
            string projectDir,
            Dictionary<int, long> ops
        )
        {
            int np = 0,
                nb = 0;
            var bad = new List<string>();
            foreach (var (p, f, err) in PageFiles(projectDir))
            {
                if (f == null)
                {
                    bad.Add($"({p}, {err})");
                    continue;
                }
                var pc = Rdb.PageFromRdb(f);
                np++;
                foreach (var b in pc.Blocks)
                {
                    nb++;
                    string? why = Opcodes.SweepOk(b.Code);
                    if (why != null)
                    {
                        bad.Add($"({p}, {b.Table}.{b.Record}.{b.Field}: {why})");
                        continue;
                    }
                    foreach (var ins in Opcodes.Instructions(b.Code))
                        ops[ins.Op] = (ops.TryGetValue(ins.Op, out long c) ? c : 0) + 1;
                }
            }
            return (np, nb, bad);
        }

        // ------------------------------------------------------------------ run / sweep
        private sealed class NoTags : ITagAccess
        {
            public Value Read(TagRef r, CicodeTask task)
            {
                task.TagError = Errors.TagNotFound;
                return Value.Long(0, 0);
            }

            public int Write(TagRef r, Value value, CicodeTask task) => 0;
        }

        private sealed class ZeroTags : ITagAccess
        {
            public Value Read(TagRef r, CicodeTask task) => Value.Long(0);

            public int Write(TagRef r, Value value, CicodeTask task) => 0;
        }

        /// <summary>The sweep's three argument sets: per parameter type 0 / 0.0 / "", 3 / 2.5 /
        /// "abc def", -1 / -1e9 / "Plant.Area.Pump1(1,2)"; an OBJECT is -1.</summary>
        private static Value ArgFor(int set, int t)
        {
            switch (set)
            {
                case 0:
                    if (t == Types.Long)
                        return Value.Long(0);
                    if (t == Types.Real)
                        return Value.Real(0.0);
                    if (t == Types.String)
                        return Value.Str("");
                    if (t == Types.Object)
                        return Value.Object(-1);
                    break;
                case 1:
                    if (t == Types.Long)
                        return Value.Long(3);
                    if (t == Types.Real)
                        return Value.Real(2.5);
                    if (t == Types.String)
                        return Value.Str("abc def");
                    if (t == Types.Object)
                        return Value.Object(-1);
                    break;
                default:
                    if (t == Types.Long)
                        return Value.Long(-1);
                    if (t == Types.Real)
                        return Value.Real(-1e9);
                    if (t == Types.String)
                        return Value.Str("Plant.Area.Pump1(1,2)");
                    if (t == Types.Object)
                        return Value.Object(-1);
                    break;
            }
            return Value.Long(0);
        }

        /// <summary>The functions whose whole call closure uses no tags and only implemented
        /// built-ins.</summary>
        private static HashSet<string> PureSet(CProgram prog, Registry reg)
        {
            var info = new Dictionary<string, (HashSet<int> Bis, HashSet<string> Calls, bool Tags)>(
                StringComparer.Ordinal
            );
            foreach (var f in prog.Functions)
            {
                var bis = new HashSet<int>();
                var calls = new HashSet<string>(StringComparer.Ordinal);
                bool tags = false;
                foreach (var ins in Opcodes.Instructions(f.Code))
                {
                    if (ins.Op == Op.Func)
                        bis.Add(ins.A);
                    else if (ins.Op == Op.FuncCallByLookup)
                        calls.Add(
                            prog.FuncLookups.TryGetValue(ins.Offset, out var fl)
                                ? fl.Name.ToLowerInvariant()
                                : "?"
                        );
                    else if (ins.Op == Op.Load || ins.Op == Op.Store)
                        tags = true;
                }
                info[f.Key] = (bis, calls, tags);
            }
            var memo = new Dictionary<string, bool>(StringComparer.Ordinal);
            bool Pure(string n, int depth)
            {
                if (memo.TryGetValue(n, out bool m))
                    return m;
                memo[n] = false;
                if (!info.ContainsKey(n) || depth > 40)
                    return false;
                var (bis, calls, tags) = info[n];
                bool ok = !tags && bis.All(reg.IsIndexImplemented) && calls.All(c => Pure(c, depth + 1));
                memo[n] = ok;
                return ok;
            }
            var r = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in prog.Functions)
                if (Pure(f.Key, 0))
                    r.Add(f.Key);
            return r;
        }

        /// <summary>One run's JSON record: outcome, state, halt code / reason, location,
        /// instruction count, error state and the result value.</summary>
        internal static void WriteRun(StringBuilder sb, CicodeTask t, string outcome)
        {
            sb.Append("\"outcome\":");
            Json.WriteString(sb, outcome);
            sb.Append(",\"state\":").AppendInv(t.State);
            sb.Append(",\"code\":").AppendInv(t.HaltCode);
            sb.Append(",\"reason\":");
            Json.WriteString(sb, t.HaltReason);
            sb.Append(",\"pc\":").AppendInv(t.Pc);
            sb.Append(",\"line\":").AppendInv(t.Line);
            sb.Append(",\"instr\":").AppendInv(t.Instructions);
            sb.Append(",\"error\":").AppendInv(t.Error);
            sb.Append(",\"expr\":").AppendInv(t.ExprError);
            sb.Append(",\"result\":");
            WriteValue(sb, t.Result);
        }

        public static void WriteValue(StringBuilder sb, Value? rv)
        {
            if (!rv.HasValue)
            {
                sb.Append("null");
                return;
            }
            Value v = rv.Value;
            sb.Append("{\"t\":").AppendInv(v.T).Append(",\"v\":");
            if (Types.IsRealType(v.T))
            {
                sb.Append("{\"f\":");
                Json.WriteString(sb, FloatText.Shortest(v.D));
                sb.Append('}');
            }
            else if (v.T == Types.String)
                Json.WriteString(sb, v.S);
            else if (v.T == Types.Variant && v.O != null)
                sb.Append("\"<ref>\"");
            else if (v.T == Types.Timestamp || v.T == Types.Quality)
                sb.Append(((ulong)v.I).ToString(CultureInfo.InvariantCulture));
            else
                sb.Append(v.I.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"q\":").AppendInv(v.Q);
            sb.Append(",\"ts\":").AppendInv(v.Ts);
            sb.Append(",\"vts\":").AppendInv(v.Vts);
            sb.Append(",\"qts\":").AppendInv(v.Qts);
            sb.Append('}');
        }

        private static string Outcome(CicodeTask t, bool waited, int n, int maxSlices)
        {
            if (waited)
                return "waits";
            if (t.State == TaskState.Stop)
                return "stop";
            if (t.State == TaskState.Halt)
                return t.HaltReason.Contains("not simulated") ? "not simulated" : "halt " + t.HaltCode;
            if (!t.Done && n >= maxSlices)
                return "still running";
            return "state " + t.State;
        }

        internal static string NotSimulatedName(CicodeTask t)
        {
            const string pre = "built-in function ",
                post = " is not simulated";
            string r = t.HaltReason;
            if (r.StartsWith(pre, StringComparison.Ordinal) && r.EndsWith(post, StringComparison.Ordinal))
                return r.Substring(pre.Length, r.Length - pre.Length - post.Length);
            return "";
        }

        public static int Sweep(List<string> args)
        {
            string? outPath = TakeOption(args, "--out");
            bool all = TakeFlag(args, "--all");
            bool pages = TakeFlag(args, "--pages");
            bool json = TakeFlag(args, "--json");
            var projects = new List<string>();
            int pk = args.IndexOf("--projects");
            if (pk >= 0)
            {
                projects.AddRange(args.Skip(pk + 1));
                args.RemoveRange(pk, args.Count - pk);
            }
            string target = args.Count > 0 ? args[0] : DefaultUser;
            List<string> libs;
            if (File.Exists(target))
                libs = new List<string> { target };
            else
            {
                libs = Libraries(target);
                if (projects.Count > 0)
                {
                    var want = new HashSet<string>(projects.Select(p => p.ToLowerInvariant()));
                    libs = libs.Where(p =>
                            want.Contains(Path.GetFileName(Path.GetDirectoryName(p)!).ToLowerInvariant())
                        )
                        .ToList();
                }
            }
            var sw = Stopwatch.StartNew();
            var stats = new Dictionary<string, long>(StringComparer.Ordinal);
            void Count(string k) => stats[k] = (stats.TryGetValue(k, out long c) ? c : 0) + 1;
            long nfun = 0;
            TextWriter? w = json ? Output(outPath) : null;
            var sb = new StringBuilder(512);
            try
            {
                if (pages)
                {
                    foreach (var p in libs)
                        nfun += RunPages(Path.GetDirectoryName(p)!, Count, w, sb);
                }
                else
                {
                    foreach (var p in libs)
                        nfun += RunProject(p, all, Count, w, sb);
                }
            }
            finally
            {
                w?.Flush();
                if (outPath != null)
                    w?.Dispose();
            }
            var summary = Console.Error;
            if (pages)
                summary.WriteLine(
                    $"projects {libs.Count}  page blocks run {nfun}  ({sw.Elapsed.TotalSeconds:F1} s)"
                );
            else
                summary.WriteLine(
                    $"libraries {libs.Count}  functions run {nfun}  ({sw.Elapsed.TotalSeconds:F1} s)"
                );
            foreach (
                var kv in stats.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            )
                summary.WriteLine($"  {kv.Key}: {kv.Value}");
            return stats.ContainsKey("VM ERROR") ? 1 : 0;
        }

        /// <summary>One run of the library sweep: the function, the argument set (0..2), the task
        /// as the run left it (null after a VM ERROR), the outcome and the error text.</summary>
        internal readonly struct LibraryRun
        {
            public readonly string Lib;
            public readonly Function Fn;
            public readonly int Set;
            public readonly CicodeTask? Task;
            public readonly string Outcome;
            public readonly string Err;

            public LibraryRun(string lib, Function fn, int set, CicodeTask? task, string outcome, string err)
            {
                Lib = lib;
                Fn = fn;
                Set = set;
                Task = task;
                Outcome = outcome;
                Err = err;
            }
        }

        /// <summary>The library sweep of one _LIBRARY.RDB.</summary>
        internal static IEnumerable<LibraryRun> LibraryRuns(string path, bool every, int maxSlices = 50)
        {
            var prog = Rdb.LoadLibrary(path);
            var reg = Registry.Default();
            var chosen = every
                ? new HashSet<string>(prog.Functions.Select(f => f.Key), StringComparer.Ordinal)
                : PureSet(prog, reg);
            var cfg = new CodeConfig { MaxSliceOps = 20000 };
            string lib = Path.GetFileName(Path.GetDirectoryName(path)!);
            foreach (var f in prog.Functions)
            {
                if (!chosen.Contains(f.Key))
                    continue;
                for (int set = 0; set < 3; set++)
                {
                    var vm = new CicodeVm(prog, reg, new NoTags(), new ManualClock(tickMs: 1.0), null, cfg);
                    var vals = new Value[f.Args.Length];
                    for (int k = 0; k < vals.Length; k++)
                        vals[k] = ArgFor(set, Types.SigType(f.Args[k]));
                    string outcome;
                    CicodeTask? t = null;
                    string err = "";
                    try
                    {
                        t = vm.TaskForCall(f, vals);
                        int n = 0;
                        bool waited = false;
                        while (!t.Done && n < maxSlices)
                        {
                            if (t.State == TaskState.Wait || t.State == TaskState.Sleep)
                            {
                                waited = true;
                                break;
                            }
                            vm.RunSlice(t);
                            n++;
                        }
                        outcome = Outcome(t, waited, n, maxSlices);
                    }
                    catch (Exception e) when (!(e is OutOfMemoryException))
                    {
                        outcome = "VM ERROR";
                        err = e.GetType().Name + ": " + e.Message;
                    }
                    yield return new LibraryRun(lib, f, set, outcome == "VM ERROR" ? null : t, outcome, err);
                }
            }
        }

        private static long RunProject(
            string path,
            bool every,
            Action<string> count,
            TextWriter? w,
            StringBuilder sb,
            int maxSlices = 50
        )
        {
            var fns = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in LibraryRuns(path, every, maxSlices))
            {
                fns.Add(r.Fn.Key);
                count("runs");
                count(r.Outcome);
                if (w == null)
                    continue;
                sb.Clear();
                sb.Append("{\"lib\":");
                Json.WriteString(sb, r.Lib);
                sb.Append(",\"index\":").AppendInv(r.Fn.Index);
                sb.Append(",\"fn\":");
                Json.WriteString(sb, r.Fn.Name);
                sb.Append(",\"set\":").AppendInv(r.Set).Append(',');
                if (r.Task != null)
                {
                    WriteRun(sb, r.Task, r.Outcome);
                    if (r.Outcome == "not simulated")
                    {
                        sb.Append(",\"nsfn\":");
                        Json.WriteString(sb, NotSimulatedName(r.Task));
                    }
                }
                else
                {
                    sb.Append("\"outcome\":\"VM ERROR\",\"err\":");
                    Json.WriteString(sb, r.Err);
                }
                sb.Append('}');
                w.WriteLine(sb.ToString());
            }
            return fns.Count;
        }

        /// <summary>One run of the page-block sweep: the page ('project/file.RDB'), the block, the
        /// task as the run left it (null after a VM ERROR), the outcome and the error text.</summary>
        internal readonly struct PageRun
        {
            public readonly string Page;
            public readonly PageBlock Block;
            public readonly CicodeTask? Task;
            public readonly string Outcome;
            public readonly string Err;

            public PageRun(string page, PageBlock block, CicodeTask? task, string outcome, string err)
            {
                Page = page;
                Block = block;
                Task = task;
                Outcome = outcome;
                Err = err;
            }
        }

        /// <summary>The page-block sweep of one project: every code block of every page .RDB
        /// (PageFiles' order) in one VM per page over the project's library, tags reading 0, at
        /// most 200 slices. Used by `ctsim-dev sweep --pages` and the golden tests.</summary>
        internal static IEnumerable<PageRun> PageRuns(string projectDir)
        {
            string lib = Path.Combine(projectDir, "_LIBRARY.RDB");
            CProgram? prog = File.Exists(lib) ? Rdb.LoadLibrary(lib) : null;
            var reg = Registry.Default();
            var cfg = new CodeConfig { MaxSliceOps = 20000 };
            foreach (var (p, f, err0) in PageFiles(projectDir))
            {
                if (f == null)
                    continue;
                var pc = Rdb.PageFromRdb(f);
                var vm = new CicodeVm(prog, reg, new ZeroTags(), new ManualClock(tickMs: 1.0), null, cfg);
                string page = Path.GetFileName(projectDir) + "/" + Path.GetFileName(p);
                foreach (var b in pc.Blocks)
                {
                    string outcome;
                    CicodeTask? t = null;
                    string err = "";
                    try
                    {
                        t = vm.TaskForBlock(b.Code, pc.Program, b.Kind == RdbFile.FExpr);
                        int k = 0;
                        bool waited = false;
                        while (!t.Done && k < 200)
                        {
                            if (t.State == TaskState.Wait || t.State == TaskState.Sleep)
                            {
                                waited = true;
                                break;
                            }
                            vm.RunSlice(t);
                            k++;
                        }
                        outcome = Outcome(t, waited, k, 200);
                    }
                    catch (Exception e) when (!(e is OutOfMemoryException))
                    {
                        outcome = "VM ERROR";
                        err = e.GetType().Name + ": " + e.Message;
                    }
                    yield return new PageRun(page, b, outcome == "VM ERROR" ? null : t, outcome, err);
                }
            }
        }

        private static long RunPages(string projectDir, Action<string> count, TextWriter? w, StringBuilder sb)
        {
            long n = 0;
            foreach (var r in PageRuns(projectDir))
            {
                n++;
                count("blocks");
                count(r.Outcome);
                if (w == null)
                    continue;
                sb.Clear();
                sb.Append("{\"page\":");
                Json.WriteString(sb, r.Page);
                sb.Append(",\"table\":");
                Json.WriteString(sb, r.Block.Table);
                sb.Append(",\"record\":").AppendInv(r.Block.Record).Append(",\"field\":");
                Json.WriteString(sb, r.Block.Field);
                sb.Append(',');
                if (r.Task != null)
                {
                    WriteRun(sb, r.Task, r.Outcome);
                    if (r.Outcome == "not simulated")
                    {
                        sb.Append(",\"nsfn\":");
                        Json.WriteString(sb, NotSimulatedName(r.Task));
                    }
                }
                else
                {
                    sb.Append("\"outcome\":\"VM ERROR\",\"err\":");
                    Json.WriteString(sb, r.Err);
                }
                sb.Append('}');
                w.WriteLine(sb.ToString());
            }
            return n;
        }

        /// <summary>ctsim-dev run &lt;rdb&gt; &lt;function&gt; [args...] [--json]: one call with the same VM
        /// set-up as the sweep. Arguments are converted to the parameter types (an INT parameter
        /// takes a whole number, a REAL a number, a STRING the text as it is).</summary>
        public static int Run(List<string> args)
        {
            bool json = TakeFlag(args, "--json");
            if (args.Count < 2)
            {
                Console.Error.WriteLine("usage: ctsim-dev run <_LIBRARY.RDB> <function> [args...] [--json]");
                return 2;
            }
            var prog = Rdb.LoadLibrary(args[0]);
            var fn = prog.Function(args[1]);
            var reg = Registry.Default();
            var cfg = new CodeConfig { MaxSliceOps = 20000 };
            var vm = new CicodeVm(prog, reg, new NoTags(), new ManualClock(tickMs: 1.0), null, cfg);
            var vals = new List<Value>();
            for (int k = 2; k < args.Count; k++)
            {
                string a = args[k];
                int t = fn != null && k - 2 < fn.Args.Length ? Types.SigType(fn.Args[k - 2]) : Types.String;
                if (
                    t == Types.Long
                    && long.TryParse(
                        a,
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture,
                        out long l
                    )
                )
                    vals.Add(Value.Long((int)l));
                else if (
                    t == Types.Real
                    && double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                )
                    vals.Add(Value.Real(d));
                else
                    vals.Add(Value.Str(a));
            }
            var task = fn != null ? vm.TaskForCall(fn, vals) : vm.TaskForCall(args[1], vals);
            int n = 0;
            bool waited = false;
            while (!task.Done && n < 50)
            {
                if (task.State == TaskState.Wait || task.State == TaskState.Sleep)
                {
                    waited = true;
                    break;
                }
                vm.RunSlice(task);
                n++;
            }
            string outcome = Outcome(task, waited, n, 50);
            if (json)
            {
                var sb = new StringBuilder();
                sb.Append("{\"fn\":");
                Json.WriteString(sb, fn?.Name ?? args[1]);
                sb.Append(',');
                WriteRun(sb, task, outcome);
                if (outcome == "not simulated")
                {
                    sb.Append(",\"nsfn\":");
                    Json.WriteString(sb, NotSimulatedName(task));
                }
                sb.Append('}');
                Console.WriteLine(sb.ToString());
            }
            else
            {
                Console.WriteLine(
                    $"{outcome}  state {task.StateName}  code {task.HaltCode}  {task.HaltReason}"
                );
                Console.WriteLine("result " + (task.Result?.ToString() ?? "(none)"));
            }
            return task.State == TaskState.Stop ? 0 : 1;
        }

        // ------------------------------------------------------------------ bench
        /// <summary>The VM's speed: FOR i = 1 TO N DO sum = sum + i END
        /// (Line, LoadStack, Const, Le, Brfalse, Add, StoreStack, Branch) and a string loop with
        /// built-in calls (StrLength, StrMid, StrToChar); instructions per second of each.</summary>
        public static int Bench(List<string> args)
        {
            int n = args.Count > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 200000;
            double seconds = args.Count > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 1.0;
            var pb = new ProgramBuilder();
            var f = pb.Function("SumTo", "", Types.Long);
            f.StackVar(Types.Long, Types.Long);
            f.Line(1).ConstInt(1).StoreStack(0);
            f.Label("top");
            f.LoadStack(0).ConstInt(n).Op("Le").BrFalse("end");
            f.Line(2).LoadStack(1).LoadStack(0).Op("Add").StoreStack(1);
            f.Line(3).LoadStack(0).ConstInt(1).Op("Add").StoreStack(0);
            f.Branch("top");
            f.Label("end");
            f.LoadStack(1).Convert(Types.Long).RetValue().End();
            var g = pb.Function("Chars", "7", Types.Long).Begin();
            g.StackVar(Types.Long, Types.Long);
            g.Label("top");
            g.LoadStack(1).LoadStack(0).Convert(Types.String).Func("StrLength", 1).Op("Lt").BrFalse("end");
            g.LoadStack(2)
                .LoadStack(0)
                .LoadStack(1)
                .ConstInt(1)
                .Func("StrMid", 3)
                .Func("StrToChar", 1)
                .Op("Add")
                .StoreStack(2);
            g.LoadStack(1).ConstInt(1).Op("Add").StoreStack(1);
            g.Branch("top");
            g.Label("end");
            g.LoadStack(2).RetValue().End();
            var prog = pb.Build();
            var cfg = new CodeConfig { TimeSlice = 5000, FullTimeSlice = true };
            var vm = new CicodeVm(prog, Registry.Default(), null, new RealClock(), null, cfg);

            double Run(string name, Value[] a, out CicodeTask t)
            {
                t = vm.TaskForCall(name, a);
                var sw = Stopwatch.StartNew();
                while (!t.Done)
                    vm.RunSlice(t);
                return sw.Elapsed.TotalSeconds;
            }

            // warm up the JIT
            Run("SumTo", new Value[0], out _);
            Run("Chars", new[] { Value.Str("abc") }, out _);
            long total = 0;
            double dt = 0;
            int reps = 0;
            CicodeTask last = null!;
            while (dt < seconds || reps == 0)
            {
                dt += Run("SumTo", new Value[0], out last);
                total += last.Instructions;
                reps++;
            }
            long want = (int)((long)n * (n + 1) / 2);
            if (last.Result!.Value.I != want)
                throw new InvalidOperationException("wrong sum " + last.Result);
            Console.WriteLine(
                $"arithmetic loop: {total} instructions in {dt:F3} s = {total / dt / 1e6:F2} M instructions/s ({reps} calls of {last.Instructions})"
            );
            var s = Value.Str(string.Concat(System.Linq.Enumerable.Repeat("abcdefghij", 25)));
            total = 0;
            dt = 0;
            reps = 0;
            while (dt < seconds || reps == 0)
            {
                dt += Run("Chars", new[] { s }, out last);
                total += last.Instructions;
                reps++;
            }
            Console.WriteLine(
                $"string loop with built-ins: {total} instructions in {dt:F3} s = {total / dt / 1e6:F2} M instructions/s ({reps} calls)"
            );
            if (Environment.GetEnvironmentVariable("CTSIM_BENCH_MORE") == "1")
            {
                var pb2 = new ProgramBuilder();
                var h = pb2.Function("Calls", "", Types.Long);
                h.StackVar(Types.Long);
                h.Label("top");
                h.LoadStack(0).ConstInt(n).Op("Lt").BrFalse("end");
                h.Func("IsError", 0).Op("Pop");
                h.LoadStack(0).ConstInt(1).Op("Add").StoreStack(0);
                h.Branch("top");
                h.Label("end");
                h.LoadStack(0).RetValue().End();
                var vm2 = new CicodeVm(pb2.Build(), Registry.Default(), null, new RealClock(), null, cfg);
                var t2 = vm2.TaskForCall("Calls", new Value[0]);
                var sw2 = Stopwatch.StartNew();
                while (!t2.Done)
                    vm2.RunSlice(t2);
                double d2 = sw2.Elapsed.TotalSeconds;
                Console.WriteLine(
                    $"IsError() loop: {t2.Instructions / d2 / 1e6:F2} M instructions/s, {d2 / n * 1e9:F0} ns per iteration"
                );
                var vm3 = new CicodeVm(pb2.Build(), Registry.Default(), null, new ManualClock(), null, cfg);
                t2 = vm3.TaskForCall("Calls", new Value[0]);
                sw2 = Stopwatch.StartNew();
                while (!t2.Done)
                    vm3.RunSlice(t2);
                d2 = sw2.Elapsed.TotalSeconds;
                Console.WriteLine(
                    $"IsError() loop, manual clock: {t2.Instructions / d2 / 1e6:F2} M instructions/s, {d2 / n * 1e9:F0} ns per iteration"
                );
                var clock = new RealClock();
                sw2 = Stopwatch.StartNew();
                ulong acc = 0;
                for (int k = 0; k < 1000000; k++)
                    acc += clock.NowFiletime() + (ulong)clock.MonotonicMs();
                Console.WriteLine(
                    $"clock reads: {sw2.Elapsed.TotalMilliseconds / 1000000 * 1e6:F0} ns per pair ({acc % 7})"
                );
                sw2 = Stopwatch.StartNew();
                for (int k = 0; k < 1000000; k++)
                    acc += (ulong)DateTime.UtcNow.Ticks;
                Console.WriteLine($"DateTime.UtcNow: {sw2.Elapsed.TotalMilliseconds:F0} ns");
                sw2 = Stopwatch.StartNew();
                for (int k = 0; k < 1000000; k++)
                    acc += (ulong)Stopwatch.GetTimestamp();
                Console.WriteLine($"Stopwatch.GetTimestamp: {sw2.Elapsed.TotalMilliseconds:F0} ns");
                sw2 = Stopwatch.StartNew();
                for (int k = 0; k < 1000000; k++)
                    acc += (ulong)Environment.TickCount;
                Console.WriteLine(
                    $"Environment.TickCount: {sw2.Elapsed.TotalMilliseconds:F0} ns ({acc % 3})"
                );
            }
            return 0;
        }
    }
}
