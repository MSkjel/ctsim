using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using CicodeSim.Compiler.Codegen;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Project;

namespace CicodeSim.Host
{
    /// <summary>ctsim commands of the source compiler: compile (write
    /// _LIBRARY.RDB from source), compile-check (compare with CtCmp32's), exec (run one function
    /// of a mixed program).</summary>
    internal static class CompilerCommands
    {
        internal static bool Handles(string cmd) =>
            cmd == "compile"
            || cmd == "compile-check"
            || cmd == "compile-pages"
            || cmd == "exec"
            || cmd == "compile-bench";

        internal const string Usage =
            "compile <project | project dir> [--user dir] [--out dir]\n"
            + "compile-check [project ...] [--user dir] [--show n]\n"
            + "compile-pages [project ...] [--user dir] [--show n] [--page name]\n"
            + "exec --project <dir> --function <name> [--file x.ci] [--args a ...] [--tags Tag=v ...] [--scenario f]\n"
            + "     [--compile changed|all|none] [--root name] [--start iso] [--max-seconds s] [--json]\n"
            + "compile-bench <project | dir> [--file x.ci] [--rounds n]";

        internal static int Dispatch(string[] args)
        {
            var rest = new List<string>(args);
            string cmd = rest[0];
            rest.RemoveAt(0);
            var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 1 << 16)
            {
                AutoFlush = false,
                NewLine = "\n",
            };
            try
            {
                switch (cmd)
                {
                    case "compile":
                        return Compile(rest, o);
                    case "compile-check":
                        return Check(rest, o);
                    case "compile-pages":
                        return Pages(rest, o);
                    case "exec":
                        return ExecCommand.Run(rest, o);
                    case "compile-bench":
                        return ExecCommand.Bench(rest, o);
                }
                return 2;
            }
            finally
            {
                o.Flush();
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

        /// <summary>(User folder, project name) of a project name or a project folder.</summary>
        internal static (string user, string name) ProjectOf(string arg, string? user)
        {
            if (Directory.Exists(arg) && Unit0Find(arg, "master.dbf") == null)
            {
                string full = WinPath.Full(arg).TrimEnd('\\', '/');
                return (user ?? WinPath.DirName(full), WinPath.BaseName(full));
            }
            return (user ?? FrontendCommands.UserDir(), arg);
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

        private static int Compile(List<string> args, TextWriter o)
        {
            string? user = Opt(args, "--user");
            string? outDir = Opt(args, "--out");
            if (args.Count != 1)
            {
                Console.Error.WriteLine("usage: ctsim " + Usage.Split('\n')[0]);
                return 2;
            }
            var (u, name) = ProjectOf(args[0], user);
            var sw = Stopwatch.StartNew();
            // a project folder: the unit of that folder (registered or not, like exec), else the
            // registered project of that name
            var unit = Directory.Exists(args[0])
                ? Units.ForProject(args[0], user).Compile()
                : CompileUnit.Build(name, u);
            double tFront = sw.Elapsed.TotalSeconds;
            var errs = unit.Errors();
            if (errs.Count > 0)
            {
                foreach (var d in errs)
                    Console.Error.WriteLine(d.ToString());
                return 1;
            }
            var img = LibraryBuilder.Build(unit);
            double tGen = sw.Elapsed.TotalSeconds - tFront;
            foreach (var (fn, why) in img.Failed)
                Console.Error.WriteLine($"not compiled: {fn}: {why}");
            byte[] bytes = RdbWriter.Write(img, RdbWriter.Stamp.Now());
            string dir = outDir ?? Path.Combine(Path.GetTempPath(), "ctsim-compile", name);
            string? project = ProjectFolderAt(dir, unit);
            if (project != null)
            {
                // never over CtCmp32's own files (a project's _LIBRARY.RDB / _FUNCSYM.DBF)
                Console.Error.WriteLine(
                    "compile: "
                        + dir
                        + " is the folder of project "
                        + project
                        + "; the compiler does not write into a project: give another --out"
                );
                return 2;
            }
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "_LIBRARY.RDB");
            File.WriteAllBytes(path, bytes);
            File.WriteAllBytes(Path.Combine(dir, "_FUNCSYM.DBF"), FuncsymWriter.Write(img, DateTime.Now));
            o.WriteLine(
                $"{path} (+ _FUNCSYM.DBF): {img.Library.Count} functions, {bytes.Length} bytes; front end {tFront:0.00} s, code {tGen:0.00} s"
            );
            return img.Failed.Count == 0 ? 0 : 1;
        }

        /// <summary>The project whose folder <paramref name="dir"/> is: a project of the unit, or a
        /// folder that holds .ci files, pages or a pages.dbf; null for any other folder.</summary>
        internal static string? ProjectFolderAt(string dir, CompileUnit unit)
        {
            string key = WinPath.Key(WinPath.Full(dir).TrimEnd('\\', '/'));
            foreach (var p in unit.Projects)
                if (
                    !string.IsNullOrEmpty(p.Folder)
                    && WinPath.Key(WinPath.Full(p.Folder!).TrimEnd('\\', '/')) == key
                )
                    return p.Name;
            var names = WinPath.ListDir(dir);
            if (names == null)
                return null;
            foreach (string fn in names)
            {
                string low = CitectText.Lower(fn);
                if (
                    low.EndsWith(".ci", StringComparison.Ordinal)
                    || low.EndsWith(".ctg", StringComparison.Ordinal)
                    || low == "pages.dbf"
                )
                    return WinPath.BaseName(WinPath.Full(dir).TrimEnd('\\', '/'));
            }
            return null;
        }

        private static int Pages(List<string> args, TextWriter o)
        {
            string? user = Opt(args, "--user");
            string? only = Opt(args, "--page");
            int show = int.Parse(Opt(args, "--show") ?? "3", CultureInfo.InvariantCulture);
            if (args.Count == 0)
                args.AddRange(new[] { "ExampleSA", "Example" });
            int bad = 0;
            foreach (string a in args)
            {
                var (u, name) = ProjectOf(a, user);
                var p = new Registry(u).Get(name);
                if (p?.Folder == null)
                {
                    o.WriteLine(name + ": not registered");
                    bad++;
                    continue;
                }
                var sw = Stopwatch.StartNew();
                var unit = CompileUnit.Build(name, u);
                var rep = PageCheck.Run(
                    unit,
                    p.Folder,
                    show,
                    only == null
                        ? null
                        : (Func<string, bool>)(
                            pg => string.Equals(pg, only, StringComparison.OrdinalIgnoreCase)
                        )
                );
                o.WriteLine(
                    $"{name}: {rep.Blocks} blocks, {rep.Same} equal, {rep.Blocks - rep.Same} different ({rep.NoSource} without a source row, {rep.Failed} not compiled); {sw.Elapsed.TotalSeconds:0.0} s"
                );
                var rules = new List<KeyValuePair<string, int>>(rep.ByRule);
                rules.Sort((x, y) => y.Value.CompareTo(x.Value));
                foreach (var kv in rules)
                    o.WriteLine($"    {kv.Value, 7}  {kv.Key}");
                foreach (string ex in rep.Examples)
                    o.WriteLine("    " + ex);
                if (rep.Same != rep.Blocks)
                    bad++;
                o.Flush();
            }
            return bad == 0 ? 0 : 1;
        }

        private static int Check(List<string> args, TextWriter o)
        {
            string? user = Opt(args, "--user");
            int show = int.Parse(Opt(args, "--show") ?? "10", CultureInfo.InvariantCulture);
            if (args.Count == 0)
                args.AddRange(new[] { "ExampleSA", "Example" });
            int bad = 0;
            foreach (string a in args)
            {
                var (u, name) = ProjectOf(a, user);
                var reg = new Registry(u);
                var p = reg.Get(name);
                if (p?.Folder == null)
                {
                    o.WriteLine(name + ": not registered");
                    bad++;
                    continue;
                }
                string rdb = Path.Combine(p.Folder, "_LIBRARY.RDB");
                var sw = Stopwatch.StartNew();
                var unit = CompileUnit.Build(name, u);
                double tFront = sw.Elapsed.TotalSeconds;
                var img = LibraryBuilder.Build(unit);
                double tGen = sw.Elapsed.TotalSeconds - tFront;
                o.WriteLine(
                    $"{name}: front end {tFront:0.00} s, code {tGen:0.00} s, {img.Failed.Count} not compiled"
                );
                for (int i = 0; i < Math.Min(show, img.Failed.Count); i++)
                    o.WriteLine("    not compiled " + img.Failed[i].name + ": " + img.Failed[i].why);
                if (!File.Exists(rdb))
                {
                    o.WriteLine("    no " + rdb);
                    continue;
                }
                var rep = LibraryCheck.Compare(img, rdb, show);
                o.WriteLine(
                    "    "
                        + rep.Summary()
                        + (rep.CountMismatch != 0 ? $" (count differs by {rep.CountMismatch})" : "")
                );
                foreach (var (idx, fn, what) in rep.Mismatches)
                    o.WriteLine($"    #{idx} {fn}: {what}");
                foreach (var (sec, same, det) in rep.Sections)
                    o.WriteLine($"    {sec, -10} {(same ? "same" : "DIFF")} {det}");
                foreach (string od in rep.Orders)
                    o.WriteLine("    " + od);
                o.WriteLine("    file " + rep.FileDetail);
                if (rep.FuncsymSame != null)
                    o.WriteLine("    _FUNCSYM.DBF " + rep.FuncsymDetail);
                if (rep.Exact != rep.Functions)
                    bad++;
                o.Flush();
            }
            return bad == 0 ? 0 : 1;
        }
    }
}
