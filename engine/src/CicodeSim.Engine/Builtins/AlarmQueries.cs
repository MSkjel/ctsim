using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;

namespace CicodeSim.Builtins
{
    /// <summary>Alarm queries over the World's alarm server.</summary>
    public static class AlarmQueries
    {
        internal sealed class FilterSession
        {
            /// <summary>The named filter it edits (null: the alarm lists of a page).</summary>
            public string? Filter;

            /// <summary>The alarm lists of a page it edits (an AN or -1).</summary>
            public List<CicodeSim.World.Display.PageAlarmList>? Lists;
            public string Text = "";
            public int Cursor = -1;
        }

        private static AlarmDb? Db(SimHost h) => h.World?.Alarms;

        /// <summary>The alarm definitions in record order (AlarmDb.All(), ordinals 0..n-1).</summary>
        private static List<AlarmDef> All(SimHost h)
        {
            var d = ProcessData.Of(h);
            if (d.AlarmList == null)
            {
                var db = Db(h)!;
                db.Get(""); // indexes the alarms: sets the ordinals
                d.AlarmList = db.All();
            }
            return d.AlarmList;
        }

        /// <summary>A filter text or the text of the named filter it names (doc 1033455:
        /// 'A filter name OR filter text').</summary>
        private static string FilterText(SimHost h, string s)
        {
            var d = ProcessData.Of(h);
            string k = CitectText.Trim(s);
            return k.Length > 0 && d.NamedFilters.TryGetValue(k, out string? f) ? f : s;
        }

        private static bool CountType(long type) =>
            (type >= 0 && type <= 9) || (type >= 11 && type <= 14) || type == 17 || type == 18;

        /// <summary>A blocking request: one turn in background code, 282 (raised, the value
        /// given) in foreground code.</summary>
        private static Value Blocking(CicodeTask t, string reason, Value fgResult, Func<Value> result)
        {
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return fgResult;
            }
            return W.BlockOneTurn(t, reason, () => result());
        }

        // ------------------------------------------------------------------ counts
        [Builtin(
            "AlarmCount",
            Doc = "1033455",
            Errors = "274 a type that is no count type (raised, -1; guess on the code); 282 CachedMode 0 in foreground code (raised, -1)",
            Notes = "World.AlarmDb.Count(type, filter): types 0..4 alarms, 5..9 hardware alarms, 11..14, 17, 18 (doc). sFilter is a named filter (AlarmFilterOpen) or filter text. CachedMode 1 (default) answers at once; 0 waits one turn. The runtime answers a NEW count in foreground with -1 and 345 until it is cached: the simulator counts at once (verify). KeepAlive is ignored."
        )]
        private static Value AlarmCount(SimHost h, CicodeTask t, ArgList a)
        {
            var db = Db(h);
            if (db == null)
                return Tags.NoWorld("AlarmCount");
            long type = a.Count > 0 ? A.Short(A.I(a.Get(0))) : 1;
            string filter = FilterText(h, a.Count > 1 ? A.S(a.Get(1)) : "");
            long cached = a.Count > 3 ? A.Short(A.I(a.Get(3))) : 1;
            if (!CountType(type))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            if (cached == 0)
                return Blocking(t, "AlarmCount", W.I(-1), () => W.I(db.Count((int)type, filter)));
            return W.I(db.Count((int)type, filter));
        }

        [Builtin(
            "AlarmActive",
            Doc = "1033448",
            Notes = "World.AlarmDb.Active(type): 0 / 1 / 3 give 1 or 0 (active, unacknowledged, disabled alarms in the user's areas), 2 the highest priority of the unacknowledged alarms, 5 / 6 hardware alarm counts; other types 0."
        )]
        private static Value AlarmActive(SimHost h, CicodeTask t, ArgList a)
        {
            var db = Db(h);
            if (db == null)
                return Tags.NoWorld("AlarmActive");
            return W.I(db.Active((int)A.Short(A.I(a.Get(0)))));
        }

        [Builtin(
            "AlarmCountEquipment",
            Doc = "1033456",
            Errors = "274 a type that is no count type (raised, -1; guess on the code); 282 CachedMode 0 in foreground code (raised, -1)",
            Notes = "World.AlarmDb.Count(type, filter, equipment, includeRef): the equipment filter is a ',' list of 'EQUIPMENT=x[*]', 'PAGE=x', 'REFCAT=x' or a bare 'x' (= EQUIPMENT=x*); bIncludingReference (default 1) adds the eqref.dbf references."
        )]
        private static Value AlarmCountEquipment(SimHost h, CicodeTask t, ArgList a)
        {
            var db = Db(h);
            if (db == null)
                return Tags.NoWorld("AlarmCountEquipment");
            long type = a.Count > 0 ? A.Short(A.I(a.Get(0))) : 1;
            string equip = a.Count > 1 ? A.S(a.Get(1)) : "";
            string filter = FilterText(h, a.Count > 2 ? A.S(a.Get(2)) : "");
            long cached = a.Count > 4 ? A.Short(A.I(a.Get(4))) : 1;
            bool inclRef = a.Count <= 5 || A.Short(A.I(a.Get(5))) != 0;
            if (!CountType(type))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            if (cached == 0)
                return Blocking(t, "AlarmCountEquipment", W.I(-1), () =>
                    W.I(db.Count((int)type, filter, equip, inclRef))
                );
            return W.I(db.Count((int)type, filter, equip, inclRef));
        }

        [Builtin(
            "AlarmHighestPriority",
            Doc = "1033485",
            Errors = "274 a mode outside 0..6 (raised, -1); 282 CachedMode 0 in foreground code (raised, -1)",
            Notes = "Mode 0 / 4 the priority of the highest priority alarm (the lowest non-zero priority among the listed alarms, else among the disabled ones), 1 / 5 its state (1 ON unacknowledged, 2 OFF unacknowledged, 3 ON acknowledged, 4 disabled), 2 / 6 state * 256 + priority, 3 the server's order setting 0 (Priority, Alarm State); modes 4..6 leave out the priorities whose almprior.dbf SHOW_AI is FALSE; IncludingReference (default 1, as AlarmCountEquipment) adds the eqref.dbf references (World.AlarmDb.Highest, the routine the page alarm indicator uses too). 0 when no alarm matches."
        )]
        private static Value AlarmHighestPriority(SimHost h, CicodeTask t, ArgList a)
        {
            var db = Db(h);
            if (db == null)
                return Tags.NoWorld("AlarmHighestPriority");
            long mode = A.Short(A.I(a.Get(0)));
            string equip = a.Count > 1 ? A.S(a.Get(1)) : "";
            string filter = FilterText(h, a.Count > 2 ? A.S(a.Get(2)) : "");
            long cached = a.Count > 4 ? A.Short(A.I(a.Get(4))) : 1;
            bool inclRef = a.Count <= 5 || A.Short(A.I(a.Get(5))) != 0;
            if (mode < 0 || mode > 6)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            Func<Value> fn = () =>
            {
                if (mode == 3)
                    return W.I(0);
                var (p, s) = db.Highest(filter, equip, inclRef, indicatorOnly: mode > 3);
                long m = mode > 3 ? mode - 4 : mode;
                return W.I(
                    m == 0 ? p
                    : m == 1 ? s
                    : ((long)s << 8) | (p & 0xFF)
                );
            };
            if (cached == 0)
                return Blocking(t, "AlarmHighestPriority", W.I(-1), fn);
            return fn();
        }

        // ------------------------------------------------------------------ tag actions
        /// <summary>The alarms a tag action names: one alarm (tag or Equipment.Item), every
        /// alarm for ' ' (doc: an empty string matches all) when <paramref name="allowAll"/>.</summary>
        private static List<AlarmDef>? Targets(SimHost h, AlarmDb db, string tag, bool allowAll)
        {
            if (tag.Length > 0 && CitectText.Trim(tag).Length == 0)
                return allowAll ? All(h) : null;
            var x = db.Get(tag);
            return x == null ? null : new List<AlarmDef> { x };
        }

        private static Value TagAction(
            SimHost h,
            CicodeTask t,
            string name,
            string tag,
            bool allowAll,
            Func<AlarmDb, AlarmDef, int> act
        )
        {
            var db = Db(h);
            if (db == null)
                return Tags.NoWorld(name);
            if (CitectText.Trim(tag).Length == 0 && !(allowAll && tag.Length > 0))
                return W.I(Codes.InvalidArg);
            Func<Value> run = () =>
            {
                var list = Targets(h, db, tag, allowAll);
                if (list == null)
                    return W.I(Codes.TagNotFound);
                int r = 0;
                foreach (var x in list)
                {
                    int e = act(db, x);
                    if (r == 0)
                        r = e;
                }
                return W.I(r);
            };
            if (t.Foreground)
                return run();
            return W.BlockOneTurn(t, name, () => run());
        }

        [Builtin(
            "AlarmAckTag",
            Doc = "1033447",
            Effects = "acknowledges the alarm (World.AlarmDb.Acknowledge)",
            Errors = "274 an empty tag (returned); 424 no such alarm (returned; guess on the code)",
            Notes = "The tag or an 'Equipment.Item'. Background code waits one turn (the help: blocking unless called from a foreground task)."
        )]
        private static Value AlarmAckTag(SimHost h, CicodeTask t, ArgList a) =>
            TagAction(h, t, "AlarmAckTag", A.S(a.Get(0)), false, (db, x) => db.Acknowledge(x.Tag, "cicode"));

        [Builtin(
            "AlarmClearTag",
            Doc = "1033452",
            Effects = "acknowledges the alarm, which takes an OFF alarm off the summary",
            Errors = "274 an empty tag (returned); 424 no such alarm (returned; guess on the code)",
            Notes = "' ' clears every alarm. AlarmDb has no separate clear: an acknowledged OFF alarm leaves the list, an ON alarm stays listed until it goes OFF (guess on the runtime's clear of an ON alarm, verify)."
        )]
        private static Value AlarmClearTag(SimHost h, CicodeTask t, ArgList a) =>
            TagAction(h, t, "AlarmClearTag", A.S(a.Get(0)), true, (db, x) => db.Acknowledge(x.Tag, "cicode"));

        [Builtin(
            "AlarmDisableTag",
            Doc = "1033461",
            Effects = "disables the alarm (World.AlarmDb.Disable) until EndTime (0 = for good)",
            Errors = "274 an empty tag or a comment of 201+ characters (returned); 424 no such alarm (returned; guess on the code)",
            Notes = "' ' disables every alarm. EndTime is a time (seconds since 1970) on the simulated clock."
        )]
        private static Value AlarmDisableTag(SimHost h, CicodeTask t, ArgList a)
        {
            string comment = a.Count > 3 ? A.S(a.Get(3)) : "";
            if (comment.Length > 200)
                return W.I(Codes.InvalidArg);
            long end = a.Count > 2 ? A.I(a.Get(2)) : 0;
            var w = h.World;
            double? until = end != 0 && w != null ? end - w.Clock.StartUnix : (double?)null;
            return TagAction(h, t, "AlarmDisableTag", A.S(a.Get(0)), true, (db, x) =>
                db.Disable(x.Tag, until, comment, "cicode")
            );
        }

        [Builtin(
            "AlarmEnableTag",
            Doc = "1033471",
            Effects = "enables the alarm (World.AlarmDb.Enable); bAcknowledge 1 acknowledges it too",
            Errors = "274 an empty tag (returned); 424 no such alarm (returned; guess on the code)",
            Notes = "' ' enables every alarm."
        )]
        private static Value AlarmEnableTag(SimHost h, CicodeTask t, ArgList a)
        {
            bool ack = a.Count > 2 && A.Short(A.I(a.Get(2))) == 1;
            return TagAction(h, t, "AlarmEnableTag", A.S(a.Get(0)), true, (db, x) =>
            {
                int r = db.Enable(x.Tag, "cicode");
                if (r == 0 && ack)
                    db.Acknowledge(x.Tag, "cicode");
                return r;
            });
        }

        [Builtin(
            "AlarmCatGetFormat",
            Doc = "1033449",
            Notes = "category.dbf of the category: Type 0 DSPFMT (default), 1 SUMDSPFMT, 2 SOEDSPFMT; a category that is not defined or has no format gives category 0's; '' when neither has one; another Type ''."
        )]
        private static Value AlarmCatGetFormat(SimHost h, CicodeTask t, ArgList a)
        {
            var db = Db(h);
            if (db == null)
                return Tags.NoWorld("AlarmCatGetFormat");
            long cat = A.Short(A.I(a.Get(0)));
            long type = a.Count > 1 ? A.Short(A.I(a.Get(1))) : 0;
            string field =
                type == 0 ? "DSPFMT"
                : type == 1 ? "SUMDSPFMT"
                : type == 2 ? "SOEDSPFMT"
                : "";
            if (field.Length == 0)
                return W.Empty;
            var cats = db.Categories();
            string v = cats.TryGetValue(cat, out var row) ? CitectText.Trim(row.Get(field)) : "";
            if (v.Length == 0 && cats.TryGetValue(0, out var row0))
                v = CitectText.Trim(row0.Get(field));
            return W.S(C.Cut(v));
        }

        // ------------------------------------------------------------------ fields
        private static readonly HashSet<string> KnownFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "ACKDATE",
            "ACKDATEEXT",
            "ACKTIME",
            "ACQERROR",
            "ALARMTYPE",
            "ALMCOMMENT",
            "AREA",
            "CATEGORY",
            "CLUSTER",
            "COMMENT",
            "CUSTOM1",
            "CUSTOM2",
            "CUSTOM3",
            "CUSTOM4",
            "CUSTOM5",
            "CUSTOM6",
            "CUSTOM7",
            "CUSTOM8",
            "DATE",
            "DATEEXT",
            "DEADBAND",
            "DELTATIME",
            "DESC",
            "DEVDELAY",
            "DEVIATION",
            "DISABLEENDTIME",
            "ERRDESC",
            "ERRPAGE",
            "EQUIPMENT",
            "FORMAT",
            "GROUP",
            "HDELAY",
            "HELP",
            "HHDELAY",
            "HIGH",
            "HIGHHIGH",
            "ITEM",
            "LDELAY",
            "LEVEL",
            "LLDELAY",
            "LOCALTIMEDATE",
            "LOGSTATE",
            "LOW",
            "LOWLOW",
            "MILLISEC",
            "NAME",
            "NATIVE_COMMENT",
            "NATIVE_DESC",
            "NATIVE_NAME",
            "NATIVE_SUMDESC",
            "OFFDATE",
            "OFFDATEEXT",
            "OFFMILLI",
            "OFFTIME",
            "OFFTIMEDATE",
            "OLD_DESC",
            "ONDATE",
            "ONDATEEXT",
            "ONMILLI",
            "ONTIME",
            "ONTIMEDATE",
            "PAGING",
            "PAGINGGROUP",
            "PRIORITY",
            "PRIV",
            "QUALITY",
            "RATE",
            "RECEIPTLOCALTIMEDATE",
            "RECEIPTDATE",
            "STATE",
            "STATE_DESC",
            "STATE_DESC0",
            "STATE_DESC1",
            "STATE_DESC2",
            "STATE_DESC3",
            "STATE_DESC4",
            "STATE_DESC5",
            "STATE_DESC6",
            "STATE_DESC7",
            "SUMDESC",
            "SUMSTATE",
            "SUMTYPE",
            "TAG",
            "TAGEX",
            "TIME",
            "TIMEDATE",
            "TYPE",
            "TYPENUM",
            "USERNAME",
            "VALUE",
            "PAGE",
            "REFCAT",
            "EQUIP",
            "RECNO",
            "DELAY",
            "SETPOINT",
            "ACK",
            "ON",
            "DISABLED",
        };

        private static readonly Dictionary<string, string> TypeNames = new Dictionary<string, string>(
            StringComparer.Ordinal
        )
        {
            [AlarmDb.Digital] = "DIGITAL",
            [AlarmDb.Analog] = "ANALOG",
            [AlarmDb.Advanced] = "ADVANCED",
            [AlarmDb.Timestamped] = "TIME STAMPED",
            [AlarmDb.TsDigital] = "TIME STAMPED DIGITAL",
            [AlarmDb.TsAnalog] = "TIME STAMPED ANALOG",
            [AlarmDb.MultiDigital] = "MULTI-DIGITAL",
            [AlarmDb.ArgyleAnalog] = "ARGYLE ANALOG",
            [AlarmDb.DoublePoint] = "DOUBLE POINT",
        };

        private static string Text(in Value v) => Conv.ValueText(v, out _);

        /// <summary>A field of an alarm (the browse and record functions' fields, doc 1234473 /
        /// 1234475); null for a field it does not know. Times are the simulated clock's.</summary>
        internal static string? Field(SimHost h, AlarmDb db, AlarmDef x, string field)
        {
            string f = CitectText.Upper(CitectText.Trim(field));
            var st = db.PeekState(x);
            double start = h.World?.Clock.StartUnix ?? 0;
            string T(double? sim, int fmt)
            {
                if (st == null || st.Transitions == 0 || sim == null || sim.Value == 0 && fmt < 0)
                    return "";
                long unix = (long)Math.Floor(start + sim.Value);
                return fmt < 0 ? C.Inv(unix) : Time.TimeToStr(h, null, unix, fmt, false);
            }
            switch (f)
            {
                case "TAG":
                    return x.Tag;
                case "NAME":
                case "NATIVE_NAME":
                    return x.Name;
                case "DESC":
                case "NATIVE_DESC":
                case "SUMDESC":
                case "NATIVE_SUMDESC":
                case "OLD_DESC":
                    return x.Desc;
                case "CATEGORY":
                    return C.Inv(x.Category);
                case "PRIORITY":
                    return C.Inv(db.Priority(x));
                case "AREA":
                    return C.Inv(x.Area);
                case "PRIV":
                    return C.Inv(x.Priv);
                case "CLUSTER":
                    return x.Cluster;
                case "EQUIPMENT":
                case "EQUIP":
                    return x.Equip;
                case "ITEM":
                    return x.Item;
                case "COMMENT":
                case "NATIVE_COMMENT":
                    return x.Comment;
                case "ALMCOMMENT":
                    return st?.Comment ?? "";
                case "HELP":
                    return x.Help;
                case "FORMAT":
                    return x.Format;
                case "TYPE":
                case "ALARMTYPE":
                    return TypeNames.TryGetValue(x.Kind, out string? tn) ? tn : CitectText.Upper(x.Kind);
                case "TYPENUM":
                case "SUMTYPE":
                    return C.Inv(AlarmDb.SumTypes.TryGetValue(x.Kind, out int k) ? k : -1);
                case "STATE":
                case "LOGSTATE":
                case "SUMSTATE":
                    if (st == null)
                        return "OFF";
                    if (st.Disabled)
                        return "DISABLED";
                    return (st.On ? "ON" : "OFF") + (st.InList && !st.Ack ? "" : " ACK");
                case "STATE_DESC":
                    return st?.Sub ?? "";
                case "VALUE":
                    return st?.Value != null ? FloatText.Shortest(st.Value.Value) : "";
                case "TIMEDATE":
                case "LOCALTIMEDATE":
                    return T(st?.Time, -1);
                case "ONTIMEDATE":
                    return T(st?.OnTime, -1);
                case "OFFTIMEDATE":
                    return T(st?.OffTime, -1);
                case "TIME":
                    return T(st?.Time, 1);
                case "ONTIME":
                    return T(st?.OnTime, 1);
                case "OFFTIME":
                    return T(st?.OffTime, 1);
                case "ACKTIME":
                    return T(st?.AckTime, 1);
                case "DATE":
                    return T(st?.Time, 2);
                case "ONDATE":
                    return T(st?.OnTime, 2);
                case "OFFDATE":
                    return T(st?.OffTime, 2);
                case "ACKDATE":
                    return T(st?.AckTime, 2);
                case "RECNO":
                    return C.Inv(x.Ordinal);
                case "PAGING":
                    return x.Paging ? "1" : "0";
                case "PAGINGGROUP":
                    return x.PagingGroup;
                case "QUALITY":
                    return "Good";
                case "ACQERROR":
                    return db.AcqError(x) ? "1" : "0";
                case "MILLISEC":
                case "ONMILLI":
                case "OFFMILLI":
                    return "0";
            }
            if (
                f.StartsWith("CUSTOM", StringComparison.Ordinal)
                && f.Length == 7
                && f[6] >= '1'
                && f[6] <= '8'
            )
                return x.Custom[f[6] - '1'];
            if (f.StartsWith("STATE_DESC", StringComparison.Ordinal))
                return "";
            string prop = CitectText.Lower(f);
            if (AlarmDb.IsProperty(prop))
                return Text(db.PropertyValue(x, prop));
            return KnownFields.Contains(f) ? "" : null;
        }

        // ------------------------------------------------------------------ browse
        private static Value BrowseOpen(SimHost h, CicodeTask t, ArgList a, string kind)
        {
            var db = Db(h);
            if (db == null)
                return Tags.NoWorld(kind + "Open");
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.I(-1);
            }
            var flt = new AlarmFilter(FilterText(h, A.S(a.Get(0))));
            string fields = A.S(a.Get(1));
            var cl = Browse.FieldSet(a.Count > 2 ? A.S(a.Get(2)) : "");
            var all = All(h);
            var m = new List<int>();
            for (int i = 0; i < all.Count; i++)
            {
                var x = all[i];
                if (cl != null && x.Cluster.Length > 0 && !cl.Contains(x.Cluster.ToUpperInvariant()))
                    continue;
                if (!flt.Empty && !flt.Match(fld => FieldValue(h, db, x, fld)))
                    continue;
                m.Add(i);
            }
            Func<int, string, string?> get = (i, f) => Field(h, db, all[i], f);
            return Browse.Open(
                h,
                t,
                kind,
                Browse.Sorted(m, get, Browse.SortSpec("", "NAME", "TAG")),
                get,
                fields
            );
        }

        /// <summary>The value a browse filter compares (numbers for the number fields).</summary>
        private static AlarmFilter.FieldValue FieldValue(SimHost h, AlarmDb db, AlarmDef x, string fld)
        {
            string f = CitectText.Upper(fld);
            switch (f)
            {
                case "CATEGORY":
                    return AlarmFilter.FieldValue.Num(x.Category);
                case "PRIORITY":
                    return AlarmFilter.FieldValue.Num(db.Priority(x));
                case "AREA":
                    return AlarmFilter.FieldValue.Num(x.Area);
                case "PRIV":
                    return AlarmFilter.FieldValue.Num(x.Priv);
                case "TYPENUM":
                    return AlarmFilter.FieldValue.Num(
                        AlarmDb.SumTypes.TryGetValue(x.Kind, out int k) ? k : -1
                    );
            }
            string? v = Field(h, db, x, f);
            return v == null ? AlarmFilter.FieldValue.None : AlarmFilter.FieldValue.Str(v);
        }

        [Builtin(
            "AlmBrowseOpen",
            Doc = "1033523",
            Blocking = true,
            Errors = "282 in foreground code (raised, -1)",
            Notes = "A browse over every configured alarm (AlarmDb.All(); doc: 'browse all configured alarms'), the filter in AlarmDb's filter syntax ('AAA' = 'Tag=AAA'), sorted by NAME then TAG (doc: alphabetically by name). Fields as doc 1234473 where the simulated alarm server has them; times are the simulated clock's."
        )]
        private static Value AlmBrowseOpen(SimHost h, CicodeTask t, ArgList a) =>
            BrowseOpen(h, t, a, "AlmBrowse");

        [Builtin(
            "AlmBrowseFirst",
            Doc = "1033519",
            Blocking = true,
            Errors = "410 bad session; 412 no records; 282 in foreground code (returned)"
        )]
        private static Value AlmBrowseFirst(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "AlmBrowse", 0, true);

        [Builtin(
            "AlmBrowseNext",
            Doc = "1033521",
            Blocking = true,
            Errors = "410 bad session; 412 past the last record; 282 in foreground code (returned)"
        )]
        private static Value AlmBrowseNext(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "AlmBrowse", 1, true);

        [Builtin(
            "AlmBrowsePrev",
            Doc = "1033524",
            Blocking = true,
            Errors = "410 bad session; 412 before the first record; 282 in foreground code (returned)"
        )]
        private static Value AlmBrowsePrev(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "AlmBrowse", -1, true);

        [Builtin(
            "AlmBrowseGetField",
            Doc = "1033520",
            Errors = "410 bad session; 412 no current record; 413 unknown field (raised, '' returned)"
        )]
        private static Value AlmBrowseGetField(SimHost h, CicodeTask t, ArgList a) =>
            Browse.GetField(h, t, a, "AlmBrowse");

        [Builtin("AlmBrowseNumRecords", Doc = "1033522", Errors = "410 bad session (raised, -1)")]
        private static Value AlmBrowseNumRecords(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Count(h, t, a, "AlmBrowse");

        [Builtin("AlmBrowseClose", Doc = "1033516", Errors = "410 bad session (returned)")]
        private static Value AlmBrowseClose(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Close(h, t, a, "AlmBrowse");

        [Builtin(
            "AlmTagsOpen",
            Doc = "1033550",
            Blocking = true,
            Errors = "282 in foreground code (raised, -1)",
            Notes = "As AlmBrowseOpen (deprecated alias): every configured alarm."
        )]
        private static Value AlmTagsOpen(SimHost h, CicodeTask t, ArgList a) =>
            BrowseOpen(h, t, a, "AlmTags");

        [Builtin(
            "AlmTagsFirst",
            Doc = "1033546",
            Blocking = true,
            Errors = "410 bad session; 412 no records; 282 in foreground code (returned)"
        )]
        private static Value AlmTagsFirst(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "AlmTags", 0, true);

        [Builtin(
            "AlmTagsNext",
            Doc = "1033548",
            Blocking = true,
            Errors = "410 bad session; 412 past the last record; 282 in foreground code (returned)"
        )]
        private static Value AlmTagsNext(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "AlmTags", 1, true);

        [Builtin(
            "AlmTagsPrev",
            Doc = "1033551",
            Blocking = true,
            Errors = "410 bad session; 412 before the first record; 282 in foreground code (returned)"
        )]
        private static Value AlmTagsPrev(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "AlmTags", -1, true);

        [Builtin(
            "AlmTagsGetField",
            Doc = "1033547",
            Errors = "410 bad session; 412 no current record; 413 unknown field (raised, '' returned)"
        )]
        private static Value AlmTagsGetField(SimHost h, CicodeTask t, ArgList a) =>
            Browse.GetField(h, t, a, "AlmTags");

        [Builtin("AlmTagsNumRecords", Doc = "1033549", Errors = "410 bad session (raised, -1)")]
        private static Value AlmTagsNumRecords(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Count(h, t, a, "AlmTags");

        [Builtin("AlmTagsClose", Doc = "1033543", Errors = "410 bad session (returned)")]
        private static Value AlmTagsClose(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Close(h, t, a, "AlmTags");

        /// <summary>An alarm browse session's current alarm (AlmBrowse / AlmTags), null when the
        /// session or its cursor is not valid; the error code to return in <paramref name="code"/>.</summary>
        private static AlarmDef? Current(SimHost h, in Value v, string kind, out int code)
        {
            var s = Browse.Session(h, v, kind);
            if (s == null)
            {
                code = Codes.BrowseSessionNotFound;
                return null;
            }
            if (s.Cursor < 0 || s.Cursor >= s.Count)
            {
                code = Codes.BrowseEof;
                return null;
            }
            code = 0;
            var all = All(h);
            int i = s.Order[s.Cursor];
            return i >= 0 && i < all.Count ? all[i] : null;
        }

        private static Value BrowseAction(
            SimHost h,
            CicodeTask t,
            ArgList a,
            string kind,
            Func<AlarmDb, AlarmDef, int> act
        )
        {
            var db = Db(h);
            if (db == null)
                return Tags.NoWorld(kind + "Ack");
            var x = Current(h, a.Get(0), kind, out int code);
            if (x == null)
                return W.I(code);
            return W.I(act(db, x));
        }

        [Builtin(
            "AlmBrowseAck",
            Doc = "1033515",
            Effects = "acknowledges the alarm at the cursor",
            Errors = "410 a bad session; 412 no current record (returned)",
            Notes = "Non-blocking (doc); 0."
        )]
        private static Value AlmBrowseAck(SimHost h, CicodeTask t, ArgList a) =>
            BrowseAction(h, t, a, "AlmBrowse", (db, x) => db.Acknowledge(x.Tag, "cicode"));

        [Builtin(
            "AlmBrowseClear",
            Effects = "clears (acknowledges) the alarm at the cursor",
            Errors = "410 a bad session; 412 no current record (returned)",
            Notes = "As AlarmClearTag of the alarm at the cursor."
        )]
        private static Value AlmBrowseClear(SimHost h, CicodeTask t, ArgList a) =>
            BrowseAction(h, t, a, "AlmBrowse", (db, x) => db.Acknowledge(x.Tag, "cicode"));

        private static int DisableAt(SimHost h, ArgList a, AlarmDb db, AlarmDef x)
        {
            string comment = a.Count > 2 ? A.S(a.Get(2)) : "";
            if (comment.Length > 200)
                return Codes.InvalidArg;
            long end = a.Count > 1 ? A.I(a.Get(1)) : 0;
            var w = h.World;
            double? until = end != 0 && w != null ? end - w.Clock.StartUnix : (double?)null;
            return db.Disable(x.Tag, until, comment, "cicode");
        }

        private static int EnableAt(ArgList a, AlarmDb db, AlarmDef x)
        {
            int r = db.Enable(x.Tag, "cicode");
            if (r == 0 && a.Count > 1 && A.Short(A.I(a.Get(1))) == 1)
                db.Acknowledge(x.Tag, "cicode");
            return r;
        }

        [Builtin(
            "AlmBrowseDisable",
            Doc = "1033517",
            Effects = "disables the alarm at the cursor until EndTime (0 = for good)",
            Errors = "410 a bad session; 412 no current record; 274 a comment of 201+ characters (returned)"
        )]
        private static Value AlmBrowseDisable(SimHost h, CicodeTask t, ArgList a) =>
            BrowseAction(h, t, a, "AlmBrowse", (db, x) => DisableAt(h, a, db, x));

        [Builtin(
            "AlmBrowseEnable",
            Doc = "1033518",
            Effects = "enables the alarm at the cursor (bAcknowledge 1: and acknowledges it)",
            Errors = "410 a bad session; 412 no current record (returned)"
        )]
        private static Value AlmBrowseEnable(SimHost h, CicodeTask t, ArgList a) =>
            BrowseAction(h, t, a, "AlmBrowse", (db, x) => EnableAt(a, db, x));

        [Builtin(
            "AlmTagsAck",
            Doc = "1033541",
            Effects = "acknowledges the alarm at the cursor",
            Errors = "410 a bad session; 412 no current record (returned)"
        )]
        private static Value AlmTagsAck(SimHost h, CicodeTask t, ArgList a) =>
            BrowseAction(h, t, a, "AlmTags", (db, x) => db.Acknowledge(x.Tag, "cicode"));

        [Builtin(
            "AlmTagsClear",
            Doc = "1033542",
            Effects = "clears (acknowledges) the alarm at the cursor",
            Errors = "410 a bad session; 412 no current record (returned)"
        )]
        private static Value AlmTagsClear(SimHost h, CicodeTask t, ArgList a) =>
            BrowseAction(h, t, a, "AlmTags", (db, x) => db.Acknowledge(x.Tag, "cicode"));

        [Builtin(
            "AlmTagsDisable",
            Doc = "1033544",
            Effects = "disables the alarm at the cursor until EndTime (0 = for good)",
            Errors = "410 a bad session; 412 no current record; 274 a comment of 201+ characters (returned)"
        )]
        private static Value AlmTagsDisable(SimHost h, CicodeTask t, ArgList a) =>
            BrowseAction(h, t, a, "AlmTags", (db, x) => DisableAt(h, a, db, x));

        [Builtin(
            "AlmTagsEnable",
            Doc = "1033545",
            Effects = "enables the alarm at the cursor (bAcknowledge 1: and acknowledges it)",
            Errors = "410 a bad session; 412 no current record (returned)"
        )]
        private static Value AlmTagsEnable(SimHost h, CicodeTask t, ArgList a) =>
            BrowseAction(h, t, a, "AlmTags", (db, x) => EnableAt(a, db, x));

        // ------------------------------------------------------------------ records
        /// <summary>' ' (blank) matches all; else the field equals the text (without case).</summary>
        private static bool Matches(string have, string want) =>
            CitectText.Trim(want).Length == 0 || C.IEq(CitectText.Trim(have), CitectText.Trim(want));

        private static long FindRec(SimHost h, AlarmDb db, long from, string tag, string name, string desc)
        {
            var all = All(h);
            for (long i = Math.Max(0, from); i < all.Count; i++)
            {
                var x = all[(int)i];
                bool tagOk =
                    CitectText.Trim(tag).Length == 0
                    || C.IEq(x.Tag, CitectText.Trim(tag))
                    || (x.Equip.Length > 0 && C.IEq(x.Equip + "." + x.Item, CitectText.Trim(tag)));
                if (tagOk && Matches(x.Name, name) && Matches(x.Desc, desc))
                    return i;
            }
            return -1;
        }

        [Builtin(
            "AlarmFirstTagRec",
            Doc = "1033475",
            Blocking = true,
            Errors = "282 in foreground code (raised, -1)",
            Notes = "The record number (the alarm's definition ordinal) of the first configured alarm whose tag (or Equipment.Item), name and description equal the arguments without case; ' ' matches all. -1 when none."
        )]
        private static Value AlarmFirstTagRec(SimHost h, CicodeTask t, ArgList a)
        {
            var db = Db(h);
            if (db == null)
                return Tags.NoWorld("AlarmFirstTagRec");
            string tag = A.S(a.Get(0)),
                name = A.S(a.Get(1)),
                desc = A.S(a.Get(2));
            return Blocking(t, "AlarmFirstTagRec", W.I(-1), () => W.I(FindRec(h, db, 0, tag, name, desc)));
        }

        [Builtin(
            "AlarmNextTagRec",
            Doc = "1033492",
            Blocking = true,
            Errors = "282 in foreground code (raised, -1)",
            Notes = "As AlarmFirstTagRec from the record after Record."
        )]
        private static Value AlarmNextTagRec(SimHost h, CicodeTask t, ArgList a)
        {
            var db = Db(h);
            if (db == null)
                return Tags.NoWorld("AlarmNextTagRec");
            long rec = A.I(a.Get(0));
            string tag = A.S(a.Get(1)),
                name = A.S(a.Get(2)),
                desc = A.S(a.Get(3));
            return Blocking(t, "AlarmNextTagRec", W.I(-1), () =>
                W.I(rec < 0 ? -1 : FindRec(h, db, rec + 1, tag, name, desc))
            );
        }

        [Builtin(
            "AlarmGetFieldRec",
            Doc = "1033479; 1234475",
            Blocking = true,
            Errors = "282 in foreground code (raised, '-1'); 274 a bad record or an unknown field (raised, '')",
            Notes = "A field of the alarm with that record number (the fields of AlmBrowseGetField)."
        )]
        private static Value AlarmGetFieldRec(SimHost h, CicodeTask t, ArgList a)
        {
            var db = Db(h);
            if (db == null)
                return Tags.NoWorld("AlarmGetFieldRec");
            long rec = A.I(a.Get(0));
            string field = A.S(a.Get(1));
            return Blocking(t, "AlarmGetFieldRec", W.S("-1"), () =>
            {
                var all = All(h);
                string? v = rec >= 0 && rec < all.Count ? Field(h, db, all[(int)rec], field) : null;
                if (v == null)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.Empty;
                }
                return W.S(C.Cut(v));
            });
        }

        [Builtin(
            "AlarmNotifyVarChange",
            Doc = "1033493",
            Effects = "re-evaluates the time-stamped alarms that read the tag",
            Errors = "424 no such variable tag (returned)",
            Notes = "The time-stamped digital / analog alarms whose inputs name the tag are evaluated now from the tag's current value (the simulator does not take Value and Timestamp: verify); 0. bSync 1 waits one turn in background code."
        )]
        private static Value AlarmNotifyVarChange(SimHost h, CicodeTask t, ArgList a)
        {
            var w = h.World;
            if (w == null)
                return Tags.NoWorld("AlarmNotifyVarChange");
            string tag = CitectText.Trim(A.S(a.Get(0)));
            bool sync = a.Count > 5 && A.Short(A.I(a.Get(5))) != 0;
            if (w.Tags.Find(tag) == null)
                return W.I(Codes.TagNotFound);
            var d = ProcessData.Of(h);
            if (d.TimestampedInputs == null)
            {
                var idx = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var x in All(h))
                {
                    if (x.Kind != AlarmDb.TsDigital && x.Kind != AlarmDb.TsAnalog)
                        continue;
                    foreach (string inp in w.Alarms.InputsOf(x))
                    {
                        if (!idx.TryGetValue(inp, out var l))
                            idx[inp] = l = new List<string>();
                        l.Add(x.Tag);
                    }
                }
                d.TimestampedInputs = idx;
            }
            if (d.TimestampedInputs.TryGetValue(tag, out var tags))
                foreach (string at in tags)
                    w.Alarms.Evaluate(at);
            if (sync && !t.Foreground)
                return W.BlockOneTurn(t, "AlarmNotifyVarChange", () => W.I(0));
            return W.I(0);
        }

        // ------------------------------------------------------------------ named filters and edit sessions
        [Builtin(
            "AlarmFilterOpen",
            Doc = "1069691",
            Effects = "creates a named filter (empty: matches every alarm)",
            Errors = "274 an empty name, mode 0 and no such filter, mode 1 and the filter exists, another mode (returned; guess on the code)",
            Notes = "Mode 0 opens, 1 creates, 2 either; names without case. AutoCloseMode (close at a page change) is not simulated."
        )]
        private static Value AlarmFilterOpen(SimHost h, CicodeTask t, ArgList a)
        {
            var d = ProcessData.Of(h);
            string name = CitectText.Trim(A.S(a.Get(0)));
            long mode = A.Short(A.I(a.Get(1)));
            if (name.Length == 0 || mode < 0 || mode > 2)
                return W.I(Codes.InvalidArg);
            bool exists = d.NamedFilters.ContainsKey(name);
            if ((mode == 0 && !exists) || (mode == 1 && exists))
                return W.I(Codes.InvalidArg);
            if (!exists)
                d.NamedFilters[name] = "";
            return W.I(0);
        }

        [Builtin(
            "AlarmFilterClose",
            Doc = "1069679",
            Blocking = true,
            Effects = "removes the named filter",
            Errors = "274 no such filter (returned; guess on the code); 282 in foreground code (raised and returned)"
        )]
        private static Value AlarmFilterClose(SimHost h, CicodeTask t, ArgList a)
        {
            var d = ProcessData.Of(h);
            string name = CitectText.Trim(A.S(a.Get(0)));
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.I(Codes.FgBlock);
            }
            return W.BlockOneTurn(t, "AlarmFilterClose", () =>
                W.I(d.NamedFilters.Remove(name) ? 0 : Codes.InvalidArg)
            );
        }

        private static FilterSession? Session(SimHost h, in Value v) =>
            ProcessData.Of(h).FilterSessions.Get(A.Short(A.I(v)));

        [Builtin(
            "AlarmFilterEditOpen",
            Doc = "1069687",
            Blocking = true,
            Errors = "282 in foreground code (raised, -1)",
            Notes = "A session on a named filter (AlarmFilterOpen), holding its current text; else, for an AN or -1, on the alarm list at that AN of the current window's page or every list of the page, holding the first list's custom filter. -1 when neither."
        )]
        private static Value AlarmFilterEditOpen(SimHost h, CicodeTask t, ArgList a)
        {
            var d = ProcessData.Of(h);
            string src = CitectText.Trim(A.S(a.Get(0)));
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.I(-1);
            }
            return W.BlockOneTurn(t, "AlarmFilterEditOpen", () =>
            {
                if (!d.NamedFilters.TryGetValue(src, out string? text))
                {
                    // an AN of an alarm list on the page, or -1 every list of the page
                    if (
                        !long.TryParse(
                            src,
                            System.Globalization.NumberStyles.AllowLeadingSign,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out long an
                        )
                        || an == 0
                    )
                        return W.I(-1);
                    var ls = AlarmLists.ListsAt(h, t, an);
                    if (ls.Count == 0)
                        return W.I(-1);
                    return W.I(
                        d.FilterSessions.Add(new FilterSession { Lists = ls, Text = ls[0].CustomFilter })
                    );
                }
                return W.I(d.FilterSessions.Add(new FilterSession { Filter = src, Text = text }));
            });
        }

        [Builtin(
            "AlarmFilterEditClose",
            Doc = "1069682",
            Errors = "269 a bad session (returned; guess on the code)",
            Notes = "The filter itself stays as committed."
        )]
        private static Value AlarmFilterEditClose(SimHost h, CicodeTask t, ArgList a)
        {
            var d = ProcessData.Of(h);
            long hd = A.Short(A.I(a.Get(0)));
            if (d.FilterSessions.Get(hd) == null)
                return W.I(Codes.BadHandle);
            d.FilterSessions.Free(hd);
            return W.I(0);
        }

        [Builtin(
            "AlarmFilterEditSet",
            Doc = "1069688",
            Effects = "replaces the session's filter text (no check)",
            Errors = "269 a bad session (returned; guess on the code)"
        )]
        private static Value AlarmFilterEditSet(SimHost h, CicodeTask t, ArgList a)
        {
            var s = Session(h, a.Get(0));
            if (s == null)
                return W.I(Codes.BadHandle);
            s.Text = A.S(a.Get(1));
            s.Cursor = -1;
            return W.I(0);
        }

        [Builtin(
            "AlarmFilterEditAppend",
            Doc = "1069680",
            Effects = "appends the text to the session's filter text as it is (no check, no separator)",
            Errors = "269 a bad session (returned; guess on the code)"
        )]
        private static Value AlarmFilterEditAppend(SimHost h, CicodeTask t, ArgList a)
        {
            var s = Session(h, a.Get(0));
            if (s == null)
                return W.I(Codes.BadHandle);
            s.Text += A.S(a.Get(1));
            return W.I(0);
        }

        /// <summary>The parts of a filter text: each ends after a ';' (the last gets one), a part
        /// without a ';' within 254 characters is cut there (doc 1069684).</summary>
        internal static List<string> Parts(string text)
        {
            var o = new List<string>();
            int i = 0;
            while (i < text.Length)
            {
                int semi = text.IndexOf(';', i);
                int end = semi < 0 ? text.Length : semi + 1;
                if (end - i > 254)
                    end = i + 254;
                string p = text.Substring(i, end - i);
                i = end;
                if (CitectText.Trim(p).Length == 0 || p == ";")
                    continue;
                if (!p.EndsWith(";", StringComparison.Ordinal) && p.Length < 254)
                    p += ";";
                o.Add(p);
            }
            return o;
        }

        private static Value Part(SimHost h, ArgList a, int how)
        {
            var s = Session(h, a.Get(0));
            if (s == null)
                return W.Empty;
            var parts = Parts(s.Text);
            int c =
                how == 0 ? 0
                : how == 2 ? parts.Count - 1
                : how > 0 ? s.Cursor + 1
                : s.Cursor - 1;
            if (c < 0 || c >= parts.Count)
            {
                s.Cursor = c < 0 ? -1 : parts.Count;
                return W.Empty;
            }
            s.Cursor = c;
            return W.S(parts[c]);
        }

        [Builtin(
            "AlarmFilterEditFirst",
            Doc = "1069684",
            Notes = "The first part of the session's text: a clause up to and with its ';' (the doc example: 'Tag=Dig*;Category=1;' gives 'Tag=Dig*;'); '' when none or for a bad session."
        )]
        private static Value AlarmFilterEditFirst(SimHost h, CicodeTask t, ArgList a) => Part(h, a, 0);

        [Builtin("AlarmFilterEditNext", Doc = "1069686", Notes = "The next part; '' past the last.")]
        private static Value AlarmFilterEditNext(SimHost h, CicodeTask t, ArgList a) => Part(h, a, 1);

        [Builtin("AlarmFilterEditPrev", Doc = "1069713", Notes = "The part before; '' before the first.")]
        private static Value AlarmFilterEditPrev(SimHost h, CicodeTask t, ArgList a) => Part(h, a, -1);

        [Builtin(
            "AlarmFilterEditLast",
            Doc = "1069685",
            Notes = "The last part (a last clause without ';' gets one: 'Priority<20;')."
        )]
        private static Value AlarmFilterEditLast(SimHost h, CicodeTask t, ArgList a) => Part(h, a, 2);

        /// <summary>The field names a filter text compares, null when it names a field the alarm
        /// server does not know (an invalid filter).</summary>
        internal static HashSet<string>? FilterFields(string text)
        {
            var o = new HashSet<string>(StringComparer.Ordinal);
            var f = new AlarmFilter(text);
            bool ok = true;
            void Walk(AlarmFilter.Node? n)
            {
                switch (n)
                {
                    case AlarmFilter.All all:
                        foreach (var x in all.Items)
                            Walk(x);
                        break;
                    case AlarmFilter.Bin b:
                        Walk(b.A);
                        Walk(b.B);
                        break;
                    case AlarmFilter.Not nt:
                        Walk(nt.A);
                        break;
                    case AlarmFilter.Cmp c:
                        if (!KnownFields.Contains(c.Field))
                            ok = false;
                        o.Add(c.Field);
                        break;
                }
            }
            Walk(f.Tree);
            return ok ? o : null;
        }

        [Builtin(
            "AlarmFilterEditHasField",
            Doc = "1069689",
            Notes = "1 when the session's filter is valid (every field it names is an alarm field) and names one of the ','-separated fields; else 0 (also for a bad session)."
        )]
        private static Value AlarmFilterEditHasField(SimHost h, CicodeTask t, ArgList a)
        {
            var s = Session(h, a.Get(0));
            if (s == null)
                return W.I(0);
            var fields = FilterFields(s.Text);
            if (fields == null)
                return W.I(0);
            foreach (string f in A.S(a.Get(1)).Split(','))
                if (fields.Contains(CitectText.Upper(CitectText.Trim(f))))
                    return W.I(1);
            return W.I(0);
        }

        [Builtin(
            "AlarmFilterEditCommit",
            Doc = "1069683",
            Blocking = true,
            Effects = "applies the session's text to its named filter or its page alarm lists",
            Errors = "269 a bad session (returned; guess on the code); 274 an invalid filter (returned; guess on the code); 282 in foreground code (raised and returned)"
        )]
        private static Value AlarmFilterEditCommit(SimHost h, CicodeTask t, ArgList a)
        {
            var d = ProcessData.Of(h);
            var s = Session(h, a.Get(0));
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.I(Codes.FgBlock);
            }
            return W.BlockOneTurn(t, "AlarmFilterEditCommit", () =>
            {
                if (s == null)
                    return W.I(Codes.BadHandle);
                if (FilterFields(s.Text) == null)
                    return W.I(Codes.InvalidArg);
                if (s.Filter != null)
                    d.NamedFilters[s.Filter] = s.Text;
                if (s.Lists != null)
                    foreach (var l in s.Lists)
                    {
                        l.CustomFilter = s.Text;
                        l.Offset = 0;
                        l.Serial++;
                    }
                return W.I(0);
            });
        }
    }
}
