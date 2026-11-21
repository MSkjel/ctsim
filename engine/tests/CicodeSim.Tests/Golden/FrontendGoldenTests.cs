using System;
using System.Collections.Generic;
using System.Linq;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Compiler.Syntax;
using CicodeSim.Host;
using CicodeSim.Values;
using Xunit;
using Obj = CicodeSim.Tests.Golden.GoldenJson.Obj;

namespace CicodeSim.Tests.Golden
{
    /// <summary>The source front end, frozen.</summary>
    public class FrontendGoldenTests
    {
        private const string UnitsFile = "frontend_units.jsonl.gz";
        private const string FieldsFile = "frontend_fields.jsonl.gz";

        /// <summary>A path below the User folder without the folder; anything else as it is.</summary>
        internal static string Rel(string path)
        {
            string u = GoldenFiles.User + "\\";
            return path.StartsWith(u, StringComparison.OrdinalIgnoreCase) ? path.Substring(u.Length) : path;
        }

        internal static object?[] Diag(Diagnostic d) =>
            new object?[]
            {
                d.Code,
                Rel(d.File),
                d.Line,
                d.Col,
                d.Context,
                d.Project,
                d.Pos,
                d.Seq,
                d.Message,
            };

        /// <summary>The digest of a syntax tree: AstDump's lines (reals as their bits) in golden
        /// text.</summary>
        internal static Obj Ast(object? root, string tag)
        {
            using var d = new GoldenJson.Digest();
            foreach (string l in AstDump.Lines(root, tag, x => "r:" + GoldenJson.RealBits(x)))
                d.Add(GoldenJson.Write(JsonValue.Parse(l)));
            return new Obj().Add("n", d.Lines).Add("h", d.Hex());
        }

        private static string W(Obj o) => GoldenJson.Write(o);

        internal static List<(string, string)> UnitLines()
        {
            var got = new List<(string, string)>();
            foreach (var (p, unit) in FrontendCommands.CiUnits(GoldenFiles.User, new HashSet<string>()))
            {
                string u = p.Name;
                got.Add(
                    (
                        u + "|unit",
                        W(
                            new Obj()
                                .Add("unit", u)
                                .Add("kind", "unit")
                                .Add("projects", unit.Projects.Count)
                                .Add("files", unit.Files.Count)
                                .Add("functions", unit.Functions.Count)
                        )
                    )
                );
                var all = unit.AllDiagnostics().ToList();
                using (var order = new GoldenJson.Digest())
                {
                    foreach (var d in all)
                    {
                        string line = W(new Obj().Add("unit", u).Add("kind", "diag").Add("d", Diag(d)));
                        got.Add((u + "|diag|" + Rel(d.File), line));
                        order.Add(line);
                    }
                    got.Add(
                        (
                            u + "|diag order",
                            W(
                                new Obj()
                                    .Add("unit", u)
                                    .Add("kind", "diag order")
                                    .Add("n", order.Lines)
                                    .Add("h", order.Hex())
                            )
                        )
                    );
                }
                foreach (var d in unit.CompilerView())
                    got.Add((u + "|view", W(new Obj().Add("unit", u).Add("kind", "view").Add("d", Diag(d)))));
                using (var order = new GoldenJson.Digest())
                {
                    foreach (var row in unit.Funcsym())
                    {
                        var vals = row.Select(kv => (object?)kv.Value).ToList();
                        string name = row.Count > 0 ? row[0].Value : "";
                        string line = W(new Obj().Add("unit", u).Add("kind", "fsym").Add("row", vals));
                        got.Add((u + "|fsym|" + name, line));
                        order.Add(line);
                    }
                    got.Add(
                        (
                            u + "|fsym order",
                            W(
                                new Obj()
                                    .Add("unit", u)
                                    .Add("kind", "fsym order")
                                    .Add("n", order.Lines)
                                    .Add("h", order.Hex())
                            )
                        )
                    );
                }
                foreach (var f in unit.Files)
                    if (ReferenceEquals(f.Project, p))
                        got.Add(
                            (
                                u + "|ast|" + Rel(f.Path),
                                W(
                                    new Obj()
                                        .Add("unit", u)
                                        .Add("kind", "ast")
                                        .Add("file", Rel(f.Path))
                                        .Add("ast", Ast(f.Module, "ast"))
                                )
                            )
                        );
            }
            return got;
        }

        private static string UnitKey(string line)
        {
            var j = JsonValue.Parse(line);
            string u = j["unit"].Str,
                kind = j["kind"].Str;
            switch (kind)
            {
                case "diag":
                    return u + "|diag|" + j["d"].Items[1].Str;
                case "fsym":
                    return u + "|fsym|" + (j["row"].Count > 0 ? j["row"].Items[0].Str : "");
                case "ast":
                    return u + "|ast|" + j["file"].Str;
                default:
                    return u + "|" + kind;
            }
        }

        [GoldenFact]
        [Trait("Golden", "light")]
        public void UnitsMatchTheGolden()
        {
            var got = UnitLines();
            if (GoldenFiles.WriteIfAsked(UnitsFile, got.Select(x => x.Item2)))
                return;
            var want = GoldenFiles.ReadLines(UnitsFile).Select(l => (UnitKey(l), l)).ToList();
            GoldenFiles.AssertSame(
                "front end units (unit|unit, unit|diag|file, unit|view, unit|fsym|function, unit|ast|file)",
                want,
                got
            );
        }

        [GoldenFact]
        [Trait("Golden", "light")]
        public void PageFieldsMatchTheGolden()
        {
            var got = new List<(string, string)>();
            foreach (
                var (root, where, res) in FrontendCommands.FieldResults(GoldenFiles.User, new List<string>())
            )
            {
                var diags = res.Diagnostics.Select(d => (object?)Diag(d)).ToList();
                got.Add(
                    (
                        root + "|" + where,
                        W(
                            new Obj()
                                .Add("root", root)
                                .Add("where", where)
                                .Add("diags", diags)
                                .Add("ast", Ast(res.Node, "fast"))
                        )
                    )
                );
            }
            if (GoldenFiles.WriteIfAsked(FieldsFile, got.Select(x => x.Item2)))
                return;
            var want = GoldenFiles
                .ReadLines(FieldsFile)
                .Select(l => (GoldenRecords.KeyOf(l, "root", "where"), l))
                .ToList();
            GoldenFiles.AssertSame("page fields (project|table:record:column)", want, got);
        }
    }
}
