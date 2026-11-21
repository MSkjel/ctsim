using System;
using System.Collections.Generic;
using System.IO;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>ServerGetProperty for the one simulated process.</summary>
    public static class ServerProps
    {
        private const int ServerNotFound = 418,
            NoServer = 281,
            General = 256,
            ClusterInactive = 405;

        /// <summary>The compile time stored in a compiled table's header (Unix seconds), or 0.</summary>
        internal static long CompileTime(string path)
        {
            try
            {
                if (!WinPath.IsFile(path))
                    return 0;
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (f.Length < 0x10A)
                        return 0;
                    f.Position = 0x106;
                    var b = new byte[4];
                    if (f.Read(b, 0, 4) != 4)
                        return 0;
                    return (int)BitConverter.ToUInt32(b, 0);
                }
            }
            catch (IOException)
            {
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return 0;
            }
        }

        private static string Rdb(SimHost h, string name) => WinPath.Join(h.World!.Unit.RootDir, name);

        /// <summary>The server's answer to a property, null = 274.</summary>
        private static string? Answer(SimHost h, Servers.ServerDef s, string prop)
        {
            int type =
                C.IEq(s.Type, "Alarm") ? 1
                : C.IEq(s.Type, "Trend") ? 2
                : C.IEq(s.Type, "Report") ? 4
                : 0;
            if (type == 0)
                return null;
            string table =
                type == 1 ? "_ALMSRV.RDB"
                : type == 2 ? "_TRNSRV.RDB"
                : "_REPSRV.RDB";
            if (C.IEq(prop, "RDBMemTime") || C.IEq(prop, "RDBDiskTime"))
                return C.Inv(CompileTime(Rdb(h, table)));
            if (C.IEq(prop, "LibRDBMemTime") || C.IEq(prop, "LibRDBDiskTime"))
                return C.Inv(CompileTime(Rdb(h, "_LIBRARY.RDB")));
            if (C.IEq(prop, "LastReloadError") || C.IEq(prop, "ReloadStatus"))
                return "0";
            if (C.IEq(prop, "ReloadProgress"))
                return "0";
            if (C.IEq(prop, "SyncStatus"))
                return type == 4 ? null : "1";
            if (C.IEq(prop, "SyncProgress"))
                return type == 4 ? null : "0";
            if (C.IEq(prop, "SchedulingDiskTime"))
                return type == 4 ? "0" : null;
            if (C.IEq(prop, "ServerStartState"))
                return type == 1 ? "1" : null;
            return null;
        }

        [Builtin(
            "ServerGetProperty",
            Doc = "1192415",
            Blocking = true,
            Errors = "274 an empty server name with a type other than Alarm / Report / Trend, an unknown property, or a property the server type has not (raised, ''); 418 no server of that name (raised, ''); with an empty server name: 256 no such cluster, 405 the cluster is inactive (ClusterDeactivate), 281 no server of the type in it (raised, ''); 282 in foreground code (raised, '')",
            Notes = "A server by name (sCluster optional), or with sServer '' the cluster's first server of the type sType ('Alarm', 'Report', 'Trend'; the corpus: ServerGetProperty(\"\", \"SchedulingDiskTime\", sCluster, \"Report\")). The answers of the one simulated process: RDBDiskTime / RDBMemTime the compile time of the server's compiled table (_ALMSRV / _TRNSRV / _REPSRV.RDB, header u32 0x106: guess on the table), LibRDBDiskTime / LibRDBMemTime that of _LIBRARY.RDB (the same: nothing is reloaded), LastReloadError / ReloadStatus / ReloadProgress 0, SyncStatus 1 (no redundant peer) and SyncProgress 0 for alarm and trend servers, ServerStartState 1 for alarm servers, SchedulingDiskTime 0 for report servers (no scheduling file: guess); every value the server's '%d'. Waits one turn."
        )]
        private static Value ServerGetProperty(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("ServerGetProperty");
            string name = CitectText.Trim(A.S(a.Get(0))),
                prop = CitectText.Trim(A.S(a.Get(1)));
            string cluster = a.Count > 2 ? CitectText.Trim(A.S(a.Get(2))) : "",
                type = a.Count > 3 ? CitectText.Trim(A.S(a.Get(3))) : "";
            var all = Servers.All(h);
            Servers.ServerDef? s = null;
            if (name.Length == 0)
            {
                if (!C.IEq(type, "Alarm") && !C.IEq(type, "Report") && !C.IEq(type, "Trend"))
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.Empty;
                }
                var cls = Servers.Clusters(h);
                string? cl =
                    cluster.Length == 0 ? (cls.Count > 0 ? cls[0] : null) : cls.Find(x => C.IEq(x, cluster));
                if (cl == null)
                {
                    W.Raise(t, General);
                    return W.Empty;
                }
                if (!RestTail.IsActive(h, cl))
                {
                    W.Raise(t, ClusterInactive); // a known cluster whose (active) is 0
                    return W.Empty;
                }
                s = all.Find(x => C.IEq(x.Type, type) && (x.Cluster.Length == 0 || C.IEq(x.Cluster, cl)));
                if (s == null)
                {
                    W.Raise(t, NoServer);
                    return W.Empty;
                }
            }
            else
            {
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
                string n = name,
                    c = cluster;
                s = all.Find(x =>
                    C.IEq(x.Name, n) && (c.Length == 0 || x.Cluster.Length == 0 || C.IEq(x.Cluster, c))
                );
                if (s == null)
                {
                    W.Raise(t, ServerNotFound);
                    return W.Empty;
                }
            }
            if (t.Foreground)
            {
                W.Raise(t, Codes.FgBlock);
                return W.Empty;
            }
            var server = s;
            return W.BlockOneTurn(t, "ServerGetProperty", () =>
            {
                string? v = Answer(h, server, prop);
                if (v == null)
                {
                    W.Raise(t, Codes.InvalidArg);
                    return W.Empty;
                }
                return W.S(v);
            });
        }
    }
}
