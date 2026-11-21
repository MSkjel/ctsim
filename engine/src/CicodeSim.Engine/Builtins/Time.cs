using System;
using System.Globalization;
using System.Text;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>The Windows [intl] settings the runtime reads at start.</summary>
    public sealed class TimeLocale
    {
        public string TimeSep = ":";
        public string DateSep = "/";

        /// <summary>sShortDate: d / m / y pattern (m = month, case-insensitive).</summary>
        public string ShortDate = "dd-mm-yy";
        public string LongDate = "dddd, d mmmm yyyy";

        /// <summary>iDate: 0 MDY, 1 DMY, 2 YMD.</summary>
        public int DateOrder = 1;

        /// <summary>iTime: 0 = 12-hour clock, 1 = 24-hour.</summary>
        public bool Hour24;
        public string Am = "AM",
            Pm = "PM";

        public static readonly string[] MonthShort =
        {
            "Jan",
            "Feb",
            "Mar",
            "Apr",
            "May",
            "Jun",
            "Jul",
            "Aug",
            "Sep",
            "Oct",
            "Nov",
            "Dec",
        };
        public static readonly string[] MonthLong =
        {
            "January",
            "February",
            "March",
            "April",
            "May",
            "June",
            "July",
            "August",
            "September",
            "October",
            "November",
            "December",
        };
        public static readonly string[] DayShort = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        public static readonly string[] DayLong =
        {
            "Sunday",
            "Monday",
            "Tuesday",
            "Wednesday",
            "Thursday",
            "Friday",
            "Saturday",
        };

        /// <summary>the runtime's defaults: ':' '/' 'dd-mm-yy' 'dddd, d mmmm yyyy', DMY, 12-hour, AM / PM.</summary>
        public static TimeLocale Binary => new TimeLocale();

        /// <summary>sShortDate with its year run(s) replaced by one 'yyyy' (the extended date
        /// format).</summary>
        public string ExtendedDate
        {
            get
            {
                var sb = new StringBuilder();
                bool done = false;
                foreach (char c in ShortDate)
                {
                    if (c == 'y' || c == 'Y')
                    {
                        if (!done)
                        {
                            sb.Append("yyyy");
                            done = true;
                        }
                    }
                    else
                        sb.Append(c);
                }
                return sb.ToString();
            }
        }

        private DateTimeFormatInfo? _dtfi;

        private static string DotNetDate(string p)
        {
            var sb = new StringBuilder();
            bool quoted = false;
            foreach (char c in p)
            {
                if (c == '\'')
                    quoted = !quoted;
                if (!quoted && (c == 'm' || c == 'M'))
                    sb.Append('M');
                else if (!quoted && c == 'D')
                    sb.Append('d');
                else if (!quoted && c == 'Y')
                    sb.Append('y');
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>A .NET DateTimeFormatInfo of these settings (the TIMESTAMP functions format
        /// with .NET patterns, doc 1193770); invariant English names.</summary>
        public DateTimeFormatInfo DotNet()
        {
            if (_dtfi != null)
                return _dtfi;
            var d = (DateTimeFormatInfo)CultureInfo.InvariantCulture.DateTimeFormat.Clone();
            d.DateSeparator = DateSep;
            d.TimeSeparator = TimeSep;
            d.AMDesignator = Am;
            d.PMDesignator = Pm;
            d.ShortDatePattern = DotNetDate(ShortDate);
            d.LongDatePattern = DotNetDate(LongDate);
            d.ShortTimePattern = Hour24 ? "HH:mm" : "hh:mm tt";
            d.LongTimePattern = Hour24 ? "HH:mm:ss" : "hh:mm:ss tt";
            d.FullDateTimePattern = d.LongDatePattern + " " + d.LongTimePattern;
            _dtfi = DateTimeFormatInfo.ReadOnly(d);
            return _dtfi;
        }
    }

    /// <summary>Time and date built-ins.</summary>
    public static class Time
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private const long FtEpoch = 116444736000000000L;

        /// <summary>The runtime's broken-down time (record: shorts hour,
        /// min, sec, mday, mon 0..11, year - 1900, wday 0 = Sunday, yday 0..365, isdst).</summary>
        internal struct Tm
        {
            public int Hour,
                Min,
                Sec,
                MDay,
                Mon,
                Year,
                WDay,
                YDay,
                IsDst;
            public long T;
        }

        internal static long Now(CicodeTask t) => Clocks.UnixSeconds(t.Vm.Clock);

        internal static DateTime UtcOf(long t) => Epoch.AddSeconds(t);

        /// <summary>utc = _gmtime32 (a negative time gives all zeros), else the
        /// host zone's local time (a time before the epoch in local terms is the epoch).</summary>
        internal static Tm Break(SimHost h, long t, bool utc)
        {
            var r = new Tm { T = t };
            DateTime d;
            if (utc)
            {
                if (t < 0)
                    return r;
                d = UtcOf(t);
            }
            else
            {
                DateTime u = UtcOf(Math.Max(t, -86400L * 2));
                d = TimeZoneInfo.ConvertTimeFromUtc(u, h.TimeZone);
                if (d < new DateTime(1970, 1, 1))
                    d = new DateTime(1970, 1, 1);
                r.IsDst = h.TimeZone.IsDaylightSavingTime(d) ? 1 : 0;
            }
            r.Hour = d.Hour;
            r.Min = d.Minute;
            r.Sec = d.Second;
            r.MDay = d.Day;
            r.Mon = d.Month - 1;
            r.Year = d.Year - 1900;
            r.WDay = (int)d.DayOfWeek;
            r.YDay = d.DayOfYear - 1;
            return r;
        }

        /// <summary>_mktime32 of local fields (normalised like the CRT: day 0 = the last day of
        /// the previous month ...); a time in the spring-forward gap moves on by the gap.</summary>
        internal static long MkTime(SimHost h, int year1900, int mon, int mday, int hour, int min, int sec)
        {
            try
            {
                DateTime d = new DateTime(1900, 1, 1)
                    .AddYears(year1900)
                    .AddMonths(mon)
                    .AddDays(mday - 1)
                    .AddHours(hour)
                    .AddMinutes(min)
                    .AddSeconds(sec);
                d = DateTime.SpecifyKind(d, DateTimeKind.Unspecified);
                if (h.TimeZone.IsInvalidTime(d))
                    d = d.AddHours(1);
                DateTime u = TimeZoneInfo.ConvertTimeToUtc(d, h.TimeZone);
                return (long)Math.Floor((u - Epoch).TotalSeconds);
            }
            catch (ArgumentException)
            {
                return -1;
            }
        }

        private static int Opt(in ArgList a, int k, int def) => k < a.Count ? (int)A.I(a.Get(k)) : def;

        [Builtin("TimeCurrent", Doc = "1193728", Notes = "_time32 of the VM's clock (the simulated clock).")]
        private static Value TimeCurrent(SimHost h, CicodeTask t, ArgList a) => W.I(Now(t));

        [Builtin(
            "SysTime",
            Doc = "1193725",
            Notes = "The millisecond counter: the VM clock's monotonic milliseconds, low 32 bits (the runtime's timeGetTime + offset)."
        )]
        private static Value SysTime(SimHost h, CicodeTask t, ArgList a) =>
            W.I((int)(long)Math.Floor(t.Vm.Clock.MonotonicMs()));

        [Builtin(
            "SysTimeDelta",
            Doc = "1193726",
            Effects = "writes the counter into lStartTime",
            Notes = "Returns now - lStartTime and stores now in it."
        )]
        private static Value SysTimeDelta(SimHost h, CicodeTask t, ArgList a)
        {
            int now = (int)(long)Math.Floor(t.Vm.Clock.MonotonicMs());
            Value r = a.Get(0);
            long start = A.I(r);
            if (r.IsRef)
                A.Assign(t, r, now);
            return W.I((int)(now - start));
        }

        [Builtin("DateAdd", Doc = "1193717")]
        private static Value DateAdd(SimHost h, CicodeTask t, ArgList a) =>
            W.I((int)(A.I(a.Get(0)) + A.I(a.Get(1))));

        [Builtin("DateSub", Doc = "1193721")]
        private static Value DateSub(SimHost h, CicodeTask t, ArgList a) =>
            W.I((int)(A.I(a.Get(0)) - A.I(a.Get(1))));

        [Builtin(
            "_TimeSub",
            Errors = "274 unknown part (returns 0)",
            Notes = "Include labels: TimeHour 0, TimeMin 1, TimeSec 2, DateDay 3, DateWeekDay 4 (1 = Sunday), DateMonth 5, DateYear 6 (two digits), TimeMidNight 7, DateYearDay 8 (0-based), 9 full year, 10 days in the month; local time."
        )]
        private static Value TimeSub(SimHost h, CicodeTask t, ArgList a)
        {
            long time = A.I(a.Get(0));
            long part = A.Short(A.I(a.Get(1)));
            var tm = Break(h, time, false);
            switch (part)
            {
                case 0:
                    return W.I(tm.Hour);
                case 1:
                    return W.I(tm.Min);
                case 2:
                    return W.I(tm.Sec);
                case 3:
                    return W.I(tm.MDay);
                case 4:
                    return W.I(tm.WDay + 1);
                case 5:
                    return W.I(tm.Mon + 1);
                case 6:
                {
                    int y = tm.Year;
                    return W.I((ushort)(y - 100) > 0x707 ? y : y - 100);
                }
                case 7:
                    return W.I((int)(time - (tm.Min + tm.Hour * 60) * 60 - tm.Sec));
                case 8:
                    return W.I(tm.YDay);
                case 9:
                    return W.I(tm.Year < 0x76C ? tm.Year + 0x76C : tm.Year);
                case 10:
                    if (tm.Mon >= 0 && tm.Mon < 12)
                        return W.I(
                            tm.Mon == 1 && DateTime.IsLeapYear(1900 + tm.Year)
                                ? 29
                                : DateTime.DaysInMonth(2001, tm.Mon + 1)
                        );
                    break;
            }
            W.Raise(t, Codes.InvalidArg);
            return W.I(0);
        }

        // ------------------------------------------------------------------ TimeToStr
        /// <summary>a Windows date pattern (d dd ddd dddd, m mm mmm mmmm, yy yyyy,
        /// 'quoted' text with '' for a quote; month letters in either case).</summary>
        internal static string DatePattern(string p, in Tm tm)
        {
            var o = new StringBuilder();
            int i = 0;
            while (i < p.Length)
            {
                char c = p[i];
                if (c == '\'')
                {
                    i++;
                    while (i < p.Length)
                    {
                        if (p[i] == '\'')
                        {
                            if (i + 1 < p.Length && p[i + 1] == '\'')
                            {
                                o.Append('\'');
                                i += 2;
                                continue;
                            }
                            i++;
                            break;
                        }
                        o.Append(p[i++]);
                    }
                    continue;
                }
                if (c == 'd' || c == 'D')
                {
                    int n = 0;
                    while (i < p.Length && (p[i] == 'd' || p[i] == 'D'))
                    {
                        n++;
                        i++;
                    }
                    int wd = tm.WDay >= 0 && tm.WDay < 7 ? tm.WDay : 0;
                    if (n == 1)
                        o.Append(tm.MDay.ToString(CultureInfo.InvariantCulture));
                    else if (n == 2)
                        o.Append(tm.MDay.ToString("00", CultureInfo.InvariantCulture));
                    else if (n == 3)
                        o.Append(TimeLocale.DayShort[wd]);
                    else
                    {
                        o.Append(TimeLocale.DayLong[wd]);
                        if (n > 4)
                            o.Append(p, i - (n - 4), n - 4);
                    }
                    continue;
                }
                if (c == 'm' || c == 'M')
                {
                    int n = 0;
                    while (i < p.Length && (p[i] == 'm' || p[i] == 'M') && n < 5)
                    {
                        n++;
                        i++;
                    }
                    int mo = tm.Mon >= 0 && tm.Mon < 12 ? tm.Mon : 0;
                    if (n == 1)
                        o.Append((tm.Mon + 1).ToString(CultureInfo.InvariantCulture));
                    else if (n == 2)
                        o.Append((tm.Mon + 1).ToString("00", CultureInfo.InvariantCulture));
                    else if (n == 3)
                        o.Append(TimeLocale.MonthShort[mo]);
                    else if (n == 4)
                        o.Append(TimeLocale.MonthLong[mo]);
                    else
                    {
                        o.Append(TimeLocale.MonthLong[mo]).Append(p[i - 1]);
                        while (i < p.Length && (p[i] == 'm' || p[i] == 'M'))
                            o.Append(p[i++]);
                    }
                    continue;
                }
                if (c == 'y' || c == 'Y')
                {
                    int n = 0;
                    while (i < p.Length && (p[i] == 'y' || p[i] == 'Y') && n < 5)
                    {
                        n++;
                        i++;
                    }
                    int yy = (short)tm.Year % 100;
                    char last = p[i - 1];
                    if (n == 1)
                        o.Append(last);
                    else if (n == 2)
                        o.Append(yy.ToString(yy < 10 && yy >= 0 ? "00" : "0", CultureInfo.InvariantCulture));
                    else if (n == 3)
                        o.Append(yy.ToString(yy < 10 && yy >= 0 ? "00" : "0", CultureInfo.InvariantCulture))
                            .Append(last);
                    else if (n == 4)
                        o.Append((tm.Year + 1900).ToString(CultureInfo.InvariantCulture));
                    else
                    {
                        o.Append((tm.Year + 1900).ToString(CultureInfo.InvariantCulture)).Append(last);
                        while (i < p.Length && (p[i] == 'y' || p[i] == 'Y'))
                            o.Append(p[i++]);
                    }
                    continue;
                }
                o.Append(c);
                i++;
            }
            return o.ToString();
        }

        private static string D2(long v) => v.ToString("00", CultureInfo.InvariantCulture);

        /// <summary>TimeToStr.</summary>
        internal static string TimeToStr(SimHost h, CicodeTask? t, long time, int fmt, bool utc)
        {
            var L = h.Locale;
            switch (fmt)
            {
                case 0:
                case 1:
                case 2:
                case 3:
                case 4:
                case 9:
                case 10:
                    if (!(time > 0x12cea5ff && time < 0x7fe6c600))
                    {
                        if (time >= 0 && time < 86400)
                            return "";
                        if (t != null)
                            W.Raise(t, Codes.InvalidArg);
                        return "";
                    }
                    break;
                case 5:
                case 6:
                case 7:
                case 8:
                    if (time == -1)
                    {
                        if (t != null)
                            W.Raise(t, Codes.InvalidArg);
                        return "";
                    }
                    break;
                case 11:
                    if (!(time >= 0 && time < 86400))
                    {
                        if (t != null)
                            W.Raise(t, Codes.InvalidArg);
                        return "";
                    }
                    break;
                default:
                    if (t != null)
                        W.Raise(t, Codes.InvalidArg);
                    return "";
            }
            string sep = L.TimeSep;
            uint u = unchecked((uint)time);
            switch (fmt)
            {
                case 5:
                    return D2(u / 3600) + sep + D2(u / 60 % 60) + sep + D2(u % 60);
                case 6:
                    return D2(u / 3600000)
                        + sep
                        + D2(u / 60000 % 60)
                        + sep
                        + D2(u / 1000 % 60)
                        + "."
                        + (u % 1000).ToString("000", CultureInfo.InvariantCulture);
                case 7:
                    return D2(u / 3600) + sep + D2(u / 60 % 60);
                case 8:
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "{0,3} Days {1,2} Hours {2,2} min {3,2} sec",
                        u / 86400,
                        u / 3600 % 24,
                        u / 60 % 60,
                        u % 60
                    );
                case 11:
                {
                    // always 12-hour: 0 -> 12 AM, 12 -> 12 AM (as the runtime does), 13 -> 1 PM
                    uint hr = u / 3600;
                    bool pm = hr > 12;
                    if (pm)
                        hr -= 12;
                    else if (hr < 1)
                        hr += 12;
                    return D2(hr) + sep + D2(u / 60 % 60) + sep + D2(u % 60) + " " + (pm ? L.Pm : L.Am);
                }
            }
            var tm = Break(h, time, utc);
            int hour = tm.Hour;
            string suffix = "";
            if (!L.Hour24)
            {
                if (hour >= 12)
                {
                    if (hour > 12)
                        hour -= 12;
                    suffix = " " + L.Pm;
                }
                else
                {
                    if (hour < 1)
                        hour += 12;
                    suffix = " " + L.Am;
                }
            }
            switch (fmt)
            {
                case 0:
                    return D2(hour) + sep + D2(tm.Min) + suffix;
                case 1:
                    return D2(hour) + sep + D2(tm.Min) + sep + D2(tm.Sec) + suffix;
                case 2:
                    return DatePattern(L.ShortDate, tm);
                case 3:
                    return DatePattern(L.LongDate, tm);
                case 4:
                    return TimeLocale.DayShort[tm.WDay]
                        + " "
                        + TimeLocale.MonthShort[tm.Mon]
                        + " "
                        + D2(tm.MDay)
                        + " "
                        + (tm.Year + 1900).ToString("0000", CultureInfo.InvariantCulture)
                        + " "
                        + D2(hour)
                        + sep
                        + D2(tm.Min)
                        + sep
                        + D2(tm.Sec)
                        + suffix;
                case 9:
                    return DatePattern(L.ExtendedDate, tm);
                default:
                    return (tm.Year + 1900).ToString("0000", CultureInfo.InvariantCulture)
                        + "-"
                        + D2(tm.Mon + 1)
                        + "-"
                        + D2(tm.MDay)
                        + " "
                        + D2(tm.Hour)
                        + ":"
                        + D2(tm.Min)
                        + ":"
                        + D2(tm.Sec);
            }
        }

        [Builtin(
            "TimeToStr",
            Doc = "1193736",
            Errors = "274 a time out of the format's range or an unknown format (returns '')",
            Notes = "Formats 0-4, 9, 10 need 1980-01-01..2037 (a time of day 0..86399 gives '' without an error); 5-8 periods; 11 a time of day, always 12-hour. Separators, date patterns, the 12 / 24 hour clock and AM / PM from the host's TimeLocale."
        )]
        private static Value TimeToStr(SimHost h, CicodeTask t, ArgList a) =>
            W.S(TimeToStr(h, t, A.I(a.Get(0)), (int)A.Short(A.I(a.Get(1))), Opt(a, 2, 0) != 0));

        [Builtin(
            "StrToTime",
            Doc = "1192596",
            Errors = "274 not a time of day (returns -1)",
            Notes = "'h[:m[:s]] [AM|PM]' with the locale's time separator; seconds since midnight; PM adds 12 to an hour below 12, AM makes 12 into 0."
        )]
        private static Value StrToTime(SimHost h, CicodeTask t, ArgList a)
        {
            long r = StrToTimeCore(h.Locale, A.S(a.Get(0)));
            if (r < 0)
                W.Raise(t, Codes.InvalidArg);
            return W.I(r);
        }

        private static bool Digit(string s, int i) => i < s.Length && s[i] >= '0' && s[i] <= '9';

        private static int Atoi(string s, ref int i)
        {
            long v = 0;
            while (Digit(s, i))
                v = Math.Min(v * 10 + (s[i++] - '0'), int.MaxValue);
            return (int)v;
        }

        internal static long StrToTimeCore(TimeLocale L, string text)
        {
            string s = A.SkipSpace(text);
            int i = 0;
            if (!Digit(s, i))
                return -1;
            short hh = (short)Atoi(s, ref i);
            short mm = 0,
                ss = 0;
            bool ok = true;
            char sep = L.TimeSep.Length > 0 ? L.TimeSep[0] : '\0';
            if (i < s.Length && s[i] == sep)
            {
                i++;
                if (!Digit(s, i))
                    ok = false;
                else
                {
                    mm = (short)Atoi(s, ref i);
                    if (i < s.Length && s[i] == sep)
                    {
                        i++;
                        if (!Digit(s, i))
                            ok = false;
                        else
                            ss = (short)Atoi(s, ref i);
                    }
                }
            }
            if (ok)
            {
                string rest = A.SkipSpace(s.Substring(i));
                if (rest.Length > 0)
                {
                    if (L.Pm.Length > 0 && rest.StartsWith(L.Pm, StringComparison.OrdinalIgnoreCase))
                        hh = hh > 11 ? hh : (short)(hh + 12);
                    else if (L.Am.Length > 0 && rest.StartsWith(L.Am, StringComparison.OrdinalIgnoreCase))
                        hh = hh == 12 ? (short)0 : hh;
                    else
                        ok = false;
                }
            }
            if (!ok || ss < 0 || ss > 59 || mm < 0 || mm > 59 || hh < 0 || hh > 23)
                return -1;
            return ss + (mm + hh * 60L) * 60;
        }

        [Builtin(
            "StrToDate",
            Doc = "1192587",
            Notes = "Three parts split at the locale's date separator in its date order (iDate); a month may be a short month name; a year below 80 is 2000+, 80..1900 is 1900+, above 1900 as given; only 1980..2037 valid; returns local midnight, -1 (no error raised) when invalid."
        )]
        private static Value StrToDate(SimHost h, CicodeTask t, ArgList a) =>
            W.I(StrToDateCore(h, A.S(a.Get(0))));

        internal static long StrToDateCore(SimHost h, string text)
        {
            var L = h.Locale;
            string s = A.Cap(text);
            char[] delims = L.DateSep.Length > 0 ? L.DateSep.ToCharArray() : new[] { '/' };
            string[] parts = s.Split(delims, StringSplitOptions.RemoveEmptyEntries);
            string? p0 = parts.Length > 0 ? parts[0] : null,
                p1 = parts.Length > 1 ? parts[1] : null,
                p2 = parts.Length > 2 ? parts[2] : null;
            int Num(string? x) => x == null ? 0 : (short)Conv.StrToInt(x, out _);
            int MonthName(string? x)
            {
                if (x == null)
                    return 0;
                for (int k = 0; k < 12; k++)
                    if (string.Equals(TimeLocale.MonthShort[k], x, StringComparison.OrdinalIgnoreCase))
                        return k + 1;
                return 0;
            }
            int a0 = Num(p0),
                a1 = Num(p1),
                a2 = Num(p2);
            int day,
                month,
                year;
            string? yearTok;
            if (L.DateOrder == 0)
            {
                month = a0 == 0 && p0 != null ? MonthName(p0) : a0;
                day = a1;
                year = a2;
                yearTok = p2;
            }
            else if (L.DateOrder != 2)
            {
                day = a0;
                month = a1 == 0 && p1 != null ? MonthName(p1) : a1;
                year = a2;
                yearTok = p2;
            }
            else
            {
                year = a0;
                month = a1 == 0 && p1 != null ? MonthName(p1) : a1;
                day = a2;
                yearTok = p0;
            }
            if (yearTok != null)
            {
                string y = yearTok.TrimStart(' ', '\t', '\n', '\v', '\f', '\r');
                if (!Digit(y, 0))
                    return -1;
            }
            if (year < 80)
                year += 100;
            else if (year > 1900)
                year -= 1900;
            bool valid =
                month - 1 >= 0
                && month - 1 < 12
                && day != 0
                && day > 0
                && day < DateTime.DaysInMonth(2001, Math.Max(1, Math.Min(12, month))) + 1
                && (uint)(year - 80) <= 0x39;
            if (!valid)
            {
                bool leapFeb29 =
                    month == 2 && day == 29 && DateTime.IsLeapYear(1900 + year) && (uint)(year - 80) <= 0x39;
                if (!leapFeb29)
                    return -1;
            }
            return MkTime(h, year, month - 1, day, 0, 0, 0);
        }

        [Builtin(
            "StrToPeriod",
            Doc = "1192594",
            Errors = "274 not a period (returns -1)",
            Notes = "'s', 'm:s' or 'h:m:s' (minutes and seconds 0..59, hours any) to seconds."
        )]
        private static Value StrToPeriod(SimHost h, CicodeTask t, ArgList a)
        {
            long r = StrToPeriodCore(h.Locale, A.S(a.Get(0)));
            if (r < 0)
                W.Raise(t, Codes.InvalidArg);
            return W.I(r);
        }

        internal static long StrToPeriodCore(TimeLocale L, string text)
        {
            string s = A.SkipSpace(text);
            int i = 0;
            if (!Digit(s, i))
                return -1;
            char sep = L.TimeSep.Length > 0 ? L.TimeSep[0] : '\0';
            long a0 = Atoi(s, ref i);
            if (!(i < s.Length && s[i] == sep))
                return a0;
            i++;
            if (!Digit(s, i))
                return -1;
            long a1 = Atoi(s, ref i);
            if (a1 >= 60)
                return -1;
            if (!(i < s.Length && s[i] == sep))
                return a0 * 60 + a1;
            i++;
            if (!Digit(s, i))
                return -1;
            long a2 = Atoi(s, ref i);
            if (a2 < 0 || a2 >= 60)
                return -1;
            long total = a0 * 3600 + a1 * 60 + a2;
            return total >= 0 && total < 0x80000000L ? total : -1;
        }

        [Builtin(
            "DateInfo",
            Doc = "1193719",
            Notes = "1 date order, 2 separator, 3 short, 4 long, 5 extended date format, 6 / 7 short / long weekday (1 = Sunday), 8 / 9 short / long month; anything else ''."
        )]
        private static Value DateInfo(SimHost h, CicodeTask t, ArgList a)
        {
            var L = h.Locale;
            long n = A.Short(A.I(a.Get(0)));
            long x = A.Short(A.I(a.Get(1)));
            switch (n)
            {
                case 1:
                    return W.S(W.Inv(L.DateOrder));
                case 2:
                    return W.S(L.DateSep);
                case 3:
                    return W.S(L.ShortDate);
                case 4:
                    return W.S(L.LongDate);
                case 5:
                    return W.S(L.ExtendedDate);
                case 6:
                    return W.S(x >= 1 && x <= 7 ? TimeLocale.DayShort[x - 1] : "");
                case 7:
                    return W.S(x >= 1 && x <= 7 ? TimeLocale.DayLong[x - 1] : "");
                case 8:
                    return W.S(x >= 1 && x <= 12 ? TimeLocale.MonthShort[x - 1] : "");
                case 9:
                    return W.S(x >= 1 && x <= 12 ? TimeLocale.MonthLong[x - 1] : "");
            }
            return W.Empty;
        }

        [Builtin(
            "TimeInfo",
            Doc = "1193730",
            Notes = "1 '0' 12-hour / '1' 24-hour, 2 time separator, 3 AM, 4 PM; anything else ''."
        )]
        private static Value TimeInfo(SimHost h, CicodeTask t, ArgList a)
        {
            var L = h.Locale;
            switch (A.Short(A.I(a.Get(0))))
            {
                case 1:
                    return W.S(L.Hour24 ? "1" : "0");
                case 2:
                    return W.S(L.TimeSep);
                case 3:
                    return W.S(L.Am);
                case 4:
                    return W.S(L.Pm);
            }
            return W.Empty;
        }

        [Builtin(
            "TimeUTCOffset",
            Doc = "1193737",
            Errors = "274 a time outside 1980..2037 (returns 0)",
            Notes = "The host zone's offset in seconds at that time (daylight saving included)."
        )]
        private static Value TimeUTCOffset(SimHost h, CicodeTask t, ArgList a)
        {
            long time = A.I(a.Get(0));
            if (!(unchecked((uint)(time + 0xed315a00L)) < 0x6d182000u && time >= 0))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(0);
            }
            return W.I((long)h.TimeZone.GetUtcOffset(UtcOf(time)).TotalSeconds);
        }

        [Builtin("TimeToOLEDate", Doc = "1193735", Errors = "274 a time outside 1980..2037 (returns 0.0)")]
        private static Value TimeToOLEDate(SimHost h, CicodeTask t, ArgList a)
        {
            long time = A.I(a.Get(0));
            bool local = A.Short(A.I(a.Get(1))) != 0;
            if (!(unchecked((uint)(time + 0xed315a00L)) < 0x6d182000u && time >= 0))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.R(0.0);
            }
            DateTime d = UtcOf(time);
            if (local)
                d = TimeZoneInfo.ConvertTimeFromUtc(d, h.TimeZone);
            return W.R(d.ToOADate());
        }

        [Builtin("OLEDateToTime", Doc = "1193724", Errors = "274 not a date of 1980..2037 (returns 0)")]
        private static Value OLEDateToTime(SimHost h, CicodeTask t, ArgList a)
        {
            double ole = A.R(a.Get(0));
            bool local = A.Short(A.I(a.Get(1))) != 0;
            try
            {
                DateTime d = DateTime.FromOADate(ole);
                long time;
                if (local)
                {
                    d = DateTime.SpecifyKind(d, DateTimeKind.Unspecified);
                    if (h.TimeZone.IsInvalidTime(d))
                        d = d.AddHours(1);
                    time = (long)
                        Math.Floor((TimeZoneInfo.ConvertTimeToUtc(d, h.TimeZone) - Epoch).TotalSeconds);
                }
                else
                    time = (long)Math.Floor((DateTime.SpecifyKind(d, DateTimeKind.Utc) - Epoch).TotalSeconds);
                if (unchecked((uint)(time + 0xed315a00L)) < 0x6d182000u)
                    return W.I(time);
            }
            catch (ArgumentException) { }
            W.Raise(t, Codes.InvalidArg);
            return W.I(0);
        }

        [Builtin(
            "TimeSet",
            Doc = "1193734",
            Errors = "274 invalid time (returned); 276 the VM clock cannot be set (returned)",
            Notes = "Sets the simulated wall clock to the supplied Unix time. Elapsed simulation time and scheduled timers are preserved."
        )]
        private static Value TimeSet(SimHost h, CicodeTask t, ArgList a)
        {
            long time = A.I(a.Get(0));
            var tm = Break(h, time, false);
            if (
                tm.Hour < 24
                && tm.Min < 60
                && tm.Sec < 60
                && tm.MDay >= 1
                && tm.MDay <= 31
                && tm.Mon < 12
                && (uint)(tm.Year - 80) < 0x3a
            )
            {
                if (!(t.Vm.Clock is World.SimClock clock))
                    return W.I(276);
                return W.I(clock.SetUnixTime(time) ? 0 : Codes.InvalidArg);
            }
            return W.I(Codes.InvalidArg);
        }

        [Builtin(
            "_Wave",
            Notes = "Test signal on the millisecond counter: iType 0 sine, 1 square, 2 triangle, 3 sawtooth, 4 random walk (the CRT rand of the VM); period iTime seconds (at least 1), phase in seconds; (iHigh - min(iLow, iHigh)) * v + min(iLow, iHigh), truncated."
        )]
        private static Value Wave(SimHost h, CicodeTask t, ArgList a)
        {
            long type = A.Short(A.I(a.Get(0)));
            long period = A.Short(A.I(a.Get(1)));
            long lo = A.Short(A.I(a.Get(2))),
                hi = A.Short(A.I(a.Get(3)));
            long phase = A.Short(A.I(a.Get(4)));
            if (period < 1)
                period = 1;
            uint ms = unchecked((uint)(long)Math.Floor(t.Vm.Clock.MonotonicMs()));
            uint pos = unchecked((uint)(ms + phase * 1000)) % (uint)(period * 1000);
            double x = pos / (double)(period * 1000);
            double v;
            switch (type)
            {
                case 1:
                    v = x < 0.5 ? 1.0 : 0.0;
                    break;
                case 2:
                    v = (x >= 0.5 ? 1.0 - x : x) * 2;
                    break;
                case 3:
                    v = x;
                    break;
                case 4:
                {
                    var vm = t.Vm;
                    long Rand()
                    {
                        long seed = (vm.RandSeed * 214013 + 2531011) & 0xFFFFFFFFL;
                        vm.RandSeed = seed;
                        return (seed >> 16) & 0x7FFF;
                    }
                    double step = Math.Pow(Rand() / 32767.0, 2.0) * 0.1;
                    double walk = h.WaveWalk + (Rand() - 0x3fff) * step;
                    if (walk > 32767.0)
                        walk = 32767.0;
                    else if (walk < 0.0)
                        walk = 0.0;
                    h.WaveWalk = walk;
                    v = walk / 32767.0;
                    break;
                }
                default:
                    v = (Math.Sin((x + x) * Math.PI) + 1.0) * 0.5;
                    break;
            }
            long l = Math.Min(lo, hi);
            return W.I((long)((hi - l) * v + l));
        }

        // ------------------------------------------------------------------ TIMESTAMP
        internal static ulong FtOf(DateTime utc) => (ulong)(utc.Ticks - 504911232000000000L);

        internal static bool DateOfFt(ulong ft, out DateTime utc)
        {
            utc = default;
            if (ft == 0 || ft > (ulong)(DateTime.MaxValue.Ticks - 504911232000000000L))
                return false;
            utc = new DateTime((long)ft + 504911232000000000L, DateTimeKind.Utc);
            return true;
        }

        private static DateTime View(SimHost h, DateTime utc, bool asUtc) =>
            asUtc ? utc : TimeZoneInfo.ConvertTimeFromUtc(utc, h.TimeZone);

        /// <summary>A custom .NET date format names the UTC offset ('z', 'K' outside quotes and
        /// escapes), or is the standard 'o' / 'O' (which prints it).</summary>
        internal static bool NamesOffset(string fmt)
        {
            if (fmt.Length == 1)
                return fmt == "o" || fmt == "O";
            char quote = '\0';
            for (int i = 0; i < fmt.Length; i++)
            {
                char c = fmt[i];
                if (quote != '\0')
                {
                    if (c == quote)
                        quote = '\0';
                }
                else if (c == '\'' || c == '"')
                    quote = c;
                else if (c == '\\')
                    i++;
                else if (c == 'z' || c == 'K')
                    return true;
            }
            return false;
        }

        /// <summary>Format a TIMESTAMP the way .NET's DateTime would on the simulated computer.</summary>
        internal static string FormatTs(
            SimHost h,
            DateTime utc,
            bool asUtc,
            string fmt,
            DateTimeFormatInfo dtfi
        )
        {
            if (fmt == "U")
                return utc.ToString("U", dtfi);
            DateTime d = View(h, utc, asUtc);
            if (NamesOffset(fmt))
            {
                if (!asUtc)
                    return new DateTimeOffset(
                        DateTime.SpecifyKind(d, DateTimeKind.Unspecified),
                        h.TimeZone.GetUtcOffset(utc)
                    ).ToString(fmt, dtfi);
                // a UTC DateTime: 'K' / 'o' print Z; .NET Framework prints this computer's offset for 'z'
                if (fmt.IndexOf('K') < 0 && fmt != "o" && fmt != "O")
                    return new DateTimeOffset(
                        DateTime.SpecifyKind(utc, DateTimeKind.Unspecified),
                        TimeSpan.Zero
                    ).ToString(fmt, dtfi);
            }
            return d.ToString(fmt, dtfi);
        }

        private static Value Ts(ulong ft) => Value.Int(Types.Timestamp, unchecked((long)ft));

        [Builtin("TimestampCurrent", Doc = "1193767", Notes = "The VM clock's FILETIME (UTC, 100 ns).")]
        private static Value TimestampCurrent(SimHost h, CicodeTask t, ArgList a) =>
            Ts(t.Vm.Clock.NowFiletime());

        [Builtin(
            "TimestampCreate",
            Doc = "1193768",
            Errors = "274 invalid parts (returns 0, INVALID_TIMESTAMP)",
            Notes = "bUtc defaults to 0 (local) by FUNC0 (the help says UTC). A local time in the spring-forward gap moves on by the gap (verify)."
        )]
        private static Value TimestampCreate(SimHost h, CicodeTask t, ArgList a)
        {
            try
            {
                var d = new DateTime(
                    (int)A.I(a.Get(0)),
                    (int)A.I(a.Get(1)),
                    (int)A.I(a.Get(2)),
                    (int)A.I(a.Get(3)),
                    (int)A.I(a.Get(4)),
                    (int)A.I(a.Get(5)),
                    Opt(a, 6, 0)
                );
                DateTime u;
                if (Opt(a, 7, 0) != 0)
                    u = DateTime.SpecifyKind(d, DateTimeKind.Utc);
                else
                {
                    if (h.TimeZone.IsInvalidTime(d))
                        d = d.AddHours(1);
                    u = TimeZoneInfo.ConvertTimeToUtc(d, h.TimeZone);
                }
                if (u.Year < 1601)
                    throw new ArgumentOutOfRangeException();
                return Ts(FtOf(u));
            }
            catch (ArgumentException)
            {
                W.Raise(t, Codes.InvalidArg);
                return Ts(0);
            }
        }

        private static ulong AddPart(ulong ft, long off, long part, out bool ok)
        {
            ok = false;
            if (!DateOfFt(ft, out DateTime d))
                return 0;
            try
            {
                switch (part)
                {
                    case 0:
                        d = d.AddYears((int)off);
                        break;
                    case 1:
                        d = d.AddMonths((int)off);
                        break;
                    case 2:
                        d = d.AddDays(off);
                        break;
                    case 3:
                        d = d.AddHours(off);
                        break;
                    case 4:
                        d = d.AddMinutes(off);
                        break;
                    case 5:
                        d = d.AddSeconds(off);
                        break;
                    case 6:
                        d = d.AddMilliseconds(off);
                        break;
                    default:
                        return 0;
                }
                if (d.Year < 1601)
                    return 0;
                ok = true;
                return FtOf(d);
            }
            catch (ArgumentException)
            {
                return 0;
            }
        }

        [Builtin(
            "TimestampAdd",
            Doc = "1193766",
            Errors = "274 invalid timestamp or part (returns 0)",
            Notes = "Years / months on the UTC calendar (verify); other parts exact."
        )]
        private static Value TimestampAdd(SimHost h, CicodeTask t, ArgList a)
        {
            ulong r = AddPart(A.U64(a.Get(0)), A.I(a.Get(1)), Opt(a, 2, 5), out bool ok);
            if (!ok)
                W.Raise(t, Codes.InvalidArg);
            return Ts(r);
        }

        [Builtin("TimestampSub", Doc = "1193772", Errors = "274 invalid timestamp or part (returns 0)")]
        private static Value TimestampSub(SimHost h, CicodeTask t, ArgList a)
        {
            ulong r = AddPart(A.U64(a.Get(0)), -A.I(a.Get(1)), Opt(a, 2, 5), out bool ok);
            if (!ok)
                W.Raise(t, Codes.InvalidArg);
            return Ts(r);
        }

        [Builtin(
            "TimestampDifference",
            Doc = "1193769",
            Errors = "274 invalid timestamp or part (returns 0)",
            Notes = "|t1 - t2| in the part's units; cumulative = the whole period, non-cumulative = the part's own field (seconds 0..59 ...). Months / years count whole calendar months (verify)."
        )]
        private static Value TimestampDifference(SimHost h, CicodeTask t, ArgList a)
        {
            ulong f1 = A.U64(a.Get(0)),
                f2 = A.U64(a.Get(1));
            long part = Opt(a, 2, 5);
            bool cum = Opt(a, 3, 1) != 0;
            if (!DateOfFt(f1, out DateTime d1) || !DateOfFt(f2, out DateTime d2) || part < 0 || part > 6)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(0);
            }
            if (d1 < d2)
            {
                var x = d1;
                d1 = d2;
                d2 = x;
            }
            long ms = (long)((d1.Ticks - d2.Ticks) / 10000);
            int months = (d1.Year - d2.Year) * 12 + d1.Month - d2.Month;
            if (months > 0 && d2.AddMonths(months) > d1)
                months--;
            long r;
            switch (part)
            {
                case 0:
                    r = months / 12;
                    break;
                case 1:
                    r = cum ? months : months % 12;
                    break;
                case 2:
                    r = cum
                        ? ms / 86400000
                        : (long)((d1 - d2.AddMonths(months)).Ticks / TimeSpan.TicksPerDay);
                    break;
                case 3:
                    r = cum ? ms / 3600000 : ms / 3600000 % 24;
                    break;
                case 4:
                    r = cum ? ms / 60000 : ms / 60000 % 60;
                    break;
                case 5:
                    r = cum ? ms / 1000 : ms / 1000 % 60;
                    break;
                default:
                    r = cum ? ms : ms % 1000;
                    break;
            }
            return W.I((int)r);
        }

        [Builtin(
            "TimestampGetPart",
            Doc = "1193771",
            Errors = "274 invalid timestamp or part (returns -1)",
            Notes = "0 year .. 6 millisecond, 7 milliseconds since midnight; bUtc 0 = local."
        )]
        private static Value TimestampGetPart(SimHost h, CicodeTask t, ArgList a)
        {
            long part = A.I(a.Get(1));
            if (!DateOfFt(A.U64(a.Get(0)), out DateTime u) || part < 0 || part > 7)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            DateTime d = View(h, u, Opt(a, 2, 0) != 0);
            switch (part)
            {
                case 0:
                    return W.I(d.Year);
                case 1:
                    return W.I(d.Month);
                case 2:
                    return W.I(d.Day);
                case 3:
                    return W.I(d.Hour);
                case 4:
                    return W.I(d.Minute);
                case 5:
                    return W.I(d.Second);
                case 6:
                    return W.I(d.Millisecond);
                default:
                    return W.I((long)(d.TimeOfDay.Ticks / 10000));
            }
        }

        [Builtin(
            "TimestampToTimeInt",
            Doc = "1193774",
            Errors = "274 invalid timestamp (returns -1)",
            Notes = "Seconds since 1970; bUtc 0 gives the local wall-clock seconds (UTC + the zone's offset) (verify)."
        )]
        private static Value TimestampToTimeInt(SimHost h, CicodeTask t, ArgList a)
        {
            if (
                !DateOfFt(A.U64(a.Get(0)), out DateTime u)
                || u < Epoch
                || u >= Epoch.AddSeconds(int.MaxValue)
            )
            {
                W.Raise(t, Codes.InvalidArg);
                return W.I(-1);
            }
            DateTime d = View(h, u, Opt(a, 1, 1) != 0);
            return W.I((long)Math.Floor((DateTime.SpecifyKind(d, DateTimeKind.Utc) - Epoch).TotalSeconds));
        }

        [Builtin(
            "TimeIntToTimestamp",
            Doc = "1193765",
            Errors = "274 a negative time or invalid (returns 0)",
            Notes = "bUtc 0: the given seconds are local wall-clock time."
        )]
        private static Value TimeIntToTimestamp(SimHost h, CicodeTask t, ArgList a)
        {
            long ti = A.I(a.Get(0));
            long ms = Opt(a, 1, 0);
            if (ti < 0)
            {
                W.Raise(t, Codes.InvalidArg);
                return Ts(0);
            }
            try
            {
                DateTime d = Epoch.AddSeconds(ti).AddMilliseconds(ms);
                if (Opt(a, 2, 1) == 0)
                {
                    DateTime l = DateTime.SpecifyKind(d, DateTimeKind.Unspecified);
                    if (h.TimeZone.IsInvalidTime(l))
                        l = l.AddHours(1);
                    d = TimeZoneInfo.ConvertTimeToUtc(l, h.TimeZone);
                }
                return Ts(FtOf(d));
            }
            catch (ArgumentException)
            {
                W.Raise(t, Codes.InvalidArg);
                return Ts(0);
            }
        }

        private static readonly string[] TsFormats =
        {
            "t",
            "T",
            "d",
            "D",
            "g",
            "G",
            "f",
            "F",
            "M",
            "Y",
            "R",
            "s",
            "u",
            "U",
            "",
            "",
        };

        [Builtin(
            "TimestampToStr",
            Doc = "1193773",
            Errors = "274 invalid timestamp or format (returns '')",
            Notes = "Formats 0..13 = the .NET standard patterns t T d D g G f F M Y R s u U of the host locale, 14 long time + '.fff', 15 the FILETIME number (verify the mapping against a runtime)."
        )]
        private static Value TimestampToStr(SimHost h, CicodeTask t, ArgList a)
        {
            ulong ft = A.U64(a.Get(0));
            long f = A.I(a.Get(1));
            if (f == 15)
                return W.S(ft.ToString(CultureInfo.InvariantCulture));
            if (!DateOfFt(ft, out DateTime u) || f < 0 || f > 14)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.Empty;
            }
            bool asUtc = Opt(a, 2, 0) != 0;
            var dtfi = h.Locale.DotNet();
            if (f == 14)
                return W.S(A.Cap(View(h, u, asUtc).ToString(dtfi.LongTimePattern + ".fff", dtfi)));
            // R and u are UTC patterns: .NET formats the value as given; U converts it to UTC
            return W.S(A.Cap(FormatTs(h, u, asUtc || f == 10 || f == 12, TsFormats[f], dtfi)));
        }

        [Builtin(
            "TimestampFormat",
            Doc = "1193770",
            Errors = "274 invalid timestamp or format (returns '')",
            Notes = ".NET DateTime custom / standard format of the host locale; bUtc 0 = local."
        )]
        private static Value TimestampFormat(SimHost h, CicodeTask t, ArgList a)
        {
            if (!DateOfFt(A.U64(a.Get(0)), out DateTime u))
            {
                W.Raise(t, Codes.InvalidArg);
                return W.Empty;
            }
            try
            {
                return W.S(A.Cap(FormatTs(h, u, Opt(a, 2, 0) != 0, A.S(a.Get(1)), h.Locale.DotNet())));
            }
            catch (FormatException)
            {
                W.Raise(t, Codes.InvalidArg);
                return W.Empty;
            }
        }

        [Builtin(
            "StrToTimestamp",
            Doc = "1193764",
            Errors = "274 not a timestamp (returns 0)",
            Notes = "Format 15: the FILETIME number; others: a date / time in the host locale's patterns, local unless UTC (verify); a time alone is on the simulated clock's day, a text with Z or an offset is that instant."
        )]
        private static Value StrToTimestamp(SimHost h, CicodeTask t, ArgList a)
        {
            string s = A.S(a.Get(0)).Trim();
            long f = Opt(a, 1, 0);
            if (f == 15)
            {
                if (ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out ulong v))
                    return Ts(v);
                W.Raise(t, Codes.InvalidArg);
                return Ts(0);
            }
            // never this computer's clock or zone: a time without a date is on the simulated
            // clock's day (.NET would take this computer's today), a 'Z' / '+hh:mm' text is that
            // instant (.NET would convert it to this computer's local time)
            var dtfi = h.Locale.DotNet();
            var styles =
                DateTimeStyles.AllowWhiteSpaces
                | DateTimeStyles.RoundtripKind
                | DateTimeStyles.NoCurrentDateDefault;
            if (DateTime.TryParse(s, dtfi, styles, out DateTime d))
            {
                bool utc = Opt(a, 2, 0) != 0;
                DateTime u;
                if (d.Kind == DateTimeKind.Utc)
                    u = d;
                else if (
                    d.Kind == DateTimeKind.Local
                    && DateTimeOffset.TryParse(s, dtfi, DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset o)
                )
                    u = o.UtcDateTime;
                else
                {
                    d = DateTime.SpecifyKind(d, DateTimeKind.Unspecified);
                    if (d.Date == DateTime.MinValue.Date)
                    {
                        DateOfFt(t.Vm.Clock.NowFiletime(), out DateTime now);
                        d = View(h, now, utc).Date + d.TimeOfDay;
                    }
                    if (utc)
                        u = DateTime.SpecifyKind(d, DateTimeKind.Utc);
                    else
                    {
                        if (h.TimeZone.IsInvalidTime(d))
                            d = d.AddHours(1);
                        u = TimeZoneInfo.ConvertTimeToUtc(d, h.TimeZone);
                    }
                }
                return Ts(FtOf(u));
            }
            W.Raise(t, Codes.InvalidArg);
            return Ts(0);
        }
    }
}
