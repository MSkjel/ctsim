using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace CicodeSim.Project
{
    /// <summary>The active install's citect.ini and User folder.</summary>
    public static class CitectIni
    {
        // a key line: blanks (space, tab) around the key, the '=' and the value
        private static readonly Regex UserRx = new Regex(
            @"^[ \t]*USER[ \t]*=[ \t]*(.+?)[ \t]*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant
        );

        private static readonly string[] RegistryRoots =
        {
            @"SOFTWARE\WOW6432Node\Citect\SCADA Installs",
            @"SOFTWARE\Citect\SCADA Installs",
            @"SOFTWARE\WOW6432Node\AVEVA\Plant SCADA Installs",
            @"SOFTWARE\AVEVA\Plant SCADA Installs",
        };

        /// <summary>CICODESIM_INI overrides the active citect.ini (tests: a temporary ini; empty
        /// string = no ini).</summary>
        public const string IniOverrideVariable = "CICODESIM_INI";

        /// <summary>The ACTIVE install's citect.ini from the registry, null when absent.</summary>
#pragma warning disable CA1416   // guarded by the Win32NT check
        public static string? RegistryActiveIni()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                return null;
            try
            {
                foreach (string b in RegistryRoots)
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(b);
                    if (key == null)
                        continue;
                    foreach (string ver in key.GetSubKeyNames())
                    {
                        try
                        {
                            using var sub = key.OpenSubKey(ver);
                            if (
                                sub?.GetValue("DefaultINIPath") is string path
                                && path.Length > 0
                                && WinPath.IsFile(path)
                            )
                                return path;
                        }
                        catch (Exception e)
                        {
                            EngineNotes.Swallowed("CitectIni.RegistryInstall", e);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                EngineNotes.Swallowed("CitectIni.Registry", e);
            }
            return null;
        }
#pragma warning restore CA1416

        private static string? ReadText(string path, char[] table)
        {
            try
            {
                byte[] b = File.ReadAllBytes(path);
                return TextCodec.DecodeWith(table, b, 0, b.Length);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The [CtEdit] USER= folder of one citect.ini, null when none.</summary>
        public static string? CteditUser(string iniPath)
        {
            string? raw = ReadText(iniPath, TextCodec.Decode1252);
            if (raw == null)
                return null;
            string text = TextCodec.UniversalNewlines(raw);
            string low = CitectText.Lower(text);
            int start = low.IndexOf("[ctedit]", StringComparison.Ordinal);
            if (start < 0)
                return null;
            int end = low.IndexOf("\n[", start + 1, StringComparison.Ordinal);
            string section = text.Substring(start, (end > 0 ? end : text.Length) - start);
            Match m = UserRx.Match(section);
            if (!m.Success)
                return null;
            string path = CitectText.Trim(m.Groups[1].Value).Trim('"');
            return path.Length > 0 ? path : null;
        }

        /// <summary>The active install's User folder, null when it cannot be found.</summary>
        public static string? LiveUserDir()
        {
            string? reg = ActiveIniOverride(out bool overridden) ?? (overridden ? null : RegistryActiveIni());
            if (reg != null)
            {
                string? user = CteditUser(reg);
                if (user != null && WinPath.IsDir(user))
                    return user;
            }
            if (overridden)
                return null;
            var cands = new List<(long mtime, string user)>();
            try
            {
                foreach (string d in Directory.GetDirectories(@"C:\ProgramData", "AVEVA Plant SCADA*"))
                {
                    string ini = Path.Combine(d, "Config", "citect.ini");
                    if (!File.Exists(ini))
                        continue;
                    string? user = CteditUser(ini);
                    if (user != null && WinPath.IsDir(user))
                    {
                        long mt = 0;
                        try
                        {
                            mt = File.GetLastWriteTimeUtc(ini).Ticks;
                        }
                        catch (Exception) { }
                        cands.Add((mt, user));
                    }
                }
            }
            catch (Exception e)
            {
                EngineNotes.Swallowed("CitectIni.Installs", e);
            }
            if (cands.Count == 0)
                return null;
            cands.Sort(
                (a, b) =>
                {
                    int c = b.mtime.CompareTo(a.mtime);
                    return c != 0 ? c : string.CompareOrdinal(b.user, a.user);
                }
            );
            return cands[0].user;
        }

        private static string? ActiveIniOverride(out bool overridden)
        {
            string? env = Environment.GetEnvironmentVariable(IniOverrideVariable);
            overridden = env != null;
            if (env == null)
                return null;
            return env.Length > 0 && WinPath.IsFile(env) ? env : null;
        }

        /// <summary>The ACTIVE install's citect.ini (the registry's, else Config next to the
        /// live User folder), null when there is none.</summary>
        public static string? ActiveIniPath()
        {
            string? ini = ActiveIniOverride(out bool overridden);
            if (overridden)
                return ini;
            ini = RegistryActiveIni();
            if (ini == null)
            {
                string? user = LiveUserDir();
                ini = user != null ? WinPath.Join(WinPath.DirName(user), "Config", "citect.ini") : null;
            }
            return ini != null && WinPath.IsFile(ini) ? ini : null;
        }

        /// <summary>A value of the ACTIVE citect.ini: section and key without case, like
        /// GetPrivateProfileString; <paramref name="def"/> when absent.</summary>
        public static string IniValue(string section, string key, string def = "")
        {
            string? ini = ActiveIniPath();
            return ini == null ? def : IniValueIn(ini, section, key, def);
        }

        /// <summary>A value of one ini file: '[section]' lines, 'key = value' lines, blanks
        /// around section names, keys and values ignored.</summary>
        public static string IniValueIn(string ini, string section, string key, string def = "")
        {
            string? text = ReadText(ini, TextCodec.Decode1252);
            if (text == null)
                return def;
            string? sec = null;
            string wantSec = CitectText.Lower(section),
                wantKey = CitectText.Lower(key);
            foreach (string ln in SplitLines(text))
            {
                string s = CitectText.Trim(ln);
                if (s.StartsWith("[", StringComparison.Ordinal) && s.EndsWith("]", StringComparison.Ordinal))
                {
                    sec = CitectText.Lower(CitectText.Trim(s.Substring(1, s.Length - 2)));
                }
                else if (sec == wantSec && s.IndexOf('=') >= 0)
                {
                    int eq = s.IndexOf('=');
                    if (CitectText.Lower(CitectText.Trim(s.Substring(0, eq))) == wantKey)
                        return CitectText.Trim(s.Substring(eq + 1));
                }
            }
            return def;
        }

        /// <summary>The lines of a text: each ends at \r\n, \n or \r (a final line break makes
        /// no empty line).</summary>
        public static List<string> SplitLines(string text)
        {
            var o = new List<string>();
            int start = 0,
                i = 0,
                n = text.Length;
            while (i < n)
            {
                char c = text[i];
                if (c != '\n' && c != '\r')
                {
                    i++;
                    continue;
                }
                o.Add(text.Substring(start, i - start));
                if (c == '\r' && i + 1 < n && text[i + 1] == '\n')
                    i++;
                i++;
                start = i;
            }
            if (start < n)
                o.Add(text.Substring(start));
            return o;
        }
    }
}
