using System;
using System.Collections.Generic;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;
using CicodeSim.World.Display;

namespace CicodeSim.Builtins
{
    /// <summary>The older alarm functions, over AlarmDb and the alarm summary.</summary>
    public static class AlarmLegacy
    {
        private static AlarmDb Db(SimHost h) => h.World!.Alarms;

        private static List<AlarmDef> All(SimHost h) => AlarmSummary.Records(h);

        private static AlarmDef? Rec(SimHost h, long rec)
        {
            var all = All(h);
            return rec >= 0 && rec < all.Count ? all[(int)rec] : null;
        }

        private static Value Blocking(CicodeTask t, string reason, Value fg, Func<Value> result)
        {
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return fg;
            }
            return W.BlockOneTurn(t, reason, () => result());
        }

        /// <summary>The end time / comment check of the disable functions (the IL before the call).</summary>
        private static bool DisableArgsBad(SimHost h, long end, string comment) =>
            (end != 0 && end <= (long)Math.Floor(h.World!.Clock.Unix())) || comment.Length > 200;

        private static double? Until(SimHost h, long end) =>
            end != 0 ? end - h.World!.Clock.StartUnix : (double?)null;

        // ------------------------------------------------------------------ by record
        private const string RecNotes =
            "The record is the alarm's definition ordinal (AlarmFirstTagRec / AlarmQueryFirstRec give it); no such record 274 (guess on the code). Waits one turn.";

        [Builtin(
            "AlarmAckRec",
            Doc = "1033446",
            Blocking = true,
            Effects = "acknowledges the alarm",
            Errors = "274 no such record; 282 in foreground code (raised, returned)",
            Notes = RecNotes
        )]
        private static Value AlarmAckRec(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("AlarmAckRec");
            var x = Rec(h, A.I(a.Get(0)));
            return Blocking(t, "AlarmAckRec", W.I(Codes.FgBlock), () =>
                W.I(x == null ? Codes.InvalidArg : Db(h).Acknowledge(x.Tag, "cicode"))
            );
        }

        [Builtin(
            "AlarmClearRec",
            Doc = "1033451",
            Blocking = true,
            Effects = "clears (acknowledges) the alarm",
            Errors = "274 no such record; 282 in foreground code (raised, returned)",
            Notes = RecNotes + " As AlarmClearTag: acknowledged."
        )]
        private static Value AlarmClearRec(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("AlarmClearRec");
            var x = Rec(h, A.I(a.Get(0)));
            return Blocking(t, "AlarmClearRec", W.I(Codes.FgBlock), () =>
                W.I(x == null ? Codes.InvalidArg : Db(h).Acknowledge(x.Tag, "cicode"))
            );
        }

        [Builtin(
            "AlarmDisableRec",
            Doc = "1033460",
            Blocking = true,
            Effects = "disables the alarm until EndTime (0 = for good)",
            Errors = "274 an EndTime not after now or a comment of 201+ characters (raised and returned), no such record (returned); 282 in foreground code (raised, returned)",
            Notes = RecNotes
        )]
        private static Value AlarmDisableRec(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("AlarmDisableRec");
            long end = a.Count > 2 ? A.I(a.Get(2)) : 0;
            string comment = a.Count > 3 ? A.S(a.Get(3)) : "";
            if (DisableArgsBad(h, end, comment))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            var x = Rec(h, A.I(a.Get(0)));
            return Blocking(t, "AlarmDisableRec", W.I(Codes.FgBlock), () =>
                W.I(x == null ? Codes.InvalidArg : Db(h).Disable(x.Tag, Until(h, end), comment, "cicode"))
            );
        }

        [Builtin(
            "AlarmEnableRec",
            Doc = "1033470",
            Blocking = true,
            Effects = "enables the alarm (bAcknowledge 1: and acknowledges it)",
            Errors = "274 no such record; 282 in foreground code (raised, returned)",
            Notes = RecNotes
        )]
        private static Value AlarmEnableRec(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("AlarmEnableRec");
            var x = Rec(h, A.I(a.Get(0)));
            bool ack = a.Count > 2 && A.Short(A.I(a.Get(2))) == 1;
            return Blocking(t, "AlarmEnableRec", W.I(Codes.FgBlock), () =>
            {
                if (x == null)
                    return W.I(Codes.InvalidArg);
                int r = Db(h).Enable(x.Tag, "cicode");
                if (r == 0 && ack)
                    Db(h).Acknowledge(x.Tag, "cicode");
                return W.I(r);
            });
        }

        /// <summary>The thresholds by type (doc 1033500: 0 HH, 1 H, 2 L, 3 LL, 4 deadband, 5 deviation, 6 rate).</summary>
        private static readonly string[] Thresholds =
        {
            "highhigh",
            "high",
            "low",
            "lowlow",
            "deadband",
            "deviation",
            "rate",
        };

        /// <summary>The delays by type (doc 1033497: 0 delay, 1 HH, 2 H, 3 L, 4 LL, 5 deviation).</summary>
        private static readonly string[] Delays =
        {
            "delay",
            "hhdelay",
            "hdelay",
            "ldelay",
            "lldelay",
            "devdelay",
        };

        /// <summary>Set a threshold or a delay; a blank value removes it (a NaN limit / a zero delay: never reached).</summary>
        private static int SetLimit(SimHost h, AlarmDef x, string prop, string value, bool delay)
        {
            string v = CitectText.Trim(value);
            Value nv;
            if (v.Length == 0)
                nv = delay ? Value.Long(0) : Value.Real(double.NaN);
            else if (delay)
                nv = Value.Long(Crt.Atol(v));
            else if (!FloatText.TryParseNumber(v, out double d))
                return Codes.InvalidArg;
            else
                nv = Value.Real(d);
            return Db(h).SetProperty(x, prop, TagDb.WVal.Of(nv), "cicode");
        }

        [Builtin(
            "AlarmSetThresholdRec",
            Doc = "1033500",
            Blocking = true,
            Effects = "changes the alarm's threshold (in the World only, never the project)",
            Errors = "274 a type outside 0..6, a value that is no number or no such record (returned); 282 in foreground code (raised, returned)",
            Notes = RecNotes
                + " Types 0 HighHigh, 1 High, 2 Low, 3 LowLow, 4 Deadband, 5 Deviation, 6 Rate; a blank value removes the threshold (a NaN limit, never reached)."
        )]
        private static Value AlarmSetThresholdRec(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("AlarmSetThresholdRec");
            long type = A.UShort(A.I(a.Get(1)));
            if (type > 6)
                return W.I(Codes.InvalidArg);
            var x = Rec(h, A.I(a.Get(0)));
            string value = A.S(a.Get(2));
            return Blocking(t, "AlarmSetThresholdRec", W.I(Codes.FgBlock), () =>
                W.I(x == null ? Codes.InvalidArg : SetLimit(h, x, Thresholds[type], value, false))
            );
        }

        [Builtin(
            "AlarmSetDelayRec",
            Doc = "1033497",
            Blocking = true,
            Effects = "changes the alarm's delay (in the World only)",
            Errors = "274 a type outside 0..5, a negative value or no such record (returned); 282 in foreground code (raised, returned)",
            Notes = RecNotes
                + " Types 0 Delay, 1 HH, 2 H, 3 L, 4 LL, 5 deviation delay; the value is seconds (atol; a blank value removes the delay)."
        )]
        private static Value AlarmSetDelayRec(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("AlarmSetDelayRec");
            long type = A.UShort(A.I(a.Get(1)));
            string value = A.S(a.Get(2));
            if (type > 5 || Crt.Atol(value) < 0)
                return W.I(Codes.InvalidArg);
            var x = Rec(h, A.I(a.Get(0)));
            return Blocking(t, "AlarmSetDelayRec", W.I(Codes.FgBlock), () =>
                W.I(x == null ? Codes.InvalidArg : SetLimit(h, x, Delays[type], value, true))
            );
        }

        [Builtin(
            "AlarmGetThresholdRec",
            Doc = "1033483",
            Blocking = true,
            Errors = "274 a type outside 0..6 or no such record (raised, 0.0); 282 in foreground code (raised)",
            Notes = RecNotes + " The threshold's value (AlarmDb's property)."
        )]
        private static Value AlarmGetThresholdRec(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("AlarmGetThresholdRec");
            long type = A.UShort(A.I(a.Get(1)));
            var x = Rec(h, A.I(a.Get(0)));
            return Blocking(t, "AlarmGetThresholdRec", W.R(0), () =>
            {
                if (x == null || type > 6)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.R(0);
                }
                return W.R(A.R(Db(h).PropertyValue(x, Thresholds[type])));
            });
        }

        [Builtin(
            "AlarmGetDelayRec",
            Doc = "1033477",
            Blocking = true,
            Errors = "274 a type outside 0..5 or no such record (raised, -1); 282 in foreground code (raised, -1)",
            Notes = RecNotes + " The delay in seconds."
        )]
        private static Value AlarmGetDelayRec(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("AlarmGetDelayRec");
            long type = A.UShort(A.I(a.Get(1)));
            var x = Rec(h, A.I(a.Get(0)));
            return Blocking(t, "AlarmGetDelayRec", W.I(-1), () =>
            {
                if (x == null || type > 5)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.I(-1);
                }
                return W.I(A.I(Db(h).PropertyValue(x, Delays[type])));
            });
        }

        // ------------------------------------------------------------------ at the cursor / on lists
        private static (Window? w, PageAlarmList? l, AlarmLists.Row? r) AtCursor(
            SimHost h,
            CicodeTask t,
            long an
        )
        {
            var d = DisplayBuiltins.Of(h);
            var w = d.OpenWindow(d.WindowOf(t));
            if (w?.Page == null)
                return (w, null, null);
            int at = an < 1 ? w.CommandCursor : (int)an;
            var l = PageObjectTables.Of(d).AlarmList(w.Number, w.Page.Name, at, false);
            if (l == null)
                return (w, null, null);
            if (l.Digest.Length == 0)
                AlarmLists.Refresh(h, l);
            return (w, l, AlarmLists.RowAt(l, at));
        }

        private static long Grp(SimHost h, long set, long v)
        {
            if (set == 0)
                return 1;
            if ((set & UsersGroups.HandleBit) != 0)
            {
                var g = ProcessData.Of(h).GroupTable.Get(set & 0xBFFF);
                if (g != null)
                    return v >= 0 && v < g.Has.Length && g.Has[v] ? 1 : 0;
            }
            return set == v ? 1 : 0;
        }

        /// <summary>(Mode, Value, op): the alarms a mode names (AlarmAck's modes: 0 the row
        /// at an AN, 0 = the cursor; 1 the rows of a list's AN, 0 the cursor's list, -1 every
        /// AlarmDsp list of the page; 2 a category, 3 a priority, 0 = all, a group handle), null = 274.</summary>
        private static List<AlarmDef>? Named(SimHost h, CicodeTask t, long mode, long value)
        {
            var db = Db(h);
            var o = new List<AlarmDef>();
            switch (mode)
            {
                case 0:
                {
                    var (_, _, r) = AtCursor(h, t, value);
                    if (r?.Def == null)
                        return null;
                    o.Add(r.Def);
                    return o;
                }
                case 1:
                {
                    var ls = AlarmLists.ListsAt(h, t, value, false);
                    if (ls.Count == 0)
                        return null;
                    foreach (var l in ls)
                    {
                        if (value == -1 && l.Last)
                            continue;
                        if (l.Digest.Length == 0)
                            AlarmLists.Refresh(h, l);
                        foreach (var x in l.RowItems)
                            if (x is AlarmLists.Row r && r.Def != null && !o.Contains(r.Def))
                                o.Add(r.Def);
                    }
                    return o;
                }
                case 2:
                case 3:
                    foreach (var x in All(h))
                        if ((mode == 2 ? Grp(h, value, x.Category) : Grp(h, value, db.Priority(x))) != 0)
                            o.Add(x);
                    return o;
            }
            return null;
        }

        private static Value ByMode(SimHost h, CicodeTask t, ArgList a, string name, Func<AlarmDef, int> act)
        {
            if (h.World == null)
                return Tags.NoWorld(name);
            long mode = A.Short(A.I(a.Get(0))),
                value = A.I(a.Get(1));
            Value Do()
            {
                var xs = Named(h, t, mode, value);
                if (xs == null)
                    return W.I(Codes.InvalidArg);
                int r = 0;
                foreach (var x in xs)
                {
                    int c = act(x);
                    if (c != 0 && r == 0)
                        r = c;
                }
                return W.I(mode >= 2 ? 0 : r);
            }
            if (t.Foreground)
                return Do();
            return W.BlockOneTurn(t, name, () => Do());
        }

        private const string ModeNotes =
            "Mode 0: Value = the AN of an alarm row (0 = the keyboard cursor); 1: a list's AN (0 = the cursor's list, -1 = every AlarmDsp list of the page); 2: a category (0 = all, a group handle); 3: a priority (0 = all, a group). In background code the answer waits one turn. The cluster argument is not used (one simulated cluster).";

        [Builtin(
            "AlarmDisable",
            Doc = "1033459",
            Effects = "disables the named alarms until EndTime (0 = for good)",
            Errors = "274 an EndTime not after now or a comment of 201+ characters (raised and returned), a bad mode or no row / list there (returned)",
            Notes = ModeNotes
        )]
        private static Value AlarmDisable(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("AlarmDisable");
            long end = a.Count > 3 ? A.I(a.Get(3)) : 0;
            string comment = a.Count > 4 ? A.S(a.Get(4)) : "";
            if (DisableArgsBad(h, end, comment))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            return ByMode(h, t, a, "AlarmDisable", x =>
                Db(h).Disable(x.Tag, Until(h, end), comment, "cicode")
            );
        }

        [Builtin(
            "AlarmEnable",
            Doc = "1033469",
            Effects = "enables the named alarms (nAcknowledgeMode 1: and acknowledges them)",
            Errors = "274 a bad mode or no row / list there (returned)",
            Notes = ModeNotes
        )]
        private static Value AlarmEnable(SimHost h, CicodeTask t, ArgList a)
        {
            bool ack = a.Count > 3 && A.Short(A.I(a.Get(3))) == 1;
            return ByMode(h, t, a, "AlarmEnable", x =>
            {
                int r = Db(h).Enable(x.Tag, "cicode");
                if (r == 0 && ack)
                    Db(h).Acknowledge(x.Tag, "cicode");
                return r;
            });
        }

        [Builtin(
            "AlarmClear",
            Doc = "1033450",
            Effects = "clears (acknowledges) the named alarms",
            Errors = "274 a bad mode or no row / list there (returned)",
            Notes = ModeNotes + " As AlarmClearTag: an acknowledged OFF alarm leaves the list."
        )]
        private static Value AlarmClear(SimHost h, CicodeTask t, ArgList a) =>
            ByMode(h, t, a, "AlarmClear", x => Db(h).Acknowledge(x.Tag, "cicode"));

        // ------------------------------------------------------------------ the cursor's alarm
        private static Value AtCursorAlarm(
            SimHost h,
            CicodeTask t,
            string name,
            Func<AlarmDef, Value> act,
            Value none
        )
        {
            if (h.World == null)
                return Tags.NoWorld(name);
            var (_, _, r) = AtCursor(h, t, 0);
            if (r?.Def == null)
            {
                W.Raise(t, Codes.InvalidArg);
                return none;
            }
            return act(r.Def);
        }

        private const string CursorNotes =
            "The alarm of the row at the keyboard cursor of the task's window (no alarm there: 274 raised).";

        [Builtin(
            "AlarmSetThreshold",
            Doc = "1033499",
            Effects = "changes the threshold of the alarm at the cursor (in the World only)",
            Errors = "274 no alarm at the cursor (raised), a type outside 0..6 or a value that is no number (returned)",
            Notes = CursorNotes + " Types as AlarmSetThresholdRec; a blank value removes the threshold."
        )]
        private static Value AlarmSetThreshold(SimHost h, CicodeTask t, ArgList a)
        {
            long type = A.UShort(A.I(a.Get(0)));
            string value = A.S(a.Get(1));
            if (type > 6)
                return W.I(Codes.InvalidArg);
            return AtCursorAlarm(
                h,
                t,
                "AlarmSetThreshold",
                x => W.I(SetLimit(h, x, Thresholds[type], value, false)),
                W.I(Codes.InvalidArg)
            );
        }

        [Builtin(
            "AlarmSetDelay",
            Doc = "1033496",
            Effects = "changes the delay of the alarm at the cursor (in the World only)",
            Errors = "274 no alarm at the cursor (raised), a type outside 0..5 or a negative value (returned)",
            Notes = CursorNotes + " Types as AlarmSetDelayRec."
        )]
        private static Value AlarmSetDelay(SimHost h, CicodeTask t, ArgList a)
        {
            long type = A.UShort(A.I(a.Get(0)));
            string value = A.S(a.Get(1));
            if (type > 5 || Crt.Atol(value) < 0)
                return W.I(Codes.InvalidArg);
            return AtCursorAlarm(
                h,
                t,
                "AlarmSetDelay",
                x => W.I(SetLimit(h, x, Delays[type], value, true)),
                W.I(Codes.InvalidArg)
            );
        }

        [Builtin(
            "AlarmGetThreshold",
            Doc = "1033482",
            Errors = "274 no alarm at the cursor or a type outside 0..6 (raised, 0.0)",
            Notes = CursorNotes
        )]
        private static Value AlarmGetThreshold(SimHost h, CicodeTask t, ArgList a)
        {
            long type = A.UShort(A.I(a.Get(0)));
            return AtCursorAlarm(
                h,
                t,
                "AlarmGetThreshold",
                x =>
                {
                    if (type > 6)
                    {
                        W.Raise(t, Codes.InvalidArg);
                        return W.R(0);
                    }
                    return W.R(A.R(Db(h).PropertyValue(x, Thresholds[type])));
                },
                W.R(0)
            );
        }

        [Builtin(
            "AlarmGetDelay",
            Doc = "1033476",
            Errors = "274 no alarm at the cursor or a type outside 0..5 (raised, -1)",
            Notes = CursorNotes + " The delay in seconds."
        )]
        private static Value AlarmGetDelay(SimHost h, CicodeTask t, ArgList a)
        {
            long type = A.UShort(A.I(a.Get(0)));
            return AtCursorAlarm(
                h,
                t,
                "AlarmGetDelay",
                x =>
                {
                    if (type > 5)
                    {
                        W.Raise(t, Codes.InvalidArg);
                        return W.I(-1);
                    }
                    return W.I(A.I(Db(h).PropertyValue(x, Delays[type])));
                },
                W.I(-1)
            );
        }

        [Builtin(
            "AlarmHelp",
            Doc = "1033484",
            Effects = "shows the help page of the alarm at the keyboard cursor in the task's window",
            Errors = "274 no window (raised and returned)",
            Notes = "The alarm row at the cursor (an alarm list there) with a HELP page: PageDisplay of it in the task's window; no row or no help page: 0, nothing shown."
        )]
        private static Value AlarmHelp(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("AlarmHelp");
            var (w, _, r) = AtCursor(h, t, 0);
            if (w == null)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            if (r?.Def == null || CitectText.Trim(r.Def.Help).Length == 0)
                return W.I(0);
            DisplayPages.Show(h, t, CitectText.Trim(r.Def.Help), w.Number, true);
            return W.I(0);
        }

        [Builtin(
            "AlarmCountList",
            Doc = "1033457",
            Errors = "274 no window or no alarm list at the AN (raised, -1)",
            Notes = "The number of alarms of the list at the AN (all its records, not only the rows shown)."
        )]
        private static Value AlarmCountList(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("AlarmCountList");
            long an = A.Short(A.I(a.Get(0)));
            var d = DisplayBuiltins.Of(h);
            var w = d.OpenWindow(d.WindowOf(t));
            var l =
                an >= 1 && w?.Page != null
                    ? PageObjectTables.Of(d).AlarmList(w.Number, w.Page.Name, (int)an, false)
                    : null;
            if (l == null)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            return W.I(AlarmLists.Items(h, l).Count);
        }

        // ------------------------------------------------------------------ summary entries
        [Builtin(
            "AlarmDelete",
            Doc = "1033458",
            Effects = "removes the named summary entries from the World's alarm summary",
            Errors = "317 a mode other than 0..3 (returned); 274 no summary row / list there (returned)",
            Notes = "Mode 0: the summary row at the AN (0 = the cursor); 1: the summary lists at the AN (0 = the cursor's, -1 = every list of the page); 2: the entries of a category, 3: of a priority (0 = all, a group handle). Only entries of summary lists (type 10) in modes 0 / 1. In background code the answer waits one turn."
        )]
        private static Value AlarmDelete(SimHost h, CicodeTask t, ArgList a)
        {
            var w = h.World;
            if (w == null)
                return Tags.NoWorld("AlarmDelete");
            long mode = A.Short(A.I(a.Get(0))),
                value = A.I(a.Get(1));
            if (mode < 0 || mode > 3)
                return W.I(317);
            Value Do()
            {
                var log = AlarmEventLog.Attach(w);
                var gone = new List<AlarmSummaryEntry>();
                if (mode == 0)
                {
                    var (_, l, r) = AtCursor(h, t, value);
                    if (l == null || l.Type != 10 || r?.Sum == null)
                        return W.I(Codes.InvalidArg);
                    gone.Add(r.Sum);
                }
                else if (mode == 1)
                {
                    var ls = AlarmLists.ListsAt(h, t, value, false);
                    if (ls.Count == 0)
                        return W.I(Codes.InvalidArg);
                    foreach (var l in ls)
                    {
                        if (l.Type != 10)
                            continue;
                        if (l.Digest.Length == 0)
                            AlarmLists.Refresh(h, l);
                        foreach (var x in l.RowItems)
                            if (x is AlarmLists.Row r && r.Sum != null)
                                gone.Add(r.Sum);
                    }
                }
                else
                    foreach (var e in log.Summary)
                        if (
                            (
                                mode == 2
                                    ? Grp(h, value, e.Def.Category)
                                    : Grp(h, value, w.Alarms.Priority(e.Def))
                            ) != 0
                        )
                            gone.Add(e);
                foreach (var e in gone)
                    log.Summary.Remove(e);
                return W.I(0);
            }
            if (t.Foreground)
                return Do();
            return W.BlockOneTurn(t, "AlarmDelete", () => Do());
        }

        /// <summary>A new summary entry of an alarm (AlarmSumAppend / Split, AlarmSplit).</summary>
        private static AlarmSummaryEntry NewEntry(World.World w, AlarmDef def, double onT)
        {
            var log = AlarmEventLog.Attach(w);
            var e = new AlarmSummaryEntry
            {
                Seq = log.NextSeq(),
                Def = def,
                OnT = onT,
                By = "cicode",
            };
            log.Summary.Add(e);
            if (log.Summary.Count > AlarmEventLog.Max)
                log.Summary.RemoveRange(0, log.Summary.Count - AlarmEventLog.Max);
            return e;
        }

        [Builtin(
            "AlarmSplit",
            Doc = "1033501",
            Blocking = true,
            Effects = "the summary entry at the AN gets the OFF time now and a new entry with its ON time and no OFF time follows it",
            Errors = "274 no window (raised and returned); 282 in foreground code (returned)",
            Notes = "AN 1 .. the page's last AN, else the keyboard cursor; only a row of a summary list (type 10). No such row, or an entry that has an OFF time: 0, nothing changes (doc)."
        )]
        private static Value AlarmSplit(SimHost h, CicodeTask t, ArgList a)
        {
            var w = h.World;
            if (w == null)
                return Tags.NoWorld("AlarmSplit");
            var (win, l, r) = AtCursor(h, t, a.Count > 0 ? A.Short(A.I(a.Get(0))) : 0);
            if (win == null)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            if (l == null || l.Type != 10 || r?.Sum == null || r.Sum.OffT >= 0)
                return W.I(0);
            if (t.Foreground)
                return W.I(Codes.FgBlock);
            var e = r.Sum;
            e.OffT = w.Clock.Now();
            NewEntry(w, e.Def, e.OnT);
            return W.BlockOneTurn(t, "AlarmSplit", () => W.I(0));
        }

        [Builtin(
            "AlarmCommentRecID",
            Doc = "1033454",
            Blocking = true,
            Effects = "gives the summary entry (SUMMARYID) or event (RECORDID) a comment",
            Errors = "274 no window (raised and returned), a list type other than 10, 15, 16 (returned); 282 in foreground code (returned)",
            Notes = "sRecID: a summary entry's SUMMARYID (type 10) or an event's RECORDID (15 / 16: the World's event log); no such entry 0, nothing changes. The comment is cut to 128 (summary) / 244 (event) characters with '...' (doc)."
        )]
        private static Value AlarmCommentRecID(SimHost h, CicodeTask t, ArgList a)
        {
            var w = h.World;
            if (w == null)
                return Tags.NoWorld("AlarmCommentRecID");
            var d = DisplayBuiltins.Of(h);
            if (d.OpenWindow(d.WindowOf(t)) == null)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            string text = A.S(a.Get(0));
            long id = Crt.Atol(A.S(a.Get(1)));
            long type = a.Count > 2 ? A.Short(A.I(a.Get(2))) : 10;
            if (type != 10 && type != 15 && type != 16)
                return W.I(Codes.InvalidArg);
            if (t.Foreground)
                return W.I(Codes.FgBlock);
            var log = AlarmEventLog.Attach(w);
            object? entry = null;
            if (type == 10)
                entry = log.Summary.Find(e => e.Seq == id);
            else
                entry = log.Events.Find(e => e.Seq == id);
            if (entry != null)
            {
                int max = type == 10 ? 128 : 244;
                if (text.Length > max)
                    text = text.Substring(0, max - 3) + "...";
                ProcessExtras.Of(h).SetComment(entry, text);
            }
            return W.BlockOneTurn(t, "AlarmCommentRecID", () => W.I(0));
        }

        // ------------------------------------------------------------------ AlarmSum*: the summary by index
        /// <summary>A user event of the summary (AlarmSumAppend('*name')): not tied to an alarm.</summary>
        internal sealed class UserEntry
        {
            public long Seq;
            public string Tag = "";
            public double OnT,
                OffT = -1,
                AckT = -1;
            public string Comment = "";
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
            World.World,
            List<UserEntry>
        > UserEntries = new System.Runtime.CompilerServices.ConditionalWeakTable<
            World.World,
            List<UserEntry>
        >();

        private static List<UserEntry> UserEvents(World.World w) =>
            UserEntries.GetValue(w, _ => new List<UserEntry>());

        private static List<AlarmSummaryEntry> Sum(SimHost h) => AlarmEventLog.Attach(h.World!).Summary;

        private static int IndexOf(List<AlarmSummaryEntry> s, long seq)
        {
            for (int i = 0; i < s.Count; i++)
                if (s[i].Seq == seq)
                    return i;
            return -1;
        }

        private const string SumNotes =
            "The index is the entry's SUMMARYID (its sequence number in the World's alarm summary, from the log's attach on).";

        [Builtin("AlarmSumFirst", Doc = "1033506", Notes = SumNotes + " The oldest entry; -1 when none.")]
        private static Value AlarmSumFirst(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("AlarmSumFirst");
            var s = Sum(h);
            return W.I(s.Count > 0 ? s[0].Seq : -1);
        }

        [Builtin("AlarmSumLast", Doc = "1033508", Notes = SumNotes + " The newest entry; -1 when none.")]
        private static Value AlarmSumLast(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("AlarmSumLast");
            var s = Sum(h);
            return W.I(s.Count > 0 ? s[s.Count - 1].Seq : -1);
        }

        [Builtin(
            "AlarmSumNext",
            Doc = "1033509",
            Notes = SumNotes
                + " The entry after the index; -1 at the end or for an index that is not in the summary."
        )]
        private static Value AlarmSumNext(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("AlarmSumNext");
            var s = Sum(h);
            int i = IndexOf(s, A.I(a.Get(0)));
            return W.I(i >= 0 && i + 1 < s.Count ? s[i + 1].Seq : -1);
        }

        [Builtin(
            "AlarmSumPrev",
            Doc = "1033510",
            Notes = SumNotes
                + " The entry before the index; -1 at the start or for an index that is not in the summary."
        )]
        private static Value AlarmSumPrev(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("AlarmSumPrev");
            var s = Sum(h);
            int i = IndexOf(s, A.I(a.Get(0)));
            return W.I(i > 0 ? s[i - 1].Seq : -1);
        }

        [Builtin(
            "AlarmSumFind",
            Doc = "1033505",
            Notes = SumNotes
                + " The entry of the alarm record (definition ordinal) whose ON time is OnTime (Unix seconds); -1 when none."
        )]
        [Builtin(
            "AlarmSumFindExact",
            Doc = "",
            Notes = SumNotes
                + " As AlarmSumFind; OnMilli must be 0 (the simulated summary holds whole seconds)."
        )]
        private static Value AlarmSumFind(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("AlarmSumFind");
            var x = Rec(h, A.I(a.Get(0)));
            long on = A.I(a.Get(1));
            if (x == null)
                return W.I(-1);
            double start = h.World.Clock.StartUnix;
            foreach (var e in Sum(h))
                if (e.Def == x && (long)Math.Floor(start + e.OnT) == on)
                    return W.I(e.Seq);
            return W.I(-1);
        }

        private static object? Entry(SimHost h, long index)
        {
            var s = Sum(h);
            int i = IndexOf(s, index);
            if (i >= 0)
                return s[i];
            return UserEvents(h.World!).Find(u => u.Seq == index);
        }

        [Builtin(
            "AlarmSumGet",
            Doc = "1033507",
            Notes = SumNotes
                + " A field of the alarm lists' summary rows (Tag, Name, Desc, Category, Help, State, OnDate, OnTime, OffDate, OffTime, AckDate, AckTime, DeltaTime, Comment ...); a user event has its Tag, times and Comment only; no such entry or field ''."
        )]
        private static Value AlarmSumGet(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("AlarmSumGet");
            object? e = Entry(h, A.I(a.Get(0)));
            string f = CitectText.Upper(CitectText.Trim(A.S(a.Get(1))));
            if (e is AlarmSummaryEntry se)
            {
                if (f == "COMMENT")
                {
                    string? c = ProcessExtras.Of(h).CommentOf(se);
                    if (c != null)
                        return W.S(C.Cut(c));
                }
                return W.S(
                    C.Cut(
                        AlarmLists.FieldText(
                            h,
                            new AlarmLists.Row
                            {
                                Def = se.Def,
                                St = Db(h).PeekState(se.Def),
                                Sum = se,
                            },
                            f
                        ) ?? ""
                    )
                );
            }
            if (e is UserEntry u)
            {
                double start = h.World.Clock.StartUnix;
                string T(double sim, int fmt) =>
                    sim < 0 ? "" : Time.TimeToStr(h, null, (long)Math.Floor(start + sim), fmt, false);
                switch (f)
                {
                    case "TAG":
                        return W.S(u.Tag);
                    case "COMMENT":
                        return W.S(u.Comment);
                    case "ONTIME":
                        return W.S(T(u.OnT, 1));
                    case "ONDATE":
                        return W.S(T(u.OnT, 2));
                    case "OFFTIME":
                        return W.S(T(u.OffT, 1));
                    case "OFFDATE":
                        return W.S(T(u.OffT, 2));
                    case "ACKTIME":
                        return W.S(T(u.AckT, 1));
                    case "ACKDATE":
                        return W.S(T(u.AckT, 2));
                }
            }
            return W.Empty;
        }

        [Builtin(
            "AlarmSumSet",
            Doc = "1033511",
            Effects = "sets the entry's AckTime, Comment, OffMilli or OffTime",
            Errors = "274 no such entry or another field (returned; guess on the code)",
            Notes = SumNotes + " AckTime / OffTime are Unix seconds (atol); OffMilli is taken and not kept."
        )]
        private static Value AlarmSumSet(SimHost h, CicodeTask t, ArgList a)
        {
            var w = h.World;
            if (w == null)
                return Tags.NoWorld("AlarmSumSet");
            object? e = Entry(h, A.I(a.Get(0)));
            string f = A.S(a.Get(1)),
                v = A.S(a.Get(2));
            if (e == null)
                return W.I(Codes.InvalidArg);
            double start = w.Clock.StartUnix;
            if (C.IEq(f, "Comment"))
            {
                if (e is UserEntry u)
                    u.Comment = v;
                else
                    ProcessExtras.Of(h).SetComment(e, v);
            }
            else if (C.IEq(f, "AckTime"))
            {
                if (e is AlarmSummaryEntry se)
                    se.AckT = Crt.Atol(v) - start;
                else
                    ((UserEntry)e).AckT = Crt.Atol(v) - start;
            }
            else if (C.IEq(f, "OffTime"))
            {
                if (e is AlarmSummaryEntry se)
                    se.OffT = Crt.Atol(v) - start;
                else
                    ((UserEntry)e).OffT = Crt.Atol(v) - start;
            }
            else if (!C.IEq(f, "OffMilli"))
                return W.I(Codes.InvalidArg);
            return W.I(0);
        }

        [Builtin(
            "AlarmSumAppend",
            Doc = "1033502",
            Effects = "adds a summary entry for the alarm (or a user event, '*name')",
            Notes = SumNotes
                + " OnTime 0 (or none) = now. An alarm tag (or Equipment.Item) adds an entry of that alarm; '*name' a user event (kept apart: AlarmSumGet / Set / Delete / Type reach it, AlarmSumNext / Prev do not, as the doc says, and the alarm lists do not show it); an unknown tag -1."
        )]
        private static Value AlarmSumAppend(SimHost h, CicodeTask t, ArgList a)
        {
            var w = h.World;
            if (w == null)
                return Tags.NoWorld("AlarmSumAppend");
            string tag = CitectText.Trim(A.S(a.Get(0)));
            long on = a.Count > 2 ? A.I(a.Get(2)) : 0;
            double onT = on == 0 ? w.Clock.Now() : on - w.Clock.StartUnix;
            if (tag.StartsWith("*", StringComparison.Ordinal))
            {
                var u = new UserEntry
                {
                    Seq = AlarmEventLog.Attach(w).NextSeq(),
                    Tag = tag,
                    OnT = onT,
                };
                var list = UserEvents(w);
                list.Add(u);
                if (list.Count > AlarmEventLog.Max)
                    list.RemoveRange(0, list.Count - AlarmEventLog.Max);
                return W.I(u.Seq);
            }
            AlarmDef? x = null;
            foreach (var d in All(h))
                if (C.IEq(d.Tag, tag) || (d.Equip.Length > 0 && C.IEq(d.Equip + "." + d.Item, tag)))
                {
                    x = d;
                    break;
                }
            if (x == null)
                return W.I(-1);
            return W.I(NewEntry(w, x, onT).Seq);
        }

        [Builtin(
            "AlarmSumSplit",
            Doc = "1033512",
            Effects = "the entry gets OnTime as its OFF time; a new entry of the alarm with that ON time follows",
            Notes = SumNotes
                + " An entry that has an OFF time: -1, nothing changes (doc). OnTime 0 = now. Returns the new entry's index."
        )]
        private static Value AlarmSumSplit(SimHost h, CicodeTask t, ArgList a)
        {
            var w = h.World;
            if (w == null)
                return Tags.NoWorld("AlarmSumSplit");
            if (!(Entry(h, A.I(a.Get(0))) is AlarmSummaryEntry e) || e.OffT >= 0)
                return W.I(-1);
            long on = a.Count > 2 ? A.I(a.Get(2)) : 0;
            double onT = on == 0 ? w.Clock.Now() : on - w.Clock.StartUnix;
            e.OffT = onT;
            return W.I(NewEntry(w, e.Def, onT).Seq);
        }

        [Builtin(
            "AlarmSumDelete",
            Doc = "1033504",
            Effects = "removes the entry from the World's alarm summary",
            Errors = "274 no such entry (returned; guess on the code)",
            Notes = SumNotes
        )]
        private static Value AlarmSumDelete(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("AlarmSumDelete");
            object? e = Entry(h, A.I(a.Get(0)));
            if (e is AlarmSummaryEntry se)
                Sum(h).Remove(se);
            else if (e is UserEntry u)
                UserEvents(h.World).Remove(u);
            else
                return W.I(Codes.InvalidArg);
            return W.I(0);
        }

        [Builtin(
            "AlarmSumCommit",
            Doc = "1033503",
            Errors = "274 no such entry, or an entry committed before (returned; guess on the code)",
            Notes = SumNotes
                + " No summary device is written in the simulator: the entry is marked committed (a second commit fails, doc)."
        )]
        private static Value AlarmSumCommit(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("AlarmSumCommit");
            object? e = Entry(h, A.I(a.Get(0)));
            if (e == null)
                return W.I(Codes.InvalidArg);
            var done = Committed.GetValue(h.World, _ => new HashSet<object>());
            return W.I(done.Add(e) ? 0 : Codes.InvalidArg);
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
            World.World,
            HashSet<object>
        > Committed = new System.Runtime.CompilerServices.ConditionalWeakTable<
            World.World,
            HashSet<object>
        >();

        [Builtin(
            "AlarmSumType",
            Doc = "1033513",
            Notes = SumNotes
                + " The alarm type number (0 digital, 1 analog, 2 advanced, 3 multi-digital, 4 Argyle analog, 5 user event, 6 high resolution, 8 / 9 time-stamped digital / analog: AlarmDb.SumTypes); -1 for no such entry."
        )]
        private static Value AlarmSumType(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("AlarmSumType");
            object? e = Entry(h, A.I(a.Get(0)));
            if (e is UserEntry)
                return W.I(5);
            if (e is AlarmSummaryEntry se)
                return W.I(AlarmDb.SumTypes.TryGetValue(se.Def.Kind, out int k) ? k : -1);
            return W.I(-1);
        }

        // ------------------------------------------------------------------ not supported
        private const string NotSupported =
            "The table entry is the runtime's 'not supported' function: 0, nothing else happens.";

        [Builtin("AlarmSaveType", Doc = "", Notes = NotSupported)]
        [Builtin("AlarmSetPriority", Doc = "", Notes = NotSupported)]
        [Builtin("AlarmSetPriorityRec", Doc = "", Notes = NotSupported)]
        [Builtin("ClusterSetName", Doc = "1070596", Notes = NotSupported + " Deprecated (doc).")]
        [Builtin("ClusterGetName", Doc = "1070592", Notes = NotSupported + " Deprecated (doc).")]
        private static Value NotSupportedEntry(SimHost h, CicodeTask t, ArgList a) => W.I(0);

        // ------------------------------------------------------------------ colours
        [Builtin(
            "CitectColourToPackedRGB",
            Doc = "1073023",
            Notes = "Swaps the first and the third byte and drops the fourth: 0x00BBGGRR <-> 0x00RRGGBB (the two functions share the code)."
        )]
        [Builtin(
            "PackedRGBToCitectColour",
            Doc = "1073029",
            Notes = "Swaps the first and the third byte and drops the fourth (the code of CitectColourToPackedRGB)."
        )]
        private static Value ColourSwap(SimHost h, CicodeTask t, ArgList a)
        {
            long v = A.I(a.Get(0));
            long r = ((v & 0xFF) << 16) | (v & 0xFF00) | ((v >> 16) & 0xFF);
            return W.I(r);
        }
    }
}
