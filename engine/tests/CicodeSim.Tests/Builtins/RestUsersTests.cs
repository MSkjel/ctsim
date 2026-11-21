using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CicodeSim.Builtins;
using CicodeSim.Values;
using CicodeSim.World;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>The user database built-ins.</summary>
    [Collection("World")]
    public sealed class RestUsersTests : DataTestBase
    {
        private const string SeedA = "00112233445566778899AABBCCDDEEFF",
            SeedO = "0F1E2D3C4B5A69788796A5B4C3D2E1F0";

        /// <summary>The expected PASSWORD hash, computed here with .NET's HMACSHA1 directly.</summary>
        private static string Hmac(string password, string seed)
        {
            using (var h = new HMACSHA1(Encoding.ASCII.GetBytes(seed)))
            {
                var sb = new StringBuilder();
                foreach (byte b in h.ComputeHash(Encoding.ASCII.GetBytes(password)))
                    sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>A FILETIME stamp of a Unix time, as the runtime appends it ('%10.10lu%10.10lu').</summary>
        private static string Stamp(long unix)
        {
            ulong ft = (ulong)((unix + 11644473600L) * 10000000L);
            return ((uint)(ft >> 32)).ToString("D10", CultureInfo.InvariantCulture)
                + ((uint)ft).ToString("D10", CultureInfo.InvariantCulture);
        }

        private static readonly long Start = DateTimeOffset
            .Parse(Tests.World.WorldFixture.DefaultStart, CultureInfo.InvariantCulture)
            .ToUnixTimeSeconds();

        private Sim MakeUsers(string? expiry = null, bool byRole = false)
        {
            return MakeData(() =>
            {
                Dbf(
                    Path.Combine(MainDir, "users.dbf"),
                    new[] { "NAME", "FULL_NAME", "PASSWORD", "ROLES", "TYPE", "COMMENT", "PWD_VER", "SEED" },
                    new[]
                    {
                        "Admin",
                        "Administrator",
                        Hmac("s3cret", SeedA) + Stamp(Start - 10 * 86400),
                        "Adm",
                        "Eng",
                        "",
                        "2",
                        SeedA,
                    },
                    new[]
                    {
                        "Op",
                        "Operator",
                        Hmac("pw", SeedO) + Stamp(Start - 40 * 86400),
                        "Op",
                        "",
                        "",
                        "2",
                        SeedO,
                    },
                    new[] { "Legacy", "Old cipher", "ABCDEF", "Op", "", "", "0", "" }
                );
                Dbf(
                    Path.Combine(MainDir, "roles.dbf"),
                    new[] { "NAME", "ACCESS", "PRIV_G", "PRIV_1", "MANAGE_USR" },
                    new[] { "Adm", "", "1..8", "", "TRUE" },
                    new[] { "Op", "6", "", "5", "FALSE" }
                );
                var p = new System.Collections.Generic.List<string[]>
                {
                    new[] { "Alarm", "ScanTime", "500" },
                };
                if (expiry != null)
                    p.Add(new[] { "General", "PasswordExpiry", expiry });
                if (byRole)
                    p.Add(new[] { "Security", "CreateUserByRole", "1" });
                Dbf(Path.Combine(MainDir, "param.dbf"), new[] { "SECTION", "NAME", "DATA" }, p.ToArray());
            });
        }

        [Fact]
        public void TheHashIsHmacSha1KeyedWithTheSeedText()
        {
            Assert.Equal(Hmac("s3cret", SeedA), UserStore.Hash("s3cret", SeedA));
            Assert.Equal(40, UserStore.Hash("", SeedA).Length);
            long ft = (Start + 11644473600L) * 10000000L;
            Assert.Equal(ft, UserStore.StampOf(new string('0', 40) + Stamp(Start)));
            Assert.Equal(0, UserStore.StampOf(new string('0', 40)));
        }

        [Fact]
        public void UserVerifyChecksThePasswordThenTheAreasAndPrivileges()
        {
            var s = MakeUsers();
            Assert.Equal(0, Call(s, "UserVerify", "Admin", "s3cret").I);
            Assert.Equal(0, Call(s, "UserVerify", "admin", "s3cret").I); // the name without case
            Assert.Equal(436, Call(s, "UserVerify", "Admin", "S3CRET").I); // the password with case
            Assert.Equal(439, Call(s, "UserVerify", "Nobody", "x").I); // no Windows users
            Assert.Equal(436, Call(s, "UserVerify", "Legacy", "anything").I); // version 0: not simulated
            Assert.Equal(0, Call(s, "UserVerify", "Op", "pw", "6").I);
            Assert.Equal(277, Call(s, "UserVerify", "Op", "pw", "7").I); // area 7 not viewable
            Assert.Equal(0, Call(s, "UserVerify", "Op", "pw", "", "", "5").I); // level 1 in area 5
            Assert.Equal(276, Call(s, "UserVerify", "Op", "pw", "", "", "6").I); // level 1 not in area 6
            Assert.Equal(276, Call(s, "UserVerify", "Op", "pw", "", "", "", "5").I); // level 2 in area 5
            Assert.Equal(0, Call(s, "UserVerify", "Admin", "s3cret", "1,7", "1..8").I);
            Assert.Equal(257, Call(s, "UserVerify", "Admin", "s3cret", "300").I);
            Assert.Equal(280, Call(s, "UserVerify", "Admin", "s3cret", "NoSuchGroup").I);
            // nobody is logged in by it
            Assert.True(s.W.Users.Session.IsDefault);
        }

        [Fact]
        public void PasswordsExpireAfterTheParameterDays()
        {
            var s = MakeUsers("30");
            Assert.Equal(0, Call(s, "UserVerify", "Admin", "s3cret").I); // set 10 days ago
            Assert.Equal(375, Call(s, "UserVerify", "Op", "pw").I); // set 40 days ago
            Assert.Equal(20, Call(s, "UserPasswordExpiryDays", "Admin").I);
            Assert.Equal(0, Call(s, "UserPasswordExpiryDays", "Op").I);
            Assert.Equal(-2, Call(s, "UserPasswordExpiryDays", "Admin", "s3cret").I); // the old cipher never matches
            Assert.Equal(-2, Call(s, "UserPasswordExpiryDays", "Nobody").I);
            var n = MakeUsers();
            Assert.Equal(-1, Call(n, "UserPasswordExpiryDays", "Admin").I);
        }

        [Fact]
        public void UserLoginAuthenticatesAndWhoAmIPrompts()
        {
            var s = MakeUsers();
            Assert.Equal(0x860, Call(s, "UserLogin", "123", "x").I);
            var r = Call(s, "UserLogin", "Op", "wrong");
            Assert.Equal(436, r.I);
            Assert.Equal(436, r.Error);
            Assert.True(s.W.Users.Session.IsDefault);
            Assert.Equal(0, Call(s, "WhoAmI").I);
            Assert.Contains("prompt: Nobody Logged In", s.Log);
            Assert.Equal(0, Call(s, "UserLogin", "Op", "pw").I);
            Assert.Equal("Op", s.W.Users.Session.Name);
            Call(s, "WhoAmI");
            Assert.Contains("prompt: User 'Op' Full Name 'Operator'", s.Log);
            Assert.Equal(0, Call(s, "UserLogin", "Admin", "s3cret", "Klingon").I);
            Assert.Contains("prompt: Undefined language", s.Log);
            Assert.Equal("English", Call(s, "GetLanguage", 0).S);
        }

        [Fact]
        public void CreatingAndDeletingUsersNeedsAManageUsersRole()
        {
            var s = MakeUsers();
            string file = Path.Combine(MainDir, "users.dbf");
            byte[] before = File.ReadAllBytes(file);
            Assert.Equal(276, Call(s, "UserCreateByRole", "New", "New User", "pw2", "Op").I); // the default user
            s.W.Users.Login("Op");
            Assert.Equal(276, Call(s, "UserCreateByRole", "New", "New User", "pw2", "Op").I); // Op has no MANAGE_USR
            s.W.Users.Login("Admin");
            Assert.Equal(328, Call(s, "UserCreateByRole", "op", "x", "pw2", "Op").I);
            Assert.Equal(584, Call(s, "UserCreateByRole", "New", "x", "pw2", "Op, Nope").I);
            Assert.Equal(274, Call(s, "UserCreateByRole", "", "x", "pw2", "Op").I);
            Assert.Equal(274, Call(s, "UserCreateByRole", "New", "x", new string('p', 41), "Op").I);
            Assert.Equal(0, Call(s, "UserCreateByRole", "New", "New User", "pw2", " Op , Adm ").I);
            Assert.Equal(0, Call(s, "UserVerify", "New", "pw2").I);
            Assert.Equal("New User", s.W.Users.UserDefs()["new"].FullName);
            Assert.Equal(new[] { "Op", "Adm" }, s.W.Users.UserDefs()["new"].Roles.ToArray());
            Assert.Equal("2", s.W.Users.UserDefs()["new"].PwdVer);
            // type mode (default): the type must be some user's TYPE, the new user gets neither type nor roles
            Assert.Equal(327, Call(s, "UserCreate", "T1", "x", "pw3", "Nope").I);
            Assert.Equal(0, Call(s, "UserCreate", "T1", "x", "pw3", "Eng").I);
            Assert.Equal("", s.W.Users.UserDefs()["t1"].Type);
            Assert.Empty(s.W.Users.UserDefs()["t1"].Roles);
            Assert.Equal(0, Call(s, "UserDelete", "New").I);
            Assert.Equal(439, Call(s, "UserVerify", "New", "pw2").I);
            Assert.Equal(0, Call(s, "UserDelete", "NoSuchUser").I);
            Assert.Equal(before, File.ReadAllBytes(file)); // the project is untouched
        }

        [Fact]
        public void UserCreateByTheRoleParameter()
        {
            var s = MakeUsers(byRole: true);
            s.W.Users.Login("Admin");
            Assert.Equal(584, Call(s, "UserCreate", "R1", "x", "pw", "Eng").I); // 'Eng' is a type, not a role
            Assert.Equal(0, Call(s, "UserCreate", "R1", "x", "pw", "Op").I);
            Assert.Equal(new[] { "Op" }, s.W.Users.UserDefs()["r1"].Roles.ToArray());
        }

        [Fact]
        public void UserPasswordWithAndWithoutTheOldOne()
        {
            var s = MakeUsers();
            Assert.Equal(436, Call(s, "UserPassword", "Op", "new", "bad").I);
            Assert.Equal(274, Call(s, "UserPassword", "Nobody", "new", "pw").I);
            Assert.Equal(0, Call(s, "UserPassword", "Op", "new", "pw").I);
            Assert.Equal(0, Call(s, "UserVerify", "Op", "new").I);
            Assert.Equal(436, Call(s, "UserVerify", "Op", "pw").I);
            Assert.NotEqual(SeedO, s.W.Users.UserDefs()["op"].Seed); // a new seed
            Assert.Equal(276, Call(s, "UserPassword", "Op", "other").I); // no old password: needs MANAGE_USR
            s.W.Users.Login("Admin");
            Assert.Equal(289, Call(s, "UserPassword", "Nobody", "other").I);
            Assert.Equal(0, Call(s, "UserPassword", "Op", "other").I);
            Assert.Equal(0, Call(s, "UserVerify", "Op", "other").I);
        }

        [Fact]
        public void UserSetStrWaitsForUserUpdateRecord()
        {
            var s = MakeUsers();
            s.W.Users.Login("Op");
            Assert.Equal(379, Call(s, "UserSetStr", "Op", "password", "x").I);
            Assert.Equal(276, Call(s, "UserSetStr", "Admin", "COMMENT", "x").I); // another user, no MANAGE_USR
            Assert.Equal(0, Call(s, "UserSetStr", "Op", "Full_Name", "Plant Op").I); // its own record
            Assert.Equal("Operator", s.W.Users.UserDefs()["op"].FullName);
            Assert.Equal(0, Call(s, "UserUpdateRecord").I);
            Assert.Equal("Plant Op", s.W.Users.UserDefs()["op"].FullName);
            Assert.Equal(380, Call(s, "UserSetStr", "Op", "NAME", "admin").I);
            Assert.Equal(274, Call(s, "UserSetStr", "Op", "NO_FIELD", "x").I);
        }

        [Fact]
        public void SetAreaNarrowsTheViewableAreas()
        {
            var s = MakeUsers();
            s.W.Users.Login("Op"); // views 6, level 1 in 5
            Assert.True(s.W.Users.CanView(5));
            Assert.Equal(0, Call(s, "SetArea", 6).I);
            Assert.True(s.W.Users.CanView(6));
            Assert.False(s.W.Users.CanView(5));
            Assert.True(s.W.Users.CanView(0));
            var r = Call(s, "SetArea", 7);
            Assert.Equal(274, r.I);
            Assert.Equal(274, r.Error);
            r = Call(s, "SetArea", 0x4123); // no such group
            Assert.Equal(269, r.I);
            s.W.Users.Login("Op"); // a login gives every area again
            Assert.True(s.W.Users.CanView(5));
        }

        /// <summary>The rule against the users the installed example projects ship (written by
        /// Plant SCADA Studio, PWD_VER 2): Example's Engineer / Worker / Operator / Manager have
        /// the documented example password 'Citect', ExampleSA's kernel user 'kernel'.</summary>
        [Theory]
        [InlineData("Example", "Engineer", "Citect")]
        [InlineData("Example", "Manager", "Citect")]
        [InlineData("ExampleSA", "Engineer", "Citect")]
        [InlineData("ExampleSA", "kernel", "kernel")]
        public void TheShippedExampleUsersVerify(string project, string user, string password)
        {
            if (!HaveR2)
                return;
            CicodeSim.World.World.ClearCaches();
            var w = new CicodeSim.World.World(Path.Combine(R2, project), root: project);
            var run = new WorldRun(w, new CicodeSim.Vm.ProgramBuilder().Build());
            var sim = new Sim
            {
                W = w,
                Run = run,
                Host = WorldBuiltins.HostOf(run.Vm.Builtins)!,
            };
            Assert.Equal(0, Call(sim, "UserVerify", user, password).I);
            Assert.Equal(436, Call(sim, "UserVerify", user, password.ToUpperInvariant()).I);
            Assert.Equal(0, Call(sim, "UserLogin", user, password).I);
            Assert.Equal(user, sim.W.Users.Session.Name);
        }
    }
}
