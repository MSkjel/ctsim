using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Xml;
using CicodeSim.Project;
using CicodeSim.World.Display;

namespace CicodeSim.Builtins
{
    /// <summary>The multi-monitor screen profile (PageInfo(30)).</summary>
    internal static class ScreenProfile
    {
        internal sealed class Screen
        {
            public string Name = "";
            public int Width,
                Height;
        }

        private sealed class Cache
        {
            public string Profile = "\0";
            public List<Screen> Screens = new List<Screen>();
        }

        private static readonly ConditionalWeakTable<ProcessData, Cache> Caches =
            new ConditionalWeakTable<ProcessData, Cache>();

        /// <summary>The screens of the profile in force (empty without one).</summary>
        internal static List<Screen> Screens(SimHost h)
        {
            var d = ProcessData.Of(h);
            var c = Caches.GetValue(d, _ => new Cache());
            string profile = CitectText.Trim(Params.Get(h, "MultiMonitors", "ScreenProfile") ?? "");
            if (!string.Equals(profile, c.Profile, StringComparison.Ordinal))
            {
                c.Profile = profile;
                c.Screens = profile.Length > 0 ? Read(h, profile) : new List<Screen>();
            }
            return c.Screens;
        }

        /// <summary>The screen name of a window ('' without a profile).</summary>
        internal static string NameOf(SimHost h, Window w)
        {
            var s = Screens(h);
            if (s.Count == 0)
                return "";
            int x = 0;
            foreach (var sc in s)
            {
                if (w.X >= x && w.X < x + Math.Max(1, sc.Width))
                    return sc.Name;
                x += Math.Max(1, sc.Width);
            }
            return s[0].Name;
        }

        private static List<Screen> Read(SimHost h, string profile)
        {
            var o = new List<Screen>();
            var world = h.World;
            if (world == null)
                return o;
            string file = profile.EndsWith(".cts", StringComparison.OrdinalIgnoreCase)
                ? profile
                : profile + ".cts";
            var dirs = new List<string> { world.Unit.ProjectDir };
            foreach (var p in world.Unit.Search)
                dirs.Add(p.Dir);
            foreach (string dir in dirs)
            {
                string path = WinPath.Join(dir, file);
                if (!WinPath.IsFile(path))
                    continue;
                try
                {
                    var doc = new XmlDocument { XmlResolver = null }; // a project file: no external entities
                    doc.Load(path);
                    var list = doc.GetElementsByTagName("Screen");
                    foreach (XmlNode n in list)
                    {
                        if (!(n is XmlElement e))
                            continue;
                        int.TryParse(e.GetAttribute("Width"), out int wd);
                        int.TryParse(e.GetAttribute("Height"), out int ht);
                        o.Add(
                            new Screen
                            {
                                Name = e.GetAttribute("Name"),
                                Width = wd,
                                Height = ht,
                            }
                        );
                    }
                }
                catch (Exception e)
                    when (e is XmlException || e is IOException || e is UnauthorizedAccessException)
                {
                    continue;
                }
                if (o.Count > 0)
                    break;
            }
            return o;
        }
    }
}
