using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CicodeSim.World.Display
{
    /// <summary>Text widths in a display font, as the runtime measures them.</summary>
    public static class TextMeasure
    {
        private static readonly object Lock = new object();
        private static IntPtr _dc;
        private static bool _gdiFailed;
        private static readonly Dictionary<string, IntPtr> Fonts = new Dictionary<string, IntPtr>(
            StringComparer.Ordinal
        );

        /// <summary>The pixel width of <paramref name="text"/> in <paramref name="font"/> (null:
        /// 8 pixels a character): the whole string as GetTextExtentPoint32 measures it (a newline
        /// is a glyph there).</summary>
        public static int Width(FontInfo? font, string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
            if (font == null)
                return text.Length * 8;
            int? w = Gdi(font, text);
            if (w.HasValue)
                return w.Value;
            return (int)Math.Round(text.Length * font.Height * 0.55, MidpointRounding.AwayFromZero);
        }

        /// <summary>The longest start of <paramref name="text"/> no wider than
        /// <paramref name="pixels"/>; with <paramref name="ellipsis"/> a cut text ends in '...'
        /// within the width (the runtime's DT_END_ELLIPSIS look: guess).</summary>
        public static string Fit(FontInfo? font, string text, long pixels, bool ellipsis)
        {
            if (pixels < 0 || Width(font, text) <= pixels)
                return text;
            if (!ellipsis)
                return text.Substring(0, Longest(font, text, "", pixels));
            int n = Longest(font, text, "...", pixels);
            if (n == 0 && Width(font, "...") > pixels)
                return "";
            return text.Substring(0, n) + "...";
        }

        /// <summary>The most characters of <paramref name="text"/> that fit with the suffix.</summary>
        private static int Longest(FontInfo? font, string text, string suffix, long pixels)
        {
            int lo = 0,
                hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (Width(font, text.Substring(0, mid) + suffix) <= pixels)
                    lo = mid;
                else
                    hi = mid - 1;
            }
            return lo;
        }

        /// <summary>The box ``text`` covers when drawn at (x, y).</summary>
        public static Rect Box(FontInfo? font, string text, int align, int x, int y) =>
            font == null
                ? Box("", 0, false, false, false, false, text, align, x, y)
                : Box(
                    font.Face,
                    font.Height,
                    font.Bold,
                    font.Italic,
                    font.Underline,
                    font.Strikeout,
                    text,
                    align,
                    x,
                    y
                );

        /// <summary><see cref="Box(FontInfo, string, int, int, int)"/> for a font given by its
        /// face and character height in pixels (0 = the 8-pixels-a-character estimate).</summary>
        public static Rect Box(
            string face,
            int height,
            bool bold,
            bool italic,
            bool underline,
            bool strikeout,
            string text,
            int align,
            int x,
            int y
        )
        {
            if (string.IsNullOrEmpty(text))
                return new Rect(x, y, x, y);
            int cx,
                cy,
                asc;
            var m = Metrics(face, height, bold, italic, underline, strikeout, text);
            if (m.HasValue)
                (cx, cy, asc) = m.Value;
            else
            {
                int hgt = height > 0 ? height : 12;
                cx =
                    height > 0
                        ? (int)Math.Round(text.Length * height * 0.55, MidpointRounding.AwayFromZero)
                        : text.Length * 8;
                cy = (int)Math.Round(hgt * 1.2);
                asc = (int)Math.Round(hgt * 0.8);
            }
            int l =
                align == 1 ? x - cx
                : align == 2 ? x - cx / 2
                : x;
            return new Rect(l, y - asc, l + cx, y - asc + cy);
        }

        /// <summary>GDI's (cx, cy, tmAscent) of <paramref name="text"/> in a font made with
        /// lfHeight = -<paramref name="height"/> pixels; null off Windows or when GDI fails.</summary>
        public static (int Cx, int Cy, int Ascent)? Metrics(
            string face,
            int height,
            bool bold,
            bool italic,
            bool underline,
            bool strikeout,
            string text
        )
        {
            if (_gdiFailed || Environment.OSVersion.Platform != PlatformID.Win32NT || height <= 0)
                return null;
            lock (Lock)
            {
                try
                {
                    IntPtr hf = FontHandle(face, height, bold, italic, underline, strikeout);
                    if (hf == IntPtr.Zero)
                        return null;
                    IntPtr old = SelectObject(_dc, hf);
                    try
                    {
                        if (!GetTextMetricsW(_dc, out TextMetric tm))
                            return null;
                        if (!GetTextExtentPoint32W(_dc, text, text.Length, out Size sz))
                            return null;
                        return (sz.Cx, sz.Cy, tm.Ascent);
                    }
                    finally
                    {
                        SelectObject(_dc, old);
                    }
                }
                catch (Exception e)
                    when (e is DllNotFoundException
                        || e is EntryPointNotFoundException
                        || e is TypeLoadException
                    )
                {
                    _gdiFailed = true;
                    return null;
                }
            }
        }

        /// <summary>The cached GDI font (the memory DC made on first use); Lock held.</summary>
        private static IntPtr FontHandle(
            string face,
            int height,
            bool bold,
            bool italic,
            bool underline,
            bool strikeout
        )
        {
            if (_dc == IntPtr.Zero)
            {
                _dc = CreateCompatibleDC(IntPtr.Zero);
                if (_dc == IntPtr.Zero)
                {
                    _gdiFailed = true;
                    return IntPtr.Zero;
                }
            }
            string key = face + "|" + height + "|" + bold + italic + underline + strikeout;
            if (!Fonts.TryGetValue(key, out IntPtr hf))
            {
                hf = CreateFontW(
                    -height,
                    0,
                    0,
                    0,
                    bold ? 700 : 400,
                    italic ? 1u : 0u,
                    underline ? 1u : 0u,
                    strikeout ? 1u : 0u,
                    1 /* DEFAULT_CHARSET */
                    ,
                    0,
                    0,
                    0,
                    0,
                    face.Length > 0 ? face : "Arial"
                );
                if (hf == IntPtr.Zero)
                    return IntPtr.Zero;
                if (Fonts.Count > 256)
                {
                    foreach (var f in Fonts.Values)
                        DeleteObject(f);
                    Fonts.Clear();
                }
                Fonts[key] = hf;
            }
            return hf;
        }

        private static int? Gdi(FontInfo font, string text)
        {
            if (_gdiFailed || Environment.OSVersion.Platform != PlatformID.Win32NT)
                return null;
            lock (Lock)
            {
                try
                {
                    if (_dc == IntPtr.Zero)
                    {
                        _dc = CreateCompatibleDC(IntPtr.Zero);
                        if (_dc == IntPtr.Zero)
                        {
                            _gdiFailed = true;
                            return null;
                        }
                    }
                    string key =
                        font.Face
                        + "|"
                        + font.Height
                        + "|"
                        + font.Bold
                        + font.Italic
                        + font.Underline
                        + font.Strikeout;
                    if (!Fonts.TryGetValue(key, out IntPtr hf))
                    {
                        // a negative height = the character (em) height in pixels, as a point
                        // size converts (guess for a positive pixel SIZE: the same height)
                        hf = CreateFontW(
                            -font.Height,
                            0,
                            0,
                            0,
                            font.Bold ? 700 : 400,
                            font.Italic ? 1u : 0u,
                            font.Underline ? 1u : 0u,
                            font.Strikeout ? 1u : 0u,
                            1 /* DEFAULT_CHARSET */
                            ,
                            0,
                            0,
                            0,
                            0,
                            font.Face.Length > 0 ? font.Face : "Arial"
                        );
                        if (hf == IntPtr.Zero)
                            return null;
                        if (Fonts.Count > 256)
                        {
                            foreach (var f in Fonts.Values)
                                DeleteObject(f);
                            Fonts.Clear();
                        }
                        Fonts[key] = hf;
                    }
                    IntPtr old = SelectObject(_dc, hf);
                    bool ok = GetTextExtentPoint32W(_dc, text, text.Length, out Size sz);
                    SelectObject(_dc, old);
                    return ok ? sz.Cx : (int?)null;
                }
                catch (Exception e)
                    when (e is DllNotFoundException
                        || e is EntryPointNotFoundException
                        || e is TypeLoadException
                    )
                {
                    _gdiFailed = true;
                    return null;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Size
        {
            public int Cx,
                Cy;
        }

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFontW(
            int height,
            int width,
            int escapement,
            int orientation,
            int weight,
            uint italic,
            uint underline,
            uint strikeOut,
            uint charSet,
            uint outPrecision,
            uint clipPrecision,
            uint quality,
            uint pitchAndFamily,
            string face
        );

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [StructLayout(LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct TextMetric
        {
            public int Height,
                Ascent,
                Descent,
                InternalLeading,
                ExternalLeading,
                AveCharWidth,
                MaxCharWidth,
                Weight,
                Overhang,
                DigitizedAspectX,
                DigitizedAspectY;
            public char FirstChar,
                LastChar,
                DefaultChar,
                BreakChar;
            public byte Italic,
                Underlined,
                StruckOut,
                PitchAndFamily,
                CharacterSet;
        }

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetTextMetricsW(IntPtr hdc, out TextMetric tm);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetTextExtentPoint32W(IntPtr hdc, string text, int count, out Size size);
    }
}
