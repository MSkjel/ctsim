using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CicodeSim.Compiler.Syntax;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.Compiler.Semantics
{
    /// <summary>One project of a compile unit, real (a folder) or virtual (texts given in memory).</summary>
    public sealed class SourceProject
    {
        public readonly string Name;
        public readonly string? Folder;
        internal readonly List<KeyValuePair<string, string>>? Texts;
        private readonly List<KeyValuePair<string, string>>? _labels;
        private readonly List<string>? _includes;
        private readonly List<KeyValuePair<string, string>>? _locvars;
        private Dictionary<string, int>? _tags;

        public SourceProject(
            string name,
            string? folder = null,
            List<KeyValuePair<string, string>>? texts = null,
            List<KeyValuePair<string, string>>? labelRows = null,
            List<string>? includeRows = null,
            List<KeyValuePair<string, string>>? locvarRows = null,
            Dictionary<string, int>? tags = null
        )
        {
            Name = name;
            Folder = folder;
            Texts = texts;
            _labels = labelRows;
            _includes = includeRows;
            _locvars = locvarRows;
            _tags = tags;
        }

        public string Key => CitectText.Lower(CitectText.Trim(Name));

        public bool IsVirtual => Texts != null;

        /// <summary>(file name, path) of the top-level .ci files, in the compiler's order (their
        /// upper-case names).</summary>
        public List<(string name, string path)> CiFiles()
        {
            var names = new List<string>();
            if (Texts != null)
            {
                foreach (var kv in Texts)
                    names.Add(kv.Key);
            }
            else if (!string.IsNullOrEmpty(Folder))
            {
                var all = WinPath.ListDir(Folder!);
                if (all != null)
                    foreach (string fn in all)
                        if (
                            CitectText.Lower(fn).EndsWith(".ci", StringComparison.Ordinal)
                            && WinPath.IsFile(WinPath.Join(Folder!, fn))
                        )
                            names.Add(fn);
            }
            names = Unit0.StableSortBy(names, s => CitectText.Upper(s));
            var o = new List<(string, string)>(names.Count);
            foreach (string fn in names)
                o.Add((fn, !string.IsNullOrEmpty(Folder) ? WinPath.Join(Folder!, fn) : fn));
            return o;
        }

        /// <summary>Texts that replace files of the folder (an editor's unsaved buffers): the
        /// normalised path key (WinPath.Key) to the text.</summary>
        public Dictionary<string, string>? Overrides;

        public string Text(string fname, string path)
        {
            if (Overrides != null && Overrides.TryGetValue(WinPath.Key(path), out string? over))
                return over;
            if (Texts != null)
            {
                foreach (var kv in Texts)
                    if (kv.Key == fname)
                        return kv.Value;
                throw new KeyNotFoundException(fname);
            }
            return Lexer.SourceText(File.ReadAllBytes(path));
        }

        public List<KeyValuePair<string, string>> LabelRows()
        {
            if (_labels != null)
                return _labels;
            string? p = !string.IsNullOrEmpty(Folder) ? LabelTable.LabelsFile(Folder!) : null;
            return p != null ? LabelTable.ReadLabelRows(p) : new List<KeyValuePair<string, string>>();
        }

        public string LabelsPath()
        {
            if (!string.IsNullOrEmpty(Folder))
                return LabelTable.LabelsFile(Folder!) ?? WinPath.Join(Folder!, "LABELS.DBF");
            return Name + "\\LABELS.DBF";
        }

        public List<string> IncludeRows()
        {
            if (_includes != null)
                return _includes;
            string? p = !string.IsNullOrEmpty(Folder) ? Unit0.Find(Folder!, "include.dbf") : null;
            var o = new List<string>();
            foreach (var r in Unit0.Rows(p, new[] { "NAME" }))
                o.Add(r[0]);
            return o;
        }

        public List<KeyValuePair<string, string>> LocvarRows()
        {
            if (_locvars != null)
                return _locvars;
            string? p = !string.IsNullOrEmpty(Folder) ? Unit0.Find(Folder!, "locvar.dbf") : null;
            var o = new List<KeyValuePair<string, string>>();
            foreach (var r in Unit0.Rows(p, new[] { "NAME", "TYPE" }))
                o.Add(new KeyValuePair<string, string>(r[0], r[1]));
            return o;
        }

        /// <summary>Variable tag names (name key) to type code, plus equipment names ('@' + key,
        /// type ANY) and alarm tags ('!' + key, type ANY).</summary>
        public Dictionary<string, int> Tags()
        {
            if (_tags != null)
                return _tags;
            var o = new Dictionary<string, int>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(Folder))
            {
                foreach (var r in Unit0.Rows(Unit0.Find(Folder!, "variable.dbf"), new[] { "NAME", "TYPE" }))
                {
                    string n = CitectText.Trim(r[0]);
                    if (n.Length == 0)
                        continue;
                    string k = Lexer.NameKey(n);
                    if (!o.ContainsKey(k))
                        o[k] = Unit0.TagType(r[1], Ast.Any);
                }
                foreach (var r in Unit0.Rows(Unit0.Find(Folder!, "equip.dbf"), new[] { "NAME" }))
                {
                    string n = CitectText.Trim(r[0]);
                    if (n.Length == 0)
                        continue;
                    string k = "@" + Lexer.NameKey(n);
                    if (!o.ContainsKey(k))
                        o[k] = Ast.Any;
                }
                foreach (string table in CompileUnit.AlarmTables)
                {
                    foreach (var r in Unit0.Rows(Unit0.Find(Folder!, table), new[] { "TAG" }))
                    {
                        string n = CitectText.Trim(r[0]);
                        if (n.Length == 0)
                            continue;
                        string k = "!" + Lexer.NameKey(n);
                        if (!o.ContainsKey(k))
                            o[k] = Ast.Any;
                    }
                }
            }
            _tags = o;
            return o;
        }

        public override string ToString() => Name;
    }

    /// <summary>Helpers shared by the unit.</summary>
    internal static class Unit0
    {
        public static string? Find(string folder, string lowerName)
        {
            var names = WinPath.ListDir(folder);
            if (names == null)
                return null;
            foreach (string fn in names)
                if (CitectText.Lower(fn) == lowerName)
                    return WinPath.Join(folder, fn);
            return null;
        }

        public static List<string[]> Rows(string? path, string[] cols)
        {
            if (string.IsNullOrEmpty(path))
                return new List<string[]>();
            try
            {
                return Dbf.CitectRows(path!, cols);
            }
            catch (Exception)
            {
                return new List<string[]>(); // an unreadable table = empty
            }
        }

        private static readonly Dictionary<string, int> TagTypes = new Dictionary<string, int>(
            StringComparer.Ordinal
        )
        {
            ["DIGITAL"] = Types.Long,
            ["INT"] = Types.Long,
            ["UINT"] = Types.Long,
            ["LONG"] = Types.Long,
            ["ULONG"] = Types.Long,
            ["BCD"] = Types.Long,
            ["LONGBCD"] = Types.Long,
            ["BYTE"] = Types.Long,
            ["REAL"] = Types.Real,
            ["LREAL"] = Types.Real,
            ["STRING"] = Types.String,
        };

        public static int TagType(string? word, int dflt) =>
            TagTypes.TryGetValue(CitectText.Upper(CitectText.Trim(word ?? "")), out int t) ? t : dflt;

        public static List<string> StableSortBy(List<string> items, Func<string, string> key)
        {
            var idx = new List<(string k, int i, string v)>(items.Count);
            for (int i = 0; i < items.Count; i++)
                idx.Add((key(items[i]), i, items[i]));
            idx.Sort(
                (a, b) =>
                {
                    int c = string.CompareOrdinal(a.k, b.k);
                    return c != 0 ? c : a.i.CompareTo(b.i);
                }
            );
            var o = new List<string>(items.Count);
            foreach (var t in idx)
                o.Add(t.v);
            return o;
        }

        /// <summary>The file name of a path without its extension.</summary>
        public static string Stem(string p)
        {
            string b = WinPath.BaseName(p);
            int dot = b.LastIndexOf('.');
            if (dot <= 0)
                return b;
            for (int i = 0; i < dot; i++)
                if (b[i] != '.')
                    return b.Substring(0, dot);
            return b;
        }
    }

    /// <summary>Project names to projects: User\MASTER.DBF of a User folder (NAME, PATH; the first
    /// live row per name; a blank PATH = User\NAME), plus virtual projects.</summary>
    public sealed class Registry
    {
        public readonly string? UserDir;
        public readonly Dictionary<string, SourceProject> Projects = new Dictionary<string, SourceProject>(
            StringComparer.Ordinal
        );

        /// <summary>The projects in registration order (MASTER.DBF row order, then Add order).</summary>
        public readonly List<string> Order = new List<string>();

        public Registry(string? userDir = null)
        {
            UserDir = userDir;
            if (!string.IsNullOrEmpty(userDir))
            {
                string? master = Unit0.Find(userDir!, "master.dbf");
                foreach (var r in Unit0.Rows(master, new[] { "NAME", "PATH" }))
                {
                    string name = CitectText.Trim(r[0]);
                    if (name.Length == 0 || Projects.ContainsKey(CitectText.Lower(name)))
                        continue;
                    string path = CitectText.Trim(r[1]).TrimEnd('\\', '/');
                    if (path.Length == 0)
                        path = name;
                    string folder = WinPath.Clean(WinPath.Join(userDir!, path));
                    Set(CitectText.Lower(name), new SourceProject(name, folder));
                }
            }
        }

        private void Set(string key, SourceProject p)
        {
            if (!Projects.ContainsKey(key))
                Order.Add(key);
            Projects[key] = p;
        }

        public void Add(SourceProject project) => Set(project.Key, project);

        public SourceProject? Get(string name) =>
            Projects.TryGetValue(CitectText.Lower(CitectText.Trim(name)), out var p) ? p : null;

        public IEnumerable<SourceProject> All
        {
            get
            {
                foreach (string k in Order)
                    yield return Projects[k];
            }
        }

        /// <summary>
        /// The compile order for ``root``: System, Include, includes depth-first, root last.
        /// </summary>
        public (List<SourceProject> order, List<Diagnostic> diags) CompileOrder(string root)
        {
            var diags = new List<Diagnostic>();
            var order = new List<SourceProject>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string n in CompileUnit.Always)
            {
                var p = Get(n);
                if (p != null && seen.Add(p.Key))
                    order.Add(p);
            }
            var rp = Get(root);
            if (rp == null)
            {
                diags.Add(new Diagnostic(4016, "", 0, 0, root));
                return (order, diags);
            }
            if (seen.Contains(rp.Key))
                return (order, diags); // compiling System or Include itself
            seen.Add(rp.Key);
            var stack = new List<string> { rp.Key };

            void Visit(SourceProject p)
            {
                foreach (string raw in p.IncludeRows())
                {
                    string name = CitectText.Trim(raw);
                    if (name.Length == 0)
                    {
                        diags.Add(new Diagnostic(2008, p.Name + "\\INCLUDE.DBF", 1, 0, "", p.Name));
                        diags.Add(new Diagnostic(4016, p.Name + "\\INCLUDE.DBF", 0, 0, "", p.Name));
                        continue;
                    }
                    string low = CitectText.Lower(name);
                    if (low == "include" || low == "system")
                        continue;
                    var child = Get(name);
                    if (child == null)
                    {
                        diags.Add(new Diagnostic(4016, p.Name + "\\INCLUDE.DBF", 0, 0, name, p.Name));
                        continue;
                    }
                    if (stack.Contains(child.Key))
                    {
                        diags.Add(new Diagnostic(4017, "", 0, 0, name, p.Name));
                        continue;
                    }
                    if (seen.Contains(child.Key))
                        continue;
                    seen.Add(child.Key);
                    stack.Add(child.Key);
                    Visit(child);
                    stack.RemoveAt(stack.Count - 1);
                    order.Add(child);
                }
            }

            Visit(rp);
            order.Add(rp);
            return (order, diags);
        }
    }

    /// <summary>One user function of the unit (its _FUNCSYM row).</summary>
    public sealed class FuncInfo
    {
        /// <summary>The definition (an incremental edit of its file puts the new parse here, the
        /// signature unchanged: <see cref="CompileUnit.ReplaceFile"/>).</summary>
        public FunctionDef Node { get; internal set; }
        public readonly string Name;
        public readonly string Key;
        public readonly bool Private;
        public readonly bool PublicKw;
        public readonly string File;
        public readonly string Stem;
        public readonly string Project;
        public int Index = -1;
        public readonly int ModuleIndex;

        public FuncInfo(FunctionDef node, string file, string project, int moduleIndex)
        {
            Node = node;
            Name = node.Name;
            Key = Lexer.NameKey(node.Name);
            Private = node.Scope == "PRIVATE";
            PublicKw = node.Scope == "PUBLIC";
            File = file;
            Stem = Unit0.Stem(file);
            Project = project;
            ModuleIndex = moduleIndex;
        }

        public string SymbolName => Private ? Name + "@" + Stem + "@" + Project : Name;

        public int Result => Node.ResultCode;

        public int[] ArgTypes => Node.ArgCodes;

        /// <summary>Arguments without a default before the last one without (the caller must pass
        /// that many; MINPARAM).</summary>
        public int Mandatory
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Node.Params.Count; i++)
                    if (Node.Params[i].Default == null)
                        n = i + 1;
                return n;
            }
        }

        public int TypeWord => Result | (Private ? 0x400 : 0) | (PublicKw ? 0x800 : 0);

        /// <summary>The _FUNCSYM.DBF row: NAME (PRIVATE = name@file@project), OP_CODE, ARGC (one
        /// type char per argument), TYPE (return code + 0x400 PRIVATE + 0x800 explicit PUBLIC),
        /// RETURNED, MANDATORY.</summary>
        public List<KeyValuePair<string, string>> FuncsymRow()
        {
            var argc = new char[ArgTypes.Length];
            for (int i = 0; i < argc.Length; i++)
                argc[i] = Types.SigChar(ArgTypes[i]);
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("NAME", SymbolName),
                new KeyValuePair<string, string>("OP_CODE", Index.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("ARGC", new string(argc)),
                new KeyValuePair<string, string>("TYPE", TypeWord.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("RETURNED", Node.RetType != null ? "1" : "0"),
                new KeyValuePair<string, string>(
                    "MANDATORY",
                    Mandatory.ToString(CultureInfo.InvariantCulture)
                ),
            };
        }
    }

    /// <summary>One parsed file of the unit and the diagnostics of each pass.</summary>
    public sealed class SourceFile
    {
        public readonly SourceProject Project;
        public readonly string Name;
        public readonly string Path;
        public readonly Module Module;

        /// <summary>The declaration pass: duplicates / W1006 (plus the parser's header messages).</summary>
        public readonly List<Diagnostic> Pass1;

        /// <summary>The body pass: syntax and lexer messages, then the resolver's (the compiler
        /// parses and checks a file in one go there, fix_diagA s09).</summary>
        public readonly List<Diagnostic> Pass2;
        public readonly Dictionary<string, VarRef> ModuleVars = new Dictionary<string, VarRef>(
            StringComparer.Ordinal
        );

        public SourceFile(SourceProject project, string name, string path, Module module)
        {
            Project = project;
            Name = name;
            Path = path;
            Module = module;
            Pass1 = new List<Diagnostic>(module.DeclDiagnostics);
            Pass2 = new List<Diagnostic>(module.Diagnostics);
        }

        public List<Diagnostic> Errors(int which)
        {
            var o = new List<Diagnostic>();
            foreach (var d in which == 1 ? Pass1 : Pass2)
                if (d.IsError)
                    o.Add(d);
            return o;
        }

        public bool HasErrors(int which)
        {
            foreach (var d in which == 1 ? Pass1 : Pass2)
                if (d.IsError)
                    return true;
            return false;
        }

        /// <summary>A pass's messages in source order (a message without a line stays after the one
        /// before it). At one token the check of what ended there comes before the syntax error at
        /// it ('(ZCb{)}' E2022 then E2041, r2_diagB b09 / r2_diagA c09).</summary>
        public List<Diagnostic> SortedPass(int which)
        {
            var items = which == 1 ? Pass1 : Pass2;
            int nSyntax = which == 1 ? Module.DeclDiagnostics.Count : Module.Diagnostics.Count;
            var keyed = new List<(int line, int col, int seq, int flag, int i, Diagnostic d)>(items.Count);
            int ll = 0,
                lc = 0,
                ls = -1;
            for (int i = 0; i < items.Count; i++)
            {
                var d = items[i];
                if (d.Line != 0)
                {
                    // the token index orders the messages inside a label expansion (its tokens all
                    // have the position of the use: r2_diagA c09, r2_rev_diagA r20)
                    ll = d.Line;
                    lc = d.Col;
                    ls = d.Seq;
                }
                keyed.Add((ll, lc, ls, i >= nSyntax ? 0 : 1, i, d));
            }
            keyed.Sort(
                (a, b) =>
                {
                    int c = a.line.CompareTo(b.line);
                    if (c != 0)
                        return c;
                    c = a.col.CompareTo(b.col);
                    if (c != 0)
                        return c;
                    c = a.seq.CompareTo(b.seq);
                    if (c != 0)
                        return c;
                    c = a.flag.CompareTo(b.flag);
                    if (c != 0)
                        return c;
                    return a.i.CompareTo(b.i);
                }
            );
            var o = new List<Diagnostic>(keyed.Count);
            foreach (var k in keyed)
                o.Add(k.d);
            return o;
        }
    }

    /// <summary>A compiled page field (<see cref="CompileUnit.CompileField"/>): Node = the expression
    /// (null when empty, NOP or broken) or the command's statement list, with Ty / Ref set;
    /// Diagnostics in the compiler's order.</summary>
    public sealed class FieldResult
    {
        public readonly object? Node;
        public readonly List<Diagnostic> Diagnostics;

        /// <summary>The field text's lines (what the message columns count in).</summary>
        public readonly LineIndex? Lines;

        public FieldResult(object? node, List<Diagnostic> diagnostics, LineIndex? lines = null)
        {
            Node = node;
            Diagnostics = diagnostics;
            Lines = lines;
        }

        /// <summary>The 1-based line and column of a node of the field (as a message's), (0, 0)
        /// when unknown.</summary>
        public (int line, int col) Position(Node n) =>
            Lines != null && n.Pos >= 0 ? Lines.LineCol(n.Pos) : (0, 0);

        public List<Diagnostic> Errors
        {
            get
            {
                var o = new List<Diagnostic>();
                foreach (var d in Diagnostics)
                    if (d.IsError)
                        o.Add(d);
                return o;
            }
        }
    }

    /// <summary>A Cicode compile unit: what CtCmp32 compiles together, and its name resolution.</summary>
    public sealed partial class CompileUnit
    {
        /// <summary>Compiled into every unit, first, in this order.</summary>
        public static readonly string[] Always = { "System", "Include" };

        /// <summary>The '$n' intrinsics that pick one of their argument texts at compile time:
        /// IFDEF, IFDEFDIGALM, IFDEFADVALM, IFDEFANAALM, IFDEFHRESALM.</summary>
        public static readonly HashSet<int> IfdefCodes = new HashSet<int> { 9, 12, 13, 14, 17 };

        /// <summary>The alarm tables whose TAG names an alarm: 'Alarm.Field' (.On, .Ack ...) is a
        /// defined reference (Example errlog.DBF: no W1007 for 'Mixer_RecipeFullMilk.On', a
        /// tsana.DBF alarm).</summary>
        public static readonly string[] AlarmTables =
        {
            "advalm.dbf",
            "anaalm.dbf",
            "argana.dbf",
            "argdig.dbf",
            "dbptalm.dbf",
            "digalm.dbf",
            "hresalm.dbf",
            "tsana.dbf",
            "tsdig.dbf",
        };

        public readonly List<SourceProject> Projects;
        public readonly Func0Rows Func0;
        public readonly List<Diagnostic> OrderDiags;
        public readonly bool UseTags;

        /// <summary>
        /// Parsed files shared between units, keyed by path, mtime, size, labels and project.
        /// </summary>
        public readonly Dictionary<string, Module>? ParseCache;
        public readonly LabelTable Labels = new LabelTable();
        public readonly List<SourceFile> Files = new List<SourceFile>();

        /// <summary>In OP_CODE order.</summary>
        public readonly List<FuncInfo> Functions = new List<FuncInfo>();
        public readonly Dictionary<string, FuncInfo> Public = new Dictionary<string, FuncInfo>(
            StringComparer.Ordinal
        );
        public readonly Dictionary<(int, string), FuncInfo> Private =
            new Dictionary<(int, string), FuncInfo>();
        public readonly Dictionary<string, VarRef> Globals = new Dictionary<string, VarRef>(
            StringComparer.Ordinal
        );

        /// <summary>The GLOBAL keys in declaration order.</summary>
        public readonly List<string> GlobalOrder = new List<string>();
        public readonly Dictionary<string, VarRef> Locvars = new Dictionary<string, VarRef>(
            StringComparer.Ordinal
        );
        public readonly List<Diagnostic> LocvarDiags = new List<Diagnostic>();
        public readonly Dictionary<string, int> Tags = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>FUNC0 names a PUBLIC function replaces.</summary>
        public readonly Dictionary<string, FuncInfo> Overrides = new Dictionary<string, FuncInfo>(
            StringComparer.Ordinal
        );
        public SourceFile? StoppedAt1;

        /// <summary>The include-row error that stopped the compile before any file (F4016 /
        /// F4017), null when none did.</summary>
        public Diagnostic? StoppedOrder;

        /// <summary>Why the compile never reached the functions of <paramref name="file"/> (it
        /// stopped at an include-row error, or at a declaration error of another file), null when
        /// it did reach them: 'the compile stops at &lt;code&gt; &lt;message&gt; [file:line] {context}'.</summary>
        public string? StopReason(string file)
        {
            if (StoppedOrder != null)
                return "the compile stops at " + StoppedOrder;
            var f = StoppedAt1;
            if (f == null || string.Equals(WinPath.Key(f.Path), WinPath.Key(file), StringComparison.Ordinal))
                return null;
            foreach (var d in f.SortedPass(1))
                if (d.IsError)
                    return "the compile stops at " + d;
            return "the compile stops at " + f.Path;
        }

        public SourceFile? StoppedAt2;
        private Dictionary<string, HashSet<int>>? _callbacks;

        /// <summary>[General]TagStartDigit=1: tag names may start with a digit (Lexer.DigitLedNameEnd).
        /// Set in the machine's citect.ini (what CtCmp32 reads) or in the project's own citect.ini
        /// (the runtime's: projects that use such tags set it there).</summary>
        public bool TagStartDigit;

        /// <summary>The TagStartDigit of <paramref name="projects"/>: the active citect.ini's, else
        /// the root project's own citect.ini (the last of the compile order).</summary>
        public static bool TagStartDigitOf(IList<SourceProject> projects)
        {
            static bool On(string v) => CicodeSim.Project.CitectText.Trim(v) == "1";
            try
            {
                if (On(CicodeSim.Project.CitectIni.IniValue("General", "TagStartDigit")))
                    return true;
                for (int k = projects.Count - 1; k >= 0; k--)
                {
                    string? dir = projects[k].Folder;
                    if (string.IsNullOrEmpty(dir))
                        continue;
                    string ini = System.IO.Path.Combine(dir!, "citect.ini");
                    if (CicodeSim.Project.WinPath.IsFile(ini))
                        return On(CicodeSim.Project.CitectIni.IniValueIn(ini, "General", "TagStartDigit"));
                }
            }
            catch (Exception e)
            {
                EngineNotes.Swallowed("CompileUnit.TagStartDigitOf", e);
            }
            return false;
        }

        public CompileUnit(
            IList<SourceProject> projects,
            Func0Rows? func0 = null,
            IList<Diagnostic>? orderDiags = null,
            bool useTags = true,
            Dictionary<string, Module>? parseCache = null
        )
        {
            Projects = new List<SourceProject>(projects);
            TagStartDigit = TagStartDigitOf(Projects);
            Func0 = func0 ?? Func0Rows.LoadOrRuntime();
            OrderDiags = orderDiags != null ? new List<Diagnostic>(orderDiags) : new List<Diagnostic>();
            UseTags = useTags;
            ParseCache = parseCache;
        }

        /// <summary>The unit of project <paramref name="root"/> registered in
        /// <paramref name="userDir"/>\MASTER.DBF.</summary>
        public static CompileUnit ForProject(
            string root,
            string userDir,
            Func0Rows? func0 = null,
            bool useTags = true,
            Dictionary<string, Module>? parseCache = null
        )
        {
            var reg = new Registry(userDir);
            var (order, diags) = reg.CompileOrder(root);
            return new CompileUnit(order, func0, diags, useTags, parseCache);
        }

        /// <summary>Compile project <paramref name="root"/> of <paramref name="userDir"/>.</summary>
        public static CompileUnit Build(
            string root,
            string userDir,
            Func0Rows? func0 = null,
            bool useTags = true
        ) => ForProject(root, userDir, func0, useTags).Compile();

        private static bool StopsOrder(IEnumerable<Diagnostic> ds)
        {
            foreach (var d in ds)
                if (d.Code == 4016 || d.Code == 4017)
                    return true;
            return false;
        }

        // --------------------------------------------------------------------------- passes
        /// <summary>Labels, parse + declarations, bodies. <paramref name="stop"/> = like the
        /// compiler, do not run the body pass after a declaration-pass error (StoppedAt1 names the
        /// failing file).</summary>
        public CompileUnit Compile(bool stop = true)
        {
            if (StopsOrder(OrderDiags))
            {
                // the compiler stops before any file (an include row that names no project:
                // F4016 / F4017). The files are still parsed and declared, nothing resolved, so a
                // simulator can name every function that did not compile and why (StopReason)
                foreach (var d in OrderDiags)
                    if (d.Code == 4016 || d.Code == 4017)
                    {
                        StoppedOrder = d;
                        break;
                    }
                try
                {
                    Parse();
                    Declare();
                }
                catch (Exception e)
                {
                    EngineNotes.Swallowed("CompileUnit.ParseAfterOrderStop", e);
                }
                return this;
            }
            LoadLabels();
            LoadLocvars();
            if (UseTags)
            {
                foreach (var p in Projects)
                foreach (var kv in p.Tags())
                    if (!Tags.ContainsKey(kv.Key))
                        Tags[kv.Key] = kv.Value;
            }
            Parse();
            Declare();
            SourceFile? first = null;
            foreach (var f in Files)
                if (f.HasErrors(1))
                {
                    first = f;
                    break;
                }
            StoppedAt1 = first;
            if (first != null && stop)
            {
                // only the failing file's bodies are checked (see CompilerView)
                Resolver.Resolve(this, first, Files.IndexOf(first));
                return this;
            }
            for (int i = 0; i < Files.Count; i++)
                Resolver.Resolve(this, Files[i], i);
            foreach (var f in Files)
                if (f.HasErrors(2))
                {
                    StoppedAt2 = f;
                    break;
                }
            return this;
        }

        public void LoadLabels()
        {
            foreach (var p in Projects)
                Labels.AddRows(p.LabelRows(), p.Name, p.LabelsPath());
        }

        private void LoadLocvars()
        {
            foreach (var p in Projects)
            {
                foreach (var r in p.LocvarRows())
                {
                    string n = CitectText.Trim(r.Key ?? "");
                    if (n.Length == 0)
                        continue;
                    string k = Lexer.NameKey(n);
                    if (Locvars.ContainsKey(k))
                    {
                        LocvarDiags.Add(new Diagnostic(2021, p.Name + "\\LOCVAR.DBF", 0, 0, n, p.Name));
                        continue;
                    }
                    int t = Unit0.TagType(r.Value, Types.Long);
                    Locvars[k] = new VarRef("locvar", n, t, project: p.Name);
                }
            }
        }

        /// <summary>Parse every file. A file that calls a name the unit declares as a GLOBAL is
        /// parsed again knowing the GLOBALs: a variable hides a function of its name (q16,
        /// fix_diagA s09), so 'v(1)' is the variable and then a new statement.</summary>
        private void Parse()
        {
            var cache = ParseCache;
            string sig = cache != null ? Labels.Signature() : "";
            // the files in the compiler's order, each with its cache key; the ones not in the
            // cache are parsed on every core (a file's parse reads only the labels and its own
            // text), then the files are put in that order again
            var items = new List<(SourceProject P, string Name, string Path, string? Key)>();
            foreach (var p in Projects)
            foreach (var (fname, path) in p.CiFiles())
            {
                string? key = null;
                if (cache != null && !string.IsNullOrEmpty(p.Folder) && !p.IsVirtual)
                {
                    var st = WinPath.Stat(path);
                    if (st != null)
                        key =
                            WinPath.CaseKey(path)
                            + "\u0001"
                            + st.Value.mtime.ToString(CultureInfo.InvariantCulture)
                            + "\u0001"
                            + st.Value.size.ToString(CultureInfo.InvariantCulture)
                            + "\u0001"
                            + sig
                            + "\u0001"
                            + p.Name;
                }
                items.Add((p, fname, path, key));
            }
            var mods = new Module?[items.Count];
            var todo = new List<int>();
            for (int i = 0; i < items.Count; i++)
            {
                Module? mod = null;
                if (items[i].Key != null)
                    cache!.TryGetValue(items[i].Key!, out mod);
                if (mod == null)
                    todo.Add(i);
                mods[i] = mod;
            }
            ParseEach(todo, i => mods[i] = ParseOne(items[i].P, items[i].Name, items[i].Path, null));
            for (int i = 0; i < items.Count; i++)
            {
                var mod = mods[i];
                if (mod == null)
                    continue;
                if (items[i].Key != null && todo.Contains(i))
                    cache![items[i].Key!] = mod;
                Files.Add(new SourceFile(items[i].P, items[i].Name, items[i].Path, mod));
            }
            var gkeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in Files)
            foreach (var it in f.Module.Items)
                if (it is VarDecl v && v.IsGlobal)
                    gkeys.Add(Lexer.NameKey(v.Name));
            if (gkeys.Count == 0)
                return;
            var again = new List<int>();
            for (int i = 0; i < Files.Count; i++)
                if (Files[i].Module.CallNames.Overlaps(gkeys))
                    again.Add(i);
            var redone = new Module?[Files.Count];
            ParseEach(again, i =>
                redone[i] = ParseOne(Files[i].Project, Files[i].Name, Files[i].Path, gkeys)
            );
            foreach (int i in again)
                if (redone[i] != null)
                    Files[i] = new SourceFile(Files[i].Project, Files[i].Name, Files[i].Path, redone[i]!);
        }

        /// <summary>Run <paramref name="parse"/> for each index, on every core when there are a
        /// few (each writes its own slot); an exception is the parse's own, as one at a time.</summary>
        private void ParseEach(List<int> indexes, Action<int> parse)
        {
            if (indexes.Count == 0)
                return;
            CallbackArgs(); // (made once, before the parses read it)
            if (indexes.Count < 4)
            {
                foreach (int i in indexes)
                    parse(i);
                return;
            }
            try
            {
                System.Threading.Tasks.Parallel.ForEach(indexes, parse);
            }
            catch (AggregateException e)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
            }
        }

        private Module? ParseOne(SourceProject p, string fname, string path, IEnumerable<string>? known)
        {
            string text;
            try
            {
                text = p.Text(fname, path);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            var lex = Lexer.Tokenize(text, Labels, path, "ci", p.Name, TagStartDigit);
            var parser = new Parser(lex, path, p.Name, knownVars: known) { Callbacks = CallbackArgs() };
            return parser.ParseModule();
        }

        /// <summary>The built-ins with FUNCTION arguments: lower-case name to argument indexes (the
        /// parser takes one name token there). A library function replacing a built-in is not
        /// known before the parse; such a name keeps the built-in's rule (not probed).</summary>
        public Dictionary<string, HashSet<int>> CallbackArgs()
        {
            if (_callbacks != null)
                return _callbacks;
            var cb = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
            foreach (var b in Func0.Rows)
            {
                var ix = new HashSet<int>();
                for (int i = 0; i < b.Args.Count; i++)
                    if (b.Args[i].kind == "FUNCTION")
                        ix.Add(i);
                if (ix.Count > 0 && !cb.ContainsKey(b.Key))
                    cb[b.Key] = ix;
            }
            _callbacks = cb;
            return cb;
        }

        /// <summary>The declaration pass, variables first.</summary>
        private void Declare()
        {
            var func0 = Func0;
            var positions = new List<Dictionary<string, int>>();
            for (int fi = 0; fi < Files.Count; fi++)
            {
                var f = Files[fi];
                var modVars = f.ModuleVars;
                var posOf = new Dictionary<string, int>(StringComparer.Ordinal);
                positions.Add(posOf);
                foreach (var it0 in f.Module.Items)
                {
                    if (!(it0 is VarDecl it))
                        continue;
                    string k = Lexer.NameKey(it.Name);
                    if (it.IsGlobal)
                    {
                        if (Globals.ContainsKey(k) || modVars.ContainsKey(k))
                        {
                            f.Pass1.Add(Diag(2021, f, it.Pos, it.Name));
                            // (a GLOBAL of another file: E2021 only, inc q8_04)
                            modVars.TryGetValue(k, out var prev);
                            RedeclaredInit(f, it, prev);
                            continue;
                        }
                        var gref = new VarRef(
                            "global",
                            it.Name,
                            Ast.TypeCode(it.Type),
                            decl: it,
                            dims: it.Dims,
                            file: f.Path,
                            project: f.Project.Name
                        );
                        it.Var = gref;
                        Globals[k] = gref;
                        GlobalOrder.Add(k);
                        continue;
                    }
                    if (modVars.ContainsKey(k))
                    {
                        f.Pass1.Add(Diag(2021, f, it.Pos, it.Name));
                        modVars.TryGetValue(k, out var prev);
                        RedeclaredInit(f, it, prev);
                        continue;
                    }
                    var mref = new VarRef(
                        "module",
                        it.Name,
                        Ast.TypeCode(it.Type),
                        decl: it,
                        dims: it.Dims,
                        file: f.Path,
                        project: f.Project.Name
                    );
                    it.Var = mref;
                    modVars[k] = mref;
                    if (!posOf.ContainsKey(k))
                        posOf[k] = it.Pos;
                }
            }
            for (int fi = 0; fi < Files.Count; fi++)
            {
                var f = Files[fi];
                var modVars = f.ModuleVars;
                var posOf = positions[fi];
                var own = new Dictionary<string, FuncInfo>(StringComparer.Ordinal);
                foreach (var it0 in f.Module.Items)
                {
                    if (!(it0 is FunctionDef it) || it.Name.Length == 0)
                        continue;
                    var info = new FuncInfo(it, f.Path, f.Project.Name, fi);
                    string k = info.Key;
                    bool above =
                        modVars.ContainsKey(k) && (posOf.TryGetValue(k, out int pp) ? pp : 1 << 30) < it.Pos;
                    bool dup = Globals.ContainsKey(k) || above || own.ContainsKey(k) || Public.ContainsKey(k);
                    if (dup)
                    {
                        f.Pass1.Add(Diag(2021, f, it.NamePos, it.Name));
                        if (info.Private && !own.ContainsKey(k) && !Private.ContainsKey((fi, k)))
                        {
                            // its own file's calls still bind to it (inc q6_05)
                            Private[(fi, k)] = info;
                        }
                        continue;
                    }
                    if (func0.Contains(k) && !Overrides.ContainsKey(k))
                        f.Pass1.Add(Diag(1006, f, it.NamePos, it.Name));
                    own[k] = info;
                    if (info.Private)
                        Private[(fi, k)] = info;
                    else
                    {
                        Public[k] = info;
                        if (func0.Contains(k) && !Overrides.ContainsKey(k))
                            Overrides[k] = info;
                    }
                    info.Index = Functions.Count;
                    Functions.Add(info);
                }
            }
        }

        /// <summary>The initial value of a variable declared again is read for the variable already
        /// there: a value that does not fit its type is E2006 / E2017 (r2_rev_core n12).</summary>
        private void RedeclaredInit(SourceFile f, VarDecl it, VarRef? prev)
        {
            if (prev == null || it.Init.Count == 0)
                return;
            var lit = it.Init[0];
            int want = prev.Type;
            if (want == Types.String)
            {
                if (lit.Kind != "STRING")
                    f.Pass1.Add(Diag(2017, f, lit.Pos));
            }
            else if (want == Types.Long || want == Types.Real)
            {
                if (lit.Kind == "STRING" || (want == Types.Long && lit.Kind == "REAL"))
                    f.Pass1.Add(Diag(2006, f, lit.Pos));
            }
        }

        private Diagnostic Diag(int code, SourceFile f, int pos, string ctx = "")
        {
            var (line, col) = f.Module.Lines != null ? f.Module.Lines.LineCol(pos) : (0, 0);
            return new Diagnostic(code, f.Path, line, col, ctx, f.Project.Name, pos);
        }

        // -------------------------------------------------------------------------- results
        /// <summary>The _FUNCSYM.DBF rows (OP_CODE order).</summary>
        public List<List<KeyValuePair<string, string>>> Funcsym()
        {
            var o = new List<List<KeyValuePair<string, string>>>(Functions.Count);
            foreach (var fi in Functions)
                o.Add(fi.FuncsymRow());
            return o;
        }

        /// <summary>Every diagnostic of every pass and file (not cut at the first failing file).</summary>
        public List<Diagnostic> AllDiagnostics()
        {
            var o = new List<Diagnostic>(OrderDiags);
            o.AddRange(Labels.Diagnostics);
            o.AddRange(LocvarDiags);
            foreach (var f in Files)
            {
                o.AddRange(f.Pass1);
                o.AddRange(f.Pass2);
            }
            return o;
        }

        /// <summary>The diagnostics CtCmp32 would report, in its order.</summary>
        public List<Diagnostic> CompilerView()
        {
            var o = new List<Diagnostic>(OrderDiags);
            if (StopsOrder(o))
                return o;
            o.AddRange(Labels.Diagnostics);
            o.AddRange(LocvarDiags);
            foreach (var f in Files)
            {
                o.AddRange(f.SortedPass(1));
                if (f.HasErrors(1))
                {
                    // that file's own body messages follow (inc q7_10); no other file's
                    o.AddRange(f.SortedPass(2));
                    return o;
                }
            }
            foreach (var f in Files)
            {
                o.AddRange(f.SortedPass(2));
                if (f.HasErrors(2))
                    break;
            }
            return o;
        }

        public List<Diagnostic> Errors()
        {
            var o = new List<Diagnostic>();
            foreach (var d in CompilerView())
                if (d.IsError)
                    o.Add(d);
            return o;
        }

        /// <summary>A call target as the compiler binds it: the file's PRIVATE
        /// function, a PUBLIC one, a FUNC0 built-in.</summary>
        public (FuncInfo? func, Func0Row? builtin) LookupFunction(string key, int fileIndex)
        {
            if (Private.TryGetValue((fileIndex, key), out var fi))
                return (fi, null);
            if (Public.TryGetValue(key, out fi))
                return (fi, null);
            return (null, Func0.Get(key));
        }

        /// <summary>Whether <paramref name="name"/> (a bare / dotted tag or Equip.Item reference) is
        /// a variable tag or equipment item of the unit, and its type (ANY when not known).</summary>
        public (bool defined, int type) TagDefined(string name)
        {
            if (Tags.Count == 0)
                return (false, Ast.Any);
            string[] parts = name.Split('.');
            string k = Lexer.NameKey(parts[0]);
            if (parts.Length == 1)
                return Tags.TryGetValue(k, out int t) ? (true, t) : (false, Ast.Any);
            string full = Lexer.NameKey(name);
            if (Tags.TryGetValue(full, out int tf))
                return (true, tf);
            if (Tags.ContainsKey(k) || Tags.ContainsKey("!" + k))
                return (true, Ast.Any); // Tag.Field, Alarm.Field
            if (parts.Length > 1 && Tags.ContainsKey(Lexer.NameKey(parts[1])))
                return (true, Ast.Any); // Cluster.Tag.Field
            for (int i = parts.Length - 1; i > 0; i--) // Equip.Item[.ExtItem]
                if (Tags.ContainsKey("@" + Lexer.NameKey(string.Join(".", parts, 0, i))))
                    return (true, Ast.Any);
            return (false, Ast.Any);
        }

        // ---------------------------------------------------------------------- page fields
        /// <summary>Check one page expression or command field against the compiled unit.
        /// <paramref name="vars"/> = module variables the field sees (a report's, declared by
        /// <see cref="CompileReportBlock"/>), by NameKey.</summary>
        public FieldResult CompileField(
            string text,
            string kind = "expression",
            string project = "",
            string where = "",
            IReadOnlyDictionary<string, VarRef>? vars = null
        ) => Field(text, kind, project, where, vars, null);

        /// <summary>A report's first {CICODE} block (help 1094538): the variables declared at its
        /// start become the report's (module variables, by NameKey into <paramref name="vars"/>:
        /// the report's later blocks and fields see them through <see cref="CompileField"/>),
        /// its statements a command field. Initial values are the declarations' (VarDecl.Init).</summary>
        public FieldResult CompileReportBlock(
            string text,
            Dictionary<string, VarRef> vars,
            string project = "",
            string where = ""
        ) => Field(text, "command", project, where, vars, vars);

        private FieldResult Field(
            string text,
            string kind,
            string project,
            string where,
            IReadOnlyDictionary<string, VarRef>? vars,
            Dictionary<string, VarRef>? declare
        )
        {
            SourceProject? proj = null;
            if (project.Length > 0)
                foreach (var p in Projects)
                    if (CitectText.Lower(p.Name) == CitectText.Lower(project))
                    {
                        proj = p;
                        break;
                    }
            if (proj == null)
                proj =
                    Projects.Count > 0
                        ? Projects[Projects.Count - 1]
                        : new SourceProject(project, null, new List<KeyValuePair<string, string>>());
            var lex = Lexer.Tokenize(text, Labels, where, "page", proj.Name, TagStartDigit);
            var parser = new Parser(lex, where, proj.Name, "page", GlobalOrder)
            {
                Callbacks = CallbackArgs(),
            };
            var mod = new Module(where, proj.Name);
            if (vars != null)
                parser.AddModuleVars(vars.Values);
            object? node;
            if (declare != null)
            {
                var decls = new List<VarDecl>();
                node = parser.ParseReportBlock(decls);
                foreach (var d in decls)
                {
                    string k = Lexer.NameKey(d.Name);
                    if (declare.ContainsKey(k))
                    {
                        var (line, col) = lex.Lines.LineCol(d.Pos);
                        parser.Diags.Add(new Diagnostic(2021, where, line, col, d.Name, proj.Name, d.Pos));
                        continue;
                    }
                    d.Var = declare[k] = new VarRef(
                        "module",
                        d.Name,
                        Ast.TypeCode(d.Type),
                        decl: d,
                        dims: d.Dims,
                        file: where,
                        project: proj.Name
                    );
                }
            }
            else if (kind == "command")
                node = parser.ParseCommandField();
            else
                node = parser.ParseExpressionField();
            mod.Lines = lex.Lines;
            mod.Text = lex.Text;
            mod.Diagnostics = parser.Diags;
            mod.CallNames = parser.CallNames;
            var f = new SourceFile(proj, where, where, mod);
            var r = new Resolver(this, f, -1);
            r.BeginField(vars);
            if (kind == "command")
                r.Block((List<Stmt>)node!);
            else if (node is Expr e)
            {
                int t = r.Expression(e);
                if (t == Ast.Void)
                {
                    // the value of the field is used: E2024 at its end (Example errlog: COL_A
                    // 'projFn10("x"){}' E2031 then E2024; a fill level '(TankLevel(..))^(0.0,100){}')
                    r.ErrEnd(2024, e);
                }
            }
            return new FieldResult(node, f.SortedPass(2), lex.Lines);
        }
    }
}
