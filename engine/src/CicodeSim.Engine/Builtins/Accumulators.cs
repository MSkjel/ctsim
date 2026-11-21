using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;

namespace CicodeSim.Builtins
{
    /// <summary>Accumulators from accums.dbf: AccControl and AccumBrowse*.</summary>
    public static class Accumulators
    {
        private sealed class AccDef
        {
            public string Name = "",
                Cluster = "",
                Running = "",
                Starts = "",
                Totaliser = "",
                Trigger = "",
                Value = "",
                Area = "",
                Priv = "",
                Equipment = "";
        }

        private static List<AccDef> Defs(SimHost h)
        {
            var rows = h.TableRows("<accums>", () =>
            {
                var o = new List<Dictionary<string, string>>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (_, r) in h.World!.Unit.Rows("accums.dbf"))
                {
                    string n = CitectText.Trim(r.Get("NAME"));
                    if (n.Length == 0 || !seen.Add(n))
                        continue;
                    o.Add(
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["NAME"] = n,
                            ["CLUSTER"] = CitectText.Trim(r.Get("CLUSTER")),
                            ["RUNNING"] = CitectText.Trim(r.Get("RUNNING")),
                            ["STARTS"] = CitectText.Trim(r.Get("STARTS")),
                            ["TOTALISER"] = CitectText.Trim(r.Get("INTEGRATE")),
                            ["TRIGGER"] = CitectText.Trim(r.Get("TRIGGER")),
                            ["VALUE"] = CitectText.Trim(r.Get("VALUE")),
                            ["AREA"] = CitectText.Trim(r.Get("AREA")),
                            ["PRIV"] = CitectText.Trim(r.Get("PRIV")),
                            ["EQUIPMENT"] = CitectText.Trim(r.Get("EQUIP")),
                        }
                    );
                }
                return o;
            });
            var defs = new List<AccDef>();
            foreach (var r in rows)
                defs.Add(
                    new AccDef
                    {
                        Name = r["NAME"],
                        Cluster = r["CLUSTER"],
                        Running = r["RUNNING"],
                        Starts = r["STARTS"],
                        Totaliser = r["TOTALISER"],
                        Trigger = r["TRIGGER"],
                        Value = r["VALUE"],
                        Area = r["AREA"],
                        Priv = r["PRIV"],
                        Equipment = r["EQUIPMENT"],
                    }
                );
            return defs;
        }

        private static Regex Mask(string mask) =>
            new Regex(
                "^" + Regex.Escape(mask).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline
            );

        private static int Reset(SimHost h, string tag)
        {
            if (tag.Length == 0)
                return 0;
            return h.World!.Tags.Set(tag, (object)0, null, "cicode");
        }

        [Builtin(
            "AccControl",
            Doc = "1026235",
            Blocking = true,
            Effects = "mode 1 writes 0 to the run time (RUNNING) and totaliser (INTEGRATE) tags, 2 to the starts tag (STARTS), 3 to all three, of every accumulator the mask names",
            Errors = "282 in foreground code (returned); 274 a mode other than 1..5 (returned; guess)",
            Notes = "sName is an accumulator name or a mask (* and ?, without case), may start with 'Cluster.'; sClusterName limits it to that cluster. Modes 4 (flush writes) and 5 (re-read from the device) change nothing: no I/O device behind the tags. No accumulator matches: 0 (guess). The reset waits one turn (the request goes to the report server)."
        )]
        private static Value AccControl(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("AccControl");
            if (t.Foreground)
                return W.I(Codes.FgBlock);
            string name = CitectText.Trim(A.S(a.Get(0)));
            long mode = A.Short(A.I(a.Get(1)));
            string cluster = a.Count > 2 ? CitectText.Trim(A.S(a.Get(2))) : "";
            if (mode < 1 || mode > 5)
                return W.I(Codes.InvalidArg);
            int dot = name.IndexOf('.');
            if (
                cluster.Length == 0
                && dot > 0
                && Servers.Clusters(h).Exists(x => C.IEq(x, name.Substring(0, dot)))
            )
            {
                cluster = name.Substring(0, dot);
                name = name.Substring(dot + 1);
            }
            var rx = Mask(name);
            foreach (var d in Defs(h))
            {
                if (!rx.IsMatch(d.Name))
                    continue;
                if (cluster.Length > 0 && d.Cluster.Length > 0 && !C.IEq(d.Cluster, cluster))
                    continue;
                if (mode == 1 || mode == 3)
                {
                    Reset(h, d.Running);
                    Reset(h, d.Totaliser);
                }
                if (mode == 2 || mode == 3)
                    Reset(h, d.Starts);
            }
            return W.BlockOneTurn(t, "AccControl", () => W.I(0));
        }

        // ------------------------------------------------------------------ browse
        private static readonly string[] Fields =
        {
            "AREA",
            "CLUSTER",
            "EQUIPMENT",
            "NAME",
            "PRIV",
            "RUNNING",
            "STARTS",
            "TOTALISER",
            "TRIGGER",
            "VALUE",
        };

        /// <summary>A tag's value as text ('' when the name is no tag: an expression).</summary>
        private static string TagText(SimHost h, string name)
        {
            if (name.Length == 0)
                return "";
            var (v, _, defined) = h.World!.Tags.Peek(name);
            if (!defined)
                return "";
            switch (v.Kind)
            {
                case JsonKind.String:
                    return v.Str;
                case JsonKind.Int:
                    return v.BigValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case JsonKind.Bool:
                    return v.Bool ? "1" : "0";
                case JsonKind.Null:
                    return "";
                default:
                    return FloatText.Shortest(v.Double);
            }
        }

        private static string? Field(SimHost h, AccDef d, string f)
        {
            switch (f)
            {
                case "NAME":
                    return d.Name;
                case "CLUSTER":
                    return d.Cluster;
                case "AREA":
                    return d.Area;
                case "PRIV":
                    return d.Priv;
                case "EQUIPMENT":
                    return d.Equipment;
                case "RUNNING":
                    return TagText(h, d.Running);
                case "STARTS":
                    return TagText(h, d.Starts);
                case "TOTALISER":
                    return TagText(h, d.Totaliser);
                case "TRIGGER":
                    return TagText(h, d.Trigger);
                case "VALUE":
                    return TagText(h, d.Value);
            }
            return null;
        }

        [Builtin(
            "AccumBrowseOpen",
            Doc = "1026241",
            Blocking = true,
            Errors = "282 in foreground code (raised, -1); 274 a filter field that is not an accumulator field (raised, -1)",
            Notes = "The accumulators of accums.dbf (the unit's, the first definition of a name), sorted by name; the filter as the other browses ('AAA' = 'NAME=AAA'); RUNNING, STARTS, TOTALISER give the value of the tag they name, TRIGGER and VALUE that of a plain tag ('' for an expression: not evaluated)."
        )]
        private static Value AccumBrowseOpen(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("AccumBrowseOpen");
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.I(-1);
            }
            var fl = Browse.Compile(A.S(a.Get(0)), "NAME");
            if (!Browse.FieldsKnown(fl, Fields))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            var cl = Browse.FieldSet(a.Count > 2 ? A.S(a.Get(2)) : "");
            var defs = Defs(h);
            Func<int, string, string?> get = (i, f) => Field(h, defs[i], f);
            var m = new List<int>();
            for (int i = 0; i < defs.Count; i++)
            {
                if (
                    cl != null
                    && defs[i].Cluster.Length > 0
                    && !cl.Contains(defs[i].Cluster.ToUpperInvariant())
                )
                    continue;
                int k = i;
                if (fl.Groups.Count == 0 || Browse.Match(fl, f => get(k, f)))
                    m.Add(i);
            }
            return Browse.Open(
                h,
                t,
                "Accum",
                Browse.Sorted(m, get, Browse.SortSpec("", "NAME")),
                get,
                a.Count > 1 ? A.S(a.Get(1)) : ""
            );
        }

        [Builtin(
            "AccumBrowseFirst",
            Doc = "1026237",
            Blocking = true,
            Errors = "410 bad session; 412 no records; 282 in foreground code (returned)"
        )]
        private static Value AccumBrowseFirst(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "Accum", 0, true);

        [Builtin(
            "AccumBrowseNext",
            Doc = "1026239",
            Blocking = true,
            Errors = "410 bad session; 412 past the last record; 282 in foreground code (returned)"
        )]
        private static Value AccumBrowseNext(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "Accum", 1, true);

        [Builtin(
            "AccumBrowsePrev",
            Doc = "1026242",
            Blocking = true,
            Errors = "410 bad session; 412 before the first record; 282 in foreground code (returned)"
        )]
        private static Value AccumBrowsePrev(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "Accum", -1, true);

        [Builtin(
            "AccumBrowseGetField",
            Doc = "1026238",
            Errors = "410 bad session; 412 no current record; 413 an unknown field (raised, '')"
        )]
        private static Value AccumBrowseGetField(SimHost h, CicodeTask t, ArgList a) =>
            Browse.GetField(h, t, a, "Accum");

        [Builtin("AccumBrowseNumRecords", Doc = "1026240", Errors = "410 bad session (raised, -1)")]
        private static Value AccumBrowseNumRecords(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Count(h, t, a, "Accum");

        [Builtin("AccumBrowseClose", Doc = "1026236", Errors = "410 bad session (returned)")]
        private static Value AccumBrowseClose(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Close(h, t, a, "Accum");
    }
}
