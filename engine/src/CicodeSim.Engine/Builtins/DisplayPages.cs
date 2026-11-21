using System;
using System.Collections.Generic;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World.Display;

namespace CicodeSim.Builtins
{
    /// <summary>Page catalogue and navigation.</summary>
    public static class DisplayPages
    {
        private static DisplayState D(SimHost h) => DisplayBuiltins.Of(h);

        private static long Arg(ArgList a, int k, long dflt) =>
            a.Count > k && !a[k].IsNothing ? A.I(a[k]) : dflt;

        /// <summary>A page was shown in a window: the page list of the window
        /// holds each page once, the position on the shown page; at most [Page]MaxList pages (the
        /// oldest goes).</summary>
        internal static void NoteShown(DisplayState d, Window w)
        {
            if (w.Page == null)
                return;
            string name = w.Page.Name;
            int k = w.PageList.FindIndex(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
            if (k < 0)
            {
                w.PageList.Add(name);
                while (w.PageList.Count > Math.Max(1, d.Config.MaxList))
                    w.PageList.RemoveAt(0);
                k = w.PageList.Count - 1;
            }
            w.PageListPos = k;
            d.RaisePageShown(w);
        }

        /// <summary>The log text of a failed page change: the page and why.</summary>
        internal static string Why(string page, int err) => Why(null, page, err);

        /// <summary>The log text of a failed page change, with the simulation's own reason
        /// (SimPages.WhyNot: which project, which file is missing) when it has one.</summary>
        internal static string Why(SimHost? h, string page, int err)
        {
            if (h?.World?.Display?.Host is CicodeSim.Sim.SimPages sp)
                return "page '"
                    + page
                    + "': "
                    + sp.WhyNot(page, err)
                    + " (error "
                    + err.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ")";
            string r;
            switch (err)
            {
                case 261:
                    r = "no compiled page of that name in the project or its include chain";
                    break;
                case 265:
                    r = "the compiled page has no Control record";
                    break;
                case 274:
                    r = page.Length == 0 ? "no page name" : "the window is not open";
                    break;
                case 277:
                    r = "the page is not in the user's areas";
                    break;
                case 318:
                    r = "too many nested page changes";
                    break;
                case 517:
                    r = "called from the window's own scan";
                    break;
                default:
                    r = "error " + err;
                    break;
            }
            return "page '" + page + "': " + r + ", error " + err;
        }

        /// <summary>The page change core as the built-ins call it: 318 (recursion) and
        /// 517 (the window's own scan) are trapped there (not raised); 0 or the error.</summary>
        internal static int Show(SimHost h, CicodeTask t, string page, int window, bool push)
        {
            var d = D(h);
            int err = d.Pages.Open(page, window, push);
            if (err == 318 || err == 517)
                W.Raise(t, err);
            if (err == 0 && d.OpenWindow(window) is Window w)
                NoteShown(d, w);
            return err;
        }

        /// <summary>A history move (and the forward / recall paths): the page at the
        /// new position shown without a new history entry.</summary>
        private static int Move(SimHost h, CicodeTask t, Window w, int delta)
        {
            var d = D(h);
            int err = d.Pages.History(w.Number, delta);
            if (err == 318 || err == 517)
                W.Raise(t, err);
            if (err == 0)
                NoteShown(d, w);
            return err;
        }

        // ------------------------------------------------------------------ primitives
        [Builtin(
            "_PageDisplay",
            Effects = "shows the page in the task's window, a new history entry",
            Errors = "256 the window is not open (raised and returned); 277 not in the user's area (returned, prompt 'Page is out of your Area'); 274 an empty name; 261 no compiled page; 265 no Control record; 318 too many nested page changes and 517 a change from the window's own scan (raised and returned); any error: prompt 'Cannot Display Page'",
            Notes = "Include's PageDisplay calls it. The forward entries of the history are dropped, the page appended; the page list holds the page once. The cluster argument is not used (one simulated cluster)."
        )]
        private static Value PageDisplayPrim(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            if (w == null)
            {
                W.Raise(t, 256);
                return W.I(256);
            }
            string page = A.SkipSpace(A.S(a.Get(0)));
            int err = Show(h, t, page, w.Number, true);
            if (err == 277)
                DisplayWindows.Prompt(h, t, 0x372, "", Why(h, page, err));
            else if (err != 0)
                DisplayWindows.Prompt(h, t, 0x373, "", Why(h, page, err));
            return W.I(err);
        }

        [Builtin(
            "_PageGoto",
            Effects = "shows the page in the task's window in place of the current history entry",
            Errors = "256 the window is not open (raised and returned); 274 / 261 / 265 / 277 (returned, prompt 'Invalid Page <name>'); 318 / 517 (raised and returned)",
            Notes = "Include's PageGoto calls it. No new history entry: the current one becomes the new page (the runtime replaces the entry at the position; an empty history gets the page)."
        )]
        private static Value PageGotoPrim(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            if (w == null)
            {
                W.Raise(t, 256);
                return W.I(256);
            }
            string page = A.SkipSpace(A.S(a.Get(0)));
            int err = Show(h, t, page, w.Number, false);
            if (err != 0)
            {
                DisplayWindows.Prompt(h, t, 0x371, page, Why(h, page, err));
                return W.I(err);
            }
            if (w.Page != null)
            {
                if (w.HistoryPos >= 0 && w.HistoryPos < w.History.Count)
                    w.History[w.HistoryPos] = w.Page.Name;
                else
                {
                    w.History.Add(w.Page.Name);
                    w.HistoryPos = w.History.Count - 1;
                }
            }
            return W.I(0);
        }

        // ------------------------------------------------------------------ next / prev / home
        private static Value NextPrev(SimHost h, CicodeTask t, bool next)
        {
            var w = DisplayWindows.Current(h, t);
            if (w == null)
            {
                W.Raise(t, 256);
                return W.I(256);
            }
            if (w.Page == null)
            {
                DisplayWindows.Prompt(h, t, 0x373);
                return W.I(274);
            }
            string page = next ? w.Page.Record.NextPage : w.Page.Record.PrevPage;
            if (page.Length == 0)
                return W.I(0);
            int err = Show(h, t, page, w.Number, true);
            if (err == 277)
            {
                DisplayWindows.Prompt(h, t, 0x372, "", Why(h, page, err));
                return W.I(277);
            }
            if (err != 0)
                DisplayWindows.Prompt(h, t, 0x373, "", Why(h, page, err));
            return W.I(err);
        }

        [Builtin(
            "PageNext",
            Doc = "1188763",
            Effects = "shows the page's NEXT_PAGE",
            Errors = "256 the window is not open (raised and returned); 277 not in the area (prompt 'Page is out of your Area'); another error of the page change (prompt 'Cannot Display Page')",
            Notes = "An empty NEXT_PAGE (the page .RDB Control record): 0, nothing happens."
        )]
        private static Value PageNext(SimHost h, CicodeTask t, ArgList a) => NextPrev(h, t, true);

        [Builtin(
            "PagePrev",
            Doc = "1188768",
            Effects = "shows the page's PREV_PAGE",
            Errors = "256 the window is not open (raised and returned); 277 (prompt 'Page is out of your Area'); another error (prompt 'Cannot Display Page')",
            Notes = "An empty PREV_PAGE: 0, nothing happens."
        )]
        private static Value PagePrev(SimHost h, CicodeTask t, ArgList a) => NextPrev(h, t, false);

        [Builtin(
            "PageHome",
            Doc = "1188753",
            Effects = "shows [Page]HomePage, a new history entry",
            Errors = "269 the window is not open; 274 [Page]HomePage not set (returned)"
        )]
        private static Value PageHome(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            if (w == null)
                return W.I(269);
            string home = CitectText.Trim(h.World?.Param("Page", "HomePage") ?? "");
            if (home.Length == 0)
                return W.I(274);
            return W.I(Show(h, t, home, w.Number, true));
        }

        // ------------------------------------------------------------------ the history
        [Builtin(
            "PageLast",
            Doc = "1188756",
            Effects = "shows the previous page of the history",
            Errors = "256 the window is not open (raised and returned); 274 no earlier page; 269 no history (returned)",
            Notes = "The history position moves back one, no entry is dropped. The runtime prompts 'No Last Page' only for 257 from the page change; an empty history gives 274 (the doc's 257: the runtime returns 0x112 there)."
        )]
        private static Value PageLast(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            if (w == null)
            {
                W.Raise(t, 256);
                return W.I(256);
            }
            int err = Move(h, t, w, -1);
            if (err == 257)
                DisplayWindows.Prompt(h, t, 0x374);
            return W.I(err);
        }

        [Builtin(
            "PageBack",
            Doc = "1188739",
            Effects = "shows the page count entries back in the history",
            Errors = "269 the window is not open; 274 the count outside the history (returned)",
            Notes = "Count default 1 (a 16-bit value; negative = forward). The position moves, no entry is dropped."
        )]
        private static Value PageBack(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            if (w == null)
                return W.I(269);
            int n = (int)A.Short(Arg(a, 0, 1));
            return W.I(Move(h, t, w, -n));
        }

        [Builtin(
            "PageForward",
            Doc = "1188746",
            Effects = "shows the page count entries forward in the history",
            Errors = "269 the window is not open or has no history; 274 a count of 0 or past the end (returned)"
        )]
        private static Value PageForward(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            if (w == null)
                return W.I(269);
            int n = (int)A.Short(Arg(a, 0, 1));
            if (n == 0)
                return W.I(274);
            return W.I(Move(h, t, w, n));
        }

        [Builtin(
            "PageRecall",
            Doc = "1188772",
            Effects = "shows the history entry at the index",
            Errors = "269 the window is not open; 274 the index is the current page or outside the history (returned)"
        )]
        private static Value PageRecall(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            if (w == null)
                return W.I(269);
            int i = (int)A.Short(A.I(a.Get(0)));
            if (i == w.HistoryPos || i < 0 || i >= w.History.Count)
                return W.I(274);
            return W.I(Move(h, t, w, i - w.HistoryPos));
        }

        [Builtin(
            "PagePeekCurrent",
            Doc = "1188764",
            Notes = "The index of the current page in the window's history (0 first), -1 when the window is not open or has no history."
        )]
        private static Value PagePeekCurrent(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            return W.I(w == null ? -1 : w.HistoryPos);
        }

        /// <summary>The configured title of a page (catalogue TITLE, its Control record when shown).</summary>
        private static string Title(DisplayState d, string page)
        {
            var e = d.Host?.FindPage(page, "") ?? d.Catalogue?.Entry(page);
            return e?.Title ?? "";
        }

        [Builtin(
            "PagePeekLast",
            Doc = "1188765",
            Errors = "274 a type of 4 or more, no window or history, or an offset outside the history (raised, \"\")",
            Notes = "The history entry offset positions back from the current page (0 = the current page, negative = forward): type 0 its name, 1 its configured title, 2 its active title (the window's title for the current page), 3 its name (accepted by the runtime, undocumented: guess)."
        )]
        private static Value PagePeekLast(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            var w = DisplayWindows.Current(h, t);
            int off = (int)A.Short(Arg(a, 0, 0));
            ushort type = (ushort)Arg(a, 1, 0);
            int i = w == null ? -1 : w.HistoryPos - off;
            if (type >= 4 || w == null || w.History.Count == 0 || i < 0 || i >= w.History.Count)
            {
                W.Raise(t, 274);
                return W.Empty;
            }
            string name = w.History[i];
            string s;
            switch (type)
            {
                case 1:
                    s = Title(d, name);
                    break;
                case 2:
                    s = i == w.HistoryPos && w.Title.Length > 0 ? w.Title : Title(d, name);
                    break;
                default:
                    s = name;
                    break;
            }
            return W.S(s.Length > 255 ? s.Substring(0, 255) : s);
        }

        [Builtin(
            "PagePopLast",
            Doc = "1188766",
            Effects = "removes the history entry before the current page",
            Errors = "269 no window, no history or no earlier page (raised, \"\")",
            Notes = "Returns the name of the page before the current one in the history and removes it (the position keeps the current page)."
        )]
        private static Value PagePopLast(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            if (w == null || w.History.Count == 0 || w.HistoryPos <= 0 || w.HistoryPos > w.History.Count - 1)
            {
                W.Raise(t, 269);
                return W.Empty;
            }
            string name = w.History[w.HistoryPos - 1];
            w.History.RemoveAt(w.HistoryPos - 1);
            w.HistoryPos--;
            return W.S(name);
        }

        [Builtin(
            "PagePushLast",
            Doc = "1188771",
            Effects = "inserts a page before the current page in the history (PageLast shows it)",
            Errors = "269 the window is not open; 257 an empty history (returned)",
            Notes = "Nothing is inserted when the entry before the current page is that page already (compare: guess on which entry is compared). Returns 0."
        )]
        private static Value PagePushLast(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            if (w == null)
                return W.I(269);
            if (w.History.Count == 0 || w.HistoryPos < 0)
                return W.I(257);
            string page = CitectText.Trim(A.SkipSpace(A.S(a.Get(0))));
            if (
                w.HistoryPos > 0
                && string.Equals(w.History[w.HistoryPos - 1], page, StringComparison.OrdinalIgnoreCase)
            )
                return W.I(0);
            w.History.Insert(w.HistoryPos, page);
            w.HistoryPos++;
            return W.I(0);
        }

        // ------------------------------------------------------------------ the page list
        [Builtin(
            "PageListCount",
            Doc = "1188757",
            Errors = "269 the window is not open (raised, -1)",
            Notes = "The pages of the window's page list (each page once, [Page]MaxList)."
        )]
        private static Value PageListCount(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            if (w == null)
            {
                W.Raise(t, 269);
                return W.I(-1);
            }
            return W.I(w.PageList.Count);
        }

        [Builtin(
            "PageListCurrent",
            Doc = "1188758",
            Errors = "269 the window is not open or its list is empty (raised, -1)",
            Notes = "The index of the shown page in the window's page list."
        )]
        private static Value PageListCurrent(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            if (w == null || w.PageList.Count == 0 || w.PageListPos < 0)
            {
                W.Raise(t, 269);
                return W.I(-1);
            }
            return W.I(w.PageListPos);
        }

        [Builtin(
            "PageListInfo",
            Doc = "1188759",
            Errors = "274 a type of 4 or more, no window or an index outside the list (raised, \"\")",
            Notes = "Type 0 the page name, 1 its configured title, 2 its active title, 3 its name (guess)."
        )]
        private static Value PageListInfo(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            var w = DisplayWindows.Current(h, t);
            int i = (int)A.Short(A.I(a.Get(0)));
            ushort type = (ushort)Arg(a, 1, 0);
            if (type >= 4 || w == null || i < 0 || i >= w.PageList.Count)
            {
                W.Raise(t, 274);
                return W.Empty;
            }
            string name = w.PageList[i];
            string s =
                type == 1 ? Title(d, name)
                : type == 2
                    ? (
                        w.Page != null
                        && string.Equals(w.Page.Name, name, StringComparison.OrdinalIgnoreCase)
                        && w.Title.Length > 0
                            ? w.Title
                            : Title(d, name)
                    )
                : name;
            return W.S(s);
        }

        [Builtin(
            "PageListDisplay",
            Doc = "1188760",
            Effects = "shows the page at the index of the page list (a new history entry), the list position moves there",
            Errors = "257 no window or an index outside the list (raised and returned); the page change's error (raised and returned)"
        )]
        private static Value PageListDisplay(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            int i = (int)A.Short(A.I(a.Get(0)));
            if (w == null || i < 0 || i >= w.PageList.Count)
            {
                W.Raise(t, 257);
                return W.I(257);
            }
            int err = Show(h, t, w.PageList[i], w.Number, true);
            if (err != 0)
            {
                if (err != 318 && err != 517)
                    W.Raise(t, err);
                return W.I(err);
            }
            w.PageListPos = Math.Min(i, w.PageList.Count - 1);
            return W.I(0);
        }

        [Builtin(
            "PageListDelete",
            Doc = "1188761",
            Effects = "removes a page from the page list; removing the shown page shows the next one (else the previous)",
            Errors = "274 no window, an index outside the list or a list of one page (raised and returned)",
            Notes = "The list position stays at the index (the next page moved there; doc example); the error code for a refused delete is 274 (guess: not read in full)."
        )]
        private static Value PageListDelete(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            int i = (int)A.Short(A.I(a.Get(0)));
            if (w == null || i < 0 || i >= w.PageList.Count || w.PageList.Count <= 1)
            {
                W.Raise(t, 274);
                return W.I(274);
            }
            bool shown = i == w.PageListPos;
            w.PageList.RemoveAt(i);
            if (shown)
            {
                int k = i < w.PageList.Count ? i : w.PageList.Count - 1;
                var keep = new List<string>(w.PageList);
                int err = Show(h, t, keep[k], w.Number, true);
                w.PageList.Clear();
                w.PageList.AddRange(keep);
                w.PageListPos = k;
                if (err != 0)
                {
                    if (err != 318 && err != 517)
                        W.Raise(t, err);
                    return W.I(err);
                }
            }
            else if (i < w.PageListPos)
                w.PageListPos--;
            return W.I(0);
        }

        // ------------------------------------------------------------------ the catalogue
        [Builtin(
            "PageExists",
            Doc = "1188742",
            Notes = "1 when the page is in the run project's compiled page catalogue (_PAGES.RDB; a page the host supplies, e.g. an unsaved one, counts too), else 0; 0 without a catalogue. Names compare without case (guess)."
        )]
        private static Value PageExists(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            string page = A.S(a.Get(0));
            if (d.Host?.FindPage(CitectText.Trim(page), "") != null)
                return W.I(1);
            return W.I(d.Catalogue != null && d.Catalogue.Loaded && d.Catalogue.Exists(page) ? 1 : 0);
        }

        /// <summary>The drawing size of a page that need not be shown (the runtime reads the page
        /// file's header): a window showing it with a size, the host's drawing summary, else the
        /// head of the page's .ctg in the unit's project folders (CtgHead).</summary>
        private static bool Size(DisplayState d, string page, out int width, out int height)
        {
            width = height = 0;
            page = CitectText.Trim(page);
            if (page.Length == 0)
                return false;
            foreach (var w in d.Windows)
                if (
                    w.Open
                    && w.Page != null
                    && string.Equals(w.Page.Name, page, StringComparison.OrdinalIgnoreCase)
                    && (w.Page.Width > 0 || w.Page.Height > 0)
                )
                {
                    width = w.Page.Width;
                    height = w.Page.Height;
                    return true;
                }
            if (d.Host != null)
            {
                var rec = d.Host.FindPage(page, "") ?? d.Catalogue?.Record(page);
                if (rec != null)
                {
                    var dr = d.Host.LoadDrawing(rec);
                    if (dr != null)
                    {
                        width = dr.Width;
                        height = dr.Height;
                        return true;
                    }
                }
            }
            if (d.World != null && CtgHead.Size(d.World.Unit, page, out width, out height))
                return true;
            return false;
        }

        [Builtin(
            "PageFileInfo",
            Doc = "1188744",
            Errors = "274 the page's drawing cannot be read or a mode other than 0 / 1 (raised, 0)",
            Notes = "Mode 0 the page width, 1 its height (pixels), from a shown page's drawing, the page's drawing summary (IDisplayHost), else the head of its .ctg (the unit's project folders in search order: the width and height in the page block's head)."
        )]
        private static Value PageFileInfo(SimHost h, CicodeTask t, ArgList a)
        {
            long mode = A.Short(A.I(a.Get(1)));
            if (Size(D(h), A.S(a.Get(0)), out int w, out int ht))
            {
                if (mode == 0)
                    return W.I((short)w);
                if (mode == 1)
                    return W.I((short)ht);
            }
            W.Raise(t, 274);
            return W.I(0);
        }

        [Builtin(
            "PageFileInfoEx",
            Doc = "1188745",
            Errors = "274 the page's drawing cannot be read (modes 0 / 1: \"0\"), the page is not in the catalogue or another mode (raised, \"\")",
            Notes = "Mode 0 width, 1 height (decimal text), 2 the content type, 3 the parent page, 4 the configured title (the catalogue's _PAGES.RDB record)."
        )]
        private static Value PageFileInfoEx(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            string page = A.S(a.Get(0));
            long mode = A.Short(A.I(a.Get(1)));
            if (mode == 0 || mode == 1)
            {
                int v = 0;
                if (Size(d, page, out int w, out int ht))
                    v = mode == 0 ? w : ht;
                else
                    W.Raise(t, 274);
                return W.S(W.Inv(v));
            }
            var e = d.Catalogue != null && d.Catalogue.Loaded ? d.Catalogue.Entry(page) : null;
            if (e == null)
            {
                W.Raise(t, 274);
                return W.Empty;
            }
            string s;
            switch (mode)
            {
                case 2:
                    s = e.ConType;
                    break;
                case 3:
                    s = e.Parent;
                    break;
                case 4:
                    s = e.Title;
                    break;
                default:
                    W.Raise(t, 274);
                    return W.Empty;
            }
            return W.S(s.Length > 64 ? s.Substring(0, 64) : s);
        }

        [Builtin(
            "PageTransformCoords",
            Doc = "1188780",
            Errors = "274 an output not a reference or a type other than 0..2; 269 the window is not open (returned)",
            Notes = "A page point to the screen: (x, y) times the window's scale (1 unscaled, rounded half away), then type 0 plus the window's client origin on the screen, 1 relative to the window rectangle, 2 relative to the client area (a simulated window has no frame: types 1 and 2 agree)."
        )]
        private static Value PageTransformCoords(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            if (!(a.Get(3).O is Ref) || !(a.Get(4).O is Ref))
                return W.I(274);
            long n = A.Short(A.I(a.Get(0)));
            var w = d.OpenWindow(n == -1 ? d.WindowOf(t) : (int)n);
            if (w == null)
                return W.I(269);
            long type = A.Short(A.I(a.Get(5)));
            if (type < 0 || type > 2)
                return W.I(274);
            static long Round(double v) => (long)(v >= 0 ? v + 0.5 : v - 0.5);
            long x = Round(A.I(a.Get(1)) * w.ScaleX);
            long y = Round(A.I(a.Get(2)) * w.ScaleY);
            if (type == 0)
            {
                x += w.X;
                y += w.Y;
            }
            W.Out(t, a.Get(3), x);
            W.Out(t, a.Get(4), y);
            return W.I(0);
        }

        [Builtin(
            "PageTask",
            Doc = "1188779",
            Effects = "starts the function as a task in the window (the function shows the page)",
            Notes = "TaskNew(sFunctionName, sFunctionArg, 0) run in window iWinNum (-1 = the task's); its handle, -1 when the window is not open or the task cannot start. The history does not keep the function for PageBack / PageForward to run again (verify)."
        )]
        private static Value PageTask(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            long n = A.Short(A.I(a.Get(0)));
            int win = n == -1 ? d.WindowOf(t) : (int)n;
            if (d.OpenWindow(win) == null || h.Registry == null)
                return W.I(-1);
            var b = h.Registry.Table.Lookup("TaskNew");
            if (b == null)
                return W.I(-1);
            object? saved = t.Data.TryGetValue(TaskContext.WindowKey, out var sv) ? sv : null;
            t.Data[TaskContext.WindowKey] = win;
            try
            {
                return h.Registry.Call(
                    b.Index,
                    t,
                    ArgList.Of(Value.Str(A.S(a.Get(1))), Value.Str(A.S(a.Get(2))), Value.Long(0))
                );
            }
            finally
            {
                if (saved != null)
                    t.Data[TaskContext.WindowKey] = saved;
                else
                    t.Data.Remove(TaskContext.WindowKey);
            }
        }
    }
}
