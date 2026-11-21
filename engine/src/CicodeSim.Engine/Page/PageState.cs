using System;
using System.Collections.Generic;
using CicodeSim.Values;

namespace CicodeSim.Page
{
    /// <summary>The alarm indicator of a group (PGALMIND): the top active priority of its
    /// equipment query and the state it shows (proposed field).</summary>
    public sealed class AlarmIndicatorState : IEquatable<AlarmIndicatorState>
    {
        /// <summary>The top priority, 0 = no alarm.</summary>
        public long Priority;

        /// <summary>unack_on | ack_on | unack_off | none.</summary>
        public string State = "none";

        /// <summary>The frame colour: the FG of the priority's almprior.dbf pair of the state
        /// (PageRun.UpdateIndicator).</summary>
        public long? Colour;

        /// <summary>The pair's FG and BG differ: the frame alternates them.</summary>
        public bool Flash;

        /// <summary>The pair itself (not sent): Colour is FG in the on flash phase and, when it
        /// flashes, BG in the off phase (PageRun.Emit), as the runtime alternates them (live:
        /// ExampleSA's ON unacknowledged frames UNACKONFG and the white UNACKONBG).</summary>
        public long? Fg,
            Bg;

        /// <summary>A copy with the frame colour of the flash phase.</summary>
        public AlarmIndicatorState InPhase(bool flashOn) =>
            new AlarmIndicatorState
            {
                Priority = Priority,
                State = State,
                Flash = Flash,
                Fg = Fg,
                Bg = Bg,
                Colour = Flash && !flashOn ? Bg : Fg,
            };

        public bool Equals(AlarmIndicatorState? o) =>
            o != null && Priority == o.Priority && State == o.State && Colour == o.Colour && Flash == o.Flash;

        public override bool Equals(object? obj) => Equals(obj as AlarmIndicatorState);

        public override int GetHashCode() => Priority.GetHashCode() ^ State.GetHashCode();
    }

    /// <summary>One pen of a trend state: the trend tag, its scale, its colour for the flash
    /// phase and its samples oldest first (Samples of them, the newest at the right edge; null =
    /// no sample: before the history, bad quality, an expression trend not sampled).</summary>
    public sealed class TrendPenState : IEquatable<TrendPenState>
    {
        /// <summary>The pen number 1..8.</summary>
        public int Pen;
        public string Tag = "";
        public double Lo,
            Hi;
        public long? Colour;

        /// <summary>The simulated seconds of the newest sample (the right edge).</summary>
        public double End;
        public double?[] Values = Array.Empty<double?>();

        public bool Equals(TrendPenState? o)
        {
            if (
                o == null
                || Pen != o.Pen
                || Tag != o.Tag
                || Colour != o.Colour
                || Values.Length != o.Values.Length
            )
                return false;
            if (
                BitConverter.DoubleToInt64Bits(Lo) != BitConverter.DoubleToInt64Bits(o.Lo)
                || BitConverter.DoubleToInt64Bits(Hi) != BitConverter.DoubleToInt64Bits(o.Hi)
            )
                return false;
            if (BitConverter.DoubleToInt64Bits(End) != BitConverter.DoubleToInt64Bits(o.End))
                return false;
            for (int i = 0; i < Values.Length; i++)
            {
                double? a = Values[i],
                    b = o.Values[i];
                if (
                    a.HasValue != b.HasValue
                    || (
                        a.HasValue
                        && BitConverter.DoubleToInt64Bits(a.Value) != BitConverter.DoubleToInt64Bits(b!.Value)
                    )
                )
                    return false;
            }
            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as TrendPenState);

        public override int GetHashCode() => Pen;
    }

    /// <summary>A trend's state.</summary>
    public sealed class TrendState : IEquatable<TrendState>
    {
        public double Period;
        public double Span;
        public int Samples;
        public bool Historical;
        public int Cursor = -1;
        public int Focus = 1;
        public List<TrendPenState> Pens = new List<TrendPenState>();

        public bool Equals(TrendState? o)
        {
            if (
                o == null
                || Period != o.Period
                || Span != o.Span
                || Samples != o.Samples
                || Historical != o.Historical
                || Cursor != o.Cursor
                || Focus != o.Focus
                || Pens.Count != o.Pens.Count
            )
                return false;
            for (int i = 0; i < Pens.Count; i++)
                if (!Pens[i].Equals(o.Pens[i]))
                    return false;
            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as TrendState);

        public override int GetHashCode() => Pens.Count;

        /// <summary>{period, span, samples, mode, cursor, focus, pens: [{pen, tag, scale: [lo, hi],
        /// colour, end, values: [v | null ...]}]}.</summary>
        public JsonValue ToJson()
        {
            var tr = JsonValue.NewObject();
            tr.Set("period", JsonValue.Of(Period));
            tr.Set("span", JsonValue.Of(Span));
            tr.Set("samples", JsonValue.Of(Samples));
            tr.Set("mode", JsonValue.Of(Historical ? "historical" : "realtime"));
            tr.Set("cursor", JsonValue.Of(Cursor));
            tr.Set("focus", JsonValue.Of(Focus));
            var pens = JsonValue.NewArray();
            foreach (var p in Pens)
            {
                var po = JsonValue.NewObject();
                po.Set("pen", JsonValue.Of(p.Pen));
                po.Set("tag", JsonValue.Of(p.Tag));
                po.Set("scale", JsonValue.Array(new[] { JsonValue.Of(p.Lo), JsonValue.Of(p.Hi) }));
                po.Set("colour", p.Colour.HasValue ? JsonValue.Of(p.Colour.Value) : JsonValue.Null);
                po.Set("end", JsonValue.Of(p.End));
                var va = JsonValue.NewArray();
                foreach (var v in p.Values)
                    va.Add(v.HasValue ? JsonValue.Of(v.Value) : JsonValue.Null);
                po.Set("values", va);
                pens.Add(po);
            }
            tr.Set("pens", pens);
            return tr;
        }
    }

    /// <summary>One row of an alarm list.</summary>
    public sealed class AlarmRowState : IEquatable<AlarmRowState>
    {
        /// <summary>The row, 0 = the first row of the list.</summary>
        public int Row;
        public string Text = "";
        public List<KeyValuePair<string, string>> Fields = new List<KeyValuePair<string, string>>();
        public string Tag = "";
        public long Priority;
        public long Category;

        /// <summary>unack_on | ack_on | unack_off | ack_off | disabled (an event: its event name).</summary>
        public string State = "";

        /// <summary>The font of the row (fonts.dbf name), its foreground and background colours.</summary>
        public string Font = "";

        /// <summary>A font handle the list was given (AlarmSetInfo 6, AlarmListCreate), -1 none.</summary>
        public long FontHandle = -1;
        public long? Colour,
            Background;
        public bool Flash;

        public bool Equals(AlarmRowState? o)
        {
            if (
                o == null
                || Row != o.Row
                || Text != o.Text
                || Tag != o.Tag
                || Priority != o.Priority
                || Category != o.Category
                || State != o.State
                || Font != o.Font
                || FontHandle != o.FontHandle
                || Colour != o.Colour
                || Background != o.Background
                || Flash != o.Flash
                || Fields.Count != o.Fields.Count
            )
                return false;
            for (int i = 0; i < Fields.Count; i++)
                if (Fields[i].Key != o.Fields[i].Key || Fields[i].Value != o.Fields[i].Value)
                    return false;
            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as AlarmRowState);

        public override int GetHashCode() => Row ^ Text.GetHashCode();

        /// <summary>The row; with <paramref name="j"/> (a viewer's page state) the text and the
        /// field values localised and the row's font as fontInfo (the list's font handle, else
        /// the category font by name).</summary>
        public JsonValue ToJson(CicodeSim.World.Display.DisplayJson? j = null)
        {
            var o = JsonValue.NewObject();
            o.Set("row", JsonValue.Of(Row));
            o.Set("text", JsonValue.Of(j != null ? j.Text(Text) : Text));
            var f = JsonValue.NewArray();
            foreach (var kv in Fields)
                f.Add(
                    JsonValue.Array(
                        new[] { JsonValue.Of(kv.Key), JsonValue.Of(j != null ? j.Text(kv.Value) : kv.Value) }
                    )
                );
            o.Set("fields", f);
            o.Set("tag", JsonValue.Of(Tag));
            o.Set("priority", JsonValue.Of(Priority));
            o.Set("category", JsonValue.Of(Category));
            o.Set("state", JsonValue.Of(State));
            o.Set("font", JsonValue.Of(Font));
            o.Set("font_handle", JsonValue.Of(FontHandle));
            o.Set("colour", Colour.HasValue ? JsonValue.Of(Colour.Value) : JsonValue.Null);
            o.Set("background", Background.HasValue ? JsonValue.Of(Background.Value) : JsonValue.Null);
            o.Set("flash", JsonValue.Of(Flash));
            if (j != null)
            {
                var fi = FontHandle >= 0 ? j.Font(FontHandle) : null;
                if (fi == null && Font.Length > 0)
                    fi = j.Font(Font);
                if (fi != null)
                    o.Set("fontInfo", fi);
            }
            return o;
        }
    }

    /// <summary>An alarm list, on the list's first AN.</summary>
    public sealed class AlarmListState : IEquatable<AlarmListState>
    {
        public int Type;

        /// <summary>The rows it shows.</summary>
        public int Count;
        public int Offset;
        public int Total;

        /// <summary>An AlarmListCreate list (rows in the array of the AN, drawn by its genie).</summary>
        public bool Array;

        /// <summary>AlarmDsp's NoDraw: the page draws the rows itself (AlarmGetDsp).</summary>
        public bool NoDraw;
        public List<KeyValuePair<string, int>> Columns = new List<KeyValuePair<string, int>>();
        public List<AlarmRowState> Rows = new List<AlarmRowState>();

        /// <summary>The list's font handle (AlarmSetInfo 6, AlarmListCreate's row font), -1 none.</summary>
        public long FontHandle = -1;

        /// <summary>An AlarmListCreate list's header font handle, -1 none.</summary>
        public long HeaderFontHandle = -1;

        /// <summary>An AlarmListCreate list's display settings (its arguments): the header row
        /// flag, the row height and the list's size in pixels (0 for another list).</summary>
        public bool DrawHeader;
        public int RowHeight,
            Width,
            Height;

        /// <summary>An AlarmListCreate list's column titles (row 0 z 4 of its array, '@(...)'
        /// texts; empty for another list).</summary>
        public List<string> Titles = new List<string>();

        public bool Equals(AlarmListState? o)
        {
            if (
                o == null
                || Type != o.Type
                || Count != o.Count
                || Offset != o.Offset
                || Total != o.Total
                || Array != o.Array
                || NoDraw != o.NoDraw
                || Columns.Count != o.Columns.Count
                || Rows.Count != o.Rows.Count
                || FontHandle != o.FontHandle
                || HeaderFontHandle != o.HeaderFontHandle
                || DrawHeader != o.DrawHeader
                || RowHeight != o.RowHeight
                || Width != o.Width
                || Height != o.Height
                || Titles.Count != o.Titles.Count
            )
                return false;
            for (int i = 0; i < Titles.Count; i++)
                if (Titles[i] != o.Titles[i])
                    return false;
            for (int i = 0; i < Columns.Count; i++)
                if (Columns[i].Key != o.Columns[i].Key || Columns[i].Value != o.Columns[i].Value)
                    return false;
            for (int i = 0; i < Rows.Count; i++)
                if (!Rows[i].Equals(o.Rows[i]))
                    return false;
            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as AlarmListState);

        public override int GetHashCode() => Count ^ Rows.Count;

        /// <summary>The list; with <paramref name="j"/> its rows localised with their fontInfo,
        /// the list's own fontInfo (its font handle, else its first row's) and headerFontInfo.</summary>
        public JsonValue ToJson(CicodeSim.World.Display.DisplayJson? j = null)
        {
            var o = JsonValue.NewObject();
            o.Set("type", JsonValue.Of(Type));
            o.Set("count", JsonValue.Of(Count));
            o.Set("offset", JsonValue.Of(Offset));
            o.Set("total", JsonValue.Of(Total));
            o.Set("array", JsonValue.Of(Array));
            o.Set("no_draw", JsonValue.Of(NoDraw));
            var c = JsonValue.NewArray();
            foreach (var kv in Columns)
                c.Add(
                    JsonValue.Array(
                        new[] { JsonValue.Of(j != null ? j.Text(kv.Key) : kv.Key), JsonValue.Of(kv.Value) }
                    )
                );
            o.Set("columns", c);
            if (Array)
            {
                // an AlarmListCreate list: how its genie draws it
                o.Set("drawHeader", JsonValue.Of(DrawHeader));
                o.Set("rowHeight", JsonValue.Of(RowHeight));
                o.Set("width", JsonValue.Of(Width));
                o.Set("height", JsonValue.Of(Height));
                var ti = JsonValue.NewArray();
                foreach (var t in Titles)
                    ti.Add(JsonValue.Of(j != null ? j.Text(t) : t));
                o.Set("titles", ti);
            }
            var r = JsonValue.NewArray();
            foreach (var row in Rows)
                r.Add(row.ToJson(j));
            o.Set("rows", r);
            if (j != null)
            {
                JsonValue? fi = FontHandle >= 0 ? j.Font(FontHandle) : null;
                if (fi == null && Rows.Count > 0)
                {
                    var first = Rows[0];
                    fi = first.FontHandle >= 0 ? j.Font(first.FontHandle) : null;
                    if (fi == null && first.Font.Length > 0)
                        fi = j.Font(first.Font);
                }
                if (fi != null)
                    o.Set("fontInfo", fi);
                var hf = HeaderFontHandle >= 0 ? j.Font(HeaderFontHandle) : null;
                if (hf != null)
                    o.Set("headerFontInfo", hf);
            }
            return o;
        }
    }

    /// <summary>What the animator tells a viewer about one animated object for one scan.</summary>
    public sealed class ObjectState
    {
        public int An;
        public bool Shown = true;
        public bool Disabled;

        /// <summary>1 gray sunk, 2 gray part, 3 gray all, 4 hide, 5 unchanged (Include LABELS.DBF).</summary>
        public int DisableStyle = 5;
        public double[]? Move;
        public double? Rotate;
        public double[]? RotateOrigin;
        public double[]? Scale;
        public double[]? ScaleOrigin;
        public long? FillColour;
        public double? FillLevel;

        /// <summary>0 up, 1 down, 2 left, 3 right (FILMODE).</summary>
        public int FillLevelDirection;
        public long? FillLevelColour;
        public string? Text;
        public string? Symbol;
        public int? SymbolState;
        public List<double[]>? VertexOffsets;
        public string? Error;
        public string? Tooltip;

        /// <summary>bad | uncertain | error | override | inhibit (proposed).</summary>
        public string? Indication;
        public AlarmIndicatorState? Alarm;
        public TrendState? Trend;

        /// <summary>An alarm list's state on its first AN.</summary>
        public AlarmListState? AlarmList;

        /// <summary>A drawn alarm list's row on the row's AN (AlarmDsp: rows on AN .. AN + Count - 1).</summary>
        public AlarmRowState? AlarmRow;

        public ObjectState(int an)
        {
            An = an;
        }

        public ObjectState Clone()
        {
            var c = (ObjectState)MemberwiseClone();
            c.Move = Move == null ? null : (double[])Move.Clone();
            c.RotateOrigin = RotateOrigin == null ? null : (double[])RotateOrigin.Clone();
            c.Scale = Scale == null ? null : (double[])Scale.Clone();
            c.ScaleOrigin = ScaleOrigin == null ? null : (double[])ScaleOrigin.Clone();
            if (VertexOffsets != null)
            {
                c.VertexOffsets = new List<double[]>();
                foreach (var p in VertexOffsets)
                    c.VertexOffsets.Add((double[])p.Clone());
            }
            return c;
        }

        private static bool Same(double[]? a, double[]? b)
        {
            if (a == null || b == null)
                return a == b;
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (BitConverter.DoubleToInt64Bits(a[i]) != BitConverter.DoubleToInt64Bits(b[i]))
                    return false;
            return true;
        }

        private static bool Same(double? a, double? b) =>
            a.HasValue == b.HasValue
            && (
                !a.HasValue
                || BitConverter.DoubleToInt64Bits(a.Value) == BitConverter.DoubleToInt64Bits(b!.Value)
            );

        /// <summary>Every field equal (bit-equal numbers).</summary>
        public bool SameAs(ObjectState o)
        {
            if (An != o.An || Shown != o.Shown || Disabled != o.Disabled || DisableStyle != o.DisableStyle)
                return false;
            if (!Same(Move, o.Move) || !Same(Rotate, o.Rotate) || !Same(RotateOrigin, o.RotateOrigin))
                return false;
            if (!Same(Scale, o.Scale) || !Same(ScaleOrigin, o.ScaleOrigin))
                return false;
            if (
                FillColour != o.FillColour
                || !Same(FillLevel, o.FillLevel)
                || FillLevelDirection != o.FillLevelDirection
            )
                return false;
            if (
                FillLevelColour != o.FillLevelColour
                || Text != o.Text
                || Symbol != o.Symbol
                || SymbolState != o.SymbolState
            )
                return false;
            if (Error != o.Error || Tooltip != o.Tooltip || Indication != o.Indication)
                return false;
            if (!Equals(Alarm, o.Alarm) || !Equals(Trend, o.Trend))
                return false;
            if (!Equals(AlarmList, o.AlarmList) || !Equals(AlarmRow, o.AlarmRow))
                return false;
            if ((VertexOffsets == null) != (o.VertexOffsets == null))
                return false;
            if (VertexOffsets != null)
            {
                if (VertexOffsets.Count != o.VertexOffsets!.Count)
                    return false;
                for (int i = 0; i < VertexOffsets.Count; i++)
                    if (!Same(VertexOffsets[i], o.VertexOffsets[i]))
                        return false;
            }
            return true;
        }

        private static JsonValue Pair(double[]? p) =>
            p == null
                ? JsonValue.Null
                : JsonValue.Array(
                    new[] { JsonValue.Of(p.Length > 0 ? p[0] : 0.0), JsonValue.Of(p.Length > 1 ? p[1] : 0.0) }
                );

        private static JsonValue Num(double? d) => d.HasValue ? JsonValue.Of(d.Value) : JsonValue.Null;

        private static JsonValue Int(long? i) => i.HasValue ? JsonValue.Of(i.Value) : JsonValue.Null;

        private static JsonValue Str(string? s) => s == null ? JsonValue.Null : JsonValue.Of(s);

        /// <summary>The state with only the fields that are not at their default ('an' always):
        /// for logs and the CLI.</summary>
        public JsonValue ToCompactJson()
        {
            var full = ToJson();
            var o = JsonValue.NewObject();
            foreach (var kv in full.Pairs)
            {
                if (kv.Key != "an" && kv.Value.IsNull)
                    continue;
                if (kv.Key == "shown" && Shown)
                    continue;
                if (kv.Key == "disabled" && !Disabled)
                    continue;
                if (kv.Key == "disable_style" && DisableStyle == 5)
                    continue;
                if (kv.Key == "fill_level_direction" && (FillLevelDirection == 0 || FillLevel == null))
                    continue;
                o.Set(kv.Key, kv.Value);
            }
            return o;
        }

        /// <summary>The state as JSON.</summary>
        public JsonValue ToJson(CicodeSim.World.Display.DisplayJson? j = null)
        {
            var o = JsonValue.NewObject();
            o.Set("an", JsonValue.Of(An));
            o.Set("shown", JsonValue.Of(Shown));
            o.Set("disabled", JsonValue.Of(Disabled));
            o.Set("disable_style", JsonValue.Of(DisableStyle));
            o.Set("move", Pair(Move));
            o.Set("rotate", Num(Rotate));
            o.Set("rotate_origin", Pair(RotateOrigin));
            o.Set("scale", Pair(Scale));
            o.Set("scale_origin", Pair(ScaleOrigin));
            o.Set("fill_colour", Int(FillColour));
            o.Set("fill_level", Num(FillLevel));
            o.Set("fill_level_direction", JsonValue.Of(FillLevelDirection));
            o.Set("fill_level_colour", Int(FillLevelColour));
            o.Set("text", Str(j != null && Text != null ? j.Text(Text) : Text));
            o.Set("symbol", Str(Symbol));
            o.Set("symbol_state", SymbolState.HasValue ? JsonValue.Of(SymbolState.Value) : JsonValue.Null);
            if (VertexOffsets == null)
                o.Set("vertex_offsets", JsonValue.Null);
            else
            {
                var a = JsonValue.NewArray();
                foreach (var p in VertexOffsets)
                    a.Add(Pair(p));
                o.Set("vertex_offsets", a);
            }
            o.Set("error", Str(Error));
            o.Set("tooltip", Str(j != null && Tooltip != null ? j.Text(Tooltip) : Tooltip));
            o.Set("indication", Str(Indication));
            if (Alarm == null)
                o.Set("alarm", JsonValue.Null);
            else
            {
                var al = JsonValue.NewObject();
                al.Set("priority", JsonValue.Of(Alarm.Priority));
                al.Set("state", JsonValue.Of(Alarm.State));
                al.Set("colour", Int(Alarm.Colour));
                al.Set("flash", JsonValue.Of(Alarm.Flash));
                o.Set("alarm", al);
            }
            o.Set("trend", Trend == null ? JsonValue.Null : Trend.ToJson());
            o.Set("alarm_list", AlarmList == null ? JsonValue.Null : AlarmList.ToJson(j));
            o.Set("alarm_row", AlarmRow == null ? JsonValue.Null : AlarmRow.ToJson(j));
            return o;
        }
    }

    /// <summary>One window's page at one scan (ctgedit: sim/state.py PageState).</summary>
    public sealed class PageState
    {
        public int Window;
        public string Page = "";
        public Dictionary<int, ObjectState> Objects = new Dictionary<int, ObjectState>();

        /// <summary>The global flash phase of this scan.</summary>
        public bool FlashOn = true;

        /// <summary>Increases every scan that changed something.</summary>
        public long Serial;

        /// <summary>The page scans run so far.</summary>
        public long Scan;

        /// <summary>What Cicode drew on the page (DspStr, DspText, DspSym ...: the window's
        /// 'drawn by Cicode' list, CicodeSim.World.Display.Window.Drawn), the
        /// last output of each AN; the renderer draws them over the objects.</summary>
        public List<CicodeSim.World.Display.DrawCommand>? Drawn;

        /// <summary>The window's page variables (Window.VarsJson), null before the first scan.</summary>
        public JsonValue? PageVars;

        /// <summary>The page variables changed in the scan that made this event.</summary>
        public bool PageVarsFresh;

        /// <summary>The page's ActiveX controls the code or a viewer used (Outside.ControlsJson:
        /// [{an, name, properties, serial}]), null when none.</summary>
        public JsonValue? Controls;

        /// <summary>The controls changed in the scan that made this event.</summary>
        public bool ControlsFresh;

        /// <summary>{window, page, serial, flashOn, objects: [{an, state}], drawn: [...]} (the
        /// pageState body, the pageChanged event body; objects in AN order; drawn only when
        /// Cicode drew something).</summary>
        public JsonValue ToJson(
            IEnumerable<ObjectState>? only = null,
            CicodeSim.World.Display.DisplayJson? j = null
        )
        {
            var o = JsonValue.NewObject();
            o.Set("window", JsonValue.Of(Window));
            o.Set("page", Page);
            o.Set("serial", JsonValue.Of(Serial));
            o.Set("scan", JsonValue.Of(Scan));
            o.Set("flashOn", JsonValue.Of(FlashOn));
            if (Drawn != null && Drawn.Count > 0)
            {
                var d = JsonValue.NewArray();
                foreach (var c in Drawn)
                    d.Add(c.ToJson(j));
                o.Set("drawn", d);
            }
            // every page variable: in the whole state, and in a pageChanged when they changed
            if (PageVars != null && (only == null || PageVarsFresh))
                o.Set("pageVars", PageVars);
            // the ActiveX controls: in the whole state, and in a pageChanged when they changed
            if (Controls != null && (only == null || ControlsFresh))
                o.Set("controls", Controls);
            var list = new List<ObjectState>(only ?? Objects.Values);
            list.Sort((a, b) => a.An.CompareTo(b.An));
            var arr = JsonValue.NewArray();
            foreach (var s in list)
            {
                var e = JsonValue.NewObject();
                e.Set("an", JsonValue.Of(s.An));
                e.Set("state", s.ToJson(j));
                arr.Add(e);
            }
            o.Set("objects", arr);
            return o;
        }
    }
}
