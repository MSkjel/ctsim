using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CicodeSim.Project;

namespace CicodeSim.World.Display
{
    /// <summary>The frame of a library element as the runtime places it on an AN: the element's
    /// origin (its hot spot) and the extent of its root record, both in element coordinates.
    /// Placed on an AN at (x, y), the symbol covers Rect - Origin + (x, y).</summary>
    public readonly struct ElementFrame
    {
        public readonly int OriginX,
            OriginY;
        public readonly Rect Rect;

        public ElementFrame(int ox, int oy, Rect r)
        {
            OriginX = ox;
            OriginY = oy;
            Rect = r;
        }

        /// <summary>The extent the element covers with its origin on (x, y), scaled by (sx, sy)
        /// about the origin.</summary>
        public Rect At(int x, int y, double sx = 1.0, double sy = 1.0)
        {
            double l = x + (Rect.L - OriginX) * sx,
                r = x + (Rect.R - OriginX) * sx;
            double t = y + (Rect.T - OriginY) * sy,
                b = y + (Rect.B - OriginY) * sy;
            return new Rect(
                (int)Math.Floor(Math.Min(l, r)),
                (int)Math.Floor(Math.Min(t, b)),
                (int)Math.Floor(Math.Max(l, r)),
                (int)Math.Floor(Math.Max(t, b))
            );
        }
    }

    /// <summary>Reads library elements the way the display client finds them.</summary>
    public static class SymbolExtents
    {
        private const int HeaderFixed = 0x10,
            EntrySize = 42,
            NameLen = 32,
            PageHead = 0x34,
            PageHeadLegacy = 0x24,
            LinkEntry = 0x60;
        private static readonly object Lock = new object();
        private static readonly Dictionary<string, LibraryFile> Files = new Dictionary<string, LibraryFile>(
            StringComparer.OrdinalIgnoreCase
        );
        private static readonly Encoding Ansi = Encoding.GetEncoding(28591);

        private sealed class LibraryFile
        {
            public long Length;
            public DateTime Written;
            public int Word2;
            public readonly Dictionary<string, ElementFrame?> Elements = new Dictionary<
                string,
                ElementFrame?
            >(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, (long Off, long Size)> Blocks = new Dictionary<
                string,
                (long, long)
            >(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Splits 'library.element' ('element' alone = the global library).</summary>
        public static (string Library, string Element) Split(string reference)
        {
            string s = (reference ?? "").Trim();
            int k = s.IndexOf('.');
            return k < 0 ? ("global", s) : (s.Substring(0, k).Trim(), s.Substring(k + 1).Trim());
        }

        private const long FindValidMs = 2000;
        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        private static readonly Dictionary<string, (long At, ElementFrame? Frame)> Found = new Dictionary<
            string,
            (long, ElementFrame?)
        >(StringComparer.OrdinalIgnoreCase);

        /// <summary>The frame of 'library.element' in the first folder that has it, or null.</summary>
        public static ElementFrame? Find(IEnumerable<string> dirs, string reference, params string[] exts)
        {
            var (lib, el) = Split(reference);
            if (lib.Length == 0 || el.Length == 0)
                return null;
            var dl = dirs as IList<string> ?? new List<string>(dirs);
            string key = string.Join("|", exts) + "\n" + string.Join("|", dl) + "\n" + lib + "." + el;
            long now = Clock.ElapsedMilliseconds;
            lock (Lock)
            {
                if (Found.TryGetValue(key, out var hit) && now - hit.At < FindValidMs)
                    return hit.Frame;
            }
            ElementFrame? found = null;
            foreach (string ext in exts)
            {
                foreach (string dir in dl)
                {
                    string path;
                    try
                    {
                        path = Path.Combine(dir, lib + ext);
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }
                    found = Frame(path, el);
                    if (found != null)
                        break;
                }
                if (found != null)
                    break;
            }
            lock (Lock)
            {
                if (Found.Count > 4096)
                    Found.Clear();
                Found[key] = (now, found);
            }
            return found;
        }

        /// <summary>The frame of <paramref name="element"/> in the library file
        /// <paramref name="path"/>; null when the file or the element is missing or unreadable.</summary>
        public static ElementFrame? Frame(string path, string element)
        {
            FileInfo fi;
            try
            {
                fi = new FileInfo(path);
                if (!fi.Exists)
                    return null;
            }
            catch (Exception e)
                when (e is IOException
                    || e is UnauthorizedAccessException
                    || e is ArgumentException
                    || e is NotSupportedException
                )
            {
                return null;
            }
            lock (Lock)
            {
                if (
                    !Files.TryGetValue(path, out var lf)
                    || lf.Length != fi.Length
                    || lf.Written != fi.LastWriteTimeUtc
                )
                {
                    lf = new LibraryFile { Length = fi.Length, Written = fi.LastWriteTimeUtc };
                    if (!ReadDirectory(path, lf))
                        return null;
                    if (Files.Count > 512)
                        Files.Clear();
                    Files[path] = lf;
                }
                if (lf.Elements.TryGetValue(element, out var known))
                    return known;
                ElementFrame? fr = null;
                if (lf.Blocks.TryGetValue(element, out var blk))
                    fr = ReadElement(path, blk.Off, blk.Size, lf.Length, lf.Word2);
                lf.Elements[element] = fr;
                return fr;
            }
        }

        private static bool ReadDirectory(string path, LibraryFile lf)
        {
            try
            {
                using var fs = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete
                );
                var hdr = new byte[HeaderFixed];
                if (fs.Read(hdr, 0, HeaderFixed) != HeaderFixed)
                    return false;
                lf.Word2 = BitConverter.ToUInt16(hdr, 2);
                uint slots = BitConverter.ToUInt32(hdr, 12);
                if (slots == 0 || slots > 100000)
                    slots = 1000;
                var raw = new byte[(int)slots * EntrySize];
                int n = fs.Read(raw, 0, raw.Length);
                for (int i = 0; i + EntrySize <= n; i += EntrySize)
                {
                    ushort tag = BitConverter.ToUInt16(raw, i);
                    uint off = BitConverter.ToUInt32(raw, i + 2 + NameLen),
                        size = BitConverter.ToUInt32(raw, i + 2 + NameLen + 4);
                    if (tag == 0 && off == 0 && size == 0)
                        continue;
                    int z = Array.IndexOf(raw, (byte)0, i + 2, NameLen);
                    string name = Ansi.GetString(raw, i + 2, (z < 0 ? i + 2 + NameLen : z) - (i + 2));
                    // a deleted element (the tag's low byte cleared, renamed
                    // 'deleted') or a stub: not usable
                    if (
                        size == 0
                        || (
                            string.Equals(name, "deleted", StringComparison.OrdinalIgnoreCase)
                            && (tag & 0xFF) == 0
                        )
                    )
                        continue;
                    if (name.Length > 0 && !lf.Blocks.ContainsKey(name))
                        lf.Blocks[name] = (off, size);
                }
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static int Doubles(int mode) =>
            mode switch
            {
                0 => 0,
                1 => 2,
                2 => 2,
                3 => 4,
                8 => 6,
                _ => mode > 3 ? 6 : 0,
            };

        private static ElementFrame? ReadElement(string path, long off, long size, long fileLen, int word2)
        {
            try
            {
                using var fs = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete
                );
                // the root record lies just after the head and link table; read a generous
                // window (a grown last block may run past its directory size)
                long want = Math.Min(Math.Max(size, 0x4000), Math.Max(0, fileLen - off));
                if (want < PageHead + 8)
                    return null;
                var d = new byte[(int)Math.Min(want, 1 << 20)];
                fs.Seek(off, SeekOrigin.Begin);
                int n = fs.Read(d, 0, d.Length);
                if (n < PageHead + 8)
                    return null;
                int count = (int)BitConverter.ToUInt32(d, 4);
                if (count <= 0)
                    return null;
                bool legacy = word2 < 0x0C;
                int ex = legacy ? 0 : BitConverter.ToInt32(d, 0x0C),
                    ey = legacy ? 0 : BitConverter.ToInt32(d, 0x10);
                uint links = BitConverter.ToUInt32(d, legacy ? 0x0C : 0x1C);
                if (links > 100000)
                    return null;
                long rec = (legacy ? PageHeadLegacy : PageHead) + links * (long)LinkEntry;
                if (rec + 8 > n)
                    return null;
                int psize = (int)BitConverter.ToUInt32(d, (int)rec);
                int p = (int)rec + 8;
                if (psize < 12 || p + psize > n)
                    return null;
                int anLen = BitConverter.ToUInt16(d, p + 0x0A);
                int o = p + 0x0C + anLen;
                int end = p + psize;
                if (o + 6 > end)
                    return null;
                int nameLen = BitConverter.ToUInt16(d, o + 4);
                o += 6;
                if (nameLen > 0 && nameLen <= 96 && o + nameLen <= end && Printable(d, o, nameLen))
                    o += nameLen;
                for (int s = 0; s < 3; s++)
                {
                    if (o >= end)
                        return null;
                    int nd = Doubles(d[o++]);
                    if (o + nd * 8 > end)
                        return null;
                    o += nd * 8;
                }
                if (o + 4 + 8 > end)
                    return null;
                o += 4; // anchor
                short l = BitConverter.ToInt16(d, o),
                    t = BitConverter.ToInt16(d, o + 2);
                short r = BitConverter.ToInt16(d, o + 4),
                    b = BitConverter.ToInt16(d, o + 6);
                return new ElementFrame(
                    ex,
                    ey,
                    new Rect(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b))
                );
            }
            catch (Exception e)
                when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                return null;
            }
        }

        /// <summary>A code-page text byte (CtDraw object names): not C0, not DEL.</summary>
        private static bool Printable(byte[] d, int o, int n)
        {
            for (int i = o; i < o + n; i++)
                if (d[i] < 0x20 || d[i] == 0x7F)
                    return false;
            return true;
        }
    }
}
