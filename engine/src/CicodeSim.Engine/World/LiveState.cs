using System;
using System.Collections.Generic;
using System.Globalization;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.World
{
    /// <summary>What a setAlarm sets (<see cref="AlarmDb.SetState"/>); null = keep or follow
    /// the alarm's state. Times are simulated seconds.</summary>
    public sealed class AlarmSet
    {
        public bool? On,
            Ack,
            Disabled;

        /// <summary>The .State value: the state enumeration (low 5 bits) + the action masks
        /// (256 unacknowledged, 512 disabled ..., doc: 1068486).</summary>
        public long? State;
        public double? OnTime,
            Time;

        public static readonly string[] Fields = { "on", "ack", "disabled", "state", "onTime", "time" };

        /// <summary>An alarm state from JSON.</summary>
        public static AlarmSet FromJson(JsonValue spec, SimClock clock)
        {
            var s = new AlarmSet();
            if (spec.Kind == JsonKind.String)
            {
                switch (CitectText.Lower(CitectText.Trim(spec.Str)))
                {
                    case "on":
                        s.On = true;
                        s.Ack = false;
                        s.Disabled = false;
                        break;
                    case "on_ack":
                        s.On = true;
                        s.Ack = true;
                        s.Disabled = false;
                        break;
                    case "off_unack":
                        s.On = false;
                        s.Ack = false;
                        s.Disabled = false;
                        break;
                    case "off":
                        s.On = false;
                        s.Ack = true;
                        s.Disabled = false;
                        break;
                    case "disabled":
                        s.Disabled = true;
                        break;
                    default:
                        throw new FormatException(
                            "not an alarm state: " + spec.Dump() + " (on, on_ack, off_unack, off, disabled)"
                        );
                }
                return s;
            }
            if (spec.Kind != JsonKind.Object)
                throw new FormatException("an alarm state is an object or a word, not " + spec.Dump());
            foreach (var kv in spec.Pairs)
            {
                if (Array.IndexOf(Fields, kv.Key) < 0)
                    throw new FormatException(
                        "unknown alarm state field "
                            + CitectText.Quote(kv.Key)
                            + " ("
                            + string.Join(", ", Fields)
                            + ")"
                    );
                var v = kv.Value;
                if (v.IsNull)
                    continue;
                try
                {
                    switch (kv.Key)
                    {
                        case "on":
                            s.On = Flag(v);
                            break;
                        case "ack":
                            s.Ack = Flag(v);
                            break;
                        case "disabled":
                            s.Disabled = Flag(v);
                            break;
                        case "state":
                            s.State = (long)v.ToInteger();
                            break;
                        case "onTime":
                            s.OnTime = TimeOf(v, clock);
                            break;
                        case "time":
                            s.Time = TimeOf(v, clock);
                            break;
                    }
                }
                catch (Exception e) when (e is OverflowException || e is InvalidOperationException)
                {
                    throw new FormatException(
                        "alarm state field " + CitectText.Quote(kv.Key) + ": " + e.Message,
                        e
                    );
                }
            }
            return s;
        }

        private static bool Flag(JsonValue v)
        {
            double d = v.ToNumber();
            return d != 0 || double.IsNaN(d);
        }

        /// <summary>A time field: a number = simulated seconds, text = an ISO 8601 instant
        /// counted from the clock's start.</summary>
        internal static double TimeOf(JsonValue v, SimClock clock)
        {
            if (v.Kind == JsonKind.String)
                return SimClock.ParseStart(v) - clock.StartUnix;
            return v.ToNumber();
        }
    }

    /// <summary>Alarm states and equipment fields from a live snapshot or scenario.</summary>
    public static class LiveState
    {
        /// <summary>Set the alarms in ``alarms``.</summary>
        public static (int set, List<string> problems) SetAlarms(
            World w,
            JsonValue? alarms,
            string by,
            IEnumerable<string>? normal = null
        )
        {
            var problems = new List<string>();
            int n = 0;
            var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (alarms != null && alarms.Kind == JsonKind.Object)
            {
                foreach (var kv in alarms.Pairs)
                {
                    named.Add(kv.Key);
                    // a page case's alarm the recorder could not read
                    if (kv.Value.Kind == JsonKind.Object && kv.Value.Has("error"))
                        continue;
                    AlarmSet s;
                    try
                    {
                        s = AlarmSet.FromJson(kv.Value, w.Clock);
                    }
                    catch (Exception e)
                        when (e is FormatException || e is OverflowException || e is InvalidOperationException
                        )
                    {
                        problems.Add(kv.Key + ": " + e.Message);
                        continue;
                    }
                    if (w.Alarms.SetState(kv.Key, s, by) != 0)
                        problems.Add(kv.Key + ": no such alarm");
                    else
                        n++;
                }
            }
            if (normal != null)
            {
                var off = new AlarmSet
                {
                    On = false,
                    Ack = true,
                    Disabled = false,
                    State = 0,
                };
                foreach (string tag in normal)
                {
                    if (named.Contains(tag))
                        continue;
                    if (w.Alarms.SetState(tag, off, by) != 0)
                        problems.Add(tag + ": no such alarm");
                    else
                        n++;
                }
            }
            return (n, problems);
        }

        /// <summary>Set equipment runtime fields.</summary>
        public static (int set, List<string> problems) SetEquipment(World w, JsonValue? equipment)
        {
            var problems = new List<string>();
            int n = 0;
            if (equipment == null || equipment.Kind != JsonKind.Object)
                return (0, problems);
            foreach (var kv in equipment.Pairs)
            {
                if (kv.Value.Kind != JsonKind.Object)
                {
                    problems.Add(kv.Key + ": not an object of fields");
                    continue;
                }
                foreach (var f in kv.Value.Pairs)
                {
                    if (f.Value.Kind == JsonKind.Object || f.Value.Kind == JsonKind.Array || f.Value.IsNull)
                        continue;
                    int err = w.Equipment.SetRuntime(kv.Key, f.Key, f.Value.Text);
                    if (err == 289)
                    {
                        problems.Add(kv.Key + ": no such equipment");
                        break;
                    }
                    if (err != 0)
                        problems.Add(
                            kv.Key
                                + "."
                                + f.Key
                                + ": not a runtime field ("
                                + string.Join(", ", EquipDb.RuntimeFieldNames)
                                + ")"
                        );
                    else
                        n++;
                }
            }
            return (n, problems);
        }

        /// <summary>An alarm's state as JSON (the ctsim/setAlarm answer): on, ack, disabled,
        /// state (the .State value), onTime / time (ISO 8601 UTC) when it has changed.</summary>
        public static JsonValue AlarmJson(World w, AlarmDef a)
        {
            var o = JsonValue.NewObject();
            var st = w.Alarms.PeekState(a);
            o.Set("tag", a.Tag);
            o.Set("on", JsonValue.Of(st != null && st.On ? 1 : 0));
            o.Set("ack", JsonValue.Of(st == null || st.Ack ? 1 : 0));
            o.Set("disabled", JsonValue.Of(st != null && st.Disabled ? 1 : 0));
            o.Set("state", JsonValue.Of(w.Alarms.StateValue(a)));
            if (st != null && st.Transitions != 0)
            {
                o.Set("onTime", Iso(w.Clock.FiletimeAt(st.OnTime)));
                o.Set("time", Iso(w.Clock.FiletimeAt(st.Time)));
            }
            return o;
        }

        private static string Iso(ulong filetime) =>
            DateTime
                .FromFileTimeUtc((long)filetime)
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }
}
