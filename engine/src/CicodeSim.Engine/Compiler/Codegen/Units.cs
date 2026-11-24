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
                func0 ?? Func0Rows.LoadOrRuntime(),
                null,
                false
            );
        }
    }
}
