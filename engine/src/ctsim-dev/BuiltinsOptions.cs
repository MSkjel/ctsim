using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CicodeSim.Builtins;
using CicodeSim.World;

namespace CicodeSim.Host
{
    /// <summary>The built-in options of world-run and world-sweep.</summary>
    internal sealed class BuiltinsOptions
    {
        public bool Default;
        public TimeZoneInfo? Zone;

        /// <summary>--sandbox memory, folder or a folder path: the File* built-ins' sandbox
        /// (default: in memory, nothing written to any disk).</summary>
        public SandboxOptions? Sandbox;

        public static BuiltinsOptions Take(List<string> args)
        {
            var o = new BuiltinsOptions();
            string? b = VmCommands.TakeOption(args, "--builtins");
            if (b != null && !string.Equals(b, "world", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(b, "default", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("--builtins world | default");
                o.Default = true;
            }
            string? tz = VmCommands.TakeOption(args, "--tz");
            if (tz != null)
                o.Zone = string.Equals(tz, "UTC", StringComparison.OrdinalIgnoreCase)
                    ? TimeZoneInfo.Utc
                    : TimeZoneInfo.FindSystemTimeZoneById(tz);
            string? sb = VmCommands.TakeOption(args, "--sandbox");
            if (sb != null)
                o.Sandbox =
                    string.Equals(sb, "memory", StringComparison.OrdinalIgnoreCase)
                        ? new SandboxOptions { InMemory = true }
                    : string.Equals(sb, "folder", StringComparison.OrdinalIgnoreCase) ? new SandboxOptions()
                    : new SandboxOptions { Folder = sb };
            return o;
        }

        public void Apply(WorldRun run)
        {
            if (Default)
            {
                run.Vm.Builtins = Registry.Default();
                return;
            }
            var host = WorldBuiltins.HostOf(run.Vm.Builtins);
            if (host != null && Zone != null)
                host.TimeZone = Zone;
            if (host != null && Sandbox != null)
                host.UseSandbox(Sandbox);
        }

        public const string Usage = "[--builtins world|default] [--tz zone] [--sandbox memory|folder|dir]";
    }

    /// <summary>ctsim-dev builtins-behaviour [--out file] [--check file]: builtins/behaviour.json from
    /// the engine's registries and the [Builtin] attributes; --check exits 1 when the file differs.</summary>
    internal static class BehaviourCommand
    {
        public const string Usage = "ctsim-dev builtins-behaviour [--out file] [--check file]";

        public static int Run(List<string> args)
        {
            string? outPath = VmCommands.TakeOption(args, "--out");
            string? check = VmCommands.TakeOption(args, "--check");
            string text = WorldBuiltins.BehaviourJson();
            if (check != null)
            {
                string have = File.Exists(check)
                    ? File.ReadAllText(check, Encoding.UTF8).Replace("\r\n", "\n")
                    : "";
                if (have != text)
                {
                    Console.Error.WriteLine(
                        check + " is not current: run ctsim-dev builtins-behaviour --out " + check
                    );
                    return 1;
                }
                return 0;
            }
            if (outPath != null)
            {
                File.WriteAllText(outPath, text, new UTF8Encoding(false));
                return 0;
            }
            var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                NewLine = "\n",
            };
            o.Write(text);
            o.Flush();
            return 0;
        }
    }
}
