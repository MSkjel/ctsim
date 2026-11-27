using System.Collections.Generic;
using System.Linq;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Compiler.Syntax;
using Xunit;

namespace CicodeSim.Tests.Compiler
{
    /// <summary>NameArgs: the string literals of a field that reach a built-in's page or tag
    /// argument, directly or through the functions that pass their parameter on.</summary>
    public class NameArgsTests
    {
        private static Func0Row Row(string name, string op, string type, string argc, string optargs = "")
        {
            var d = new Dictionary<string, string>
            {
                ["NAME"] = name,
                ["OP_CODE"] = op,
                ["TYPE"] = type,
                ["ARGC"] = argc,
                ["OPTARGS"] = optargs,
            };
            return Func0Rows.ParseRow(k => d.TryGetValue(k, out string? v) ? v : "")!;
        }

        private static readonly Func0Rows Func0 = new Func0Rows(
            new List<Func0Row>
            {
                Row("_PageDisplay", "39", "INT", "(STRING,STRING)", "(,\"\")"),
                Row("TagRead", "541", "STRING", "(STRING,INT,STRING)", "(,0,\"\")"),
                Row("Ass", "539", "INT", "(INT,STRING,STRING,INT,STRING)", "(,,,,\"\")"),
                Row("StrLength", "36", "INT", "(STRING)"),
                Row("IntToStr", "29", "STRING", "(LONG)"),
                Row("GetName", "900", "INT", "(var STRING)"),
            }
        );

        private static NameRole Roles(string builtin, int arg) =>
            builtin == "_PageDisplay" && arg == 0 ? NameRole.Page
            : builtin == "TagRead" && arg == 0 ? NameRole.Tag
            : builtin == "Ass" && arg == 2 ? NameRole.Tag
            : NameRole.None;

        // Include's shapes: PageDisplay assigns its parameter only on the other branch,
        // AssWin passes its tags on through AssVarTags
        private const string Lib =
            "INT FUNCTION PageDisplay(STRING sPageName, STRING sCluster = \"\")\n"
            + "\tIF StrLength(sCluster) = 0 THEN\n\t\tRETURN _PageDisplay(sPageName, sCluster);\n"
            + "\tELSE\n\t\tsPageName = sCluster + \".\" + sPageName;\n\tEND\n\tRETURN 0;\nEND\n"
            + "FUNCTION AssVarTags(INT hWin, STRING sTag1 = \"\", STRING sTag2 = \"\")\n"
            + "\tAss(hWin, \"1\", sTag1, 0);\n\tAss(hWin, \"2\", sTag2, 0);\nEND\n"
            + "INT FUNCTION AssWin(STRING sPage, INT x = 0, STRING sTag1 = \"\", STRING sTag2 = \"\")\n"
            + "\tAssVarTags(-2, sTag1, sTag2);\n\tRETURN PageDisplay(sPage);\nEND\n"
            // the name is made before it is passed on: not followed
            + "INT FUNCTION Prefixed(STRING sPage)\n\tsPage = \"Mix_\" + sPage;\n\tRETURN _PageDisplay(sPage);\nEND\n"
            // a built-in's reference argument writes it
            + "INT FUNCTION Fetched(STRING sPage)\n\tGetName(sPage);\n\tRETURN _PageDisplay(sPage);\nEND\n"
            // a parameter of another use
            + "INT FUNCTION Length(STRING s)\n\tRETURN StrLength(s);\nEND\n";

        private static CompileUnit Unit(string lib = Lib)
        {
            var reg = new Registry(null);
            reg.Add(
                new SourceProject(
                    "Main",
                    null,
                    new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("lib.ci", lib),
                    },
                    new List<KeyValuePair<string, string>>(),
                    new List<string>(),
                    new List<KeyValuePair<string, string>>(),
                    new Dictionary<string, int>()
                )
            );
            var (order, diags) = reg.CompileOrder("Main");
            var u = new CompileUnit(order, Func0, diags).Compile();
            Assert.DoesNotContain(u.CompilerView(), d => d.IsError);
            return u;
        }

        private static List<string> Uses(CompileUnit u, string text, string kind = "expression")
        {
            var res = u.CompileField(text, kind, "Main");
            Assert.Empty(res.Errors);
            return NameArgs
                .For(u, Roles)
                .Literals(res.Node)
                .Select(x =>
                    x.Role + ":" + x.Literal.Value + "@" + res.Position(x.Literal).col + " via " + x.Primitive
                )
                .ToList();
        }

        [Fact]
        public void ALiteralIsFollowedThroughTheFunctionsThatPassItOn()
        {
            var u = Unit();
            Assert.Equal(new[] { "Page:Mix@14 via _PageDisplay" }, Uses(u, "_PageDisplay(\"Mix\")"));
            Assert.Equal(new[] { "Page:Mix@13 via _PageDisplay" }, Uses(u, "PageDisplay(\"Mix\")"));
            Assert.Equal(
                new[] { "Page:Pop@8 via _PageDisplay", "Tag:T1@18 via Ass", "Tag:T2@24 via Ass" },
                Uses(u, "AssWin(\"Pop\", 1, \"T1\", \"T2\")")
            );
            Assert.Equal(new[] { "Tag:Level@9 via TagRead" }, Uses(u, "TagRead(\"Level\") <> \"\""));
        }

        [Fact]
        public void OnlyFixedTextThatReachesTheArgumentUnchangedCounts()
        {
            var u = Unit();
            // computed, blank, made into another name, written by reference, another use
            Assert.Empty(Uses(u, "PageDisplay(\"Mi\" + \"x\")"));
            Assert.Empty(Uses(u, "PageDisplay(\"  \")"));
            Assert.Empty(Uses(u, "Prefixed(\"A\")"));
            Assert.Empty(Uses(u, "Fetched(\"A\")"));
            Assert.Empty(Uses(u, "Length(\"A\")"));
            // the cluster argument is no page
            Assert.Equal(new[] { "Page:A@13 via _PageDisplay" }, Uses(u, "PageDisplay(\"A\", \"C\")"));
        }

        [Fact]
        public void ACommandFieldGivesItsLiteralsInSourceOrder()
        {
            var u = Unit();
            Assert.Equal(
                new[] { "Tag:X@9 via TagRead", "Page:Y@27 via _PageDisplay", "Page:Z@50 via _PageDisplay" },
                Uses(u, "TagRead(\"X\"); PageDisplay(\"Y\"); IF 1 THEN AssWin(\"Z\"); END", "command")
            );
        }

        [Fact]
        public void AnEditOfTheUnitIsSeen()
        {
            var u = Unit();
            Assert.Empty(Uses(u, "Length(\"A\")"));
            var edit = u.ReplaceFile(
                "lib.ci",
                Lib.Replace("RETURN StrLength(s);", "RETURN _PageDisplay(s);")
            );
            Assert.True(edit.Incremental, edit.Reason);
            Assert.Equal(new[] { "Page:A@8 via _PageDisplay" }, Uses(u, "Length(\"A\")"));
        }
    }
}
