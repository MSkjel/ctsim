using System;
using System.Collections.Generic;
using CicodeSim.Compiled;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>The compiled tables (RdbOpen and the other Rdb built-ins).</summary>
    public static class RdbTables
    {
        internal sealed class Cursor
        {
            public RdbFile File = null!;
            public Section Table = null!;
            public long Rec = -1;
            public PageCode? Code;

            /// <summary>RdbSet's values: (record, FIELD) to the text (the file is never written).</summary>
            public readonly Dictionary<(long, string), string> Set = new Dictionary<(long, string), string>();
        }

        private static HandleTable<Cursor> Table(SimHost h) => ProcessData.Of(h).RdbCursors;

        private static Cursor? Get(SimHost h, in Value v) => Table(h).Get(A.Short(A.I(v)));

        /// <summary>The table of a compiled file: the section named like the file without its
        /// leading '_' (any case), else the last table section.</summary>
        internal static Section? TableOf(RdbFile f, string name)
        {
            string want = name.StartsWith("_", StringComparison.Ordinal) ? name.Substring(1) : name;
            foreach (string n in f.Order)
                if (string.Equals(n, want, StringComparison.OrdinalIgnoreCase) && !f.Sections[n].IsPool)
                    return f.Sections[n];
            return ProcessData.TableSection(f);
        }

        /// <summary>A record field as the runtime writes it: text as is, numbers in decimal,
        /// a digital (type 7) '1' / '0'; null for an unknown field.</summary>
        internal static string? FieldText(Cursor c, string field)
        {
            if (c.Rec < 0 || c.Rec >= c.Table.NRec)
                return "";
            FieldDesc? fd = c.Table.Fields.Find(x =>
                string.Equals(x.Name, field, StringComparison.OrdinalIgnoreCase)
            );
            if (fd == null)
                return null;
            if (c.Set.TryGetValue((c.Rec, fd.Name), out string? s))
                return s;
            int start = (int)(c.Table.Data + c.Rec * c.Table.RecSize);
            byte[] b = c.File.Data;
            int at = start + fd.Offset;
            bool fits(int n) => fd.Offset + n <= c.Table.RecSize && at + n <= b.Length;
            // by field type: 1 text, 2 inline chars, 3 / 6 i16 (decimal),
            // 4 / 0xb / 0x14 i32 (decimal), 5 f32 and 0x1c f64 ('%f'), 7 a digital
            // '1' / '0', 8 u8; other types ''
            switch (fd.Type)
            {
                case 1:
                case 2:
                    return c.File.DecodeField(fd, start, c.Table.RecSize) as string ?? "";
                case 3:
                case 6:
                    return fits(2) ? C.Inv(BitConverter.ToInt16(b, at)) : "";
                case 4:
                case 0xb:
                case 0x14:
                    return fits(4) ? C.Inv(BitConverter.ToInt32(b, at)) : "";
                case 5:
                    return fits(4) ? Printf.Format("%f", PrintfArg.Float(BitConverter.ToSingle(b, at))) : "";
                case 0x1c:
                    return fits(8) ? Printf.Format("%f", PrintfArg.Float(BitConverter.ToDouble(b, at))) : "";
                case 7:
                    return fits(1) ? (b[at] != 0 ? "1" : "0") : "";
                case 8:
                    return fits(1) ? C.Inv(b[at]) : "";
            }
            return "";
        }

        /// <summary>Run a code block of the file nested in the caller; its result, null when
        /// it gave none.</summary>
        internal static Value? RunBlock(SimHost h, CicodeTask t, byte[] code, Program program, string name)
        {
            if (h.Nesting >= SimHost.MaxNesting)
            {
                t.Vm.Fatal(
                    t,
                    Errors.Stack,
                    "Cicode stack overflow (nested calls deeper than " + W.Inv(SimHost.MaxNesting) + ")"
                );
                return null;
            }
            var x = t.Vm.TaskForBlock(code, program, t.Foreground, name);
            h.Nesting++;
            try
            {
                t.Vm.Run(x, 1000);
            }
            catch (InvalidOperationException) { }
            finally
            {
                h.Nesting--;
            }
            // a 295 in the nested block halts the caller too (W.RunNested's rule: one shared stack)
            if (x.State == TaskState.Halt && x.HaltCode == Errors.Stack)
                t.Vm.Fatal(t, Errors.Stack, x.HaltReason);
            return x.State == TaskState.Stop ? x.Result : null;
        }

        /// <summary>The code block of (table, record, field), null when there is none.</summary>
        internal static PageBlock? Block(Cursor c, string table, long rec, string field)
        {
            c.Code ??= Rdb.PageFromRdb(c.File);
            foreach (var b in c.Code.Blocks)
                if (
                    b.Record == rec
                    && string.Equals(b.Field, field, StringComparison.OrdinalIgnoreCase)
                    && b.Table == table
                )
                    return b;
            return null;
        }

        [Builtin(
            "RdbOpen",
            Notes = "The running project's '<sName>.RDB' and its table; a handle, -1 when the file or its table is missing (undocumented built-in)."
        )]
        private static Value RdbOpen(SimHost h, CicodeTask t, ArgList a)
        {
            string name = CitectText.Trim(A.S(a.Get(0)));
            if (name.Length == 0 || name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
                return W.I(-1);
            var d = ProcessData.Of(h);
            var f = d.CompiledTable(name);
            if (f == null)
                return W.I(-1);
            var sec = TableOf(f, name);
            if (sec == null)
                return W.I(-1);
            return W.I(Table(h).Add(new Cursor { File = f, Table = sec }));
        }

        [Builtin("RdbClose", Errors = "274 a bad handle (returned)")]
        private static Value RdbClose(SimHost h, CicodeTask t, ArgList a)
        {
            long hd = A.Short(A.I(a.Get(0)));
            if (Table(h).Get(hd) == null)
                return W.I(Codes.InvalidArg);
            Table(h).Free(hd);
            return W.I(0);
        }

        [Builtin(
            "RdbEOF",
            Notes = "1 when there is no current record (before First, past the end) or for a bad handle, else 0."
        )]
        private static Value RdbEOF(SimHost h, CicodeTask t, ArgList a)
        {
            var c = Get(h, a.Get(0));
            return W.I(c == null || c.Rec < 0 || c.Rec >= c.Table.NRec ? 1 : 0);
        }

        private static Value Move(SimHost h, ArgList a, Func<Cursor, long> to)
        {
            var c = Get(h, a.Get(0));
            if (c == null)
                return W.I(-1);
            long r = to(c);
            c.Rec = r >= 0 && r < c.Table.NRec ? r : -1;
            return W.I(c.Rec);
        }

        [Builtin("RdbFirstRec", Notes = "The first record (0), -1 for an empty table or a bad handle.")]
        private static Value RdbFirstRec(SimHost h, CicodeTask t, ArgList a) => Move(h, a, c => 0);

        [Builtin("RdbLastRec", Notes = "The last record, -1 for an empty table or a bad handle.")]
        private static Value RdbLastRec(SimHost h, CicodeTask t, ArgList a) =>
            Move(h, a, c => c.Table.NRec - 1);

        [Builtin(
            "RdbNextRec",
            Notes = "The next record (from no record: the first), -1 past the end or for a bad handle."
        )]
        private static Value RdbNextRec(SimHost h, CicodeTask t, ArgList a) => Move(h, a, c => c.Rec + 1);

        [Builtin("RdbPrevRec", Notes = "The record before, -1 before the first or for a bad handle.")]
        private static Value RdbPrevRec(SimHost h, CicodeTask t, ArgList a) =>
            Move(h, a, c => c.Rec < 0 ? -1 : c.Rec - 1);

        [Builtin(
            "RdbPosRec",
            Notes = "The first record then n records on; -1 past the end or for a bad handle."
        )]
        private static Value RdbPosRec(SimHost h, CicodeTask t, ArgList a)
        {
            long n = A.I(a.Get(1));
            return Move(h, a, c => n < 0 ? 0 : n);
        }

        [Builtin("RdbNoRec", Notes = "The number of records; 0 for a bad handle.")]
        private static Value RdbNoRec(SimHost h, CicodeTask t, ArgList a)
        {
            var c = Get(h, a.Get(0));
            return W.I(c == null ? 0 : c.Table.NRec);
        }

        [Builtin(
            "RdbGet",
            Notes = "The current record's field: text, numbers in decimal; an expression field (type 15) is run and its value given as text, a command field (type 16) is run and gives ''; '' for no record, an unknown field or a bad handle."
        )]
        private static Value RdbGet(SimHost h, CicodeTask t, ArgList a)
        {
            var c = Get(h, a.Get(0));
            string field = CitectText.Trim(A.S(a.Get(1)));
            if (c == null || c.Rec < 0 || c.Rec >= c.Table.NRec)
                return W.Empty;
            FieldDesc? fd = c.Table.Fields.Find(x =>
                string.Equals(x.Name, field, StringComparison.OrdinalIgnoreCase)
            );
            if (fd == null)
                return W.Empty;
            if (fd.Type == RdbFile.FExpr || fd.Type == RdbFile.FCode)
            {
                var b = Block(c, c.Table.Name, c.Rec, fd.Name);
                if (b == null)
                    return W.Empty;
                Value? r = RunBlock(h, t, b.Code, c.Code!.Program, c.Table.Name + "." + fd.Name);
                if (fd.Type == RdbFile.FCode || r == null)
                    return W.Empty;
                return W.S(C.Cut(Conv.ValueText(r.Value, out _)));
            }
            return W.S(C.Cut(FieldText(c, fd.Name) ?? ""));
        }

        [Builtin(
            "RdbSet",
            Effects = "sets the current record's field in the process's copy (the file is never written)",
            Errors = "274 a bad handle or no current record (returned)",
            Notes = "Text and number fields take the text (later RdbGet reads it); 0. An unknown field changes nothing (0)."
        )]
        private static Value RdbSet(SimHost h, CicodeTask t, ArgList a)
        {
            var c = Get(h, a.Get(0));
            if (c == null || c.Rec < 0 || c.Rec >= c.Table.NRec)
                return W.I(Codes.InvalidArg);
            string field = CitectText.Trim(A.S(a.Get(1)));
            FieldDesc? fd = c.Table.Fields.Find(x =>
                string.Equals(x.Name, field, StringComparison.OrdinalIgnoreCase)
            );
            if (fd != null && fd.Type != RdbFile.FExpr && fd.Type != RdbFile.FCode)
                c.Set[(c.Rec, fd.Name)] = A.S(a.Get(2));
            return W.I(0);
        }

        [Builtin(
            "RdbFind",
            Notes = "From the record after the current one, the first whose field's text equals sValue without case; its number, -1 when none or for a bad handle."
        )]
        private static Value RdbFind(SimHost h, CicodeTask t, ArgList a)
        {
            var c = Get(h, a.Get(0));
            if (c == null)
                return W.I(-1);
            string field = CitectText.Trim(A.S(a.Get(1)));
            string want = A.S(a.Get(2));
            for (long r = c.Rec + 1; r < c.Table.NRec; r++)
            {
                c.Rec = r;
                string? v = FieldText(c, field);
                if (v != null && C.IEq(v, want))
                    return W.I(r);
            }
            c.Rec = -1;
            return W.I(-1);
        }
    }
}
