using System;
using System.Collections.Generic;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World.Display;

namespace CicodeSim.Builtins
{
    /// <summary>Animation point lookups and extents.</summary>
    public static class DisplayAns
    {
        private static DisplayState D(SimHost h) => DisplayBuiltins.Of(h);

        /// <summary>An AN argument as the runtime takes it (a 16-bit short), -2 = the task's AN.</summary>
        internal static int AnArg(CicodeTask t, in Value v, bool minusTwo = true)
        {
            int an = (int)A.Short(A.I(v));
            return minusTwo && an == -2 ? TaskContext.An(t) : an;
        }

        /// <summary>(ushort)(an - 1) &lt;= 0x7cfe: an AN of 1..31999.</summary>
        internal static bool AnOk(int an) => (ushort)(an - 1) <= 0x7cfe;

        private static long Arg(ArgList a, int k, long dflt) =>
            a.Count > k && !a[k].IsNothing ? A.I(a[k]) : dflt;

        /// <summary>The objects of a page under <paramref name="parent"/> (0 = the top level), in
        /// page order.</summary>
        internal static IEnumerable<AnObject> ChildrenOf(PageInstance p, int parent) => p.ChildList(parent);

        /// <summary>The current extent of an AN: its configured extent moved and scaled by its
        /// animation this scan and DspAnMove's offset.</summary>
        internal static Rect CurrentExtent(DisplayState d, Window w, AnObject o)
        {
            var e = o.Extent;
            double l = e.L,
                tp = e.T,
                r = e.R,
                b = e.B;
            var m = d.Runtime.Motion(w.Number, o.An);
            if (m != null)
            {
                var mv = m.Value;
                l = mv.Ox + (l - mv.Ox) * mv.Sx + mv.Dx;
                r = mv.Ox + (r - mv.Ox) * mv.Sx + mv.Dx;
                tp = mv.Oy + (tp - mv.Oy) * mv.Sy + mv.Dy;
                b = mv.Oy + (b - mv.Oy) * mv.Sy + mv.Dy;
            }
            return new Rect(
                (int)(Math.Min(l, r) + o.MovedX),
                (int)(Math.Min(tp, b) + o.MovedY),
                (int)(Math.Max(l, r) + o.MovedX),
                (int)(Math.Max(tp, b) + o.MovedY)
            );
        }

        /// <summary>The page shapes (line, freehand, polyline, pipe, rectangle, polygon, ellipse)
        /// whose reported extent is the drawn one grown by a pixel each side.</summary>
        private static readonly HashSet<int> DrawnShapes = new HashSet<int>
        {
            0x01,
            0x02,
            0x0A,
            0x0B,
            0x0F,
            0x10,
            0x11,
        };

        /// <summary>The extent DspGetAnExtent reports.</summary>
        internal static Rect ReportedExtent(DisplayState d, Window w, AnObject o)
        {
            var r = CurrentExtent(d, w, o);
            if (o.OwnExtent != null || CicodeExtents.Follows(o) || !DrawnShapes.Contains(o.Type))
                return r;
            return new Rect(r.L - 1, r.T - 1, r.R + 1, r.B + 1);
        }

        /// <summary>The current position of an AN (+4 / +8): its anchor moved by its animation and
        /// DspAnMove.</summary>
        internal static (int X, int Y) CurrentPos(DisplayState d, Window w, AnObject o)
        {
            double x = o.PosX,
                y = o.PosY;
            var m = d.Runtime.Motion(w.Number, o.An);
            if (m != null)
            {
                x += m.Value.Dx;
                y += m.Value.Dy;
            }
            return ((int)x + o.MovedX, (int)y + o.MovedY);
        }

        private static bool Hidden(DisplayState d, Window w, AnObject o)
        {
            if (o.ShownByCicode == false)
                return true;
            var m = d.Runtime.Motion(w.Number, o.An);
            return m != null ? m.Value.Hidden : o.Hidden;
        }

        // ------------------------------------------------------------------ lookup by name
        /// <summary>DspGetAnFromNameRelative's rule: the AN of the
        /// object a dotted path names, -1 when none.</summary>
        internal static int FromName(SimHost h, CicodeTask t, int nAn, string path)
        {
            if (path.Length == 0)
                return -1;
            var w = DisplayWindows.Current(h, t);
            var p = w?.Page;
            if (w == null || p == null)
                return -1;
            // the "..\" levels: every occurrence counted, the text advanced 3 each time
            int k = 0,
                pos = 0;
            while (path.IndexOf("..\\", pos, StringComparison.Ordinal) >= 0)
            {
                pos += 3;
                k++;
                if (pos > path.Length)
                {
                    pos = path.Length;
                    break;
                }
            }
            string rest = path.Substring(Math.Min(pos, path.Length));
            if (rest.Length > 0 && rest[0] == '.')
                rest = rest.Substring(1);
            else if (k == 0 && nAn == -1)
                return Descend(p, null, rest);
            int start = nAn;
            if (nAn == -1)
            {
                start = TaskContext.An(t);
                if (start == -1)
                    start = w.CommandCursor;
                if (start == -1)
                    return -1;
            }
            var obj = p.Get(start);
            if (obj == null)
                return -1;
            // a group starts at itself, a leaf at its group (the page root for a top-level one)
            // with one level less (doc 1130136: '.Object1' from a member is its group's
            // Object1; '..\..\GroupA.Object2' from a member of a top-level group is the root's)
            AnObject? cur = obj;
            int levels = k;
            if (obj.Type != 8)
            {
                cur = obj.Parent > 0 ? p.Get(obj.Parent) : null;
                levels = k != 0 ? k - 1 : 0;
            }
            // climb: each level the parent, the page root the last one (past the
            // root = no match)
            for (; levels > 0; levels--)
            {
                if (cur == null)
                    return -1;
                cur = cur.Parent > 0 ? p.Get(cur.Parent) : null;
            }
            return Descend(p, cur, rest);
        }

        /// <summary>The dotted path below an object (null = the page root): each '.' segment the
        /// name of a child (empty segments skipped), every segment but the last a group
        /// (tc 8). Names compare without case (guess: as DspAnSetName's sibling check).</summary>
        private static int Descend(PageInstance p, AnObject? from, string path)
        {
            var segs = path.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length == 0)
                return -1;
            int parent = from?.An ?? 0;
            for (int i = 0; i < segs.Length; i++)
            {
                AnObject? hit = null;
                foreach (var c in ChildrenOf(p, parent))
                    if (string.Equals(c.Name, segs[i], StringComparison.OrdinalIgnoreCase))
                    {
                        hit = c;
                        break;
                    }
                if (hit == null)
                    return -1;
                if (i == segs.Length - 1)
                    return hit.An;
                if (hit.Type != 8)
                    return -1;
                parent = hit.An;
            }
            return -1;
        }

        [Builtin(
            "DspGetAnFromName",
            Doc = "1104675",
            Notes = "DspGetAnFromNameRelative(-1, sName): a path without '..\\' or a leading '.' is searched from the page's top level (Group.Name), else relative to the task's AN (else the keyboard cursor). -1 when not found, no window or page. Names come from the page's drawing summary."
        )]
        private static Value DspGetAnFromName(SimHost h, CicodeTask t, ArgList a) =>
            W.I(FromName(h, t, -1, A.S(a.Get(0))));

        [Builtin(
            "DspGetAnFromNameRelative",
            Doc = "1104676",
            Notes = "k = the number of '..\\'; a leading '.' (after them) is dropped. k = 0 and nAN -1 without the '.': from the page's top level. Else from nAN (-1: the task's AN, else the keyboard cursor; none: -1): a group (tc 8) starts at itself and climbs k levels, a leaf starts at its group (the page root for a top-level one) and climbs k - 1 (doc 1130136's examples; the page root is the last level: verify); then each '.' segment names a child, every one but the last a group. The object's AN, -1 on any miss."
        )]
        private static Value DspGetAnFromNameRelative(SimHost h, CicodeTask t, ArgList a) =>
            W.I(FromName(h, t, (int)A.Short(A.I(a.Get(0))), A.S(a.Get(1))));

        [Builtin(
            "DspGetNameFromAn",
            Doc = "1104689",
            Notes = "The object's name, \"\" for an AN of 0 or less, no window / page, an unknown AN or an object without a name."
        )]
        private static Value DspGetNameFromAn(SimHost h, CicodeTask t, ArgList a)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            if (an <= 0)
                return W.Empty;
            var o = DisplayWindows.Current(h, t)?.Page?.Get(an);
            string s = o?.Name ?? "";
            return W.S(s.Length > 254 ? s.Substring(0, 254) : s);
        }

        [Builtin(
            "DspAnSetName",
            Doc = "1104647",
            Effects = "renames the object (page instance state)",
            Errors = "274 an AN below 1, an empty name, a name starting with a digit or holding one of <>:\"/\\|?*. (returned); 269 no window, page or object, or a sibling of that name (without case) (returned)"
        )]
        private static Value DspAnSetName(SimHost h, CicodeTask t, ArgList a)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            string name = A.S(a.Get(1));
            if (an < 1 || name.Length == 0)
                return W.I(274);
            if (name[0] >= '0' && name[0] <= '9')
                return W.I(274);
            foreach (char c in name)
                if ("<>:\"/\\|?*.".IndexOf(c) >= 0)
                    return W.I(274);
            var p = DisplayWindows.Current(h, t)?.Page;
            var o = p?.Get(an);
            if (p == null || o == null)
                return W.I(269);
            foreach (var s in ChildrenOf(p, o.Parent))
                if (
                    s.An != o.An
                    && s.Name.Length > 0
                    && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)
                )
                    return W.I(269);
            o.Name = name;
            o.NameByCicode = true;
            return W.I(0);
        }

        // ------------------------------------------------------------------ the object tree
        [Builtin(
            "DspGetParentAn",
            Doc = "1104691",
            Errors = "274 no window or page, or the object not found (raised, 0)",
            Notes = "The parent group's AN (1), 0 for a top-level object."
        )]
        private static Value DspGetParentAn(SimHost h, CicodeTask t, ArgList a)
        {
            var o = DisplayWindows.Current(h, t)?.Page?.Get((int)A.Short(A.I(a.Get(0))));
            if (o == null)
            {
                W.Raise(t, 274);
                return W.I(0);
            }
            return W.I(o.Parent > 0 ? o.Parent : 0);
        }

        [Builtin(
            "DspGetAnFirst",
            Doc = "1104674",
            Notes = "The first AN of the page (record order), -1 when none or no page."
        )]
        private static Value DspGetAnFirst(SimHost h, CicodeTask t, ArgList a)
        {
            var p = DisplayWindows.Current(h, t)?.Page;
            return W.I(p != null && p.Order.Count > 0 ? p.Order[0] : -1);
        }

        [Builtin(
            "DspGetAnNext",
            Doc = "1104680",
            Notes = "The AN after AN in page order, -1 at the end, for an AN below 1 or one not on the page."
        )]
        private static Value DspGetAnNext(SimHost h, CicodeTask t, ArgList a)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            var p = DisplayWindows.Current(h, t)?.Page;
            if (an < 1 || p == null)
                return W.I(-1);
            int k = p.Order.IndexOf(an);
            return W.I(k >= 0 && k + 1 < p.Order.Count ? p.Order[k + 1] : -1);
        }

        /// <summary>DspAnInfo type 3: the drawing class of a record type code;
        /// -1 for a type code outside the table.</summary>
        internal static int ClassOf(int tc)
        {
            switch (tc)
            {
                case 0:
                case 7:
                    return 11;
                case 1:
                    return 12;
                case 2:
                    return 13;
                case 3:
                case 0xe:
                case 0xf:
                    return 14;
                case 4:
                case 0x11:
                    return 15;
                case 5:
                    return 3;
                case 6:
                    return 16;
                case 8:
                    return 17;
                case 9:
                    return 18;
                case 0xa:
                case 0x10:
                    return 19;
                case 0xb:
                    return 21;
                case 0xc:
                    return 22;
                case 0xd:
                    return 23;
                case 0x13:
                    return 30;
                case 0x1000:
                    return 24;
                case 0x1001:
                    return 7;
                case 0x1002:
                    return 2;
                case 0x1003:
                    return 6;
                case 0x1004:
                    return 25;
                case 0x1005:
                case 0x1006:
                    return 3;
                case 0x1007:
                    return 4;
                case 0x1008:
                    return 1;
                case 0x1009:
                    return 26;
                case 0x100b:
                    return 27;
                case 0x100d:
                    return 5;
                case 0x100e:
                    return 28;
                case 0x100f:
                    return 8;
                case 0x1010:
                    return 29;
                case 0x1011:
                    return 10;
                default:
                    return -1;
            }
        }

        /// <summary>DspAnInfo type 0: the animation class (colour 1, bar 2, text 3, symbol 4, AN
        /// symbol 5, trend 6, button 7, slider 8, plot 9, none 0) (doc: 1104641; per class: guess).</summary>
        private static int AnimationOf(int tc)
        {
            switch (tc)
            {
                case 0x1008:
                    return 1;
                case 0x1002:
                    return 2;
                case 5:
                case 0x1005:
                case 0x1006:
                case 0x1004:
                    return 3;
                case 0x1007:
                case 7:
                case 0xc:
                    return 4;
                case 0x100d:
                    return 5;
                case 0x1003:
                    return 6;
                case 0x1001:
                    return 7;
                case 0x100f:
                    return 8;
                default:
                    return 0;
            }
        }

        [Builtin(
            "DspAnInfo",
            Doc = "1104641",
            Errors = "274 no window or an AN outside 1..31999 (raised, \"\")",
            Notes = "-2 = the task's AN; an unknown object: \"\". Type 3: the class of the record type code (0/7 bitmap 11, 1 line 12, 2 free hand 13, 3/0xe/0xf rectangle 14, 4/0x11 ellipse 15, 5 text 3, 6 spark 16, 8 group 17, 9 metafile 18, 0xa/0x10 polyline 19, 0xb pipe 21, 0xc symbol set 22, 0xd ActiveX 23, 0x13 web content 30, 0x1000 basic AN 24, 0x1001 button 7, 0x1002 bar 2, 0x1003 trend 6, 0x1004 number 25, 0x1005/0x1006 text 3, 0x1007 symbol 4, 0x1008 colour 1, 0x1009 advanced 26, 0x100b keyboard 27, 0x100d animated symbol 5, 0x100e size/move 28, 0x100f slider 8, 0x1010 touch 29, 0x1011 rich text 10); another type code: the object's own answer, as type 0. Type 0: the animation class (colour 1, bar 2, text 3, symbol 4, AN symbol 5, trend 6, button 7, slider 8, nothing 0; guess per class); 1: its state (what Cicode drew there: a text's font handle, a symbol's name), 2: its text (the text or button Cicode drew there, else the drawing's text; a drawn text or button on an AN that is no record of the page too) (guess); other types \"\"."
        )]
        private static Value DspAnInfo(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            int an = AnArg(t, a.Get(0));
            var w = DisplayWindows.Current(h, t);
            if (w == null || !AnOk(an))
            {
                W.Raise(t, 274);
                return W.Empty;
            }
            var o = w.Page?.Get(an);
            var drawn = w.DrawnAt(an);
            long type = A.Short(A.I(a.Get(1)));
            // the text a text or a button Cicode drew there (Include page.ci _PageMenuFn reads its
            // DspButtonFn button's caption with DspAnInfo(KeyGetCursor(), 2), also on a
            // dynamic AN that is no record of the page)
            bool drawnText = drawn != null && (drawn.Kind == "text" || drawn.Kind == "button");
            if (o == null)
            {
                if (type != 2 || !drawnText)
                    return W.Empty;
                return W.S(drawn!.Text.Length > 255 ? drawn.Text.Substring(0, 255) : drawn.Text);
            }
            int tc = o.Drawing?.Type ?? o.Type;
            switch (type)
            {
                case 3:
                {
                    int c = ClassOf(tc);
                    return W.S(W.Inv(c >= 0 ? c : AnimationOf(tc)));
                }
                case 0:
                    return W.S(W.Inv(AnimationOf(tc)));
                case 1:
                    if (drawn == null)
                        return W.Empty;
                    return W.S(drawn.Kind == "text" ? W.Inv(drawn.Font) : drawn.Text);
                case 2:
                {
                    string s = drawnText ? drawn!.Text : o.Drawing?.Text?.Off ?? "";
                    return W.S(s.Length > 255 ? s.Substring(0, 255) : s);
                }
                default:
                    return W.Empty;
            }
        }

        [Builtin(
            "DspAnGetPrivilege",
            Doc = "1104640",
            Errors = "274 no window or an AN above 31999 (raised, 0)",
            Notes = "The object's privilege (its PRIV), 0 for an unknown object or none."
        )]
        private static Value DspAnGetPrivilege(SimHost h, CicodeTask t, ArgList a) => PrivArea(h, t, a, true);

        [Builtin(
            "DspAnGetArea",
            Doc = "1104636",
            Errors = "274 no window or an AN above 31999 (raised, 0)",
            Notes = "The object's area (its AREA), 0 for an unknown object or none."
        )]
        private static Value DspAnGetArea(SimHost h, CicodeTask t, ArgList a) => PrivArea(h, t, a, false);

        private static Value PrivArea(SimHost h, CicodeTask t, ArgList a, bool priv)
        {
            var d = D(h);
            int an = (ushort)A.I(a.Get(0));
            var w = DisplayWindows.Current(h, t);
            if (w == null || an > 31999)
            {
                W.Raise(t, 274);
                return W.I(0);
            }
            if (w.Page?.Get(an) == null)
                return W.I(0);
            var m = d.Runtime.Motion(w.Number, an);
            return W.I(
                m == null ? 0
                : priv ? m.Value.Priv
                : m.Value.Area
            );
        }

        [Builtin(
            "DspIsVisible",
            Errors = "274 no window or an AN above 31999 (raised, 1)",
            Notes = "-2 = the task's AN. 1 when the object shows (its animation and DspShow), 0 when hidden; 1 for an unknown object."
        )]
        private static Value DspIsVisible(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            int an = AnArg(t, a.Get(0));
            var w = DisplayWindows.Current(h, t);
            if (w == null || (ushort)an > 31999)
            {
                W.Raise(t, 274);
                return W.I(1);
            }
            var o = w.Page?.Get(an);
            if (o == null)
                return W.I(1);
            return W.I(Hidden(d, w, o) ? 0 : 1);
        }

        [Builtin(
            "DspShow",
            Effects = "shows or hides the object (page instance state)",
            Errors = "274 no window, an AN above 31999 (raised), the object not found or already shown / hidden as asked (returned)",
            Notes = "-2 = the task's AN. Returns 0 when the object's state changed; an object already in the asked state is left alone and gives 274 (only a state that differs from the runtime's current one is set, the other path ends at 'return 0x112')."
        )]
        private static Value DspShow(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            int an = AnArg(t, a.Get(0));
            var w = DisplayWindows.Current(h, t);
            if (w == null || (ushort)an > 31999)
            {
                W.Raise(t, 274);
                return W.I(274);
            }
            var o = w.Page?.Get(an);
            if (o == null)
                return W.I(274);
            bool show = A.Short(A.I(a.Get(1))) != 0;
            if (Hidden(d, w, o) != show)
                return W.I(274);
            o.ShownByCicode = show;
            w.DrawnSerial++;
            return W.I(0);
        }

        // ------------------------------------------------------------------ geometry
        private static void Out4(CicodeTask t, ArgList a, int top, int left, int bottom, int right)
        {
            W.Out(t, a.Get(1), top);
            W.Out(t, a.Get(2), left);
            W.Out(t, a.Get(3), bottom);
            W.Out(t, a.Get(4), right);
        }

        [Builtin(
            "DspGetAnExtent",
            Doc = "1104672",
            Errors = "274 no window or an AN outside 1..31999 (raised and returned, all 0); 269 no page (returned, all 0)",
            Notes = "Writes Top, Left, Bottom, Right (in that argument order) of the object's CURRENT extent (moved and scaled by its animation, DspAnMove; a drawn shape's grown by a pixel each side, the area it draws: SA NavigationLink.ci 'DspGetAnWidth returns 2 pixels larger') divided by the window's scale; an unknown object: all 0, returns 0 (the lookup leaves the rectangle empty and the error 0). (A server process gives 408: never the simulator.)"
        )]
        private static Value DspGetAnExtent(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            int an = (int)A.Short(A.I(a.Get(0)));
            var w = DisplayWindows.Current(h, t);
            if (w == null || !AnOk(an))
            {
                W.Raise(t, 274);
                Out4(t, a, 0, 0, 0, 0);
                return W.I(274);
            }
            if (w.Page == null)
            {
                Out4(t, a, 0, 0, 0, 0);
                return W.I(269);
            }
            var o = w.Page.Get(an);
            if (o == null)
            {
                Out4(t, a, 0, 0, 0, 0);
                return W.I(0);
            }
            var r = ReportedExtent(d, w, o);
            Out4(
                t,
                a,
                (int)(r.T / w.ScaleY),
                (int)(r.L / w.ScaleX),
                (int)(r.B / w.ScaleY),
                (int)(r.R / w.ScaleX)
            );
            return W.I(0);
        }

        [Builtin(
            "DspGetAnRawExtent",
            Doc = "1104673",
            Errors = "274 no window or an AN outside 1..31999 (raised and returned, all 0)",
            Notes = "Top, Left, Bottom, Right of the object's configured extent (the drawing's rect1 before animation; a DspAnNew AN its own point); an unknown object or no page: all 0, returns 0."
        )]
        private static Value DspGetAnRawExtent(SimHost h, CicodeTask t, ArgList a)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            var w = DisplayWindows.Current(h, t);
            if (w == null || !AnOk(an))
            {
                W.Raise(t, 274);
                Out4(t, a, 0, 0, 0, 0);
                return W.I(274);
            }
            var o = w.Page?.Get(an);
            if (o == null)
            {
                Out4(t, a, 0, 0, 0, 0);
                return W.I(0);
            }
            var e = o.Extent;
            Out4(t, a, e.T, e.L, e.B, e.R);
            return W.I(0);
        }

        [Builtin(
            "DspAnGetPos",
            Doc = "1104639",
            Errors = "274 no window or page, an AN outside 1..31999, the object not found or a mode other than 0 / 1 (raised and returned; X and Y not written)",
            Notes = "-2 = the task's AN. Mode 0: the object's current position (its anchor moved by its animation and DspAnMove) divided by the window's scale; a hidden object 0, 0. Mode 1: the configured position (the drawing's anchor)."
        )]
        private static Value DspAnGetPos(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            int an = AnArg(t, a.Get(0));
            long mode = A.Short(Arg(a, 3, 0));
            var w = DisplayWindows.Current(h, t);
            var o = w != null && AnOk(an) ? w.Page?.Get(an) : null;
            if (w == null || o == null || (mode != 0 && mode != 1))
            {
                W.Raise(t, 274);
                return W.I(274);
            }
            int x,
                y;
            if (mode == 1)
            {
                x = o.RawPosX;
                y = o.RawPosY;
            }
            else if (Hidden(d, w, o))
            {
                x = 0;
                y = 0;
            }
            else
            {
                var p = CurrentPos(d, w, o);
                x = (int)(p.X / w.ScaleX);
                y = (int)(p.Y / w.ScaleY);
            }
            W.Out(t, a.Get(1), x);
            W.Out(t, a.Get(2), y);
            return W.I(0);
        }

        /// <summary>DspAnMove's offset update: the object and what Cicode drew on it move.</summary>
        private static void MoveTo(DisplayState d, Window w, AnObject o, int x, int y)
        {
            var p = CurrentPos(d, w, o);
            o.MovedX += x - p.X;
            o.MovedY += y - p.Y;
            if (x != p.X || y != p.Y)
                (d.Controller as ISymbolPlacer)?.SymbolMoved(w, o.An, x - p.X, y - p.Y);
            var c = w.DrawnAt(o.An);
            if (c != null)
            {
                c.X = x;
                c.Y = y;
            }
            w.DrawnSerial++;
            d.Host?.DisplayOutput(w.Number, o.An, "move", W.Inv(x) + "," + W.Inv(y));
        }

        [Builtin(
            "DspAnMove",
            Doc = "1104643",
            Effects = "moves the AN (and what Cicode drew on it) to (x, y)",
            Errors = "274 no window or an AN outside 1..31999 (raised and returned); 269 no page (returned)",
            Notes = "-2 = the task's AN. An unknown object: 0 (the runtime keeps 269 in its last-error word only). The move is page instance state."
        )]
        private static Value DspAnMove(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            int an = AnArg(t, a.Get(0));
            var w = DisplayWindows.Current(h, t);
            if (w == null || !AnOk(an))
            {
                W.Raise(t, 274);
                return W.I(274);
            }
            if (w.Page == null)
                return W.I(269);
            var o = w.Page.Get(an);
            if (o != null)
                MoveTo(d, w, o, (int)A.Short(A.I(a.Get(1))), (int)A.Short(A.I(a.Get(2))));
            return W.I(0);
        }

        [Builtin(
            "DspAnMoveRel",
            Doc = "1104644",
            Effects = "moves the AN by (dx, dy)",
            Errors = "274 no window or an AN outside 1..31999 (raised and returned); 269 no page (returned)",
            Notes = "-2 = the task's AN."
        )]
        private static Value DspAnMoveRel(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            int an = AnArg(t, a.Get(0));
            var w = DisplayWindows.Current(h, t);
            if (w == null || !AnOk(an))
            {
                W.Raise(t, 274);
                return W.I(274);
            }
            if (w.Page == null)
                return W.I(269);
            var o = w.Page.Get(an);
            if (o != null)
            {
                var p = CurrentPos(d, w, o);
                MoveTo(d, w, o, p.X + (int)A.Short(A.I(a.Get(1))), p.Y + (int)A.Short(A.I(a.Get(2))));
            }
            return W.I(0);
        }

        [Builtin(
            "DspGetNearestAn",
            Doc = "1104690",
            Notes = "The AN whose current position is nearest to (x, y) (squared distance, the first of equals in page order); objects with an empty extent, a position of (-32768, -32768) or a dynamic AN are skipped; -1 when none or no page."
        )]
        private static Value DspGetNearestAn(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            var w = DisplayWindows.Current(h, t);
            var p = w?.Page;
            if (w == null || p == null)
                return W.I(-1);
            long x = A.Short(A.I(a.Get(0))),
                y = A.Short(A.I(a.Get(1)));
            int best = -1;
            long bd = long.MaxValue;
            foreach (int an in p.Order)
            {
                var o = p.Objects[an];
                if (o.Dynamic || o.Extent.Width <= 0 || o.Extent.Height <= 0)
                    continue;
                var pos = CurrentPos(d, w, o);
                if (pos.X == -32768 && pos.Y == -32768)
                    continue;
                long dx = x - pos.X,
                    dy = y - pos.Y;
                long dist = dx * dx + dy * dy;
                if (dist < bd)
                {
                    bd = dist;
                    best = an;
                }
            }
            return W.I(best);
        }

        [Builtin(
            "DspAnInRgn",
            Doc = "1104642",
            Errors = "274 no window or any AN outside 1..31999 (raised, 0)",
            Notes = "1 when the AN's position lies in the rectangle the positions of One and Two span (inclusive), 0 otherwise or when any of the three has an empty extent or is unknown."
        )]
        private static Value DspAnInRgn(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            int an = AnArg(t, a.Get(0));
            int a1 = (int)A.Short(A.I(a.Get(1))),
                a2 = (int)A.Short(A.I(a.Get(2)));
            var w = DisplayWindows.Current(h, t);
            if (w == null || !AnOk(an) || !AnOk(a1) || !AnOk(a2))
            {
                W.Raise(t, 274);
                return W.I(0);
            }
            var p = w.Page;
            var o = p?.Get(an);
            var o1 = p?.Get(a1);
            var o2 = p?.Get(a2);
            if (o == null || o1 == null || o2 == null)
                return W.I(0);
            if (
                o.Extent.Width <= 0
                || o.Extent.Height <= 0
                || o1.Extent.Width <= 0
                || o1.Extent.Height <= 0
                || o2.Extent.Width <= 0
                || o2.Extent.Height <= 0
            )
                return W.I(0);
            var q = CurrentPos(d, w, o);
            var q1 = CurrentPos(d, w, o1);
            var q2 = CurrentPos(d, w, o2);
            bool inX = Math.Min(q1.X, q2.X) <= q.X && q.X <= Math.Max(q1.X, q2.X);
            bool inY = Math.Min(q1.Y, q2.Y) <= q.Y && q.Y <= Math.Max(q1.Y, q2.Y);
            return W.I(inX && inY ? 1 : 0);
        }

        [Builtin(
            "DspGetAnFromPoint",
            Doc = "1104677",
            Errors = "274 no window (raised, 0)",
            Notes = "The first shown object in page order whose extent holds the point (page coordinates: the point through the window's scale), after PrevAN when one is given (0 = from the start); 0 when none, no page, or PrevAN is not among them. Dynamic ANs are skipped."
        )]
        private static Value DspGetAnFromPoint(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            var w = DisplayWindows.Current(h, t);
            if (w == null)
            {
                W.Raise(t, 274);
                return W.I(0);
            }
            var p = w.Page;
            if (p == null)
                return W.I(0);
            int x = (int)(A.Short(A.I(a.Get(0))) * w.ScaleX),
                y = (int)(A.Short(A.I(a.Get(1))) * w.ScaleY);
            int prev = (int)A.Short(Arg(a, 2, 0));
            bool take = prev == 0;
            foreach (int an in p.Order)
            {
                var o = p.Objects[an];
                if (o.Dynamic || Hidden(d, w, o))
                    continue;
                var r = CurrentExtent(d, w, o);
                if (!(x >= r.L && x < r.R && y >= r.T && y < r.B))
                    continue; // PtInRect
                if (an == prev)
                    take = true;
                else if (take)
                    return W.I(an);
            }
            return W.I(0);
        }

        // ------------------------------------------------------------------ dynamic ANs
        private static int NewAn(PageInstance p, int x, int y)
        {
            // an AN DspAnNew made before at the point (doc 1104645: 'If an AN already exists at
            // this location, that AN is returned'); a page object's anchor does not count (guess:
            // ExampleSA DefaultAlarm_HD1080's treeview makes its first item AN at the anchor of
            // the EquipmentFilterBar rectangle, and live the bar and the item both show)
            foreach (int an in p.Order)
            {
                var o = p.Objects[an];
                if (o.Dynamic && o.PosX + o.MovedX == x && o.PosY + o.MovedY == y)
                    return an;
            }
            int n = p.NextDynamicAn;
            while (n > 0 && p.Objects.ContainsKey(n))
                n--;
            if (n <= 0)
                return -1;
            p.NextDynamicAn = n - 1;
            var no = p.Ensure(n);
            no.Dynamic = true;
            no.PosX = no.RawPosX = x;
            no.PosY = no.RawPosY = y;
            no.Extent = new Rect(x, y, x, y);
            no.Bounds = no.Extent;
            return n;
        }

        [Builtin(
            "DspAnNew",
            Doc = "1104645",
            Effects = "creates an AN at (x, y) on the page",
            Notes = "An AN DspAnNew made before at that point is returned (a page object's anchor does not count: guess); else a new one numbered from 31999 down (guess: the runtime's numbering is not read). -1 without a window or page."
        )]
        private static Value DspAnNew(SimHost h, CicodeTask t, ArgList a)
        {
            var p = DisplayWindows.Current(h, t)?.Page;
            if (p == null)
                return W.I(-1);
            return W.I(NewAn(p, (int)A.Short(A.I(a.Get(0))), (int)A.Short(A.I(a.Get(1)))));
        }

        [Builtin(
            "DspAnNewRel",
            Doc = "1104646",
            Effects = "creates an AN at (x, y) from an AN",
            Errors = "274 no window or an AN outside 1..31999 (raised, -1)",
            Notes = "-2 = the task's AN. The new AN's point = the reference AN's current position + (x, y); -1 when the reference is unknown."
        )]
        private static Value DspAnNewRel(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            int an = AnArg(t, a.Get(0));
            var w = DisplayWindows.Current(h, t);
            if (w == null || !AnOk(an))
            {
                W.Raise(t, 274);
                return W.I(-1);
            }
            var o = w.Page?.Get(an);
            if (o == null)
                return W.I(-1);
            var pos = CurrentPos(d, w, o);
            return W.I(
                NewAn(w.Page!, pos.X + (int)A.Short(A.I(a.Get(1))), pos.Y + (int)A.Short(A.I(a.Get(2))))
            );
        }

        [Builtin(
            "DspAnFree",
            Doc = "1104635",
            Effects = "removes the AN (and what Cicode drew on it) from the page in memory",
            Errors = "274 no window or an AN above 31999 (raised and returned); 269 no page (returned)",
            Notes = "-2 = the task's AN. A page AN goes too (doc); the page shows it again when it is opened next. Returns 0, also for an unknown AN."
        )]
        private static Value DspAnFree(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            int an = AnArg(t, a.Get(0));
            var w = DisplayWindows.Current(h, t);
            if (w == null || (ushort)an > 31999)
            {
                W.Raise(t, 274);
                return W.I(274);
            }
            if (w.Page == null)
                return W.I(269);
            if (w.Page.Objects.Remove(an))
            {
                w.Page.Order.Remove(an);
                w.Page.StructureChanged();
                d.Undraw(w, an);
            }
            return W.I(0);
        }

        // ------------------------------------------------------------------ mouse
        [Builtin(
            "DspGetMouse",
            Doc = "1104687",
            Errors = "256 no window, or the pointer is not in the task's window (returned; X and Y -1)",
            Notes = "X and Y are set to -1 first; with the pointer in the window's client area: the pointer in window pixels divided by the window's scale, return 0. The pointer comes from the viewer (ctsim/input move)."
        )]
        private static Value DspGetMouse(SimHost h, CicodeTask t, ArgList a)
        {
            W.Out(t, a.Get(0), -1);
            W.Out(t, a.Get(1), -1);
            var w = DisplayWindows.Current(h, t);
            if (w == null || !w.MouseInside)
                return W.I(256);
            W.Out(t, a.Get(0), (int)(w.MouseX / w.ScaleX));
            W.Out(t, a.Get(1), (int)(w.MouseY / w.ScaleY));
            return W.I(0);
        }

        // ------------------------------------------------------------------ metadata
        /// <summary>DspAnSetMetadata's rule: 0 or the raised error.</summary>
        internal static int SetMetadata(SimHost h, CicodeTask t, int an, string name, string value)
        {
            if (an == -2)
                an = TaskContext.An(t);
            var w = DisplayWindows.Current(h, t);
            if (w == null || !AnOk(an))
            {
                W.Raise(t, 303);
                return 303;
            }
            var o = w.Page?.Get(an);
            if (o == null)
            {
                W.Raise(t, 269);
                return 269;
            }
            for (int i = 0; i < o.Metadata.Count; i++)
                if (string.Equals(o.Metadata[i].Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    o.Metadata[i] = new KeyValuePair<string, string>(o.Metadata[i].Key, value);
                    o.NoteCicodeMeta(o.Metadata[i].Key, value);
                    return 0;
                }
            W.Raise(t, 289);
            return 289;
        }

        [Builtin(
            "DspAnSetMetadata",
            Doc = "1104648",
            Effects = "changes the value of an existing metadata entry of the AN (page instance state)",
            Errors = "303 no window or an AN outside 1..31999; 269 the object not found; 289 no entry of that name (raised and returned)",
            Notes = "-2 = the task's AN. A name the page does not define is never added. Names compare without case (guess, as DspAnGetMetadata)."
        )]
        private static Value DspAnSetMetadata(SimHost h, CicodeTask t, ArgList a) =>
            W.I(SetMetadata(h, t, (int)A.Short(A.I(a.Get(0))), A.S(a.Get(1)), A.S(a.Get(2))));

        [Builtin(
            "DspAnGetMetadataAt",
            Doc = "1104638",
            Errors = "303 no window or an AN outside 1..31999; 269 the object not found; 257 an index outside the entries; 274 a field other than Name / Value (raised, \"\")",
            Notes = "-2 = the task's AN. The entry's name or value (field compared without case). The 303: the two early exits jump to before the shared raise; the other errors enter it past that move with the lookup's code."
        )]
        private static Value DspAnGetMetadataAt(SimHost h, CicodeTask t, ArgList a)
        {
            int an = AnArg(t, a.Get(0));
            var w = DisplayWindows.Current(h, t);
            if (w == null || (ushort)(an - 1) >= 31999)
            {
                W.Raise(t, 303);
                return W.Empty;
            }
            var o = w.Page?.Get(an);
            if (o == null)
            {
                W.Raise(t, 269);
                return W.Empty;
            }
            long i = A.Short(A.I(a.Get(1)));
            if (i < 0 || i >= o.Metadata.Count)
            {
                W.Raise(t, 257);
                return W.Empty;
            }
            string f = A.S(a.Get(2));
            string s;
            if (string.Equals(f, "Name", StringComparison.OrdinalIgnoreCase))
                s = o.Metadata[(int)i].Key;
            else if (string.Equals(f, "Value", StringComparison.OrdinalIgnoreCase))
                s = o.Metadata[(int)i].Value;
            else
            {
                W.Raise(t, 274);
                return W.Empty;
            }
            return W.S(s.Length > 255 ? s.Substring(0, 255) : s);
        }

        [Builtin(
            "DspAnSetMetadataAt",
            Doc = "1104649",
            Effects = "changes the name or value of the entry at the index (page instance state)",
            Errors = "303 no window or an AN outside 1..31999; 269 the object not found; 257 an index outside the entries; 274 a field other than Name / Value (raised and returned)",
            Notes = "-2 = the task's AN. The error codes of past 303: guess (DspAnGetMetadataAt's)."
        )]
        private static Value DspAnSetMetadataAt(SimHost h, CicodeTask t, ArgList a)
        {
            int an = AnArg(t, a.Get(0));
            var w = DisplayWindows.Current(h, t);
            int code = 0;
            var o = w != null && AnOk(an) ? w.Page?.Get(an) : null;
            long i = A.Short(A.I(a.Get(1)));
            string f = A.S(a.Get(2));
            if (w == null || !AnOk(an))
                code = 303;
            else if (o == null)
                code = 269;
            else if (i < 0 || i >= o.Metadata.Count)
                code = 257;
            else if (string.Equals(f, "Name", StringComparison.OrdinalIgnoreCase))
                o.Metadata[(int)i] = new KeyValuePair<string, string>(
                    A.S(a.Get(3)),
                    o.Metadata[(int)i].Value
                );
            else if (string.Equals(f, "Value", StringComparison.OrdinalIgnoreCase))
                o.Metadata[(int)i] = new KeyValuePair<string, string>(o.Metadata[(int)i].Key, A.S(a.Get(3)));
            else
                code = 274;
            if (code == 0)
                o!.NoteCicodeMeta(o.Metadata[(int)i].Key, o.Metadata[(int)i].Value);
            if (code != 0)
                W.Raise(t, code);
            return W.I(code);
        }

        private static Value GetMeta(SimHost h, CicodeTask t, int an, string meta)
        {
            string v = DisplayBuiltins.AnMetadata(h, t, an, meta, out int code);
            if (code != 0)
                W.Raise(t, code);
            return W.S(v);
        }

        [Builtin(
            "DspGetMetadataFromName",
            Doc = "1104685",
            Errors = "274 the name not found (the lookup gives -1) or no window; 269 / 289 as DspAnGetMetadata (raised, \"\")",
            Notes = "DspAnGetMetadata(DspGetAnFromName(sName), sMetaName)."
        )]
        private static Value DspGetMetadataFromName(SimHost h, CicodeTask t, ArgList a) =>
            GetMeta(h, t, FromName(h, t, -1, A.S(a.Get(0))), A.S(a.Get(1)));

        [Builtin(
            "DspGetMetadataFromNameRelative",
            Doc = "1104686",
            Errors = "274 the name not found or no window; 269 / 289 as DspAnGetMetadata (raised, \"\")",
            Notes = "DspAnGetMetadata(DspGetAnFromNameRelative(nAN, sName), sMetaName)."
        )]
        private static Value DspGetMetadataFromNameRelative(SimHost h, CicodeTask t, ArgList a) =>
            GetMeta(h, t, FromName(h, t, (int)A.Short(A.I(a.Get(0))), A.S(a.Get(1))), A.S(a.Get(2)));

        [Builtin(
            "DspSetMetadataFromName",
            Doc = "1104724",
            Effects = "as DspAnSetMetadata",
            Errors = "303 the name not found (-1) or no window; 269 / 289 as DspAnSetMetadata (raised and returned)",
            Notes = "DspAnSetMetadata(DspGetAnFromName(sName), sMetaName, sValue)."
        )]
        private static Value DspSetMetadataFromName(SimHost h, CicodeTask t, ArgList a) =>
            W.I(SetMetadata(h, t, FromName(h, t, -1, A.S(a.Get(0))), A.S(a.Get(1)), A.S(a.Get(2))));

        [Builtin(
            "DspSetMetadataFromNameRelative",
            Doc = "1104725",
            Effects = "as DspAnSetMetadata",
            Errors = "303 the name not found (-1) or no window; 269 / 289 as DspAnSetMetadata (raised and returned)",
            Notes = "DspAnSetMetadata(DspGetAnFromNameRelative(nAN, sName), sMetaName, sValue)."
        )]
        private static Value DspSetMetadataFromNameRelative(SimHost h, CicodeTask t, ArgList a) =>
            W.I(
                SetMetadata(
                    h,
                    t,
                    FromName(h, t, (int)A.Short(A.I(a.Get(0))), A.S(a.Get(1))),
                    A.S(a.Get(2)),
                    A.S(a.Get(3))
                )
            );

        [Builtin(
            "DspGetEnv",
            Doc = "1104684",
            Notes = "The value of the page environment variable (the drawing's environment list), name compared without case (_stricmp; the doc says case-sensitive: verify), cut to 255; \"\" when none, no window or page."
        )]
        private static Value DspGetEnv(SimHost h, CicodeTask t, ArgList a)
        {
            var p = DisplayWindows.Current(h, t)?.Page;
            if (p == null)
                return W.Empty;
            string name = A.S(a.Get(0));
            foreach (var kv in p.Env)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                    return W.S(kv.Value.Length > 255 ? kv.Value.Substring(0, 255) : kv.Value);
            return W.Empty;
        }

        // ------------------------------------------------------------------ arrays by AN
        /// <summary>Per-host state of arrays bound to ANs.</summary>
        private sealed class AnArrays
        {
            public CicodeVm? Vm;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SimHost, AnArrays> Arrs =
            new System.Runtime.CompilerServices.ConditionalWeakTable<SimHost, AnArrays>();

        /// <summary>The host's state; the first use makes a page close free the arrays bound to
        /// that window (ArrayClosePage: guess on the timing).</summary>
        private static AnArrays ArraysOf(SimHost h) => Arrs.GetValue(h, host =>
            {
                var aa = new AnArrays();
                DisplayBuiltins.Of(host).PageClosing += (w, p) =>
                {
                    var gone = new List<int>();
                    foreach (var (hd, arr) in ProcessData.Of(host).ArrayTable.Live())
                        if (arr.An > 0 && arr.Window == w.Number)
                            gone.Add(hd);
                    if (gone.Count == 0 || aa.Vm == null)
                        return;
                    var t = aa.Vm.TaskForBlock(
                        new byte[] { CicodeSim.Compiled.Op.End },
                        aa.Vm.Library,
                        false,
                        "ArrayClosePage"
                    );
                    foreach (int hd in gone)
                        Call(host, t, "ArrayDestroy", Value.Long(hd));
                };
                return aa;
            });

        /// <summary>The handle of the array bound to (AN, window), -1 when none or for an AN
        /// outside 1..31999: the first live array in handle order with that AN and window.</summary>
        internal static int ArrayOfAn(SimHost h, int an, int window)
        {
            if ((ushort)(an - 1) >= 31999)
                return -1;
            foreach (var (hd, arr) in ProcessData.Of(h).ArrayTable.Live())
                if (arr.An == an && arr.Window == window)
                    return hd;
            return -1;
        }

        /// <summary>Bind an array to (AN, window) (as the runtime binds it):
        /// Array*ByAn and ArrayFillFromAlarmDataByAn find it, and the window's next page close
        /// frees it. An alarm list's array (AlarmListCreate) is bound with this too.</summary>
        internal static void BindArray(SimHost h, CicodeTask t, int hd, int an, int window)
        {
            var arr = ProcessData.Of(h).ArrayTable.Get(hd);
            if (arr == null)
                return;
            ArraysOf(h).Vm = t.Vm;
            arr.An = an;
            arr.Window = window;
        }

        /// <summary>Call a registry built-in by name (the Array* functions).</summary>
        private static Value Call(SimHost h, CicodeTask t, string name, params Value[] args)
        {
            if (h.Registry == null)
                return Value.Long(-1);
            var b = h.Registry.Table.Lookup(name);
            if (b == null)
                return Value.Long(-1);
            return h.Registry.Call(b.Index, t, ArgList.Of(args));
        }

        /// <summary>The array handle of (task's window, AN), -1 when none (-1 also
        /// without a window or for an AN outside 1..31999).</summary>
        private static int HandleOf(SimHost h, CicodeTask t, int an)
        {
            var w = DisplayWindows.Current(h, t);
            return w == null ? -1 : ArrayOfAn(h, an, w.Number);
        }

        private static void Pass(ArgList a, int from, int hd, out Value[] args)
        {
            var l = new List<Value> { Value.Long(hd) };
            for (int i = from; i < a.Count; i++)
                l.Add(a[i]);
            args = l.ToArray();
        }

        [Builtin(
            "ArrayCreateByAn",
            Doc = "1069788",
            Effects = "creates an array for the AN of the task's window (its previous array destroyed)",
            Errors = "as ArrayCreate (257 / 272 / 271 raised, -1)",
            Notes = "The array is named after the page and the AN ('<page>_AN<n>', guess) and bound to (window, AN) in its own record, the binding every ...ByAn function and ArrayFillFromAlarmDataByAn look up; ArrayDestroy of the handle ends it; freed when the window's page closes. Returns the handle."
        )]
        private static Value ArrayCreateByAn(SimHost h, CicodeTask t, ArgList a)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            var w = DisplayWindows.Current(h, t);
            int old = HandleOf(h, t, an);
            if (old != -1)
                Call(h, t, "ArrayDestroy", Value.Long(old));
            string name = (w?.Page?.Name ?? "") + "_AN" + W.Inv(an);
            var args = new List<Value> { Value.Str(name) };
            for (int i = 1; i < a.Count; i++)
                args.Add(a[i]);
            var r = Call(h, t, "ArrayCreate", args.ToArray());
            int hd = (int)A.I(r);
            if (hd >= 0 && w != null && (ushort)(an - 1) < 31999 && ArrayOfAn(h, an, w.Number) == -1)
                BindArray(h, t, hd, an, w.Number);
            return W.I(hd);
        }

        [Builtin(
            "ArrayDestroyByAn",
            Doc = "1069790",
            Effects = "destroys the AN's array",
            Notes = "Returns 0 (also when the AN has none)."
        )]
        private static Value ArrayDestroyByAn(SimHost h, CicodeTask t, ArgList a)
        {
            int an = (int)A.Short(A.I(a.Get(0)));
            int hd = HandleOf(h, t, an);
            if (hd != -1)
                Call(h, t, "ArrayDestroy", Value.Long(hd));
            return W.I(0);
        }

        [Builtin(
            "ArrayExistsByAn",
            Doc = "1069792",
            Notes = "1 when the AN of the task's window has an array, else 0 (no window, an AN outside 1..31999: 0)."
        )]
        private static Value ArrayExistsByAn(SimHost h, CicodeTask t, ArgList a) =>
            W.I(HandleOf(h, t, (int)A.Short(A.I(a.Get(0)))) != -1 ? 1 : 0);

        [Builtin("ArrayGetArrayByAn", Doc = "1069794", Notes = "The AN's array handle, -1 when none.")]
        private static Value ArrayGetArrayByAn(SimHost h, CicodeTask t, ArgList a) =>
            W.I(HandleOf(h, t, (int)A.Short(A.I(a.Get(0)))));

        [Builtin(
            "ArrayGetMapNameByAn",
            Doc = "1069799",
            Errors = "269 the AN has no array (raised \"ArrayGetMapName(%d) bad handle\", \"\")",
            Notes = "The hidden map's name of the AN's array (ArrayGetMapName)."
        )]
        private static Value ArrayGetMapNameByAn(SimHost h, CicodeTask t, ArgList a)
        {
            int hd = HandleOf(h, t, (int)A.Short(A.I(a.Get(0))));
            if (hd == -1)
            {
                W.Raise(t, 269);
                return W.Empty;
            }
            return Call(h, t, "ArrayGetMapName", Value.Long(hd));
        }

        [Builtin(
            "ArraySetStringByAn",
            Doc = "1069807",
            Effects = "as ArraySetString on the AN's array",
            Notes = "ArraySetString(handle of the AN or -1, sValue, x, y, z): a bad handle raises ArraySetString's 269."
        )]
        private static Value ArraySetStringByAn(SimHost h, CicodeTask t, ArgList a)
        {
            Pass(a, 1, HandleOf(h, t, (int)A.Short(A.I(a.Get(0)))), out var args);
            return Call(h, t, "ArraySetString", args);
        }

        [Builtin(
            "ArrayGetStringByAn",
            Doc = "1069801",
            Notes = "ArrayGetString(handle of the AN or -1, x, y, z)."
        )]
        private static Value ArrayGetStringByAn(SimHost h, CicodeTask t, ArgList a)
        {
            Pass(a, 1, HandleOf(h, t, (int)A.Short(A.I(a.Get(0)))), out var args);
            return Call(h, t, "ArrayGetString", args);
        }

        [Builtin(
            "ArraySetIntByAn",
            Doc = "1069804",
            Effects = "as ArraySetString of the value's decimal text",
            Notes = "The value written as decimal text (snprintf), then ArraySetString(handle of the AN or -1, text, x, y, z)."
        )]
        private static Value ArraySetIntByAn(SimHost h, CicodeTask t, ArgList a)
        {
            Pass(a, 1, HandleOf(h, t, (int)A.Short(A.I(a.Get(0)))), out var args);
            if (args.Length > 1)
                args[1] = Value.Str(W.Inv(A.I(args[1])));
            return Call(h, t, "ArraySetString", args);
        }

        [Builtin(
            "ArrayGetIntByAn",
            Doc = "1069797",
            Notes = "atol(ArrayGetString(handle of the AN or -1, x, y, z))."
        )]
        private static Value ArrayGetIntByAn(SimHost h, CicodeTask t, ArgList a)
        {
            Pass(a, 1, HandleOf(h, t, (int)A.Short(A.I(a.Get(0)))), out var args);
            var r = Call(h, t, "ArrayGetString", args);
            return W.I(Conv.StrToInt(A.S(r), out _));
        }
    }
}
