using System;
using System.Collections.Generic;
using System.Globalization;
using CicodeSim.Project;
using CicodeSim.Values;

// The display client's state: what Cicode draws on a window ("drawn by Cicode": DspStr,
// DspText, DspSym, DspBar ... recorded as commands for the renderer, never drawn by the engine),
// fonts, window creation order and children, the page list, the rubber band, the tool tip mode,
// the screen, and the page controller a process without a page animator uses (HeadlessPages).
namespace CicodeSim.World.Display
{
    /// <summary>A file display (DspFile): the window's lines and characters, the row and column
    /// offsets, and the lines of the file DspFileSetName read.</summary>
    public sealed class FileView
    {
        public int Height,
            Width,
            Row,
            Col;

        public List<string> Lines = new List<string>();

        public FileView Copy() =>
            new FileView
            {
                Height = Height,
                Width = Width,
                Row = Row,
                Col = Col,
                Lines = Lines,
            };
    }

    /// <summary>One display output Cicode made on an AN: the LAST output of
    /// each AN, kept per window in <see cref="Window.Drawn"/> until DspDel or the page changes.
    /// The renderer (ctgedit) draws it; the engine only records it.</summary>
    public sealed class DrawCommand
    {
        public int An;

        /// <summary>text (DspText / DspStr), sym (DspSym, DspSymAtSize), symAnm (DspSymAnm), bar
        /// (DspBar), trend (DspTrend), col (DspCol), bmp (DspBmp), chart (DspChart), file
        /// (DspFile), richText (DspRichText), move (DspAnMove / DspAnMoveRel), show (DspShow), plot
        /// (PlotClose).</summary>
        public string Kind = "";
        public string Text = "";

        /// <summary>The font handle (text), -1 none.</summary>
        public int Font = -1;

        /// <summary>Where the AN is (page pixels): its position when drawn, moved by DspAnMove.</summary>
        public int X,
            Y;

        /// <summary>The other arguments by name, as given (numbers as numbers).</summary>
        public List<KeyValuePair<string, JsonValue>> Args = new List<KeyValuePair<string, JsonValue>>();

        /// <summary>The window's draw serial when it was made.</summary>
        public long Serial;

        /// <summary>An array output's cells when it was drawn (kind 'array', DspArrayByAn):
        /// {rows, cols, data, firstRow, serial ...}; null for the other kinds.</summary>
        public JsonValue? Cells;

        /// <summary>The array's change count the cells were taken at (-1 none).</summary>
        public long CellsSerial = -1;

        /// <summary>A file display's window, offsets and lines (kind 'file'); null for the other
        /// kinds.</summary>
        public FileView? File;

        /// <summary>A rich text box's size, state, scroll position and RTF (kind 'richText');
        /// null for the other kinds.</summary>
        public RichTextView? RichText;

        public DrawCommand Arg(string name, long v)
        {
            Args.Add(new KeyValuePair<string, JsonValue>(name, JsonValue.Of(v)));
            return this;
        }

        public DrawCommand Arg(string name, double v)
        {
            Args.Add(new KeyValuePair<string, JsonValue>(name, JsonValue.Of(v)));
            return this;
        }

        public DrawCommand Arg(string name, string v)
        {
            Args.Add(new KeyValuePair<string, JsonValue>(name, JsonValue.Of(v)));
            return this;
        }

        /// <summary>The same output: AN, kind, text, font, position and arguments (the serial
        /// aside).</summary>
        public bool Same(DrawCommand o)
        {
            if (
                An != o.An
                || Kind != o.Kind
                || Text != o.Text
                || Font != o.Font
                || X != o.X
                || Y != o.Y
                || Args.Count != o.Args.Count
                || CellsSerial != o.CellsSerial
            )
                return false;
            for (int i = 0; i < Args.Count; i++)
                if (Args[i].Key != o.Args[i].Key || Args[i].Value.ToString() != o.Args[i].Value.ToString())
                    return false;
            return true;
        }

        /// <summary>The kinds whose text is display text (localised for a viewer); a sym's,
        /// bmp's or web's text is a symbol, file or URL name.</summary>
        private bool TextIsDisplay => Kind == "text" || Kind == "button";

        /// <summary>{an, kind, text?, font?, fontInfo?, args{...}, cells?, serial}. With
        /// <paramref name="j"/> (a viewer's page state) the display text
        /// is localised and a font handle comes with its fontInfo.</summary>
        public JsonValue ToJson(DisplayJson? j = null)
        {
            var o = JsonValue.NewObject();
            o.Set("an", JsonValue.Of(An));
            o.Set("kind", Kind);
            o.Set("x", JsonValue.Of(X));
            o.Set("y", JsonValue.Of(Y));
            string text = j != null && TextIsDisplay ? j.Text(Text) : Text;
            if (text.Length > 0)
                o.Set("text", text);
            if (Font >= 0)
            {
                o.Set("font", JsonValue.Of(Font));
                var fi = j?.Font(Font);
                if (fi != null)
                    o.Set("fontInfo", fi);
            }
            var a = JsonValue.NewObject();
            foreach (var kv in Args)
                a.Set(
                    kv.Key,
                    j != null && Kind == "plot" && kv.Key == "prims" && kv.Value.Kind == JsonKind.Array
                        ? j.Cells(kv.Value, LocalisePlot)
                        : kv.Value
                );
            o.Set("args", a);
            if (Cells != null)
                o.Set("cells", j != null ? j.Cells(Cells, LocaliseCells) : Cells);
            o.Set("serial", JsonValue.Of(Serial));
            return o;
        }

        /// <summary>A plot's primitives with each text localised and its font's fontInfo.</summary>
        private static JsonValue LocalisePlot(JsonValue prims, DisplayJson j)
        {
            var o = JsonValue.NewArray();
            foreach (var p in prims.Items)
            {
                if (p.Get("op")?.Str != "text")
                {
                    o.Add(p);
                    continue;
                }
                var t = JsonValue.NewObject();
                foreach (var kv in p.Pairs)
                    t.Set(kv.Key, kv.Key == "text" ? JsonValue.Of(j.Text(kv.Value.Str)) : kv.Value);
                var f = p.Get("font");
                var fi = f != null && f.IsNumber ? j.Font(f.Long) : null;
                if (fi != null)
                    t.Set("fontInfo", fi);
                o.Add(t);
            }
            return o;
        }

        /// <summary>The cells with their display texts (data, column titles) localised.</summary>
        private static JsonValue LocaliseCells(JsonValue cells, DisplayJson j)
        {
            var o = JsonValue.NewObject();
            foreach (var kv in cells.Pairs)
            {
                if (kv.Key == "data" && kv.Value.Kind == JsonKind.Array)
                {
                    var rows = JsonValue.NewArray();
                    foreach (var row in kv.Value.Items)
                    {
                        var r = JsonValue.NewArray();
                        foreach (var c in row.Items)
                            r.Add(c.Kind == JsonKind.String ? JsonValue.Of(j.Text(c.Str)) : c);
                        rows.Add(r);
                    }
                    o.Set("data", rows);
                }
                else if (kv.Key == "columns" && kv.Value.Kind == JsonKind.Array)
                {
                    var cols = JsonValue.NewArray();
                    foreach (var c in kv.Value.Items)
                    {
                        var co = JsonValue.NewObject();
                        foreach (var p in c.Pairs)
                            co.Set(
                                p.Key,
                                p.Key == "title" && p.Value.Kind == JsonKind.String
                                    ? JsonValue.Of(j.Text(p.Value.Str))
                                    : p.Value
                            );
                        cols.Add(co);
                    }
                    o.Set("columns", cols);
                }
                else
                    o.Set(kv.Key, kv.Value);
            }
            // the row / header fonts by handle
            var rf = cells.Get("rowFont");
            if (rf != null && rf.IsNumber && o.Get("rowFontInfo") == null)
            {
                var fi = j.Font(rf.Long);
                if (fi != null)
                    o.Set("rowFontInfo", fi);
            }
            var hf = cells.Get("headerFont");
            if (hf != null && hf.IsNumber && o.Get("headerFontInfo") == null)
            {
                var fi = j.Font(hf.Long);
                if (fi != null)
                    o.Set("headerFontInfo", fi);
            }
            return o;
        }
    }

    /// <summary>A font of the display client (fonts.dbf rows of the unit, then DspFont's):
    /// handle = the row index (guess: the runtime's own numbering is not read).</summary>
    public sealed partial class FontInfo
    {
        public int Handle;

        /// <summary>The fonts.dbf NAME, "" for a DspFont font.</summary>
        public string Name = "";
        public string Type = "";
        public int Size;
        public uint FgOn,
            BgOn,
            FgOff,
            BgOff;
    }

    /// <summary>The rubber band state of a window's page.</summary>
    public sealed class RubberBand
    {
        public int X0,
            Y0,
            X1,
            Y1;
        public int Mode;
        public bool Active;
        public bool Clip;
        public int ClipL,
            ClipT,
            ClipR,
            ClipB;
    }

    /// <summary>A monitor of the simulated screen.</summary>
    public sealed class Monitor
    {
        public int X,
            Y,
            W,
            H;
        public int WorkX,
            WorkY,
            WorkW,
            WorkH;
        public string Name = "\\\\.\\DISPLAY1";
    }

    /// <summary>What the page runtime knows of an AN that the display client does not: its
    /// movement and scaling this scan (the object state), and whether it is shown.</summary>
    public struct AnMotion
    {
        public double Dx,
            Dy;
        public double Sx,
            Sy;
        public double Ox,
            Oy;
        public bool Hidden;

        /// <summary>The object's privilege and area (PRIV / AREA of its rows; DspAnGetPrivilege,
        /// DspAnGetArea).</summary>
        public int Priv,
            Area;
    }

    /// <summary>The page runtime's side of the display client (implemented by the page animator next to
    /// IPageController; a process without one uses <see cref="HeadlessPages"/>).</summary>
    public interface IPageRuntime
    {
        /// <summary>A key code typed in a window (KeyPut): the key command line and its keyboard
        /// commands. True when a command ran.</summary>
        bool KeyPress(int window, int code);

        /// <summary>The window's associations changed while its page shows (AssWinReplace): the
        /// page's '?' references resolve again.</summary>
        void AssociationsChanged(int window);

        /// <summary>The AN's movement / scaling this scan, null when it has no animation.</summary>
        AnMotion? Motion(int window, int an);

        /// <summary>The AN's compiled expressions (DspInfoNew): per expression its context (the
        /// property tab: "Move X", "Visibility" ...) and code; null when the AN has none.</summary>
        List<AnExpression>? Expressions(int window, int an);
    }

    /// <summary>The page controller places the genies DspSym shows (a genie 'marked for use with
    /// DspSym': its library element and its compiled rows, '_' + SHA-1 .RDB): the element's records
    /// become objects of the page under the AN, with their names and animation.</summary>
    public interface ISymbolPlacer
    {
        /// <summary>Place a symbol DspSym names on an AN.</summary>
        CicodeSim.Values.JsonValue? PlaceSymbol(Window w, int an, string reference, int type = 0);

        /// <summary>DspAnMove moved the AN by (dx, dy): its genie's members move with it.</summary>
        void SymbolMoved(Window w, int an, int dx, int dy);

        /// <summary>The AN's output went (DspDel, another output, DspAnFree): its genie's members go.</summary>
        void SymbolGone(Window w, int an);
    }

    /// <summary>The page controller's slider animations (DspGetSlider / DspSetSlider).</summary>
    public interface ISliders
    {
        /// <summary>The AN's slider on the window's page (the first of its horizontal, vertical
        /// and rotational sliders): the code that reads its percent, the code that writes the
        /// percent in GetGlbStr(0) to its tag (null when it does not write) and their program;
        /// false when the AN has no slider.</summary>
        bool Slider(
            Window w,
            int an,
            out byte[] read,
            out byte[]? write,
            out CicodeSim.Compiled.Program? program
        );
    }

    /// <summary>One compiled expression of an AN (DspInfo).</summary>
    public sealed class AnExpression
    {
        public string Context = "";
        public byte[] Code = Array.Empty<byte>();

        /// <summary>The page's pools the code refers to (its TAGREFs).</summary>
        public CicodeSim.Compiled.Program? Program;
    }

    public sealed partial class Window
    {
        /// <summary>What Cicode drew on the shown page: the last output of each AN, in the order
        /// the ANs were first drawn.</summary>
        public readonly List<DrawCommand> Drawn = new List<DrawCommand>();

        /// <summary>Increases with every change of <see cref="Drawn"/>.</summary>
        public long DrawnSerial;

        /// <summary>Creation order (the runtime appends a child to its parent's child list).</summary>
        public long OpenSerial;

        /// <summary>The page list of PageListDisplay ([Page]MaxList) and its position.</summary>
        public readonly List<string> PageList = new List<string>();
        public int PageListPos = -1;

        /// <summary>The rubber band, null when none was started.</summary>
        public RubberBand? Rub;

        /// <summary>The last completed key command line (KeyReplay), empty when none.</summary>
        public readonly List<int> LastKeyLine = new List<int>();

        /// <summary>The keys the key command line holds: 32 two-byte codes (the
        /// runtime stores a key only while the count is below 0x20, a key past it is
        /// dropped and matches nothing).</summary>
        public const int KeyLineMax = 32;

        /// <summary>The window's display scale (PageInfo 16 / 17, the coordinate transform); 1 when
        /// not scaled.</summary>
        public double ScaleX = 1.0,
            ScaleY = 1.0;

        /// <summary>The output of an AN, null when none.</summary>
        public DrawCommand? DrawnAt(int an)
        {
            foreach (var c in Drawn)
                if (c.An == an)
                    return c;
            return null;
        }

        /// <summary>The window shows another page (or none): what Cicode drew and the page's own
        /// rubber band go.</summary>
        public void ResetForPage()
        {
            TitleSet = false;
            if (Drawn.Count > 0)
            {
                Drawn.Clear();
                DrawnSerial++;
            }
            Rub = null;
        }
    }

    public sealed partial class Window
    {
        /// <summary>WinTitle set the title (window +100): the page scan leaves it
        /// (the runtime formats the page title only while the flag is clear); displaying a page
        /// in the window clears it (the page display: +100 = 0 before).</summary>
        public bool TitleSet;
    }

    public sealed partial class AnObject
    {
        /// <summary>The tool tip was set by DspSetTip (it replaces the configured one).</summary>
        public bool TipSet;

        /// <summary>A dynamic AN (DspAnNew / DspAnNewRel), freed by DspAnFree.</summary>
        public bool Dynamic;

        /// <summary>KeyAllowCursor: the keyboard cursor may stop here without a keyboard command.</summary>
        public bool KeyCursorAllowed;

        /// <summary>May the command cursor stop on this object?</summary>
        public bool CursorAllowed(PageInstance? page) =>
            HasKeyCommand || HasTouch || KeyCursorAllowed || page?.Get(0)?.KeyCursorAllowed == true;

        /// <summary>DspAnMove / DspAnMoveRel offsets from the configured position.</summary>
        public int MovedX,
            MovedY;

        /// <summary>DspShow: false hides the object (null = the animation decides).</summary>
        public bool? ShownByCicode;

        /// <summary>DspGrayButton: the grey mode of a button (0 normal).</summary>
        public int Gray;

        /// <summary>The metadata entries Cicode set (DspAnSetMetadata, DspSetMetadataFromName,
        /// DspAnSetMetadataAt), by name without case: a drawing summary that arrives later
        /// (ctsim/pageModel) keeps them.</summary>
        public Dictionary<string, KeyValuePair<string, string>>? CicodeMeta;

        /// <summary>DspAnSetName renamed the object (a later drawing summary keeps the name).</summary>
        public bool NameByCicode;

        /// <summary>Cicode set a metadata entry.</summary>
        public void NoteCicodeMeta(string name, string value)
        {
            CicodeMeta ??= new Dictionary<string, KeyValuePair<string, string>>(
                StringComparer.OrdinalIgnoreCase
            );
            CicodeMeta[name] = new KeyValuePair<string, string>(name, value);
        }

        /// <summary>After a drawing summary replaced the metadata: what Cicode set wins (an entry
        /// the summary has takes Cicode's value; one it lacks is kept at the end).</summary>
        public void ReapplyCicodeMeta()
        {
            if (CicodeMeta == null)
                return;
            foreach (var kv in CicodeMeta.Values)
            {
                int k = Metadata.FindIndex(m =>
                    string.Equals(m.Key, kv.Key, StringComparison.OrdinalIgnoreCase)
                );
                if (k >= 0)
                    Metadata[k] = new KeyValuePair<string, string>(Metadata[k].Key, kv.Value);
                else
                    Metadata.Add(kv);
            }
        }
    }

    public sealed partial class PageInstance
    {
        /// <summary>The next dynamic AN (DspAnNew numbers from 31999 down: guess).</summary>
        public int NextDynamicAn = 31999;

        /// <summary>The page's drawing summary as its AN objects: size, environment, declared
        /// associations, and per AN the parent / children, name, type, extent, bounds, position,
        /// metadata, commands and tip. Null = a page without a drawing.</summary>
        public void Take(PageDrawing? drawing)
        {
            Drawing = drawing;
            if (drawing == null)
                return;
            Width = drawing.Width;
            Height = drawing.Height;
            Env.AddRange(drawing.Env);
            Associations.AddRange(drawing.Associations);
            foreach (int an in drawing.Order)
            {
                var d = drawing.Objects[an];
                var ao = Ensure(an);
                ao.Parent = d.Parent;
                ao.Children.AddRange(d.Children);
                ao.Name = d.Name;
                ao.Type = d.Type;
                if (d.Rect != null)
                    ao.Extent = d.Rect.Value;
                ao.Bounds = d.Bounds ?? ao.Extent;
                ao.PosX = ao.RawPosX = d.PosX ?? ao.Extent.L;
                ao.PosY = ao.RawPosY = d.PosY ?? ao.Extent.T;
                ao.Metadata.AddRange(d.Metadata);
                ao.HasKeyCommand = d.HasKeyCommand;
                ao.HasTouch = d.HasTouch;
                ao.Tip = d.Tip;
                ao.Drawing = d;
            }
            StructureChanged();
        }
    }

    public sealed partial class DisplayState
    {
        private HeadlessPages? _headless;
        private DisplayLanguage? _language;

        /// <summary>The display client's language (the '@(...)' texts it draws).</summary>
        public DisplayLanguage Language => _language ??= new DisplayLanguage(World);
        private long _openSerial;
        private List<FontInfo>? _fonts;

        public FontInfo? PopupMenuFont;

        /// <summary>The default font handle: the first font, -1 when none.</summary>
        public int DefaultFont => Fonts.Count > 0 ? 0 : -1;

        /// <summary>DspTipMode: 1 tool tips on (default on: guess), 0 off.</summary>
        public int TipMode = 1;

        /// <summary>The simulated screen: one 1920 x 1080 monitor, work area 1920 x 1040 (guess:
        /// a taskbar of 40 pixels).</summary>
        public readonly List<Monitor> Screen = new List<Monitor>
        {
            new Monitor
            {
                W = 1920,
                H = 1080,
                WorkW = 1920,
                WorkH = 1040,
            },
        };

        /// <summary>A page of a window is about to close (its page change, its window freed): the
        /// built-ins free what they attached to the page (Array*ByAn).</summary>
        public event Action<Window, PageInstance>? PageClosing;

        /// <summary>The pointer is on the screen at (x, y) (DspGetMouse outside a window); null
        /// when it is not known.</summary>
        public int? ScreenMouseX,
            ScreenMouseY;

        /// <summary>The page controller: the page animator, else the headless one (pages shown
        /// from their compiled records, no page code runs).</summary>
        public IPageController Pages => Controller ?? (_headless ??= new HeadlessPages(this));

        /// <summary>The page runtime of <see cref="Pages"/>.</summary>
        public IPageRuntime Runtime => Pages as IPageRuntime ?? (_headless ??= new HeadlessPages(this));

        /// <summary>Raise <see cref="PageClosing"/> (the animator and the headless controller call it).</summary>
        public void OnPageClosing(Window w, PageInstance p) => PageClosing?.Invoke(w, p);

        /// <summary>Stamp a newly opened window with the creation order.</summary>
        public void Opened(Window w) => w.OpenSerial = ++_openSerial;

        /// <summary>The open windows whose parent is <paramref name="parent"/>, in creation order
        /// (the runtime appends to the parent's child list).</summary>
        public List<Window> ChildrenOf(int parent)
        {
            var l = new List<Window>();
            foreach (var w in Windows)
                if (w.Open && w.Parent == parent && w.Number != parent)
                    l.Add(w);
            l.Sort(
                (a, b) =>
                    a.OpenSerial != b.OpenSerial
                        ? a.OpenSerial.CompareTo(b.OpenSerial)
                        : a.Number.CompareTo(b.Number)
            );
            return l;
        }

        /// <summary>The window's first child, -1 none.</summary>
        public int FirstChildOf(int window)
        {
            var c = ChildrenOf(window);
            return c.Count > 0 ? c[0].Number : -1;
        }

        /// <summary>The next child of the window's parent after it, -1 none.</summary>
        public int NextChildOf(int window)
        {
            var w = OpenWindow(window);
            if (w == null || w.Parent < 0)
                return -1;
            var c = ChildrenOf(w.Parent);
            int k = c.IndexOf(w);
            return k >= 0 && k + 1 < c.Count ? c[k + 1].Number : -1;
        }

        /// <summary>The number of open windows under <paramref name="window"/>.</summary>
        public int DescendantCount(int window, int depth = 0)
        {
            if (depth > 64)
                return 0;
            int n = 0;
            foreach (var c in ChildrenOf(window))
                n += 1 + DescendantCount(c.Number, depth + 1);
            return n;
        }

        /// <summary>The next open window after <paramref name="window"/>, cyclic; -1 when it is
        /// the only one.</summary>
        public int NextOpen(int window)
        {
            int n = Windows.Length;
            for (int k = 1; k < n; k++)
            {
                int i = ((window < 0 ? 0 : window) + k) % n;
                if (Windows[i].Open)
                    return i;
            }
            return -1;
        }

        /// <summary>The previous open window before <paramref name="window"/>, cyclic; -1 none.</summary>
        public int PrevOpen(int window)
        {
            int n = Windows.Length;
            for (int k = 1; k < n; k++)
            {
                int i = (((window < 0 ? 0 : window) - k) % n + n) % n;
                if (Windows[i].Open)
                    return i;
            }
            return -1;
        }

        /// <summary>Write a runtime message to the window's prompt line (the page
        /// shows it on AN [Page]Prompt).</summary>
        public void SetPrompt(int window, string text)
        {
            var w = Get(window);
            if (w != null)
                w.Prompt = text;
        }

        // ------------------------------------------------------------------ fonts
        /// <summary>The font table: fonts.dbf of the unit (in unit compile order, the first row
        /// of a name wins) and the fonts DspFont made.</summary>
        public List<FontInfo> Fonts
        {
            get
            {
                if (_fonts != null)
                    return _fonts;
                var l = new List<FontInfo>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (World != null)
                {
                    try
                    {
                        foreach (var (_, r) in World.Unit.Rows("fonts.dbf"))
                        {
                            string name = CitectText.Trim(r.Get("NAME"));
                            if (name.Length == 0 || !seen.Add(name) || l.Count >= 256)
                                continue;
                            // the colours are labels (BLACK, LIGHT_GREEN: Include labels.dbf) or
                            // numbers; no background = transparent (help 1095439); the flash
                            // colours (FGND_COLF / BGND_COLF) default to the steady ones
                            uint fg = Colour(World.Unit, r.Get("FGND_COLR"), 0);
                            uint bg = Colour(World.Unit, r.Get("BGND_COLR"), 0xFF000000u);
                            l.Add(
                                new FontInfo
                                {
                                    Handle = l.Count,
                                    Name = name,
                                    Type = CitectText.Trim(r.Get("TYPE")),
                                    Size = (int)Num(r.Get("SIZE")),
                                    FgOn = fg,
                                    BgOn = bg,
                                    FgOff = Colour(World.Unit, r.Get("FGND_COLF"), fg),
                                    BgOff = Colour(World.Unit, r.Get("BGND_COLF"), bg),
                                }
                            );
                        }
                    }
                    catch (Exception e)
                        when (e is System.IO.IOException
                            || e is FormatException
                            || e is InvalidOperationException
                        ) { }
                }
                _fonts = l;
                return l;
            }
        }

        /// <summary>A fonts.dbf colour field: a label or a number (0x00RRGGBB, 0xFF000000 =
        /// transparent), <paramref name="dflt"/> when empty or neither.</summary>
        private static uint Colour(Project.Unit unit, string text, uint dflt)
        {
            if (CitectText.Trim(text).Length == 0)
                return dflt;
            long? v = unit.LabelValue(text);
            return v.HasValue ? unchecked((uint)v.Value) : dflt;
        }

        private static long Num(string s)
        {
            s = CitectText.Trim(s);
            return long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v)
                ? v
                : 0;
        }

        /// <summary>DspFontHnd's lookup: a fonts.dbf font by NAME (without case: guess), -1 none.</summary>
        public int FontByName(string name)
        {
            name = CitectText.Trim(name);
            if (name.Length == 0)
                return -1;
            foreach (var f in Fonts)
                if (f.Name.Length > 0 && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                    return f.Handle;
            return -1;
        }

        /// <summary>DspFont: the handle of an equal font, else a new one; -1 when 256 are in use
        /// (the limit is the 8-bit handle DspText accepts).</summary>
        public int MakeFont(string type, int size, uint fgOn, uint bgOn, uint fgOff, uint bgOff)
        {
            var l = Fonts;
            foreach (var f in l)
                if (
                    string.Equals(f.Type, type, StringComparison.OrdinalIgnoreCase)
                    && f.Size == size
                    && f.FgOn == fgOn
                    && f.BgOn == bgOn
                    && f.FgOff == fgOff
                    && f.BgOff == bgOff
                    && f.Name.Length == 0
                )
                    return f.Handle;
            if (l.Count >= 256)
                return -1;
            var nf = new FontInfo
            {
                Handle = l.Count,
                Type = type,
                Size = size,
                FgOn = fgOn,
                BgOn = bgOn,
                FgOff = fgOff,
                BgOff = bgOff,
            };
            l.Add(nf);
            return nf.Handle;
        }

        public FontInfo? Font(int handle) => handle >= 0 && handle < Fonts.Count ? Fonts[handle] : null;

        // ------------------------------------------------------------------ drawing
        /// <summary>Record an AN's display output and tell the viewer.</summary>
        public void Draw(Window w, DrawCommand c)
        {
            int k = w.Drawn.FindIndex(x => x.An == c.An);
            if (k >= 0 && w.Drawn[k].Same(c))
                return;
            // a new output starts from the AN's own extent (DspSym / DspText set theirs after)
            var ao = w.Page?.Get(c.An);
            if (ao != null)
                CicodeExtents.Restore(ao);
            // another output on the AN replaces a genie DspSym placed there (DspSym itself
            // places or removes it before it draws)
            if (c.Kind != "sym" && k >= 0 && w.Drawn[k].Kind == "sym")
                (Controller as ISymbolPlacer)?.SymbolGone(w, c.An);
            w.DrawnSerial++;
            c.Serial = w.DrawnSerial;
            if (k >= 0)
                w.Drawn[k] = c;
            else
                w.Drawn.Add(c);
            Host?.DisplayOutput(w.Number, c.An, c.Kind, c.Text);
        }

        /// <summary>DspDel: the AN's output removed; false when it had none.</summary>
        public bool Undraw(Window w, int an)
        {
            var ao = w.Page?.Get(an);
            if (ao != null)
                CicodeExtents.Restore(ao);
            int k = w.Drawn.FindIndex(x => x.An == an);
            if (k < 0)
                return false;
            w.Drawn.RemoveAt(k);
            w.DrawnSerial++;
            (Controller as ISymbolPlacer)?.SymbolGone(w, an);
            Host?.DisplayOutput(w.Number, an, "del", "");
            return true;
        }
    }

    /// <summary>The page controller of a display client without a page animator.</summary>
    public sealed class HeadlessPages : IPageController, IPageRuntime
    {
        private readonly DisplayState _d;

        public HeadlessPages(DisplayState d)
        {
            _d = d;
        }

        public int Open(string page, int window, bool pushHistory)
        {
            page = CitectText.Trim(page ?? "");
            if (page.Length == 0)
                return 274;
            var w = _d.Get(window);
            if (w == null)
                return 274;
            PageRecord? rec = _d.Host?.FindPage(page, "") ?? _d.Catalogue?.Record(page);
            if (rec == null)
            {
                string p = _d.Catalogue?.RdbPath(page) ?? "";
                return p.Length > 0 && System.IO.File.Exists(p) ? 265 : 261;
            }
            if (!_d.AreaOk(rec.Areas))
                return 277;
            if (w.Page != null)
                Close(w);
            w.Open = true;
            var inst = new PageInstance(rec) { Name = rec.Name.Length > 0 ? rec.Name : page };
            inst.Take(_d.Host?.LoadDrawing(rec));
            CicodeExtents.StaticTexts(_d, inst);
            w.Page = inst;
            w.ScanTime = rec.ScanTime > 0 ? Math.Min(rec.ScanTime, 60000) : _d.Config.ScanTime;
            w.IgnoreValueQuality = rec.IgnoreVq >= 0 ? rec.IgnoreVq : _d.Config.IgnoreValueQuality;
            w.Shows = 0;
            w.ClearVars();
            w.KeyBuffer.Clear();
            w.CommandCursor = -1;
            w.Cluster = rec.Cluster;
            w.Title = rec.Title.Length > 0 ? rec.Title : inst.Name;
            w.ReadyCountdown = 0;
            w.ResetForPage();
            // the window's pending associations, then the page's declared defaults (the animator's
            // step 7)
            _d.ClaimForPage(w, inst.Associations);
            if (pushHistory)
            {
                if (w.HistoryPos < w.History.Count - 1)
                    w.History.RemoveRange(w.HistoryPos + 1, w.History.Count - w.HistoryPos - 1);
                w.History.Add(inst.Name);
                while (w.History.Count > Math.Max(_d.Config.MaxList, _d.Config.MaxLast))
                    w.History.RemoveAt(0);
                w.HistoryPos = w.History.Count - 1;
            }
            _d.Host?.Shown(w.Number, inst);
            _d.Host?.WindowChanged(w);
            return 0;
        }

        private void Close(Window w)
        {
            if (w.Page == null)
                return;
            _d.OnPageClosing(w, w.Page);
            // the page's trend objects and alarm lists go with it, as the animator's close does
            // (a world-run's AlarmDsp / TrnNew on a headless page made them)
            PageObjectTables.Of(_d).PageClosed(w.Number, w.Page.Name);
            foreach (var c in _d.Windows)
                if (c.Open && c.Parent == w.Number && (c.Mode & 1) != 0 && c.Number != w.Number)
                    FreeWindow(c.Number);
            w.Page = null;
            w.Associations.Clear();
            w.ResetForPage();
        }

        public int NewWindow(string page, int x, int y, int mode, int parent)
        {
            if (_d.OpenCount() >= _d.Windows.Length)
                return -1;
            int n = _d.FreeWindow();
            if (n < 0)
                return -1;
            var w = _d.Windows[n];
            w.Open = true;
            _d.Opened(w);
            w.X = x;
            w.Y = y;
            w.Mode = mode & 0x7fffffff;
            w.Parent = parent;
            w.History.Clear();
            w.HistoryPos = -1;
            int err = Open(page, n, true);
            if (err != 0)
            {
                w.Open = false;
                w.Parent = -1;
                return -1;
            }
            return n;
        }

        public int FreeWindow(int window)
        {
            var w = _d.Get(window);
            if (w == null || !w.Open)
                return 256;
            foreach (var c in _d.Windows)
                if (c.Open && c.Parent == window && c.Number != window)
                    FreeWindow(c.Number);
            Close(w);
            w.Open = window == 0 && _d.OpenCount() == 1;
            w.Parent = -1;
            w.Mode = 0;
            w.History.Clear();
            w.HistoryPos = -1;
            _d.Host?.WindowChanged(w);
            return 0;
        }

        public int History(int window, int delta)
        {
            var w = _d.OpenWindow(window);
            if (w == null)
                return 269;
            int pos = w.HistoryPos + delta;
            if (pos < 0 || pos >= w.History.Count)
                return 274;
            int err = Open(w.History[pos], window, false);
            if (err == 0)
                w.HistoryPos = pos;
            return err;
        }

        public bool KeyPress(int window, int code)
        {
            var w = _d.OpenWindow(window);
            if (w == null || w.KeyBuffer.Count >= Window.KeyLineMax)
                return false;
            w.KeyBuffer.Add(code);
            return false;
        }

        public void AssociationsChanged(int window) { }

        public AnMotion? Motion(int window, int an) => null;

        public List<AnExpression>? Expressions(int window, int an) => null;
    }
}
