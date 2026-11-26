using System.Collections.Generic;
using System.Linq;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Compiler.Syntax;
using Xunit;
using Index = CicodeSim.Compiler.Syntax.Index;
using Range = CicodeSim.Compiler.Syntax.Range;

namespace CicodeSim.Tests.Compiler
{
    /// <summary>The Cicode parser: grammar, precedence and the compiler's error cascades
    /// (probe names cited).</summary>
    public class ParserTests
    {
        internal static Module Mod(string src) => Parser.ParseSource(src, null, "t.ci", "P");

        /// <summary>The parser's messages (both passes) in source order.</summary>
        internal static List<string> Codes(string src)
        {
            var m = Mod(src);
            var all = new List<Diagnostic>(m.Diagnostics);
            all.AddRange(m.DeclDiagnostics);
            return all.Select((d, i) => (d, i))
                .OrderBy(x => x.d.Pos)
                .ThenBy(x => x.d.Line)
                .ThenBy(x => x.i)
                .Select(x => x.d.Name)
                .ToList();
        }

        internal static List<Stmt> Body(string src) => Mod(src).Functions.First().Body;

        // ------------------------------------------------------------------------ file scope
        [Fact]
        public void FunctionHeaders()
        {
            var m = Mod(
                "PRIVATE STRING FUNCTION F(INT a, REAL b = 1.5, STRING s = \"x\")\n"
                    + "\tRETURN s;\nEND\n"
                    + "FUNCTION G\nEND\n"
            );
            var fs = m.Functions.ToList();
            var f = fs[0];
            var g = fs[1];
            Assert.Equal(("F", "PRIVATE", "STRING"), (f.Name, f.Scope, f.RetType));
            Assert.Equal(
                new[] { ("INT", "a"), ("REAL", "b"), ("STRING", "s") },
                f.Params.Select(p => (p.Type ?? "", p.Name)).ToArray()
            );
            Assert.Null(f.Params[0].Default);
            Assert.Equal(1.5, ((Num)f.Params[1].Default!).RValue);
            Assert.Equal(new[] { 0x04, 0x02, 0x07 }, f.ArgCodes);
            Assert.Equal(0x07, f.ResultCode);
            Assert.True(f.Complete && f.ReturnsValue);
            Assert.Equal(("G", (string?)null, false), (g.Name, g.RetType, g.HasParens));
            Assert.Empty(m.Diagnostics);
            Assert.Empty(m.DeclDiagnostics);
        }

        [Fact]
        public void FileVariables()
        {
            var m = Mod(
                "GLOBAL INT g[2][3] = 1, 2, 3;\nMODULE STRING s = \"a\";\nREAL r = -1.5;\n"
                    + "MODULE INT a, MODULE INT b;\n"
            );
            var vs = m.Variables.ToList();
            var (g, s, r, a, b) = (vs[0], vs[1], vs[2], vs[3], vs[4]);
            Assert.True(g.IsGlobal);
            Assert.Equal(new long[] { 2, 3 }, g.Dims.ToArray());
            Assert.Equal(6, g.Count);
            Assert.Equal(new long[] { 1, 2, 3 }, g.Init.Select(l => l.IValue).ToArray());
            Assert.Equal(("MODULE", "STRING", "a"), (s.Scope, s.Type, s.Init[0].SValue));
            Assert.Null(r.Scope);
            Assert.Equal(-1.5, r.Init[0].RValue);
            Assert.Equal(("a", "b"), (a.Name, b.Name));
        }

        [Fact]
        public void FileScopeErrors()
        {
            Assert.Equal(new[] { "E2031" }, Codes("INT FUNCTION P()\n\tRETURN 0;\nEND;\n")); // t03
            Assert.Equal(new[] { "E2006", "E2031" }, Codes("MODULE INT a = (1);\nFUNCTION Z()\nEND\n")); // i07
            Assert.Equal(new[] { "E2006" }, Codes("MODULE INT a = \"5\";\nFUNCTION Z()\nEND\n")); // i05
            Assert.Equal(new[] { "E2011", "E2031" }, Codes("MODULE INT a[2,3];\nFUNCTION Z()\nEND\n")); // d16
            Assert.Equal(
                new[] { "E2017", "E2031" },
                Codes("MODULE STRING s = (\"x\");\nFUNCTION Z()\nEND\n")
            );
            Assert.Equal(new[] { "E2069" }, Codes("MODULE FUNCTION F()\nEND\n")); // m11
        }

        // ------------------------------------------------------------------------ statements
        [Fact]
        public void IfElseWhileForReturn()
        {
            string src =
                "INT FUNCTION P(INT a)\n"
                + "\tINT i;\n"
                + "\tIF a > 1 THEN\n\t\ti = 1;\n\tELSE\n\t\ti = 2;\n\tEND\n"
                + "\tWHILE i < 5 DO\n\t\ti = i + 1;\n\tEND\n"
                + "\tFOR i = 1 TO 10 DO\n\t\tSleep(1);\n\tEND\n"
                + "\tRETURN i;\n"
                + "END\n";
            var m = Mod(src);
            Assert.Empty(m.Diagnostics);
            var b = m.Functions.First().Body;
            var decl = Assert.IsType<LocalDecl>(b[0]);
            Assert.Equal("i", decl.Name);
            var iff = Assert.IsType<If>(b[1]);
            Assert.Equal((3, 5, 7), (iff.Line, iff.ElseLine, iff.EndLine));
            Assert.IsType<Assign>(iff.Then[0]);
            Assert.Equal(4, iff.Then[0].Line);
            Assert.Equal(10, Assert.IsType<While>(b[2]).EndLine);
            var fo = Assert.IsType<For>(b[3]);
            Assert.True(fo.Complete);
            Assert.Equal("i", fo.Var.Text);
            var ret = Assert.IsType<Return>(b[4]);
            Assert.Equal("i", ((Name)ret.Value!).Text);
        }

        [Fact]
        public void SelectCase()
        {
            string src =
                "FUNCTION P(INT a)\n"
                + "\tSELECT CASE a\n"
                + "\tCASE 1, 2\n\t\tSleep(1);\n"
                + "\tCASE 3 TO 5 DO Sleep(3);\n" // an optional DO (s05)
                + "\tCASE IS > 9 Sleep(9);\n"
                + "\tCASE ELSE\n\t\tSleep(2);\n"
                + "\tEND\n\tSELECT\n" // END SELECT across lines (s13)
                + "END\n";
            var m = Mod(src);
            Assert.Empty(m.Diagnostics);
            var sel = Assert.IsType<Select>(m.Functions.First().Body[0]);
            Assert.Empty(sel.Pre);
            Assert.Equal(
                new[] { "value,value", "range", "is" },
                sel.Cases.Select(c => string.Join(",", c.Items.Select(i => i.Kind))).ToArray()
            );
            Assert.Equal(">", sel.Cases[2].Items[0].Op);
            Assert.NotNull(sel.Else);
            Assert.Equal(7, sel.ElseLine);
        }

        [Fact]
        public void SelectErrors()
        {
            // a plain END does not close a SELECT: E2034 at the END where SELECT belongs (it is
            // taken), and again at the end of the input (s02)
            Assert.Equal(
                new[] { "E2034", "E2034" },
                Codes("FUNCTION Z(INT x)\n\tINT y;\n\tSELECT CASE x\n\tCASE 1\n\t\ty = 1;\n\tEND\nEND\n")
            );
            // an empty clause: the next CASE is a stray token (r2_rev_diagB y04)
            Assert.Equal(
                new[] { "E2041" },
                Codes(
                    "FUNCTION Z(INT a)\n\tSELECT CASE a\n\tCASE 1\n\tCASE 2\n\t\tSleep(0);\n\tEND SELECT\nEND\n"
                )
            );
            // no selector: E2041 at the CASE, E2034 at the item, then the END closes the function
            // and 'SELECT' is at file scope (s16)
            Assert.Equal(
                new[] { "E2041", "E2034", "E2031" },
                Codes("FUNCTION Z(INT x)\n\tINT y;\n\tSELECT CASE\n\tCASE 1\n\t\ty = 1;\n\tEND SELECT\nEND\n")
            );
        }

        [Fact]
        public void LocalDeclarationsAnywhereAtTheTopLevel()
        {
            var b = Body("FUNCTION P()\n\tSleep(1);\n\tINT i = 2, j;\n\tSTRING s;\nEND\n");
            Assert.Equal(
                new[] { "ExprStmt", "LocalDecl", "LocalDecl", "LocalDecl" },
                b.Select(s => s.GetType().Name).ToArray()
            );
            Assert.Equal(2L, ((Num)((LocalDecl)b[1]).Init!).IValue);
            Assert.Null(((LocalDecl)b[2]).Init);
        }

        [Fact]
        public void TwoStatementsOnALineAndOptionalSemicolons()
        {
            var b = Body("FUNCTION P()\n\tINT i\n\tINT j\n\ti = 1 j = 2\nEND\n"); // t02, 9.2
            Assert.Equal(
                new[] { "LocalDecl", "LocalDecl", "Assign", "Assign" },
                b.Select(s => s.GetType().Name).ToArray()
            );
        }

        [Fact]
        public void AVariableHidesAFunction()
        {
            var b = Body("FUNCTION P()\n\tINT v;\n\tv(1);\nEND\n"); // q16
            Assert.IsType<Name>(((ExprStmt)b[1]).Expr);
        }

        [Fact]
        public void EndIfIsNotACloser()
        {
            // 'END IF': END closes the IF, then 'IF' starts a new statement (syntax b09)
            Assert.Equal(
                "E2041",
                Codes("FUNCTION P()\n\tINT i;\n\tIF i THEN\n\t\ti = 1;\n\tEND IF\nEND\n")[0]
            );
        }

        // ----------------------------------------------------------------------- expressions
        private static Expr ExprOf(string src) =>
            ((Assign)Body("FUNCTION P()\n\tINT a; INT b;\n\tx = " + src + ";\nEND\n")[2]).Value;

        [Fact]
        public void Precedence()
        {
            var e = Assert.IsType<Binary>(ExprOf("NOT a = b")); // (NOT a) = b (8.2 o20)
            Assert.Equal("=", e.Op);
            Assert.IsType<Unary>(e.Left);
            e = Assert.IsType<Binary>(ExprOf("\"x\" + a:##")); // ':' binds tighter than '+' (f22)
            Assert.Equal("+", e.Op);
            Assert.IsType<Format>(e.Right);
            e = Assert.IsType<Binary>(ExprOf("a = b BITAND 1")); // BITAND below '=' (o38)
            Assert.Equal("BITAND", e.Op);
            Assert.Equal("=", ((Binary)e.Left).Op);
            e = Assert.IsType<Binary>(ExprOf("1 + 2 * 3 - 4"));
            Assert.Equal("-", e.Op);
            Assert.Equal("+", ((Binary)e.Left).Op);
            Assert.Equal("*", ((Binary)((Binary)e.Left).Right).Op);
            var n = Assert.IsType<Num>(ExprOf("-5"));
            Assert.Equal(-5L, n.IValue); // folded (7.6)
        }

        [Fact]
        public void FormatSpecs()
        {
            var p = Parser.PictureSpec("#0.##EU");
            Assert.Equal((5, 2, true, true), ((int)p.Width, (int)p.Decimals, p.ZeroPad, p.Eu));
            p = Parser.PictureSpec("#-###");
            Assert.True(p.Left);
            Assert.Equal(5, (int)p.Width);
            var e = (Format)ExprOf("a:5");
            Assert.Equal("width", e.Spec.Kind);
            Assert.Equal(5, (int)e.Spec.Width);
            e = (Format)ExprOf("a:-3.2");
            Assert.True(e.Spec.Left);
            Assert.Equal((3, 2), ((int)e.Spec.Width, (int)e.Spec.Decimals));
            e = (Format)ExprOf("a:~");
            Assert.Equal("tilde", e.Spec.Kind);
        }

        [Fact]
        public void EndPositionsAreTheTokenAfter()
        {
            string src = "FUNCTION P()\n\tSTRING s;\n\ts = \"a\" - \"b\";\nEND\n";
            var m = Mod(src);
            var v = ((Assign)m.Functions.First().Body[1]).Value;
            Assert.Equal(';', m.Text[v.End]);
        }

        // ------------------------------------------------------------ error cascades (PROBED)
        [Fact]
        public void MissingKeywords()
        {
            Assert.Equal("E2032", Codes("FUNCTION P()\n\tINT i;\n\tIF i i = 1; END\nEND\n")[0]);
            Assert.Equal("E2033", Codes("FUNCTION P()\n\tINT i;\n\tWHILE i i = 1; END\nEND\n")[0]);
            Assert.Equal(new[] { "E2034" }, Codes("FUNCTION P()\n\tINT i;\n\ti = 1;\n"));
        }

        [Fact]
        public void StrayOperators()
        {
            // '*/' outside a comment: E2041 at '*' only (c09); '&&': one E2041 (o08)
            Assert.Equal(
                new[] { "E2041" },
                Codes("INT FUNCTION P()\n\tINT i;\n\ti = 1; */\n\tRETURN i;\nEND\n")
            );
            Assert.Equal(
                new[] { "E2041" },
                Codes("INT FUNCTION P()\n\tINT i; INT j;\n\ti = i && j;\n\tRETURN 0;\nEND\n")
            );
            Assert.Equal(
                new[] { "E2020" },
                Codes("INT FUNCTION P()\n\tINT i; INT j;\n\ti = i ^ j;\n\tRETURN 0;\nEND\n")
            ); // o10
            Assert.Equal(
                new[] { "E2041", "E2041" },
                Codes("INT FUNCTION P()\n\tINT i; INT j;\n\ti = {1};\n\tRETURN 0;\nEND\n")
            ); // o26
        }

        [Fact]
        public void ArgumentListRecovery()
        {
            // an empty argument is E2041, then E2011 at the token where ',' or ')' belongs and the
            // rest is new statements (sem d15)
            Assert.Equal(
                new[] { "E2041", "E2011", "E2041" },
                Codes("FUNCTION P()\n\tProbeAdd(1, , 4);\nEND\n")
            );
            Assert.Equal(new[] { "E2041" }, Codes("FUNCTION P()\n\tZzA(,);\nEND\n")); // e02
        }

        [Fact]
        public void IndexOnAScalarAndDottedDeclaration()
        {
            Assert.Equal(
                new[] { "E2041", "E2041" },
                Codes("INT FUNCTION P()\n\tINT i;\n\ti = i[0];\n\tRETURN 0;\nEND\n")
            ); // o27
            Assert.Equal(new[] { "E2041" }, Codes("INT FUNCTION P()\n\tINT a.b = 1;\n\tRETURN 0;\nEND\n")); // i11
        }

        [Fact]
        public void HeaderErrors()
        {
            // E2008 at a keyword parameter name: the body starts there (kx_param_while)
            Assert.Equal(
                new[] { "E2008", "E2041", "E2033" },
                Codes("INT FUNCTION F(INT while)\n\tRETURN 1;\nEND\n")
            );
            // E2011 at the '[' of an array parameter: the body starts after it (h17)
            Assert.Equal(new[] { "E2011", "E2041", "E2041" }, Codes("FUNCTION F(INT a[])\nEND\n"));
            // a keyword as a parameter: END closes the function, ')' is at file scope (h47)
            Assert.Equal(new[] { "E2008", "E2031" }, Codes("FUNCTION F(INT end)\nEND\n"));
            Assert.Equal(new[] { "E2015", "E2015" }, Codes("FUNCTION F(a)\nEND\n")); // untyped (h20, r36)
        }

        [Fact]
        public void BigNegativeNumber()
        {
            Assert.Empty(Codes("FUNCTION Z()\n\tINT i;\n\ti = -2147483648;\nEND\n")); // n26
            // E2006 at the number, the token after it is passed over (s20: E2011 at the ';')
            Assert.Equal(
                new[] { "E2006", "E2011" },
                Codes("FUNCTION Z()\n\tINT i;\n\ti = (-2147483649);\nEND\n")
            );
        }

        [Fact]
        public void KeywordsAsFunctionNames()
        {
            var m = Mod("INT FUNCTION if()\n\tRETURN 1;\nEND\n"); // 11.3 kf_if
            Assert.Equal("if", m.Functions.First().Name);
            Assert.Empty(m.Diagnostics);
            Assert.Equal(new[] { "E2041" }, Codes("INT FUNCTION P()\n\tRETURN nop();\nEND\n")); // kx_nop_call
        }

        // ------------------------------------------------------------- page fields
        private static (Expr? e, List<string> diags) PageExpr(string src)
        {
            var p = new Parser(Lexer.Tokenize(src, null, "", "page"), mode: "page");
            return (p.ParseExpressionField(), p.Diags.Select(d => d.Name).ToList());
        }

        private static (List<Stmt> stmts, List<string> diags) PageCmd(string src)
        {
            var p = new Parser(Lexer.Tokenize(src, null, "", "page"), mode: "page");
            return (p.ParseCommandField(), p.Diags.Select(d => d.Name).ToList());
        }

        [Fact]
        public void PageRangeSuffix()
        {
            // CtDraw's 'Specify range' (336k R2 page fields): (expr)^(lo,hi), signed numbers
            var (e, diags) = PageExpr("(_LibControl_GetInt(\"_tree_mt\", \"propMoveHorz\"))^(-4000,4000)");
            Assert.Empty(diags);
            var r = Assert.IsType<Range>(e);
            Assert.IsType<Call>(r.Operand);
            Assert.Equal((-4000L, 4000L), (r.Lo.IValue, r.Hi.IValue));
            (e, diags) = PageExpr("(15-3)^(0,1600)");
            Assert.Empty(diags);
            r = Assert.IsType<Range>(e);
            Assert.IsType<Binary>(r.Operand);
            Assert.Equal(1600L, r.Hi.IValue);
            // the precedence of ':' (tighter than '+')
            (e, _) = PageExpr("a + b^(0.0,100)");
            var b = Assert.IsType<Binary>(e);
            Assert.IsType<Range>(b.Right);
            // in a .ci file '^' stays E2020 (lexical o10)
            Assert.Contains("E2020", Codes("FUNCTION F()\n\tINT i = 2 ^ 3;\nEND\n"));
        }

        [Fact]
        public void PageAssociations()
        {
            foreach (
                var (src, name, suffix) in new[]
                {
                    ("?Tag?", "Tag", ""),
                    ("?Tag?.Field.VT", "Tag", ".Field.VT"),
                    ("?Digital 4?", "Digital 4", ""),
                    ("?1?", "1", ""),
                    ("?SENSOR?_LL_AL.Delay", "SENSOR", "_LL_AL.Delay"),
                }
            )
            {
                var (e0, d0) = PageExpr(src);
                Assert.Empty(d0);
                var a = Assert.IsType<Assoc>(e0);
                Assert.Equal((name, suffix), (a.Name, a.Suffix));
            }
            var (e, diags) = PageExpr("(?Equipment?.Output):#####.##");
            Assert.Empty(diags);
            var f = Assert.IsType<Format>(e);
            Assert.Equal(".Output", ((Assoc)f.Operand).Suffix);
            var (stmts, d2) = PageCmd(
                "?Tag?.OverrideMode.V = TagExtensions_SelectOverrideMode(?Tag?.OverrideMode.V)"
            );
            Assert.Empty(d2);
            var asg = Assert.IsType<Assign>(stmts[0]);
            Assert.IsType<Assoc>(asg.Target);
            // a .ci file: E2061
            Assert.Equal(new[] { "E2061" }, Codes("FUNCTION F()\n\tx = ?a?;\nEND\n"));
        }

        [Fact]
        public void PageLanguagePrefixAndCommentOnlyField()
        {
            var (stmts, diags) = PageCmd("CiVBA\r\nCiVBA_AN545.Caption = Oper_Input_String");
            Assert.Empty(diags);
            var v = Assert.IsType<CiVBA>(((ExprStmt)stmts[0]).Expr);
            Assert.Equal("CiVBA_AN545.Caption = Oper_Input_String", v.Text);
            var (e, d2) = PageExpr("CICODE x + 1");
            Assert.Empty(d2);
            Assert.IsType<Binary>(e);
            var (e3, d3) = PageExpr("!"); // 5 Example VISIBLE fields are only a comment
            Assert.Null(e3);
            Assert.Empty(d3);
        }

        [Fact]
        public void PicturesMatchTheCompiledFormatWords()
        {
            // StringFmt words of the R2 _LIBRARY.RDB (ExampleSA / Example / Test) next to the source
            // line's picture: low byte width, byte 1 decimals, flags 0x01 zero pad / 0x10 exponential
            foreach (
                var (pic, word) in new[]
                {
                    ("#", 0x1),
                    ("####", 0x4),
                    ("#####", 0x5),
                    ("#0", 0x10002),
                    ("#0#", 0x10003),
                    ("#.##", 0x204),
                    ("#s##", 0x100204),
                }
            )
            {
                var p = Parser.PictureSpec(pic);
                int flags = (p.ZeroPad ? 0x01 : 0) | (p.Left ? 0x02 : 0) | (p.Exponent ? 0x10 : 0);
                Assert.Equal(word, (int)p.Width | (int)p.Decimals << 8 | flags << 16);
            }
        }

        [Fact]
        public void ShortformDecimalsAreRoundedTenths()
        {
            // only ((int)(x * 10 + 0.5)) % 10 of a real shortform is used
            FormatSpec Spec(string src) =>
                (
                    (Format)((LocalDecl)Body("FUNCTION F()\n\tSTRING s = 1.5:" + src + ";\nEND\n")[0]).Init!
                ).Spec;
            Assert.Equal((3, 2), ((int)Spec("3.2").Width, (int)Spec("3.2").Decimals));
            Assert.Equal(3, (int)Spec("3.25").Decimals);
            Assert.Equal(0, (int)Spec("3.99").Decimals);
        }

        [Fact]
        public void AStatementThatTakesNothingNeverLoops()
        {
            // '/ <= NOP': E2041 at '/', the quiet operand after '<=' leaves NOP for the statement
            // parser, which then takes nothing and must not loop
            Assert.Equal(new[] { "E2041", "E2041", "E2041", "E2034" }, Codes("FUNCTION F() / <= NOP ELSE"));
            Assert.Equal(
                new[] { "E2041", "E2041" },
                Codes("FUNCTION F()\n\tIF 1 THEN / <= NOP END\nEND\n").Take(2).ToArray()
            );
        }

        [Fact]
        public void EmptyArgumentThenAStringGoesWithThePlaceholder()
        {
            // r2_rev_diagA ext r15 'ZzUn2(1, , "x")': E2041 at the second ',' only; a number there is
            // E2011 and the call ends (sem d15 'ProbeAdd(1, , 4)': E2041, E2011, then E2041 at ')')
            Assert.Equal(new[] { "E2041" }, Codes("FUNCTION F()\n\tINT i = ZzUn2(1, , \"x\");\nEND\n"));
            Assert.Equal(
                new[] { "E2041", "E2011", "E2041" },
                Codes("FUNCTION F()\n\tINT i = ZzUn2(1, , 4);\nEND\n")
            );
            var call = (Call)((ExprStmt)Body("FUNCTION F()\n\tZzUn2(1, , \"x\", 2);\nEND\n")[0]).Expr;
            Assert.Equal(3, call.Args.Count);
            Assert.IsType<ErrorOperand>(call.Args[1]);
        }

        [Fact]
        public void OneUnaryPlusOnly()
        {
            // one '+' before an operand is passed over
            Assert.Empty(Codes("FUNCTION F()\n\tINT v = 1 + +2;\n\tv = +5;\n\tv = -+2;\n\tv = +-2;\nEND\n"));
            Assert.Equal("E2041", Codes("FUNCTION F()\n\tINT v = ++5;\nEND\n")[0]);
        }

        [Fact]
        public void NestingPastTheLimitIsReportedNotACrash()
        {
            int n = Parser.MaxNesting + 50;
            string deep =
                "FUNCTION F()\n\tINT v = " + new string('(', n) + "1" + new string(')', n) + ";\nEND\n";
            Assert.Equal(new[] { "F4008" }, Codes(deep));
            string ifs =
                "FUNCTION F()\n\tINT v;\n"
                + string.Concat(Enumerable.Repeat("\tIF v THEN\n", n))
                + "\tv = 1;\n"
                + string.Concat(Enumerable.Repeat("\tEND\n", n))
                + "END\n";
            Assert.Equal(new[] { "F4008" }, Codes(ifs));
            int k = Parser.MaxNesting - 1;
            string ok =
                "FUNCTION F()\n\tINT v = " + new string('(', k) + "1" + new string(')', k) + ";\nEND\n";
            Assert.Empty(Codes(ok));
            var p = new Parser(
                Lexer.Tokenize("x = " + string.Concat(Enumerable.Repeat("NOT ", n)) + "1", null, "", "page"),
                mode: "page"
            );
            p.ParseCommandField();
            Assert.Equal(new[] { "F4008" }, p.Diags.Select(d => d.Name).ToArray());
        }

        [Fact]
        public void VeryDeepInputNeverOverflowsTheStack()
        {
            // a .NET stack overflow cannot be caught: the cap holds for 100,000 levels of every kind
            int n = 100_000;
            Assert.Equal(
                new[] { "F4008" },
                Codes("FUNCTION F()\n\tINT v = " + new string('(', n) + "1;\nEND\n")
            );
            Assert.Equal(
                new[] { "F4008" },
                Codes("FUNCTION F()\n\tINT v = " + string.Concat(Enumerable.Repeat("-", n)) + "1;\nEND\n")
            );
            Assert.Equal(
                new[] { "F4008" },
                Codes("FUNCTION F()\n\tINT v = " + string.Concat(Enumerable.Repeat("f(", n)) + "1;\nEND\n")
            );
            Assert.Equal(
                new[] { "F4008" },
                Codes(
                    "FUNCTION F()\n\tINT v;\n"
                        + string.Concat(Enumerable.Repeat("WHILE v DO ", n))
                        + "\nEND\n"
                )
            );
            // a long left-nested chain is no nesting at all: 200,000 terms parse and dump
            string chain =
                "FUNCTION F()\n\tINT v = " + string.Join(" + ", Enumerable.Repeat("1", 200_000)) + ";\nEND\n";
            var m = Mod(chain);
            Assert.Empty(m.Diagnostics);
            Assert.True(AstDump.Lines(m, "t").Count() > 400_000);
        }

        [Fact]
        public void AnAssignmentWhoseIndexSkipPassedItsEqualsEndsAtTheTarget()
        {
            // AssignAhead sees 'a[...] =', but the skip after the E2041 at '^' takes the '(' with
            // everything to the next ')', the '=' too: the statement ends at the target without a
            // message, the next one starts there (CtCmp32: conformance/compile/recovery a01..a12)
            const string head = "INT a[3];\nFUNCTION F()\n\t";
            Assert.Equal(new[] { "E2041" }, Codes(head + "a[^(]=1)]\nEND\n"));
            Assert.Equal(new[] { "E2041" }, Codes(head + "a[^(]=1)] 7;\nEND\n"));
            Assert.Equal(new[] { "E2041", "E2041" }, Codes(head + "a[^(]=1)] 7 + ;\nEND\n"));
            Assert.Equal(new[] { "E2041", "E2041" }, Codes(head + "a[^(]=1)] = ;\nEND\n"));
            // at the end of the input: no step past the end (it threw before)
            Assert.Equal(new[] { "E2041", "E2034" }, Codes(head + "a[^(]=1)]"));
        }
    }
}
