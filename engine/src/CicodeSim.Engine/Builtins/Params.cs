using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using CicodeSim.Compiled;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>Parameters, environment, ini files and product information.</summary>
    public static class Params
    {
        /// <summary>The result ParameterGet fills (256 bytes): a value of
        /// 256+ characters does not fit and counts as not found there.</summary>
        private const int ParamBuffer = 0x100;

        /// <summary>Look up one section: citect.ini first, then the compiled parameter records.</summary>
        private static string? Lookup(ProcessData d, string section, string name) =>
            Lookup(d, section, name, out _);

        private static string? Lookup(ProcessData d, string section, string name, out string source)
        {
            source = "";
            if (d.Ini.TryGet(IniFiles.CitectIniKey, section, name, out string v) && v.Length > 0)
            {
                source = "citect.ini";
                return v.Length < ParamBuffer ? v : null;
            }
            string? p = ParamRecord(d, section, name);
            if (p != null && p.Length > 0 && p.Length < ParamBuffer)
            {
                source = d.ParamUncompiled ? "param.dbf" : "_PARAM.RDB";
                return p;
            }
            return null;
        }

        /// <summary>Who uses a parameter: the Cicode function, else the page object.</summary>
        private static string UserOf(CicodeTask t)
        {
            string f = t.Func?.Name ?? "";
            if (f.Length > 0)
                return f;
            int an = World.Display.TaskContext.An(t);
            return an > 0
                ? "AN " + an.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + t.Name
                : t.Name;
        }

        /// <summary>The first compiled parameter record of (section, name).</summary>
        internal static string? ParamRecord(ProcessData d, string section, string name)
        {
            if (d.ParamIndex == null)
            {
                // (section, name) in the C locale's lower case to the first record's DATA
                var idx = new Dictionary<string, string>(StringComparer.Ordinal);
                var f = d.CompiledTable("_PARAM");
                if (f != null && f.Sections.TryGetValue("PARAM", out var sec))
                {
                    var fs = sec.Fields;
                    FieldDesc? fsec = fs.Find(x => x.Name == "SECTION"),
                        fname = fs.Find(x => x.Name == "NAME"),
                        fdata = fs.Find(x => x.Name == "DATA");
                    if (fsec != null && fname != null && fdata != null)
                    {
                        foreach (var (_, start) in f.RawRecords("PARAM"))
                        {
                            string k =
                                A.CLower(f.DecodeField(fsec, start, sec.RecSize) as string ?? "")
                                + "\0"
                                + A.CLower(f.DecodeField(fname, start, sec.RecSize) as string ?? "");
                            if (!idx.ContainsKey(k))
                                idx[k] = f.DecodeField(fdata, start, sec.RecSize) as string ?? "";
                        }
                        d.ParamIndex = idx;
                    }
                }
                if (d.ParamIndex == null)
                {
                    d.ParamIndex = idx;
                    d.ParamUncompiled = true;
                }
            }
            if (d.ParamUncompiled)
                return d.Host.World?.Unit.Param(section, name);
            return d.ParamIndex.TryGetValue(A.CLower(section) + "\0" + A.CLower(name), out string? v)
                ? v
                : null;
        }

        /// <summary>ParameterGet's lookup ('\\computer\Section' first, then 'Section'), null when
        /// not found: for the built-ins that read a [Section] ([Format] names).</summary>
        internal static string? Get(SimHost h, string section, string name)
        {
            var d = ProcessData.Of(h);
            return Lookup(d, "\\\\" + d.ComputerName + "\\" + section, name) ?? Lookup(d, section, name);
        }

        [Builtin(
            "ParameterGet",
            Doc = "1188602",
            Notes = "Looks up '\\\\<computer name>\\Section' first, then 'Section'; in each citect.ini (the process's copy first: ParameterPut) then the compiled parameter records (_PARAM.RDB of the running project, first match without case; param.dbf when uncompiled). An empty citect.ini value falls through to the records (the read returns its length); an empty DATA counts as not found; a value of 256+ characters is not found. Not found: Default (when it fits, else ''). The runtime's alarm for a protected parameter (error 531) is not simulated."
        )]
        private static Value ParameterGet(SimHost h, CicodeTask t, ArgList a)
        {
            var d = ProcessData.Of(h);
            string section = A.S(a.Get(0)),
                name = A.S(a.Get(1)),
                def = A.S(a.Get(2));
            string? v = Lookup(d, "\\\\" + d.ComputerName + "\\" + section, name, out string src);
            if (v == null)
                v = Lookup(d, section, name, out src);
            if (v == null)
            {
                v = def.Length < ParamBuffer ? def : "";
                src = "default";
            }
            var u = d.UseOf(section, name);
            u.Value = v;
            u.Default = def;
            if (u.Source != "simulator" || src != "citect.ini")
                u.Source = src;
            u.Reads++;
            u.AddUser(UserOf(t));
            d.ParamSerial++;
            return W.S(C.Cut(v));
        }

        [Builtin(
            "ParameterPut",
            Doc = "1188603",
            Effects = "writes [Section] Name = Value into the process's copy of citect.ini (never the file)",
            Notes = "Returns 0 (always). ParameterGet reads the written value first (a plain section; an empty value falls through to the parameter records)."
        )]
        private static Value ParameterPut(SimHost h, CicodeTask t, ArgList a)
        {
            var d = ProcessData.Of(h);
            d.Ini.Put(IniFiles.CitectIniKey, A.S(a.Get(0)), A.S(a.Get(1)), A.S(a.Get(2)));
            var u = d.UseOf(A.S(a.Get(0)), A.S(a.Get(1)));
            u.Value = A.S(a.Get(2));
            u.Source = "ParameterPut";
            u.Writes++;
            u.AddUser(UserOf(t));
            d.ParamSerial++;
            return W.I(0);
        }

        [Builtin(
            "GetEnv",
            Doc = "1188590",
            Notes = "The simulated process's environment (ProcessData.Environment; names without case), never this computer's: COMPUTERNAME, USERNAME 'PlantSCADA', OS, PROCESSOR_ARCHITECTURE 'x86', SystemRoot, windir, TEMP / TMP 'C:\\Temp', ProgramData, ProgramFiles (guess on the defaults); '' when missing; 255 characters at most."
        )]
        private static Value GetEnv(SimHost h, CicodeTask t, ArgList a)
        {
            var d = ProcessData.Of(h);
            return W.S(d.Environment.TryGetValue(A.S(a.Get(0)), out string? v) ? C.Cut(v) : "");
        }

        [Builtin(
            "WndGetFileProfile",
            Doc = "1194003",
            Notes = "sFile with its path operators resolved ([RUN] etc. as PathToStr; a path without '[' unchanged); [sGroup] sName of the process's copy, else of the real file when it lies in a project folder of the unit or the install's Config folder (ProcessData.Ini.ExtraReadable adds folders); a relative path (the Windows folder) and a file above 32 MB (IniFiles.MaxFileBytes) read nothing. Missing: sDefault. 254 characters at most."
        )]
        private static Value WndGetFileProfile(SimHost h, CicodeTask t, ArgList a)
        {
            var d = ProcessData.Of(h);
            string group = A.S(a.Get(0)),
                name = A.S(a.Get(1)),
                def = A.S(a.Get(2)),
                file = Misc.PathOf(h, A.S(a.Get(3)));
            string v = d.Ini.TryGet(file, group, name, out string got) ? got : CitectText.TrimEnd(def);
            return W.S(C.Cut(v, 254));
        }

        [Builtin(
            "WndPutFileProfile",
            Doc = "1194008",
            Effects = "writes [sGroup] sName = sValue into the process's copy of the file (never the file)",
            Errors = "274 an empty group or name (returned)",
            Notes = "0 when written, 274 when the write fails (the return of IniWriteValueAsString); the copy refuses only an empty group or name (guess)."
        )]
        private static Value WndPutFileProfile(SimHost h, CicodeTask t, ArgList a)
        {
            var d = ProcessData.Of(h);
            string file = Misc.PathOf(h, A.S(a.Get(3)));
            return W.I(d.Ini.Put(file, A.S(a.Get(0)), A.S(a.Get(1)), A.S(a.Get(2))) ? 0 : Codes.InvalidArg);
        }

        // ------------------------------------------------------------------ product, version
        /// <summary>The brand of Bin\CtVersion.xml: (company,
        /// product); AVEVA / Plant SCADA when the file is not there.</summary>
        internal static (string Company, string Product) Brand()
        {
            var b = _brand;
            if (b != null)
                return (b.Item1, b.Item2);
            string company = "AVEVA",
                product = "Plant SCADA";
            try
            {
                string bin = Path.GetDirectoryName(Func0Table.Func0Default) ?? "";
                string xml = Path.Combine(bin, "CtVersion.xml");
                if (File.Exists(xml))
                {
                    var doc = new XmlDocument { XmlResolver = null };
                    using (
                        var r = XmlReader.Create(
                            xml,
                            new XmlReaderSettings
                            {
                                DtdProcessing = DtdProcessing.Prohibit,
                                XmlResolver = null,
                            }
                        )
                    )
                        doc.Load(r);
                    string? c = doc.SelectSingleNode("/VERSIONINFO/BRAND/COMPANY")?.InnerText;
                    string? p = doc.SelectSingleNode("/VERSIONINFO/BRAND/PRODUCT")?.InnerText;
                    if (c != null)
                        company = c;
                    if (p != null)
                        product = p;
                }
            }
            catch (Exception) { }
            _brand = Tuple.Create(company, product); // a reference: published whole (hosts on other threads read it)
            return (company, product);
        }

        private static Tuple<string, string>? _brand;

        /// <summary>The product version label.</summary>
        public const string VersionLabel = "8.50.0.368";

        [Builtin(
            "ProductInfo",
            Doc = "1188607",
            Notes = "0 the product (CtVersion.xml BRAND/PRODUCT 'Plant SCADA'), 1 the company ('AVEVA'), 2 '8' and 3 '50', 4 '8.50.0.368', other ''."
        )]
        private static Value ProductInfo(SimHost h, CicodeTask t, ArgList a)
        {
            var (company, product) = Brand();
            switch (A.Short(A.I(a.Get(0))))
            {
                case 0:
                    return W.S(C.Cut(product, 254));
                case 1:
                    return W.S(C.Cut(company, 254));
                case 2:
                    return W.S("8");
                case 3:
                    return W.S("50");
                case 4:
                    return W.S(VersionLabel);
            }
            return W.Empty;
        }

        [Builtin(
            "Version",
            Doc = "1188629",
            Notes = "0 '8', 1 '50', 2 the revision '0', 3 the version text '8.50.0.368', other ''."
        )]
        private static Value Version(SimHost h, CicodeTask t, ArgList a)
        {
            switch (A.Short(A.I(a.Get(0))))
            {
                case 0:
                    return W.S("8");
                case 1:
                    return W.S("50");
                case 2:
                    return W.S("0");
                case 3:
                    return W.S(VersionLabel);
            }
            return W.Empty;
        }

        [Builtin(
            "CitectInfo",
            Doc = "1188579",
            Errors = "274 'Queue' with an unknown queue name (raised, -1); 274 'General' type 7 or 28 (raised, 0); 418 'IODevice' with an unknown device (raised, 0)",
            Notes = "sType is atoi'd. One simulated process: 'Queue' type 0 = the length of the queue of that name (case-insensitive), other types 0; 'General' 3 / 5 the simulated start (Unix seconds), 4 / 6 the seconds since, 32 0, others 0; 'Memory' 1 GiB free and 2 GiB - 1 total in the unit sName names (0 / KB / MB / GB) (guess); 'Disk' 1 GiB free, 2 GiB - 1 total (bytes, KB, MB for types 0..5) (guess); 'Port', 'Unit' / 'IODevice' (a unit of units.dbf), 'Network', 'Stats' 0; another group 0 (no raise)."
        )]
        private static Value CitectInfo(SimHost h, CicodeTask t, ArgList a)
        {
            string group = A.S(a.Get(0)),
                name = A.S(a.Get(1));
            long type = C.Atol(A.S(a.Get(2)));
            if (C.IEq(group, "Queue"))
            {
                if (type != 0)
                    return W.I(0);
                foreach (var (_, q) in h.Queues.Live())
                    if (C.IEq(q.Name, name))
                        return W.I(q.Items.Count);
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            if (C.IEq(group, "General"))
            {
                var w = h.World;
                long start = w != null ? (long)Math.Truncate(w.Clock.StartUnix) : 0;
                long now = w != null ? w.Clock.Time32() : 0;
                switch (type)
                {
                    case 3:
                    case 5:
                        return W.I(start);
                    case 4:
                    case 6:
                        return W.I(now - start);
                    case 7:
                    case 28:
                        W.Raise(t, Codes.InvalidArg);
                        return W.I(0);
                }
                return W.I(0);
            }
            if (C.IEq(group, "Memory"))
            {
                long unit =
                    C.IEq(name, "KB") ? 1024
                    : C.IEq(name, "MB") ? 1024 * 1024
                    : C.IEq(name, "GB") ? 1024 * 1024 * 1024
                    : 1;
                switch (type)
                {
                    case 0:
                    case 2:
                        return W.I((1L << 30) / unit);
                    case 3:
                    case 4:
                        return W.I(int.MaxValue / unit);
                    case 1:
                        return W.I(50);
                    case 5:
                        return W.I(50);
                }
                return W.I(0);
            }
            if (C.IEq(group, "Disk"))
            {
                switch (type)
                {
                    case 0:
                        return W.I(1L << 30);
                    case 1:
                        return W.I(int.MaxValue);
                    case 2:
                        return W.I(1L << 20);
                    case 3:
                        return W.I(int.MaxValue / 1024);
                    case 4:
                        return W.I(1024);
                    case 5:
                        return W.I(int.MaxValue / (1024 * 1024));
                }
                return W.I(0);
            }
            if (C.IEq(group, "Unit") || C.IEq(group, "IODevice"))
            {
                if (Servers.IoDevice(h, name, "") == null)
                {
                    W.Raise(t, 418);
                    return W.I(0);
                }
                return W.I(type == 0 || type == 1 ? 1 : 0);
            }
            return W.I(0);
        }
    }
}
