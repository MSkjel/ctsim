using System;
using System.Collections.Generic;
using System.IO;

namespace CicodeSim.Project
{
    /// <summary>One project of a scope: its name, folder and relation to the page's.</summary>
    public sealed class ScopeProject
    {
        public string Name { get; }
        public string Dir { get; }
        public string Relation { get; }

        public ScopeProject(string name, string dir, string relation)
        {
            Name = name;
            Dir = dir;
            Relation = relation;
        }
    }

    /// <summary>Which compiled project (root) a project runs as, the candidates (the roots
    /// whose include closure holds it, by name) and why that root.</summary>
    public sealed class RootPick
    {
        /// <summary>Why a root was picked.</summary>
        public const string Asked = "asked",
            Chosen = "chosen",
            Self = "self",
            Only = "only",
            LastDatabase = "lastDatabase",
            FirstByName = "firstByName",
            None = "none";
        public string? Root { get; }
        public string? RootDir { get; }
        public IReadOnlyList<string> Candidates { get; }
        public string Reason { get; }

        public RootPick(string? root, string? rootDir, IReadOnlyList<string> candidates, string reason)
        {
            Root = root;
            RootDir = rootDir;
            Candidates = candidates;
            Reason = reason;
        }
    }

    /// <summary>The projects of one page folder's scope, in search order.</summary>
    public sealed class Scope
    {
        public string ProjectDir { get; }
        public string? Root { get; }
        public IReadOnlyList<string> Roots { get; }
        public IReadOnlyList<ScopeProject> Projects { get; }

        public Scope(
            string dir,
            string? root,
            IReadOnlyList<string> roots,
            IReadOnlyList<ScopeProject> projects
        )
        {
            ProjectDir = dir;
            Root = root;
            Roots = roots;
            Projects = projects;
        }
    }

    /// <summary>The projects a page's tags, equipment and lists come from (its tag scope).</summary>
    public static class TagScope
    {
        public const string Own = "own",
            Include = "include",
            Includer = "includer",
            Compiled = "compiled";

        public static string Norm(string path) => WinPath.Key(path);

        /// <summary>A project's name: its folder's name.</summary>
        public static string ProjectName(string path)
        {
            string b = WinPath.BaseName(WinPath.Clean(path));
            return b.Length > 0 ? b : path;
        }

        /// <summary>The folder's table <paramref name="name"/> whatever its case, or null (one
        /// stat on Windows: names are case-insensitive).</summary>
        public static string? FindTable(string folder, string name)
        {
            string p = WinPath.Join(folder, name);
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                return WinPath.IsFile(p) ? p : null;
            if (WinPath.IsFile(p))
                return p;
            string low = CitectText.Lower(name);
            var files = WinPath.ListDir(folder);
            if (files != null)
                foreach (string f in files)
                    if (CitectText.Lower(f) == low)
                        return WinPath.Join(folder, f);
            return null;
        }

        private static string? StampIn(string folder, string name)
        {
            string? p = FindTable(folder, name);
            if (p == null)
                return null;
            return Dbf.Stamp(p);
        }

        // ------------------------------------------------------------------ root choice
        private static readonly Dictionary<string, string> Chosen = new(StringComparer.Ordinal);
        private static readonly object Lock = new object();

        /// <summary>Make <paramref name="root"/> the compiled project the scope of
        /// <paramref name="project"/> uses when several compile it (null = the default).</summary>
        public static void ChooseRoot(string project, string? root)
        {
            string key = CitectText.Lower(ProjectName(project));
            lock (Lock)
            {
                if (!string.IsNullOrEmpty(root))
                    Chosen[key] = ProjectName(root!);
                else
                    Chosen.Remove(key);
            }
        }

        public static string? ChosenRoot(string project)
        {
            lock (Lock)
                return Chosen.TryGetValue(CitectText.Lower(ProjectName(project)), out var r) ? r : null;
        }

        private static string LastDatabase()
        {
            try
            {
                return CitectText.Trim(CitectIni.IniValue("ctedit", "lastdatabase"));
            }
            catch (Exception)
            {
                return "";
            }
        }

        // ------------------------------------------------------------------ topology
        private static (ProjectCatalog cat, ProjectInfo? info) CatalogFor(string projectDir)
        {
            string parent = WinPath.DirName(WinPath.Full(projectDir));
            string? user =
                WinPath.IsFile(WinPath.Join(parent, "MASTER.DBF"))
                || WinPath.IsFile(WinPath.Join(parent, "master.dbf"))
                    ? parent
                    : null;
            var cat = ProjectCatalog.For(user);
            ProjectInfo? info;
            try
            {
                info = cat.ProjectForDir(projectDir);
            }
            catch (Exception)
            {
                info = null;
            }
            return (cat, info);
        }

        private static string TopologyStamps(ProjectCatalog cat)
        {
            var parts = new List<string>();
            foreach (var inst in cat.Installs)
                parts.Add(StampIn(inst.UserDir, "MASTER.DBF") ?? "-");
            List<ProjectInfo> projects;
            try
            {
                projects = cat.ProjectList();
            }
            catch (Exception)
            {
                projects = new List<ProjectInfo>();
            }
            foreach (var p in projects)
                parts.Add(CitectText.Lower(p.Name) + ":" + (StampIn(p.Dir, "include.dbf") ?? "-"));
            return string.Join("\n", parts);
        }

        private static readonly Dictionary<ProjectCatalog, string> TopoSeen = new();

        private static (ProjectCatalog cat, ProjectInfo? info, string st) FreshCatalog(string projectDir)
        {
            var (cat, info) = CatalogFor(projectDir);
            string st = TopologyStamps(cat);
            string? seen;
            lock (Lock)
            {
                TopoSeen.TryGetValue(cat, out seen);
                TopoSeen[cat] = st;
            }
            if (seen != null && seen != st)
            {
                try
                {
                    cat.Reload();
                }
                catch (Exception e)
                {
                    EngineNotes.Swallowed("TagScope.Reload", e);
                }
                (cat, info) = CatalogFor(projectDir);
                st = TopologyStamps(cat);
                lock (Lock)
                    TopoSeen[cat] = st;
            }
            return (cat, info, st);
        }

        private static List<ProjectInfo> RootsOf(ProjectCatalog cat, ProjectInfo? info)
        {
            var o = new List<ProjectInfo>();
            if (info == null)
                return o;
            if (info.IsRoot)
            {
                o.Add(info);
                return o;
            }
            foreach (var r in cat.Roots())
            {
                try
                {
                    foreach (var p in cat.Closure(r))
                        if (p.Key == info.Key)
                        {
                            o.Add(r);
                            break;
                        }
                }
                catch (Exception e)
                {
                    EngineNotes.Swallowed("TagScope.RootsIncluding", e);
                }
            }
            return ProjectCatalog.SortByName(o);
        }

        /// <summary>The names of the compiled projects (roots) that include the folder's
        /// project, by name (empty for a folder that is no project; itself for a root).</summary>
        public static List<string> RootNames(string projectDir)
        {
            try
            {
                var (cat, info, _) = FreshCatalog(projectDir);
                return RootsOf(cat, info).ConvertAll(r => r.Name);
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        private static ProjectInfo? PickRoot(string project, List<ProjectInfo> candidates, string? root) =>
            PickRootWhy(project, candidates, root).hit;

        /// <summary>The root of <paramref name="candidates"/> and why it was picked
        /// (<see cref="RootPick"/>).</summary>
        private static (ProjectInfo? hit, string why) PickRootWhy(
            string project,
            List<ProjectInfo> candidates,
            string? root
        )
        {
            if (candidates.Count == 0)
                return (null, RootPick.None);
            var by = new Dictionary<string, ProjectInfo>(StringComparer.Ordinal);
            foreach (var c in candidates)
                by[CitectText.Lower(c.Name)] = c;
            if (
                !string.IsNullOrEmpty(root)
                && by.TryGetValue(CitectText.Lower(ProjectName(root!)), out var asked)
            )
                return (asked, RootPick.Asked);
            string? chosen = ChosenRoot(project);
            if (
                !string.IsNullOrEmpty(chosen)
                && by.TryGetValue(CitectText.Lower(ProjectName(chosen!)), out var ch)
            )
                return (ch, RootPick.Chosen);
            if (candidates.Count > 1)
            {
                string last = LastDatabase();
                if (last.Length > 0 && by.TryGetValue(CitectText.Lower(last), out var hit))
                    return (hit, RootPick.LastDatabase);
                return (candidates[0], RootPick.FirstByName);
            }
            bool self = CitectText.Lower(candidates[0].Name) == CitectText.Lower(ProjectName(project));
            return (candidates[0], self ? RootPick.Self : RootPick.Only);
        }

        /// <summary>The compiled root the runtime runs ``projectDir`` as, and why.</summary>
        public static RootPick PickRootFor(string projectDir, string? root = null)
        {
            string pdir = WinPath.Full(string.IsNullOrEmpty(projectDir) ? "." : projectDir);
            List<ProjectInfo> cands;
            try
            {
                var (cat, info, _) = FreshCatalog(pdir);
                cands = RootsOf(cat, info);
            }
            catch (Exception)
            {
                cands = new List<ProjectInfo>();
            }
            var (hit, why) = PickRootWhy(ProjectName(pdir), cands, root);
            return new RootPick(hit?.Name, hit?.Dir, cands.ConvertAll(c => c.Name), why);
        }

        private static List<string> Down(string projectDir)
        {
            try
            {
                return Projects.IncludeSearchDirs(projectDir);
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        private static (List<ScopeProject> projects, string? chosen, List<string> cands, string st) OwnScope(
            string projectDir,
            string? root
        )
        {
            string pdir = WinPath.Full(projectDir);
            var o = new List<ScopeProject>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Add(string name, string d, string rel)
            {
                string k = Norm(d);
                if (seen.Contains(k) || !WinPath.IsDir(d))
                    return;
                seen.Add(k);
                o.Add(new ScopeProject(name, d, rel));
            }

            ProjectCatalog? cat;
            ProjectInfo? info;
            string st;
            try
            {
                (cat, info, st) = FreshCatalog(pdir);
            }
            catch (Exception)
            {
                cat = null;
                info = null;
                st = "";
            }
            Add(info != null ? info.Name : ProjectName(pdir), pdir, Own);
            foreach (string d in Down(pdir))
                Add(ProjectName(d), d, Include);
            var cands = cat != null ? RootsOf(cat, info) : new List<ProjectInfo>();
            var chosen = PickRoot(ProjectName(pdir), cands, root);
            if (cat != null && info != null && chosen != null && chosen.Key != info.Key)
            {
                List<ProjectInfo> closure;
                try
                {
                    closure = cat.Closure(chosen);
                }
                catch (Exception)
                {
                    closure = new List<ProjectInfo>();
                }
                var inside = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in closure)
                    inside.Add(p.Key);
                List<ProjectInfo> ups;
                try
                {
                    ups = cat.Dependents(info).FindAll(p => inside.Contains(p.Key));
                }
                catch (Exception e)
                {
                    EngineNotes.Swallowed("TagScope.Includers", e);
                    ups = new List<ProjectInfo>();
                }
                foreach (var p in ups)
                    Add(p.Name, p.Dir, Includer);
                foreach (var p in closure)
                    Add(p.Name, p.Dir, Compiled);
            }
            else if (cat != null && info != null && chosen == null)
            {
                try
                {
                    foreach (var p in cat.Dependents(info))
                        Add(p.Name, p.Dir, Includer);
                }
                catch (Exception e)
                {
                    EngineNotes.Swallowed("TagScope.Includers", e);
                }
            }
            return (o, chosen?.Name, cands.ConvertAll(c => c.Name), st);
        }

        /// <summary>The CICODESIM_LIB_PATH folders (a page copied out of its project names the
        /// project there; EngineEnv).</summary>
        public static List<string> ExtraDirs()
        {
            var o = new List<string>();
            string env = EngineEnv.Get("LIB_PATH") ?? "";
            foreach (string p in env.Split(Path.PathSeparator))
                if (p.Length > 0 && WinPath.IsDir(p))
                    o.Add(WinPath.Full(p));
            return o;
        }

        private static readonly Dictionary<string, (string sig, Scope sc)> Scopes = new(
            StringComparer.Ordinal
        );
        private const int MaxScopes = 64;

        /// <summary>The tag scope of the page folder <paramref name="projectDir"/>;
        /// <paramref name="root"/> = the compiled project to use when several include it.</summary>
        public static Scope For(string projectDir, string? root = null)
        {
            string pdir = WinPath.Full(string.IsNullOrEmpty(projectDir) ? "." : projectDir);
            var extra = ExtraDirs().FindAll(d => Norm(d) != Norm(pdir));
            string key =
                Norm(pdir)
                + "|"
                + CitectText.Lower(root ?? "")
                + "|"
                + CitectText.Lower(ChosenRoot(pdir) ?? "")
                + "|"
                + string.Join(";", extra.ConvertAll(Norm))
                + "|"
                + CitectText.Lower(LastDatabase());
            string? sig;
            try
            {
                var parts = new List<string> { FreshCatalog(pdir).st };
                foreach (string d in extra)
                    parts.Add(FreshCatalog(d).st);
                sig = string.Join("\u0001", parts);
            }
            catch (Exception)
            {
                sig = null;
            }
            lock (Lock)
            {
                if (Scopes.TryGetValue(key, out var hit) && sig != null && hit.sig == sig)
                    return hit.sc;
            }
            var (projects, chosen, cands, _) = OwnScope(pdir, root);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in projects)
                seen.Add(Norm(p.Dir));
            foreach (string d in extra)
            {
                foreach (var p in OwnScope(d, null).projects)
                {
                    if (seen.Add(Norm(p.Dir)))
                    {
                        string rel = Norm(p.Dir) == Norm(d) ? Own : p.Relation;
                        projects.Add(new ScopeProject(p.Name, p.Dir, rel));
                    }
                }
            }
            var sc = new Scope(pdir, chosen, cands, projects);
            lock (Lock)
            {
                if (Scopes.Count >= MaxScopes)
                    Scopes.Clear();
                if (sig != null)
                    Scopes[key] = (sig, sc);
            }
            return sc;
        }

        /// <summary>Forget the cached scopes and root choices (tests).</summary>
        public static void Clear()
        {
            lock (Lock)
            {
                Scopes.Clear();
                TopoSeen.Clear();
                Chosen.Clear();
            }
            ProjectCatalog.ClearShared();
        }
    }
}
