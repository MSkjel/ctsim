using System;
using System.Collections.Generic;
using System.Globalization;
using CicodeSim.Project;

// The runtime's super genie association rules:
// - the association name of a reference text = the text between its FIRST and LAST
//   '?' (one '?': the text after it), the whole text when it has none. No trim, no type word: the
//   compiler strips '?INT 1?' to '?1?' in a page's TAGREFs (corpus: Example !EnergyStartStop.RDB).
// - the active association of a window by that name (ASCII letters without case).
// - a 'partial' argument has two '?' and text before the first or after the last.
// - the reference an argument names = the text before its first '?' + the
//   association's tag + the text after its last '?'; an '[n]' in it (not for a literal) is the
//   array index, taken out of the text.
// - the effective cluster of a reference: a given cluster (403 when
//   unknown), a 'Cluster.' prefix (404 when it is not the given one), else the cluster the
// project's tag configuration gives (THE cluster when the project
//   has one, else the tag's own), else the calling task's (none in the simulator: verify).
// - the store behind Ass and AssMetadata (the error a tag check leaves, the cluster
//   and the tag kept), an argument resolved for AssInfo /
//   AssGetProperty / AssScaleStr / AssFmt, the TagName / ClusterName / FullName /
//   AssFullName texts.
namespace CicodeSim.World.Display
{
    /// <summary>An association argument resolved: the reference, the array index
    /// (-1 none), the cluster, 0 or the error.</summary>
    public struct AssResolved
    {
        public AssEntry Entry;
        public string Tag;
        public int Index;
        public string Cluster;
        public int Error;
    }

    /// <summary>The association rules.</summary>
    public static class AssRules
    {
        /// <summary>The longest text the runtime keeps (254 characters).</summary>
        private const int TextMax = 254;

        private static string Cut(string s) => s.Length > TextMax ? s.Substring(0, TextMax) : s;

        /// <summary>The association name of a reference text: the text between its
        /// first and last '?' (one '?': the text after it), else the whole text.</summary>
        public static string NameOf(string text)
        {
            text = Cut(text ?? "");
            int a = text.IndexOf('?');
            if (a < 0)
                return text;
            int b = text.LastIndexOf('?');
            return a == b ? text.Substring(a + 1) : text.Substring(a + 1, b - a - 1);
        }

        /// <summary>The argument is a partial reference: two '?' and text outside
        /// them ('?Equip?.PV', 'a?1?').</summary>
        public static bool IsPartial(string arg)
        {
            int a = arg.IndexOf('?');
            if (a < 0)
                return false;
            int b = arg.LastIndexOf('?');
            if (a == b)
                return false;
            return !(a == 0 && b == arg.Length - 1);
        }

        /// <summary>The active association of window <paramref name="w"/> a reference text names;
        /// null when none.</summary>
        public static AssEntry? Active(Window? w, string text)
        {
            if (w == null)
                return null;
            string name = NameOf(text);
            return w.Associations.TryGetValue(name, out var e) ? e : null;
        }

        /// <summary>C atoi of a text (blanks, a sign, digits).</summary>
        private static int Atoi(string s, int from)
        {
            int i = from;
            while (i < s.Length && (s[i] == ' ' || (s[i] >= '\t' && s[i] <= '\r')))
                i++;
            bool neg = false;
            if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            {
                neg = s[i] == '-';
                i++;
            }
            long v = 0;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9' && v < 0x100000000L)
            {
                v = v * 10 + (s[i] - '0');
                i++;
            }
            return (int)(neg ? -v : v);
        }

        /// <summary>The reference an argument names through its association.</summary>
        public static string Compose(string arg, AssEntry e, out int index)
        {
            index = -1;
            arg = Cut(arg ?? "");
            int a = arg.IndexOf('?');
            string s = a < 0 ? e.Tag : arg.Substring(0, a) + e.Tag + arg.Substring(arg.LastIndexOf('?') + 1);
            s = Cut(s);
            if (!e.Literal)
            {
                int l = s.IndexOf('['),
                    r = s.IndexOf(']');
                if (l >= 0 && r >= 0)
                {
                    index = Atoi(s, l + 1);
                    // the text cut at '[' and at ']', joined with the text after ']'
                    s =
                        r > l
                            ? s.Substring(0, l) + s.Substring(r + 1)
                            : s.Substring(0, r) + s.Substring(r + 1, l - r - 1);
                }
            }
            return s;
        }

        // ------------------------------------------------------------------ clusters
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
            World,
            List<string>
        > ClusterCache = new System.Runtime.CompilerServices.ConditionalWeakTable<World, List<string>>();

        private static readonly List<string> NoClusters = new List<string>();

        /// <summary>The unit's clusters (cluster.dbf, each name once, configured spelling; read
        /// once per World). Do not change the list.</summary>
        public static List<string> Clusters(World? world)
        {
            if (world == null)
                return NoClusters;
            return ClusterCache.GetValue(world, w =>
            {
                var o = new List<string>();
                foreach (string c in w.Unit.Clusters())
                    if (o.FindIndex(x => CitectText.SameName(x, c)) < 0)
                        o.Add(c);
                return o;
            });
        }

        private static int IndexOf(List<string> names, string name)
        {
            for (int i = 0; i < names.Count; i++)
                if (CitectText.SameName(names[i], name))
                    return i;
            return -1;
        }

        /// <summary>The tag configuration's cluster of a reference.</summary>
        public static string? RdbCluster(World? world, List<string> names, string tag)
        {
            if (names.Count == 1)
                return names[0];
            if (world == null)
                return null;
            string? c = world.Tags.Find(tag)?.Cluster;
            if (string.IsNullOrEmpty(c))
                c = world.Equipment.Get(tag)?.Cluster;
            if (string.IsNullOrEmpty(c))
                return null;
            int i = IndexOf(names, c!);
            return i < 0 ? c : names[i];
        }

        /// <summary>The configured spelling of a cluster of the project, null when it is none
        /// (the store looks the name up in the cluster table).</summary>
        public static string? KnownCluster(World? world, string? name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            var names = Clusters(world);
            int i = IndexOf(names, name!);
            return i < 0 ? null : names[i];
        }

        /// <summary>The name (without an '[n]' index) is a local variable. A
        /// local variable has no cluster: the effective cluster is none for it,
        /// and the store does not check it (0).</summary>
        public static bool IsLocal(World? world, string tag)
        {
            if (world == null || string.IsNullOrEmpty(tag))
                return false;
            string name = tag;
            int l = name.IndexOf('[');
            if (l >= 0)
                name = name.Substring(0, l);
            name = CitectText.Trim(name);
            return name.Length > 0 && world.Tags.FindName(name)?.Kind == TagDef.Local;
        }

        /// <summary>The effective cluster of a reference: (0 or the error, the cluster or null,
        /// the tag without its cluster prefix).</summary>
        public static int EffectiveCluster(
            World? world,
            List<string> names,
            string clusterArg,
            string tag,
            out string? cluster,
            out string tagOut
        )
        {
            cluster = null;
            tagOut = tag;
            int dot = tag.IndexOf('.');
            bool hadDot = dot >= 0;
            string after = hadDot ? tag.Substring(dot + 1) : tag;
            // a prefix of 1..31 characters that names a cluster
            int pIdx = hadDot && dot > 0 && dot < 32 ? IndexOf(names, tag.Substring(0, dot)) : -1;
            if (string.IsNullOrEmpty(clusterArg))
            {
                if (pIdx >= 0)
                {
                    cluster = names[pIdx];
                    tagOut = after;
                    return 0;
                }
                if (hadDot && tag[0] == '.')
                {
                    tagOut = after;
                    return 403;
                }
            }
            else
            {
                int cIdx = IndexOf(names, clusterArg);
                if (cIdx < 0)
                {
                    tagOut = "";
                    return 403;
                }
                if (pIdx < 0)
                {
                    if (hadDot && tag[0] == '.')
                    {
                        tagOut = after;
                        return 403;
                    }
                }
                else
                {
                    tagOut = after;
                    if (pIdx != cIdx)
                        return 404;
                }
                cluster = names[cIdx];
                return 0;
            }
            // a local variable has none (before the tag configuration, also in a
            // one-cluster project); else the tag configuration, then the task's cluster
            // (none here, verify)
            if (IsLocal(world, tagOut))
                return 0;
            cluster = RdbCluster(world, names, tagOut);
            return 0;
        }

        // ------------------------------------------------------------------ the store
        /// <summary>The error the association store keeps for a tag.</summary>
        public static int StoreCheck(
            World? world,
            string tag,
            string clusterArg,
            out string cluster,
            out string tagOut
        )
        {
            // the store's check is no use of the name by code (ctsim/usedTags): a page
            // association's default ('__Type' = '0') is checked when the page shows
            if (world != null)
            {
                string c = "",
                    t = "";
                int e = world.Tags.NotUsed(() => StoreCheckCore(world, tag, clusterArg, out c, out t));
                cluster = c;
                tagOut = t;
                return e;
            }
            return StoreCheckCore(world, tag, clusterArg, out cluster, out tagOut);
        }

        private static int StoreCheckCore(
            World? world,
            string tag,
            string clusterArg,
            out string cluster,
            out string tagOut
        )
        {
            var names = Clusters(world);
            int err = EffectiveCluster(world, names, clusterArg ?? "", tag, out string? cl, out tagOut);
            // the cluster table's entry of the name found (none found: none kept)
            cluster = KnownCluster(world, cl) ?? "";
            if (err != 0 || world == null)
                return err;
            // no cluster and a local variable: no check
            if (cl == null && IsLocal(world, tagOut))
                return 0;
            // the check (on: the live runtime's Ass returned 289 for an
            // equipment name, conformance/pages FP_* cases)
            string chk = cluster;
            if (chk.Length == 0)
            {
                // the task's cluster (none here), else the project's only cluster
                if (names.Count == 1)
                    chk = names[0];
                else if (names.Count > 1)
                    err = 417;
            }
            // the runtime finds variable tags, not local
            // variables: live, Ass(-2, "LC", "Cluster1.SA_DisableLabel", 0) = 424 while
            // Ass(-2, "LV", "SA_DisableLabel", 0) = 0 (the local variable test above)
            var found = world.Tags.Resolve(tagOut, "", "", chk);
            if (found.Found && found.Tag?.Kind != TagDef.Local)
                return err;
            if (chk.Length == 0 && names.Count > 1)
                return 274; // without a cluster
            return tagOut.IndexOf('.') < 0 ? 424 : 289;
        }

        // ------------------------------------------------------------------ resolving an argument
        /// <summary>the reference, index and cluster an argument names through the
        /// association <paramref name="e"/>; <paramref name="translate"/> = the Equip.Item replaced
        /// by its variable tag (the AssInfo / AssGetProperty call).</summary>
        public static AssResolved Resolve(
            World? world,
            string arg,
            AssEntry e,
            bool translate = true,
            string defaultCluster = ""
        )
        {
            var r = new AssResolved
            {
                Entry = e,
                Cluster = "",
                Index = -1,
            };
            r.Tag = Compose(arg, e, out r.Index);
            if (e.Literal)
                return r;
            var names = Clusters(world);
            int err = EffectiveCluster(world, names, e.Cluster, r.Tag, out string? cl, out string tagOut);
            if (err == 404)
                err = EffectiveCluster(world, names, "", r.Tag, out cl, out tagOut);
            if (err != 0)
            {
                r.Error = err;
                return r;
            }
            if (cl != null)
                r.Tag = tagOut;
            // the cluster found, else the association's own, else the tag
            // configuration's, else the caller's default (the window's page cluster)
            bool defaulted = false;
            string c = cl ?? "";
            // a local variable: no cluster and no error (the runtime's comes first;
            // the association's own cluster would have been found above)
            if (c.Length == 0 && IsLocal(world, r.Tag))
                return r;
            if (c.Length == 0)
            {
                if (e.Cluster.Length > 0)
                {
                    int i = IndexOf(names, e.Cluster);
                    if (i < 0)
                    {
                        r.Error = 403;
                        return r;
                    }
                    c = names[i];
                }
                else if (r.Tag.Length == 0)
                {
                    r.Error = 274;
                    return r;
                }
                else
                {
                    string? rc = RdbCluster(world, names, r.Tag);
                    if (rc == null && !string.IsNullOrEmpty(defaultCluster))
                    {
                        int i = IndexOf(names, defaultCluster);
                        if (i < 0)
                        {
                            r.Error = 403;
                            return r;
                        }
                        rc = names[i];
                        defaulted = true;
                    }
                    c = rc ?? "";
                }
            }
            e.ClusterDefaulted = defaulted;
            r.Cluster = c;
            if (translate && world != null && r.Tag.IndexOf('.') >= 0)
            {
                // an Equip.Item becomes its variable tag
                var d = world.Tags.FindItem(r.Tag);
                if (
                    d != null
                    && (c.Length == 0 || d.Cluster.Length == 0 || CitectText.SameName(d.Cluster, c))
                )
                    r.Tag = d.Name;
            }
            return r;
        }

        /// <summary>The bad-quality errors that blank a TagName / ClusterName / FullName answer
        /// (403, 53, 402, 405, 424).</summary>
        private static bool BlanksAnswer(int code) =>
            code == 403 || code == 0x35 || code == 402 || code == 405 || code == 424;

        /// <summary>The text of an association reference.</summary>
        public static string Text(
            World? world,
            in AssResolved r,
            bool partial,
            int mode,
            Func<ulong, int>? qualityError = null
        )
        {
            var e = r.Entry;
            if (e == null)
                return "";
            string tag = r.Tag,
                cluster = r.Cluster;
            TagDef? def = null;
            if (world != null && tag.Length > 0)
            {
                var res = world.Tags.Resolve(tag, "", "", cluster);
                // a local variable has no cluster: named with one it has no subscription (live:
                // AssInfo of 'Cluster1.SA_DisableLabel', stored with 424, type 0 = "")
                if (res.Found && !(cluster.Length > 0 && res.Tag?.Kind == TagDef.Local))
                    def = res.Tag;
            }
            if ((mode & 4) == 0)
            {
                if (def == null)
                {
                    // no subscription behind the reference
                    if (!partial && e.Error != 0)
                        return "";
                }
                else if (qualityError != null)
                {
                    ulong q = world!.Tags.Peek(def.Name).quality;
                    if (BlanksAnswer(qualityError(q)))
                        return "";
                }
            }
            string o = "";
            if ((mode & 1) != 0)
            {
                o = Cut(cluster);
                if (o.Length > 0)
                {
                    if ((mode & 2) == 0)
                        return o;
                    o += ".";
                }
                else if ((mode & 2) == 0)
                    return "";
            }
            else if ((mode & 2) == 0)
                return "";
            // the tag's TagName property: the configured name, else the text
            o += def != null && def.Kind != TagDef.Local ? def.Name : tag;
            if (r.Index >= 0)
                o += "[" + r.Index.ToString(CultureInfo.InvariantCulture) + "]";
            return Cut(o);
        }

        /// <summary>the TagGetProperty name of an AssInfo type (a number as text or
        /// a property name, compared without case), null for an unknown one (348).</summary>
        public static string? PropertyOf(string type, out int number)
        {
            number =
                string.Equals(type, "0", StringComparison.OrdinalIgnoreCase) ? 0
                : ShortAtoi(type) > 0 ? ShortAtoi(type)
                : -1;
            int k = number;
            bool Is(string name, int n) =>
                string.Equals(type, name, StringComparison.OrdinalIgnoreCase) || k == n;
            if (Is("TagName", 0))
                return "TagName";
            string[] names =
            {
                "Units",
                "RangeLow",
                "RangeHigh",
                "EngUnitsLow",
                "EngUnitsHigh",
                "FormatWidth",
                "FormatDecPlaces",
                "Format",
                "LogicalUnit",
                "RawType",
                "DataBitWidth",
                "UnitType",
                "UnitAddress",
                "ArraySize",
                "RecordNumber",
                "Description",
                "ClusterName",
                "FullName",
            };
            for (int i = 0; i < names.Length; i++)
                if (Is(names[i], i + 1))
                    return names[i];
            if (Is("Address", 20))
                return "Address";
            if (Is("NetworkNumber", 21))
                return "NetworkNumber";
            if (Is("Equipment", 22))
                return "Equipment";
            if (Is("Historian", 24))
                return "Historian";
            if (Is("Item", 25))
                return "Item";
            if (Is("Type", 23))
                return "Type";
            for (int i = 1; i <= 8; i++)
                if (Is("Custom" + i.ToString(CultureInfo.InvariantCulture), 25 + i))
                    return "Custom" + i.ToString(CultureInfo.InvariantCulture);
            return null;
        }

        /// <summary>(short)atoi(s).</summary>
        public static int ShortAtoi(string s) => (short)Atoi(s ?? "", 0);
    }
}
