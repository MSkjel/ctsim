using System;

namespace CicodeSim.Page
{
    /// <summary>The geometric classes' arithmetic. Inputs are percentages, clamped by the class.</summary>
    public static class Formulas
    {
        /// <summary>The runtime's pi.</summary>
        public const double Pi = 3.14159265359;

        /// <summary>A linear class map: out_min at 0 %, out_max at 100 %, the input clamped
        /// (slope = (out_max - out_min) / 100.0, out = v * slope + out_min).</summary>
        public static double Map(double p, double outMin, double outMax)
        {
            if (double.IsNaN(p))
                p = 0.0;
            if (p > 100.0)
                return outMax;
            if (p < 0.0)
                return outMin;
            double slope = (outMax - outMin) / 100.0;
            return p * slope + outMin;
        }

        /// <summary>C's (int)(short)(int)x: truncation toward zero, then 16 bits.</summary>
        public static int TruncShort(double x)
        {
            if (double.IsNaN(x) || double.IsInfinity(x))
                return 0;
            double t = Math.Truncate(x);
            long l = t > int.MaxValue || t < int.MinValue ? unchecked((long)(int)0x80000000) : (long)t;
            return (short)(l & 0xFFFF);
        }

        /// <summary>Move X: from -MVXMINPT (left) at 0 % to
        /// MVXMAXPT (right) at 100 %, truncated to whole pixels.</summary>
        public static int MoveX(double p, int minPt, int maxPt) => TruncShort(Map(p, -minPt, maxPt));

        /// <summary>Move Y: from MVYMINPT (down) at 0 % to
        /// -MVYMAXPT (up) at 100 %.</summary>
        public static int MoveY(double p, int minPt, int maxPt) => TruncShort(Map(p, minPt, -maxPt));

        /// <summary>A size factor: (MINPC + p (MAXPC - MINPC) / 100) / 100.</summary>
        public static double ScaleFactor(double p, int minPc, int maxPc) =>
            Map(p, minPc / 100.0, maxPc / 100.0);

        /// <summary>A rotation in degrees, clockwise positive.</summary>
        public static double RotateDegrees(double p, int minPc, int maxPc) => Map(p, -minPc, maxPc);

        /// <summary>The runtime's angle in radians (what the drawing transform turns by).</summary>
        public static double RotateRadians(double p, int minPc, int maxPc) =>
            Map(p, minPc * -6.28318530718 / 360.0, maxPc * 6.28318530718 / 360.0);

        /// <summary>The fill level fraction.</summary>
        public static double FillFraction(double p, int minPc, int maxPc) =>
            Map(p, minPc / 100.0, maxPc / 100.0);

        /// <summary>An Express axis position along a span of n pixels: -1 = 0, 1 = n, else n / 2
        /// (C division of a short).</summary>
        public static int Express(int org, int n)
        {
            if (org == -1)
                return 0;
            if (org == 1)
                return n;
            return (short)n / 2;
        }

        /// <summary>A pivot offset from the rect's left / top: Express (mode 0) or Custom
        /// (mode != 0: n / 2 + offset).</summary>
        public static int Pivot(int mode, int org, int n) => mode == 0 ? Express(org, n) : (short)n / 2 + org;

        /// <summary>A vertex offset: fraction = p / 100 (no
        /// clamp seen; verify), From + fraction (To - From).</summary>
        public static double Vertex(double p, int from, int to) => from + p / 100.0 * (to - from);

        /// <summary>A slider's drag back to a percent: the
        /// offset through the inverse slope, clamped to 0..100.</summary>
        public static double SliderPercent(double offset, double outMin, double outMax)
        {
            if (outMax == outMin)
                return 0.0;
            double p = (offset - outMin) * 100.0 / (outMax - outMin);
            return p < 0.0 ? 0.0
                : p > 100.0 ? 100.0
                : p;
        }

        /// <summary>The touch repeat schedule: the timer
        /// period T = RPT_RATE + 1 ms, the first repeat after (1000 / T + 1) ticks.</summary>
        public static (int Period, int FirstMs) RepeatSchedule(int rate)
        {
            int t = rate + 1;
            if (t <= 0)
                t = 1;
            int limit = 1000 / t;
            return (t, (limit + 1) * t);
        }
    }
}
