using System;
using System.Collections.Generic;
using System.Text;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Project;

namespace CicodeSim.Compiler.Syntax
{
    /// <summary>One labels.DBF row: a compile-time macro.</summary>
    public sealed class Label
    {
        public readonly string Name;
        public readonly string Key;
        public readonly List<string> Params;

        /// <summary>Per parameter the default text, null = required.</summary>
        public readonly List<string?> Defaults;
        public readonly string Body;
        public readonly bool FuncLike;
        public readonly string Project;
        public readonly int Row;

        /// <summary>n for a '$n' text, -1 otherwise.</summary>
        public readonly int Intrinsic;
        private readonly Dictionary<string, int> _pkeys = new Dictionary<string, int>(StringComparer.Ordinal);
        private List<Piece>? _tpl;

        private struct Piece
        {
            public int Kind; // 0 literal, 1 paste, 2 arg, 3 stringised arg
            public string Text;
            public int Index;
        }

        public Label(
            string name,
            string body,
            IList<string>? parameters = null,
            IList<string?>? defaults = null,
            bool funcLike = false,
            string project = "",
            int row = 0
        )
        {
            Name = name;
            Key = Lexer.NameKey(name);
            Params = parameters != null ? new List<string>(parameters) : new List<string>();
            if (defaults != null && defaults.Count > 0)
                Defaults = new List<string?>(defaults);
            else
            {
                Defaults = new List<string?>();
                for (int i = 0; i < Params.Count; i++)
                    Defaults.Add(null);
            }
            Body = body;
            FuncLike = funcLike;
            Project = project;
            Row = row;
            Intrinsic = IntrinsicOf(body);
            for (int i = 0; i < Params.Count; i++)
                _pkeys[Lexer.NameKey(Params[i])] = i;
        }

        // ^\s*\$(\d+)\s*$
        private static int IntrinsicOf(string body)
        {
            int i = 0,
                n = body.Length;
            while (i < n && CitectText.IsWhite(body[i]))
                i++;
            if (i >= n || body[i] != '$')
                return -1;
            i++;
            int d0 = i;
            long v = 0;
            while (i < n && body[i] >= '0' && body[i] <= '9')
            {
                if (v < int.MaxValue)
                    v = v * 10 + (body[i] - '0');
                i++;
            }
            if (i == d0)
                return -1;
            while (i < n && CitectText.IsWhite(body[i]))
                i++;
            if (i != n)
                return -1;
            return v > int.MaxValue ? int.MaxValue : (int)v;
        }

        /// <summary>A label from a labels.DBF row: NAME 'X' or 'X(a, b=1)', EXPR the text.</summary>
        public static Label FromRow(string nameCol, string expr, string project = "", int row = 0)
        {
            nameCol = CitectText.Trim(nameCol);
            int k = nameCol.IndexOf('(');
            if (k < 0)
                return new Label(nameCol, expr, project: project, row: row);
            string name = CitectText.Trim(nameCol.Substring(0, k));
            string inner = nameCol.Substring(k + 1);
            string rs = CitectText.TrimEnd(inner);
            if (rs.EndsWith(")", StringComparison.Ordinal))
                inner = rs.Substring(0, rs.Length - 1);
            var ps = new List<string>();
            var ds = new List<string?>();
            if (CitectText.Trim(inner).Length > 0)
            {
                foreach (string part in LabelTable.SplitTop(inner))
                {
                    int e = part.IndexOf('=');
                    if (e >= 0)
                    {
                        ps.Add(CitectText.Trim(part.Substring(0, e)));
                        ds.Add(CitectText.Trim(part.Substring(e + 1)));
                    }
                    else
                    {
                        ps.Add(CitectText.Trim(part));
                        ds.Add(null);
                    }
                }
            }
            return new Label(name, expr, ps, ds, true, project, row);
        }

        /// <summary>The argument texts for every parameter (defaults filled in), or null for E2057
        /// (too many, or a parameter without default missing).</summary>
        public List<string>? Bind(IList<string> args)
        {
            if (args.Count > Params.Count)
                return null;
            var o = new List<string>(args);
            for (int i = args.Count; i < Params.Count; i++)
            {
                string? d = Defaults[i];
                if (d == null)
                    return null;
                o.Add(d);
            }
            return o;
        }

        private List<Piece> Template()
        {
            if (_tpl != null)
                return _tpl;
            var pieces = new List<Piece>();
            string text = Body;
            int i = 0,
                n = text.Length;
            var buf = new StringBuilder();
            while (i < n)
            {
                char c = text[i];
                if (c == '"')
                {
                    int j = i + 1;
                    while (j < n)
                    {
                        if (text[j] == '^')
                        {
                            j += 2;
                            continue;
                        }
                        if (text[j] == '"')
                            break;
                        j++;
                    }
                    buf.Append(text, i, Math.Min(j + 1, n) - i);
                    i = j + 1;
                    continue;
                }
                if (c == '#' && i + 1 < n && text[i + 1] == '#')
                {
                    pieces.Add(new Piece { Kind = 0, Text = buf.ToString() });
                    buf.Clear();
                    pieces.Add(new Piece { Kind = 1, Text = "" });
                    i += 2;
                    continue;
                }
                if (c == '#' && i + 1 < n && Lexer.IsNameStart(text[i + 1]))
                {
                    int j = i + 1;
                    while (j < n && Lexer.IsNameChar(text[j]))
                        j++;
                    if (_pkeys.TryGetValue(Lexer.NameKey(text.Substring(i + 1, j - i - 1)), out int idx))
                    {
                        pieces.Add(new Piece { Kind = 0, Text = buf.ToString() });
                        buf.Clear();
                        pieces.Add(
                            new Piece
                            {
                                Kind = 3,
                                Text = "",
                                Index = idx,
                            }
                        );
                        i = j;
                        continue;
                    }
                }
                if (Lexer.IsNameStart(c))
                {
                    int j = i + 1;
                    while (j < n && Lexer.IsNameChar(text[j]))
                        j++;
                    string w = text.Substring(i, j - i);
                    if (_pkeys.TryGetValue(Lexer.NameKey(w), out int idx))
                    {
                        pieces.Add(new Piece { Kind = 0, Text = buf.ToString() });
                        buf.Clear();
                        pieces.Add(
                            new Piece
                            {
                                Kind = 2,
                                Text = "",
                                Index = idx,
                            }
                        );
                    }
                    else
                        buf.Append(w);
                    i = j;
                    continue;
                }
                buf.Append(c);
                i++;
            }
            pieces.Add(new Piece { Kind = 0, Text = buf.ToString() });
            _tpl = pieces;
            return pieces;
        }

        private static string Stringise(string arg) =>
            "\"" + arg.Replace("^", "^^").Replace("\"", "^\"") + "\"";

        /// <summary>The label text with the arguments put in ('#p' stringised, '##' pasted).</summary>
        public string Substitute(IList<string> actual)
        {
            if (Params.Count == 0)
                return Body;
            var output = new List<string>();
            bool paste = false;
            foreach (var p in Template())
            {
                if (p.Kind == 0)
                {
                    string s = p.Text;
                    if (paste)
                    {
                        s = CitectText.TrimStart(s);
                        paste = false;
                    }
                    output.Add(s);
                }
                else if (p.Kind == 1)
                {
                    if (output.Count > 0)
                        output[output.Count - 1] = CitectText.TrimEnd(output[output.Count - 1]);
                    paste = true;
                }
                else if (p.Kind == 2)
                {
                    output.Add(actual[p.Index]);
                    paste = false;
                }
                else
                {
                    output.Add(Stringise(actual[p.Index]));
                    paste = false;
                }
            }
            return string.Concat(output);
        }
    }

    /// <summary>The labels of one compile unit, in one case-insensitive table.</summary>
    public sealed class LabelTable
    {
        public const int MaxArgs = 32;
        public const int MaxArgText = 1024;

        /// <summary>The 18 '$n' intrinsics by the Include project's label names.</summary>
        public static readonly IReadOnlyDictionary<int, string> Intrinsics = new Dictionary<int, string>
        {
            [0] = "__TIME__",
            [1] = "__DATE__",
            [2] = "__FILE__",
            [3] = "__LINE__",
            [4] = "__DB__",
            [5] = "__RECORD__",
            [6] = "__FIELD__",
            [7] = "GetVar",
            [8] = "GetVarStr",
            [9] = "IFDEF",
            [10] = "GetVarDef",
            [11] = "GetVarStrDef",
            [12] = "IFDEFDIGALM",
            [13] = "IFDEFADVALM",
            [14] = "IFDEFANAALM",
            [15] = "VarToStr",
            [16] = "VarToArrayIndex",
            [17] = "IFDEFHRESALM",
        };

        private readonly Dictionary<string, Label> _labels = new Dictionary<string, Label>(
            StringComparer.Ordinal
        );
        private readonly List<Label> _order = new List<Label>();
        public readonly List<Diagnostic> Diagnostics = new List<Diagnostic>();
        private string? _signature;

        private readonly Dictionary<string, Label> _folded = new Dictionary<string, Label>(
            AsciiFold.Instance
        );

        public Label? Get(string key) => _labels.TryGetValue(key, out var l) ? l : null;

        /// <summary>The label of a name as written (looked up by its name key).</summary>
        public Label? GetFolded(string name) => _folded.TryGetValue(name, out var l) ? l : null;

        public bool Contains(string key) => _labels.ContainsKey(key);

        public int Count => _labels.Count;

        public IEnumerable<Label> All => _order;

        /// <summary>A text standing for every label (name, parameters, defaults, text): two tables
        /// with the same signature expand any source the same way.</summary>
        public string Signature()
        {
            if (_signature != null)
                return _signature;
            var sb = new StringBuilder();
            foreach (var lb in _order)
            {
                sb.Append(lb.Key).Append('\u0001');
                foreach (string p in lb.Params)
                    sb.Append(p).Append('\u0002');
                sb.Append('\u0001');
                foreach (string? d in lb.Defaults)
                    sb.Append(d == null ? "\u0003" : d + "\u0002");
                sb.Append('\u0001')
                    .Append(lb.Body)
                    .Append('\u0001')
                    .Append(lb.FuncLike ? '1' : '0')
                    .Append('\u0004');
            }
            _signature = sb.ToString();
            return _signature;
        }

        /// <summary>Add a label; a name already defined is E2038 (reported at <paramref name="file"/>,
        /// line 1 whatever the row) and the earlier definition stays
        /// (q7_04 .. q7_16).</summary>
        public bool Add(Label label, string file = "")
        {
            if (label.Key.Length == 0)
                return false;
            if (_labels.ContainsKey(label.Key))
            {
                Diagnostics.Add(new Diagnostic(2038, file, 1, 0, label.Name, label.Project));
                return false;
            }
            _labels[label.Key] = label;
            _folded[label.Key] = label;
            _order.Add(label);
            _signature = null;
            return true;
        }

        /// <summary>Add labels.DBF rows (NAME, EXPR) in row order; blank names are skipped.</summary>
        public void AddRows(
            IEnumerable<KeyValuePair<string, string>> rows,
            string project = "",
            string file = ""
        )
        {
            int rowNo = 0;
            foreach (var r in rows)
            {
                rowNo++;
                string name = CitectText.Trim(r.Key ?? "");
                if (name.Length == 0)
                    continue;
                Add(Label.FromRow(name, r.Value ?? "", project, rowNo), file);
            }
        }

        /// <summary>Split at commas outside parentheses / brackets and strings ('^' escapes).</summary>
        public static List<string> SplitTop(string text)
        {
            var parts = new List<string>();
            int depth = 0;
            var cur = new StringBuilder();
            int i = 0,
                n = text.Length;
            while (i < n)
            {
                char c = text[i];
                if (c == '"')
                {
                    int j = i + 1;
                    while (j < n)
                    {
                        if (text[j] == '^')
                        {
                            j += 2;
                            continue;
                        }
                        if (text[j] == '"')
                            break;
                        j++;
                    }
                    cur.Append(text, i, Math.Min(j + 1, n) - i);
                    i = j + 1;
                    continue;
                }
                if (c == '(' || c == '[')
                    depth++;
                else if (c == ')' || c == ']')
                    depth--;
                else if (c == ',' && depth == 0)
                {
                    parts.Add(cur.ToString());
                    cur.Clear();
                    i++;
                    continue;
                }
                cur.Append(c);
                i++;
            }
            parts.Add(cur.ToString());
            return parts;
        }

        /// <summary>Read the '(args)' after a label name.</summary>
        public static List<string>? ReadLabelArgs(string text, int j, int n, out int end, out bool error)
        {
            int k = j;
            while (k < n && Lexer.IsWs(text[k]))
                k++;
            if (k >= n || text[k] != '(')
            {
                end = j;
                error = false;
                return null;
            }
            int depth = 0;
            int i = k;
            while (i < n)
            {
                char c = text[i];
                if (c == '"')
                {
                    i++;
                    while (i < n)
                    {
                        if (text[i] == '^')
                        {
                            i += 2;
                            continue;
                        }
                        if (text[i] == '"')
                            break;
                        i++;
                    }
                }
                else if (c == '(')
                    depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                        break;
                }
                i++;
            }
            if (i >= n)
            {
                end = n;
                error = true;
                return null;
            }
            string inner = text.Substring(k + 1, i - k - 1);
            if (inner.Length == 0)
            {
                end = i + 1;
                error = false;
                return new List<string>();
            }
            if (inner.Length >= MaxArgText)
            {
                end = i + 1;
                error = true;
                return null;
            }
            var parts = SplitTop(inner);
            var args = new List<string>(parts.Count);
            foreach (string a in parts)
                args.Add(CitectText.Trim(a));
            if (args.Count > MaxArgs)
            {
                end = i + 1;
                error = true;
                return null;
            }
            end = i + 1;
            error = false;
            return args;
        }

        /// <summary>The project's labels.DBF (any case), or null.</summary>
        public static string? LabelsFile(string projectDir)
        {
            var names = WinPath.ListDir(projectDir);
            if (names == null)
                return null;
            foreach (string fn in names)
                if (CitectText.Lower(fn) == "labels.dbf")
                    return WinPath.Join(projectDir, fn);
            return null;
        }

        /// <summary>The live rows of a labels.DBF (NAME / EXPR as stored, values stripped).</summary>
        public static List<KeyValuePair<string, string>> ReadLabelRows(string path)
        {
            var o = new List<KeyValuePair<string, string>>();
            try
            {
                foreach (var r in Dbf.CitectRows(path, new[] { "NAME", "EXPR" }))
                    o.Add(new KeyValuePair<string, string>(r[0], r[1]));
            }
            catch (Exception)
            {
                o.Clear(); // an unreadable table = no labels
            }
            return o;
        }
    }
}
