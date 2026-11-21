using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CicodeSim.Page;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World.Display;

namespace CicodeSim.Builtins
{
    /// <summary>Keyboard command line and super genie associations.</summary>
    public static class DisplayKeys
    {
        private static DisplayState D(SimHost h) => DisplayBuiltins.Of(h);

        private static long Arg(ArgList a, int k, long dflt) =>
            a.Count > k && !a[k].IsNothing ? A.I(a[k]) : dflt;

        private static readonly ConditionalWeakTable<SimHost, KeyTable> Keys =
            new ConditionalWeakTable<SimHost, KeyTable>();

        /// <summary>The unit's key names (keys.dbf; the animator's table).</summary>
        private static KeyTable KeysOf(SimHost h) =>
            Keys.GetValue(h, x =>
                x.World?.Display.Controller is Animator an ? an.Keys
                : x.World != null ? KeyTable.FromUnit(x.World.Unit)
                : new KeyTable()
            );

        // ------------------------------------------------------------------ the key command line
        [Builtin(
            "KeyGet",
            Doc = "1106240",
            Effects = "removes the last key of the window's key command line",
            Notes = "The last key code of the task's window's key command line, removed; 0 when it is empty or there is no window."
        )]
        private static Value KeyGet(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            if (w == null || w.KeyBuffer.Count == 0)
                return W.I(0);
            int c = w.KeyBuffer[w.KeyBuffer.Count - 1];
            w.KeyBuffer.RemoveAt(w.KeyBuffer.Count - 1);
            return W.I((ushort)c);
        }

        [Builtin(
            "KeyBS",
            Doc = "1106238",
            Effects = "removes the last key of the window's key command line",
            Notes = "The same function as KeyGet (one FUNC0 address): returns the removed key code, 0 when the line is empty."
        )]
        private static Value KeyBS(SimHost h, CicodeTask t, ArgList a) => KeyGet(h, t, a);

        [Builtin(
            "KeyPeek",
            Doc = "1106244",
            Notes = "The key code Offset keys from the end of the key command line (0 = the last), not removed; 0 outside the line or without a window."
        )]
        private static Value KeyPeek(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            int off = (int)A.Short(A.I(a.Get(0)));
            if (w == null || off < 0 || off >= w.KeyBuffer.Count)
                return W.I(0);
            return W.I((ushort)w.KeyBuffer[w.KeyBuffer.Count - 1 - off]);
        }

        [Builtin(
            "KeyPut",
            Doc = "1106245",
            Effects = "types a key in the task's window: the key command line, then a matching keyboard command runs",
            Errors = "256 no window (returned)"
        )]
        private static Value KeyPut(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            var w = DisplayWindows.Current(h, t);
            if (w == null)
                return W.I(256);
            d.Runtime.KeyPress(w.Number, (ushort)A.I(a.Get(0)));
            return W.I(0);
        }

        [Builtin(
            "KeyPutStr",
            Doc = "1106246",
            Effects = "types the keys of a string (key names or characters) in the task's window",
            Errors = "256 no window (returned)",
            Notes = "Blank-separated key names (keys.dbf) are their codes, any other text one code per character (letters upper-cased); at most 200 keys. Returns 0."
        )]
        private static Value KeyPutStr(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            var w = DisplayWindows.Current(h, t);
            if (w == null)
                return W.I(256);
            var codes = KeysOf(h).Sequence(A.S(a.Get(0)));
            int n = Math.Min(codes.Count, 200);
            for (int i = 0; i < n; i++)
                d.Runtime.KeyPress(w.Number, codes[i]);
            return W.I(0);
        }

        [Builtin(
            "KeyReplay",
            Doc = "1106247",
            Effects = "the last completed key command line, minus its last Sub keys, is the key command line again",
            Errors = "274 no window and a negative count (returned)",
            Notes = "Sub default 1. Nothing happens when the last line held fewer than two keys. Returns 0."
        )]
        private static Value KeyReplay(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            int sub = (int)A.Short(Arg(a, 0, 1));
            if (w == null)
                return W.I(sub < 0 ? 274 : 0);
            int n = w.LastKeyLine.Count;
            if (n > 1)
            {
                int keep = Math.Max(0, Math.Min(n, n - sub));
                w.KeyBuffer.Clear();
                for (int i = 0; i < keep; i++)
                    w.KeyBuffer.Add(w.LastKeyLine[i]);
            }
            return W.I(0);
        }

        [Builtin(
            "KeySetSeq",
            Doc = "1106251",
            Effects = "adds a key command to the window's shown page: typing the sequence (with the cursor on the AN, 0 = anywhere) runs the function as a new task, the sequence's data in the global strings (Arg1..Arg8)",
            Errors = "256 no window (returned); 274 an empty sequence or no such function (returned)",
            Notes = "(Sequence, AN, Fn): Fn is the function's name (the compiler passes a FUNCTION argument as its name). The command lasts while the page instance is shown (verify against a runtime: a page shown again needs it added again). Returns 0."
        )]
        private static Value KeySetSeq(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            if (w == null)
                return W.I(256);
            string seq = A.S(a.Get(0)).Trim();
            var fn = CallText.Find(t, a.Count > 2 ? A.S(a.Get(2)) : "");
            if (seq.Length == 0 || fn == null)
                return W.I(Codes.InvalidArg);
            w.RuntimeKeys.RemoveAll(r => r.Page != w.Page);
            w.RuntimeKeys.Add(
                new RuntimeKey
                {
                    Sequence = seq,
                    An = (ushort)A.I(a.Get(1)),
                    Function = fn.Index,
                    Page = w.Page,
                }
            );
            return W.I(0);
        }

        [Builtin(
            "KeySetType",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Notes = "Undocumented keyboard mode: not simulated, returns 0."
        )]
        private static Value KeySetType(SimHost h, CicodeTask t, ArgList a) => W.I(0);

        // ------------------------------------------------------------------ the command cursor
        private static bool CursorAllowed(Window w, AnObject o) => o.CursorAllowed(w.Page);

        [Builtin(
            "KeyAllowCursor",
            Doc = "1106237",
            Effects = "the command cursor may (not) stop at the AN (0 = every AN)",
            Errors = "256 no window (returned)",
            Notes = "Returns 0 (an AN above 32000 or no page: nothing changes)."
        )]
        private static Value KeyAllowCursor(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            if (w == null)
                return W.I(256);
            int an = (ushort)A.I(a.Get(0));
            if (an > 0x7d00 || w.Page == null)
                return W.I(0);
            bool allow = A.Short(A.I(a.Get(1))) != 0;
            if (an == 0)
            {
                foreach (var o in w.Page.Objects.Values)
                    o.KeyCursorAllowed = allow;
                return W.I(0);
            }
            var obj = w.Page.Get(an);
            if (obj != null)
                obj.KeyCursorAllowed = allow;
            return W.I(0);
        }

        [Builtin(
            "KeySetCursor",
            Doc = "1106250",
            Effects = "the window's command cursor moves to the AN",
            Errors = "256 no window; 274 an AN outside 1..32000 (returned)",
            Notes = "0 when the AN exists and has a keyboard or touch command or KeyAllowCursor allowed it (AnObject.CursorAllowed: the mouse-over rule); else 1 and the cursor stays."
        )]
        private static Value KeySetCursor(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            if (w == null)
                return W.I(256);
            int an = (int)A.Short(A.I(a.Get(0)));
            if ((ushort)(an - 1) >= 32000)
                return W.I(274);
            var o = w.Page?.Get(an);
            if (o == null || !CursorAllowed(w, o))
                return W.I(1);
            w.CommandCursor = an;
            return W.I(0);
        }

        [Builtin(
            "KeyMove",
            Doc = "1106243",
            Effects = "the command cursor moves to the nearest AN with a command in the direction",
            Errors = "274 a direction above 4; 256 no window (returned)",
            Notes = "0 no move, 1 left, 2 right, 3 up, 4 down: the nearest AN (squared distance of the positions) the cursor may stop on (a keyboard or touch command, or KeyAllowCursor: AnObject.CursorAllowed) strictly in that direction from the cursor (guess: not read; the pgkey cursor overrides UP_AN ... are not used). No cursor: the first such AN. Returns 0."
        )]
        private static Value KeyMove(SimHost h, CicodeTask t, ArgList a)
        {
            ushort dir = (ushort)A.I(a.Get(0));
            if (dir > 4)
                return W.I(274);
            var d = D(h);
            var w = DisplayWindows.Current(h, t);
            if (w == null)
                return W.I(256);
            var p = w.Page;
            if (dir == 0 || p == null)
                return W.I(0);
            var cur = w.CommandCursor > 0 ? p.Get(w.CommandCursor) : null;
            int best = -1;
            long bd = long.MaxValue;
            var cp = cur != null ? DisplayAns.CurrentPos(d, w, cur) : (0, 0);
            foreach (int an in p.Order)
            {
                var o = p.Objects[an];
                if (o == cur || !CursorAllowed(w, o))
                    continue;
                var q = DisplayAns.CurrentPos(d, w, o);
                if (cur != null)
                {
                    bool ok =
                        dir == 1 ? q.X < cp.Item1
                        : dir == 2 ? q.X > cp.Item1
                        : dir == 3 ? q.Y < cp.Item2
                        : q.Y > cp.Item2;
                    if (!ok)
                        continue;
                }
                long dx = q.X - cp.Item1,
                    dy = q.Y - cp.Item2;
                long dist = cur == null ? 0 : dx * dx + dy * dy;
                if (dist < bd)
                {
                    bd = dist;
                    best = an;
                }
            }
            if (best > 0)
                w.CommandCursor = best;
            return W.I(0);
        }

        // ------------------------------------------------------------------ associations
        [Builtin(
            "AssChain",
            Doc = "1192656",
            Effects = "copies the source window's active associations to the target's next page",
            Notes = "Every association the page of window hSource took becomes a pending one for hDest (-2 the next new window or page, a window number); a literal stays a literal, an entry whose cluster was the caller's default loses it. hDest is used as given: the runtime does not make -3 the current window as Ass does (doc 1192656 says it does; corpus: hDest is always -2 or WinNumber), so a -3 entry is never taken by a page. An argument already pending for hDest or for -2 is kept and not copied. Returns the number copied."
        )]
        private static Value AssChain(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            int dest = (int)A.Short(A.I(a.Get(0)));
            int src = (int)A.Short(A.I(a.Get(1)));
            var w = d.Get(src);
            if (w == null || !w.Open)
                return W.I(0);
            int n = 0;
            foreach (var kv in new List<KeyValuePair<string, AssEntry>>(w.Associations))
            {
                // an Ass made before the chain wins (the runtime skips a name already pending)
                if (d.PendingIndex(dest, kv.Value.Arg) >= 0)
                    continue;
                var e = kv.Value.Copy(dest);
                e.UsedDefault = false;
                if (e.ClusterDefaulted)
                {
                    e.Cluster = "";
                    e.ClusterDefaulted = false;
                }
                d.Pending.Add(e);
                n++;
            }
            return W.I(n);
        }

        /// <summary>Call a registry built-in by name.</summary>
        private static Value Call(SimHost h, CicodeTask t, string name, params Value[] args)
        {
            var b = h.Registry?.Table.Lookup(name);
            if (b == null)
                return W.Empty;
            return h.Registry!.Call(b.Index, t, ArgList.Of(args));
        }

        private static string WithIndex(in AssResolved r) =>
            r.Index >= 0
                ? r.Tag + "[" + r.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]"
                : r.Tag;

        [Builtin(
            "AssGetProperty",
            Doc = "1192663",
            Errors = "274 the argument is not associated; the reference's cluster errors 403 / 404; TagGetProperty's errors for a tag property (raised, \"\")",
            Notes = "\"\" without a window. The argument as AssInfo's (a name / number, '?name?', a partial '?Equip?.Item'). A literal association: TagName = its text, Literal \"1\", ErrorValUsed \"0\", other properties \"\" (no error). A tag: TagName / ClusterName / FullName / AssFullName as AssInfo's types 0 / 17 / 18 / 19 (an Equip.Item named by its variable tag), Literal \"0\", ErrorValUsed (\"1\" when the page's value on error replaced it), else TagGetProperty(reference, sProperty, iCachedMode, cluster) (Equipment, Item, Description, EngUnitsHigh ...). Property names compare without case (_stricmp)."
        )]
        private static Value AssGetProperty(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            if (w == null)
                return W.Empty;
            string arg = A.S(a.Get(0));
            string prop = A.S(a.Get(1));
            long cached = Arg(a, 2, -1);
            var entry = AssRules.Active(w, arg);
            if (entry == null)
            {
                W.Raise(t, 274);
                return W.Empty;
            }
            var r = AssRules.Resolve(h.World, arg, entry);
            bool Is(string p) => string.Equals(prop, p, StringComparison.OrdinalIgnoreCase);
            if (entry.Literal)
            {
                if (Is("TagName"))
                    return W.S(r.Tag.Length > 254 ? r.Tag.Substring(0, 254) : r.Tag);
                if (Is("Literal"))
                    return W.S("1");
                if (Is("ErrorValUsed"))
                    return W.S("0");
                return W.Empty;
            }
            if (r.Error != 0)
            {
                W.Raise(t, r.Error);
                return W.Empty;
            }
            int mode =
                Is("TagName") ? 2
                : Is("ClusterName") ? 1
                : Is("FullName") ? 3
                : Is("AssFullName") ? 7
                : 0;
            if (mode != 0)
                return W.S(AssRules.Text(h.World, r, AssRules.IsPartial(arg), mode, Tags.QualityError));
            if (Is("Literal"))
                return W.S("0");
            if (Is("ErrorValUsed"))
                return W.S(entry.ErrorValueUsed ? "1" : "0");
            return Call(
                h,
                t,
                "TagGetProperty",
                Value.Str(WithIndex(r)),
                Value.Str(prop),
                Value.Long(cached),
                Value.Str(r.Cluster)
            );
        }

        [Builtin(
            "AssInfoEx",
            Doc = "1192666",
            Errors = "as AssInfo (raised, \"\")",
            Notes = "AssInfo itself (the same core, its errors raised as 'AssInfoEx'): the type as text or a property name, iCachedMode as TagGetProperty's."
        )]
        private static Value AssInfoEx(SimHost h, CicodeTask t, ArgList a) =>
            DisplayBuiltins.AssInfoCore(h, t, a, "AssInfoEx");

        private static Value Scale(SimHost h, CicodeTask t, ArgList a)
        {
            var w = DisplayWindows.Current(h, t);
            if (w == null)
                return W.Empty;
            string arg = A.S(a.Get(0));
            var entry = AssRules.Active(w, arg);
            if (entry == null || entry.Literal)
            {
                W.Raise(t, 274);
                return W.Empty;
            }
            var r = AssRules.Resolve(h.World, arg, entry);
            if (r.Error != 0)
            {
                W.Raise(t, r.Error);
                return W.Empty;
            }
            return Call(
                h,
                t,
                "TagScaleStr",
                Value.Str(WithIndex(r)),
                a.Get(1),
                a.Get(2),
                Value.Str(r.Cluster),
                Value.Long(Arg(a, 3, -1))
            );
        }

        [Builtin(
            "AssScaleStr",
            Doc = "1192673",
            Errors = "274 the argument is not associated or is a literal; the reference's cluster errors 403 / 404; TagScaleStr's errors (raised, \"\")",
            Notes = "\"\" without a window. TagScaleStr(the reference (AssInfo's argument rules), Percent, EngUnits, its cluster, CachedMode): the scale value in the tag's format. 432 'Property not ready' would be raised as under ErrSet(1); the simulated tag database never answers it."
        )]
        private static Value AssScaleStr(SimHost h, CicodeTask t, ArgList a) => Scale(h, t, a);

        [Builtin(
            "AssGetScale",
            Doc = "1192664",
            Errors = "as AssScaleStr (raised, \"\")",
            Notes = "AssScaleStr's code (the same call)."
        )]
        private static Value AssGetScale(SimHost h, CicodeTask t, ArgList a) => Scale(h, t, a);

        [Builtin(
            "AssWinReplace",
            Doc = "1192678",
            Effects = "the window's page takes the pending associations without reloading",
            Errors = "274 the window is not open or has no pending association; 265 no page; 277 not in the user's area (returned)",
            Notes = "The window's associations are replaced by its pending ones (target the window or -2) and the page's declared defaults (DisplayState.ClaimForPage); the page's '?' references resolve again. Returns 0."
        )]
        private static Value AssWinReplace(SimHost h, CicodeTask t, ArgList a)
        {
            var d = D(h);
            int n = (int)A.Short(A.I(a.Get(0)));
            var w = d.OpenWindow(n);
            if (w == null || !d.HasPending(n))
                return W.I(274);
            if (w.Page == null)
                return W.I(265);
            if (!d.AreaOk(w.Page.Record.Areas))
                return W.I(277);
            d.ClaimForPage(w, w.Page.Associations);
            d.Runtime.AssociationsChanged(n);
            return W.I(0);
        }

        [Builtin(
            "AssEquipParameters",
            Doc = "1192661",
            Effects = "associates equipment runtime parameters with the target window's next page",
            Notes = "Reads eqparam.dbf. An empty cluster uses the destination page's cluster. ISTAG false supplies a literal."
        )]
        private static Value AssEquipParameters(SimHost h, CicodeTask t, ArgList a)
        {
            var display = D(h);
            int target = (int)A.I(a.Get(0));
            string equipment = A.S(a.Get(2));
            if (equipment.Length > 0)
                display.Pending.Add(
                    new AssEntry
                    {
                        Target = target == -3 ? display.WindowOf(t) : target,
                        Equipment = equipment,
                        Cluster = A.S(a.Get(1)),
                    }
                );
            return W.I(0);
        }
    }
}
