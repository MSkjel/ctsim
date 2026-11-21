using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.World
{
    /// <summary>Runs a list of API operations against a World.</summary>
    public static class WorldOps
    {
        private static JsonValue Jv(in Value v)
        {
            var a = JsonValue.NewArray();
            a.Add(JsonValue.Of((long)v.T));
            a.Add(JsonValue.From(WorldValues.Payload(v)));
            a.Add(JsonValue.Of(v.Q));
            a.Add(JsonValue.Of(v.Ts));
            a.Add(JsonValue.Of(v.Vts));
            a.Add(JsonValue.Of(v.Qts));
            return a;
        }

        private static JsonValue Arr(params JsonValue[] xs) => JsonValue.Array(xs);

        private static JsonValue Ints(IEnumerable<int> s)
        {
            var l = new List<int>(s);
            l.Sort();
            var a = JsonValue.NewArray();
            foreach (int x in l)
                a.Add(JsonValue.Of((long)x));
            return a;
        }

        private static JsonValue Strs(IEnumerable<string> s)
        {
            var a = JsonValue.NewArray();
            foreach (string x in s)
                a.Add(JsonValue.Of(x));
            return a;
        }

        private static int? Index(JsonValue o)
        {
            var v = o.Get("index");
            return v == null || v.IsNull ? (int?)null : (int)v.Long;
        }

        private static string Str(JsonValue o, string key) => o.Get(key)?.Str ?? "";

        /// <summary>The code of a failed operation: bad-value (a value or an argument it cannot
        /// take), overflow, not-found (an unknown operation or key), failed (anything else).</summary>
        private static string ErrorCode(Exception e)
        {
            switch (e)
            {
                case FormatException _:
                    return "bad-value";
                case ArgumentException _:
                    return "bad-value";
                case OverflowException _:
                    return "overflow";
                case KeyNotFoundException _:
                    return "not-found";
                default:
                    return "failed";
            }
        }

        private static Value ValueOf(JsonValue spec)
        {
            int t = (int)spec["t"].Long;
            JsonValue p = spec["v"];
            if (Types.IsRealType(t))
                return Value.RealOf(t, p.ToNumber());
            if (t == Types.String)
                return Value.Str(p.Str);
            return Value.Int(t, (long)p.BigValue);
        }

        public static void Run(
            string projectDir,
            string opsPath,
            string outPath,
            string start = "2026-10-01T08:00:00Z"
        )
        {
            var w = new World(projectDir, start: start);
            var sw = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
            var tw = new TraceWriter(stream: sw, project: w.Project, clock: w.Clock, engine: "x");
            w.AttachTrace(tw);
            var lines = new List<string>();
            foreach (string line in File.ReadAllLines(opsPath, Encoding.UTF8))
            {
                if (line.Length == 0)
                    continue;
                var o = JsonValue.Parse(line);
                string k = o["op"].Str;
                var rec = JsonValue.NewObject();
                rec.Set("op", JsonValue.Of(k));
                try
                {
                    rec.Set("res", One(w, k, o));
                }
                catch (Exception e)
                {
                    var err = JsonValue.NewObject();
                    err.Set("code", JsonValue.Of(ErrorCode(e)));
                    err.Set("message", JsonValue.Of(e.Message));
                    rec.Set("error", err);
                }
                lines.Add(rec.Dump());
            }
            using var f = new StreamWriter(outPath, false, new UTF8Encoding(false)) { NewLine = "\n" };
            foreach (string l in lines)
            {
                f.Write(l);
                f.Write('\n');
            }
            f.Write("#trace\n");
            f.Write(sw.ToString());
        }

        private static JsonValue One(World w, string k, JsonValue o)
        {
            switch (k)
            {
                case "start":
                    w.Start();
                    return JsonValue.Null;
                case "read":
                {
                    var r = WorldValues.Ref(
                        Str(o, "name"),
                        Str(o, "element"),
                        Str(o, "item"),
                        Str(o, "cluster"),
                        o["light"].Long
                    );
                    var t = new SimpleTask(TaskFlags.Default);
                    var v = w.Tags.Read(r, t, Index(o));
                    return Arr(Jv(v), JsonValue.Of((long)t.TagError));
                }
                case "write":
                {
                    var r = WorldValues.Ref(
                        Str(o, "name"),
                        Str(o, "element"),
                        Str(o, "item"),
                        Str(o, "cluster")
                    );
                    var t = new SimpleTask((int)o["flags"].Long);
                    int code = w.Tags.Write(r, ValueOf(o["value"]), t, Index(o));
                    return Arr(JsonValue.Of((long)code), JsonValue.Of((long)t.TagError));
                }
                case "set":
                {
                    var q = o.Get("quality");
                    return JsonValue.Of(
                        (long)
                            w.Tags.Set(
                                Str(o, "name"),
                                o.Get("value") ?? JsonValue.Null,
                                q == null || q.IsNull ? null : q,
                                by: "scenario",
                                element: Str(o, "element"),
                                index: Index(o)
                            )
                    );
                }
                case "advance":
                    return JsonValue.Of((long)w.Clock.Advance(o["dt"].ToNumber()));
                case "ack":
                    return JsonValue.Of((long)w.Alarms.Acknowledge(Str(o, "tag"), "user"));
                case "ackall":
                    return JsonValue.Of((long)w.Alarms.AcknowledgeAll(Str(o, "filter"), "user"));
                case "disable":
                {
                    var u = o.Get("until");
                    return JsonValue.Of(
                        (long)
                            w.Alarms.Disable(
                                Str(o, "tag"),
                                u == null || u.IsNull ? (double?)null : u.ToNumber(),
                                "",
                                "user"
                            )
                    );
                }
                case "enable":
                    return JsonValue.Of((long)w.Alarms.Enable(Str(o, "tag"), "user"));
                case "hw":
                {
                    var hw = w.Alarms.RaiseHardware(o["code"].Long, name: Str(o, "name"));
                    return Arr(JsonValue.Of(hw.Code), JsonValue.Of(hw.Desc), JsonValue.Of((long)hw.Count));
                }
                case "count":
                    return JsonValue.Of(
                        (long)
                            w.Alarms.Count(
                                (int)o["kind"].Long,
                                Str(o, "filter"),
                                Str(o, "equipment"),
                                o["ref"].Bool
                            )
                    );
                case "active":
                    return JsonValue.Of(w.Alarms.Active((int)o["mode"].Long));
                case "highest":
                    return JsonValue.Of(
                        w.Alarms.HighestPriority((int)o["kind"].Long, Str(o, "filter"), Str(o, "equipment"))
                    );
                case "login":
                    return JsonValue.Of((long)w.Users.Login(Str(o, "name")));
                case "logout":
                    return JsonValue.Of((long)w.Users.Logout());
                case "loginas":
                {
                    var pr = o.Get("priv");
                    var ar = o.Get("areas");
                    var s = w.Users.LoginAs(
                        "U",
                        "",
                        pr == null || pr.IsNull ? null : pr,
                        ar == null || ar.IsNull ? null : ar
                    );
                    var levels = new List<int>(s.AreaPrivs.Keys);
                    levels.Sort();
                    var ap = JsonValue.NewObject();
                    foreach (int lv in levels)
                        ap.Set(lv.ToString(CultureInfo.InvariantCulture), Ints(s.AreaPrivs[lv]));
                    return Arr(Ints(s.GlobalPrivs), ap, Ints(s.ViewAreas));
                }
                case "priv":
                {
                    string what = Str(o, "what");
                    int p = (int)o["p"].Long,
                        a = (int)o["a"].Long;
                    if (what == "get")
                        return JsonValue.Of((long)w.Users.GetPriv(p, a));
                    if (what == "view")
                        return JsonValue.Of(w.Users.CanView(a));
                    return JsonValue.Of(w.Users.UserInfo(p));
                }
                case "info":
                {
                    var d = w.Tags.Find(Str(o, "name"));
                    return d == null
                        ? JsonValue.Null
                        : Arr(
                            JsonValue.Of(d.Info((int)o["k"].Long)),
                            JsonValue.Of(d.Property(Str(o, "prop")))
                        );
                }
                case "peek":
                {
                    var (v, q, ok) = w.Tags.Peek(Str(o, "name"));
                    return Arr(v, JsonValue.Of(q), JsonValue.Of(ok));
                }
                case "changes":
                    return Strs(w.Tags.ChangesSince(o["serial"].Long));
                case "resolve":
                {
                    var r = w.Tags.Resolve(
                        Str(o, "name"),
                        Str(o, "element"),
                        Str(o, "item"),
                        Str(o, "cluster")
                    );
                    return Arr(
                        JsonValue.Of(r.Found),
                        JsonValue.Of(r.Kind),
                        JsonValue.Of(r.Element),
                        JsonValue.Of(r.Item),
                        JsonValue.Of(r.Prop),
                        r.Index == null ? JsonValue.Null : JsonValue.Of((long)r.Index.Value),
                        JsonValue.Of(r.Via),
                        JsonValue.Of(r.Name)
                    );
                }
                case "state":
                {
                    var a = w.Alarms.Get(Str(o, "tag"));
                    return a == null
                        ? JsonValue.Null
                        : Arr(JsonValue.Of(w.Alarms.StateValue(a)), JsonValue.Of(w.Alarms.AcqError(a)));
                }
                case "prop":
                    return Jv(w.Tags.Get(Str(o, "name")));
                case "final":
                {
                    var listed = new List<string>();
                    var disabled = new List<string>();
                    foreach (var a in w.Alarms.All())
                    {
                        var st = w.Alarms.PeekState(a);
                        if (st != null && st.InList)
                            listed.Add(a.Key);
                        if (st != null && st.Disabled)
                            disabled.Add(a.Key);
                    }
                    listed.Sort(string.CompareOrdinal);
                    disabled.Sort(string.CompareOrdinal);
                    return Arr(
                        Strs(w.Tags.Used),
                        Strs(w.Tags.ChangesSince(0)),
                        JsonValue.Of(w.Clock.Now()),
                        JsonValue.Of(w.Alarms.Generation),
                        Strs(listed),
                        Strs(disabled)
                    );
                }
            }
            throw new KeyNotFoundException("no operation " + CitectText.Quote(k));
        }
    }
}
