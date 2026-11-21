using System;
using System.Collections.Generic;
using System.IO;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Vm
{
    /// <summary>Compiled-function cases recorded on the live runtime.</summary>
    public class RuntimeFunctionCasesTests
    {
        internal static readonly string Library = Path.Combine(
            CicodeSim.Tests.Host.CiSimRT.Dir,
            "_LIBRARY.RDB"
        );

        /// <summary>Cases the engine cannot run: (function, the built-in it stops at).</summary>
        private static readonly Dictionary<string, string> NotSimulated = new Dictionary<string, string>(
            StringComparer.Ordinal
        );

        internal static string CasesDir()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                string p = Path.Combine(d.FullName, "conformance", "runtime", "functions");
                if (Directory.Exists(p) && File.Exists(Path.Combine(d.FullName, "engine", "CicodeSim.sln")))
                    return p;
            }
            return "";
        }

        /// <summary>Every recorded case's function run in <paramref name="prog"/> with the World
        /// registry (a new VM a case): the cases compared and a line per case whose result differs
        /// from the live one.</summary>
        internal static (int Compared, List<string> Bad) Replay(Program prog)
        {
            var bad = new List<string>();
            int compared = 0;
            foreach (string path in Directory.GetFiles(CasesDir(), "*.json"))
            {
                var c = (Dictionary<string, object?>)Json.Parse(File.ReadAllText(path))!;
                if (!(c.TryGetValue("ok", out var ok) && ok is bool b && b))
                    continue;
                string fn = (string)c["function"]!;
                string want = (string)c["result"]!;
                var reg = WorldBuiltins.Create(
                    (CicodeSim.World.World?)null,
                    new SimHostOptions { Sandbox = new SandboxOptions { InMemory = true } }
                );
                var vm = new CicodeVm(
                    prog,
                    reg,
                    null,
                    new ManualClock(tickMs: 1.0),
                    null,
                    new CodeConfig { MaxSliceOps = 20000 }
                );
                var task = vm.TaskForCall(fn, Array.Empty<Value>());
                for (int n = 0; n < 50 && !task.Done; n++)
                    vm.RunSlice(task);
                if (NotSimulated.TryGetValue(fn, out string? builtin))
                {
                    Assert.Contains(builtin, task.HaltReason);
                    continue;
                }
                compared++;
                string got =
                    task.State == TaskState.Stop && task.Result != null && task.Result.Value.T == Types.String
                        ? task.Result.Value.S ?? ""
                        : $"(state {task.StateName}, halt {task.HaltCode} {task.HaltReason})";
                if (got != want)
                    bad.Add($"{fn}: live {want}\n  engine {got}");
            }
            return (compared, bad);
        }

        [RuntimeCasesFact]
        public void CompiledFunctionsGiveTheLiveRuntimesResults()
        {
            var (compared, bad) = Replay(Rdb.LoadLibrary(Library));
            Assert.True(compared >= 14, "cases compared: " + compared);
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }
    }

    /// <summary>Skipped without the kept CiSimRT library or the cases folder.</summary>
    public sealed class RuntimeCasesFactAttribute : FactAttribute
    {
        public RuntimeCasesFactAttribute()
        {
            if (
                !File.Exists(RuntimeFunctionCasesTests.Library)
                || RuntimeFunctionCasesTests.CasesDir().Length == 0
            )
                Skip = "no " + RuntimeFunctionCasesTests.Library + " (or no conformance/runtime/functions)";
        }
    }
}
