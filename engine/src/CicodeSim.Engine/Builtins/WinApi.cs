using System;
using System.Collections.Generic;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>
    /// The few Windows functions project code opens with DLLOpen that the simulator answers itself.
    /// </summary>
    internal static class WinApi
    {
        private const int Base = 0x7c10;

        private static readonly (string Dll, string Function)[] Known =
        {
            ("KERNEL32.DLL", "GETCURRENTPROCESSID"),
            ("USER32.DLL", "LOCKWINDOWUPDATE"),
            ("WINUTIL.DLL", "WINUTIL_WAITCURSOR"),
            ("WINUTIL.DLL", "WINUTIL_NORMALCURSOR"),
        };

        /// <summary>The pseudo handle of a stood-in function, -1 for any other.</summary>
        internal static int Open(string file, string function)
        {
            string f = file.Replace('/', '\\').Trim();
            int k = f.LastIndexOf('\\');
            string name = (k >= 0 ? f.Substring(k + 1) : f).ToUpperInvariant();
            if (!name.EndsWith(".DLL", StringComparison.Ordinal))
                name += ".DLL";
            string fn = function.Trim().ToUpperInvariant();
            for (int i = 0; i < Known.Length; i++)
                if (Known[i].Dll == name && Known[i].Function == fn)
                    return Base + i;
            return -1;
        }

        internal static bool Is(long handle) => handle >= Base && handle < Base + Known.Length;

        /// <summary>The function's answer (DLLCall gives its text, DLLCallEx the number).</summary>
        internal static long Call(long handle)
        {
            switch (Known[(int)(handle - Base)].Function)
            {
                case "GETCURRENTPROCESSID":
                    try
                    {
                        return System.Diagnostics.Process.GetCurrentProcess().Id;
                    }
                    catch (InvalidOperationException)
                    {
                        return 1;
                    }
                case "LOCKWINDOWUPDATE":
                    return 1;
                default:
                    return 0;
            }
        }
    }
}
