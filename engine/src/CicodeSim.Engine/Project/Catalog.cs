using System;
using System.Collections.Generic;

namespace CicodeSim.Project
{
    /// <summary>One install's User folder.</summary>
    public sealed class InstallInfo
    {
        public string UserDir { get; }
        public bool IsLive { get; }

        public InstallInfo(string userDir, bool isLive)
        {
            UserDir = userDir;
            IsLive = isLive;
        }

        public string Key => WinPath.Key(UserDir);
    }

    /// <summary>A project (or loose folder) inside one install (catalog.ProjectInfo).</summary>
    public sealed class ProjectInfo
    {
        public string Name { get; }
        public string Dir { get; }
        public InstallInfo? Install { get; }
        public bool Registered { get; }
        public bool Exists { get; }
        public List<string> Includes { get; internal set; } = new List<string>();
        public List<string> IncludedBy { get; } = new List<string>();
        public bool IsRoot { get; internal set; }
        public bool IsFolder { get; }
        private string? _key;

        public ProjectInfo(
            string name,
            string dir,
            InstallInfo? install,
            bool registered = true,
            bool exists = true,
            bool isFolder = false
        )
        {
            Name = name;
            Dir = dir;
            Install = install;
            Registered = registered;
            Exists = exists;
            IsFolder = isFolder;
        }

        public bool IsImplicit =>
            Array.Exists(Projects.ImplicitIncludeProjects, n =>
                CitectText.Lower(n) == CitectText.Lower(Name)
            );
        public string Key => _key ??= WinPath.Key(Dir);

        public override string ToString() => $"ProjectInfo({Name})";
    }

    /// <summary>The project topology of an install and its sibling installs.</summary>
    public sealed class ProjectCatalog
    {
        private static readonly string[] CtExts = { ".ctg", ".ctt", ".ctl", ".ctm" };
        public List<InstallInfo> Installs { get; } = new List<InstallInfo>();
        private readonly object _lock = new object();
        private readonly Dictionary<string, Dictionary<string, ProjectInfo>> _topo = new(
            StringComparer.Ordinal
        );
        private readonly Dictionary<string, ProjectInfo> _folders = new(StringComparer.Ordinal);

        public ProjectCatalog(string? userDir = null, bool otherInstalls = true)
        {
            string? live = userDir ?? CitectIni.LiveUserDir();
            if (live != null && WinPath.IsDir(live))
            {
                Installs.Add(new InstallInfo(WinPath.Full(live), true));
                if (otherInstalls)
                {
                    foreach (string u in Projects.SiblingInstallUserDirs(live))
                        Installs.Add(new InstallInfo(WinPath.Full(u), false));
                }
            }
        }

        public InstallInfo? LiveInstall => Installs.Count > 0 && Installs[0].IsLive ? Installs[0] : null;

        public Dictionary<string, ProjectInfo> Topology(InstallInfo install)
        {
            lock (_lock)
            {
                if (_topo.TryGetValue(install.Key, out var topo))
                    return topo;
                topo = new Dictionary<string, ProjectInfo>(StringComparer.Ordinal);
                var order = new List<string>(); // the order the projects were found
                string ud = install.UserDir;
                string master = WinPath.Join(ud, "MASTER.DBF");
                if (!WinPath.IsFile(master))
                    master = WinPath.Join(ud, "master.dbf");
                if (WinPath.IsFile(master))
                {
                    foreach (DbfRow row in Dbf.CitectRows(master))
                    {
                        string name = CitectText.Trim(row.Get("NAME")),
                            path = CitectText.Trim(row.Get("PATH"));
                        if (name.Length == 0 || path.Length == 0)
                            continue;
                        path = WinPath.Clean(path);
                        string low = CitectText.Lower(name);
                        if (topo.ContainsKey(low))
                            continue;
                        topo[low] = new ProjectInfo(name, path, install, true, WinPath.IsDir(path));
                        order.Add(low);
                    }
                }
                var registeredDirs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in topo.Values)
                    registeredDirs.Add(p.Key);
                List<string> subdirs = CitectText.SortIgnoringCase(WinPath.ListDir(ud) ?? new List<string>());
                foreach (string d in subdirs)
                {
                    string full = WinPath.Join(ud, d);
                    if (
                        d.StartsWith(".", StringComparison.Ordinal)
                        || topo.ContainsKey(CitectText.Lower(d))
                        || registeredDirs.Contains(WinPath.Key(full))
                        || !WinPath.IsDir(full)
                    )
                        continue;
                    bool hasCt = false;
                    var files = WinPath.ListDir(full);
                    if (files != null)
                    {
                        foreach (string f in files)
                        {
                            string lf = CitectText.Lower(f);
                            foreach (string ext in CtExts)
                                if (lf.EndsWith(ext, StringComparison.Ordinal))
                                {
                                    hasCt = true;
                                    break;
                                }
                            if (hasCt)
                                break;
                        }
                    }
                    if (hasCt)
                    {
                        topo[CitectText.Lower(d)] = new ProjectInfo(d, full, install, registered: false);
                        order.Add(CitectText.Lower(d));
                    }
                }
                foreach (string k in order)
                {
                    var info = topo[k];
                    if (!info.Exists)
                        continue;
                    info.Includes = Projects.IncludedProjectNames(info.Dir);
                }
                foreach (string k in order)
                {
                    var info = topo[k];
                    foreach (string inc in info.Includes)
                    {
                        if (
                            topo.TryGetValue(CitectText.Lower(inc), out var tgt)
                            && !tgt.IncludedBy.Contains(info.Name)
                        )
                            tgt.IncludedBy.Add(info.Name);
                    }
                }
                var implicitLow = new HashSet<string>(StringComparer.Ordinal);
                foreach (string n in Projects.ImplicitIncludeProjects)
                    implicitLow.Add(CitectText.Lower(n));
                foreach (string k in order)
                {
                    var info = topo[k];
                    info.IsRoot =
                        info.Exists
                        && info.Registered
                        && info.IncludedBy.Count == 0
                        && !implicitLow.Contains(k);
                }
                _order[install.Key] = order;
                _topo[install.Key] = topo;
                return topo;
            }
        }

        private readonly Dictionary<string, List<string>> _order = new(StringComparer.Ordinal);

        private IEnumerable<ProjectInfo> TopoValues(InstallInfo inst)
        {
            var topo = Topology(inst);
            foreach (string k in _order[inst.Key])
                yield return topo[k];
        }

        /// <summary>Projects of <paramref name="install"/> (default: the live one), by name.</summary>
        public List<ProjectInfo> ProjectList(InstallInfo? install = null, bool existingOnly = true)
        {
            var inst = install ?? LiveInstall;
            var o = new List<ProjectInfo>();
            if (inst == null)
                return o;
            foreach (var p in TopoValues(inst))
                if (p.Exists || !existingOnly)
                    o.Add(p);
            return SortByName(o);
        }

        internal static List<ProjectInfo> SortByName(List<ProjectInfo> items)
        {
            var idx = new List<(string, int, ProjectInfo)>();
            for (int i = 0; i < items.Count; i++)
                idx.Add((CitectText.Lower(items[i].Name), i, items[i]));
            idx.Sort(
                (a, b) =>
                {
                    int c = string.CompareOrdinal(a.Item1, b.Item1);
                    return c != 0 ? c : a.Item2.CompareTo(b.Item2);
                }
            );
            var o = new List<ProjectInfo>(items.Count);
            foreach (var t in idx)
                o.Add(t.Item3);
            return o;
        }

        /// <summary>Root projects (compiled on their own; nobody includes them).</summary>
        public List<ProjectInfo> Roots(InstallInfo? install = null) =>
            ProjectList(install).FindAll(p => p.IsRoot);

        public ProjectInfo? Project(string name, InstallInfo? install = null)
        {
            var inst = install ?? LiveInstall;
            if (inst == null || string.IsNullOrEmpty(name))
                return null;
            return Topology(inst).TryGetValue(CitectText.Lower(name), out var p) ? p : null;
        }

        /// <summary>The project whose folder is <paramref name="directory"/> (any install).</summary>
        public ProjectInfo? ProjectForDir(string? directory)
        {
            if (string.IsNullOrEmpty(directory))
                return null;
            string k = WinPath.Key(directory!);
            foreach (var inst in Installs)
            foreach (var p in TopoValues(inst))
                if (p.Exists && p.Key == k)
                    return p;
            return null;
        }

        /// <summary>A loose folder (outside any project) as a pseudo-project.</summary>
        public ProjectInfo Folder(string directory)
        {
            var info = ProjectForDir(directory);
            if (info != null)
                return info;
            string k = WinPath.Key(directory);
            lock (_lock)
            {
                if (!_folders.TryGetValue(k, out info))
                {
                    string name = WinPath.BaseName(WinPath.Clean(directory));
                    if (name.Length == 0)
                        name = directory;
                    info = new ProjectInfo(
                        name,
                        WinPath.Full(directory),
                        InstallForPath(directory),
                        registered: false,
                        exists: WinPath.IsDir(directory),
                        isFolder: true
                    );
                    _folders[k] = info;
                }
                return info;
            }
        }

        public InstallInfo? InstallForPath(string path)
        {
            string p = WinPath.Key(path);
            foreach (var inst in Installs)
            {
                string k = inst.Key;
                if (p == k || p.StartsWith(k + "\\", StringComparison.Ordinal))
                    return inst;
            }
            return null;
        }

        private ProjectInfo? AsProject(object project)
        {
            if (project is ProjectInfo pi)
                return pi;
            if (project is string s)
            {
                if (s.IndexOf('\\') >= 0 || s.IndexOf('/') >= 0)
                    return ProjectForDir(s) ?? Folder(s);
                return Project(s);
            }
            return null;
        }

        /// <summary>The project, its recursive include chain and the implicit includes
        /// (dedup-guarded, CtDraw's search order), names resolved within its own install.</summary>
        public List<ProjectInfo> Closure(object project)
        {
            var root = AsProject(project);
            var o = new List<ProjectInfo>();
            if (root == null)
                return o;
            if (root.Install == null || root.IsFolder)
            {
                o.Add(root);
                return o;
            }
            var topo = Topology(root.Install);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Add(ProjectInfo? info)
            {
                if (info == null || !info.Exists || seen.Contains(info.Key))
                    return;
                seen.Add(info.Key);
                o.Add(info);
                foreach (string inc in info.Includes)
                    Add(topo.TryGetValue(CitectText.Lower(inc), out var t) ? t : null);
            }

            Add(root);
            foreach (string n in Projects.ImplicitIncludeProjects)
                Add(topo.TryGetValue(CitectText.Lower(n), out var t) ? t : null);
            return o;
        }

        /// <summary>The projects whose include chain reaches <paramref name="project"/> (not
        /// itself); every project of the install reaches the implicit Include / System.</summary>
        public List<ProjectInfo> Dependents(object project)
        {
            var info = AsProject(project);
            var o = new List<ProjectInfo>();
            if (info == null || info.Install == null || info.IsFolder)
                return o;
            var topo = Topology(info.Install);
            if (info.IsImplicit)
            {
                foreach (var p in TopoValues(info.Install))
                    if (p.Exists && p.Key != info.Key)
                        o.Add(p);
                return SortByName(o);
            }
            var seen = new HashSet<string>(StringComparer.Ordinal) { info.Key };
            var todo = new List<string>(info.IncludedBy);
            while (todo.Count > 0)
            {
                string n = todo[0];
                todo.RemoveAt(0);
                if (!topo.TryGetValue(CitectText.Lower(n), out var p) || !p.Exists || seen.Contains(p.Key))
                    continue;
                seen.Add(p.Key);
                o.Add(p);
                todo.AddRange(p.IncludedBy);
            }
            return o;
        }

        /// <summary>Forget everything incl. the topology.</summary>
        public void Reload()
        {
            lock (_lock)
            {
                _topo.Clear();
                _order.Clear();
                _folders.Clear();
            }
        }

        private static readonly Dictionary<string, ProjectCatalog> Catalogs = new(StringComparer.Ordinal);

        /// <summary>The shared catalog of <paramref name="userDir"/> (default: the live install).</summary>
        public static ProjectCatalog For(string? userDir = null)
        {
            string ud = userDir ?? CitectIni.LiveUserDir() ?? "";
            string key = ud.Length > 0 ? WinPath.Key(ud) : "";
            lock (Catalogs)
            {
                if (!Catalogs.TryGetValue(key, out var cat))
                {
                    cat = new ProjectCatalog(ud.Length > 0 ? ud : null);
                    Catalogs[key] = cat;
                }
                return cat;
            }
        }

        /// <summary>Forget the shared catalogs (tests).</summary>
        public static void ClearShared()
        {
            lock (Catalogs)
                Catalogs.Clear();
        }
    }
}
