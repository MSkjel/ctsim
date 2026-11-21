using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;
using CicodeSim.World.Display;

namespace CicodeSim.Builtins
{
    /// <summary>The alarm summary browse and alarm record queries.</summary>
    public static class AlarmSummary
    {
        private const string Kind = "AlmSummary";
        private const int BadSession = Codes.BrowseSessionNotFound,
            Eof = Codes.BrowseEof,
            BadField = Codes.BrowseFieldNotFound,
            InvalidField = 414,
            RecordInvalid = 428;

        /// <summary>A summary browse session's own state: the entries it found (oldest first)
        /// and the values AlmSummarySetFieldValue keeps for the current entry until the commit.</summary>
        private sealed class SumSession
        {
            public readonly List<AlarmSummaryEntry> Entries = new List<AlarmSummaryEntry>();
            public readonly Dictionary<string, string> Pending = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase
            );
            public int PendingAt = -1;
        }

        private static readonly ConditionalWeakTable<BrowseSession, SumSession> Sums =
            new ConditionalWeakTable<BrowseSession, SumSession>();

        private static (BrowseSession s, SumSession x)? Find(SimHost h, in Value v)
        {
            var s = h.Browses.Get(A.I(v));
            if (s == null || s.Kind != Kind || !Sums.TryGetValue(s, out var x))
                return null;
            return (s, x);
        }

        private static AlarmLists.Row RowOf(SimHost h, AlarmSummaryEntry e) =>
            new AlarmLists.Row
            {
                Def = e.Def,
                St = h.World!.Alarms.PeekState(e.Def),
                Sum = e,
            };

        /// <summary>A field of a summary entry as AlmSummaryGetField gives it (the fields of the
        /// alarm lists' summary rows, AlarmLists.FieldText, with the entry's comment); null for a
        /// field it does not know.</summary>
        private static string? Field(SimHost h, AlarmSummaryEntry e, string field)
        {
            string f = CitectText.Upper(CitectText.Trim(field));
            if (f == "COMMENT" || f == "ALMCOMMENT")
            {
                string? c = ProcessExtras.Of(h).CommentOf(e);
                if (c != null)
                    return c;
            }
            return AlarmLists.FieldText(h, RowOf(h, e), f);
        }

        /// <summary>The value a filter compares: the times as Unix seconds (doc 1033538 example:
        /// 'OnTime >= ' + TimestampToTimeInt(...)), the number fields as numbers.</summary>
        private static AlarmFilter.FieldValue FilterValue(SimHost h, AlarmSummaryEntry e, string fld)
        {
            string f = CitectText.Upper(fld);
            double start = h.World!.Clock.StartUnix;
            switch (f)
            {
                case "ONTIME":
                case "ONTIMEDATE":
                    return AlarmFilter.FieldValue.Num((long)Math.Floor(start + e.OnT));
                case "OFFTIME":
                case "OFFTIMEDATE":
                    return e.OffT >= 0
                        ? AlarmFilter.FieldValue.Num((long)Math.Floor(start + e.OffT))
                        : AlarmFilter.FieldValue.Num(0);
                case "ACKTIME":
                    return e.AckT >= 0
                        ? AlarmFilter.FieldValue.Num((long)Math.Floor(start + e.AckT))
                        : AlarmFilter.FieldValue.Num(0);
                case "CATEGORY":
                    return AlarmFilter.FieldValue.Num(e.Def.Category);
                case "PRIORITY":
                    return AlarmFilter.FieldValue.Num(h.World.Alarms.Priority(e.Def));
                case "AREA":
                    return AlarmFilter.FieldValue.Num(e.Def.Area);
                case "PRIV":
                    return AlarmFilter.FieldValue.Num(e.Def.Priv);
            }
            string? v = Field(h, e, f);
            return v == null ? AlarmFilter.FieldValue.None : AlarmFilter.FieldValue.Str(v);
        }

        private static string FilterText(SimHost h, string s)
        {
            string t = CitectText.Trim(s);
            return ProcessData.Of(h).NamedFilters.TryGetValue(t, out string? f) ? f : s;
        }

        // ------------------------------------------------------------------ open / move / fields
        [Builtin(
            "AlmSummaryOpen",
            Doc = "1033538",
            Blocking = true,
            Errors = "282 in foreground code (raised, -1); 271 every browse handle is taken (raised, -1)",
            Notes = "A browse of the alarm summary (World AlarmEventLog: every ON occurrence with its OFF and acknowledge times, from the log's attach on; the page animator attaches it, else this first open) oldest first, the filter in AlarmDb's syntax ('AAA' = 'Tag=AAA'; OnTime / OffTime / AckTime compare as Unix seconds), sClusters a comma list, RowLimit > 0 the oldest that many (-1 the [Alarm]BrowseRowLimit default: no limit here). AutoCloseMode 1 needs a page on the task's window (else -1, no error, as the IL); a mode with other bits -1. The session's records are fixed at the open."
        )]
        private static Value AlmSummaryOpen(SimHost h, CicodeTask t, ArgList a)
        {
            var w = h.World;
            if (w == null)
                return Tags.NoWorld("AlmSummaryOpen");
            long close = a.Count > 4 ? A.Short(A.I(a.Get(4))) : 0;
            if ((close & ~7L) != 0 || (close & ~1L) != 0)
                return W.I(-1);
            if (close == 1)
            {
                var d = DisplayBuiltins.Of(h);
                if (d.OpenWindow(d.WindowOf(t))?.Page == null)
                    return W.I(-1);
            }
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.I(-1);
            }
            var flt = new AlarmFilter(FilterText(h, A.S(a.Get(0))));
            string fields = a.Count > 1 ? A.S(a.Get(1)) : "";
            var cl = Browse.FieldSet(a.Count > 2 ? A.S(a.Get(2)) : "");
            long limit = a.Count > 3 ? A.I(a.Get(3)) : -1;
            var log = AlarmEventLog.Attach(w);
            var x = new SumSession();
            foreach (var e in log.Summary)
            {
                if (cl != null && e.Def.Cluster.Length > 0 && !cl.Contains(e.Def.Cluster.ToUpperInvariant()))
                    continue;
                if (!flt.Empty && !flt.Match(fld => FilterValue(h, e, fld)))
                    continue;
                x.Entries.Add(e);
                if (limit > 0 && x.Entries.Count >= limit)
                    break;
            }
            var order = new int[x.Entries.Count];
            for (int i = 0; i < order.Length; i++)
                order[i] = i;
            if (h.Browses.Count >= SimHost.MaxBrowses)
            {
                W.Raise(t, Codes.NoHandles);
                return W.I(-1);
            }
            var s = new BrowseSession
            {
                Kind = Kind,
                Order = order,
                Get = (i, f) => Field(h, x.Entries[i], f),
                Fields = Browse.FieldSet(fields),
            };
            Sums.Add(s, x);
            int hd = h.Browses.Add(s);
            return W.BlockOneTurn(t, "AlmSummaryOpen", () => W.I(hd));
        }

        /// <summary>a bad session 410, foreground 282, past an end 412.</summary>
        private static Value Move(SimHost h, CicodeTask t, ArgList a, int how)
        {
            var f = Find(h, a.Get(0));
            if (f == null)
                return W.I(BadSession);
            if (t.Foreground)
                return W.I(Codes.FgBlock);
            var (s, x) = f.Value;
            int c =
                how == 0 ? 0
                : how == 2 ? s.Count - 1
                : how > 0 ? s.Cursor + 1
                : s.Cursor < 0 ? s.Count - 1
                : s.Cursor - 1;
            if (c != s.Cursor)
                x.Pending.Clear();
            if (c < 0 || c >= s.Count)
            {
                if (how == 1 || how == -1)
                    s.Cursor = c < 0 ? -1 : s.Count;
                return W.I(Eof);
            }
            s.Cursor = c;
            return W.I(0);
        }

        [Builtin(
            "AlmSummaryFirst",
            Doc = "1033533",
            Blocking = true,
            Errors = "410 bad session; 412 no records; 282 in foreground code (returned)",
            Notes = "The oldest summary entry of the session."
        )]
        private static Value AlmSummaryFirst(SimHost h, CicodeTask t, ArgList a) => Move(h, t, a, 0);

        [Builtin(
            "AlmSummaryNext",
            Doc = "1033536",
            Blocking = true,
            Errors = "410 bad session; 412 past the last record; 282 in foreground code (returned)",
            Notes = "Right after the open: the oldest entry (doc)."
        )]
        private static Value AlmSummaryNext(SimHost h, CicodeTask t, ArgList a) => Move(h, t, a, 1);

        [Builtin(
            "AlmSummaryPrev",
            Doc = "1033539",
            Blocking = true,
            Errors = "410 bad session; 412 before the first record; 282 in foreground code (returned)",
            Notes = "Right after the open: the newest entry (doc)."
        )]
        private static Value AlmSummaryPrev(SimHost h, CicodeTask t, ArgList a) => Move(h, t, a, -1);

        [Builtin(
            "AlmSummaryLast",
            Doc = "1033535",
            Blocking = true,
            Errors = "410 bad session; 412 no records; 282 in foreground code (returned)",
            Notes = "The newest summary entry of the session."
        )]
        private static Value AlmSummaryLast(SimHost h, CicodeTask t, ArgList a) => Move(h, t, a, 2);

        [Builtin(
            "AlmSummaryGetField",
            Doc = "1033534",
            Errors = "410 bad session; 412 no current record; 413 an unknown field or one the session's field list leaves out (raised, '')",
            Notes = "The fields of the alarm lists' summary rows (ONDATE, ONTIME, OFFDATE, OFFTIME, ACKDATE, ACKTIME, DELTATIME, SUMSTATE, SUMMARYID, the alarm's own fields; times as the simulated clock's TimeToStr), COMMENT / ALMCOMMENT the entry's comment (AlarmComment, AlmSummarySetFieldValue) when it has one."
        )]
        private static Value AlmSummaryGetField(SimHost h, CicodeTask t, ArgList a)
        {
            var f = Find(h, a.Get(0));
            string fld = A.S(a.Get(1)).Trim().ToUpperInvariant();
            if (f == null || f.Value.s.Cursor < 0 || f.Value.s.Cursor >= f.Value.s.Count)
            {
                W.Raise(t, f == null ? BadSession : Eof);
                return W.Empty;
            }
            var s = f.Value.s;
            string? v = s.Fields != null && !s.Fields.Contains(fld) ? null : s.Get(s.Order[s.Cursor], fld);
            if (v == null)
            {
                W.Raise(t, BadField);
                return W.Empty;
            }
            return W.S(A.Cap(v));
        }

        [Builtin("AlmSummaryNumRecords", Doc = "1033537", Errors = "410 bad session (raised, -1)")]
        private static Value AlmSummaryNumRecords(SimHost h, CicodeTask t, ArgList a)
        {
            var f = Find(h, a.Get(0));
            if (f == null)
            {
                W.Raise(t, BadSession);
                return W.I(-1);
            }
            return W.I(f.Value.s.Count);
        }

        [Builtin("AlmSummaryClose", Doc = "1033528", Errors = "410 bad session (returned)")]
        private static Value AlmSummaryClose(SimHost h, CicodeTask t, ArgList a)
        {
            var f = Find(h, a.Get(0));
            if (f == null)
                return W.I(BadSession);
            h.Browses.Free(A.I(a.Get(0)));
            Sums.Remove(f.Value.s);
            return W.I(0);
        }

        // ------------------------------------------------------------------ changes
        private static (BrowseSession s, SumSession x, AlarmSummaryEntry e)? Current(
            SimHost h,
            CicodeTask t,
            in Value v,
            out int code
        )
        {
            var f = Find(h, v);
            if (f == null)
            {
                code = BadSession;
                return null;
            }
            if (t.Foreground)
            {
                code = Codes.FgBlock;
                return null;
            }
            var (s, x) = f.Value;
            if (s.Cursor < 0 || s.Cursor >= s.Count)
            {
                code = RecordInvalid;
                return null;
            }
            code = 0;
            return (s, x, x.Entries[s.Order[s.Cursor]]);
        }

        [Builtin(
            "AlmSummarySetFieldValue",
            Doc = "1033540",
            Blocking = true,
            Effects = "keeps the value for the entry at the cursor until AlmSummaryCommit (moving the cursor drops it)",
            Errors = "410 bad session; 282 in foreground code; 428 no current record; 414 a field other than AckTime, Comment, OffMilli, OffTime (all returned)",
            Notes = "The fields the IL accepts (_stricmp): AckTime, Comment, OffMilli, OffTime."
        )]
        private static Value AlmSummarySetFieldValue(SimHost h, CicodeTask t, ArgList a)
        {
            var c = Current(h, t, a.Get(0), out int code);
            if (c == null)
                return W.I(code);
            string field = A.S(a.Get(1));
            if (
                !C.IEq(field, "AckTime")
                && !C.IEq(field, "Comment")
                && !C.IEq(field, "OffMilli")
                && !C.IEq(field, "OffTime")
            )
                return W.I(InvalidField);
            var (s, x, _) = c.Value;
            if (x.PendingAt != s.Cursor)
            {
                x.Pending.Clear();
                x.PendingAt = s.Cursor;
            }
            x.Pending[field] = A.S(a.Get(2));
            return W.I(0);
        }

        [Builtin(
            "AlmSummaryCommit",
            Doc = "1033526",
            Blocking = true,
            Effects = "writes the kept values into the summary entry: Comment its comment, AckTime / OffTime its acknowledge / OFF time",
            Errors = "410 bad session; 282 in foreground code; 428 no current record (returned)",
            Notes = "AckTime and OffTime are Unix seconds (atol of the text; guess: the archive's time format was not read), OffMilli is taken and not kept (the summary holds whole seconds). No kept values: 0."
        )]
        private static Value AlmSummaryCommit(SimHost h, CicodeTask t, ArgList a)
        {
            var c = Current(h, t, a.Get(0), out int code);
            if (c == null)
                return W.I(code);
            var (s, x, e) = c.Value;
            if (x.PendingAt == s.Cursor)
            {
                double start = h.World!.Clock.StartUnix;
                foreach (var kv in x.Pending)
                {
                    if (C.IEq(kv.Key, "Comment"))
                        ProcessExtras.Of(h).SetComment(e, kv.Value);
                    else if (C.IEq(kv.Key, "AckTime"))
                        e.AckT = Crt.Atol(kv.Value) - start;
                    else if (C.IEq(kv.Key, "OffTime"))
                        e.OffT = Crt.Atol(kv.Value) - start;
                }
            }
            x.Pending.Clear();
            x.PendingAt = -1;
            return W.I(0);
        }

        [Builtin(
            "AlmSummaryDelete",
            Doc = "1033529",
            Blocking = true,
            Effects = "removes the entry at the cursor from the World's alarm summary; the cursor is on the next entry",
            Errors = "410 bad session; 282 in foreground code; 428 no current record (returned)"
        )]
        private static Value AlmSummaryDelete(SimHost h, CicodeTask t, ArgList a)
        {
            var c = Current(h, t, a.Get(0), out int code);
            if (c == null)
                return W.I(code);
            var (s, x, e) = c.Value;
            AlarmEventLog.Attach(h.World!).Summary.Remove(e);
            int k = s.Order[s.Cursor];
            x.Entries.RemoveAt(k);
            Reindex(s, x);
            x.Pending.Clear();
            return W.I(0);
        }

        [Builtin(
            "AlmSummaryDeleteAll",
            Doc = "1033530",
            Blocking = true,
            Effects = "removes every entry of the session from the World's alarm summary",
            Errors = "410 bad session; 282 in foreground code (returned)"
        )]
        private static Value AlmSummaryDeleteAll(SimHost h, CicodeTask t, ArgList a)
        {
            var f = Find(h, a.Get(0));
            if (f == null)
                return W.I(BadSession);
            if (t.Foreground)
                return W.I(Codes.FgBlock);
            var (s, x) = f.Value;
            var sum = AlarmEventLog.Attach(h.World!).Summary;
            foreach (var e in x.Entries)
                sum.Remove(e);
            x.Entries.Clear();
            Reindex(s, x);
            s.Cursor = -1;
            x.Pending.Clear();
            return W.I(0);
        }

        private static void Reindex(BrowseSession s, SumSession x)
        {
            var order = new int[x.Entries.Count];
            for (int i = 0; i < order.Length; i++)
                order[i] = i;
            s.Order = order;
        }

        private static Value Act(SimHost h, CicodeTask t, ArgList a, Func<AlarmDb, AlarmDef, int> act)
        {
            var f = Find(h, a.Get(0));
            if (f == null)
                return W.I(BadSession);
            var s = f.Value.s;
            if (s.Cursor < 0 || s.Cursor >= s.Count)
                return W.I(Eof);
            return W.I(act(h.World!.Alarms, f.Value.x.Entries[s.Order[s.Cursor]].Def));
        }

        [Builtin(
            "AlmSummaryAck",
            Doc = "1033525",
            Effects = "acknowledges the alarm of the entry at the cursor",
            Errors = "410 bad session; 412 no current record (returned)"
        )]
        private static Value AlmSummaryAck(SimHost h, CicodeTask t, ArgList a) =>
            Act(h, t, a, (db, x) => db.Acknowledge(x.Tag, "cicode"));

        [Builtin(
            "AlmSummaryClear",
            Doc = "1033527",
            Effects = "clears (acknowledges) the alarm of the entry at the cursor",
            Errors = "410 bad session; 412 no current record (returned)",
            Notes = "As AlmBrowseClear: the alarm is acknowledged."
        )]
        private static Value AlmSummaryClear(SimHost h, CicodeTask t, ArgList a) =>
            Act(h, t, a, (db, x) => db.Acknowledge(x.Tag, "cicode"));

        [Builtin(
            "AlmSummaryEnable",
            Doc = "1033532",
            Effects = "enables the alarm of the entry at the cursor (bAcknowledge 1: and acknowledges it)",
            Errors = "410 bad session; 412 no current record (returned)"
        )]
        private static Value AlmSummaryEnable(SimHost h, CicodeTask t, ArgList a) => Act(h, t, a, (db, x) =>
            {
                int r = db.Enable(x.Tag, "cicode");
                if (r == 0 && a.Count > 1 && A.Short(A.I(a.Get(1))) == 1)
                    db.Acknowledge(x.Tag, "cicode");
                return r;
            });

        [Builtin(
            "AlmSummaryDisable",
            Doc = "1033531",
            Effects = "disables the alarm of the entry at the cursor until EndTime (0 = for good)",
            Errors = "274 an EndTime not after now, or a comment of 201+ characters (raised and returned); 410 bad session; 412 no current record (returned)",
            Notes = "The IL refuses an EndTime that is not 0 and not after TimeCurrent before it looks at the session."
        )]
        private static Value AlmSummaryDisable(SimHost h, CicodeTask t, ArgList a)
        {
            var w = h.World;
            if (w == null)
                return Tags.NoWorld("AlmSummaryDisable");
            long end = a.Count > 1 ? A.I(a.Get(1)) : 0;
            string comment = a.Count > 2 ? A.S(a.Get(2)) : "";
            long now = (long)Math.Floor(w.Clock.Unix());
            if ((end != 0 && end <= now) || comment.Length > 200)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            return Act(h, t, a, (db, x) =>
                db.Disable(x.Tag, end != 0 ? end - w.Clock.StartUnix : (double?)null, comment, "cicode")
            );
        }

        // ------------------------------------------------------------------ record queries
        internal static List<AlarmDef> Records(SimHost h)
        {
            var d = ProcessData.Of(h);
            if (d.AlarmList == null)
            {
                var db = h.World!.Alarms;
                db.Get(""); // indexes the alarms: sets the ordinals
                d.AlarmList = db.All();
            }
            return d.AlarmList;
        }

        private static bool InSet(SimHost h, long set, long v)
        {
            if (set == 0)
                return true;
            if ((set & UsersGroups.HandleBit) != 0)
            {
                var g = ProcessData.Of(h).GroupTable.Get(set & 0xBFFF);
                if (g != null)
                    return v >= 0 && v < g.Has.Length && g.Has[v];
            }
            return set == v;
        }

        /// <summary>The query (rec, category, priority, area, type, cluster): the next
        /// definition record after <paramref name="from"/> - 1 of that type in that group.</summary>
        private static long Query(
            SimHost h,
            long from,
            long group,
            long type,
            long area,
            long byPriority,
            string cluster
        )
        {
            var db = h.World!.Alarms;
            var all = Records(h);
            HashSet<AlarmDef>? logged = null;
            if (type == 10 || type == 15 || type == 16)
            {
                logged = new HashSet<AlarmDef>();
                var log = AlarmEventLog.Attach(h.World);
                if (type == 10)
                    foreach (var e in log.Summary)
                        logged.Add(e.Def);
                else
                    foreach (var e in log.Events)
                        if (type == 16 || (e.Event != "disable" && e.Event != "enable"))
                            logged.Add(e.Def);
            }
            bool hardware = (type >= 5 && type <= 9) || type == 13 || type == 14;
            if (hardware)
                return -1;
            for (long i = Math.Max(0, from); i < all.Count; i++)
            {
                var x = all[(int)i];
                if (area != -1 && x.Area != area)
                    continue;
                if (cluster.Length > 0 && x.Cluster.Length > 0 && !CitectText.SameName(x.Cluster, cluster))
                    continue;
                if (byPriority == 0 ? !InSet(h, group, x.Category) : !InSet(h, group, db.Priority(x)))
                    continue;
                if (logged != null ? !logged.Contains(x) : !AlarmDb.Classify(db.PeekState(x), (int)type))
                    continue;
                return i;
            }
            return -1;
        }

        private static Value Blocking(CicodeTask t, string reason, Func<Value> result)
        {
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.I(-1);
            }
            return W.BlockOneTurn(t, reason, () => result());
        }

        private const string QueryNotes =
            "The record number (the alarm's definition ordinal, as AlarmGetFieldRec reads it) of the first configured alarm from the start (Next: after Record) of type nType (AlarmCount's types over AlarmDb's states; 10 alarms with a summary entry, 15 / 16 with an event; the hardware types none: -1) whose category (QueryType 0) or priority (1) is Group (0 = all, a group handle = its members), in Area (-1 = all), of the cluster when one is named. -1 when none. In background code the answer waits one turn.";

        [Builtin(
            "_AlarmQueryFirstRec",
            Doc = "1033494",
            Blocking = true,
            Errors = "282 in foreground code (raised, -1)",
            Notes = QueryNotes
        )]
        [Builtin(
            "AlarmQueryFirstRec",
            Doc = "1033494",
            Blocking = true,
            Errors = "282 in foreground code (raised, -1)",
            Notes = QueryNotes
        )]
        private static Value AlarmQueryFirstRec(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("AlarmQueryFirstRec");
            long group = A.Short(A.I(a.Get(0))),
                type = a.Count > 1 ? A.Short(A.I(a.Get(1))) : 1,
                area = a.Count > 2 ? A.Short(A.I(a.Get(2))) : -1;
            long q = a.Count > 3 ? A.Short(A.I(a.Get(3))) : 0;
            string cl = a.Count > 4 ? A.S(a.Get(4)) : "";
            return Blocking(t, "AlarmQueryFirstRec", () => W.I(Query(h, 0, group, type, area, q, cl)));
        }

        [Builtin(
            "_AlarmQueryNextRec",
            Doc = "1033495",
            Blocking = true,
            Errors = "282 in foreground code (raised, -1)",
            Notes = QueryNotes
        )]
        [Builtin(
            "AlarmQueryNextRec",
            Doc = "1033495",
            Blocking = true,
            Errors = "282 in foreground code (raised, -1)",
            Notes = QueryNotes
        )]
        private static Value AlarmQueryNextRec(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("AlarmQueryNextRec");
            long rec = A.I(a.Get(0));
            long group = A.Short(A.I(a.Get(1))),
                type = a.Count > 2 ? A.Short(A.I(a.Get(2))) : 1,
                area = a.Count > 3 ? A.Short(A.I(a.Get(3))) : -1;
            long q = a.Count > 4 ? A.Short(A.I(a.Get(4))) : 0;
            string cl = a.Count > 5 ? A.S(a.Get(5)) : "";
            return Blocking(t, "AlarmQueryNextRec", () =>
                W.I(rec < 0 ? -1 : Query(h, rec + 1, group, type, area, q, cl))
            );
        }

        // ------------------------------------------------------------------ journal
        [Builtin(
            "AlarmComment",
            Doc = "1033453",
            Blocking = true,
            Effects = "gives the summary or event entry at the AN a comment (its COMMENT field)",
            Errors = "274 no window (raised and returned), a list that is no summary or event list at the AN (returned); 282 in foreground code (returned)",
            Notes = "AN 1 .. the page's last AN (else the keyboard cursor's AN) names a row of an alarm list of type 10, 15 or 16 on the task's window; no list, no row there or an empty row: 0, nothing changes. The comment is cut to 128 characters on a summary row, 244 on an event row, with '...' at the end (doc)."
        )]
        private static Value AlarmComment(SimHost h, CicodeTask t, ArgList a)
        {
            var d = DisplayBuiltins.Of(h);
            var win = d.OpenWindow(d.WindowOf(t));
            if (win == null)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            string text = A.S(a.Get(0));
            long an = a.Count > 1 ? A.Short(A.I(a.Get(1))) : 0;
            if (win.Page == null)
                return W.I(0);
            if (an < 1)
                an = win.CommandCursor;
            var l = PageObjectTables.Of(d).AlarmList(win.Number, win.Page.Name, (int)an, false);
            if (l == null)
                return W.I(0);
            var r = AlarmLists.RowAt(l, (int)an);
            if (r == null)
                return W.I(0);
            if (l.Type != 10 && l.Type != 15 && l.Type != 16)
                return W.I(Codes.InvalidArg);
            object? entry = (object?)r.Sum ?? r.Ev;
            if (entry == null)
                return W.I(0);
            if (t.Foreground)
                return W.I(Codes.FgBlock);
            int max = r.Sum != null ? 128 : 244;
            if (text.Length > max)
                text = text.Substring(0, max - 3) + "...";
            ProcessExtras.Of(h).SetComment(entry, text);
            return W.BlockOneTurn(t, "AlarmComment", () => W.I(0));
        }

        [Builtin(
            "SOEEventAdd",
            Doc = "1192398",
            Blocking = true,
            Effects = "journals a user event (ProcessExtras.UserEvents; the trace's 'soeEvent')",
            Errors = "282 in foreground code (raised and returned)",
            Notes = "Tag given without Cluster: the cluster before the tag's first '.' when it names one of the unit's clusters, else the first cluster. The event is kept with its time, message, tag and cluster; the simulator's event lists (AlarmEventLog) hold alarm transitions only, so it is not in an SOE list (verify against the event journal). 0."
        )]
        private static Value SOEEventAdd(SimHost h, CicodeTask t, ArgList a)
        {
            var w = h.World;
            if (w == null)
                return Tags.NoWorld("SOEEventAdd");
            ulong ts = A.U64(a.Get(0));
            string msg = A.S(a.Get(1)),
                tag = a.Count > 2 ? A.S(a.Get(2)) : "",
                cl = a.Count > 3 ? A.S(a.Get(3)) : "";
            if (tag.Length > 0 && cl.Length == 0)
            {
                int dot = tag.IndexOf('.');
                string first = dot > 0 ? tag.Substring(0, dot) : "";
                bool isCluster = false;
                foreach (string c in w.Unit.Clusters())
                    if (CitectText.SameName(c, first))
                    {
                        isCluster = true;
                        break;
                    }
                if (isCluster)
                {
                    cl = first;
                    tag = tag.Substring(dot + 1);
                }
                else
                    foreach (string c in w.Unit.Clusters())
                    {
                        cl = c;
                        break;
                    }
            }
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.I(Codes.FgBlock);
            }
            var x = ProcessExtras.Of(h);
            x.UserEvents.Add(
                new ProcessExtras.UserEvent
                {
                    Time = ts,
                    Message = msg,
                    Tag = tag,
                    Cluster = cl,
                }
            );
            if (x.UserEvents.Count > AlarmEventLog.Max)
                x.UserEvents.RemoveRange(0, x.UserEvents.Count - AlarmEventLog.Max);
            w.Trace?.Event(
                "soeEvent",
                null,
                t.Handle >= 0 ? (object)t.Handle : null,
                ("message", msg),
                ("tag", tag),
                ("cluster", cl)
            );
            return W.BlockOneTurn(t, "SOEEventAdd", () => W.I(0));
        }

        [Builtin(
            "AlarmTagFromEquipment",
            Doc = "1033514",
            Notes = "The alarm tag of 'Equipment.Item' (the alarm whose EQUIP and ITEM it names), of an alarm tag itself, or the first alarm (definition order) of an equipment; a name that is none of these is returned as it is (the IL copies the translation, which keeps the text when it finds nothing). A leading 'Cluster.' is taken off. The result type is STRING (the help says INT)."
        )]
        private static Value AlarmTagFromEquipment(SimHost h, CicodeTask t, ArgList a)
        {
            var w = h.World;
            if (w == null)
                return Tags.NoWorld("AlarmTagFromEquipment");
            string tag = CitectText.Trim(A.S(a.Get(0)));
            int dot = tag.IndexOf('.');
            if (dot > 0)
                foreach (string c in w.Unit.Clusters())
                    if (CitectText.SameName(c, tag.Substring(0, dot)))
                    {
                        tag = tag.Substring(dot + 1);
                        break;
                    }
            var all = Records(h);
            foreach (var x in all)
                if (C.IEq(x.Tag, tag))
                    return W.S(C.Cut(x.Tag));
            foreach (var x in all)
                if (x.Equip.Length > 0 && C.IEq(x.Equip + "." + x.Item, tag))
                    return W.S(C.Cut(x.Tag));
            foreach (var x in all)
                if (x.Equip.Length > 0 && C.IEq(x.Equip, tag))
                    return W.S(C.Cut(x.Tag));
            return W.S(C.Cut(tag));
        }

        [Builtin(
            "AlarmFilterEditAppendEquipment",
            Doc = "1069681",
            Effects = "appends the equipment filter to the session's filter text (no check, no separator, as AlarmFilterEditAppend)",
            Errors = "269 a bad session (returned; guess on the code)",
            Notes = "Each comma-separated part: 'XYZ' becomes 'EQUIPMENT=XYZ*', 'EQUIPMENT=XYZ' stays (doc); the parts are joined with ' OR ' in parentheses. Ref 1 adds, for each equipment named exactly, the items it references (eqref.dbf: '(EQUIPMENT=<ref equipment> AND (ITEM=a OR ITEM=b))'). the runtime's own text (the cluster terms of the doc's example) is not reproduced (verify)."
        )]
        private static Value AlarmFilterEditAppendEquipment(SimHost h, CicodeTask t, ArgList a)
        {
            var s = ProcessData.Of(h).FilterSessions.Get(A.Short(A.I(a.Get(0))));
            if (s == null)
                return W.I(Codes.BadHandle);
            string list = A.S(a.Get(1));
            bool refs = a.Count > 2 && A.Short(A.I(a.Get(2))) == 1;
            var parts = new List<string>();
            var named = new List<string>();
            foreach (string raw in list.Split(','))
            {
                string p = CitectText.Trim(raw);
                if (p.Length == 0)
                    continue;
                int eq = p.IndexOf('=');
                if (eq < 0)
                {
                    parts.Add("EQUIPMENT=" + p + "*");
                    continue;
                }
                parts.Add(p);
                if (C.IEq(CitectText.Trim(p.Substring(0, eq)), "EQUIPMENT"))
                    named.Add(CitectText.Trim(p.Substring(eq + 1)));
            }
            if (refs && h.World != null)
                foreach (string e in named)
                {
                    var byRef = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    var order = new List<string>();
                    foreach (var (_, r) in h.World.Unit.Rows("eqref.dbf"))
                    {
                        if (!C.IEq(CitectText.Trim(r.Get("EQUIP")), e))
                            continue;
                        string re = CitectText.Trim(r.Get("REFEQUIP")),
                            ri = CitectText.Trim(r.Get("REFITEM"));
                        if (re.Length == 0)
                            continue;
                        if (!byRef.TryGetValue(re, out var items))
                        {
                            byRef[re] = items = new List<string>();
                            order.Add(re);
                        }
                        if (ri.Length > 0)
                            items.Add(ri);
                    }
                    foreach (string re in order)
                    {
                        var items = byRef[re];
                        parts.Add(
                            items.Count == 0
                                ? "EQUIPMENT=" + re
                                : "(EQUIPMENT="
                                    + re
                                    + " AND ("
                                    + string.Join(" OR ", items.ConvertAll(i => "ITEM=" + i))
                                    + "))"
                        );
                    }
                }
            if (parts.Count > 0)
                s.Text += "(" + string.Join(" OR ", parts) + ")";
            return W.I(0);
        }
    }
}
