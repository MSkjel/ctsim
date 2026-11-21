using System;
using System.Collections.Generic;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World.Display;

namespace CicodeSim.Builtins
{
    /// <summary>Windows: WinNew / WinNewAt / WinNewPinAt, WinFree, WinSelect and friends.</summary>
    public static class DisplayWindows
    {
        // ------------------------------------------------------------------ shared helpers
        /// <summary>The task's window, null when it is not open.</summary>
        internal static Window? Current(SimHost h, CicodeTask t) =>
            DisplayBuiltins.Of(h).OpenWindow(DisplayBuiltins.Of(h).WindowOf(t));

        /// <summary>Select a window for the task (the current window).</summary>
        internal static void Select(CicodeTask t, int window) => t.Data[TaskContext.WindowKey] = window;

        /// <summary>The messages the display built-ins write to the prompt line (the runtime's
        /// English texts).</summary>
        internal static string Message(int id, string arg = "")
        {
            string s;
            switch (id)
            {
                case 0x36b:
                    s = "Cannot Open Window %s";
                    break;
                case 0x36c:
                    s = "Too many windows";
                    break;
                case 0x36d:
                    s = "Cannot open this window twice";
                    break;
                case 0x36e:
                    s = "Cannot free Parent window";
                    break;
                case 0x36f:
                    s = "Cannot free last window";
                    break;
                case 0x370:
                    s = "Invalid Window";
                    break;
                case 0x371:
                    s = "Invalid Page %s";
                    break;
                case 0x372:
                    s = "Page is out of your Area";
                    break;
                case 0x373:
                    s = "Cannot Display Page";
                    break;
                case 0x374:
                    s = "No Last Page";
                    break;
                default:
                    s = "";
                    break;
            }
            return s.Replace("%s", arg);
        }

        /// <summary>prompt(id): the message on the task's window's prompt line, and logged.</summary>
        /// <paramref name="detail"/> goes to the log only (the prompt line shows the runtime's text).
        internal static void Prompt(SimHost h, CicodeTask t, int id, string arg = "", string detail = "")
        {
            string text = Message(id, arg);
            var d = DisplayBuiltins.Of(h);
            d.SetPrompt(d.WindowOf(t), text);
            h.Note(t, "prompt: " + text + (detail.Length > 0 ? " (" + detail + ")" : ""));
        }

        private static long Arg(ArgList a, int k, long dflt) =>
            a.Count > k && !a[k].IsNothing ? A.I(a[k]) : dflt;

        // ------------------------------------------------------------------ select / goto
        [Builtin(
            "WinSelect",
            Doc = "1193997",
            Notes = "An open window (number below 0x8000 and [Page]Windows): it becomes the task's window and the previous one is returned. Otherwise nothing changes and the task's window is returned. (A process that is no display client raises 256: never the simulator.)"
        )]
        private static Value WinSelect(SimHost h, CicodeTask t, ArgList a)
        {
            var d = DisplayBuiltins.Of(h);
            int cur = d.WindowOf(t);
            long n = A.Short(A.I(a.Get(0)));
            if (n >= 0 && d.OpenWindow((int)n) != null)
            {
                Select(t, (int)n);
                return W.I(cur);
            }
            return W.I(cur);
        }

        /// <summary>WinGoto's rule: focus and select an open window, 0; else the prompt line's message and 880.</summary>
        internal static int Goto(SimHost h, CicodeTask t, int n)
        {
            var d = DisplayBuiltins.Of(h);
            if (n >= 0 && d.OpenWindow(n) != null)
            {
                d.Focus = n;
                Select(t, n);
                return 0;
            }
            Prompt(h, t, 0x370);
            return 880;
        }

        [Builtin(
            "WinGoto",
            Doc = "1193985",
            Effects = "the window gets the focus and becomes the task's window",
            Errors = "880 the window is not open (returned, the prompt line says 'Invalid Window')",
            Notes = "The runtime returns 880 itself."
        )]
        private static Value WinGoto(SimHost h, CicodeTask t, ArgList a) =>
            W.I(Goto(h, t, (int)A.Short(A.I(a.Get(0)))));

        [Builtin(
            "WinNext",
            Doc = "1193991",
            Notes = "The next open window after the task's (cyclic, window numbers) gets the focus (WinGoto); returns it, -1 when there is no other window or the task's is not open."
        )]
        private static Value WinNext(SimHost h, CicodeTask t, ArgList a)
        {
            var d = DisplayBuiltins.Of(h);
            if (Current(h, t) == null)
                return W.I(-1);
            int n = d.NextOpen(d.WindowOf(t));
            if (n >= 0)
                Goto(h, t, n);
            return W.I(n);
        }

        [Builtin(
            "WinPrev",
            Doc = "1193994",
            Notes = "The previous open window before the task's (cyclic) gets the focus; returns it, -1 when none (the mirror of WinNext: guess, its loop was not read in full)."
        )]
        private static Value WinPrev(SimHost h, CicodeTask t, ArgList a)
        {
            var d = DisplayBuiltins.Of(h);
            if (Current(h, t) == null)
                return W.I(-1);
            int n = d.PrevOpen(d.WindowOf(t));
            if (n >= 0)
                Goto(h, t, n);
            return W.I(n);
        }

        // ------------------------------------------------------------------ new / free
        /// <summary>The page change of a new window: the lowest free
        /// window, its parent the task's window for a child mode (1, 2); the new window is the
        /// task's. -1 with the prompt messages of the runtime.</summary>
        internal static int NewWindow(SimHost h, CicodeTask t, string page, int x, int y, long mode)
        {
            var d = DisplayBuiltins.Of(h);
            if (mode < 0 && mode != unchecked((int)0x80000000))
                return -1;
            if (d.OpenCount() >= d.Windows.Length)
            {
                Prompt(h, t, 0x36c);
                return -1;
            }
            page = A.SkipSpace(page);
            int cur = d.WindowOf(t);
            // a child of an OPEN window only: a task whose window was freed (a popup's task that
            // runs on) would otherwise give the new window a closed parent, and two such windows
            // can end up each other's parent, which the recursive free of children never leaves
            // (a stack overflow, uncatchable). Every open window's
            // parent stays open (a freed window's children are freed with it).
            int parent = (mode & 3) != 0 && mode >= 0 && d.OpenWindow(cur) != null ? cur : -1;
            if (mode >= 0 && (mode & 0x280) != 0)
            {
                foreach (var w in d.Windows)
                {
                    if (
                        !w.Open
                        || w.Page == null
                        || !string.Equals(
                            w.Page.Name,
                            CicodeSim.Project.CitectText.Trim(page),
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        continue;
                    bool twice = (mode & 0x80) != 0;
                    if (!twice && (mode & 0x200) != 0 && SameAssociations(d, w))
                    {
                        // the same super genie with the same associations: the pending ones go
                        // (guess)
                        d.Pending.RemoveAll(p => p.Target == -2);
                        twice = true;
                    }
                    if (twice)
                    {
                        Prompt(h, t, 0x36d);
                        return -1;
                    }
                }
            }
            int n = d.Pages.NewWindow(page, x, y, (int)(mode & 0x7fffffff), parent);
            if (n < 0)
            {
                Prompt(h, t, 0x36b, page);
                return -1;
            }
            var nw = d.Get(n);
            if (nw != null)
            {
                nw.Parent = parent;
                if (nw.OpenSerial == 0)
                    d.Opened(nw);
                DisplayPages.NoteShown(d, nw);
            }
            Select(t, n);
            return n;
        }

        /// <summary>Every pending -2 association names the same tag in the window (WinNewAt mode
        /// 512, the loop: name, mode, literal flag and tag compared).</summary>
        private static bool SameAssociations(DisplayState d, Window w)
        {
            bool any = false;
            foreach (var p in d.Pending)
            {
                if (p.Target != -2)
                    continue;
                any = true;
                if (!w.Associations.TryGetValue(p.Arg, out var e))
                    return false;
                if (
                    e.Literal != p.Literal
                    || e.Mode != p.Mode
                    || !string.Equals(e.Tag, p.Tag, StringComparison.OrdinalIgnoreCase)
                )
                    return false;
            }
            return any || w.Associations.Count == 0;
        }

        [Builtin(
            "WinNew",
            Doc = "1193988",
            Effects = "opens a window showing the page; it becomes the task's window",
            Notes = "The lowest free window, mode 0, no parent. -1 when [Page]Windows are open (prompt 'Too many windows') or the page cannot be shown (prompt 'Cannot Open Window <page>'; the task's window unchanged). The cluster argument is not used (one simulated cluster)."
        )]
        private static Value WinNew(SimHost h, CicodeTask t, ArgList a) =>
            W.I(NewWindow(h, t, A.S(a.Get(0)), 0, 0, unchecked((int)0x80000000)));

        [Builtin(
            "WinNewAt",
            Doc = "1193989",
            Effects = "opens a window at (x, y) showing the page; it becomes the task's window",
            Notes = "Mode (bit 0x80000 cleared): negative -> -1; 1 page child / 2 window child of the task's window (closed when its page changes / when it is freed); 128 and 512: a window already showing the page (512: with the same pending -2 associations) refuses it (-1, prompt 'Cannot open this window twice'); PageInfo(11) gives the mode. Other bits are kept, not acted on (no real window). -1 when [Page]Windows are open or the page cannot be shown."
        )]
        private static Value WinNewAt(SimHost h, CicodeTask t, ArgList a)
        {
            long mode = A.I(a.Get(3)) & ~0x80000L;
            return W.I(NewWindow(h, t, A.S(a.Get(0)), (int)A.I(a.Get(1)), (int)A.I(a.Get(2)), mode));
        }

        [Builtin(
            "WinNewPinAt",
            Doc = "1193990",
            Effects = "opens a pinned child window",
            Notes = "WinNewAt with mode (mode & 0x3f400) | 0x80003 (a page and window child, pinned). The runtime raises the last window error on a failure: not raised here (guess)."
        )]
        private static Value WinNewPinAt(SimHost h, CicodeTask t, ArgList a)
        {
            long mode = (A.I(a.Get(3)) & 0x3f400) | 0x80003;
            return W.I(NewWindow(h, t, A.S(a.Get(0)), (int)A.I(a.Get(1)), (int)A.I(a.Get(2)), mode));
        }

        /// <summary>WinFree's rule.</summary>
        private static Value Free(SimHost h, CicodeTask t, int n)
        {
            var d = DisplayBuiltins.Of(h);
            var w = d.OpenWindow(n);
            if (w == null)
            {
                W.Raise(t, 256);
                return W.I(256);
            }
            // (a) the window and the windows under it are every open window
            if (d.DescendantCount(n) + 1 >= d.OpenCount())
            {
                Prompt(h, t, 0x36e);
                return W.I(320);
            }
            int next = d.NextOpen(n);
            if (next == -1)
            {
                Prompt(h, t, 0x36f);
                return W.I(320);
            }
            int parent = w.Parent;
            d.Pages.FreeWindow(n);
            ForgetNames(d);
            int to = parent >= 0 && d.OpenWindow(parent) != null ? parent : next;
            if (d.OpenWindow(to) == null)
                to = d.NextOpen(n);
            if (to >= 0)
                Goto(h, t, to);
            return W.I(0);
        }

        /// <summary>A freed window loses its WinSetName name (doc 1193998): every name of a window
        /// that is no longer open goes (the window and its children).</summary>
        private static void ForgetNames(DisplayState d)
        {
            var gone = new List<string>();
            foreach (var kv in d.WindowNames)
                if (d.OpenWindow(kv.Value) == null)
                    gone.Add(kv.Key);
            foreach (string k in gone)
            {
                var w = d.Get(d.WindowNames[k]);
                if (w != null)
                    w.Name = "";
                d.WindowNames.Remove(k);
            }
        }

        [Builtin(
            "WinFree",
            Doc = "1193977",
            Effects = "frees the task's window (its page closes, its child windows go); its parent (else the next window) gets the focus",
            Errors = "256 the task's window is not open (raised and returned); 320 freeing it would leave no window: 'Cannot free Parent window' when the window and its children are every open window, 'Cannot free last window' (returned, prompt line)",
            Notes = "The runtime never frees its last window."
        )]
        private static Value WinFree(SimHost h, CicodeTask t, ArgList a) =>
            Free(h, t, DisplayBuiltins.Of(h).WindowOf(t));

        [Builtin(
            "WinFreeEx",
            Doc = "1193978",
            Effects = "frees a window (-1 = the task's)",
            Errors = "256 the window is not open (raised and returned); 320 it would leave no window (returned)",
            Notes = "WinFree of the given window."
        )]
        private static Value WinFreeEx(SimHost h, CicodeTask t, ArgList a)
        {
            long n = A.Short(A.I(a.Get(0)));
            return Free(h, t, n == -1 ? DisplayBuiltins.Of(h).WindowOf(t) : (int)n);
        }

        // ------------------------------------------------------------------ the window tree
        [Builtin(
            "WinGetParent",
            Doc = "1193983",
            Notes = "-1 = the task's window; not open: -1. bGetRoot 0: its parent (-1 none); else the parent chain is followed while the parent is open and the last window reached is returned."
        )]
        private static Value WinGetParent(SimHost h, CicodeTask t, ArgList a)
        {
            var d = DisplayBuiltins.Of(h);
            long n = A.Short(Arg(a, 0, -1));
            var w = d.OpenWindow(n < 0 ? d.WindowOf(t) : (int)n);
            if (w == null)
                return W.I(-1);
            if (Arg(a, 1, 0) == 0)
                return W.I(w.Parent);
            int last = w.Parent;
            int guard = 0;
            var p = d.OpenWindow(w.Parent);
            while (p != null && guard++ < 256)
            {
                last = p.Number;
                p = p.Parent >= 0 ? d.OpenWindow(p.Parent) : null;
            }
            return W.I(last);
        }

        [Builtin(
            "WinGetFirstChild",
            Doc = "1193976",
            Notes = "The window's first child window (creation order), -1 when the window is not open or has none."
        )]
        private static Value WinGetFirstChild(SimHost h, CicodeTask t, ArgList a)
        {
            var d = DisplayBuiltins.Of(h);
            int n = (int)A.Short(A.I(a.Get(0)));
            return W.I(d.OpenWindow(n) == null ? -1 : d.FirstChildOf(n));
        }

        [Builtin(
            "WinGetNextChild",
            Doc = "1193982",
            Notes = "The next child of the window's parent after it (creation order), -1 when none or the window is not open."
        )]
        private static Value WinGetNextChild(SimHost h, CicodeTask t, ArgList a)
        {
            var d = DisplayBuiltins.Of(h);
            int n = (int)A.Short(A.I(a.Get(0)));
            return W.I(d.OpenWindow(n) == null ? -1 : d.NextChildOf(n));
        }

        // ------------------------------------------------------------------ names, focus, handles
        [Builtin(
            "WinSetName",
            Doc = "1193998",
            Effects = "names a window (WinNumber finds it)",
            Notes = "-1 = the task's window. The names are a std::map name -> window and WinSetName is its insert (the returned pair's bool): 1 when stored; 0 when the name is already in use (by any window: it keeps its window) or the window is not open (the doc's '0 if successful' is not the runtime's). A window keeps every name it was given (none is replaced); its names go when it is freed."
        )]
        private static Value WinSetName(SimHost h, CicodeTask t, ArgList a)
        {
            var d = DisplayBuiltins.Of(h);
            string name = A.S(a.Get(0));
            long n = A.Short(Arg(a, 1, -1));
            var w = d.OpenWindow(n == -1 ? d.WindowOf(t) : (int)n);
            if (w == null || d.WindowNames.ContainsKey(name))
                return W.I(0);
            d.WindowNames[name] = w.Number;
            w.Name = FirstName(d, w.Number);
            return W.I(1);
        }

        /// <summary>The window's first name in the map's order (std::less of std::string: byte order,
        /// here ordinal), "" when it has none (the runtime walks the map from its start).</summary>
        private static string FirstName(DisplayState d, int window)
        {
            string? first = null;
            foreach (var kv in d.WindowNames)
                if (kv.Value == window && (first == null || string.CompareOrdinal(kv.Key, first) < 0))
                    first = kv.Key;
            return first ?? "";
        }

        [Builtin(
            "WinGetName",
            Doc = "1193981",
            Notes = "-1 = the task's window. Its first name in name order (the map is walked from its start: byte order), \"\" when none or the window is not open."
        )]
        private static Value WinGetName(SimHost h, CicodeTask t, ArgList a)
        {
            var d = DisplayBuiltins.Of(h);
            long n = A.Short(Arg(a, 0, -1));
            var w = d.OpenWindow(n == -1 ? d.WindowOf(t) : (int)n);
            if (w == null)
                return W.Empty;
            string s = FirstName(d, w.Number);
            return W.S(s.Length > 255 ? s.Substring(0, 255) : s);
        }

        [Builtin(
            "WinGetFocus",
            Doc = "1193980",
            Notes = "The window with the keyboard focus (WinGoto, a new window: the simulator's focus), -1 when none."
        )]
        private static Value WinGetFocus(SimHost h, CicodeTask t, ArgList a)
        {
            var d = DisplayBuiltins.Of(h);
            return W.I(d.OpenWindow(d.Focus) != null ? d.Focus : -1);
        }

        [Builtin(
            "WinGetClicked",
            Doc = "1193979",
            Notes = "The window last clicked with the left button: the focus window in the simulator (guess: viewers do not report clicks per window), -1 when none."
        )]
        private static Value WinGetClicked(SimHost h, CicodeTask t, ArgList a)
        {
            var d = DisplayBuiltins.Of(h);
            return W.I(d.OpenWindow(d.Focus) != null ? d.Focus : -1);
        }

        /// <summary>
        /// The simulated HWND of a window: 0x10000 + its number. The runtime returns a real handle.
        /// </summary>
        internal static long Hwnd(int window) => 0x10000 + window;

        [Builtin(
            "WinGetWndHnd",
            Doc = "1193984",
            Notes = "The task's window's handle: a simulated HWND 0x10000 + the window number (the real one is a Win32 handle), 0 when the window is not open."
        )]
        private static Value WinGetWndHnd(SimHost h, CicodeTask t, ArgList a)
        {
            var w = Current(h, t);
            return W.I(w == null ? 0 : Hwnd(w.Number));
        }

        [Builtin(
            "WndFind",
            Doc = "1194002",
            Notes = "The simulated HWND of the Plant SCADA window whose title is exactly sTitle, 0 when none (the simulated computer has no other application windows)."
        )]
        private static Value WndFind(SimHost h, CicodeTask t, ArgList a)
        {
            string title = A.S(a.Get(0));
            foreach (var w in DisplayBuiltins.Of(h).Windows)
                if (w.Open && string.Equals(w.Title, title, StringComparison.Ordinal))
                    return W.I(Hwnd(w.Number));
            return W.I(0);
        }

        [Builtin(
            "WndShow",
            Doc = "1194009",
            Notes = "ShowWindow of a simulated handle: 0 for a Plant SCADA window's, else 0 too (nothing to show: no outside windows). The mode is not recorded."
        )]
        private static Value WndShow(SimHost h, CicodeTask t, ArgList a) => W.I(0);

        // ------------------------------------------------------------------ titles
        [Builtin(
            "GetWinTitle",
            Doc = "1193972",
            Notes = "The task's window's title (the page TITLE, else its name, or the WinTitle text), \"\" when the window is not open or shows no page."
        )]
        private static Value GetWinTitle(SimHost h, CicodeTask t, ArgList a)
        {
            var w = Current(h, t);
            if (w == null || w.Page == null)
                return W.Empty;
            string s =
                w.Title.Length > 0
                    ? w.Title
                    : (w.Page.Record.Title.Length > 0 ? w.Page.Record.Title : w.Page.Name);
            return W.S(s.Length > 255 ? s.Substring(0, 255) : s);
        }

        [Builtin(
            "_WinTitle",
            Effects = "sets the task's window's title",
            Errors = "256 the window is not open (returned)",
            Notes = "Include's WinTitle(sTitle) calls it. The title stays until a page is displayed in the window (the page scan's title, waits on the flag it sets). Returns 0."
        )]
        private static Value WinTitle(SimHost h, CicodeTask t, ArgList a)
        {
            var w = Current(h, t);
            if (w == null)
                return W.I(256);
            w.Title = A.S(a.Get(0));
            w.TitleSet = true; // +100 = 1: the page scan keeps it
            DisplayBuiltins.Of(h).Host?.WindowChanged(w);
            return W.I(0);
        }

        // ------------------------------------------------------------------ position, size, mode
        [Builtin(
            "WinMove",
            Doc = "1193987",
            Effects = "the task's window's position and size",
            Errors = "256 the window is not open (raised and returned)",
            Notes = "PageInfo(14 / 15) gives the new position. Returns 0."
        )]
        private static Value WinMove(SimHost h, CicodeTask t, ArgList a)
        {
            var w = Current(h, t);
            if (w == null)
            {
                W.Raise(t, 256);
                return W.I(256);
            }
            w.X = (int)A.I(a.Get(0));
            w.Y = (int)A.I(a.Get(1));
            w.W = (int)A.I(a.Get(2));
            w.H = (int)A.I(a.Get(3));
            DisplayBuiltins.Of(h).Host?.WindowChanged(w);
            return W.I(0);
        }

        [Builtin(
            "WinPos",
            Doc = "1193993",
            Effects = "the task's window's position (its size kept)",
            Errors = "256 the window is not open (raised and returned)"
        )]
        private static Value WinPos(SimHost h, CicodeTask t, ArgList a)
        {
            var w = Current(h, t);
            if (w == null)
            {
                W.Raise(t, 256);
                return W.I(256);
            }
            w.X = (int)A.I(a.Get(0));
            w.Y = (int)A.I(a.Get(1));
            DisplayBuiltins.Of(h).Host?.WindowChanged(w);
            return W.I(0);
        }

        [Builtin(
            "WinSize",
            Doc = "1193999",
            Effects = "the task's window's size (its position kept)",
            Errors = "256 the window is not open (raised and returned)",
            Notes = "Mode 0 = the client area: the runtime adds the frame (window minus client size); a simulated window has no frame, so both modes set the same size (guess)."
        )]
        private static Value WinSize(SimHost h, CicodeTask t, ArgList a)
        {
            var w = Current(h, t);
            if (w == null)
            {
                W.Raise(t, 256);
                return W.I(256);
            }
            w.W = (int)A.I(a.Get(0));
            w.H = (int)A.I(a.Get(1));
            DisplayBuiltins.Of(h).Host?.WindowChanged(w);
            return W.I(0);
        }

        [Builtin(
            "WinMode",
            Doc = "1193986",
            Errors = "274 a mode of 10 or more (returned); 256 the window is not open (raised and returned)",
            Notes = "ShowWindow of the task's window: 0 (the simulated window shows nothing)."
        )]
        private static Value WinMode(SimHost h, CicodeTask t, ArgList a)
        {
            var w = Current(h, t);
            if (w == null)
            {
                W.Raise(t, 256);
                return W.I(256);
            }
            return W.I((ushort)A.I(a.Get(0)) < 10 ? 0 : 274);
        }

        [Builtin(
            "WinStyle",
            Doc = "1194000",
            Effects = "the window mode bits 16384 / 32768 / 65536 / 131072 (PageInfo 11)",
            Errors = "274 a style outside 1..4 (returned); 256 no window (returned)",
            Notes = "Style 1 hide horizontal / 2 vertical scroll bars, 3 / 4 disable horizontal / vertical scrolling; mode 0 clears the bit, else sets it. Returns 0."
        )]
        private static Value WinStyle(SimHost h, CicodeTask t, ArgList a)
        {
            var w = Current(h, t);
            if (w == null)
                return W.I(256);
            int bit;
            switch (A.Short(A.I(a.Get(0))))
            {
                case 1:
                    bit = 0x4000;
                    break;
                case 2:
                    bit = 0x8000;
                    break;
                case 3:
                    bit = 0x10000;
                    break;
                case 4:
                    bit = 0x20000;
                    break;
                default:
                    return W.I(274);
            }
            if (A.Short(A.I(a.Get(1))) == 0)
                w.Mode &= ~bit;
            else
                w.Mode |= bit;
            return W.I(0);
        }

        // ------------------------------------------------------------------ printing (no printer)
        [Builtin(
            "WinPrint",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1193995",
            Notes = "Prints the window: the simulated computer has no printer; nothing is printed, 0 returned."
        )]
        private static Value WinPrint(SimHost h, CicodeTask t, ArgList a) => W.I(0);

        [Builtin(
            "WinPrintFile",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1193996",
            Notes = "Prints a file in a window: nothing is printed, 0 returned."
        )]
        private static Value WinPrintFile(SimHost h, CicodeTask t, ArgList a) => W.I(0);

        [Builtin(
            "WinFile",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1193975",
            Notes = "Writes the window as a bitmap file: nothing is written (the renderer is the viewer's), 0 returned."
        )]
        private static Value WinFile(SimHost h, CicodeTask t, ArgList a) => W.I(0);

        [Builtin(
            "WinCopy",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1193974",
            Errors = "274 a width or height scale that is not above 0 (raised with the halt flag, 0 returned)",
            Notes = "Copies the window to the clipboard: nothing is copied, 0 returned. The scale check is the runtime's ('0.0 < x && 0.0 < y', else; before the raise)."
        )]
        private static Value WinCopy(SimHost h, CicodeTask t, ArgList a)
        {
            double x = A.R(a.Get(0)),
                y = A.R(a.Get(1));
            if (!(x > 0.0 && y > 0.0))
                t.RaiseError(274, true);
            return W.I(0);
        }

        // ------------------------------------------------------------------ screen metrics
        /// <summary>GetSystemMetrics of the simulated computer: Windows 10 at 96 dpi with the
        /// simulated screen (one 1920 x 1080 monitor by default) (values: guess, the Windows 10
        /// defaults).</summary>
        internal static int Metric(DisplayState d, int type)
        {
            var m =
                d.Screen.Count > 0
                    ? d.Screen[0]
                    : new Monitor
                    {
                        W = 1920,
                        H = 1080,
                        WorkW = 1920,
                        WorkH = 1040,
                    };
            int vl = 0,
                vt = 0,
                vr = 0,
                vb = 0;
            foreach (var x in d.Screen)
            {
                vl = Math.Min(vl, x.X);
                vt = Math.Min(vt, x.Y);
                vr = Math.Max(vr, x.X + x.W);
                vb = Math.Max(vb, x.Y + x.H);
            }
            switch (type)
            {
                case 0:
                    return m.W; // SM_CXSCREEN
                case 1:
                    return m.H; // SM_CYSCREEN
                case 2:
                case 3:
                case 9:
                case 10:
                case 20:
                case 21:
                    return 17; // scroll bar arrows / thumbs
                case 4:
                    return 23; // SM_CYCAPTION
                case 5:
                case 6:
                    return 1; // SM_CXBORDER / SM_CYBORDER
                case 7:
                case 8:
                    return 3; // SM_CXDLGFRAME / SM_CYDLGFRAME
                case 11:
                case 12:
                case 13:
                case 14:
                    return 32; // icons, cursors
                case 15:
                    return 20; // SM_CYMENU
                case 16:
                    return m.WorkW; // SM_CXFULLSCREEN
                case 17:
                    return m.WorkH - 23; // SM_CYFULLSCREEN: work area minus the caption
                case 19:
                    return 1; // SM_MOUSEPRESENT
                case 28:
                case 34:
                    return 136; // SM_CXMIN / SM_CXMINTRACK
                case 29:
                case 35:
                    return 39; // SM_CYMIN / SM_CYMINTRACK
                case 30:
                    return 36; // SM_CXSIZE
                case 31:
                    return 22; // SM_CYSIZE
                case 32:
                case 33:
                    return 4; // SM_CXFRAME / SM_CYFRAME
                case 36:
                case 37:
                    return 4; // double click rectangle
                case 38:
                case 39:
                    return 75; // icon spacing
                case 43:
                    return 3; // SM_CMOUSEBUTTONS
                case 45:
                case 46:
                    return 2; // SM_CXEDGE / SM_CYEDGE
                case 47:
                    return 160; // SM_CXMINSPACING
                case 48:
                    return 28; // SM_CYMINSPACING
                case 49:
                case 50:
                    return 16; // small icons
                case 51:
                    return 23; // SM_CYSMCAPTION
                case 52:
                case 53:
                    return 22; // SM_CXSMSIZE / SM_CYSMSIZE
                case 54:
                case 55:
                    return 19; // SM_CXMENUSIZE / SM_CYMENUSIZE
                case 56:
                    return 8; // SM_ARRANGE
                case 57:
                    return 160; // SM_CXMINIMIZED
                case 58:
                    return 28; // SM_CYMINIMIZED
                case 59:
                    return vr - vl + 12; // SM_CXMAXTRACK
                case 60:
                    return vb - vt + 12; // SM_CYMAXTRACK
                case 61:
                    return m.WorkW + 16; // SM_CXMAXIMIZED
                case 62:
                    return m.WorkH + 16; // SM_CYMAXIMIZED
                case 63:
                    return 3; // SM_NETWORK
                case 68:
                case 69:
                    return 4; // drag rectangle
                case 71:
                case 72:
                    return 15; // menu check marks
                case 75:
                    return 1; // SM_MOUSEWHEELPRESENT
                case 76:
                    return vl; // SM_XVIRTUALSCREEN
                case 77:
                    return vt;
                case 78:
                    return vr - vl; // SM_CXVIRTUALSCREEN
                case 79:
                    return vb - vt;
                case 80:
                    return d.Screen.Count; // SM_CMONITORS
                case 81:
                    return 1; // SM_SAMEDISPLAYFORMAT
                case 82:
                    return 1; // SM_IMMENABLED
                case 83:
                case 84:
                    return 1; // focus border
                case 92:
                    return 4; // SM_CXPADDEDBORDER
                default:
                    return 0;
            }
        }

        [Builtin(
            "WndInfo",
            Doc = "1194005",
            Errors = "274 a type above 97 (unsigned 16-bit: a negative type too) (raised, 0)",
            Notes = "GetSystemMetrics(type) of the simulated computer: Windows 10 at 96 dpi, the simulated screen (default one 1920 x 1080 monitor, work area 1920 x 1040): 0 / 1 screen size, 16 / 17 full-screen client size, 19 mouse present, 76..79 the virtual screen, 80 the monitor count ... (values for the metrics: guess). Types 0..40 are a 16-bit table read at start (sign-extended), like the runtime's."
        )]
        private static Value WndInfo(SimHost h, CicodeTask t, ArgList a)
        {
            ushort type = (ushort)A.I(a.Get(0));
            if (type > 0x61)
            {
                W.Raise(t, 274);
                return W.I(0);
            }
            int v = Metric(DisplayBuiltins.Of(h), type);
            return W.I(type <= 0x28 ? (short)v : v);
        }

        /// <summary>WndMonitorInfo's answer: monitor 0 = the virtual screen, 1..n the
        /// monitors; another monitor or type: -1.</summary>
        internal static long MonitorInfo(DisplayState d, int monitor, int type)
        {
            if (monitor < 0 || monitor > d.Screen.Count)
                return -1;
            if (monitor == 0)
            {
                switch (type)
                {
                    case 0:
                    case 4:
                        return Metric(d, 76);
                    case 1:
                    case 5:
                        return Metric(d, 77);
                    case 2:
                    case 6:
                        return Metric(d, 78);
                    case 3:
                    case 7:
                        return Metric(d, 79);
                    default:
                        return -1;
                }
            }
            var m = d.Screen[monitor - 1];
            switch (type)
            {
                case 0:
                    return m.X;
                case 1:
                    return m.Y;
                case 2:
                    return m.W;
                case 3:
                    return m.H;
                case 4:
                    return m.WorkX;
                case 5:
                    return m.WorkY;
                case 6:
                    return m.WorkW;
                case 7:
                    return m.WorkH;
                default:
                    return -1;
            }
        }

        [Builtin(
            "WndMonitorInfo",
            Doc = "1194006",
            Notes = "Monitor 0 = the virtual screen (types 0 / 4 x, 1 / 5 y, 2 / 6 width, 3 / 7 height), 1..n the simulated screen's monitors (0..3 the monitor, 4..7 its work area); another monitor or type: -1."
        )]
        private static Value WndMonitorInfo(SimHost h, CicodeTask t, ArgList a) =>
            W.I(MonitorInfo(DisplayBuiltins.Of(h), (ushort)A.I(a.Get(0)), (int)A.Short(A.I(a.Get(1)))));

        [Builtin(
            "WndMonitorInfoEx",
            Doc = "1194007",
            Notes = "WndMonitorInfo of the monitor whose screen profile name is sMonitor (without case; the simulated monitors are named \\\\.\\DISPLAY1 ...), -1 when none."
        )]
        private static Value WndMonitorInfoEx(SimHost h, CicodeTask t, ArgList a)
        {
            var d = DisplayBuiltins.Of(h);
            string name = A.S(a.Get(0));
            for (int i = 0; i < d.Screen.Count; i++)
                if (
                    name.Length > 0
                    && string.Equals(d.Screen[i].Name, name, StringComparison.OrdinalIgnoreCase)
                )
                    return W.I(MonitorInfo(d, i + 1, (int)A.Short(A.I(a.Get(1)))));
            return W.I(-1);
        }
    }
}
