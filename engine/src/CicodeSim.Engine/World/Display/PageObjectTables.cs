using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

// The page objects the display client keeps besides the animation classes: the trend objects (the
// runtime's trend client table) and the alarm lists (AlarmDsp / AlarmDspLast /
// AlarmListCreate, following the help's rules). The animator makes them for a shown page, the Trn* and
// alarm list built-ins read and change them, the animator turns them into object states.
namespace CicodeSim.World.Display
{
    /// <summary>One pen of a trend object.</summary>
    public sealed class TrendPen
    {
        /// <summary>The trend tag (trend.dbf NAME, 'Cluster.Tag' as given), "" = no pen.</summary>
        public string Tag = "";

        /// <summary>The scale at 0 % and 100 % (floats); the default one is
        /// trend.dbf ENG_ZERO / ENG_FULL, else the expression tag's range.</summary>
        public float Lo,
            Hi = 100f,
            DefLo,
            DefHi = 100f;

        /// <summary>The end time of the pen's display in historical mode, a FILETIME
        /// (0 = not set).</summary>
        public long End;

        /// <summary>TrnSetDisplayMode's mode.</summary>
        public long DisplayMode;

        /// <summary>The value format: width and decimals.</summary>
        public int Width = 5,
            Decimals = 1;

        /// <summary>Engineering units (at most 7 characters).</summary>
        public string Units = "";
        public string Comment = "";

        /// <summary>SPCSetLimit's manual control limit of an SPC series pen (the
        /// setting and the value): a manual limit draws as that constant.</summary>
        public bool SpcManual;
        public double SpcLimit;

        public bool Used => Tag.Length > 0;

        public TrendPen Copy() => (TrendPen)MemberwiseClone();
    }

    /// <summary>A trend marker (DspMarkerNew): a vertical line at a sample offset from the
    /// trend's left edge (0..the samples) or a horizontal line at 0..32000 of the height from the
    /// bottom; not shown until DspMarkerMove places it.</summary>
    public sealed class TrendMarker
    {
        public bool Horizontal;

        /// <summary>0xRRGGBB, -1 transparent.</summary>
        public long Colour;

        /// <summary>The offset DspMarkerMove gave, -1 before the first move.</summary>
        public int Offset = -1;
    }

    /// <summary>A trend object on a window's page.</summary>
    public sealed class PageTrend
    {
        public int Window;
        public string Page = "";
        public int An;

        /// <summary>PGTREND TRENDNO (the trend's number on its page).</summary>
        public int TrendNo;
        public readonly TrendPen[] Pens = new TrendPen[8];

        /// <summary>The focus pen, 0-based.</summary>
        public int Focus;

        /// <summary>The samples across the display (from the drawing: legacy.trend_info).</summary>
        public int Samples = DefaultSamples;
        public int PixelsPerSample = 1;

        /// <summary>The sample period in 100 ns ticks; 0 = not set yet (the smallest
        /// period of the pens).</summary>
        public long PeriodTicks;

        /// <summary>The span in 100 ns ticks; 0 = samples x the period. TrnSetSpan keeps
        /// the span it is given and divides it into the period.</summary>
        public long SpanTicks;

        /// <summary>Historical mode (4); false = real time (1).</summary>
        public bool Historical;

        /// <summary>The cursor: samples back from the newest one, -1 off.</summary>
        public int Cursor = -1;

        /// <summary>The cluster the pens' trends are on.</summary>
        public string Cluster = "";
        public bool Periodic = true;

        /// <summary>TrnEcho's count: a positive count holds the redraw.</summary>
        public int EchoHold;

        /// <summary>The trend symbol TrnNew took ('lib.name').</summary>
        public string Symbol = "";

        /// <summary>The pen colours of the drawing ((on, off) pairs; up to 8).</summary>
        public readonly List<ColourPair> PenColours = new List<ColourPair>();

        /// <summary>Made by TrnSelect for a page not shown yet (window -2 / AN -3 wildcards).</summary>
        public bool Pending;

        /// <summary>Changes on every change a viewer must see (pens, period, mode, cursor, scale).</summary>
        public long Serial;

        /// <summary>The markers DspMarkerNew made, the handle = the index (up to MaxMarkers).</summary>
        public readonly List<TrendMarker> Markers = new List<TrendMarker>();

        /// <summary>The markers a trend takes (the help's 10).</summary>
        public const int MaxMarkers = 10;

        /// <summary>A trend without a drawing: the samples (guess; the trend drawing gives them).</summary>
        public const int DefaultSamples = 100;

        public PageTrend()
        {
            for (int i = 0; i < Pens.Length; i++)
                Pens[i] = new TrendPen();
        }

        public int PenCount
        {
            get
            {
                int n = 0;
                foreach (var p in Pens)
                    if (p.Used)
                        n++;
                return n;
            }
        }

        /// <summary>The pen of a Cicode pen number: 0 = the focus pen, 1..8 = that pen; null outside.</summary>
        public TrendPen? Pen(long pen)
        {
            int k = pen == 0 ? Focus : (int)pen - 1;
            return k >= 0 && k < Pens.Length ? Pens[k] : null;
        }

        public void Touch() => Serial++;
    }

    /// <summary>One alarm list of a window's page: a classic AlarmDsp / AlarmDspLast list
    /// (rows on the ANs An .. An + Count - 1) or an SA list made by AlarmListCreate (rows in the
    /// array of the AN).</summary>
    public sealed class PageAlarmList
    {
        public int Window;
        public string Page = "";
        public int An;

        /// <summary>The rows shown (Count; for AlarmListCreate the rows the height gives).</summary>
        public int Count = 1;

        /// <summary>The list type 0..18 (AlarmDsp's types; doc 1033462).</summary>
        public int Type = 1;
        public string Cluster = "";

        /// <summary>AlarmDsp's NoDraw: the alarm server's data is kept, no rows are drawn.</summary>
        public bool NoDraw;

        /// <summary>The function called (as a task, with the list's AN) when the list's data changes.</summary>
        public string Callback = "";

        /// <summary>AlarmDspLast's list.</summary>
        public bool Last;

        /// <summary>The AlarmDsp / AlarmDspLast arguments the list was made with: the same call
        /// again keeps the list (its offset and AlarmSetInfo settings).</summary>
        public string CreateKey = "";

        /// <summary>AlarmListCreate's list (an array of the AN).</summary>
        public bool ArrayList;

        /// <summary>The vertical offset in lines (AlarmSetInfo / AlarmGetInfo type 1).</summary>
        public int Offset;

        /// <summary>Type 2: a category or a group handle (0 = all).</summary>
        public long Category;

        /// <summary>Type 7: a priority or a group handle (0 = all).</summary>
        public long Priority;

        /// <summary>Type 4: display every alarm with the format and fonts of this category (-1 none).</summary>
        public long FormatCategory = -1;

        /// <summary>Type 5: a format handle (-1 = the categories' formats).</summary>
        public long FormatHandle = -1;

        /// <summary>Type 6: a font handle for every user alarm (-1 = the categories' fonts).</summary>
        public long Font = -1;

        /// <summary>Type 8: 0 by category, 1 by priority.</summary>
        public long DisplayMode;

        /// <summary>Type 9: 0 by ON time within its groups, 1 by the order-by keys (type 10).</summary>
        public long SortMode;
        public string OrderBy = "";

        /// <summary>Type 12: the named filter it is linked to ("" none).</summary>
        public string NamedFilter = "";

        /// <summary>A filter text edited through the AN (AlarmFilterEdit*), "" none.</summary>
        public string CustomFilter = "";

        /// <summary>Type 15: -1 always, 0 disabled, 1 page 1 only (alarm summary).</summary>
        public long AutoRefresh = -1;

        /// <summary>AlarmListCreate: the display size, row height, header flag, format name
        /// and fonts, the array handle.</summary>
        public int Width,
            Height,
            RowHeight = 1;
        public bool DrawHeader;
        public string FormatName = "";
        public long FontRow = -1,
            FontHeader = -1;
        public int ArrayHandle = -1;

        /// <summary>The array record the handle named when the list made it (the code may destroy
        /// it, and the handle then name another array), null when none.</summary>
        public object? ArrayRecord;

        /// <summary>Frees the list's array (set by AlarmListCreate; called when the page closes).</summary>
        public Action? ReleaseArray;

        /// <summary>The columns of an AlarmListCreate list (format field name, width).</summary>
        public readonly List<(string Name, int Width)> Columns = new List<(string, int)>();

        /// <summary>The record ids of the rows of the last fill (an alarm's definition ordinal or
        /// an event's number), the total of the list's alarms.</summary>
        public readonly List<object> RowItems = new List<object>();
        public int Total;

        /// <summary>A digest of the last fill: a change calls the callback.</summary>
        public string Digest = "";

        /// <summary>The digest the callback last ran for.</summary>
        public string Notified = "";
        public long Serial;
    }

    /// <summary>The trend objects and alarm lists of a display client (one per DisplayState).</summary>
    public sealed class PageObjectTables
    {
        private static readonly ConditionalWeakTable<DisplayState, PageObjectTables> All =
            new ConditionalWeakTable<DisplayState, PageObjectTables>();

        public static PageObjectTables Of(DisplayState d) => All.GetValue(d, _ => new PageObjectTables());

        public readonly List<PageTrend> Trends = new List<PageTrend>();

        /// <summary>The current trend: TrnSelect's, the one AN -2 names.</summary>
        public PageTrend? Selected;
        public readonly List<PageAlarmList> AlarmLists = new List<PageAlarmList>();

        private static bool SamePage(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>The trend of a window's page at an AN (the window, the AN or
        /// a -3 wildcard, the page name compared without case).</summary>
        public PageTrend? Trend(int window, string page, int an)
        {
            foreach (var t in Trends)
                if (
                    !t.Pending
                    && t.Window == window
                    && (t.An == an || an == -3 || t.An == -3)
                    && SamePage(t.Page, page)
                )
                    return t;
            return null;
        }

        /// <summary>Every trend of a window's page (AN -1 in the setters).</summary>
        public List<PageTrend> TrendsOf(int window, string page)
        {
            var l = new List<PageTrend>();
            foreach (var t in Trends)
                if (!t.Pending && t.Window == window && SamePage(t.Page, page))
                    l.Add(t);
            return l;
        }

        /// <summary>A TrnSelect trend a page shown now takes (window or -2, the
        /// AN or -3, the page name); it is bound to the window and AN.</summary>
        public PageTrend? Adopt(int window, string page, int an)
        {
            foreach (var t in Trends)
                if (
                    t.Pending
                    && (t.Window == window || t.Window == -2)
                    && (t.An == an || t.An == -3)
                    && SamePage(t.Page, page)
                )
                {
                    t.Pending = false;
                    t.Window = window;
                    t.An = an;
                    return t;
                }
            return null;
        }

        /// <summary>The page of a window closed: its trends and alarm lists go (a TrnSelect
        /// trend for a page not shown yet stays).</summary>
        public void PageClosed(int window, string page)
        {
            Trends.RemoveAll(t => !t.Pending && t.Window == window && SamePage(t.Page, page));
            if (Selected != null && !Trends.Contains(Selected))
                Selected = null;
            // an AlarmListCreate list's array goes with its page: it is bound
            // to its AN, so the display's PageClosing (raised first) has freed it and ReleaseArray
            // finds the record gone; ReleaseArray frees one that close did not
            foreach (var l in AlarmLists)
                if (l.Window == window && SamePage(l.Page, page))
                    l.ReleaseArray?.Invoke();
            AlarmLists.RemoveAll(l => l.Window == window && SamePage(l.Page, page));
        }

        /// <summary>The alarm list of a window's page whose rows include <paramref name="an"/>
        /// (its first AN when <paramref name="exact"/>).</summary>
        public PageAlarmList? AlarmList(int window, string page, int an, bool exact = false)
        {
            foreach (var l in AlarmLists)
                if (
                    l.Window == window
                    && SamePage(l.Page, page)
                    && (exact ? l.An == an : an >= l.An && an < l.An + Math.Max(1, l.Count))
                )
                    return l;
            return null;
        }

        public List<PageAlarmList> AlarmListsOf(int window, string page)
        {
            var o = new List<PageAlarmList>();
            foreach (var l in AlarmLists)
                if (l.Window == window && SamePage(l.Page, page))
                    o.Add(l);
            return o;
        }
    }
}
