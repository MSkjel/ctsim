using System;
using System.Collections.Generic;
using System.IO;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Compiler.Codegen;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Tests.Golden;
using CicodeSim.Values;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Compiler
{
    /// <summary>One edited file recompiles alone against the rest of the unit (the same
    /// result as compiling the whole unit with that text), and a mixed program runs the edited
    /// functions from source over a compiled library.</summary>
    public class CodegenIncrementalTests
    {
        private static List<KeyValuePair<string, string>> Texts(string a, string b) =>
            new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("a.ci", a),
                new KeyValuePair<string, string>("b.ci", b),
            };

        private const string A0 =
            "MODULE INT mCount = 3;\r\nGLOBAL STRING gName = \"x\";\r\nINT FUNCTION F(INT n = 2)\r\n\tmCount = mCount + n;\r\n\tRETURN mCount;\r\nEND\r\n";
        private const string A1 =
            "MODULE INT mCount = 3;\r\nGLOBAL STRING gName = \"x\";\r\nINT FUNCTION F(INT n = 2)\r\n\tmCount = mCount + n * 10;\r\n\tgName = \"edited\";\r\n\tRETURN mCount;\r\nEND\r\n";
        private const string B = "STRING FUNCTION G()\r\n\tRETURN F(1):# + gName;\r\nEND\r\n";

        private static string Run(Program prog, string fn)
        {
            var vm = new CicodeVm(
                prog,
                CicodeSim.Builtins.Registry.Default(),
                null,
                new ManualClock(tickMs: 1.0),
                null,
                new CodeConfig { MaxSliceOps = 20000 }
            );
            var t = vm.TaskForCall(fn, Array.Empty<Value>());
            for (int n = 0; n < 20 && !t.Done; n++)
                vm.RunSlice(t);
            return t.Result != null
                ? (
                    t.Result.Value.S
                    ?? t.Result.Value.I.ToString(System.Globalization.CultureInfo.InvariantCulture)
                )
                : "(none " + t.HaltReason + ")";
        }

        [Fact]
        public void AnEditedFileCompilesAloneAsTheWholeUnitWould()
        {
            var lib = SourceLibrary.Of(() => Units.ForTexts("P", Texts(A0, B)));
            Assert.Empty(lib.Errors());
            Assert.Equal("4x", Run(lib.Image().ToProgram(), "G"));
            // the virtual project's text changes: a body edit, the declarations keep their shape
            var e = lib.Edit("a.ci", A1);
            Assert.True(e.Incremental, e.Reason);
            byte[] incremental = RdbWriter.Write(lib.Image(), RdbWriter.Stamp.Of(new byte[0]));
            var fresh = SourceLibrary.Of(() => Units.ForTexts("P", Texts(A1, B)));
            byte[] whole = RdbWriter.Write(fresh.Image(), RdbWriter.Stamp.Of(new byte[0]));
            Assert.Equal(whole, incremental);
            Assert.Equal("13edited", Run(lib.Image().ToProgram(), "G"));
            // a new function changes the file's shape: the whole unit again
            var e2 = lib.Edit("a.ci", A1 + "INT FUNCTION H()\r\n\tRETURN 7;\r\nEND\r\n");
            Assert.False(e2.Incremental);
            Assert.Equal("7", Run(lib.Image().ToProgram(), "H"));
        }

        [Fact]
        public void AMixedProgramRunsTheEditedFileOverACompiledLibrary()
        {
            var lib = SourceLibrary.Of(() => Units.ForTexts("P", Texts(A0, B)));
            var compiled = lib.Image().ToProgram("compiled");
            lib.Edit("a.ci", A1);
            var mixed = lib.Link(compiled, new[] { "a.ci" });
            Assert.Equal("13edited", Run(mixed, "G"));
            // the base program is not changed
            Assert.Equal("4x", Run(compiled, "G"));
            Assert.Equal(compiled.Functions.Count, mixed.Functions.Count);
        }

        /// <summary>A real unit: Mixing.ci of ExampleSA edited (a string changed) compiles alone to
        /// the same library as the whole unit compiled with that text, and to CtCmp32's RDB again
        /// when the text goes back.</summary>
        [GoldenFact]
        [Trait("Golden", "light")]
        public void ExampleSaEditedFileMatchesAFullCompile()
        {
            string file = Path.Combine(GoldenFiles.User, "ExampleSA", "Mixing.ci");
            string text = CicodeSim.Compiler.Syntax.Lexer.SourceText(File.ReadAllBytes(file));
            int q = text.IndexOf("\"", StringComparison.Ordinal);
            Assert.True(q > 0);
            string edited = text.Substring(0, q + 1) + "Zz" + text.Substring(q + 1);
            var lib = SourceLibrary.Open("ExampleSA", GoldenFiles.User);
            var e = lib.Edit(file, edited);
            Assert.True(e.Incremental, e.Reason);
            var fresh = SourceLibrary.Open(
                "ExampleSA",
                GoldenFiles.User,
                new Dictionary<string, string> { [file] = edited }
            );
            var stamp = RdbWriter.Stamp.Of(
                File.ReadAllBytes(Path.Combine(GoldenFiles.User, "ExampleSA", "_LIBRARY.RDB"))
            );
            Assert.Equal(RdbWriter.Write(fresh.Image(), stamp), RdbWriter.Write(lib.Image(), stamp));
            lib.Edit(file, null);
            var rep = LibraryCheck.Compare(
                lib.Image(),
                Path.Combine(GoldenFiles.User, "ExampleSA", "_LIBRARY.RDB")
            );
            Assert.True(rep.FileSame, rep.FileDetail);
        }

        /// <summary>Edited files compile alone to the same bytes as a whole-unit compile.</summary>
        internal static (int compared, List<string> bad) EditEveryFile(string project, int step)
        {
            var lib = SourceLibrary.Open(project, GoldenFiles.User);
            Assert.Empty(lib.Errors());
            var stamp = RdbWriter.Stamp.Of(new byte[0]);
            var files = new List<SourceFile>(lib.Unit.Files);
            int compared = 0;
            var bad = new List<string>();
            for (int i = 0; i < files.Count; i += step)
            {
                var f = files[i];
                string text = f.Project.Text(f.Name, f.Path);
                for (int mut = 1; mut <= 2; mut++)
                {
                    string edited;
                    if (mut == 1)
                        edited = "\r\n" + text;
                    else
                    {
                        int end = text.LastIndexOf("\nEND", StringComparison.OrdinalIgnoreCase);
                        if (end < 0)
                            continue;
                        string stmt = "\tSleep(0);\r\n";
                        foreach (var v in f.Module.Variables)
                            if (v.Dims.Count == 0)
                            {
                                stmt = "\t" + v.Name + " = " + v.Name + ";\r\n" + stmt;
                                break;
                            }
                        edited = text.Substring(0, end + 1) + stmt + text.Substring(end + 1);
                    }
                    var e = lib.Edit(f.Path, edited);
                    var fresh = SourceLibrary.Open(
                        project,
                        GoldenFiles.User,
                        new Dictionary<string, string> { [f.Path] = edited }
                    );
                    if (fresh.Errors().Count == 0 && e.Errors.Count == 0)
                    {
                        compared++;
                        if (!e.Incremental)
                            bad.Add(f.Name + " edit " + mut + ": not incremental (" + e.Reason + ")");
                        else if (
                            !Same(RdbWriter.Write(lib.Image(), stamp), RdbWriter.Write(fresh.Image(), stamp))
                        )
                            bad.Add(f.Name + " edit " + mut);
                    }
                    lib.Edit(f.Path, null);
                }
            }
            return (compared, bad);
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        [GoldenFact]
        [Trait("Golden", "light")]
        public void EditedFilesCompileAloneAsTheWholeUnitWould()
        {
            var (n, bad) = EditEveryFile("ExampleSA", 15);
            Assert.True(n >= 10, "edits compared: " + n);
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }
    }
}
