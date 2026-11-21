using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>A stand-in for CTUTIL.DLL's DspPopupMenu.</summary>
    internal static class CtUtil
    {
        /// <summary>The pseudo handles DLLOpen gives the three functions.</summary>
        internal const int PopupHandle = 0x7c00,
            IsActiveHandle = 0x7c01,
            GetDataHandle = 0x7c02;

        private sealed class State
        {
            public readonly Dictionary<int, List<string>> Menus = new Dictionary<int, List<string>>();
            public readonly Dictionary<int, Request> Requests = new Dictionary<int, Request>();
            public int Next;
        }

        private sealed class Request
        {
            public Dialog? Dialog;
            public string Data = "0";
        }

        private static readonly ConditionalWeakTable<SimHost, State> States =
            new ConditionalWeakTable<SimHost, State>();

        private static State Of(SimHost h) => States.GetValue(h, _ => new State());

        /// <summary>DLLOpen of a CTUTIL.DLL function: its pseudo handle, -1 for another DLL or
        /// function.</summary>
        internal static int Open(string file, string function)
        {
            string f = file.Replace('/', '\\').Trim();
            int k = f.LastIndexOf('\\');
            string name = k >= 0 ? f.Substring(k + 1) : f;
            if (
                !string.Equals(name, "CTUTIL.DLL", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "CTUTIL", StringComparison.OrdinalIgnoreCase)
            )
                return -1;
            switch (function.Trim().ToUpperInvariant())
            {
                case "DSPPOPUPMENU":
                    return PopupHandle;
                case "ISACTIVE":
                    return IsActiveHandle;
                case "GETDATA":
                    return GetDataHandle;
                default:
                    return -1;
            }
        }

        internal static bool Is(long handle) =>
            handle == PopupHandle || handle == IsActiveHandle || handle == GetDataHandle;

        /// <summary>DLLCallEx / DLLCall of a pseudo handle with its arguments.</summary>
        internal static Value Call(SimHost h, CicodeTask t, long handle, IReadOnlyList<Value> args)
        {
            var st = Of(h);
            string S(int i) => i < args.Count ? A.S(args[i]) : "";
            long I(int i) => i < args.Count ? A.I(args[i]) : -1;
            switch (handle)
            {
                case PopupHandle:
                    return W.I(
                        PopupMenu(h, t, st, (int)A.Short(I(0)), S(1), (int)A.Short(I(2)), (int)A.Short(I(3)))
                    );
                case IsActiveHandle:
                {
                    int r = (int)ArgNumber(S(0));
                    if (!st.Requests.TryGetValue(r, out var q))
                        return W.S("0");
                    return W.S(q.Dialog != null && !q.Dialog.Answered ? "1" : "0");
                }
                default:
                {
                    int r = (int)ArgNumber(S(0));
                    if (!st.Requests.TryGetValue(r, out var q))
                        return W.S("0");
                    if (q.Dialog != null)
                    {
                        if (!q.Dialog.Answered)
                            return W.S("0");
                        q.Dialog.TakeAnswer(out bool ok, out string? text, out _);
                        q.Data = ok && text != null && text.Trim().Length > 0 ? text.Trim() : "0";
                        q.Dialog.Drop();
                        h.Interaction.Closed(q.Dialog);
                        q.Dialog = null;
                    }
                    st.Requests.Remove(r);
                    return W.S(q.Data);
                }
            }
        }

        /// <summary>The leading whole number of a DLLCall argument string ("12", "12,x").</summary>
        private static long ArgNumber(string s) => DisplayBuiltins.Atoi(s);

        private static int NewRequest(State st, Request q)
        {
            int n = st.Next;
            st.Next = (st.Next + 1) & 0x7fffffff;
            st.Requests[n] = q;
            // requests a CI never collected (an add's) are dropped after a while
            if (st.Requests.Count > 64)
            {
                var drop = new List<int>();
                foreach (var kv in st.Requests)
                    if (kv.Value.Dialog == null && kv.Key != n)
                        drop.Add(kv.Key);
                foreach (int k in drop)
                    st.Requests.Remove(k);
            }
            return n;
        }

        private static int PopupMenu(SimHost h, CicodeTask t, State st, int menu, string items, int x, int y)
        {
            if (menu >= 0)
            {
                if (!st.Menus.TryGetValue(menu, out var l))
                    st.Menus[menu] = l = new List<string>();
                l.AddRange(SplitCsv(items));
                return NewRequest(st, new Request());
            }
            if (items.Length > 0)
            {
                // a show with items: they go to menu 0 first (guess)
                if (!st.Menus.TryGetValue(0, out var l0))
                    st.Menus[0] = l0 = new List<string>();
                l0.AddRange(SplitCsv(items));
            }
            var tree = Build(st, 0, 0);
            st.Menus.Clear();
            if (tree.Count == 0)
                return NewRequest(st, new Request());
            var d = new Dialog
            {
                Kind = DialogKind.PopupMenu,
                Title = "",
                X = x,
                Y = y,
                Menu = tree,
                MenuFont = DisplayBuiltins.Of(h).PopupMenuFont,
                Window = World.Display.TaskContext.Window(t, DisplayBuiltins.Of(h).DefaultWindow),
                Task = t.Handle,
            };
            h.World?.Trace?.Event(
                "dialog",
                null,
                t.Handle >= 0 ? (object)t.Handle : null,
                ("op", "show"),
                ("kind", "popupmenu"),
                ("title", "")
            );
            if (ReferenceEquals(h.Interaction, HeadlessInteraction.Instance))
                h.Log?.Invoke(HeadlessInteraction.DialogText(d) + ": cancelled (no UI)");
            h.Interaction.Show(d);
            return NewRequest(st, new Request { Dialog = d });
        }

        /// <summary>The CSV items of a call (a ',' separates; quotes are not special: the CI
        /// passes the text as written, '^n' already a line feed).</summary>
        private static List<string> SplitCsv(string s)
        {
            var o = new List<string>();
            if (s.Length == 0)
                return o;
            o.AddRange(s.Split(','));
            return o;
        }

        /// <summary>A menu's items with its links followed (at most 8 deep).</summary>
        private static List<MenuItem> Build(State st, int menu, int depth)
        {
            var o = new List<MenuItem>();
            if (!st.Menus.TryGetValue(menu, out var raw) || depth > 8)
                return o;
            int pos = 0;
            foreach (string r in raw)
            {
                string s = r;
                if (s.Trim().Length == 0)
                {
                    o.Add(new MenuItem { Separator = true, Enabled = false });
                    continue;
                }
                var it = new MenuItem();
                // '!' disabled and '~' checked, in either order, before the text
                while (s.Length > 0 && (s[0] == '!' || s[0] == '~'))
                {
                    if (s[0] == '!')
                        it.Enabled = false;
                    else
                        it.Checked = true;
                    s = s.Substring(1);
                }
                if (s.StartsWith("[", StringComparison.Ordinal))
                {
                    int close = s.IndexOf(']');
                    if (close > 0)
                    {
                        it.Bitmap = s.Substring(1, close - 1);
                        s = s.Substring(close + 1);
                    }
                }
                int gt = s.LastIndexOf('>');
                if (
                    gt >= 0
                    && gt + 1 < s.Length
                    && int.TryParse(
                        s.Substring(gt + 1),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int link
                    )
                )
                {
                    it.Label = s.Substring(0, gt);
                    it.Items = link != menu ? Build(st, link, depth + 1) : new List<MenuItem>();
                    o.Add(it);
                    continue;
                }
                it.Label = s;
                it.Id = menu * 100 + (++pos);
                o.Add(it);
            }
            return o;
        }

        /// <summary>[{label, enabled, checked, separator, id, bitmap?, items?}].</summary>
        internal static JsonValue ToJson(IReadOnlyList<MenuItem> items, Func<string, string>? localise = null)
        {
            var a = JsonValue.NewArray();
            foreach (var it in items)
            {
                var o = JsonValue.NewObject();
                o.Set("label", localise != null ? localise(it.Label) : it.Label);
                o.Set("enabled", JsonValue.Of(it.Enabled));
                o.Set("checked", JsonValue.Of(it.Checked));
                o.Set("separator", JsonValue.Of(it.Separator));
                o.Set("id", JsonValue.Of(it.Id));
                if (it.Bitmap.Length > 0)
                    o.Set("bitmap", it.Bitmap);
                if (it.Items != null)
                    o.Set("items", ToJson(it.Items, localise));
                a.Add(o);
            }
            return a;
        }

        /// <summary>Whether a chosen number names an enabled item of the menu (an answer that
        /// names nothing counts as cancelled).</summary>
        internal static bool HasItem(IReadOnlyList<MenuItem> items, int id)
        {
            foreach (var it in items)
            {
                if (it.Id == id && id != 0)
                    return it.Enabled;
                if (it.Items != null && HasItem(it.Items, id))
                    return true;
            }
            return false;
        }
    }
}
