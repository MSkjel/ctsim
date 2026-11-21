using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;
using Rdb = CicodeSim.Compiled.Rdb;

namespace CicodeSim.Host
{
    /// <summary>ctsim-dev world-sweep: every function of each library against the World.</summary>
    internal static class WorldSweepCommand
    {
        public const string Usage =
            "ctsim-dev world-sweep [project ...] [--user dir] [--start iso] " + BuiltinsOptions.Usage;

        private static readonly long[] Longs = { 0, 3, -1 };
        private static readonly double[] Reals = { 0.0, 2.5, -1e9 };
        private static readonly string[] Strs = { "", "abc def", "Plant.Area.Pump1(1,2)" };

        /// <summary>One record of the sweep.</summary>
        internal sealed class Record
        {
            public string Lib = "";
            public Function? Fn;
            public int Set;
            public CicodeTask? Task;
            public string Outcome = "";
            public double Now;
            public List<TagChange> Changes = new List<TagChange>();
            public bool End;
            public List<string> Used = new List<string>();
        }

        /// <summary>The sweep's records, project by project (sorted by folder name), as
        /// `ctsim-dev world-sweep` writes them; also read by the golden tests.</summary>
        internal static IEnumerable<Record> Records(
            string user,
            string start,
            BuiltinsOptions bopt,
            ICollection<string> only
        )
        {
            var dirs = Directory
                .GetDirectories(user)
                .Where(d => File.Exists(Path.Combine(d, "_LIBRARY.RDB")))
                .OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal);
            var rec = new Record();
            foreach (string dir in dirs)
            {
                string name = Path.GetFileName(dir);
                if (only.Count > 0 && !only.Contains(name))
                    continue;
                var world = new CicodeSim.World.World(dir, root: name, start: start);
                world.Start();
                var prog = Rdb.LoadLibrary(Path.Combine(dir, "_LIBRARY.RDB"));
                var run = new WorldRun(world, prog);
                bopt.Apply(run);
                var changes = new List<TagChange>();
                rec.Changes = changes;
                world.Tags.Subscribe(ch =>
                {
                    if (ch.By == "cicode")
                        changes.Add(ch);
                });
                foreach (var fn in prog.Functions)
                {
                    for (int set = 0; set < 3; set++)
                    {
                        var vals = new List<Value>();
                        foreach (char c in fn.Args)
                        {
                            int t = Types.SigType(c);
                            if (t == Types.Long)
                                vals.Add(Value.Long(Longs[set]));
                            else if (t == Types.Real)
                                vals.Add(Value.Real(Reals[set]));
                            else if (t == Types.String)
                                vals.Add(Value.Str(Strs[set]));
                            else if (t == Types.Object)
                                vals.Add(Value.Object(-1));
                            else
                                vals.Add(Value.Long(0));
                        }
                        changes.Clear();
                        var task = run.Scheduler.Spawn(fn, vals);
                        int n = 0;
                        while (!task.Done && n < 50)
                        {
                            if (run.Scheduler.Step())
                            {
                                n++;
                                continue;
                            }
                            if (task.State == TaskState.Wait || task.State == TaskState.Sleep)
                            {
                                world.Clock.Advance(0.25);
                                n++;
                                continue;
                            }
                            break;
                        }
                        bool waited =
                            !task.Done && (task.State == TaskState.Wait || task.State == TaskState.Sleep);
                        rec.Outcome =
                            waited ? "waits"
                            : task.State == TaskState.Stop ? "stop"
                            : task.State == TaskState.Halt
                                ? (
                                    task.HaltReason.Contains("not simulated")
                                        ? "not simulated"
                                        : "halt "
                                            + task.HaltCode.ToString(
                                                System.Globalization.CultureInfo.InvariantCulture
                                            )
                                )
                            : !task.Done && n >= 50 ? "still running"
                            : "state "
                                + task.State.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        rec.Lib = name;
                        rec.Fn = fn;
                        rec.Set = set;
                        rec.Task = task;
                        rec.Now = world.Clock.Now();
                        rec.End = false;
                        yield return rec;
                        foreach (var t in run.Scheduler.Live())
                            run.Vm.Halt(t, "sweep");
                        run.Scheduler.Step();
                    }
                }
                rec.Lib = name;
                rec.Fn = null;
                rec.Task = null;
                rec.End = true;
                rec.Used = world.Tags.Used;
                yield return rec;
            }
        }

        public static int Run(List<string> args)
        {
            string user = VmCommands.TakeOption(args, "--user")
                ?? @"C:\ProgramData\AVEVA Plant SCADA 2023 R2\User";
            string start = VmCommands.TakeOption(args, "--start") ?? "2026-10-01T08:00:00Z";
            var bopt = BuiltinsOptions.Take(args);
            var only = new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);
            var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                NewLine = "\n",
            };
            var sb = new StringBuilder();
            foreach (var r in Records(user, start, bopt, only))
            {
                sb.Clear();
                sb.Append("{\"lib\":");
                Json.WriteString(sb, r.Lib);
                if (r.End)
                {
                    sb.Append(",\"end\":true,\"used\":[");
                    for (int i = 0; i < r.Used.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(',');
                        Json.WriteString(sb, r.Used[i]);
                    }
                    sb.Append("]}");
                    o.WriteLine(sb.ToString());
                    o.Flush();
                    continue;
                }
                var task = r.Task!;
                sb.Append(",\"fn\":");
                Json.WriteString(sb, r.Fn!.Name);
                sb.Append(",\"set\":").AppendInv(r.Set).Append(',');
                VmCommands.WriteRun(sb, task, r.Outcome);
                if (r.Outcome == "not simulated")
                {
                    sb.Append(",\"nsfn\":");
                    Json.WriteString(sb, VmCommands.NotSimulatedName(task));
                }
                sb.Append(",\"now\":");
                Json.WriteString(sb, FloatText.Shortest(r.Now));
                sb.Append(",\"changes\":[");
                for (int i = 0; i < r.Changes.Count; i++)
                {
                    var ch = r.Changes[i];
                    if (i > 0)
                        sb.Append(',');
                    sb.Append('[');
                    Json.WriteString(sb, ch.Name);
                    sb.Append(',');
                    Json.WriteString(sb, ch.Element);
                    sb.Append(',');
                    if (ch.Index == null)
                        sb.Append("null");
                    else
                        sb.AppendInv(ch.Index.Value);
                    sb.Append(',').Append(ch.Value.Dump()).Append(',').AppendInv(ch.Quality).Append(']');
                }
                sb.Append("]}");
                o.WriteLine(sb.ToString());
            }
            o.Flush();
            return 0;
        }
    }
}
