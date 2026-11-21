using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World.Display;

namespace CicodeSim.Builtins
{
    /// <summary>The display built-ins the page animator needs to open and scan a page.</summary>
    public static class DisplayBuiltins
    {
        private static readonly ConditionalWeakTable<SimHost, DisplayState> Own =
            new ConditionalWeakTable<SimHost, DisplayState>();

        /// <summary>The host's display client: its World's, else one of its own (no page).</summary>
        internal static DisplayState Of(SimHost h) =>
            h.World != null ? h.World.Display : Own.GetValue(h, _ => new DisplayState());

        private static Window? Current(SimHost h, CicodeTask t) => Of(h).OpenWindow(Of(h).WindowOf(t));

        private static string Inv(long v) => v.ToString(CultureInfo.InvariantCulture);

        private static string Cut(string s, int n) => s.Length > n ? s.Substring(0, n) : s;

        /// <summary>C atoi: blanks skipped, a sign, then digits (0 when none).</summary>
        internal static long Atoi(string s)
        {
            int i = 0;
            while (
                i < s.Length
                && (
                    s[i] == ' '
                    || s[i] == '\t'
                    || s[i] == '\n'
                    || s[i] == '\r'
                    || s[i] == '\v'
                    || s[i] == '\f'
                )
            )
                i++;
            bool neg = false;
            if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            {
                neg = s[i] == '-';
                i++;
            }
            long v = 0;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9' && v < 0x100000000L)
            {
                v = v * 10 + (s[i] - '0');
                i++;
            }
            v = neg ? -v : v;
            return (int)v;
        }

        private static int WindowArg(SimHost h, CicodeTask t, ArgList a, int k)
        {
            long w = a.Count > k ? A.I(a[k]) : -1;
            return w < 0 ? Of(h).WindowOf(t) : (int)w;
        }

        // ------------------------------------------------------------------ windows / pages
        [Builtin(
            "WinNumber",
            Doc = "1193996",
            Notes = "Empty name: the task's window (TaskContext; 0 for a task outside a page). A name: the window WinSetName gave it (case-sensitive std::map, guess), -1 when none."
        )]
        private static Value WinNumber(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.S(a.Get(0));
            var d = Of(h);
            if (name.Length == 0)
                return W.I(d.WindowOf(t));
            return W.I(d.WindowNames.TryGetValue(name, out int n) ? n : -1);
        }

        [Builtin(
            "PageInfo",
            Doc = "1188754",
            Errors = "256 no open window or no page, or a type outside 0..31 (raised, \"\")",
            Notes = "0 NAME (a leading '!' as a space), 1 ALIAS, 2 TITLE, 3 BGND_FILE, 4 SYMB_FILE, 5 NEXT_PAGE, 6 PREV_PAGE, 31 CON_TYPE (the page .RDB Control record); 7 scans since shown; 8..10 parent / first child / next child window (creation order); 11 mode; 12 / 21 width, 13 / 22 height (the page size); 14 / 15 window x / y; 16 / 17 the window scale (%g, 1 unscaled); 18 flash phase + 1; 19 / 20 '1' inside the animation / communication cycle; 23 the highest AN, 24 the number of ANs; 25 '1' once On Page Shown ran; 26 the page cluster; 27 '0'; 28 / 29 the client left / top; 30 the screen of the window ([MultiMonitors] ScreenProfile's .cts file: ScreenProfile; '' without a profile)."
        )]
        private static Value PageInfo(SimHost h, CicodeTask t, ArgList a)
        {
            long type = A.I(a.Get(0));
            var d = Of(h);
            var w = Current(h, t);
            var p = w?.Page;
            if (w == null || p == null)
            {
                W.Raise(t, 256);
                return W.Empty;
            }
            var r = p.Record;
            string s;
            switch (type)
            {
                case 0:
                    s = r.Name.StartsWith("!", StringComparison.Ordinal) ? " " + r.Name.Substring(1) : r.Name;
                    break;
                case 1:
                    s = r.Alias;
                    break;
                case 2:
                    s = r.Title;
                    break;
                case 3:
                    s = r.BgndFile;
                    break;
                case 4:
                    s = r.SymbFile;
                    break;
                case 5:
                    s = r.NextPage;
                    break;
                case 6:
                    s = r.PrevPage;
                    break;
                case 7:
                    s = Inv(w.Shows);
                    break; // the previous display count: 0 in the first scan
                case 8:
                    s = Inv(w.Parent);
                    break;
                // the window's child list, creation order
                case 9:
                    s = Inv(d.FirstChildOf(w.Number));
                    break;
                case 10:
                    s = Inv(d.NextChildOf(w.Number));
                    break;
                case 11:
                    s = Inv(w.Mode & 0x7fffffff);
                    break;
                case 12:
                case 21:
                    s = Inv((p.Width > 0 ? p.Width : w.W) & 0xffff);
                    break;
                case 13:
                case 22:
                    s = Inv((p.Height > 0 ? p.Height : w.H) & 0xffff);
                    break;
                case 14:
                case 28:
                    s = Inv(w.X);
                    break;
                case 15:
                case 29:
                    s = Inv(w.Y);
                    break;
                // the window's display scale, sprintf "%g"
                case 16:
                    s = Crt.Sprintf("%g", PrintfArg.Float(w.ScaleX));
                    break;
                case 17:
                    s = Crt.Sprintf("%g", PrintfArg.Float(w.ScaleY));
                    break;
                case 18:
                    s = Inv(d.FlashPhase + 1);
                    break;
                case 19:
                    s = w.InAnimation ? "1" : "0";
                    break;
                case 20:
                    s = w.InComms ? "1" : "0";
                    break;
                case 23:
                {
                    int max = 0;
                    foreach (int an in p.Objects.Keys)
                        if (an > max)
                            max = an;
                    s = Inv(max);
                    break;
                }
                case 24:
                    s = Inv(p.Objects.Count);
                    break;
                case 25:
                    s = w.ReadyCountdown == 0 ? "1" : "0";
                    break;
                case 26:
                    s = r.Cluster;
                    break;
                case 27:
                    s = "0";
                    break;
                case 30:
                    s = ScreenProfile.NameOf(h, w);
                    break;
                case 31:
                    s = r.ConType;
                    break;
                default:
                    W.Raise(t, 256);
                    return W.Empty;
            }
            return W.S(Cut(s, Types.StringMax));
        }

        // ------------------------------------------------------------------ page variables
        [Builtin(
            "PageGetInt",
            Doc = "1188747",
            Errors = "269 the window is not open (trapped, 0); 274 an empty label (trapped, 0)",
            Notes = "atoi(label) = -2: the window's scan time. Else the window's integer variable (case-sensitive), 0 when missing. The variables are cleared when the window shows another page (verify)."
        )]
        private static Value PageGetInt(SimHost h, CicodeTask t, ArgList a)
        {
            string label = A.S(a.Get(0));
            var w = Of(h).OpenWindow(WindowArg(h, t, a, 1));
            if (w == null || w.Page == null)
            {
                W.Raise(t, 269);
                return W.I(0);
            }
            if (label.Length == 0)
            {
                W.Raise(t, 274);
                return W.I(0);
            }
            if (Atoi(label) == -2)
                return W.I(w.ScanTime);
            w.NoteVar(label, false, false);
            return W.I(w.IntVars.TryGetValue(label, out long v) ? v : 0);
        }

        [Builtin(
            "PageSetInt",
            Doc = "1188749",
            Effects = "sets a page integer variable; -2 the page scan time",
            Errors = "269 the window is not open; 274 an empty label; 257 a scan time outside 1..60000 (trapped and returned)"
        )]
        private static Value PageSetInt(SimHost h, CicodeTask t, ArgList a)
        {
            string label = A.S(a.Get(0));
            long value = A.I(a.Get(1));
            var w = Of(h).OpenWindow(WindowArg(h, t, a, 2));
            if (w == null || w.Page == null)
            {
                W.Raise(t, 269);
                return W.I(269);
            }
            if (label.Length == 0)
            {
                W.Raise(t, 274);
                return W.I(274);
            }
            if (Atoi(label) == -2)
            {
                // iValue - 1 must be below 60000 (unsigned); [Page]Delay (superseded) taken as 0
                if ((ulong)(uint)(value - 1) >= 60000UL)
                {
                    W.Raise(t, 257);
                    return W.I(257);
                }
                w.ScanTime = (int)value;
                return W.I(0);
            }
            if (w.LockedVars.Contains(Window.VarKey(label, false)))
            {
                w.NoteVar(label, false, false);
                h.Note(t, "page variable " + label + " is locked: PageSetInt ignored");
                return W.I(0);
            }
            bool changed = !w.IntVars.TryGetValue(label, out long old) || old != (int)value;
            w.IntVars[label] = (int)value;
            w.NoteVar(label, false, changed);
            return W.I(0);
        }

        [Builtin(
            "PageGetStr",
            Doc = "1188748",
            Errors = "269 the window is not open (trapped, \"\"); 274 an empty label (trapped, \"\")",
            Notes = "The window's string variable cut to 254 characters, \"\" when missing."
        )]
        private static Value PageGetStr(SimHost h, CicodeTask t, ArgList a)
        {
            string label = A.S(a.Get(0));
            var w = Of(h).OpenWindow(WindowArg(h, t, a, 1));
            if (w == null || w.Page == null)
            {
                W.Raise(t, 269);
                return W.Empty;
            }
            if (label.Length == 0)
            {
                W.Raise(t, 274);
                return W.Empty;
            }
            w.NoteVar(label, true, false);
            return W.S(w.StrVars.TryGetValue(label, out string? v) ? Cut(v, 254) : "");
        }

        [Builtin(
            "PageSetStr",
            Doc = "1188750",
            Effects = "sets a page string variable",
            Errors = "269 the window is not open; 274 an empty label (trapped and returned)"
        )]
        private static Value PageSetStr(SimHost h, CicodeTask t, ArgList a)
        {
            string label = A.S(a.Get(0));
            string value = A.S(a.Get(1));
            var w = Of(h).OpenWindow(WindowArg(h, t, a, 2));
            if (w == null || w.Page == null)
            {
                W.Raise(t, 269);
                return W.I(269);
            }
            if (label.Length == 0)
            {
                W.Raise(t, 274);
                return W.I(274);
            }
            if (w.LockedVars.Contains(Window.VarKey(label, true)))
            {
                w.NoteVar(label, true, false);
                h.Note(t, "page variable " + label + " is locked: PageSetStr ignored");
                return W.I(0);
            }
            bool changed = !w.StrVars.TryGetValue(label, out string? old) || old != value;
            w.StrVars[label] = value;
            w.NoteVar(label, true, changed);
            return W.I(0);
        }

        // ------------------------------------------------------------------ animation points
        [Builtin(
            "DspGetAnCur",
            Doc = "1104671",
            Notes = "The AN of the object whose expression or command runs (TaskContext), -1 outside one; commands see their object's AN too (verify: doc 1104671 says -1)."
        )]
        private static Value DspGetAnCur(SimHost h, CicodeTask t, ArgList a) => W.I(TaskContext.An(t));

        [Builtin(
            "KeyGetCursor",
            Doc = "1106241",
            Notes = "The window's keyboard command cursor AN, -1 without a window or a cursor."
        )]
        private static Value KeyGetCursor(SimHost h, CicodeTask t, ArgList a)
        {
            var w = Current(h, t);
            return W.I(w == null ? -1 : w.CommandCursor);
        }

        [Builtin(
            "DspGetMouseOver",
            Doc = "1104686",
            Errors = "274 no window and an AN of 32000+ (raised, 0)",
            Notes = "-1 = the current AN. 1 when the pointer is in the AN's rectangle including its right and bottom pixel, in the current window; 0 otherwise, for an AN of 0 or less, or an AN not found (error 269 kept, not raised)."
        )]
        private static Value DspGetMouseOver(SimHost h, CicodeTask t, ArgList a)
        {
            long an = a.Count > 0 ? A.I(a[0]) : -1;
            if (an == -1)
                an = TaskContext.An(t);
            if (an <= 0)
                return W.I(0);
            var w = Current(h, t);
            if (w == null)
            {
                if (an >= 32000)
                    W.Raise(t, 274);
                return W.I(0);
            }
            if (!w.MouseInside)
                return W.I(0);
            var o = w.Page?.Get((int)an);
            if (o == null || o.Drawing?.Rect == null)
                return W.I(0);
            return W.I(o.Extent.ContainsInclusive(w.MouseX, w.MouseY) ? 1 : 0);
        }

        /// <summary>DspAnGetMetadata's rule; code = the raised error, 0 ok.</summary>
        internal static string AnMetadata(SimHost h, CicodeTask t, long an, string name, out int code)
        {
            code = 0;
            if (an == -2)
                an = TaskContext.An(t);
            var w = Current(h, t);
            if (w == null || an < 1 || an > 31999)
            {
                code = 274;
                return "";
            }
            var o = w.Page?.Get((int)an);
            if (o == null)
            {
                code = 269;
                return "";
            }
            string? v = o.MetadataValue(name);
            if (v == null)
            {
                code = 289;
                return "";
            }
            return Cut(v, Types.StringMax);
        }

        [Builtin(
            "DspAnGetMetadata",
            Doc = "1104640",
            Errors = "274 no window or an AN outside 1..31999; 269 the object not found; 289 the metadata name not found (raised, \"\")",
            Notes = "-2 = the current AN. Names compare without case (guess: the runtime lowers the key; verify). Value cut to 255."
        )]
        private static Value DspAnGetMetadata(SimHost h, CicodeTask t, ArgList a)
        {
            string v = AnMetadata(h, t, A.I(a.Get(0)), A.S(a.Get(1)), out int code);
            if (code != 0)
                W.Raise(t, code);
            return W.S(v);
        }

        // ------------------------------------------------------------------ associations
        [Builtin(
            "Ass",
            Doc = "1192655",
            Effects = "stores a super genie association for the target window's next page",
            Errors = "557 a tag of 255+ characters (raised and returned); the store's tag check (returned, the association stored all the same): 424 an unknown name without a '.', 289 a dotted name that is no variable tag or Equip.Item (an equipment name: the live runtime's 289 on the SA faceplates), 403 an unknown cluster or a name starting with '.', 404 a 'Cluster.' prefix that is not the given cluster, 417 no cluster in a project of several",
            Notes = "hWin = a window, -2 the next new window or page, -3 the current window at its next page change. nArg trimmed. A tag starting with ' is a literal (any mode). Else the entry keeps the cluster (the given one, the 'Cluster.' prefix, or the tag configuration's: the project's only cluster) and the tag without its prefix. An empty tag stores nothing (0). A later Ass of the name for the same window or for -2 replaces that entry."
        )]
        private static Value Ass(SimHost h, CicodeTask t, ArgList a)
        {
            var d = Of(h);
            int target = (int)A.I(a.Get(0));
            string arg = A.S(a.Get(1));
            string tag = A.S(a.Get(2));
            int mode = (int)A.I(a.Get(3));
            string cluster = A.S(a.Get(4));
            int r = d.Ass(target, arg, tag, mode, cluster, d.WindowOf(t));
            if (r == 557)
                W.Raise(t, 557);
            return W.I(r);
        }

        [Builtin(
            "AssMetadata",
            Doc = "1192668",
            Effects = "Ass(hWin, name, value, 0) for every metadata entry of the AN",
            Errors = "303 an AN outside 1..31999; 269 the object not found (returned)",
            Notes = "nAn -2 = the keyboard command cursor's AN. Returns the last store's result, 0 when the object has no metadata. Every entry goes through Ass's store (the same tag check and literal rule); hWin -3 = the current window, as for Ass (cmp bx, -3 -> the current window; drops it); an entry with an empty value deletes the pending association of that name (Ass itself returns before the store)."
        )]
        private static Value AssMetadata(SimHost h, CicodeTask t, ArgList a)
        {
            var d = Of(h);
            int target = (int)A.I(a.Get(0));
            long an = a.Count > 1 ? A.I(a[1]) : -2;
            var w = Current(h, t);
            if (an == -2)
                an = w == null ? -1 : w.CommandCursor;
            if (an < 1 || an > 31999)
                return W.I(303);
            var o = w?.Page?.Get((int)an);
            if (o == null)
                return W.I(269);
            int r = 0;
            foreach (var kv in o.Metadata)
                r = d.Ass(target, kv.Key, kv.Value, 0, "", d.WindowOf(t), emptyRemoves: true);
            return W.I(r);
        }

        [Builtin(
            "AssInfo",
            Doc = "1192665",
            Errors = "274 the argument is not associated (no window: \"\" without an error); the reference's cluster errors 403 / 404; 348 an unknown type; types 1+: the tag's property errors (424 tag not found) (raised, \"\")",
            Notes = "sArg = an argument name / number, '?name?', or a partial reference ('?Equip?.Item', 'a?1?b'): the association's tag between the text before the first and after the last '?'; an '[n]' is the array index. sType = a number as text or a property name. 0 / TagName, 17 / ClusterName, 18 / FullName, 19 / AssFullName: the runtime's texts: the reference's configured tag name (an Equip.Item as its variable tag), else the reference as given, with 'Cluster.' for 17 / 18 / 19; 0 / 17 / 18 answer \"\" for an unresolved reference that is not partial whose association stored an error (an equipment name), 19 answers 'Cluster.reference' even then (doc: 'even if the tag is not resolved'). Other types: TagGetProperty's name of the type (1 Units ... 16 Description, 20 Address, 21 NetworkNumber, 22 Equipment, 23 Type, 24 Historian, 25 Item, 26..33 Custom1..8) of the reference, iCachedMode as TagGetProperty's; types the tables cannot answer (9, 12, 13, 21, 24) give 348 (live, an SA tag: 21 '2', 24 '0'). A local variable has no cluster (17 \"\", 18 / 19 its name). A literal association answers its text for 0 / TagName and \"\" (no error) for every other type."
        )]
        private static Value AssInfo(SimHost h, CicodeTask t, ArgList a) => AssInfoCore(h, t, a, "AssInfo");

        /// <summary>AssInfo and AssInfoEx.</summary>
        internal static Value AssInfoCore(SimHost h, CicodeTask t, ArgList a, string fn)
        {
            string arg = A.S(a.Get(0));
            string type = A.S(a.Get(1));
            long cached = a.Count > 2 && !a[2].IsNothing ? A.I(a[2]) : -1;
            var w = Current(h, t);
            if (w == null)
                return W.Empty;
            var e = AssRules.Active(w, arg);
            if (e == null)
            {
                W.Raise(t, 274);
                return W.Empty;
            }
            var world = h.World;
            var r = AssRules.Resolve(world, arg, e);
            if (r.Error != 0)
            {
                W.Raise(t, r.Error);
                return W.Empty;
            }
            string? prop = AssRules.PropertyOf(type, out int k);
            bool Is(string name) => string.Equals(type, name, StringComparison.OrdinalIgnoreCase);
            if (e.Literal)
                return Is("TagName") || k == 0 ? W.S(Cut(r.Tag, Types.StringMax)) : W.Empty;
            int mode =
                Is("TagName") || k == 0 ? 2
                : Is("ClusterName") || k == 17 ? 1
                : Is("FullName") || k == 18 ? 3
                : Is("AssFullName") || k == 19 ? 7
                : 0;
            if (mode != 0)
                return W.S(AssRules.Text(world, r, AssRules.IsPartial(arg), mode, Tags.QualityError));
            if (prop == null)
            {
                W.Raise(t, 348);
                return W.Empty;
            }
            string target =
                r.Index >= 0 ? r.Tag + "[" + r.Index.ToString(CultureInfo.InvariantCulture) + "]" : r.Tag;
            return Tags.PropertyCall(
                h,
                t,
                target,
                prop,
                cached,
                r.Cluster,
                d => d.Property(prop) ?? (k > 0 ? d.Info(k) : null),
                fn
            );
        }

        /// <summary>An association argument ('Equip', '?Equip?', '?Equip?.Item', '5') as the
        /// reference of the window's association (AssRules.Compose: the text before the first
        /// and after the last '?' around the association's tag), null when not associated.</summary>
        internal static string? Resolve(Window w, string arg, out bool literal, out string cluster)
        {
            literal = false;
            cluster = "";
            var e = AssRules.Active(w, arg);
            if (e == null)
                return null;
            literal = e.Literal;
            cluster = e.Cluster;
            string s = AssRules.Compose(arg, e, out int index);
            return index >= 0 ? s + "[" + index.ToString(CultureInfo.InvariantCulture) + "]" : s;
        }

        // ------------------------------------------------------------------ _LibControl_*
        /// <summary>IsError: the task's error, read and cleared.</summary>
        private static int TakeError(CicodeTask t)
        {
            int e = t.Error;
            t.Error = 0;
            return e;
        }

        /// <summary>Put the caller's earlier IsError() code back after a successful read.</summary>
        private static void RestoreError(CicodeTask t, int saved)
        {
            if (saved != 0 && !t.Unwinding)
                t.Error = saved;
        }

        private static long AnOfControl(SimHost h, CicodeTask t, string id) =>
            A.I(PageGetInt(h, t, ArgList.Of(Value.Str(Cut(">AN" + id, 255)), Value.Long(-1))));

        // The read's own error (274 / 269 / 289 from, 274 for a value that is no
        // number) is raised and read back inside the getter's ErrSet(1): the
        // caller never sees it (the default, IsError() 0), so it is not reported either; the
        // library reads names that are not set yet on purpose, every scan.
        private static Value GetStr(SimHost h, CicodeTask t, long an, string field, string def)
        {
            t.ErrSetLevel++; // ErrSet(1): cl = 1
            try
            {
                if (an != 0)
                {
                    int saved = TakeError(t);
                    string v = AnMetadata(h, t, an, field, out int code);
                    if (code == 0)
                    {
                        RestoreError(t, saved);
                        return W.S(v);
                    }
                }
                return W.S(def);
            }
            finally
            {
                if (t.ErrSetLevel > 0)
                    t.ErrSetLevel--; // ErrSet(0)
            }
        }

        private static Value GetInt(SimHost h, CicodeTask t, long an, string field, long def)
        {
            t.ErrSetLevel++;
            try
            {
                if (an != 0)
                {
                    int saved = TakeError(t);
                    string p = AnMetadata(h, t, an, field, out int code);
                    long v = 0;
                    if (code == 0)
                    {
                        string s = A.SkipSpace(p);
                        char c0 = s.Length > 0 ? s[0] : '\0';
                        if (!(char.IsDigit(c0) || c0 == '-' || c0 == '+' || c0 == '\0'))
                            code = 274;
                        else
                            v = Conv.StrToInt(s, out _);
                    }
                    if (code == 0)
                    {
                        def = v;
                        RestoreError(t, saved);
                    }
                }
                return W.I(def);
            }
            finally
            {
                if (t.ErrSetLevel > 0)
                    t.ErrSetLevel--;
            }
        }

        [Builtin(
            "_LibControl_GetANName",
            Notes = "PageGetInt(\">AN\" + id, -1): the AN a Library_Controls control registered for itself."
        )]
        private static Value LibControlGetANName(SimHost h, CicodeTask t, ArgList a) =>
            W.I(AnOfControl(h, t, A.S(a.Get(0))));

        [Builtin(
            "_LibControl_GetStr",
            Notes = "DspAnGetMetadata(PageGetInt(\">AN\" + id), field) under ErrSet(1); any error -> the default and IsError() 0 afterwards; a successful read restores the caller's earlier IsError() code. Never halts. Neither the read's own error nor the restored code is reported again (the runtime logs them only with the DEBUG parameter 2)."
        )]
        private static Value LibControlGetStr(SimHost h, CicodeTask t, ArgList a) =>
            GetStr(h, t, AnOfControl(h, t, A.S(a.Get(0))), A.S(a.Get(1)), A.S(a.Get(2)));

        [Builtin("_LibControl_GetStr_ByAN", Notes = "_LibControl_GetStr with the AN given.")]
        private static Value LibControlGetStrByAn(SimHost h, CicodeTask t, ArgList a) =>
            GetStr(h, t, A.I(a.Get(0)), A.S(a.Get(1)), A.S(a.Get(2)));

        [Builtin(
            "_LibControl_GetInt",
            Notes = "atol(DspAnGetMetadata(PageGetInt(\">AN\" + id), field)) under ErrSet(1): a value whose first non-blank is not a digit, '-', '+' or the end raises 274 (swallowed); any error -> the default, IsError() 0 afterwards; a successful read restores the caller's earlier IsError() code. Neither the read's own error nor the restored code is reported again (the runtime logs them only with the DEBUG parameter 2)."
        )]
        private static Value LibControlGetInt(SimHost h, CicodeTask t, ArgList a) =>
            GetInt(h, t, AnOfControl(h, t, A.S(a.Get(0))), A.S(a.Get(1)), A.I(a.Get(2)));

        [Builtin("_LibControl_GetInt_ByAN", Notes = "_LibControl_GetInt with the AN given.")]
        private static Value LibControlGetIntByAn(SimHost h, CicodeTask t, ArgList a) =>
            GetInt(h, t, A.I(a.Get(0)), A.S(a.Get(1)), A.I(a.Get(2)));
    }
}
