using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>Error, trace and environment string built-ins that need the host.</summary>
    public static class Misc
    {
        [Builtin(
            "TraceMsg",
            Doc = "1188628",
            Effects = "writes the message to the host's log (the kernel / debugger window)",
            Notes = "The message's @(...) texts are translated first (native text here)."
        )]
        private static Value TraceMsg(SimHost h, CicodeTask t, ArgList a)
        {
            h.Note(t, "TraceMsg: " + LocalText(A.S(a.Get(0))));
            return W.I(0);
        }

        [Builtin(
            "ErrLog",
            Doc = "1105318",
            Effects = "writes the message to the host's log (SYSLOG.DAT on a runtime)",
            Notes = "Prefixed with the date and time on a runtime; the host log has the bare message (verify the SYSLOG format)."
        )]
        private static Value ErrLog(SimHost h, CicodeTask t, ArgList a)
        {
            h.Note(t, "ErrLog: " + LocalText(A.S(a.Get(0))));
            return W.I(0);
        }

        [Builtin(
            "ErrSetHw",
            Doc = "1105321",
            Effects = "sets the hardware error status of a device",
            Notes = "Device -1 raises Error on the task (suppressed); else the (device, type) status is kept for ErrGetHw; DeviceType -1 = from the device number (+ 8192 = an I/O device)."
        )]
        private static Value ErrSetHw(SimHost h, CicodeTask t, ArgList a)
        {
            long dev = A.I(a.Get(0)) & 0xFFFF;
            long err = A.I(a.Get(1));
            long type = a.Count > 2 ? A.Short(A.I(a.Get(2))) : -1;
            if (dev == 0xFFFF)
            {
                t.ErrSetLevel++;
                W.Raise(t, (int)(err & 0xFFFF));
                if (t.ErrSetLevel > 0)
                    t.ErrSetLevel--;
                return W.I(0);
            }
            if (type == -1)
            {
                type = (dev & 0x2000) != 0 ? 2 : 0;
                dev &= ~0x2000L;
            }
            h.HwErrors[(type, dev)] = err;
            return W.I(0);
        }

        [Builtin("ErrGetHw", Doc = "1105316", Notes = "The status ErrSetHw left (0 = none).")]
        private static Value ErrGetHw(SimHost h, CicodeTask t, ArgList a)
        {
            long dev = A.I(a.Get(0)) & 0xFFFF;
            long type = a.Count > 1 ? A.Short(A.I(a.Get(1))) : -1;
            if (type == -1)
            {
                type = (dev & 0x2000) != 0 ? 2 : 0;
                dev &= ~0x2000L;
            }
            return W.I(h.HwErrors.TryGetValue((type, dev), out long e) ? e : 0);
        }

        [Builtin(
            "ErrInfo",
            Doc = "1105339",
            Notes = "Type 0: the AN of the last error (as text) and clears it; no animation errors in background code: '0'. Other types ''."
        )]
        private static Value ErrInfo(SimHost h, CicodeTask t, ArgList a) =>
            A.Short(A.I(a.Get(0))) == 0 ? W.S("0") : W.Empty;

        [Builtin(
            "ErrCom",
            Doc = "1105314",
            Notes = "The task's I/O status: 0 (the simulated devices always answer; verify with a bad-quality tag at task start)."
        )]
        private static Value ErrCom(SimHost h, CicodeTask t, ArgList a) => W.I(0);

        [Builtin(
            "CodeTrace",
            Doc = "1193640",
            Errors = "269 bad task handle (returned)",
            Notes = "Sets the task's trace flags (& 0x11f); -2 / -3 the default for new tasks, -4 every task; the simulator keeps them in the task's data."
        )]
        private static Value CodeTrace(SimHost h, CicodeTask t, ArgList a)
        {
            long hd = A.Short(A.I(a.Get(0)));
            long mode = A.I(a.Get(1)) & 0x11F;
            if (hd == -2 || hd == -3)
            {
                h.TraceDefault = mode;
                return W.I(0);
            }
            if (hd == -4)
            {
                var s = t.Vm.Scheduler;
                if (s != null)
                    foreach (var x in s.All)
                        x.Data["trace"] = mode;
                return W.I(0);
            }
            var task = Tasks.Find(t, hd);
            if (task == null)
                return W.I(Codes.BadHandle);
            task.Data["trace"] = mode;
            return W.I(0);
        }

        [Builtin(
            "DebugBreak",
            Doc = "1105313",
            Errors = "342 Debug break (raised)",
            Notes = "With a debugger attached the runtime breaks here; the simulator asks the debugger to pause the task at its next line (task.Pause) and raises 342 when nothing is attached (verify the code)."
        )]
        private static Value DebugBreak(SimHost h, CicodeTask t, ArgList a)
        {
            if (t.Vm.LineHook != null && !t.Foreground)
            {
                t.Pause = true;
                return Value.Nothing;
            }
            W.Raise(t, 342);
            return Value.Nothing;
        }

        // ------------------------------------------------------------------ strings
        /// <summary>'@(text)' to its native text (no language database: the native string;
        /// '^' escapes inside kept as the compiler left them; '@(OK,20,C)' gives 'OK').</summary>
        internal static string LocalText(string s) => World.Display.DisplayLanguage.Translate(s, null);

        [Builtin(
            "StrToLocalText",
            Doc = "1192593",
            Notes = "No local language database is loaded: each @(native) becomes the native text (the runtime's result without a translation); 254 characters at most."
        )]
        private static Value StrToLocalText(SimHost h, CicodeTask t, ArgList a)
        {
            string r = LocalText(A.S(a.Get(0)));
            return W.S(r.Length > 254 ? r.Substring(0, 254) : r);
        }

        /// <summary>The [NAME] path operators of citect.ini [CtEdit] (RUN, DATA, USER,
        /// BIN, ...) and user paths.</summary>
        internal static string PathOf(SimHost h, string s)
        {
            string t = s.TrimStart();
            if (!t.StartsWith("[", StringComparison.Ordinal))
                return s;
            int close = t.IndexOf(']');
            if (close < 0)
                return s;
            string key = t.Substring(1, close - 1).Trim();
            string rest = t.Substring(close + 1);
            if (rest.StartsWith(":", StringComparison.Ordinal))
                rest = rest.Substring(1);
            string? dir = null;
            if (string.Equals(key, "RUN", StringComparison.OrdinalIgnoreCase) && h.World != null)
                dir = h.World.Unit.RootDir;
            if (dir == null)
            {
                string v = Project.CitectIni.IniValue("CtEdit", key, "");
                if (v.Length == 0)
                    v = Project.CitectIni.IniValue("Path", key, "");
                if (v.Length > 0)
                    dir = v;
            }
            if (dir == null)
                return s;
            dir = dir.TrimEnd('\\', '/');
            return dir + (rest.Length > 0 && rest[0] != '\\' && rest[0] != '/' ? "\\" : "") + rest;
        }

        [Builtin(
            "PathToStr",
            Doc = "1192566",
            Notes = "[RUN] = the compiled project's folder; other [NAME]s from citect.ini [CtEdit] then [Path]; a string without a known operator is unchanged; 254 characters at most. Only a string: no file is touched."
        )]
        private static Value PathToStr(SimHost h, CicodeTask t, ArgList a)
        {
            string r = PathOf(h, A.S(a.Get(0)));
            return W.S(r.Length > 254 ? r.Substring(0, 254) : r);
        }

        // ------------------------------------------------------------------ quality text
        // the runtime's English texts; the runtime translates them through
        // the language database when the text is localised ('@(%s)').
        private static readonly string[] QualGeneral = { "Bad", "Uncertain", "", "Good" };
        private static readonly string[] QualGood =
        {
            "Non Specific",
            "No Substatus",
            "No Substatus",
            "No Substatus",
            "No Substatus",
            "No Substatus",
            "Overridden",
        };
        private static readonly string[] QualBad =
        {
            "Non Specific",
            "Configuration Error",
            "Not Connected",
            "Device Failure",
            "Sensor Failure",
            "Last Known Value",
            "Comm. Failure",
            "Not Active",
            "Waiting For Initial Data",
        };
        private static readonly string[] QualUncr =
        {
            "Non Specific",
            "Last Usable Value",
            "No Substatus",
            "No Substatus",
            "Sensor Not Accurate",
            "Eng Unit Exceeded",
            "Subnormal",
        };
        private static readonly string[] QualLimit =
        {
            "Limit Not Set",
            "Below Low",
            "Above High",
            "Constant",
        };
        private static readonly string[] QualExt =
        {
            "Non Specific Extended Substatus",
            "Scheduled Device Offline",
            "Invalid Tag Configuration",
            "Invalid Tag Value",
            "Software Error",
            "Too Many Devices",
            "Comm. Not Initialized",
            "Bad Communication",
            "Tag Address Out Of Range",
            "Tag Not Readable",
            "Tag Not Writable",
            "Cluster Not Specified",
            "Cluster Not Found",
            "Cluster Disabled",
            "Session Not Connected",
            "Tag Not Resolved",
            "Tag Value Not Replicated",
            "Stale Tag Value",
            "Waiting For Initial Data",
            "Invalid Argument",
            "Tag Value Out Of Range",
        };

        /// <summary>One part's text: 274 raised for a value with no text.</summary>
        internal static string QualityPart(CicodeTask t, ulong q, int part)
        {
            int general = (int)((q & 0xFF) >> 6);
            int sub = (int)((q >> 2) & 0xF);
            string[]? table = null;
            int index = 0;
            switch (part)
            {
                case 0:
                case 1:
                    if (general == 2)
                    {
                        // Part 0 raises first; part 1 gives the same text without the error.
                        if (part == 0)
                            W.Raise(t, Codes.InvalidArg);
                        return ":2:";
                    }
                    if (part == 0)
                        return QualGeneral[general];
                    table =
                        general == 3 ? QualGood
                        : general == 1 ? QualUncr
                        : QualBad;
                    index = sub;
                    break;
                case 2:
                    return QualLimit[(int)(q & 3)];
                case 3:
                    table = QualExt;
                    index = (int)((q >> 8) & 0xFF);
                    break;
                case 4:
                    return (q & 0x10000) != 0 ? "Override" : "";
                case 5:
                    return (q & 0x20000) != 0 ? "Control Inhibit" : "";
            }
            if (table == null || index >= table.Length)
            {
                W.Raise(t, Codes.InvalidArg);
                return "";
            }
            return table[index];
        }

        [Builtin(
            "QualityToStr",
            Doc = "1189273",
            Errors = "274 a part with no text (general quality 2, substatus or extended value out of range, an unknown part)",
            Notes = "Part -1 = general + ' [Override]' + ' [Control Inhibit]' + ' - substatus' (the substatus only when its bits are not 0); -2 = general + ' - substatus'; 0..5 one part. General quality 2 shows ':2:'. bLocalized: no language database here, so the English native text."
        )]
        private static Value QualityToStr(SimHost h, CicodeTask t, ArgList a)
        {
            ulong q = A.U64(a.Get(0));
            long part = a.Count > 1 ? A.Short(A.I(a.Get(1))) : -1;
            if (part >= 0 && part <= 5)
                return W.S(QualityPart(t, q, (int)part));
            if (part == -1 || part == -2)
            {
                string general = QualityPart(t, q, 0);
                string sub = "";
                if ((q & 0x3C) != 0)
                {
                    string s = QualityPart(t, q, 1);
                    if (s.Length > 0)
                        sub = " - " + s;
                }
                if (part == -2)
                    return W.S(general + sub);
                string ovr = (q & 0x10000) != 0 ? " [Override]" : "";
                string inh = (q & 0x20000) != 0 ? " [Control Inhibit]" : "";
                return W.S(general + ovr + inh + sub);
            }
            W.Raise(t, Codes.InvalidArg);
            return W.Empty;
        }

        /// <summary>A font by handle (-1 = the default font, the first of the table), null when
        /// there is none.</summary>
        private static World.Display.FontInfo? FontOf(SimHost h, long handle)
        {
            var d = DisplayBuiltins.Of(h);
            return d.Font(
                handle == -1 ? d.DefaultFont : (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, handle))
            );
        }

        [Builtin(
            "StrCalcWidth",
            Doc = "1192568",
            Notes = "The width GDI's GetTextExtentPoint32 gives the text in the font (-1 = the default font, the first of the font table; World.Display.TextMeasure: the face, height and style of the font table entry, 96 dpi). No font: 8 pixels a character (guess)."
        )]
        private static Value StrCalcWidth(SimHost h, CicodeTask t, ArgList a) =>
            W.I(World.Display.TextMeasure.Width(FontOf(h, a.Count > 1 ? A.I(a.Get(1)) : -1), A.S(a.Get(0))));

        /// <summary>StrTruncFont's rule: iLength -1 (or none) the whole text; mode 2 characters;
        /// modes 0 / 1 pixels in the font (1 ends a cut text in '...').</summary>
        private static Value Trunc(World.Display.FontInfo? font, ArgList a)
        {
            string s = A.S(a.Get(0));
            long len = a.Count > 2 && !a[2].IsNothing ? A.I(a.Get(2)) : -1;
            long mode = a.Count > 3 && !a[3].IsNothing ? A.I(a.Get(3)) : 0;
            if (len < 0)
                return W.S(s);
            if (mode == 2)
                return W.S(s.Length > len ? s.Substring(0, (int)len) : s);
            return W.S(World.Display.TextMeasure.Fit(font, s, len, mode == 1));
        }

        [Builtin(
            "StrTruncFont",
            Doc = "1192599",
            Notes = "iLength -1 = the whole text; mode 2 cuts to iLength characters; modes 0 / 1 to the longest start no wider than iLength pixels in the named font (World.Display.TextMeasure: GDI measures; a font not in the table = the default font), mode 1 with '...' at the end within the width (guess: DT_END_ELLIPSIS)."
        )]
        private static Value StrTruncFont(SimHost h, CicodeTask t, ArgList a)
        {
            var d = DisplayBuiltins.Of(h);
            int hf = d.FontByName(A.S(a.Get(1)));
            return Trunc(d.Font(hf >= 0 ? hf : d.DefaultFont), a);
        }

        [Builtin(
            "StrTruncFontHnd",
            Doc = "1192600",
            Notes = "As StrTruncFont with a font handle (-1 = the default font)."
        )]
        private static Value StrTruncFontHnd(SimHost h, CicodeTask t, ArgList a) =>
            Trunc(FontOf(h, A.I(a.Get(1))), a);
    }
}
