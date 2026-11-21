using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CicodeSim.Values;
using CicodeSim.World;
using WorldQuality = CicodeSim.World.Quality;

namespace CicodeSim.Host
{
    /// <summary>ctsim-dev world-run: run one function against the World.</summary>
    internal static class WorldRunCommand
    {
        public const string Usage =
            "ctsim-dev world-run --project <dir> --function <name> [args ...] [--root name] [--rdb file] [--start iso]\n"
            + "                [--set Tag=value ...] [--max-seconds s] [--max-slices n] "
            + BuiltinsOptions.Usage;

        public static int Run(List<string> args)
        {
            string? project = VmCommands.TakeOption(args, "--project");
            string? function = VmCommands.TakeOption(args, "--function");
            string? root = VmCommands.TakeOption(args, "--root");
            string? rdb = VmCommands.TakeOption(args, "--rdb");
            string? start = VmCommands.TakeOption(args, "--start");
            string? maxSeconds = VmCommands.TakeOption(args, "--max-seconds");
            string? maxSlices = VmCommands.TakeOption(args, "--max-slices");
            var bopt = BuiltinsOptions.Take(args);
            var sets = new List<string>();
            for (string? s; (s = VmCommands.TakeOption(args, "--set")) != null; )
                sets.Add(s);
            if (project == null || function == null)
            {
                Console.Error.WriteLine("usage: " + Usage);
                return 2;
            }
            var run = WorldRun.ForProject(project, root, start ?? "2026-10-01T08:00:00Z", rdb);
            bopt.Apply(run);
            foreach (string s in sets)
            {
                int eq = s.IndexOf('=');
                if (eq <= 0)
                {
                    Console.Error.WriteLine("--set needs Tag=value: " + s);
                    return 2;
                }
                string name = s.Substring(0, eq),
                    text = s.Substring(eq + 1);
                object value = double.TryParse(
                    text,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double d
                )
                    ? (object)d
                    : text;
                int err = run.World.Tags.Set(name, value, by: "user");
                if (err != 0)
                {
                    Console.Error.WriteLine($"--set {name}: error {err}");
                    return 2;
                }
            }
            var fn = run.Library.Function(function);
            var res = run.Call(
                function,
                WorldRun.ParseArgs(fn, args),
                maxSeconds != null ? double.Parse(maxSeconds, CultureInfo.InvariantCulture) : 60.0,
                maxSlices != null ? long.Parse(maxSlices, CultureInfo.InvariantCulture) : 100000
            );
            var sb = new StringBuilder();
            sb.Append("{\"project\":");
            Json.WriteString(sb, run.World.Project);
            sb.Append(",\"root\":");
            Json.WriteString(sb, run.World.Root);
            sb.Append(",\"fn\":");
            Json.WriteString(sb, fn?.Name ?? function);
            sb.Append(',');
            VmCommands.WriteRun(sb, res.Task, res.Outcome);
            if (res.Outcome == "not simulated")
            {
                sb.Append(",\"nsfn\":");
                Json.WriteString(sb, VmCommands.NotSimulatedName(res.Task));
            }
            sb.Append(",\"slices\":").AppendInv(res.Slices);
            sb.Append(",\"sim_seconds\":").Append(JsonValue.Of(res.SimSeconds).Dump());
            sb.Append(",\"written\":[");
            for (int i = 0; i < res.Changes.Count; i++)
            {
                var ch = res.Changes[i];
                if (i > 0)
                    sb.Append(',');
                string name =
                    ch.Element == "field" || ch.Element.Length == 0 ? ch.Name : ch.Name + "." + ch.Element;
                if (ch.Index != null)
                    name += "[" + ch.Index.Value.ToString(CultureInfo.InvariantCulture) + "]";
                sb.Append("{\"tag\":");
                Json.WriteString(sb, name);
                sb.Append(",\"value\":").Append(ch.Value.Dump());
                sb.Append(",\"quality\":");
                Json.WriteString(sb, WorldQuality.Name(ch.Quality));
                sb.Append('}');
            }
            sb.Append("],\"read\":[");
            var used = run.World.Tags.Used;
            for (int i = 0; i < used.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                Json.WriteString(sb, used[i]);
            }
            sb.Append("]}");
            var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                NewLine = "\n",
            };
            o.WriteLine(sb.ToString());
            o.Flush();
            return res.Task.State == Vm.TaskState.Stop ? 0 : 1;
        }
    }
}
