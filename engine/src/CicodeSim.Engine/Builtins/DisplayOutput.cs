using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World.Display;

namespace CicodeSim.Builtins
{
    /// <summary>What Cicode draws on an AN at run time (DspText, DspSym and friends).</summary>
    public static class DisplayOutput
    {
        private static DisplayState D(SimHost h) => DisplayBuiltins.Of(h);

        /// <summary>A FUNCTION argument (the compiler passes the function's name, '' for none).</summary>
        private static string FnName(ArgList a, int k) =>
            a.Count > k && !a[k].IsNothing ? A.S(a[k]).Trim() : "";

        private static long Arg(ArgList a, int k, long dflt) =>
            a.Count > k && !a[k].IsNothing ? A.I(a[k]) : dflt;

        /// <summary>The position Cicode output at an AN takes (its current position; (0, 0) for
        /// an AN the page does not have).</summary>
        private static (int, int) At(DisplayState d, Window w, int an)
        {
            var o = w.Page?.Get(an);
            return o != null ? DisplayAns.CurrentPos(d, w, o) : (0, 0);
        }

        /// <summary>Record an output on an AN of the task's window (the checks done).</summary>
        private static void Draw(DisplayState d, Window w, DrawCommand c)
        {
            (c.X, c.Y) = At(d, w, c.An);
            d.Draw(w, c);
        }

        // ------------------------------------------------------------------ fonts
        [Builtin(
            "DspFontHnd",
            Doc = "1104668",
            Notes = "The handle of a fonts.dbf font of the unit by NAME (without case: guess; handles in unit order, guess), -1 when none."
        )]
        private static Value DspFontHnd(SimHost h, CicodeTask t, ArgList a) =>
            W.I(D(h).FontByName(A.S(a.Get(0))));

        [Builtin(
            "DspFont",
            Doc = "1104667",
            Effects = "creates a font (the display client's font table)",
            Errors = "a full font table (256): error 274 and a hardware alarm, -1",
            Notes = "The handle of an equal font made before, else a new one (after the fonts.dbf fonts). The off colours default to the on colours (guess)."
        )]
        private static Value DspFont(SimHost h, CicodeTask t, ArgList a)
        {
            uint fgOn = (uint)A.I(a.Get(2)),
                bgOn = (uint)A.I(a.Get(3));
            uint fgOff = a.Count > 4 && !a[4].IsNothing ? (uint)A.I(a[4]) : fgOn;
            uint bgOff = a.Count > 5 && !a[5].IsNothing ? (uint)A.I(a[5]) : bgOn;
            int hnd = D(h).MakeFont(A.S(a.Get(0)), (int)A.I(a.Get(1)), fgOn, bgOn, fgOff, bgOff);
            if (hnd < 0)
                h.Note(t, "hardware alarm 274: DspFont: no free font");
            return W.I(hnd);
        }

        // ------------------------------------------------------------------ text
        /// <summary>DspText's rule: 0, 274 (raised) or 369 (trapped).</summary>
        private static int Text(
            SimHost h,
            CicodeTask t,
            int an,
            int font,
            string text,
            long length,
            long align,
            long trunc,
            bool tooltip
        )
        {
            var d = D(h);
            if (an == -2)
                an = TaskContext.An(t);
            if (font == -1)
                font = d.DefaultFont;
            var w = DisplayWindows.Current(h, t);
            if (w == null || (ushort)an > 31999 || (ushort)font > 0xff)
            {
                W.Raise(t, 274);
                return 274;
            }
            var o = w.Page?.Get(an);
            if (o == null)
            {
                W.Raise(t, 369);
                return 369;
            }
            if (align < 0 || align > 2)
                align = 0;
            // the length and its mode (help 1104736): mode 2 = characters, cut
            // here; modes 0 / 1 = pixels in the font: the viewer cuts the full text it gets
            // (length, trunc), the engine only measures it for the tool tip (PROTOCOL.md 10)
            string shown = text.Length > 255 ? text.Substring(0, 255) : text;
            bool cut;
            if (length > 0 && trunc == 2)
            {
                if (shown.Length > length)
                    shown = shown.Substring(0, (int)length);
                cut = shown != text;
            }
            else
                cut =
                    shown != text
                    || (tooltip && length > 0 && TextMeasure.Width(d.Font(font), shown) > length);
            if (tooltip)
            {
                o.Tip = cut ? text : "";
                o.TipSet = true;
            }
            var c = new DrawCommand
            {
                An = an,
                Kind = "text",
                Text = shown,
                Font = font,
            };
            c.Arg("length", length).Arg("align", align).Arg("trunc", trunc);
            Draw(d, w, c);
            // the AN's extent = the text as the viewer draws it (a pixel length cuts it)
            string drawn =
                length > 0 && trunc != 2 ? TextMeasure.Fit(d.Font(font), shown, length, trunc == 1) : shown;
            CicodeExtents.Text(o, d.Font(font), drawn, (int)align);
            return 0;
        }

        [Builtin(
            "DspText",
            Doc = "1104736",
            Effects = "records a text output on the AN (Window.Drawn)",
            Errors = "274 no window, an AN above 31999 or a font above 255 (raised and returned); 369 the AN is not on the page (trapped and returned)",
            Notes = "AN -2 = the task's AN, hFont -1 = the default font. iAlign 0..2 (else 0). iLengthMode 2: a positive iLength cuts the text to that many characters; modes 0 / 1 (pixels, 1 with an ellipsis): the full text is recorded with its length and mode, the viewer cuts it in the font (GDI measures it here for the tool tip). bTooltip: the full text becomes the AN's tool tip when cut, else the tip is cleared."
        )]
        private static Value DspText(SimHost h, CicodeTask t, ArgList a) =>
            W.I(
                Text(
                    h,
                    t,
                    (int)A.Short(A.I(a.Get(0))),
                    (int)A.Short(A.I(a.Get(1))),
                    A.S(a.Get(2)),
                    Arg(a, 3, 0),
                    Arg(a, 4, 0),
                    Arg(a, 5, 0),
                    Arg(a, 6, 0) != 0
                )
            );

        [Builtin(
            "DspStr",
            Doc = "1104731",
            Effects = "records a text output on the AN (Window.Drawn)",
            Errors = "274 neither the font name nor a default font (raised and returned); DspText's errors",
            Notes = "DspText(AN, DspFontHnd(sFont) or the default font, sText, ...). AN -2 = the task's AN."
        )]
        private static Value DspStr(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            int font = d.FontByName(A.S(a.Get(1)));
            if (font == -1)
                font = d.DefaultFont;
            if (font == -1)
            {
                W.Raise(t, 274);
                return W.I(274);
            }
            return W.I(
                Text(
                    h,
                    t,
                    (int)A.Short(A.I(a.Get(0))),
                    font,
                    A.S(a.Get(2)),
                    Arg(a, 3, 0),
                    Arg(a, 4, 0),
                    Arg(a, 5, 0),
                    Arg(a, 6, 0) != 0
                )
            );
        }

        // ------------------------------------------------------------------ symbols, bars, trends
        /// <summary>The checks the drawing outputs share (AN -2, a window, an AN below 32000, a
        /// page): 0 or the code (raised: 274; 269 no page).</summary>
        private static int Check(SimHost h, CicodeTask t, ref int an, out Window? w)
        {
            if (an == -2)
                an = TaskContext.An(t);
            w = DisplayWindows.Current(h, t);
            if (w == null || (ushort)an >= 32000)
            {
                W.Raise(t, 274);
                return 274;
            }
            if (w.Page == null)
            {
                W.Raise(t, 269);
                return 269;
            }
            return 0;
        }

        [Builtin(
            "DspSym",
            Doc = "1104732",
            Effects = "records a symbol output on the AN",
            Errors = "274 a type outside 0..5, no window or an AN of 32000+ (raised and returned); 269 no page (raised and returned)",
            Notes = "AN -2 = the task's AN; 'library.symbol' recorded with its mode and type (every symbol draws). A genie marked for use with DspSym (the project's '_' + SHA-1('library.element') .RDB) is placed: its element's records become objects under the AN, with their names and rows (the output's 'members')."
        )]
        private static Value DspSym(SimHost h, CicodeTask t, ArgList a)
        {
            long type = A.Short(Arg(a, 3, 0));
            if (type < 0 || type > 5)
            {
                W.Raise(t, 274);
                return W.I(274);
            }
            int an = (int)A.Short(A.I(a.Get(0)));
            int err = Check(h, t, ref an, out var w);
            if (err != 0)
                return W.I(err);
            var c = new DrawCommand
            {
                An = an,
                Kind = "sym",
                Text = A.S(a.Get(1)),
            };
            c.Arg("mode", Arg(a, 2, 0)).Arg("type", type);
            // a genie marked for use with DspSym: its records become objects under the AN
            // (Page/SymbolInstances.cs); the viewer gets them as 'members'
            if (D(h).Controller is ISymbolPlacer sp)
            {
                var members = sp.PlaceSymbol(w!, an, c.Text, (int)type);
                if (members != null)
                    c.Args.Add(new KeyValuePair<string, JsonValue>("members", members));
                else
                    sp.SymbolGone(w!, an);
            }
            Draw(D(h), w!, c);
            var o = w!.Page!.Get(an);
            if (o != null)
                CicodeExtents.Symbol(D(h), o, c.Text, type);
            return W.I(0);
        }

        [Builtin(
            "DspSymAtSize",
            Doc = "1104735",
            Effects = "records a symbol output at a position and size",
            Errors = "274 a type outside 0..5, no window or an AN of 32000+ (raised and returned); 269 no page",
            Notes = "Sizes clamped to 0..32000. AN -2 = the task's AN."
        )]
        private static Value DspSymAtSize(SimHost h, CicodeTask t, ArgList a)
        {
            long type = A.Short(Arg(a, 7, 0));
            if (type < 0 || type > 5)
            {
                W.Raise(t, 274);
                return W.I(274);
            }
            int an = (int)A.Short(A.I(a.Get(0)));
            int err = Check(h, t, ref an, out var w);
            if (err != 0)
                return W.I(err);
            long cx = Math.Max(0, Math.Min(32000, A.Short(A.I(a.Get(4)))));
            long cy = Math.Max(0, Math.Min(32000, A.Short(A.I(a.Get(5)))));
            var c = new DrawCommand
            {
                An = an,
                Kind = "sym",
                Text = A.S(a.Get(1)),
            };
            c.Arg("posX", A.Short(A.I(a.Get(2))))
                .Arg("posY", A.Short(A.I(a.Get(3))))
                .Arg("sizeX", cx)
                .Arg("sizeY", cy)
                .Arg("mode", Arg(a, 6, 0))
                .Arg("type", type);
            Draw(D(h), w!, c);
            var o = w!.Page!.Get(an);
            if (o != null)
            {
                if (cx == 0 || cy == 0)
                    CicodeExtents.Restore(o);
                else
                    CicodeExtents.Symbol(
                        D(h),
                        o,
                        c.Text,
                        type,
                        (int)A.Short(A.I(a.Get(2))),
                        (int)A.Short(A.I(a.Get(3))),
                        cx / 32000.0,
                        cy / 32000.0
                    );
            }
            return W.I(0);
        }

        [Builtin(
            "DspSymAnm",
            Doc = "1104733",
            Effects = "records an animated symbol (up to 9 frames) on the AN",
            Errors = "274 no window, an AN of 32000+ or a display mode outside 0..1 without the bits 8 / 16 (raised and returned); 269 no page",
            Notes = "The frames cycle at [Page]AnmDelay in the runtime; the record lists them (args symbols, mode)."
        )]
        private static Value DspSymAnm(SimHost h, CicodeTask t, ArgList a)
        {
            long mode = A.Short(Arg(a, 9, 0));
            int an = (int)A.Short(A.I(a.Get(0)));
            if (!((mode >= 0 && mode < 2) || (mode & 0x18) != 0))
            {
                W.Raise(t, 274);
                return W.I(274);
            }
            int err = Check(h, t, ref an, out var w);
            if (err != 0)
                return W.I(err);
            var syms = new List<string>();
            for (int i = 1; i <= 8; i++)
            {
                string s = A.S(a.Get(i));
                if (s.Length > 0)
                    syms.Add(s);
            }
            string s9 = A.S(a.Get(10));
            if (s9.Length > 0)
                syms.Add(s9);
            var c = new DrawCommand
            {
                An = an,
                Kind = "symAnm",
                Text = syms.Count > 0 ? syms[0] : "",
            };
            c.Arg("symbols", string.Join(",", syms)).Arg("mode", mode);
            Draw(D(h), w!, c);
            var o = w!.Page!.Get(an);
            if (o != null)
                CicodeExtents.Symbol(D(h), o, c.Text, 0);
            return W.I(0);
        }

        [Builtin(
            "DspBar",
            Doc = "1104650",
            Effects = "records a bar output on the AN",
            Errors = "274 no window or an AN of 32000+ (raised and returned); 269 no page",
            Notes = "AN -2 = the task's AN; the bar name and the value (0..32000 of the bar's range in the runtime) recorded."
        )]
        private static Value DspBar(SimHost h, CicodeTask t, ArgList a)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            int err = Check(h, t, ref an, out var w);
            if (err != 0)
                return W.I(err);
            var c = new DrawCommand
            {
                An = an,
                Kind = "bar",
                Text = A.S(a.Get(1)),
            };
            c.Arg("value", A.Short(A.I(a.Get(2))));
            Draw(D(h), w!, c);
            return W.I(0);
        }

        private static Value Series(SimHost h, CicodeTask t, ArgList a, string kind)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            int err = Check(h, t, ref an, out var w);
            if (err != 0)
                return W.I(err);
            var c = new DrawCommand
            {
                An = an,
                Kind = kind,
                Text = A.S(a.Get(1)),
            };
            var vals = new List<string>();
            for (int i = 2; i < a.Count && i < 10; i++)
                if (!a[i].IsNothing)
                    vals.Add(W.Inv(A.Short(A.I(a[i]))));
            c.Arg("values", string.Join(",", vals));
            Draw(D(h), w!, c);
            return W.I(0);
        }

        [Builtin(
            "DspTrend",
            Doc = "1104738",
            Effects = "records the trend output on the AN (its latest values)",
            Errors = "274 no window or an AN of 32000+ (raised and returned); 269 no page",
            Notes = "The trend name and up to 8 pen values recorded (the scrolling trace is the renderer's)."
        )]
        private static Value DspTrend(SimHost h, CicodeTask t, ArgList a) => Series(h, t, a, "trend");

        [Builtin(
            "DspChart",
            Doc = "1104654",
            Effects = "records the chart output on the AN",
            Errors = "274 no window or an AN of 32000+ (raised and returned); 269 no page"
        )]
        private static Value DspChart(SimHost h, CicodeTask t, ArgList a) => Series(h, t, a, "chart");

        [Builtin(
            "DspBmp",
            Doc = "1104651",
            Effects = "records a bitmap file output on the AN",
            Errors = "274 no window or an AN of 32000+ (raised and returned); 269 no page",
            Notes = "The file name and mode recorded (the file is not read)."
        )]
        private static Value DspBmp(SimHost h, CicodeTask t, ArgList a)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            int err = Check(h, t, ref an, out var w);
            if (err != 0)
                return W.I(err);
            var c = new DrawCommand
            {
                An = an,
                Kind = "bmp",
                Text = A.S(a.Get(1)),
            };
            c.Arg("mode", Arg(a, 2, 0));
            Draw(D(h), w!, c);
            return W.I(0);
        }

        [Builtin(
            "DspCol",
            Doc = "1104656",
            Notes = "Deprecated: the runtime's entry is: nothing happens, 0."
        )]
        private static Value DspCol(SimHost h, CicodeTask t, ArgList a) => W.I(0);

        [Builtin(
            "DspDel",
            Doc = "1104657",
            Effects = "removes what Cicode drew on the AN",
            Errors = "274 no window or an AN above 31999 (raised and returned); 269 no page (returned)",
            Notes = "AN -2 = the task's AN. Returns 0 (also when nothing was drawn). the runtime's 274 for an AN of a dynamic set and 408 for an AN an animation owns are not modelled (guess)."
        )]
        private static Value DspDel(SimHost h, CicodeTask t, ArgList a)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            if (an == -2)
                an = TaskContext.An(t);
            var w = DisplayWindows.Current(h, t);
            if (w == null || (ushort)an > 31999)
            {
                W.Raise(t, 274);
                return W.I(274);
            }
            if (w.Page == null)
                return W.I(269);
            D(h).Undraw(w, an);
            return W.I(0);
        }

        [Builtin(
            "DspError",
            Doc = "1104661",
            Effects = "the message on the task's window's prompt line",
            Notes = "Returns 0."
        )]
        private static Value DspError(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            d.SetPrompt(d.WindowOf(t), A.S(a.Get(0)));
            h.Note(t, "DspError: " + A.S(a.Get(0)));
            return W.I(0);
        }

        // ------------------------------------------------------------------ files, rich text, sound
        /// <summary>A file display's output: its window, offsets and the lines it shows, each at
        /// its AN (the display's AN + k) where the page has that AN, cut to the window's width
        /// from the column offset.</summary>
        private static DrawCommand FileOutput(
            DisplayState d,
            Window w,
            int an,
            int font,
            string name,
            FileView f
        )
        {
            var c = new DrawCommand
            {
                An = an,
                Kind = "file",
                Font = font,
                Text = name,
                File = f,
            };
            c.Arg("height", f.Height)
                .Arg("width", f.Width)
                .Arg("row", f.Row)
                .Arg("col", f.Col)
                .Arg("count", f.Lines.Count);
            var lines = JsonValue.NewArray();
            for (int k = 0; k < f.Height && f.Row + k < f.Lines.Count; k++)
            {
                var o = w.Page?.Get(an + k);
                if (o == null)
                    continue;
                string s = f.Lines[f.Row + k];
                s = f.Col < s.Length ? s.Substring(f.Col) : "";
                if (s.Length > Math.Max(0, f.Width))
                    s = s.Substring(0, Math.Max(0, f.Width));
                var (x, y) = DisplayAns.CurrentPos(d, w, o);
                var e = JsonValue.NewObject();
                e.Set("an", JsonValue.Of(an + k));
                e.Set("x", JsonValue.Of(x));
                e.Set("y", JsonValue.Of(y));
                e.Set("text", s);
                lines.Add(e);
            }
            c.Args.Add(new KeyValuePair<string, JsonValue>("lines", lines));
            return c;
        }

        /// <summary>The AN's file display on the task's window, else null.</summary>
        private static DrawCommand? FileAt(SimHost h, CicodeTask t, int an, out Window? w)
        {
            w = DisplayWindows.Current(h, t);
            var c = w?.DrawnAt(an);
            return c != null && c.Kind == "file" && c.File != null ? c : null;
        }

        /// <summary>A file's lines as FileReadLn reads them: ended by CR LF, CR or LF, each the
        /// text up to a NUL.</summary>
        private static List<string> FileLines(byte[] data)
        {
            var o = new List<string>();
            int i = 0;
            while (i < data.Length)
            {
                int a = i;
                while (i < data.Length && data[i] != '\r' && data[i] != '\n')
                    i++;
                o.Add(Cp1252.DecodeZ(data, a, i - a));
                if (i < data.Length && data[i] == '\r')
                {
                    i++;
                    if (i < data.Length && data[i] == '\n')
                        i++;
                }
                else if (i < data.Length)
                    i++;
            }
            return o;
        }

        [Builtin(
            "DspFile",
            Doc = "1104662",
            Effects = "records a file display window on the AN",
            Errors = "274 no window or an AN of 32000+ (raised and returned); 269 no page",
            Notes = "Height lines of Width characters, line k at the AN + k; no file until DspFileSetName. A file display made again starts empty."
        )]
        private static Value DspFile(SimHost h, CicodeTask t, ArgList a)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            int err = Check(h, t, ref an, out var w);
            if (err != 0)
                return W.I(err);
            var f = new FileView
            {
                Height = (int)A.Short(A.I(a.Get(2))),
                Width = (int)A.Short(A.I(a.Get(3))),
            };
            var d = D(h);
            Draw(d, w!, FileOutput(d, w!, an, (int)A.Short(A.I(a.Get(1))), "", f));
            return W.I(0);
        }

        [Builtin(
            "DspFileSetName",
            Doc = "1104666",
            Effects = "the file a file display window shows",
            Errors = "262 the file cannot be read (returned, not raised; the display shows no lines) (guess)",
            Notes = "Reads the file (through the sandbox, as FileOpen: the unit's project folders are readable, and so is a file Cicode wrote) and shows it from its first line and column. An AN without a file display: 0, nothing kept (guess)."
        )]
        private static Value DspFileSetName(SimHost h, CicodeTask t, ArgList a)
        {
            var c = FileAt(h, t, (int)A.Short(A.I(a.Get(0))), out var w);
            if (c == null)
                return W.I(0);
            string name = A.S(a.Get(1));
            byte[]? data = h.Sandbox.ReadAll(Files.FullName(h, name));
            var f = c.File!.Copy();
            f.Row = f.Col = 0;
            f.Lines = data != null ? FileLines(data) : new List<string>();
            var d = D(h);
            Draw(d, w!, FileOutput(d, w!, c.An, c.Font, name, f));
            return W.I(data != null ? 0 : 262);
        }

        [Builtin(
            "DspFileGetName",
            Doc = "1104664",
            Notes = "The file of the AN's file display, \"\" when none."
        )]
        private static Value DspFileGetName(SimHost h, CicodeTask t, ArgList a)
        {
            var c = DisplayWindows.Current(h, t)?.DrawnAt((int)A.Short(A.I(a.Get(0))));
            return W.S(c != null && c.Kind == "file" ? c.Text : "");
        }

        [Builtin(
            "DspFileScroll",
            Doc = "1104665",
            Effects = "the row or column offset of a file display",
            Notes = "Direction 1 left, 2 right, 3 up, 4 down, by nChars characters or lines. Up and left stop at the first row and column; down stops when the last line is on the window's last row, right when the longest line's end is at the window's right edge (guess). Another direction or an AN without a file display: nothing, 0 (guess)."
        )]
        private static Value DspFileScroll(SimHost h, CicodeTask t, ArgList a)
        {
            var c = FileAt(h, t, (int)A.Short(A.I(a.Get(0))), out var w);
            if (c == null)
                return W.I(0);
            var f = c.File!.Copy();
            int n = (int)Math.Max(0, Math.Min(int.MaxValue, A.I(a.Get(2))));
            int longest = 0;
            foreach (string s in f.Lines)
                longest = Math.Max(longest, s.Length);
            switch (A.I(a.Get(1)))
            {
                case 1:
                    f.Col = Math.Max(0, f.Col - n);
                    break;
                case 2:
                    f.Col = (int)Math.Min((long)f.Col + n, Math.Max(f.Col, longest - f.Width));
                    break;
                case 3:
                    f.Row = Math.Max(0, f.Row - n);
                    break;
                case 4:
                    f.Row = (int)Math.Min((long)f.Row + n, Math.Max(f.Row, f.Lines.Count - f.Height));
                    break;
                default:
                    return W.I(0);
            }
            var d = D(h);
            Draw(d, w!, FileOutput(d, w!, c.An, c.Font, c.Text, f));
            return W.I(0);
        }

        [Builtin(
            "DspFileGetInfo",
            Doc = "1104663",
            Notes = "Type 0 the window's width in characters, 1 its height in lines, 2 the row offset, 3 the column offset, 4 the file's lines; another type 0 (guess). An AN without a file display: 0, nothing raised (the Include's _PageFile makes the display when DspFileGetInfo(21, 0) <= 0)."
        )]
        private static Value DspFileGetInfo(SimHost h, CicodeTask t, ArgList a)
        {
            var c = FileAt(h, t, (int)A.Short(A.I(a.Get(0))), out _);
            if (c == null)
                return W.I(0);
            var f = c.File!;
            switch (A.I(a.Get(1)))
            {
                case 0:
                    return W.I(f.Width);
                case 1:
                    return W.I(f.Height);
                case 2:
                    return W.I(f.Row);
                case 3:
                    return W.I(f.Col);
                case 4:
                    return W.I(f.Lines.Count);
            }
            return W.I(0);
        }

        /// <summary>A rich text box's output: its size, mode, state, scroll position, lines and
        /// RTF.</summary>
        private static DrawCommand RichOutput(int an, string name, RichTextView r)
        {
            var c = new DrawCommand
            {
                An = an,
                Kind = "richText",
                Text = name,
                RichText = r,
            };
            c.Arg("height", r.Height)
                .Arg("width", r.Width)
                .Arg("mode", r.Mode)
                .Arg("enabled", r.Enabled ? 1 : 0)
                .Arg("editable", r.Editable ? 1 : 0)
                .Arg("row", r.Row)
                .Arg("col", r.Col)
                .Arg("count", r.Lines.Count)
                .Arg("page", r.PageLines)
                .Arg("rtf", r.Rtf);
            return c;
        }

        /// <summary>The AN's rich text box on the task's window, else null.</summary>
        private static DrawCommand? RichAt(SimHost h, CicodeTask t, int an, out Window? w)
        {
            w = DisplayWindows.Current(h, t);
            var c = w?.DrawnAt(an);
            return c != null && c.Kind == "richText" && c.RichText != null ? c : null;
        }

        /// <summary>Change the AN's rich text box: <paramref name="change"/> edits a copy (false:
        /// nothing changes) and the box is drawn again. 0, or 274 (returned) when the AN has no
        /// rich text box.</summary>
        private static Value RichChange(
            SimHost h,
            CicodeTask t,
            ArgList a,
            Func<RichTextView, bool> change,
            Func<DrawCommand, string>? name = null
        )
        {
            var c = RichAt(h, t, (int)A.Short(A.I(a.Get(0))), out var w);
            if (c == null)
                return W.I(Codes.InvalidArg);
            var r = c.RichText!.Copy();
            if (!change(r))
                return W.I(Codes.InvalidArg);
            Draw(D(h), w!, RichOutput(c.An, name != null ? name(c) : c.Text, r));
            return W.I(0);
        }

        /// <summary>The RTF a rich text box writes: what it shows, an empty document when nothing
        /// was loaded (guess).</summary>
        private static byte[] RichBytes(RichTextView r) =>
            Cp1252.Encode(r.Rtf.Length > 0 ? r.Rtf : "{\\rtf1\\ansi }");

        /// <summary>Write RTF to the port's next printout file (Printers\(port)-(n).rtf).</summary>
        private static int PrintRich(SimHost h, CicodeTask t, string fn, string port, byte[] data)
        {
            string full = Plots.NextPrintout(h, port, "rtf");
            if (h.Sandbox.WriteOutput(full, data, t.Vm.Clock.NowFiletime()))
            {
                h.Note(t, fn + ": the printout for " + port + " is in " + full);
                return 0;
            }
            h.Note(t, fn + ": the printout for " + port + " could not be written to " + full);
            return 262;
        }

        [Builtin(
            "DspRichText",
            Doc = "1104708",
            Effects = "records a rich text box on the AN",
            Errors = "274 no window or an AN of 32000+ (raised and returned); 269 no page",
            Notes = "iHeight by iWidth pixels down and right from the AN, empty until DspRichTextLoad; made again it starts empty (guess). nMode: + 1 enabled, + 2 read / write (editing needs it enabled too). The viewer shows the text read-only: edits are not sent back."
        )]
        private static Value DspRichText(SimHost h, CicodeTask t, ArgList a)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            int err = Check(h, t, ref an, out var w);
            if (err != 0)
                return W.I(err);
            int mode = (int)Arg(a, 3, 0);
            var r = new RichTextView
            {
                Height = (int)Math.Max(0, A.Short(A.I(a.Get(1)))),
                Width = (int)Math.Max(0, A.Short(A.I(a.Get(2)))),
                Mode = mode,
                Enabled = (mode & 1) != 0,
                Editable = (mode & 2) != 0,
            };
            Draw(D(h), w!, RichOutput(an, "", r));
            return W.I(0);
        }

        [Builtin(
            "DspRichTextLoad",
            Doc = "1104712",
            Effects = "the file a rich text box shows",
            Errors = "274 the AN has no rich text box (returned) (guess); 262 the file cannot be read or is over 512 KB (returned; the box shows nothing) (guess)",
            Notes = "Reads the file (through the sandbox, as FileOpen: the unit's project folders are readable, and so is a file Cicode or a report wrote) and shows it from its first line and column. Its lines are its paragraphs (\\par)."
        )]
        private static Value DspRichTextLoad(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.S(a.Get(1));
            byte[]? data = null;
            var v = RichChange(
                h,
                t,
                a,
                r =>
                {
                    data = h.Sandbox.ReadAll(Files.FullName(h, name));
                    if (data != null && data.Length > RichTextView.MaxBytes)
                        data = null;
                    r.Rtf = data != null ? Cp1252.DecodeZ(data, 0) : "";
                    r.Lines = RichTextView.Paragraphs(r.Rtf);
                    r.Row = r.Col = 0;
                    return true;
                },
                _ => name
            );
            return A.I(v) != 0 ? v : W.I(data != null ? 0 : 262);
        }

        /// <summary>Scroll a rich text box: 1 left, 2 right, 3 up, 4 down by n lines or
        /// characters, 8 to the top, 16 to the bottom; false for another direction.</summary>
        private static bool Scroll(RichTextView r, long dir, long n)
        {
            n = Math.Max(0, Math.Min(int.MaxValue, n));
            switch (dir)
            {
                case 1:
                    r.Col = (int)Math.Max(0, r.Col - n);
                    return true;
                case 2:
                    r.Col = (int)Math.Min(r.Col + n, Math.Max(r.Col, r.MaxCol));
                    return true;
                case 3:
                    r.Row = (int)Math.Max(0, r.Row - n);
                    return true;
                case 4:
                    r.Row = (int)Math.Min(r.Row + n, Math.Max(r.Row, r.MaxRow));
                    return true;
                case 8:
                    r.Row = 0;
                    return true;
                case 16:
                    r.Row = Math.Max(r.Row, r.MaxRow);
                    return true;
            }
            return false;
        }

        [Builtin(
            "DspRichTextScroll",
            Doc = "1104716",
            Effects = "the line or column offset of a rich text box",
            Errors = "274 the AN has no rich text box or another direction (returned) (guess)",
            Notes = "iDirection 1 left, 2 right, 3 up, 4 down by iAmount characters (8 pixels, guess) or lines (paragraphs, guess), 8 to the top, 16 to the bottom. Up and left stop at the first line and column; down stops when the last line is on the box's last row (16 pixel lines, guess), right when the longest line's end is at the box's right edge (guess)."
        )]
        private static Value DspRichTextScroll(SimHost h, CicodeTask t, ArgList a)
        {
            long dir = A.I(a.Get(1)),
                n = A.I(a.Get(2));
            return RichChange(h, t, a, r => Scroll(r, dir, n));
        }

        [Builtin(
            "DspRichTextPgScroll",
            Doc = "1104713",
            Effects = "the line or column offset of a rich text box",
            Errors = "274 the AN has no rich text box or another direction (returned) (guess)",
            Notes = "As DspRichTextScroll by a page: the box's height in lines (16 pixels each, guess) up or down, its width in characters (8 pixels each, guess) left or right; 8 to the top, 16 to the bottom."
        )]
        private static Value DspRichTextPgScroll(SimHost h, CicodeTask t, ArgList a)
        {
            long dir = A.I(a.Get(1));
            return RichChange(h, t, a, r => Scroll(r, dir, dir == 1 || dir == 2 ? r.PageCols : r.PageLines));
        }

        [Builtin(
            "DspRichTextGetInfo",
            Doc = "1104711",
            Notes = "As a string: type 0 the box's height, 1 its width (pixels). Another type or an AN without a rich text box: \"\" (guess)."
        )]
        private static Value DspRichTextGetInfo(SimHost h, CicodeTask t, ArgList a)
        {
            var c = RichAt(h, t, (int)A.Short(A.I(a.Get(0))), out _);
            if (c == null)
                return W.Empty;
            var r = c.RichText!;
            switch (A.I(a.Get(1)))
            {
                case 0:
                    return W.S(W.Inv(r.Height));
                case 1:
                    return W.S(W.Inv(r.Width));
            }
            return W.Empty;
        }

        [Builtin(
            "DspRichTextSave",
            Doc = "1104715",
            Effects = "writes the box's RTF to the file",
            Errors = "274 the AN has no rich text box (returned) (guess); 262 the file cannot be written (returned) (guess)",
            Notes = "Writes the RTF the box shows (as loaded: edits in the viewer are not sent back) through the sandbox, replacing the file; a box with nothing loaded writes an empty document (guess)."
        )]
        private static Value DspRichTextSave(SimHost h, CicodeTask t, ArgList a)
        {
            var c = RichAt(h, t, (int)A.Short(A.I(a.Get(0))), out _);
            if (c == null)
                return W.I(Codes.InvalidArg);
            byte[] data = RichBytes(c.RichText!);
            ulong now = t.Vm.Clock.NowFiletime();
            var f = h.Sandbox.Open(Files.FullName(h, A.S(a.Get(1))), false, true, true, true, now);
            if (f == null || (data.Length > 0 && h.Sandbox.Write(f, data, data.Length, now) != data.Length))
                return W.I(262);
            return W.I(0);
        }

        [Builtin(
            "DspRichTextPrint",
            Doc = "1104714",
            Effects = "writes the box's RTF to the sandbox as the port's printout",
            Errors = "274 the AN has no rich text box (returned) (guess); 262 the printout cannot be written (returned) (guess)",
            Notes = "No printer: the RTF the box shows goes to the sandbox as Printers\\(port)-(n).rtf, the port with \\ / : * ? \" < > | as '_', n counting that port's printouts (plots and rich text) from 1."
        )]
        private static Value DspRichTextPrint(SimHost h, CicodeTask t, ArgList a)
        {
            var c = RichAt(h, t, (int)A.Short(A.I(a.Get(0))), out _);
            if (c == null)
                return W.I(Codes.InvalidArg);
            return W.I(PrintRich(h, t, "DspRichTextPrint", A.S(a.Get(1)), RichBytes(c.RichText!)));
        }

        [Builtin(
            "FileRichTextPrint",
            Doc = "1105376",
            Effects = "writes the file to the sandbox as the port's printout",
            Errors = "262 the file cannot be read or the printout cannot be written (returned) (guess)",
            Notes = "No printer: the file (read through the sandbox) goes to the sandbox as Printers\\(port)-(n).rtf, as DspRichTextPrint."
        )]
        private static Value FileRichTextPrint(SimHost h, CicodeTask t, ArgList a)
        {
            byte[]? data = h.Sandbox.ReadAll(Files.FullName(h, A.S(a.Get(0))));
            if (data == null)
                return W.I(262);
            return W.I(PrintRich(h, t, "FileRichTextPrint", A.S(a.Get(1)), data));
        }

        [Builtin(
            "DspRichTextEnable",
            Doc = "1104710",
            Effects = "enables or disables a rich text box",
            Errors = "274 the AN has no rich text box (returned) (guess)",
            Notes = "Kept on the box and sent to the viewer (args.enabled). The viewer shows the text read-only either way."
        )]
        private static Value DspRichTextEnable(SimHost h, CicodeTask t, ArgList a)
        {
            bool on = A.I(a.Get(1)) != 0;
            return RichChange(h, t, a, r =>
            {
                r.Enabled = on;
                return true;
            });
        }

        [Builtin(
            "DspRichTextEdit",
            Doc = "1104709",
            Effects = "makes a rich text box read / write or read-only",
            Errors = "274 the AN has no rich text box (returned) (guess)",
            Notes = "Kept on the box and sent to the viewer (args.editable). Edits in the viewer are not sent back, so DspRichTextSave writes the loaded RTF."
        )]
        private static Value DspRichTextEdit(SimHost h, CicodeTask t, ArgList a)
        {
            bool on = A.I(a.Get(1)) != 0;
            return RichChange(h, t, a, r =>
            {
                r.Editable = on;
                return true;
            });
        }

        [Builtin(
            "DspPlaySound",
            Doc = "1104705",
            Effects = "logs the sound (nothing plays)",
            Notes = "Returns 0."
        )]
        private static Value DspPlaySound(SimHost h, CicodeTask t, ArgList a)
        {
            h.Note(t, "DspPlaySound: " + A.S(a.Get(0)) + " mode " + W.Inv(Arg(a, 1, 0)));
            return W.I(0);
        }

        [Builtin(
            "DspMCI",
            Status = BuiltinStatus.Stub,
            Returns = "\"\"",
            Doc = "1104704",
            Notes = "A Windows media control command: no device, \"\"."
        )]
        private static Value DspMCI(SimHost h, CicodeTask t, ArgList a) => W.Empty;

        // ------------------------------------------------------------------ tool tips
        [Builtin(
            "DspSetTip",
            Doc = "1104728",
            Effects = "the AN's tool tip (it replaces the configured one)",
            Errors = "274 an AN of -1 (returned); 274 no window or an AN outside 1..31999 (raised and returned)",
            Notes = "Returns 0, also for an AN the page does not have. The tip shows when the pointer rests on the object."
        )]
        private static Value DspSetTip(SimHost h, CicodeTask t, ArgList a)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            if (an == -1)
                return W.I(274);
            var w = DisplayWindows.Current(h, t);
            if (w == null || !DisplayAns.AnOk(an))
            {
                W.Raise(t, 274);
                return W.I(274);
            }
            var o = w.Page?.Get(an);
            if (o != null)
            {
                o.Tip = A.S(a.Get(1));
                o.TipSet = true;
            }
            return W.I(0);
        }

        [Builtin(
            "DspGetTip",
            Doc = "1104693",
            Errors = "274 no window or an AN outside 1..31999 (raised, \"\"); 274 a mode other than 0 / 1 (trapped, the tip returned)",
            Notes = "The AN's tool tip (DspSetTip's, else the configured one), cut to 255; mode 1: up to the first line feed. An AN of -1: \"\"."
        )]
        private static Value DspGetTip(SimHost h, CicodeTask t, ArgList a)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            if (an == -1)
                return W.Empty;
            var w = DisplayWindows.Current(h, t);
            if (w == null || !DisplayAns.AnOk(an))
            {
                W.Raise(t, 274);
                return W.Empty;
            }
            string s = w.Page?.Get(an)?.Tip ?? "";
            if (s.Length > 255)
                s = s.Substring(0, 255);
            long mode = A.Short(Arg(a, 1, 0));
            if (mode == 1)
            {
                int k = s.IndexOf('\n');
                if (k >= 0)
                    s = s.Substring(0, k);
            }
            else if (mode != 0)
                W.Raise(t, 274);
            return W.S(s);
        }

        [Builtin(
            "DspTipMode",
            Doc = "1104737",
            Effects = "tool tips on / off for the process",
            Errors = "274 a mode of 4 or more (raised)",
            Notes = "0 off, 1 on, 2 toggle, 3 no change; returns the previous mode (1 = on at start: guess)."
        )]
        private static Value DspTipMode(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            int prev = d.TipMode;
            ushort mode = (ushort)A.I(a.Get(0));
            if (mode >= 4)
            {
                W.Raise(t, 274);
                return W.I(prev);
            }
            if (mode == 0)
                d.TipMode = 0;
            else if (mode == 1)
                d.TipMode = 1;
            else if (mode == 2)
                d.TipMode = prev == 0 ? 1 : 0;
            return W.I(prev);
        }

        // ------------------------------------------------------------------ the rubber band
        /// <summary>A coordinate kept inside [lo, hi] the runtime's way (lo wins when the clip is
        /// inverted).</summary>
        private static int ClipTo(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));

        [Builtin(
            "DspRubStart",
            Doc = "1104721",
            Effects = "starts the window's rubber band at (x, y) (a band already shown is replaced)",
            Errors = "274 no window (returned)",
            Notes = "Mode: bit 1 = DspRubEnd gives cx, cy relative to x, y; bit 2 = the corners ordered (top left first); bit 4 = the band kept inside the clip rectangle (DspRubSetClip), else not clipped. Returns 0."
        )]
        private static Value DspRubStart(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            if (w == null)
                return W.I(274);
            int x = (int)A.Short(A.I(a.Get(0))),
                y = (int)A.Short(A.I(a.Get(1)));
            int mode = (int)A.Short(A.I(a.Get(2)));
            var r = w.Rub ?? new RubberBand();
            r.Mode = mode;
            r.Active = true;
            r.Clip = (mode & 4) != 0;
            if (r.Clip)
            {
                x = ClipTo(x, r.ClipL, r.ClipR);
                y = ClipTo(y, r.ClipT, r.ClipB);
            }
            r.X0 = r.X1 = x;
            r.Y0 = r.Y1 = y;
            w.Rub = r;
            return W.I(0);
        }

        [Builtin(
            "DspRubMove",
            Doc = "1104718",
            Effects = "moves the rubber band's corner",
            Errors = "274 no window or no rubber band shown (returned)",
            Notes = "The corner is kept inside the clip rectangle while the band is clipped. Returns 0."
        )]
        private static Value DspRubMove(SimHost h, CicodeTask t, ArgList a)
        {
            var r = DisplayWindows.Current(h, t)?.Rub;
            if (r == null || !r.Active)
                return W.I(274);
            int x = (int)A.Short(A.I(a.Get(0))),
                y = (int)A.Short(A.I(a.Get(1)));
            if (r.Clip)
            {
                x = ClipTo(x, r.ClipL, r.ClipR);
                y = ClipTo(y, r.ClipT, r.ClipB);
            }
            r.X1 = x;
            r.Y1 = y;
            return W.I(0);
        }

        [Builtin(
            "DspRubEnd",
            Doc = "1104717",
            Effects = "ends the rubber band",
            Errors = "274 no window or no rubber band shown (returned, all 0)",
            Notes = "Writes x, y, cx, cy by the band's mode: the start point and the current point as they are (mode 0: cx, cy absolute pixel positions); bit 2 ordered, top left then bottom right; bit 1 then cx, cy minus x, y. The band ends, the clip rectangle stays."
        )]
        private static Value DspRubEnd(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            var r = w?.Rub;
            if (w == null || r == null || !r.Active)
            {
                for (int i = 0; i < 4; i++)
                    W.Out(t, a.Get(i), 0);
                return W.I(274);
            }
            int x = r.X0,
                y = r.Y0,
                cx = r.X1,
                cy = r.Y1;
            if ((r.Mode & 2) != 0)
            {
                x = Math.Min(r.X0, r.X1);
                cx = Math.Max(r.X0, r.X1);
                y = Math.Min(r.Y0, r.Y1);
                cy = Math.Max(r.Y0, r.Y1);
            }
            if ((r.Mode & 1) != 0)
            {
                cx -= x;
                cy -= y;
            }
            W.Out(t, a.Get(0), x);
            W.Out(t, a.Get(1), y);
            W.Out(t, a.Get(2), cx);
            W.Out(t, a.Get(3), cy);
            r.Active = false;
            return W.I(0);
        }

        [Builtin(
            "DspRubSetClip",
            Doc = "1104719",
            Effects = "the rubber band's clip rectangle (ordered); the band shown is clipped from now on",
            Errors = "274 no window (returned)",
            Notes = "The rectangle is kept with its corners ordered and turns clipping on (1) until the next DspRubStart sets it from its mode. Returns 0."
        )]
        private static Value DspRubSetClip(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            if (w == null)
                return W.I(274);
            var r = w.Rub ?? (w.Rub = new RubberBand());
            int l = (int)A.Short(A.I(a.Get(0))),
                tp = (int)A.Short(A.I(a.Get(1)));
            int rt = (int)A.Short(A.I(a.Get(2))),
                b = (int)A.Short(A.I(a.Get(3)));
            r.ClipL = Math.Min(l, rt);
            r.ClipR = Math.Max(l, rt);
            r.ClipT = Math.Min(tp, b);
            r.ClipB = Math.Max(tp, b);
            r.Clip = true;
            return W.I(0);
        }

        // ------------------------------------------------------------------ object information
        private sealed class InfoBlock
        {
            public int Window,
                An;

            /// <summary>The page the block was made on, held weakly: a block nobody destroys must not
            /// keep a closed page (its objects and drawing) alive.</summary>
            public WeakReference<PageInstance>? Page;
            public PageInstance? PageNow => Page != null && Page.TryGetTarget(out var p) ? p : null;
            public List<AnExpression> Exprs = new List<AnExpression>();
            public List<List<(string Tag, string Cluster)>> Tags = new List<List<(string, string)>>();
            public int Last;
        }

        private sealed class InfoTable
        {
            public readonly HandleTable<InfoBlock> Blocks = new HandleTable<InfoBlock>();
        }

        private static readonly ConditionalWeakTable<SimHost, InfoTable> Infos =
            new ConditionalWeakTable<SimHost, InfoTable>();

        /// <summary>The tags a code block reads or writes (its Load / Store TAGREFs, in order, once each).</summary>
        private static List<(string, string)> TagsOf(AnExpression e)
        {
            var l = new List<(string, string)>();
            if (e.Program == null)
                return l;
            List<Instr> ins;
            try
            {
                ins = Opcodes.Instructions(e.Code);
            }
            catch (DecodeError)
            {
                return l;
            }
            foreach (var i in ins)
            {
                if (i.Op != Op.Load && i.Op != Op.Store)
                    continue;
                if (!e.Program.TagRefs.TryGetValue(i.Offset, out var r))
                    continue;
                string tag = r.Tag + (r.Item.Length > 0 ? "." + r.Item : "");
                if (!l.Exists(x => string.Equals(x.Item1, tag, StringComparison.OrdinalIgnoreCase)))
                    l.Add((tag, r.Cluster));
            }
            return l;
        }

        private static InfoBlock? Block(SimHost h, long hd)
        {
            var b = Infos.GetValue(h, _ => new InfoTable()).Blocks.Get(hd);
            if (b == null)
                return null;
            var w = D(h).OpenWindow(b.Window);
            if (w != null && w.Page != null && ReferenceEquals(w.Page, b.PageNow))
                return b;
            // the page closed: the block stays allocated (DspInfoDestroy frees it) but lets go of
            // the page's compiled expressions
            b.Page = null;
            b.Exprs = new List<AnExpression>();
            b.Tags.Clear();
            return null;
        }

        [Builtin(
            "DspInfoNew",
            Doc = "1104698",
            Effects = "creates an object information block",
            Notes = "A handle over the AN's compiled expressions (the page runtime's: visibility, movement, colour, text ...), -1 when the AN has none, there is no page, or 383 blocks are open. The block is valid while the page shows."
        )]
        private static Value DspInfoNew(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            var w = DisplayWindows.Current(h, t);
            int an = (int)A.Short(A.I(a.Get(0)));
            if (w?.Page == null)
                return W.I(-1);
            var ex = d.Runtime.Expressions(w.Number, an);
            if (ex == null)
                return W.I(-1);
            var tab = Infos.GetValue(h, _ => new InfoTable()).Blocks;
            if (tab.Count >= 383)
                return W.I(-1);
            var b = new InfoBlock
            {
                Window = w.Number,
                An = an,
                Page = new WeakReference<PageInstance>(w.Page),
                Exprs = ex,
            };
            foreach (var e in ex)
                b.Tags.Add(TagsOf(e));
            return W.I(tab.Add(b));
        }

        [Builtin(
            "DspInfoValid",
            Doc = "1104699",
            Notes = "1 while the block's page shows, 0 after DspInfoDestroy or the page closed."
        )]
        private static Value DspInfoValid(SimHost h, CicodeTask t, ArgList a) =>
            W.I(Block(h, A.I(a.Get(0))) != null ? 1 : 0);

        [Builtin(
            "DspInfoDestroy",
            Doc = "1104696",
            Effects = "frees an object information block",
            Notes = "Returns 0."
        )]
        private static Value DspInfoDestroy(SimHost h, CicodeTask t, ArgList a)
        {
            Infos.GetValue(h, _ => new InfoTable()).Blocks.Free(A.I(a.Get(0)));
            return W.I(0);
        }

        /// <summary>The name of an object's class (DspInfo type 0, guess per class).</summary>
        private static string Title(int tc) =>
            tc switch
            {
                1 => "Line",
                2 => "Freehand",
                3 or 0xe or 0xf => "Rectangle",
                4 or 0x11 => "Ellipse",
                5 => "Text",
                7 or 0 => "Symbol",
                8 => "Group",
                0xa or 0x10 => "Polygon",
                0xb => "Pipe",
                0xc => "Symbol Set",
                0xd => "ActiveX",
                0x13 => "Web Content",
                0x1001 => "Button",
                0x1002 => "Bar",
                0x1003 => "Trend",
                0x1004 => "Number",
                0x1005 => "String",
                0x1006 => "Text",
                0x1007 => "Symbol",
                0x1008 => "Colour",
                0x1009 => "Cicode Object",
                0x100d => "Animated Symbol",
                0x100f => "Slider",
                _ => "Object",
            };

        [Builtin(
            "DspInfo",
            Doc = "1104695",
            Errors = "269 a block that is not valid (raised, \"\")",
            Notes = "Types: 0 the object's class name (guess), 1 the expression text and 2 its result text (\"\": the compiled page keeps no source), 3 the Index-th tag of the last queried expression (else expression 0), 5 its engineering value, 6 the expression's context ('Move X', 'Visibility' ...), 7 the number of expressions, 8 the number of tags in expression Index, 9 the tag's cluster, 10 cluster.tag; other types or indexes \"\"."
        )]
        private static Value DspInfo(SimHost h, CicodeTask t, ArgList a)
        {
            var b = Block(h, A.I(a.Get(0)));
            if (b == null)
            {
                W.Raise(t, 269);
                return W.Empty;
            }
            long type = A.Short(A.I(a.Get(1)));
            int i = (int)A.Short(A.I(a.Get(2)));
            switch (type)
            {
                case 0:
                {
                    var o = b.PageNow?.Get(b.An);
                    return W.S(Title(o?.Drawing?.Type ?? o?.Type ?? 0));
                }
                case 1:
                case 2:
                    if (i >= 0 && i < b.Exprs.Count)
                        b.Last = i;
                    return W.Empty;
                case 6:
                    if (i < 0 || i >= b.Exprs.Count)
                        return W.Empty;
                    b.Last = i;
                    return W.S(b.Exprs[i].Context);
                case 7:
                    return W.S(W.Inv(b.Exprs.Count));
                case 8:
                    if (i < 0 || i >= b.Exprs.Count)
                        return W.Empty;
                    b.Last = i;
                    return W.S(W.Inv(b.Tags[i].Count));
                case 3:
                case 5:
                case 9:
                case 10:
                {
                    var tags =
                        b.Last >= 0 && b.Last < b.Tags.Count ? b.Tags[b.Last] : new List<(string, string)>();
                    if (i < 0 || i >= tags.Count)
                        return W.Empty;
                    var (tag, cl) = tags[i];
                    var def = h.World?.Tags.Find(tag, cl);
                    string cluster = cl.Length > 0 ? cl : def?.Cluster ?? "";
                    if (type == 3)
                        return W.S(tag);
                    if (type == 9)
                        return W.S(cluster);
                    if (type == 10)
                        return W.S(cluster.Length > 0 ? cluster + "." + tag : tag);
                    if (h.World == null)
                        return W.Empty;
                    return W.S(Conv.ValueText(h.World.Tags.Get(tag, cluster: cl), out _));
                }
                default:
                    return W.Empty;
            }
        }

        [Builtin(
            "DspInfoField",
            Doc = "1104697",
            Errors = "557 a tag of 255+ characters (raised, \"\")",
            Notes = "A field of the tag's definition: Cluster, Comment, Eng_Full, Eng_Zero, Eng_Units, Eng_Value (its current value as text), Field (its description), FullName, Name, Type, Unit (its I/O device); another field or an unknown tag: \"\". hInfo is not needed (0 allowed)."
        )]
        private static Value DspInfoField(SimHost h, CicodeTask t, ArgList a)
        {
            string tag = A.S(a.Get(1));
            if (tag.Length >= 255)
            {
                W.Raise(t, 557);
                return W.Empty;
            }
            int br = tag.IndexOf('[');
            if (br > 0)
                tag = tag.Substring(0, br);
            string cluster = A.S(a.Get(3));
            var db = h.World?.Tags;
            var def = db?.Find(tag, cluster);
            if (def == null)
                return W.Empty;
            string f = A.S(a.Get(2));
            bool Is(string n) => string.Equals(f, n, StringComparison.OrdinalIgnoreCase);
            string s;
            if (Is("Cluster"))
                s = def.Cluster;
            else if (Is("Comment") || Is("Field"))
                s = def.Comment;
            else if (Is("Eng_Full"))
                s = def.Info(5) ?? "";
            else if (Is("Eng_Zero"))
                s = def.Info(4) ?? "";
            else if (Is("Eng_Units"))
                s = def.EngUnits;
            else if (Is("Eng_Value"))
                s = Conv.ValueText(db!.Get(def.Name, cluster: cluster), out _);
            else if (Is("FullName"))
                s = def.Cluster.Length > 0 ? def.Cluster + "." + def.Name : def.Name;
            else if (Is("Name"))
                s = def.Name;
            else if (Is("Type"))
                s = def.TypeName.Length > 0 ? def.TypeName : "INT";
            else if (Is("Unit"))
                s = def.UnitText;
            else
                s = "";
            return W.S(s.Length > 255 ? s.Substring(0, 255) : s);
        }

        // ------------------------------------------------------------------ XP button cursor helpers
        /// <summary>The per-window cursor state of the Include XP button helpers (the
        /// pressed AN of windows 0..19; 750 object ids a window maps to ANs), zero at
        /// start like the runtime's static tables.</summary>
        private sealed class CursorTables
        {
            public readonly short[] Pressed = new short[20];
            public readonly short[,] Ids = new short[20, 750];
        }

        private static readonly ConditionalWeakTable<SimHost, CursorTables> Cursors =
            new ConditionalWeakTable<SimHost, CursorTables>();

        private static int CursorOf(SimHost h, CicodeTask t) =>
            DisplayWindows.Current(h, t)?.CommandCursor ?? -1;

        [Builtin(
            "_DspCursor_MouseDown",
            Notes = "Include's XP buttons. The AN = the command cursor (isCommand set) or the task's AN; with isCommand set it becomes the window's pressed AN. 1 when the AN is positive and is the pressed AN; 0 for a window number of 20 or more."
        )]
        private static Value DspCursorMouseDown(SimHost h, CicodeTask t, ArgList a)
        {
            var c = Cursors.GetValue(h, _ => new CursorTables());
            int win = D(h).WindowOf(t);
            if ((ushort)win >= 20)
                return W.I(0);
            bool cmd = A.Short(A.I(a.Get(0))) != 0;
            int an = cmd ? CursorOf(h, t) : TaskContext.An(t);
            if (an <= 0)
                return W.I(0);
            if (cmd)
                c.Pressed[win] = (short)an;
            return W.I(an == c.Pressed[win] ? 1 : 0);
        }

        [Builtin(
            "_DspCursor_MouseUp",
            Notes = "Include's XP buttons. The AN = the command cursor (isCommand set) or the task's AN; a non-negative nObjectId maps that id to the AN. With isCommand set and the AN pressed: the press ends, and unless noHalt the calling command halts when the pointer is not on the object ('Halted'). 1 when the cursor is on the AN and it is not pressed, else 0."
        )]
        private static Value DspCursorMouseUp(SimHost h, CicodeTask t, ArgList a)
        {
            var c = Cursors.GetValue(h, _ => new CursorTables());
            var d = D(h);
            int win = d.WindowOf(t);
            if ((ushort)win >= 20)
                return W.I(0);
            bool cmd = A.Short(A.I(a.Get(0))) != 0;
            int id = (int)A.Short(A.I(a.Get(1)));
            bool noHalt = A.Short(Arg(a, 2, 0)) != 0;
            var w = d.OpenWindow(win);
            int cursor = w == null ? -1 : w.CommandCursor;
            int an = cmd ? cursor : TaskContext.An(t);
            if (an <= 0)
                return W.I(0);
            if (id >= 0 && id < 750)
                c.Ids[win, id] = (short)an;
            if (cmd && c.Pressed[win] == an)
            {
                c.Pressed[win] = 0;
                if (!noHalt)
                {
                    bool inside = false;
                    if (w != null && w.MouseInside && w.Page?.Get(an) is AnObject o)
                        inside = o.Extent.ContainsInclusive(w.MouseX, w.MouseY);
                    if (w == null && an >= 32000)
                        W.Raise(t, 274);
                    if (!inside)
                    {
                        t.Vm.Halt(t, "_DspCursor_MouseUp: the button was released off the object");
                        return W.I(0);
                    }
                }
            }
            return W.I(cursor == an && c.Pressed[win] != cursor ? 1 : 0);
        }

        private static short IdAn(CursorTables c, int win, int id) => id < 750 ? c.Ids[win, id] : (short)-1;

        [Builtin(
            "_DspObjectGetCursorUp",
            Notes = "1 when the command cursor is on the object id's AN and that AN is not pressed (window 0..19, id 0..; an id of 750+ maps to -1)."
        )]
        private static Value DspObjectGetCursorUp(SimHost h, CicodeTask t, ArgList a)
        {
            var c = Cursors.GetValue(h, _ => new CursorTables());
            int win = D(h).WindowOf(t);
            int id = (int)A.Short(A.I(a.Get(0)));
            if ((ushort)win >= 20 || id < 0)
                return W.I(0);
            short an = IdAn(c, win, id);
            return W.I(CursorOf(h, t) == an && c.Pressed[win] != an ? 1 : 0);
        }

        [Builtin(
            "_DspObjectGetCursorDown",
            Notes = "1 when the object id's AN is the window's pressed AN (an id of 750+: when nothing is pressed... the pressed AN is -1)."
        )]
        private static Value DspObjectGetCursorDown(SimHost h, CicodeTask t, ArgList a)
        {
            var c = Cursors.GetValue(h, _ => new CursorTables());
            int win = D(h).WindowOf(t);
            int id = (int)A.Short(A.I(a.Get(0)));
            if ((ushort)win >= 20 || id < 0)
                return W.I(0);
            if (id >= 750)
                return W.I(c.Pressed[win] == -1 ? 1 : 0);
            return W.I(c.Ids[win, id] == c.Pressed[win] ? 1 : 0);
        }

        [Builtin(
            "_DspObjectGetCursor",
            Notes = "1 when the command cursor is on the object id's AN (without a window: when the id maps to -1)."
        )]
        private static Value DspObjectGetCursor(SimHost h, CicodeTask t, ArgList a)
        {
            var c = Cursors.GetValue(h, _ => new CursorTables());
            int win = D(h).WindowOf(t);
            int id = (int)A.Short(A.I(a.Get(0)));
            if ((ushort)win >= 20 || id < 0)
                return W.I(0);
            short an = IdAn(c, win, id);
            var w = DisplayWindows.Current(h, t);
            if (w == null)
                return W.I(an == -1 ? 1 : 0);
            return W.I(w.CommandCursor == an ? 1 : 0);
        }

        // ------------------------------------------------------------------ options without a real window
        [Builtin(
            "DspSetCurColor",
            Doc = "1104723",
            Notes = "Sets the process's current colour and returns the previous one (0 at start)."
        )]
        private static Value DspSetCurColor(SimHost h, CicodeTask t, ArgList a)
        {
            long prev = CurColour.GetValue(h, _ => new long[1])[0];
            CurColour.GetValue(h, _ => new long[1])[0] = A.I(a.Get(0));
            return W.I(prev);
        }

        private static readonly ConditionalWeakTable<SimHost, long[]> CurColour =
            new ConditionalWeakTable<SimHost, long[]>();

        [Builtin(
            "DspKernel",
            Doc = "1104701",
            Notes = "Opens the kernel window: the simulated user has no kernel access level, 513 (guess)."
        )]
        private static Value DspKernel(SimHost h, CicodeTask t, ArgList a) => W.I(513);

        [Builtin(
            "DspFullScreen",
            Doc = "1104669",
            Errors = "256 no window (returned)",
            Notes = "Full screen mode 0..2 of the task's window: nothing to resize, 0."
        )]
        private static Value DspFullScreen(SimHost h, CicodeTask t, ArgList a) =>
            W.I(DisplayWindows.Current(h, t) == null ? 256 : 0);

        [Builtin("DspVerbose", Effects = "logs the message", Notes = "Returns 0.")]
        private static Value DspVerbose(SimHost h, CicodeTask t, ArgList a)
        {
            h.Note(t, "DspVerbose: " + A.S(a.Get(0)));
            return W.I(0);
        }

        [Builtin(
            "DspDelayRenderBegin",
            Doc = "1104658",
            Notes = "Delays drawing until DspDelayRenderEnd: the simulator does not draw, 0."
        )]
        private static Value DspDelayRenderBegin(SimHost h, CicodeTask t, ArgList a) => W.I(0);

        [Builtin("DspDelayRenderEnd", Doc = "1104659", Notes = "0.")]
        private static Value DspDelayRenderEnd(SimHost h, CicodeTask t, ArgList a) => W.I(0);

        [Builtin(
            "DspDirty",
            Doc = "1104660",
            Errors = "274 no window or a bad AN (raised and returned); 269 the object not found (returned)",
            Notes = "Marks the AN for redrawing: 0."
        )]
        private static Value DspDirty(SimHost h, CicodeTask t, ArgList a)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            if (an == -2)
                an = TaskContext.An(t);
            var w = DisplayWindows.Current(h, t);
            if (w == null || (ushort)(an + 1) >= 0x7d01)
            {
                W.Raise(t, 274);
                return W.I(274);
            }
            if (w.Page?.Get(an) == null)
                return W.I(269);
            w.DrawnSerial++;
            return W.I(0);
        }

        [Builtin(
            "DspStatus",
            Doc = "1104730",
            Errors = "274 no window, an AN of 32000+ or the object not found (returned)",
            Notes = "Turns the AN's animation on / off in the runtime (the object's call): not modelled, 0."
        )]
        private static Value DspStatus(SimHost h, CicodeTask t, ArgList a)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            if (an == -2)
                an = TaskContext.An(t);
            var w = DisplayWindows.Current(h, t);
            if (w == null || (ushort)an >= 32000 || w.Page?.Get(an) == null)
                return W.I(274);
            return W.I(0);
        }

        [Builtin(
            "DspSetClip",
            Doc = "1104722",
            Errors = "274 an AN of -1 or outside 1..31999, an empty rectangle, or no window (returned)",
            Notes = "Clips the AN's drawing to the rectangle: recorded on the AN's drawn output when it has one (args clip). Returns 0."
        )]
        private static Value DspSetClip(SimHost h, CicodeTask t, ArgList a)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            long l = A.Short(A.I(a.Get(1))),
                tp = A.Short(A.I(a.Get(2))),
                r = A.Short(A.I(a.Get(3))),
                b = A.Short(A.I(a.Get(4)));
            var w = DisplayWindows.Current(h, t);
            if (an == -1 || l >= r || tp >= b || w == null || (ushort)(an - 1) >= 31999)
                return W.I(274);
            var c = w.DrawnAt(an);
            if (c != null)
            {
                c.Args.RemoveAll(x => x.Key == "clip");
                c.Arg("clip", W.Inv(l) + "," + W.Inv(tp) + "," + W.Inv(r) + "," + W.Inv(b));
                w.DrawnSerial++;
            }
            return W.I(0);
        }

        [Builtin(
            "DspClearClip",
            Doc = "1104655",
            Errors = "274 an AN of -1 or outside 1..31999, or no window (returned)"
        )]
        private static Value DspClearClip(SimHost h, CicodeTask t, ArgList a)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            var w = DisplayWindows.Current(h, t);
            if (an == -1 || w == null || (ushort)(an - 1) >= 31999)
                return W.I(274);
            var c = w.DrawnAt(an);
            if (c != null && c.Args.RemoveAll(x => x.Key == "clip") > 0)
                w.DrawnSerial++;
            return W.I(0);
        }

        [Builtin(
            "DspWebContentSetURL",
            Doc = "1104741",
            Effects = "the web content object's URL",
            Errors = "303 no window, a bad AN or not a web content object (raised and returned)",
            Notes = "AN -2 = the task's AN. The URL is kept on the object (DspWebContentGetURL); nothing is loaded."
        )]
        private static Value DspWebContentSetURL(SimHost h, CicodeTask t, ArgList a)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            if (an == -2)
                an = TaskContext.An(t);
            var w = DisplayWindows.Current(h, t);
            var o = w != null && (ushort)(an - 1) < 31999 ? w.Page?.Get(an) : null;
            if (w == null || o == null || (o.Drawing?.Type ?? o.Type) != 0x13)
            {
                W.Raise(t, 303);
                return W.I(303);
            }
            var c = new DrawCommand
            {
                An = an,
                Kind = "web",
                Text = A.S(a.Get(1)),
            };
            Draw(D(h), w, c);
            return W.I(0);
        }

        [Builtin(
            "DspWebContentGetURL",
            Doc = "1104740",
            Errors = "303 no window, a bad AN or not a web content object; 583 a URL of 256 characters or more (raised, \"\")",
            Notes = "The URL DspWebContentSetURL gave, \"\" when none. the runtime's eight early exits jump to before the raise; a URL that does not fit the 256-byte result takes."
        )]
        private static Value DspWebContentGetURL(SimHost h, CicodeTask t, ArgList a)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            if (an == -2)
                an = TaskContext.An(t);
            var w = DisplayWindows.Current(h, t);
            var o = w != null && DisplayAns.AnOk(an) ? w.Page?.Get(an) : null;
            if (w == null || o == null || (o.Drawing?.Type ?? o.Type) != 0x13)
            {
                W.Raise(t, 303);
                return W.Empty;
            }
            var c = w.DrawnAt(an);
            string url = c != null && c.Kind == "web" ? c.Text : "";
            if (url.Length >= 256)
            {
                W.Raise(t, 583);
                return W.Empty;
            }
            return W.S(url);
        }

        [Builtin(
            "DspGrayButton",
            Doc = "1104694",
            Effects = "greys a button (page instance state, recorded as the AN's 'gray' output)",
            Errors = "274 no window or an AN outside 1..31999 (raised and returned); 269 no page",
            Notes = "AN -2 = the task's AN. Mode 0 normal, 1 grey: returns 0."
        )]
        private static Value DspGrayButton(SimHost h, CicodeTask t, ArgList a)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            if (an == -2)
                an = TaskContext.An(t);
            var w = DisplayWindows.Current(h, t);
            if (w == null || !DisplayAns.AnOk(an))
            {
                W.Raise(t, 274);
                return W.I(274);
            }
            if (w.Page == null)
                return W.I(269);
            var o = w.Page.Get(an);
            if (o == null)
                return W.I(0);
            o.Gray = (int)(A.I(a.Get(1)) & 0xff);
            w.DrawnSerial++;
            return W.I(0);
        }

        [Builtin(
            "DspIsButtonGray",
            Doc = "1104700",
            Errors = "274 no window or an AN outside 1..31999 (raised, -1)",
            Notes = "AN -2 = the task's AN. The grey mode DspGrayButton gave (0 normal), 0 for an unknown AN."
        )]
        private static Value DspIsButtonGray(SimHost h, CicodeTask t, ArgList a)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            if (an == -2)
                an = TaskContext.An(t);
            var w = DisplayWindows.Current(h, t);
            if (w == null || (ushort)(an - 1) >= 31999)
            {
                W.Raise(t, 274);
                return W.I(-1);
            }
            return W.I(w.Page?.Get(an)?.Gray ?? 0);
        }

        /// <summary>The AN's slider on the task's window (ISliders), false when it has none.</summary>
        private static bool SliderAt(
            SimHost h,
            CicodeTask t,
            int an,
            out byte[] read,
            out byte[]? write,
            out CicodeSim.Compiled.Program? program
        )
        {
            read = Array.Empty<byte>();
            write = null;
            program = null;
            if (an == -2)
                an = TaskContext.An(t);
            var w = DisplayWindows.Current(h, t);
            return w != null
                && D(h).Controller is ISliders s
                && s.Slider(w, an, out read, out write, out program)
                && program != null;
        }

        [Builtin(
            "DspGetSlider",
            Doc = "1104692",
            Notes = "The position of the AN's slider animation (its horizontal, else vertical, else rotational slider: guess): the percent its tag reads as the slider shows it, 0..100 as 0..32000. -1 when the AN has no slider animation or is not on the page."
        )]
        private static Value DspGetSlider(SimHost h, CicodeTask t, ArgList a)
        {
            if (!SliderAt(h, t, (int)A.Short(A.I(a.Get(0))), out var read, out _, out var program))
                return W.I(-1);
            var v = RdbTables.RunBlock(h, t, read, program!, "DspGetSlider");
            double pct = v != null ? A.R(v.Value) : 0.0;
            if (double.IsNaN(pct))
                pct = 0.0;
            return W.I((long)Math.Round(Math.Max(0.0, Math.Min(100.0, pct)) * 320.0));
        }

        [Builtin(
            "DspSetSlider",
            Doc = "1104727",
            Effects = "writes the slider's tag",
            Errors = "274 the AN has no slider animation (returned) (guess)",
            Notes = "Moves the AN's slider animation (as DspGetSlider picks it) to nPos (0..32000, kept in that range: guess) as a drag released there does: its tag gets the percent nPos / 320 through the slider's write code, and the slider shows the tag from the next scan. A slider whose tag is not one tag does not write (0)."
        )]
        private static Value DspSetSlider(SimHost h, CicodeTask t, ArgList a)
        {
            if (!SliderAt(h, t, (int)A.Short(A.I(a.Get(0))), out _, out var write, out var program))
                return W.I(Codes.InvalidArg);
            if (write == null)
                return W.I(0);
            double pct = Math.Max(0, Math.Min(32000, A.I(a.Get(1)))) / 320.0;
            t.Vm.GlbStr[0] = Crt.Sprintf("%e", PrintfArg.Float(pct));
            RdbTables.RunBlock(h, t, write, program!, "DspSetSlider");
            return W.I(0);
        }

        [Builtin(
            "DspSetTooltipFont",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1104729",
            Notes = "The tool tip font (the renderer's): not recorded, 0."
        )]
        private static Value DspSetTooltipFont(SimHost h, CicodeTask t, ArgList a) => W.I(0);

        [Builtin(
            "DspSetPopupMenuFont",
            Doc = "1104726",
            Effects = "sets the typeface, size and style of subsequent popup menus"
        )]
        private static Value DspSetPopupMenuFont(SimHost h, CicodeTask t, ArgList a)
        {
            var display = D(h);
            var font = display.Font((int)A.I(a.Get(0)));
            if (font != null)
                display.PopupMenuFont = font;
            return W.I(0);
        }

        // ------------------------------------------------------------------ buttons, fixed-width text, lists
        private static Value Button(SimHost h, CicodeTask t, ArgList a, bool fn)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            if (an == -2)
                an = TaskContext.An(t);
            var w = DisplayWindows.Current(h, t);
            if (w == null || !DisplayAns.AnOk(an))
            {
                W.Raise(t, 274);
                return W.I(274);
            }
            if (w.Page == null)
                return W.I(269);
            var c = new DrawCommand
            {
                An = an,
                Kind = "button",
                Text = A.S(a.Get(2)),
                Font = (int)A.Short(Arg(a, 3, -1)),
            };
            c.Arg("width", A.Short(Arg(a, 4, 0))).Arg("height", A.Short(Arg(a, 5, 0)));
            if (fn)
                // the FUNCTION arguments arrive as the functions' names ('' for none: the label's 0)
                c.Arg("upFunction", FnName(a, 1))
                    .Arg("downFunction", FnName(a, 6))
                    .Arg("repeatFunction", FnName(a, 7));
            else
                c.Arg("upKey", A.I(a.Get(1))).Arg("downKey", Arg(a, 6, 0)).Arg("repeatKey", Arg(a, 7, 0));
            c.Arg("style", Arg(a, 8, 0));
            Draw(D(h), w, c);
            return W.I(0);
        }

        [Builtin(
            "_DspButton",
            Effects = "records a button on the AN (Include's DspButton label)",
            Errors = "274 no window or an AN outside 1..31999 (raised and returned); 269 no page (returned)",
            Notes = "AN -2 = the task's AN. The name, font, size, up / down / repeat keys and style recorded. A viewer's press (ctsim/input on the AN) puts the down key, its release the up key, into the window's key command line (the repeat key is not repeated)."
        )]
        private static Value DspButton(SimHost h, CicodeTask t, ArgList a) => Button(h, t, a, false);

        [Builtin(
            "_DspButtonFn",
            Effects = "records a button calling functions on the AN (Include's DspButtonFn label)",
            Errors = "274 no window or an AN outside 1..31999 (raised and returned); 269 no page (returned)",
            Notes = "AN -2 = the task's AN. The name, font, size and the up / down / repeat functions recorded (FUNCTION arguments: the names). A viewer's press (ctsim/input on the AN) runs the down function, its release the up function, each as a task in the window with the button's AN and the command cursor on it (the repeat function is not repeated)."
        )]
        private static Value DspButtonFn(SimHost h, CicodeTask t, ArgList a) => Button(h, t, a, true);

        [Builtin(
            "_DspFixedWidthText",
            Effects = "records a text output on the AN (as DspText)",
            Errors = "274 an AN below 1 (returned); DspText's errors",
            Notes = "A negative font: a font with foreground 0x142b41 on transparent is made first. Then DspText(AN, font, nWidth = 0 ? \"\" : sValue, nWidth, nAlignment, truncate 1, tooltip 1)."
        )]
        private static Value DspFixedWidthText(SimHost h, CicodeTask t, ArgList a)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            if (an <= 0)
                return W.I(274);
            long width = A.Short(A.I(a.Get(2)));
            int font = (int)A.Short(A.I(a.Get(3)));
            if (font < 0)
            {
                font = D(h).MakeFont("", 0, 0x142b41, 0xff000000, 0x142b41, 0xff000000);
                if (font < 0)
                    h.Note(t, "hardware alarm 274: _DspFixedWidthText: no free font");
            }
            string text = width != 0 ? A.S(a.Get(1)) : "";
            return W.I(Text(h, t, an, font, text, width, A.Short(Arg(a, 4, 0)), 1, true));
        }

        [Builtin(
            "__TabAlarm_GetIDByAN",
            Notes = "PageGetInt(\"_tabalm_AN<nListAN>_ID\", -1): the id a Tab style alarm list registered for its AN; -1 for an AN below 1."
        )]
        private static Value TabAlarmGetIdByAn(SimHost h, CicodeTask t, ArgList a)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            if (an < 1)
                return W.I(-1);
            var b = h.Registry?.Table.Lookup("PageGetInt");
            if (b == null)
                return W.I(0);
            return h.Registry!.Call(
                b.Index,
                t,
                ArgList.Of(Value.Str("_tabalm_AN" + W.Inv(an) + "_ID"), Value.Long(-1))
            );
        }

        [Builtin(
            "DspArrayByAn",
            Doc = "1069809",
            Effects = "records the AN's array (an alarm list) as its output",
            Notes = "AN -2 = the task's AN. The array of the AN (ArrayCreateByAn) drawn as a list at the AN: recorded with its handle (args array) and its cells as they are at the call (cells: the value layer, at most 500 rows x 64 columns, and an SA ArrayView array's settings, columns and row flags); the row layout of is the renderer's. Returns 0; an AN without an array or a window: 0."
        )]
        private static Value DspArrayByAn(SimHost h, CicodeTask t, ArgList a)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            if (an == -2)
                an = TaskContext.An(t);
            var w = DisplayWindows.Current(h, t);
            if (w?.Page == null || !DisplayAns.AnOk(an))
                return W.I(0);
            var b = h.Registry?.Table.Lookup("ArrayGetArrayByAn");
            if (b == null)
                return W.I(0);
            long hd = A.I(h.Registry!.Call(b.Index, t, ArgList.Of(Value.Long(an))));
            if (hd < 0)
                return W.I(0);
            var c = new DrawCommand { An = an, Kind = "array" };
            c.Arg("array", hd);
            var arr = ProcessData.Of(h).ArrayTable.Get(hd);
            if (arr != null)
            {
                // the cells as they are now (the runtime draws the array when this is called);
                // the same serial as the output shown keeps its cells (no new page state)
                var prev = w.Drawn.Find(x => x.An == an);
                c.CellsSerial = arr.Serial;
                c.Cells =
                    prev != null
                    && prev.Kind == "array"
                    && prev.CellsSerial == arr.Serial
                    && prev.Cells != null
                    && prev.Args.Count > 0
                    && prev.Args[0].Value.ToString() == W.Inv(hd)
                        ? prev.Cells
                        : Cells(arr);
            }
            Draw(D(h), w, c);
            return W.I(0);
        }

        /// <summary>The most rows and columns an array output carries.</summary>
        public const int MaxCellRows = 500,
            MaxCellCols = 64;

        /// <summary>An array's cells for a viewer.</summary>
        internal static JsonValue Cells(Arrays.CicodeArray arr)
        {
            int rows = Math.Min(arr.Y, MaxCellRows),
                cols = Math.Min(arr.X, MaxCellCols);
            int layer = arr.Z > 1 ? 1 : 0;
            string Item(int x, int y, int z) =>
                x < arr.X
                && y < arr.Y
                && z < arr.Z
                && arr.Items.TryGetValue((y * arr.X + x) * arr.Z + z, out string? v)
                    ? v
                    : "";
            long Num(int x, int y, int z) => DisplayBuiltins.Atoi(Item(x, y, z));
            var o = JsonValue.NewObject();
            o.Set("rows", JsonValue.Of(arr.Y));
            o.Set("cols", JsonValue.Of(arr.X));
            o.Set("layers", JsonValue.Of(arr.Z));
            var data = JsonValue.NewArray();
            for (int y = 0; y < rows; y++)
            {
                var r = JsonValue.NewArray();
                for (int x = 0; x < cols; x++)
                    r.Add(JsonValue.Of(Item(x, y, layer)));
                data.Add(r);
            }
            o.Set("data", data);
            bool sa = arr.Z >= 16;
            o.Set("firstRow", JsonValue.Of(sa ? Num(0, 0, 2) : 0));
            if (sa)
            {
                o.Set("xOff", JsonValue.Of(Num(0, 0, 1)));
                o.Set("rowHeight", JsonValue.Of(Num(0, 0, 6)));
                o.Set("drawHeader", JsonValue.Of(Num(0, 0, 7) != 0));
                string rf = Item(0, 0, 9),
                    hf = Item(0, 0, 10);
                if (rf.Length > 0)
                    o.Set("rowFont", JsonValue.Of(DisplayBuiltins.Atoi(rf)));
                if (hf.Length > 0)
                    o.Set("headerFont", JsonValue.Of(DisplayBuiltins.Atoi(hf)));
                o.Set("hidden", JsonValue.Of(Num(0, 0, 15) != 0));
                var cj = JsonValue.NewArray();
                for (int x = 1; x < cols; x++)
                {
                    var col = JsonValue.NewObject();
                    col.Set("col", JsonValue.Of(x));
                    col.Set("field", Item(x, 0, 1));
                    col.Set("type", JsonValue.Of(Num(x, 0, 2)));
                    col.Set("width", JsonValue.Of(Num(x, 0, 3)));
                    col.Set("title", Item(x, 0, 4));
                    if (Num(x, 0, 2) == 3)
                    {
                        // a symbol column (the SA treeview's PriorityAndState): the cells' z 2
                        // = the symbol's state (Treeview.ci _Treeview_UpdatePriorityInfo: the
                        // AlarmHighestPriority state + 5, sa_priorities COL_ARR_5..9)
                        var meta = JsonValue.NewArray();
                        for (int y = 1; y < rows; y++)
                            meta.Add(JsonValue.Of(Item(x, y, 2)));
                        col.Set("meta", meta);
                    }
                    cj.Add(col);
                }
                o.Set("columns", cj);
                var ri = JsonValue.NewArray();
                for (int y = 1; y < rows; y++)
                {
                    var row = JsonValue.NewObject();
                    row.Set("row", JsonValue.Of(y));
                    string f = Item(0, y, 1);
                    if (f.Length > 0)
                        row.Set("font", JsonValue.Of(DisplayBuiltins.Atoi(f)));
                    row.Set("selected", JsonValue.Of(Num(0, y, 4) != 0));
                    ri.Add(row);
                }
                o.Set("rowInfo", ri);
            }
            o.Set("serial", JsonValue.Of(arr.Serial));
            return o;
        }

        // ------------------------------------------------------------------ the remaining entries
        [Builtin("WndPutProfile", Notes = "The FUNC0 entry is: nothing happens, 0.")]
        [Builtin("WndHelp", Notes = "The FUNC0 entry is: nothing happens, 0.")]
        [Builtin("WndViewer", Notes = "The FUNC0 entry is: nothing happens, 0.")]
        [Builtin("DspBarLoad", Notes = "The FUNC0 entry is: nothing happens, 0.")]
        [Builtin(
            "DspSymLoad",
            Notes = "The FUNC0 entry is (also WndPutProfile, WndGetProfile, WndHelp, WndViewer, DspBarLoad, DspCol): nothing happens, 0."
        )]
        private static Value ReturnZero(SimHost h, CicodeTask t, ArgList a) => W.I(0);

        [Builtin("WndGetProfile", Notes = "The FUNC0 entry is: a null string, \"\".")]
        private static Value WndGetProfile(SimHost h, CicodeTask t, ArgList a) => W.Empty;

        [Builtin("DspAnWrite", Notes = "The entry is: 256 always.")]
        private static Value DspAnWrite(SimHost h, CicodeTask t, ArgList a) => W.I(256);

        [Builtin(
            "DspRubSetColor",
            Notes = "Sets the process's rubber band colour and returns the previous one (0 at start)."
        )]
        private static Value DspRubSetColor(SimHost h, CicodeTask t, ArgList a)
        {
            var c = RubColour.GetValue(h, _ => new long[1]);
            long prev = c[0];
            c[0] = A.I(a.Get(0));
            return W.I(prev);
        }

        private static readonly ConditionalWeakTable<SimHost, long[]> RubColour =
            new ConditionalWeakTable<SimHost, long[]>();

        [Builtin(
            "DspPage",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Errors = "256 no window (returned)",
            Notes = "Undocumented: reopens the window's drawing file (261 when it cannot): the engine reads no drawing file, 0."
        )]
        private static Value DspPage(SimHost h, CicodeTask t, ArgList a) =>
            W.I(DisplayWindows.Current(h, t) == null ? 256 : 0);

        [Builtin(
            "DspTrnLoad",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Notes = "Adds the named trend to the page's V3/V4 trend window and returns its 1-based pen number (an empty name, \"0\" or \"local\" looks the pen up instead); the simulator has no V3/V4 trend window: 0."
        )]
        private static Value DspTrnLoad(SimHost h, CicodeTask t, ArgList a) => W.I(0);

        [Builtin(
            "DspFlushObj",
            Errors = "274 a mode other than 0 (raised and returned); mode 0 raises 1 (no halt)",
            Notes = "Mode 0 flushes the cached drawing objects (nothing cached here) and returns 1. Both paths end in the shared raise with the result (and a jump past:; then): mode 0 sets the task's error to 1 too."
        )]
        private static Value DspFlushObj(SimHost h, CicodeTask t, ArgList a)
        {
            int r = A.Short(A.I(a.Get(0))) == 0 ? 1 : 274;
            W.Raise(t, r);
            return W.I(r);
        }

        [Builtin(
            "KeyOEM",
            Status = BuiltinStatus.Stub,
            Returns = "-1",
            Notes = "An OEM keyboard's key ('OEMUSR', index below 10): no OEM keyboard is attached, -1."
        )]
        private static Value KeyOEM(SimHost h, CicodeTask t, ArgList a) => W.I(-1);

        [Builtin(
            "_DspExec",
            Errors = "274 always (raised and returned)",
            Notes = "the runtime's body raises 274 and returns it."
        )]
        private static Value DspExec(SimHost h, CicodeTask t, ArgList a)
        {
            W.Raise(t, 274);
            return W.I(274);
        }
    }
}
