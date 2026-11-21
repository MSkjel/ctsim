using System;
using System.Collections.Generic;
using CicodeSim.Compiled;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.Compiler.Codegen
{
    /// <summary>Compile units for the code generator: a registered project, a project folder
    /// that MASTER.DBF does not list (a copy, compiled with its include.dbf against the User
    /// folder's registered projects), or source texts alone.</summary>
    public static class Units
    {
        /// <summary>The compile unit of the project at ``folder``.</summary>
        public static CompileUnit ForProject(
            string folder,
            string? userDir = null,
            Func0Rows? func0 = null,
            bool useTags = true
        )
        {
            string name;
            string? dir = null;
            if (WinPath.IsDir(folder))
            {
                dir = WinPath.Clean(WinPath.Full(folder)).TrimEnd('\\', '/');
                name = WinPath.BaseName(dir);
                if (userDir == null)
                {
                    string? parent = WinPath.DirName(dir);
                    userDir =
                        parent != null && Unit0.Find(parent, "master.dbf") != null ? parent : DefaultUser;
                }
            }
            else
                name = folder;
            var reg = new Registry(userDir ?? DefaultUser);
            var p = reg.Get(name);
            if (dir != null && (p == null || p.Folder == null || WinPath.Key(p.Folder) != WinPath.Key(dir)))
                reg.Add(new SourceProject(name, dir));
            var (order, diags) = reg.CompileOrder(name);
            return new CompileUnit(order, func0, diags, useTags);
        }

        public const string DefaultUser = @"C:\ProgramData\AVEVA Plant SCADA 2023 R2\User";

        /// <summary>A unit of source texts only (file name, text), no labels, includes or tags.</summary>
        public static CompileUnit ForTexts(
            string project,
            IList<KeyValuePair<string, string>> files,
            Func0Rows? func0 = null
        )
        {
            var p = new SourceProject(
                project,
                null,
                new List<KeyValuePair<string, string>>(files),
                new List<KeyValuePair<string, string>>(),
                new List<string>(),
                new List<KeyValuePair<string, string>>(),
                new Dictionary<string, int>(StringComparer.Ordinal)
            );
            return new CompileUnit(
                new List<SourceProject> { p },
                func0 ?? Func0OrRuntimeTable(),
                null,
                false
            );
        }

        /// <summary>Bin\FUNC0.DBF when installed, else the compiler's view of the runtime's own
        /// built-in table (data/builtins_table.json): names, OP_CODEs, argument and result types,
        /// no OPTARGS defaults, so a call must give every argument.</summary>
        public static Func0Rows Func0OrRuntimeTable()
        {
            var t = Func0Rows.Load();
            return t.Count > 0 ? t : FromRuntimeTable();
        }

        private static Func0Rows? _runtime;

        public static Func0Rows FromRuntimeTable()
        {
            if (_runtime != null)
                return _runtime;
            var table = Func0Table.Load(func0Path: "\u0001no FUNC0");
            var rows = new List<Func0Row>();
            foreach (var b in table.Entries)
            {
                var args = new List<(string, bool)>();
                foreach (char c in b.Args)
                    args.Add(Kind(c));
                var defaults = new List<string?>();
                foreach (var _ in args)
                    defaults.Add(null);
                rows.Add(new Func0Row(b.Name, b.Index, RetWord(b.Ret), args, defaults, 0));
            }
            return _runtime = new Func0Rows(rows);
        }

        /// <summary>A runtime signature char (chr(0x30 + type)) as FUNC0's (kind, var).</summary>
        private static (string, bool) Kind(char c)
        {
            switch (c)
            {
                case '1':
                    return ("INT", false);
                case '2':
                    return ("REAL", false);
                case '4':
                    return ("LONG", false);
                case '5':
                    return ("LONG", true);
                case '6':
                    return ("REAL", true);
                case '7':
                    return ("STRING", false);
                case '8':
                    return ("STRING", true);
                case '9':
                    return ("FUNCTION", false);
                case '<':
                    return ("OBJECT", false);
                case '=':
                    return ("VARIANT", false);
                case '?':
                    return ("VARIANT", false);
                case '@':
                    return ("VARARG", false);
                case 'C':
                    return ("TIMESTAMP", false);
                case 'D':
                    return ("QUALITY", false);
            }
            return ("INT", false);
        }

        private static string RetWord(int t)
        {
            switch (t)
            {
                case Types.Int16:
                    return "INT";
                case Types.Long:
                    return "LONG";
                case Types.Real:
                    return "REAL";
                case Types.String:
                    return "STRING";
                case Types.Void:
                    return "VOID";
                case Types.Object:
                    return "OBJECT";
                case Types.Variant:
                    return "VARIANT";
                case Types.Timestamp:
                    return "TIMESTAMP";
                case Types.Quality:
                    return "QUALITY";
                case Types.ULong:
                    return "ULONG";
            }
            return "INT";
        }
    }
}
