using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using CicodeSim.Values;

namespace CicodeSim.Host
{
    /// <summary>One running server as its discovery file describes it
    /// (%LOCALAPPDATA%\cicode-sim\servers\&lt;id&gt;.json).</summary>
    public sealed class ServerEntry
    {
        public const string Format = "ctsim-server/0";

        public string Id = "";

        /// <summary>pipe, unix or tcp (Listener.Transport).</summary>
        public string Transport = Listener.Pipe;

        /// <summary>What a client connects to: \\.\pipe\ctsim.&lt;id&gt;, a socket path or host:port.</summary>
        public string Address = "";
        public int Pid;

        /// <summary>The process start time, FILETIME (UTC, 100 ns since 1601).</summary>
        public long ProcessStart;

        /// <summary>serve or dap.</summary>
        public string Mode = "serve";

        /// <summary>The project folder the server simulates.</summary>
        public string Project = "";
        public string ProjectName = "";

        /// <summary>The compiled project it runs as (the root of the compile unit).</summary>
        public string Root = "";

        /// <summary>Why that root (Project.RootPick: asked, chosen, self, only, lastDatabase,
        /// firstByName, none).</summary>
        public string RootReason = "";

        /// <summary>The roots whose include closure holds the project (by name).</summary>
        public List<string> Roots = new List<string>();

        /// <summary>The User folder of the project's install ('' when the project is not in one).</summary>
        public string User = "";

        /// <summary>The install's data folder (the User folder's parent), '' when unknown.</summary>
        public string Install = "";
        public string Version = "";

        /// <summary>EngineInfo.ProtocolRevision of the server (0 when the entry has none); a client
        /// compares it with the revision it needs before attaching.</summary>
        public int ProtocolRevision = EngineInfo.ProtocolRevision;
        public string Commit = EngineInfo.Commit;
        public string BuildTime = EngineInfo.BuildTime;

        /// <summary>When the server started (ISO 8601 UTC).</summary>
        public string Started = "";

        public JsonValue ToJson()
        {
            var o = JsonValue.NewObject();
            o.Set("format", Format);
            o.Set("id", Id);
            o.Set("transport", Transport);
            o.Set("pipe", Transport == Listener.Pipe ? Address : "");
            o.Set("address", Address);
            o.Set("pid", JsonValue.Of(Pid));
            o.Set("processStart", JsonValue.Of(ProcessStart));
            o.Set("processStartIso", Iso(ProcessStart));
            o.Set("mode", Mode);
            o.Set("project", Project);
            o.Set("projectName", ProjectName);
            o.Set("root", Root);
            o.Set("rootReason", RootReason);
            var roots = JsonValue.NewArray();
            foreach (string r in Roots)
                roots.Add(JsonValue.Of(r));
            o.Set("roots", roots);
            o.Set("user", User);
            o.Set("install", Install);
            o.Set("version", Version);
            o.Set("protocolRevision", JsonValue.Of(ProtocolRevision));
            o.Set("commit", Commit);
            o.Set("buildTime", BuildTime);
            o.Set("started", Started);
            return o;
        }

        public static ServerEntry? FromJson(JsonValue o)
        {
            if (o.Kind != JsonKind.Object || o.Get("format")?.Str != Format)
                return null;
            var e = new ServerEntry
            {
                Id = o.Get("id")?.Str ?? "",
                Transport = o.Get("transport")?.Str ?? Listener.Pipe,
                Address = o.Get("address")?.Str ?? "",
                Pid = (int)(o.Get("pid")?.Long ?? 0),
                ProcessStart = o.Get("processStart")?.Long ?? 0,
                Mode = o.Get("mode")?.Str ?? "",
                Project = o.Get("project")?.Str ?? "",
                ProjectName = o.Get("projectName")?.Str ?? "",
                Root = o.Get("root")?.Str ?? "",
                RootReason = o.Get("rootReason")?.Str ?? "",
                User = o.Get("user")?.Str ?? "",
                Install = o.Get("install")?.Str ?? "",
                Version = o.Get("version")?.Str ?? "",
                ProtocolRevision =
                    o.Get("protocolRevision") is JsonValue pr && pr.FitsLong ? (int)pr.Long : 0,
                Commit = o.Get("commit")?.Str ?? "",
                BuildTime = o.Get("buildTime")?.Str ?? "",
                Started = o.Get("started")?.Str ?? "",
            };
            var rs = o.Get("roots");
            if (rs != null && rs.Kind == JsonKind.Array)
                foreach (var x in rs.Items)
                    if (x.Kind == JsonKind.String)
                        e.Roots.Add(x.Str);
            return e.Id.Length > 0 && e.Pid > 0 ? e : null;
        }

        /// <summary>Does this server simulate ``project`` run as ``root``?</summary>
        public bool Matches(string project, string? root = null)
        {
            bool same;
            try
            {
                string want = Path.GetFullPath(project).TrimEnd('\\', '/');
                same = string.Equals(want, Project.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                same = false;
            }
            if (!same && project.IndexOfAny(new[] { '\\', '/', ':' }) < 0)
                same = string.Equals(project, ProjectName, StringComparison.OrdinalIgnoreCase);
            if (!same)
                return false;
            return string.IsNullOrEmpty(root)
                || string.Equals(root!.Trim(), Root, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>ISO 8601 UTC with milliseconds of a FILETIME ('' for 0).</summary>
        public static string Iso(long filetime)
        {
            if (filetime <= 0)
                return "";
            try
            {
                return DateTime
                    .FromFileTimeUtc(filetime)
                    .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            }
            catch (ArgumentException)
            {
                return "";
            }
        }
    }

    /// <summary>The discovery folder, where running servers announce themselves.</summary>
    public static class Discovery
    {
        /// <summary>Two start times this far apart (100 ns units, 1 s) are another process.</summary>
        public const long StartTolerance = 10_000_000;

        public static string Folder
        {
            get
            {
                string? over = Environment.GetEnvironmentVariable("CICODESIM_SERVERS");
                if (!string.IsNullOrEmpty(over))
                    return over!;
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(local))
                    local = Path.GetTempPath();
                return Path.Combine(local, "cicode-sim", "servers");
            }
        }

        public static string PathOf(string id, string? folder = null) =>
            Path.Combine(folder ?? Folder, id + ".json");

        /// <summary>A new server id: the pid and 8 random hex digits ('[0-9a-f-]' only: it names
        /// a pipe and a file).</summary>
        public static string NewId(int? pid = null)
        {
            var b = new byte[4];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                rng.GetBytes(b);
            uint r = BitConverter.ToUInt32(b, 0);
            return (pid ?? CurrentPid).ToString(CultureInfo.InvariantCulture)
                + "-"
                + r.ToString("x8", CultureInfo.InvariantCulture);
        }

        public static int CurrentPid
        {
            get
            {
                using (var p = Process.GetCurrentProcess())
                    return p.Id;
            }
        }

        /// <summary>A process's start time as a UTC FILETIME, null when there is no such process
        /// (or its start time cannot be read: then it counts as alive with an unknown start).</summary>
        public static long? ProcessStart(int pid, out bool exists)
        {
            exists = false;
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    try
                    {
                        exists = !p.HasExited;
                    }
                    catch (Exception)
                    {
                        // Win32Exception 'Access is denied' (a protected or system process now has
                        // the pid): this user can always open its own servers, so it is not one
                        return null;
                    }
                    if (!exists)
                        return null;
                    try
                    {
                        return p.StartTime.ToUniversalTime().ToFileTimeUtc();
                    }
                    catch (Exception)
                    {
                        return null; // access denied (another user's process): alive, unknown start
                    }
                }
            }
            catch (ArgumentException)
            {
                return null; // no such process
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>The entry's server process still runs: the pid exists and, when both start
        /// times are known, they agree within a second.</summary>
        public static bool IsAlive(ServerEntry e)
        {
            if (e.Pid <= 0)
                return false;
            long? start = ProcessStart(e.Pid, out bool exists);
            if (!exists)
                return false;
            if (start == null || e.ProcessStart == 0)
                return true;
            return Math.Abs(start.Value - e.ProcessStart) <= StartTolerance;
        }

        /// <summary>Write the entry atomically; the file's path.</summary>
        public static string Write(ServerEntry e, string? folder = null)
        {
            string dir = folder ?? Folder;
            Directory.CreateDirectory(dir);
            string path = PathOf(e.Id, dir);
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, e.ToJson().DumpSpaced() + "\n", new UTF8Encoding(false));
            try
            {
                if (File.Exists(path))
                    File.Replace(tmp, path, null);
                else
                    File.Move(tmp, path);
            }
            catch (Exception)
            {
                try
                {
                    File.Delete(tmp);
                }
                catch (Exception) { }
                throw;
            }
            return path;
        }

        /// <summary>Remove a server's entry (missing is fine).</summary>
        public static void Remove(string id, string? folder = null)
        {
            try
            {
                File.Delete(PathOf(id, folder));
            }
            catch (Exception) { }
        }

        /// <summary>The live servers, oldest first. Stale entries (a dead process, a reused
        /// pid), files that are no entry and temporary files older than a minute are deleted
        /// when <paramref name="clean"/>.</summary>
        public static List<ServerEntry> List(string? folder = null, bool clean = true)
        {
            string dir = folder ?? Folder;
            var r = new List<ServerEntry>();
            if (!Directory.Exists(dir))
                return r;
            foreach (string f in Directory.GetFiles(dir))
            {
                string name = Path.GetFileName(f);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    if (clean && Older(f, TimeSpan.FromMinutes(1)))
                        TryDelete(f);
                    continue;
                }
                if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    continue;
                ServerEntry? e = null;
                try
                {
                    e = ServerEntry.FromJson(JsonValue.Parse(File.ReadAllText(f, Encoding.UTF8)));
                }
                catch (Exception)
                {
                    // unreadable or not JSON: another program's file, or a disk fault
                }
                if (e == null)
                {
                    if (clean && Older(f, TimeSpan.FromMinutes(1)))
                        TryDelete(f);
                    continue;
                }
                if (!IsAlive(e))
                {
                    if (clean)
                        TryDelete(f);
                    continue;
                }
                r.Add(e);
            }
            r.Sort((a, b) => string.CompareOrdinal(a.Started, b.Started));
            return r;
        }

        /// <summary>The live servers of a project folder (compared as Windows paths, without case).</summary>
        public static List<ServerEntry> ForProject(string projectDir, string? folder = null)
        {
            string key = Key(projectDir);
            return List(folder).FindAll(e => Key(e.Project) == key);
        }

        private static string Key(string dir)
        {
            try
            {
                dir = Path.GetFullPath(dir);
            }
            catch (Exception) { }
            return dir.TrimEnd('\\', '/').ToLowerInvariant();
        }

        private static bool Older(string f, TimeSpan age)
        {
            try
            {
                return DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > age;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void TryDelete(string f)
        {
            try
            {
                File.Delete(f);
            }
            catch (Exception) { }
        }
    }
}
