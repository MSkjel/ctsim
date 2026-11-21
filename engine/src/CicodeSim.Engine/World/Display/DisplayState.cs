using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;

// World.Display: the display client of one
// simulated process: windows, the page shown in each, page variables, the animation points of the
// shown page with their names / extents / metadata, the keyboard cursor, the mouse and the super
// genie associations. The page animator (CicodeSim.Page) runs pages into it; the display
// built-ins read it. The headless default is one open window 0 with no page.
namespace CicodeSim.World.Display
{
    /// <summary>One animation point of the shown page (AnObject).</summary>
    public sealed partial class AnObject
    {
        public int An;

        /// <summary>The parent's AN, 0 for a top-level object (after a change call the page's
        /// PageInstance.StructureChanged: its children index).</summary>
        public int Parent;
        public List<int> Children = new List<int>();
        public string Name = "";
        public int Type;
        public Rect Extent;
        public Rect Bounds;
        public int PosX,
            PosY,
            RawPosX,
            RawPosY;

        /// <summary>The current animation state: hidden.</summary>
        public bool Hidden;
        public List<KeyValuePair<string, string>> Metadata = new List<KeyValuePair<string, string>>();
        public bool HasKeyCommand,
            HasTouch;
        public string Tip = "";

        /// <summary>The page's drawing summary of this AN, null when the page has none.</summary>
        public DrawObject? Drawing;

        /// <summary>The metadata value of a name (case-insensitive, guess: the
        /// runtime lowers the key), null when none.</summary>
        public string? MetadataValue(string name)
        {
            foreach (var kv in Metadata)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            return null;
        }
    }

    /// <summary>A page shown in a window.</summary>
    public sealed partial class PageInstance
    {
        public string Name = "";
        public PageRecord Record;
        public int Width,
            Height;
        public Dictionary<int, AnObject> Objects = new Dictionary<int, AnObject>();

        /// <summary>Page object order (record order).</summary>
        public List<int> Order = new List<int>();

        /// <summary>The page environment (DspGetEnv: ASCII letters without case).</summary>
        public List<KeyValuePair<string, string>> Env = new List<KeyValuePair<string, string>>();
        public List<PageAssociation> Associations = new List<PageAssociation>();
        public int Cluster = -1;
        public PageDrawing? Drawing;

        /// <summary>The animator's runtime of this page (CicodeSim.Page).</summary>
        public object? Runtime;

        public PageInstance(PageRecord record)
        {
            Record = record;
            Name = record.Name;
        }

        public AnObject? Get(int an) => Objects.TryGetValue(an, out var o) ? o : null;

        public AnObject Ensure(int an)
        {
            if (!Objects.TryGetValue(an, out var o))
            {
                o = new AnObject { An = an };
                Objects[an] = o;
                Order.Add(an);
                _structure++;
            }
            return o;
        }
    }

    /// <summary>A super genie association: Ass(hWin, arg, tag, mode, cluster).</summary>
    public sealed class AssEntry
    {
        /// <summary>A window number, -2 = the next new window or page displayed.</summary>
        public int Target;
        public string Arg = "";
        internal string? Equipment; // expanded using the destination page's cluster

        /// <summary>The tag reference without its cluster prefix (Equip.Item allowed, an
        /// equipment name for a partial association), or the literal's text.</summary>
        public string Tag = "";

        /// <summary>The cluster the store resolved (the given one, the 'Cluster.' prefix,
        /// or the tag configuration's: THE cluster of a one-cluster project), "" none.</summary>
        public string Cluster = "";
        public int Mode;
        public bool Literal;

        /// <summary>0 or the error the store's tag check left (424 an unknown name, 289 a
        /// dotted name that is no tag or Equip.Item, an equipment name included; 403 / 404 / 417
        /// cluster errors). The association is stored and works all the same (AssRules).</summary>
        public int Error;

        /// <summary>The page's declared default was used (the caller gave none).</summary>
        public bool UsedDefault;

        /// <summary>The page's value on error replaced it (AssGetProperty "ErrorValUsed").</summary>
        public bool ErrorValueUsed;

        /// <summary>The page association's value on error, "" none.</summary>
        public string OnError = "";

        /// <summary>The cluster came from the caller's default: AssChain
        /// copies the entry without it.</summary>
        public bool ClusterDefaulted;

        public AssEntry Copy(int target) =>
            new AssEntry
            {
                Target = target,
                Arg = Arg,
                Tag = Tag,
                Cluster = Cluster,
                Mode = Mode,
                Literal = Literal,
                Error = Error,
                UsedDefault = UsedDefault,
                ErrorValueUsed = ErrorValueUsed,
                OnError = OnError,
                ClusterDefaulted = ClusterDefaulted,
            };
    }

    /// <summary>A KeySetSeq command: the sequence, the AN (0 = the page's), the function
    /// handle it calls and the page instance it was added on.</summary>
    public sealed class RuntimeKey
    {
        public string Sequence = "";
        public int An;
        public long Function = -1;
        public PageInstance? Page;
    }

    /// <summary>One window.</summary>
    public sealed partial class Window
    {
        public int Number;

        /// <summary>Slot state: false free, true open.</summary>
        public bool Open;
        public PageInstance? Page;
        public int Parent = -1,
            FirstChild = -1,
            NextChild = -1;

        /// <summary>WinNewAt mode &amp; 0x7fffffff.</summary>
        public int Mode;
        public int X,
            Y,
            W,
            H;
        public string Title = "";

        /// <summary>WinSetName's name, "" when none.</summary>
        public string Name = "";

        /// <summary>PageSetInt / PageGetInt: std::map of std::string, case-sensitive.</summary>
        public Dictionary<string, long> IntVars = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>PageSetStr / PageGetStr.</summary>
        public Dictionary<string, string> StrVars = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>The page variables the window's code used since its page showed, first
        /// use first (a simulator view: the runtime keeps no such list). Str = PageGetStr /
        /// PageSetStr, else PageGetInt / PageSetInt.</summary>
        public readonly List<(string Name, bool Str)> VarNames = new List<(string, bool)>();
        private readonly HashSet<string> _varSeen = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Page variables the simulator's user locked ('i:' / 's:' + name): PageSetInt /
        /// PageSetStr leave them as they are (a simulator-only feature; cleared with the page).</summary>
        public readonly HashSet<string> LockedVars = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The last page state serial of a page shown in this window (PageRun): the
        /// next page shown here goes on from it, so the serials of a window only grow (a
        /// viewer compares a pageChanged with a pageState by serial, across page displays).</summary>
        public long PageSerial;

        public static string VarKey(string name, bool str) => (str ? "s:" : "i:") + name;

        /// <summary>Increases when a page variable is first used, changes or the list clears.</summary>
        public long VarSerial;

        /// <summary>A page variable was read or written (<paramref name="changed"/>: its value).</summary>
        public void NoteVar(string name, bool str, bool changed)
        {
            if (_varSeen.Add((str ? "s:" : "i:") + name))
            {
                VarNames.Add((name, str));
                VarSerial++;
            }
            else if (changed)
                VarSerial++;
        }

        /// <summary>The page variables cleared (the window shows another page).</summary>
        public void ClearVars()
        {
            IntVars.Clear();
            StrVars.Clear();
            VarNames.Clear();
            _varSeen.Clear();
            LockedVars.Clear();
            VarSerial++;
        }

        /// <summary>[{name, type 'int' | 'string', value}] in first-use order.</summary>
        public JsonValue VarsJson()
        {
            var arr = JsonValue.NewArray();
            foreach (var (name, str) in VarNames)
            {
                var o = JsonValue.NewObject();
                o.Set("name", name);
                o.Set("type", str ? "string" : "int");
                if (str)
                    o.Set("value", StrVars.TryGetValue(name, out var s) ? s : "");
                else
                    o.Set("value", JsonValue.Of(IntVars.TryGetValue(name, out long v) ? v : 0));
                if (LockedVars.Contains(VarKey(name, str)))
                    o.Set("locked", JsonValue.True);
                arr.Add(o);
            }
            return arr;
        }

        /// <summary>The page scan period, ms.</summary>
        public int ScanTime = 250;

        /// <summary>The page navigation history and the position in it.</summary>
        public List<string> History = new List<string>();
        public int HistoryPos = -1;

        /// <summary>The active associations of the shown page, by argument name (case-insensitive,
        /// guess).</summary>
        public Dictionary<string, AssEntry> Associations = new Dictionary<string, AssEntry>(
            StringComparer.OrdinalIgnoreCase
        );

        /// <summary>The keyboard command cursor AN, -1 none.</summary>
        public int CommandCursor = -1;

        /// <summary>The key command line (typed key codes).</summary>
        public List<int> KeyBuffer = new List<int>();

        /// <summary>KeySetSeq: the run-time key commands added while a page is shown (they
        /// answer only while that page instance is).</summary>
        public List<RuntimeKey> RuntimeKeys = new List<RuntimeKey>();

        /// <summary>The mouse in window client pixels, (-1, -1) outside.</summary>
        public int MouseX = -1,
            MouseY = -1;
        public string Prompt = "";

        /// <summary>Page scans since the page was shown (PageInfo 7).</summary>
        public long Shows;

        /// <summary>[Page]ReadyCicodeDelay countdown; PageInfo(25) = countdown == 0.</summary>
        public int ReadyCountdown;

        /// <summary>The window is inside its page scan / communication cycle.</summary>
        public bool InAnimation,
            InComms;

        /// <summary>The quality indication mode of the shown page: 0 #COM, 1 none, 2 background.</summary>
        public int IgnoreValueQuality;
        public bool Active;
        public string Cluster = "";

        public bool MouseInside => MouseX >= 0 && MouseY >= 0;
    }

    /// <summary>The [Page] / [Animator] parameters the display client reads at start
    /// (defaults: the runtime's own).</summary>
    public sealed class DisplayConfig
    {
        public int ScanTime = 250;
        public int KeyEcho = 1;
        public int Prompt = 2;
        public int MaxLast = 10;
        public int MaxList = 25;
        public int TipTimeOut = 1000;
        public bool RangeCheck;

        /// <summary>The runtime reads 0 (push 0, clamp 0..32); doc 1118047 says 5
        /// (verify).</summary>
        public int MaxRecursion;
        public int ReadyCicodeDelay = 2;
        public bool WaitForValidData = true;
        public int IgnoreValueQuality;
        public bool ShowBadText = true,
            ShowUncertainText = true,
            ShowErrorText = true;
        public string BadText = "#BAD",
            UncertainText = "#UNC";
        public int FlashTime = 500;
        public int AnmDelay = 500;
        public int Windows = 50;

        /// <summary>[Keyboard] argument separator (default ',').</summary>
        public int KeySeparator = ',';
        public bool EnableQualityToolTip = true;

        private static int Clamp(int v, int lo, int hi) =>
            v < lo ? lo
            : v > hi ? hi
            : v;

        /// <summary>From parameters: get(section, key) -&gt; the text or null.</summary>
        public static DisplayConfig From(Func<string, string, string?> get)
        {
            var c = new DisplayConfig();
            int Num(string sec, string key, int d)
            {
                string? s = get(sec, key);
                if (s == null)
                    return d;
                s = CitectText.Trim(s);
                return int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v)
                    ? v
                    : d;
            }
            string Text(string key, string dflt)
            {
                string? s = get("Page", key);
                if (s == null)
                    return dflt;
                s = CitectText.Trim(s);
                // "" -> the default; else '#' + text (BadText / UncertainText)
                return s.Length == 0 ? dflt : "#" + s;
            }
            c.ScanTime = Clamp(Num("Page", "ScanTime", c.ScanTime), 1, 60000);
            c.KeyEcho = Num("Page", "KeyEcho", c.KeyEcho);
            c.Prompt = Num("Page", "Prompt", c.Prompt);
            c.MaxLast = Clamp(Num("Page", "MaxLast", c.MaxLast), 1, 500);
            c.MaxList = Clamp(Num("Page", "MaxList", c.MaxList), 1, 500);
            c.TipTimeOut = Clamp(Num("Page", "TipTimeOut", c.TipTimeOut), 200, 20000);
            c.RangeCheck = Num("Page", "RangeCheck", 0) != 0;
            c.MaxRecursion = Clamp(Num("Page", "MaxRecursion", c.MaxRecursion), 0, 32);
            c.ReadyCicodeDelay = Clamp(Num("Page", "ReadyCicodeDelay", c.ReadyCicodeDelay), 1, 255);
            c.WaitForValidData = Num("Page", "WaitForValidData", 1) != 0;
            c.IgnoreValueQuality = Num("Page", "IgnoreValueQuality", 0);
            c.ShowBadText = Num("Page", "ShowBadText", 1) != 0;
            c.ShowUncertainText = Num("Page", "ShowUncertainText", 1) != 0;
            c.ShowErrorText = Num("Page", "ShowErrorText", 1) != 0;
            c.BadText = Text("BadText", c.BadText);
            c.UncertainText = Text("UncertainText", c.UncertainText);
            c.FlashTime = Clamp(Num("Animator", "FlashTime", c.FlashTime), 100, 3000);
            c.AnmDelay = Clamp(Num("Page", "AnmDelay", c.AnmDelay), 100, 10000);
            c.Windows = Clamp(Num("Page", "Windows", c.Windows), 1, 200);
            c.EnableQualityToolTip = Num("Page", "EnableQualityToolTip", 1) != 0;
            return c;
        }
    }

    /// <summary>What a viewer (ctgedit) or the engine's own reader supplies.
    /// Every member has a default, so a host implements only what it has.</summary>
    public interface IDisplayHost
    {
        /// <summary>A page the catalogue does not hold (an unsaved page), null for the catalogue's.</summary>
        PageRecord? FindPage(string name, string cluster);

        /// <summary>The page's drawing summary (from its .ctg / .ctF), null when none.</summary>
        PageDrawing? LoadDrawing(PageRecord page);

        /// <summary>A page was displayed in a window.</summary>
        void Shown(int window, PageInstance page);

        /// <summary>A display output on an AN (DspStr, DspText, DspSym, DspDel ...).</summary>
        void DisplayOutput(int window, int an, string kind, string text);

        /// <summary>A window was opened, freed, moved or retitled.</summary>
        void WindowChanged(Window w);
    }

    /// <summary>What the display built-ins ask the page animator for (page changes, windows).</summary>
    public interface IPageController
    {
        /// <summary>Show a page in a window (PageDisplay / PageGoto core): 0 or the error.</summary>
        int Open(string page, int window, bool pushHistory);

        /// <summary>A page in a new window (WinNewAt core): the window, -1 when none.</summary>
        int NewWindow(string page, int x, int y, int mode, int parent);

        /// <summary>Free a window (WinFree core): 0 or the error.</summary>
        int FreeWindow(int window);

        /// <summary>Move in the page history (PageBack / PageForward): 0 or the error.</summary>
        int History(int window, int delta);
    }

    /// <summary>The task-side context of the display client: the window and the AN of a Cicode
    /// task, kept in CicodeTask.Data so every package sees them.</summary>
    public static class TaskContext
    {
        public const string WindowKey = "display.window";
        public const string AnKey = "display.an";

        /// <summary>The task's window, else <paramref name="dflt"/> (the headless default: 0).</summary>
        public static int Window(CicodeTask t, int dflt = 0) =>
            t.Data.TryGetValue(WindowKey, out var v) && v is int w ? w : dflt;

        /// <summary>The AN of the graphics object whose code runs, -1 none.</summary>
        public static int An(CicodeTask t) => t.Data.TryGetValue(AnKey, out var v) && v is int a ? a : -1;

        public static void Set(CicodeTask t, int window, int an)
        {
            t.Data[WindowKey] = window;
            t.Data[AnKey] = an;
        }
    }

    /// <summary>The display client of one simulated process (Display).</summary>
    public sealed partial class DisplayState
    {
        public World? World { get; }
        public DisplayConfig Config { get; set; }

        /// <summary>This process shows windows: true in the simulator.</summary>
        public bool DisplayClient = true;
        public Window[] Windows { get; private set; }
        public Dictionary<string, int> WindowNames = new Dictionary<string, int>(StringComparer.Ordinal);
        public int Focus;

        /// <summary>The global flash phase, 0 / 1 (PageInfo 18 = phase + 1).</summary>
        public int FlashPhase;

        /// <summary>Associations waiting for a page (target -2 or a window number).</summary>
        public List<AssEntry> Pending = new List<AssEntry>();
        public PageCatalogue? Catalogue;
        public IDisplayHost? Host;

        /// <summary>The window a task without one runs in (the headless default 0).</summary>
        public int DefaultWindow;

        /// <summary>The page animator that shows pages in the windows (CicodeSim.Page.Animator),
        /// null without one (the headless default: no page ever shows).</summary>
        public IPageController? Controller;

        /// <summary>A page was shown in a window and joined its page list (DisplayPages.NoteShown:
        /// openPage, a scenario's page step, PageDisplay and the other Cicode page changes).</summary>
        public event Action<Window>? PageShown;

        internal void RaisePageShown(Window w) => PageShown?.Invoke(w);

        private static readonly ConditionalWeakTable<World, DisplayState> Of_ =
            new ConditionalWeakTable<World, DisplayState>();

        /// <summary>The display client of a World (one per World, made on first use).</summary>
        public static DisplayState Of(World world) => Of_.GetValue(world, w => new DisplayState(w));

        public DisplayState(World? world = null, DisplayConfig? config = null)
        {
            World = world;
            Config = config
                ?? (world != null ? DisplayConfig.From((s, k) => world.Param(s, k)) : new DisplayConfig());
            Windows = new Window[Config.Windows];
            for (int i = 0; i < Windows.Length; i++)
                Windows[i] = new Window { Number = i, ScanTime = Config.ScanTime };
            // the headless default: window 0 open, no page (guess: the runtime always has window 0
            // once its startup page shows)
            Windows[0].Open = true;
            if (world != null)
            {
                string dir = world.Unit.RootDir;
                Catalogue = new PageCatalogue(dir);
            }
        }

        /// <summary>The window, null when the number is outside the table.</summary>
        public Window? Get(int n) => n >= 0 && n < Windows.Length ? Windows[n] : null;

        /// <summary>The window if it is open.</summary>
        public Window? OpenWindow(int n)
        {
            var w = Get(n);
            return w != null && w.Open ? w : null;
        }

        public int WindowOf(CicodeTask t) => TaskContext.Window(t, DefaultWindow);

        /// <summary>The page object an AN names in a window (-2 = the task's AN), null when none.</summary>
        public AnObject? Object(int window, int an) => OpenWindow(window)?.Page?.Get(an);

        /// <summary>The lowest free window, -1 when all are in use.</summary>
        public int FreeWindow()
        {
            for (int i = 0; i < Windows.Length; i++)
                if (!Windows[i].Open)
                    return i;
            return -1;
        }

        public int OpenCount()
        {
            int n = 0;
            foreach (var w in Windows)
                if (w.Open)
                    n++;
            return n;
        }

        // ------------------------------------------------------------------ access
        /// <summary>The area (area &amp; 0xff) is viewable: area 0 always.</summary>
        public bool AreaOk(int area)
        {
            area &= 0xff;
            if (area == 0 || World == null)
                return true;
            return World.Users.CanView(area);
        }

        /// <summary>The user holds privilege <paramref name="priv"/> in the area (0 = none needed).</summary>
        public bool PrivOk(int priv, int area)
        {
            if (priv <= 0 || World == null)
                return true;
            return World.Users.GetPriv(priv, area & 0xff) != 0;
        }

        // ------------------------------------------------------------------ associations
        /// <summary>Ass(): store an association for the target's next page.</summary>
        public int Ass(
            int target,
            string arg,
            string tag,
            int mode,
            string cluster = "",
            int callerWindow = 0,
            bool emptyRemoves = false,
            string onError = ""
        )
        {
            tag ??= "";
            if (tag.Length >= 255)
                return 557;
            arg = CitectText.Trim(arg ?? "");
            if (arg.Length > 254)
                arg = arg.Substring(0, 254);
            if (target == -3)
                target = callerWindow;
            int at = PendingIndex(target, arg);
            if (tag.Length == 0 && (onError ?? "").Length == 0)
            {
                if (emptyRemoves && at >= 0)
                    Pending.RemoveAt(at);
                return 0;
            }
            var e = new AssEntry
            {
                Target = target,
                Arg = arg,
                Mode = mode,
                OnError = onError ?? "",
            };
            if (tag.Length > 0 && tag[0] == '\'')
            {
                // a literal: the text without its quotes (at most 254 characters; a closing quote
                // removed)
                string x = tag.Substring(1);
                if (x.Length > 254)
                    x = x.Substring(0, 254);
                if (x.EndsWith("'", StringComparison.Ordinal))
                    x = x.Substring(0, x.Length - 1);
                e.Literal = true;
                e.Tag = x;
            }
            else
            {
                e.Error = AssRules.StoreCheck(World, tag, cluster ?? "", out string cl, out string kept);
                e.Cluster = cl;
                e.Tag = kept.Length > 254 ? kept.Substring(0, 254) : kept;
            }
            if (at >= 0)
                Pending.RemoveAt(at);
            Pending.Add(e);
            return e.Error;
        }

        /// <summary>The pending entry of a name for a target or for -2, -1 none.</summary>
        public int PendingIndex(int target, string arg)
        {
            for (int i = 0; i < Pending.Count; i++)
            {
                var p = Pending[i];
                if (
                    p.Equipment == null
                    && (p.Target == target || p.Target == -2)
                    && string.Equals(p.Arg, arg, StringComparison.OrdinalIgnoreCase)
                )
                    return i;
            }
            return -1;
        }

        private void ExpandEquipmentAssociations(Window w)
        {
            if (
                World == null
                || !Pending.Exists(p => p.Equipment != null && (p.Target == w.Number || p.Target == -2))
            )
                return;
            var requests = Pending;
            Pending = new List<AssEntry>();
            foreach (var p in requests)
            {
                if (p.Equipment == null || (p.Target != w.Number && p.Target != -2))
                {
                    Pending.Add(p);
                    continue;
                }
                string cluster = p.Cluster.Length == 0 ? w.Cluster : p.Cluster;
                if (cluster.Length == 0)
                {
                    var clusters = AssRules.Clusters(World);
                    if (clusters.Count == 1)
                        cluster = clusters[0];
                }
                foreach (var row in World.Equipment.RuntimeParameters(p.Equipment, cluster))
                {
                    string name = row.Get("NAME"),
                        value = row.Get("VALUE");
                    string isTag = row.Get("ISTAG");
                    if (string.Equals(isTag, "FALSE", StringComparison.OrdinalIgnoreCase) || isTag == "0")
                        Pending.Add(
                            new AssEntry
                            {
                                Target = w.Number,
                                Arg = name,
                                Tag = value,
                                Literal = true,
                            }
                        );
                    else
                        Ass(w.Number, name, value, 0, cluster, w.Number);
                }
            }
        }

        /// <summary>Move the pending associations a page shown in window <paramref name="w"/> takes
        /// (its own window's and the -2 ones) into the window; a later Ass
        /// of the same argument wins (guess on the -2 / window precedence).</summary>
        public void ClaimAssociations(Window w)
        {
            w.Associations.Clear();
            var keep = new List<AssEntry>();
            foreach (var p in Pending)
            {
                if (p.Target == w.Number || p.Target == -2)
                    w.Associations[p.Arg] = p.Copy(w.Number);
                else
                    keep.Add(p);
            }
            Pending = keep;
        }

        /// <summary>A page shown in a window takes its associations.</summary>
        public void ClaimForPage(Window w, IEnumerable<PageAssociation>? declared)
        {
            ExpandEquipmentAssociations(w);
            var defaults = new List<string>();
            if (declared != null)
                foreach (var pa in declared)
                {
                    if (pa.Name.Length == 0 || PendingIndex(w.Number, CitectText.Trim(pa.Name)) >= 0)
                        continue;
                    if (pa.Default.Length == 0 && pa.OnError.Length == 0)
                        continue;
                    Ass(w.Number, pa.Name, pa.Default, 0, w.Cluster, w.Number, onError: pa.OnError);
                    defaults.Add(CitectText.Trim(pa.Name));
                }
            ClaimAssociations(w);
            foreach (string n in defaults)
                if (w.Associations.TryGetValue(n, out var e))
                    e.UsedDefault = true;
        }

        /// <summary>The window has an association waiting.</summary>
        public bool HasPending(int window)
        {
            foreach (var p in Pending)
                if (p.Target == window || p.Target == -2)
                    return true;
            return false;
        }

        /// <summary>The active association a reference text names in a window (the name between its first and last '?', AssRules.NameOf; ASCII letters without case), null when none.
        /// The name is not trimmed and keeps a type word: the compiler writes '?INT 1?' as '?1?'
        /// into a page's TAGREFs, and the runtime does not strip one from a Cicode argument.</summary>
        public static AssEntry? Find(Window w, string token)
        {
            string name = AssName(token);
            if (name.Length == 0)
                return null;
            return w.Associations.TryGetValue(name, out var e) ? e : null;
        }

        /// <summary>The association name of a reference text (AssRules.NameOf).</summary>
        public static string AssName(string token) => AssRules.NameOf(token ?? "");
    }
}
