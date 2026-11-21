using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.World;

namespace CicodeSim.Builtins
{
    /// <summary>Per-process state of the data built-ins: computer name, environment, ini overlays.</summary>
    public sealed class ParamUse
    {
        public string Section = "";
        public string Name = "";

        /// <summary>The value the last read got (or the last write set).</summary>
        public string Value = "";

        /// <summary>The default the last read passed.</summary>
        public string Default = "";

        /// <summary>Where the value came from: citect.ini, _PARAM.RDB, param.dbf, default,
        /// ParameterPut (Cicode wrote it), simulator (set in the simulator).</summary>
        public string Source = "";
        public int Reads;
        public int Writes;

        /// <summary>The functions / page objects that read or wrote it ('PageInit',
        /// 'AN 12 visible on Mixer'), at most 8, first first.</summary>
        public readonly List<string> Users = new List<string>();

        internal void AddUser(string who)
        {
            if (who.Length > 0 && Users.Count < 8 && !Users.Contains(who))
                Users.Add(who);
        }
    }

    public sealed class ProcessData
    {
        private static readonly ConditionalWeakTable<SimHost, ProcessData> All =
            new ConditionalWeakTable<SimHost, ProcessData>();

        /// <summary>The data state of <paramref name="host"/> (made on first use).</summary>
        public static ProcessData Of(SimHost host) => All.GetValue(host, h => new ProcessData(h));

        /// <summary>The data state of <paramref name="host"/> when one was made (null before its
        /// first use: a reload does not make one).</summary>
        internal static ProcessData? Existing(SimHost host) => All.TryGetValue(host, out var d) ? d : null;

        public SimHost Host { get; }

        /// <summary>The simulated computer's name: GetComputerNameA (ParameterGet's
        /// '\\name\section' lookup, the COMPUTERNAME variable). Default this machine's. Setting
        /// it also changes the COMPUTERNAME and USERDOMAIN variables that still hold the old name.</summary>
        public string ComputerName
        {
            get => _computerName;
            set
            {
                string old = _computerName;
                _computerName = value ?? "";
                foreach (string k in new[] { "COMPUTERNAME", "USERDOMAIN" })
                    if (Environment.TryGetValue(k, out string? v) && v == old)
                        Environment[k] = _computerName;
            }
        }

        private string _computerName = "";

        /// <summary>The process environment GetEnv reads. A sandbox: not the host's
        /// environment (a guess on the defaults).</summary>
        public Dictionary<string, string> Environment { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The generator of MapOpen's random names (random in the runtime);
        /// seeded so a run repeats.</summary>
        public Mulberry32 Random { get; set; } = new Mulberry32(0x0C1C0DE5);

        /// <summary>The ini files: reads of the real files (read-only) under the readable
        /// folders, writes into an in-memory copy.</summary>
        public IniFiles Ini { get; }

        internal readonly Maps.Store MapStore = new Maps.Store();
        internal readonly HandleTable<Arrays.CicodeArray> ArrayTable = new HandleTable<Arrays.CicodeArray>();
        internal readonly HandleTable<RdbTables.Cursor> RdbCursors = new HandleTable<RdbTables.Cursor>();
        internal readonly HandleTable<UsersGroups.Group> GroupTable = new HandleTable<UsersGroups.Group>();
        internal readonly HandleTable<AlarmQueries.FilterSession> FilterSessions =
            new HandleTable<AlarmQueries.FilterSession>();

        /// <summary>AlarmFilterOpen's named filters: name (case-insensitive) to the filter text.</summary>
        internal readonly Dictionary<string, string> NamedFilters = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase
        );
        internal Menus.Tree? MenuTree;

        /// <summary>The alarm definitions in record order (AlarmQueries).</summary>
        internal List<AlarmDef>? AlarmList;

        /// <summary>Variable tag to the time-stamped alarms that read it (AlarmNotifyVarChange).</summary>
        internal Dictionary<string, List<string>>? TimestampedInputs;

        /// <summary>The compiled parameter records (Params.ParamRecord); ParamUncompiled = none: param.dbf.</summary>
        internal Dictionary<string, string>? ParamIndex;
        internal bool ParamUncompiled;

        /// <summary>The parameters Cicode read (ParameterGet) or wrote (ParameterPut, ctsim/
        /// setParam), by (section, name) in lower case, in first-read order (ctsim/params).</summary>
        internal readonly Dictionary<string, ParamUse> ParamUses = new Dictionary<string, ParamUse>(
            StringComparer.Ordinal
        );
        internal readonly List<ParamUse> ParamOrder = new List<ParamUse>();

        /// <summary>Raised on every change of ParamUses (a client polls by it).</summary>
        internal int ParamSerial;

        internal ParamUse UseOf(string section, string name)
        {
            string k = CitectText.Lower(section) + "\0" + CitectText.Lower(name);
            if (!ParamUses.TryGetValue(k, out var u))
            {
                u = new ParamUse { Section = section, Name = name };
                ParamUses[k] = u;
                ParamOrder.Add(u);
            }
            return u;
        }

        /// <summary>cluster.dbf of the unit, each cluster once (Servers.Clusters).</summary>
        internal List<string>? ClusterList;

        /// <summary>units.dbf of the unit: the first row of each NAME (C-locale lower case) and
        /// of each NUMBER text (Servers.IoDevice).</summary>
        internal Dictionary<string, DbfRow>? UnitsByName,
            UnitsByNumber;

        /// <summary>The compiled tables a process loaded (path key to the file).</summary>
        internal readonly Dictionary<string, CicodeSim.Compiled.RdbFile?> Compiled = new Dictionary<
            string,
            CicodeSim.Compiled.RdbFile?
        >(StringComparer.Ordinal);

        private ProcessData(SimHost host)
        {
            Host = host;
            string name;
            try
            {
                name = System.Environment.MachineName;
            }
            catch (Exception)
            {
                name = "PLANTSCADA";
            }
            ComputerName = CitectText.Upper(name);
            Ini = new IniFiles(this);
            var e = Environment;
            e["COMPUTERNAME"] = ComputerName;
            e["USERNAME"] = "PlantSCADA";
            e["USERDOMAIN"] = ComputerName;
            e["OS"] = "Windows_NT";
            e["PROCESSOR_ARCHITECTURE"] = "x86";
            e["NUMBER_OF_PROCESSORS"] = "1";
            e["SystemDrive"] = "C:";
            e["SystemRoot"] = @"C:\Windows";
            e["windir"] = @"C:\Windows";
            e["TEMP"] = @"C:\Temp";
            e["TMP"] = @"C:\Temp";
            e["ProgramData"] = @"C:\ProgramData";
            e["ProgramFiles"] = @"C:\Program Files (x86)";
            e["CommonProgramFiles"] = @"C:\Program Files (x86)\Common Files";
        }

        /// <summary>The compiled table <paramref name="name"/> (e.g. "_PARAM") of the running
        /// project's folder, null when it is not there (an uncompiled project).</summary>
        internal CicodeSim.Compiled.RdbFile? CompiledTable(string name)
        {
            var w = Host.World;
            if (w == null)
                return null;
            string path = WinPath.Join(w.Unit.RootDir, name + ".RDB");
            string key = WinPath.CaseKey(path);
            if (Compiled.TryGetValue(key, out var f))
                return f;
            f = null;
            try
            {
                if (WinPath.IsFile(path))
                    f = CicodeSim.Compiled.RdbFile.Open(path);
            }
            catch (Exception)
            {
                f = null;
            }
            Compiled[key] = f;
            return f;
        }

        private static readonly HashSet<string> PoolNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "TEXT",
            "SCALE",
            "RUN",
            "TAGREF",
            "FUNCLOOKUP",
            "CODEVAR",
            "REQUEST",
            "CODE",
            "FUNC",
            "Library",
            "DEBUG",
            "DBFRECNO",
        };

        /// <summary>The records of the compiled table's last non-pool section whose records
        /// have more than one byte (the table itself: PARAM of _PARAM.RDB, PAGES of _PAGES.RDB).</summary>
        internal static CicodeSim.Compiled.Section? TableSection(
            CicodeSim.Compiled.RdbFile f,
            string? name = null
        )
        {
            if (name != null && f.Sections.TryGetValue(name, out var s0) && !s0.IsPool)
                return s0;
            CicodeSim.Compiled.Section? last = null;
            foreach (string n in f.Order)
            {
                if (PoolNames.Contains(n))
                    continue;
                var s = f.Sections[n];
                if (!s.IsPool)
                    last = s;
            }
            return last;
        }
    }

    /// <summary>Ini files as GetPrivateProfileString sees them, with writes kept in memory.</summary>
    public sealed class IniFiles
    {
        private readonly ProcessData _data;

        /// <summary>file key to [section] (lower case) to key (lower case) to value.</summary>
        private readonly Dictionary<string, Dictionary<string, Dictionary<string, string>>> _written =
            new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.Ordinal);
        private readonly Dictionary<
            string,
            (long mtime, long size, Dictionary<string, Dictionary<string, string>> data)
        > _read = new Dictionary<string, (long, long, Dictionary<string, Dictionary<string, string>>)>(
            StringComparer.Ordinal
        );
        private string? _citectIni;
        private bool _citectIniKnown;
        private List<string>? _readable;

        /// <summary>The key of the runtime's own citect.ini in the overlay (ParameterPut).</summary>
        public const string CitectIniKey = "<citect.ini>";

        internal IniFiles(ProcessData data)
        {
            _data = data;
        }

        /// <summary>The citect.ini the simulated runtime reads (the World's install: the active
        /// one, CICODESIM_INI overrides; null = none).</summary>
        public string? CitectIniPath
        {
            get
            {
                if (!_citectIniKnown)
                {
                    _citectIni = CitectIni.ActiveIniPath();
                    _citectIniKnown = true;
                }
                return _citectIni;
            }
            set
            {
                _citectIni = value;
                _citectIniKnown = true;
                _readable = null;
            }
        }

        /// <summary>Extra folders whose ini files may be read (a host's decision).</summary>
        public List<string> ExtraReadable { get; } = new List<string>();

        private List<string> Readable()
        {
            if (_readable != null)
                return _readable;
            var o = new List<string>();
            var w = _data.Host.World;
            if (w != null)
                foreach (var p in w.Unit.Projects)
                    o.Add(WinPath.Key(p.Dir));
            string? ini = CitectIniPath;
            if (ini != null)
                o.Add(WinPath.Key(WinPath.DirName(WinPath.Full(ini))));
            _readable = o;
            return o;
        }

        private bool MayRead(string path)
        {
            WinPath.SplitDrive(path.Replace('/', '\\'), out string drive, out string rest);
            if (drive.Length == 0 || !rest.StartsWith("\\", StringComparison.Ordinal))
                return false;
            string k = WinPath.Key(path);
            foreach (string d in Readable())
                if (k.StartsWith(d + "\\", StringComparison.Ordinal))
                    return true;
            foreach (string d in ExtraReadable)
            {
                string dk = WinPath.Key(d);
                if (k.StartsWith(dk + "\\", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static string FileKey(string path)
        {
            if (path == CitectIniKey)
                return path;
            WinPath.SplitDrive(path.Replace('/', '\\'), out string drive, out _);
            return drive.Length > 0 ? WinPath.Key(path) : WinPath.CaseKey(path);
        }

        /// <summary>The value of [section] key, and whether it's there.</summary>
        public bool TryGet(string file, string section, string key, out string value)
        {
            string fk = FileKey(file);
            string s = CitectText.Lower(CitectText.Trim(section)),
                k = CitectText.Lower(CitectText.Trim(key));
            if (
                _written.TryGetValue(fk, out var secs)
                && secs.TryGetValue(s, out var keys)
                && keys.TryGetValue(k, out string? v)
            )
            {
                value = v;
                return true;
            }
            string? real = fk == CitectIniKey ? CitectIniPath : file;
            if (real != null && (fk == CitectIniKey || MayRead(real)))
            {
                var data = ReadFile(real);
                if (data != null && data.TryGetValue(s, out var rk) && rk.TryGetValue(k, out string? rv))
                {
                    value = rv;
                    return true;
                }
            }
            value = "";
            return false;
        }

        /// <summary>WritePrivateProfileString into the in-memory copy; true (the copy always takes it).</summary>
        public bool Put(string file, string section, string key, string value)
        {
            string fk = FileKey(file);
            string s = CitectText.Lower(CitectText.Trim(section)),
                k = CitectText.Lower(CitectText.Trim(key));
            if (s.Length == 0 || k.Length == 0)
                return false;
            if (!_written.TryGetValue(fk, out var secs))
                _written[fk] = secs = new Dictionary<string, Dictionary<string, string>>(
                    StringComparer.Ordinal
                );
            if (!secs.TryGetValue(s, out var keys))
                secs[s] = keys = new Dictionary<string, string>(StringComparer.Ordinal);
            keys[k] = value;
            return true;
        }

        /// <summary>The values the program wrote: (file, section, key, value), for a host that
        /// shows the sandbox's changes.</summary>
        public IEnumerable<(string File, string Section, string Key, string Value)> Written()
        {
            foreach (var f in _written)
            foreach (var s in f.Value)
            foreach (var k in s.Value)
                yield return (f.Key, s.Key, k.Key, k.Value);
        }

        /// <summary>A real file larger than this is not read (reads as empty): an ini file is a
        /// few KB, and a project folder also holds DBF tables of 1 GB that a
        /// program could name; the simulator's own limit, not the runtime's.</summary>
        public const long MaxFileBytes = 32L << 20;

        private Dictionary<string, Dictionary<string, string>>? ReadFile(string path)
        {
            string key = WinPath.Key(path);
            var st = WinPath.Stat(path);
            if (st == null || st.Value.size > MaxFileBytes)
                return null;
            if (
                _read.TryGetValue(key, out var hit)
                && hit.mtime == st.Value.mtime
                && hit.size == st.Value.size
            )
                return hit.data;
            string text;
            try
            {
                byte[] b = File.ReadAllBytes(path);
                text = Cp1252.Decode(b);
            }
            catch (Exception)
            {
                return null;
            }
            var data = Parse(text);
            _read[key] = (st.Value.mtime, st.Value.size, data);
            return data;
        }

        /// <summary>An ini text: '[section]' lines and 'key=value' lines; the first of two equal
        /// keys counts; lines before the first section and lines without '=' are ignored.</summary>
        internal static Dictionary<string, Dictionary<string, string>> Parse(string text)
        {
            var o = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            Dictionary<string, string>? cur = null;
            foreach (string ln in CitectIni.SplitLines(text))
            {
                string s = CitectText.Trim(ln);
                if (s.Length == 0)
                    continue;
                if (s[0] == '[')
                {
                    int close = s.IndexOf(']');
                    string name = CitectText.Lower(
                        CitectText.Trim(close > 0 ? s.Substring(1, close - 1) : s.Substring(1))
                    );
                    if (!o.TryGetValue(name, out cur))
                        o[name] = cur = new Dictionary<string, string>(StringComparer.Ordinal);
                    continue;
                }
                if (cur == null || s[0] == ';')
                    continue;
                int eq = s.IndexOf('=');
                if (eq < 0)
                    continue;
                string k = CitectText.Lower(CitectText.Trim(s.Substring(0, eq)));
                string v = CitectText.Trim(s.Substring(eq + 1));
                if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[v.Length - 1] == v[0])
                    v = v.Substring(1, v.Length - 2);
                if (k.Length > 0 && !cur.ContainsKey(k))
                    cur[k] = v;
            }
            return o;
        }
    }

    /// <summary>byte-wise string order of the C library (strcmp / std::less of std::string on
    /// Windows-1252 bytes): what std::map keys sort by.</summary>
    internal sealed class CByteOrder : IComparer<string>
    {
        public static readonly CByteOrder Instance = new CByteOrder();

        public int Compare(string? x, string? y)
        {
            x ??= "";
            y ??= "";
            int n = Math.Min(x.Length, y.Length);
            for (int i = 0; i < n; i++)
            {
                int a = Cp1252.Byte(x[i]),
                    b = Cp1252.Byte(y[i]);
                if (a != b)
                    return a < b ? -1 : 1;
            }
            return x.Length.CompareTo(y.Length);
        }
    }

    /// <summary>The C library helpers the data built-ins share.</summary>
    internal static class C
    {
        /// <summary>_stricmp in the C locale: only A..Z fold.</summary>
        public static bool IEq(string? a, string? b) =>
            string.Equals(A.CLower(a ?? ""), A.CLower(b ?? ""), StringComparison.Ordinal);

        /// <summary>atol of a text (leading blanks, a sign, digits; the C library clamps).</summary>
        public static long Atol(string s) => Crt.Atol(s);

        public static string Inv(long v) => v.ToString(CultureInfo.InvariantCulture);

        /// <summary>A string result cut to <paramref name="max"/> characters (the buffer size - 1).</summary>
        public static string Cut(string s, int max = Types.StringMax) =>
            s.Length > max ? s.Substring(0, max) : s;
    }
}
