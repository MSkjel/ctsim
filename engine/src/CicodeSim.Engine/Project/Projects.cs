using System;
using System.Collections.Generic;

namespace CicodeSim.Project
{
    /// <summary>Project topology: the MASTER.DBF registry and include chains.</summary>
    public static class Projects
    {
        /// <summary>Projects every Citect project includes implicitly (no include.dbf row).</summary>
        public static readonly string[] ImplicitIncludeProjects = { "Include", "System" };

        /// <summary>The User\MASTER.DBF registry: lower-case NAME to normalised PATH ({} when
        /// absent or unreadable).</summary>
        public static Dictionary<string, string> MasterProjects(string userDir)
        {
            var o = new Dictionary<string, string>(StringComparer.Ordinal);
            string path = WinPath.Join(userDir, "MASTER.DBF");
            if (!WinPath.IsFile(path))
            {
                path = WinPath.Join(userDir, "master.dbf");
                if (!WinPath.IsFile(path))
                    return o;
            }
            foreach (DbfRow row in Dbf.CitectRows(path))
            {
                string name = row.Get("NAME"),
                    p = row.Get("PATH");
                if (name.Length > 0 && p.Length > 0)
                    o[CitectText.Lower(name)] = WinPath.Clean(p);
            }
            return o;
        }

        /// <summary>The ordered NAME rows of a project's include.dbf (empty when none).</summary>
        public static List<string> IncludedProjectNames(string projectDir)
        {
            var o = new List<string>();
            List<string>? names = WinPath.ListDir(projectDir);
            if (names == null)
                return o;
            foreach (string fn in names)
            {
                if (CitectText.Lower(fn) == "include.dbf")
                {
                    foreach (DbfRow r in Dbf.CitectRows(WinPath.Join(projectDir, fn)))
                    {
                        string n = r.Get("NAME");
                        if (!CitectText.IsBlank(n))
                            o.Add(n);
                    }
                    return o;
                }
            }
            return o;
        }

        /// <summary>User folders of OTHER installs next to this one (any sibling of the
        /// install root holding User\MASTER.DBF), sorted by lower-case name.</summary>
        public static List<string> SiblingInstallUserDirs(string userDir)
        {
            var o = new List<string>();
            userDir = WinPath.Full(userDir);
            string installRoot = WinPath.DirName(userDir);
            string grand = WinPath.DirName(installRoot);
            if (grand.Length == 0 || grand == installRoot)
                return o;
            string userBase = WinPath.BaseName(userDir);
            List<string>? sibs = WinPath.ListDir(grand);
            if (sibs == null)
                return o;
            foreach (string sib in CitectText.SortIgnoringCase(sibs))
            {
                string root = WinPath.Join(grand, sib);
                if (WinPath.CaseKey(root) == WinPath.CaseKey(installRoot))
                    continue;
                string cand = WinPath.Join(root, userBase);
                if (WinPath.IsFile(WinPath.Join(cand, "MASTER.DBF")))
                    o.Add(cand);
            }
            return o;
        }

        /// <summary>Project NAME to every existing folder across the installs, in
        /// <paramref name="userDirs"/> order: per install the registered PATH, else
        /// User\name; one folder per install.</summary>
        public static List<string> ResolveProjectDirs(
            string name,
            IList<string> userDirs,
            Dictionary<string, Dictionary<string, string>>? masters = null
        )
        {
            string low = CitectText.Lower(name);
            masters ??= new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            var o = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string ud in userDirs)
            {
                if (!masters.TryGetValue(ud, out var reg))
                {
                    reg = MasterProjects(ud);
                    masters[ud] = reg;
                }
                var cands = new List<string>(2);
                if (reg.TryGetValue(low, out string? p) && p.Length > 0)
                    cands.Add(p);
                cands.Add(WinPath.Join(ud, name));
                foreach (string cand in cands)
                {
                    if (WinPath.IsDir(cand))
                    {
                        string key = WinPath.Key(cand);
                        if (seen.Add(key))
                            o.Add(cand);
                        break; // the registry path wins within one install
                    }
                }
            }
            return o;
        }

        /// <summary>Project NAME to its first existing folder, null when none.</summary>
        public static string? ResolveProjectDir(
            string name,
            IList<string> userDirs,
            Dictionary<string, Dictionary<string, string>>? masters = null
        )
        {
            var d = ResolveProjectDirs(name, userDirs, masters);
            return d.Count > 0 ? d[0] : null;
        }

        /// <summary>The included project folders of ``projectDir``, depth first.</summary>
        public static List<string> IncludeSearchDirs(string projectDir)
        {
            projectDir = WinPath.Full(projectDir);
            string userDir = WinPath.DirName(projectDir);
            var userDirs = new List<string> { userDir };
            userDirs.AddRange(SiblingInstallUserDirs(userDir));
            var masters = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            var o = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal) { WinPath.CaseKey(projectDir) };

            void Add(string name)
            {
                foreach (string p in ResolveProjectDirs(name, userDirs, masters))
                {
                    string key = WinPath.Key(p);
                    if (!seen.Add(key))
                        continue;
                    o.Add(p);
                    Walk(p);
                }
            }

            void Walk(string d)
            {
                foreach (string name in IncludedProjectNames(d))
                    Add(name);
            }

            Walk(projectDir);
            foreach (string name in ImplicitIncludeProjects)
                Add(name);
            return o;
        }
    }
}
