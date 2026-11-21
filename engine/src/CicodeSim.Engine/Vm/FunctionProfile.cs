using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using CicodeSim.Compiled;
using CicodeSim.Values;

namespace CicodeSim.Vm
{
    /// <summary>Cicode time per function.</summary>
    public sealed class FunctionProfile
    {
        private sealed class Cell
        {
            public long Calls,
                Total,
                Self,
                Max;
            public bool Builtin;
            public string File = "";
            public readonly Dictionary<string, long> Callers = new Dictionary<string, long>(
                StringComparer.OrdinalIgnoreCase
            );
        }

        private sealed class Open
        {
            public Function Fn = null!;
            public long At,
                Child;
            public Node Node = null!;
            public bool Folded; // (past MaxDepth: counted in its parent)
        }

        /// <summary>One call path of the call tree: a function called from the
        /// path above it, its calls and times there.</summary>
        private sealed class Node
        {
            public readonly string Name;
            public readonly bool Builtin;
            public readonly int Depth;
            public long Calls,
                Total,
                Self;
            public Dictionary<string, Node>? Children;

            public Node(string name, bool builtin, int depth)
            {
                Name = name;
                Builtin = builtin;
                Depth = depth;
            }

            public Node Child(string name, bool builtin)
            {
                Children ??= new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
                if (!Children.TryGetValue(name, out var n))
                    Children[name] = n = new Node(name, builtin, Depth + 1);
                return n;
            }
        }

        /// <summary>The call tree's deepest path (a deeper recursion is counted in the node
        /// at this depth).</summary>
        public const int MaxDepth = 64;

        private sealed class Run
        {
            public long Busy,
                SliceAt;
            public bool InSlice;
            public readonly List<Open> Stack = new List<Open>();
        }

        private readonly Dictionary<string, Cell> _cells = new Dictionary<string, Cell>(
            StringComparer.OrdinalIgnoreCase
        );
        private readonly ConditionalWeakTable<CicodeTask, Run> _runs =
            new ConditionalWeakTable<CicodeTask, Run>();

        /// <summary>The call tree's roots: who runs the code (Origin).</summary>
        private readonly Dictionary<string, Node> _roots = new Dictionary<string, Node>(
            StringComparer.OrdinalIgnoreCase
        );

        private Node RootOf(CicodeTask t)
        {
            string o = Origin(t);
            if (!_roots.TryGetValue(o, out var n))
                _roots[o] = n = new Node(o, false, 0);
            return n;
        }

        /// <summary>The node a call of <paramref name="name"/> from the task's current path
        /// is counted in.</summary>
        private Node NodeFor(CicodeTask t, Run? r, string name, bool builtin, out bool folded)
        {
            Node parent = r != null && r.Stack.Count > 0 ? r.Stack[r.Stack.Count - 1].Node : RootOf(t);
            folded = parent.Depth >= MaxDepth;
            return folded ? parent : parent.Child(name, builtin);
        }

        public void Reset()
        {
            _cells.Clear();
            _roots.Clear();
        }

        private static long Now() => Stopwatch.GetTimestamp();

        private static long BusyNow(Run r) => r.Busy + (r.InSlice ? Now() - r.SliceAt : 0);

        /// <summary>A slice of the task starts: its open calls are the task's frames (a call
        /// a halt left open is dropped).</summary>
        internal void SliceStart(CicodeTask t)
        {
            var r = _runs.GetOrCreateValue(t);
            int open = t.Frames.Count;
            if (r.Stack.Count > open)
                r.Stack.RemoveRange(open, r.Stack.Count - open);
            r.SliceAt = Now();
            r.InSlice = true;
        }

        internal void SliceEnd(CicodeTask t)
        {
            if (_runs.TryGetValue(t, out var r) && r.InSlice)
            {
                r.Busy += Now() - r.SliceAt;
                r.InSlice = false;
            }
        }

        internal void Enter(CicodeTask t, Function fn)
        {
            var r = _runs.GetOrCreateValue(t);
            if (!r.InSlice)
            {
                r.SliceAt = Now();
                r.InSlice = true;
            }
            var node = NodeFor(t, r, fn.Name, false, out bool folded);
            r.Stack.Add(
                new Open
                {
                    Fn = fn,
                    At = BusyNow(r),
                    Node = node,
                    Folded = folded,
                }
            );
        }

        internal void Leave(CicodeTask t)
        {
            if (!_runs.TryGetValue(t, out var r) || r.Stack.Count == 0)
                return;
            var o = r.Stack[r.Stack.Count - 1];
            r.Stack.RemoveAt(r.Stack.Count - 1);
            long total = BusyNow(r) - o.At;
            string caller;
            bool again = false;
            if (r.Stack.Count > 0)
            {
                var up = r.Stack[r.Stack.Count - 1];
                up.Child += total;
                caller = up.Fn.Name;
                foreach (var x in r.Stack)
                    if (ReferenceEquals(x.Fn, o.Fn))
                    {
                        again = true;
                        break;
                    }
            }
            else
                caller = Origin(t);
            var c = CellOf(o.Fn.Name, false, o.Fn.Filename);
            c.Calls++;
            if (!again)
                c.Total += total; // (a recursive call is in its outer call's total)
            c.Self += total - o.Child;
            if (total > c.Max)
                c.Max = total;
            c.Callers.TryGetValue(caller, out long n);
            c.Callers[caller] = n + 1;
            var node = o.Node;
            if (o.Folded)
                node.Self += total - o.Child; // (in its parent's total already)
            else
            {
                node.Calls++;
                node.Total += total;
                node.Self += total - o.Child;
            }
        }

        /// <summary>A built-in function's call took <paramref name="ticks"/>: its own time, and
        /// part of the calling Cicode function's total (not its self time).</summary>
        internal void Builtin(CicodeTask t, string name, long ticks)
        {
            var c = CellOf(name, true, "");
            c.Calls++;
            c.Total += ticks;
            c.Self += ticks;
            if (ticks > c.Max)
                c.Max = ticks;
            _runs.TryGetValue(t, out var rr);
            var node = NodeFor(t, rr, name, true, out bool folded);
            if (folded)
                node.Self += ticks;
            else
            {
                node.Calls++;
                node.Total += ticks;
                node.Self += ticks;
            }
            if (_runs.TryGetValue(t, out var r) && r.Stack.Count > 0)
            {
                var up = r.Stack[r.Stack.Count - 1];
                up.Child += ticks;
                string caller = up.Fn.Name;
                c.Callers.TryGetValue(caller, out long n);
                c.Callers[caller] = n + 1;
            }
            else
            {
                string caller = Origin(t);
                c.Callers.TryGetValue(caller, out long n);
                c.Callers[caller] = n + 1;
            }
        }

        /// <summary>Who runs code that is not in a function: page code (an object's
        /// expression, a page event) or a task by its name.</summary>
        private static string Origin(CicodeTask t) =>
            t.Foreground ? "(page code)" : "(task " + (string.IsNullOrEmpty(t.Name) ? "?" : t.Name) + ")";

        private Cell CellOf(string name, bool builtin, string file)
        {
            if (!_cells.TryGetValue(name, out var c))
            {
                _cells[name] = c = new Cell { Builtin = builtin, File = file ?? "" };
            }
            return c;
        }

        /// <summary>A PRIVATE function is stored as 'name@file@project': its name.</summary>
        private static string Short(string name)
        {
            int at = name.IndexOf('@');
            return at > 0 ? name.Substring(0, at) : name;
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        /// <summary>The call tree, each level's costliest first.</summary>
        public JsonValue TreeReport(int maxNodes = 6000, double minShare = 0.0001)
        {
            // a call that has not returned yet (a task's endless loop: while TRUE ... Sleep)
            // has no total of its own: its total is at least its own time and what it called
            var eff = new Dictionary<Node, long>();
            long grand = 0;
            foreach (var root in _roots.Values)
                grand += Effective(root, eff);
            long min = (long)(grand * Math.Max(0.0, minShare));
            int budget = Math.Max(16, maxNodes);
            var a = JsonValue.NewArray();
            foreach (var root in _roots.Values.OrderByDescending(x => eff[x]))
            {
                if (budget <= 0)
                    break;
                a.Add(NodeJson(root, true, min, eff, ref budget));
            }
            return a;
        }

        private static long Effective(Node n, Dictionary<Node, long> eff)
        {
            long sum = n.Self;
            if (n.Children != null)
                foreach (var ch in n.Children.Values)
                    sum += Effective(ch, eff);
            long v = Math.Max(n.Total, sum);
            eff[n] = v;
            return v;
        }

        private static JsonValue NodeJson(
            Node n,
            bool origin,
            long min,
            Dictionary<Node, long> eff,
            ref int budget
        )
        {
            budget--;
            var o = JsonValue.NewObject();
            o.Set("name", Short(n.Name));
            o.Set(
                "kind",
                origin ? "origin"
                    : n.Builtin ? "builtin"
                    : "cicode"
            );
            o.Set("calls", JsonValue.Of(origin ? SumCalls(n) : n.Calls));
            o.Set("ms", JsonValue.Of(Math.Round(Ms(eff[n]), 3)));
            o.Set("selfMs", JsonValue.Of(Math.Round(Ms(n.Self), 3)));
            if (n.Children != null && n.Children.Count > 0)
            {
                var kids = JsonValue.NewArray();
                long otherT = 0,
                    otherC = 0;
                foreach (var ch in n.Children.Values.OrderByDescending(x => eff[x]))
                {
                    long t = eff[ch];
                    if (budget > 0 && t >= min && t > 0)
                        kids.Add(NodeJson(ch, false, min, eff, ref budget));
                    else
                    {
                        otherT += t;
                        otherC += ch.Calls;
                    }
                }
                if (otherT > 0)
                {
                    var x = JsonValue.NewObject();
                    x.Set("name", "(other)");
                    x.Set("kind", "other");
                    x.Set("calls", JsonValue.Of(otherC));
                    x.Set("ms", JsonValue.Of(Math.Round(Ms(otherT), 3)));
                    x.Set("selfMs", JsonValue.Of(Math.Round(Ms(otherT), 3)));
                    kids.Add(x);
                }
                o.Set("children", kids);
            }
            return o;
        }

        private static long SumCalls(Node n)
        {
            long s = 0;
            if (n.Children != null)
                foreach (var ch in n.Children.Values)
                    s += ch.Calls;
            return s;
        }

        /// <summary>[{name, builtin, file, calls, ms (total), selfMs, avgUs (total per call),
        /// maxUs, callers: [{name, calls}] (the most frequent 5)}], the costliest
        /// <paramref name="top"/> by self time; <paramref name="count"/> = how many were timed.</summary>
        public JsonValue Report(int top, out int count)
        {
            count = _cells.Count;
            var a = JsonValue.NewArray();
            foreach (var kv in _cells.OrderByDescending(x => x.Value.Self).Take(Math.Max(1, top)))
            {
                var c = kv.Value;
                var o = JsonValue.NewObject();
                o.Set("name", Short(kv.Key));
                o.Set("builtin", JsonValue.Of(c.Builtin));
                o.Set("file", c.File);
                o.Set("calls", JsonValue.Of(c.Calls));
                o.Set("ms", JsonValue.Of(Math.Round(Ms(c.Total), 3)));
                o.Set("selfMs", JsonValue.Of(Math.Round(Ms(c.Self), 3)));
                o.Set(
                    "avgUs",
                    JsonValue.Of(c.Calls > 0 ? Math.Round(Ms(c.Total) * 1000.0 / c.Calls, 1) : 0.0)
                );
                o.Set("maxUs", JsonValue.Of(Math.Round(Ms(c.Max) * 1000.0, 1)));
                var callers = JsonValue.NewArray();
                foreach (var w in c.Callers.OrderByDescending(x => x.Value).Take(5))
                {
                    var co = JsonValue.NewObject();
                    co.Set("name", Short(w.Key));
                    co.Set("calls", JsonValue.Of(w.Value));
                    callers.Add(co);
                }
                o.Set("callers", callers);
                a.Add(o);
            }
            return a;
        }
    }
}
