using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using CicodeSim.Sim;
using CicodeSim.Values;

namespace CicodeSim.Host
{
    /// <summary>ctsim serve / ctsim dap: one simulation of a project behind a named pipe.</summary>
    internal static class ServeCommand
    {
        public const string Usage =
            "ctsim serve --project <dir> [--id id] [--root name] [--rdb file] [--compile changed|all|none]\n"
            + "            [--start iso] [--clock run|paused] [--speed x] [--step seconds] [--scan ms] [--trace file]\n"
            + "            [--startup function|auto] [--tz zone] [--discovery dir] [--log]\n"
            + "            [--simulate-undefined] [--seed-trend-history]\n"
            + "            [--dll off|ask|on] [--sql off|ask|on] [--dll-allow path]... [--sql-allow database]...\n"
            + "            [--dll-timeout seconds]\n"
            + "ctsim dap --project <dir> (the same options)";

        public static int Run(List<string> args, bool dap)
        {
            Stream? dapIn = null,
                dapOut = null;
            if (dap)
            {
                // the stdio session owns stdout: nothing else may write there
                dapOut = Console.OpenStandardOutput();
                dapIn = Console.OpenStandardInput();
                Console.SetOut(Console.Error);
            }
            string? project = CliArgs.TakeOption(args, "--project");
            string? id = CliArgs.TakeOption(args, "--id");
            string? discovery = CliArgs.TakeOption(args, "--discovery");
            bool log = TakeFlag(args, "--log");
            var o = new SimulationOptions();
            o.SimulateUndefined = TakeFlag(args, "--simulate-undefined");
            o.SeedTrendHistory = TakeFlag(args, "--seed-trend-history");
            string? dllMode = CliArgs.TakeOption(args, "--dll"),
                sqlMode = CliArgs.TakeOption(args, "--sql");
            for (
                string? x = CliArgs.TakeOption(args, "--dll-allow");
                x != null;
                x = CliArgs.TakeOption(args, "--dll-allow")
            )
                o.DllAllowed.Add(x);
            for (
                string? x = CliArgs.TakeOption(args, "--sql-allow");
                x != null;
                x = CliArgs.TakeOption(args, "--sql-allow")
            )
                o.SqlAllowed.Add(x);
            string? dllTimeout = CliArgs.TakeOption(args, "--dll-timeout");
            o.Root = CliArgs.TakeOption(args, "--root");
            o.Rdb = CliArgs.TakeOption(args, "--rdb");
            o.Compile = CliArgs.TakeOption(args, "--compile") ?? CodeSource.Changed;
            string? start = CliArgs.TakeOption(args, "--start");
            o.Start = start;
            o.ClockMode = CliArgs.TakeOption(args, "--clock") ?? "run";
            string? speed = CliArgs.TakeOption(args, "--speed");
            string? step = CliArgs.TakeOption(args, "--step");
            string? scan = CliArgs.TakeOption(args, "--scan");
            o.TracePath = CliArgs.TakeOption(args, "--trace");
            o.Startup = CliArgs.TakeOption(args, "--startup");
            string? tz = CliArgs.TakeOption(args, "--tz");
            if (project == null || args.Count > 0)
            {
                Console.Error.WriteLine("usage: " + Usage.Replace("\n", "\n       "));
                if (args.Count > 0)
                    Console.Error.WriteLine("unknown argument: " + args[0]);
                return 2;
            }
            if (
                (dllMode != null && !CicodeSim.Builtins.OutsideAccess.TryParse(dllMode, out o.Dll))
                || (sqlMode != null && !CicodeSim.Builtins.OutsideAccess.TryParse(sqlMode, out o.Sql))
            )
            {
                Console.Error.WriteLine("--dll and --sql take off, ask or on");
                return 2;
            }
            if (dllTimeout != null)
            {
                if (
                    !int.TryParse(
                        dllTimeout,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int secs
                    )
                    || secs < 0
                )
                {
                    Console.Error.WriteLine("--dll-timeout takes seconds (0 = no limit): " + dllTimeout);
                    return 2;
                }
                o.DllTimeoutSeconds = secs;
            }
            if (id != null && !ValidId(id))
            {
                Console.Error.WriteLine("--id takes letters, digits, '-', '_' and '.': " + id);
                return 2;
            }
            // bad numbers are bad arguments (exit 2), not an unhandled exception; a scan of 0 ms
            // would make the simulation loop spin
            try
            {
                o.Speed = Num(speed, 1.0);
                o.StepSeconds = Num(step, 0.25);
                double scanMs = Num(scan, 250);
                if (!(o.Speed > 0) || double.IsInfinity(o.Speed))
                    throw new ArgumentException("--speed must be a number above 0: " + speed);
                if (!(o.StepSeconds > 0) || double.IsInfinity(o.StepSeconds))
                    throw new ArgumentException("--step must be seconds above 0: " + step);
                if (!(scanMs >= 1 && scanMs <= 3_600_000) || scanMs != Math.Floor(scanMs))
                    throw new ArgumentException("--scan must be whole milliseconds, 1 to 3600000: " + scan);
                o.ScanMs = (int)scanMs;
                if (start != null)
                    CicodeSim.World.SimClock.FiletimeFromUnix(CicodeSim.World.SimClock.ParseStart(start));
            }
            catch (Exception e)
                when (e is ArgumentException || e is FormatException || e is OverflowException)
            {
                Console.Error.WriteLine("ctsim " + (dap ? "dap" : "serve") + ": " + e.Message);
                return 2;
            }
            o.ProjectDir = project;
            if (tz != null)
            {
                try
                {
                    o.TimeZone = TimeZoneInfo.FindSystemTimeZoneById(tz);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine("--tz " + tz + ": " + e.Message);
                    return 2;
                }
                // a --start without 'Z' or an offset is a wall time of the simulated computer's
                // zone (--tz), not this computer's
                if (start != null)
                {
                    try
                    {
                        var iso = CicodeSim.World.IsoDateTime.Parse(start);
                        if (iso.OffsetSeconds == null)
                        {
                            var naive = new DateTime(
                                iso.Year,
                                iso.Month,
                                iso.Day,
                                0,
                                0,
                                0,
                                DateTimeKind.Unspecified
                            )
                                .AddHours(iso.Hour)
                                .AddMinutes(iso.Minute)
                                .AddSeconds(iso.Second)
                                .AddTicks(iso.Microsecond * 10L);
                            o.Start = TimeZoneInfo.ConvertTimeToUtc(naive, o.TimeZone);
                        }
                    }
                    catch (Exception e) when (e is ArgumentException || e is FormatException)
                    {
                        Console.Error.WriteLine(
                            "ctsim "
                                + (dap ? "dap" : "serve")
                                + ": --start "
                                + start
                                + " in "
                                + tz
                                + ": "
                                + e.Message
                        );
                        return 2;
                    }
                }
            }
            if (o.ClockMode != "run" && o.ClockMode != "paused" && o.ClockMode != "pause")
            {
                Console.Error.WriteLine("--clock must be run or paused: " + o.ClockMode);
                return 2;
            }
            Simulation? made = null;
            // a simulation's libraries run in a helper process (ctsim dllhost): one that crashes or
            // hangs does not take the simulator along
            CicodeSim.Builtins.Dll.RunnerFactory = host =>
            {
                var r = CicodeSim.Builtins.DllChildRunner.ForThisProcess(
                    typeof(ServeCommand).Assembly.Location
                );
                r.Log = line => made?.Note("info", line);
                return r;
            };
            Simulation sim;
            try
            {
                sim = new Simulation(o);
                made = sim;
            }
            catch (Exception e)
            {
                // any failure to load (a missing or damaged RDB / DBF ...) is exit code 1 with its
                // message, never an unhandled exception
                bool plain =
                    e is IOException
                    || e is ArgumentException
                    || e is NotSupportedException
                    || e is FormatException
                    || e is UnauthorizedAccessException
                    || e is InvalidOperationException;
                string why = plain ? e.Message : e.GetType().Name + ": " + e.Message;
                Console.Error.WriteLine("ctsim " + (dap ? "dap" : "serve") + ": " + why);
                // dap: the stdio session stays up and says why (VS Code would show only 'debug
                // adapter exited'); exit code 1 when it ends
                if (dap)
                    return StartFailedSession.Run(dapIn!, dapOut!, "ctsim cannot load the project: " + why);
                return 1;
            }
            var server = new SimServer(sim, id, dap ? "dap" : "serve") { DapFactory = DapSessions.Create };
            var done = new ManualResetEventSlim(false);
            string reason = "";
            server.ShutdownRequested += why =>
            {
                reason = why;
                done.Set();
            };
            if (log)
                sim.Log += e => Console.Error.WriteLine("[" + e.Level + "] " + e.Text);
            ConsoleCancelEventHandler onCancel = (s, e) =>
            {
                e.Cancel = true;
                reason = "Ctrl+C";
                done.Set();
            };
            Console.CancelKeyPress += onCancel;
            EventHandler onExit = (s, e) =>
            {
                // the process ends without going through the code below: remove the discovery file
                try
                {
                    server.Dispose();
                }
                catch (Exception) { }
            };
            AppDomain.CurrentDomain.ProcessExit += onExit;
            try
            {
                sim.Start();
                try
                {
                    server.Listen(discovery);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine("ctsim: cannot listen: " + e.Message);
                    sim.Dispose();
                    if (dap)
                        return StartFailedSession.Run(
                            dapIn!,
                            dapOut!,
                            "ctsim cannot open its pipe: " + e.Message
                        );
                    return 1;
                }
                if (dap)
                    server.AddConnection(dapIn!, dapOut!, "stdio", stdio: true);
                else
                    WriteReady(server, sim);
                done.Wait();
                if (log)
                    Console.Error.WriteLine("ctsim: ending (" + reason + ")");
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
                server.Dispose();
                sim.Dispose();
                AppDomain.CurrentDomain.ProcessExit -= onExit;
            }
            return 0;
        }

        private static void WriteReady(SimServer server, Simulation sim)
        {
            var o = JsonValue.NewObject();
            o.Set("event", "ready");
            o.Set("id", server.Id);
            o.Set("pipe", server.Listener!.Transport == Listener.Pipe ? server.Listener.Address : "");
            o.Set("transport", server.Listener.Transport);
            o.Set("address", server.Listener.Address);
            o.Set("pid", JsonValue.Of(Discovery.CurrentPid));
            o.Set("discovery", server.DiscoveryPath ?? "");
            o.Set("project", sim.ProjectDir);
            o.Set("root", sim.Root);
            o.Set("rootReason", sim.RootReason);
            var roots = JsonValue.NewArray();
            foreach (string r in sim.RootCandidates)
                roots.Add(JsonValue.Of(r));
            o.Set("roots", roots);
            o.Set("loadMs", JsonValue.Of(sim.LoadMs));
            var w = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                NewLine = "\n",
            };
            w.WriteLine(o.Dump());
            w.Flush();
        }

        private static bool ValidId(string id)
        {
            if (id.Length == 0 || id.Length > 100)
                return false;
            foreach (char c in id)
                if (!(char.IsLetterOrDigit(c) && c < 128) && c != '-' && c != '_' && c != '.')
                    return false;
            return true;
        }

        private static bool TakeFlag(List<string> args, string flag)
        {
            int i = args.IndexOf(flag);
            if (i < 0)
                return false;
            args.RemoveAt(i);
            return true;
        }

        private static double Num(string? s, double def)
        {
            if (s == null)
                return def;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                throw new ArgumentException("not a number: " + s);
            return d;
        }
    }
}
