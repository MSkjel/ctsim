using System;

namespace CicodeSim.Host
{
    /// <summary>ctsim-dev: the development and corpus commands (ctsim.exe keeps serve / dap /
    /// servers; ctsim-dev also answers those, so one exe does everything on a developer box).</summary>
    internal static class DevProgram
    {
        internal static int Main(string[] args)
        {
            Invariant.Pin();
            if (args.Length > 0 && CicodeSim.World.WorldCli.Handles(args[0]))
                return CicodeSim.World.WorldCli.Main(args);
            if (args.Length > 0 && FrontendCommands.Handles(args[0]))
                return FrontendCommands.Dispatch(args);
            if (args.Length > 0 && PageCommands.Handles(args[0]))
                return PageCommands.Dispatch(args);
            if (args.Length > 0 && CompilerCommands.Handles(args[0]))
                return CompilerCommands.Dispatch(args);
            if (args.Length > 0)
            {
                var rest = new System.Collections.Generic.List<string>(args);
                rest.RemoveAt(0);
                switch (args[0])
                {
                    case "--version":
                        return Program.Version(args);
                    case "pcode":
                        return VmCommands.PcodeListing(rest);
                    case "check":
                        return VmCommands.Check(rest);
                    case "run":
                        return VmCommands.Run(rest);
                    case "sweep":
                        return VmCommands.Sweep(rest);
                    case "bench":
                        return VmCommands.Bench(rest);
                    case "world-run":
                        return WorldRunCommand.Run(rest);
                    case "world-sweep":
                        return WorldSweepCommand.Run(rest);
                    case "builtins-behaviour":
                        return BehaviourCommand.Run(rest);
                    case "serve":
                    case "dap":
                    case "servers":
                        return Program.Main(args);
                }
            }
            Console.Error.WriteLine("usage: ctsim-dev --version");
            Console.Error.WriteLine(
                "       ctsim-dev pcode <rdb> [function | table | label ...] [--out file]"
            );
            Console.Error.WriteLine("       ctsim-dev check [UserDir] [--pages project ...]");
            Console.Error.WriteLine("       ctsim-dev run <_LIBRARY.RDB> <function> [args ...] [--json]");
            Console.Error.WriteLine(
                "       ctsim-dev sweep <rdb | UserDir> [--all] [--pages] [--json] [--out file] [--projects name ...]"
            );
            Console.Error.WriteLine("       ctsim-dev bench [N] [seconds]");
            Console.Error.WriteLine(
                "       ctsim-dev world-check [project ...] [--root name] [--user dir] [--json file]"
            );
            Console.Error.WriteLine(
                "       ctsim-dev world-dump <project dir> [--root name] [--rdb file] [--out file] [--no-lookups]"
            );
            Console.Error.WriteLine(
                "       ctsim-dev world-scenario <project dir> <scenario.json> <trace.jsonl> [--start iso] [--root name]"
            );
            Console.Error.WriteLine("       ctsim-dev world-ops <project dir> <ops.jsonl> <out.txt>");
            foreach (string u in WorldRunCommand.Usage.Split('\n'))
                Console.Error.WriteLine("       " + u);
            Console.Error.WriteLine("       " + WorldSweepCommand.Usage);
            foreach (string u in FrontendCommands.Usage.Split('\n'))
                Console.Error.WriteLine("       ctsim-dev " + u);
            Console.Error.WriteLine("       " + BehaviourCommand.Usage);
            foreach (string u in CompilerCommands.Usage.Split('\n'))
                Console.Error.WriteLine("       ctsim-dev " + u);
            foreach (string u in PageCommands.Usage.Split('\n'))
                Console.Error.WriteLine("       " + u);
            Console.Error.WriteLine("       ctsim-dev serve | dap | servers ... (as ctsim)");
            return 2;
        }
    }
}
