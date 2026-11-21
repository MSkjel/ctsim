using System.Linq;
using CicodeSim.Compiler.Syntax;
using Xunit;

namespace CicodeSim.Tests.Compiler
{
    /// <summary>A dotted name's part may start with digits in a page field.</summary>
    public class DigitPartLexerTests
    {
        private static (string, string) N(string v) => ("name", v);

        [Fact]
        public void PageFieldPartsThatStartWithDigits()
        {
            Assert.Equal(
                new[] { N("A100001.Area.Unit.S100.I200.12_34XY567_1.STS_Open") },
                LexerTests.Kv("A100001.Area.Unit.S100.I200.12_34XY567_1.STS_Open", mode: "page")
            );
            Assert.Equal(
                new[] { N("A100002.Unit.Level02.Outdoor.42_Q1_42_07_Run_On_Off_SP.Valid.v") },
                LexerTests.Kv("A100002.Unit.Level02.Outdoor.42_Q1_42_07_Run_On_Off_SP.Valid.v", mode: "page")
            );
            Assert.Equal(
                new[] { N("a.1b"), ("+", "+"), N("c.2_") }.Select(x => x.Item1),
                LexerTests.Kv("a.1b + c.2_", mode: "page").Select(x => x.Item1)
            );
            // a part of digits only is no part: 'ZzTag' then the rest (E2041 when parsed)
            var z = LexerTests.Kv("ZzTag.1", mode: "page");
            Assert.Equal(N("ZzTag"), z[0]);
            Assert.True(z.Count > 1);
            // digits, then a character that is no letter or '_': no part (the digit-led name's shape)
            Assert.Equal(N("ZzTag"), LexerTests.Kv("ZzTag.1\\a", mode: "page")[0]);
        }

        [Fact]
        public void CiFilePartsThatStartWithDigitsAreNoParts()
        {
            // CtCmp32 8.50 (CIPROBE_PRIVATE rig): every one E2041 at the '.'
            foreach (
                string e in new[]
                {
                    "ZzTag.1a",
                    "ZzTag.1_",
                    "ZzTag.12_34XY567_1.STS",
                    "ZzTag.1e5",
                    "ZzTag.12b",
                }
            )
            {
                var (toks, _) = LexerTests.Toks(
                    "INT FUNCTION P()\n INT i;\n i = " + e + ";\n RETURN 0;\nEND\n"
                );
                Assert.Contains(toks, t => LexerTests.Val(t) == "ZzTag");
                Assert.DoesNotContain(toks, t => LexerTests.Val(t) == e);
                // the probe's first message
                var codes = ParserTests.Codes(
                    "INT FUNCTION P()\n\tINT i; INT j;\n\ti = " + e + ";\n\tRETURN 0;\nEND\n"
                );
                Assert.True(codes.Count > 0 && codes[0] == "E2041", e + ": " + string.Join(",", codes));
            }
            // a project's pgdynobj page field: one name, no message
            var pf = new Parser(
                Lexer.Tokenize("A100001.Area.Unit.S100.I300.12_34XY890_1.STS_Open", null, "", "page"),
                mode: "page"
            );
            Assert.NotNull(pf.ParseExpressionField());
            Assert.Empty(pf.Diags);
        }
    }
}
