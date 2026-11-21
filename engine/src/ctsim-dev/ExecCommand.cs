using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using CicodeSim.Compiled;
using CicodeSim.Compiler.Codegen;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.World;
using WorldQuality = CicodeSim.World.Quality;

namespace CicodeSim.Host
{
    /// <summary>ctsim-dev exec (run one function from source against the World) and
    /// compile-bench (the source compiler's timings: cold, warm, one edited file).</summary>
    internal static class ExecCommand
    {
        /// <summary>The values after a list option up to the next '--' option.</summary>
        private static List<string> TakeList(List<string> args, string name)
        {
            var o = new List<string>();
            int k = args.IndexOf(name);
            while (k >= 0)
            {
                int j = k + 1;
                while (j < args.Count && !args[j].StartsWith("--", StringComparison.Ordinal))
                {
                    o.Add(args[j]);
                    j++;
                }
                args.RemoveRange(k, j - k);
                k = args.IndexOf(name);
            }
            return o;
        }

        internal static int Run(List<string> args, TextWriter o)
        {
            string? project = VmCommands.TakeOption(args, "--project");
            string? function = VmCommands.TakeOption(args, "--function");
            string? file = VmCommands.TakeOption(args, "--file");
            string? scenario = VmCommands.TakeOption(args, "--scenario");
            string mode = VmCommands.TakeOption(args, "--compile") ?? MixedProgram.Changed;
            string? root = VmCommands.TakeOption(args, "--root");
            string? start = VmCommands.TakeOption(args, "--start");
            string? maxSeconds = VmCommands.TakeOption(args, "--max-seconds");
            bool json = VmCommands.TakeFlag(args, "--json");
            var callArgs = TakeList(args, "--args");
            var tags = TakeList(args, "--tags");
            if (project == null && file != null)
                project = WinPath.DirName(WinPath.Full(file));
            if (project == null || function == null || args.Count > 0)
            {
                Console.Error.WriteLine(
                    "usage: ctsim "
                        + CompilerCommands.Usage.Split('\n')[3]
                        + "\n            "
                        + CompilerCommands.Usage.Split('\n')[4]
                );
                return 2;
            }
            var sw = Stopwatch.StartNew();
            MixedProgram.Result built;
            try
            {
                built = MixedProgram.Load(
                    project,
                    mode,
                    null,
                    null,
                    null,
                    file != null ? new[] { file } : null
                );
            }
            catch (Exception e)
                when (e is FileNotFoundException || e is DirectoryNotFoundException || e is ArgumentException)
            {
                Console.Error.WriteLine(e.Message);
                return 2;
            }
            double compileMs = sw.Elapsed.TotalMilliseconds;
            foreach (var d in built.Errors)
                Console.Error.WriteLine(d.ToString());
            foreach (var (fn, why) in built.Failed)
                Console.Error.WriteLine("not compiled: " + fn + ": " + why);
            if (file != null && built.Source != null)
            {
                // the file run from does not compile (its own errors: the unit's list stops at the
                // first file with an error)
                var own = LiveProgram.FileErrors(built.Source.Unit, file);
                if (own.Count > 0)
                {
                    foreach (var d in own)
                        if (!built.Errors.Contains(d))
                            Console.Error.WriteLine(d.ToString());
                    return 1;
                }
            }
            // a PRIVATE function of the file is name@file@project
            string name = function;
            if (file != null && built.Source != null)
            {
                string fk = WinPath.Key(file),
                    key = CicodeSim.Compiler.Syntax.Lexer.NameKey(function);
                foreach (var info in built.Source.Unit.Functions)
                    if (info.Private && info.Key == key && WinPath.Key(info.File) == fk)
                    {
                        name = info.SymbolName;
                        break;
                    }
            }
            string dir = WinPath.IsDir(project)
                ? project
                : (new Registry(Units.DefaultUser).Get(project)?.Folder ?? project);
            var world = new World.World(
                dir.TrimEnd('\\', '/'),
                root ?? WinPath.BaseName(dir.TrimEnd('\\', '/')),
                start: start ?? "2026-10-01T08:00:00Z"
            );
            world.Start();
            var run = new WorldRun(world, built.Program);
            if (scenario != null)
            {
                var runner = new ScenarioRunner(world, Scenario.Load(scenario));
                runner.Setup();
            }
            foreach (string s in tags)
            {
                int eq = s.IndexOf('=');
                if (eq <= 0)
                {
                    Console.Error.WriteLine("--tags needs Tag=value: " + s);
                    return 2;
                }
                string tn = s.Substring(0, eq),
                    text = s.Substring(eq + 1);
                object value = double.TryParse(
                    text,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double dv
                )
                    ? (object)dv
                    : text;
                int err = world.Tags.Set(tn, value, by: "user");
                if (err != 0)
                {
                    Console.Error.WriteLine($"--tags {tn}: error {err}");
                    return 2;
                }
            }
            var fnObj = run.Library.Function(name);
            if (fnObj == null)
            {
                Console.Error.WriteLine("no function " + function + (file != null ? " in " + file : ""));
                return 2;
            }
            var res = run.Call(
                fnObj.Name,
                WorldRun.ParseArgs(fnObj, callArgs),
                maxSeconds != null ? double.Parse(maxSeconds, CultureInfo.InvariantCulture) : 60.0
            );
            if (json)
            {
                var sb = new StringBuilder();
                sb.Append("{\"project\":");
                Json.WriteString(sb, world.Project);
                sb.Append(",\"fn\":");
                Json.WriteString(sb, fnObj.Name);
                sb.Append(",\"compile\":");
                Json.WriteString(sb, mode);
                sb.Append(",\"source\":[");
                for (int i = 0; i < built.SourceFiles.Count && mode != MixedProgram.All; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    Json.WriteString(sb, built.SourceFiles[i]);
                }
                sb.Append("],\"compile_ms\":").Append(JsonValue.Of(Math.Round(compileMs, 1)).Dump());
                sb.Append(',');
                VmCommands.WriteRun(sb, res.Task, res.Outcome);
                if (res.Outcome == "not simulated")
                {
                    sb.Append(",\"nsfn\":");
                    Json.WriteString(sb, VmCommands.NotSimulatedName(res.Task));
                }
                sb.Append(",\"sim_seconds\":").Append(JsonValue.Of(res.SimSeconds).Dump());
                sb.Append(",\"written\":[");
                for (int i = 0; i < res.Changes.Count; i++)
                {
                    var ch = res.Changes[i];
                    if (i > 0)
                        sb.Append(',');
                    string tn =
                        ch.Element == "field" || ch.Element.Length == 0
                            ? ch.Name
                            : ch.Name + "." + ch.Element;
                    sb.Append("{\"tag\":");
                    Json.WriteString(sb, tn);
                    sb.Append(",\"value\":").Append(ch.Value.Dump());
                    sb.Append(",\"quality\":");
                    Json.WriteString(sb, WorldQuality.Name(ch.Quality));
                    sb.Append('}');
                }
                sb.Append("]}");
                o.WriteLine(sb.ToString());
            }
            else
            {
                var t = res.Task;
                if (t.State == Vm.TaskState.Stop)
                    o.WriteLine(
                        fnObj.Name + " returned " + (t.Result != null ? Show(t.Result.Value) : "(nothing)")
                    );
                else
                    o.WriteLine(
                        fnObj.Name
                            + ": "
                            + res.Outcome
                            + (t.HaltReason.Length > 0 ? " (" + t.HaltReason + ")" : "")
                    );
            }
            return res.Task.State == Vm.TaskState.Stop ? 0 : 1;
        }

        private static string Show(Value v) => v.T == Types.String ? "\"" + (v.S ?? "") + "\"" : v.ToString();

        /// <summary>compile-bench: the front end and code generation of a whole unit, cold and warm,
        /// and one file edited (parse + resolve + code of that file, linked into the RDB).</summary>
        internal static int Bench(List<string> args, TextWriter o)
        {
            string? file = VmCommands.TakeOption(args, "--file");
            int rounds = int.Parse(
                VmCommands.TakeOption(args, "--rounds") ?? "5",
                CultureInfo.InvariantCulture
            );
            if (args.Count != 1)
            {
                Console.Error.WriteLine("usage: ctsim " + CompilerCommands.Usage.Split('\n')[5]);
                return 2;
            }
            string project = args[0];
            var sw = Stopwatch.StartNew();
            var lib = SourceLibrary.Open(project);
            double front = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var img = lib.Image();
            double gen = sw.Elapsed.TotalMilliseconds;
            o.WriteLine($"cold: front end {front:0} ms, code {gen:0} ms ({img.Library.Count} functions)");
            sw.Restart();
            var lib2 = SourceLibrary.Open(project);
            double front2 = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            lib2.Image();
            o.WriteLine(
                $"warm: front end {front2:0} ms, code {sw.Elapsed.TotalMilliseconds:0} ms; cached code {Time(() => lib2.Image()):0} ms"
            );
            string dir = WinPath.IsDir(project)
                ? project
                : (new Registry(Units.DefaultUser).Get(project)?.Folder ?? project);
            string rdb = Path.Combine(dir, "_LIBRARY.RDB");
            var baseProg = File.Exists(rdb) ? Rdb.LoadLibrary(rdb) : img.ToProgram();
            string f = file ?? LargestRootFile(lib2);
            string text = CicodeSim.Compiler.Syntax.Lexer.SourceText(File.ReadAllBytes(f));
            var times = new List<double>();
            bool incremental = true;
            for (int i = 0; i < rounds; i++)
            {
                sw.Restart();
                var e = lib2.Edit(f, text + (i % 2 == 0 ? "\r\n" : ""));
                lib2.Link(baseProg, new[] { f });
                times.Add(sw.Elapsed.TotalMilliseconds);
                incremental &= e.Incremental;
            }
            times.Sort();
            o.WriteLine(
                $"one file edited ({Path.GetFileName(f)}, {text.Split('\n').Length} lines, {(incremental ? "incremental" : "full rebuild")}): "
                    + $"median {times[times.Count / 2]:0.0} ms, min {times[0]:0.0} ms over {rounds} (parse + resolve + code + link into the RDB)"
            );
            sw.Restart();
            var e2 = lib2.Edit(f, text + "\r\nINT FUNCTION ZzBenchShapeChange() RETURN 1; END\r\n");
            o.WriteLine(
                $"declarations changed: {(e2.Incremental ? "incremental" : "whole unit again")} {sw.Elapsed.TotalMilliseconds:0} ms"
            );
            return 0;
        }

        private static double Time(Action a)
        {
            var sw = Stopwatch.StartNew();
            a();
            return sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>The biggest .ci of the unit's root project (of the unit when the root has none).</summary>
        private static string LargestRootFile(SourceLibrary lib)
        {
            var root = lib.Unit.Projects[lib.Unit.Projects.Count - 1];
            string best = "";
            long size = -1;
            bool any = lib.Unit.Files.Exists(x => x.Project == root);
            foreach (var f in lib.Unit.Files)
            {
                if (any && f.Project != root)
                    continue;
                long n = new FileInfo(f.Path).Length;
                if (n > size)
                {
                    size = n;
                    best = f.Path;
                }
            }
            return best;
        }
    }
}
