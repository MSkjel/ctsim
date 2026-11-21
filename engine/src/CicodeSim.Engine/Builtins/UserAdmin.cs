using System;
using System.Collections.Generic;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;

namespace CicodeSim.Builtins
{
    /// <summary>User database built-ins: verify, log in, add and change users.</summary>
    public static class UserAdmin
    {
        private const int Exists = 328,
            NoType = 327,
            NoRole = 584,
            CannotModify = 379,
            NameExists = 380,
            NotFound = 289,
            NoPriv = 276,
            NotInArea = 277,
            BadPassword = 436,
            AuthFailed = 439,
            Expired = 375,
            AllDigits = 0x860,
            NoGroup = 280,
            OutOfRange = 257;

        private static UserStore Store(SimHost h)
        {
            var s = UserStore.Of(h.World!);
            s.ExpiryDays = UserStore.ExpiryOf(Params.Get(h, "General", "PasswordExpiry"));
            return s;
        }

        private static Func<uint> Rng(SimHost h)
        {
            var r = ProcessData.Of(h).Random;
            return () => r.Next();
        }

        private static string Arg(ArgList a, int k) => a.Count > k ? A.S(a.Get(k)) : "";

        /// <summary>A message on the task's window's prompt line, logged.</summary>
        internal static void Prompt(SimHost h, CicodeTask t, string text)
        {
            var d = DisplayBuiltins.Of(h);
            d.SetPrompt(d.WindowOf(t), text);
            h.Note(t, "prompt: " + text);
        }

        /// <summary>The runtime table's user (UserDefs: the compiled _USERS.RDB the runtime reads).</summary>
        private static UserDef? Defined(SimHost h, string name) =>
            h.World!.Users.UserDefs().TryGetValue(CitectText.Lower(CitectText.Trim(name)), out var u)
                ? u
                : null;

        /// <summary>the runtime's authentication of a name and password: a user of the users
        /// table checks its PASSWORD (0, 436, 375); any other name goes to Windows authentication,
        /// which the simulator does not have (439, guess on the code).</summary>
        private static int Authenticate(SimHost h, string name, string password, out UserDef? user)
        {
            user = Defined(h, name);
            if (user == null)
                return AuthFailed;
            return Store(h).Check(user, password);
        }

        // ------------------------------------------------------------------ area and privilege lists
        /// <summary>A list of area numbers and groups.dbf group
        /// names; an element of 256 or more 257, an unknown name 280 (the elements before it kept).
        /// 'a..b' is read as a range (guess: the lexer's tokens for '..' were not read).</summary>
        internal static int ParseAreas(SimHost h, string text, HashSet<int> into)
        {
            var groups = h.World!.Users.Groups();
            int err = 0;
            foreach (string raw in text.Replace(';', ',').Split(','))
            {
                string p = CitectText.Trim(raw);
                if (p.Length == 0)
                    continue;
                if (groups.ContainsKey(CitectText.Lower(p)))
                {
                    foreach (int e in World.Users.ParseList(p, groups))
                        if (e >= 0 && e < 256)
                            into.Add(e);
                        else
                            err = OutOfRange;
                    continue;
                }
                int dd = p.IndexOf("..", StringComparison.Ordinal);
                string lo = dd >= 0 ? CitectText.Trim(p.Substring(0, dd)) : p,
                    hi = dd >= 0 ? CitectText.Trim(p.Substring(dd + 2)) : p;
                if (!CitectText.IsDigits(lo) || !CitectText.IsDigits(hi))
                {
                    err = NoGroup;
                    continue;
                }
                long a = Crt.Atol(lo),
                    b = Crt.Atol(hi);
                if (a > b)
                {
                    long x = a;
                    a = b;
                    b = x;
                }
                for (long i = a; i <= b && i < 0x10000; i++)
                    if (i < 256)
                        into.Add((int)i);
                    else
                        err = OutOfRange;
            }
            return err;
        }

        // ------------------------------------------------------------------ verify / login
        [Builtin(
            "UserVerify",
            Doc = "1192359",
            Errors = "436 a wrong password; 375 the password expired ([General]PasswordExpiry); 439 a name the users table does not hold (no Windows authentication in the simulator; guess on the code); 277 a required area the user may not view; 276 a required privilege the user does not hold; 280 / 257 a bad area list (returned, never raised)",
            Notes = "Authenticates the user against the users table (the PASSWORD hash of PWD_VER 2: HMAC-SHA1 of the password keyed with SEED, upper-case hex, the first 40 characters compared; versions 0 / 1 are not simulated and never match), then checks what it would log in with (World.Users.SessionOf, not logged in): every area of sAccess viewable, every level n held in each area of sPriv<n> (the exact level, no Exclusive ladder: the runtime compares bit masks), every level of sPrivGlobal held in area 0 (guess: the runtime puts the global list into area 0's mask). Does not log the user in. The password is the plain text here (the runtime takes a secure string)."
        )]
        private static Value UserVerify(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("UserVerify");
            var areas = new HashSet<int>();
            int err = ParseAreas(h, Arg(a, 2), areas);
            if (err != 0)
                return W.I(err);
            var global = new HashSet<int>();
            err = ParseAreas(h, Arg(a, 3), global);
            if (err != 0)
                return W.I(err);
            var perLevel = new List<HashSet<int>>();
            for (int n = 1; n <= 8; n++)
            {
                var s = new HashSet<int>();
                err = ParseAreas(h, Arg(a, 3 + n), s);
                if (err != 0)
                    return W.I(err);
                perLevel.Add(s);
            }
            int code = Authenticate(h, Arg(a, 0), Arg(a, 1), out UserDef? u);
            if (code != 0)
                return W.I(code);
            var sess = h.World.Users.SessionOf(u!);
            var sorted = new List<int>(areas);
            sorted.Sort();
            foreach (int ar in sorted)
                if (!World.Users.MayView(sess, ar))
                    return W.I(NotInArea);
            foreach (int g in global)
                if (g >= 1 && g <= 8 && !World.Users.HoldsLevel(sess, 0, g))
                    return W.I(NoPriv);
            for (int n = 1; n <= 8; n++)
                foreach (int ar in perLevel[n - 1])
                    if (!World.Users.HoldsLevel(sess, ar, n))
                        return W.I(NoPriv);
            return W.I(0);
        }

        [Builtin(
            "UserLogin",
            Doc = "1192353",
            Effects = "logs the user in (World.Users.Login); sLanguage sets the language GetLanguage gives",
            Errors = "2144 a name of digits only (returned); 436 a wrong password, 375 an expired one, 439 a name the users table does not hold (prompt 'Invalid username or password' / 'User password expired'; raised and returned)",
            Notes = "The password check of UserVerify (the users.dbf hash with its seed). A language the project does not define leaves the language and writes 'Undefined language' to the prompt line (doc). The password is plain text here (the runtime takes a secure string)."
        )]
        private static Value UserLogin(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("UserLogin");
            string name = Arg(a, 0);
            bool digits = true;
            foreach (char c in name)
                if (c < '0' || c > '9')
                {
                    digits = false;
                    break;
                }
            if (digits)
                return W.I(AllDigits);
            int code = Authenticate(h, name, Arg(a, 1), out UserDef? u);
            if (code != 0)
            {
                Prompt(h, t, code == Expired ? "User password expired" : "Invalid username or password");
                W.Raise(t, code);
                return W.I(code);
            }
            h.World.Users.Login(u!.Name);
            string lang = Arg(a, 2);
            if (CitectText.Trim(lang).Length > 0 && !LanguageInfo.Select(h, lang))
                Prompt(h, t, "Undefined language");
            return W.I(0);
        }

        [Builtin(
            "WhoAmI",
            Doc = "1192362",
            Effects = "writes the user to the prompt line",
            Notes = "The prompt line gets 'User '<name>' Full Name '<full name>'', or 'Nobody Logged In' for the default user; 0."
        )]
        private static Value WhoAmI(SimHost h, CicodeTask t, ArgList a)
        {
            var s = h.World?.Users.Session;
            string name = s == null || s.IsDefault ? "" : s.Name;
            if (name.Length > 31)
                name = name.Substring(0, 31);
            Prompt(
                h,
                t,
                name.Length == 0
                    ? "Nobody Logged In"
                    : C.Cut("User '" + name + "' Full Name '" + s!.FullName + "'", 255)
            );
            return W.I(0);
        }

        // ------------------------------------------------------------------ passwords
        [Builtin(
            "UserPassword",
            Doc = "1192354",
            Effects = "sets the user's PASSWORD, SEED and PWD_VER in the simulated users database (a new seed, the hash, the time) and reloads the runtime's users",
            Errors = "276 no old password and the session may not manage users (returned); 274 a password of 41+ characters, or a user the root project's users.dbf does not hold when the old password is given (returned); 289 no such user without the old password (returned); 436 a wrong old password (returned)",
            Notes = "With sOldPassword the first record of that name whose password it is gets the new one (records of the same name in turn, as the runtime loops); without it the session needs a role with MANAGE_USR. The seed is drawn from the process's seeded generator (the runtime's RNGCryptoServiceProvider). Only the root project's users can be changed (doc: 'contained in the main project')."
        )]
        private static Value UserPassword(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("UserPassword");
            string name = Arg(a, 0),
                pw = Arg(a, 1),
                old = Arg(a, 2);
            var st = Store(h);
            if (old.Length == 0)
            {
                if (!h.World.Users.CanManageUsers())
                    return W.I(NoPriv);
                if (pw.Length > 40)
                    return W.I(Codes.InvalidArg);
                var rec = st.Find("NAME", name);
                if (rec == null)
                    return W.I(NotFound);
                st.SetPassword(rec, pw, Rng(h));
                st.Reload();
                return W.I(0);
            }
            if (old.Length > 40 || pw.Length > 40)
                return W.I(Codes.InvalidArg);
            bool any = false;
            foreach (var rec in st.Rows)
            {
                if (!rec.TryGetValue("NAME", out string? n) || !UserStore.Same(n, name))
                    continue;
                any = true;
                var u = new UserDef
                {
                    Password = Get(rec, "PASSWORD"),
                    Seed = Get(rec, "SEED"),
                    PwdVer = Get(rec, "PWD_VER"),
                };
                if (
                    (int)Crt.Atol(u.PwdVer) != 2
                    || u.Password.Length < 40
                    || string.CompareOrdinal(u.Password, 0, UserStore.Hash(old, u.Seed), 0, 40) != 0
                )
                    continue;
                st.SetPassword(rec, pw, Rng(h));
                st.Reload();
                return W.I(0);
            }
            return W.I(any ? BadPassword : Codes.InvalidArg);
        }

        private static string Get(Dictionary<string, string> rec, string col) =>
            rec.TryGetValue(col, out string? v) ? CitectText.Trim(v) : "";

        [Builtin(
            "UserPasswordExpiryDays",
            Doc = "1192355",
            Notes = "From the runtime's users: without a password the days left of the user's password ([General]PasswordExpiry less the whole days since the day it was set, at least 0), -1 when passwords never expire or the user has no password, -2 for no such user. With a password the runtime compares the password's old cipher (at most 16 characters) with the stored hash, which a version-2 hash never equals: -2."
        )]
        private static Value UserPasswordExpiryDays(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("UserPasswordExpiryDays");
            string name = Arg(a, 0),
                pw = Arg(a, 1);
            if (pw.Length > 40)
                return W.I(-2);
            var u = Defined(h, name);
            if (u == null || pw.Length > 0)
                return W.I(-2);
            if (u.Password.Length == 0)
                return W.I(-1);
            return W.I(Store(h).DaysLeft(UserStore.StampOf(u.Password)));
        }

        // ------------------------------------------------------------------ create / delete / change
        private static Value Create(SimHost h, CicodeTask t, ArgList a, bool byRole, bool roleList)
        {
            var w = h.World!;
            string name = Arg(a, 0),
                full = Arg(a, 1),
                pw = Arg(a, 2),
                what = Arg(a, 3);
            if (!w.Users.CanManageUsers())
                return W.I(NoPriv);
            if (name.Length == 0 || pw.Length > 40)
                return W.I(Codes.InvalidArg);
            var st = Store(h);
            if (name.Length > st.Width("NAME") || full.Length > st.Width("FULL_NAME"))
                return W.I(Codes.InvalidArg);
            if (st.Find("NAME", name) != null)
                return W.I(Exists);
            var roles = w.Users.Roles();
            if (roleList)
            {
                // split at ',' (empty tokens skipped), each token's blanks trimmed: every role must exist
                foreach (string tok in what.Split(','))
                {
                    if (tok.Length == 0)
                        continue;
                    string r = tok.Trim(' ', '\t', '\n', '\v', '\f', '\r');
                    if (!roles.ContainsKey(CitectText.Lower(r)))
                        return W.I(NoRole);
                }
            }
            else if (byRole)
            {
                if (!roles.ContainsKey(CitectText.Lower(CitectText.Trim(what))))
                    return W.I(NoRole);
            }
            else if (st.Find("TYPE", what) == null)
                return W.I(NoType);
            var rec = st.Append();
            if (byRole || roleList)
                rec[st.Column("ROLES") ?? "ROLES"] = what;
            rec[st.Column("TYPE") ?? "TYPE"] = "";
            rec[st.Column("NAME") ?? "NAME"] = name;
            if (full.Length > 0)
                rec[st.Column("FULL_NAME") ?? "FULL_NAME"] = full;
            st.SetPassword(rec, pw, Rng(h));
            st.Reload();
            return W.I(0);
        }

        [Builtin(
            "UserCreate",
            Doc = "1192347; 1186791",
            Effects = "adds the user to the simulated users database and reloads the runtime's users",
            Errors = "276 the session may not manage users; 274 an empty name, a password of 41+ characters, a name or full name longer than its users.dbf field; 328 the user exists; 584 ([Security]CreateUserByRole 1) the role is not defined; 327 (0) no user has that TYPE (all returned)",
            Notes = "[Security]CreateUserByRole 1: the 4th argument is a role (ROLES); 0 (default): a user type that some user's TYPE must have, and the new user gets no ROLES and an empty TYPE (the runtime writes TYPE '' in both modes). The password is hashed as UserPassword's."
        )]
        private static Value UserCreate(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("UserCreate");
            bool byRole = Crt.Atol(Params.Get(h, "Security", "CreateUserByRole") ?? "0") == 1;
            return Create(h, t, a, byRole, false);
        }

        [Builtin(
            "UserCreateByRole",
            Doc = "1192348",
            Effects = "adds the user with its roles to the simulated users database and reloads the runtime's users",
            Errors = "276 the session may not manage users; 274 an empty name, a password of 41+ characters, a name or full name longer than its field; 328 the user exists; 584 a role of the list is not defined (all returned)",
            Notes = "sRole is a comma-separated list (strtok_s, each role trimmed); it is stored as given in ROLES, TYPE ''."
        )]
        private static Value UserCreateByRole(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("UserCreateByRole");
            return Create(h, t, a, true, true);
        }

        [Builtin(
            "UserDelete",
            Doc = "1192350",
            Effects = "removes the user's first record from the simulated users database and reloads the runtime's users",
            Errors = "276 the session may not manage users (returned)",
            Notes = "No such user: 0, nothing changes (the search fails and the function closes the table and returns 0). The logged-in session stays."
        )]
        private static Value UserDelete(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("UserDelete");
            if (!h.World.Users.CanManageUsers())
                return W.I(NoPriv);
            var st = Store(h);
            var rec = st.Find("NAME", Arg(a, 0));
            if (rec == null)
                return W.I(0);
            st.Rows.Remove(rec);
            st.Reload();
            return W.I(0);
        }

        [Builtin(
            "UserSetStr",
            Doc = "1192357",
            Effects = "sets a field of the user's record in the simulated users database (not the runtime's users until UserUpdateRecord)",
            Errors = "379 the field PASSWORD; 276 another user's record and the session may not manage users; 380 NAME set to a name that exists; 274 no such user, or a field users.dbf does not have (guess on the code) (all returned)",
            Notes = "The session's own record needs no privilege (its name compared exactly, strcmp); a value longer than its field is cut to it (guess: the table write)."
        )]
        private static Value UserSetStr(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("UserSetStr");
            string name = Arg(a, 0),
                field = Arg(a, 1),
                value = Arg(a, 2);
            if (C.IEq(field, "PASSWORD"))
                return W.I(CannotModify);
            var users = h.World.Users;
            if (!string.Equals(users.Session.Name, name, StringComparison.Ordinal) && !users.CanManageUsers())
                return W.I(NoPriv);
            var st = Store(h);
            if (C.IEq(field, "NAME") && st.Find("NAME", value) != null)
                return W.I(NameExists);
            var rec = st.Find("NAME", name);
            if (rec == null)
                return W.I(Codes.InvalidArg);
            string? col = st.Column(field);
            if (col == null)
                return W.I(Codes.InvalidArg);
            int width = st.Width(col);
            rec[col] = value.Length > width ? value.Substring(0, width) : value;
            return W.I(0);
        }

        [Builtin(
            "UserUpdateRecord",
            Doc = "1192358",
            Effects = "reloads the runtime's users from the simulated users database",
            Notes = "The runtime recompiles the users and notifies the running system; the simulator rebuilds World.Users' definitions from its users database at once. 0."
        )]
        private static Value UserUpdateRecord(SimHost h, CicodeTask t, ArgList a)
        {
            if (h.World == null)
                return Tags.NoWorld("UserUpdateRecord");
            Store(h).Reload();
            return W.I(0);
        }

        // ------------------------------------------------------------------ current areas
        [Builtin(
            "SetArea",
            Doc = "1188616",
            Effects = "sets the session's current viewable areas (World.Users.SetCurrentAreas)",
            Errors = "269 a negative value, or a value with bit 0x4000 that is no open group (raised and returned); 274 an area the user may not view (raised and returned; the others are set)",
            Notes = "Area may be a number 0..255 or a group handle (GrpOpen; GetArea's group gives the areas now current). The current areas become area 0 and the asked areas the user may view; until the next login. A negative value (bit 15: an index into a table the runtime checks) is taken as no such handle (guess)."
        )]
        private static Value SetArea(SimHost h, CicodeTask t, ArgList a)
        {
            var w = h.World;
            if (w == null)
                return Tags.NoWorld("SetArea");
            long v = A.Short(A.I(a.Get(0)));
            var asked = new HashSet<int>();
            if (v < 0)
            {
                W.Raise(t, Codes.BadHandle);
                return W.I(Codes.BadHandle);
            }
            if ((v & UsersGroups.HandleBit) != 0)
            {
                var g = ProcessData.Of(h).GroupTable.Get(v & 0xBFFF);
                if (g == null)
                {
                    W.Raise(t, Codes.BadHandle);
                    return W.I(Codes.BadHandle);
                }
                for (int i = 0; i < 256; i++)
                    if (g.IsArea ? w.Users.CanView(i) : g.Has[i])
                        asked.Add(i);
            }
            else if (v < 256)
                asked.Add((int)v);
            var sess = w.Users.Session;
            var current = new HashSet<int> { 0 };
            int code = 0;
            foreach (int ar in asked)
            {
                if (World.Users.MayView(sess, ar))
                    current.Add(ar);
                else
                    code = Codes.InvalidArg;
            }
            w.Users.SetCurrentAreas(current);
            if (code != 0)
                W.Raise(t, code);
            return W.I(code);
        }
    }
}
