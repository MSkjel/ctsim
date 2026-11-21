using System.Collections.Generic;
using CicodeSim.Values;

namespace CicodeSim.Sim
{
    /// <summary>What a viewer is told about one animated object for one scan.</summary>
    public sealed class PageObjectState
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

        private static JsonValue Pair(double[]? p) =>
            p == null
                ? JsonValue.Null
                : JsonValue.Array(
                    new[] { JsonValue.Of(p.Length > 0 ? p[0] : 0.0), JsonValue.Of(p.Length > 1 ? p[1] : 0.0) }
                );

        private static JsonValue Num(double? d) => d.HasValue ? JsonValue.Of(d.Value) : JsonValue.Null;

        private static JsonValue Int(long? i) => i.HasValue ? JsonValue.Of(i.Value) : JsonValue.Null;

        /// <summary>The state object (the 'state' of one {an, state} entry).</summary>
        public JsonValue ToJson()
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
            o.Set("text", Text == null ? JsonValue.Null : JsonValue.Of(Text));
            o.Set("symbol", Symbol == null ? JsonValue.Null : JsonValue.Of(Symbol));
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
            o.Set("error", Error == null ? JsonValue.Null : JsonValue.Of(Error));
            o.Set("tooltip", Tooltip == null ? JsonValue.Null : JsonValue.Of(Tooltip));
            return o;
        }

        /// <summary>One window's page at one scan: the pageState body and the pageChanged event
        /// body ({window, page, serial, flashOn, objects: [{an, state}]}).</summary>
        public static JsonValue PageJson(
            int window,
            string page,
            long serial,
            bool flashOn,
            IEnumerable<PageObjectState> objects
        )
        {
            var o = JsonValue.NewObject();
            o.Set("window", JsonValue.Of(window));
            o.Set("page", page);
            o.Set("serial", JsonValue.Of(serial));
            o.Set("flashOn", JsonValue.Of(flashOn));
            var a = JsonValue.NewArray();
            foreach (var s in objects)
            {
                var e = JsonValue.NewObject();
                e.Set("an", JsonValue.Of(s.An));
                e.Set("state", s.ToJson());
                a.Add(e);
            }
            o.Set("objects", a);
            return o;
        }
    }
}
