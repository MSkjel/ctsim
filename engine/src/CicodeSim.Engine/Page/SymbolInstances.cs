using System;
using System.Collections.Generic;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.World.Display;

namespace CicodeSim.Page
{
    /// <summary>The genies DspSym places.</summary>
    public sealed partial class PageRun
    {
        private sealed class Instance
        {
            public int An;
            public string Reference = "";
            public readonly List<int> Members = new List<int>();
            public readonly Dictionary<int, int> Map = new Dictionary<int, int>();
            public readonly Dictionary<int, DrawObject> Drawings = new Dictionary<int, DrawObject>();
            public CompiledPage? Code;
            public bool Applied;

            /// <summary>The AN's own type and extent before the genie came (put back when it goes).</summary>
            public int RootType;
            public Rect RootExtent,
                RootBounds;

            /// <summary>The root as placed: the element root's type, its extent on the page.</summary>
            public int RootTypeElement;
            public Rect PlacedExtent,
                PlacedBounds;

            /// <summary>What it was placed from: the element, the AN's point, the members' JSON
            /// (the same DspSym again keeps the instance).</summary>
            public LibraryElement? Element;
            public int BaseX,
                BaseY;
            public JsonValue? Json;
        }

        private readonly Dictionary<int, Instance> _instances = new Dictionary<int, Instance>();
        private readonly List<Instance> _pendingInstances = new List<Instance>();

        /// <summary>The genies placed on the page: (AN, 'library.element', member count).</summary>
        public List<(int An, string Reference, int Members)> SymbolInstances()
        {
            var l = new List<(int, string, int)>();
            foreach (var i in _instances.Values)
                l.Add((i.An, i.Reference, i.Members.Count));
            return l;
        }

        /// <summary>Place the element on the AN (a genie placed there before goes first): the
        /// members as page objects now, the rows at the next scan. The members' JSON.</summary>
        internal JsonValue PlaceSymbol(int an, string reference, LibraryElement el, CompiledPage? code)
        {
            var root = Page.Ensure(an);
            int bx = root.PosX + root.MovedX,
                by = root.PosY + root.MovedY;
            // the same genie again on the AN, at the same point (a DspSym in a loop or a Cicode
            // object's CMD every scan): kept as it is, its members' ANs, metadata and rows (else
            // every call made new member ANs and the viewer drew the genie again; guess: the
            // runtime's own output of an unchanged DspSym is not read)
            if (
                _instances.TryGetValue(an, out var had)
                && had.Json != null
                && had.Element == el
                && had.Code == code
                && string.Equals(had.Reference, reference, StringComparison.OrdinalIgnoreCase)
                && had.BaseX == bx
                && had.BaseY == by
            )
                return had.Json;
            RemoveSymbol(an);
            var inst = new Instance
            {
                An = an,
                Reference = reference,
                Code = code,
                Element = el,
                BaseX = bx,
                BaseY = by,
            };
            int dx = bx - el.OriginX,
                dy = by - el.OriginY;
            Rect Shift(Rect r) => new Rect(r.L + dx, r.T + dy, r.R + dx, r.B + dy);
            var members = JsonValue.NewArray();
            void Member(int a, ElementRecord r)
            {
                var m = JsonValue.NewObject();
                m.Set("an", JsonValue.Of(a));
                m.Set("handle", JsonValue.Of(r.Handle));
                m.Set("name", r.Name);
                m.Set("type", JsonValue.Of(r.Type));
                members.Add(m);
            }
            // the root: the AN itself, a group of the element's extent (a Cicode object that
            // DspSyms a genie on its own AN then finds the genie's members from it: SA_Include
            // PageNavigation.ci DspSym(iSymbolAn, ...) + DspGetAnFromNameRelative(iSymbolAn,
            // ".Shape"), ExampleSA's priority buttons DspSetMetadataFromName(".Shape", ...))
            inst.RootType = root.Type;
            inst.RootExtent = root.Extent;
            inst.RootBounds = root.Bounds;
            root.Type = inst.RootTypeElement = el.Root.Type;
            root.Extent = inst.PlacedExtent = Shift(el.Root.Extent);
            root.Bounds = inst.PlacedBounds = Shift(el.Root.Bounds);
            if (root.Dynamic && root.Metadata.Count == 0)
                root.Metadata.AddRange(el.Root.Metadata);
            inst.Map[el.Root.Handle + 1] = an;
            Member(an, el.Root);
            void Walk(ElementRecord rec, int parent, AnObject owner)
            {
                foreach (var c in rec.Children)
                {
                    if (c.IsInput)
                    {
                        // a command record is its owner's (the same handle)
                        if (c.Type == 0x1010)
                            owner.HasTouch = true;
                        else
                            owner.HasKeyCommand = true;
                        if (owner.Drawing != null)
                        {
                            owner.Drawing.HasTouch = owner.HasTouch;
                            owner.Drawing.HasKeyCommand = owner.HasKeyCommand;
                        }
                        continue;
                    }
                    int n = Page.NextDynamicAn;
                    while (n > 0 && Page.Objects.ContainsKey(n))
                        n--;
                    if (n <= 0)
                        return; // no AN left (the runtime's 272)
                    Page.NextDynamicAn = n - 1;
                    var o = Page.Ensure(n);
                    o.Dynamic = true;
                    o.Parent = parent;
                    o.Name = c.Name;
                    o.Type = c.Type;
                    o.Extent = Shift(c.Extent);
                    o.Bounds = Shift(c.Bounds);
                    o.PosX = o.RawPosX = c.AnchorX + dx;
                    o.PosY = o.RawPosY = c.AnchorY + dy;
                    o.Metadata.AddRange(c.Metadata);
                    var d = new DrawObject
                    {
                        An = n,
                        Type = c.Type,
                        Parent = parent,
                        Name = c.Name,
                        Rect = o.Extent,
                        Bounds = o.Bounds,
                        PosX = o.PosX,
                        PosY = o.PosY,
                    };
                    d.Metadata.AddRange(c.Metadata);
                    d.Colour = c.Colour;
                    o.Drawing = d;
                    inst.Drawings[n] = d;
                    if (Page.Objects.TryGetValue(parent, out var po))
                        po.Children.Add(n);
                    inst.Members.Add(n);
                    if (!inst.Map.ContainsKey(c.Handle + 1))
                        inst.Map[c.Handle + 1] = n;
                    Member(n, c);
                    Walk(c, n, o);
                }
            }
            Walk(el.Root, an, root);
            Page.StructureChanged();
            _instances[an] = inst;
            if (code != null)
                _pendingInstances.Add(inst);
            inst.Json = members;
            return members;
        }

        /// <summary>Re-apply DspSym genies after a drawing summary arrives.</summary>
        internal void KeepSymbols()
        {
            foreach (var inst in _instances.Values)
            {
                if (Page.Get(inst.An) is AnObject root)
                {
                    // what the drawing gave the AN is its own look again when the genie goes
                    if (root.Type != inst.RootTypeElement || !root.Extent.Equals(inst.PlacedExtent))
                    {
                        inst.RootType = root.Type;
                        inst.RootExtent = root.Extent;
                        inst.RootBounds = root.Bounds;
                    }
                    root.Type = inst.RootTypeElement;
                    root.Extent = inst.PlacedExtent;
                    root.Bounds = inst.PlacedBounds;
                    foreach (int m in inst.Members)
                        if (Page.Get(m) is AnObject mo && mo.Parent == inst.An && !root.Children.Contains(m))
                            root.Children.Add(m);
                }
                foreach (var kv in inst.Drawings)
                    if (_byAn.TryGetValue(kv.Key, out var o) && o.Program == inst.Code?.Program)
                        o.Drawing = kv.Value;
            }
            Page.StructureChanged();
        }

        /// <summary>DspAnMove moved the AN: its members (and their drawings, the classes' pivots)
        /// by the same offset.</summary>
        internal void MoveSymbol(int an, int dx, int dy)
        {
            if (!_instances.TryGetValue(an, out var inst))
                return;
            inst.BaseX += dx;
            inst.BaseY += dy;
            foreach (int m in inst.Members)
                if (Page.Get(m) is AnObject o)
                {
                    o.MovedX += dx;
                    o.MovedY += dy;
                }
            foreach (var d in inst.Drawings.Values)
            {
                if (d.Rect is Rect r)
                    d.Rect = new Rect(r.L + dx, r.T + dy, r.R + dx, r.B + dy);
                if (d.Bounds is Rect b)
                    d.Bounds = new Rect(b.L + dx, b.T + dy, b.R + dx, b.B + dy);
                if (d.PosX != null)
                    d.PosX += dx;
                if (d.PosY != null)
                    d.PosY += dy;
            }
        }

        /// <summary>The genie placed on the AN goes: its member objects and their rows.</summary>
        internal void RemoveSymbol(int an)
        {
            if (!_instances.TryGetValue(an, out var inst))
                return;
            _instances.Remove(an);
            _pendingInstances.Remove(inst);
            var gone = new HashSet<int>(inst.Members);
            var prog = inst.Code?.Program;
            if (inst.Applied && prog != null)
            {
                // the root's own rows go too (the AN stays: it is the page's or DspAnNew's)
                gone.Add(an);
                bool Mine(int a, CicodeSim.Compiled.Program? p) => gone.Contains(a) && p == prog;
                Objects.RemoveAll(o => Mine(o.An, o.Program));
                _order.RemoveAll(o => Mine(o.An, o.Program));
                foreach (int m in gone)
                {
                    if (_byAn.TryGetValue(m, out var o) && o.Program == prog)
                    {
                        _byAn.Remove(m);
                        State.Objects.Remove(m);
                    }
                    if (Touches.TryGetValue(m, out var tl))
                    {
                        tl.RemoveAll(t => t.Program == prog);
                        if (tl.Count == 0)
                            Touches.Remove(m);
                    }
                }
                Keys.RemoveAll(k => Mine(k.An, k.Program));
                CicodeObjects.RemoveAll(c => Mine(c.An, c.Program));
                gone.Remove(an);
            }
            foreach (int m in inst.Members)
                State.Objects.Remove(m);
            foreach (int m in inst.Members)
            {
                Page.Objects.Remove(m);
                Page.Order.Remove(m);
                Window.CommandCursor = Window.CommandCursor == m ? -1 : Window.CommandCursor;
            }
            if (Page.Get(an) is AnObject root)
            {
                root.Children.RemoveAll(c => gone.Contains(c));
                root.Type = inst.RootType;
                root.Extent = inst.RootExtent;
                root.Bounds = inst.RootBounds;
            }
            Page.StructureChanged();
        }

        /// <summary>The rows of the genies placed since the last scan join the page.</summary>
        private void ApplyInstances()
        {
            if (_pendingInstances.Count == 0)
                return;
            var list = new List<Instance>(_pendingInstances);
            _pendingInstances.Clear();
            foreach (var inst in list)
                if (inst.Code != null && _instances.TryGetValue(inst.An, out var cur) && cur == inst)
                    Join(inst);
        }

        private Dictionary<string, object?>? Remap(Instance inst, Dictionary<string, object?> r, out int an)
        {
            an = 0;
            if (!inst.Map.TryGetValue(CompiledPage.Int(r, "AN"), out an))
                return null;
            var c = new Dictionary<string, object?>(r, StringComparer.OrdinalIgnoreCase);
            c["AN"] = (long)an;
            int g = CompiledPage.Int(r, "GROUP", -1);
            if (g > 0 && inst.Map.TryGetValue(g, out int gm))
                c["GROUP"] = (long)gm;
            return c;
        }

        private void Join(Instance inst)
        {
            var cp = inst.Code!;
            var prog = cp.Program;
            var added = new List<AnimObject>();
            bool Own(int an) => an != inst.An || !_byAn.ContainsKey(an);
            AnimObject Make(int an, string table, int type)
            {
                var o = new AnimObject(an)
                {
                    Table = table,
                    Type = type,
                    Program = prog,
                };
                inst.Drawings.TryGetValue(an, out var d);
                o.Drawing = d ?? Page.Get(an)?.Drawing;
                _byAn[an] = o;
                Objects.Add(o);
                added.Add(o);
                return o;
            }
            foreach (var r0 in cp.Rows("PGDYNOBJ"))
            {
                var r = Remap(inst, r0, out int an);
                if (r == null || !Own(an))
                    continue;
                int type = CompiledPage.TypeCode(CompiledPage.Str(r, "TYPE"));
                var o =
                    _byAn.TryGetValue(an, out var had) && had.Program == prog
                        ? had
                        : Make(an, "PGDYNOBJ", type);
                o.Table = "PGDYNOBJ";
                o.Type = type;
                Fill(o, r, cp);
            }
            foreach (var r0 in cp.Rows("PGBUTTON"))
            {
                var r = Remap(inst, r0, out int an);
                if (r == null || !Own(an))
                    continue;
                var o =
                    _byAn.TryGetValue(an, out var had) && had.Program == prog
                        ? had
                        : Make(an, "PGBUTTON", 0x1001);
                o.Table = "PGBUTTON";
                o.Type = 0x1001;
                Fill(o, r, cp);
                o.ButtonText = TextOf(r, "TEXT");
                AddTouch(
                    new TouchCommand
                    {
                        An = an,
                        Button = true,
                        RptRate = CompiledPage.Int(r, "RPT_RATE"),
                        Up = cp.FieldCode(r, "CMD"),
                        Down = cp.FieldCode(r, "DOWN_CMD"),
                        Repeat = cp.FieldCode(r, "REPEAT_CMD"),
                        LogUp = TextOf(r, "MSG_LOG"),
                        Program = prog,
                    }
                );
            }
            foreach (var r0 in cp.Rows("PGTOUCH"))
            {
                var r = Remap(inst, r0, out int an);
                if (r == null)
                    continue;
                AddTouch(
                    new TouchCommand
                    {
                        An = an,
                        Action = CompiledPage.Int(r, "ACTION", 1),
                        RptRate = CompiledPage.Int(r, "RPT_RATE"),
                        Up = cp.FieldCode(r, "CMD"),
                        Down = cp.FieldCode(r, "DOWN_CMD"),
                        Dbl = cp.FieldCode(r, "DBLCLK_CMD"),
                        Repeat = cp.FieldCode(r, "REPEAT_CMD"),
                        LogUp = TextOf(r, "MSG_LOG"),
                        LogDown = TextOf(r, "MSG_LOGDN"),
                        LogDbl = TextOf(r, "MSG_LOGDBL"),
                        LogRepeat = TextOf(r, "MSG_LOGRPT"),
                        Program = prog,
                    }
                );
            }
            var keys = Animator.Keys;
            foreach (var r0 in cp.Rows("PGKEY"))
            {
                var r = Remap(inst, r0, out int an);
                if (r == null)
                    continue;
                var k = new KeyCommand
                {
                    An = an,
                    Sequence = TextOf(r, "KEY_SEQ"),
                    Cmd = cp.FieldCode(r, "CMD"),
                    Priv = CompiledPage.Int(r, "PRIV"),
                    Area = CompiledPage.Int(r, "AREA"),
                    MsgLog = TextOf(r, "MSG_LOG"),
                    Program = prog,
                };
                k.Codes = keys.Sequence(k.Sequence);
                Keys.Add(k);
            }
            foreach (var r0 in cp.Rows("PGADV"))
            {
                var r = Remap(inst, r0, out int an);
                if (r == null)
                    continue;
                CicodeObjects.Add(
                    new CicodeObject
                    {
                        An = an,
                        Cmd = cp.FieldCode(r, "CMD"),
                        Program = prog,
                    }
                );
            }
            // the parents (GROUP, else the placed record's parent) and the order, parents first
            foreach (var o in added)
            {
                if (o.GroupAn > 0 && _byAn.TryGetValue(o.GroupAn, out var p) && p != o)
                    o.Parent = p;
                else if (
                    o.Drawing != null
                    && o.Drawing.Parent > 0
                    && _byAn.TryGetValue(o.Drawing.Parent, out var p2)
                    && p2 != o
                )
                    o.Parent = p2;
            }
            var seen = new HashSet<AnimObject>(_order);
            void Visit(AnimObject o, int depth)
            {
                if (seen.Contains(o) || depth > 64)
                    return;
                if (o.Parent != null && !seen.Contains(o.Parent))
                    Visit(o.Parent, depth + 1);
                if (seen.Add(o))
                    _order.Add(o);
            }
            foreach (var o in added)
                Visit(o, 0);
            inst.Applied = true;
        }
    }
}
