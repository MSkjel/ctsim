using System;
using System.Collections.Generic;
using CicodeSim.Project;

namespace CicodeSim.World
{
    /// <summary>The name and Equip.Item index of one variable.dbf.</summary>
    public sealed class TagIndex
    {
        private const int PageBits = 16;
        private const int PageSize = 1 << PageBits; // below the large object heap's 85 KB
        private static readonly char[] Lower1252 = BuildLower();

        private static char[] BuildLower()
        {
            var t = new char[256];
            for (int i = 0; i < 256; i++)
                t[i] = char.ToLowerInvariant(TextCodec.Decode1252[i]);
            return t;
        }

        public int Rows { get; private set; }
        private byte[][] _pages = Array.Empty<byte[]>();
        private int[] _nameAt = Array.Empty<int>(); // page << 16 | offset
        private byte[] _nameLen = Array.Empty<byte>(); // a DBF field is at most 255 bytes
        private int[] _equipId = Array.Empty<int>(),
            _itemId = Array.Empty<int>();
        private string[] _equipPool =  { "" },
            _itemPool =  { "" };
        private int[] _nameSlots = new int[16];
        private int[] _itemSlots = new int[16];
        public int NameEntries { get; private set; }
        public int ItemEntries { get; private set; }
        public int[] ItemRows { get; private set; } = Array.Empty<int>();
        internal long Weight { get; private set; }

        private byte[] Page(int row) => _pages[_nameAt[row] >> PageBits];

        private int Off(int row) => _nameAt[row] & (PageSize - 1);

        public string Name(int row) => TextCodec.Decode(Page(row), Off(row), _nameLen[row]);

        public bool HasName(int row) => _nameLen[row] > 0;

        public string Equip(int row) => _equipPool[_equipId[row]];

        public string Item(int row) => _itemPool[_itemId[row]];

        // ------------------------------------------------------------------ hashing
        private static uint Mix(uint h, char c)
        {
            unchecked
            {
                return (h ^ c) * 16777619;
            }
        }

        /// <summary>The hash of a key compared without case (the query in any case).</summary>
        private static int HashQuery(string q)
        {
            uint h = 2166136261;
            foreach (char c in q)
                h = Mix(h, char.ToLowerInvariant(c));
            return (int)(h & 0x7FFFFFFF);
        }

        private int HashName(int row)
        {
            uint h = 2166136261;
            byte[] p = Page(row);
            int a = Off(row),
                n = _nameLen[row];
            for (int i = 0; i < n; i++)
                h = Mix(h, Lower1252[p[a + i]]);
            return (int)(h & 0x7FFFFFFF);
        }

        private int HashItem(int row)
        {
            uint h = 2166136261;
            foreach (char c in _equipPool[_equipId[row]])
                h = Mix(h, char.ToLowerInvariant(c));
            h = Mix(h, '.');
            foreach (char c in _itemPool[_itemId[row]])
                h = Mix(h, char.ToLowerInvariant(c));
            return (int)(h & 0x7FFFFFFF);
        }

        private bool NameIs(int row, string q)
        {
            int n = _nameLen[row];
            if (n != q.Length)
                return false;
            byte[] p = Page(row);
            int a = Off(row);
            for (int i = 0; i < n; i++)
            {
                char x = Lower1252[p[a + i]],
                    c = q[i];
                if (x != c && x != char.ToLowerInvariant(c))
                    return false;
            }
            return true;
        }

        private bool NameEq(int x, int y)
        {
            int n = _nameLen[x];
            if (n != _nameLen[y])
                return false;
            byte[] px = Page(x),
                py = Page(y);
            int a = Off(x),
                b = Off(y);
            for (int i = 0; i < n; i++)
                if (Lower1252[px[a + i]] != Lower1252[py[b + i]])
                    return false;
            return true;
        }

        private bool ItemIs(int row, string q)
        {
            string e = _equipPool[_equipId[row]],
                it = _itemPool[_itemId[row]];
            if (e.Length + 1 + it.Length != q.Length || q[e.Length] != '.')
                return false;
            for (int i = 0; i < e.Length; i++)
                if (char.ToLowerInvariant(e[i]) != char.ToLowerInvariant(q[i]))
                    return false;
            for (int i = 0; i < it.Length; i++)
                if (char.ToLowerInvariant(it[i]) != char.ToLowerInvariant(q[e.Length + 1 + i]))
                    return false;
            return true;
        }

        private bool ItemEq(int x, int y) =>
            CitectText.SameName(_equipPool[_equipId[x]], _equipPool[_equipId[y]])
            && CitectText.SameName(_itemPool[_itemId[x]], _itemPool[_itemId[y]]);

        /// <summary>The first row of a name compared without case (the query in any case), -1
        /// when none.</summary>
        public int Find(string name)
        {
            int mask = _nameSlots.Length - 1,
                s = HashQuery(name) & mask;
            while (_nameSlots[s] != 0)
            {
                int r = _nameSlots[s] - 1;
                if (NameIs(r, name))
                    return r;
                s = (s + 1) & mask;
            }
            return -1;
        }

        /// <summary>The first row whose 'equip.item' is <paramref name="key"/> compared without
        /// case, -1 when none.</summary>
        public int FindItem(string key)
        {
            int mask = _itemSlots.Length - 1,
                s = HashQuery(key) & mask;
            while (_itemSlots[s] != 0)
            {
                int r = _itemSlots[s] - 1;
                if (ItemIs(r, key))
                    return r;
                s = (s + 1) & mask;
            }
            return -1;
        }

        /// <summary>A power of two at least 4/3 of n (the load stays at or below 3/4).</summary>
        private static int Cap(int n)
        {
            int c = 16;
            while (c * 3 < n * 4)
                c <<= 1;
            return c;
        }

        private void BuildTables()
        {
            int names = 0,
                items = 0;
            for (int r = 0; r < Rows; r++)
            {
                if (!HasName(r))
                    continue;
                names++;
                if (_equipId[r] != 0 && _itemId[r] != 0)
                    items++;
            }
            NameEntries = names;
            ItemEntries = items;
            var itemRows = new int[items];
            _nameSlots = new int[Cap(names)];
            _itemSlots = new int[Cap(items)];
            int nmask = _nameSlots.Length - 1,
                imask = _itemSlots.Length - 1,
                k = 0;
            for (int r = 0; r < Rows; r++)
            {
                if (!HasName(r))
                    continue;
                int s = HashName(r) & nmask;
                bool dup = false;
                while (_nameSlots[s] != 0)
                {
                    if (NameEq(_nameSlots[s] - 1, r))
                    {
                        dup = true;
                        break;
                    }
                    s = (s + 1) & nmask;
                }
                if (!dup)
                    _nameSlots[s] = r + 1;
                if (_equipId[r] == 0 || _itemId[r] == 0)
                    continue;
                itemRows[k++] = r;
                s = HashItem(r) & imask;
                dup = false;
                while (_itemSlots[s] != 0)
                {
                    if (ItemEq(_itemSlots[s] - 1, r))
                    {
                        dup = true;
                        break;
                    }
                    s = (s + 1) & imask;
                }
                if (!dup)
                    _itemSlots[s] = r + 1;
            }
            ItemRows = itemRows;
        }

        // ------------------------------------------------------------------ reading
        private static TagIndex Read(string path)
        {
            var ix = new TagIndex();
            // the header's record count bounds the rows (deleted rows are left out)
            int expect = Math.Max(0, Dbf.Header(path)?.Count ?? 0);
            var nameAt = new int[expect];
            var nameLen = new byte[expect];
            var eqIds = new int[expect];
            var itIds = new int[expect];
            var pages = new List<byte[]>();
            byte[] page = new byte[PageSize];
            int pageUsed = 0;
            pages.Add(page);
            var eqShare = new StringInterner();
            var itShare = new StringInterner();
            var eqPool = new List<string> { "" };
            var itPool = new List<string> { "" };
            var eqMap = new Dictionary<string, int>(StringComparer.Ordinal) { [""] = 0 };
            var itMap = new Dictionary<string, int>(StringComparer.Ordinal) { [""] = 0 };
            int fName = -2,
                fEq = -2,
                fIt = -2,
                rows = 0;
            Dbf.ForEach(path, rec =>
            {
                if (fName == -2)
                {
                    fName = rec.Header.IndexOfLoose("NAME");
                    fEq = rec.Header.IndexOfLoose("EQUIP");
                    fIt = rec.Header.IndexOfLoose("ITEM");
                }
                if (rows == nameAt.Length)
                {
                    // more rows than the header said (it changed under us): grow
                    int n2 = Math.Max(16, rows * 2);
                    Array.Resize(ref nameAt, n2);
                    Array.Resize(ref nameLen, n2);
                    Array.Resize(ref eqIds, n2);
                    Array.Resize(ref itIds, n2);
                }
                rec.Span(fName, out byte[] buf, out int off, out int len);
                len = Math.Min(len, 255);
                if (pageUsed + len > PageSize)
                {
                    page = new byte[PageSize];
                    pages.Add(page);
                    pageUsed = 0;
                }
                System.Buffer.BlockCopy(buf, off, page, pageUsed, len);
                nameAt[rows] = ((pages.Count - 1) << PageBits) | pageUsed;
                nameLen[rows] = (byte)len;
                pageUsed += len;
                string e = rec.Field(fEq, eqShare);
                if (!eqMap.TryGetValue(e, out int ei))
                {
                    ei = eqPool.Count;
                    eqMap[e] = ei;
                    eqPool.Add(e);
                }
                eqIds[rows] = ei;
                string it = rec.Field(fIt, itShare);
                if (!itMap.TryGetValue(it, out int ii))
                {
                    ii = itPool.Count;
                    itMap[it] = ii;
                    itPool.Add(it);
                }
                itIds[rows] = ii;
                rows++;
            });
            // the last page cut to what it holds (a page is below the large object heap's size)
            if (pageUsed < PageSize)
            {
                var last = new byte[pageUsed];
                System.Buffer.BlockCopy(page, 0, last, 0, pageUsed);
                pages[pages.Count - 1] = last;
            }
            ix._pages = pages.ToArray();
            ix._nameAt = nameAt;
            ix._nameLen = nameLen;
            ix._equipId = eqIds;
            ix._itemId = itIds;
            ix._equipPool = eqPool.ToArray();
            ix._itemPool = itPool.ToArray();
            ix.Rows = rows;
            ix.BuildTables();
            long w =
                (long)(pages.Count - 1) * PageSize
                + pageUsed
                + 13L * nameAt.Length
                + 4L * (ix._nameSlots.Length + ix._itemSlots.Length + ix.ItemRows.Length)
                + 200;
            foreach (string s in eqPool)
                w += 40 + 2 * s.Length;
            foreach (string s in itPool)
                w += 40 + 2 * s.Length;
            ix.Weight = w;
            return ix;
        }

        public static readonly TagIndex Empty = new TagIndex();

        // the process-wide cache, least recently used first out
        private static readonly object Lock = new object();
        private static readonly LinkedList<(string stamp, TagIndex ix)> Lru =
            new LinkedList<(string, TagIndex)>();
        private static readonly Dictionary<string, LinkedListNode<(string, TagIndex)>> ByStamp =
            new Dictionary<string, LinkedListNode<(string, TagIndex)>>(StringComparer.Ordinal);
        private static long _bytes;
        public static long Budget = 320L << 20;

        /// <summary>The index of the table at <paramref name="path"/>, empty when the file cannot be
        /// stat'ed.</summary>
        public static TagIndex For(string path)
        {
            string? st = Dbf.Stamp(path);
            if (st == null)
                return Empty;
            lock (Lock)
            {
                if (ByStamp.TryGetValue(st, out var node))
                {
                    Lru.Remove(node);
                    Lru.AddLast(node);
                    return node.Value.Item2;
                }
            }
            var ix = Read(path);
            if (Dbf.Stamp(path) != st)
                return ix;
            lock (Lock)
            {
                if (ByStamp.TryGetValue(st, out var node))
                    return node.Value.Item2;
                ByStamp[st] = Lru.AddLast((st, ix));
                _bytes += ix.Weight;
                while (Lru.Count > 1 && _bytes > Budget)
                {
                    var old = Lru.First!.Value;
                    Lru.RemoveFirst();
                    ByStamp.Remove(old.Item1);
                    _bytes -= old.Item2.Weight;
                }
            }
            return ix;
        }

        public static void ClearCache()
        {
            lock (Lock)
            {
                Lru.Clear();
                ByStamp.Clear();
                _bytes = 0;
            }
        }
    }
}
