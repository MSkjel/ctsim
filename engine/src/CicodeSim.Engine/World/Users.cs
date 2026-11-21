using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.World
{
    /// <summary>A role (roles.dbf): global privileges, privileges per level for specific areas,
    /// viewable areas.</summary>
    public sealed class Role
    {
        public string Name = "";
        public string Group = "";
        public HashSet<int> ViewAreas = new HashSet<int>();
        public HashSet<int> GlobalPrivs = new HashSet<int>();
        public Dictionary<int, HashSet<int>> AreaPrivs = new Dictionary<int, HashSet<int>>(); // level -> areas
        public string EntryFunc = "",
            ExitFunc = "",
            Comment = "",
            Project = "";

        /// <summary>MANAGE_USR: the role may manage other users.</summary>
        public bool ManageUsers;

        /// <summary>ALLOW_RPC: the role may make remote procedure calls (ServerRPC, MsgRPC; doc 1193662: 513 otherwise).</summary>
        public bool AllowRpc;

        public static Role FromRow(
            DbfRow r,
            string project = "",
            Dictionary<string, List<string>>? groups = null
        )
        {
            var role = new Role
            {
                Name = r.Get("NAME"),
                Group = r.Get("GROUP_NAME"),
                EntryFunc = r.Get("ENTRY_FUNC"),
                ExitFunc = r.Get("EXIT_FUNC"),
                Comment = r.Get("COMMENT"),
                Project = project,
                ManageUsers = Users.IsTrue(r.Get("MANAGE_USR")),
                AllowRpc = Users.IsTrue(r.Get("ALLOW_RPC")),
            };
            for (int n = 1; n <= 8; n++)
            {
                var areas = Users.ParseList(
                    r.Get("PRIV_" + n.ToString(CultureInfo.InvariantCulture)),
                    groups
                );
                if (areas.Count > 0)
                    role.AreaPrivs[n] = areas;
            }
            role.ViewAreas = Users.ParseList(r.Get("ACCESS"), groups);
            foreach (int p in Users.ParseList(r.Get("PRIV_G")))
                if (p > 0)
                    role.GlobalPrivs.Add(p);
            return role;
        }
    }

    public sealed class UserDef
    {
        public string Name = "",
            FullName = "",
            Type = "",
            Comment = "",
            Project = "";
        public List<string> Roles = new List<string>();

        /// <summary>The password columns of users.dbf: PASSWORD (the hash text and the time it
        /// was set, UserStore), SEED and PWD_VER; empty where the table has none.</summary>
        public string Password = "",
            Seed = "",
            PwdVer = "";
    }

    /// <summary>The logged-in user (or the default user).</summary>
    public sealed class Session
    {
        public string Name = "";
        public string FullName = "";
        public HashSet<int> GlobalPrivs = new HashSet<int>();
        public Dictionary<int, HashSet<int>> AreaPrivs = new Dictionary<int, HashSet<int>>();
        public HashSet<int> ViewAreas = new HashSet<int>();
        public List<string> Roles = new List<string>();
        public double LoginTime; // Unix seconds (simulated)
        public double LastCommand;
        public int Commands;
        public int LoginType; // UserInfo(6): 0 view-only default, 1 explicit login

        /// <summary>SetArea's current viewable areas (null: every area the session may view;
        /// the session's area array, which SetArea rewrites from the
        /// user's areas). Area 0 is always in it.</summary>
        public HashSet<int>? CurrentAreas;

        public bool IsDefault => LoginType == 0;
    }

    /// <summary>The simulated login.</summary>
    public sealed class Users
    {
        public Unit? Unit { get; }
        public SimClock? Clock { get; }
        private Dictionary<string, UserDef>? _users;
        private Dictionary<string, Role>? _roles;
        private Dictionary<string, List<string>>? _groups;
        private bool? _exclusive;
        public Session Session { get; private set; } = new Session();

        /// <summary>Changes with every login / logout (what a cached count keys on).</summary>
        public long Serial { get; private set; }
        public List<Action<Session>> Listeners { get; } = new List<Action<Session>>();

        public Users(Unit? unit = null, SimClock? clock = null, bool? exclusive = null)
        {
            Unit = unit;
            Clock = clock;
            _exclusive = exclusive;
        }

        /// <summary>'1..8' / '1,2,3' / '0..255' / '5' to the numbers (invalid parts skipped); a part
        /// that names a groups.dbf group stands for its members (groups again allowed).</summary>
        public static HashSet<int> ParseList(
            string? text,
            Dictionary<string, List<string>>? groups = null,
            HashSet<string>? seen = null
        )
        {
            var o = new HashSet<int>();
            foreach (string raw in (text ?? "").Replace(";", ",").Split(','))
            {
                string part = CitectText.Trim(raw);
                if (part.Length == 0)
                    continue;
                if (groups != null && groups.TryGetValue(CitectText.Lower(part), out var members))
                {
                    var sn = seen ?? new HashSet<string>(StringComparer.Ordinal);
                    if (sn.Add(CitectText.Lower(part))) // a group naming itself: once
                        foreach (string m in members)
                            o.UnionWith(ParseList(m, groups, sn));
                    continue;
                }
                int dd = part.IndexOf("..", StringComparison.Ordinal);
                if (dd >= 0)
                {
                    BigInteger? a = CitectText.ParseInt(part.Substring(0, dd)),
                        b = CitectText.ParseInt(part.Substring(dd + 2));
                    if (a == null || b == null)
                        continue;
                    long lo = ClampInt(BigInteger.Min(a.Value, b.Value)),
                        hi = ClampInt(BigInteger.Max(a.Value, b.Value));
                    for (long i = lo; i <= hi; i++)
                        o.Add((int)i);
                }
                else
                {
                    BigInteger? v = CitectText.ParseInt(part);
                    if (v == null)
                        continue;
                    o.Add(ClampInt(v.Value));
                }
            }
            return o;
        }

        /// <summary>A whole number as an area / level: beyond the int range it is the nearest end;
        /// a real area (0..255) or level (1..8) never equals it.</summary>
        internal static int ClampInt(BigInteger v) =>
            v > int.MaxValue ? int.MaxValue
            : v < int.MinValue ? int.MinValue
            : (int)v;

        // ------------------------------------------------------------------ configuration
        public bool Exclusive
        {
            get
            {
                if (_exclusive == null)
                {
                    string? v = Unit != null ? Unit.Param("Privilege", "Exclusive", "1") : "1";
                    string t = CitectText.Trim(string.IsNullOrEmpty(v) ? "1" : v);
                    _exclusive = t != "0";
                }
                return _exclusive.Value;
            }
            set => _exclusive = value;
        }

        /// <summary>groups.dbf of the unit: lower-case NAME to its members ASS1..ASS10; the first
        /// definition wins.</summary>
        public Dictionary<string, List<string>> Groups()
        {
            if (_groups == null)
            {
                var o = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                if (Unit != null)
                {
                    foreach (var (_, r) in Unit.Rows("groups.dbf"))
                    {
                        string n = CitectText.Trim(r.Get("NAME"));
                        if (n.Length == 0 || o.ContainsKey(CitectText.Lower(n)))
                            continue;
                        var m = new List<string>();
                        for (int i = 1; i <= 10; i++)
                        {
                            string a = r.Get("ASS" + i.ToString(CultureInfo.InvariantCulture));
                            if (a.Length > 0)
                                m.Add(CitectText.Trim(a));
                        }
                        o[CitectText.Lower(n)] = m;
                    }
                }
                _groups = o;
            }
            return _groups;
        }

        public Dictionary<string, Role> Roles()
        {
            if (_roles == null)
            {
                var o = new Dictionary<string, Role>(StringComparer.Ordinal);
                if (Unit != null)
                {
                    var groups = Groups();
                    foreach (var (p, r) in Unit.Rows("roles.dbf"))
                    {
                        var role = Role.FromRow(r, p.Name, groups);
                        if (role.Name.Length > 0 && !o.ContainsKey(CitectText.Lower(role.Name)))
                            o[CitectText.Lower(role.Name)] = role;
                    }
                }
                _roles = o;
            }
            return _roles;
        }

        public Dictionary<string, UserDef> UserDefs()
        {
            if (_users == null)
            {
                var o = new Dictionary<string, UserDef>(StringComparer.Ordinal);
                if (Unit != null)
                {
                    foreach (var (p, r) in Unit.Rows("users.dbf"))
                    {
                        string n = r.Get("NAME");
                        if (n.Length == 0 || o.ContainsKey(CitectText.Lower(n)))
                            continue;
                        var roles = new List<string>();
                        foreach (string x in r.Get("ROLES").Split(','))
                            if (CitectText.Trim(x).Length > 0)
                                roles.Add(CitectText.Trim(x));
                        o[CitectText.Lower(n)] = new UserDef
                        {
                            Name = n,
                            FullName = r.Get("FULL_NAME"),
                            Type = r.Get("TYPE"),
                            Roles = roles,
                            Comment = r.Get("COMMENT"),
                            Project = p.Name,
                            Password = r.Get("PASSWORD"),
                            Seed = r.Get("SEED"),
                            PwdVer = r.Get("PWD_VER"),
                        };
                    }
                }
                _users = o;
            }
            return _users;
        }

        /// <summary>Replace the user definitions the runtime holds (a reload of the users
        /// database after UserCreate / UserDelete / UserPassword / UserUpdateRecord: UserStore).
        /// The logged-in session stays as it is.</summary>
        public void SetUserDefs(Dictionary<string, UserDef> defs) => _users = defs;

        /// <summary>'TRUE' (any case) or a non-zero whole number.</summary>
        public static bool IsTrue(string? text)
        {
            string t = CitectText.Trim(text ?? "");
            if (CitectText.Upper(t) == "TRUE")
                return true;
            BigInteger? v = CitectText.ParseInt(t);
            return v != null && !v.Value.IsZero;
        }

        /// <summary>The areas and privileges a user would log in with (the session SessionFor
        /// builds, not started): what UserVerify checks without logging the user in.</summary>
        public Session SessionOf(UserDef u) => SessionFor(u.Name, u.FullName, u.Roles);

        /// <summary>The logged-in session may manage users: one of its roles has MANAGE_USR
        /// (the session record's flag; the default user has none).</summary>
        public bool CanManageUsers()
        {
            var s = Session;
            if (s.IsDefault)
                return false;
            foreach (string rn in s.Roles)
                if (Roles().TryGetValue(CitectText.Lower(rn), out Role? role) && role.ManageUsers)
                    return true;
            return false;
        }

        /// <summary>The logged-in session may make remote procedure calls: one of its roles has
        /// ALLOW_RPC (doc 1193662; the default user has none).</summary>
        public bool CanRpc()
        {
            var s = Session;
            if (s.IsDefault)
                return false;
            foreach (string rn in s.Roles)
                if (Roles().TryGetValue(CitectText.Lower(rn), out Role? role) && role.AllowRpc)
                    return true;
            return false;
        }

        /// <summary>SetArea: the session's current viewable areas (area 0 added; null = all it may view).</summary>
        public void SetCurrentAreas(HashSet<int>? areas)
        {
            if (areas != null)
                areas.Add(0);
            Session.CurrentAreas = areas;
            Serial++;
        }

        /// <summary><paramref name="s"/> may view the area, before SetArea's current areas
        /// (the rule of <see cref="CanView(int)"/> for any session: UserVerify, SetArea).</summary>
        public static bool MayView(Session s, int area)
        {
            if (area == 0 || s.ViewAreas.Contains(area) || s.GlobalPrivs.Count > 0)
                return true;
            foreach (var areas in s.AreaPrivs.Values)
                if (areas.Contains(area))
                    return true;
            return false;
        }

        /// <summary><paramref name="s"/> holds privilege level <paramref name="level"/> in the
        /// area: a global privilege, or the level for that area (area 0: in any area). The exact
        /// level, as the runtime's bit masks compare, not Exclusive's ladder.</summary>
        public static bool HoldsLevel(Session s, int area, int level)
        {
            if (s.GlobalPrivs.Contains(level))
                return true;
            if (!s.AreaPrivs.TryGetValue(level, out var areas))
                return false;
            return area == 0 ? areas.Count > 0 : areas.Contains(area);
        }

        // ------------------------------------------------------------------ login
        private double Now() => Clock != null ? Clock.Unix() : 0.0;

        /// <summary>Log <paramref name="name"/> in with the privileges of its roles; 0, or 1 for an
        /// unknown user (guess: Login's error code).</summary>
        public int Login(string? name, string? password = null)
        {
            if (!UserDefs().TryGetValue(CitectText.Lower(name ?? ""), out UserDef? u))
                return 1;
            var sess = SessionFor(u.Name, u.FullName, u.Roles);
            Start(sess);
            return 0;
        }

        /// <summary>A simulated login without users.dbf.</summary>
        public Session LoginAs(
            string name = "User",
            string fullName = "",
            JsonValue? privileges = null,
            JsonValue? areas = null,
            IEnumerable<string>? roles = null
        )
        {
            var roleList = roles != null ? new List<string>(roles) : new List<string>();
            var sess = SessionFor(name, fullName.Length > 0 ? fullName : name, roleList);
            var gp = new HashSet<int>(sess.GlobalPrivs);
            var ap = new Dictionary<int, HashSet<int>>();
            foreach (var kv in sess.AreaPrivs)
                ap[kv.Key] = new HashSet<int>(kv.Value);
            if (privileges != null && !privileges.IsNull)
            {
                var items = new List<(JsonValue area, JsonValue lv)>();
                if (privileges.Kind == JsonKind.Object)
                    foreach (var kv in privileges.Pairs)
                        items.Add((JsonValue.Of(kv.Key), kv.Value));
                else
                    items.Add((JsonValue.Of(0), privileges));
                foreach (var (areaV, lv) in items)
                {
                    var levels = Levels(lv);
                    BigInteger a = areaV.ToInteger();
                    if (a.IsZero)
                        gp.UnionWith(levels);
                    else
                        foreach (int n in levels)
                        {
                            if (!ap.TryGetValue(n, out var set))
                                ap[n] = set = new HashSet<int>();
                            set.Add(ClampInt(a));
                        }
                }
            }
            var view = new HashSet<int>(sess.ViewAreas);
            if (areas != null && !areas.IsNull)
            {
                if (areas.Kind == JsonKind.String)
                    view.UnionWith(ParseList(areas.Str, Groups()));
                else if (areas.Kind == JsonKind.Array)
                    foreach (var a in areas.Items)
                        view.Add(ClampInt(a.ToInteger()));
                else if (areas.Kind == JsonKind.Object)
                    foreach (var kv in areas.Pairs)
                        view.Add(ClampInt(JsonValue.Of(kv.Key).ToInteger()));
                else
                    throw new FormatException(
                        "view areas are a list, an object or a text such as '1..3', not " + areas.Dump()
                    );
            }
            sess.GlobalPrivs = gp;
            sess.AreaPrivs = ap;
            sess.ViewAreas = view;
            Start(sess);
            return sess;
        }

        private Session SessionFor(string name, string fullName, List<string> roleNames)
        {
            var gp = new HashSet<int>();
            var ap = new Dictionary<int, HashSet<int>>();
            var view = new HashSet<int>();
            foreach (string rn in roleNames)
            {
                if (!Roles().TryGetValue(CitectText.Lower(rn), out Role? role))
                    continue;
                gp.UnionWith(role.GlobalPrivs);
                foreach (var kv in role.AreaPrivs)
                {
                    if (!ap.TryGetValue(kv.Key, out var set))
                        ap[kv.Key] = set = new HashSet<int>();
                    set.UnionWith(kv.Value);
                }
                view.UnionWith(role.ViewAreas);
            }
            return new Session
            {
                Name = name,
                FullName = fullName,
                GlobalPrivs = gp,
                AreaPrivs = ap,
                ViewAreas = view,
                Roles = new List<string>(roleNames),
                LoginType = 1,
            };
        }

        private void Start(Session sess)
        {
            sess.LoginTime = Now();
            sess.LastCommand = sess.LoginTime;
            Session = sess;
            Serial++;
            foreach (var fn in Listeners.ToArray())
                fn(sess);
        }

        public int Logout()
        {
            Session = new Session();
            Serial++;
            foreach (var fn in Listeners.ToArray())
                fn(Session);
            return 0;
        }

        /// <summary>A command was entered (UserInfo 4 / 5).</summary>
        public void NoteCommand()
        {
            Session.Commands++;
            Session.LastCommand = Now();
        }

        // ------------------------------------------------------------------ checks
        private HashSet<int> LevelsIn(int area)
        {
            var s = Session;
            var levels = new HashSet<int>(s.GlobalPrivs);
            if (area == 0)
                foreach (int lv in s.AreaPrivs.Keys)
                    levels.Add(lv);
            else
                foreach (var kv in s.AreaPrivs)
                    if (kv.Value.Contains(area))
                        levels.Add(kv.Key);
            return levels;
        }

        /// <summary>GetPriv(Priv, Area): 1 or 0 (arguments already checked by the caller).
        /// Privilege 0 asks whether the area is viewable.</summary>
        public int GetPriv(int priv, int area = 0)
        {
            if (priv <= 0)
                return CanView(area) ? 1 : 0;
            var levels = LevelsIn(area);
            if (Exclusive)
                return levels.Contains(priv) ? 1 : 0;
            foreach (int lv in levels)
                if (lv >= priv)
                    return 1;
            return 0;
        }

        /// <summary>GetPriv's argument check.</summary>
        public static bool PrivArgsOk(int priv, int area) =>
            priv >= 0 && priv < 9 && area >= 0 && area < 0x100;

        /// <summary>The area is viewable: area 0, a viewable area, any global privilege, or a
        /// privilege in that area (doc: 1095085).</summary>
        public bool CanView(int area)
        {
            var s = Session;
            // SetArea narrowed the current areas: only those, of the ones it may view
            if (s.CurrentAreas != null && area != 0 && !s.CurrentAreas.Contains(area))
                return false;
            if (area == 0 || s.ViewAreas.Contains(area) || s.GlobalPrivs.Count > 0)
                return true;
            foreach (var areas in s.AreaPrivs.Values)
                if (areas.Contains(area))
                    return true;
            return false;
        }

        /// <summary>An element with privilege priv in area may be operated: viewable and the
        /// privilege held there.</summary>
        public bool CanOperate(int priv, int area) => CanView(area) && GetPriv(priv, area) != 0;

        public string UserInfo(int kind)
        {
            var s = Session;
            switch (kind)
            {
                case 0:
                    return s.IsDefault ? "0" : "1";
                case 1:
                    return s.Name;
                case 2:
                    return s.FullName;
                case 3:
                    return ((long)Math.Truncate(s.LoginTime)).ToString(CultureInfo.InvariantCulture);
                case 4:
                    return ((long)Math.Truncate(s.LastCommand)).ToString(CultureInfo.InvariantCulture);
                case 5:
                    return s.Commands.ToString(CultureInfo.InvariantCulture);
                case 6:
                    return s.LoginType.ToString(CultureInfo.InvariantCulture);
            }
            return "";
        }

        private static HashSet<int> Levels(JsonValue value)
        {
            var o = new HashSet<int>();
            switch (value.Kind)
            {
                case JsonKind.Bool:
                    return o;
                case JsonKind.Int:
                {
                    var n = value.BigValue;
                    for (long i = 1, top = ClampInt(n); i <= top; i++)
                        o.Add((int)i);
                    return o;
                }
                case JsonKind.String:
                {
                    string v = CitectText.Trim(value.Str);
                    if (CitectText.IsDigits(v))
                    {
                        var n = CitectText.ParseInt(v)!.Value; // digits only
                        for (long i = 1, top = ClampInt(n); i <= top; i++)
                            o.Add((int)i);
                        return o;
                    }
                    foreach (int n2 in ParseList(v))
                        if (n2 > 0)
                            o.Add(n2);
                    return o;
                }
                case JsonKind.Array:
                    foreach (var x in value.Items)
                    {
                        var n = x.ToInteger();
                        if (n > 0)
                            o.Add(ClampInt(n));
                    }
                    return o;
                case JsonKind.Object:
                    foreach (var kv in value.Pairs)
                    {
                        var n = JsonValue.Of(kv.Key).ToInteger();
                        if (n > 0)
                            o.Add(ClampInt(n));
                    }
                    return o;
                default:
                    throw new FormatException(
                        "privilege levels are a number, a list or a text such as '1..3', not " + value.Dump()
                    );
            }
        }
    }
}
