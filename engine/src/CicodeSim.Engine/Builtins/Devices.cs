using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>
    /// Cicode devices (devices.dbf, DevModify) over the simulation's sandbox: a text device writes
    /// and reads its file, a device with a format keeps records (a dBASE device as a dBASE file,
    /// a text device as the format's fixed-width lines), a printer spools to a text file of the
    /// sandbox. Real project files are read, never written (the sandbox's rule). SQL devices have
    /// no database: they do not open.
    /// </summary>
    public static class Devices
    {
        internal const int AsciiDev = 0,
            PrinterDev = 1,
            DbaseDev = 2,
            SqlDev = 4;
        private const int CannotOpen = 262;

        /// <summary>A device's definition: its devices.dbf row, with DevModify's changes.</summary>
        internal sealed class Def
        {
            public string Name = "",
                Format = "",
                Header = "",
                FileName = "";
            public int Type = AsciiDev;
            public int Files = 1;
        }

        /// <summary>A field of a device's format ({NAME,width}) and the text before it.</summary>
        internal sealed class Field
        {
            public string Name = "",
                Before = "";
            public int Width;
        }

        /// <summary>An open device.</summary>
        internal sealed class Dev
        {
            public Def Def = new Def();
            public string Path = "";
            public bool ReadOnly,
                Exclusive;

            /// <summary>The format's fields (none: a plain text device) and the text after them.</summary>
            public List<Field> Fields = new List<Field>();
            public string Trailer = "";

            /// <summary>The records of a device with fields; the current one (-1 none).</summary>
            public List<string[]> Records = new List<string[]>();
            public int Current = -1;

            /// <summary>A plain text device's text and its read position.</summary>
            public StringBuilder Text = new StringBuilder();
            public int Pos;
            public bool Eof;

            public bool HasFields => Fields.Count > 0;
        }

        private sealed class State
        {
            public readonly Dictionary<string, Def> Modified = new Dictionary<string, Def>(
                StringComparer.OrdinalIgnoreCase
            );
            public readonly Dictionary<long, Dev> Open = new Dictionary<long, Dev>();
            public long Next = 1;
        }

        private static readonly ConditionalWeakTable<SimHost, State> States =
            new ConditionalWeakTable<SimHost, State>();

        private static State Of(SimHost h) => States.GetValue(h, _ => new State());

        private static Value Bad(CicodeTask t, Value result, int code, bool halt = true)
        {
            t.RaiseError(code, halt);
            return result;
        }

        // ------------------------------------------------------------------ definitions
        private static int TypeOf(string s)
        {
            switch (s.Trim().ToUpperInvariant())
            {
                case "PRINTER_DEV":
                case "1":
                    return PrinterDev;
                case "DBASE_DEV":
                case "2":
                    return DbaseDev;
                case "SQL_DEV":
                case "4":
                    return SqlDev;
                default:
                    return AsciiDev;
            }
        }

        /// <summary>The definition of device <paramref name="name"/>: DevModify's, else its
        /// devices.dbf row; null when the project has none.</summary>
        internal static Def? Find(SimHost h, string name)
        {
            name = name.Trim();
            if (Of(h).Modified.TryGetValue(name, out var m))
                return m;
            if (h.World == null)
                return null;
            try
            {
                foreach (var (_, r) in h.World.Unit.Rows("devices.dbf"))
                {
                    if (
                        !string.Equals(
                            Project.CitectText.Trim(r.Get("NAME")),
                            name,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        continue;
                    int files = int.TryParse(Project.CitectText.Trim(r.Get("FILES")), out int f) ? f : 1;
                    return new Def
                    {
                        Name = Project.CitectText.Trim(r.Get("NAME")),
                        Format = Project.CitectText.Trim(r.Get("FORMAT")),
                        Header = Project.CitectText.Trim(r.Get("HEADER")),
                        FileName = Project.CitectText.Trim(r.Get("FILENAME")),
                        Type = TypeOf(r.Get("TYPE")),
                        Files = Math.Max(1, files),
                    };
                }
            }
            catch (Exception e)
                when (e is System.IO.IOException || e is FormatException || e is InvalidOperationException)
            { }
            return null;
        }

        /// <summary>The text a format's escapes stand for (^t tab, ^n new line, ^r carriage return,
        /// ^v vertical tab, ^f form feed, ^^ a caret).</summary>
        internal static string Unescape(string s)
        {
            if (s.IndexOf('^') < 0)
                return s;
            var b = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '^' || i + 1 >= s.Length)
                {
                    b.Append(c);
                    continue;
                }
                char e = char.ToLowerInvariant(s[++i]);
                b.Append(
                    e switch
                    {
                        't' => "\t",
                        'n' => "\r\n",
                        'r' => "\r",
                        'v' => "\v",
                        'f' => "\f",
                        '^' => "^",
                        _ => "^" + s[i],
                    }
                );
            }
            return b.ToString();
        }

        /// <summary>The fields of a format ('{NAME,width}', more after the width ignored) and the
        /// text after the last one.</summary>
        internal static (List<Field> fields, string trailer) Parse(string format)
        {
            var fields = new List<Field>();
            var lit = new StringBuilder();
            int i = 0;
            while (i < format.Length)
            {
                int open = format.IndexOf('{', i);
                int close = open < 0 ? -1 : format.IndexOf('}', open);
                if (open < 0 || close < 0)
                {
                    lit.Append(format, i, format.Length - i);
                    break;
                }
                lit.Append(format, i, open - i);
                string[] parts = format.Substring(open + 1, close - open - 1).Split(',');
                string name = parts[0].Trim();
                int width =
                    parts.Length > 1
                    && int.TryParse(
                        parts[1].Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int w
                    )
                        ? Math.Max(1, w)
                        : Math.Max(1, name.Length);
                if (name.Length > 0)
                {
                    fields.Add(
                        new Field
                        {
                            Name = name,
                            Width = width,
                            Before = Unescape(lit.ToString()),
                        }
                    );
                    lit.Clear();
                }
                i = close + 1;
            }
            return (fields, Unescape(lit.ToString()));
        }

        // ------------------------------------------------------------------ the file
        private static string PathOf(SimHost h, Def d) =>
            d.Type == PrinterDev
                ? h.Sandbox.Full("Printers\\" + d.Name + ".txt")
                : h.Sandbox.Full(Misc.PathOf(h, d.FileName));

        private static byte[]? ReadAll(SimHost h, string path)
        {
            if (!h.Sandbox.FileExists(path))
                return null;
            var f = h.Sandbox.Open(path, true, false, false, false, 0);
            if (f == null)
                return null;
            long n = h.Sandbox.Length(f);
            if (n <= 0)
                return Array.Empty<byte>();
            var buf = new byte[n];
            int k = h.Sandbox.Read(f, buf, (int)n);
            if (k < n)
                Array.Resize(ref buf, Math.Max(0, k));
            return buf;
        }

        private static bool WriteAll(SimHost h, string path, byte[] data, ulong now)
        {
            var f = h.Sandbox.Open(path, false, true, true, true, now);
            return f != null
                && (data.Length == 0 || h.Sandbox.Write(f, data, data.Length, now) == data.Length);
        }

        /// <summary>Read the device's file (none: empty; a new text device starts with its header).</summary>
        private static void Load(SimHost h, Dev d)
        {
            byte[]? data = ReadAll(h, d.Path);
            if (d.Def.Type == DbaseDev)
            {
                if (data != null && data.Length > 32)
                    ReadDbf(d, data);
                return;
            }
            string text = data == null ? "" : Cp1252.Decode(data);
            if (!d.HasFields)
            {
                d.Text.Append(text);
                return;
            }
            string header = Unescape(d.Def.Header);
            foreach (string raw in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (raw.Length == 0 || (header.Length > 0 && raw == header))
                    continue;
                d.Records.Add(ParseLine(d, raw));
            }
        }

        /// <summary>Write the device's file (the sandbox's; a real file is copied there first).</summary>
        private static bool Save(SimHost h, CicodeTask t, Dev d)
        {
            if (d.ReadOnly)
                return true;
            byte[] data;
            if (d.Def.Type == DbaseDev)
                data = WriteDbf(d);
            else if (d.HasFields)
            {
                var b = new StringBuilder();
                string header = Unescape(d.Def.Header);
                if (header.Length > 0)
                    b.Append(header).Append("\r\n");
                foreach (var r in d.Records)
                    b.Append(Line(d, r)).Append("\r\n");
                data = Cp1252.Encode(b.ToString());
            }
            else
                data = Cp1252.Encode(d.Text.ToString());
            return WriteAll(h, d.Path, data, t.Vm.Clock.NowFiletime());
        }

        private static string Fit(string v, int width) =>
            v.Length >= width ? v.Substring(0, width) : v.PadRight(width);

        /// <summary>A record as its format's line.</summary>
        private static string Line(Dev d, string[] r)
        {
            var b = new StringBuilder();
            for (int k = 0; k < d.Fields.Count; k++)
                b.Append(d.Fields[k].Before).Append(Fit(r[k], d.Fields[k].Width));
            return b.Append(d.Trailer).ToString();
        }

        /// <summary>A line of the format back into its fields (by position).</summary>
        private static string[] ParseLine(Dev d, string line)
        {
            var r = new string[d.Fields.Count];
            int pos = 0;
            for (int k = 0; k < d.Fields.Count; k++)
            {
                pos += d.Fields[k].Before.Length;
                r[k] =
                    pos >= line.Length
                        ? ""
                        : line.Substring(pos, Math.Min(d.Fields[k].Width, line.Length - pos)).TrimEnd();
                pos += d.Fields[k].Width;
            }
            return r;
        }

        // dBASE III: a 32-byte header, 32 bytes a field (character fields), 0x0D, the records
        // (a delete flag byte and the fields), 0x1A
        private static byte[] WriteDbf(Dev d)
        {
            int recSize = 1;
            foreach (var f in d.Fields)
                recSize += Math.Min(254, f.Width);
            int headSize = 32 + 32 * d.Fields.Count + 1;
            var buf = new byte[headSize + recSize * d.Records.Count + 1];
            buf[0] = 0x03;
            var now = DateTime.Now;
            buf[1] = (byte)(now.Year - 1900);
            buf[2] = (byte)now.Month;
            buf[3] = (byte)now.Day;
            BitConverter.GetBytes(d.Records.Count).CopyTo(buf, 4);
            BitConverter.GetBytes((ushort)headSize).CopyTo(buf, 8);
            BitConverter.GetBytes((ushort)recSize).CopyTo(buf, 10);
            for (int k = 0; k < d.Fields.Count; k++)
            {
                int at = 32 + 32 * k;
                byte[] name = Cp1252.Encode(d.Fields[k].Name.ToUpperInvariant());
                Array.Copy(name, 0, buf, at, Math.Min(10, name.Length));
                buf[at + 11] = (byte)'C';
                buf[at + 16] = (byte)Math.Min(254, d.Fields[k].Width);
            }
            buf[headSize - 1] = 0x0D;
            for (int i = 0; i < d.Records.Count; i++)
            {
                int at = headSize + i * recSize;
                buf[at++] = (byte)' ';
                for (int k = 0; k < d.Fields.Count; k++)
                {
                    int w = Math.Min(254, d.Fields[k].Width);
                    byte[] v = Cp1252.Encode(Fit(d.Records[i][k], w));
                    Array.Copy(v, 0, buf, at, w);
                    at += w;
                }
            }
            buf[buf.Length - 1] = 0x1A;
            return buf;
        }

        private static void ReadDbf(Dev d, byte[] data)
        {
            int n = BitConverter.ToInt32(data, 4);
            int headSize = BitConverter.ToUInt16(data, 8);
            int recSize = BitConverter.ToUInt16(data, 10);
            var names = new List<(string name, int width)>();
            for (int at = 32; at + 32 <= headSize && data[at] != 0x0D; at += 32)
            {
                int end = Array.IndexOf(data, (byte)0, at, 11);
                string name = Cp1252.Decode(data, at, (end < 0 ? at + 11 : end) - at).Trim();
                names.Add((name, data[at + 16]));
            }
            // a format of '*' (or none): the file's own fields
            if (!d.HasFields)
                foreach (var (name, width) in names)
                    d.Fields.Add(new Field { Name = name, Width = width });
            for (int i = 0; i < n; i++)
            {
                int at = headSize + i * recSize;
                if (at + recSize > data.Length)
                    break;
                if (data[at] == 0x2A)
                    continue; // (deleted)
                var r = new string[d.Fields.Count];
                for (int k = 0; k < r.Length; k++)
                    r[k] = "";
                int pos = at + 1;
                foreach (var (name, width) in names)
                {
                    int k = d.Fields.FindIndex(f =>
                        string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)
                    );
                    if (k >= 0)
                        r[k] = Cp1252.Decode(data, pos, width).TrimEnd();
                    pos += width;
                }
                d.Records.Add(r);
            }
        }

        // ------------------------------------------------------------------ handles
        private static Dev? Get(SimHost h, Value handle) =>
            Of(h).Open.TryGetValue(A.I(handle), out var d) ? d : null;

        private static int FieldIndex(Dev d, string name) =>
            d.Fields.FindIndex(f => string.Equals(f.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

        // ------------------------------------------------------------------ the built-ins
        [Builtin(
            "DevOpen",
            Doc = "1076389",
            Effects = "opens the device's file in the sandbox (read; a write copies a real file there first)",
            Errors = "289 with the halt flag: no such device (-1); 262 with the halt flag: an SQL device (no database), a printer or file that cannot be opened, or a device open exclusively (-1)",
            Notes = "devices.dbf and DevModify define the device. Mode 0 shared, 1 exclusive, 2 or 4 read only. A text device: its file's text (a new file starts with the header); with a format, its records as fixed-width lines; a dBASE device: a dBASE file (format '*': the file's fields); a printer: a spool file Printers\\<name>.txt of the sandbox."
        )]
        private static Value DevOpen(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.S(a.Get(0));
            long mode = a.Count > 1 && !a.Get(1).IsNothing ? A.I(a.Get(1)) : 0;
            var def = Find(h, name);
            if (def == null)
                return Bad(t, W.I(-1), Outside.NameNotFoundCode);
            if (def.Type == SqlDev)
            {
                h.Note(t, "DevOpen(\"" + name + "\"): an SQL device has no database in the simulator");
                return Bad(t, W.I(-1), CannotOpen);
            }
            var st = Of(h);
            var d = new Dev
            {
                Def = def,
                Path = PathOf(h, def),
                ReadOnly = (mode & 6) != 0,
                Exclusive = (mode & 1) != 0,
            };
            foreach (var o in st.Open.Values)
                if (Sandbox.KeyOf(o.Path) == Sandbox.KeyOf(d.Path) && (o.Exclusive || d.Exclusive))
                    return Bad(t, W.I(-1), CannotOpen);
            (d.Fields, d.Trailer) = Parse(def.Format == "*" ? "" : def.Format);
            Load(h, d);
            if (!d.ReadOnly && !d.HasFields && d.Text.Length == 0 && def.Header.Length > 0)
                d.Text.Append(Unescape(def.Header)).Append("\r\n");
            d.Current = d.Records.Count > 0 ? 0 : -1;
            if (!d.ReadOnly && !Save(h, t, d))
                return Bad(t, W.I(-1), CannotOpen);
            long hd = st.Next++;
            st.Open[hd] = d;
            return W.I(hd);
        }

        [Builtin(
            "DevOpenGrp",
            Doc = "1076389",
            Errors = "as DevOpen",
            Notes = "A device group opens as the device of the group's name (device groups are not read)."
        )]
        private static Value DevOpenGrp(SimHost h, CicodeTask t, ArgList a) => DevOpen(h, t, a);

        [Builtin(
            "DevClose",
            Doc = "1076375",
            Errors = "269 with the halt flag: no such device (returned)",
            Notes = "Writes the device's file and closes it: 0."
        )]
        private static Value DevClose(SimHost h, CicodeTask t, ArgList a)
        {
            var d = Get(h, a.Get(0));
            if (d == null)
                return Bad(t, W.I(Codes.BadHandle), Codes.BadHandle);
            Save(h, t, d);
            Of(h).Open.Remove(A.I(a.Get(0)));
            return W.I(0);
        }

        [Builtin(
            "DevFlush",
            Doc = "1076383",
            Errors = "269 with the halt flag: no such device (returned)",
            Notes = "Writes the device's file: 0."
        )]
        private static Value DevFlush(SimHost h, CicodeTask t, ArgList a)
        {
            var d = Get(h, a.Get(0));
            if (d == null)
                return Bad(t, W.I(Codes.BadHandle), Codes.BadHandle);
            Save(h, t, d);
            return W.I(0);
        }

        private static Value Write(SimHost h, CicodeTask t, ArgList a, bool line)
        {
            var d = Get(h, a.Get(0));
            if (d == null)
                return Bad(t, W.I(Codes.BadHandle), Codes.BadHandle);
            if (d.ReadOnly || d.Def.Type == DbaseDev)
                return Bad(t, W.I(Codes.InvalidArg), Codes.InvalidArg);
            string s = A.S(a.Get(1)) + (line ? "\r\n" : "");
            if (d.HasFields)
                d.Records.Add(ParseLine(d, s.TrimEnd('\r', '\n')));
            else
                d.Text.Append(s);
            Save(h, t, d);
            return W.I(0);
        }

        [Builtin(
            "DevWrite",
            Doc = "1076398",
            Errors = "269 with the halt flag: no such device (returned); 274 with the halt flag: read only or a dBASE device (returned)",
            Notes = "Adds the text at the end of the device's file (in the sandbox): 0."
        )]
        private static Value DevWrite(SimHost h, CicodeTask t, ArgList a) => Write(h, t, a, false);

        [Builtin(
            "DevWriteLn",
            Doc = "1076399",
            Errors = "as DevWrite",
            Notes = "Adds the text and CR LF at the end of the device's file (in the sandbox): 0."
        )]
        private static Value DevWriteLn(SimHost h, CicodeTask t, ArgList a) => Write(h, t, a, true);

        [Builtin(
            "DevPrint",
            Doc = "1076391",
            Errors = "as DevWrite",
            Notes = "(hDev, sData, NewLine): the text, and CR LF when NewLine is not 0, at the end of the device: 0."
        )]
        private static Value DevPrint(SimHost h, CicodeTask t, ArgList a) =>
            Write(h, t, a, a.Count > 2 && A.I(a.Get(2)) != 0);

        [Builtin(
            "DevRead",
            Doc = "1076392",
            Errors = "269 with the halt flag: no such device (\"\")",
            Notes = "(hDev, Length): the next Length characters of a text device (\"\" at the end: DevEOF is 1 then); a device with records: the current record's line, then the next record."
        )]
        private static Value DevRead(SimHost h, CicodeTask t, ArgList a)
        {
            var d = Get(h, a.Get(0));
            if (d == null)
                return Bad(t, W.Empty, Codes.BadHandle);
            if (d.HasFields)
                return W.S(NextRecordLine(d));
            int n = (int)Math.Max(0, Math.Min(255, A.I(a.Get(1))));
            int left = d.Text.Length - d.Pos;
            if (left <= 0)
            {
                d.Eof = true;
                return W.Empty;
            }
            string s = d.Text.ToString(d.Pos, Math.Min(n, left));
            d.Pos += s.Length;
            return W.S(s);
        }

        private static string NextRecordLine(Dev d)
        {
            if (d.Current < 0 || d.Current >= d.Records.Count)
            {
                d.Eof = true;
                return "";
            }
            string s = Line(d, d.Records[d.Current]);
            d.Current++;
            return s;
        }

        [Builtin(
            "DevReadLn",
            Doc = "1076393",
            Errors = "269 with the halt flag: no such device (\"\")",
            Notes = "The next line of a text device without its CR LF (\"\" at the end: DevEOF is 1 then); a device with records: the current record's line, then the next record."
        )]
        private static Value DevReadLn(SimHost h, CicodeTask t, ArgList a)
        {
            var d = Get(h, a.Get(0));
            if (d == null)
                return Bad(t, W.Empty, Codes.BadHandle);
            if (d.HasFields)
                return W.S(NextRecordLine(d));
            if (d.Pos >= d.Text.Length)
            {
                d.Eof = true;
                return W.Empty;
            }
            string all = d.Text.ToString();
            int nl = all.IndexOf('\n', d.Pos);
            int end = nl < 0 ? all.Length : nl;
            string s = all.Substring(d.Pos, end - d.Pos).TrimEnd('\r');
            d.Pos = nl < 0 ? all.Length : nl + 1;
            return W.S(s);
        }

        [Builtin(
            "DevEOF",
            Doc = "1076380",
            Errors = "269 with the halt flag: no such device (returned)",
            Notes = "1 after a read or a move past the device's end, else 0."
        )]
        private static Value DevEOF(SimHost h, CicodeTask t, ArgList a)
        {
            var d = Get(h, a.Get(0));
            if (d == null)
                return Bad(t, W.I(Codes.BadHandle), Codes.BadHandle);
            bool end = d.HasFields ? d.Eof || d.Current < 0 || d.Current >= d.Records.Count : d.Eof;
            return W.I(end ? 1 : 0);
        }

        [Builtin(
            "DevAppend",
            Doc = "1076374",
            Errors = "269 with the halt flag: no such device (returned); 274 with the halt flag: read only (returned)",
            Notes = "A device with records: a new blank record at the end, the current one (DevSetField fills it): 0. A plain text device: nothing to add, 0."
        )]
        private static Value DevAppend(SimHost h, CicodeTask t, ArgList a)
        {
            var d = Get(h, a.Get(0));
            if (d == null)
                return Bad(t, W.I(Codes.BadHandle), Codes.BadHandle);
            if (d.ReadOnly)
                return Bad(t, W.I(Codes.InvalidArg), Codes.InvalidArg);
            if (!d.HasFields)
                return W.I(0);
            var r = new string[d.Fields.Count];
            for (int k = 0; k < r.Length; k++)
                r[k] = "";
            d.Records.Add(r);
            d.Current = d.Records.Count - 1;
            d.Eof = false;
            Save(h, t, d);
            return W.I(0);
        }

        [Builtin(
            "DevSetField",
            Doc = "1076396",
            Errors = "269 with the halt flag: no such device (returned); 266 with the halt flag: no such field or no current record (returned); 274: read only (returned)",
            Notes = "Sets a field of the current record (cut to the field's width when written): 0."
        )]
        private static Value DevSetField(SimHost h, CicodeTask t, ArgList a)
        {
            var d = Get(h, a.Get(0));
            if (d == null)
                return Bad(t, W.I(Codes.BadHandle), Codes.BadHandle);
            if (d.ReadOnly)
                return Bad(t, W.I(Codes.InvalidArg), Codes.InvalidArg);
            int k = FieldIndex(d, A.S(a.Get(1)));
            if (k < 0 || d.Current < 0 || d.Current >= d.Records.Count)
                return Bad(t, W.I(Codes.FieldNotFound), Codes.FieldNotFound);
            d.Records[d.Current][k] = A.S(a.Get(2));
            Save(h, t, d);
            return W.I(0);
        }

        [Builtin(
            "DevGetField",
            Doc = "1076384",
            Errors = "269 with the halt flag: no such device (\"\"); 266 with the halt flag: no such field or no current record (\"\")",
            Notes = "A field of the current record, its trailing blanks removed."
        )]
        private static Value DevGetField(SimHost h, CicodeTask t, ArgList a)
        {
            var d = Get(h, a.Get(0));
            if (d == null)
                return Bad(t, W.Empty, Codes.BadHandle);
            int k = FieldIndex(d, A.S(a.Get(1)));
            if (k < 0 || d.Current < 0 || d.Current >= d.Records.Count)
                return Bad(t, W.Empty, Codes.FieldNotFound);
            return W.S(Fit(d.Records[d.Current][k], d.Fields[k].Width).TrimEnd());
        }

        private static Value Move(SimHost h, CicodeTask t, ArgList a, int by)
        {
            var d = Get(h, a.Get(0));
            if (d == null)
                return Bad(t, W.I(Codes.BadHandle), Codes.BadHandle);
            if (!d.HasFields)
            {
                // (a text device: the next / previous line)
                if (by > 0)
                    DevReadLn(h, t, a);
                return W.I(0);
            }
            int to = d.Current + by;
            d.Eof = to >= d.Records.Count;
            d.Current = Math.Max(0, Math.Min(d.Records.Count, to));
            if (d.Records.Count == 0)
                d.Current = -1;
            return W.I(0);
        }

        [Builtin(
            "DevNext",
            Doc = "1076388",
            Errors = "269 with the halt flag: no such device (returned)",
            Notes = "The next record (past the last: DevEOF is 1); a text device: skips a line. 0."
        )]
        private static Value DevNext(SimHost h, CicodeTask t, ArgList a) => Move(h, t, a, 1);

        [Builtin(
            "DevPrev",
            Doc = "1076390",
            Errors = "269 with the halt flag: no such device (returned)",
            Notes = "The previous record (the first stays the first). 0."
        )]
        private static Value DevPrev(SimHost h, CicodeTask t, ArgList a) => Move(h, t, a, -1);

        [Builtin(
            "DevSeek",
            Doc = "1076395",
            Errors = "269 with the halt flag: no such device (returned)",
            Notes = "(hDev, Offset): a device with records: record Offset (1 the first, as DevRecNo); a text device: the character offset. 0."
        )]
        private static Value DevSeek(SimHost h, CicodeTask t, ArgList a)
        {
            var d = Get(h, a.Get(0));
            if (d == null)
                return Bad(t, W.I(Codes.BadHandle), Codes.BadHandle);
            long off = A.I(a.Get(1));
            if (d.HasFields)
            {
                d.Current = d.Records.Count == 0 ? -1 : (int)Math.Max(0, Math.Min(d.Records.Count, off - 1));
                d.Eof = d.Current >= d.Records.Count;
            }
            else
            {
                d.Pos = (int)Math.Max(0, Math.Min(d.Text.Length, off));
                d.Eof = false;
            }
            return W.I(0);
        }

        [Builtin(
            "DevRecNo",
            Doc = "1076394",
            Errors = "269 with the halt flag: no such device (-1)",
            Notes = "The current record's number (1 the first; 0 no record); a text device: the read position."
        )]
        private static Value DevRecNo(SimHost h, CicodeTask t, ArgList a)
        {
            var d = Get(h, a.Get(0));
            if (d == null)
                return Bad(t, W.I(-1), Codes.BadHandle);
            return W.I(d.HasFields ? d.Current + 1 : d.Pos);
        }

        [Builtin(
            "DevSize",
            Doc = "1076397",
            Errors = "269 with the halt flag: no such device (-1)",
            Notes = "The number of records; a text device: its length in characters."
        )]
        private static Value DevSize(SimHost h, CicodeTask t, ArgList a)
        {
            var d = Get(h, a.Get(0));
            if (d == null)
                return Bad(t, W.I(-1), Codes.BadHandle);
            return W.I(d.HasFields ? d.Records.Count : d.Text.Length);
        }

        [Builtin(
            "DevFind",
            Doc = "1076381",
            Errors = "269 with the halt flag: no such device (returned)",
            Notes = "(hDev, Find, Field): from the current record on, the first whose field is Find (without case or trailing blanks) becomes the current one: 0; none: past the end (DevEOF 1), 1 (verify the runtime's code)."
        )]
        private static Value DevFind(SimHost h, CicodeTask t, ArgList a)
        {
            var d = Get(h, a.Get(0));
            if (d == null)
                return Bad(t, W.I(Codes.BadHandle), Codes.BadHandle);
            string want = A.S(a.Get(1)).TrimEnd();
            int k = FieldIndex(d, A.S(a.Get(2)));
            if (k < 0)
                return Bad(t, W.I(Codes.FieldNotFound), Codes.FieldNotFound);
            for (int i = Math.Max(0, d.Current); i < d.Records.Count; i++)
            {
                if (string.Equals(d.Records[i][k].TrimEnd(), want, StringComparison.OrdinalIgnoreCase))
                {
                    d.Current = i;
                    d.Eof = false;
                    return W.I(0);
                }
            }
            d.Current = d.Records.Count;
            d.Eof = true;
            return W.I(1);
        }

        [Builtin(
            "DevDelete",
            Doc = "1076378",
            Errors = "269 with the halt flag: no such device (returned); 266 with the halt flag: no current record (returned)",
            Notes = "Removes the current record (the next one becomes current): 0."
        )]
        private static Value DevDelete(SimHost h, CicodeTask t, ArgList a)
        {
            var d = Get(h, a.Get(0));
            if (d == null)
                return Bad(t, W.I(Codes.BadHandle), Codes.BadHandle);
            if (d.Current < 0 || d.Current >= d.Records.Count)
                return Bad(t, W.I(Codes.FieldNotFound), Codes.FieldNotFound);
            d.Records.RemoveAt(d.Current);
            if (d.Records.Count == 0)
                d.Current = -1;
            Save(h, t, d);
            return W.I(0);
        }

        [Builtin(
            "DevZap",
            Doc = "1076400",
            Errors = "269 with the halt flag: no such device (returned)",
            Notes = "Empties the device (its records, or its text): 0."
        )]
        private static Value DevZap(SimHost h, CicodeTask t, ArgList a)
        {
            var d = Get(h, a.Get(0));
            if (d == null)
                return Bad(t, W.I(Codes.BadHandle), Codes.BadHandle);
            d.Records.Clear();
            d.Current = -1;
            d.Text.Clear();
            d.Pos = 0;
            Save(h, t, d);
            return W.I(0);
        }

        [Builtin(
            "DevHistory",
            Doc = "1076385",
            Errors = "269 with the halt flag: no such device (returned)",
            Notes = "Starts a new file: the device's file becomes its history file (the name with extension .001, the older ones shifted on up to FILES), the device empty: 0."
        )]
        private static Value DevHistory(SimHost h, CicodeTask t, ArgList a)
        {
            var d = Get(h, a.Get(0));
            if (d == null)
                return Bad(t, W.I(Codes.BadHandle), Codes.BadHandle);
            Save(h, t, d);
            string stem = System.IO.Path.ChangeExtension(d.Path, null);
            int keep = Math.Max(1, d.Def.Files);
            h.Sandbox.Delete(stem + "." + keep.ToString("000", CultureInfo.InvariantCulture));
            for (int k = keep - 1; k >= 1; k--)
                h.Sandbox.Rename(
                    stem + "." + k.ToString("000", CultureInfo.InvariantCulture),
                    stem + "." + (k + 1).ToString("000", CultureInfo.InvariantCulture)
                );
            byte[]? now = ReadAll(h, d.Path);
            if (now != null)
                WriteAll(h, stem + ".001", now, t.Vm.Clock.NowFiletime());
            return DevZap(h, t, a);
        }

        [Builtin(
            "DevInfo",
            Doc = "1076386",
            Errors = "269 with the halt flag: no such device (\"\")",
            Notes = "(hDev, Type): 0 the name, 1 the format, 2 the header, 3 the file name, 4 the type, 5 the number of files, 6 the number of fields; -n: the name of field n (1 the first); other: \"\"."
        )]
        private static Value DevInfo(SimHost h, CicodeTask t, ArgList a)
        {
            var d = Get(h, a.Get(0));
            if (d == null)
                return Bad(t, W.Empty, Codes.BadHandle);
            long type = A.I(a.Get(1));
            if (type < 0)
                return W.S(-type - 1 < d.Fields.Count ? d.Fields[(int)(-type - 1)].Name : "");
            return W.S(
                type switch
                {
                    0 => d.Def.Name,
                    1 => d.Def.Format,
                    2 => d.Def.Header,
                    3 => d.Def.FileName,
                    4 => d.Def.Type.ToString(CultureInfo.InvariantCulture),
                    5 => d.Def.Files.ToString(CultureInfo.InvariantCulture),
                    6 => d.Fields.Count.ToString(CultureInfo.InvariantCulture),
                    _ => "",
                }
            );
        }

        [Builtin(
            "DevControl",
            Doc = "1076376",
            Errors = "269 with the halt flag: no such device (returned)",
            Notes = "Database and printer controls of an open device: nothing to control in the simulator, 0."
        )]
        private static Value DevControl(SimHost h, CicodeTask t, ArgList a) =>
            Get(h, a.Get(0)) == null ? Bad(t, W.I(Codes.BadHandle), Codes.BadHandle) : W.I(0);

        [Builtin(
            "DevModify",
            Doc = "1076387",
            Errors = "289 with the halt flag: no such device (returned)",
            Notes = "(Name, Format, Header, FileName, Type): the device's definition for the simulation from now on ('*' or a negative type keeps a value); a device open now keeps the one it opened with. 0."
        )]
        private static Value DevModify(SimHost h, CicodeTask t, ArgList a)
        {
            string name = A.S(a.Get(0)).Trim();
            var cur = Find(h, name);
            if (cur == null)
                return Bad(t, W.I(Outside.NameNotFoundCode), Outside.NameNotFoundCode);
            string Pick(int k, string old)
            {
                string v = k < a.Count && !a.Get(k).IsNothing ? A.S(a.Get(k)) : "*";
                return v.Trim() == "*" ? old : v;
            }
            long type = a.Count > 4 && !a.Get(4).IsNothing ? A.I(a.Get(4)) : -1;
            var def = new Def
            {
                Name = cur.Name,
                Format = Pick(1, cur.Format),
                Header = Pick(2, cur.Header),
                FileName = Pick(3, cur.FileName),
                Type = type < 0 ? cur.Type : (int)type,
                Files = cur.Files,
            };
            Of(h).Modified[name] = def;
            return W.I(0);
        }

        [Builtin(
            "DevCurr",
            Doc = "1076377",
            Notes = "The device of the report running now: no report runs in the simulator, -1."
        )]
        private static Value DevCurr(SimHost h, CicodeTask t, ArgList a) => W.I(-1);

        [Builtin(
            "DevDisable",
            Doc = "1076379",
            Notes = "Disables or enables every device's output: the devices write to the sandbox either way, 0."
        )]
        private static Value DevDisable(SimHost h, CicodeTask t, ArgList a) => W.I(0);
    }
}
