using System;
using System.Collections.Generic;

namespace CicodeSim.Host
{
    /// <summary>Command line options shared by ctsim and ctsim-dev.</summary>
    internal static class CliArgs
    {
        internal static string? TakeOption(List<string> args, string name)
        {
            int k = args.IndexOf(name);
            if (k < 0)
                return null;
            if (k + 1 >= args.Count)
                throw new ArgumentException(name + " needs a value");
            string v = args[k + 1];
            args.RemoveRange(k, 2);
            return v;
        }

        /// <summary>True when every argument was taken; else the first one left (a misspelt
        /// option) and the command's usage go to stderr: the caller exits with 2, so a script
        /// never reads an unfiltered answer as the one it asked for.</summary>
        internal static bool NoneLeft(List<string> args, string command, string usage)
        {
            if (args.Count == 0)
                return true;
            Console.Error.WriteLine("ctsim " + command + ": unknown argument '" + args[0] + "'");
            Console.Error.WriteLine("usage: " + usage);
            return false;
        }

        internal static bool TakeFlag(List<string> args, string name)
        {
            int k = args.IndexOf(name);
            if (k < 0)
                return false;
            args.RemoveAt(k);
            return true;
        }
    }
}
