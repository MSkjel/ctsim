using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using CicodeSim.Values;

namespace CicodeSim.Project
{
    /// <summary>One project of a compile unit.</summary>
    public sealed class UnitProject
    {
        public string Name { get; }
        public string Dir { get; }

        public UnitProject(string name, string dir)
        {
            Name = name;
            Dir = dir;
        }

        public override string ToString() => Name;
    }

    /// <summary>The compile unit a simulated runtime runs.</summary>
    public sealed class Unit
    {
        /// <summary>Projects every unit compiles before the include projects.</summary>
        public static readonly string[] Implicit = { "System", "Include" };

        public string ProjectDir { get; }
        public string RootDir { get; private set; }
        public List<UnitProject> Projects { get; private set; }
        public List<UnitProject> Search { get; }
        private readonly object _lock = new object();
        private Dictionary<string, string>? _labels;
        private Dictionary<(string, string), string>? _params;

        private static string NormKey(string path) => WinPath.Key(path);

        private static List<string> UserDirs(string projectDir)
        {
            string user = WinPath.DirName(WinPath.Full(projectDir));
            var o = new List<string> { user };
            o.AddRange(CicodeSim.Project.Projects.SiblingInstallUserDirs(user));
            return o;
        }

        /// <summary>The projects compiling <paramref name="rootDir"/> compiles, in compile
        /// order. A name that is not registered is left out (the compiler stops with F4016).</summary>
        public static List<UnitProject> CompileUnit(string rootDir)
        {
            rootDir = WinPath.Full(rootDir);
            var users = UserDirs(rootDir);
            var masters = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            var o = new List<UnitProject>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Add(string name, string d)
            {
                if (seen.Add(NormKey(d)))
                    o.Add(new UnitProject(name, d));
            }

            foreach (string name in Implicit)
            {
                string? d = CicodeSim.Project.Projects.ResolveProjectDir(name, users, masters);
                if (d != null)
                    Add(name, d);
            }
            var visiting = new HashSet<string>(StringComparer.Ordinal);

            void Walk(string name, string d)
            {
                string k = NormKey(d);
                if (seen.Contains(k) || visiting.Contains(k))
                    return;
                visiting.Add(k);
                foreach (string child in CicodeSim.Project.Projects.IncludedProjectNames(d))
                {
                    string? cd = CicodeSim.Project.Projects.ResolveProjectDir(
                        CitectText.Trim(child),
                        users,
                        masters
                    );
                    if (cd != null)
                        Walk(TagScope.ProjectName(cd), cd);
                }
                visiting.Remove(k);
                Add(name, d);
            }

            foreach (string child in CicodeSim.Project.Projects.IncludedProjectNames(rootDir))
            {
                string? cd = CicodeSim.Project.Projects.ResolveProjectDir(
                    CitectText.Trim(child),
                    users,
                    masters
                );
                if (cd != null)
                    Walk(TagScope.ProjectName(cd), cd);
            }
            Add(TagScope.ProjectName(rootDir), rootDir);
            return o;
        }

        /// <summary>The folder of the root the runtime runs for <paramref name="projectDir"/>.</summary>
        public static string ChooseRoot(string projectDir, string? root = null) => Pick(projectDir, root).dir;

        /// <summary>The root folder, the candidate roots and why (TagScope.PickRootFor): an
        /// asked <paramref name="root"/> (a folder or a registered name) is taken as asked; else
        /// the tag scope's rule; no candidate = the folder itself (reason none).</summary>
        private static (string dir, IReadOnlyList<string> cands, string why) Pick(
            string projectDir,
            string? root
        )
        {
            projectDir = WinPath.Full(projectDir);
            RootPick pick;
            try
            {
                pick = TagScope.PickRootFor(projectDir, null);
            }
            catch (Exception)
            {
                pick = new RootPick(null, null, new List<string>(), RootPick.None);
            }
            if (!string.IsNullOrEmpty(root))
            {
                if (WinPath.IsDir(root))
                    return (WinPath.Full(root!), pick.Candidates, RootPick.Asked);
                string? d = CicodeSim.Project.Projects.ResolveProjectDir(root!, UserDirs(projectDir));
                if (d != null)
                    return (WinPath.Full(d), pick.Candidates, RootPick.Asked);
                throw new ArgumentException($"project {CitectText.Quote(root!)} is not registered");
            }
            if (
                pick.Root != null
                && CitectText.Lower(pick.Root) != CitectText.Lower(TagScope.ProjectName(projectDir))
            )
            {
                string? d = CicodeSim.Project.Projects.ResolveProjectDir(pick.Root, UserDirs(projectDir))
                    ?? pick.RootDir;
                if (d != null)
                    return (WinPath.Full(d), pick.Candidates, pick.Reason);
            }
            return (projectDir, pick.Candidates, pick.Root != null ? pick.Reason : RootPick.None);
        }

        public Unit(string projectDir, string? root = null)
        {
            ProjectDir = WinPath.Full(projectDir);
            var (dir, cands, why) = Pick(ProjectDir, root);
            RootDir = dir;
            RootCandidates = cands;
            RootReason = why;
            Projects = CompileUnit(RootDir);
            bool inside = false;
            foreach (var p in Projects)
                if (NormKey(p.Dir) == NormKey(ProjectDir))
                {
                    inside = true;
                    break;
                }
            if (!inside)
            {
                // an asked root that does not compile the project is an error: a client would
                // simulate another program than the one it asked for
                if (why == RootPick.Asked)
                {
                    string list = cands.Count > 0 ? string.Join(", ", cands) : "none";
                    throw new ArgumentException(
                        $"project {CitectText.Quote(TagScope.ProjectName(ProjectDir))} is not compiled as part of "
                            + $"{CitectText.Quote(TagScope.ProjectName(RootDir))} (the projects that compile it: {list})"
                    );
                }
                // a folder outside the root's closure (a copy, a test folder): it is the unit
                RootDir = ProjectDir;
                RootReason = RootPick.None;
                Projects = CompileUnit(ProjectDir);
            }
            Search = SearchOrder();
        }

        /// <summary>Why the root was picked: a <see cref="RootPick"/> reason.</summary>
        public string RootReason { get; private set; }

        /// <summary>The roots whose include closure holds the project, by name (the project
        /// itself when it is a root; empty when no registered root compiles it).</summary>
        public IReadOnlyList<string> RootCandidates { get; }

        public string Root => TagScope.ProjectName(RootDir);
        public string Project => TagScope.ProjectName(ProjectDir);

        public List<string> Names() => Projects.ConvertAll(p => p.Name);

        private List<UnitProject> SearchOrder()
        {
            var inside = new Dictionary<string, UnitProject>(StringComparer.Ordinal);
            foreach (var p in Projects)
                inside[NormKey(p.Dir)] = p;
            var o = new List<UnitProject>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Add(string d)
            {
                string k = NormKey(d);
                if (inside.TryGetValue(k, out var up) && seen.Add(k))
                    o.Add(up);
            }

            Add(ProjectDir);
            try
            {
                foreach (var sp in TagScope.For(ProjectDir, TagScope.ProjectName(RootDir)).Projects)
                    Add(sp.Dir);
            }
            catch (Exception e)
            {
                EngineNotes.Swallowed("Unit.TagScopeProjects", e);
            }
            for (int i = Projects.Count - 1; i >= 0; i--)
                Add(Projects[i].Dir); // root first, then its includes
            return o;
        }

        // ------------------------------------------------------------------ tables
        /// <summary>(project, path) of every project of the unit that has <paramref name="table"/>
        /// (search order by default, or "compile").</summary>
        public List<(UnitProject project, string path)> Tables(string table, string order = "search")
        {
            var projs = order == "search" ? Search : Projects;
            var o = new List<(UnitProject, string)>();
            foreach (var p in projs)
            {
                string? path = TagScope.FindTable(p.Dir, table);
                if (path != null)
                    o.Add((p, path));
            }
            return o;
        }

        /// <summary>Every row of a small table of every project (cached shared rows,
        /// read-only); compile order by default.</summary>
        public List<(UnitProject project, DbfRow row)> Rows(string table, string order = "compile")
        {
            var o = new List<(UnitProject, DbfRow)>();
            foreach (var (p, path) in Tables(table, order))
            foreach (DbfRow r in Dbf.Rows(path))
                o.Add((p, r));
            return o;
        }

        // ------------------------------------------------------------------ labels and parameters
        /// <summary>Lower-case label name to its EXPR text (labels.DBF of the unit, first
        /// definition wins: a later one is E2038).</summary>
        public Dictionary<string, string> Labels()
        {
            var built = System.Threading.Volatile.Read(ref _labels);
            if (built != null)
                return built;
            lock (_lock)
            {
                if (_labels == null)
                {
                    var o = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var (_, r) in Rows("labels.dbf"))
                    {
                        string n = CitectText.Trim(r.Get("NAME"));
                        if (n.Length > 0 && n.IndexOf('(') < 0 && !o.ContainsKey(CitectText.Lower(n)))
                            o[CitectText.Lower(n)] = CitectText.Trim(r.Get("EXPR"));
                    }
                    System.Threading.Volatile.Write(ref _labels, o);
                }
                return _labels!;
            }
        }

        /// <summary>An integer field that may hold a number or a label (alarm CATEGORY
        /// '_LOOPS_MEDIUM', PRIV '_PRIV_ACK'): its integer value, <paramref name="def"/> when it
        /// is neither.</summary>
        public long? LabelValue(string? text, long? def = null)
        {
            text = CitectText.Trim(text ?? "");
            for (int k = 0; k < 8; k++) // a label may name another label
            {
                if (text.Length == 0)
                    return def;
                long? v = NumberValue(text, out bool numeric);
                if (numeric)
                    return v;
                if (!Labels().TryGetValue(CitectText.Lower(text), out string? nxt))
                    return def;
                text = nxt;
            }
            return def;
        }

        /// <summary>The number a label value is read as first: decimal (a leading zero allowed),
        /// '0x..' hex, a text with a '.' as a real truncated toward zero; numeric = false when
        /// the text is none of these (then it is looked up as a label).</summary>
        internal static long? NumberValue(string text, out bool numeric)
        {
            numeric = false;
            if (text.IndexOf('.') >= 0)
            {
                if (!FloatText.TryParseNumber(text, out double d))
                    return null;
                if (double.IsNaN(d) || double.IsInfinity(d))
                    return null; // no integer
                double t = Math.Truncate(d);
                if (t < long.MinValue || t > long.MaxValue)
                {
                    numeric = true;
                    return t < 0 ? long.MinValue : long.MaxValue;
                }
                numeric = true;
                return (long)t;
            }
            string head2 = CitectText.Lower(text.Length >= 2 ? text.Substring(0, 2) : text);
            string head3 = CitectText.Lower(text.Length >= 3 ? text.Substring(0, 3) : text);
            BigInteger? b =
                (head2 == "0x" || head2 == "-0") && head3.IndexOf('x') >= 0
                    ? CitectText.ParseInt(text, 16)
                    : CitectText.ParseInt(text, 10);
            if (b == null)
                return null;
            numeric = true;
            if (b.Value > long.MaxValue)
                return long.MaxValue;
            if (b.Value < long.MinValue)
                return long.MinValue;
            return (long)b.Value;
        }

        /// <summary>(lower section, lower name) to DATA of param.dbf over the unit; a later
        /// project in compile order (the root last) overrides an earlier one (assumed: the
        /// runtime reads the compiled project's merged parameters).</summary>
        public Dictionary<(string, string), string> Params()
        {
            // built once and never changed: read without the lock after that ([Client]
            // TagReadCachedMode is read on every TagGetProperty / TagInfo of a page scan)
            var built = System.Threading.Volatile.Read(ref _params);
            if (built != null)
                return built;
            lock (_lock)
            {
                if (_params == null)
                {
                    var o = new Dictionary<(string, string), string>();
                    foreach (var (_, r) in Rows("param.dbf"))
                    {
                        string s = CitectText.Trim(r.Get("SECTION")),
                            n = CitectText.Trim(r.Get("NAME"));
                        if (s.Length > 0 && n.Length > 0)
                            o[(CitectText.Lower(s), CitectText.Lower(n))] = CitectText.Trim(r.Get("DATA"));
                    }
                    System.Threading.Volatile.Write(ref _params, o);
                }
                return _params!;
            }
        }

        public string? Param(string section, string name, string? def = null) =>
            Params().TryGetValue((CitectText.Lower(section), CitectText.Lower(name)), out string? v)
                ? v
                : def;

        public List<string> Clusters()
        {
            var o = new List<string>();
            foreach (var (_, r) in Rows("cluster.dbf"))
            {
                string n = r.Get("NAME");
                if (n.Length > 0)
                    o.Add(n);
            }
            return o;
        }
    }
}
