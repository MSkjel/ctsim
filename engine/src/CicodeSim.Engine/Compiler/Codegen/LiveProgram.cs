using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using CicodeSim.Compiled;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Project;

namespace CicodeSim.Compiler.Codegen
{
    /// <summary>The program a simulation runs, kept up to date as sources change.</summary>
    public sealed class LiveProgram
    {
        public const string Changed = "changed",
            All = "all",
            None = "none";

        public string Project { get; }
        public string Mode { get; }
        public string? RdbPath { get; }
        public string? UserDir { get; }
        public Program Program { get; private set; } = null!;

        /// <summary>The compiler state, null until a file is compiled from source.</summary>
        public SourceLibrary? Source { get; private set; }

        /// <summary>The files whose code comes from source ('changed').</summary>
        public List<string> SourceFiles { get; } = new List<string>();
        public List<string> Notes { get; } = new List<string>();
        public List<Diagnostic> Errors { get; private set; } = new List<Diagnostic>();
        public List<(string name, string why)> Failed { get; } = new List<(string, string)>();
        public double FrontEndMs { get; private set; }
        public double CodeMs { get; private set; }

        private Program? _base;
        private readonly HashSet<string> _keys = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _texts = new Dictionary<string, string>(
            StringComparer.Ordinal
        );

        private LiveProgram(string project, string mode, string? rdb, string? userDir)
        {
            Project = project;
            Mode = mode;
            RdbPath = rdb;
            UserDir = userDir;
        }

        public static bool IsMode(string m) => m == Changed || m == All || m == None;

        /// <summary>Load the program of <paramref name="project"/> (a project folder or a registered
        /// name). <paramref name="texts"/> = unsaved buffers (path to text); <paramref name="alwaysSource"/>
        /// = files compiled from source whatever their date (the file a function is run from).</summary>
        public static LiveProgram Open(
            string project,
            string mode,
            string? rdbPath = null,
            string? userDir = null,
            IDictionary<string, string>? texts = null,
            IEnumerable<string>? alwaysSource = null
        )
        {
            if (!IsMode(mode))
                throw new ArgumentException("compile must be changed, all or none: " + mode);
            // a folder that does not exist (a mistyped path) is no project, although its unit
            // (System and Include alone) would compile from source
            if (!WinPath.IsDir(project))
            {
                var reg = new Registry(userDir ?? Units.DefaultUser).Get(project);
                if (reg == null)
                    throw new DirectoryNotFoundException(
                        "no project "
                            + project
                            + ": no such folder, and no project of that name in "
                            + Path.Combine(userDir ?? Units.DefaultUser, "MASTER.DBF")
                    );
                if (reg.Folder == null || !WinPath.IsDir(reg.Folder))
                    throw new DirectoryNotFoundException(
                        "the folder of project " + reg.Name + " does not exist: " + reg.Folder
                    );
            }
            string? rdb = rdbPath ?? RdbOf(project, userDir);
            var lp = new LiveProgram(project, mode, rdb, userDir);
            if (texts != null)
                foreach (var kv in texts)
                    lp._texts[kv.Key] = kv.Value;
            bool haveRdb = rdb != null && File.Exists(rdb);
            if (rdbPath != null && !haveRdb && mode != All)
                throw new FileNotFoundException("no compiled code: " + rdbPath + " does not exist", rdbPath);
            if (mode == None)
            {
                if (!haveRdb)
                    throw new FileNotFoundException(
                        "no compiled code: " + (rdb ?? "_LIBRARY.RDB") + " does not exist",
                        rdb
                    );
                lp.Program = lp._base = Rdb.LoadLibrary(rdb!);
                return lp;
            }
            if (mode == All || !haveRdb)
            {
                if (!haveRdb)
                    lp.Notes.Add(
                        "no "
                            + (rdb != null ? Path.GetFileName(rdb) : "_LIBRARY.RDB")
                            + ": every function from source"
                    );
                lp.StartCompiler();
                lp.BuildAll();
                lp._baseline = lp.BrokenFiles();
                foreach (var f in lp.Source!.Unit.Files)
                    if (lp._baseline.Contains(WinPath.Key(f.Path)))
                        lp.Notes.Add(
                            f.Name
                                + " does not compile"
                                + FirstErrorOf(lp.Source!.Unit, f)
                                + ": its functions are compiled from what the parser recovered"
                        );
                return lp;
            }
            lp._base = Rdb.LoadLibrary(rdb!);
            var pick = new List<string>();
            if (alwaysSource != null)
                pick.AddRange(alwaysSource);
            pick.AddRange(lp._texts.Keys);
            DateTime t = File.GetLastWriteTimeUtc(rdb!);
            foreach (string f in UnitFiles(project, userDir))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f) > t)
                        pick.Add(f);
                }
                catch (Exception e)
                {
                    EngineNotes.Swallowed("LiveProgram.ChangedUnits", e);
                }
            }
            foreach (string f in pick)
                lp.AddSource(f);
            if (lp.SourceFiles.Count == 0)
            {
                lp.Program = lp._base;
                return lp;
            }
            lp.StartCompiler();
            // a newer file that does not compile keeps its compiled code (CtCmp32 would not have
            // compiled the project either: the RDB is what last compiled)
            var broken = lp.BrokenFiles();
            var keep = lp.SourceFiles.FindAll(f => !broken.Contains(WinPath.Key(f)));
            foreach (string f in lp.SourceFiles)
                if (broken.Contains(WinPath.Key(f)))
                    lp.Notes.Add(
                        Path.GetFileName(f)
                            + " does not compile ("
                            + lp.ErrorsOf(f).Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            + " errors): its code from "
                            + Path.GetFileName(rdb)
                            + " runs"
                    );
            lp.SetSource(keep);
            lp._baseline = broken;
            if (lp.SourceFiles.Count == 0)
            {
                lp.Program = lp._base;
                return lp;
            }
            lp.Relink();
            lp.Notes.Add(
                lp.SourceFiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " file(s) from source: "
                    + string.Join(", ", lp.SourceFiles.ConvertAll(Path.GetFileName))
            );
            return lp;
        }

        /// <summary>The errors of one file of the unit (its declaration and body passes), whatever the
        /// other files hold: <see cref="Errors"/> is the compiler's view, which stops at the first file
        /// with an error. Empty when the compiler has not started or the file is not in the unit.</summary>
        public List<Diagnostic> ErrorsOf(string file) =>
            Source != null ? FileErrors(Source.Unit, file) : new List<Diagnostic>();

        public static List<Diagnostic> FileErrors(CompileUnit unit, string file)
        {
            var o = new List<Diagnostic>();
            string key = WinPath.Key(file);
            foreach (var f in unit.Files)
            {
                if (WinPath.Key(f.Path) != key)
                    continue;
                foreach (var d in f.SortedPass(1))
                    if (d.IsError)
                        o.Add(d);
                foreach (var d in f.SortedPass(2))
                    if (d.IsError)
                        o.Add(d);
            }
            return o;
        }

        /// <summary>The keys (WinPath.Key) of the unit's files that have errors of their own.</summary>
        private HashSet<string> BrokenFiles()
        {
            var o = new HashSet<string>(StringComparer.Ordinal);
            if (Source == null)
                return o;
            foreach (var f in Source.Unit.Files)
                if (f.HasErrors(1) || f.HasErrors(2))
                    o.Add(WinPath.Key(f.Path));
            return o;
        }

        /// <summary>Files that did not compile when the running program was made (their compiled code
        /// runs, or in 'all' what the parser recovered): they do not hold back a reload of another
        /// file. Any other file with errors does: the program never takes code CtCmp32 would refuse.</summary>
        private HashSet<string> _baseline = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>'changed': files whose last reload did not compile while their code comes from
        /// the RDB (that code keeps running; they do not hold back the others).</summary>
        private readonly HashSet<string> _refused = new HashSet<string>(StringComparer.Ordinal);

        private bool _inStep = true;

        /// <summary>Is the source unit the code that runs?</summary>
        public bool SourceInStep => _inStep && _refused.Count == 0;

        private void SetSource(List<string> files)
        {
            SourceFiles.Clear();
            _keys.Clear();
            foreach (string f in files)
                AddSource(f);
        }

        private static string? RdbOf(string project, string? userDir)
        {
            if (WinPath.IsDir(project))
                return Path.Combine(project, "_LIBRARY.RDB");
            var p = new Registry(userDir ?? Units.DefaultUser).Get(project);
            return p?.Folder != null ? Path.Combine(p.Folder, "_LIBRARY.RDB") : null;
        }

        /// <summary>' (E2021 Record already defined [file:line] {name})' for the note of a file
        /// that does not compile, '' when its errors are not known.</summary>
        private static string FirstErrorOf(
            CicodeSim.Compiler.Semantics.CompileUnit unit,
            CicodeSim.Compiler.Semantics.SourceFile f
        )
        {
            foreach (var d in FileErrors(unit, f.Path))
                if (d.IsError)
                    return " (" + d + ")";
            return "";
        }

        /// <summary>The .ci files of the project's unit (no parse).</summary>
        public static List<string> UnitFiles(string project, string? userDir = null)
        {
            var o = new List<string>();
            var unit = Units.ForProject(project, userDir);
            foreach (var p in unit.Projects)
            foreach (var (_, path) in p.CiFiles())
                o.Add(path);
            return o;
        }

        private void AddSource(string f)
        {
            if (_keys.Add(WinPath.Key(f)))
                SourceFiles.Add(f);
        }

        private void StartCompiler()
        {
            if (Source != null)
                return;
            Source = SourceLibrary.Open(Project, UserDir, _texts);
            FrontEndMs = Source.FrontEndMs;
            Errors = Source.Errors();
        }

        /// <summary>The source compiler, started when it is not yet (a program of mode 'none'
        /// or one run from the RDB alone has none until something asks): a check of a page
        /// field (ctsim/checkExpression) needs the unit's names.</summary>
        public SourceLibrary EnsureSource()
        {
            StartCompiler();
            return Source!;
        }

        private void BuildAll()
        {
            var sw = Stopwatch.StartNew();
            var img = Source!.Image();
            Failed.Clear();
            Failed.AddRange(img.Failed);
            Program = img.ToProgram("source:" + Project);
            SourceFiles.Clear();
            _keys.Clear();
            foreach (string f in Source.Files)
                AddSource(f);
            CodeMs = sw.Elapsed.TotalMilliseconds;
        }

        private void Relink()
        {
            var sw = Stopwatch.StartNew();
            Failed.Clear();
            Program = Source!.Link(_base!, SourceFiles, Failed);
            CodeMs = sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>The result of a <see cref="Reload"/>.</summary>
        public sealed class ReloadResult
        {
            /// <summary>The new program replaced the old one.</summary>
            public bool Compiled;

            /// <summary>Only the file was compiled again (its declarations kept their shape).</summary>
            public bool Incremental;
            public string Reason = "";
            public List<Diagnostic> Errors = new List<Diagnostic>();
            public double Ms;
        }

        /// <summary>A file saved (text null: read it) or an unsaved buffer (text): compile it and
        /// rebuild <see cref="Program"/>. A file with compile errors leaves the program as it was
        /// (the errors are returned).</summary>
        public ReloadResult Reload(string file, string? text = null)
        {
            var res = new ReloadResult();
            var sw = Stopwatch.StartNew();
            if (Mode == None)
            {
                res.Reason = "compile 'none' runs the compiled library only";
                return res;
            }
            // a .ci file of the unit's folders (a new one too: the unit is then compiled again)
            string key = WinPath.Key(file);
            bool inUnit = false;
            foreach (string f in UnitFiles(Project, UserDir))
                if (WinPath.Key(f) == key)
                {
                    inUnit = true;
                    break;
                }
            if (!inUnit)
            {
                res.Reason = "not a .ci file of the project's compile unit";
                res.Ms = sw.Elapsed.TotalMilliseconds;
                return res;
            }
            if (text != null)
                _texts[file] = text;
            else
                _texts.Remove(file);
            _inStep = false;
            if (Source == null)
            {
                // the first file from source starts the compiler (the whole unit, this text in it);
                // what does not compile then (but this file) is the baseline
                StartCompiler();
                _baseline = BrokenFiles();
                _baseline.Remove(key);
                res.Incremental = false;
            }
            else
            {
                var e = Source.Edit(file, text);
                res.Incremental = e.Incremental;
                if (!e.Incremental)
                    res.Reason = e.Reason;
                Errors = Source.Errors();
            }
            // the file's own errors (the unit's list stops at the first file with an error, which
            // may be another one)
            res.Errors = ErrorsOf(file);
            bool mixed = Mode != All && _base != null;
            if (res.Errors.Count > 0)
            {
                // a file whose code comes from the RDB keeps it (and does not hold back the
                // others); one already from source holds them back until it compiles
                if (mixed && !_keys.Contains(key))
                    _refused.Add(key);
                res.Reason = "the file does not compile: the previous code keeps running";
                res.Ms = sw.Elapsed.TotalMilliseconds;
                return res;
            }
            _refused.Remove(key);
            // from now on this file's code comes from source, also when another file holds the
            // link back below (it then goes in with that file's fix)
            if (mixed)
                AddSource(file);
            var held = new List<string>();
            var broken = BrokenFiles();
            foreach (var f in Source!.Unit.Files)
            {
                string fk = WinPath.Key(f.Path);
                if (!broken.Contains(fk) || _baseline.Contains(fk))
                    continue;
                if (mixed && !_keys.Contains(fk) && _refused.Contains(fk))
                    continue;
                held.Add(f.Name);
            }
            if (held.Count > 0)
            {
                // a file that compiled with the running program does not compile with this change
                // (or an earlier reload left it with errors): no program CtCmp32 would refuse
                res.Reason =
                    string.Join(", ", held)
                    + (held.Count == 1 ? " does" : " do")
                    + " not compile: the previous code keeps running";
                res.Ms = sw.Elapsed.TotalMilliseconds;
                return res;
            }
            if (Mode == All || _base == null)
                BuildAll();
            else
                Relink();
            _baseline = broken;
            _inStep = true;
            res.Compiled = true;
            res.Ms = sw.Elapsed.TotalMilliseconds;
            return res;
        }
    }

    /// <summary>One-shot use of <see cref="LiveProgram"/> (ctsim-dev exec).</summary>
    public static class MixedProgram
    {
        public const string Changed = LiveProgram.Changed,
            All = LiveProgram.All,
            None = LiveProgram.None;

        public sealed class Result
        {
            public Program Program = null!;
            public SourceLibrary? Source;
            public List<string> SourceFiles = new List<string>();
            public List<string> Notes = new List<string>();
            public List<Diagnostic> Errors = new List<Diagnostic>();
            public List<(string name, string why)> Failed = new List<(string, string)>();
            public double FrontEndMs,
                CodeMs;
        }

        public static Result Load(
            string project,
            string mode,
            string? rdbPath = null,
            string? userDir = null,
            IDictionary<string, string>? texts = null,
            IEnumerable<string>? alwaysSource = null
        )
        {
            var lp = LiveProgram.Open(project, mode, rdbPath, userDir, texts, alwaysSource);
            var r = new Result
            {
                Program = lp.Program,
                Source = lp.Source,
                Errors = lp.Errors,
                FrontEndMs = lp.FrontEndMs,
                CodeMs = lp.CodeMs,
            };
            r.SourceFiles.AddRange(lp.SourceFiles);
            r.Notes.AddRange(lp.Notes);
            r.Failed.AddRange(lp.Failed);
            return r;
        }
    }
}
