using System;

namespace CicodeSim.Host
{
    internal static class Program
    {
        internal static int Main(string[] args)
        {
            Invariant.Pin();
            if (args.Length > 0 && args[0] == "--version")
                return Version(args);
            if (args.Length > 0)
            {
                var rest = new System.Collections.Generic.List<string>(args);
                rest.RemoveAt(0);
                if (args[0] == "serve" || args[0] == "dap")
                    StartJitProfile(args[0]);
                switch (args[0])
                {
                    case "serve":
                        return ServeCommand.Run(rest, dap: false);
                    case "dap":
                        return ServeCommand.Run(rest, dap: true);
                    case "servers":
                        return ServersCommand(rest);
                    // the library helper of a simulation (Builtins.DllChildRunner): JSON lines on stdin / stdout
                    case "dllhost":
                        return CicodeSim.Builtins.DllServer.Main();
                }
            }
            // the development and corpus commands (pcode, check, run, sweep, world-*, page-run,
            // compile, exec, frontend probes ...) are in ctsim-dev.exe, not shipped
            Console.Error.WriteLine("usage: ctsim --version [--json]");
            foreach (string u in ServeCommand.Usage.Split('\n'))
                Console.Error.WriteLine("       " + u);
            Console.Error.WriteLine("       " + ServersUsage);
            Console.Error.WriteLine("(the development commands are in ctsim-dev.exe)");
            return 2;
        }

        /// <summary>Multicore JIT: the methods a serve or dap run compiled are compiled on other
        /// cores at the next start, ahead of the thread that needs them (the profile is written
        /// when the process ends, in %LOCALAPPDATA%\cicode-sim\jit). A start without it when the
        /// folder cannot be made.</summary>
        private static void StartJitProfile(string command)
        {
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(local))
                    return;
                string dir = System.IO.Path.Combine(local, "cicode-sim", "jit");
                System.IO.Directory.CreateDirectory(dir);
                System.Runtime.ProfileOptimization.SetProfileRoot(dir);
                System.Runtime.ProfileOptimization.StartProfile(command + ".jitprofile");
            }
            catch (Exception) { }
        }

        private const string VersionUsage = "ctsim --version [--json]";
        private const string ServersUsage = "ctsim servers [--discovery dir] [--project dir [--root name]]";

        internal static int Version(string[] args)
        {
            var rest = new System.Collections.Generic.List<string>(args);
            rest.RemoveAt(0);
            bool json = CliArgs.TakeFlag(rest, "--json");
            if (!CliArgs.NoneLeft(rest, "--version", VersionUsage))
                return 2;
            if (json)
            {
                var o = CicodeSim.Values.JsonValue.NewObject();
                o.Set("name", EngineInfo.Name);
                o.Set("version", EngineInfo.Version);
                o.Set("protocolRevision", CicodeSim.Values.JsonValue.Of(EngineInfo.ProtocolRevision));
                o.Set("commit", EngineInfo.Commit);
                o.Set("buildTime", EngineInfo.BuildTime);
                Console.WriteLine(o.ToString());
            }
            else
                Console.WriteLine(EngineInfo.Describe());
            return 0;
        }

        /// <summary>ctsim servers: the live servers of the discovery folder, one JSON line each
        /// (stale entries are cleaned).</summary>
        private static int ServersCommand(System.Collections.Generic.List<string> args)
        {
            string? folder,
                project,
                root;
            try
            {
                folder = CliArgs.TakeOption(args, "--discovery");
                project = CliArgs.TakeOption(args, "--project");
                root = CliArgs.TakeOption(args, "--root");
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine("ctsim servers: " + e.Message);
                Console.Error.WriteLine("usage: " + ServersUsage);
                return 2;
            }
            // (a misspelt filter is refused: listing every server for it reads as a match)
            if (!CliArgs.NoneLeft(args, "servers", ServersUsage))
                return 2;
            var w = new System.IO.StreamWriter(
                Console.OpenStandardOutput(),
                new System.Text.UTF8Encoding(false)
            )
            {
                NewLine = "\n",
            };
            foreach (var e in Discovery.List(folder))
                if (project == null || e.Matches(project, root))
                    w.WriteLine(e.ToJson().Dump());
            w.Flush();
            return 0;
        }
    }
}
