using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>
    /// Clusters, servers and I/O devices. The simulator is one process acting as every server.
    /// </summary>
    public static class Servers
    {
        internal sealed class ServerDef
        {
            public string Type = ""; // Alarm, Trend, Report, IOServer
            public string Name = "",
                Cluster = "",
                Mode = "",
                NetAddr = "",
                Port = "",
                Comment = "";
        }

        /// <summary>Each cluster once (the first spelling), compile order; read once a process
        /// (the page expressions call the cluster and device functions every scan, and the
        /// unit's table rows are found and stat'ed on every Unit.Rows call).</summary>
        internal static List<string> Clusters(SimHost h)
        {
            var d = ProcessData.Of(h);
            if (d.ClusterList != null)
                return d.ClusterList;
            var o = new List<string>();
            var w = h.World;
            if (w == null)
                return o;
            foreach (string c in w.Unit.Clusters())
            {
                string n = CitectText.Trim(c);
                if (n.Length > 0 && !o.Exists(x => C.IEq(x, n)))
                    o.Add(n);
            }
            d.ClusterList = o;
            return o;
        }

        private static string? FindCluster(SimHost h, string name)
        {
            foreach (string c in Clusters(h))
                if (C.IEq(c, CitectText.Trim(name)))
                    return c;
            return null;
        }

        internal static List<ServerDef> All(SimHost h)
        {
            var w = h.World;
            var o = new List<ServerDef>();
            if (w == null)
                return o;
            var rows = h.TableRows("<servers>", () => new List<Dictionary<string, string>>());
            if (rows.Count == 0)
            {
                foreach (
                    var (table, type) in new[]
                    {
                        ("almsrv.dbf", "Alarm"),
                        ("trnsrv.dbf", "Trend"),
                        ("repsrv.dbf", "Report"),
                    }
                )
                foreach (var (_, r) in w.Unit.Rows(table))
                {
                    string n = CitectText.Trim(r.Get("NAME"));
                    if (n.Length == 0)
                        continue;
                    rows.Add(
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["TYPE"] = type,
                            ["NAME"] = n,
                            ["CLUSTER"] = CitectText.Trim(r.Get("CLUSTER")),
                            ["MODE"] = CitectText.Trim(r.Get("MODE")),
                            ["NETADDR"] = CitectText.Trim(r.Get("NETADDR")),
                            ["PORT"] = CitectText.Trim(r.Get("PORT")),
                            ["COMMENT"] = CitectText.Trim(r.Get("COMMENT")),
                        }
                    );
                }
                foreach (var io in IoServers(h))
                    rows.Add(io);
            }
            foreach (var r in rows)
                o.Add(
                    new ServerDef
                    {
                        Type = r["TYPE"],
                        Name = r["NAME"],
                        Cluster = r["CLUSTER"],
                        Mode = r["MODE"],
                        NetAddr = r["NETADDR"],
                        Port = r["PORT"],
                        Comment = r["COMMENT"],
                    }
                );
            return o;
        }

        /// <summary>The I/O servers: ServerConfiguration.json of the compiled project (written
        /// by the compiler: Clusters[].Servers[] with ServiceType 'IO'), else the SERVER names of
        /// units.dbf in the first cluster.</summary>
        private static List<Dictionary<string, string>> IoServers(SimHost h)
        {
            var w = h.World!;
            var o = new List<Dictionary<string, string>>();
            string json = WinPath.Join(w.Unit.RootDir, "ServerConfiguration.json");
            try
            {
                if (WinPath.IsFile(json))
                {
                    var root = JsonValue.Parse(File.ReadAllText(json));
                    var cls = root.Get("Clusters");
                    if (cls != null && cls.Kind == JsonKind.Array)
                        foreach (var c in cls.Items)
                        {
                            string cn = c.Get("Name")?.Str ?? "";
                            var ss = c.Get("Servers");
                            if (ss == null || ss.Kind != JsonKind.Array)
                                continue;
                            foreach (var s in ss.Items)
                            {
                                if (!C.IEq(s.Get("ServiceType")?.Str ?? "", "IO"))
                                    continue;
                                var port = s.Get("Port");
                                o.Add(
                                    new Dictionary<string, string>(StringComparer.Ordinal)
                                    {
                                        ["TYPE"] = "IOServer",
                                        ["NAME"] = s.Get("Name")?.Str ?? "",
                                        ["CLUSTER"] = cn,
                                        ["MODE"] = "Primary",
                                        ["NETADDR"] = s.Get("HostName")?.Str ?? "",
                                        ["PORT"] =
                                            port == null ? ""
                                            : port.Kind == JsonKind.String ? port.Str
                                            : port.Dump(),
                                        ["COMMENT"] = "",
                                    }
                                );
                            }
                        }
                    if (o.Count > 0)
                        return o;
                }
            }
            catch (Exception)
            {
                o.Clear();
            }
            var cl = Clusters(h);
            string first = cl.Count > 0 ? cl[0] : "";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, r) in w.Unit.Rows("units.dbf"))
            {
                string n = CitectText.Trim(r.Get("SERVER"));
                if (n.Length > 0 && seen.Add(n))
                    o.Add(
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["TYPE"] = "IOServer",
                            ["NAME"] = n,
                            ["CLUSTER"] = first,
                            ["MODE"] = "Primary",
                            ["NETADDR"] = "",
                            ["PORT"] = "",
                            ["COMMENT"] = "",
                        }
                    );
            }
            return o;
        }

        /// <summary>An I/O device of units.dbf by name (a ',P' / ',S' suffix ignored) or number.</summary>
        internal static DbfRowView? IoDevice(SimHost h, string device, string cluster)
        {
            var w = h.World;
            if (w == null)
                return null;
            string d = CitectText.Trim(device);
            int comma = d.IndexOf(',');
            if (comma >= 0)
                d = CitectText.Trim(d.Substring(0, comma));
            if (d.Length == 0)
                return null;
            bool number = CitectText.IsDigits(d);
            var ds = ProcessData.Of(h);
            if (ds.UnitsByName == null || ds.UnitsByNumber == null)
            {
                // the first row of each NAME (C-locale case) and of each NUMBER text, compile order
                var byName = new Dictionary<string, DbfRow>(StringComparer.Ordinal);
                var byNumber = new Dictionary<string, DbfRow>(StringComparer.Ordinal);
                foreach (var (_, r) in w.Unit.Rows("units.dbf"))
                {
                    string n = A.CLower(CitectText.Trim(r.Get("NAME")));
                    if (!byName.ContainsKey(n))
                        byName[n] = r;
                    string k = CitectText.Trim(r.Get("NUMBER"));
                    if (!byNumber.ContainsKey(k))
                        byNumber[k] = r;
                }
                ds.UnitsByName = byName;
                ds.UnitsByNumber = byNumber;
            }
            DbfRow? hit;
            if (
                number
                    ? ds.UnitsByNumber.TryGetValue(C.Inv(C.Atol(d)), out hit)
                    : ds.UnitsByName.TryGetValue(A.CLower(d), out hit)
            )
                return new DbfRowView(hit);
            return null;
        }

        /// <summary>A units.dbf row (trimmed field reads).</summary>
        internal sealed class DbfRowView
        {
            private readonly DbfRow _r;

            public DbfRowView(DbfRow r)
            {
                _r = r;
            }

            public string this[string f] => CitectText.Trim(_r.Get(f));
        }

        private static int TypeBit(string type) =>
            type == "Alarm" ? 1
            : type == "Trend" ? 2
            : type == "Report" ? 4
            : type == "IOServer" ? 8
            : 0;

        // ------------------------------------------------------------------ clusters
        [Builtin(
            "ClusterFirst",
            Doc = "1070591",
            Notes = "The first cluster of cluster.dbf (the unit's, compile order); '' when none."
        )]
        private static Value ClusterFirst(SimHost h, CicodeTask t, ArgList a)
        {
            var cl = Clusters(h);
            return W.S(cl.Count > 0 ? C.Cut(cl[0]) : "");
        }

        [Builtin(
            "ClusterNext",
            Doc = "1070594",
            Notes = "The cluster after the named one (without case); '' at the end or for an unknown name."
        )]
        private static Value ClusterNext(SimHost h, CicodeTask t, ArgList a)
        {
            var cl = Clusters(h);
            string n = CitectText.Trim(A.S(a.Get(0)));
            for (int i = 0; i < cl.Count; i++)
                if (C.IEq(cl[i], n))
                    return W.S(i + 1 < cl.Count ? C.Cut(cl[i + 1]) : "");
            return W.Empty;
        }

        [Builtin(
            "ClusterStatus",
            Doc = "1070597",
            Errors = "274 a server type other than 1, 2, 4, 8 (raised, -1)",
            Notes = "-2 an unknown cluster, -1 no server of that type in it, else 2 (active and online: every simulated server is); 0 for a cluster ClusterDeactivate made inactive."
        )]
        private static Value ClusterStatus(SimHost h, CicodeTask t, ArgList a)
        {
            string c = A.S(a.Get(0));
            long type = A.Short(A.I(a.Get(1)));
            if (type != 1 && type != 2 && type != 4 && type != 8)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            string? cl = FindCluster(h, c);
            if (cl == null)
                return W.I(-2);
            foreach (var s in All(h))
                if (TypeBit(s.Type) == type && C.IEq(s.Cluster, cl))
                    return W.I(RestTail.IsActive(h, cl) ? 2 : 0); // ClusterDeactivate
            return W.I(-1);
        }

        [Builtin(
            "ClusterIsActive",
            Doc = "1070593",
            Errors = "274 an unknown cluster (raised, 0)",
            Notes = "1: every configured cluster is active, unless ClusterDeactivate made it inactive (0)."
        )]
        private static Value ClusterIsActive(SimHost h, CicodeTask t, ArgList a)
        {
            string? cl = FindCluster(h, A.S(a.Get(0)));
            if (cl == null)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(0);
            }
            return W.I(RestTail.IsActive(h, cl) ? 1 : 0); // ClusterDeactivate
        }

        [Builtin(
            "ClusterServerTypes",
            Doc = "1070595",
            Notes = "OR of 1 alarm, 2 trend, 4 report, 8 I/O server configured in the cluster; 0 for an unknown cluster."
        )]
        private static Value ClusterServerTypes(SimHost h, CicodeTask t, ArgList a)
        {
            string? cl = FindCluster(h, A.S(a.Get(0)));
            if (cl == null)
                return W.I(0);
            int bits = 0;
            foreach (var s in All(h))
                if (C.IEq(s.Cluster, cl))
                    bits |= TypeBit(s.Type);
            return W.I(bits);
        }

        // ------------------------------------------------------------------ servers
        private static string Info(SimHost h, CicodeTask t, string name, long type, string component)
        {
            string n = CitectText.Trim(name);
            var ds = ProcessData.Of(h);
            if (C.IEq(n, "Alarm") || C.IEq(n, "Trend") || C.IEq(n, "Report"))
            {
                switch (type)
                {
                    case 0:
                        return "1"; // active
                    case 1:
                        return "1"; // clients: this process
                    case 2:
                        return "0"; // attached to the primary
                    case 3:
                        return "1"; // connected
                    case 4:
                        return C.IEq(n, "Alarm") ? "2" : ""; // alarm DB main
                }
                return "";
            }
            if (C.IEq(n, "Client"))
            {
                if (type == 0)
                    return ds.ComputerName;
                if (component.Length > 0 && (type == 1 || type == 2))
                {
                    if (C.IEq(component, "IOServer"))
                        return "";
                    if (type == 2)
                        return "";
                    foreach (var s in All(h))
                        if (C.IEq(s.Type, component))
                            return s.Name;
                    return "";
                }
                if (component.Length > 0 && type == 3)
                    return "Citect.ini";
                return "";
            }
            if (C.IEq(n, "Server"))
            {
                if (type == 0)
                {
                    foreach (var s in All(h))
                        if (component.Length > 0 && C.IEq(s.Type, component))
                            return s.Name;
                    return "";
                }
                return type == 1 ? "1" : "";
            }
            if (CitectText.IsDigits(n))
            {
                long k = C.Atol(n) % 100;
                long ty = type % 100;
                if (k != 0 && k != 1)
                    return "";
                switch (ty)
                {
                    case 0:
                        return component.Length > 0 ? component : "Alarm";
                    case 1:
                        return h.World?.Users.Session.Name ?? "";
                    case 2:
                        return ds.ComputerName;
                    case 3:
                        return C.Inv((long)Math.Truncate(h.World?.Users.Session.LoginTime ?? 0));
                    case 4:
                    case 5:
                    case 6:
                    case 7:
                        return "0";
                    case 8:
                        return "0";
                }
                return "";
            }
            W.Raise(t, Codes.InvalidArg);
            return "";
        }

        [Builtin(
            "ServerInfo",
            Doc = "1192416",
            Errors = "274 an unknown name (raised, '')",
            Notes = "One process that is every server: Alarm / Trend / Report 0 active '1', 1 clients '1', 2 primary '0', 3 connected '1', 4 (Alarm) '2' main; Client 0 the computer name; Server 1 '1'; a client number 0 / 1: 0 'Alarm', 1 the user, 2 the computer, 3 the login time, others '0'."
        )]
        private static Value ServerInfo(SimHost h, CicodeTask t, ArgList a) =>
            W.S(C.Cut(Info(h, t, A.S(a.Get(0)), A.Short(A.I(a.Get(1))), "")));

        [Builtin(
            "ServerInfoEx",
            Doc = "1192417",
            Blocking = true,
            Errors = "274 an unknown name or a component other than '', Alarm, Trend, Report, IOServer (raised, ''); 282 in foreground code (raised, '')",
            Notes = "As ServerInfo with the component's answers (Client 1 the component's server name, 3 'Citect.ini'; Server 0 the component's server name); waits one turn."
        )]
        private static Value ServerInfoEx(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.S(a.Get(0));
            long type = A.Short(A.I(a.Get(1)));
            string comp = CitectText.Trim(A.S(a.Get(2)));
            if (
                comp.Length > 0
                && !(
                    C.IEq(comp, "Alarm")
                    || C.IEq(comp, "Trend")
                    || C.IEq(comp, "Report")
                    || C.IEq(comp, "IOServer")
                )
            )
            {
                W.Raise(t, Codes.InvalidArg);
                return W.Empty;
            }
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.Empty;
            }
            return W.BlockOneTurn(t, "ServerInfoEx", () => W.S(C.Cut(Info(h, t, name, type, comp))));
        }

        [Builtin(
            "ServerIsOnline",
            Doc = "1192418",
            Errors = "418 no such server (raised, 0); 282 a remote check (bLocal 0) in foreground code (raised, 0)",
            Notes = "1 for a configured server (in the cluster when one is named) that is online, 0 while it is offline (a scenario's setServer step, or the seconds of a ServerRestart: World.ServerStates); a remote check (bLocal 0, default) waits one turn."
        )]
        private static Value ServerIsOnline(SimHost h, CicodeTask t, ArgList a)
        {
            string name = CitectText.Trim(A.S(a.Get(0)));
            string cl = a.Count > 1 ? CitectText.Trim(A.S(a.Get(1))) : "";
            bool local = a.Count > 2 && A.Short(A.I(a.Get(2))) != 0;
            bool found = false;
            foreach (var s in All(h))
                if (C.IEq(s.Name, name) && (cl.Length == 0 || C.IEq(s.Cluster, cl)))
                    found = true;
            if (!found)
            {
                W.Raise(t, 418);
                return W.I(0);
            }
            var w = h.World;
            int Online() => w == null || World.ServerStates.Of(w).IsOnline(name, w.Clock.Now()) ? 1 : 0;
            if (local)
                return W.I(Online());
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.I(0);
            }
            return W.BlockOneTurn(t, "ServerIsOnline", () => W.I(Online()));
        }

        private static readonly string[] ServerFields =
        {
            "NAME",
            "TYPE",
            "COMMENT",
            "CLUSTER",
            "MODE",
            "NETADDR",
            "PORT",
        };

        [Builtin(
            "ServerBrowseOpen",
            Doc = "1192409",
            Blocking = true,
            Errors = "282 in foreground code (raised, -1); 274 an unknown filter field (raised, -1)",
            Notes = "The alarm, trend, report and I/O servers (NAME, TYPE: Alarm / Trend / Report / IOServer, COMMENT, CLUSTER, MODE, NETADDR, PORT), the Browse filter rules, sorted by NAME; sClusters limits the clusters."
        )]
        private static Value ServerBrowseOpen(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("ServerBrowseOpen");
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.I(-1);
            }
            var fl = Browse.Compile(A.S(a.Get(0)), "NAME");
            if (!Browse.FieldsKnown(fl, ServerFields))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            var cl = Browse.FieldSet(A.S(a.Get(2)));
            var rows = new List<Dictionary<string, string>>();
            foreach (var s in All(h))
            {
                if (cl != null && !cl.Contains(s.Cluster.ToUpperInvariant()))
                    continue;
                rows.Add(
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["NAME"] = s.Name,
                        ["TYPE"] = s.Type,
                        ["COMMENT"] = s.Comment,
                        ["CLUSTER"] = s.Cluster,
                        ["MODE"] = s.Mode,
                        ["NETADDR"] = s.NetAddr,
                        ["PORT"] = s.Port,
                    }
                );
            }
            return Browse.OpenRows(h, t, "Server", rows, fl, A.S(a.Get(1)), Browse.SortSpec("", "NAME"));
        }

        [Builtin(
            "ServerBrowseFirst",
            Doc = "1192410",
            Blocking = true,
            Errors = "410 bad session; 412 no records; 282 in foreground code (returned)"
        )]
        private static Value ServerBrowseFirst(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "Server", 0, true);

        [Builtin(
            "ServerBrowseNext",
            Doc = "1192411",
            Blocking = true,
            Errors = "410 bad session; 412 past the last record; 282 in foreground code (returned)"
        )]
        private static Value ServerBrowseNext(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "Server", 1, true);

        [Builtin(
            "ServerBrowsePrev",
            Doc = "1192412",
            Blocking = true,
            Errors = "410 bad session; 412 before the first record; 282 in foreground code (returned)"
        )]
        private static Value ServerBrowsePrev(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Move(h, t, a, "Server", -1, true);

        [Builtin(
            "ServerBrowseGetField",
            Doc = "1192413",
            Errors = "410 bad session; 412 no current record; 413 unknown field (raised, '' returned)"
        )]
        private static Value ServerBrowseGetField(SimHost h, CicodeTask t, ArgList a) =>
            Browse.GetField(h, t, a, "Server");

        [Builtin("ServerBrowseNumRecords", Doc = "1192414", Errors = "410 bad session (raised, -1)")]
        private static Value ServerBrowseNumRecords(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Count(h, t, a, "Server");

        [Builtin("ServerBrowseClose", Doc = "1192408", Errors = "410 bad session (returned)")]
        private static Value ServerBrowseClose(SimHost h, CicodeTask t, ArgList a) =>
            Browse.Close(h, t, a, "Server");

        [Builtin(
            "ServiceGetList",
            Doc = "1188615",
            Notes = "'<type>.<cluster>.<name>' of every server of the unit (IOServer, Alarm, Trend, Report, in that order), then 'Client'; 255 characters at most."
        )]
        private static Value ServiceGetList(SimHost h, CicodeTask t, ArgList a)
        {
            var sb = new StringBuilder();
            var all = All(h);
            foreach (string type in new[] { "IOServer", "Alarm", "Trend", "Report" })
            foreach (var s in all)
                if (s.Type == type)
                    sb.Append(type).Append('.').Append(s.Cluster).Append('.').Append(s.Name).Append(',');
            sb.Append("Client");
            return W.S(C.Cut(sb.ToString()));
        }

        [Builtin(
            "IODeviceInfo",
            Doc = "1106224",
            Notes = "A units.dbf device by name (',P' / ',S' ignored) or number: 0 NAME, 1 PROTOCOL, 2 ADDR, 3 / 10 '1' (running), 4..9 '0', 11 NUMBER, 12 SERVER, 13 PORT, 14 MODE, 15 COMMENT, 16 / 17 SERVER, 18 '1'; other types '0'. An unknown device '', for type 3 '0' (type 3 is _itoa of the client unit's status word, 0 for a device it does not find; an empty device name ''). The cluster and server arguments are not checked (the runtime raises for an unknown cluster: code not read)."
        )]
        private static Value IODeviceInfo(SimHost h, CicodeTask t, ArgList a)
        {
            var d = IoDevice(h, A.S(a.Get(0)), a.Count > 2 ? A.S(a.Get(2)) : "");
            long type = A.Short(A.I(a.Get(1)));
            if (d == null)
                return type == 3 && A.SkipSpace(A.S(a.Get(0))).Length > 0 ? W.S("0") : W.Empty;
            switch (type)
            {
                case 0:
                    return W.S(C.Cut(d["NAME"], 254));
                case 1:
                    return W.S(C.Cut(d["PROTOCOL"], 254));
                case 2:
                    return W.S(C.Cut(d["ADDR"], 254));
                case 3:
                case 10:
                case 18:
                    return W.S("1");
                case 11:
                    return W.S(d["NUMBER"]);
                case 12:
                case 16:
                case 17:
                    return W.S(C.Cut(d["SERVER"], 254));
                case 13:
                    return W.S(C.Cut(d["PORT"], 254));
                case 14:
                    return W.S(C.Cut(d["MODE"], 254));
                case 15:
                    return W.S(C.Cut(d["COMMENT"], 254));
            }
            return W.S("0");
        }

        [Builtin(
            "KerCmd",
            Status = BuiltinStatus.Stub,
            Returns = "0",
            Doc = "1188596",
            Effects = "logs the kernel command",
            Notes = "No kernel window in the simulator: the command is logged and 0 returned."
        )]
        private static Value KerCmd(SimHost h, CicodeTask t, ArgList a)
        {
            h.Note(t, "KerCmd(" + A.S(a.Get(0)) + "): " + A.S(a.Get(1)));
            return W.I(0);
        }
    }
}
