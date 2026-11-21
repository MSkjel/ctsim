using System;
using System.Collections.Generic;
using System.IO;
using CicodeSim.Compiled;
using CicodeSim.Project;

namespace CicodeSim.World.Display
{
    /// <summary>A page's own record from its compiled .RDB.</summary>
    public sealed class PageRecord
    {
        public string Name = "";
        public string Alias = "";
        public string Title = "";

        /// <summary>The page's area (Control AREAS, 0 = every area).</summary>
        public int Areas;
        public string BgndFile = "";
        public string SymbFile = "";
        public string NextPage = "";
        public string PrevPage = "";

        /// <summary>Control SCANTIME: -1 = [Page]ScanTime.</summary>
        public int ScanTime = -1;
        public string LogDevice = "";
        public string Cluster = "";

        /// <summary>Control IGNOREVQ: -1 = [Page]IgnoreValueQuality, 0 #COM, 1 No, 2 background.</summary>
        public int IgnoreVq = -1;
        public string ConType = "";
        public long LibRdbTime;
        public string Comment = "";
        public string Parent = "";

        /// <summary>The page .RDB, "" when the page has no compiled file.</summary>
        public string RdbPath = "";

        /// <summary>The Control record of a page .RDB (null when the file has none: error 265).</summary>
        public static PageRecord? FromRdb(RdbFile f)
        {
            if (!f.Sections.TryGetValue("Control", out var s) || s.IsPool || s.NRec < 1)
                return null;
            var rec = f.Records("Control")[0];
            string S(string k) => rec.TryGetValue(k, out var v) && v is string x ? x : "";
            var p = new PageRecord
            {
                Name = S("NAME"),
                Alias = S("ALIAS"),
                Title = S("TITLE"),
                BgndFile = S("BGND_FILE"),
                SymbFile = S("SYMB_FILE"),
                NextPage = S("NEXT_PAGE"),
                PrevPage = S("PREV_PAGE"),
                LogDevice = S("LOGDEVICE"),
                Cluster = S("CLUSTER"),
                ConType = S("CON_TYPE"),
                RdbPath = f.Path,
            };
            // the raw record: SCANTIME is a 4-byte field (type 3, corpus), AREAS a 2-byte area
            // (type 6), IGNOREVQ i16 (corpus: WTP_INTAKE_HD1080.RDB Control descriptors)
            int start = s.Data;
            foreach (var fd in s.Fields)
            {
                int a = start + fd.Offset;
                if (a + Math.Max(fd.Size, 1) > f.Data.Length)
                    continue;
                switch (fd.Name)
                {
                    case "SCANTIME":
                        p.ScanTime =
                            fd.Size >= 4 ? BitConverter.ToInt32(f.Data, a) : BitConverter.ToInt16(f.Data, a);
                        break;
                    case "AREAS":
                        p.Areas = fd.Size >= 2 ? BitConverter.ToInt16(f.Data, a) : f.Data[a];
                        break;
                    case "IGNOREVQ":
                        p.IgnoreVq = BitConverter.ToInt16(f.Data, a);
                        break;
                    case "LIBRDB_TIME":
                        p.LibRdbTime = fd.Size >= 4 ? BitConverter.ToUInt32(f.Data, a) : 0;
                        break;
                }
            }
            return p;
        }
    }

    /// <summary>The page catalogue of the running project.</summary>
    public sealed class PageCatalogue
    {
        public string Dir { get; }
        private readonly List<PageRecord> _pages = new List<PageRecord>();
        private readonly Dictionary<string, PageRecord> _byName = new Dictionary<string, PageRecord>(
            StringComparer.OrdinalIgnoreCase
        );
        private readonly Dictionary<string, PageRecord?> _records = new Dictionary<string, PageRecord?>(
            StringComparer.OrdinalIgnoreCase
        );

        /// <summary>The catalogue file was read.</summary>
        public bool Loaded { get; }

        public PageCatalogue(string dir)
        {
            Dir = dir;
            string path = Path.Combine(dir, "_PAGES.RDB");
            if (!File.Exists(path))
                return;
            RdbFile f;
            try
            {
                f = RdbFile.Open(path);
            }
            catch (Exception e) when (e is RdbError || e is IOException)
            {
                return;
            }
            Loaded = true;
            foreach (var r in f.Records("PAGES"))
            {
                string S(string k) => r.TryGetValue(k, out var v) && v is string x ? x : "";
                var p = new PageRecord
                {
                    Name = S("NAME"),
                    Title = S("TITLE"),
                    NextPage = S("NEXT_PAGE"),
                    PrevPage = S("PREV_PAGE"),
                    Comment = S("COMMENT"),
                    ConType = S("CON_TYPE"),
                    Parent = S("PARENT"),
                };
                if (p.Name.Length == 0 || _byName.ContainsKey(p.Name))
                    continue;
                _pages.Add(p);
                _byName[p.Name] = p;
            }
        }

        /// <summary>The pages in catalogue order.</summary>
        public IReadOnlyList<PageRecord> Pages => _pages;

        public bool Exists(string name) => _byName.ContainsKey(CitectText.Trim(name));

        /// <summary>The catalogue entry, null when the page is not in it.</summary>
        public PageRecord? Entry(string name) =>
            _byName.TryGetValue(CitectText.Trim(name), out var p) ? p : null;

        /// <summary>The path of a page's compiled file (it need not exist); "" for a name no file
        /// can have (a path separator, wildcard or a character Windows refuses: such a page is
        /// never in the catalogue, and .NET Framework's Path would throw on it).</summary>
        public string RdbPath(string name)
        {
            name = CitectText.Trim(name);
            if (name.Length == 0 || name.IndexOfAny(BadNameChars) >= 0)
                return "";
            foreach (char c in name)
                if (c < ' ')
                    return "";
            return Path.Combine(Dir, name + ".RDB");
        }

        private static readonly char[] BadNameChars = { '"', '<', '>', '|', '*', '?', '/', '\\', ':' };

        /// <summary>The page's Control record from its .RDB with the catalogue's comment / parent;
        /// null when the file is missing (error 261) or has no Control section (265). Cached
        /// per name.</summary>
        public PageRecord? Record(string name)
        {
            name = CitectText.Trim(name);
            if (_records.TryGetValue(name, out var hit))
                return hit;
            PageRecord? r = null;
            string path = RdbPath(name);
            if (path.Length > 0 && File.Exists(path))
            {
                try
                {
                    r = PageRecord.FromRdb(RdbFile.Open(path));
                }
                catch (Exception e) when (e is RdbError || e is IOException)
                {
                    r = null;
                }
                var e2 = Entry(name);
                if (r != null && e2 != null)
                {
                    r.Comment = e2.Comment;
                    r.Parent = e2.Parent;
                }
            }
            _records[name] = r;
            return r;
        }
    }
}
