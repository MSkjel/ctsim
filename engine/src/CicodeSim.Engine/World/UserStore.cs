using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.World
{
    /// <summary>The simulated users database.</summary>
    public sealed class UserStore
    {
        private static readonly ConditionalWeakTable<World, UserStore> All =
            new ConditionalWeakTable<World, UserStore>();

        public static UserStore Of(World w) => All.GetValue(w, x => new UserStore(x));

        /// <summary>The columns of a users.dbf the root project has none of (the corpus' layout).</summary>
        public static readonly (string Name, int Width)[] DefaultColumns =
        {
            ("NAME", 16),
            ("FULL_NAME", 32),
            ("PASSWORD", 60),
            ("ROLES", 254),
            ("TYPE", 16),
            ("COMMENT", 48),
            ("PWD_VER", 4),
            ("SEED", 32),
        };

        private readonly World _w;
        private List<Dictionary<string, string>>? _rows;
        private (string Name, int Width)[]? _columns;

        private UserStore(World w)
        {
            _w = w;
        }

        /// <summary>The root project's users.dbf, if it has one.</summary>
        private string? RootTable() => TagScope.FindTable(_w.Unit.RootDir, "users.dbf");

        /// <summary>The columns of the root's users.dbf (as its header names them, with their
        /// widths), the default layout when it has none.</summary>
        public (string Name, int Width)[] Columns
        {
            get
            {
                if (_columns == null)
                {
                    string? path = RootTable();
                    DbfHeader? hd = path != null ? Dbf.Header(path) : null;
                    if (hd == null)
                        _columns = DefaultColumns;
                    else
                    {
                        var c = new (string, int)[hd.Names.Length];
                        for (int i = 0; i < c.Length; i++)
                            c[i] = (hd.Names[i], hd.Lengths[i]);
                        _columns = c;
                    }
                }
                return _columns;
            }
        }

        /// <summary>The column a field name means (case-insensitive), null when none.</summary>
        public string? Column(string field)
        {
            foreach (var (n, _) in Columns)
                if (C(n) == C(field))
                    return n;
            return null;
        }

        /// <summary>The width of a column (0 when the table has no such column).</summary>
        public int Width(string column)
        {
            foreach (var (n, w) in Columns)
                if (C(n) == C(column))
                    return w;
            return 0;
        }

        private static string C(string s) => A(s).ToUpperInvariant();

        private static string A(string s) => CitectText.Trim(s);

        /// <summary>The root project's user records (copied at the first use), each a
        /// column-to-value map in the table's order.</summary>
        public List<Dictionary<string, string>> Rows
        {
            get
            {
                if (_rows == null)
                {
                    var o = new List<Dictionary<string, string>>();
                    string? path = RootTable();
                    if (path != null)
                        foreach (DbfRow r in Dbf.Rows(path))
                        {
                            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            for (int i = 0; i < r.Header.Names.Length; i++)
                                d[r.Header.Names[i]] = r[i];
                            o.Add(d);
                        }
                    _rows = o;
                }
                return _rows;
            }
        }

        /// <summary>The first record whose column equals the value, ignoring case.</summary>
        public Dictionary<string, string>? Find(string column, string value)
        {
            foreach (var r in Rows)
                if (r.TryGetValue(column, out string? v) && Same(v, value))
                    return r;
            return null;
        }

        /// <summary>ASCII letters without case, the rest by byte.</summary>
        public static bool Same(string a, string b) =>
            string.Equals(CLower(A(a)), CLower(A(b)), StringComparison.Ordinal);

        private static string CLower(string s)
        {
            var c = s.ToCharArray();
            for (int i = 0; i < c.Length; i++)
                if (c[i] >= 'A' && c[i] <= 'Z')
                    c[i] = (char)(c[i] + 32);
            return new string(c);
        }

        /// <summary>A new record with every column empty, appended.</summary>
        public Dictionary<string, string> Append()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (n, _) in Columns)
                d[n] = "";
            Rows.Add(d);
            return d;
        }

        /// <summary>The runtime's definitions again from the copy (the reload after a change).</summary>
        public void Reload()
        {
            var o = new Dictionary<string, UserDef>(StringComparer.Ordinal);
            var unit = _w.Unit;
            string rootKey = Key(unit.RootDir);
            bool rootDone = false;
            void AddRow(string project, Func<string, string> get)
            {
                string n = get("NAME");
                if (n.Length == 0 || o.ContainsKey(CitectText.Lower(n)))
                    return;
                var roles = new List<string>();
                foreach (string x in get("ROLES").Split(','))
                    if (CitectText.Trim(x).Length > 0)
                        roles.Add(CitectText.Trim(x));
                o[CitectText.Lower(n)] = new UserDef
                {
                    Name = n,
                    FullName = get("FULL_NAME"),
                    Type = get("TYPE"),
                    Roles = roles,
                    Comment = get("COMMENT"),
                    Project = project,
                    Password = get("PASSWORD"),
                    Seed = get("SEED"),
                    PwdVer = get("PWD_VER"),
                };
            }
            void AddRoot()
            {
                if (rootDone)
                    return;
                rootDone = true;
                foreach (var r in Rows)
                    AddRow(unit.Root, k => r.TryGetValue(k, out string? v) ? A(v) : "");
            }
            foreach (var p in unit.Projects)
            {
                if (Key(p.Dir) == rootKey)
                {
                    AddRoot();
                    continue;
                }
                string? path = TagScope.FindTable(p.Dir, "users.dbf");
                if (path == null)
                    continue;
                foreach (DbfRow r in Dbf.Rows(path))
                    AddRow(p.Name, k => r.Get(k));
            }
            AddRoot();
            _w.Users.SetUserDefs(o);
        }

        private static string Key(string dir) => WinPath.Key(dir);

        // ------------------------------------------------------------------ passwords
        /// <summary>[General]PasswordExpiry: the days a password stays valid, 0 (default) never
        /// expires, at most 365 (the parameter clamped to
        /// 0..365 at start; doc 1114039). Set by the built-ins from the parameter.</summary>
        public int ExpiryDays;

        /// <summary>The parameter text as the runtime reads it into <see cref="ExpiryDays"/>.</summary>
        public static int ExpiryOf(string? text)
        {
            long d = Crt.Atol(text ?? "0");
            return d < 0 ? 0
                : d > 365 ? 365
                : (int)d;
        }

        /// <summary>The simulated clock as a FILETIME (100 ns from 1601-01-01 UTC).</summary>
        public long NowFileTime()
        {
            double unix = _w.Clock.Unix();
            return (long)Math.Floor((unix + 11644473600.0) * 1e7);
        }

        /// <summary>The days left of a password set at <paramref name="setTime"/> (the expiry days less the whole days from the set day's midnight (UTC) to now, at least
        /// 0); -1 when passwords never expire.</summary>
        public int DaysLeft(long setTime)
        {
            int expiry = ExpiryDays;
            if (expiry == 0)
                return -1;
            const long Day = 864000000000L;
            long midnight = setTime - (((setTime % Day) + Day) % Day);
            long elapsed = (NowFileTime() - midnight) / Day; // __alldiv: toward zero
            long left = (short)(expiry - (short)elapsed);
            return left < 0 ? 0 : (int)left;
        }

        /// <summary>
        /// The PASSWORD text the runtime stores: HMAC-SHA1 hex of the password keyed with the seed.
        /// </summary>
        public static string Hash(string password, string seed)
        {
            using (var h = new HMACSHA1(Ascii(seed)))
            {
                byte[] d = h.ComputeHash(Ascii(password));
                var sb = new StringBuilder(40);
                foreach (byte b in d)
                    sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>The ASCII bytes .NET's Encoding.ASCII gives (every character above 0x7F is '?').</summary>
        private static byte[] Ascii(string s)
        {
            var b = new byte[s.Length];
            for (int i = 0; i < s.Length; i++)
                b[i] = s[i] < 0x80 ? (byte)s[i] : (byte)'?';
            return b;
        }

        public static string Stamp(long fileTime)
        {
            ulong ft = unchecked((ulong)fileTime);
            return ((uint)(ft >> 32)).ToString("D10", CultureInfo.InvariantCulture)
                + ((uint)ft).ToString("D10", CultureInfo.InvariantCulture);
        }

        /// <summary>The password's time stamp: the FILETIME after the 40 hash characters of a
        /// PASSWORD text of 60 or more characters ('%10lu%10lu'), 0 for a shorter one.</summary>
        public static long StampOf(string stored)
        {
            if (stored.Length < 60)
                return 0;
            ulong hi = Lu(stored.Substring(40, 10)),
                lo = Lu(stored.Substring(50, 10));
            return unchecked((long)((hi << 32) | (lo & 0xFFFFFFFFUL)));
        }

        private static ulong Lu(string s)
        {
            string t = s.TrimStart(' ', '\t');
            ulong v = 0;
            foreach (char c in t)
            {
                if (c < '0' || c > '9')
                    break;
                v = unchecked(v * 10 + (ulong)(c - '0'));
            }
            return v & 0xFFFFFFFFUL;
        }

        /// <summary>A new seed: 16 bytes in upper-case hex (the runtime draws them at
        /// random; the simulator from its own seeded generator, so a run repeats).</summary>
        public static string NewSeed(Func<uint> next)
        {
            var sb = new StringBuilder(32);
            for (int i = 0; i < 4; i++)
            {
                uint v = next();
                for (int k = 0; k < 4; k++)
                    sb.Append(((byte)(v >> (8 * k))).ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>Check a typed password against the users table.</summary>
        public int Check(UserDef u, string password)
        {
            int ver = (int)Crt.Atol(u.PwdVer);
            if (ver != 2)
                return 436;
            string want = Hash(password, u.Seed);
            if (u.Password.Length < 40 || string.CompareOrdinal(u.Password, 0, want, 0, 40) != 0)
                return 436;
            int left = DaysLeft(StampOf(u.Password));
            return left < 1 && left != -1 ? 375 : 0;
        }

        /// <summary>Set a record's password as the runtime writes it: a new seed, PASSWORD = hash +
        /// the time, SEED, PWD_VER '2'.</summary>
        public void SetPassword(Dictionary<string, string> rec, string password, Func<uint> next)
        {
            string seed = NewSeed(next);
            rec[Column("PASSWORD") ?? "PASSWORD"] = Hash(password, seed) + Stamp(NowFileTime());
            rec[Column("SEED") ?? "SEED"] = seed;
            rec[Column("PWD_VER") ?? "PWD_VER"] = "2";
        }
    }
}
