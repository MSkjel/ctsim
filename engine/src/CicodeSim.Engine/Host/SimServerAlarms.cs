using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using CicodeSim.Sim;
using CicodeSim.Values;
using CicodeSim.World;
using Timer = System.Threading.Timer;

namespace CicodeSim.Host
{
    /// <summary>The viewer's alarm and equipment lists: ctsim/alarms,
    /// ctsim/equipment and the ctsim/alarmsChanged event (engine/PROTOCOL.md 'Alarms and
    /// equipment').</summary>
    public sealed partial class SimServer
    {
        /// <summary>At most this many rows in one ctsim/alarms or ctsim/equipment answer.</summary>
        public const int MaxListCount = 5000;

        /// <summary>The shortest time between two ctsim/alarmsChanged events (at most 4 a second).</summary>
        public const int AlarmsChangedMs = 250;

        private Timer? _alarmTimer;
        private long _alarmGeneration;
        private int _alarmPolling;

        /// <summary>Start the alarmsChanged poll (the constructor calls it).</summary>
        private void StartAlarmEvents()
        {
            _alarmGeneration = Simulation.World.Alarms.Generation;
            _alarmTimer = new Timer(_ => PollAlarms(), null, AlarmsChangedMs, AlarmsChangedMs);
        }

        private void StopAlarmEvents()
        {
            try
            {
                _alarmTimer?.Dispose();
            }
            catch (Exception) { }
            _alarmTimer = null;
        }

        /// <summary>Send ctsim/alarmsChanged to viewers when the alarm server's generation changes.</summary>
        private void PollAlarms()
        {
            if (Interlocked.Exchange(ref _alarmPolling, 1) != 0)
                return;
            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return;
                bool any;
                lock (_lock)
                    any = _clients.Exists(c => c.Wants("alarms"));
                long g = Simulation.World.Alarms.Generation;
                if (g == _alarmGeneration)
                    return;
                _alarmGeneration = g;
                if (!any)
                    return;
                var b = JsonValue.NewObject();
                b.Set("serial", JsonValue.Of(g));
                Broadcast("alarms", "ctsim/alarmsChanged", b);
            }
            catch (Exception x) when (!(x is OutOfMemoryException))
            {
                // a timer callback's exception would end the process (a closed sink, a world
                // being read while it changes): the next tick tries again
            }
            finally
            {
                Volatile.Write(ref _alarmPolling, 0);
            }
        }

        private static string AlarmIso(World.World w, double t) =>
            DateTime
                .FromFileTimeUtc((long)w.Clock.FiletimeAt(t))
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        /// <summary>The protocol's alarm type of an alarm kind: the kind's name, 'time' for a
        /// time-stamped alarm.</summary>
        internal static string AlarmTypeName(string kind) => kind == AlarmDb.Timestamped ? "time" : kind;

        /// <summary>The protocol's alarm state: off | unack_on | ack_on | unack_off | disabled.</summary>
        internal static string AlarmStateName(AlarmState? st)
        {
            string s = Builtins.AlarmLists.StateName(st);
            return s == "ack_off" ? "off" : s;
        }

        /// <summary>One configured alarm as ctsim/alarms lists it.</summary>
        internal static JsonValue AlarmEntry(World.World w, AlarmDef a)
        {
            var st = w.Alarms.PeekState(a);
            var o = JsonValue.NewObject();
            o.Set("tag", a.Tag);
            o.Set("name", a.Name);
            o.Set("desc", a.Desc);
            o.Set("equipment", a.Equip);
            o.Set("item", a.Item);
            o.Set("type", AlarmTypeName(a.Kind));
            o.Set("category", JsonValue.Of(a.Category));
            o.Set("priority", JsonValue.Of(w.Alarms.Priority(a)));
            o.Set("state", AlarmStateName(st));
            o.Set("on", JsonValue.Of(st != null && st.On));
            o.Set("ack", JsonValue.Of(st == null || st.Ack || !st.InList));
            o.Set("disabled", JsonValue.Of(st != null && st.Disabled));
            o.Set("onTime", st != null && st.Transitions != 0 && st.InList ? AlarmIso(w, st.OnTime) : "");
            return o;
        }

        /// <summary>A raised hardware alarm as ctsim/alarms lists it (tag 'hardware:&lt;code&gt;',
        /// category 255, priority 0).</summary>
        internal static JsonValue HardwareEntry(World.World w, HardwareAlarm hw)
        {
            var st = hw.St;
            var o = JsonValue.NewObject();
            o.Set("tag", "hardware:" + hw.Code.ToString(CultureInfo.InvariantCulture));
            o.Set("name", hw.Name);
            o.Set("desc", hw.Desc);
            o.Set("equipment", "");
            o.Set("item", "");
            o.Set("type", AlarmDb.Hardware);
            o.Set("category", JsonValue.Of(255));
            o.Set("priority", JsonValue.Of(0));
            o.Set("state", AlarmStateName(st));
            o.Set("on", JsonValue.Of(st.On));
            o.Set("ack", JsonValue.Of(st.Ack || !st.InList));
            o.Set("disabled", JsonValue.Of(st.Disabled));
            o.Set("onTime", st.InList ? AlarmIso(w, st.OnTime) : "");
            o.Set("count", JsonValue.Of(hw.Count));
            return o;
        }

        private static (int start, int count) Paging(JsonValue a)
        {
            long start = Int(a, "start", 0),
                count = Int(a, "count", MaxListCount);
            if (start < 0)
                throw new ArgumentException("start must be 0 or more");
            if (count < 0)
                throw new ArgumentException("count must be 0 or more");
            return ((int)Math.Min(start, int.MaxValue), (int)Math.Min(count, MaxListCount));
        }

        private static bool InEquipment(string equip, string scope) =>
            equip.Length > 0
            && (
                string.Equals(equip, scope, StringComparison.OrdinalIgnoreCase)
                || equip.StartsWith(scope + ".", StringComparison.OrdinalIgnoreCase)
            );

        internal const string RegexTooSlow =
            "the regular expression takes too long on a name (make it simpler)";

        /// <summary>A request's filter: {filter, regex?} (regex: a regular expression, revision
        /// 20); false when it is refused (answered: not a regular expression).</summary>
        private bool FilterOf(Client c, JsonValue req, JsonValue a, string filter, out TagListing.Filter f)
        {
            try
            {
                f = new TagListing.Filter(filter, Bool(a, "regex", false));
                return true;
            }
            catch (ArgumentException e)
            {
                f = null!;
                Fail(c.Sink, req, "not a regular expression: " + e.Message);
                return false;
            }
        }

        /// <summary>ctsim/alarms: every configured alarm, filtered and paged.</summary>
        private void Alarms(Client c, JsonValue req, JsonValue a)
        {
            string filter = Str(a, "filter"),
                equipment = Str(a, "equipment").Trim(),
                page = Str(a, "page");
            int? window = Window(a);
            var (start, count) = Paging(a);
            HashSet<string>? pageAlarms = null,
                pageEquip = null;
            string pageName = "";
            if (page.Length > 0 || window != null)
            {
                var refs = PageRefsFor(page, window, out string why);
                if (refs == null)
                {
                    Fail(c.Sink, req, why);
                    return;
                }
                pageName = refs.Page;
                pageAlarms = new HashSet<string>(refs.All(), StringComparer.OrdinalIgnoreCase);
                pageEquip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            if (!FilterOf(c, req, a, filter, out var flt))
                return;
            JsonValue body;
            try
            {
                body = Simulation.Invoke(() =>
                {
                    var w = Simulation.World;
                    if (pageAlarms != null)
                    {
                        foreach (var (eq, _, name) in w.Tags.IterItems())
                            if (pageAlarms.Contains(name))
                                pageEquip!.Add(eq);
                        foreach (string n in pageAlarms)
                        {
                            int dot = n.LastIndexOf('.');
                            if (dot > 0 && w.Equipment.Get(n.Substring(0, dot)) != null)
                                pageEquip!.Add(n.Substring(0, dot));
                        }
                    }
                    var f = flt;
                    bool Keep(string tag, string equip, string item, string name)
                    {
                        if (equipment.Length > 0 && !InEquipment(equip, equipment))
                            return false;
                        if (!f.Empty && !f.Match(tag, equip, item) && !f.Match(name, "", ""))
                            return false;
                        if (pageAlarms != null)
                        {
                            if (pageAlarms.Contains(tag))
                                return true;
                            if (equip.Length == 0)
                                return false;
                            if (pageEquip!.Contains(equip))
                                return true;
                            var e = w.Equipment.Get(equip);
                            return e != null
                                && pageName.Length > 0
                                && string.Equals(
                                    CicodeSim.Project.CitectText.Trim(e.Get("PAGE")).TrimStart('!'),
                                    pageName.TrimStart('!'),
                                    StringComparison.OrdinalIgnoreCase
                                );
                        }
                        return true;
                    }
                    var list = JsonValue.NewArray();
                    int total = 0;
                    foreach (var def in w.Alarms.All())
                    {
                        if (!Keep(def.Tag, def.Equip, def.Item, def.Name))
                            continue;
                        if (total >= start && list.Count < count)
                            list.Add(AlarmEntry(w, def));
                        total++;
                    }
                    foreach (var hw in w.Alarms.HardwareAlarms)
                    {
                        if (!hw.St.InList && !hw.St.Disabled)
                            continue;
                        string tag = "hardware:" + hw.Code.ToString(CultureInfo.InvariantCulture);
                        if (equipment.Length > 0 || pageAlarms != null)
                            continue;
                        if (
                            !f.Empty
                            && !f.Match(tag, "", "")
                            && !f.Match(hw.Name, "", "")
                            && !f.Match(hw.Desc, "", "")
                        )
                            continue;
                        if (total >= start && list.Count < count)
                            list.Add(HardwareEntry(w, hw));
                        total++;
                    }
                    var b = JsonValue.NewObject();
                    b.Set("total", JsonValue.Of(total));
                    b.Set("start", JsonValue.Of(start));
                    b.Set("serial", JsonValue.Of(w.Alarms.Generation));
                    b.Set("alarms", list);
                    return b;
                });
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                Fail(c.Sink, req, RegexTooSlow);
                return;
            }
            Ok(c.Sink, req, body);
        }

        /// <summary>ctsim/equipment: every equipment instance with its runtime fields.</summary>
        private void EquipmentList(Client c, JsonValue req, JsonValue a)
        {
            string filter = Str(a, "filter");
            var (start, count) = Paging(a);
            if (!FilterOf(c, req, a, filter, out var flt))
                return;
            JsonValue body;
            try
            {
                body = Simulation.Invoke(() =>
                {
                    var w = Simulation.World;
                    var f = flt;
                    var list = JsonValue.NewArray();
                    int total = 0;
                    foreach (var e in w.Equipment.InOrder())
                    {
                        if (!f.Empty && !f.Match(e.Name, "", ""))
                            continue;
                        if (total >= start && list.Count < count)
                        {
                            var o = JsonValue.NewObject();
                            o.Set("name", e.Name);
                            o.Set("type", e.Get("TYPE"));
                            foreach (var (fld, v) in w.Equipment.RuntimeOf(e))
                                o.Set(fld, v);
                            o.Set("scheduled", JsonValue.Of(EquipDb.Scheduled(e)));
                            list.Add(o);
                        }
                        total++;
                    }
                    var b = JsonValue.NewObject();
                    b.Set("total", JsonValue.Of(total));
                    b.Set("start", JsonValue.Of(start));
                    b.Set("equipment", list);
                    return b;
                });
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                Fail(c.Sink, req, RegexTooSlow);
                return;
            }
            Ok(c.Sink, req, body);
        }
    }
}
