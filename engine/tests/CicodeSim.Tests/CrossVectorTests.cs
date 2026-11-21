using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CicodeSim.Compiler.Syntax;
using CicodeSim.Project;
using CicodeSim.Values;
using Xunit;

namespace CicodeSim.Tests
{
    /// <summary>Vectors shared with ctgedit.</summary>
    public class CrossVectorTests
    {
        private static string Shared([CallerFilePath] string here = "") =>
            Path.GetFullPath(
                Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "conformance", "shared")
            );

        private static JsonValue Load(string name) =>
            JsonValue.Parse(File.ReadAllText(Path.Combine(Shared(), name), Encoding.UTF8));

        [Fact]
        public void TheNameCharactersAreTheVectorsOnes()
        {
            var bad = new List<string>();
            foreach (var row in Load("cicode_names.json")["bytes"].Items)
            {
                int b = (int)row["byte"].ToInteger();
                char ch = TextCodec.Decode1252[b];
                if (Lexer.IsNameStart(ch) != row["start"].Bool)
                    bad.Add($"0x{b:X2} start");
                if (Lexer.IsNameChar(ch) != row["part"].Bool)
                    bad.Add($"0x{b:X2} part");
            }
            Assert.Empty(bad);
        }

        /// <summary>sha1 of MASTER.DBF and every include.dbf (the vector's 'inputs').</summary>
        private static string ProjectInputs(string user)
        {
            var files = new List<string> { Path.Combine(user, "MASTER.DBF") };
            var dirs = new List<string>(Directory.GetDirectories(user));
            dirs.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));
            foreach (string d in dirs)
            foreach (string f in Directory.GetFiles(d))
                if (string.Equals(Path.GetFileName(f), "include.dbf", StringComparison.OrdinalIgnoreCase))
                    files.Add(f);
            using var sha = SHA1.Create();
            using var ms = new MemoryStream();
            foreach (string f in files)
            {
                byte[] rel = Encoding.UTF8.GetBytes(f.Substring(user.Length + 1).ToLowerInvariant());
                ms.Write(rel, 0, rel.Length);
                byte[] data = File.ReadAllBytes(f);
                ms.Write(data, 0, data.Length);
            }
            return BitConverter.ToString(sha.ComputeHash(ms.ToArray())).Replace("-", "").ToLowerInvariant();
        }

        [Fact]
        public void TheTagScopeOrderIsTheVectorsOne()
        {
            var v = Load("tagscope.json");
            string user = v["user"].Str;
            if (!Directory.Exists(user))
                return; // no corpus here
            if (ProjectInputs(user) != v["inputs"].Str)
                return; // the project tree changed since the vectors: regenerate them (ctgedit tools/cross_vectors.py)
            var bad = new List<string>();
            foreach (var c in v["cases"].Items)
            {
                string project = c["project"].Str,
                    root = c["root"].Str;
                var want = new List<string>();
                foreach (var p in c["projects"].Items)
                    want.Add(p.Items[0].Str + ":" + p.Items[1].Str);
                var got = new List<string>();
                foreach (var p in TagScope.For(Path.Combine(user, project), root).Projects)
                    got.Add(p.Name + ":" + p.Relation);
                if (
                    !string.Equals(
                        string.Join(",", want),
                        string.Join(",", got),
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    bad.Add(
                        $"{project} as {root}:\n  vectors {string.Join(", ", want)}\n  engine  {string.Join(", ", got)}"
                    );
            }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }
    }
}
