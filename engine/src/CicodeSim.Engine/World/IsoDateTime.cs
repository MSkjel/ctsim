using System;
using System.Globalization;
using CicodeSim.Values;

namespace CicodeSim.World
{
    /// <summary>An ISO 8601 date and time, as a scenario's clock.start writes it.</summary>
    public readonly struct IsoDateTime
    {
        public readonly int Year,
            Month,
            Day,
            Hour,
            Minute,
            Second,
            Microsecond;

        /// <summary>The UTC offset in seconds, null for a local time (no 'Z', no offset).</summary>
        public readonly int? OffsetSeconds;

        public IsoDateTime(int y, int mo, int d, int h, int mi, int s, int us, int? off)
        {
            Year = y;
            Month = mo;
            Day = d;
            Hour = h;
            Minute = mi;
            Second = s;
            Microsecond = us;
            OffsetSeconds = off;
        }

        public override string ToString() =>
            string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} {2} {3} {4} {5} {6} {7}",
                Year,
                Month,
                Day,
                Hour,
                Minute,
                Second,
                Microsecond,
                OffsetSeconds == null ? "local" : OffsetSeconds.Value.ToString(CultureInfo.InvariantCulture)
            );

        private static FormatException Bad(string text) =>
            new FormatException("not an ISO 8601 date and time: " + CicodeSim.Project.CitectText.Quote(text));

        /// <summary>The fields of an ISO 8601 date and time; FormatException when the text is
        /// not one or names a day or time that does not exist.</summary>
        public static IsoDateTime Parse(string text)
        {
            string s = CicodeSim.Project.CitectText.Trim(text);
            int p = 0,
                n = s.Length;
            if (!Digits(s, ref p, 4, out int year))
                throw Bad(text);
            bool ext = p < n && s[p] == '-';
            if (ext)
                p++;
            int month,
                day;
            if (p < n && s[p] == 'W')
            {
                p++;
                if (!Digits(s, ref p, 2, out int week))
                    throw Bad(text);
                int wday = 1;
                if (ext ? p + 1 < n && s[p] == '-' && IsDigit(s[p + 1]) : p < n && IsDigit(s[p]))
                {
                    if (ext)
                        p++;
                    wday = s[p++] - '0';
                }
                if (year < 1 || wday < 1 || wday > 7 || week < 1 || week > WeeksIn(year))
                    throw Bad(text);
                FromWeekDate(year, week, wday, out year, out month, out day);
            }
            else
            {
                if (!Digits(s, ref p, 2, out month))
                    throw Bad(text);
                if (ext)
                {
                    if (p >= n || s[p] != '-')
                        throw Bad(text);
                    p++;
                }
                if (!Digits(s, ref p, 2, out day))
                    throw Bad(text);
                if (year < 1 || month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
                    throw Bad(text);
            }
            int hour = 0,
                minute = 0,
                second = 0,
                micro = 0;
            int? offset = null;
            if (p < n)
            {
                if (s[p] != 'T' && s[p] != 't' && s[p] != ' ')
                    throw Bad(text);
                p++;
                if (!Digits(s, ref p, 2, out hour))
                    throw Bad(text);
                bool colon = p < n && s[p] == ':';
                if (colon ? p + 1 < n && IsDigit(s[p + 1]) : p < n && IsDigit(s[p]))
                {
                    if (colon)
                        p++;
                    if (!Digits(s, ref p, 2, out minute))
                        throw Bad(text);
                    if (colon ? p + 1 < n && s[p] == ':' && IsDigit(s[p + 1]) : p < n && IsDigit(s[p]))
                    {
                        if (colon)
                            p++;
                        if (!Digits(s, ref p, 2, out second))
                            throw Bad(text);
                        if (p < n && (s[p] == '.' || s[p] == ','))
                        {
                            p++;
                            int start = p,
                                scale = 100000;
                            while (p < n && IsDigit(s[p]))
                            {
                                if (scale > 0)
                                    micro += (s[p] - '0') * scale;
                                scale /= 10;
                                p++;
                            }
                            if (p == start)
                                throw Bad(text);
                        }
                    }
                }
                if (p < n)
                {
                    if (s[p] == 'Z' || s[p] == 'z')
                    {
                        p++;
                        offset = 0;
                    }
                    else if (s[p] == '+' || s[p] == '-')
                    {
                        int sign = s[p] == '-' ? -1 : 1;
                        p++;
                        if (!Digits(s, ref p, 2, out int oh))
                            throw Bad(text);
                        int om = 0;
                        if (p < n && s[p] == ':')
                        {
                            p++;
                            if (!Digits(s, ref p, 2, out om))
                                throw Bad(text);
                        }
                        else if (p < n && !Digits(s, ref p, 2, out om))
                            throw Bad(text);
                        if (oh > 23 || om > 59)
                            throw Bad(text);
                        offset = sign * (oh * 3600 + om * 60);
                    }
                }
                if (p != n)
                    throw Bad(text);
                if (hour == 24 && minute == 0 && second == 0 && micro == 0)
                {
                    var next = new DateTime(year, month, day).AddDays(1);
                    if (next.Year > 9999)
                        throw Bad(text);
                    year = next.Year;
                    month = next.Month;
                    day = next.Day;
                    hour = 0;
                }
                if (hour > 23 || minute > 59 || second > 59)
                    throw Bad(text);
            }
            return new IsoDateTime(year, month, day, hour, minute, second, micro, offset);
        }

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        private static bool Digits(string s, ref int p, int count, out int value)
        {
            value = 0;
            if (p + count > s.Length)
                return false;
            for (int k = 0; k < count; k++)
            {
                char c = s[p + k];
                if (!IsDigit(c))
                    return false;
                value = value * 10 + (c - '0');
            }
            p += count;
            return true;
        }

        /// <summary>52 or 53: the ISO weeks of a year (53 when it starts on a Thursday, or on a
        /// Wednesday in a leap year).</summary>
        private static int WeeksIn(int year)
        {
            var jan1 = new DateTime(year, 1, 1).DayOfWeek;
            return jan1 == DayOfWeek.Thursday || (jan1 == DayOfWeek.Wednesday && DateTime.IsLeapYear(year))
                ? 53
                : 52;
        }

        private static void FromWeekDate(int year, int week, int wday, out int y, out int m, out int d)
        {
            // week 1 holds the year's first Thursday; its Monday is the start
            var jan4 = new DateTime(year, 1, 4);
            int iso = ((int)jan4.DayOfWeek + 6) % 7; // Monday = 0
            DateTime day1;
            try
            {
                day1 = jan4.AddDays(-iso + (week - 1) * 7 + (wday - 1));
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new FormatException("week date out of range");
            }
            y = day1.Year;
            m = day1.Month;
            d = day1.Day;
        }

        /// <summary>Unix seconds of the instant: an offset time exactly (whole microseconds
        /// over 1e6), a local time through the C runtime's _mktime64 (<see cref="Crt.Mktime64"/>)
        /// plus the microseconds.</summary>
        public double ToUnix()
        {
            if (OffsetSeconds != null)
            {
                long days = (long)(new DateTime(Year, Month, Day) - new DateTime(1970, 1, 1)).TotalDays;
                long micro =
                    ((days * 86400 + Hour * 3600L + Minute * 60L + Second - OffsetSeconds.Value) * 1_000_000)
                    + Microsecond;
                return FloatText.Quotient(micro, 1_000_000);
            }
            return Crt.Mktime64(Year, Month, Day, Hour, Minute, Second) + Microsecond / 1e6;
        }
    }
}
