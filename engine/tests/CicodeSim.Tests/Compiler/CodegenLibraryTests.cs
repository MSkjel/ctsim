using System;
using System.Collections.Generic;
using System.IO;
using CicodeSim.Compiler.Codegen;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Tests.Golden;
using Xunit;

namespace CicodeSim.Tests.Compiler
{
    /// <summary>The source compiler against CtCmp32 on the libraries compiled here.</summary>
    public class CodegenLibraryTests
    {
        [GoldenFact]
        [Trait("Golden", "light")]
        public void ExampleLibrariesAreReproducedByteForByte()
        {
            var bad = new List<string>();
            foreach (string name in new[] { "ExampleSA", "Example" })
            {
                // compiled before its tags changed: its RDB is not its source's (see below)
                if (CompiledBeforeItsTags(Path.Combine(GoldenFiles.User, name)))
                    continue;
                var unit = CompileUnit.Build(name, GoldenFiles.User);
                Assert.Empty(unit.Errors());
                var img = LibraryBuilder.Build(unit);
                string rdb = Path.Combine(GoldenFiles.User, name, "_LIBRARY.RDB");
                var rep = LibraryCheck.Compare(img, rdb, 5);
                if (img.Failed.Count > 0)
                    bad.Add(
                        name
                            + ": "
                            + img.Failed.Count
                            + " functions not compiled, first "
                            + img.Failed[0].name
                            + ": "
                            + img.Failed[0].why
                    );
                if (rep.Exact != rep.Functions || rep.CountMismatch != 0)
                {
                    bad.Add(name + ": " + rep.Summary());
                    foreach (var (i, fn, what) in rep.Mismatches)
                        bad.Add($"  #{i} {fn}: {what}");
                }
                foreach (var (sec, same, det) in rep.Sections)
                    if (!same)
                        bad.Add($"{name}: section {sec} differs: {det}");
                if (!rep.FileSame)
                    bad.Add(name + ": file " + rep.FileDetail);
                if (rep.FuncsymSame != true)
                    bad.Add(
                        name + ": _FUNCSYM.DBF " + (rep.FuncsymSame == null ? "missing" : rep.FuncsymDetail)
                    );
            }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }

        /// <summary>Every page code block compiled from its field equals the block in the .RDB.</summary>
        [GoldenFact]
        [Trait("Golden", "light")]
        public void PageBlocksAreReproduced()
        {
            var known = new Dictionary<string, (int blocks, int diff)>
            {
                ["ExampleSA"] = (29695, 0),
                ["Example"] = (48262, 0),
            };
            var bad = new List<string>();
            foreach (var kv in known)
            {
                string pdir = Path.Combine(GoldenFiles.User, kv.Key);
                if (CompiledBeforeItsTags(pdir) || CompiledBeforeItsPages(pdir))
                    continue;
                var unit = CompileUnit.Build(kv.Key, GoldenFiles.User);
                var rep = PageCheck.Run(unit, Path.Combine(GoldenFiles.User, kv.Key), 3);
                if (rep.Blocks != kv.Value.blocks || rep.Blocks - rep.Same != kv.Value.diff)
                {
                    bad.Add($"{kv.Key}: {rep.Blocks} blocks, {rep.Same} equal");
                    bad.AddRange(rep.Examples);
                }
            }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }

        /// <summary>The written file reads back as the same program (what the VM loads).</summary>
        [GoldenFact]
        [Trait("Golden", "light")]
        public void WrittenLibraryLoadsAsTheImageProgram()
        {
            var unit = CompileUnit.Build("Example", GoldenFiles.User);
            var img = LibraryBuilder.Build(unit);
            byte[] bytes = RdbWriter.Write(img, RdbWriter.Stamp.Now());
            var fromFile = CicodeSim.Compiled.Rdb.LibraryFromRdb(
                CicodeSim.Compiled.RdbFile.FromBytes(bytes, "written")
            );
            var direct = img.ToProgram();
            Assert.Equal(direct.Functions.Count, fromFile.Functions.Count);
            for (int i = 0; i < direct.Functions.Count; i++)
            {
                var a = direct.Functions[i];
                var b = fromFile.Functions[i];
                Assert.Equal(a.Name, b.Name);
                Assert.Equal(a.Args, b.Args);
                Assert.Equal(a.TypeWord, b.TypeWord);
                Assert.Equal(a.MinParam, b.MinParam);
                Assert.Equal(a.Code, b.Code);
                Assert.Equal(a.Locals, b.Locals);
            }
            Assert.Equal(direct.Text, fromFile.Text);
            Assert.Equal(direct.RunImage, fromFile.RunImage);
            Assert.Equal(direct.CodeVarOrder.Count, fromFile.CodeVarOrder.Count);
        }

        /// <summary>The live program equals CtCmp32's _LIBRARY.RDB field for field.</summary>
        [GoldenFact]
        [Trait("Golden", "light")]
        public void TheLiveProgramIsTheRdbsProgramFieldForField()
        {
            var bad = new List<string>();
            int checkedProjects = 0;
            foreach (string name in new[] { "ExampleSA", "Example" })
            {
                string dir = Path.Combine(GoldenFiles.User, name);
                // a project whose tag tables changed after its compile: its _LIBRARY.RDB is not
                // the program of its source until Studio compiles it again (the code of a tag
                // that is defined now differs: a STRING tag's StringFmt, IFDEF)
                if (CompiledBeforeItsTags(dir))
                    continue;
                checkedProjects++;
                var src = LiveProgram.Open(dir, LiveProgram.All).Program;
                var rdb = CicodeSim.Compiled.Rdb.LoadLibrary(Path.Combine(dir, "_LIBRARY.RDB"));
                var diff = new List<string>();
                if (src.Functions.Count != rdb.Functions.Count)
                    diff.Add("functions " + src.Functions.Count + " vs " + rdb.Functions.Count);
                for (int i = 0; i < src.Functions.Count && i < rdb.Functions.Count; i++)
                    SameFields(src.Functions[i], rdb.Functions[i], "function " + rdb.Functions[i].Name, diff);
                if (!Same(src.Text, rdb.Text))
                    diff.Add("TEXT");
                if (!Same(src.RunImage, rdb.RunImage))
                    diff.Add("RUN");
                SameRecords(src.CodeVars, rdb.CodeVars, "CODEVAR", diff);
                SameRecords(src.TagRefs, rdb.TagRefs, "TAGREF", diff);
                SameRecords(src.Scales, rdb.Scales, "SCALE", diff);
                SameRecords(src.FuncLookups, rdb.FuncLookups, "FUNCLOOKUP", diff);
                if (src.CodeVarOrder.Count != rdb.CodeVarOrder.Count)
                    diff.Add("CODEVAR order count");
                for (int i = 0; i < src.CodeVarOrder.Count && i < rdb.CodeVarOrder.Count; i++)
                    SameFields(src.CodeVarOrder[i], rdb.CodeVarOrder[i], "CODEVAR order " + i, diff);
                foreach (string d in diff.GetRange(0, System.Math.Min(diff.Count, 10)))
                    bad.Add(name + ": " + d);
            }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
            Assert.True(checkedProjects >= 2, "ExampleSA and Example must be compiled after their tags");
        }

        /// <summary>The project's variable.DBF or locvar.DBF is newer than its _LIBRARY.RDB.</summary>
        internal static bool CompiledBeforeItsTags(string dir)
        {
            string rdb = Path.Combine(dir, "_LIBRARY.RDB");
            if (!File.Exists(rdb))
                return false;
            var compiled = File.GetLastWriteTimeUtc(rdb);
            foreach (string t in new[] { "variable.DBF", "locvar.DBF" })
            {
                string p = Path.Combine(dir, t);
                if (File.Exists(p) && File.GetLastWriteTimeUtc(p) > compiled.AddSeconds(5))
                    return true;
            }
            return false;
        }

        /// <summary>The project's page tables (pages.DBF, pg*.DBF) were written after its newest
        /// compiled page: its page .RDBs are not its pages' (the record numbers in them shift when
        /// the tables are rewritten). Compile the project to compare it again.</summary>
        internal static bool CompiledBeforeItsPages(string dir)
        {
            if (!Directory.Exists(dir))
                return false;
            var compiled = DateTime.MinValue;
            foreach (string f in Directory.GetFiles(dir, "*.RDB"))
                if (!Path.GetFileName(f).StartsWith("_", StringComparison.Ordinal))
                {
                    var t = File.GetLastWriteTimeUtc(f);
                    if (t > compiled)
                        compiled = t;
                }
            if (compiled == DateTime.MinValue)
                return false;
            foreach (string f in Directory.GetFiles(dir, "*.DBF"))
            {
                string n = Path.GetFileName(f).ToLowerInvariant();
                if (
                    (n == "pages.dbf" || n.StartsWith("pg", StringComparison.Ordinal))
                    && File.GetLastWriteTimeUtc(f) > compiled.AddSeconds(5)
                )
                    return true;
            }
            return false;
        }

        private static bool Same(byte[] a, byte[] b) => System.Linq.Enumerable.SequenceEqual(a, b);

        private static string Text(object? v)
        {
            if (v == null)
                return "null";
            if (v is string s)
                return "'" + s + "'";
            if (v is System.Collections.IEnumerable e)
            {
                var l = new List<string>();
                foreach (var x in e)
                    l.Add(Text(x));
                return "[" + string.Join(",", l) + "]";
            }
            return System.Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }

        private static void SameFields(object a, object b, string where, List<string> diff)
        {
            foreach (
                var f in a.GetType()
                    .GetFields(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                    )
            )
            {
                string x = Text(f.GetValue(a)),
                    y = Text(f.GetValue(b));
                if (x != y)
                    diff.Add(where + " " + f.Name + ": source " + x + ", RDB " + y);
            }
        }

        private static void SameRecords<T>(
            Dictionary<long, T> src,
            Dictionary<long, T> rdb,
            string what,
            List<string> diff
        )
            where T : class
        {
            if (src.Count != rdb.Count)
                diff.Add(what + " records " + src.Count + " vs " + rdb.Count);
            foreach (var kv in rdb)
            {
                if (!src.TryGetValue(kv.Key, out var s))
                {
                    diff.Add(what + " @" + kv.Key + " missing");
                    continue;
                }
                SameFields(s, kv.Value, what + " @" + kv.Key, diff);
            }
        }
    }
}
