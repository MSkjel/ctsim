using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Page;
using CicodeSim.Tests.Vm;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World.Display;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>The super genie association functions.</summary>
    [Collection("World")]
    public class AssociationTests : DataTestBase
    {
        private sealed class Host : IDisplayHost
        {
            public readonly Dictionary<string, (PageRecord Rec, PageDrawing? Drawing)> Pages = new Dictionary<
                string,
                (PageRecord, PageDrawing?)
            >(StringComparer.OrdinalIgnoreCase);

            public PageRecord? FindPage(string name, string cluster) =>
                Pages.TryGetValue(name, out var p) ? p.Rec : null;

            public PageDrawing? LoadDrawing(PageRecord page) =>
                Pages.TryGetValue(page.Name, out var p) ? p.Drawing : null;

            public void Shown(int window, PageInstance page) { }

            public void DisplayOutput(int window, int an, string kind, string text) { }

            public void WindowChanged(Window w) { }

            public void Add(string name, PageDrawing? d = null) =>
                Pages[name] = (new PageRecord { Name = name, Title = name }, d);
        }

        /// <summary>The synthetic World with a cluster table (C1, or C1 and C2) and two pages:
        /// '!Pop' (declaring the associations Def = 'Tank1_Temp', OnErr with only a value on
        /// error) and 'Plain'.</summary>
        private Sim Make(bool twoClusters = false)
        {
            var s = MakeData(() =>
            {
                var rows = twoClusters ? new[] { new[] { "C1" }, new[] { "C2" } } : new[] { new[] { "C1" } };
                Dbf(Path.Combine(MainDir, "cluster.dbf"), new[] { "NAME" }, rows);
            });
            var host = new Host();
            var pop = new PageDrawing
            {
                Page = "!Pop",
                Width = 400,
                Height = 300,
            };
            pop.Associations.Add(new PageAssociation { Name = "Def", Default = "Tank1_Temp" });
            pop.Associations.Add(new PageAssociation { Name = "OnErr", OnError = "0" });
            host.Add("!Pop", pop);
            host.Add(
                "Plain",
                new PageDrawing
                {
                    Page = "Plain",
                    Width = 400,
                    Height = 300,
                }
            );
            s.W.Display.Host = host;
            return s;
        }

        private static Res At(Sim s, int window, string name, params object[] args)
        {
            var b = Func0Table.Default.Lookup(name);
            Assert.True(b != null, name);
            var t = new CicodeTask(s.Run.Vm, "test", false);
            TaskContext.Set(t, window, -1);
            var vals = new List<Value>();
            foreach (object a in args)
                vals.Add(a is Value v ? v : CicodeVm.ValueOf(a));
            var r = s.Run.Vm.Builtins!.Call(b!.Index, t, ArgList.Of(vals.ToArray()));
            Assert.False(r.IsNotSimulated, name + " is not simulated");
            bool blocked = false;
            if (r.IsBlock)
            {
                blocked = true;
                r = t.Wait!.Result!() ?? Value.Nothing;
            }
            return new Res
            {
                V = r,
                Task = t,
                Blocked = blocked,
            };
        }

        private static Res C(Sim s, string name, params object[] args) => At(s, 0, name, args);

        private static AssEntry Pending(Sim s, string arg) =>
            s.W.Display.Pending.Single(p => string.Equals(p.Arg, arg, StringComparison.OrdinalIgnoreCase));

        // ------------------------------------------------------------------ the rules
        [Fact]
        public void TheNameIsTheTextBetweenTheFirstAndTheLastQuestionMark()
        {
            Assert.Equal("Equip", AssRules.NameOf("?Equip?.Item"));
            Assert.Equal("Equip", AssRules.NameOf("Equip"));
            Assert.Equal("A?_?B", AssRules.NameOf("?A?_?B?")); // one association per reference
            Assert.Equal("def", AssRules.NameOf("abc?def")); // one '?': the text after it
            Assert.Equal("INT 1", AssRules.NameOf("?INT 1?")); // no type word stripped
            Assert.Equal(" X ", AssRules.NameOf("? X ?")); // no trim
            Assert.False(AssRules.IsPartial("Equip"));
            Assert.False(AssRules.IsPartial("?Equip?"));
            Assert.True(AssRules.IsPartial("?Equip?.PV"));
            Assert.True(AssRules.IsPartial("a?1?"));
            Assert.False(AssRules.IsPartial("a?b"));
            // prefix + tag + suffix; the first '[' .. ']' is the index of a tag
            var e = new AssEntry { Tag = "Plant.Tank1" };
            Assert.Equal("Plant.Tank1.Level", AssRules.Compose("?E?.Level", e, out int ix));
            Assert.Equal(-1, ix);
            Assert.Equal("x_Plant.Tank1_y", AssRules.Compose("x_?E?_y", e, out _));
            Assert.Equal("Plant.Tank1", AssRules.Compose("E", e, out _));
            var arr = new AssEntry { Tag = "Arr_Real[3]" };
            Assert.Equal("Arr_Real", AssRules.Compose("?A?", arr, out ix));
            Assert.Equal(3, ix);
            var lit = new AssEntry { Tag = "a[1]", Literal = true };
            Assert.Equal("a[1]", AssRules.Compose("?L?", lit, out ix));
            Assert.Equal(-1, ix);
            Assert.Equal("Units", AssRules.PropertyOf("1", out _));
            Assert.Equal("Units", AssRules.PropertyOf("units", out _));
            Assert.Equal("Description", AssRules.PropertyOf("16", out _));
            Assert.Equal("Type", AssRules.PropertyOf("23", out _));
            Assert.Equal("Item", AssRules.PropertyOf("25", out _));
            Assert.Equal("Custom8", AssRules.PropertyOf("33", out _));
            Assert.Null(AssRules.PropertyOf("19", out _)); // AssFullName: not a property
            Assert.Null(AssRules.PropertyOf("34", out _));
            Assert.Null(AssRules.PropertyOf("", out _));
        }

        // ------------------------------------------------------------------ Ass: the store
        [Fact]
        public void AssStoresEveryAssociationAndReturnsTheTagChecksError()
        {
            var s = Make();
            Assert.Equal(0, C(s, "Ass", -2, "Tag", "Tank1_Level", 0).I); // a variable tag
            Assert.Equal(0, C(s, "Ass", -2, "Item", "Plant.Tank1.Level", 0).I); // an Equip.Item
            Assert.Equal(289, C(s, "Ass", -2, "Equip", "Plant.Tank1", 0).I); // an equipment: no tag
            Assert.Equal(289, C(s, "Ass", -2, "Dotted", "No.Such", 0).I);
            Assert.Equal(424, C(s, "Ass", -2, "Plain", "NoSuchTag", 0).I); // no '.': tag not found
            // all of them stored, with the project's only cluster
            Assert.Equal(5, s.W.Display.Pending.Count);
            Assert.Equal(289, Pending(s, "Equip").Error);
            Assert.Equal("C1", Pending(s, "Equip").Cluster);
            Assert.Equal("Plant.Tank1", Pending(s, "Equip").Tag);
            // a 'Cluster.' prefix is taken off the tag
            Assert.Equal(0, C(s, "Ass", -2, "Pre", "C1.Tank1_Level", 0).I);
            Assert.Equal("Tank1_Level", Pending(s, "Pre").Tag);
            Assert.Equal("C1", Pending(s, "Pre").Cluster);
            // the cluster argument: unknown 403, a prefix of another cluster 404
            Assert.Equal(403, C(s, "Ass", -2, "Bad", "Tank1_Level", 0, "Nowhere").I);
            Assert.Equal(403, C(s, "Ass", -2, "Dot", ".Tank1_Level", 0).I);
            // nArg trimmed; the arg name of the next Ass replaces the entry
            Assert.Equal(0, C(s, "Ass", -2, "  Tag ", "Tank1_Temp", 0).I);
            Assert.Equal("Tank1_Temp", Pending(s, "Tag").Tag);
            // an empty tag stores nothing and returns 0; 255 characters: 557 raised
            Assert.Equal(0, C(s, "Ass", -2, "Empty", "", 0).I);
            Assert.DoesNotContain(s.W.Display.Pending, p => p.Arg == "Empty");
            var lng = C(s, "Ass", -2, "Long", new string('x', 255), 0);
            Assert.Equal(557, lng.I);
            Assert.Equal(557, lng.Error);
        }

        [Fact]
        public void ALiteralIsAnyQuotedTagWhateverTheMode()
        {
            var s = Make();
            // the runtime tests its last argument (0 from Ass), not nMode
            Assert.Equal(0, C(s, "Ass", -2, "L", "'Hello'", 1).I);
            Assert.True(Pending(s, "L").Literal);
            Assert.Equal("Hello", Pending(s, "L").Tag);
            Assert.Equal("", Pending(s, "L").Cluster);
            Assert.Equal(0, C(s, "Ass", -2, "Open", "'no end", 0).I);
            Assert.Equal("no end", Pending(s, "Open").Tag);
        }

        [Fact]
        public void AnAssForAWindowTakesOverAPendingMinusTwoEntry()
        {
            var s = Make();
            Assert.Equal(0, C(s, "Ass", -2, "Tag", "Tank1_Level", 0).I);
            // the runtime finds the -2 entry of the name: it is replaced (now for window 0)
            Assert.Equal(0, C(s, "Ass", 0, "Tag", "Tank1_Temp", 0).I);
            var e = Pending(s, "Tag");
            Assert.Equal(0, e.Target);
            Assert.Equal("Tank1_Temp", e.Tag);
            // -3 = the caller's window
            Assert.Equal(0, At(s, 3, "Ass", -3, "Three", "Tank1_Level", 0).I);
            Assert.Equal(3, Pending(s, "Three").Target);
        }

        [Fact]
        public void SeveralClustersWithoutOneGive417AndTheTagsOwnCluster()
        {
            var s = Make(twoClusters: true);
            // no cluster given, the tag found anywhere: 417 'Cluster required for operation'
            Assert.Equal(417, C(s, "Ass", -2, "Tag", "Tank1_Level", 0).I);
            Assert.Equal("", Pending(s, "Tag").Cluster);
            // the tag's own CLUSTER (Lib's Lib_Flag is in C1): the store keeps it, no error
            Assert.Equal(0, C(s, "Ass", -2, "Flag", "Lib_Flag", 0).I);
            Assert.Equal("C1", Pending(s, "Flag").Cluster);
            // a prefix that is not the cluster given: 404
            Assert.Equal(404, C(s, "Ass", -2, "Mis", "C1.Lib_Flag", 0, "C2").I);
            // without a cluster, a name that is no tag: 274 (with no cluster)
            Assert.Equal(274, C(s, "Ass", -2, "None", "Nothing.Here", 0).I);
            // a local variable: no cluster needed, no check, so no 417
            Assert.Equal(0, C(s, "Ass", -2, "Loc", "LocInt", 0).I);
            Assert.Equal("", Pending(s, "Loc").Cluster);
        }

        [Fact]
        public void ALocalVariableHasNoCluster()
        {
            // the effective cluster tests before the tag configuration, and the store's tag
            // check does not find a local variable; the live runtime agrees (CorpusAssociationTests:
            // SA_DisableLabel)
            var s = Make();
            Assert.Equal(0, C(s, "Ass", -2, "LV", "LocInt", 0).I);
            Assert.Equal("", Pending(s, "LV").Cluster);
            Assert.Equal(424, C(s, "Ass", -2, "LC", "C1.LocInt", 0).I);
            Assert.Equal("C1", Pending(s, "LC").Cluster);
            Assert.Equal(0, C(s, "_PageDisplay", "Plain").I);
            Assert.Equal("LocInt", C(s, "AssInfo", "?LV?", "0").S);
            Assert.Equal("", C(s, "AssInfo", "?LV?", "17").S);
            Assert.Equal("LocInt", C(s, "AssInfo", "?LV?", "18").S);
            Assert.Equal("LocInt", C(s, "AssInfo", "?LV?", "19").S);
            Assert.Equal("", C(s, "AssGetProperty", "?LV?", "ClusterName").S);
            Assert.Equal("", C(s, "AssInfo", "?LC?", "0").S);
            Assert.Equal("C1.LocInt", C(s, "AssInfo", "?LC?", "19").S);
        }

        // ------------------------------------------------------------------ AssInfo
        private Sim Shown(bool twoClusters = false)
        {
            var s = Make(twoClusters);
            Assert.Equal(289, C(s, "Ass", -2, "E", "Plant.Tank1", 0).I);
            Assert.Equal(0, C(s, "Ass", -2, "T", "Tank1_Level", 0).I);
            Assert.Equal(0, C(s, "Ass", -2, "L", "'42'", 0).I);
            Assert.Equal(0, C(s, "Ass", -2, "A", "Arr_Real[2]", 0).I);
            Assert.Equal(0, C(s, "_PageDisplay", "Plain").I);
            return s;
        }

        [Fact]
        public void AssInfoAnswersAnEquipmentAssociationAsAssFullNameOnly()
        {
            var s = Shown();
            // help 1192665: 19 = full name 'even if the tag is not resolved'; 0 / 17 / 18 '' then
            Assert.Equal("C1.Plant.Tank1", C(s, "AssInfo", "?E?", "19").S);
            Assert.Equal("C1.Plant.Tank1", C(s, "AssInfo", "E", "AssFullName").S);
            Assert.Equal("", C(s, "AssInfo", "?E?", "0").S);
            Assert.Equal("", C(s, "AssInfo", "?E?", "17").S);
            Assert.Equal("", C(s, "AssInfo", "?E?", "18").S);
            Assert.Equal(0, C(s, "AssInfo", "?E?", "0").Error);
            // a partial reference through it: the item's variable tag
            Assert.Equal("Tank1_Level", C(s, "AssInfo", "?E?.Level", "0").S);
            Assert.Equal("Tank1_Level", C(s, "AssInfo", "?E?.Level", "TagName").S);
            Assert.Equal("C1", C(s, "AssInfo", "?E?.Level", "17").S);
            Assert.Equal("C1.Tank1_Level", C(s, "AssInfo", "?E?.Level", "18").S);
            Assert.Equal("C1.Tank1_Level", C(s, "AssInfo", "?E?.Level", "19").S);
            // an unresolved partial reference still has a text (no check for it)
            Assert.Equal("Plant.Tank1.Nope", C(s, "AssInfo", "?E?.Nope", "0").S);
            Assert.Equal("C1.Plant.Tank1.Nope", C(s, "AssInfo", "?E?.Nope", "19").S);
            // Include tag.ci AssResolve = AssInfo(arg, 19) under ErrSet(1)
            Assert.Equal("C1.Plant.Tank1", C(s, "AssInfo", "?E?", 19).S);
        }

        [Fact]
        public void AssInfoGivesEveryTypeOfATagAssociation()
        {
            var s = Shown();
            Assert.Equal("Tank1_Level", C(s, "AssInfo", "T", "0").S);
            Assert.Equal("%", C(s, "AssInfo", "T", "1").S);
            Assert.Equal("%", C(s, "AssInfo", "T", "Units").S); // a property name
            Assert.Equal("0", C(s, "AssInfo", "T", "4").S);
            Assert.Equal("100", C(s, "AssInfo", "T", "5").S);
            Assert.Equal("Plant.Tank1", C(s, "AssInfo", "T", "22").S);
            Assert.Equal("Level", C(s, "AssInfo", "T", "25").S);
            Assert.Equal("levelsensor", C(s, "AssInfo", "T", "26").S);
            Assert.Equal("C1", C(s, "AssInfo", "T", "17").S);
            Assert.Equal("C1.Tank1_Level", C(s, "AssInfo", "T", "18").S);
            // an unknown type: 348; a type the tables cannot answer: 348
            var bad = C(s, "AssInfo", "T", "34");
            Assert.Equal("", bad.S);
            Assert.Equal(348, bad.Error);
            Assert.Equal(348, C(s, "AssInfo", "T", "9").Error);
            // the array index comes back on the name
            Assert.Equal("Arr_Real[2]", C(s, "AssInfo", "A", "0").S);
        }

        [Fact]
        public void AssInfoOfALiteralAndOfNothing()
        {
            var s = Shown();
            Assert.Equal("42", C(s, "AssInfo", "L", "0").S);
            Assert.Equal("x42y", C(s, "AssInfo", "x?L?y", "0").S);
            var u = C(s, "AssInfo", "L", "1"); // other types: '' and no error
            Assert.Equal("", u.S);
            Assert.Equal(0, u.Error);
            // not associated: 274 (the runtime returns 0x112), the name keeps a type word
            var na = C(s, "AssInfo", "?Missing?", "0");
            Assert.Equal("", na.S);
            Assert.Equal(274, na.Error);
            Assert.Equal(274, C(s, "AssInfo", "?INT T?", "0").Error);
            // AssInfoEx is the same core
            Assert.Equal("C1.Plant.Tank1", C(s, "AssInfoEx", "?E?", "19").S);
            Assert.Equal(274, C(s, "AssInfoEx", "Missing", "0").Error);
            // no window: '' and no error
            var closed = At(s, 7, "AssInfo", "T", "0");
            Assert.Equal("", closed.S);
            Assert.Equal(0, closed.Error);
        }

        // ------------------------------------------------------------------ AssGetProperty, AssScaleStr
        [Fact]
        public void AssGetPropertyOfAPartialEquipmentReference()
        {
            var s = Shown();
            Assert.Equal("Tank1_Level", C(s, "AssGetProperty", "?E?.Level", "TagName").S);
            Assert.Equal("C1", C(s, "AssGetProperty", "?E?.Level", "ClusterName").S);
            Assert.Equal("C1.Tank1_Level", C(s, "AssGetProperty", "?E?.Level", "FullName").S);
            Assert.Equal("C1.Plant.Tank1", C(s, "AssGetProperty", "?E?", "AssFullName").S);
            Assert.Equal("", C(s, "AssGetProperty", "?E?", "TagName").S);
            Assert.Equal("0", C(s, "AssGetProperty", "?E?.Level", "Literal").S);
            Assert.Equal("%", C(s, "AssGetProperty", "?E?.Level", "Units").S);
            Assert.Equal("Plant.Tank1", C(s, "AssGetProperty", "?E?.Level", "Equipment").S);
            Assert.Equal("1", C(s, "AssGetProperty", "L", "Literal").S);
            Assert.Equal("42", C(s, "AssGetProperty", "L", "TagName").S);
            Assert.Equal(274, C(s, "AssGetProperty", "?Missing?", "TagName").Error);
        }

        [Fact]
        public void AssScaleStrScalesTheReference()
        {
            var s = Shown();
            // Tank1_Level: 0..100 %, '###.#EU'
            string half = C(s, "AssScaleStr", "?E?.Level", 50, 0).S;
            Assert.Equal(C(s, "TagScaleStr", "Tank1_Level", 50, 0).S, half);
            Assert.Equal(C(s, "TagScaleStr", "Tank1_Level", 100, 1).S, C(s, "AssGetScale", "T", 100, 1).S);
            Assert.Equal(274, C(s, "AssScaleStr", "L", 50, 0).Error); // a literal has no scale
            Assert.Equal(274, C(s, "AssScaleStr", "Missing", 50, 0).Error);
        }

        [Fact]
        public void EquipmentParametersUseTheNextPagesClusterAndPreserveLiterals()
        {
            var s = Make(twoClusters: true);
            Dbf(
                Path.Combine(MainDir, "eqparam.dbf"),
                new[] { "CLUSTER", "EQUIP", "NAME", "VALUE", "ISTAG" },
                new[] { "C1", "Pump", "Speed", "Unused", "" },
                new[] { "C2", "Pump", "Speed", "Tank1_Temp", "" },
                new[] { "C2", "Pump", "Caption", "Pump's outlet", "FALSE" },
                new[] { "C2", "Pump", "Units", "rpm", "FALSE" }
            );
            var display = s.W.Display;
            var window = display.Get(0)!;
            window.Cluster = "C1";
            C(s, "AssEquipParameters", -3, "", "pump");
            Assert.Empty(window.Associations);
            C(s, "Ass", -3, "Units", "'Hz'", 0);
            window.Cluster = "C2";
            display.ClaimForPage(window, null);
            Assert.Equal("Tank1_Temp", window.Associations["Speed"].Tag);
            Assert.Equal("C2", window.Associations["Speed"].Cluster);
            Assert.True(window.Associations["Caption"].Literal);
            Assert.Equal("Pump's outlet", window.Associations["Caption"].Tag);
            Assert.Equal("Hz", window.Associations["Units"].Tag);
            display.ClaimForPage(window, null);
            Assert.Empty(window.Associations);
        }

        // ------------------------------------------------------------------ AssChain, AssMetadata
        [Fact]
        public void AssChainUsesTheTargetAsGiven()
        {
            var s = Shown();
            // the runtime does not make -3 the current window (Ass does): the entries wait for a
            // window -3 that never shows a page
            Assert.Equal(4, C(s, "AssChain", -3, 0, 0).I);
            Assert.Equal(4, s.W.Display.Pending.Count(p => p.Target == -3));
            Assert.Equal(0, C(s, "_PageDisplay", "Plain").I);
            Assert.Empty(s.W.Display.Windows[0].Associations);
            Assert.Equal(4, s.W.Display.Pending.Count(p => p.Target == -3));
            // the corpus form: AssChain(WinNumber(), WinNumber(), 0) then a page change
            var t = Shown();
            Assert.Equal(4, C(t, "AssChain", 0, 0, 0).I);
            Assert.Equal(0, C(t, "_PageDisplay", "Plain").I);
            Assert.Equal("C1.Plant.Tank1", C(t, "AssInfo", "?E?", "19").S);
        }

        // ------------------------------------------------------------------ the page's defaults
        [Fact]
        public void APageTakesItsDeclaredDefaultsAndValuesOnError()
        {
            var s = Make();
            Assert.Equal(0, C(s, "_PageDisplay", "!Pop").I);
            var w = s.W.Display.Windows[0];
            Assert.True(w.Associations.TryGetValue("Def", out var d));
            Assert.Equal("Tank1_Temp", d!.Tag);
            Assert.True(d.UsedDefault);
            // a declared association with only a value on error is stored (the runtime keeps an
            // entry whose tag is empty when it has one)
            Assert.True(w.Associations.TryGetValue("OnErr", out var oe));
            Assert.Equal("", oe!.Tag);
            Assert.Equal("0", oe.OnError);
            // a caller's association wins over the default
            Assert.Equal(0, C(s, "Ass", -2, "Def", "Tank1_Level", 0).I);
            Assert.Equal(0, C(s, "_PageDisplay", "!Pop").I);
            Assert.Equal("Tank1_Level", w.Associations["Def"].Tag);
            Assert.False(w.Associations["Def"].UsedDefault);
        }

        [Fact]
        public void TheStoreCheckIsNoUseOfTheNameByCode()
        {
            // ctsim/usedTags: a page association's default ('__Type' = '0' on SA alarm pages) is
            // checked by the store when the page shows; that is no tag the page's code uses
            var s = Make();
            var used = new List<string>();
            s.W.Tags.CodeUsed = used.Add;
            Assert.Equal(424, C(s, "Ass", -2, "Type", "0", 0).I);
            Assert.Equal(0, C(s, "Ass", -2, "Tag", "Tank1_Level", 0).I);
            Assert.Equal(0, C(s, "_PageDisplay", "!Pop").I); // its default Tank1_Temp
            Assert.Empty(used);
            // a lookup by code still counts
            Assert.NotNull(s.W.Tags.Find("Tank1_Level"));
            Assert.Equal(new[] { "Tank1_Level" }, used);
        }

        [Fact]
        public void AssWinReplaceTakesThePendingAndTheDefaults()
        {
            var s = Make();
            Assert.Equal(0, C(s, "_PageDisplay", "!Pop").I);
            Assert.Equal(274, C(s, "AssWinReplace", 0).I); // nothing pending
            Assert.Equal(0, C(s, "Ass", 0, "New", "Tank1_Level", 0).I);
            Assert.Equal(0, C(s, "AssWinReplace", 0).I);
            var w = s.W.Display.Windows[0];
            Assert.Equal("Tank1_Level", w.Associations["New"].Tag);
            Assert.Equal("Tank1_Temp", w.Associations["Def"].Tag);
        }

        // ------------------------------------------------------------------ AssFmt and TAGREFs
        [Fact]
        public void AssFmtFormatsThePartialReference()
        {
            var s = Shown();
            var tags = new PageTags(s.W.Tags, s.W.Display);
            var t = new CicodeTask(s.Run.Vm, "test", false);
            TaskContext.Set(t, 0, -1);
            // the PV of an equipment association: the item's tag ('###.#EU': width 5, 1 decimal, EU)
            Assert.True(tags.Format("?E?.Level", t, out long word, out string eu));
            Assert.Equal(TagTypesWord("###.#EU"), word);
            Assert.Equal("%", eu);
            // a tag without a FORMAT keeps the subscription's 0x206 (live: FP_* '0.00')
            Assert.True(tags.Format("?E?.Temp", t, out word, out _));
            Assert.Equal(0x206, word);
            // a literal: its own width; not associated: false
            Assert.True(tags.Format("?L?", t, out word, out _));
            Assert.Equal(2, word);
            Assert.False(tags.Format("?Nope?.Level", t, out _, out _));
            // the reference's value
            s.W.Tags.Set("Tank1_Level", 25);
            Assert.True(tags.Resolve("?E?.Level", t, out Value v));
            Assert.Equal(25.0, A.R(v), 6);
        }

        private static long TagTypesWord(string f) => CicodeSim.World.TagTypes.FormatWord(f);

        [Fact]
        public void ATagRefNamesOneAssociationBetweenItsFirstAndLastQuestionMark()
        {
            var s = Shown();
            var tags = new PageTags(s.W.Tags, s.W.Display);
            s.W.Tags.Set("Tank1_Level", 33);
            var r = tags.Resolve(new TagRef(0) { Tag = "?E?.Level" }, 0);
            Assert.Equal(0, r.Error);
            Assert.Equal("Plant.Tank1.Level", r.Ref!.Tag);
            // two tokens: the name is 'E?.?T' (not associated, 323 #ASC)
            Assert.Equal(PageTags.NotAssociated, tags.Resolve(new TagRef(0) { Tag = "?E?.?T?" }, 0).Error);
            // a literal
            var l = tags.Resolve(new TagRef(0) { Tag = "?L?" }, 0);
            Assert.Equal(42.0, A.R(l.Literal!.Value), 6);
        }
    }
}
