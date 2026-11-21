using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CicodeSim.Values;
using CicodeSim.World.Display;

namespace CicodeSim.Page
{
    /// <summary>The ctsim-page/0 page model: the drawing summary a viewer sends.</summary>
    public sealed class PageModel
    {
        public const string Format = "ctsim-page/0";

        public string Project = "";
        public string Page = "";

        /// <summary>"saved" (the .RDB is current), "unsaved" (the rows are the truth) ...</summary>
        public string Source = "";
        public PageDrawing Drawing = new PageDrawing();

        /// <summary>pages.DBF fields as strings.</summary>
        public Dictionary<string, string> Control = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase
        );

        /// <summary>pg*.DBF rows by table (lower-case name), each row column to string.</summary>
        public Dictionary<string, List<Dictionary<string, string>>> Tables = new Dictionary<
            string,
            List<Dictionary<string, string>>
        >(StringComparer.OrdinalIgnoreCase);

        /// <summary>Format notes (fields not understood), for the report.</summary>
        public List<string> Notes = new List<string>();

        public static PageModel Load(string path) => Parse(File.ReadAllText(path));

        public static PageModel Parse(string text) => FromJson(JsonValue.Parse(text));

        public static PageModel FromJson(JsonValue j)
        {
            var m = new PageModel();
            string fmt = Str(j.Get("format"));
            if (fmt.Length > 0 && fmt != Format)
                m.Notes.Add("format " + fmt + " (expected " + Format + ")");
            m.Project = Str(j.Get("project"));
            m.Page = Str(j.Get("page"));
            m.Source = Str(j.Get("source"));
            var d = m.Drawing;
            d.Page = m.Page;
            var size = j.Get("size");
            if (size != null && size.Kind == JsonKind.Array && size.Count >= 2)
            {
                d.Width = (int)size.Items[0].ToNumber();
                d.Height = (int)size.Items[1].ToNumber();
            }
            var bg = j.Get("background");
            if (bg != null && !bg.IsNull)
                d.Background = Colour(bg) ?? 0;
            var ctl = j.Get("control");
            if (ctl != null && ctl.Kind == JsonKind.Object)
                foreach (var kv in ctl.Pairs)
                    m.Control[kv.Key] = kv.Value.IsNull ? "" : kv.Value.Text;
            var ass = j.Get("associations");
            if (ass != null && ass.Kind == JsonKind.Array)
                foreach (var a in ass.Items)
                    d.Associations.Add(
                        new PageAssociation
                        {
                            Name = Str(a.Get("name")),
                            Default = Str(a.Get("default")),
                            OnError = Str(a.Get("onError")),
                            Description = Str(a.Get("description")),
                        }
                    );
            ReadPairs(j.Get("env"), d.Env);
            var tables = j.Get("tables");
            if (tables != null && tables.Kind == JsonKind.Object)
                foreach (var kv in tables.Pairs)
                {
                    var rows = new List<Dictionary<string, string>>();
                    foreach (var r in kv.Value.Items)
                    {
                        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var c in r.Pairs)
                            row[c.Key] = c.Value.IsNull ? "" : c.Value.Text;
                        rows.Add(row);
                    }
                    m.Tables[kv.Key] = rows;
                }
            var objs = j.Get("objects");
            if (objs != null && objs.Kind == JsonKind.Array)
                foreach (var o in objs.Items)
                    d.Add(ReadObject(o, m.Notes));
            return m;
        }

        private static DrawObject ReadObject(JsonValue o, List<string> notes)
        {
            var x = new DrawObject
            {
                An = (int)Num(o.Get("an")),
                Type = (int)Num(o.Get("type")),
                Parent = (int)Num(o.Get("parent")),
                Name = Str(o.Get("name")),
                ControlClsid = Str(o.Get("controlClsid")),
                Tip = Str(o.Get("tip")),
                Rect = RectOf(o.Get("rect")),
                Bounds = RectOf(o.Get("bounds")),
                HasKeyCommand = Bool(o.Get("hasKey")),
                HasTouch = Bool(o.Get("hasTouch")),
            };
            var ch = o.Get("children");
            if (ch != null && ch.Kind == JsonKind.Array)
                foreach (var c in ch.Items)
                    x.Children.Add((int)c.ToNumber());
            var pos = o.Get("pos");
            if (pos != null && pos.Kind == JsonKind.Array && pos.Count >= 2)
            {
                x.PosX = (int)pos.Items[0].ToNumber();
                x.PosY = (int)pos.Items[1].ToNumber();
            }
            var mag = o.Get("magnitude");
            if (mag != null && mag.Kind == JsonKind.Array && mag.Count >= 2)
            {
                x.MagnitudeX = mag.Items[0].ToNumber();
                x.MagnitudeY = mag.Items[1].ToNumber();
            }
            ReadPairs(o.Get("metadata"), x.Metadata);
            var col = o.Get("colour");
            if (col != null && col.Kind == JsonKind.Object)
            {
                var cs = new ColourStates
                {
                    State0 = Pair(col.Get("state0")),
                    MaxOn = Pair(col.Get("maxon")),
                    FilCol = Pair(col.Get("filcol")),
                };
                var sta = col.Get("sta");
                if (sta != null && sta.Kind == JsonKind.Array)
                    foreach (var p in sta.Items)
                        cs.Sta.Add(Pair(p) ?? default);
                var arr = col.Get("arr");
                if (arr != null && arr.Kind == JsonKind.Array)
                {
                    cs.Arr = new List<ColourPair>();
                    foreach (var p in arr.Items)
                        cs.Arr.Add(Pair(p) ?? default);
                }
                var thr = col.Get("thr");
                if (thr != null && thr.Kind == JsonKind.Array)
                    foreach (var t in thr.Items)
                    {
                        // [cmp, pct, on, off] (spec 12.2) or {cmp, pct, on, off}
                        if (t.Kind == JsonKind.Array && t.Count >= 3)
                            cs.Thr.Add(
                                new Threshold(
                                    (int)t.Items[0].ToNumber(),
                                    t.Items[1].ToNumber(),
                                    new ColourPair(
                                        Colour(t.Items[2]) ?? 0,
                                        Colour(t.Count > 3 ? t.Items[3] : t.Items[2]) ?? 0
                                    )
                                )
                            );
                        else if (t.Kind == JsonKind.Object)
                        {
                            uint on = Colour(t.Get("on")) ?? 0;
                            cs.Thr.Add(
                                new Threshold(
                                    (int)Num(t.Get("cmp")),
                                    Num(t.Get("pct")),
                                    new ColourPair(on, Colour(t.Get("off")) ?? on)
                                )
                            );
                        }
                    }
                x.Colour = cs;
            }
            var txt = o.Get("text");
            if (txt != null && txt.Kind == JsonKind.Object)
            {
                var ts = new TextStrings
                {
                    Off = StrOrNull(txt.Get("off")),
                    On = StrOrNull(txt.Get("on")),
                    Caption = StrOrNull(txt.Get("caption")),
                };
                var tmode = txt.Get("mode");
                if (tmode != null && tmode.IsNumber)
                    ts.Mode = (int)tmode.ToNumber();
                var tf = txt.Get("font");
                if (tf != null && tf.Kind == JsonKind.Object)
                {
                    var f = new TextFont
                    {
                        Face = StrOrNull(tf.Get("face")) ?? "",
                        Points = (int)Num(tf.Get("points")),
                        Bold = Bool(tf.Get("bold")),
                        Italic = Bool(tf.Get("italic")),
                        Underline = Bool(tf.Get("underline")),
                        Strikeout = Bool(tf.Get("strikeout")),
                        Justify = (int)Num(tf.Get("justify")),
                    };
                    var orient = tf.Get("orient");
                    if (orient != null && orient.IsNumber)
                        f.Orient = (int)orient.ToNumber();
                    var anc = tf.Get("anchor");
                    if (anc != null && anc.Kind == JsonKind.Array && anc.Count >= 2)
                    {
                        f.AnchorX = (int)anc.Items[0].ToNumber();
                        f.AnchorY = (int)anc.Items[1].ToNumber();
                    }
                    ts.Font = f;
                }
                var st = txt.Get("states");
                if (st != null && st.Kind == JsonKind.Array)
                    foreach (var s in st.Items)
                        ts.States.Add(s.IsNull ? "" : s.Text);
                var ar = txt.Get("array");
                if (ar != null && ar.Kind == JsonKind.Array)
                    foreach (var s in ar.Items)
                        ts.Array.Add(s.IsNull ? "" : s.Text);
                x.Text = ts;
            }
            var ss = o.Get("symbolSet");
            if (ss != null && ss.Kind == JsonKind.Object)
            {
                var si = new SymbolSetInfo { States = (int)Num(ss.Get("states")) };
                var dec = ss.Get("declared");
                if (dec != null && dec.IsNumber)
                    si.Declared = (int)dec.ToNumber();
                var names = ss.Get("names");
                if (names != null && names.Kind == JsonKind.Array)
                    foreach (var n in names.Items)
                        si.Names.Add(n.IsNull ? "" : n.Text);
                if (si.States == 0 && si.Names.Count > 0)
                    si.States = si.Names.Count;
                x.SymbolSet = si;
            }
            else if (ss != null && ss.IsNumber)
                x.SymbolSet = new SymbolSetInfo { States = (int)ss.ToNumber() };
            var vx = o.Get("vertices");
            if (vx != null && vx.IsNumber)
                x.Vertices = (int)vx.ToNumber();
            else if (vx != null && vx.Kind == JsonKind.Array)
                x.Vertices = vx.Count;
            var tr = o.Get("trend");
            if (tr != null && tr.Kind == JsonKind.Object)
            {
                var ti = new TrendInfo { Samples = (int)Num(tr.Get("samples")) };
                var pps = tr.Get("pixelsPerSample");
                if (pps != null && pps.IsNumber)
                    ti.PixelsPerSample = (int)pps.ToNumber();
                var pens = tr.Get("pens");
                if (pens != null && pens.Kind == JsonKind.Array)
                    foreach (var p in pens.Items)
                        ti.Pens.Add(Pair(p) ?? default);
                x.Trend = ti;
            }
            return x;
        }

        private static void ReadPairs(JsonValue? v, List<KeyValuePair<string, string>> into)
        {
            if (v == null)
                return;
            if (v.Kind == JsonKind.Object)
                foreach (var kv in v.Pairs)
                    into.Add(new KeyValuePair<string, string>(kv.Key, kv.Value.IsNull ? "" : kv.Value.Text));
            else if (v.Kind == JsonKind.Array)
                foreach (var e in v.Items)
                {
                    if (e.Kind == JsonKind.Array && e.Count >= 2)
                        into.Add(
                            new KeyValuePair<string, string>(
                                e.Items[0].Text,
                                e.Items[1].IsNull ? "" : e.Items[1].Text
                            )
                        );
                    else if (e.Kind == JsonKind.String)
                    {
                        // "name=value" (the METADATA_n form)
                        string s = e.Str;
                        int k = s.IndexOf('=');
                        into.Add(
                            k < 0
                                ? new KeyValuePair<string, string>(s, "")
                                : new KeyValuePair<string, string>(s.Substring(0, k), s.Substring(k + 1))
                        );
                    }
                }
        }

        private static string Str(JsonValue? v) => v == null || v.IsNull ? "" : v.Text;

        private static string? StrOrNull(JsonValue? v) => v == null || v.IsNull ? null : v.Text;

        private static double Num(JsonValue? v)
        {
            if (v == null || v.IsNull)
                return 0;
            if (v.IsNumber)
                return v.ToNumber();
            if (v.Kind == JsonKind.String)
            {
                string s = v.Str.Trim();
                if (
                    s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(
                        s.Substring(2),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out long h
                    )
                )
                    return h;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    return d;
            }
            return 0;
        }

        private static bool Bool(JsonValue? v) =>
            v != null && (v.Kind == JsonKind.Bool ? v.Bool : v.IsNumber && v.ToNumber() != 0);

        /// <summary>A colour: a number or a hex text; null for JSON null.</summary>
        public static uint? Colour(JsonValue? v)
        {
            if (v == null || v.IsNull)
                return null;
            if (v.IsNumber)
                return unchecked((uint)(long)v.ToNumber());
            string s = v.Str.Trim();
            if (s.StartsWith("#", StringComparison.Ordinal))
                s = s.Substring(1);
            else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(2);
            return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint c)
                ? c
                : (uint?)null;
        }

        /// <summary>A pair [on, off] or one steady colour; null for JSON null.</summary>
        public static ColourPair? Pair(JsonValue? v)
        {
            if (v == null || v.IsNull)
                return null;
            if (v.Kind == JsonKind.Array)
            {
                if (v.Count == 0)
                    return null;
                uint on = Colour(v.Items[0]) ?? 0;
                return new ColourPair(on, v.Count > 1 ? Colour(v.Items[1]) ?? on : on);
            }
            uint? c = Colour(v);
            return c == null ? (ColourPair?)null : ColourPair.Steady(c.Value);
        }

        private static Rect? RectOf(JsonValue? v)
        {
            if (v == null || v.Kind != JsonKind.Array || v.Count < 4)
                return null;
            return new Rect(
                (int)v.Items[0].ToNumber(),
                (int)v.Items[1].ToNumber(),
                (int)v.Items[2].ToNumber(),
                (int)v.Items[3].ToNumber()
            );
        }
    }
}
