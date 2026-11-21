using System;
using System.Collections.Generic;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>
    /// The outside world the simulator doesn't reach: ActiveX, DDE, COM ports, devices, printing
    /// and so on.
    /// </summary>
    public static class Outside
    {
        /// <summary>No such name (a device, a server): 289.</summary>
        internal const int NameNotFoundCode = 289;

        private const int CannotOpen = 262,
            NameNotFound = 289,
            SqlError = 307,
            NoClass = 354;

        private static Value Bad(CicodeTask t, Value result, int code = Codes.BadHandle, bool halt = false)
        {
            t.RaiseError(code, halt);
            return result;
        }

        private static readonly Value NullVariant = Value.Variant(null);
        private static readonly Value NoObject = Value.Object(-1);

        private static void Log(SimHost h, CicodeTask t, string what) => h.Note(t, what + " (not simulated)");

        // ------------------------------------------------------------------ DLLs
        // The DLL functions are Builtins.Dll (ctsim/options dll: off by default).
        internal static void LogNotSimulated(SimHost h, CicodeTask t, string what) => Log(h, t, what);

        // ------------------------------------------------------------------ ActiveX and CiVBA objects
        [Builtin(
            "CreateObject",
            Status = BuiltinStatus.Stub,
            Returns = "-1",
            Doc = "1030953",
            Errors = "354 the class is not registered (raised)",
            Notes = "No COM object is ever made: an invalid object handle."
        )]
        private static Value CreateObject(SimHost h, CicodeTask t, ArgList a)
        {
            Log(h, t, "CreateObject(\"" + A.S(a.Get(0)) + "\")");
            return Bad(t, NoObject, NoClass);
        }

        [Builtin(
            "CreateControlObject",
            Status = BuiltinStatus.Stub,
            Returns = "-1",
            Doc = "1030952",
            Errors = "354 with the halt flag: the class is not registered",
            Notes = "No ActiveX control is ever made."
        )]
        private static Value CreateControlObject(SimHost h, CicodeTask t, ArgList a)
        {
            Log(h, t, "CreateControlObject(\"" + A.S(a.Get(0)) + "\")");
            return Bad(t, NoObject, NoClass, true);
        }

        // An ActiveX control of a shown page: the simulator runs no control, but the page's code
        // finds the object it names (ObjectByName("SEARCH") in SA_Include AlarmPage_Init) and
        // reads and writes its properties (SA_Include TreeviewSearch.ci polls the search box's
        // Text every 500 ms and filters the tree by it). So a control is a simulated property
        // store: a property reads what was last written to it (by the code or by a viewer,
        // ctsim/setObjectProperty), else its default ("": a control's own defaults are not
        // known); a method call does nothing and returns "". The store is the page display's: a
        // page shown again gets new controls (the runtime creates them when it shows the page).
        internal sealed class PageControl
        {
            public int Window,
                An;
            public string Page = "",
                Name = "";

            /// <summary>The page display the control belongs to.</summary>
            public global::CicodeSim.World.Display.PageInstance? Instance;

            /// <summary>The properties written so far (names without case), in first-written order.</summary>
            public readonly Dictionary<string, Value> Props = new Dictionary<string, Value>(
                StringComparer.OrdinalIgnoreCase
            );
            public readonly List<string> Order = new List<string>();
            public List<string>? Items;
            public int Selected = -1;

            /// <summary>A ListIndex value: a whole number (text that reads as one too); anything
            /// else is no row (the control refuses it, as an index out of range).</summary>
            public static bool IndexOf(Value value, out long index)
            {
                index = 0;
                if (
                    !global::CicodeSim.Values.FloatText.TryParseNumber(A.S(value).Trim(), out double d)
                    || d != Math.Floor(d)
                )
                    return false;
                index = (long)d;
                return true;
            }

            public bool Select(int index)
            {
                Selected = index;
                bool changed = Put("ListIndex", W.I(index));
                return Put("Text", W.S(index < 0 ? "" : Items![index])) || changed;
            }

            public bool Write(string name, Value value)
            {
                if (Items != null)
                {
                    if (C.IEq(name, "ListIndex"))
                    {
                        return IndexOf(value, out long index)
                            && index >= -1
                            && index < Items.Count
                            && Select((int)index);
                    }
                    if (C.IEq(name, "ListCount"))
                        return false;
                    if (C.IEq(name, "Text"))
                    {
                        string text = A.S(value);
                        // Preserve the selected occurrence when the list contains duplicates.
                        int index = Selected >= 0 && Items[Selected] == text ? Selected : Items.IndexOf(text);
                        Selected = index;
                        bool changed = Put("ListIndex", W.I(index));
                        return Put("Text", W.S(text)) || changed;
                    }
                }
                return Put(name, value);
            }

            /// <summary>The control's change count (a property written with a new value).</summary>
            public long Serial;

            /// <summary>Write a property; true when its value changed.</summary>
            public bool Put(string name, Value v)
            {
                if (Props.TryGetValue(name, out Value old))
                {
                    if (old.T == v.T && A.S(old) == A.S(v))
                        return false;
                }
                else
                    Order.Add(name);
                Props[name] = v;
                Serial++;
                return true;
            }
        }

        private sealed class Controls
        {
            public readonly Dictionary<long, PageControl> ByHandle = new Dictionary<long, PageControl>();
            public long Next = 0x10001;
            public readonly HashSet<string> Logged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Grows with every control made or property changed (a viewer's page state).</summary>
            public long Serial;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
            SimHost,
            Controls
        > ControlTables = new System.Runtime.CompilerServices.ConditionalWeakTable<SimHost, Controls>();

        private static string Inv(int v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The ActiveX control named <paramref name="name"/> on the page of a window: the
        /// project's activex.dbf rows of the page (OBJ_NAME, else the default name 'AN' + the AN),
        /// then the page's drawing (an ActiveX record, type 0x0D, of that name or AN); 0 none.</summary>
        internal static int FindControl(SimHost h, global::CicodeSim.World.Display.Window w, string name)
        {
            var page = w.Page;
            if (page == null || h.World == null)
                return 0;
            string n = name.Trim();
            if (n.Length == 0)
                return 0;
            try
            {
                foreach (var (_, r) in h.World.Unit.Rows("activex.dbf"))
                {
                    if (
                        !string.Equals(
                            Project.CitectText.Trim(r.Get("PAGE")),
                            page.Name,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        continue;
                    if (
                        !int.TryParse(
                            Project.CitectText.Trim(r.Get("AN")),
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out int an
                        )
                    )
                        continue;
                    string obj = Project.CitectText.Trim(r.Get("OBJ_NAME"));
                    if (obj.Length == 0)
                        obj = "AN" + Inv(an);
                    if (string.Equals(obj, n, StringComparison.OrdinalIgnoreCase))
                        return an;
                }
            }
            catch (Exception e)
                when (e is System.IO.IOException || e is FormatException || e is InvalidOperationException)
            { }
            var drawing = page.Drawing;
            if (drawing != null)
                foreach (var d in drawing.Objects.Values)
                {
                    if (d.Type != 0x0D)
                        continue;
                    if (
                        string.Equals(d.Name, n, StringComparison.OrdinalIgnoreCase)
                        || string.Equals("AN" + Inv(d.An), n, StringComparison.OrdinalIgnoreCase)
                    )
                        return d.An;
                }
            return 0;
        }

        /// <summary>The name a page's control at an AN has: its activex.dbf OBJ_NAME, else its
        /// drawing record's name, else 'AN' + the AN; null when the AN is no control of the page.</summary>
        private static string? ControlName(
            SimHost h,
            global::CicodeSim.World.Display.Window w,
            int an,
            out string clsid
        )
        {
            var page = w.Page;
            clsid = page?.Drawing?.Get(an)?.ControlClsid ?? "";
            if (page == null || h.World == null)
                return null;
            try
            {
                foreach (var (_, r) in h.World.Unit.Rows("activex.dbf"))
                {
                    if (
                        !string.Equals(
                            Project.CitectText.Trim(r.Get("PAGE")),
                            page.Name,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        continue;
                    if (
                        !int.TryParse(
                            Project.CitectText.Trim(r.Get("AN")),
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out int ra
                        )
                        || ra != an
                    )
                        continue;
                    string obj = Project.CitectText.Trim(r.Get("OBJ_NAME"));
                    if (clsid.Length == 0)
                        clsid = Project.CitectText.Trim(r.Get("OBJ_GUID"));
                    return obj.Length > 0 ? obj : "AN" + Inv(an);
                }
            }
            catch (Exception e)
                when (e is System.IO.IOException || e is FormatException || e is InvalidOperationException)
            { }
            var d = page.Drawing?.Get(an);
            if (d != null && d.Type == 0x0D)
                return d.Name.Length > 0 ? d.Name : "AN" + Inv(an);
            return null;
        }

        private static bool Live(SimHost h, PageControl pc)
        {
            var w = DisplayBuiltins.Of(h).OpenWindow(pc.Window);
            return w?.Page != null && ReferenceEquals(w.Page, pc.Instance);
        }

        /// <summary>The live control a handle names: one ObjectByNameEx gave whose page display is
        /// still shown in its window; null otherwise.</summary>
        private static PageControl? Control(SimHost h, in Value v)
        {
            long hd = A.I(v);
            if (!ControlTables.TryGetValue(h, out var c) || !c.ByHandle.TryGetValue(hd, out var pc))
                return null;
            return Live(h, pc) ? pc : null;
        }

        /// <summary>The control of a window's page at an AN with its handle: the one made before,
        /// else a new one (the controls of page displays no longer shown go then).</summary>
        private static (long handle, PageControl pc) ControlAt(
            SimHost h,
            global::CicodeSim.World.Display.Window w,
            int an,
            string name,
            string? controlClsid = null
        )
        {
            var tab = ControlTables.GetValue(h, _ => new Controls());
            foreach (var kv in tab.ByHandle)
                if (
                    kv.Value.Window == w.Number
                    && kv.Value.An == an
                    && ReferenceEquals(kv.Value.Instance, w.Page)
                )
                    return (kv.Key, kv.Value);
            var gone = new List<long>();
            foreach (var kv in tab.ByHandle)
                if (!Live(h, kv.Value))
                    gone.Add(kv.Key);
            foreach (long g in gone)
                tab.ByHandle.Remove(g);
            long hd = tab.Next++;
            var pc = new PageControl
            {
                Window = w.Number,
                An = an,
                Page = w.Page?.Name ?? "",
                Name = name,
                Instance = w.Page,
            };
            if (controlClsid == null)
                ControlName(h, w, an, out controlClsid);
            if (C.IEq(controlClsid, "{E8A1DF62-3D7C-49B6-9344-2104253063A4}"))
            {
                pc.Items = new List<string>();
                pc.Put("ListCount", W.I(0));
                pc.Select(-1);
            }
            tab.ByHandle[hd] = pc;
            tab.Serial++;
            return (hd, pc);
        }

        /// <summary>The change count of every page's controls (a control made, a property changed).</summary>
        public static long ControlsSerial(SimHost h) =>
            ControlTables.TryGetValue(h, out var c) ? c.Serial : 0;

        /// <summary>The controls of a window's page the code or a viewer used, for a viewer's page
        /// state (PROTOCOL.md 'controls'): [{an, name, properties: {name: text}, serial}] in AN
        /// order, null when there are none.</summary>
        public static JsonValue? ControlsJson(SimHost h, global::CicodeSim.World.Display.Window w)
        {
            if (w.Page == null || !ControlTables.TryGetValue(h, out var tab))
                return null;
            var list = new List<PageControl>();
            foreach (var pc in tab.ByHandle.Values)
                if (pc.Window == w.Number && ReferenceEquals(pc.Instance, w.Page))
                    list.Add(pc);
            if (list.Count == 0)
                return null;
            list.Sort((a, b) => a.An.CompareTo(b.An));
            var arr = JsonValue.NewArray();
            foreach (var pc in list)
            {
                var o = JsonValue.NewObject();
                o.Set("an", JsonValue.Of(pc.An));
                o.Set("name", pc.Name);
                var props = JsonValue.NewObject();
                foreach (string k in pc.Order)
                    props.Set(k, A.S(pc.Props[k]));
                o.Set("properties", props);
                if (pc.Items != null)
                {
                    var items = JsonValue.NewArray();
                    foreach (string item in pc.Items)
                        items.Add(JsonValue.Of(item));
                    o.Set("items", items);
                }
                o.Set("serial", JsonValue.Of(pc.Serial));
                arr.Add(o);
            }
            return arr;
        }

        /// <summary>A viewer writes a property of an ActiveX control on a window's page.</summary>
        public static bool? SetControlProperty(
            SimHost h,
            global::CicodeSim.World.Display.Window w,
            int an,
            string property,
            string value
        )
        {
            if (w.Page == null || property.Trim().Length == 0)
                return null;
            string? name = ControlName(h, w, an, out string clsid);
            if (name == null)
                return null;
            var (_, pc) = ControlAt(h, w, an, name, clsid);
            if (pc.Items != null && C.IEq(property.Trim(), "ListIndex"))
            {
                if (
                    !int.TryParse(
                        value,
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out int index
                    )
                    || index < -1
                    || index >= pc.Items.Count
                )
                    return null;
            }
            bool changed = pc.Write(property.Trim(), Value.Str(value));
            if (changed)
                ControlTables.GetValue(h, _ => new Controls()).Serial++;
            return changed;
        }

        [Builtin(
            "ObjectByNameEx",
            Errors = "360 no object of that name on the window's page (raised, -1)",
            Notes = "An ActiveX control of the window's page (ObjectByName = ObjectByNameEx(WinNumber(), name)): the project's activex.dbf rows of the page (OBJ_NAME, the default 'AN<n>'), else the page's drawing (an ActiveX record of that name): the object handle of a simulated control (no control runs: a property store, see _ObjectGetProperty; the same control while its page display is shown, a page shown again gets new ones); another name 360 and -1."
        )]
        private static Value ObjectByNameEx(SimHost h, CicodeTask t, ArgList a)
        {
            var d = DisplayBuiltins.Of(h);
            long win = a.Count > 1 ? A.Short(A.I(a.Get(0))) : d.WindowOf(t);
            string name = A.S(a.Get(a.Count > 1 ? 1 : 0));
            var w = d.OpenWindow(win < 0 ? d.WindowOf(t) : (int)win);
            int an = w != null ? FindControl(h, w, name) : 0;
            if (w?.Page == null || an <= 0)
                return Bad(t, NoObject, 360);
            var (hd, pc) = ControlAt(h, w, an, name.Trim());
            var tab = ControlTables.GetValue(h, _ => new Controls());
            if (tab.Logged.Add(w.Page.Name + "|" + Inv(an)))
                h.Note(
                    t,
                    "ActiveX control '"
                        + name
                        + "' (AN "
                        + Inv(an)
                        + " of "
                        + w.Page.Name
                        + (pc.Items == null ? "): simulated as a property store" : "): simulated combo box")
                );
            return Value.Object(hd);
        }

        [Builtin(
            "ObjectIsValid",
            Doc = "1030958",
            Notes = "1 for a simulated control ObjectByNameEx gave while its page display is shown, else 0 (no other object is ever live)."
        )]
        private static Value ObjectIsValid(SimHost h, CicodeTask t, ArgList a) =>
            W.I(Control(h, a.Get(0)) != null ? 1 : 0);

        [Builtin(
            "ObjectToStr",
            Doc = "1030959",
            Notes = "A simulated control's handle in decimal; an object that is not live gives \"\"."
        )]
        private static Value ObjectToStr(SimHost h, CicodeTask t, ArgList a) =>
            Control(h, a.Get(0)) != null ? W.S(C.Inv(A.I(a.Get(0)))) : W.Empty;

        [Builtin(
            "ObjectNothing",
            Status = BuiltinStatus.Stub,
            Returns = "-1",
            Notes = "The empty object handle."
        )]
        private static Value ObjectNothing(SimHost h, CicodeTask t, ArgList a) => NoObject;

        [Builtin(
            "ObjectHasInterface",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1030957",
            Errors = "274 not a live object (raised)",
            Notes = "A simulated control has no interface: 0."
        )]
        private static Value ObjectHasInterface(SimHost h, CicodeTask t, ArgList a) =>
            Control(h, a.Get(0)) != null ? W.I(0) : Bad(t, W.I(0), Codes.InvalidArg);

        [Builtin(
            "_ObjectGetProperty",
            Doc = "1030949",
            Errors = "274 not a live object (raised)",
            Notes = "Reads the control's stored property. ciText.ComboBox also exposes ListCount, zero-based ListIndex (-1 for no selection), and Text."
        )]
        private static Value ObjectGetProperty(SimHost h, CicodeTask t, ArgList a)
        {
            var pc = Control(h, a.Get(0));
            if (pc == null)
                return Bad(t, NullVariant, Codes.InvalidArg);
            return pc.Props.TryGetValue(A.S(a.Get(1)).Trim(), out Value v) ? v : W.Empty;
        }

        [Builtin(
            "_ObjectSetProperty",
            Doc = "1030950",
            Errors = "274 not a live object (raised, returned)",
            Notes = "Stores a control property. On ciText.ComboBox, writing ListIndex selects a row and updates Text. Writing Text selects a matching row if present."
        )]
        private static Value ObjectSetProperty(SimHost h, CicodeTask t, ArgList a)
        {
            var pc = Control(h, a.Get(0));
            if (pc == null)
                return Bad(t, W.I(Codes.InvalidArg), Codes.InvalidArg);
            string name = A.S(a.Get(1)).Trim();
            if (
                pc.Items != null
                && C.IEq(name, "ListIndex")
                && (!PageControl.IndexOf(a.Get(2), out long want) || want < -1 || want >= pc.Items.Count)
            )
                return Bad(t, W.I(Codes.InvalidArg), Codes.InvalidArg);
            if (name.Length > 0 && pc.Write(name, a.Get(2)))
                ControlTables.GetValue(h, _ => new Controls()).Serial++;
            return W.I(0);
        }

        [Builtin(
            "_ObjectCallMethod",
            Doc = "1030948",
            Errors = "274 not a live object (raised)",
            Notes = "ciText.ComboBox implements AddItem(item, optional index), RemoveItem(index) and Clear(). Other control methods return an empty string. An invalid handle raises 274."
        )]
        private static Value ObjectCallMethod(SimHost h, CicodeTask t, ArgList a)
        {
            var pc = Control(h, a.Get(0));
            if (pc == null)
                return Bad(t, NullVariant, Codes.InvalidArg);
            var items = pc.Items;
            if (items == null)
                return W.Empty;

            string method = A.S(a.Get(1)).Trim();
            var args = Dll.Extras(a, 2);
            if (C.IEq(method, "AddItem"))
            {
                long index = args.Count > 1 ? A.I(args[1]) : items.Count;
                if (args.Count < 1 || args.Count > 2 || index < 0 || index > items.Count)
                    return Bad(t, NullVariant, Codes.InvalidArg);
                items.Insert((int)index, A.S(args[0]));
                if (pc.Selected >= index)
                    pc.Select(pc.Selected + 1);
            }
            else if (C.IEq(method, "RemoveItem"))
            {
                if (args.Count != 1)
                    return Bad(t, NullVariant, Codes.InvalidArg);
                long index = A.I(args[0]);
                if (index < 0 || index >= items.Count)
                    return Bad(t, NullVariant, Codes.InvalidArg);
                items.RemoveAt((int)index);
                if (pc.Selected == index)
                    pc.Select(-1);
                else if (pc.Selected > index)
                    pc.Select(pc.Selected - 1);
            }
            else if (C.IEq(method, "Clear"))
            {
                items.Clear();
                pc.Select(-1);
            }
            else
                return W.Empty;

            pc.Put("ListCount", W.I(items.Count));
            ControlTables.GetValue(h, _ => new Controls()).Serial++;
            return W.Empty;
        }

        [Builtin(
            "ObjectAssociateEvents",
            Status = BuiltinStatus.Stub,
            Doc = "1030954",
            Errors = "274 not a live object (raised, returned)",
            Notes = "A control stub raises no events: 0."
        )]
        private static Value ObjectAssociateEvents(SimHost h, CicodeTask t, ArgList a) =>
            Control(h, a.Get(0)) != null ? W.I(0) : Bad(t, W.I(Codes.InvalidArg), Codes.InvalidArg);

        [Builtin(
            "ObjectAssociatePropertyWithTag",
            Status = BuiltinStatus.Stub,
            Doc = "1030955",
            Errors = "274 not a live object (raised, returned)",
            Notes = "A control stub: nothing is associated, 0."
        )]
        private static Value ObjectAssociatePropertyWithTag(SimHost h, CicodeTask t, ArgList a) =>
            Control(h, a.Get(0)) != null ? W.I(0) : Bad(t, W.I(Codes.InvalidArg), Codes.InvalidArg);

        [Builtin(
            "ObjectSaveState",
            Status = BuiltinStatus.Stub,
            Errors = "274 not a live object (raised, returned)",
            Notes = "A control stub: 0."
        )]
        private static Value ObjectSaveState(SimHost h, CicodeTask t, ArgList a) =>
            Control(h, a.Get(0)) != null ? W.I(0) : Bad(t, W.I(Codes.InvalidArg), Codes.InvalidArg);

        [Builtin(
            "ObjectResetState",
            Status = BuiltinStatus.Stub,
            Errors = "274 not a live object (raised, returned)",
            Notes = "A control stub: 0."
        )]
        private static Value ObjectResetState(SimHost h, CicodeTask t, ArgList a) =>
            Control(h, a.Get(0)) != null ? W.I(0) : Bad(t, W.I(Codes.InvalidArg), Codes.InvalidArg);

        [Builtin(
            "_ObjectServerInvoke",
            Status = BuiltinStatus.Stub,
            Returns = "\"\"",
            Notes = "Does nothing in the runtime either."
        )]
        private static Value ObjectServerInvoke(SimHost h, CicodeTask t, ArgList a) => W.Empty;

        [Builtin(
            "_ObjectServerInvokeEx",
            Status = BuiltinStatus.Stub,
            Returns = "\"\"",
            Notes = "Does nothing in the runtime either."
        )]
        private static Value ObjectServerInvokeEx(SimHost h, CicodeTask t, ArgList a) => W.Empty;

        [Builtin(
            "_VbExpressionOpen",
            Status = BuiltinStatus.Stub,
            Returns = "-1",
            Errors = "274 (raised)",
            Notes = "No CiVBA in the simulator."
        )]
        private static Value VbExpressionOpen(SimHost h, CicodeTask t, ArgList a) =>
            Bad(t, NoObject, Codes.InvalidArg);

        [Builtin(
            "_VbCallRun",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Errors = "274 (raised)",
            Notes = "No CiVBA in the simulator."
        )]
        private static Value VbCallRun(SimHost h, CicodeTask t, ArgList a) =>
            Bad(t, W.I(0), Codes.InvalidArg);

        [Builtin(
            "_VbCicodeCallReturn",
            Status = BuiltinStatus.Stub,
            Notes = "No CiVBA in the simulator: nothing to return to."
        )]
        private static Value VbCicodeCallReturn(SimHost h, CicodeTask t, ArgList a) => Value.Nothing;

        [Builtin(
            "VbCallOpen",
            Status = BuiltinStatus.Stub,
            Returns = "-1",
            Doc = "1218134",
            Notes = "No CiVBA in the simulator: the runtime's path without the CiVBA engine (0) or without the named function returns -1 and raises nothing (the help says 0). Projects that log every operator command through VbCallReturn(VbCallRun(VbCallOpen(...))) after the command's TagWrite run on."
        )]
        private static Value VbCallOpen(SimHost h, CicodeTask t, ArgList a) => NoObject;

        [Builtin(
            "VbCallReturn",
            Status = BuiltinStatus.Stub,
            Returns = "\"\"",
            Doc = "1218135",
            Notes = "No CiVBA in the simulator: the handle is no live call (the runtime finds no CiVbCode symbol) and nothing is raised. The result is the VARIANT VariantInit leaves (VT_EMPTY), which COM converts to '' / 0 without an error: given as '' (guess, verify: the engine's null VARIANT reference raised 433 on the store into a STRING, which halted the end of such an operator command)."
        )]
        private static Value VbCallReturn(SimHost h, CicodeTask t, ArgList a) => W.Empty;

        [Builtin(
            "HtmlHelp",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Notes = "No help viewer in the simulator. nCommand 2 (close all) 0; 0 (contents), 1 (context number) and 3 (topic file) open the viewer in the runtime (0, or 274 when HtmlHelpA fails twice): 0 here, as if it opened (guess); another command 257 (returned). Nothing is raised."
        )]
        private static Value HtmlHelp(SimHost h, CicodeTask t, ArgList a)
        {
            long cmd = A.Short(A.I(a.Get(1)));
            if (cmd != 0 && cmd != 1 && cmd != 2 && cmd != 3)
                return W.I(Codes.OutOfRange);
            if (cmd != 2)
                Log(h, t, "HtmlHelp " + A.S(a.Get(0)));
            return W.I(0);
        }

        // ------------------------------------------------------------------ SQL
        // The SQL functions are Builtins.Sql; these are what they answer while the simulation may
        // not reach a database (ctsim/options sql 'off', the default): no database, no handle.
        private static Value SqlFail(SimHost h, CicodeTask t, Value result, int code = Codes.BadHandle)
        {
            h.SqlFailed = true;
            return Bad(t, result, code);
        }

        /// <summary>SQLConnect without a database: fails as a database that cannot be reached
        /// (307 at the task's next turn; 282 in foreground code).</summary>
        internal static Value SQLConnectOff(SimHost h, CicodeTask t, ArgList a)
        {
            if (t.Foreground)
                return SqlFail(h, t, W.I(-1), Codes.FgBlock);
            Log(h, t, "SQLConnect");
            return W.BlockOneTurn(t, "SQLConnect", () => SqlFail(h, t, W.I(-1), SqlError));
        }

        internal static Value SQLCreateOff(SimHost h, CicodeTask t, ArgList a)
        {
            Log(h, t, "SQLCreate");
            return SqlFail(h, t, W.I(-1), SqlError);
        }

        /// <summary>"SQL Error" after a failed SQL call, "Successful completion" before any.</summary>
        internal static Value SQLErrMsgOff(SimHost h, CicodeTask t, ArgList a) =>
            W.S(h.SqlFailed ? "SQL Error" : "Successful completion");

        // the SQL functions on a handle: no handle exists
        internal static Value SqlStatus(SimHost h, CicodeTask t, ArgList a) =>
            SqlFail(h, t, W.I(Codes.BadHandle));

        internal static Value SqlHandle(SimHost h, CicodeTask t, ArgList a) => SqlFail(h, t, W.I(-1));

        internal static Value SqlCount(SimHost h, CicodeTask t, ArgList a) => SqlFail(h, t, W.I(0));

        internal static Value SqlText(SimHost h, CicodeTask t, ArgList a) => SqlFail(h, t, W.Empty);

        internal static Value SQLFieldInfoOff(SimHost h, CicodeTask t, ArgList a)
        {
            W.Out(t, a.Get(2), "");
            W.Out(t, a.Get(3), 0);
            return SqlFail(h, t, W.I(0));
        }

        [Builtin(
            "ExecuteDTSPkg",
            Doc = "1192457",
            Status = BuiltinStatus.Stub,
            Returns = "307",
            Errors = "307 no database (raised, returned)",
            Notes = "A SQL Server DTS package: no database in the simulator."
        )]
        private static Value ExecuteDTSPkg(SimHost h, CicodeTask t, ArgList a) =>
            SqlFail(h, t, W.I(SqlError), SqlError);

        // ------------------------------------------------------------------ devices (Devices.cs)
        [Builtin(
            "PrintFont",
            Status = BuiltinStatus.Stub,
            Returns = "269",
            Doc = "1076402",
            Errors = "269 with the halt flag: no such device (returned)",
            Notes = "A printer font: the printer devices spool text (Devices.cs), no fonts."
        )]
        private static Value PrintFont(SimHost h, CicodeTask t, ArgList a) =>
            Bad(t, W.I(Codes.BadHandle), Codes.BadHandle, true);

        // ------------------------------------------------------------------ plotting
        [Builtin(
            "PlotOpen",
            Status = BuiltinStatus.Stub,
            Returns = "-1",
            Doc = "1188851",
            Errors = "274 no page AN to plot on or no printer (raised)",
            Notes = "No page or printer to plot on in the simulator."
        )]
        private static Value PlotOpen(SimHost h, CicodeTask t, ArgList a)
        {
            Log(h, t, "PlotOpen");
            return Bad(t, W.I(-1), Codes.InvalidArg);
        }

        [Builtin(
            "PlotClose",
            Status = BuiltinStatus.Stub,
            Returns = "269",
            Doc = "1188844",
            Errors = "269 no such plot (raised, returned)"
        )]
        [Builtin(
            "PlotGrid",
            Status = BuiltinStatus.Stub,
            Returns = "269",
            Doc = "1188847",
            Errors = "269 no such plot (raised, returned)"
        )]
        [Builtin(
            "PlotLine",
            Status = BuiltinStatus.Stub,
            Returns = "269",
            Doc = "1188849",
            Errors = "269 no such plot (raised, returned)"
        )]
        [Builtin(
            "PlotXYLine",
            Status = BuiltinStatus.Stub,
            Returns = "269",
            Doc = "1188855",
            Errors = "269 no such plot (raised, returned)"
        )]
        [Builtin(
            "PlotDraw",
            Status = BuiltinStatus.Stub,
            Returns = "269",
            Doc = "1188845",
            Errors = "269 no such plot (raised, returned)"
        )]
        [Builtin(
            "PlotMarker",
            Status = BuiltinStatus.Stub,
            Returns = "269",
            Doc = "1188850",
            Errors = "269 no such plot (raised, returned)"
        )]
        [Builtin(
            "PlotText",
            Status = BuiltinStatus.Stub,
            Returns = "269",
            Doc = "1188854",
            Errors = "269 no such plot (raised, returned)"
        )]
        [Builtin(
            "PlotScaleMarker",
            Status = BuiltinStatus.Stub,
            Returns = "269",
            Doc = "1188852",
            Errors = "269 no such plot (raised, returned)"
        )]
        [Builtin(
            "PlotSetMarker",
            Status = BuiltinStatus.Stub,
            Returns = "269",
            Doc = "1188853",
            Errors = "269 no such marker (raised, returned)"
        )]
        private static Value PlotStatus(SimHost h, CicodeTask t, ArgList a) => Bad(t, W.I(Codes.BadHandle));

        [Builtin(
            "PlotInfo",
            Status = BuiltinStatus.Stub,
            Returns = "\"\"",
            Doc = "1188848",
            Errors = "269 no such plot (raised)"
        )]
        private static Value PlotInfo(SimHost h, CicodeTask t, ArgList a) => Bad(t, W.Empty);

        [Builtin(
            "PlotGetMarker",
            Status = BuiltinStatus.Stub,
            Returns = "-1",
            Doc = "1188846",
            Errors = "289 no such marker (raised)"
        )]
        private static Value PlotGetMarker(SimHost h, CicodeTask t, ArgList a) =>
            Bad(t, W.I(-1), NameNotFound);

        [Builtin(
            "FileRichTextPrint",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1105376",
            Notes = "Printing: nothing is printed, 0."
        )]
        private static Value FileRichTextPrint(SimHost h, CicodeTask t, ArgList a)
        {
            Log(h, t, "FileRichTextPrint(\"" + A.S(a.Get(0)) + "\")");
            return W.I(0);
        }

        // ------------------------------------------------------------------ programs, reports, the process
        [Builtin(
            "Exec",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1188588",
            Notes = "Never runs a program: logged, 0 (guess: as a started program; a refusal would break shipped buttons)."
        )]
        private static Value Exec(SimHost h, CicodeTask t, ArgList a)
        {
            Log(h, t, "Exec(\"" + A.S(a.Get(0)) + "\")");
            return W.I(0);
        }

        [Builtin(
            "Shutdown",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1188618",
            Notes = "Logged; the simulation goes on (it does not stop the process), 0."
        )]
        private static Value Shutdown(SimHost h, CicodeTask t, ArgList a)
        {
            Log(h, t, "Shutdown(\"" + A.S(a.Get(0)) + "\", \"" + A.S(a.Get(1)) + "\")");
            return W.I(0);
        }

        [Builtin(
            "ShutdownMode",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1188620",
            Notes = "No shutdown is under way: 0."
        )]
        private static Value ShutdownMode(SimHost h, CicodeTask t, ArgList a) => W.I(0);

        [Builtin(
            "Beep",
            Status = BuiltinStatus.Stub,
            Returns = "1",
            Doc = "1188578",
            Notes = "No sound; returns 1 as the runtime does."
        )]
        private static Value Beep(SimHost h, CicodeTask t, ArgList a) => W.I(1);

        [Builtin(
            "LanguageFileTranslate",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1188600",
            Notes = "No language database is loaded: nothing is written, 0."
        )]
        private static Value LanguageFileTranslate(SimHost h, CicodeTask t, ArgList a) => W.I(0);

        [Builtin(
            "Report",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1189291",
            Notes = "No report server: the report does not run, 0."
        )]
        private static Value Report(SimHost h, CicodeTask t, ArgList a)
        {
            Log(h, t, "Report(\"" + A.S(a.Get(0)) + "\")");
            return W.I(0);
        }

        [Builtin(
            "RepGetControl",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1189290",
            Notes = "No report runs: 0."
        )]
        private static Value RepGetControl(SimHost h, CicodeTask t, ArgList a) => W.I(0);

        [Builtin(
            "RepSetControl",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1189292",
            Notes = "No report runs: 0."
        )]
        private static Value RepSetControl(SimHost h, CicodeTask t, ArgList a) => W.I(0);

        [Builtin(
            "RepGetCluster",
            Doc = "1189289",
            Status = BuiltinStatus.Stub,
            Returns = "\"\"",
            Notes = "No report runs: \"\"."
        )]
        private static Value RepGetCluster(SimHost h, CicodeTask t, ArgList a) => W.Empty;
    }
}
