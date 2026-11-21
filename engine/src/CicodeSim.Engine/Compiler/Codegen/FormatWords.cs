using System;
using System.Numerics;
using CicodeSim.Compiler.Syntax;
using CicodeSim.Project;

namespace CicodeSim.Compiler.Codegen
{
    /// <summary>The StringFmt format word for a ':' format or a tag's FORMAT.</summary>
    public static class FormatWords
    {
        /// <summary>The word of an empty FORMAT field .</summary>
        public const uint NoFormat = 0x200206;

        /// <summary>The format word of a '#' picture, and whether it asks for engineering units.</summary>
        public static uint Picture(string text, out bool eu)
        {
            eu = false;
            string t = text;
            if (t.StartsWith("#!", StringComparison.Ordinal))
            {
                // '#!' = no digits, only the units
                eu = true;
                return 0x40000;
            }
            string low = CitectText.Lower(t);
            int k = low.IndexOf("engunit", StringComparison.Ordinal);
            if (k < 0)
                k = low.IndexOf("eu", StringComparison.Ordinal);
            string body = t;
            if (k >= 0)
            {
                eu = true;
                body = CitectText.TrimEnd(t.Substring(0, k));
            }
            uint width = 0,
                dec = 0,
                flags = 0;
            int i = 0,
                n = body.Length;
            while (i < n && (body[i] == '#' || body[i] == '0' || body[i] == '-'))
            {
                if (i == 1 && body[i] == '0')
                    flags |= 0x01;
                if (i > 0 && body[i] == '-')
                    flags |= 0x02;
                width++;
                i++;
            }
            if (i < n && (body[i] == '.' || body[i] == 's' || body[i] == 'S'))
            {
                if (body[i] != '.')
                    flags |= 0x10;
                width++;
                i++;
                while (i < n && (body[i] == '#' || body[i] == '0'))
                {
                    dec++;
                    width++;
                    i++;
                }
            }
            if (eu)
                flags |= 0x04;
            return (width & 0xFF) | ((dec & 0xFF) << 8) | (flags << 16);
        }

        /// <summary>The format word of a format operator spec.</summary>
        public static uint OfSpec(FormatSpec spec, out bool eu)
        {
            eu = false;
            if (spec.Kind == "picture")
                return Picture(spec.Text, out eu);
            if (spec.Kind == "width")
            {
                long w = Clamp(spec.Width),
                    d = Clamp(spec.Decimals);
                if (d != 0)
                    w += 1 + d;
                uint flags = spec.Left ? 0x02u : 0u;
                return (uint)(w & 0xFF) | (uint)((d & 0xFF) << 8) | (flags << 16);
            }
            return 0;
        }

        private static long Clamp(BigInteger v)
        {
            if (v > int.MaxValue)
                return int.MaxValue;
            if (v < int.MinValue)
                return int.MinValue;
            return (long)v;
        }

        /// <summary>A tag's FORMAT field as a word (empty: <see cref="NoFormat"/>).</summary>
        public static uint OfTagFormat(string? format, out bool eu)
        {
            eu = false;
            string f = CitectText.Trim(format ?? "");
            if (f.Length == 0)
                return NoFormat;
            if (f[0] == '#')
                return Picture(f, out eu);
            // a shortform in a FORMAT field: read like the ':' operand with the engine's parser
            // (the same on every framework), finite only
            if (Values.FloatText.TryParseNumber(f, out double v) && !double.IsNaN(v) && !double.IsInfinity(v))
            {
                bool left = v < 0;
                v = Math.Abs(v);
                // exact truncation (a C# cast of a huge double to long differs by framework)
                long w = Clamp(new BigInteger(Math.Truncate(v)));
                long d = f.IndexOf('.') >= 0 ? (long)(new BigInteger(Math.Truncate(v * 10 + 0.5)) % 10) : 0;
                if (d != 0)
                    w += 1 + d;
                return (uint)(w & 0xFF) | (uint)((d & 0xFF) << 8) | ((left ? 0x02u : 0u) << 16);
            }
            return NoFormat;
        }
    }
}
