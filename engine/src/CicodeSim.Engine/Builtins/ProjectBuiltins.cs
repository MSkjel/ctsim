using System;
using CicodeSim.Compiled;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>ProjectInfo: the compiled project list.</summary>
    public static class ProjectBuiltins
    {
        /// <summary>Record offsets of the project fields, by type.</summary>
        private static readonly int[] TypeOffset = { 0x000, 0x041, 0x141, 0x241, 0x445, 0x45b };

        [Builtin(
            "ProjectInfo",
            Doc = "1188608",
            Notes = "The n-th record (0-based) of the running project's compiled project table (_PROJECT.RDB, table 'project': the root project first, then its included projects in the reverse of the compile order, Include and System last; ExampleSA: ExampleSA, IndustrialGraphics_Include, SA_Library, SA_Include, SA_Controls, Include, System); iType 0 NAME, 1 DESC, 2 MAJOR, 3 MINOR (revision texts), 4 DATE, 5 TIME, as stored. iProject is taken as an unsigned 16-bit number: below 0x8000 and below the record count, else ''; another iType ''; no compiled table (an uncompiled project, or a compile without one) ''. The text is cut at 255 characters (strncpy_s into the 0x100-byte result buffer). No error is raised."
        )]
        private static Value ProjectInfo(SimHost h, CicodeTask t, ArgList a)
        {
            int project = (ushort)A.I(a.Get(0));
            int type = (short)A.I(a.Get(1));
            if (project >= 0x8000 || type < 0 || type >= TypeOffset.Length)
                return W.Empty;
            var f = ProcessData.Of(h).CompiledTable("_PROJECT");
            Section? sec = null;
            if (f != null)
                foreach (string n in f.Order)
                    if (
                        string.Equals(n, "project", StringComparison.OrdinalIgnoreCase)
                        && !f.Sections[n].IsPool
                    )
                    {
                        sec = f.Sections[n];
                        break;
                    }
            if (f == null || sec == null || project >= sec.NRec)
                return W.Empty;
            int start = (int)(sec.Data + (long)project * sec.RecSize);
            int at = start + TypeOffset[type];
            int end = Math.Min(start + sec.RecSize, f.Data.Length);
            if (at >= end)
                return W.Empty;
            string s = Cp1252.DecodeZ(f.Data, at, end - at);
            return W.S(s.Length > 255 ? s.Substring(0, 255) : s);
        }
    }
}
