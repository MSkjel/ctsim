using System;
using System.Globalization;
using System.Text;
using CicodeSim.Values;
using CicodeSim.World.Display;

namespace CicodeSim.Builtins
{
    /// <summary>A printer plot as an SVG page: a white page of the plot's size with its
    /// primitives in order (engine/PROTOCOL.md, drawn 'plot').</summary>
    internal static class PlotSvg
    {
        private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

        private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private static string Rgb(long c) =>
            c < 0 ? "none" : "#" + (c & 0xFFFFFF).ToString("x6", CultureInfo.InvariantCulture);

        private static long L(JsonValue o, string k) =>
            o.Get(k) is JsonValue v && v.IsNumber ? (long)v.ToNumber() : 0;

        private static string Esc(string s)
        {
            var b = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '&':
                        b.Append("&amp;");
                        break;
                    case '<':
                        b.Append("&lt;");
                        break;
                    case '>':
                        b.Append("&gt;");
                        break;
                    case '"':
                        b.Append("&quot;");
                        break;
                    default:
                        if (ch >= ' ' || ch == '\t')
                            b.Append(ch);
                        break;
                }
            }
            return b.ToString();
        }

        /// <summary>The dashes of a 1-pixel GDI pen style (a wider pen draws solid).</summary>
        private static string Dashes(long style, long width)
        {
            if (width > 1)
                return "";
            switch (style)
            {
                case 1:
                    return "18,6";
                case 2:
                    return "3,3";
                case 3:
                    return "9,6,3,6";
                case 4:
                    return "9,3,3,3,3,3";
                default:
                    return "";
            }
        }

        private static string Stroke(JsonValue? pen)
        {
            if (pen == null)
                return " stroke=\"none\"";
            long style = L(pen, "style"),
                colour = L(pen, "colour"),
                width = Math.Max(1, L(pen, "width"));
            if (style == 5 || colour < 0)
                return " stroke=\"none\"";
            string s = " stroke=\"" + Rgb(colour) + "\" stroke-width=\"" + N(width) + "\"";
            string d = Dashes(style, width);
            return d.Length > 0 ? s + " stroke-dasharray=\"" + d + "\"" : s;
        }

        public static string Write(DisplayState d, Plots.Plot p)
        {
            var b = new StringBuilder();
            b.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            b.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"")
                .Append(N(p.Width))
                .Append("\" height=\"")
                .Append(N(p.Height))
                .Append("\" viewBox=\"0 0 ")
                .Append(N(p.Width))
                .Append(' ')
                .Append(N(p.Height))
                .Append("\">\n");
            b.Append("<title>").Append(Esc(p.Output)).Append("</title>\n");
            b.Append("<rect x=\"0\" y=\"0\" width=\"")
                .Append(N(p.Width))
                .Append("\" height=\"")
                .Append(N(p.Height))
                .Append("\" fill=\"#ffffff\"/>\n");
            foreach (var o in p.Prims.Items)
                Prim(b, d, p, o);
            b.Append("</svg>\n");
            return b.ToString();
        }

        private static void Prim(StringBuilder b, DisplayState d, Plots.Plot p, JsonValue o)
        {
            string op = o.Get("op")?.Str ?? "";
            var pen = o.Get("pen");
            switch (op)
            {
                case "rect":
                case "ellipse":
                {
                    long x1 = L(o, "x1"),
                        y1 = L(o, "y1"),
                        x2 = L(o, "x2"),
                        y2 = L(o, "y2");
                    long l = Math.Min(x1, x2),
                        t = Math.Min(y1, y2),
                        w = Math.Abs(x2 - x1),
                        h = Math.Abs(y2 - y1);
                    string fill = " fill=\"" + Rgb(L(o, "fill")) + "\"";
                    if (op == "rect")
                        b.Append("<rect x=\"")
                            .Append(N(l))
                            .Append("\" y=\"")
                            .Append(N(t))
                            .Append("\" width=\"")
                            .Append(N(w))
                            .Append("\" height=\"")
                            .Append(N(h))
                            .Append('"');
                    else
                        b.Append("<ellipse cx=\"")
                            .Append(N(l + w / 2.0))
                            .Append("\" cy=\"")
                            .Append(N(t + h / 2.0))
                            .Append("\" rx=\"")
                            .Append(N(w / 2.0))
                            .Append("\" ry=\"")
                            .Append(N(h / 2.0))
                            .Append('"');
                    b.Append(fill).Append(Stroke(pen)).Append("/>\n");
                    break;
                }
                case "line":
                    b.Append("<line x1=\"")
                        .Append(N(L(o, "x1")))
                        .Append("\" y1=\"")
                        .Append(N(L(o, "y1")))
                        .Append("\" x2=\"")
                        .Append(N(L(o, "x2")))
                        .Append("\" y2=\"")
                        .Append(N(L(o, "y2")))
                        .Append('"')
                        .Append(Stroke(pen))
                        .Append("/>\n");
                    break;
                case "point":
                {
                    long w = pen != null ? Math.Max(1, L(pen, "width")) : 1;
                    long c = pen != null ? L(pen, "colour") : 0;
                    b.Append("<rect x=\"")
                        .Append(N(L(o, "x") - (w - 1) / 2))
                        .Append("\" y=\"")
                        .Append(N(L(o, "y") - (w - 1) / 2))
                        .Append("\" width=\"")
                        .Append(N(w))
                        .Append("\" height=\"")
                        .Append(N(w))
                        .Append("\" fill=\"")
                        .Append(Rgb(c))
                        .Append("\"/>\n");
                    break;
                }
                case "polyline":
                {
                    var pts = o.Get("points");
                    if (pts == null)
                        break;
                    b.Append("<polyline points=\"");
                    var items = pts.Items;
                    for (int i = 0; i + 1 < items.Count; i += 2)
                    {
                        if (i > 0)
                            b.Append(' ');
                        b.Append(N((long)items[i].ToNumber()))
                            .Append(',')
                            .Append(N((long)items[i + 1].ToNumber()));
                    }
                    b.Append("\" fill=\"none\" stroke-linejoin=\"round\"").Append(Stroke(pen)).Append("/>\n");
                    break;
                }
                case "text":
                    Text(b, d, p, o);
                    break;
                case "marker":
                    Marker(b, o);
                    break;
            }
        }

        private static void Text(StringBuilder b, DisplayState d, Plots.Plot p, JsonValue o)
        {
            string text = d.Language.Localise(o.Get("text")?.Str ?? "");
            if (text.Length == 0)
                return;
            long x = L(o, "x"),
                y = L(o, "y"),
                orient = L(o, "orient");
            var f = d.Font((int)L(o, "font"));
            string face = f != null && f.Face.Length > 0 ? f.Face : "Arial";
            int height = f != null ? Plots.FontHeight(f, p) : (int)Math.Round(12.0 * p.Dpi / 96);
            long colour = f != null ? Plots.Colour(f.FgOn) : 0;
            b.Append("<text x=\"")
                .Append(N(x))
                .Append("\" y=\"")
                .Append(N(y))
                .Append("\" font-family=\"")
                .Append(Esc(face))
                .Append("\" font-size=\"")
                .Append(N(height))
                .Append("\" fill=\"")
                .Append(Rgb(colour))
                .Append('"');
            if (f != null && f.Bold)
                b.Append(" font-weight=\"bold\"");
            if (f != null && f.Italic)
                b.Append(" font-style=\"italic\"");
            if (f != null && (f.Underline || f.Strikeout))
                b.Append(" text-decoration=\"")
                    .Append(
                        f.Underline && f.Strikeout ? "underline line-through"
                        : f.Underline ? "underline"
                        : "line-through"
                    )
                    .Append('"');
            int angle =
                orient == 1 ? -90
                : orient == 2 ? 180
                : orient == 3 ? 90
                : 0;
            if (angle != 0)
                b.Append(" transform=\"rotate(")
                    .Append(N(angle))
                    .Append(' ')
                    .Append(N(x))
                    .Append(' ')
                    .Append(N(y))
                    .Append(")\"");
            b.Append(" xml:space=\"preserve\">").Append(Esc(text)).Append("</text>\n");
        }

        private static void Marker(StringBuilder b, JsonValue o)
        {
            long x = L(o, "x"),
                y = L(o, "y"),
                style = L(o, "marker"),
                size = Math.Max(2, L(o, "size"));
            string c = Rgb(L(o, "colour"));
            double h = size / 2.0;
            string stroke = " stroke=\"" + c + "\" stroke-width=\"1\"";
            string fill = style >= 5 && style <= 8 ? " fill=\"" + c + "\"" : " fill=\"none\"";
            switch (style)
            {
                case 1:
                case 5:
                    b.Append("<polygon points=\"")
                        .Append(N(x))
                        .Append(',')
                        .Append(N(y - h))
                        .Append(' ')
                        .Append(N(x + h))
                        .Append(',')
                        .Append(N(y + h))
                        .Append(' ')
                        .Append(N(x - h))
                        .Append(',')
                        .Append(N(y + h))
                        .Append('"');
                    break;
                case 2:
                case 6:
                    b.Append("<rect x=\"")
                        .Append(N(x - h))
                        .Append("\" y=\"")
                        .Append(N(y - h))
                        .Append("\" width=\"")
                        .Append(N(size))
                        .Append("\" height=\"")
                        .Append(N(size))
                        .Append('"');
                    break;
                case 3:
                case 7:
                    b.Append("<circle cx=\"")
                        .Append(N(x))
                        .Append("\" cy=\"")
                        .Append(N(y))
                        .Append("\" r=\"")
                        .Append(N(h))
                        .Append('"');
                    break;
                case 4:
                case 8:
                    b.Append("<polygon points=\"")
                        .Append(N(x))
                        .Append(',')
                        .Append(N(y - h))
                        .Append(' ')
                        .Append(N(x + h))
                        .Append(',')
                        .Append(N(y))
                        .Append(' ')
                        .Append(N(x))
                        .Append(',')
                        .Append(N(y + h))
                        .Append(' ')
                        .Append(N(x - h))
                        .Append(',')
                        .Append(N(y))
                        .Append('"');
                    break;
                default:
                    // a symbol marker: its outline, named
                    b.Append("<g><title>")
                        .Append(Esc(o.Get("symbol")?.Str ?? ""))
                        .Append("</title><rect x=\"")
                        .Append(N(x - h))
                        .Append("\" y=\"")
                        .Append(N(y - h))
                        .Append("\" width=\"")
                        .Append(N(size))
                        .Append("\" height=\"")
                        .Append(N(size))
                        .Append("\" fill=\"none\"")
                        .Append(stroke)
                        .Append(" stroke-dasharray=\"2,2\"/></g>\n");
                    return;
            }
            b.Append(fill).Append(stroke).Append("/>\n");
        }
    }
}
