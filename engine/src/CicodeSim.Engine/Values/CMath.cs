using System;
using System.Runtime.InteropServices;
using System.Security;

namespace CicodeSim.Values
{
    /// <summary>C runtime maths functions, matching the Universal CRT's results.</summary>
    public static class CMath
    {
#if NETFRAMEWORK
        [SuppressUnmanagedCodeSecurity]
        private static class Ucrt
        {
            [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern double sin(double x);

            [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern double cos(double x);

            [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern double tan(double x);

            [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern double atan(double x);

            [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern double asin(double x);

            [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern double acos(double x);

            [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern double exp(double x);

            [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern double log(double x);

            [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern double log10(double x);

            [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
            public static extern double pow(double x, double y);
        }

        /// <summary>True when ucrtbase.dll is used (net48 on Windows).</summary>
        public static readonly bool UsesUcrt = Probe();

        private static bool Probe()
        {
            try
            {
                Ucrt.sin(0.5);
                Ucrt.cos(0.5);
                Ucrt.tan(0.5);
                Ucrt.atan(0.5);
                Ucrt.asin(0.5);
                Ucrt.acos(0.5);
                Ucrt.exp(0.5);
                Ucrt.log(0.5);
                Ucrt.log10(0.5);
                Ucrt.pow(0.5, 0.5);
                return true;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }

        public static double Sin(double x) => UsesUcrt ? Ucrt.sin(x) : Math.Sin(x);

        public static double Cos(double x) => UsesUcrt ? Ucrt.cos(x) : Math.Cos(x);

        public static double Tan(double x) => UsesUcrt ? Ucrt.tan(x) : Math.Tan(x);

        public static double Atan(double x) => UsesUcrt ? Ucrt.atan(x) : Math.Atan(x);

        public static double Asin(double x) => UsesUcrt ? Ucrt.asin(x) : Math.Asin(x);

        public static double Acos(double x) => UsesUcrt ? Ucrt.acos(x) : Math.Acos(x);

        public static double Exp(double x) => UsesUcrt ? Ucrt.exp(x) : Math.Exp(x);

        public static double Log(double x) => UsesUcrt ? Ucrt.log(x) : Math.Log(x);

        public static double Log10(double x) => UsesUcrt ? Ucrt.log10(x) : Math.Log10(x);

        public static double Pow(double x, double y) => UsesUcrt ? Ucrt.pow(x, y) : Math.Pow(x, y);
#else
        /// <summary>True when ucrtbase.dll is called directly (net48 only; net10.0's Math is the CRT).</summary>
        public static readonly bool UsesUcrt = false;

        public static double Sin(double x) => Math.Sin(x);

        public static double Cos(double x) => Math.Cos(x);

        public static double Tan(double x) => Math.Tan(x);

        public static double Atan(double x) => Math.Atan(x);

        public static double Asin(double x) => Math.Asin(x);

        public static double Acos(double x) => Math.Acos(x);

        public static double Exp(double x) => Math.Exp(x);

        public static double Log(double x) => Math.Log(x);

        public static double Log10(double x) => Math.Log10(x);

        public static double Pow(double x, double y) => Math.Pow(x, y);
#endif
    }
}
