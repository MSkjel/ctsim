using System;
using System.Collections.Generic;
using System.IO;

namespace CicodeSim.Project
{
    /// <summary>Windows path rules for project folders and files.</summary>
    public static class WinPath
    {
        private static bool IsSep(char c) => c == '\\' || c == '/';

        /// <summary>The drive or UNC share at the start: ("C:", rest), ("\\server\share", rest)
        /// or ("", path).</summary>
        public static void SplitDrive(string p, out string drive, out string rest)
        {
            if (p.Length >= 2 && IsSep(p[0]) && IsSep(p[1]) && !(p.Length > 2 && IsSep(p[2])))
            {
                int i = 2;
                while (i < p.Length && !IsSep(p[i]))
                    i++;
                if (i < p.Length)
                {
                    int j = i + 1;
                    while (j < p.Length && !IsSep(p[j]))
                        j++;
                    drive = p.Substring(0, j);
                    rest = p.Substring(j);
                    return;
                }
                drive = p;
                rest = "";
                return;
            }
            if (p.Length >= 2 && p[1] == ':')
            {
                drive = p.Substring(0, 2);
                rest = p.Substring(2);
                return;
            }
            drive = "";
            rest = p;
        }

        /// <summary>The path with '/' as '\', '.' parts and empty parts dropped and '..' applied
        /// to the part before it (kept at the start of a relative path); "." for an empty
        /// result. Nothing is looked up.</summary>
        public static string Clean(string path)
        {
            if (string.IsNullOrEmpty(path))
                return ".";
            string p = path.Replace('/', '\\');
            SplitDrive(p, out string drive, out string rest);
            string prefix = "";
            int i = 0;
            if (rest.Length > 0 && rest[0] == '\\')
            {
                prefix = "\\";
                while (i < rest.Length && rest[i] == '\\')
                    i++;
            }
            var comps = new List<string>();
            foreach (string c in rest.Substring(i).Split('\\'))
            {
                if (c.Length == 0 || c == ".")
                    continue;
                if (c == "..")
                {
                    if (comps.Count > 0 && comps[comps.Count - 1] != "..")
                        comps.RemoveAt(comps.Count - 1);
                    else if (prefix.Length == 0)
                        comps.Add(c);
                    continue;
                }
                comps.Add(c);
            }
            string res = drive + prefix + string.Join("\\", comps);
            return res.Length == 0 ? "." : res;
        }

        /// <summary>The full path (GetFullPathName of the cleaned path, without a trailing
        /// separator); the cleaned path when it cannot be made full.</summary>
        public static string Full(string path)
        {
            string n = Clean(string.IsNullOrEmpty(path) ? "." : path);
            try
            {
                string full = Path.GetFullPath(n);
                if (full.Length > 3 && full.EndsWith("\\", StringComparison.Ordinal))
                    full = full.TrimEnd('\\');
                return full;
            }
            catch (Exception)
            {
                return n;
            }
        }

        /// <summary>The comparison key of a path without looking it up: lower case, '/' as '\'.</summary>
        public static string CaseKey(string path) => CitectText.Lower(path).Replace('/', '\\');

        /// <summary>The comparison key of a folder or file: its full path in lower case. Two
        /// paths name the same folder when their keys are equal.</summary>
        public static string Key(string path) => CaseKey(Full(path));

        /// <summary>(head, tail): the folder part and the last name.</summary>
        public static void Split(string p, out string head, out string tail)
        {
            SplitDrive(p, out string d, out string rest);
            int i = rest.Length;
            while (i > 0 && !IsSep(rest[i - 1]))
                i--;
            string h = rest.Substring(0, i);
            tail = rest.Substring(i);
            string stripped = h.TrimEnd('\\', '/');
            head = d + (stripped.Length > 0 ? stripped : h);
        }

        /// <summary>The folder part of a path (the drive root stays itself).</summary>
        public static string DirName(string p)
        {
            Split(p, out string h, out _);
            return h;
        }

        /// <summary>The last name of a path ("" after a trailing separator).</summary>
        public static string BaseName(string p)
        {
            Split(p, out _, out string t);
            return t;
        }

        /// <summary>b inside a (b alone when it has a drive or starts at a root).</summary>
        public static string Join(string a, string b)
        {
            SplitDrive(b, out string bd, out string brest);
            if (bd.Length > 0 || (brest.Length > 0 && IsSep(brest[0])))
            {
                if (bd.Length == 0)
                {
                    SplitDrive(a, out string ad, out _);
                    return ad + b;
                }
                return b;
            }
            if (a.Length == 0)
                return b;
            char last = a[a.Length - 1];
            if (IsSep(last) || (a.Length == 2 && a[1] == ':'))
                return a + b;
            return a + "\\" + b;
        }

        public static string Join(string a, string b, string c) => Join(Join(a, b), c);

        public static bool IsDir(string? p)
        {
            if (string.IsNullOrEmpty(p))
                return false;
            try
            {
                return Directory.Exists(p);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool IsFile(string? p)
        {
            if (string.IsNullOrEmpty(p))
                return false;
            try
            {
                return File.Exists(p);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The names in a folder (the file system's order); null when it cannot be
        /// listed.</summary>
        public static List<string>? ListDir(string dir)
        {
            try
            {
                var o = new List<string>();
                foreach (string f in Directory.EnumerateFileSystemEntries(dir))
                    o.Add(Path.GetFileName(f));
                return o;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>(mtime in 100 ns ticks, size) of a file, null when it cannot be stat'ed.</summary>
        public static (long mtime, long size)? Stat(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists)
                    return null;
                return (fi.LastWriteTimeUtc.Ticks, fi.Length);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
