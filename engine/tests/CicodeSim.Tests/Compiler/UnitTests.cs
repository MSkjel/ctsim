using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Compiler.Syntax;
using CicodeSim.Values;
using Xunit;

namespace CicodeSim.Tests.Compiler
{
    /// <summary>The Cicode compile unit.</summary>
    public class UnitTests
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

        internal static readonly Func0Rows SmallFunc0 = new Func0Rows(
            new List<Func0Row>
            {
                Row("StrLength", "36", "INT", "(STRING)"),
                Row("StrLeft", "32", "STRING", "(STRING,INT)"),
                Row("Sleep", "183", "INT", "(LONG)"),
                Row("PageNext", "2", "INT", "()"),
                Row("FormButton", "254", "INT", "(INT,INT,STRING,FUNCTION,INT)"),
                Row("TaskNew", "149", "INT", "(STRING,STRING,INT,STRING)", "(,,,\"\")"),
                Row("DLLCallEx", "695", "VARIANT", "(INT,VARARG)"),
            }
        );

        internal sealed class P
        {
            public Dictionary<string, string> Files = new Dictionary<string, string>();
            public List<string> Includes = new List<string>();
            public List<(string, string)> Labels = new List<(string, string)>();
            public List<(string, string)> Locvars = new List<(string, string)>();
            public Dictionary<string, int> Tags = new Dictionary<string, int>();
        }

        private static P Proj(
            Dictionary<string, string>? files = null,
            IEnumerable<string>? incl = null,
            IEnumerable<(string, string)>? labels = null,
            IEnumerable<(string, string)>? locvars = null,
            Dictionary<string, int>? tags = null
        )
        {
            return new P
            {
                Files = files ?? new Dictionary<string, string>(),
                Includes = incl?.ToList() ?? new List<string>(),
                Labels = labels?.ToList() ?? new List<(string, string)>(),
                Locvars = locvars?.ToList() ?? new List<(string, string)>(),
                Tags = tags ?? new Dictionary<string, int>(),
            };
        }

        private static List<KeyValuePair<string, string>> Pairs(IEnumerable<(string, string)> xs) =>
            xs.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)).ToList();

        private static Registry Reg(params (string name, P spec)[] projects)
        {
            var reg = new Registry(null);
            foreach (var (name, spec) in projects)
                reg.Add(
                    new SourceProject(
                        name,
                        null,
                        spec.Files.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value)).ToList(),
                        Pairs(spec.Labels),
                        spec.Includes,
                        Pairs(spec.Locvars),
                        spec.Tags
                    )
                );
            return reg;
        }

        private static CompileUnit Unit(string root, params (string name, P spec)[] projects)
        {
            var reg = Reg(projects);
            var (order, diags) = reg.CompileOrder(root);
            return new CompileUnit(order, SmallFunc0, diags).Compile();
        }

        private static CompileUnit Unit(params (string name, P spec)[] projects) => Unit("Main", projects);

        private static List<string> View(CompileUnit u) => u.CompilerView().Select(d => d.Name).ToList();

        private static List<string> Errors(CompileUnit u) =>
            u.CompilerView().Where(d => d.IsError).Select(d => d.Name).ToList();

        private static Dictionary<string, string> Files(params (string name, string text)[] fs)
        {
            var d = new Dictionary<string, string>();
            foreach (var (n, t) in fs)
                d[n] = t;
            return d;
        }

        /// <summary>'RETURN Unknown();' in an INT function: E2031, the void value E2037 + E2024,
        /// then E2037 at END (lexical base02, inc q1_00 / q6_01).</summary>
        private static readonly string[] Unknown = { "E2031", "E2037", "E2024", "E2037" };

        private static CompileUnit One(string src, params (string name, P spec)[] more)
        {
            var all = new List<(string, P)> { ("Main", Proj(Files(("main.ci", src)))) };
            all.AddRange(more);
            return Unit(all.ToArray());
        }

        private static List<(string, int)> Lines(CompileUnit u) =>
            u.CompilerView().Select(d => (d.Name, d.Line)).ToList();

        // -------------------------------------------------------------------------- projects
        [Fact]
        public void CompileOrderIsDepthFirstPostOrderRootLast()
        {
            var reg = Reg(
                ("System", Proj()),
                ("Include", Proj()),
                ("Main", Proj(incl: new[] { "A", "B" })),
                ("A", Proj(incl: new[] { "D" })),
                ("B", Proj(incl: new[] { "C" })),
                ("C", Proj(incl: new[] { "D", "Include" })),
                ("D", Proj())
            );
            var (order, diags) = reg.CompileOrder("Main");
            // System and Include first; a diamond's shared project once (q3_o_dfs_vs_level, q10_01)
            Assert.Equal(
                new[] { "System", "Include", "D", "A", "C", "B", "Main" },
                order.Select(p => p.Name).ToArray()
            );
            Assert.Empty(diags);
        }

        [Fact]
        public void CompileOrderErrors()
        {
            var reg = Reg(
                ("Main", Proj(incl: new[] { "X", "Nope", " " })),
                ("X", Proj(incl: new[] { "Main" }))
            );
            var (order, diags) = reg.CompileOrder("Main");
            Assert.Equal(new[] { "F4017", "F4016", "E2008", "F4016" }, diags.Select(d => d.Name).ToArray()); // q9 / q10
            Assert.Equal(new[] { "X", "Main" }, order.Select(p => p.Name).ToArray());
        }

        [Fact]
        public void FilesCompileInUpperCaseNameOrder()
        {
            var p = new SourceProject(
                "P",
                null,
                new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("b.ci", ""),
                    new KeyValuePair<string, string>("A.ci", ""),
                    new KeyValuePair<string, string>("c.CI", ""),
                }
            );
            Assert.Equal(new[] { "A.ci", "b.ci", "c.CI" }, p.CiFiles().Select(f => f.name).ToArray());
        }

        // ------------------------------------------------------------------ the signature table
        [Fact]
        public void FuncsymRows()
        {
            var u = Unit(
                (
                    "Main",
                    Proj(
                        Files(
                            (
                                "b.ci",
                                "PRIVATE INT FUNCTION Hidden(INT a, STRING s = \"x\")\n\tRETURN a;\nEND\n"
                                    + "PUBLIC FUNCTION Shown()\nEND\n"
                                    + "REAL FUNCTION Calc(REAL r, INT n, OBJECT o = -1)\n\tRETURN r;\nEND\n"
                            ),
                            ("a.ci", "STRING FUNCTION First()\n\tRETURN \"\";\nEND\n")
                        )
                    )
                )
            );
            Assert.Empty(View(u));
            var rows = u.Funcsym().Select(r => r.ToDictionary(kv => kv.Key, kv => kv.Value)).ToList();
            Assert.Equal(
                new[] { "First", "Hidden@b@Main", "Shown", "Calc" },
                rows.Select(r => r["NAME"]).ToArray()
            );
            var by = rows.ToDictionary(r => r["NAME"]);
            var ops = rows.Select(r =>
                    int.Parse(r["OP_CODE"], System.Globalization.CultureInfo.InvariantCulture)
                )
                .ToList();
            Assert.Equal(ops.OrderBy(x => x).ToList(), ops);
            Assert.Equal(
                (Types.Long | 0x400).ToString(System.Globalization.CultureInfo.InvariantCulture),
                by["Hidden@b@Main"]["TYPE"]
            ); // PRIVATE
            Assert.Equal(
                (Types.Void | 0x800).ToString(System.Globalization.CultureInfo.InvariantCulture),
                by["Shown"]["TYPE"]
            ); // explicit PUBLIC
            Assert.Equal("0", by["Shown"]["RETURNED"]);
            Assert.Equal("1", by["First"]["RETURNED"]);
            Assert.Equal(
                new string(
                    new[]
                    {
                        Types.SigChar(Types.Real),
                        Types.SigChar(Types.Long),
                        Types.SigChar(Types.Object),
                    }
                ),
                by["Calc"]["ARGC"]
            );
            Assert.Equal("1", by["Hidden@b@Main"]["MANDATORY"]);
            Assert.Equal("2", by["Calc"]["MANDATORY"]);
        }

        // -------------------------------------------------------------------------- visibility
        [Fact]
        public void PrivateFunctionsAreFileLocal()
        {
            var u = Unit(
                (
                    "Main",
                    Proj(
                        Files(
                            ("a.ci", "PRIVATE INT FUNCTION P()\n\tRETURN 1;\nEND\n"),
                            ("b.ci", "INT FUNCTION Q()\n\tRETURN P();\nEND\n")
                        )
                    )
                )
            );
            Assert.Equal(Unknown, View(u)); // q6_01 / q6_02
            u = Unit(
                (
                    "Main",
                    Proj(
                        Files(
                            ("a.ci", "PRIVATE INT FUNCTION P()\n\tRETURN 1;\nEND\n"),
                            ("b.ci", "PRIVATE INT FUNCTION P()\n\tRETURN 2;\nEND\n")
                        )
                    )
                )
            );
            Assert.Empty(View(u)); // q6_15
        }

        [Fact]
        public void DuplicatePublicFunctionLandsOnTheLaterDefinition()
        {
            var u = Unit(
                ("Main", Proj(Files(("main.ci", "INT FUNCTION Dup()\n\tRETURN 1;\nEND\n")), new[] { "Lib" })),
                ("Lib", Proj(Files(("z.ci", "INT FUNCTION Dup()\n\tRETURN 2;\nEND\n"))))
            );
            var d = u.CompilerView();
            Assert.Equal(new[] { "E2021" }, d.Select(x => x.Name).ToArray());
            Assert.Equal("main.ci", d[0].File); // q3_d_*
        }

        [Fact]
        public void ALibraryCanCallItsIncluderAndIncludesAreVisible()
        {
            var u = Unit(
                (
                    "Main",
                    Proj(
                        Files(("main.ci", "INT FUNCTION MainFn()\n\tRETURN LibFn();\nEND\n")),
                        new[] { "Lib" }
                    )
                ),
                ("Lib", Proj(Files(("lib.ci", "INT FUNCTION LibFn()\n\tRETURN MainFn();\nEND\n"))))
            );
            Assert.Empty(View(u)); // q2_01
            u = Unit(
                "Lib",
                ("Lib", Proj(Files(("lib.ci", "INT FUNCTION LibFn()\n\tRETURN MainFn();\nEND\n"))))
            );
            Assert.Equal(Unknown, View(u));
        }

        [Fact]
        public void ModuleAndGlobalVariables()
        {
            var u = Unit(
                (
                    "Main",
                    Proj(
                        Files(
                            ("a.ci", "GLOBAL INT g = 1;\nMODULE INT m = 2;\n"),
                            ("b.ci", "INT FUNCTION F()\n\tRETURN g + m;\nEND\n")
                        )
                    )
                )
            );
            Assert.Equal(new[] { "W1007" }, View(u)); // m is a.ci's: an unknown tag in b.ci
            u = Unit(("Main", Proj(Files(("a.ci", "GLOBAL INT g;\n"), ("b.ci", "GLOBAL STRING g;\n")))));
            Assert.Equal(new[] { "E2021" }, View(u));
            u = Unit(("Main", Proj(Files(("a.ci", "GLOBAL INT F;\n"), ("b.ci", "FUNCTION F()\nEND\n")))));
            Assert.Equal(new[] { "E2021" }, View(u)); // a function named like a GLOBAL (s07)
        }

        [Fact]
        public void LibraryFunctionReplacesTheBuiltinEverywhere()
        {
            var u = Unit(
                ("Main", Proj(Files(("main.ci", "FUNCTION P()\n\tSleep(5);\nEND\n")), new[] { "Lib" })),
                ("Lib", Proj(Files(("lib.ci", "FUNCTION Sleep()\nEND\n"))))
            );
            Assert.Equal(
                new[] { "E2022", "W1006" },
                View(u).OrderBy(x => x, StringComparer.Ordinal).ToArray()
            ); // q12 / g08
        }

        // -------------------------------------------------------------------- names and types
        [Fact]
        public void NamesResolveLocalsThenVariablesThenTags()
        {
            var u = One("INT FUNCTION P(INT a)\n\tINT v;\n\tv(1);\n\tRETURN a + Unknown;\nEND\n");
            // 'v(1)' is the variable v, then the statement '(1)': W1021 at '(' and at ';' (sem q16);
            // then an unknown tag
            Assert.Equal(new[] { "W1021", "W1021", "W1007" }, View(u));
            u = Unit(
                (
                    "Main",
                    Proj(
                        Files(("main.ci", "INT FUNCTION P()\n\tRETURN Known + Known.Field;\nEND\n")),
                        tags: new Dictionary<string, int> { ["known"] = Types.Long }
                    )
                )
            );
            Assert.Empty(View(u)); // a defined tag and one of its items
            u = One("FUNCTION P()\n\tTIMESTAMP t = Tag.T;\n\tQUALITY q = Tag.Q;\nEND\n");
            Assert.Equal(new[] { "W1007", "W1007" }, View(u)); // .T TIMESTAMP, .Q QUALITY
            u = Unit(
                (
                    "Main",
                    Proj(
                        Files(("main.ci", "STRING FUNCTION P()\n\tRETURN Lv;\nEND\n")),
                        locvars: new[] { ("Lv", "STRING") }
                    )
                )
            );
            Assert.Empty(View(u)); // locvar.dbf
            u = One("FUNCTION F(INT a)\nEND\nFUNCTION P()\n\tINT i = F;\nEND\n");
            Assert.Equal(new[] { "E2019", "W1004" }, View(u)); // sem q05
        }

        [Fact]
        public void TypeErrorsAndMissingReturn()
        {
            Assert.Equal(
                new[] { "E2024" },
                View(One("FUNCTION P()\n\tSTRING s;\n\ts = \"a\" - \"b\";\nEND\n"))
            ); // o15
            Assert.Equal(new[] { "E2037" }, View(One("INT FUNCTION P()\n\tINT i;\nEND\n"))); // h30
            Assert.Equal(new[] { "E2027" }, View(One("FUNCTION P()\n\tSTRING s;\n\tIF s THEN END\nEND\n")));
            // an unknown function: E2031, void as a value (base02: then E2037 + E2024 at RETURN)
            Assert.Equal(
                new[] { "E2031", "E2037", "E2024", "E2037" },
                View(One("INT FUNCTION P()\n\tRETURN Nope();\nEND\n"))
            );
        }

        [Fact]
        public void ValueStatementsWarn()
        {
            Assert.Equal(new[] { "W1021" }, View(One("FUNCTION P()\n\tINT x;\n\tx;\nEND\n")));
            Assert.Empty(View(One("FUNCTION P()\n\tStrLength(\"a\");\nEND\n")));
            // after a void RETURN the value is W1023, unless it is led by a call (fix_diagA v01 .. w36)
            Assert.Equal(new[] { "W1023" }, View(One("FUNCTION P()\n\tRETURN StrLength(\"a\") + 1;\nEND\n")));
            Assert.Empty(View(One("FUNCTION P()\n\tRETURN (StrLength(\"a\")) + 1;\nEND\n")));
            Assert.Empty(View(One("FUNCTION P()\n\tRETURN NOT StrLength(\"a\");\nEND\n")));
            Assert.Equal(new[] { "W1023" }, View(One("FUNCTION P()\n\tINT x;\n\tRETURN -x;\nEND\n")));
        }

        [Fact]
        public void BuiltinArityAndCallbacks()
        {
            Assert.Equal(new[] { "E2022" }, View(One("FUNCTION P()\n\tStrLength();\nEND\n")));
            Assert.Equal(
                new[] { "E2022", "E2022" },
                View(One("FUNCTION P()\n\tStrLeft(\"a\", 1, 2, 3);\nEND\n"))
            );
            Assert.Empty(View(One("FUNCTION P()\n\tTaskNew(\"a\", \"b\", 1);\nEND\n"))); // default
            Assert.Equal(new[] { "E2022", "E2022" }, View(One("FUNCTION P()\n\tDLLCallEx();\nEND\n"))); // a14
            string cb = "INT FUNCTION ZCb()\n\tRETURN 0;\nEND\n";
            Assert.Empty(View(One(cb + "FUNCTION Z()\n\tFormButton(1, 1, \"x\", ZCb, 0);\nEND\n")));
            Assert.Empty(View(One(cb + "FUNCTION Z()\n\tFormButton(1, 1, \"x\", 0, 0);\nEND\n")));
            // one name token: '(ZCb)' E2008 (b09), 'ZCb + 0' E2011 (b10), then the rest cascades
            Assert.Equal(
                new[] { "E2008", "E2011", "E2022", "E2041", "E2041", "E2041" },
                View(One(cb + "FUNCTION Z()\n\tFormButton(1, 1, \"x\", (ZCb), 0);\nEND\n"))
            );
            Assert.Equal(
                new[] { "E2011", "E2022", "E2041", "E2041" },
                View(One(cb + "FUNCTION Z()\n\tFormButton(1, 1, \"x\", ZCb + 0, 0);\nEND\n"))
            );
        }

        [Fact]
        public void LabelsOfEveryProjectApplyToTheWholeUnit()
        {
            var u = Unit(
                ("Main", Proj(Files(("main.ci", "INT FUNCTION P()\n\tRETURN TWO;\nEND\n")), new[] { "Lib" })),
                ("Lib", Proj(labels: new[] { ("TWO", "2") }))
            );
            Assert.Empty(View(u));
            u = Unit(
                ("Main", Proj(incl: new[] { "Lib" }, labels: new[] { ("A", "1") })),
                ("Lib", Proj(labels: new[] { ("A", "2") }))
            );
            Assert.Equal(new[] { "E2038" }, View(u)); // q7_04
        }

        [Fact]
        public void StopAtTheFirstFailingFile()
        {
            var u = Unit(
                (
                    "Main",
                    Proj(
                        Files(
                            ("a.ci", "FUNCTION A()\n\tNope1();\nEND\n"),
                            ("b.ci", "FUNCTION B()\n\tNope2();\nEND\n")
                        )
                    )
                )
            );
            var d = u.CompilerView().Where(x => x.IsError).ToList();
            Assert.Equal(new[] { "E2031" }, d.Select(x => x.Name).ToArray());
            Assert.Equal("a.ci", d[0].File); // q3_o_lib_two_files
        }

        // ------------------------------------------------------------------------ the R2 install
        internal static readonly string User = EngineEnv.Get("CICODE_USER")
            ?? @"C:\ProgramData\AVEVA Plant SCADA 2023 R2\User";

        internal static bool Heavy() =>
            Environment.GetEnvironmentVariable("CTG_SKIP_HEAVY") != "1"
            && File.Exists(Path.Combine(User, "ExampleSA", "_FUNCSYM.DBF"));

        [FuncsymFact]
        public void SourceBuiltFuncsymEqualsTheCompiledTable()
        {
            var u = CompileUnit.Build("ExampleSA", User);
            Assert.Empty(u.Errors());
            var want = CicodeSim.Project.Dbf.CitectRows(Path.Combine(User, "ExampleSA", "_FUNCSYM.DBF"));
            var got = u.Funcsym();
            Assert.Equal(want.Count, got.Count);
            for (int i = 0; i < want.Count; i++)
            {
                Assert.Equal(want[i].FieldCount, got[i].Count);
                foreach (var kv in got[i])
                    Assert.Equal(want[i].Get(kv.Key), kv.Value);
            }
        }

        [Fact]
        public void TheCompilersFunc0ViewMatchesTheRuntimeTable()
        {
            var rows = Func0Rows.Load();
            if (rows.Count == 0)
                return; // no install
            Assert.Equal(1147, rows.Count);
            var task = rows.Get("tasknew")!;
            Assert.Equal((4, 3, false), (task.Args.Count, task.MinArgs, task.VarArg));
            Assert.Equal("\"\"", task.Defaults[3]);
            Assert.True(rows.Get("dllcallex")!.VarArg);
            Assert.Equal(-1, rows.Get("dllcallex")!.MaxArgs);
            Assert.Contains(rows.Get("formbutton")!.Args, a => a.kind == "FUNCTION");
            Assert.Contains(rows.Get("trngettable")!.Args, a => a.byRef && a.kind == "REAL");
            // the VM's table (CicodeSim.Compiled.Func0Table) holds the same OP_CODEs
            var vm = CicodeSim.Compiled.Func0Table.Default;
            foreach (var r in rows.Rows)
            {
                var b = vm.Get(r.Opcode);
                Assert.NotNull(b);
                Assert.Equal(r.Name, b!.Func0Name);
            }
        }

        // ----------------------------------------------------- held-out verdicts
        [Fact]
        public void SyntaxErrorDropsTheFilesTagAndStatementWarnings()
        {
            // syntax l04 without its label: 'ENDIF' is a name, W1007 + W1021 are not reported because
            // the function's END is missing (E2034 at the end) -- before the error too
            Assert.Equal(
                new[] { ("E2034", 7) },
                Lines(One("FUNCTION Z(INT x)\n\tINT y;\n\tIF x THEN\n\t\ty = 1;\n\tENDIF\nEND\n"))
            );
            // l15: a FOREVER line in a function that ends fine, then a file-scope END
            Assert.Equal(
                new[] { ("E2031", 5) },
                Lines(One("FUNCTION Z()\n\tFOREVER\n\t\tSleep(1);\n\tEND\nEND\n"))
            );
            // without a syntax error the same warnings are reported
            Assert.Equal(new[] { "W1007", "W1021" }, View(One("FUNCTION Z()\n\tFOREVER\nEND\n")));
        }

        [Fact]
        public void SyntaxErrorDropsUnknownFunctionMessages()
        {
            // P_k21 without its label: no E2031 / E2037 / E2024 for the unknown PROBE_OB, only the
            // argument list's syntax errors
            var u = One("INT FUNCTION ProbeK()\n\tRETURN PROBE_OB(, 2) + PROBE_OB(5, 2);\nEND\n");
            Assert.Equal(new[] { "E2041", "E2011", "E2041" }, Errors(u));
        }

        [Fact]
        public void FileScopeE2031EndsTheFilesMessages()
        {
            // P_l13 / syntax l07 / rev z09: one E2031, nothing after it
            Assert.Equal(
                new[] { ("E2031", 1) },
                Lines(One("ZzInt FUNCTION ZzF()\n\tRETURN 1;\nEND\nZzInt FUNCTION ZzG()\nEND\n"))
            );
            Assert.Equal(new[] { ("E2031", 1) }, Lines(One("BOOL FUNCTION F()\n\tRETURN 1;\nEND\n")));
            var u = One(
                "ZINT FUNCTION ZCb()\n\tRETURN 0;\nEND\nFUNCTION Z()\n\tFormButton(1, 1, \"x\", ZCb, 0);\nEND\n"
            );
            Assert.Equal(new[] { ("E2031", 1) }, Lines(u));
        }

        [Fact]
        public void FunctionsAfterAFileScopeE2031AreStillDeclared()
        {
            // fix_diagA s09: Include AlarmFilterUtil.ci defines PageAlarmFilter below a file-scope
            // E2031, and AFilter.ci calls it without an E2031
            var u = Unit(
                (
                    "Main",
                    Proj(
                        Files(
                            ("a.ci", "FUNCTION P()\n\tLaterFn();\nEND\n"),
                            ("b.ci", "FUNCTION Q()\nEND\nEND\nFUNCTION LaterFn()\nEND\n")
                        )
                    )
                )
            );
            Assert.True(u.Public.ContainsKey("laterfn"));
            Assert.Equal(new[] { ("E2031", 3) }, Lines(u));
        }

        [Fact]
        public void FunctionInsideABodyIsTakenAndTheBodyGoesOn()
        {
            // lexical l13 (END = 5): at the top level of a body FUNCTION is E2076, taken, and the name
            // after it is a call of this body; h22: nothing more
            var u = One("FUNCTION F()\n\tINT x = 1;\nFUNCTION G()\nEND\n");
            Assert.Equal(new[] { ("E2076", 3) }, Lines(u));
            Assert.Equal(new[] { "F" }, u.Functions.Select(f => f.Name).ToArray());
            u = One("FUNCTION P()\n\tSTRING p;\nFUNCTION Q(STRING p, INT n)\n\tSleep(n);\nEND\n");
            // 'Q(STRING' E2041 at STRING, E2011 at p, E2041 at ',', then 'INT n' declares n, E2041 at ')'
            Assert.Equal(new[] { "E2076", "E2041", "E2011", "E2041", "E2041" }, Errors(u));
        }

        [Fact]
        public void FunctionInsideANestedBlockEndsIt()
        {
            // r2_grammar nest2: in a nested block (not as its first statement) E2076 ends the block
            // and the statement around it reports E2034 at the token after FUNCTION, which stays
            string src =
                "FUNCTION F1()\n  IF a THEN\n    WHILE b DO\nPUBLIC STRING FUNCTION F2()\n"
                + "  RETURN \"x\";\nEND\n"
                + "INT FUNCTION F3() SELECT CASE 1 CASE 2 INT FUNCTION F4() RETURN 1 END\n"
                + "INT x = 1\nINT\nFUNCTION F5() END\n";
            Assert.Equal(
                new[]
                {
                    ("E2041", 4),
                    ("E2041", 4),
                    ("E2076", 4),
                    ("E2034", 4),
                    ("E2076", 7),
                    ("E2041", 7),
                    ("E2076", 7),
                    ("E2034", 7),
                },
                Lines(One(src))
            );
            // as the first statement of the block it is a stray token (syntax h24)
            var u = One("FUNCTION F()\n\tINT x = 1;\n\tIF x THEN\n\t\tFUNCTION G()\n\t\tEND\n\tEND\nEND\n");
            Assert.Equal(new[] { ("E2041", 4), ("E2031", 7) }, Lines(u));
        }

        [Fact]
        public void ArraySizeThatIsAName()
        {
            // fix_diagB m11 / syntax d20 without the label: E2006 at the name only
            Assert.Equal(new[] { ("E2006", 1) }, Lines(One("MODULE INT a[NSIZE];\nFUNCTION Z()\nEND\n")));
        }

        [Fact]
        public void VoidOperandKeepsTheValueVoid()
        {
            // P_k04 without its label: E2031 twice, E2024 once at the ';', the RETURN's E2037 there,
            // and E2037 at the END (no value returned)
            var u = One("INT FUNCTION ProbeK()\n\tRETURN PROBE_INC(1) + PROBE_INC(1, 2);\nEND\n\n");
            Assert.Equal(new[] { "E2031", "E2031", "E2024", "E2037", "E2037" }, View(u));
        }

        [Fact]
        public void RadixRescanNeverOverflows()
        {
            // r2_grammar big.ci: '4294967296x' / '99999999999o' give no E2058
            Assert.DoesNotContain(
                "E2058",
                View(One("FUNCTION F()\n\tv = 4294967296x + 99999999999o;\nEND\n"))
            );
        }

        [Fact]
        public void ArityErrorDropsTheFilesLateMessages()
        {
            // fix_builtins review v02: 'LONG h;' (LONG is no type: two tags) above an E2022 -- the
            // compiler reports the E2022 alone
            Assert.Equal(
                new[] { "E2022" },
                View(One("FUNCTION P()\n\tLONG h;\n\th = TaskNew(\"c\");\nEND\n"))
            );
        }

        [Fact]
        public void DeclarationErrorDropsLateMessagesBelowIt()
        {
            // rev_providers n02: no W1021 for 'ZzG(1)' below the E2021; fix_diagA s10: the W1021 above
            // the E2021 stay
            var u = One(
                "INT ZzG;\nINT FUNCTION ZzG(INT a)\n\tRETURN a;\nEND\nINT FUNCTION ZzU()\n\tRETURN ZzG(1);\nEND\n"
            );
            Assert.Equal(new[] { ("E2021", 2) }, Lines(u));
            u = One(
                "GLOBAL INT ZzK;\nFUNCTION A()\n\tZzK(1);\nEND\nINT FUNCTION ZzK(INT a)\n\tRETURN a;\nEND\n"
            );
            Assert.Contains("W1021", View(u));
            Assert.Contains("E2021", View(u));
        }

        [Fact]
        public void BuiltinNameAsCallbackIsATag()
        {
            // r2_diagB b06 / b07 / b39: W1007 and E2024
            Assert.Equal(
                new[] { "W1007", "E2024" },
                View(One("FUNCTION Z()\n\tFormButton(1, 1, \"x\", PageNext, 0);\nEND\n"))
            );
        }

        [Fact]
        public void ALongOperatorChainResolvesWithoutRecursion()
        {
            // a left-nested chain is as deep as it is long: the resolver walks the left spine in a
            // loop; the type of every link is still set
            Assert.Empty(
                View(
                    One(
                        "INT FUNCTION P()\n\tRETURN "
                            + string.Join(" + ", Enumerable.Repeat("1", 200_000))
                            + ";\nEND\n"
                    )
                )
            );
            Assert.Equal(
                new[] { "E2024" },
                View(
                    One(
                        "STRING FUNCTION P()\n\tRETURN "
                            + string.Join(" + ", Enumerable.Repeat("\"a\"", 500))
                            + " + 1;\nEND\n"
                    )
                )
            );
        }

        [Fact]
        public void AMixedLeftSpineResolvesWithoutRecursion()
        {
            // 'b * b:1 * b:1 ...' nests Binary and Format nodes alternately to the left (and a page
            // value 'x * x^(0,100) * ...' Binary and Range nodes): as deep as the source is long
            const int n = 50_000;
            var u = One(
                "STRING FUNCTION P()\n\tINT b;\n\tRETURN b"
                    + string.Concat(Enumerable.Repeat(" * b:1", n))
                    + ";\nEND\n"
            );
            // every '*' of a STRING (the format before it) is E2024; the chain's type is the last format's
            Assert.Equal(Enumerable.Repeat("E2024", n - 1), View(u));
            var ret = u.Files[0].Module.Items.OfType<FunctionDef>().Single().Body.OfType<Return>().Single();
            Assert.Equal(Types.String, ret.Value!.Ty);
            Expr e = ret.Value;
            int formats = 0;
            while (true)
            {
                Assert.NotNull(e.Ty); // every link typed
                if (e is Format fm)
                {
                    formats++;
                    Assert.Equal(Types.String, fm.Ty);
                    e = fm.Operand;
                }
                else if (e is Binary bi)
                    e = bi.Left;
                else
                    break;
            }
            Assert.Equal(n, formats);
            // void operands: E2083 at each ':' in source order, the format stays void
            var v = One(
                "FUNCTION Q()\nEND\nFUNCTION P()\n\tQ()"
                    + string.Concat(Enumerable.Repeat(":1 * 1", 3))
                    + ";\nEND\n"
            );
            Assert.Equal(new[] { "E2083", "E2024", "E2083", "E2024", "E2083", "E2024" }, View(v));
            // page values: Binary and Range, Format and Range
            var f = FieldUnit();
            Assert.Empty(Field(f, "ZzTag" + string.Concat(Enumerable.Repeat(" * ZzTag^(0,100)", n))));
            Assert.Empty(Field(f, "ZzTag" + string.Concat(Enumerable.Repeat(":1^(0,100)", n))));
            var res = f.CompileField(
                "ZzTag" + string.Concat(Enumerable.Repeat(" * ZzTag^(0,100)", n)),
                "expression",
                "Main"
            );
            Assert.Equal(Types.Real, ((Expr)res.Node!).Ty);
            // a "bad" format ends the spine: its operand is not looked at (syntax m20)
            Assert.Equal(
                new[] { "E2035" },
                Field(f, "NoSuchTag * 2:@" + string.Concat(Enumerable.Repeat(" * ZzTag^(0,100)", n)))
            );
        }

        // ------------------------------------------------------------------------- page fields
        private static CompileUnit FieldUnit()
        {
            string src =
                "GLOBAL INT gVal = 1;\nMODULE INT mVal = 2;\nINT FUNCTION Pub(INT a)\n\tRETURN a;\nEND\n"
                + "PRIVATE INT FUNCTION Priv()\n\tRETURN 1;\nEND\n";
            return Unit(
                (
                    "Main",
                    Proj(
                        Files(("main.ci", src)),
                        labels: new[] { ("IFDEF(sTag,sTrue,sFalse)", "$9") },
                        tags: new Dictionary<string, int> { ["zztag"] = Types.Real, ["!zzalarm"] = -1 }
                    )
                )
            );
        }

        private static List<string> Field(CompileUnit u, string text, string kind = "expression") =>
            u.CompileField(text, kind, "Main", "pgdynobj.dbf:1:VISIBLE")
                .Diagnostics.Select(d => d.Name)
                .ToList();

        [Fact]
        public void PageFieldsSeeTheUnitLikeTheCompiler()
        {
            // what CtCmp32 reports for page fields (Example / Test errlog.DBF): an unknown tag W1007,
            // an unknown function E2031 + E2024 where its value is used
            var u = FieldUnit();
            Assert.Empty(Field(u, "ZzTag > 1 AND gVal = 1"));
            Assert.Empty(Field(u, "Pub(1) + 2"));
            Assert.Equal(new[] { "W1007" }, Field(u, "NoSuchTag = 0"));
            Assert.Equal(new[] { "W1007" }, Field(u, "mVal")); // a MODULE variable is file-local
            Assert.Equal(new[] { "E2031", "E2024" }, Field(u, "Priv()")); // so is a PRIVATE function
            Assert.Equal(new[] { "E2031", "E2024" }, Field(u, "MarkIsActive(\"x\")"));
            Assert.Equal(new[] { "W1007" }, Field(u, "(NoSuchTag)^(0.0,100)")); // a range keeps its operand checked
            Assert.Empty(Field(u, "ZzAlarm.On")); // an alarm tag's field (tsana.DBF)
            Assert.Equal(new[] { "W1007" }, Field(u, "ZzAlarm"));
            Assert.Equal(new[] { "W1007", "E2031", "E2024" }, Field(u, "X = GetSum(\"a\", 1);", "command"));
            Assert.Empty(Field(u, "?Tag? = 1; ?Tag?.Field = ZzTag", "command"));
            Assert.Empty(Field(u, "CiVBA\r\nCiVBA_AN545.Value = Temperature", "command"));
            Assert.Empty(Field(u, "IFDEF(ZzTag, Pub(1), Pub(2))", "command"));
            // the picked text decides the W1021 (builtins_editor g ok_13 'IFDEF(1, 1, 1);')
            Assert.Equal(new[] { "W1021" }, Field(u, "IFDEF(NoSuchTag, Pub(1), 5)", "command"));
            Assert.Equal(new[] { "W1021" }, Field(u, "IFDEF(ZzTag, , Pub(1))", "command")); // the tag's value
            Assert.Empty(Field(u, "IFDEF(ZzTag, gVal = 2, 5)", "command"));
            Assert.Equal(new[] { "W1021" }, Field(u, "Pub(1); 5", "command"));
            var res = u.CompileField("ZzTag * 2", "expression", "Main");
            Assert.Equal(Types.Real, ((Expr)res.Node!).Ty);
            Assert.Empty(res.Errors);
        }

        /// <summary>TagStartDigit=1 makes digit-led words tag names.</summary>
        [Fact]
        public void TagStartDigitMakesDigitLedWordsTagNames()
        {
            var u = FieldUnit();
            string[] names =
            {
                "432_131_XF504_stsAlarm",
                "1abc",
                "12_",
                "07_T",
                "1_000",
                "1e5",
                "1E5x",
                "1e",
                "1_e5",
                "0X10",
                "432_131_XF504_stsAlarm AND 1",
            };
            string[] numbers = { "123", "12.5", "1.5e2", "0x1F", "0b101", "1e5 + 1" };
            u.TagStartDigit = false;
            // (without it '1e5', '1e' and '0X10' are numbers: CtCmp32 compiled '1e5' and '1e')
            foreach (string w in names.Where(x => x != "1e5" && x != "1e" && x != "0X10"))
                Assert.Contains(Field(u, w), d => d.StartsWith("E", StringComparison.Ordinal));
            Assert.Empty(Field(u, "1e5"));
            u.TagStartDigit = true;
            foreach (string w in names)
                Assert.Equal(new[] { "W1007" }, Field(u, w).Distinct().ToArray());
            foreach (string w in numbers.Take(5))
                Assert.Empty(Field(u, w));
            Assert.Equal(new[] { "W1007" }, Field(u, numbers[5])); // '1e5' is a tag there
            Assert.Equal(new[] { "W1007" }, Field(u, "2 + 432_a"));
        }
    }

    /// <summary>A fact over the R2 install's compiled ExampleSA (skipped without it, or with
    /// CTG_SKIP_HEAVY=1; CICODESIM_CICODE_USER names another User folder).</summary>
    public sealed class FuncsymFactAttribute : FactAttribute
    {
        public FuncsymFactAttribute()
        {
            if (!UnitTests.Heavy())
                Skip = "no compiled R2 ExampleSA (or CTG_SKIP_HEAVY=1)";
        }
    }
}
