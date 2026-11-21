using System;
using System.Collections.Generic;
using System.IO;
using CicodeSim.Builtins;
using CicodeSim.Compiled;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.World.Display;

namespace CicodeSim.Page
{
    /// <summary>The genies DspSym places (PageRun.PlaceSymbol): the element from its library in
    /// the unit's project folders, its rows from the project's '_' + SHA-1 .RDB.</summary>
    public sealed partial class Animator
    {
        private readonly Dictionary<string, (DateTime Time, long Size, CompiledPage Page)> _symbolCode =
            new Dictionary<string, (DateTime, long, CompiledPage)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The compiled rows of a genie placed with DspSym, null when the project has none
        /// (it is no genie marked for use with DspSym, or the project is not compiled).</summary>
        public CompiledPage? SymbolCode(string reference)
        {
            string name = LibraryElements.CompiledName(reference);
            string? path = Display.Catalogue?.RdbPath(name);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                path = CicodeSim.Sim.PageTags.FindRdb(World.Unit, name);
            if (path == null)
                return null;
            var fi = new FileInfo(path);
            if (
                _symbolCode.TryGetValue(path, out var had)
                && had.Time == fi.LastWriteTimeUtc
                && had.Size == fi.Length
            )
                return had.Page;
            try
            {
                var cp = CompiledPage.Load(path);
                _symbolCode[path] = (fi.LastWriteTimeUtc, fi.Length, cp);
                return cp;
            }
            catch (Exception e) when (e is RdbError || e is IOException)
            {
                LogLine("DspSym " + reference + ": " + e.Message);
                return null;
            }
        }

        /// <summary>The 'library.element' a DspSym type names.</summary>
        public string SymbolReference(string text, int type)
        {
            if (type < 2 || type > 5)
                return CitectText.Trim(text);
            string col = type == 2 || type == 4 ? "LIBOBJNAME" : "THUMBNAIL";
            string want = CitectText.Trim(text);
            foreach (var (_, r) in World.Unit.Rows(type <= 3 ? "almprior.dbf" : "almmodes.dbf"))
            {
                string key = CitectText.Trim(r.Get(type <= 3 ? "PRIORITY" : "DISPNAME"));
                if (
                    !string.Equals(
                        key,
                        want,
                        type <= 3 ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase
                    )
                )
                    continue;
                string lib = CitectText.Trim(r.Get("LIBNAME")),
                    el = CitectText.Trim(r.Get(col));
                return lib.Length > 0 && el.Length > 0 ? lib + "." + el : "";
            }
            return "";
        }

        JsonValue? ISymbolPlacer.PlaceSymbol(Window w, int an, string text, int type)
        {
            if (!(w.Page?.Runtime is PageRun run) || run.Window != w || run.Closed)
                return null;
            string reference = SymbolReference(text, type);
            if (reference.Length == 0)
            {
                run.RemoveSymbol(an);
                return null;
            }
            var code = SymbolCode(reference);
            if (code == null)
            {
                run.RemoveSymbol(an);
                return null;
            }
            var el = LibraryElements.Find(World.Unit, reference);
            if (el == null)
            {
                LogLine(
                    "DspSym "
                        + reference
                        + ": the genie's compiled rows are there, its library element is not"
                );
                run.RemoveSymbol(an);
                return null;
            }
            return run.PlaceSymbol(an, reference, el, code);
        }

        void ISymbolPlacer.SymbolMoved(Window w, int an, int dx, int dy)
        {
            if (w.Page?.Runtime is PageRun run && run.Window == w)
                run.MoveSymbol(an, dx, dy);
        }

        void ISymbolPlacer.SymbolGone(Window w, int an)
        {
            if (w.Page?.Runtime is PageRun run && run.Window == w)
                run.RemoveSymbol(an);
        }
    }
}
