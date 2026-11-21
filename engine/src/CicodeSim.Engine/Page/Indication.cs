using CicodeSim.World.Display;

namespace CicodeSim.Page
{
    /// <summary>Quality and error indication of animated objects.</summary>
    public static class Indication
    {
        public const int Error = 1,
            Uncertain = 2,
            Bad = 4,
            Override = 8,
            Inhibit = 0x10;

        /// <summary>The flag word of one evaluation: from the result's
        /// quality byte 0 bits 7..6 (0 BAD, 1 UNCERTAIN), byte 2 bit 0 OVERRIDE, bit 1 CONTROL
        /// INHIBIT, and ERROR for an error code (257 only with [Page]RangeCheck).</summary>
        public static int Flags(ulong quality, int error, bool rangeCheck)
        {
            int f = 0;
            int general = (int)((quality & 0xFF) >> 6);
            if (general == 0)
                f |= Bad;
            else if (general == 1)
                f |= Uncertain;
            if ((quality & (1UL << 16)) != 0)
                f |= Override;
            if ((quality & (1UL << 17)) != 0)
                f |= Inhibit;
            if (error != 0 && (error != 257 || rangeCheck))
                f |= Error;
            return f;
        }

        /// <summary>The display text of an error code.</summary>
        public static string ErrorText(int code)
        {
            switch (code)
            {
                case 0:
                    return "#OK";
                case 0x20:
                    return "#WAIT";
                case 0x101:
                    return "#RANGE";
                case 0x110:
                    return "#MEM";
                case 0x111:
                    return "#DIV/0";
                case 0x127:
                    return "#STACK";
                case 0x143:
                case 0x144:
                    return "#ASC";
                case 0x158:
                    return "#OVR";
                case 0x1a7:
                case 0x1b0:
                    return "#PEND";
                case 0x35:
                case 0x119:
                case 0x192:
                case 0x193:
                case 0x194:
                case 0x195:
                case 0x1a2:
                case 0x1a8:
                case 0x1a9:
                case 0x20d:
                case 0x214:
                    return "#COM";
            }
            return code > 0xff ? "#ERR" : "#COM";
        }

        /// <summary>What a text object shows, given the value's quality.</summary>
        public static string Text(
            string value,
            int flags,
            int error,
            int mode,
            DisplayConfig cfg,
            out string? replaced
        )
        {
            replaced = null;
            if (mode == 1 || mode == 2)
                return value;
            if (cfg.ShowBadText && (flags & 5) == 5)
                replaced = cfg.BadText;
            else if (cfg.ShowUncertainText && (flags & 3) == 3)
                replaced = cfg.UncertainText;
            else if (cfg.ShowErrorText && (flags & Error) != 0)
                replaced = ErrorText(error);
            return replaced ?? value;
        }

        /// <summary>The proposed 'indication' field: the first of bad, uncertain, error, override,
        /// inhibit the flags hold; null in IgnoreValueQuality mode 1 or with no flag (the
        /// precedence on non-text objects is a guess).</summary>
        public static string? Name(int flags, int mode)
        {
            if (mode == 1 || flags == 0)
                return null;
            if ((flags & Bad) != 0)
                return "bad";
            if ((flags & Uncertain) != 0)
                return "uncertain";
            if ((flags & Error) != 0)
                return "error";
            if ((flags & Override) != 0)
                return "override";
            if ((flags & Inhibit) != 0)
                return "inhibit";
            return null;
        }
    }
}
