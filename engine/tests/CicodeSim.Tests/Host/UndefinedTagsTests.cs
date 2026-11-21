using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CicodeSim.Compiled;
using CicodeSim.Host;
using CicodeSim.Sim;
using CicodeSim.Values;
using Xunit;
using Xunit.Abstractions;
using static CicodeSim.Tests.Host.ProtocolClient;

namespace CicodeSim.Tests.Host
{
    /// <summary>Undefined tags and the 'simulate undefined tags' option.</summary>
    [Collection("ctsim server")]
    public sealed partial class UndefinedTagsTests
    {
        private readonly ITestOutputHelper _out;

        public UndefinedTagsTests(ITestOutputHelper output)
        {
            _out = output;
        }

        // ------------------------------------------------------------------ TagUse
        private static byte[] Code(params object[] parts)
        {
            var b = new List<byte>();
            foreach (var p in parts)
            {
                switch (p)
                {
                    case byte x:
                        b.Add(x);
                        break;
                    case int x:
                        b.AddRange(BitConverter.GetBytes(x));
                        break;
                    case long x:
                        b.AddRange(BitConverter.GetBytes(x));
                        break;
                    case double x:
                        b.AddRange(BitConverter.GetBytes(x));
                        break;
                    default:
                        throw new ArgumentException("part " + p);
                }
            }
            return b.ToArray();
        }

        private static byte[] LoadOf(long off) => Code((byte)Op.Load, off);

        private static byte[] StoreOf(long off) => Code((byte)Op.Store, off);

        private static byte[] Int(int v) => Code((byte)Op.Const, (byte)0x04, v);

        private static byte[] Real(double v) => Code((byte)Op.Const, (byte)Types.Real, v);

        private static byte[] Str(long off) => Code((byte)Op.Const, (byte)Types.String, off);

        private static byte[] Cat(params byte[][] parts) =>
            parts.SelectMany(x => x).Concat(new[] { (byte)Op.End }).ToArray();

        [Fact]
        public void TagUseGuessesTheTypeFromHowTheCodeUsesATag()
        {
            var p = new CicodeSim.Compiled.Program { Text = new byte[] { (byte)'a', 0 } };
            string[] names = { "Bit", "Step", "Text", "Value", "Scaled", "Bare", "Unused", "Q", "Fmt" };
            for (int i = 0; i < names.Length; i++)
                p.TagRefs[i * 100] = new TagRef(i * 100)
                {
                    Tag = names[i],
                    Element = names[i] == "Q" ? "Q" : "",
                };
            p.Scales[7] = new Scale(7)
            {
                EngZero = 0,
                EngFull = 2000,
                RawZero = 0,
                RawFull = 2000,
            };
            var blocks = new List<byte[]>
            {
                Cat(LoadOf(0), Int(1), new[] { (byte)Op.Eq }), // Bit = 1
                Cat(LoadOf(0), Int(0), new[] { (byte)Op.Eq }), // Bit = 0
                Cat(LoadOf(100), Int(250), new[] { (byte)Op.Ge }), // Step >= 250
                Cat(LoadOf(100), Int(0), new[] { (byte)Op.Eq }), // Step = 0
                Cat(Str(0), StoreOf(200)), // Text = "a"
                Cat(LoadOf(300), Real(2.5), new[] { (byte)Op.Gt }), // Value > 2.5
                Cat(LoadOf(400), Code((byte)Op.Bar, 7)), // Scaled ^(0, 2000)
                Cat(LoadOf(500)), // Bare (the whole expression)
                Cat(LoadOf(700), Int(5), new[] { (byte)Op.Eq }), // Q.Q = 5: an extension, no evidence
                // Fmt = "": CtCmp32 formats the text before the store (a STRING tag, whatever
                // the format's width and decimals say)
                Cat(Str(0), Code((byte)Op.StringFmt, 0x00200206, 0L), StoreOf(800)),
            };
            var g = TagUse.Guess(p, blocks);
            Assert.Equal("DIGITAL", g["bit"]);
            Assert.Equal("INT", g["step"]);
            Assert.Equal("STRING", g["text"]);
            Assert.Equal("REAL", g["value"]);
            Assert.Equal("REAL", g["scaled"]);
            Assert.Equal("DIGITAL", g["bare"]);
            Assert.False(g.ContainsKey("unused"));
            Assert.False(g.ContainsKey("q"));
            Assert.Equal("STRING", g["fmt"]);
        }

        /// <summary>A page compiled from its rows has no .RDB: its code blocks come from its CODE
        /// pool, and they give the same guesses as the CtCmp32 .RDB of the page (Example Mixer).</summary>
        [ProjectFact]
        public void ARowsCompiledPagesBlocksGuessAsItsRdb()
        {
            var rdb = CicodeSim.Page.CompiledPage.Load(Path.Combine(SimCorpus.Example, "Mixer.RDB"));
            var fromRdb = TagUse.PageBlocks(rdb);
            Assert.True(fromRdb.Count > 100);
            // the same pool walked block by block holds every block the .RDB's records name
            var pool = new CicodeSim.Page.CompiledPage(rdb.Name) { Program = rdb.Program, Code = rdb.Code };
            var linear = new HashSet<string>(TagUse.PageBlocks(pool).Select(Convert.ToBase64String));
            Assert.All(fromRdb, b => Assert.Contains(Convert.ToBase64String(b), linear));
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                var cp = sim.Pages.CompileRows("Mixer", null, new List<string>());
                Assert.NotNull(cp);
                var a = TagUse.Guess(rdb.Program, fromRdb);
                var b2 = TagUse.Guess(cp!.Program, TagUse.PageBlocks(cp));
                Assert.True(a.Count > 20);
                var differ = a.Where(kv => b2.TryGetValue(kv.Key, out var t) && t != kv.Value)
                    .Select(kv => kv.Key)
                    .ToList();
                Assert.Empty(differ);
                Assert.True(
                    a.Keys.Count(k => b2.ContainsKey(k)) >= a.Count * 9 / 10,
                    a.Count + " vs " + b2.Count
                );
            }
        }

        // ------------------------------------------------------------------ simulated tags
        [ProjectFact]
        public void SimulatedTagsExistOnlyWhileTheOptionIsOn()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                const string name = "NoSuchTag_Undef1";
                Assert.Equal(424, sim.Invoke(() => sim.SetTag(name, JsonValue.Of(3))));
                Assert.False(sim.Invoke(() => sim.ReadTag(name).Defined));
                sim.Invoke(() => sim.SimulateUndefined = true);
                // the code does not use it: REAL; a name that is no plain tag name is refused
                Assert.Equal(0, sim.Invoke(() => sim.SetTag(name, JsonValue.Of(3.5))));
                var u = sim.Invoke(() => sim.ReadTag(name));
                Assert.True(u.Defined);
                Assert.Equal(3.5, u.Value.Double);
                Assert.Equal("REAL", sim.Invoke(() => sim.World.Tags.SimulatedTag(name)!.TypeName));
                Assert.Equal(424, sim.Invoke(() => sim.SetTag("No.Such.Thing", JsonValue.Of(1))));
                Assert.Equal(424, sim.Invoke(() => sim.SetTag("?Assoc?", JsonValue.Of(1))));
                // an explicit type, and Cicode reads the simulated tag
                Assert.Equal(
                    0,
                    sim.Invoke(() => sim.SetTag("NoSuchTag_Undef2", JsonValue.Of("hello"), type: "STRING"))
                );
                CallOutcome? o = null;
                var done = new System.Threading.ManualResetEventSlim(false);
                sim.Invoke(() => sim.StartCall("TagRead(\"NoSuchTag_Undef2\")", r =>
                    {
                        o = r;
                        done.Set();
                    }));
                Assert.True(done.Wait(10000));
                Assert.Equal("hello", o!.Result);
                // a defined tag is never shadowed
                Assert.Null(sim.Invoke(() => sim.World.Tags.AddSimulated("Mixer_Sugar", "STRING")));
                // off again: undefined (424 / bad quality), the value kept for the next time
                sim.Invoke(() => sim.SimulateUndefined = false);
                Assert.False(sim.Invoke(() => sim.ReadTag(name).Defined));
                Assert.Equal(424, sim.Invoke(() => sim.SetTag(name, JsonValue.Of(1))));
                sim.Invoke(() => sim.SimulateUndefined = true);
                Assert.Equal(3.5, sim.Invoke(() => sim.ReadTag(name)).Value.Double);
            }
        }

        // ------------------------------------------------------------------ window associations
        [ProjectFact]
        public void APopupsTagsAreResolvedThroughItsWindowsAssociations()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            using (var server = new SimServer(sim, "assoc-" + Guid.NewGuid().ToString("N").Substring(0, 8)))
            {
                server.Listen(
                    Path.Combine(Path.GetTempPath(), "ctsim-assoc-" + Guid.NewGuid().ToString("N"))
                );
                using (var c = Connect(server.Listener!.Address))
                {
                    c.Ok("ctsim/hello", Args(("client", "assoc")));
                    var model = JsonValue.Parse(
                        File.ReadAllText(
                            Path.Combine(
                                AppContext.BaseDirectory,
                                "Page",
                                "Fixtures",
                                "Example_Mixer_meters.page.json"
                            )
                        )
                    );
                    Assert.Equal(
                        0,
                        c.Ok(
                            "ctsim/openPage",
                            Args(("name", "Mixer"), ("window", 0), ("model", model)),
                            60000
                        )["error"].Long
                    );
                    c.Ok("ctsim/clock", Args(("mode", "step"), ("value", 0.5)));
                    Assert.True(
                        c.Ok(
                            "ctsim/input",
                            Args(("window", 0), ("an", 939), ("kind", "click"), ("x", 313), ("y", 480))
                        )["ran"].Bool
                    );
                    c.Ok("ctsim/clock", Args(("mode", "step"), ("value", 4.0)), 60000);
                    var wins = c.Ok("ctsim/windows")["windows"].Items;
                    var pop = wins.FirstOrDefault(x =>
                        string.Equals(x["page"].Str, "!PM800Detail", StringComparison.OrdinalIgnoreCase)
                    );
                    Assert.NotNull(pop);
                    long n = pop!["window"].Long;
                    // the window's associations travel with the window list
                    Assert.Equal(@"Raw_SkimMilkOutPump_PM800\MMXU1\TotW", pop["associations"]["ToTW"].Str);
                    Assert.StartsWith("'", pop["associations"]["PM800_Equip"].Str);
                    var t = c.Ok("ctsim/tags", Args(("window", n)), 60000);
                    Assert.Equal("!PM800Detail", t["page"].Str, ignoreCase: true);
                    Assert.Equal(n, t["window"].Long);
                    var tot = t["tags"]
                        .Items.FirstOrDefault(x => x["name"].Str == @"Raw_SkimMilkOutPump_PM800\MMXU1\TotW");
                    Assert.NotNull(tot);
                    Assert.Equal("ToTW", tot!["via"].Str);
                    Assert.Contains("?", tot["ref"].Str);
                    Assert.True(t["tags"].Items.Count(x => x.Get("via") != null) > 5);
                    foreach (var x in t["tags"].Items)
                        _out.WriteLine(x["name"].Str + (x.Get("via") != null ? " via " + x["via"].Str : ""));
                    foreach (var x in t["literals"].Items)
                        _out.WriteLine("literal " + x["ref"].Str + " = " + x["value"]);
                    Assert.Empty(t["unresolved"].Items);
                    // a page name that is not the window's
                    Assert.False(
                        c.Request("ctsim/tags", Args(("window", n), ("page", "Mixer")))["success"].Bool
                    );
                    // without the window the references cannot be resolved
                    var bare = c.Ok("ctsim/tags", Args(("page", "!PM800Detail")), 60000);
                    Assert.DoesNotContain(bare["tags"].Items, x =>
                        x["name"].Str == @"Raw_SkimMilkOutPump_PM800\MMXU1\TotW"
                    );
                    Assert.Contains(bare["unresolved"].Items, x =>
                        string.Equals(x["via"].Str, "ToTW", StringComparison.OrdinalIgnoreCase)
                    );
                    // subscribe the window: the resolved tags
                    var sub = c.Ok("ctsim/subscribe", Args(("window", n), ("replace", true)), 60000);
                    Assert.Contains(sub["tags"].Items, x =>
                        x["name"].Str == @"Raw_SkimMilkOutPump_PM800\MMXU1\TotW"
                    );
                    Assert.Equal(n, sub["page"]["window"].Long);
                    Assert.True(sub["page"]["viaAssociations"].Long > 5);
                    Assert.False(c.Request("ctsim/subscribe", Args(("window", 7)))["success"].Bool);
                    // the window's associations change (AssWinReplace takes the pending ones): the
                    // window list says so, and the tags follow on the next request
                    string ns = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    c.Ok("ctsim/call", Args(("expr", "Ass(" + ns + ", \"ToTW\", \"Mixer_Sugar\", 0)")));
                    c.Ok("ctsim/call", Args(("expr", "AssWinReplace(" + ns + ")")));
                    c.WaitEvent("ctsim/windowsChanged", e =>
                        e["windows"]
                            .Items.Any(x =>
                                x["window"].Long == n && x["associations"].Get("ToTW")?.Str == "Mixer_Sugar"
                            )
                    );
                    var after = c.Ok("ctsim/tags", Args(("window", n)), 60000);
                    Assert.Contains(after["tags"].Items, x =>
                        x["name"].Str == "Mixer_Sugar" && x["via"].Str == "ToTW"
                    );
                    Assert.DoesNotContain(after["tags"].Items, x =>
                        x["name"].Str == @"Raw_SkimMilkOutPump_PM800\MMXU1\TotW"
                    );
                }
            }
        }
    }
}
