using System;
using System.Collections.Generic;
using System.Globalization;

namespace CicodeSim.Builtins
{
    /// <summary>A picture of 0xRRGGBB pixels, top row first: what WinFile writes as a .bmp
    /// and WinPrintFile reads back.</summary>
    public sealed class BmpImage
    {
        /// <summary>The largest picture read or made (pixels).</summary>
        public const int MaxPixels = 64 * 1024 * 1024;

        public readonly int Width,
            Height;

        /// <summary>0xRRGGBB, row by row from the top.</summary>
        public readonly int[] Pixels;

        public BmpImage(int width, int height)
        {
            if (width < 1 || height < 1 || (long)width * height > MaxPixels)
                throw new ArgumentException(
                    "a picture of "
                        + width.ToString(CultureInfo.InvariantCulture)
                        + " x "
                        + height.ToString(CultureInfo.InvariantCulture)
                        + " pixels"
                );
            Width = width;
            Height = height;
            Pixels = new int[width * height];
        }

        public int this[int x, int y]
        {
            get => Pixels[y * Width + x];
            set => Pixels[y * Width + x] = value & 0xFFFFFF;
        }

        private static int U16(byte[] b, int o) => b[o] | (b[o + 1] << 8);

        private static int I32(byte[] b, int o) =>
            b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);

        /// <summary>A Windows bitmap file: 24 or 32 bits a pixel, uncompressed (BI_RGB) or a
        /// 32-bit BI_BITFIELDS one, rows bottom-up or top-down. Null for anything else.</summary>
        public static BmpImage? Read(byte[] data)
        {
            if (data == null || data.Length < 54 || data[0] != (byte)'B' || data[1] != (byte)'M')
                return null;
            int bits = I32(data, 10),
                head = I32(data, 14);
            if (head < 40 || 14 + head > data.Length)
                return null;
            int w = I32(data, 18),
                h = I32(data, 22),
                bpp = U16(data, 28),
                compression = I32(data, 30);
            bool topDown = h < 0;
            if (topDown)
                h = -h;
            if (w < 1 || h < 1 || (long)w * h > MaxPixels || (bpp != 24 && bpp != 32))
                return null;
            int rm = 0xFF0000,
                gm = 0xFF00,
                bm = 0xFF;
            if (compression == 3 && bpp == 32)
            {
                // the masks follow a 40-byte header, else sit in a V4 / V5 header
                int mo = 14 + 40;
                if (mo + 12 > data.Length)
                    return null;
                rm = I32(data, mo);
                gm = I32(data, mo + 4);
                bm = I32(data, mo + 8);
            }
            else if (compression != 0)
                return null;
            long stride = ((long)w * bpp + 31) / 32 * 4;
            if (bits < 14 + head || bits + stride * h > data.Length)
                return null;
            var img = new BmpImage(w, h);
            int bytes = bpp / 8;
            for (int y = 0; y < h; y++)
            {
                int row = bits + (int)(stride * (topDown ? y : h - 1 - y));
                for (int x = 0; x < w; x++)
                {
                    int o = row + x * bytes;
                    int rgb;
                    if (bpp == 24 || compression == 0)
                        rgb = (data[o + 2] << 16) | (data[o + 1] << 8) | data[o];
                    else
                    {
                        int v = I32(data, o);
                        rgb = (Channel(v, rm) << 16) | (Channel(v, gm) << 8) | Channel(v, bm);
                    }
                    img.Pixels[y * w + x] = rgb;
                }
            }
            return img;
        }

        /// <summary>The 8-bit value of a BI_BITFIELDS channel.</summary>
        private static int Channel(int v, int mask)
        {
            if (mask == 0)
                return 0;
            uint m = (uint)mask,
                u = (uint)v & m;
            while ((m & 1) == 0)
            {
                m >>= 1;
                u >>= 1;
            }
            return (int)((ulong)u * 255 / m);
        }

        /// <summary>The picture as a 24-bit uncompressed Windows bitmap file (rows bottom-up,
        /// 96 dpi).</summary>
        public byte[] ToBmp()
        {
            int stride = (Width * 3 + 3) / 4 * 4;
            int size = 54 + stride * Height;
            var o = new byte[size];
            o[0] = (byte)'B';
            o[1] = (byte)'M';
            Put32(o, 2, size);
            Put32(o, 10, 54);
            Put32(o, 14, 40);
            Put32(o, 18, Width);
            Put32(o, 22, Height);
            o[26] = 1;
            o[28] = 24;
            Put32(o, 34, stride * Height);
            Put32(o, 38, 3780);
            Put32(o, 42, 3780);
            for (int y = 0; y < Height; y++)
            {
                int row = 54 + stride * (Height - 1 - y);
                for (int x = 0; x < Width; x++)
                {
                    int p = Pixels[y * Width + x];
                    o[row + x * 3] = (byte)p;
                    o[row + x * 3 + 1] = (byte)(p >> 8);
                    o[row + x * 3 + 2] = (byte)(p >> 16);
                }
            }
            return o;
        }

        private static void Put32(byte[] b, int o, int v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }

        /// <summary>Recolour every pixel from its own colour: the first entry of
        /// <paramref name="map"/> that matches gives the new colour, else with
        /// <paramref name="swapBlackWhite"/> black becomes white and white black.</summary>
        public void Recolour(bool swapBlackWhite, IReadOnlyList<ColourSwap> map)
        {
            if (!swapBlackWhite && map.Count == 0)
                return;
            var done = new Dictionary<int, int>();
            for (int i = 0; i < Pixels.Length; i++)
            {
                int p = Pixels[i];
                if (!done.TryGetValue(p, out int q))
                {
                    q = p;
                    bool mapped = false;
                    foreach (var m in map)
                        if (m.Matches(p))
                        {
                            q = m.To;
                            mapped = true;
                            break;
                        }
                    if (!mapped && swapBlackWhite)
                        q =
                            p == 0 ? 0xFFFFFF
                            : p == 0xFFFFFF ? 0
                            : p;
                    done[p] = q;
                }
                Pixels[i] = q;
            }
        }
    }

    /// <summary>One line of a colour map file (WinCopy / WinFile / WinPrint sMap):
    /// 'RRR GGG BBB RRR GGG BBB [HHH] [SSS] [LLL]', the FROM colour, the TO colour and the
    /// hue / saturation / luminance tolerances applied to the FROM colour.</summary>
    public sealed class ColourSwap
    {
        public int From,
            To;

        /// <summary>Hue tolerance in degrees (0..360); saturation and luminance on 0..255.</summary>
        public int Hue,
            Sat,
            Lum;

        /// <summary>The pixel takes this swap: its own colour with no tolerance, else hue,
        /// saturation and luminance each within its tolerance of the FROM colour's (hue
        /// around the circle).</summary>
        public bool Matches(int rgb)
        {
            if (rgb == From)
                return true;
            if (Hue == 0 && Sat == 0 && Lum == 0)
                return false;
            Hsl(rgb, out double h, out double s, out double l);
            Hsl(From, out double fh, out double fs, out double fl);
            double dh = Math.Abs(h - fh);
            if (dh > 180)
                dh = 360 - dh;
            return dh <= Hue && Math.Abs(s - fs) <= Sat && Math.Abs(l - fl) <= Lum;
        }

        /// <summary>Hue in degrees 0..360, saturation and luminance on 0..255.</summary>
        internal static void Hsl(int rgb, out double h, out double s, out double l)
        {
            double r = ((rgb >> 16) & 0xFF) / 255.0,
                g = ((rgb >> 8) & 0xFF) / 255.0,
                b = (rgb & 0xFF) / 255.0;
            double max = Math.Max(r, Math.Max(g, b)),
                min = Math.Min(r, Math.Min(g, b)),
                d = max - min;
            double ll = (max + min) / 2;
            double ss = d == 0 ? 0 : d / (1 - Math.Abs(2 * ll - 1));
            double hh;
            if (d == 0)
                hh = 0;
            else if (max == r)
                hh = 60 * (((g - b) / d) % 6);
            else if (max == g)
                hh = 60 * ((b - r) / d + 2);
            else
                hh = 60 * ((r - g) / d + 4);
            if (hh < 0)
                hh += 360;
            h = hh;
            s = ss * 255;
            l = ll * 255;
        }

        /// <summary>The swaps of a map file's text: one a line, comments after // or !, a
        /// line with fewer than six numbers skipped. Saturation and luminance tolerances
        /// default to twice the hue tolerance.</summary>
        public static List<ColourSwap> Parse(string text)
        {
            var list = new List<ColourSwap>();
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw;
                int c = line.IndexOf("//", StringComparison.Ordinal);
                if (c >= 0)
                    line = line.Substring(0, c);
                c = line.IndexOf('!');
                if (c >= 0)
                    line = line.Substring(0, c);
                var nums = new List<int>();
                foreach (
                    string w in line.Split(
                        new[] { ' ', '\t', '\r', ',' },
                        StringSplitOptions.RemoveEmptyEntries
                    )
                )
                {
                    if (!int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                        break;
                    nums.Add(n);
                }
                if (nums.Count < 6)
                    continue;
                int Byte(int k) => Math.Max(0, Math.Min(255, nums[k]));
                int hue = nums.Count > 6 ? Math.Max(0, Math.Min(360, nums[6])) : 0;
                list.Add(
                    new ColourSwap
                    {
                        From = (Byte(0) << 16) | (Byte(1) << 8) | Byte(2),
                        To = (Byte(3) << 16) | (Byte(4) << 8) | Byte(5),
                        Hue = hue,
                        Sat = nums.Count > 7 ? Byte(7) : Math.Min(255, 2 * hue),
                        Lum = nums.Count > 8 ? Byte(8) : Math.Min(255, 2 * hue),
                    }
                );
            }
            return list;
        }
    }
}
