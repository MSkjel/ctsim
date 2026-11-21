using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CicodeSim.Project;

namespace CicodeSim.Builtins
{
    /// <summary>A page's drawing size from its .ctg file head, without parsing the drawing.</summary>
    internal static class CtgHead
    {
        private const int HeaderFixed = 0x10,
            EntrySize = 42,
            NameLen = 32,
            DefaultSlots = 8,
            MaxSlots = 1024;

        private static readonly Dictionary<string, (long Mtime, long Length, int W, int H)> Cache =
            new Dictionary<string, (long, long, int, int)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The page's .ctg in the unit's project folders (search order).</summary>
        internal static bool Size(Unit unit, string page, out int width, out int height)
        {
            width = height = 0;
            page = CitectText.Trim(page);
            if (page.Length == 0 || page.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return false;
            foreach (var p in unit.Search)
            {
                string f = Path.Combine(p.Dir, page + ".ctg");
                if (File.Exists(f))
                    return Read(f, out width, out height);
            }
            return false;
        }

        /// <summary>The width and height of one .ctg (false when it has no readable page head).</summary>
        internal static bool Read(string path, out int width, out int height)
        {
            width = height = 0;
            FileInfo fi;
            try
            {
                fi = new FileInfo(path);
            }
            catch (Exception e)
                when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException)
            {
                return false;
            }
            if (!fi.Exists)
                return false;
            long mt = fi.LastWriteTimeUtc.Ticks,
                len = fi.Length;
            lock (Cache)
                if (Cache.TryGetValue(path, out var c) && c.Mtime == mt && c.Length == len)
                {
                    width = c.W;
                    height = c.H;
                    return c.W > 0 || c.H > 0;
                }
            int w = 0,
                h = 0;
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
                    var hdr = new byte[HeaderFixed];
                    if (fs.Read(hdr, 0, HeaderFixed) == HeaderFixed)
                    {
                        int word2 = BitConverter.ToUInt16(hdr, 2);
                        uint slots = BitConverter.ToUInt32(hdr, 0x0C);
                        int cap = slots > 0 && slots <= MaxSlots ? (int)slots : DefaultSlots;
                        var dir = new byte[cap * EntrySize];
                        int got = fs.Read(dir, 0, dir.Length);
                        // (an old form, word2 9..0x0B, keeps the size at the same offsets: its
                        // head is the first 0x24 bytes of the current one; older ones do not open)
                        for (int i = 0; i + EntrySize <= got && word2 >= 9; i += EntrySize)
                        {
                            int end = Array.IndexOf(dir, (byte)0, i + 2, NameLen);
                            string name = Encoding.ASCII.GetString(
                                dir,
                                i + 2,
                                (end < 0 ? i + 2 + NameLen : end) - (i + 2)
                            );
                            if (name != "page")
                                continue;
                            uint off = BitConverter.ToUInt32(dir, i + 2 + NameLen);
                            uint size = BitConverter.ToUInt32(dir, i + 6 + NameLen);
                            if (size < 0x1C || off + 0x1C > len)
                                break;
                            fs.Seek(off, SeekOrigin.Begin);
                            var head = new byte[0x1C];
                            if (fs.Read(head, 0, head.Length) == head.Length)
                            {
                                uint pw = BitConverter.ToUInt32(head, 0x14),
                                    ph = BitConverter.ToUInt32(head, 0x18);
                                if (pw <= 100000 && ph <= 100000)
                                {
                                    w = (int)pw;
                                    h = (int)ph;
                                }
                            }
                            break;
                        }
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return false;
            }
            lock (Cache)
                Cache[path] = (mt, len, w, h);
            width = w;
            height = h;
            return w > 0 || h > 0;
        }
    }
}
