using System;
using System.Collections.Generic;

// The extent an AN takes from what Cicode draws on it, and the extent of a static text as the
// display client measures it (engine/PROTOCOL.md Versions 11).
namespace CicodeSim.World.Display
{
    public sealed partial class AnObject
    {
        /// <summary>The extent the AN had before Cicode output changed it (null: unchanged).</summary>
        public Rect? OwnExtent;
        public Rect? OwnBounds;
    }

    /// <summary>What Cicode draws on an AN gives the AN its extent.</summary>
    public static class CicodeExtents
    {
        /// <summary>An AN whose extent follows its Cicode output.</summary>
        public static bool Follows(AnObject o) => o.Dynamic || o.Type == 0x1000 || o.Type == 0x1009;

        /// <summary>The AN's extent becomes <paramref name="r"/> (in configured coordinates:
        /// the AN's DspAnMove offset and animation are applied on top, DisplayAns.CurrentExtent).</summary>
        public static void Set(AnObject o, Rect r)
        {
            if (!Follows(o))
                return;
            if (o.OwnExtent == null)
            {
                o.OwnExtent = o.Extent;
                o.OwnBounds = o.Bounds;
            }
            o.Extent = r;
            o.Bounds = r;
        }

        /// <summary>The AN's own extent again (DspDel, or an output that sets none).</summary>
        public static void Restore(AnObject o)
        {
            if (o.OwnExtent == null)
                return;
            o.Extent = o.OwnExtent.Value;
            o.Bounds = o.OwnBounds ?? o.OwnExtent.Value;
            o.OwnExtent = null;
            o.OwnBounds = null;
        }

        /// <summary>The project folders a symbol is searched in: the run unit's search order.</summary>
        public static List<string> SearchDirs(DisplayState d)
        {
            var o = new List<string>();
            var u = d.World?.Unit;
            if (u == null)
                return o;
            foreach (var p in u.Search)
                if (!string.IsNullOrEmpty(p.Dir))
                    o.Add(p.Dir);
            return o;
        }

        /// <summary>The extent of a symbol output on <paramref name="o"/>: type 0 a .ctl symbol,
        /// 1 a .ctm genie (else a .ctl of that name); types 2..5 (alarm priority / mode genies)
        /// are not looked up (the extent is left as it was).</summary>
        public static void Symbol(
            DisplayState d,
            AnObject o,
            string name,
            long type,
            int dx = 0,
            int dy = 0,
            double sx = 1.0,
            double sy = 1.0
        )
        {
            if (!Follows(o))
                return;
            ElementFrame? f = null;
            if (type == 0)
                f = SymbolExtents.Find(SearchDirs(d), name, ".ctl", ".ctm");
            else if (type == 1)
                f = SymbolExtents.Find(SearchDirs(d), name, ".ctm", ".ctl");
            if (f == null)
            {
                Restore(o);
                return;
            }
            Set(o, f.Value.At(o.PosX + dx, o.PosY + dy, sx, sy));
        }

        /// <summary>The extent of a text output on <paramref name="o"/>: the shown text's box
        /// with its baseline on the AN's position (TextMeasure.Box).</summary>
        public static void Text(AnObject o, FontInfo? font, string shown, int align)
        {
            if (!Follows(o))
                return;
            Set(o, TextMeasure.Box(font, shown, align, o.PosX, o.PosY));
        }

        /// <summary>Measure a page's static texts as the display client draws them.</summary>
        public static void StaticTexts(DisplayState d, PageInstance p)
        {
            if (p.Drawing == null)
                return;
            foreach (var o in p.Objects.Values)
            {
                var dr = o.Drawing;
                var tf = dr?.Text?.Font;
                if (dr == null || tf == null || dr.Type != 5 || tf.Orient != 1 || dr.Text!.Mode > 0)
                    continue;
                string? caption = dr.Text.Caption;
                if (string.IsNullOrEmpty(caption) || caption!.IndexOf("@(", StringComparison.Ordinal) < 0)
                    continue;
                string shown = d.Language.Localise(caption);
                if (shown == caption)
                    continue;
                int height = tf.Points > 0 ? FontInfo.Dpi * tf.Points / 72 : 0;
                if (height <= 0)
                    continue;
                Rect box = new Rect(tf.AnchorX, tf.AnchorY, tf.AnchorX, tf.AnchorY);
                bool any = false;
                int y = tf.AnchorY;
                foreach (string line in Lines(shown))
                {
                    if (line.Length > 0)
                    {
                        var r = TextMeasure.Box(
                            tf.Face,
                            height,
                            tf.Bold,
                            tf.Italic,
                            tf.Underline,
                            tf.Strikeout,
                            line,
                            tf.Justify,
                            tf.AnchorX,
                            y
                        );
                        box = any
                            ? new Rect(
                                Math.Min(box.L, r.L),
                                Math.Min(box.T, r.T),
                                Math.Max(box.R, r.R),
                                Math.Max(box.B, r.B)
                            )
                            : r;
                        any = true;
                        y += r.B - r.T;
                    }
                    else
                        y +=
                            TextMeasure
                                .Box(
                                    tf.Face,
                                    height,
                                    tf.Bold,
                                    tf.Italic,
                                    tf.Underline,
                                    tf.Strikeout,
                                    " ",
                                    0,
                                    0,
                                    0
                                )
                                .B
                            - TextMeasure
                                .Box(
                                    tf.Face,
                                    height,
                                    tf.Bold,
                                    tf.Italic,
                                    tf.Underline,
                                    tf.Strikeout,
                                    " ",
                                    0,
                                    0,
                                    0
                                )
                                .T;
                }
                if (!any)
                    continue;
                o.Extent = box;
                o.Bounds = box;
            }
        }

        /// <summary>The lines of a caption (CR LF, CR or LF; a trailing newline adds none).</summary>
        private static IEnumerable<string> Lines(string s)
        {
            var parts = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int n = parts.Length;
            if (n > 1 && parts[n - 1].Length == 0)
                n--;
            for (int i = 0; i < n; i++)
                yield return parts[i];
        }
    }
}
