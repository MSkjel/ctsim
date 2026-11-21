using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CicodeSim.Compiler.Codegen;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Page;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.World.Display;

namespace CicodeSim.Sim
{
    /// <summary>The pages of a simulation.</summary>
    public sealed class SimPages : IDisplayHost, IDisposable
    {
        private readonly Simulation _sim;
        private Animator? _animator;

        /// <summary>Drawing summaries given for pages, by lower-case page name.</summary>
        private readonly Dictionary<string, PageModel> _models = new Dictionary<string, PageModel>(
            StringComparer.Ordinal
        );

        /// <summary>Pages compiled from rows, by lower-case page name (they win over the .RDB).</summary>
        private readonly Dictionary<string, CompiledPage> _compiled = new Dictionary<string, CompiledPage>(
            StringComparer.Ordinal
        );
        private int _logsSeen;
        private string _windowsSent = "";
        private bool _windowsQueued;

        /// <summary>The source compiler of page rows: built on first use, again after a reload
        /// (guarded by <see cref="_unitLock"/>; any thread).</summary>
        private CompileUnit? _unit;
        private TagTable? _tags;
        private readonly object _unitLock = new object();

        /// <summary>A page's states changed: the pageChanged body (event thread).</summary>
        public event Action<JsonValue>? PageChanged;

        /// <summary>The windows changed: the windowsChanged body (event thread).</summary>
        public event Action<JsonValue>? WindowsChanged;

        public SimPages(Simulation sim)
        {
            _sim = sim;
        }

        /// <summary>The animator, null until a page was asked for.</summary>
        public Animator? Animator => _animator;

        public DisplayState Display => _sim.World.Display;

        private DisplayJson? _json;

        /// <summary>How a viewer's page state is written: the display strings localised, fonts
        /// with their fontInfo.</summary>
        public DisplayJson Json => _json ??= new DisplayJson(Display);

        private static string Key(string page) => CitectText.Lower(CitectText.Trim(page ?? ""));

        /// <summary>The animator over the simulation's VM (made on first use).</summary>
        public Animator EnsureAnimator()
        {
            if (_animator != null)
                return _animator;
            var a = new Animator(_sim.World, _sim.Vm, _sim.Scheduler);
            a.CompiledSource = name => _compiled.TryGetValue(Key(name), out var cp) ? cp : FromRows(name);
            a.PageChanged += OnPageChanged;
            a.WantDrawing = WantDrawing;
            // page code the debugger stops holds the simulation there (ForegroundCanBreak)
            a.ForegroundStopped = _sim.WaitForegroundStop;
            // a held page that starts in window 0 runs a waiting 'startup auto' (RuntimeStartup)
            a.Released += w => _sim.Boot.PageShown(w);
            Display.Host = this;
            _animator = a;
            return a;
        }

        // ------------------------------------------------------------------ drawings Cicode needs
        /// <summary>A viewer that sends drawings is connected (hello {models: true}): a page
        /// Cicode displays with no drawing summary waits for one (ctsim/modelWanted).</summary>
        public Func<bool>? ModelViewers;

        /// <summary>{window, page, path}: a viewer should send ctsim/pageModel for the window.</summary>
        public event Action<JsonValue>? ModelWanted;

        /// <summary>How long a held page waits for its drawing before starting without one.</summary>
        public int ModelWaitMs = 20000;

        private bool WantDrawing(int window, PageInstance page)
        {
            if (ModelViewers == null || !ModelViewers())
                return false;
            var b = JsonValue.NewObject();
            b.Set("window", JsonValue.Of(window));
            b.Set("page", page.Name);
            b.Set("path", DrawingFile(page.Name) ?? "");
            _sim.Events.Post(() => _sim.Events.Each(ModelWanted, h => ((Action<JsonValue>)h)(b)));
            // the timer is kept in _holdTimers until it fired: an unreferenced Timer can be
            // collected before it fires, and the page would then wait for ever
            System.Threading.Timer? timer = null;
            timer = new System.Threading.Timer(
                _ =>
                {
                    lock (_holdTimers)
                        if (timer != null)
                            _holdTimers.Remove(timer);
                    timer?.Dispose();
                    try
                    {
                        _sim.Post(() =>
                        {
                            if (_animator != null && _animator.Release(window, page))
                            {
                                _sim.Note(
                                    "info",
                                    "page "
                                        + page.Name
                                        + " (window "
                                        + window.ToString(CultureInfo.InvariantCulture)
                                        + "): no drawing came in "
                                        + ModelWaitMs.ToString(CultureInfo.InvariantCulture)
                                        + " ms, it starts without one"
                                );
                                DrainLogs();
                            }
                        });
                    }
                    catch (ObjectDisposedException) { }
                },
                null,
                System.Threading.Timeout.Infinite,
                System.Threading.Timeout.Infinite
            );
            lock (_holdTimers)
                _holdTimers.Add(timer);
            timer.Change(ModelWaitMs, System.Threading.Timeout.Infinite);
            return true;
        }

        private readonly HashSet<System.Threading.Timer> _holdTimers = new HashSet<System.Threading.Timer>();

        public void Dispose()
        {
            lock (_holdTimers)
            {
                foreach (var t in _holdTimers)
                    t.Dispose();
                _holdTimers.Clear();
            }
            _animator?.Dispose();
            _animator = null;
        }

        // ------------------------------------------------------------------ compiling rows (any thread)
        /// <summary>The source compiler is out of date (a reload changed the library's code).</summary>
        public void LibraryChanged()
        {
            lock (_unitLock)
            {
                _unit = null;
                _tags = null;
            }
            _noRows.Clear();
        }

        /// <summary>Pages with neither a compiled file nor project rows (asked once).</summary>
        private readonly HashSet<string> _noRows = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Compile a page from its rows when it has no .RDB.</summary>
        private CompiledPage? FromRows(string page)
        {
            string k = Key(page);
            if (k.Length == 0 || _noRows.Contains(k))
                return null;
            if (RdbOf(page) != null)
                return null;
            var notes = new List<string>();
            CompiledPage? cp;
            try
            {
                cp = CompileRows(page, null, notes);
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                _sim.Note(
                    "warning",
                    "page " + page + " is not compiled and its project rows did not compile: " + e.Message
                );
                cp = null;
            }
            if (cp == null)
            {
                _noRows.Add(k);
                return null;
            }
            _compiled[k] = cp;
            _sim.Note(
                "info",
                "page "
                    + page
                    + " is not compiled: it runs from its project rows ("
                    + (notes.Count > 0 ? notes[0] : "")
                    + ")"
            );
            return cp;
        }

        /// <summary>Why a page cannot be displayed, in words (the log of a failed PageDisplay).</summary>
        public string WhyNot(string page, int err)
        {
            string name = CitectText.Trim(page ?? "");
            string root = Path.GetFileName(
                _sim.World.Unit.RootDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            );
            switch (err)
            {
                case 261:
                {
                    string? ctg = null;
                    foreach (var p in _sim.World.Unit.Search)
                    {
                        string c = Path.Combine(p.Dir, name + ".ctg");
                        if (File.Exists(c))
                        {
                            ctg = c;
                            break;
                        }
                    }
                    if (ctg == null)
                        return "there is no page '" + name + "' in " + root + " or the projects it includes";
                    return "the page file "
                        + ctg
                        + " exists, but the page was never saved into its project (it is not in "
                        + "pages.DBF and has no compiled .RDB), so there is nothing to run. Open it in ctgedit and save it, "
                        + "then display it again";
                }
                case 265:
                    return "the compiled page "
                        + (RdbOf(name) ?? name + ".RDB")
                        + " has no Control record (an older or damaged "
                        + "file): compile the project again";
                case 277:
                    return "the page belongs to an area the logged-in user may not view (choose User: Full access to see "
                        + "every area)";
                case 274:
                    return name.Length == 0 ? "no page name was given" : "the window is not open";
                case 318:
                    return "too many page changes inside each other (a page's entry code displays another page, and so on)";
                case 517:
                    return "a page cannot be changed from its own window's scan (an expression or Cicode object of the page "
                        + "itself)";
                default:
                    return "error " + err.ToString(CultureInfo.InvariantCulture);
            }
        }

        private (CompileUnit, TagTable) Unit()
        {
            lock (_unitLock)
            {
                if (_unit == null || _tags == null)
                {
                    string? user = _sim.UserDir.Length > 0 ? _sim.UserDir : null;
                    var unit = Units.ForProject(_sim.World.Unit.RootDir, user).Compile();
                    _unit = unit;
                    _tags = TagTable.For(unit);
                }
                return (_unit, _tags);
            }
        }

        /// <summary>The running program's source compiler when it compiles the unit
        /// <see cref="Unit"/> would (the simulation's root and user folder), else null (compile
        /// 'none', or every function from the RDB). Simulation thread.</summary>
        private SourceLibrary? SharedSource()
        {
            var live = _sim.Live;
            var src = live?.Source;
            // a reload that did not compile left the unit ahead of the code that runs
            if (live == null || src == null || NoSharedSource || !live.SourceInStep)
                return null;
            string root = _sim.World.Unit.RootDir;
            string mine = EffectiveUser(root, _sim.UserDir.Length > 0 ? _sim.UserDir : null);
            return WinPath.Key(EffectiveUser(root, live.UserDir)) == WinPath.Key(mine) ? src : null;
        }

        /// <summary>Test hook: compile page rows against a unit of their own.</summary>
        internal bool NoSharedSource;

        /// <summary>Test hook: the last page rows compiled against the running program's unit.</summary>
        internal bool LastRowsShared;

        /// <summary>The user folder <see cref="Units.ForProject"/> takes for a project folder.</summary>
        private static string EffectiveUser(string root, string? user)
        {
            if (user != null)
                return user;
            if (!WinPath.IsDir(root))
                return Units.DefaultUser;
            string? parent = WinPath.DirName(WinPath.Clean(WinPath.Full(root)).TrimEnd('\\', '/'));
            return parent != null && Unit0.Find(parent, "master.dbf") != null ? parent : Units.DefaultUser;
        }

        /// <summary>Compile a page from its rows.</summary>
        public CompiledPage? CompileRows(string page, PageModel? model, List<string> notes)
        {
            IReadOnlyDictionary<string, List<Dictionary<string, string>>> tables;
            IReadOnlyDictionary<string, string>? control;
            string project = "";
            if (model != null && model.Tables.Count > 0)
            {
                tables = model.Tables;
                control = model.Control;
                project = model.Project;
            }
            else
            {
                Dictionary<string, List<Dictionary<string, string>>>? found = null;
                Dictionary<string, string>? ctl = null;
                foreach (var p in _sim.World.Unit.Search)
                {
                    var (t, c) = PageRowCompiler.ProjectRows(p.Dir, page);
                    if (c == null && t.Count == 0)
                        continue;
                    found = t;
                    ctl = c;
                    project = p.Name;
                    break;
                }
                if (found == null)
                    return null;
                tables = found;
                control = ctl;
            }
            var world = _sim.World;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // the running program's source unit when it has one (the source compiler kept warm:
            // compiling a big unit again takes seconds, the page itself milliseconds); it is read
            // and edited on the simulation thread, so the page compiles there too
            var cp = _sim.Invoke(() =>
            {
                var src = SharedSource();
                return src == null
                    ? null
                    : PageRowCompiler.Compile(
                        src.Unit,
                        src.Tags,
                        page,
                        tables,
                        control,
                        t => world.Unit.LabelValue(t),
                        notes,
                        project
                    );
            });
            LastRowsShared = cp != null;
            if (cp == null)
            {
                var (unit, tags) = Unit();
                cp = PageRowCompiler.Compile(
                    unit,
                    tags,
                    page,
                    tables,
                    control,
                    t => world.Unit.LabelValue(t),
                    notes,
                    project
                );
            }
            notes.Insert(
                0,
                "compiled from its rows: "
                    + Count(cp).ToString(CultureInfo.InvariantCulture)
                    + " records in "
                    + sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)
                    + " ms"
            );
            return cp;
        }

        private static int Count(CompiledPage cp)
        {
            int n = 0;
            foreach (var t in cp.Tables.Values)
                n += t.Count;
            return n;
        }

        /// <summary>Test hook: pages to treat as not compiled (their .RDB is not looked at).</summary>
        internal Func<string, bool>? HideRdb;

        /// <summary>The page's .RDB in the unit (the catalogue's, else a project folder's), null.</summary>
        public string? RdbOf(string page)
        {
            if (HideRdb != null && HideRdb(page))
                return null;
            string? p = Display.Catalogue?.RdbPath(page);
            if (!string.IsNullOrEmpty(p) && File.Exists(p))
                return p;
            return PageTags.FindRdb(_sim.World.Unit, page);
        }

        /// <summary>The tag names a page compiled from rows reads (null: no such page here).</summary>
        public List<string>? CompiledTagNames(string page) =>
            _compiled.TryGetValue(Key(page), out var cp) ? PageTags.Names(cp.Program) : null;

        private readonly Dictionary<string, (DateTime Time, long Size, CompiledPage Page)> _rdbPages =
            new Dictionary<string, (DateTime, long, CompiledPage)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The code a page runs here: the page compiled from rows, else its .RDB (loaded
        /// once per file version); null when it has neither.</summary>
        public CompiledPage? CompiledFor(string page)
        {
            if (_compiled.TryGetValue(Key(page), out var cp))
                return cp;
            string? rdb = RdbOf(page);
            if (rdb == null)
                return null;
            var fi = new FileInfo(rdb);
            if (
                _rdbPages.TryGetValue(rdb, out var had)
                && had.Time == fi.LastWriteTimeUtc
                && had.Size == fi.Length
            )
                return had.Page;
            var loaded = CompiledPage.Load(rdb);
            if (_rdbPages.Count > 16)
                _rdbPages.Clear();
            _rdbPages[rdb] = (fi.LastWriteTimeUtc, fi.Length, loaded);
            return loaded;
        }

        /// <summary>The page a window shows and the code it runs there, null when the window shows
        /// no page.</summary>
        public (Window Window, CompiledPage Page)? WindowPage(int window)
        {
            var run = _animator?.Run(window);
            if (run == null)
                return null;
            return (run.Window, run.Compiled);
        }

        // ------------------------------------------------------------------ requests
        /// <summary>ctsim/openPage: show a page in a window.</summary>
        public JsonValue Open(
            string name,
            int? window,
            PageModel? model,
            CompiledPage? compiled,
            JsonValue? associations,
            string source,
            List<string> notes
        )
        {
            var a = EnsureAnimator();
            string key = Key(name);
            if (model != null)
            {
                _models[key] = model;
                if (model.Page.Length > 0)
                    _models[Key(model.Page)] = model;
            }
            if (compiled != null)
                _compiled[key] = compiled;
            else if (source == "rdb")
                _compiled.Remove(key);
            int n;
            if (window == null)
            {
                n = Display.Windows[0].Page == null ? 0 : Display.FreeWindow();
                if (n < 0)
                    throw new InvalidOperationException(
                        "no free window (all "
                            + Display.Windows.Length.ToString(CultureInfo.InvariantCulture)
                            + " are open)"
                    );
            }
            else
                n = window.Value;
            var w = Display.Get(n)
                ?? throw new ArgumentException(
                    "no window "
                        + n.ToString(CultureInfo.InvariantCulture)
                        + " (0 to "
                        + (Display.Windows.Length - 1).ToString(CultureInfo.InvariantCulture)
                        + ")"
                );
            bool fresh = !w.Open;
            if (fresh)
            {
                w.Open = true;
                w.X = w.Y = 0;
                w.Mode = 0;
                w.Parent = -1;
                w.History.Clear();
                w.HistoryPos = -1;
            }
            if (associations != null && associations.Kind == JsonKind.Object)
                foreach (var kv in associations.Pairs)
                    Display.Ass(n, kv.Key, kv.Value.IsNull ? "" : kv.Value.Text, 0, "", n);
            int err = a.Open(name, n, model, true, compiled);
            if (err != 0 && fresh)
            {
                w.Open = n == 0;
                w.Parent = -1;
            }
            if (err == 0)
            {
                // the window's page list, as a page Cicode displays joins it (PageListCurrent)
                CicodeSim.Builtins.DisplayPages.NoteShown(Display, w); // also runs a waiting startup (Boot.PageShown)
            }
            QueueWindows();
            DrainLogs();
            var b = JsonValue.NewObject();
            b.Set("window", JsonValue.Of(n));
            b.Set("page", w.Page?.Name ?? name);
            b.Set("error", JsonValue.Of(err));
            if (err != 0)
                b.Set("errorText", OpenError(err));
            b.Set("source", source);
            var nj = JsonValue.NewArray();
            for (int i = 0; i < notes.Count && i < 50; i++)
                nj.Add(JsonValue.Of(notes[i]));
            if (notes.Count > 50)
                nj.Add(
                    JsonValue.Of("... " + (notes.Count - 50).ToString(CultureInfo.InvariantCulture) + " more")
                );
            b.Set("notes", nj);
            if (err == 0)
                b.Set("state", StateJson(n));
            return b;
        }

        /// <summary>Keep a page's model for the next time Cicode displays it.</summary>
        public void Prime(string name, PageModel? model, CompiledPage? compiled)
        {
            EnsureAnimator();
            string key = Key(name);
            if (model != null)
            {
                _models[key] = model;
                if (model.Page.Length > 0)
                    _models[Key(model.Page)] = model;
            }
            if (compiled != null)
                _compiled[key] = compiled;
        }

        /// <summary>The page is one this simulation can show: a compiled .RDB, rows compiled
        /// here, or rows in a project of the unit.</summary>
        public bool PageExists(string name)
        {
            if (CitectText.Trim(name).Length == 0)
                return false;
            if (_compiled.ContainsKey(Key(name)) || RdbOf(name) != null)
                return true;
            foreach (var p in _sim.World.Unit.Search)
            {
                var (t, c) = PageRowCompiler.ProjectRows(p.Dir, name);
                if (c != null || t.Count > 0)
                    return true;
            }
            return false;
        }

        /// <summary>Show a page the way Cicode's PageDisplay does.</summary>
        public JsonValue ShowAsCicode(string name, int window)
        {
            var a = EnsureAnimator();
            var w = Display.Get(window)
                ?? throw new ArgumentException("no window " + window.ToString(CultureInfo.InvariantCulture));
            bool fresh = !w.Open;
            if (fresh)
            {
                w.Open = true;
                w.X = w.Y = 0;
                w.Mode = 0;
                w.Parent = -1;
                w.History.Clear();
                w.HistoryPos = -1;
            }
            int err = ((CicodeSim.World.Display.IPageController)a).Open(name, window, true);
            if (err != 0 && fresh)
            {
                w.Open = window == 0;
                w.Parent = -1;
            }
            if (err == 0)
                CicodeSim.Builtins.DisplayPages.NoteShown(Display, w);
            QueueWindows();
            DrainLogs();
            var b = JsonValue.NewObject();
            b.Set("window", JsonValue.Of(window));
            b.Set("page", w.Page?.Name ?? name);
            b.Set("error", JsonValue.Of(err));
            if (err != 0)
                b.Set("errorText", OpenError(err));
            var run = a.Run(window);
            b.Set("held", JsonValue.Of(err == 0 && run != null && run.PendingStart != null));
            return b;
        }

        /// <summary>The windows that show <paramref name="name"/> now (ascending).</summary>
        public List<int> WindowsShowing(string name)
        {
            var o = new List<int>();
            string k = Key(name).TrimStart('!');
            foreach (var w in Display.Windows)
                if (w.Open && w.Page != null && Key(w.Page.Name).TrimStart('!') == k)
                    o.Add(w.Number);
            return o;
        }

        /// <summary>The text of a page open error (Animator.Open's codes).</summary>
        public static string OpenError(int err) =>
            err switch
            {
                261 => "the page is not compiled and has no rows (no .RDB, no pg*.DBF rows)",
                265 => "the page's compiled file has no Control record",
                274 => "no page name",
                277 => "the page is not in the user's area",
                318 => "page changes nested too deeply",
                393 => "the page and its compiled file do not match",
                517 => "the window is in its own scan",
                _ => CicodeSim.Vm.Errors.Text(err),
            };

        /// <summary>ctsim/closePage: the page closes (its events: _PageStop, OnEvent 5 then 9,
        /// EXIT_FUNC); a window other than 0 is freed (its window children with it).</summary>
        public JsonValue Close(int window)
        {
            var b = JsonValue.NewObject();
            b.Set("window", JsonValue.Of(window));
            var a = _animator;
            var w = Display.Get(window);
            if (a == null || w == null || !w.Open)
            {
                b.Set("closed", JsonValue.False);
                return b;
            }
            string page = w.Page?.Name ?? "";
            if (window == 0)
                a.Close(0);
            else
                a.FreeWindow(window);
            QueueWindows();
            DrainLogs();
            b.Set("closed", JsonValue.True);
            b.Set("page", page);
            return b;
        }

        /// <summary>ctsim/pageState: every object state of the window's page (PAGE).</summary>
        public JsonValue StateJson(int window)
        {
            var st = _animator?.State(window);
            if (st != null)
                return st.ToJson(null, Json);
            var w = Display.Get(window);
            var b = JsonValue.NewObject();
            b.Set("window", JsonValue.Of(window));
            b.Set("page", w?.Page?.Name ?? "");
            b.Set("serial", JsonValue.Of(0));
            b.Set("flashOn", JsonValue.Of(Display.FlashPhase == 0));
            b.Set("objects", JsonValue.NewArray());
            return b;
        }

        /// <summary>ctsim/pageVars: {window, page, vars: [{name, type, value}]}, the page
        /// variables the window's code used since its page showed.</summary>
        public JsonValue PageVarsJson(int window)
        {
            var w = Display.Get(window);
            var b = JsonValue.NewObject();
            b.Set("window", JsonValue.Of(window));
            b.Set("page", w?.Page?.Name ?? "");
            b.Set("vars", w != null && w.Open ? w.VarsJson() : JsonValue.NewArray());
            return b;
        }

        /// <summary>ctsim/setPageVar: set a page variable.</summary>
        public JsonValue SetPageVar(int window, JsonValue a)
        {
            var w = Display.Get(window);
            if (w == null || !w.Open || w.Page == null)
                throw new ArgumentException(
                    "window " + window.ToString(CultureInfo.InvariantCulture) + " shows no page"
                );
            var nv = a.Get("name");
            string name = nv != null && nv.Kind == JsonKind.String ? nv.Str : "";
            if (name.Length == 0)
                throw new ArgumentException("setPageVar needs a name");
            var v = a.Get("value") ?? throw new ArgumentException("setPageVar needs a value");
            var tv = a.Get("type");
            string type = tv != null && tv.Kind == JsonKind.String ? tv.Str : "";
            if (type.Length == 0)
            {
                foreach (var (n, str) in w.VarNames)
                    if (n == name)
                    {
                        type = str ? "string" : "int";
                        break;
                    }
            }
            string text = v.Kind == JsonKind.String ? v.Str : v.ToString();
            var lv = a.Get("locked");
            bool? lockIt = lv != null && lv.Kind == JsonKind.Bool ? lv.Bool : (bool?)null;
            if (type.Length == 0)
                type = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                    ? "int"
                    : "string";
            if (type == "int")
            {
                long n;
                if (v.IsNumber)
                    n = v.FitsLong ? v.Long : (long)v.Double;
                else if (
                    !long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)
                )
                    throw new ArgumentException("'" + text + "' is not a whole number");
                int iv = (int)n; // PageSetInt stores an INT
                bool changed = !w.IntVars.TryGetValue(name, out long old) || old != iv;
                w.IntVars[name] = iv;
                w.NoteVar(name, false, changed);
            }
            else if (type == "string")
            {
                bool changed = !w.StrVars.TryGetValue(name, out string? old) || old != text;
                w.StrVars[name] = text;
                w.NoteVar(name, true, changed);
            }
            else
                throw new ArgumentException("type must be 'int' or 'string'");
            if (lockIt != null)
            {
                string key = Window.VarKey(name, type == "string");
                bool had = w.LockedVars.Contains(key);
                if (lockIt.Value)
                    w.LockedVars.Add(key);
                else
                    w.LockedVars.Remove(key);
                if (had != lockIt.Value)
                    w.VarSerial++;
            }
            return PageVarsJson(window);
        }

        /// <summary>ctsim/setObjectProperty: write a property of an ActiveX control.</summary>
        public JsonValue SetObjectProperty(int window, JsonValue a)
        {
            var anim = EnsureAnimator();
            var w = Display.Get(window);
            if (w == null || !w.Open || w.Page == null)
                throw new ArgumentException(
                    "window " + window.ToString(CultureInfo.InvariantCulture) + " shows no page"
                );
            int an = Num(a, "an") is double d && d > 0 ? (int)d : 0;
            string prop = a.Get("property")?.Text ?? "";
            var v = a.Get("value") ?? throw new ArgumentException("setObjectProperty needs a value");
            string text = v.Kind == JsonKind.String ? v.Str : v.ToString();
            if (an <= 0 || prop.Trim().Length == 0)
                throw new ArgumentException("setObjectProperty needs an an and a property");
            if (anim.Host == null)
                throw new InvalidOperationException("no display client");
            bool? changed = CicodeSim.Builtins.Outside.SetControlProperty(anim.Host, w, an, prop, text);
            if (changed == null)
                throw new ArgumentException(
                    "AN "
                        + an.ToString(CultureInfo.InvariantCulture)
                        + " is no ActiveX control of "
                        + w.Page.Name
                );
            var b = JsonValue.NewObject();
            b.Set("window", JsonValue.Of(window));
            b.Set("an", JsonValue.Of(an));
            b.Set("property", prop.Trim());
            b.Set("value", text);
            b.Set("changed", JsonValue.Of(changed.Value));
            return b;
        }

        /// <summary>ctsim/pageModel: the drawing summary of a window's page.</summary>
        public JsonValue AttachModel(int window, PageModel model)
        {
            var b = JsonValue.NewObject();
            b.Set("window", JsonValue.Of(window));
            var w = Display.OpenWindow(window);
            var inst = w?.Page;
            if (inst == null)
                throw new InvalidOperationException(
                    "window " + window.ToString(CultureInfo.InvariantCulture) + " shows no page"
                );
            if (model.Page.Length > 0 && Key(model.Page) != Key(inst.Name))
                throw new ArgumentException(
                    "window "
                        + window.ToString(CultureInfo.InvariantCulture)
                        + " shows "
                        + inst.Name
                        + ", not "
                        + model.Page
                );
            _models[Key(inst.Name)] = model;
            Take(inst, model.Drawing);
            if (inst.Runtime is PageRun run)
            {
                foreach (var o in run.Objects)
                {
                    o.Drawing = model.Drawing.Get(o.An);
                    if (o.Type == 0 && o.Drawing != null)
                        o.Type = o.Drawing.Type;
                }
                // the genies DspSym placed keep their members (the drawing has none of them)
                run.KeepSymbols();
            }
            // a page Cicode displayed waits for this drawing: it starts now
            bool started = _animator != null && _animator.Release(window, inst);
            QueueWindows();
            DrainLogs();
            b.Set("page", inst.Name);
            b.Set("objects", JsonValue.Of(model.Drawing.Objects.Count));
            if (started)
                b.Set("started", JsonValue.True);
            return b;
        }

        /// <summary>(ctsim/pageModel {window, none: true}) The viewer has no drawing for the page
        /// window shows (its file is not there): a page held for one starts at once, without it,
        /// instead of after ModelWaitMs.</summary>
        public JsonValue NoModel(int window)
        {
            var b = JsonValue.NewObject();
            b.Set("window", JsonValue.Of(window));
            var inst = Display.OpenWindow(window)?.Page;
            if (inst == null)
                throw new InvalidOperationException(
                    "window " + window.ToString(CultureInfo.InvariantCulture) + " shows no page"
                );
            bool started = _animator != null && _animator.Release(window, inst);
            if (started)
                _sim.Note(
                    "info",
                    "page "
                        + inst.Name
                        + " (window "
                        + window.ToString(CultureInfo.InvariantCulture)
                        + "): the viewer has no drawing for it, it starts without one"
                );
            QueueWindows();
            DrainLogs();
            b.Set("page", inst.Name);
            if (started)
                b.Set("started", JsonValue.True);
            return b;
        }

        /// <summary>A page instance takes a drawing summary (what Animator.Open does with the one
        /// it gets): size, environment, associations declared, and per AN the extent, bounds,
        /// position, name, metadata and tool tip.</summary>
        internal static void Take(PageInstance inst, PageDrawing drawing)
        {
            inst.Drawing = drawing;
            inst.Width = drawing.Width;
            inst.Height = drawing.Height;
            if (inst.Env.Count == 0)
                inst.Env.AddRange(drawing.Env);
            if (inst.Associations.Count == 0)
                inst.Associations.AddRange(drawing.Associations);
            foreach (int an in drawing.Order)
            {
                var d = drawing.Objects[an];
                var ao = inst.Ensure(an);
                ao.Parent = d.Parent;
                ao.Children.Clear();
                ao.Children.AddRange(d.Children);
                // what Cicode set on the page (DspAnSetName, DspAnSetMetadata ...) wins over a
                // drawing summary that arrives after it
                if (d.Name.Length > 0 && !ao.NameByCicode)
                    ao.Name = d.Name;
                if (d.Type != 0)
                    ao.Type = d.Type;
                if (d.Rect != null)
                    ao.Extent = d.Rect.Value;
                ao.Bounds = d.Bounds ?? ao.Extent;
                ao.PosX = ao.RawPosX = d.PosX ?? ao.Extent.L;
                ao.PosY = ao.RawPosY = d.PosY ?? ao.Extent.T;
                ao.Metadata.Clear();
                ao.Metadata.AddRange(d.Metadata);
                ao.ReapplyCicodeMeta();
                ao.HasKeyCommand |= d.HasKeyCommand;
                ao.HasTouch |= d.HasTouch;
                if (d.Tip.Length > 0 && !ao.TipSet)
                    ao.Tip = d.Tip;
                ao.Drawing = d;
            }
            // the parents changed: the children index of the name lookups (an AN the page
            // already had, e.g. a second model, adds no object, so Ensure does not mark it)
            inst.StructureChanged();
        }

        // ------------------------------------------------------------------ input
        /// <summary>What a pointer input does to a button Cicode drew.</summary>
        public static Animator.TouchKind DrawnButtonKind(string kind) =>
            kind == "up" ? Animator.TouchKind.Up
            : kind == "click" ? Animator.TouchKind.DoubleClick
            : Animator.TouchKind.Down;

        /// <summary>ctsim/input: pointer and key input on a window's page.</summary>
        public JsonValue Input(int window, JsonValue a)
        {
            var anim = EnsureAnimator();
            string kind = a.Get("kind")?.Str ?? "";
            var run = anim.Run(window);
            if (run == null)
                throw new InvalidOperationException(
                    "window " + window.ToString(CultureInfo.InvariantCulture) + " shows no page"
                );
            var ans = new List<int>();
            var av = a.Get("ans");
            if (av != null && av.Kind == JsonKind.Array)
                foreach (var x in av.Items)
                    if (x.IsNumber && x.ToInteger() > 0)
                        ans.Add((int)x.ToInteger());
            int an0 = Num(a, "an") is double d0 && d0 > 0 ? (int)d0 : 0;
            if (an0 > 0 && !ans.Contains(an0))
                ans.Insert(0, an0);
            double? x0 = Num(a, "x"),
                y0 = Num(a, "y");
            int button = Button(a.Get("button")?.Text ?? "");
            int modBits = MouseModifiers((int)(Num(a, "modifiers") ?? 0));
            bool ran = false;
            int target = an0;
            switch (kind)
            {
                case "move":
                    anim.Mouse(
                        window,
                        x0 != null ? (int)Math.Floor(x0.Value) : -1,
                        y0 != null ? (int)Math.Floor(y0.Value) : -1
                    );
                    break;
                case "down":
                case "up":
                case "click":
                case "dblclick":
                case "double":
                {
                    if (x0 != null && y0 != null)
                        anim.Mouse(window, (int)Math.Floor(x0.Value), (int)Math.Floor(y0.Value));
                    // a release goes the left way whatever the keys held then (the press holds
                    // the mouse: the slider it grabbed is let go, the repeat ends)
                    if (button != MouseLeft || (modBits != 0 && kind != "up"))
                    {
                        ran = OtherButton(anim, run, window, ans, an0, kind, button, modBits, out target);
                        break;
                    }
                    target = Pick(run, ans, an0);
                    if (target <= 0)
                        break;
                    // a button Cicode drew (DspButton / DspButtonFn) takes the press itself; a
                    // double click is a press (Windows: down, up, double click, up: the second
                    // release comes as its own up), a click a press and a release
                    if (!run.Touches.ContainsKey(target) && Animator.DrawnButton(run, target) != null)
                    {
                        if (kind == "up" && _press is (int bw, _, _, _) && bw == window)
                            _press = null;
                        ran = anim.PressDrawnButton(window, target, DrawnButtonKind(kind));
                        break;
                    }
                    if (kind == "down")
                    {
                        _press = Slider(run, ans) is int s
                            ? (window, s, x0 ?? 0, y0 ?? 0)
                            : ((int, int, double, double)?)null;
                        ran = anim.Touch(window, target, Animator.TouchKind.Down);
                    }
                    else if (kind == "up")
                    {
                        if (_press is (int pw, int pan, double px, double py) && pw == window)
                        {
                            DragTo(
                                anim,
                                run,
                                window,
                                pan,
                                px,
                                py,
                                x0,
                                y0,
                                Num(a, "dx"),
                                Num(a, "dy"),
                                release: true
                            );
                            _press = null;
                        }
                        ran = anim.Touch(window, target, Animator.TouchKind.Up);
                    }
                    else if (kind == "click")
                    {
                        ran = anim.Touch(window, target, Animator.TouchKind.Down);
                        ran |= anim.Touch(window, target, Animator.TouchKind.Up);
                    }
                    else
                        ran = anim.Touch(window, target, Animator.TouchKind.DoubleClick);
                    break;
                }
                case "drag":
                {
                    int sl;
                    double px,
                        py;
                    if (button != MouseLeft)
                    {
                        sl = 0; // only the left button drags a slider
                        px = py = 0;
                    }
                    else if (_press is (int pw, int pan, double ppx, double ppy) && pw == window)
                    {
                        sl = pan;
                        px = ppx;
                        py = ppy;
                    }
                    else
                    {
                        sl = Slider(run, ans) ?? 0;
                        px = (x0 ?? 0) - (Num(a, "dx") ?? 0);
                        py = (y0 ?? 0) - (Num(a, "dy") ?? 0);
                    }
                    target = sl;
                    if (sl > 0)
                        ran = DragTo(
                            anim,
                            run,
                            window,
                            sl,
                            px,
                            py,
                            x0,
                            y0,
                            Num(a, "dx"),
                            Num(a, "dy"),
                            release: a.Get("release")?.Kind == JsonKind.Bool && a.Get("release")!.Bool
                        );
                    if (x0 != null && y0 != null)
                        anim.Mouse(window, (int)Math.Floor(x0.Value), (int)Math.Floor(y0.Value));
                    break;
                }
                case "slider":
                {
                    // the slider of the object set to a percent (value 0 to 100) as a drag released there
                    target = Slider(run, ans) ?? 0;
                    double pct = Num(a, "value")
                        ?? throw new ArgumentException("input slider needs a value (a percent, 0 to 100)");
                    var o = target > 0 ? run.Object(target) : null;
                    if (o == null)
                        throw new ArgumentException(
                            "no slider on AN " + an0.ToString(CultureInfo.InvariantCulture)
                        );
                    double magX = o.Drawing?.MagnitudeX ?? 1.0,
                        magY = o.Drawing?.MagnitudeY ?? 1.0;
                    double? dx = o.SliderX ? Formulas.MoveX(pct, o.MvxMin, o.MvxMax) * magX : (double?)null;
                    double? dy = o.SliderY ? Formulas.MoveY(pct, o.MvyMin, o.MvyMax) * magY : (double?)null;
                    double? ang = o.SliderRot
                        ? Formulas.RotateDegrees(pct, o.RotMin, o.RotMax)
                        : (double?)null;
                    ran = anim.Drag(window, target, dx, dy, ang, release: true);
                    break;
                }
                case "key":
                {
                    int code = KeyCode(
                        anim,
                        a.Get("key")?.Text ?? "",
                        a.Get("text")?.Text ?? "",
                        (int)(Num(a, "modifiers") ?? 0)
                    );
                    if (code < 0)
                        throw new ArgumentException(
                            "unknown key "
                                + CitectText.Quote(a.Get("key")?.Text ?? "")
                                + " (a keys.dbf name, a KEY_ label, one character or a code)"
                        );
                    if (an0 > 0)
                        run.Window.CommandCursor = Pick(run, ans, an0, keys: true);
                    ran = anim.KeyCode(window, code);
                    target = run.Window.CommandCursor;
                    break;
                }
                default:
                    throw new ArgumentException(
                        "input kind must be down, up, click, dblclick, key, move, drag or slider: "
                            + CitectText.Quote(kind)
                    );
            }
            DrainLogs();
            var b = JsonValue.NewObject();
            b.Set("ran", JsonValue.Of(ran));
            b.Set("an", JsonValue.Of(target));
            b.Set("kind", kind == "double" ? "dblclick" : kind);
            return b;
        }

        public const int MouseLeft = 1,
            MouseRight = 2,
            MouseMiddle = 4;

        /// <summary>The input's <c>button</c>: left (also absent or empty), right, middle.</summary>
        private static int Button(string text)
        {
            string b = CitectText.Lower(CitectText.Trim(text));
            if (b.Length == 0 || b == "left")
                return MouseLeft;
            if (b == "right")
                return MouseRight;
            if (b == "middle")
                return MouseMiddle;
            throw new ArgumentException(
                "input button must be left, right or middle: " + CitectText.Quote(text)
            );
        }

        /// <summary>The key code bits of the held keys on a mouse event: Shift 0x1000, Ctrl 0x2000
        /// (labels.dbf KEY_LBTN_CMD_DNS 0x1605 = KEY_LBTN_CMD_DN | 0x1000, _DNC | 0x2000); the
        /// input's modifiers 1 Shift, 2 Ctrl (4 Alt has no key code bit).</summary>
        private static int MouseModifiers(int modifiers) =>
            ((modifiers & 1) != 0 ? 0x1000 : 0) | ((modifiers & 2) != 0 ? 0x2000 : 0);

        /// <summary>The command cursor's mouse key code of a button: KEY_LBTN_CMD_DN / _UP / _DBL,
        /// KEY_RBTN_CMD_*, KEY_MBTN_CMD_* (labels.dbf of the unit, else Include's values).</summary>
        private static int MouseKeyCode(Animator anim, int button, string what)
        {
            string b =
                button == MouseRight ? "R"
                : button == MouseMiddle ? "M"
                : "L";
            long? v = anim.World.Unit.LabelValue("KEY_" + b + "BTN_CMD_" + what);
            if (v != null)
                return (int)v.Value;
            int k =
                button == MouseRight ? 1
                : button == MouseMiddle ? 3
                : 0;
            switch (what)
            {
                case "DN":
                    return new[] { 0x0605, 0x0606, 0, 0x0608 }[k];
                case "UP":
                    return new[] { 0x0601, 0x0602, 0, 0x0604 }[k];
                default:
                    return new[] { 0x0603, 0x0607, 0, 0x0609 }[k];
            }
        }

        /// <summary>A pointer event that isn't a plain left button.</summary>
        private bool OtherButton(
            Animator anim,
            PageRun run,
            int window,
            List<int> ans,
            int an0,
            string kind,
            int button,
            int modBits,
            out int target
        )
        {
            if (kind == "down")
                _press = null;
            target =
                an0 > 0 ? an0
                : ans.Count > 0 ? ans[0]
                : 0;
            int t = 0;
            foreach (int an in ans)
                if (run.Keys.Exists(k => k.An == an))
                {
                    t = an;
                    break;
                }
            bool ran = false;
            int Plain(string what)
            {
                bool r = button == MouseRight,
                    m = button == MouseMiddle;
                int c =
                    what == "DN"
                        ? (
                            r ? KeyTable.RButtonDown
                            : m ? KeyTable.MButtonDown
                            : KeyTable.LButtonDown
                        )
                    : what == "UP"
                        ? (
                            r ? KeyTable.RButtonUp
                            : m ? KeyTable.MButtonUp
                            : KeyTable.LButtonUp
                        )
                    : (
                        r ? KeyTable.RButtonDbl
                        : m ? KeyTable.MButtonDbl
                        : KeyTable.LButtonDbl
                    );
                return c | modBits;
            }
            bool Key(string what) =>
                t > 0
                    ? anim.MouseKeys(window, MouseKeyCode(anim, button, what) | modBits, Plain(what))
                    : anim.PageMouseKey(window, Plain(what)); // the page's and the project's commands
            if (t > 0)
            {
                target = t;
                run.Window.CommandCursor = t;
            }
            if (kind == "down" || kind == "click")
                ran |= Key("DN");
            if (kind == "up" || kind == "click")
                ran |= Key("UP");
            if (kind == "dblclick" || kind == "double")
                ran |= Key("DBL");
            return ran;
        }

        /// <summary>The press a drag belongs to: (window, slider AN, x, y), null when the press
        /// was on no slider.</summary>
        private (int, int, double, double)? _press;

        private static double? Num(JsonValue a, string key)
        {
            var v = a.Get(key);
            return v != null && v.IsNumber ? v.ToNumber() : (double?)null;
        }

        /// <summary>The object of the chain that takes the pointer: a touch command, a button, a
        /// slider, or (keys) a keyboard command; else the innermost AN.</summary>
        private static int Pick(PageRun run, List<int> ans, int fallback, bool keys = false)
        {
            foreach (int an in ans)
            {
                if (run.Touches.ContainsKey(an))
                    return an;
                if (!keys && Animator.DrawnButton(run, an) != null)
                    return an;
                var o = run.Object(an);
                if (o != null && (o.Table == "PGBUTTON" || o.SliderX || o.SliderY || o.SliderRot))
                    return an;
                if (keys && run.Keys.Exists(k => k.An == an))
                    return an;
            }
            if (!keys)
                foreach (int an in ans)
                    if (run.Keys.Exists(k => k.An == an))
                        return an;
            return fallback > 0 ? fallback
                : ans.Count > 0 ? ans[0]
                : 0;
        }

        private static int? Slider(PageRun run, List<int> ans)
        {
            foreach (int an in ans)
            {
                var o = run.Object(an);
                if (o != null && (o.SliderX || o.SliderY || o.SliderRot))
                    return an;
            }
            return null;
        }

        /// <summary>A slider dragged to the pointer: the offset from the press (dx, dy, else the
        /// pointer minus the press), a rotational slider by the angle the pointer turned about
        /// the object's rotation centre (degrees clockwise).</summary>
        private static bool DragTo(
            Animator anim,
            PageRun run,
            int window,
            int an,
            double px,
            double py,
            double? x,
            double? y,
            double? dx,
            double? dy,
            bool release
        )
        {
            var o = run.Object(an);
            if (o == null)
                return false;
            double ddx = dx ?? (x != null ? x.Value - px : 0.0),
                ddy = dy ?? (y != null ? y.Value - py : 0.0);
            double? ang = null;
            if (o.SliderRot && o.State.RotateOrigin is double[] org && org.Length >= 2)
            {
                double cx = org[0],
                    cy = org[1];
                double a0 = Math.Atan2(py - cy, px - cx),
                    a1 = Math.Atan2(py + ddy - cy, px + ddx - cx);
                double deg = (a1 - a0) * 180.0 / Math.PI;
                while (deg > 180)
                    deg -= 360;
                while (deg < -180)
                    deg += 360;
                ang = deg;
            }
            return anim.Drag(
                window,
                an,
                o.SliderX ? ddx : (double?)null,
                o.SliderY ? ddy : (double?)null,
                ang,
                release
            );
        }

        /// <summary>Key names viewers send (Qt's / Windows') to keys.dbf names.</summary>
        private static readonly Dictionary<string, string> KeyAliases = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase
        )
        {
            ["Return"] = "ENTER",
            ["Enter"] = "ENTER",
            ["Esc"] = "ESC",
            ["Escape"] = "ESC",
            ["Backspace"] = "BS",
            ["PgDown"] = "PGDN",
            ["PageDown"] = "PGDN",
            ["PgUp"] = "PGUP",
            ["PageUp"] = "PGUP",
            ["Del"] = "DEL",
            ["Delete"] = "DEL",
            ["Ins"] = "INS",
            ["Insert"] = "INS",
            ["Tab"] = "TAB",
            ["Backtab"] = "BACKTAB",
            ["Space"] = "SPACE",
        };

        /// <summary>The Windows virtual keys the runtime codes on key-down:
        /// PgUp .. Down, Select, Execute, PrtScr, Ins, Del, Help, F1 .. F16, NumLock, by the names
        /// viewers send (Qt's, Windows', keys.dbf's).</summary>
        private static readonly Dictionary<string, int> KeyDownKeys = BuildKeyDownKeys();

        private static Dictionary<string, int> BuildKeyDownKeys()
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["PGUP"] = 0x21,
                ["PGDN"] = 0x22,
                ["END"] = 0x23,
                ["HOME"] = 0x24,
                ["LEFT"] = 0x25,
                ["UP"] = 0x26,
                ["RIGHT"] = 0x27,
                ["DOWN"] = 0x28,
                ["SELECT"] = 0x29,
                ["EXECUTE"] = 0x2B,
                ["SNAPSHOT"] = 0x2C,
                ["PRINT"] = 0x2C,
                ["PRTSCR"] = 0x2C,
                ["SYSREQ"] = 0x2C,
                ["INS"] = 0x2D,
                ["DEL"] = 0x2E,
                ["HELP"] = 0x2F,
                ["NUMLOCK"] = 0x90,
                ["NUM LOCK"] = 0x90,
            };
            for (int f = 1; f <= 16; f++)
                d["F" + f.ToString(CultureInfo.InvariantCulture)] = 0x6F + f;
            return d;
        }

        /// <summary>The characters of keys a viewer names (the runtime gets them as WM_CHAR).</summary>
        private static readonly Dictionary<string, int> CharKeys = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase
        )
        {
            ["ENTER"] = 0x0D,
            ["ESC"] = 0x1B,
            ["BS"] = 0x08,
            ["TAB"] = 0x09,
            ["BACKTAB"] = 0x09,
            ["SPACE"] = 0x20,
        };

        private static int Lower(int c) => c >= 0 && c < 0x100 ? char.ToLowerInvariant((char)c) : c;

        /// <summary>A key's code, as the runtime's display client makes it.</summary>
        public static int KeyCode(Animator anim, string key, string text, int modifiers)
        {
            string k = key.Trim();
            if (KeyAliases.TryGetValue(k, out string? alias))
                k = alias;
            var unit = anim.World.Unit;
            bool shift = (modifiers & 1) != 0,
                ctrl = (modifiers & 2) != 0,
                alt = (modifiers & 4) != 0;
            int bits = (shift ? 0x1000 : 0) | (ctrl ? 0x2000 : 0) | (alt ? 0x4000 : 0);
            int Name(string n)
            {
                if (anim.Keys.TryCode(n, out int c))
                    return c;
                long? v = unit.LabelValue(
                    n.StartsWith("KEY_", StringComparison.OrdinalIgnoreCase) ? n : "KEY_" + n
                );
                return v != null ? (int)v.Value : -1;
            }
            // the keys coded on key-down
            if (KeyDownKeys.TryGetValue(k, out int vk))
                return (vk + 0x100) | bits;
            if (k.Length == 1 && ctrl)
            {
                char ch = k[0];
                if (alt && ch >= 'A' && ch <= 'Z' || alt && ch >= 'a' && ch <= 'z')
                    return Lower(ch) | (shift ? 0x7000 : 0x6000);
                if (ch >= '0' && ch <= '9')
                    return ch | (alt ? 0x6000 : 0x2000) | (shift ? 0x1000 : 0);
            }
            // a name keys.dbf or a label defines (no modifiers: a scenario names the code)
            if (k.Length > 1 && bits == 0 && !CharKeys.ContainsKey(k))
            {
                int n = Name(k);
                if (n >= 0)
                    return n;
            }
            // the character (WM_CHAR)
            int c0;
            if (text.Length == 1 && text[0] != 0x7F)
                c0 = text[0];
            else if (CharKeys.TryGetValue(k, out int ck))
                c0 = ck;
            else if (k.Length == 1)
                c0 = shift ? char.ToUpperInvariant(k[0]) : char.ToLowerInvariant(k[0]);
            else
            {
                int n = Name(k);
                if (n >= 0)
                    return n | bits;
                return anim.Keys.Parse(k);
            }
            int code = c0;
            if (ctrl)
            {
                if (code == 0x0A)
                    code = 0x0D;
                else if (code < 0x20)
                    code += 0x40;
                code = Lower(code) | (shift ? 0x3000 : 0x2000);
            }
            if (alt)
                code = Lower(code) | (shift ? 0x5000 : 0x4000);
            if (shift && (code & 0x1000) == 0)
                code = Lower(code) | 0x1000;
            return code;
        }

        // ------------------------------------------------------------------ events
        private void OnPageChanged(PageState state, List<ObjectState> changed)
        {
            var body = state.ToJson(changed, Json);
            _sim.Events.Post(() => _sim.Events.Each(PageChanged, h => ((Action<JsonValue>)h)(body)));
            DrainLogs();
        }

        /// <summary>The animator's log lines (hardware alarms, MSG_LOG, engine exceptions) as
        /// simulation log entries.</summary>
        public void DrainLogs()
        {
            var a = _animator;
            if (a == null)
                return;
            var logs = a.Logs;
            if (_logsSeen > logs.Count)
                _logsSeen = Math.Max(0, logs.Count - 1);
            for (; _logsSeen < logs.Count; _logsSeen++)
            {
                string t = logs[_logsSeen];
                string level =
                    t.StartsWith("engine exception", StringComparison.Ordinal) ? "error"
                    : t.StartsWith("hardware alarm", StringComparison.Ordinal) ? "warning"
                    : "info";
                _sim.Note(level, "page: " + t);
            }
        }

        /// <summary>A window opened, closed, changed page or moved: the windowsChanged event goes
        /// at the end of the simulation's current command (one for many changes).</summary>
        private void QueueWindows()
        {
            if (_windowsQueued)
                return;
            _windowsQueued = true;
            _sim.Defer(FlushWindows);
        }

        private void FlushWindows()
        {
            _windowsQueued = false;
            var body = WindowsJson();
            string s = body.ToString();
            if (s == _windowsSent)
                return;
            _windowsSent = s;
            _sim.Events.Post(() => _sim.Events.Each(WindowsChanged, h => ((Action<JsonValue>)h)(body)));
        }

        /// <summary>{windows: [{window, page, title, x, y, w, h, mode, parent, active, hasModel,
        /// path}]} for every open window (path = the page's drawing file in the unit, for a
        /// viewer to send its model; '' when not found).</summary>
        public JsonValue WindowsJson()
        {
            var arr = JsonValue.NewArray();
            foreach (var w in Display.Windows)
            {
                if (!w.Open)
                    continue;
                var o = JsonValue.NewObject();
                o.Set("window", JsonValue.Of(w.Number));
                string page = w.Page?.Name ?? "";
                o.Set("page", page);
                o.Set("title", Display.Language.Localise(w.Title)); // as the caption shows it
                o.Set("x", JsonValue.Of(w.X));
                o.Set("y", JsonValue.Of(w.Y));
                o.Set("w", JsonValue.Of(w.W > 0 ? w.W : w.Page?.Width ?? 0));
                o.Set("h", JsonValue.Of(w.H > 0 ? w.H : w.Page?.Height ?? 0));
                o.Set("mode", JsonValue.Of(w.Mode));
                o.Set("parent", JsonValue.Of(w.Parent));
                o.Set("active", JsonValue.Of(w.Number == Display.Focus));
                o.Set("hasModel", JsonValue.Of(w.Page?.Drawing != null));
                o.Set("path", page.Length > 0 ? DrawingFile(page) ?? "" : "");
                // the window's super genie associations (name -> tag, or a literal's text): a
                // viewer asks ctsim/tags {window} again when they change
                var ass = JsonValue.NewObject();
                if (w.Page != null)
                    foreach (var kv in w.Associations)
                        ass.Set(kv.Key, (kv.Value.Literal ? "'" : "") + kv.Value.Tag);
                o.Set("associations", ass);
                arr.Add(o);
            }
            var b = JsonValue.NewObject();
            b.Set("windows", arr);
            return b;
        }

        private readonly Dictionary<string, string?> _files = new Dictionary<string, string?>(
            StringComparer.Ordinal
        );

        /// <summary>The page's .ctg in the unit's project folders (search order), null when none.</summary>
        private string? DrawingFile(string page)
        {
            string k = Key(page);
            if (_files.TryGetValue(k, out var f))
                return f;
            string? hit = null;
            foreach (var p in _sim.World.Unit.Search)
            {
                string c = Path.Combine(p.Dir, page + ".ctg");
                if (File.Exists(c))
                {
                    hit = c;
                    break;
                }
            }
            _files[k] = hit;
            return hit;
        }

        // ------------------------------------------------------------------ IDisplayHost
        PageRecord? IDisplayHost.FindPage(string name, string cluster) => null;

        PageDrawing? IDisplayHost.LoadDrawing(PageRecord page) =>
            _models.TryGetValue(Key(page.Name), out var m) ? m.Drawing : null;

        void IDisplayHost.Shown(int window, PageInstance page) => QueueWindows();

        void IDisplayHost.DisplayOutput(int window, int an, string kind, string text) { }

        void IDisplayHost.WindowChanged(Window w) => QueueWindows();

        // ------------------------------------------------------------------ scenarios
        /// <summary>A scenario's page step: open the page in a window (0 or the error).</summary>
        public int OpenForScenario(int window, string name)
        {
            var a = EnsureAnimator();
            var w = Display.Get(window);
            if (w == null)
                return 256;
            if (!w.Open)
            {
                w.Open = true;
                w.Parent = -1;
            }
            int err = a.Open(name, window, _models.TryGetValue(Key(name), out var m) ? m : null, true, null);
            if (err == 0)
            {
                CicodeSim.Builtins.DisplayPages.NoteShown(Display, w);
            }
            QueueWindows();
            return err;
        }
    }
}
