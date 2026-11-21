using System;
using System.Collections.Generic;
using System.Text;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>Login and group built-ins.</summary>
    public static class UsersGroups
    {
        public const int Elements = 0x3ff8;
        public const int HandleBit = 0x4000;

        internal sealed class Group
        {
            public string Name = "";
            public readonly bool[] Has = new bool[Elements];

            /// <summary>The login area group (GetArea): filled from the session when read.</summary>
            public bool IsArea;
        }

        private static HandleTable<Group> Table(SimHost h)
        {
            var tab = ProcessData.Of(h).GroupTable;
            if (tab.Count == 0 && tab.Get(0) == null)
                tab.Add(new Group { IsArea = true }); // slot 0: the area group made at start
            return tab;
        }

        /// <summary>The open group a value names (bit 0x4000 and a slot in use).</summary>
        private static Group? Find(SimHost h, long v)
        {
            long x = v & 0xFFFF;
            if ((x & HandleBit) == 0)
                return null;
            var g = Table(h).Get(x & 0xBFFF);
            if (g != null && g.IsArea)
                FillArea(h, g);
            return g;
        }

        /// <summary>The area group: the areas 0..255 the session may view (guess: the runtime
        /// fills it at login from the user's areas).</summary>
        private static void FillArea(SimHost h, Group g)
        {
            Array.Clear(g.Has, 0, g.Has.Length);
            var u = h.World?.Users;
            for (int a = 0; a < 256; a++)
                g.Has[a] = u == null ? a == 0 : u.CanView(a);
        }

        /// <summary>A groups.dbf group's elements, or null when there is no such group.</summary>
        private static HashSet<int>? DatabaseGroup(SimHost h, string name)
        {
            var u = h.World?.Users;
            if (u == null)
                return null;
            var groups = u.Groups();
            if (!groups.TryGetValue(CitectText.Lower(CitectText.Trim(name)), out var members))
                return null;
            var o = new HashSet<int>();
            var seen = new HashSet<string>(StringComparer.Ordinal)
            {
                CitectText.Lower(CitectText.Trim(name)),
            };
            foreach (string m in members)
                o.UnionWith(World.Users.ParseList(m, groups, seen));
            return o;
        }

        // ------------------------------------------------------------------ the login
        [Builtin(
            "UserInfo",
            Doc = "1192352",
            Notes = "0 '1' when a user other than the default one is logged in else '0', 1 the name, 2 the full name, 3 the login time and 4 the last command time (Unix seconds of the simulated clock), 5 the command count, 6 the login type; other types ''."
        )]
        private static Value UserInfo(SimHost h, CicodeTask t, ArgList a)
        {
            var u = h.World?.Users;
            long type = A.Short(A.I(a.Get(0)));
            if (u == null || type < 0 || type > 6)
                return W.Empty;
            return W.S(C.Cut(u.UserInfo((int)type)));
        }

        [Builtin(
            "GetPriv",
            Doc = "1192339",
            Errors = "274 a privilege outside 0..8 or an area outside 0..255 (raised, 0)",
            Notes = "1 when the session holds the privilege in the area (World.Users: global privileges count everywhere, area 0 every area's, [Privilege]Exclusive); privilege 0 = the area is viewable."
        )]
        private static Value GetPriv(SimHost h, CicodeTask t, ArgList a)
        {
            long priv = A.UShort(A.I(a.Get(0))),
                area = A.UShort(A.I(a.Get(1)));
            if (priv >= 9 || area >= 0x100)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(0);
            }
            var u = h.World?.Users;
            return W.I(u == null ? (priv == 0 && area == 0 ? 1 : 0) : u.GetPriv((int)priv, (int)area));
        }

        [Builtin(
            "Name",
            Doc = "1192346",
            Notes = "The logged-in user's name; '' for the default user. bIncludeDomain is for Windows users (none here)."
        )]
        private static Value Name(SimHost h, CicodeTask t, ArgList a)
        {
            var u = h.World?.Users;
            return W.S(u == null || u.Session.IsDefault ? "" : C.Cut(u.Session.Name));
        }

        [Builtin(
            "AreaCheck",
            Doc = "1188576",
            Notes = "1 when the session may view the area (World.Users.CanView: area 0, a viewable area, a global privilege or a privilege in it), else 0; an area above 255 0 (no error)."
        )]
        private static Value AreaCheck(SimHost h, CicodeTask t, ArgList a)
        {
            long area = A.UShort(A.I(a.Get(0)));
            if (area > 0xFF)
                return W.I(0);
            var u = h.World?.Users;
            return W.I(
                u == null ? (area == 0 ? 1 : 0)
                : u.CanView((int)area) ? 1
                : 0
            );
        }

        [Builtin(
            "FullName",
            Doc = "1192337",
            Notes = "The logged-in user's full name; '' for the default user."
        )]
        private static Value FullName(SimHost h, CicodeTask t, ArgList a) =>
            W.S(C.Cut(h.World?.Users.Session.FullName ?? ""));

        [Builtin(
            "GetArea",
            Doc = "1188589",
            Notes = "The handle of the process's area group (16384: the first group, made at start); it holds the areas 0..255 the session may view, filled when read (guess on the content); changing it does not change the login."
        )]
        private static Value GetArea(SimHost h, CicodeTask t, ArgList a)
        {
            Table(h);
            return W.I(HandleBit);
        }

        [Builtin(
            "Login",
            Doc = "1192340",
            Effects = "logs the user in (World.Users.Login)",
            Errors = "274 an unknown user (prompt 'Invalid username or password'; raised and returned; guess on the code); 282 bSync in foreground code (raised and returned)",
            Notes = "Already logged in as that user: 0. The password is not checked (users.dbf holds no readable password). A non-zero bSync waits one turn; in foreground code the user is logged in first, then 282 is raised and returned (the login, then CodeBlock; the runtime waits only while counts a pending server entry: verify). sLanguage is ignored (no language switch simulated)."
        )]
        private static Value Login(SimHost h, CicodeTask t, ArgList a)
        {
            var w = h.World;
            if (w == null)
                return Tags.NoWorld("Login");
            string user = A.S(a.Get(0));
            bool sync = a.Count > 2 && A.Short(A.I(a.Get(2))) != 0; // CodeBlock for any non-zero bSync
            var u = w.Users;
            if (!u.Session.IsDefault && C.IEq(u.Session.Name, user))
                return W.I(0);
            if (u.Login(user, A.S(a.Get(1))) != 0)
            {
                h.Note(t, "Prompt: Invalid username or password");
                W.Raise(t, Codes.InvalidArg);
                return W.I(Codes.InvalidArg);
            }
            if (sync)
            {
                if (t.Foreground)
                {
                    W.Raise(t, Codes.FgBlock);
                    return W.I(Codes.FgBlock);
                }
                return W.BlockOneTurn(t, "Login", () => W.I(0));
            }
            return W.I(0);
        }

        [Builtin(
            "Logout",
            Doc = "1192342",
            Effects = "logs the user out (the default user: area 0, privilege 0)",
            Errors = "449 'Invalid logout' when only the default user is logged in (returned)"
        )]
        private static Value Logout(SimHost h, CicodeTask t, ArgList a)
        {
            var u = h.World?.Users;
            if (u == null || u.Session.IsDefault)
                return W.I(449);
            u.Logout();
            return W.I(0);
        }

        // ------------------------------------------------------------------ groups
        [Builtin(
            "GrpOpen",
            Doc = "1106205",
            Notes = "The name (blanks trimmed, 31 characters) is looked up among the open groups first (without case), then in groups.dbf (a copy is made: its numbers, ranges 'a..b' and member groups). Mode 0 opens (-1 when neither), 1 creates (-1 when the name exists), 2 either. Handles are slot | 0x4000."
        )]
        private static Value GrpOpen(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.SkipSpace(A.S(a.Get(0)));
            if (name.Length > 31)
                name = name.Substring(0, 31);
            long mode = A.Short(A.I(a.Get(1)));
            var tab = Table(h);
            int found = -1;
            foreach (var (hd, g) in tab.Live())
                if (!g.IsArea && C.IEq(g.Name, name))
                {
                    found = hd;
                    break;
                }
            if (found < 0)
            {
                var db = DatabaseGroup(h, name);
                if (db != null)
                {
                    var g = new Group { Name = name };
                    foreach (int e in db)
                        if (e >= 0 && e < Elements)
                            g.Has[e] = true;
                    found = tab.Add(g);
                }
            }
            if (mode == 0)
                return W.I(found >= 0 ? found | HandleBit : -1);
            if (mode == 1 && found >= 0)
                return W.I(-1);
            if (mode != 1 && mode != 2)
                return W.I(-1);
            if (found >= 0)
                return W.I(found | HandleBit);
            if (tab.Count >= 0x3FFF)
                return W.I(-1);
            return W.I(tab.Add(new Group { Name = name }) | HandleBit);
        }

        [Builtin("GrpClose", Doc = "1106197", Errors = "269 a bad handle (returned)")]
        private static Value GrpClose(SimHost h, CicodeTask t, ArgList a)
        {
            long v = A.I(a.Get(0)) & 0xFFFF;
            if (Find(h, v) == null)
                return W.I(Codes.BadHandle);
            Table(h).Free(v & 0xBFFF);
            return W.I(0);
        }

        [Builtin("GrpName", Doc = "1106203", Notes = "'' for a bad handle.")]
        private static Value GrpName(SimHost h, CicodeTask t, ArgList a) =>
            W.S(Find(h, A.I(a.Get(0)))?.Name ?? "");

        [Builtin(
            "GrpIn",
            Doc = "1106200",
            Notes = "1 when the element (0..16375) is in the group, or when Value is an open group whose every element is in it; else 0 (also for a bad handle)."
        )]
        private static Value GrpIn(SimHost h, CicodeTask t, ArgList a)
        {
            var g = Find(h, A.I(a.Get(0)));
            long v = A.I(a.Get(1)) & 0xFFFF;
            if (g == null)
                return W.I(0);
            var other = Find(h, v);
            if (other != null)
            {
                for (int i = 0; i < Elements; i++)
                    if (other.Has[i] && !g.Has[i])
                        return W.I(0);
                return W.I(1);
            }
            return W.I(v < Elements && g.Has[v] ? 1 : 0);
        }

        [Builtin(
            "GrpInsert",
            Doc = "1106201",
            Effects = "adds the element, every element (-1) or another group's elements",
            Errors = "269 a bad handle (returned); 257 an element outside 0..16375 (returned)"
        )]
        private static Value GrpInsert(SimHost h, CicodeTask t, ArgList a)
        {
            var g = Find(h, A.I(a.Get(0)));
            long v = A.I(a.Get(1)) & 0xFFFF;
            if (g == null)
                return W.I(Codes.BadHandle);
            var other = Find(h, v);
            if (other != null)
            {
                for (int i = 0; i < Elements; i++)
                    if (other.Has[i])
                        g.Has[i] = true;
                return W.I(0);
            }
            if (v == 0xFFFF)
            {
                for (int i = 0; i < Elements; i++)
                    g.Has[i] = true;
                return W.I(0);
            }
            if (v >= Elements)
                return W.I(Codes.OutOfRange);
            g.Has[v] = true;
            return W.I(0);
        }

        [Builtin(
            "GrpDelete",
            Doc = "1106198",
            Effects = "removes the element, every element (-1) or another group's elements",
            Errors = "269 a bad handle (returned); 257 an element outside 0..16375 (returned)"
        )]
        private static Value GrpDelete(SimHost h, CicodeTask t, ArgList a)
        {
            var g = Find(h, A.I(a.Get(0)));
            long v = A.I(a.Get(1)) & 0xFFFF;
            if (g == null)
                return W.I(Codes.BadHandle);
            var other = Find(h, v);
            if (other != null)
            {
                for (int i = 0; i < Elements; i++)
                    if (other.Has[i])
                        g.Has[i] = false;
                return W.I(0);
            }
            if (v == 0xFFFF)
            {
                Array.Clear(g.Has, 0, Elements);
                return W.I(0);
            }
            if (v >= Elements)
                return W.I(Codes.OutOfRange);
            g.Has[v] = false;
            return W.I(0);
        }

        [Builtin(
            "GrpFirst",
            Doc = "1106199",
            Notes = "The lowest element; -1 for an empty group or a bad handle."
        )]
        private static Value GrpFirst(SimHost h, CicodeTask t, ArgList a)
        {
            var g = Find(h, A.I(a.Get(0)));
            if (g != null)
                for (int i = 0; i < Elements; i++)
                    if (g.Has[i])
                        return W.I(i);
            return W.I(-1);
        }

        [Builtin(
            "GrpNext",
            Doc = "1106204",
            Notes = "The lowest element above Value; -1 at the end, for a Value outside 0..32767 or a bad handle."
        )]
        private static Value GrpNext(SimHost h, CicodeTask t, ArgList a)
        {
            var g = Find(h, A.I(a.Get(0)));
            long v = A.I(a.Get(1)) & 0xFFFF;
            if (g != null && v < 0x8000)
                for (long i = v + 1; i < Elements; i++)
                    if (g.Has[i])
                        return W.I(i);
            return W.I(-1);
        }

        [Builtin(
            "GrpToStr",
            Doc = "1106206",
            Notes = "Runs of two or more elements as 'a..b', single ones as 'a', joined by ','; the text stops once it reaches 244 characters. A value that is not a group handle (no bit 0x4000) gives its own number; a bad handle ''."
        )]
        private static Value GrpToStr(SimHost h, CicodeTask t, ArgList a)
        {
            long v = A.I(a.Get(0)) & 0xFFFF;
            var g = Find(h, v);
            if (g == null)
                return W.S((v & HandleBit) == 0 ? C.Inv(A.Short(v)) : "");
            var sb = new StringBuilder();
            int i = 0;
            while (i < Elements)
            {
                if (sb.Length >= 0xf4)
                    break;
                if (!g.Has[i])
                {
                    i++;
                    continue;
                }
                int j = i;
                while (j + 1 < Elements && g.Has[j + 1])
                    j++;
                if (j > i)
                    sb.Append(C.Inv(i)).Append("..").Append(C.Inv(j)).Append(',');
                else
                    sb.Append(C.Inv(i)).Append(',');
                i = j + 1;
            }
            if (sb.Length > 0)
                sb.Length--;
            return W.S(C.Cut(sb.ToString()));
        }

        [Builtin(
            "StrToGrp",
            Doc = "1192589",
            Effects = "replaces the group's elements with the list",
            Errors = "269 a bad handle (returned)",
            Notes = "A list of numbers and 'a..b' ranges separated by ',' (elements outside 0..16375 are skipped); the list is read up to the first token it does not expect."
        )]
        private static Value StrToGrp(SimHost h, CicodeTask t, ArgList a)
        {
            var g = Find(h, A.I(a.Get(0)));
            if (g == null)
                return W.I(Codes.BadHandle);
            Array.Clear(g.Has, 0, Elements);
            string s = A.S(a.Get(1));
            int p = 0;
            bool Num(out long v)
            {
                while (p < s.Length && (s[p] == ' ' || s[p] == '\t'))
                    p++;
                int st = p;
                while (p < s.Length && s[p] >= '0' && s[p] <= '9')
                    p++;
                v = 0;
                if (p == st)
                    return false;
                v = C.Atol(s.Substring(st, Math.Min(p - st, 9)));
                return true;
            }
            while (Num(out long lo))
            {
                while (p < s.Length && (s[p] == ' ' || s[p] == '\t'))
                    p++;
                long hi = lo;
                if (p + 1 < s.Length && s[p] == '.' && s[p + 1] == '.')
                {
                    p += 2;
                    if (!Num(out hi))
                        break;
                    while (p < s.Length && (s[p] == ' ' || s[p] == '\t'))
                        p++;
                }
                if (lo < Elements && hi < Elements)
                    for (long i = lo; i <= hi; i++)
                        g.Has[i] = true;
                if (p < s.Length && s[p] == ',')
                {
                    p++;
                    continue;
                }
                break;
            }
            return W.I(0);
        }

        [Builtin(
            "GrpMath",
            Doc = "1106202",
            Effects = "hResult = hOne op hTwo",
            Errors = "269 a bad handle or type (returned)",
            Notes = "0 union, 1 hOne without hTwo, 2 intersection, 3 NOT hOne (hTwo unused), 4 XOR."
        )]
        private static Value GrpMath(SimHost h, CicodeTask t, ArgList a)
        {
            var r = Find(h, A.I(a.Get(0)));
            var x = Find(h, A.I(a.Get(1)));
            var y = Find(h, A.I(a.Get(2)));
            long type = A.Short(A.I(a.Get(3)));
            if (r == null || x == null || (y == null && type != 3) || type < 0 || type > 4)
                return W.I(Codes.BadHandle);
            var o = new bool[Elements];
            for (int i = 0; i < Elements; i++)
            {
                bool p = x.Has[i],
                    q = y != null && y.Has[i];
                o[i] =
                    type == 0 ? p || q
                    : type == 1 ? p && !q
                    : type == 2 ? p && q
                    : type == 3 ? !p
                    : p != q;
            }
            Array.Copy(o, r.Has, Elements);
            return W.I(0);
        }
    }
}
