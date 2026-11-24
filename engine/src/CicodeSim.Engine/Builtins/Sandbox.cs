using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CicodeSim.Project;

namespace CicodeSim.Builtins
{
    /// <summary>Options of a simulation's sandbox.</summary>
    public sealed class SandboxOptions
    {
        /// <summary>The folder writes go to; null = %LOCALAPPDATA%\cicode-sim\sandbox\&lt;Id&gt;,
        /// created at the first write. Ignored with <see cref="InMemory"/>.</summary>
        public string? Folder;

        /// <summary>Keep written files in memory only (sweeps, tests): nothing on any disk.</summary>
        public bool InMemory;

        /// <summary>The sandbox id (the folder name under %LOCALAPPDATA%\cicode-sim\sandbox);
        /// null = &lt;project&gt;-&lt;pid&gt;-&lt;n&gt;.</summary>
        public string? Id;

        /// <summary>Real folders Cicode may read (never write); null = every project folder of
        /// the World's unit and the folder of the active citect.ini.</summary>
        public IList<string>? ReadRoots;

        /// <summary>Every real file is readable (still never written).</summary>
        public bool ReadAnywhere;

        /// <summary>A file may be created in any folder; false = the folder must exist (as a
        /// real folder, a readable root or a folder of a sandbox file), like the real system.</summary>
        public bool AnyFolder;

        /// <summary>The folder relative paths start from; null = the run folder ([RUN], the
        /// compiled project's folder), else C:\.</summary>
        public string? CurrentDirectory;

        /// <summary>Most bytes all sandbox files may hold together (a write past it fails like a
        /// full disk).</summary>
        public long MaxBytes = 64L << 20;

        /// <summary>Most files the sandbox may hold.</summary>
        public int MaxFiles = 4096;

        /// <summary>Most files Cicode may have open at once (FileOpen past it fails, 262).</summary>
        public int MaxOpenFiles = 512;
    }

    /// <summary>A change of a sandbox file: op = create / write / delete / copy / rename / time;
    /// Path the file, Other the source (copy) or the new name (rename), Bytes for a write.</summary>
    public sealed class FileEvent
    {
        public string Op = "";
        public string Path = "";
        public string Other = "";
        public long Bytes;

        public override string ToString() => Op + " " + Path + (Other.Length > 0 ? " " + Other : "");
    }

    /// <summary>A file or folder a sandbox search found.</summary>
    public readonly struct Found
    {
        /// <summary>The name in its folder.</summary>
        public readonly string Name;

        /// <summary>Windows file attributes (0x10 folder, 0x20 archive, 0x1 read-only ...).</summary>
        public readonly int Attributes;

        /// <summary>A sandbox file (else a real one under a readable root).</summary>
        public readonly bool InSandbox;

        public Found(string name, int attributes, bool inSandbox)
        {
            Name = name;
            Attributes = attributes;
            InSandbox = inSandbox;
        }

        public bool IsFolder => (Attributes & 0x10) != 0;
    }

    /// <summary>An open file of the sandbox (a FileOpen handle's file).</summary>
    public sealed class SandboxFile
    {
        internal Sandbox.Entry? Entry;
        internal string? Real;

        /// <summary>The virtual path it was opened as.</summary>
        public string Path = "";
        public long Position;
        public bool CanRead,
            CanWrite;
    }

    /// <summary>
    /// The simulation's virtual file system: real files are read-only, writes go to the sandbox.
    /// </summary>
    public sealed class Sandbox
    {
        internal sealed class Entry
        {
            public string Path = "";
            public string Key = "";
            public byte[] Data = Array.Empty<byte>();
            public int Length;
            public string? Backing;

            /// <summary>Last write time (FILETIME, UTC).</summary>
            public ulong Time;
            public int Attributes = 0x20;
        }

        private static int _seq;
        private readonly SandboxOptions _o;
        private readonly Dictionary<string, Entry> _files = new Dictionary<string, Entry>(
            StringComparer.Ordinal
        );
        private readonly List<string> _roots = new List<string>();
        private string? _folder;
        private long _bytes;

        /// <summary>The folder relative paths start from.</summary>
        public string CurrentDirectory { get; }

        /// <summary>The sandbox id.</summary>
        public string Id { get; }
        public bool InMemory => _o.InMemory;
        public IReadOnlyList<string> ReadRoots => _roots;
        public long MaxBytes => _o.MaxBytes;
        public int MaxOpenFiles => _o.MaxOpenFiles;

        /// <summary>Every change of a sandbox file (for the trace and the viewers).</summary>
        public event Action<FileEvent>? Changed;

        public Sandbox(SandboxOptions? options = null, World.World? world = null)
        {
            _o = options ?? new SandboxOptions();
            string project = world?.Root ?? "sim";
            // the id is one folder name (a given one too: "..\x" must not leave the sandboxes' folder)
            Id =
                _o.Id != null
                    ? SafeName(_o.Id)
                    : SafeName(project)
                        + "-"
                        + System
                            .Diagnostics.Process.GetCurrentProcess()
                            .Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "-"
                        + System
                            .Threading.Interlocked.Increment(ref _seq)
                            .ToString(System.Globalization.CultureInfo.InvariantCulture);
            CurrentDirectory = Normal(_o.CurrentDirectory ?? world?.Unit.RootDir ?? "C:\\", "C:\\");
            if (_o.ReadRoots != null)
            {
                foreach (string r in _o.ReadRoots)
                    if (r.Length > 0)
                        _roots.Add(Normal(r, CurrentDirectory));
            }
            else
            {
                if (world != null)
                    foreach (var p in world.Unit.Projects)
                        _roots.Add(Normal(p.Dir, CurrentDirectory));
                string? ini = CitectIni.ActiveIniPath();
                if (ini != null)
                    _roots.Add(Normal(WinPath.DirName(ini), CurrentDirectory));
            }
            if (!_o.InMemory && _o.Folder != null)
                _folder = _o.Folder;
        }

        private static string SafeName(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return sb.Length > 0 ? sb.ToString() : "sim";
        }

        /// <summary>The sandbox folder (created at the first write; null in memory).</summary>
        public string? Folder
        {
            get
            {
                if (_o.InMemory)
                    return null;
                if (_folder == null)
                    _folder = System.IO.Path.Combine(SandboxesFolder(), Id);
                return _folder;
            }
        }

        /// <summary>%LOCALAPPDATA%\cicode-sim\sandbox: the folder of the simulations' own sandboxes.</summary>
        public static string SandboxesFolder()
        {
            string local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            if (local.Length == 0)
                local = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local",
                    "share"
                );
            return System.IO.Path.Combine(local, "cicode-sim", "sandbox");
        }

        /// <summary>How long a simulation's own sandbox folder is kept after its last write.</summary>
        public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);
        private static int _pruned;

        /// <summary>Remove stale sandbox folders whose process has ended.</summary>
        public static int Prune(string parent, string? keep, TimeSpan age)
        {
            int n = 0;
            DateTime limit = DateTime.UtcNow - age;
            IEnumerable<string> dirs;
            try
            {
                dirs = Directory.EnumerateDirectories(parent);
            }
            catch (Exception)
            {
                return 0;
            }
            foreach (string d in new List<string>(dirs))
            {
                string name = System.IO.Path.GetFileName(d);
                if (keep != null && string.Equals(name, keep, StringComparison.OrdinalIgnoreCase))
                    continue;
                var m = System.Text.RegularExpressions.Regex.Match(name, @"^.+-([0-9]+)-[0-9]+$");
                if (!m.Success)
                    continue;
                try
                {
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    if (
                        int.TryParse(
                            m.Groups[1].Value,
                            System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out int pid
                        ) && Running(pid)
                    )
                        continue;
                    if (Newest(d) >= limit)
                        continue;
                    Directory.Delete(d, true);
                    n++;
                }
                catch (Exception) { }
            }
            return n;
        }

        private static bool Running(int pid)
        {
            try
            {
                using (var p = System.Diagnostics.Process.GetProcessById(pid))
                    return !p.HasExited;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static DateTime Newest(string dir)
        {
            DateTime t = Directory.GetLastWriteTimeUtc(dir);
            foreach (string p in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
            {
                DateTime x = File.GetLastWriteTimeUtc(p);
                if (x > t)
                    t = x;
            }
            return t;
        }

        // ------------------------------------------------------------------ paths
        private static bool Sep(char c) => c == '\\' || c == '/';

        /// <summary>A full Windows path: '/' as '\', '.' and '..' applied, a relative path in
        /// <paramref name="cwd"/> (a path starting with '\' on its drive), no trailing '\'
        /// except on a drive root. Nothing is looked up.</summary>
        public static string Normal(string p, string cwd)
        {
            p = p.Replace('/', '\\').Trim();
            WinPath.SplitDrive(p, out string drive, out string rest);
            if (drive.Length == 0)
            {
                WinPath.SplitDrive(cwd.Replace('/', '\\'), out string cd, out string crest);
                if (rest.StartsWith("\\", StringComparison.Ordinal))
                    p = cd + rest;
                else
                    p = cd + crest.TrimEnd('\\') + "\\" + rest;
            }
            else if (drive.Length == 2 && !rest.StartsWith("\\", StringComparison.Ordinal))
            {
                // C:file is relative to the drive's current folder: its root here
                p = drive + "\\" + rest;
            }
            string c = WinPath.Clean(p);
            WinPath.SplitDrive(c, out string d2, out string r2);
            if (d2.Length == 2 && r2.Length == 0)
                c = d2 + "\\";
            if (c.Length > 3 && c.EndsWith("\\", StringComparison.Ordinal))
                c = c.TrimEnd('\\');
            return c;
        }

        /// <summary>The full virtual path of a Cicode file name (its [NAME] path operators already replaced).</summary>
        public string Full(string name) => Normal(name, CurrentDirectory);

        internal static string KeyOf(string full) => CitectText.Lower(full);

        private static string ParentOf(string full)
        {
            WinPath.Split(full, out string head, out _);
            return head;
        }

        private static bool Under(string key, string rootKey) =>
            key == rootKey
            || key.StartsWith(
                rootKey.EndsWith("\\", StringComparison.Ordinal) ? rootKey : rootKey + "\\",
                StringComparison.Ordinal
            );

        private bool Readable(string full)
        {
            if (_o.ReadAnywhere)
                return true;
            string k = KeyOf(full);
            foreach (string r in _roots)
                if (Under(k, KeyOf(r)))
                    return true;
            return false;
        }

        /// <summary>The host path of a virtual path (this computer's form).</summary>
        private static string HostPath(string full) =>
            System.IO.Path.DirectorySeparatorChar == '\\'
                ? full
                : full.Replace('\\', System.IO.Path.DirectorySeparatorChar);

        /// <summary>The real file behind a virtual path when it is readable, else null.</summary>
        private string? RealFile(string full)
        {
            if (!Readable(full))
                return null;
            string h = HostPath(full);
            try
            {
                return File.Exists(h) ? h : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private string? RealFolder(string full)
        {
            if (!Readable(full))
                return null;
            string h = HostPath(full);
            try
            {
                return Directory.Exists(h) ? h : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The host file that holds a sandbox file.</summary>
        private string? BackingOf(string full)
        {
            WinPath.SplitDrive(full, out string drive, out string rest);
            var names = new List<string>();
            if (drive.Length == 2 && drive[1] == ':')
                names.Add(drive.Substring(0, 1));
            else
            {
                names.Add("UNC");
                foreach (string s in drive.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                    names.Add(s);
            }
            foreach (string s in rest.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                names.Add(s);
            char sep = System.IO.Path.DirectorySeparatorChar;
            string root;
            try
            {
                root = System
                    .IO.Path.GetFullPath(Folder!)
                    .TrimEnd(sep, System.IO.Path.AltDirectorySeparatorChar);
            }
            catch (Exception)
            {
                return null;
            }
            var b = new StringBuilder(root);
            foreach (string s in names)
                b.Append(sep).Append(HostName(s));
            string p = b.ToString();
            try
            {
                string check = System.IO.Path.GetFullPath(p);
                if (
                    !string.Equals(check, p, StringComparison.OrdinalIgnoreCase)
                    || !check.StartsWith(root + sep, StringComparison.OrdinalIgnoreCase)
                )
                    return null;
            }
            catch (Exception)
            {
                return null;
            }
            return p;
        }

        private static readonly HashSet<string> DeviceNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase
        )
        {
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "CONIN$",
            "CONOUT$",
            "CLOCK$",
        };

        /// <summary>Is ``s`` a name the host file system stores as itself?</summary>
        private static bool PlainName(string s)
        {
            if (s.Length == 0 || s == "." || s == ".." || s[0] == '~')
                return false;
            char last = s[s.Length - 1];
            if (last == '.' || last == ' ')
                return false;
            foreach (char c in s)
                if (c < 0x20 || c == 0x7f || "<>:\"/\\|?*".IndexOf(c) >= 0)
                    return false;
            int dot = s.IndexOf('.');
            string stem = (dot < 0 ? s : s.Substring(0, dot)).TrimEnd(' ');
            if (DeviceNames.Contains(stem))
                return false;
            if (
                stem.Length == 4
                && (
                    stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                char d = stem[3];
                if ((d >= '0' && d <= '9') || d == '\u00b9' || d == '\u00b2' || d == '\u00b3')
                    return false;
            }
            return true;
        }

        /// <summary>The host name of one path name: the name itself when it is plain, else '~'
        /// and its characters in hex (two digits each, '_' and four digits for one above U+00FF),
        /// so two names never share a host file.</summary>
        internal static string HostName(string s)
        {
            if (PlainName(s))
                return s;
            var b = new StringBuilder("~");
            foreach (char c in s)
            {
                if (c <= 0xff)
                    b.Append(((int)c).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                else
                    b.Append('_')
                        .Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            }
            return b.ToString();
        }

        // ------------------------------------------------------------------ questions
        /// <summary>A file of that path: in the sandbox, or real and readable.</summary>
        public bool FileExists(string full) => _files.ContainsKey(KeyOf(full)) || RealFile(full) != null;

        /// <summary>A sandbox file of that path.</summary>
        public bool InSandbox(string full) => _files.ContainsKey(KeyOf(full));

        /// <summary>Does the folder exist, in the sandbox or for real?</summary>
        public bool FolderExists(string full)
        {
            if (_o.AnyFolder)
                return true;
            string k = KeyOf(full);
            WinPath.SplitDrive(full, out string drive, out string rest);
            if (rest == "\\" || rest.Length == 0)
                return true;
            foreach (var e in _files.Values)
                if (Under(KeyOf(ParentOf(e.Path)), k))
                    return true;
            foreach (string r in _roots)
                if (Under(KeyOf(r), k) || Under(k, KeyOf(r)) && RealFolder(full) != null)
                    return true;
            if (!(drive.Length == 2 && drive[1] == ':' && char.IsLetter(drive[0])))
                return false;
            try
            {
                return Directory.Exists(HostPath(full));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>A folder Cicode can see: a sandbox file lies in it, or it is a readable real folder.</summary>
        public bool FolderVisible(string full)
        {
            string k = KeyOf(full);
            foreach (var e in _files.Values)
                if (Under(KeyOf(ParentOf(e.Path)), k))
                    return true;
            return RealFolder(full) != null;
        }

        /// <summary>The bytes of a file (sandbox, else readable real), null when there is none:
        /// for viewers and tests.</summary>
        public byte[]? ReadAll(string full)
        {
            if (_files.TryGetValue(KeyOf(full), out var e))
            {
                var b = new byte[Len(e)];
                return ReadAt(e, 0, b, 0, b.Length) < 0 ? null : b;
            }
            string? r = RealFile(full);
            try
            {
                return r == null ? null : File.ReadAllBytes(r);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The sandbox files (path, length), in path order.</summary>
        public List<(string Path, long Length)> Files()
        {
            var l = new List<(string, long)>();
            foreach (var e in _files.Values)
                l.Add((e.Path, Len(e)));
            l.Sort((a, b) => string.CompareOrdinal(KeyOf(a.Item1), KeyOf(b.Item1)));
            return l;
        }

        /// <summary>The bytes all sandbox files hold.</summary>
        public long Bytes => _bytes;

        /// <summary>The host file that holds a sandbox file (null in memory or for no such file).</summary>
        public string? BackingPath(string full) =>
            _files.TryGetValue(KeyOf(full), out var e) ? e.Backing : null;

        /// <summary>The files and folders of <paramref name="folder"/> whose names match the
        /// wildcard <paramref name="pattern"/> ('*', '?'; FindFirstFile's '*.*' and '*.' rules),
        /// the sandbox's shadowing the real ones of the same name, in name order (without case).</summary>
        public List<Found> Find(string folder, string pattern)
        {
            var map = new SortedDictionary<string, Found>(StringComparer.Ordinal);
            string fk = KeyOf(folder);
            foreach (var e in _files.Values)
            {
                string parent = ParentOf(e.Path);
                string pk = KeyOf(parent);
                if (pk == fk)
                {
                    string name = WinPath.BaseName(e.Path);
                    if (Match(name, pattern))
                        map[KeyOf(name)] = new Found(name, e.Attributes, true);
                }
                else if (Under(pk, fk))
                {
                    // a folder below holding sandbox files
                    string tail = parent.Substring(folder.TrimEnd('\\').Length).TrimStart('\\');
                    int cut = tail.IndexOf('\\');
                    string name = cut < 0 ? tail : tail.Substring(0, cut);
                    if (name.Length > 0 && Match(name, pattern) && !map.ContainsKey(KeyOf(name)))
                        map[KeyOf(name)] = new Found(name, 0x10, true);
                }
            }
            string? real = RealFolder(folder);
            if (real != null)
            {
                try
                {
                    foreach (string p in Directory.EnumerateFileSystemEntries(real))
                    {
                        string name = System.IO.Path.GetFileName(p);
                        if (!Match(name, pattern) || map.ContainsKey(KeyOf(name)))
                            continue;
                        int attr;
                        try
                        {
                            attr = (int)File.GetAttributes(p);
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        map[KeyOf(name)] = new Found(name, attr, false);
                    }
                }
                catch (Exception) { }
            }
            return new List<Found>(map.Values);
        }

        /// <summary>FindFirstFile's long-name match (case-insensitive): '*' any run, '?' one
        /// character; a pattern ending in ".*" also matches a name without a '.', "*." matches
        /// a name without a '.'.</summary>
        public static bool Match(string name, string pattern)
        {
            if (pattern == "*" || pattern == "*.*")
                return true;
            if (
                pattern.EndsWith(".*", StringComparison.Ordinal)
                && name.IndexOf('.') < 0
                && Glob(name, pattern.Substring(0, pattern.Length - 2), 0, 0)
            )
                return true;
            if (
                pattern.EndsWith(".", StringComparison.Ordinal)
                && pattern.Length > 1
                && name.IndexOf('.') < 0
            )
                return Glob(name, pattern.Substring(0, pattern.Length - 1), 0, 0);
            return Glob(name, pattern, 0, 0);
        }

        private static bool Glob(string s, string p, int i, int j)
        {
            while (j < p.Length)
            {
                char c = p[j];
                if (c == '*')
                {
                    while (j < p.Length && p[j] == '*')
                        j++;
                    if (j == p.Length)
                        return true;
                    for (int k = i; k <= s.Length; k++)
                        if (Glob(s, p, k, j))
                            return true;
                    return false;
                }
                if (i >= s.Length)
                    return false;
                if (c != '?' && CitectText.Lower(s[i].ToString()) != CitectText.Lower(c.ToString()))
                    return false;
                i++;
                j++;
            }
            return i == s.Length;
        }

        public static bool HasWildcard(string s) => s.IndexOf('*') >= 0 || s.IndexOf('?') >= 0;

        // ------------------------------------------------------------------ storage
        internal int Len(Entry e) => e.Length;

        internal int ReadAt(Entry e, long pos, byte[] buf, int off, int n)
        {
            if (pos >= e.Length || n <= 0)
                return 0;
            int k = (int)Math.Min(n, e.Length - pos);
            if (e.Backing == null)
            {
                Buffer.BlockCopy(e.Data, (int)pos, buf, off, k);
                return k;
            }
            try
            {
                using (
                    var fs = new FileStream(
                        e.Backing,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete
                    )
                )
                {
                    fs.Seek(pos, SeekOrigin.Begin);
                    int got = 0;
                    while (got < k)
                    {
                        int r = fs.Read(buf, off + got, k - got);
                        if (r <= 0)
                            break;
                        got += r;
                    }
                    return got;
                }
            }
            catch (Exception)
            {
                return -1; // the host file went away or cannot be read: a read error
            }
        }

        /// <summary>Write at <paramref name="pos"/>; false when the sandbox is full.</summary>
        internal bool WriteAt(Entry e, long pos, byte[] data, int off, int n, ulong time)
        {
            long end = pos + n;
            long grow = Math.Max(0, end - e.Length);
            if (_bytes + grow > _o.MaxBytes || end > int.MaxValue)
                return false;
            if (e.Backing == null)
            {
                if (end > e.Data.Length)
                {
                    var nd = new byte[
                        Math.Max(end, Math.Min((long)int.MaxValue, Math.Max(256, (long)e.Data.Length * 2)))
                    ];
                    Buffer.BlockCopy(e.Data, 0, nd, 0, e.Length);
                    e.Data = nd;
                }
                if (pos > e.Length)
                    Array.Clear(e.Data, e.Length, (int)(pos - e.Length));
                Buffer.BlockCopy(data, off, e.Data, (int)pos, n);
            }
            else
            {
                try
                {
                    using (
                        var fs = new FileStream(
                            e.Backing,
                            FileMode.Open,
                            FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete
                        )
                    )
                    {
                        fs.Seek(pos, SeekOrigin.Begin);
                        fs.Write(data, off, n);
                    }
                }
                catch (Exception)
                {
                    return false; // the host disk is full or the file went away: a write error
                }
                SetHostTime(e, time);
            }
            if (end > e.Length)
            {
                _bytes += end - e.Length;
                e.Length = (int)end;
            }
            e.Time = time;
            Changed?.Invoke(
                new FileEvent
                {
                    Op = "write",
                    Path = e.Path,
                    Bytes = n,
                }
            );
            return true;
        }

        private static void SetHostTime(Entry e, ulong time)
        {
            try
            {
                if (e.Backing != null && time >= 504911232000000000UL)
                    File.SetLastWriteTimeUtc(e.Backing, DateTime.FromFileTimeUtc((long)time));
            }
            catch (Exception) { }
        }

        private bool Truncate(Entry e, ulong time)
        {
            if (e.Backing != null)
            {
                try
                {
                    using (
                        new FileStream(
                            e.Backing,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete
                        )
                    ) { }
                }
                catch (Exception)
                {
                    return false;
                }
                SetHostTime(e, time);
            }
            _bytes -= e.Length;
            e.Length = 0;
            e.Time = time;
            return true;
        }

        /// <summary>A new, empty sandbox file (null when the sandbox has no room).</summary>
        private Entry? Create(string full, ulong time)
        {
            if (_files.Count >= _o.MaxFiles)
                return null;
            var e = new Entry
            {
                Path = full,
                Key = KeyOf(full),
                Time = time,
            };
            if (!_o.InMemory)
            {
                string? b = BackingOf(full);
                if (b == null)
                    return null;
                // a simulation's own folder: the old ones of finished simulations go (once a process)
                if (_o.Folder == null && System.Threading.Interlocked.Exchange(ref _pruned, 1) == 0)
                    Prune(SandboxesFolder(), Id, KeepFor);
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(b)!);
                    using (
                        new FileStream(
                            b,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete
                        )
                    ) { }
                }
                catch (Exception)
                {
                    return null;
                }
                e.Backing = b;
                SetHostTime(e, time);
            }
            _files[e.Key] = e;
            Changed?.Invoke(new FileEvent { Op = "create", Path = full });
            return e;
        }

        /// <summary>A sandbox copy of a real file (copy on write); null when there is no room.</summary>
        private Entry? CopyIn(string full, string real, ulong time)
        {
            byte[] data;
            try
            {
                if (_bytes + new FileInfo(real).Length > _o.MaxBytes)
                    return null; // no room: not read at all
                data = File.ReadAllBytes(real);
            }
            catch (Exception)
            {
                return null;
            }
            if (_bytes + data.Length > _o.MaxBytes)
                return null;
            ulong t = time;
            try
            {
                t = (ulong)File.GetLastWriteTimeUtc(real).ToFileTimeUtc();
            }
            catch (Exception) { }
            var e = Create(full, t);
            if (e == null)
                return null;
            if (data.Length > 0 && !WriteAt(e, 0, data, 0, data.Length, t))
                return null;
            e.Time = t;
            SetHostTime(e, t);
            return e;
        }

        // ------------------------------------------------------------------ file operations
        /// <summary>Open a file (the runtime's rules, FileOpen modes): read / write access,
        /// <paramref name="create"/> a missing file, <paramref name="truncate"/> it. Null when it
        /// cannot be opened (missing, a folder, no room, a missing folder).</summary>
        public SandboxFile? Open(string full, bool read, bool write, bool create, bool truncate, ulong time)
        {
            string k = KeyOf(full);
            _files.TryGetValue(k, out var e);
            string? real = e == null ? RealFile(full) : null;
            if (e == null && real == null)
            {
                if (!create || !FolderExists(ParentOf(full)) || RealFolder(full) != null)
                    return null;
                e = Create(full, time);
                if (e == null)
                    return null;
            }
            else if (write)
            {
                if (e == null)
                {
                    e = truncate ? Create(full, time) : CopyIn(full, real!, time);
                    if (e == null)
                        return null;
                    real = null;
                }
                else if (truncate && !Truncate(e, time))
                    return null;
            }
            return new SandboxFile
            {
                Entry = e,
                Real = e == null ? real : null,
                Path = e?.Path ?? full,
                CanRead = read,
                CanWrite = write,
            };
        }

        /// <summary>The length of an open file, -1 when it cannot be read.</summary>
        public long Length(SandboxFile f)
        {
            if (f.Entry != null)
                return Len(f.Entry);
            try
            {
                return new FileInfo(f.Real!).Length;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>Read up to n bytes at the file's position (it moves on); -1 when the file is
        /// not open for reading.</summary>
        public int Read(SandboxFile f, byte[] buf, int n)
        {
            if (!f.CanRead)
                return -1;
            int k;
            if (f.Entry != null)
            {
                k = ReadAt(f.Entry, f.Position, buf, 0, n);
                if (k < 0)
                    return -1;
            }
            else
            {
                try
                {
                    using (
                        var fs = new FileStream(
                            f.Real!,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete
                        )
                    )
                    {
                        fs.Seek(f.Position, SeekOrigin.Begin);
                        k = 0;
                        while (k < n)
                        {
                            int r = fs.Read(buf, k, n - k);
                            if (r <= 0)
                                break;
                            k += r;
                        }
                    }
                }
                catch (Exception)
                {
                    return -1;
                }
            }
            f.Position += k;
            return k;
        }

        /// <summary>Write the bytes at the file's position (it moves on); -1 when the file is not
        /// open for writing or the sandbox is full.</summary>
        public int Write(SandboxFile f, byte[] data, int n, ulong time)
        {
            if (!f.CanWrite || f.Entry == null)
                return -1;
            if (!_files.ContainsKey(f.Entry.Key))
                return -1; // deleted while open
            if (n == 0)
                return 0;
            if (!WriteAt(f.Entry, f.Position, data, 0, n, time))
                return -1;
            f.Position += n;
            return n;
        }

        /// <summary>A whole file the simulator itself writes (a printer's output): replaced when it
        /// exists, made in its folder when that folder does not exist yet. False when there is no
        /// room or the name is a folder.</summary>
        public bool WriteOutput(string full, byte[] data, ulong time)
        {
            var f = Open(full, false, true, true, true, time);
            if (f == null)
            {
                if (_files.ContainsKey(KeyOf(full)) || RealFile(full) != null || RealFolder(full) != null)
                    return false;
                var e = Create(full, time);
                if (e == null)
                    return false;
                f = new SandboxFile
                {
                    Entry = e,
                    Path = full,
                    CanWrite = true,
                };
            }
            return data.Length == 0 || Write(f, data, data.Length, time) == data.Length;
        }

        /// <summary>Delete a sandbox file; false for a real one (read-only) or none.</summary>
        public bool Delete(string full)
        {
            string k = KeyOf(full);
            if (!_files.TryGetValue(k, out var e))
                return false;
            _files.Remove(k);
            _bytes -= e.Length;
            if (e.Backing != null)
            {
                try
                {
                    File.Delete(e.Backing);
                }
                catch (Exception) { }
            }
            Changed?.Invoke(new FileEvent { Op = "delete", Path = e.Path });
            return true;
        }

        /// <summary>Rename a sandbox file (rename()'s rule: the new name must not exist); false
        /// for a real file, none, an existing target or a missing folder.</summary>
        public bool Rename(string from, string to)
        {
            string k = KeyOf(from);
            if (!_files.TryGetValue(k, out var e))
                return false;
            if (FileExists(to) || !FolderExists(ParentOf(to)))
                return false;
            string nk = KeyOf(to);
            if (e.Backing != null)
            {
                string? nb = BackingOf(to);
                if (nb == null)
                    return false;
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(nb)!);
                    File.Move(e.Backing, nb);
                }
                catch (Exception)
                {
                    return false;
                }
                e.Backing = nb;
            }
            _files.Remove(k);
            e.Path = to;
            e.Key = nk;
            _files[nk] = e;
            Changed?.Invoke(
                new FileEvent
                {
                    Op = "rename",
                    Path = from,
                    Other = to,
                }
            );
            return true;
        }

        /// <summary>Copy a file into the sandbox (the target's time and attributes become the
        /// source's): 0, or 261 (no source), 262 (target cannot be made), 264 (no room).</summary>
        public int Copy(string from, string to, bool onlyIfTimeDiffers, ulong now)
        {
            string? real = _files.ContainsKey(KeyOf(from)) ? null : RealFile(from);
            try
            {
                if (real != null && _bytes + new FileInfo(real).Length > _o.MaxBytes)
                    return 264;
            }
            catch (Exception)
            {
                return Codes.FileNotFound;
            }
            byte[]? data = ReadAll(from);
            if (data == null)
                return Codes.FileNotFound;
            ulong t = Time(from) ?? now;
            if (onlyIfTimeDiffers && FileExists(to) && Time(to) == t)
                return 0;
            if (RealFolder(to) != null || !FolderExists(ParentOf(to)))
                return 262;
            string k = KeyOf(to);
            if (!_files.TryGetValue(k, out var e))
            {
                e = Create(to, t);
                if (e == null)
                    return 262;
            }
            else if (!Truncate(e, t))
                return 262;
            if (data.Length > 0 && !WriteAt(e, 0, data, 0, data.Length, t))
                return 264;
            e.Time = t;
            SetHostTime(e, t);
            Changed?.Invoke(
                new FileEvent
                {
                    Op = "copy",
                    Path = to,
                    Other = from,
                    Bytes = data.Length,
                }
            );
            return 0;
        }

        /// <summary>A file's last write time (FILETIME, UTC), null when there is no file.</summary>
        public ulong? Time(string full)
        {
            if (_files.TryGetValue(KeyOf(full), out var e))
                return e.Time;
            string? r = RealFile(full);
            if (r == null)
                return null;
            try
            {
                return (ulong)File.GetLastWriteTimeUtc(r).ToFileTimeUtc();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public ulong? Time(SandboxFile f) => f.Entry != null ? f.Entry.Time : Time(f.Path);

        /// <summary>Set an open file's times (SetFileTime); false for a real file.</summary>
        public bool SetTime(SandboxFile f, ulong time)
        {
            if (f.Entry == null || !f.CanWrite)
                return false;
            f.Entry.Time = time;
            SetHostTime(f.Entry, time);
            Changed?.Invoke(new FileEvent { Op = "time", Path = f.Entry.Path });
            return true;
        }

        /// <summary>Remove every sandbox file (and the sandbox folder when this sandbox made it).</summary>
        public void Clear()
        {
            foreach (var e in new List<Entry>(_files.Values))
                Delete(e.Path);
            if (!_o.InMemory && _o.Folder == null && _folder != null)
            {
                try
                {
                    if (Directory.Exists(_folder))
                        Directory.Delete(_folder, true);
                }
                catch (Exception) { }
            }
        }
    }
}
