using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CicodeSim.Project;
using CicodeSim.World.Display;

namespace CicodeSim.Builtins
{
    /// <summary>One record of a library element (a genie of a .ctm): its handle (AN = handle + 1
    /// in the element's compiled rows), type code, name, anchor, extent (rect1) and bounds (rect2)
    /// in the element's coordinates, and its children.</summary>
    internal sealed class ElementRecord
    {
        public int Handle;
        public int Type;
        public uint Flags;
        public string Name = "";
        public int AnchorX,
            AnchorY;
        public Rect Extent,
            Bounds;

        /// <summary>The record's colour-state array (the fill colour classes' colours: COL_ARR,
        /// COL_STA ...), null when the record's sections do not walk.</summary>
        public ColourStates? Colour;

        /// <summary>The record's metadata (METADATA_n = 'name=value', in n order).</summary>
        public readonly List<KeyValuePair<string, string>> Metadata =
            new List<KeyValuePair<string, string>>();
        public readonly List<ElementRecord> Children = new List<ElementRecord>();

        /// <summary>A touch (0x1010) or keyboard (0x100B) command record: it belongs to its
        /// owner (the same handle), it is not an object of its own.</summary>
        public bool IsInput => Type == 0x1010 || Type == 0x100B;
    }

    /// <summary>A library element as the runtime places it with DspSym: its origin (the hot spot,
    /// the page block head's i32 x at 0x0C / y at 0x10) and its record forest (one root).</summary>
    internal sealed class LibraryElement
    {
        public string Library = "",
            Element = "",
            Path = "";
        public int OriginX,
            OriginY;
        public ElementRecord Root = new ElementRecord();
    }

    /// <summary>Reads a genie element from its .ctm library.</summary>
    internal static class LibraryElements
    {
        private const int HeaderFixed = 0x10,
            EntrySize = 42,
            NameLen = 32,
            DefaultSlots = 8,
            MaxSlots = 1024;
        private const int HeadSize = 0x34,
            LinkSize = 0x60;

        private sealed class Lib
        {
            public long Mtime,
                Length;
            public byte[] Data = Array.Empty<byte>();
            public int Word2;
            public readonly Dictionary<string, (long Off, long Size)> Blocks = new Dictionary<
                string,
                (long, long)
            >(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, LibraryElement?> Elements = new Dictionary<
                string,
                LibraryElement?
            >(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly Dictionary<string, Lib> Cache = new Dictionary<string, Lib>(
            StringComparer.OrdinalIgnoreCase
        );

        /// <summary>'library.element' of a genie library (.ctm) in the unit's project folders
        /// (search order, the first library file that has the element); null when none.</summary>
        internal static LibraryElement? Find(Unit unit, string reference, string ext = "ctm")
        {
            reference = CitectText.Trim(reference);
            int dot = reference.IndexOf('.');
            if (dot <= 0 || dot == reference.Length - 1)
                return null;
            string lib = reference.Substring(0, dot),
                el = reference.Substring(dot + 1);
            if (lib.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                return null;
            foreach (var p in unit.Search)
            {
                string f = System.IO.Path.Combine(p.Dir, lib + "." + ext);
                if (!File.Exists(f))
                    continue;
                var e = Read(f, el);
                if (e != null)
                {
                    e.Library = lib;
                    return e;
                }
            }
            return null;
        }

        /// <summary>The element of one library file, null when it has none (or cannot be read).</summary>
        internal static LibraryElement? Read(string path, string element)
        {
            Lib? lib = Open(path);
            if (lib == null)
                return null;
            lock (lib)
            {
                if (lib.Elements.TryGetValue(element, out var had))
                    return had;
                LibraryElement? e = null;
                if (lib.Blocks.TryGetValue(element, out var b))
                {
                    try
                    {
                        e = Parse(lib.Data, b.Off, lib.Word2);
                    }
                    catch (Exception x) when (x is ArgumentException || x is IndexOutOfRangeException)
                    {
                        e = null;
                    }
                    if (e != null)
                    {
                        e.Element = element;
                        e.Path = path;
                    }
                }
                lib.Elements[element] = e;
                return e;
            }
        }

        private static Lib? Open(string path)
        {
            FileInfo fi;
            try
            {
                fi = new FileInfo(path);
            }
            catch (Exception e)
                when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException)
            {
                return null;
            }
            if (!fi.Exists)
                return null;
            long mt = fi.LastWriteTimeUtc.Ticks,
                len = fi.Length;
            lock (Cache)
                if (Cache.TryGetValue(path, out var c) && c.Mtime == mt && c.Length == len)
                    return c;
            byte[] data;
            try
            {
                using (
                    var fs = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete
                    )
                )
                {
                    data = new byte[fs.Length];
                    int got = 0;
                    while (got < data.Length)
                    {
                        int n = fs.Read(data, got, data.Length - got);
                        if (n <= 0)
                            break;
                        got += n;
                    }
                    if (got < data.Length)
                        Array.Resize(ref data, got);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return null;
            }
            var lib = new Lib
            {
                Mtime = mt,
                Length = len,
                Data = data,
            };
            if (data.Length >= HeaderFixed)
            {
                lib.Word2 = BitConverter.ToUInt16(data, 2);
                uint slots = BitConverter.ToUInt32(data, 0x0C);
                int cap = slots > 0 && slots <= MaxSlots ? (int)slots : DefaultSlots;
                for (int i = 0; i < cap; i++)
                {
                    int off = HeaderFixed + i * EntrySize;
                    if (off + EntrySize > data.Length)
                        break;
                    int tag = BitConverter.ToUInt16(data, off);
                    int end = Array.IndexOf(data, (byte)0, off + 2, NameLen);
                    string name = TextCodec.Decode(
                        data,
                        off + 2,
                        (end < 0 ? off + 2 + NameLen : end) - (off + 2)
                    );
                    uint boff = BitConverter.ToUInt32(data, off + 2 + NameLen);
                    uint bsize = BitConverter.ToUInt32(data, off + 6 + NameLen);
                    if (tag == 0 && boff == 0 && bsize == 0)
                        continue;
                    // a deleted element keeps its data, its tag's low byte cleared, named 'deleted'
                    if (bsize == 0 || (tag & 0xFF) == 0)
                        continue;
                    if (name.Length == 0 || lib.Blocks.ContainsKey(name))
                        continue;
                    lib.Blocks[name] = (boff, bsize);
                }
            }
            lock (Cache)
                Cache[path] = lib;
            return lib;
        }

        private static int XformDoubles(int mode) =>
            mode switch
            {
                0 => 0,
                1 => 2,
                2 => 2,
                3 => 4,
                8 => 6,
                _ => mode > 3 ? 6 : 0,
            };

        private static LibraryElement? Parse(byte[] d, long blockOff, int word2)
        {
            if (word2 < 0x0C)
                return null; // a legacy head: not read
            long o = blockOff;
            if (o < 0 || o + HeadSize > d.Length)
                return null;
            uint count = BitConverter.ToUInt32(d, (int)o + 4);
            var e = new LibraryElement
            {
                OriginX = BitConverter.ToInt32(d, (int)o + 0x0C),
                OriginY = BitConverter.ToInt32(d, (int)o + 0x10),
            };
            uint links = BitConverter.ToUInt32(d, (int)o + 0x1C);
            if (links > 4096)
                return null;
            long p = o + HeadSize + links * LinkSize;
            var recs = new List<(ElementRecord Rec, int Children)>();
            for (uint i = 0; i < count && i < 100000; i++)
            {
                if (p + 8 > d.Length)
                    break;
                uint size = BitConverter.ToUInt32(d, (int)p);
                long pay = p + 8;
                if (pay + size > d.Length)
                    break;
                recs.Add(Record(d, (int)pay, (int)size));
                p = pay + size;
            }
            if (recs.Count == 0)
                return null;
            int k = 0;
            ElementRecord Take(int depth)
            {
                var (r, n) = recs[k++];
                for (int c = 0; c < n && k < recs.Count && depth < 64; c++)
                    r.Children.Add(Take(depth + 1));
                return r;
            }
            e.Root = Take(0);
            return e;
        }

        private static (ElementRecord, int) Record(byte[] d, int pay, int size)
        {
            var r = new ElementRecord();
            if (size < 0x0C)
                return (r, 0);
            int children = BitConverter.ToUInt16(d, pay);
            r.Type = (int)BitConverter.ToUInt32(d, pay + 2);
            r.Flags = BitConverter.ToUInt32(d, pay + 6);
            int anLen = BitConverter.ToUInt16(d, pay + 0x0A);
            int body = pay + 0x0C + anLen,
                end = pay + size;
            if (body + 6 > end)
                return (r, children);
            r.Handle = BitConverter.ToUInt16(d, body + 2);
            int nameLen = BitConverter.ToUInt16(d, body + 4);
            int q = body + 6;
            if (q + nameLen > end)
                return (r, children);
            r.Name = nameLen > 0 ? TextCodec.Decode(d, q, nameLen) : "";
            q += nameLen;
            for (int s = 0; s < 3; s++)
            {
                if (q >= end)
                    return (r, children);
                q += 1 + XformDoubles(d[q]) * 8;
            }
            if (q + 20 > end)
                return (r, children);
            r.AnchorX = BitConverter.ToInt16(d, q);
            r.AnchorY = BitConverter.ToInt16(d, q + 2);
            r.Extent = new Rect(
                BitConverter.ToInt16(d, q + 4),
                BitConverter.ToInt16(d, q + 6),
                BitConverter.ToInt16(d, q + 8),
                BitConverter.ToInt16(d, q + 10)
            );
            r.Bounds = new Rect(
                BitConverter.ToInt16(d, q + 12),
                BitConverter.ToInt16(d, q + 14),
                BitConverter.ToInt16(d, q + 16),
                BitConverter.ToInt16(d, q + 18)
            );
            if ((r.Flags & 0x100) != 0)
                Metadata(d, q + 20, end, r.Metadata);
            try
            {
                r.Colour = ColourStateArray(d, q + 20, end, r.Flags);
            }
            catch (Exception x) when (x is ArgumentException || x is IndexOutOfRangeException)
            {
                r.Colour = null;
            }
            return (r, children);
        }

        /// <summary>The colour-state array after the geometry header.</summary>
        private static ColourStates? ColourStateArray(byte[] d, int o, int end, uint flags)
        {
            int Lp(int p)
            {
                if (p + 2 > end)
                    throw new ArgumentException("lpstr");
                return p + 2 + BitConverter.ToUInt16(d, p);
            }
            if ((flags & 0x1) != 0)
                o = Lp(o);
            if ((flags & 0x8) != 0)
            {
                if (o + 2 > end)
                    return null;
                int n = BitConverter.ToUInt16(d, o);
                o += 2;
                for (int b = 0; b < (n + 1) * 3; b++)
                {
                    if (o + 4 > end)
                        return null;
                    int cnt = BitConverter.ToUInt16(d, o + 2);
                    o += 4;
                    for (int k = 0; k < cnt; k++)
                        o = Lp(Lp(o));
                }
            }
            if ((flags & 0x40) != 0)
                o = Lp(o) + 2 + 24;
            if ((flags & 0x80) != 0)
                o = Lp(o);
            if (o + 12 > end)
                return null;
            uint flag = BitConverter.ToUInt32(d, o);
            o += 4;
            ColourPair Pair(int p) =>
                new ColourPair(BitConverter.ToUInt32(d, p), BitConverter.ToUInt32(d, p + 4));
            var cs = new ColourStates { State0 = Pair(o) };
            o += 8;
            if ((flag & 0x2) != 0)
            {
                if (o + 8 > end)
                    return null;
                cs.MaxOn = Pair(o);
                o += 8;
            }
            int Count(int size)
            {
                if (o + 2 > end)
                    throw new ArgumentException("count");
                int n = BitConverter.ToUInt16(d, o);
                if (o + 2 + size * n > end)
                    throw new ArgumentException("array");
                o += 2;
                return n;
            }
            if ((flag & 0x4) != 0)
                for (int k = Count(8); k > 0; k--, o += 8)
                    cs.Sta.Add(Pair(o));
            if ((flag & 0x8) != 0)
            {
                cs.Arr = new List<ColourPair>();
                for (int k = Count(8); k > 0; k--, o += 8)
                    cs.Arr.Add(Pair(o));
            }
            if ((flag & 0x10) != 0)
                for (int k = Count(11); k > 0; k--, o += 11)
                    cs.Thr.Add(new Threshold(d[o], BitConverter.ToInt16(d, o + 1), Pair(o + 3)));
            if ((flag & 0x20) != 0)
            {
                if (o + 8 > end)
                    return null;
                cs.MaxOn = Pair(o); // COL_MAXON again (live: it writes back into COL_MAXON)
                o += 8;
            }
            if ((flag & 0x40) != 0 && o + 8 <= end)
                cs.FilCol = Pair(o);
            return cs;
        }

        private static readonly byte[] MetaKey = Encoding.ASCII.GetBytes("METADATA_");

        /// <summary>The record's METADATA_n entries.</summary>
        private static void Metadata(byte[] d, int from, int end, List<KeyValuePair<string, string>> into)
        {
            var found = new SortedDictionary<int, KeyValuePair<string, string>>();
            for (int i = Math.Max(from, 2); i + MetaKey.Length < end; i++)
            {
                if (d[i] != (byte)'M')
                    continue;
                int j = 0;
                while (j < MetaKey.Length && d[i + j] == MetaKey[j])
                    j++;
                if (j < MetaKey.Length)
                    continue;
                int keyLen = BitConverter.ToUInt16(d, i - 2);
                int k = i + MetaKey.Length,
                    n = 0,
                    digits = 0;
                while (k < i + keyLen && k < end && d[k] >= (byte)'0' && d[k] <= (byte)'9')
                {
                    n = n * 10 + (d[k] - '0');
                    k++;
                    digits++;
                }
                if (digits == 0 || digits > 4 || keyLen != MetaKey.Length + digits)
                    continue;
                int v = i + keyLen;
                if (v + 2 > end)
                    continue;
                int vlen = BitConverter.ToUInt16(d, v);
                if (v + 2 + vlen > end || vlen > 512)
                    continue;
                if (found.ContainsKey(n))
                    continue;
                string text = TextCodec.Decode(d, v + 2, vlen);
                int eq = text.IndexOf('=');
                found[n] =
                    eq < 0
                        ? new KeyValuePair<string, string>(text, "")
                        : new KeyValuePair<string, string>(text.Substring(0, eq), text.Substring(eq + 1));
                i = v + 1 + vlen;
            }
            into.AddRange(found.Values);
        }

        /// <summary>The name of the compiled rows of a genie placed with DspSym: '_' + the upper
        /// case SHA-1 of the lower case 'library.element' (the compiler's .RDB name; ExampleSA's
        /// _528676CE...RDB = sa_controls.treeviewitem_hd1080).</summary>
        internal static string CompiledName(string reference)
        {
            byte[] b = Encoding.ASCII.GetBytes(CitectText.Trim(reference).ToLowerInvariant());
            byte[] h;
            using (var sha = System.Security.Cryptography.SHA1.Create())
                h = sha.ComputeHash(b);
            var sb = new StringBuilder("_", 41);
            foreach (byte x in h)
                sb.Append(x.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
