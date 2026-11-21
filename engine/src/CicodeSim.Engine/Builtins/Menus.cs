using System;
using System.Collections.Generic;
using CicodeSim.Compiled;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>The page menu configuration.</summary>
    public static class Menus
    {
        internal sealed class Node
        {
            public int Handle;
            public string Name = "";
            public Node? Parent;
            public readonly List<Node> Children = new List<Node>();
            public readonly Dictionary<string, string> Fields = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase
            );

            /// <summary>The compiled record (code blocks), -1 for none.</summary>
            public long Record = -1;
            public int Row = int.MaxValue;
            public string Page = "";

            /// <summary>A root made by duplicating another (MenuGetPageNode mode 2 / 3).</summary>
            public bool Copy;
        }

        internal sealed class Tree
        {
            public readonly Dictionary<int, Node> ByHandle = new Dictionary<int, Node>();

            /// <summary>Page (lower case; '' = generic) to its root.</summary>
            public readonly Dictionary<string, Node> Roots = new Dictionary<string, Node>(
                StringComparer.Ordinal
            );
            public int Next = 1;
            public RdbFile? File;
            public PageCode? Code;

            /// <summary>MenuGetWindowNode's root of each window: (the page instance it was made
            /// for, its handle).</summary>
            public readonly Dictionary<int, (object Page, int Handle)> WindowRoots =
                new Dictionary<int, (object, int)>();

            public Node NewNode(string name, Node? parent)
            {
                var n = new Node
                {
                    Handle = Next++,
                    Name = name,
                    Parent = parent,
                };
                ByHandle[n.Handle] = n;
                parent?.Children.Add(n);
                return n;
            }
        }

        private static readonly string[] Levels =
        {
            "LEVEL1",
            "LEVEL2",
            "LEVEL3",
            "LEVEL4",
            "LEVEL5",
            "LEVEL6",
        };
        private static readonly string[] FieldNames =
        {
            "SYMBOL",
            "PRIVILEGE",
            "AREA",
            "DISSTYLE",
            "CHECKED",
            "WIDTH",
            "COMMENT",
            "CLUSTER",
            "EQUIP",
            "ORDER",
            "PAGE",
            "TARGETPAGE",
            "CUSTOM1",
            "CUSTOM2",
            "CUSTOM3",
            "CUSTOM4",
            "CUSTOM5",
            "CUSTOM6",
            "CUSTOM7",
            "CUSTOM8",
            "COMMAND",
            "HIDDEN",
            "DISABLED",
        };

        internal static Tree TreeOf(SimHost h)
        {
            var d = ProcessData.Of(h);
            if (d.MenuTree != null)
                return d.MenuTree;
            var tree = new Tree();
            var rows = new List<(long Rec, Dictionary<string, string> F)>();
            var f = d.CompiledTable("_PAGEMEN");
            Section? sec = f != null ? RdbTables.TableOf(f, "_PAGEMEN") : null;
            if (f != null && sec != null && sec.Fields.Exists(x => x.Name == "LEVEL1"))
            {
                tree.File = f;
                foreach (var (off, start) in f.RawRecords(sec.Name))
                {
                    var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var fd in sec.Fields)
                    {
                        if (fd.Type == RdbFile.FExpr || fd.Type == RdbFile.FCode)
                            continue;
                        object? v = f.DecodeField(fd, start, sec.RecSize);
                        r[fd.Name == "EQUIPMENT" ? "EQUIP" : fd.Name] =
                            v is string s ? s
                            : v is long l ? C.Inv(l)
                            : v is double x ? FloatText.Shortest(x)
                            : "";
                    }
                    rows.Add((off / sec.RecSize, r));
                }
            }
            else if (h.World != null)
            {
                foreach (var (_, row) in h.World.Unit.Rows("pagemenu.dbf"))
                {
                    var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string fn in row.Header.Names)
                        r[fn] = CitectText.Trim(row.Get(fn));
                    rows.Add((-1, r));
                }
            }
            for (int i = 0; i < rows.Count; i++)
            {
                var (rec, r) = rows[i];
                string page = CitectText.Trim(r.TryGetValue("PAGE", out string? p) ? p : "");
                string key = CitectText.Lower(page);
                if (!tree.Roots.TryGetValue(key, out Node? cur))
                {
                    tree.Roots[key] = cur = tree.NewNode(page, null);
                    cur.Page = page;
                }
                bool any = false;
                foreach (string lv in Levels)
                {
                    string name = CitectText.Trim(r.TryGetValue(lv, out string? v) ? v : "");
                    if (name.Length == 0)
                        break;
                    any = true;
                    Node? next = cur.Children.Find(c => C.IEq(c.Name, name));
                    if (next == null)
                    {
                        next = tree.NewNode(name, cur);
                        next.Page = page;
                    }
                    cur = next;
                }
                if (!any)
                    continue;
                foreach (var kv in r)
                    cur.Fields[kv.Key] = kv.Value;
                cur.Record = rec;
                cur.Row = Math.Min(cur.Row, i);
            }
            foreach (var root in tree.Roots.Values)
                Sort(root);
            d.MenuTree = tree;
            return tree;
        }

        private static long OrderOf(Node n)
        {
            string o = n.Fields.TryGetValue("ORDER", out string? s) ? CitectText.Trim(s) : "";
            return o.Length == 0 || !CitectText.IsDigits(o.TrimStart('-')) ? long.MaxValue : C.Atol(o);
        }

        private static void Sort(Node n)
        {
            var keyed = new List<(long Order, int Row, int I, Node N)>();
            for (int i = 0; i < n.Children.Count; i++)
                keyed.Add((OrderOf(n.Children[i]), n.Children[i].Row, i, n.Children[i]));
            keyed.Sort(
                (x, y) =>
                {
                    int c = x.Order.CompareTo(y.Order);
                    if (c != 0)
                        return c;
                    c = x.Row.CompareTo(y.Row);
                    return c != 0 ? c : x.I.CompareTo(y.I);
                }
            );
            n.Children.Clear();
            foreach (var k in keyed)
            {
                n.Children.Add(k.N);
                Sort(k.N);
            }
        }

        private static Node? Get(SimHost h, in Value v)
        {
            long hd = A.I(v);
            return hd > 0 && hd <= int.MaxValue && TreeOf(h).ByHandle.TryGetValue((int)hd, out Node? n)
                ? n
                : null;
        }

        private static Node Duplicate(Tree tree, Node src, Node? parent)
        {
            var n = tree.NewNode(src.Name, parent);
            n.Page = src.Page;
            n.Record = src.Record;
            n.Row = src.Row;
            n.Copy = true;
            foreach (var kv in src.Fields)
                n.Fields[kv.Key] = kv.Value;
            foreach (var c in src.Children)
                Duplicate(tree, c, n);
            return n;
        }

        [Builtin(
            "MenuGetPageNode",
            Doc = "1188527",
            Notes = "Mode 0 the page's menu root or -1, 1 the root made when missing, 2 / 3 a new copy of the tree (-1 when none; the copy's page-close disposal is not simulated)."
        )]
        private static Value MenuGetPageNode(SimHost h, CicodeTask t, ArgList a)
        {
            var tree = TreeOf(h);
            string page = CitectText.Trim(A.S(a.Get(0)));
            long mode = a.Count > 1 ? A.Short(A.I(a.Get(1))) : 0;
            tree.Roots.TryGetValue(CitectText.Lower(page), out Node? root);
            if (mode == 1)
            {
                if (root == null)
                {
                    tree.Roots[CitectText.Lower(page)] = root = tree.NewNode(page, null);
                    root.Page = page;
                }
                return W.I(root.Handle);
            }
            if (root == null)
                return W.I(-1);
            if (mode == 2 || mode == 3)
                return W.I(Duplicate(tree, root, null).Handle);
            return W.I(root.Handle);
        }

        [Builtin(
            "MenuGetGenericNode",
            Doc = "1188524",
            Notes = "The root of the rows without a PAGE; bCreate 1 makes it when missing; -1 when none."
        )]
        private static Value MenuGetGenericNode(SimHost h, CicodeTask t, ArgList a)
        {
            var tree = TreeOf(h);
            bool create = a.Count > 0 && A.Short(A.I(a.Get(0))) != 0;
            if (!tree.Roots.TryGetValue("", out Node? root))
            {
                if (!create)
                    return W.I(-1);
                tree.Roots[""] = root = tree.NewNode("", null);
            }
            return W.I(root.Handle);
        }

        [Builtin(
            "MenuGetWindowNode",
            Doc = "1188530",
            Notes = "A root made for the page instance the window shows (once per instance): the generic menu's items, the page's own merged in (a same-named item at the same level takes the page's fields when it has a row and merges its children; the merge rule is a guess). A page with no menu rows gets an empty root (guess). -1 for a negative or closed window or a window without a page."
        )]
        private static Value MenuGetWindowNode(SimHost h, CicodeTask t, ArgList a)
        {
            long n = A.Short(A.I(a.Get(0)));
            if (n < 0)
                return W.I(-1);
            var w = DisplayBuiltins.Of(h).OpenWindow((int)n);
            if (w?.Page == null)
                return W.I(-1);
            var tree = TreeOf(h);
            if (
                tree.WindowRoots.TryGetValue((int)n, out var made)
                && ReferenceEquals(made.Page, w.Page)
                && tree.ByHandle.ContainsKey(made.Handle)
            )
                return W.I(made.Handle);
            tree.Roots.TryGetValue("", out Node? generic);
            tree.Roots.TryGetValue(CitectText.Lower(CitectText.Trim(w.Page.Name)), out Node? own);
            var root = tree.NewNode(w.Page.Name, null);
            root.Page = w.Page.Name;
            root.Copy = true;
            if (generic != null)
                foreach (var c in generic.Children)
                    Duplicate(tree, c, root);
            if (own != null)
                foreach (var c in own.Children)
                    Merge(tree, c, root);
            Sort(root);
            tree.WindowRoots[(int)n] = (w.Page, root.Handle);
            return W.I(root.Handle);
        }

        /// <summary>A page item merged into a window root: a new name is copied, a same-named
        /// one takes the page row's fields (when it has a row) and merges the children.</summary>
        private static void Merge(Tree tree, Node src, Node dst)
        {
            var have = dst.Children.Find(c => C.IEq(c.Name, src.Name));
            if (have == null)
            {
                Duplicate(tree, src, dst);
                return;
            }
            if (src.Record >= 0 || src.Fields.Count > 0)
            {
                foreach (var kv in src.Fields)
                    have.Fields[kv.Key] = kv.Value;
                have.Record = src.Record;
                have.Page = src.Page;
                have.Row = Math.Min(have.Row, src.Row);
            }
            foreach (var c in src.Children)
                Merge(tree, c, have);
        }

        [Builtin("MenuGetFirstChild", Doc = "1188523", Notes = "-1 when none or for a bad handle.")]
        private static Value MenuGetFirstChild(SimHost h, CicodeTask t, ArgList a)
        {
            var n = Get(h, a.Get(0));
            return W.I(n != null && n.Children.Count > 0 ? n.Children[0].Handle : -1);
        }

        private static Value Sibling(SimHost h, ArgList a, int step)
        {
            var n = Get(h, a.Get(0));
            if (n?.Parent == null)
                return W.I(-1);
            int i = n.Parent.Children.IndexOf(n) + step;
            return W.I(i >= 0 && i < n.Parent.Children.Count ? n.Parent.Children[i].Handle : -1);
        }

        [Builtin(
            "MenuGetNextChild",
            Doc = "1188525",
            Notes = "The next sibling; -1 at the end or for a bad handle."
        )]
        private static Value MenuGetNextChild(SimHost h, CicodeTask t, ArgList a) => Sibling(h, a, 1);

        [Builtin(
            "MenuGetPrevChild",
            Doc = "1188529",
            Notes = "The previous sibling; -1 before the first or for a bad handle."
        )]
        private static Value MenuGetPrevChild(SimHost h, CicodeTask t, ArgList a) => Sibling(h, a, -1);

        [Builtin("MenuGetParent", Doc = "1188528", Notes = "-1 for a root or a bad handle.")]
        private static Value MenuGetParent(SimHost h, CicodeTask t, ArgList a)
        {
            var n = Get(h, a.Get(0));
            return W.I(n?.Parent != null ? n.Parent.Handle : -1);
        }

        [Builtin(
            "MenuGetChild",
            Doc = "1188522",
            Notes = "The child of that name (without case; guess); -1 when none or for a bad handle."
        )]
        private static Value MenuGetChild(SimHost h, CicodeTask t, ArgList a)
        {
            var n = Get(h, a.Get(0));
            string name = CitectText.Trim(A.S(a.Get(1)));
            var c = n?.Children.Find(x => C.IEq(x.Name, name));
            return W.I(c != null ? c.Handle : -1);
        }

        [Builtin(
            "MenuGetNodeByPath",
            Doc = "1188526",
            Notes = "The node the '.'-separated path names below hNode (each level by name, without case); -1 when a level is missing or for a bad handle."
        )]
        private static Value MenuGetNodeByPath(SimHost h, CicodeTask t, ArgList a)
        {
            var n = Get(h, a.Get(0));
            if (n == null)
                return W.I(-1);
            foreach (string part in A.S(a.Get(1)).Split('.'))
            {
                string p = CitectText.Trim(part);
                if (p.Length == 0)
                    continue;
                n = n.Children.Find(x => C.IEq(x.Name, p));
                if (n == null)
                    return W.I(-1);
            }
            return W.I(n.Handle);
        }

        private static string Field(Node n, string f) => n.Fields.TryGetValue(f, out string? v) ? v : "";

        private static string? PropName(long field)
        {
            switch (field)
            {
                case 1:
                    return "SYMBOL";
                case 2:
                    return "PRIVILEGE";
                case 3:
                    return "AREA";
                case 4:
                    return "DISSTYLE";
                case 5:
                    return "CHECKED";
                case 6:
                    return "WIDTH";
                case 7:
                    return "COMMENT";
                case 8:
                    return "CLUSTER";
                case 9:
                    return "EQUIP";
                case 10:
                    return "<EXPANDED>";
                case 11:
                    return "ORDER";
                case 12:
                    return "PAGE";
                case 13:
                    return "TARGETPAGE";
            }
            if (field >= 101 && field <= 108)
                return "CUSTOM" + C.Inv(field - 100);
            return null;
        }

        [Builtin(
            "MenuNodeGetProperty",
            Doc = "1188534",
            Notes = "0 the name, 1 SYMBOL, 2 PRIVILEGE, 3 AREA, 4 DISSTYLE, 5 CHECKED, 6 WIDTH, 7 COMMENT, 8 CLUSTER, 9 EQUIP, 10 the expansion state, 11 ORDER, 12 PAGE, 13 TARGETPAGE, 101..108 CUSTOM1..8 (the compiled record's values: numbers in decimal); '' for another field or a bad handle."
        )]
        private static Value MenuNodeGetProperty(SimHost h, CicodeTask t, ArgList a)
        {
            var n = Get(h, a.Get(0));
            long field = A.Short(A.I(a.Get(1)));
            if (n == null)
                return W.Empty;
            if (field == 0)
                return W.S(C.Cut(n.Name));
            if (field == 12)
                return W.S(C.Cut(n.Page));
            string? p = PropName(field);
            return W.S(p == null ? "" : C.Cut(Field(n, p)));
        }

        [Builtin(
            "MenuNodeSetProperty",
            Doc = "1188545",
            Effects = "sets the node's field in the process's tree (never the database)",
            Notes = "Fields 0..10, 13, 101..108 (doc); 0, or -1 for a bad handle or another field."
        )]
        private static Value MenuNodeSetProperty(SimHost h, CicodeTask t, ArgList a)
        {
            var n = Get(h, a.Get(0));
            long field = A.Short(A.I(a.Get(1)));
            string v = A.S(a.Get(2));
            if (n == null || field == 11 || field == 12)
                return W.I(-1);
            if (field == 0)
            {
                n.Name = v;
                return W.I(0);
            }
            string? p = PropName(field);
            if (p == null)
                return W.I(-1);
            n.Fields[p] = v;
            return W.I(0);
        }

        [Builtin(
            "MenuNodeGetExpanded",
            Doc = "1188538",
            Errors = "the runtime raises for a bad handle (code not read): here -1 without a raise",
            Notes = "The expansion state (property 10) as a number; 0 when unset (a node is collapsed until MenuNodeSetExpanded: live, ExampleSA DefaultAlarm_HD1080's equipment tree shows Cluster1 expanded, its Treeview.ci sets that root expanded, and Company / Plant collapsed, which nothing set), -1 for a bad handle."
        )]
        private static Value MenuNodeGetExpanded(SimHost h, CicodeTask t, ArgList a)
        {
            var n = Get(h, a.Get(0));
            if (n == null)
                return W.I(-1);
            string s = CitectText.Trim(Field(n, "<EXPANDED>"));
            return W.I(s.Length == 0 ? 0 : C.Atol(s));
        }

        [Builtin(
            "MenuNodeSetExpanded",
            Doc = "1188543",
            Effects = "sets the expansion state (property 10)",
            Notes = "0, -1 for a bad handle."
        )]
        private static Value MenuNodeSetExpanded(SimHost h, CicodeTask t, ArgList a)
        {
            var n = Get(h, a.Get(0));
            if (n == null)
                return W.I(-1);
            n.Fields["<EXPANDED>"] = C.Inv(A.I(a.Get(1)));
            return W.I(0);
        }

        [Builtin(
            "MenuNodeGetDepth",
            Doc = "1188533",
            Notes = "The depth of the node's PARENT (called on node+4): 0 for a child of the root (a menu's first level, help: 'on the root node'), 1 below it ...; the root itself 0xffff (65535: the ushort -1 zero-extended); -1 for a bad handle. The live SA equipment tree indents Cluster1 (a child of the page node) by 0; Navigation_SplitMenu's [0] is the 'Navigation' item."
        )]
        private static Value MenuNodeGetDepth(SimHost h, CicodeTask t, ArgList a)
        {
            var n = Get(h, a.Get(0));
            if (n == null)
                return W.I(-1);
            if (n.Parent == null)
                return W.I(0xffff);
            int d = 0;
            for (var p = n.Parent.Parent; p != null; p = p.Parent)
                d++;
            return W.I(d);
        }

        [Builtin(
            "MenuNodeGetTargetPage",
            Doc = "1188535",
            Notes = "TARGETPAGE of the node; without hNode (the compiled default -2) or any negative handle the node whose command is running (MenuNodeRunCommand); '' for none."
        )]
        private static Value MenuNodeGetTargetPage(SimHost h, CicodeTask t, ArgList a)
        {
            Node? n = a.Count > 0 ? Get(h, a.Get(0)) : null;
            // FUNC0.DBF OPTARGS '(-2)': a call without the handle passes -2
            if (
                n == null
                && (a.Count == 0 || A.I(a.Get(0)) < 0)
                && t.Data.TryGetValue("menunode", out object? o)
                && o is int hd
            )
                TreeOf(h).ByHandle.TryGetValue(hd, out n);
            return W.S(n != null ? C.Cut(Field(n, "TARGETPAGE")) : "");
        }

        /// <summary>The node's code field (COMMAND, HIDDEN, DISABLED), null when it has none.</summary>
        private static PageBlock? Code(SimHost h, Node n, string field)
        {
            var tree = TreeOf(h);
            if (tree.File == null || n.Record < 0)
                return null;
            tree.Code ??= Rdb.PageFromRdb(tree.File);
            foreach (var b in tree.Code.Blocks)
                if (b.Record == n.Record && string.Equals(b.Field, field, StringComparison.OrdinalIgnoreCase))
                    return b;
            return null;
        }

        /// <summary>HIDDEN / DISABLED: 1 when its code gives a non-zero value (no code: 0).</summary>
        private static long Evaluate(SimHost h, CicodeTask t, Node n, string field)
        {
            long? when = RestTail.WhenValue(h, t, n, field); // MenuNodeSetHiddenWhen / SetDisabledWhen
            if (when != null)
                return when.Value;
            var b = Code(h, n, field);
            if (b == null)
                return 0;
            Value? r = RdbTables.RunBlock(h, t, b.Code, TreeOf(h).Code!.Program, "PAGEMEN." + field);
            if (r == null)
                return 0;
            Value v = Value.Deref(r.Value);
            return Types.IsRealType(v.T) ? (v.D != 0 ? 1 : 0)
                : v.T == Types.String ? (C.Atol(v.S ?? "") != 0 ? 1 : 0)
                : (v.I != 0 ? 1 : 0);
        }

        [Builtin(
            "MenuNodeHasCommand",
            Doc = "1188536",
            Notes = "1 when the node's compiled COMMAND has code, else 0 (also for a bad handle)."
        )]
        private static Value MenuNodeHasCommand(SimHost h, CicodeTask t, ArgList a)
        {
            var n = Get(h, a.Get(0));
            return W.I(
                n != null && (Code(h, n, "COMMAND") != null || Field(n, "<CMDNAME>").Length > 0) ? 1 : 0
            );
        }

        [Builtin(
            "MenuNodeIsHidden",
            Doc = "1188539",
            Notes = "Runs the node's HIDDEN code nested in the caller: 1 / 0 (no code or a bad handle: 0)."
        )]
        private static Value MenuNodeIsHidden(SimHost h, CicodeTask t, ArgList a)
        {
            var n = Get(h, a.Get(0));
            return W.I(n == null ? 0 : Evaluate(h, t, n, "HIDDEN"));
        }

        [Builtin(
            "MenuNodeIsDisabled",
            Doc = "1188537",
            Notes = "Runs the node's DISABLED code nested in the caller: 1 / 0 (no code or a bad handle: 0)."
        )]
        private static Value MenuNodeIsDisabled(SimHost h, CicodeTask t, ArgList a)
        {
            var n = Get(h, a.Get(0));
            return W.I(n == null ? 0 : Evaluate(h, t, n, "DISABLED"));
        }

        [Builtin(
            "__Tabmenu_GetMenuConfigDisabled",
            Errors = "274 a bad handle (raised, 0)",
            Notes = "Tab_Style's helper: the node's DISABLED configuration evaluated, 1 / 0 (guess from the name; undocumented)."
        )]
        private static Value TabmenuGetMenuConfigDisabled(SimHost h, CicodeTask t, ArgList a)
        {
            var n = Get(h, a.Get(0));
            if (n == null)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(0);
            }
            return W.I(Evaluate(h, t, n, "DISABLED"));
        }

        [Builtin(
            "MenuNodeAddChild",
            Doc = "1188531",
            Effects = "adds a node to the process's tree (never the database)",
            Notes = "A child named sName with the command sCommandName(sCommandArgs) (run as call text), SYMBOL sSymbol and ORDER iOrder, placed among its siblings by ORDER (the given order, then the end); the new handle, -1 for a bad parent."
        )]
        private static Value MenuNodeAddChild(SimHost h, CicodeTask t, ArgList a)
        {
            var p = Get(h, a.Get(0));
            if (p == null)
                return W.I(-1);
            var tree = TreeOf(h);
            var n = tree.NewNode(A.S(a.Get(1)), null);
            n.Page = p.Page;
            n.Parent = p;
            n.Fields["<CMDNAME>"] = CitectText.Trim(A.S(a.Get(2)));
            n.Fields["<CMDARGS>"] = a.Count > 3 ? A.S(a.Get(3)) : "";
            n.Fields["SYMBOL"] = a.Count > 4 ? A.S(a.Get(4)) : "";
            long order = a.Count > 5 ? A.Short(A.I(a.Get(5))) : 0;
            n.Fields["ORDER"] = a.Count > 5 ? C.Inv(order) : "";
            int at = p.Children.Count;
            if (a.Count > 5)
                for (int i = 0; i < p.Children.Count; i++)
                    if (OrderOf(p.Children[i]) > order)
                    {
                        at = i;
                        break;
                    }
            p.Children.Insert(at, n);
            return W.I(n.Handle);
        }

        [Builtin(
            "MenuNodeRemove",
            Doc = "1188540",
            Effects = "removes the node and its children from the process's tree",
            Notes = "0, -1 for a bad handle; removing a root removes the page's menu."
        )]
        private static Value MenuNodeRemove(SimHost h, CicodeTask t, ArgList a)
        {
            var n = Get(h, a.Get(0));
            if (n == null)
                return W.I(-1);
            var tree = TreeOf(h);
            void Drop(Node x)
            {
                tree.ByHandle.Remove(x.Handle);
                foreach (var c in x.Children)
                    Drop(c);
            }
            Drop(n);
            if (n.Parent != null)
                n.Parent.Children.Remove(n);
            else
                foreach (var kv in new List<KeyValuePair<string, Node>>(tree.Roots))
                    if (kv.Value == n)
                        tree.Roots.Remove(kv.Key);
            return W.I(0);
        }

        /// <summary>A menu command's task runs in its caller's window with the caller's AN (as a
        /// TaskNew task runs in its creator's window: KeyGetCursor, DspGetAnExtent of the
        /// header popups), and in its cluster context.</summary>
        private static void Inherit(CicodeTask from, CicodeTask to)
        {
            foreach (
                string k in new[]
                {
                    CicodeSim.World.Display.TaskContext.WindowKey,
                    CicodeSim.World.Display.TaskContext.AnKey,
                    "cluster",
                }
            )
                if (from.Data.TryGetValue(k, out object? v))
                    to.Data[k] = v;
        }

        [Builtin(
            "MenuNodeRunCommand",
            Doc = "1188541",
            Effects = "runs the node's compiled COMMAND as a new task",
            Errors = "269 a bad handle (returned)",
            Notes = "The command runs as a task of the scheduler (nested in the caller when there is none); MenuNodeGetTargetPage() without a handle sees the node. 0 (also when the node has no command)."
        )]
        private static Value MenuNodeRunCommand(SimHost h, CicodeTask t, ArgList a)
        {
            var n = Get(h, a.Get(0));
            if (n == null)
                return W.I(Codes.BadHandle);
            string cmd = Field(n, "<CMDNAME>");
            if (cmd.Length > 0)
            {
                // a node MenuNodeAddChild made: its function with the argument text
                Function? fn = CallText.Find(t, cmd);
                if (fn == null)
                    return W.I(0);
                var args = CallText.Bind(fn, Field(n, "<CMDARGS>"), out _);
                var sch = t.Vm.Scheduler;
                if (sch != null)
                {
                    var x = sch.Spawn(fn, args);
                    x.Data["menunode"] = n.Handle;
                    Inherit(t, x);
                }
                else
                {
                    t.Data["menunode"] = n.Handle;
                    W.RunNested(h, t, fn, args, 1000);
                }
                return W.I(0);
            }
            var b = Code(h, n, "COMMAND");
            if (b == null)
                return W.I(0);
            var prog = TreeOf(h).Code!.Program;
            var s = t.Vm.Scheduler;
            if (s != null)
            {
                var x = t.Vm.TaskForBlock(b.Code, prog, false, "PAGEMEN.COMMAND");
                x.Data["menunode"] = n.Handle;
                Inherit(t, x);
                s.Add(x);
                return W.I(0);
            }
            t.Data["menunode"] = n.Handle;
            RdbTables.RunBlock(h, t, b.Code, prog, "PAGEMEN.COMMAND");
            return W.I(0);
        }
    }
}
