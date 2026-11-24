using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World.Display;

namespace CicodeSim.Builtins
{
    /// <summary>
    /// The plot system (Plot*): a plot records drawing primitives in its own coordinates. PlotClose
    /// shows a display plot at its AN (a drawn 'plot' entry, engine/PROTOCOL.md) or writes a
    /// printer plot to the sandbox as an SVG file (Printers\(port)-(n).svg).
    /// </summary>
    public static class Plots
    {
        /// <summary>The open plots a process may have (guess).</summary>
        private const int MaxPlots = 256;

        /// <summary>The primitives one plot keeps; later ones are dropped (guess).</summary>
        private const int MaxPrims = 100000;

        /// <summary>The basic animation point's record type: the only AN a display plot takes.</summary>
        private const int BasicAn = 0x1000;

        /// <summary>The simulated printer: A4 portrait at 300 dpi (guess).</summary>
        private const int PrinterDpi = 300,
            PrinterWidth = 2480,
            PrinterHeight = 3508;

        private const int ScreenDpi = 96;

        /// <summary>PlotLine / PlotXYLine mode bit: the values are trend samples, an invalid or
        /// gated one breaks the line (graph.ci's MODE_TREND_DATA; guess).</summary>
        private const int TrendData = 32;

        internal sealed class Frame
        {
            public int L,
                T,
                R,
                B,
                Samples;
        }

        internal sealed class Plot
        {
            public bool Display;
            public string Output = "";
            public int Mode;
            public int Window = -1,
                An;
            public PageInstance? Page;
            public readonly JsonValue Prims = JsonValue.NewArray();
            public Frame? Grid;

            /// <summary>The samples of the last line drawn: (sample index, x, y) of each valid one.</summary>
            public List<(int I, int X, int Y)>? Line;

            /// <summary>What the primitives cover (plot coordinates), null when nothing yet.</summary>
            public Rect? Bounds;

            public int Width,
                Height,
                Dpi;
        }

        private sealed class State
        {
            public readonly Dictionary<long, Plot> Open = new Dictionary<long, Plot>();
            public readonly Dictionary<int, string> Markers = new Dictionary<int, string>();
            public readonly Dictionary<string, int> Printed = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase
            );

            /// <summary>The V3 graph's plot handle (-1 none) and the samples GraphOpen gave.</summary>
            public long Graph = -1;
            public int GraphSamples;
        }

        private static readonly ConditionalWeakTable<SimHost, State> States =
            new ConditionalWeakTable<SimHost, State>();

        private static State Of(SimHost h) => States.GetValue(h, _ => new State());

        private static Value Fail(CicodeTask t, Value result, int code)
        {
            W.Raise(t, code);
            return result;
        }

        private static Plot? Get(SimHost h, in Value handle) =>
            Of(h).Open.TryGetValue(A.I(handle), out var p) ? p : null;

        private static int I(ArgList a, int k) => (int)A.I(a.Get(k));

        /// <summary>A Plot built-in's result: 0, or the error raised and returned.</summary>
        private static Value Done(CicodeTask t, int err) => err == 0 ? W.I(0) : Fail(t, W.I(err), err);

        /// <summary>A Cicode colour as the protocol gives it: 0xRRGGBB, -1 transparent.</summary>
        internal static long Colour(long c) => (c & 0xFF000000L) != 0 ? -1 : c & 0xFFFFFF;

        /// <summary>Element <paramref name="i"/> of the REAL table an argument starts (a module /
        /// global array: the following elements; a local: element 0, the rest 0).</summary>
        private static double Element(in Value table, int i)
        {
            if (table.O is CodeVarRef cr)
                return A.R(cr.Slot.Get(cr.Element + i, out _));
            return i == 0 ? A.R(table) : 0.0;
        }

        // ------------------------------------------------------------------ primitives
        private static JsonValue Pen(int style, long colour, int width)
        {
            var o = JsonValue.NewObject();
            o.Set("style", JsonValue.Of(style < 0 || style > 5 ? 0 : style));
            o.Set("colour", JsonValue.Of(Colour(colour)));
            o.Set("width", JsonValue.Of(Math.Max(1, Math.Min(32, width))));
            return o;
        }

        private static void Cover(Plot p, int l, int t, int r, int b)
        {
            if (l > r)
                (l, r) = (r, l);
            if (t > b)
                (t, b) = (b, t);
            var c = p.Bounds;
            p.Bounds =
                c == null
                    ? new Rect(l, t, r, b)
                    : new Rect(
                        Math.Min(c.Value.L, l),
                        Math.Min(c.Value.T, t),
                        Math.Max(c.Value.R, r),
                        Math.Max(c.Value.B, b)
                    );
        }

        private static bool Add(Plot p, JsonValue prim)
        {
            if (p.Prims.Count >= MaxPrims)
                return false;
            p.Prims.Add(prim);
            return true;
        }

        private static void Shape(Plot p, string op, int x1, int y1, int x2, int y2, JsonValue pen, long fill)
        {
            var o = JsonValue.NewObject();
            o.Set("op", op);
            o.Set("x1", JsonValue.Of(x1));
            o.Set("y1", JsonValue.Of(y1));
            o.Set("x2", JsonValue.Of(x2));
            o.Set("y2", JsonValue.Of(y2));
            o.Set("pen", pen);
            if (op == "rect" || op == "ellipse")
                o.Set("fill", JsonValue.Of(Colour(fill)));
            if (Add(p, o))
                Cover(p, x1, y1, x2, y2);
        }

        private static void Polyline(Plot p, List<(int I, int X, int Y)> run, JsonValue pen)
        {
            if (run.Count < 2)
                return;
            var pts = JsonValue.NewArray();
            foreach (var q in run)
            {
                pts.Add(JsonValue.Of(q.X));
                pts.Add(JsonValue.Of(q.Y));
                Cover(p, q.X, q.Y, q.X, q.Y);
            }
            var o = JsonValue.NewObject();
            o.Set("op", "polyline");
            o.Set("points", pts);
            o.Set("pen", pen);
            Add(p, o);
        }

        /// <summary>A marker's size in pixels: 8 on the screen, the same at the printer's dpi (guess).</summary>
        private static int MarkerSize(Plot p) => (int)Math.Round(8.0 * p.Dpi / ScreenDpi);

        private static void Marker(SimHost h, Plot p, int style, long colour, int x, int y)
        {
            int s = MarkerSize(p);
            var o = JsonValue.NewObject();
            o.Set("op", "marker");
            o.Set("x", JsonValue.Of(x));
            o.Set("y", JsonValue.Of(y));
            o.Set("marker", JsonValue.Of(style));
            o.Set("colour", JsonValue.Of(Colour(colour)));
            o.Set("size", JsonValue.Of(s));
            if (style >= 20 && Of(h).Markers.TryGetValue(style, out string? sym))
                o.Set("symbol", sym);
            if (Add(p, o))
                Cover(p, x - s / 2, y - s / 2, x + s / 2, y + s / 2);
        }

        /// <summary>A marker style PlotMarker / PlotLine take: 0 none, 1..8 built in, 20..32000 a
        /// registered symbol.</summary>
        private static bool MarkerOk(SimHost h, int style) =>
            style >= 0 && style <= 8 || style >= 20 && style <= 32000 && Of(h).Markers.ContainsKey(style);

        // ------------------------------------------------------------------ open, close
        /// <summary>A valid PlotOpen mode: 1 (normal) with at most one of the animation bits 16,
        /// 32, 64, 128, 256; or 2 (the old graph system, printers only).</summary>
        private static bool ModeOk(int mode, bool display)
        {
            if (mode == 2)
                return !display;
            if ((mode & 1) == 0 || (mode & ~0x1F1) != 0)
                return false;
            int anim = mode & 0x1F0;
            return (anim & (anim - 1)) == 0;
        }

        [Builtin(
            "PlotOpen",
            Doc = "1188851",
            Effects = "opens a plot (the process's plot table)",
            Errors = "274 an invalid mode, an empty output, no window or page, an AN that is not on the page or is a graphic object (not a basic animation point), 256 plots open (raised, -1)",
            Notes = "Returns the plot handle (the lowest free, from 0: guess). sOutput 'Display' (without case) plots on the task's window at the AN, coordinates relative to the AN: PlotClose shows it there (a drawn 'plot' entry), replacing what was drawn on the AN. Another output is a printer: the AN is ignored, coordinates are page pixels of a simulated A4 portrait page at 300 dpi (2480 x 3508; guess), and PlotClose writes the plot to the sandbox as Printers\\(port)-(n).svg. Modes: 1 normal, + 16 soft, 32 hard, 64 persistent, 128 opaque, 256 overlapped animation (one of them); 2 the old graph system (printers only). The viewer draws the whole page again, so every display mode shows the plot over the page (guess)."
        )]
        private static Value PlotOpen(SimHost h, CicodeTask t, ArgList a)
        {
            int err = Open(h, t, I(a, 0), A.S(a.Get(1)).Trim(), I(a, 2), false, out long hnd);
            return err == 0 ? W.I(hnd) : Fail(t, W.I(-1), err);
        }

        /// <summary>Open a plot: 0 and its handle, or the error. <paramref name="port"/>: the
        /// output is a port whatever its name (a V3 graph).</summary>
        private static int Open(
            SimHost h,
            CicodeTask t,
            int an,
            string output,
            int mode,
            bool port,
            out long hnd
        )
        {
            hnd = -1;
            bool display = !port && string.Equals(output, "Display", StringComparison.OrdinalIgnoreCase);
            if (!port && output.Length == 0 || !ModeOk(mode, display))
                return Codes.InvalidArg;
            var st = Of(h);
            if (st.Open.Count >= MaxPlots)
                return Codes.InvalidArg;
            var p = new Plot
            {
                Display = display,
                Output = output,
                Mode = mode,
                Width = PrinterWidth,
                Height = PrinterHeight,
                Dpi = PrinterDpi,
            };
            if (display)
            {
                var w = DisplayWindows.Current(h, t);
                var o = w?.Page?.Get(an);
                if (w == null || o == null || o.Type != BasicAn)
                    return Codes.InvalidArg;
                p.Window = w.Number;
                p.An = an;
                p.Page = w.Page;
                p.Width = w.Page!.Width > 0 ? w.Page.Width : 640;
                p.Height = w.Page.Height > 0 ? w.Page.Height : 480;
                p.Dpi = ScreenDpi;
            }
            hnd = 0;
            while (st.Open.ContainsKey(hnd))
                hnd++;
            st.Open[hnd] = p;
            return 0;
        }

        [Builtin(
            "PlotClose",
            Doc = "1188844",
            Effects = "shows the plot at its AN or writes it to the sandbox, and closes it",
            Errors = "269 no such plot (raised, returned)",
            Notes = "A display plot is drawn on its AN when its window still shows the page it was opened on (else it is dropped: guess): a drawn 'plot' entry with the primitives, and the AN's extent becomes what they cover. A printer plot is written to the sandbox as an SVG file Printers\\(port)-(n).svg: the port with \\ / : * ? \" < > | as '_', n counting that port's printouts (plots and rich text) from 1; a white page, texts localised in their font's foreground (a text's background is not drawn)."
        )]
        private static Value PlotClose(SimHost h, CicodeTask t, ArgList a)
        {
            var st = Of(h);
            long hnd = A.I(a.Get(0));
            if (!st.Open.TryGetValue(hnd, out var p))
                return Fail(t, W.I(Codes.BadHandle), Codes.BadHandle);
            Close(h, t, hnd);
            return W.I(0);
        }

        /// <summary>Close an open plot: shown at its AN or printed.</summary>
        private static void Close(SimHost h, CicodeTask t, long hnd)
        {
            var st = Of(h);
            var p = st.Open[hnd];
            st.Open.Remove(hnd);
            if (st.Graph == hnd)
                st.Graph = -1;
            if (p.Display)
                Show(h, p);
            else
                Print(h, t, p);
        }

        private static void Show(SimHost h, Plot p)
        {
            var d = DisplayBuiltins.Of(h);
            var w = d.Get(p.Window);
            var o = w?.Page != null && ReferenceEquals(w.Page, p.Page) ? w.Page.Get(p.An) : null;
            if (w == null || o == null)
                return;
            var c = new DrawCommand { An = p.An, Kind = "plot" };
            c.Arg("mode", p.Mode).Arg("output", p.Output);
            c.Args.Add(new KeyValuePair<string, JsonValue>("prims", p.Prims));
            (c.X, c.Y) = DisplayAns.CurrentPos(d, w, o);
            d.Draw(w, c);
            if (p.Bounds is Rect b)
                CicodeExtents.Set(o, new Rect(o.PosX + b.L, o.PosY + b.T, o.PosX + b.R, o.PosY + b.B));
        }

        /// <summary>A port's name as a file name: \ / : * ? " &lt; &gt; | and control characters
        /// as '_', the ends trimmed of '_', '.' and spaces ('LPT1:' = 'LPT1').</summary>
        internal static string PortFile(string port)
        {
            var b = new StringBuilder();
            foreach (char ch in port)
                b.Append(ch < ' ' || "\\/:*?\"<>|".IndexOf(ch) >= 0 ? '_' : ch);
            string s = b.ToString().Trim('_', '.', ' ');
            return s.Length > 0 ? s : "Printer";
        }

        /// <summary>The sandbox file of a port's next printout: Printers\(port)-(n).(ext), n
        /// counting the port's printouts (plots, rich text and window pictures) from 1.</summary>
        internal static string NextPrintout(SimHost h, string port, string ext)
        {
            var st = Of(h);
            string p = PortFile(port);
            st.Printed.TryGetValue(p, out int n);
            st.Printed[p] = ++n;
            return h.Sandbox.Full(
                "Printers\\" + p + "-" + n.ToString(CultureInfo.InvariantCulture) + "." + ext
            );
        }

        private static void Print(SimHost h, CicodeTask t, Plot p)
        {
            string full = NextPrintout(h, p.Output, "svg");
            byte[] data = Encoding.UTF8.GetBytes(PlotSvg.Write(DisplayBuiltins.Of(h), p));
            if (h.Sandbox.WriteOutput(full, data, t.Vm.Clock.NowFiletime()))
                h.Note(t, "PlotClose: the plot for " + p.Output + " is in " + full);
            else
                h.Note(t, "PlotClose: the plot for " + p.Output + " could not be written to " + full);
        }

        // ------------------------------------------------------------------ drawing
        [Builtin(
            "PlotDraw",
            Doc = "1188845",
            Effects = "adds a rectangle, ellipse, line or point to the plot",
            Errors = "269 no such plot (raised, returned); 274 a type outside 1..4 (raised, returned)",
            Notes = "Type 1 rectangle, 2 ellipse in the box, 3 line, 4 point (X2, Y2 ignored); nFill fills a rectangle or ellipse (TRANSPARENT none). Pen style 0 solid, 1 dash, 2 dot, 3 dash dot, 4 dash dot dot, 5 hollow (another style: solid, guess); the width is kept within 1..32 and a pen wider than 1 draws solid."
        )]
        private static Value PlotDraw(SimHost h, CicodeTask t, ArgList a)
        {
            var p = Get(h, a.Get(0));
            if (p == null)
                return Fail(t, W.I(Codes.BadHandle), Codes.BadHandle);
            return Done(
                t,
                Draw(
                    p,
                    I(a, 1),
                    I(a, 2),
                    A.I(a.Get(3)),
                    I(a, 4),
                    A.I(a.Get(5)),
                    I(a, 6),
                    I(a, 7),
                    I(a, 8),
                    I(a, 9)
                )
            );
        }

        private static int Draw(
            Plot p,
            int type,
            int style,
            long colour,
            int width,
            long fill,
            int x1,
            int y1,
            int x2,
            int y2
        )
        {
            var pen = Pen(style, colour, width);
            switch (type)
            {
                case 1:
                    Shape(p, "rect", x1, y1, x2, y2, pen, fill);
                    break;
                case 2:
                    Shape(p, "ellipse", x1, y1, x2, y2, pen, fill);
                    break;
                case 3:
                    Shape(p, "line", x1, y1, x2, y2, pen, fill);
                    break;
                case 4:
                {
                    var o = JsonValue.NewObject();
                    o.Set("op", "point");
                    o.Set("x", JsonValue.Of(x1));
                    o.Set("y", JsonValue.Of(y1));
                    o.Set("pen", pen);
                    if (Add(p, o))
                        Cover(p, x1, y1, x1, y1);
                    break;
                }
                default:
                    return Codes.InvalidArg;
            }
            return 0;
        }

        [Builtin(
            "PlotGrid",
            Doc = "1188847",
            Effects = "sets the plot's frame and adds its fill, grid lines and frame",
            Errors = "269 no such plot (raised, returned); 274 nSamples outside 2..16000 (raised, returned)",
            Notes = "The frame (X1, Y1) - (X2, Y2) is the one PlotLine, PlotXYLine and PlotScaleMarker use (the last PlotGrid's). Drawn as the fill (nFill, TRANSPARENT none), nHorGrid rows and nVerGrid columns of 1-pixel solid grid lines (the lines between them), then the frame in FrameWidth (0: not drawn) and FrameCol."
        )]
        private static Value PlotGrid(SimHost h, CicodeTask t, ArgList a)
        {
            var p = Get(h, a.Get(0));
            if (p == null)
                return Fail(t, W.I(Codes.BadHandle), Codes.BadHandle);
            return Done(
                t,
                Grid(
                    p,
                    I(a, 1),
                    I(a, 2),
                    I(a, 3),
                    I(a, 4),
                    I(a, 5),
                    I(a, 6),
                    A.I(a.Get(7)),
                    I(a, 8),
                    A.I(a.Get(9)),
                    I(a, 10),
                    A.I(a.Get(11)),
                    A.I(a.Get(12))
                )
            );
        }

        private static int Grid(
            Plot p,
            int samples,
            int x1,
            int y1,
            int x2,
            int y2,
            int rows,
            long rowCol,
            int cols,
            long colCol,
            int frameWidth,
            long frameCol,
            long fill
        )
        {
            if (samples < 2 || samples > 16000)
                return Codes.InvalidArg;
            var f = new Frame
            {
                L = Math.Min(x1, x2),
                T = Math.Min(y1, y2),
                R = Math.Max(x1, x2),
                B = Math.Max(y1, y2),
                Samples = samples,
            };
            p.Grid = f;
            if (Colour(fill) >= 0)
                Shape(p, "rect", f.L, f.T, f.R, f.B, Pen(5, 0, 1), fill);
            for (int k = 1; k < rows; k++)
            {
                int y = f.T + (int)Math.Round((f.B - f.T) * (double)k / rows);
                Shape(p, "line", f.L, y, f.R, y, Pen(0, rowCol, 1), 0);
            }
            for (int k = 1; k < cols; k++)
            {
                int x = f.L + (int)Math.Round((f.R - f.L) * (double)k / cols);
                Shape(p, "line", x, f.T, x, f.B, Pen(0, colCol, 1), 0);
            }
            if (frameWidth > 0)
                Shape(p, "rect", f.L, f.T, f.R, f.B, Pen(0, frameCol, frameWidth), -1);
            return 0;
        }

        /// <summary>A sample's place in the frame: x from its fraction across, y from its fraction
        /// up (mode 1 bottom left, 2 bottom right, 4 top left, 8 top right; another mode as 1).</summary>
        private static (int, int) InFrame(Frame f, int mode, double fx, double fy)
        {
            fx = Math.Max(0.0, Math.Min(1.0, fx));
            fy = Math.Max(0.0, Math.Min(1.0, fy));
            bool right = (mode & 0xF) == 2 || (mode & 0xF) == 8,
                top = (mode & 0xF) == 4 || (mode & 0xF) == 8;
            double x = right ? f.R - fx * (f.R - f.L) : f.L + fx * (f.R - f.L);
            double y = top ? f.T + fy * (f.B - f.T) : f.B - fy * (f.B - f.T);
            return ((int)Math.Round(x), (int)Math.Round(y));
        }

        private static double Fraction(double v, double lo, double hi) =>
            hi == lo ? 0.0 : (v - lo) / (hi - lo);

        /// <summary>PlotLine / PlotXYLine: the line through the samples (broken at invalid trend
        /// samples with mode bit 32), its markers, and the samples kept for PlotMarker.</summary>
        private static Value Line(SimHost h, CicodeTask t, ArgList a, bool xy)
        {
            var p = Get(h, a.Get(0));
            if (p == null)
                return Fail(t, W.I(Codes.BadHandle), Codes.BadHandle);
            return Done(
                t,
                Line(
                    h,
                    p,
                    I(a, 1),
                    A.I(a.Get(2)),
                    I(a, 3),
                    I(a, 4),
                    A.I(a.Get(5)),
                    I(a, 6),
                    I(a, 7),
                    a.Get(8),
                    A.R(a.Get(9)),
                    A.R(a.Get(10)),
                    xy,
                    xy ? a.Get(11) : default,
                    xy ? A.R(a.Get(12)) : 0,
                    xy ? A.R(a.Get(13)) : 0,
                    I(a, xy ? 14 : 11)
                )
            );
        }

        private static int Line(
            SimHost h,
            Plot p,
            int style,
            long colour,
            int width,
            int markerStyle,
            long markerCol,
            int every,
            int length,
            Value table,
            double lo,
            double hi,
            bool xy,
            Value yTable,
            double ylo,
            double yhi,
            int mode
        )
        {
            var f = p.Grid;
            if (f == null || length < 0 || !MarkerOk(h, markerStyle))
                return Codes.InvalidArg;
            int n = Math.Min(length, f.Samples);
            var pen = Pen(style, colour, width);
            var line = new List<(int, int, int)>();
            var run = new List<(int, int, int)>();
            for (int i = 0; i < n; i++)
            {
                double v = Element(table, i);
                double x = xy ? v : 0;
                double y = xy ? Element(yTable, i) : v;
                bool bad =
                    (mode & TrendData) != 0 && (Trends.Validity(x) != 0 || Trends.Validity(y) != 0)
                    || double.IsNaN(x)
                    || double.IsNaN(y)
                    || double.IsInfinity(x)
                    || double.IsInfinity(y);
                if (bad)
                {
                    if (style != 5)
                        Polyline(p, run, pen);
                    run.Clear();
                    continue;
                }
                double fx = xy ? Fraction(x, lo, hi) : (double)i / (f.Samples - 1);
                double fy = xy ? Fraction(y, ylo, yhi) : Fraction(y, lo, hi);
                var (px, py) = InFrame(f, mode, fx, fy);
                run.Add((i, px, py));
                line.Add((i, px, py));
            }
            if (style != 5)
                Polyline(p, run, pen);
            p.Line = line;
            if (markerStyle != 0)
                Markers(h, p, line, markerStyle, markerCol, every, n);
            return 0;
        }

        /// <summary>Markers on the samples below <paramref name="count"/>: every
        /// <paramref name="every"/>th one from the first (0 or less: every sample; guess).</summary>
        private static void Markers(
            SimHost h,
            Plot p,
            List<(int I, int X, int Y)> line,
            int style,
            long colour,
            int every,
            int count
        )
        {
            int step = Math.Max(1, every);
            foreach (var q in line)
                if (q.I < count && q.I % step == 0)
                    Marker(h, p, style, colour, q.X, q.Y);
        }

        private const string LineNotes =
            "MarkerStyle 0 none, 1 triangle, 2 square, 3 circle, 4 diamond, 5..8 the filled ones, 20..32000 a symbol PlotSetMarker registered (another: 274); a marker on every nMarker-th sample from the first (0: every sample, guess), 8 pixels on the screen (25 at the printer's 300 dpi, guess). Mode: the origin 1 bottom left, 2 bottom right, 4 top left, 8 top right (another: 1, guess); + 32 the values are trend samples, an invalid or gated one breaks the line (graph.ci's MODE_TREND_DATA; guess). A value outside the scale is drawn at the frame's edge (guess). The samples are kept for PlotMarker's X = Y = -1.";

        [Builtin(
            "PlotLine",
            Doc = "1188849",
            Effects = "adds a line through the table's values to the plot",
            Errors = "269 no such plot (raised, returned); 274 no PlotGrid frame, a negative Length or an unknown marker style (raised, returned)",
            Notes = "Sample i of the first min(Length, nSamples) elements of pTable at i / (nSamples - 1) across the PlotGrid frame, its value scaled from LoScale (the origin's edge) to HiScale. "
                + LineNotes
        )]
        private static Value PlotLine(SimHost h, CicodeTask t, ArgList a) => Line(h, t, a, false);

        [Builtin(
            "PlotXYLine",
            Doc = "1188855",
            Effects = "adds a line through the (x, y) values to the plot",
            Errors = "269 no such plot (raised, returned); 274 no PlotGrid frame, a negative Length or an unknown marker style (raised, returned)",
            Notes = "The first min(Length, nSamples) pairs of xTable and yTable, in their order, x scaled from LoXScale to HiXScale across the PlotGrid frame and y from LoYScale to HiYScale. "
                + LineNotes
        )]
        private static Value PlotXYLine(SimHost h, CicodeTask t, ArgList a) => Line(h, t, a, true);

        [Builtin(
            "PlotMarker",
            Doc = "1188850",
            Effects = "adds markers to the plot",
            Errors = "269 no such plot (raised, returned); 274 an unknown marker style (raised, returned)",
            Notes = "X = Y = -1: on the samples of the last PlotLine / PlotXYLine below Length, every nMarker-th from the first (0: every one, guess); else one marker at (X, Y) (Length ignored, guess). Style 0 draws nothing; 20..32000 a symbol PlotSetMarker registered (the entry names it)."
        )]
        private static Value PlotMarker(SimHost h, CicodeTask t, ArgList a)
        {
            var p = Get(h, a.Get(0));
            if (p == null)
                return Fail(t, W.I(Codes.BadHandle), Codes.BadHandle);
            return Done(t, MarkerAt(h, p, I(a, 1), A.I(a.Get(2)), I(a, 3), I(a, 4), I(a, 5), I(a, 6)));
        }

        private static int MarkerAt(
            SimHost h,
            Plot p,
            int style,
            long colour,
            int every,
            int length,
            int x,
            int y
        )
        {
            if (!MarkerOk(h, style))
                return Codes.InvalidArg;
            if (style == 0)
                return 0;
            if (x == -1 && y == -1)
            {
                if (p.Line != null)
                    Markers(h, p, p.Line, style, colour, every, length);
            }
            else
                Marker(h, p, style, colour, x, y);
            return 0;
        }

        [Builtin(
            "PlotScaleMarker",
            Doc = "1188852",
            Effects = "adds a scale line with tick marks to the plot",
            Errors = "269 no such plot (raised, returned); 274 no PlotGrid frame (raised, returned)",
            Notes = "A vertical line from (X, Y) up by the frame's height (graph.ci passes the frame's bottom), in PenWidth and PenCol; nMarker tick marks spread evenly over it from end to end (1: at Y), each a marker's size long (8 pixels on the screen, 25 at the printer's dpi; guess): Mode 0 both sides, 1 left, 2 right (another: both)."
        )]
        private static Value PlotScaleMarker(SimHost h, CicodeTask t, ArgList a)
        {
            var p = Get(h, a.Get(0));
            if (p == null)
                return Fail(t, W.I(Codes.BadHandle), Codes.BadHandle);
            return Done(t, Scale(p, I(a, 1), I(a, 2), I(a, 3), I(a, 4), A.I(a.Get(5)), I(a, 6)));
        }

        private static int Scale(Plot p, int x, int y, int ticks, int width, long colour, int mode)
        {
            var f = p.Grid;
            if (f == null)
                return Codes.InvalidArg;
            int height = f.B - f.T;
            var pen = Pen(0, colour, width);
            Shape(p, "line", x, y, x, y - height, pen, 0);
            int len = MarkerSize(p);
            int left = mode == 2 ? x : x - len,
                right = mode == 1 ? x : x + len;
            for (int k = 0; k < ticks; k++)
            {
                int ty = ticks == 1 ? y : y - (int)Math.Round(height * (double)k / (ticks - 1));
                Shape(p, "line", left, ty, right, ty, pen, 0);
            }
            return 0;
        }

        [Builtin(
            "PlotText",
            Doc = "1188854",
            Effects = "adds a text to the plot",
            Errors = "269 no such plot (raised, returned)",
            Notes = "The text in the DspFont font hFont (its colours) with its baseline's start at (X, Y) (graph.ci places titles by the font's height below the top: guess). Orientation 0 left to right, 1 upwards, 2 right to left (upside down), 3 downwards (another: 0, guess). '@(...)' texts are localised when shown or printed."
        )]
        private static Value PlotText(SimHost h, CicodeTask t, ArgList a)
        {
            var p = Get(h, a.Get(0));
            if (p == null)
                return Fail(t, W.I(Codes.BadHandle), Codes.BadHandle);
            Text(h, p, I(a, 1), I(a, 2), I(a, 3), I(a, 4), A.S(a.Get(5)));
            return W.I(0);
        }

        private static void Text(SimHost h, Plot p, int font, int orient, int x, int y, string text)
        {
            if (orient < 0 || orient > 3)
                orient = 0;
            var o = JsonValue.NewObject();
            o.Set("op", "text");
            o.Set("x", JsonValue.Of(x));
            o.Set("y", JsonValue.Of(y));
            o.Set("text", text);
            o.Set("font", JsonValue.Of(font));
            o.Set("orient", JsonValue.Of(orient));
            if (Add(p, o) && orient == 0 && text.Length > 0)
            {
                var fi = DisplayBuiltins.Of(h).Font(font);
                var r = TextMeasure.Box(fi, DisplayBuiltins.Of(h).Language.Localise(text), 0, x, y);
                Cover(p, r.L, r.T, r.R, r.B);
            }
        }

        // ------------------------------------------------------------------ markers, information
        [Builtin(
            "PlotSetMarker",
            Doc = "1188853",
            Effects = "registers a symbol as a marker (the process's marker table)",
            Errors = "274 a marker number outside 20..32000 or an empty name (raised, returned)",
            Notes = "The symbol ('library.symbol') is not looked up here (guess); a number registered again takes the new symbol. A viewer draws the symbol with its hot spot at the marker's point."
        )]
        private static Value PlotSetMarker(SimHost h, CicodeTask t, ArgList a)
        {
            int no = I(a, 0);
            string name = A.S(a.Get(1)).Trim();
            if (no < 20 || no > 32000 || name.Length == 0)
                return Fail(t, W.I(Codes.InvalidArg), Codes.InvalidArg);
            Of(h).Markers[no] = name;
            return W.I(0);
        }

        [Builtin(
            "PlotGetMarker",
            Doc = "1188846",
            Errors = "289 no marker of that symbol (raised, -1)",
            Notes = "The lowest number PlotSetMarker registered the symbol under (the name without case: guess)."
        )]
        private static Value PlotGetMarker(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.S(a.Get(0)).Trim();
            int best = -1;
            foreach (var kv in Of(h).Markers)
                if (
                    string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase)
                    && (best < 0 || kv.Key < best)
                )
                    best = kv.Key;
            return best >= 0 ? W.I(best) : Fail(t, W.I(-1), Codes.NameNotFound);
        }

        /// <summary>A font's height in the plot's pixels: a positive size is pixels, a negative one
        /// points at the plot's dpi.</summary>
        internal static int FontHeight(FontInfo f, Plot p) =>
            f.Size > 0 ? f.Size
            : f.Size < 0 ? (int)Math.Round(-f.Size * (double)p.Dpi / 72.0, MidpointRounding.AwayFromZero)
            : (int)Math.Round(12.0 * p.Dpi / ScreenDpi);

        private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

        /// <summary>A font's average character width in the plot's pixels: the width of the 52
        /// letters / 52, rounded (guess: GDI's tmAveCharWidth is not read).</summary>
        private static int CharWidth(FontInfo f, Plot p)
        {
            double w = TextMeasure.Width(f, Alphabet) / 52.0;
            int h = f.Height;
            return (int)Math.Round(h > 0 ? w * FontHeight(f, p) / h : w);
        }

        private static string Mm(int pixels, int dpi) =>
            ((int)Math.Round(pixels * 25.4 / dpi)).ToString(CultureInfo.InvariantCulture);

        [Builtin(
            "PlotInfo",
            Doc = "1188848",
            Errors = "269 no such plot (raised); 274 another type (raised)",
            Notes = "The answer as a string. A printer: an A4 portrait page at 300 dpi (guess): 0 2480, 1 3508, 2 / 3 300, 4 / 5 300 (graph.ci reads type 5 as pixels per inch: guess), 11 210, 12 297 (mm). A display: the page's width and height (640 x 480 without them), 96 per inch, sizes in mm at 96 dpi. Types 6..8 of the font handle sInput: 6 the height (a positive DspFont size is pixels, a negative one points at the plot's dpi), 7 the external leading 0 (guess), 8 the average character width (the 52 letters' width / 52: guess); \"0\" without a font. 9 rotated text \"1\"; 10 \"1\" when sInput is a font handle, else \"0\"."
        )]
        private static Value PlotInfo(SimHost h, CicodeTask t, ArgList a)
        {
            var p = Get(h, a.Get(0));
            if (p == null)
                return Fail(t, W.Empty, Codes.BadHandle);
            string? s = Info(h, p, I(a, 1), a.Count > 2 && !a[2].IsNothing ? A.S(a[2]).Trim() : "");
            return s != null ? W.S(s) : Fail(t, W.Empty, Codes.InvalidArg);
        }

        /// <summary>PlotInfo's answer, null for an unknown type.</summary>
        private static string? Info(SimHost h, Plot p, int type, string input)
        {
            FontInfo? font = int.TryParse(
                input,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int fh
            )
                ? DisplayBuiltins.Of(h).Font(fh)
                : null;
            string Inv(int v) => v.ToString(CultureInfo.InvariantCulture);
            switch (type)
            {
                case 0:
                    return Inv(p.Width);
                case 1:
                    return Inv(p.Height);
                case 2:
                case 3:
                case 4:
                case 5:
                    return Inv(p.Dpi);
                case 6:
                    return font != null ? Inv(FontHeight(font, p)) : "0";
                case 7:
                    return "0";
                case 8:
                    return font != null ? Inv(CharWidth(font, p)) : "0";
                case 9:
                    return "1";
                case 10:
                    return font != null ? "1" : "0";
                case 11:
                    return Mm(p.Width, p.Dpi);
                case 12:
                    return Mm(p.Height, p.Dpi);
                default:
                    return null;
            }
        }

        // ------------------------------------------------------------------ V3 graphs
        /// <summary>The one graph GraphOpen opened, null when none is open.</summary>
        private static Plot? Graph(SimHost h)
        {
            var st = Of(h);
            return st.Graph >= 0 && st.Open.TryGetValue(st.Graph, out var p) ? p : null;
        }

        /// <summary>A Graph built-in's result: 0, or 274 raised and returned.</summary>
        private static Value GraphDone(CicodeTask t, int err) =>
            err == 0 ? W.I(0) : Fail(t, W.I(Codes.InvalidArg), Codes.InvalidArg);

        private static Value NoGraph(CicodeTask t) => GraphDone(t, Codes.InvalidArg);

        [Builtin(
            "GraphOpen",
            Doc = "",
            Effects = "opens the graph (a plot of the process's plot table)",
            Errors = "274 nSamples outside 2..16000 or 256 plots open (raised, returned)",
            Notes = "The V3 form of PlotOpen(-, sOutput, 2): the graph always goes to the output port, never to the page, so 'Display' and '' name ports like any other (PlotClose's printer path: Printers\\Display-(n).svg, Printers\\Printer-(n).svg for ''). nSamples is the samples GraphGrid gives PlotGrid; the third argument is not used. A GraphOpen while a graph is open replaces it: the old one is dropped unclosed. Returns 0."
        )]
        private static Value GraphOpen(SimHost h, CicodeTask t, ArgList a)
        {
            int samples = I(a, 1);
            if (samples < 2 || samples > 16000)
                return NoGraph(t);
            var st = Of(h);
            if (st.Graph >= 0)
            {
                st.Open.Remove(st.Graph);
                st.Graph = -1;
            }
            int err = Open(h, t, 0, A.S(a.Get(0)).Trim(), 2, true, out long hnd);
            if (err != 0)
                return Fail(t, W.I(err), err);
            st.Graph = hnd;
            st.GraphSamples = samples;
            return W.I(0);
        }

        [Builtin(
            "GraphClose",
            Doc = "",
            Effects = "prints the graph to its port and closes it",
            Errors = "274 no graph open (raised, returned)",
            Notes = "The V3 form of PlotClose on the one graph GraphOpen opened. Returns 0."
        )]
        private static Value GraphClose(SimHost h, CicodeTask t, ArgList a)
        {
            if (Graph(h) == null)
                return NoGraph(t);
            Close(h, t, Of(h).Graph);
            return W.I(0);
        }

        [Builtin(
            "GraphGrid",
            Doc = "",
            Effects = "sets the graph's frame and adds its grid lines and frame",
            Errors = "274 no graph open, X1 >= X2, Y1 >= Y2, nVert outside 0..X2 - X1 - 1 or nHorz outside 0..Y2 - Y1 - 1 (raised, returned)",
            Notes = "The V3 form of PlotGrid on the one graph GraphOpen opened: GraphOpen's nSamples, nVert rows and nHorz columns of black grid lines, a black frame 2 pixels wide, no fill."
        )]
        private static Value GraphGrid(SimHost h, CicodeTask t, ArgList a)
        {
            var p = Graph(h);
            int x1 = I(a, 0),
                y1 = I(a, 1),
                x2 = I(a, 2),
                y2 = I(a, 3),
                vert = I(a, 4),
                horz = I(a, 5);
            if (
                p == null
                || x1 >= x2
                || y1 >= y2
                || vert < 0
                || vert >= x2 - x1
                || horz < 0
                || horz >= y2 - y1
            )
                return NoGraph(t);
            return GraphDone(
                t,
                Grid(p, Of(h).GraphSamples, x1, y1, x2, y2, vert, 0, horz, 0, 2, 0, 0xFFFFFFFFL)
            );
        }

        [Builtin(
            "GraphLine",
            Doc = "",
            Effects = "adds a line through the table's values to the graph",
            Errors = "274 no graph open, LineType above 5, a negative nMarker or MarkerSize, or PlotLine's errors (raised, returned)",
            Notes = "The V3 form of PlotLine on the one graph GraphOpen opened: LineType the pen style, a black pen 1 pixel wide, nMarker the marker style in black, MarkerSize the samples between markers; Mode + 32 (the values are trend samples)."
        )]
        private static Value GraphLine(SimHost h, CicodeTask t, ArgList a)
        {
            var p = Graph(h);
            int type = I(a, 0),
                marker = I(a, 1),
                size = I(a, 2);
            if (p == null || type >= 6 || marker < 0 || size < 0)
                return NoGraph(t);
            return GraphDone(
                t,
                Line(
                    h,
                    p,
                    type,
                    0,
                    1,
                    marker,
                    0,
                    size,
                    I(a, 3),
                    a.Get(4),
                    A.R(a.Get(5)),
                    A.R(a.Get(6)),
                    false,
                    default,
                    0,
                    0,
                    I(a, 7) | TrendData
                )
            );
        }

        [Builtin(
            "GraphBox",
            Doc = "",
            Effects = "adds a rectangle to the graph",
            Errors = "274 no graph open, PenStyle above 5, PenWidth above 32, X1 > X2 or Y1 > Y2 (raised, returned)",
            Notes = "The V3 form of PlotDraw type 1 (a rectangle) on the one graph GraphOpen opened, in a black pen of PenStyle and PenWidth, filled with FillColour."
        )]
        private static Value GraphBox(SimHost h, CicodeTask t, ArgList a)
        {
            var p = Graph(h);
            int style = I(a, 0),
                width = I(a, 1),
                x1 = I(a, 3),
                y1 = I(a, 4),
                x2 = I(a, 5),
                y2 = I(a, 6);
            if (p == null || style >= 6 || width > 32 || x1 > x2 || y1 > y2)
                return NoGraph(t);
            return GraphDone(t, Draw(p, 1, style, 0, width, A.I(a.Get(2)), x1, y1, x2, y2));
        }

        [Builtin(
            "GraphText",
            Doc = "",
            Effects = "adds a text to the graph",
            Errors = "274 no graph open or Type above 3 (raised, returned)",
            Notes = "The V3 form of PlotText on the one graph GraphOpen opened: Font -1 is the default font, Type the orientation."
        )]
        private static Value GraphText(SimHost h, CicodeTask t, ArgList a)
        {
            var p = Graph(h);
            int font = I(a, 0),
                type = I(a, 1);
            if (p == null || type >= 4)
                return NoGraph(t);
            if (font == -1)
                font = DisplayBuiltins.Of(h).DefaultFont;
            Text(h, p, font, type, I(a, 2), I(a, 3), A.S(a.Get(4)));
            return W.I(0);
        }

        [Builtin(
            "GraphMarker",
            Doc = "",
            Effects = "adds markers to the graph",
            Errors = "274 no graph open, a negative nMarker or MarkerSize, Length 0 or less, or PlotMarker's errors (raised, returned)",
            Notes = "The V3 form of PlotMarker on the one graph GraphOpen opened: nMarker the marker style in black, MarkerSize the samples between markers."
        )]
        private static Value GraphMarker(SimHost h, CicodeTask t, ArgList a)
        {
            var p = Graph(h);
            int marker = I(a, 0),
                size = I(a, 1),
                length = I(a, 2);
            if (p == null || marker < 0 || size < 0 || length <= 0)
                return NoGraph(t);
            return GraphDone(t, MarkerAt(h, p, marker, 0, size, length, I(a, 3), I(a, 4)));
        }

        [Builtin(
            "GraphScaleMarker",
            Doc = "",
            Effects = "adds a scale line with tick marks to the graph",
            Errors = "274 no graph open, a negative third argument or no GraphGrid frame (raised, returned)",
            Notes = "The V3 form of PlotScaleMarker(X, Y, nMarker, 2, black, Mode) on the one graph GraphOpen opened: the scale line is 2 pixels wide."
        )]
        private static Value GraphScaleMarker(SimHost h, CicodeTask t, ArgList a)
        {
            var p = Graph(h);
            int ticks = I(a, 2);
            if (p == null || ticks < 0)
                return NoGraph(t);
            return GraphDone(t, Scale(p, I(a, 0), I(a, 1), ticks, 2, 0, I(a, 3)));
        }

        [Builtin(
            "GraphGetInfo",
            Doc = "",
            Errors = "274 no graph open or a type PlotInfo does not answer (raised, \"\")",
            Notes = "The V3 form of PlotInfo on the one graph GraphOpen opened (a printer page)."
        )]
        private static Value GraphGetInfo(SimHost h, CicodeTask t, ArgList a)
        {
            var p = Graph(h);
            int type = I(a, 0);
            string? s = p != null && type <= 12 ? Info(h, p, type, A.S(a.Get(1)).Trim()) : null;
            return s != null ? W.S(s) : Fail(t, W.Empty, Codes.InvalidArg);
        }
    }
}
