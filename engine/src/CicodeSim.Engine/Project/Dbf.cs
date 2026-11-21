using System;
using System.Collections.Generic;
using System.IO;

namespace CicodeSim.Project
{
    /// <summary>The field layout of one dBASE table.</summary>
    public sealed class DbfHeader
    {
        public string[] Names { get; }
        public int[] Offsets { get; }
        public int[] Lengths { get; }
        public int HeaderSize { get; }
        public int RecordSize { get; }

        /// <summary>min(header record count, whole records in the file).</summary>
        public int Count { get; }

        /// <summary>1 + the field lengths (the bytes of a record the fields cover).</summary>
        public int FieldsEnd { get; }
        private readonly Dictionary<string, int> _last;

        internal DbfHeader(string[] names, int[] offsets, int[] lengths, int hdr, int rec, int count, int end)
        {
            Names = names;
            Offsets = offsets;
            Lengths = lengths;
            HeaderSize = hdr;
            RecordSize = rec;
            Count = count;
            FieldsEnd = end;
            _last = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int k = 0; k < names.Length; k++)
                _last[names[k]] = k; // the LAST field of a name wins
        }

        /// <summary>The index of the last field named exactly <paramref name="name"/> (-1: none).</summary>
        public int IndexOf(string name) => _last.TryGetValue(name, out int k) ? k : -1;

        /// <summary>The field to read for a requested column: as the header spells it, else the
        /// case-insensitive match, -1 when absent.</summary>
        public int IndexOfLoose(string column)
        {
            int k = IndexOf(column);
            if (k >= 0)
                return k;
            string up = CitectText.Upper(column);
            for (int i = 0; i < Names.Length; i++)
            {
                if (CitectText.Upper(Names[i]) == up)
                    return IndexOf(Names[i]);
            }
            return -1;
        }
    }

    /// <summary>One row of a table as {FIELD: value} (values decoded and stripped); a
    /// duplicate field name gives the last field's value. Shared rows are read-only.</summary>
    public sealed class DbfRow
    {
        public DbfHeader Header { get; }
        private readonly string[] _v;

        internal DbfRow(DbfHeader h, string[] v)
        {
            Header = h;
            _v = v;
        }

        /// <summary>The value of the field named exactly <paramref name="name"/> (case-sensitive),
        /// <paramref name="def"/> when there is none.</summary>
        public string Get(string name, string def = "")
        {
            int k = Header.IndexOf(name);
            return k < 0 ? def : _v[k];
        }

        public bool Has(string name) => Header.IndexOf(name) >= 0;

        /// <summary>The values by field position (a duplicate name's earlier field included).</summary>
        public string this[int field] => _v[field];

        public int FieldCount => _v.Length;
    }

    /// <summary>A reusable view of the current record while a table is streamed.</summary>
    public sealed class DbfRecord
    {
        internal byte[] Buf = Array.Empty<byte>();
        internal int Off;
        public DbfHeader Header { get; internal set; } = null!;
        public int Row { get; internal set; }

        /// <summary>The text of field k: decoded, then trimmed of blanks.</summary>
        public string Field(int k)
        {
            if (k < 0)
                return "";
            return Dbf.DecodeStrip(Buf, Off + Header.Offsets[k], Header.Lengths[k], null);
        }

        public string Field(int k, StringInterner? share)
        {
            if (k < 0)
                return "";
            return Dbf.DecodeStrip(Buf, Off + Header.Offsets[k], Header.Lengths[k], share);
        }

        /// <summary>The value of the last field so named, "" when there is none.</summary>
        public string Get(string name, StringInterner? share = null) => Field(Header.IndexOf(name), share);

        /// <summary>The bytes of field k after the strip (the record buffer, offset, length; 0
        /// for a missing field): what <see cref="Field(int)"/> decodes, without a string.</summary>
        public void Span(int k, out byte[] buf, out int off, out int len)
        {
            buf = Buf;
            if (k < 0)
            {
                off = 0;
                len = 0;
                return;
            }
            Dbf.StripSpan(Buf, Off + Header.Offsets[k], Header.Lengths[k], out off, out len);
        }
    }

    /// <summary>Equal strings made one object while a table is read: looked up by the field's
    /// bytes, so a repeated value costs no allocation.</summary>
    public sealed class StringInterner
    {
        private string?[] _slots = new string?[1024];
        private int[] _hashes = new int[1024];
        private int _count;

        public int Count => _count;

        internal string Get(byte[] b, int off, int len)
        {
            uint h = 2166136261;
            for (int i = 0; i < len; i++)
            {
                h ^= b[off + i];
                h *= 16777619;
            }
            int hash = (int)(h & 0x7FFFFFFF);
            int mask = _slots.Length - 1;
            int s = hash & mask;
            var t = TextCodec.Decode1252;
            while (true)
            {
                string? e = _slots[s];
                if (e == null)
                    break;
                if (_hashes[s] == hash && e.Length == len)
                {
                    int i = 0;
                    while (i < len && e[i] == t[b[off + i]])
                        i++;
                    if (i == len)
                        return e;
                }
                s = (s + 1) & mask;
            }
            string v = TextCodec.Decode(b, off, len);
            _slots[s] = v;
            _hashes[s] = hash;
            if (++_count * 2 > _slots.Length)
                Grow();
            return v;
        }

        private void Grow()
        {
            var os = _slots;
            var oh = _hashes;
            _slots = new string?[os.Length * 2];
            _hashes = new int[os.Length * 2];
            int mask = _slots.Length - 1;
            for (int i = 0; i < os.Length; i++)
            {
                string? e = os[i];
                if (e == null)
                    continue;
                int s = oh[i] & mask;
                while (_slots[s] != null)
                    s = (s + 1) & mask;
                _slots[s] = e;
                _hashes[s] = oh[i];
            }
        }
    }

    /// <summary>Reads the project dBASE tables.</summary>
    public static class Dbf
    {
        private const int StreamMin = 1 << 20;
        private const int Chunk = 4 << 20;
        private const byte Deleted = 0x2A;
        private const byte FieldTerm = 0x0D;

        // the blank bytes (CitectText.IsWhite)
        private static readonly bool[] SpaceByte = BuildSpace();
        private const ulong Blanks8 = 0x2020202020202020UL;

        private static bool[] BuildSpace()
        {
            var s = new bool[256];
            for (int i = 0; i < 256; i++)
                s[i] = CitectText.IsWhite(TextCodec.Decode1252[i]);
            return s;
        }

        internal static void StripSpan(byte[] b, int off, int len, out int start, out int n)
        {
            int a = off,
                e = off + len;
            if (e > b.Length)
                e = b.Length;
            var sp = SpaceByte;
            // blank padding eight bytes at a time (a field of a big variable.dbf is mostly
            // blanks: 254-byte CUSTOM fields), then byte by byte
            while (a + 8 <= e && BitConverter.ToUInt64(b, a) == Blanks8)
                a += 8;
            while (a < e && sp[b[a]])
                a++;
            while (e - 8 >= a && BitConverter.ToUInt64(b, e - 8) == Blanks8)
                e -= 8;
            while (e > a && sp[b[e - 1]])
                e--;
            start = a;
            n = Math.Max(0, e - a);
        }

        internal static string DecodeStrip(byte[] b, int off, int len, StringInterner? share)
        {
            int a = off,
                e = off + len;
            if (e > b.Length)
                e = b.Length;
            var sp = SpaceByte;
            while (a + 8 <= e && BitConverter.ToUInt64(b, a) == Blanks8)
                a += 8;
            while (a < e && sp[b[a]])
                a++;
            while (e - 8 >= a && BitConverter.ToUInt64(b, e - 8) == Blanks8)
                e -= 8;
            while (e > a && sp[b[e - 1]])
                e--;
            int n = e - a;
            if (n <= 0)
                return "";
            return share != null ? share.Get(b, a, n) : TextCodec.Decode(b, a, n);
        }

        // ------------------------------------------------------------------ reading
        private sealed class Source : IDisposable
        {
            public FileStream? Fh;
            public byte[]? Data;
            public long Size;

            public byte[] ReadAt(long off, int n)
            {
                if (Data != null)
                {
                    int m = (int)Math.Max(0, Math.Min(n, Size - off));
                    var r = new byte[m];
                    if (m > 0)
                        Buffer.BlockCopy(Data, (int)off, r, 0, m);
                    return r;
                }
                Fh!.Seek(off, SeekOrigin.Begin);
                var buf = new byte[n];
                int got = 0;
                while (got < n)
                {
                    int k = Fh.Read(buf, got, n - got);
                    if (k <= 0)
                        break;
                    got += k;
                }
                if (got < n)
                    Array.Resize(ref buf, got);
                return buf;
            }

            public void Dispose() => Fh?.Dispose();
        }

        // The read buffers of the outermost ForEach of a thread are reused (a 37-table unit would
        // otherwise leave a 4 MB chunk and a whole small table per table on the large object
        // heap); a nested ForEach (a label read while a table is walked) gets its own.
        [ThreadStatic]
        private static byte[]? _poolWhole;

        [ThreadStatic]
        private static byte[]? _poolChunk;

        [ThreadStatic]
        private static int _depth;

        private static byte[] ReadBuffer(ref byte[]? pool, int n, bool pooled)
        {
            if (!pooled)
                return new byte[n];
            if (pool == null || pool.Length < n)
                pool = new byte[Math.Max(n, 1 << 16)];
            return pool;
        }

        private static Source? Open(string path, bool pooled = false)
        {
            try
            {
                var fh = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    1 << 16
                );
                long size = fh.Length;
                var src = new Source { Fh = fh, Size = size };
                if (size < StreamMin)
                {
                    var data = ReadBuffer(ref _poolWhole, (int)size, pooled);
                    int got = 0;
                    while (got < size)
                    {
                        int k = fh.Read(data, got, (int)size - got);
                        if (k <= 0)
                            break;
                        got += k;
                    }
                    src.Data = data;
                    src.Size = got;
                }
                return src;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static DbfHeader? ParseHeader(Source f)
        {
            long n = f.Size;
            if (n < 33)
                return null;
            byte[] top = f.ReadAt(0, 12);
            if (top.Length < 12)
                return null;
            uint nrec = BitConverter.ToUInt32(top, 4);
            int hdr = BitConverter.ToUInt16(top, 8);
            int rec = BitConverter.ToUInt16(top, 10);
            if (rec <= 0 || hdr < 33 || hdr > n)
                return null;
            byte[] head = f.ReadAt(0, hdr);
            if (head.Length < hdr)
                return null;
            var names = new List<string>();
            var offsets = new List<int>();
            var lengths = new List<int>();
            int off = 32,
                pos = 1;
            while (off + 32 <= hdr && head[off] != FieldTerm)
            {
                int z = 0;
                while (z < 11 && head[off + z] != 0)
                    z++;
                names.Add(CitectText.Trim(TextCodec.Decode(head, off, z)));
                int ln = head[off + 16];
                offsets.Add(pos);
                lengths.Add(ln);
                pos += ln;
                off += 32;
            }
            if (names.Count == 0 || pos > rec)
                return null;
            long whole = (n - hdr) / rec;
            int count = (int)Math.Min(nrec, whole);
            return new DbfHeader(names.ToArray(), offsets.ToArray(), lengths.ToArray(), hdr, rec, count, pos);
        }

        /// <summary>The header of a table, null when it cannot be read or is not a table.</summary>
        public static DbfHeader? Header(string path)
        {
            using var f = Open(path);
            return f == null ? null : ParseHeader(f);
        }

        /// <summary>Calls <paramref name="each"/> for every row that is not deleted, in file
        /// order (the view is valid during the call). Returns the header, null when the table
        /// cannot be read (nothing is called).</summary>
        public static DbfHeader? ForEach(string path, Action<DbfRecord> each)
        {
            _depth++;
            try
            {
                return ForEachCore(path, each, _depth == 1);
            }
            finally
            {
                _depth--;
            }
        }

        private static DbfHeader? ForEachCore(string path, Action<DbfRecord> each, bool pooled)
        {
            using var f = Open(path, pooled);
            if (f == null)
                return null;
            DbfHeader? h = ParseHeader(f);
            if (h == null)
                return null;
            var rec = new DbfRecord { Header = h };
            int rs = h.RecordSize;
            if (f.Data != null)
            {
                int k = (int)Math.Min(h.Count, Math.Max(0, (f.Size - h.HeaderSize) / rs));
                rec.Buf = f.Data;
                for (int i = 0; i < k; i++)
                {
                    int o = h.HeaderSize + i * rs;
                    if (f.Data[o] == Deleted)
                        continue;
                    rec.Off = o;
                    rec.Row = i;
                    each(rec);
                }
                return h;
            }
            int per = Math.Max(1, Chunk / rs);
            var buf = ReadBuffer(ref _poolChunk, per * rs, pooled);
            f.Fh!.Seek(h.HeaderSize, SeekOrigin.Begin);
            int left = h.Count,
                row = 0;
            rec.Buf = buf;
            while (left > 0)
            {
                int want = Math.Min(per, left);
                int need = want * rs,
                    got = 0;
                while (got < need)
                {
                    int r = f.Fh.Read(buf, got, need - got);
                    if (r <= 0)
                        break;
                    got += r;
                }
                int recs = got / rs;
                for (int i = 0; i < recs; i++)
                {
                    int o = i * rs;
                    if (buf[o] != Deleted)
                    {
                        rec.Off = o;
                        rec.Row = row;
                        each(rec);
                    }
                    row++;
                }
                if (recs < want)
                    break;
                left -= recs;
            }
            return h;
        }

        /// <summary>Every row as {FIELD: value} (a fresh list;
        /// empty when the file cannot be read or is not a table).</summary>
        public static List<DbfRow> CitectRows(string path)
        {
            var rows = new List<DbfRow>();
            DbfHeader? hh = null;
            string[]? spanNames = null;
            ForEach(path, r =>
            {
                if (hh == null)
                {
                    hh = r.Header;
                    spanNames = hh.Names;
                }
                var v = new string[hh.Names.Length];
                for (int k = 0; k < v.Length; k++)
                    v[k] = r.Field(k);
                rows.Add(new DbfRow(hh, v));
            });
            return rows;
        }

        /// <summary>Each row as the values of
        /// <paramref name="columns"/> ('' for a column the table lacks).</summary>
        public static List<string[]> CitectRows(
            string path,
            string[] columns,
            string def = "",
            StringInterner? share = null
        )
        {
            var rows = new List<string[]>();
            int[]? picks = null;
            ForEach(path, r =>
            {
                if (picks == null)
                {
                    picks = new int[columns.Length];
                    for (int c = 0; c < columns.Length; c++)
                        picks[c] = r.Header.IndexOf(columns[c]);
                }
                var v = new string[columns.Length];
                for (int c = 0; c < v.Length; c++)
                    v[c] = picks[c] < 0 ? def : r.Field(picks[c], share);
                rows.Add(v);
            });
            return rows;
        }

        /// <summary>The field names of a table's header, empty when unreadable.</summary>
        public static string[] FieldNames(string path)
        {
            DbfHeader? h = Header(path);
            return h == null ? Array.Empty<string>() : h.Names;
        }

        // ------------------------------------------------------------------ caches
        private static readonly object Lock = new object();
        private const int MaxEntries = 64;
        private const long MaxCells = 2_500_000;
        private static readonly LinkedList<(string key, IReadOnlyList<DbfRow> rows, long w)> RowCache = new();
        private static readonly Dictionary<
            string,
            LinkedListNode<(string key, IReadOnlyList<DbfRow> rows, long w)>
        > RowIndex = new();
        private static long _cells;

        /// <summary>(normalised path, mtime, size) of a file, null when it cannot be stat'ed.</summary>
        public static string? Stamp(string path)
        {
            var st = WinPath.Stat(path);
            if (st == null)
                return null;
            return WinPath.Key(path) + "|" + st.Value.mtime + "|" + st.Value.size;
        }

        /// <summary>dbfcols.rows: <see cref="CitectRows(string)"/> cached while the file's stamp
        /// is unchanged (shared, read-only).</summary>
        public static IReadOnlyList<DbfRow> Rows(string path)
        {
            string? st = Stamp(path);
            if (st == null)
                return CitectRows(path);
            lock (Lock)
            {
                if (RowIndex.TryGetValue(st, out var node))
                {
                    RowCache.Remove(node);
                    RowCache.AddLast(node);
                    return node.Value.rows;
                }
            }
            var rows = CitectRows(path);
            if (Stamp(path) != st)
                return rows;
            long w = Math.Max(1, rows.Count * (rows.Count > 0 ? rows[0].FieldCount : 1));
            lock (Lock)
            {
                if (RowIndex.TryGetValue(st, out var node))
                    return node.Value.rows;
                var n = RowCache.AddLast((st, rows, w));
                RowIndex[st] = n;
                _cells += w;
                while (RowCache.Count > 1 && (RowCache.Count > MaxEntries || _cells > MaxCells))
                {
                    var old = RowCache.First!;
                    RowCache.RemoveFirst();
                    RowIndex.Remove(old.Value.key);
                    _cells -= old.Value.w;
                }
            }
            return rows;
        }

        private static readonly LinkedList<ColumnTable> ColCache = new();
        private static readonly Dictionary<string, LinkedListNode<ColumnTable>> ColIndex = new();
        private static long _colBytes;

        /// <summary>The column tables' memory budget (an estimate of the bytes they hold).</summary>
        public static long ColumnBudget = 320L << 20;

        /// <summary>The columns of the table at <paramref name="path"/>,
        /// kept while its stamp is unchanged; null when the file cannot be stat'ed.
        /// <paramref name="unique"/> = columns whose values are mostly distinct (not shared).</summary>
        public static ColumnTable? Columns(string path, string[] columns, string[]? unique = null)
        {
            string? st = Stamp(path);
            if (st == null)
                return null;
            var key = st + "|" + string.Join(",", Array.ConvertAll(columns, CitectText.Upper));
            lock (Lock)
            {
                if (ColIndex.TryGetValue(key, out var node))
                {
                    ColCache.Remove(node);
                    ColCache.AddLast(node);
                    return node.Value;
                }
            }
            var t = BuildColumns(path, columns, unique ?? new[] { "NAME" });
            t.Key = key;
            if (Stamp(path) == st)
            {
                lock (Lock)
                {
                    if (ColIndex.TryGetValue(key, out var node))
                        return node.Value;
                    ColIndex[key] = ColCache.AddLast(t);
                    _colBytes += t.Weight;
                    while (ColCache.Count > 1 && _colBytes > ColumnBudget)
                    {
                        var old = ColCache.First!.Value;
                        ColCache.RemoveFirst();
                        ColIndex.Remove(old.Key);
                        _colBytes -= old.Weight;
                    }
                }
            }
            return t;
        }

        private static ColumnTable BuildColumns(string path, string[] columns, string[] unique)
        {
            var uniq = new HashSet<string>(Array.ConvertAll(unique, CitectText.Upper));
            var cols = new List<string>[columns.Length];
            for (int c = 0; c < cols.Length; c++)
                cols[c] = new List<string>();
            var shares = new StringInterner?[columns.Length];
            for (int c = 0; c < columns.Length; c++)
                shares[c] = uniq.Contains(CitectText.Upper(columns[c])) ? null : new StringInterner();
            int[]? picks = null;
            long weight = 200;
            ForEach(path, r =>
            {
                if (picks == null)
                {
                    picks = new int[columns.Length];
                    for (int c = 0; c < columns.Length; c++)
                        picks[c] = r.Header.IndexOfLoose(columns[c]);
                }
                for (int c = 0; c < columns.Length; c++)
                {
                    string v = picks[c] < 0 ? "" : r.Field(picks[c], shares[c]);
                    cols[c].Add(v);
                    if (shares[c] == null)
                        weight += 8 + 26 + 2 * v.Length;
                }
            });
            for (int c = 0; c < columns.Length; c++)
                weight += 8L * cols[c].Count;
            var arrays = new string[columns.Length][];
            for (int c = 0; c < columns.Length; c++)
            {
                arrays[c] = cols[c].ToArray();
                if (shares[c] != null)
                    weight += 40L * shares[c]!.Count;
            }
            return new ColumnTable(path, columns, arrays) { Weight = weight };
        }

        /// <summary>Forget every cached table (dbfcols.clear).</summary>
        public static void ClearCache()
        {
            lock (Lock)
            {
                RowCache.Clear();
                RowIndex.Clear();
                _cells = 0;
                ColCache.Clear();
                ColIndex.Clear();
                _colBytes = 0;
            }
        }

        /// <summary>The estimated memory the cached column tables hold.</summary>
        public static long ColumnBytes
        {
            get
            {
                lock (Lock)
                    return _colBytes;
            }
        }
    }

    /// <summary>Some columns of one table, column by column ('citect' values: stripped text,
    /// '' for a column the table lacks; deleted rows left out). Shared: read-only.</summary>
    public sealed class ColumnTable
    {
        public string Path { get; }
        public string[] ColumnNames { get; }
        private readonly Dictionary<string, string[]> _cols;
        public int Rows { get; }
        public long Weight { get; internal set; }
        internal string Key = "";

        public ColumnTable(string path, string[] columns, string[][] cols)
        {
            Path = path;
            ColumnNames = columns;
            _cols = new Dictionary<string, string[]>(StringComparer.Ordinal);
            for (int c = 0; c < columns.Length; c++)
                _cols[CitectText.Upper(columns[c])] = cols[c];
            Rows = cols.Length > 0 ? cols[0].Length : 0;
        }

        /// <summary>The column's values ('' in every row for a column not read).</summary>
        public string[] Column(string name)
        {
            string up = CitectText.Upper(name);
            lock (_cols)
            {
                if (!_cols.TryGetValue(up, out var col))
                {
                    col = new string[Rows];
                    for (int i = 0; i < col.Length; i++)
                        col[i] = "";
                    _cols[up] = col;
                }
                return col;
            }
        }

        public bool Has(string name) => _cols.ContainsKey(CitectText.Upper(name));

        /// <summary>A table made in memory (not cached).</summary>
        public static ColumnTable FromColumns(string[] columns, string[][] cols, string path = "") =>
            new ColumnTable(path, columns, cols);
    }
}
