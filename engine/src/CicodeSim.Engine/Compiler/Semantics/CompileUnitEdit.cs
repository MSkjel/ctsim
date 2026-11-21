using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CicodeSim.Compiler.Syntax;
using CicodeSim.Project;

namespace CicodeSim.Compiler.Semantics
{
    /// <summary>The outcome of <see cref="CompileUnit.ReplaceFile"/>.</summary>
    public sealed class FileEdit
    {
        public string Path = "";

        /// <summary>The file was parsed and resolved again on its own; false = its declarations
        /// changed (or it is not in the unit): the unit must be compiled again (<see cref="Reason"/>).</summary>
        public bool Incremental;
        public string Reason = "";
        public int Index = -1;
        public SourceFile? File;

        /// <summary>The file's errors (both passes), in the compiler's order.</summary>
        public List<Diagnostic> Errors = new List<Diagnostic>();
    }

    public sealed partial class CompileUnit
    {
        /// <summary>Recompile one edited file of the unit; the rest stays.</summary>
        public FileEdit ReplaceFile(string path, string? text = null)
        {
            var res = new FileEdit { Path = path };
            string key = WinPath.Key(path);
            int fi = -1;
            for (int i = 0; i < Files.Count; i++)
                if (WinPath.Key(Files[i].Path) == key || Files[i].Path == path)
                {
                    fi = i;
                    break;
                }
            if (fi < 0)
            {
                res.Reason = "not a file of the unit";
                return res;
            }
            var old = Files[fi];
            string src;
            try
            {
                src = text ?? old.Project.Text(old.Name, old.Path);
            }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException)
            {
                res.Reason = "cannot read " + path + ": " + e.Message;
                return res;
            }
            var gkeys = new HashSet<string>(GlobalOrder, StringComparer.Ordinal);
            Module mod = ParseText(old.Project, old.Path, src, null);
            if (gkeys.Count > 0 && mod.CallNames.Overlaps(gkeys))
                mod = ParseText(old.Project, old.Path, src, gkeys);
            string was = Shape(old.Module),
                now = Shape(mod);
            if (was != now)
            {
                res.Reason = "the file's declarations changed";
                return res;
            }
            var nf = new SourceFile(old.Project, old.Name, old.Path, mod);
            var oldVars = new List<VarDecl>(old.Module.Variables);
            var newVars = new List<VarDecl>(mod.Variables);
            for (int k = 0; k < newVars.Count; k++)
            {
                var v = oldVars[k].Var;
                newVars[k].Var = v;
                if (v != null)
                    v.Decl = newVars[k];
            }
            foreach (var kv in old.ModuleVars)
                nf.ModuleVars[kv.Key] = kv.Value;
            var oldFns = new List<FunctionDef>(old.Module.Functions);
            var newFns = new List<FunctionDef>(mod.Functions);
            var infoOf = new Dictionary<FunctionDef, FuncInfo>();
            foreach (var f in Functions)
                infoOf[f.Node] = f;
            foreach (var f in Private.Values)
                if (!infoOf.ContainsKey(f.Node))
                    infoOf[f.Node] = f;
            for (int k = 0; k < newFns.Count; k++)
                if (infoOf.TryGetValue(oldFns[k], out var info))
                    info.Node = newFns[k];
            Files[fi] = nf;
            // the declaration pass's own messages of the file (E2021 ...) are not computed again:
            // the shape is unchanged, so the same ones hold (they are kept, positions as before)
            for (int i = old.Module.DeclDiagnostics.Count; i < old.Pass1.Count; i++)
                nf.Pass1.Add(old.Pass1[i]);
            Resolver.Resolve(this, nf, fi);
            res.Incremental = true;
            res.Index = fi;
            res.File = nf;
            foreach (var d in nf.SortedPass(1))
                if (d.IsError)
                    res.Errors.Add(d);
            foreach (var d in nf.SortedPass(2))
                if (d.IsError)
                    res.Errors.Add(d);
            return res;
        }

        private Module ParseText(SourceProject p, string path, string text, IEnumerable<string>? known)
        {
            var lex = Lexer.Tokenize(text, Labels, path, "ci", p.Name, TagStartDigit);
            var parser = new Parser(lex, path, p.Name, knownVars: known) { Callbacks = CallbackArgs() };
            return parser.ParseModule();
        }

        /// <summary>What other files see of a file: its variables and function headers in order.</summary>
        internal static string Shape(Module m)
        {
            var sb = new StringBuilder();
            foreach (var it in m.Items)
            {
                if (it is VarDecl v)
                {
                    sb.Append("V|")
                        .Append(v.Scope ?? "")
                        .Append('|')
                        .Append(v.Type)
                        .Append('|')
                        .Append(v.Name)
                        .Append('|');
                    foreach (long d in v.Dims)
                        sb.Append(d.ToString(CultureInfo.InvariantCulture)).Append(',');
                    sb.Append('|').Append(v.Init.Count > 0 ? '1' : '0').Append('\n');
                }
                else if (it is FunctionDef f)
                {
                    sb.Append("F|")
                        .Append(f.Scope ?? "")
                        .Append('|')
                        .Append(f.RetType ?? "")
                        .Append('|')
                        .Append(f.Name)
                        .Append('|');
                    foreach (var p in f.Params)
                        sb.Append(p.Type ?? "?").Append(p.Default != null ? "=" : "").Append(',');
                    sb.Append('\n');
                }
            }
            return sb.ToString();
        }
    }
}
