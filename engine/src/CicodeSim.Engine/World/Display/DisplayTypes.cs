using System;
using System.Collections.Generic;

// The drawing side of a page as the display client needs it:
// what ctgedit's reader (or a ctsim-page/0 model) supplies
// per animation point. The engine never parses .ctg / .ctF itself.
namespace CicodeSim.World.Display
{
    /// <summary>A rectangle in page pixels (l, t, r, b), the runtime's RECT.</summary>
    public readonly struct Rect : IEquatable<Rect>
    {
        public readonly int L,
            T,
            R,
            B;

        public Rect(int l, int t, int r, int b)
        {
            L = l;
            T = t;
            R = r;
            B = b;
        }

        public int Width => R - L;
        public int Height => B - T;

        /// <summary>The point lies in (l, t, r + 1, b + 1): the rectangle including its right and
        /// bottom pixel (DspGetMouseOver).</summary>
        public bool ContainsInclusive(int x, int y) => x >= L && x < R + 1 && y >= T && y < B + 1;

        public bool Equals(Rect o) => L == o.L && T == o.T && R == o.R && B == o.B;

        public override bool Equals(object? obj) => obj is Rect r && Equals(r);

        public override int GetHashCode() => ((L * 31 + T) * 31 + R) * 31 + B;

        public override string ToString() => $"({L},{T},{R},{B})";
    }

    /// <summary>A colour as the runtime keeps it: an (on, off) pair, the two flash phases;
    /// packed 0x00RRGGBB, 0xFF in the high byte = transparent (ctgedit
    /// format/fillcolour.py).</summary>
    public readonly struct ColourPair : IEquatable<ColourPair>
    {
        public readonly uint On,
            Off;

        public ColourPair(uint on, uint off)
        {
            On = on;
            Off = off;
        }

        public static ColourPair Steady(uint c) => new ColourPair(c, c);

        public bool Flashing => On != Off;

        /// <summary>The half shown in a flash phase (true = the ON half).</summary>
        public uint At(bool flashOn) => flashOn ? On : Off;

        public bool Equals(ColourPair o) => On == o.On && Off == o.Off;

        public override bool Equals(object? obj) => obj is ColourPair c && Equals(c);

        public override int GetHashCode() => unchecked((int)(On * 31 + Off));

        public override string ToString() => $"0x{On:x8}/0x{Off:x8}";
    }

    /// <summary>One threshold of a Threshold fill colour: cmp 0 '&lt;', 1 '&gt;', 2 '&lt;=',
    /// 3 '&gt;=' (ctgedit: colourstate._parse_thr), the percent and its colour pair.</summary>
    public readonly struct Threshold
    {
        public readonly int Cmp;
        public readonly double Percent;
        public readonly ColourPair Colour;

        public Threshold(int cmp, double percent, ColourPair colour)
        {
            Cmp = cmp;
            Percent = percent;
            Colour = colour;
        }
    }

    /// <summary>The colour-state array of a drawing record (ctgedit: format/colourstate.py FLAG
    /// walk): every colour the fill colour and fill level classes read.</summary>
    public sealed class ColourStates
    {
        /// <summary>The static / state-0 colour (On/Off OFF, Multi-state 0, Array 0, Gradient
        /// minimum).</summary>
        public ColourPair? State0;

        /// <summary>COL_MAXON: On/Off ON and the Gradient maximum (default green).</summary>
        public ColourPair? MaxOn;

        /// <summary>COL_STA_0..n-1 (Multi-state).</summary>
        public List<ColourPair> Sta = new List<ColourPair>();

        /// <summary>COL_ARR_0..255 (Array), null when the record has none.</summary>
        public List<ColourPair>? Arr;
        public List<Threshold> Thr = new List<Threshold>();

        /// <summary>FILCOL: the unfilled part of a fill level.</summary>
        public ColourPair? FilCol;
    }

    /// <summary>The text string section of a text record (ctgedit: writer.text_string_section):
    /// the strings the display value classes show.</summary>
    public sealed class TextStrings
    {
        public string? Off;
        public string? On;
        public List<string> States = new List<string>();
        public List<string> Array = new List<string>();

        /// <summary>The TXTMODE of the string section (0 = a static caption), -1 not given.</summary>
        public int Mode = -1;

        /// <summary>The caption of the text tail (what a static text shows), null not given.</summary>
        public string? Caption;

        /// <summary>The text record's own font and anchor (page model text.font), null not given.</summary>
        public TextFont? Font;
    }

    /// <summary>A text record's font and layout (ctgedit sim/pagemodel text.font): the face, the
    /// size in points (lfHeight = -(96 x points) / 72), the style bits, the justification (0 left,
    /// 1 right, 2 centre), the orientation (1 = upright) and the baseline anchor.</summary>
    public sealed class TextFont
    {
        public string Face = "";
        public int Points;
        public bool Bold,
            Italic,
            Underline,
            Strikeout;
        public int Justify;
        public int Orient = 1;
        public int AnchorX,
            AnchorY;
    }

    /// <summary>A symbol set (tc 0x0C): its state children and declared count (ctgedit:
    /// format/symbolset.py).</summary>
    public sealed class SymbolSetInfo
    {
        /// <summary>The direct state children (touch / keyboard records not counted).</summary>
        public int States;

        /// <summary>The declared count (256 for an array set written by CtDraw).</summary>
        public int Declared = 256;

        /// <summary>'lib.element' of each state, when known.</summary>
        public List<string> Names = new List<string>();
    }

    /// <summary>A trend's drawing data (ctgedit: legacy.trend_info).</summary>
    public sealed class TrendInfo
    {
        public List<ColourPair> Pens = new List<ColourPair>();
        public int Samples;
        public int PixelsPerSample = 1;
    }

    /// <summary>The drawing summary of one animation point (AN = geometry-header handle + 1).</summary>
    public sealed class DrawObject
    {
        public int An;

        /// <summary>The record's type code.</summary>
        public int Type;

        /// <summary>The parent's AN, 0 for a top-level object.</summary>
        public int Parent;
        public List<int> Children = new List<int>();

        /// <summary>The object name (__AN_NAME__ / header name).</summary>
        public string Name = "";
        public string ControlClsid = "";

        /// <summary>The rectangle the classes use for pivots and hit tests (drawing object;
        /// which of extent / bounds: verify).</summary>
        public Rect? Rect;

        /// <summary>rect2 (bounds).</summary>
        public Rect? Bounds;
        public int? PosX,
            PosY;

        /// <summary>The magnitude ratio of the object's Total vs its parent's transform (movement
        /// offsets scale with a scaled genie); 1 when not given.</summary>
        public double MagnitudeX = 1.0,
            MagnitudeY = 1.0;

        /// <summary>METADATA_n as (name, value) in key order.</summary>
        public List<KeyValuePair<string, string>> Metadata = new List<KeyValuePair<string, string>>();
        public string Tip = "";
        public ColourStates? Colour;
        public TextStrings? Text;
        public SymbolSetInfo? SymbolSet;

        /// <summary>The polygon / pipe vertex count, when known.</summary>
        public int? Vertices;
        public TrendInfo? Trend;
        public bool HasKeyCommand;
        public bool HasTouch;
    }

    /// <summary>A page association (attributes list 2: name, default, value on error,
    /// description).</summary>
    public sealed class PageAssociation
    {
        public string Name = "";
        public string Default = "";
        public string OnError = "";
        public string Description = "";
    }

    /// <summary>The drawing summary of a page: its objects by AN, size, background, environment
    /// and associations. From ctgedit (or a ctsim-page/0 model); a page without one still runs
    /// from its compiled files.</summary>
    public sealed class PageDrawing
    {
        public string Page = "";
        public int Width,
            Height;
        public uint Background;
        public Dictionary<int, DrawObject> Objects = new Dictionary<int, DrawObject>();

        /// <summary>Record order of the ANs.</summary>
        public List<int> Order = new List<int>();

        /// <summary>The page environment (attributes list 1 keys not starting with '!').</summary>
        public List<KeyValuePair<string, string>> Env = new List<KeyValuePair<string, string>>();
        public List<PageAssociation> Associations = new List<PageAssociation>();

        public DrawObject? Get(int an) => Objects.TryGetValue(an, out var o) ? o : null;

        public void Add(DrawObject o)
        {
            if (!Objects.ContainsKey(o.An))
                Order.Add(o.An);
            Objects[o.An] = o;
        }
    }
}
