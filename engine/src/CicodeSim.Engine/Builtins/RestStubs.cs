using System;
using System.Collections.Generic;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>
    /// Stubs for what the simulator can't run, answering as the runtime does when the thing isn't
    /// there.
    /// </summary>
    public static class RestStubs
    {
        private const int NoClass = 354;
        private static readonly Value NoObject = Value.Object(-1);

        // ------------------------------------------------------------------ ActiveX
        [Builtin(
            "DspAnCreateControlObject",
            Status = BuiltinStatus.Stub,
            Returns = "-1",
            Doc = "1104634",
            Errors = "354 with the halt flag: the class is not registered (a page is shown)",
            Notes = "No ActiveX control is ever made: with a page on the task's window the class lookup fails as in the runtime (raise 354 with the halt flag,) and -1 (an invalid object); without a window or a page -1, nothing raised."
        )]
        private static Value DspAnCreateControlObject(SimHost h, CicodeTask t, ArgList a)
        {
            var d = DisplayBuiltins.Of(h);
            var w = d.OpenWindow(d.WindowOf(t));
            if (w?.Page == null)
                return NoObject;
            h.Note(
                t,
                "DspAnCreateControlObject(" + A.S(a.Get(0)) + ", \"" + A.S(a.Get(1)) + "\") (not simulated)"
            );
            t.RaiseError(NoClass, true);
            return NoObject;
        }

        // ------------------------------------------------------------------ the scheduler
        private static Value NoScheduler(CicodeTask t)
        {
            W.Raise(t, Codes.InvalidArg);
            return W.I(-1);
        }

        private const string NoSchd = "No scheduler server is simulated (as SchdOpen): ";

        // the simulation's own scheduler (World.EquipScheduler): the items live in memory
        private static long Opt(in ArgList a, int k, long def) =>
            k < a.Count && !a.Get(k).IsNothing ? A.I(a.Get(k)) : def;

        private static string Os(in ArgList a, int k) =>
            k < a.Count && !a.Get(k).IsNothing ? A.S(a.Get(k)) : "";

        /// <summary>A time argument as FILETIME: a TIMESTAMP as it is, a number as seconds
        /// since 1970 (a TIMESTAMP passed to a LONG parameter stays a TIMESTAMP value).</summary>
        private static long Ft(in ArgList a, int k, long def)
        {
            if (k >= a.Count || a.Get(k).IsNothing)
                return def;
            var v = a.Get(k);
            if (v.T == Types.Timestamp)
                return (long)A.U64(v);
            long n = A.I(v);
            return n <= 0 ? n : n * 10_000_000L + 116444736000000000L;
        }

        private const string Sched = "The simulation's own scheduler (World.EquipScheduler, in memory): ";

        [Builtin(
            "ScheduleItemAdd",
            Doc = "1189337",
            Blocking = true,
            Errors = "274 no world (raised, -1)",
            Notes = Sched + "the new item's ID (1, 2, ...)."
        )]
        private static Value ScheduleItemAdd(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return NoScheduler(t);
            var s = CicodeSim.World.EquipScheduler.Of(h.World);
            long id = s.Add(
                Os(a, 0),
                Os(a, 1),
                Os(a, 2),
                Ft(a, 3, 0),
                Ft(a, 4, 0),
                Os(a, 5),
                (long)h.World.Clock.NowFiletime()
            );
            return W.BlockOneTurn(t, "ScheduleItemAdd", () => W.I(id));
        }

        [Builtin(
            "ScheduleItemSetRepeat",
            Doc = "1189340",
            Blocking = true,
            Errors = "274 no such item (returned)",
            Notes = Sched + "the item's recurrence set, 0."
        )]
        private static Value ScheduleItemSetRepeat(SimHost h, CicodeTask t, ArgList a)
        {
            var it = h.World != null ? CicodeSim.World.EquipScheduler.Of(h.World).Get(Opt(a, 1, -1)) : null;
            if (it == null)
                return W.I(Codes.InvalidArg);
            it.Freq = Opt(a, 2, -1);
            it.Interval = Math.Max(1, Opt(a, 3, 1));
            it.Weekday = Opt(a, 4, 0);
            it.WeekdayMask = Opt(a, 5, 0);
            it.MaxRec = Opt(a, 6, -1);
            long until = Ft(a, 7, -1);
            it.RecUntil = until <= 0 ? -1 : until;
            it.DayOrd = Opt(a, 8, 0);
            it.DayOfMonth = Opt(a, 9, 0);
            it.Month = Opt(a, 10, 0);
            it.SpecialInc = Opt(a, 11, 0);
            it.GroupIds = Os(a, 12);
            return W.BlockOneTurn(t, "ScheduleItemSetRepeat", () => W.I(0));
        }

        [Builtin(
            "ScheduleItemModify",
            Doc = "1189339",
            Blocking = true,
            Errors = "274 no such item (returned)",
            Notes = Sched + "the item's state, times and description set, 0."
        )]
        private static Value ScheduleItemModify(SimHost h, CicodeTask t, ArgList a)
        {
            var it = h.World != null ? CicodeSim.World.EquipScheduler.Of(h.World).Get(Opt(a, 1, -1)) : null;
            if (it == null)
                return W.I(Codes.InvalidArg);
            it.State = Os(a, 2);
            it.Start = Ft(a, 3, it.Start);
            it.End = Ft(a, 4, it.End);
            it.Desc = Os(a, 5);
            it.Modified = (long)h.World!.Clock.NowFiletime();
            return W.BlockOneTurn(t, "ScheduleItemModify", () => W.I(0));
        }

        [Builtin(
            "ScheduleItemDelete",
            Doc = "1189338",
            Blocking = true,
            Errors = "274 no such item (returned)",
            Notes = Sched + "the item removed, 0."
        )]
        private static Value ScheduleItemDelete(SimHost h, CicodeTask t, ArgList a)
        {
            bool ok = h.World != null && CicodeSim.World.EquipScheduler.Of(h.World).Delete(Opt(a, 1, -1));
            return W.BlockOneTurn(t, "ScheduleItemDelete", () => W.I(ok ? 0 : Codes.InvalidArg));
        }

        private const string NoSession =
            "The sessions SchdSpecialOpen / SchdSpecialItemOpen open over the simulation's special days (Builtins.SpecialDays).";

        [Builtin(
            "SchdSpecialFirst",
            Doc = "1189317",
            Errors = "410 bad session (returned)",
            Notes = NoSession
        )]
        private static Value SchdSpecialFirst(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "SchdSpecial", 0, false);

        [Builtin(
            "SchdSpecialNext",
            Doc = "1189333",
            Errors = "410 bad session (returned)",
            Notes = NoSession
        )]
        private static Value SchdSpecialNext(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "SchdSpecial", 1, false);

        [Builtin(
            "SchdSpecialPrev",
            Doc = "1189336",
            Errors = "410 bad session (returned)",
            Notes = NoSession
        )]
        private static Value SchdSpecialPrev(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "SchdSpecial", -1, false);

        [Builtin(
            "SchdSpecialGetField",
            Doc = "1189318",
            Errors = "410 bad session (raised, '')",
            Notes = NoSession
        )]
        private static Value SchdSpecialGetField(SimHost h, CicodeTask t, ArgList a) =>
            Browse.GetField(h, t, a, "SchdSpecial");

        [Builtin(
            "SchdSpecialNumRecords",
            Doc = "1189334",
            Errors = "410 bad session (raised, -1)",
            Notes = NoSession
        )]
        private static Value SchdSpecialNumRecords(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Count(h, t, a, "SchdSpecial");

        [Builtin(
            "SchdSpecialClose",
            Doc = "1189316",
            Errors = "410 bad session (returned)",
            Notes = NoSession
        )]
        private static Value SchdSpecialClose(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Close(h, t, a, "SchdSpecial");

        [Builtin(
            "SchdSpecialItemFirst",
            Doc = "1189324",
            Errors = "410 bad session (returned)",
            Notes = NoSession
        )]
        private static Value SchdSpecialItemFirst(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "SchdSpecialItem", 0, false);

        [Builtin(
            "SchdSpeciaItemlNext",
            Doc = "1189328",
            Errors = "410 bad session (returned)",
            Notes = NoSession + " The FUNC0 name is spelled so (the help: SchdSpecialItemNext)."
        )]
        private static Value SchdSpeciaItemlNext(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "SchdSpecialItem", 1, false);

        [Builtin(
            "SchdSpecialItemPrev",
            Doc = "1189331",
            Errors = "410 bad session (returned)",
            Notes = NoSession
        )]
        private static Value SchdSpecialItemPrev(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "SchdSpecialItem", -1, false);

        [Builtin(
            "SchdSpecialItemGetField",
            Doc = "1189325",
            Errors = "410 bad session (raised, '')",
            Notes = NoSession
        )]
        private static Value SchdSpecialItemGetField(SimHost h, CicodeTask t, ArgList a) =>
            Browse.GetField(h, t, a, "SchdSpecialItem");

        [Builtin(
            "SchdSpecialItemNumRecords",
            Doc = "1189329",
            Errors = "410 bad session (raised, -1)",
            Notes = NoSession
        )]
        private static Value SchdSpecialItemNumRecords(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Count(h, t, a, "SchdSpecialItem");

        [Builtin(
            "SchdSpecialItemClose",
            Doc = "1189321",
            Errors = "410 bad session (returned)",
            Notes = NoSession
        )]
        private static Value SchdSpecialItemClose(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Close(h, t, a, "SchdSpecialItem");

        // ------------------------------------------------------------------ I/O devices
        [Builtin(
            "IODeviceControl",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1106223",
            Effects = "keeps the control (ProcessExtras.IoControls); no I/O device runs, so nothing follows from it",
            Errors = "289 no such I/O device (returned); 274 a type the runtime no longer supports (0, 2, 3) or none of the help's (1, 4..16, 18) (returned; guess for the others)",
            Notes = "IODevice: a units.dbf device by name or number, '*' / '-1' every device (the last device's result). The runtime passes the control to its I/O server (UnitDirectControl); the simulator records the Data text per device and type and returns 0."
        )]
        private static Value IODeviceControl(SimHost h, CicodeTask t, ArgList a)
        {
            var w = h.World;
            if (w == null)
                return Tags.NoWorld("IODeviceControl");
            string dev = CitectText.Trim(A.S(a.Get(0)));
            long type = A.Short(A.I(a.Get(1)));
            string data = A.S(a.Get(2));
            string cluster = a.Count > 3 ? A.S(a.Get(3)) : "";
            var names = new List<string>();
            if (
                dev.StartsWith("*", StringComparison.Ordinal) || dev.StartsWith("-", StringComparison.Ordinal)
            )
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (_, r) in w.Unit.Rows("units.dbf"))
                {
                    string n = CitectText.Trim(r.Get("NAME"));
                    if (n.Length > 0 && seen.Add(n))
                        names.Add(n);
                }
                if (names.Count == 0)
                    return W.I(0);
            }
            else
            {
                var d = Servers.IoDevice(h, dev, cluster);
                if (d == null)
                    return W.I(289);
                names.Add(d["NAME"]);
            }
            if (type == 0 || type == 2 || type == 3 || type < 0 || type == 17 || type > 18)
                return W.I(Codes.InvalidArg);
            var x = ProcessExtras.Of(h);
            foreach (string n in names)
                x.IoControls[(A.CLower(n), (int)type)] = data;
            h.Note(t, "IODeviceControl(" + dev + ", " + C.Inv(type) + ", " + data + ") (not simulated)");
            return W.I(0);
        }
    }
}
