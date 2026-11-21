using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.World
{
    /// <summary>One scheduled call. Due times are whole simulated milliseconds; a repeating
    /// timer's call k is due at base + k * period (computed, not summed: a 0.1 s timer neither
    /// drifts nor lands a hair after a scenario step at 0.3 s).</summary>
    public sealed class Timer
    {
        internal long Due;
        internal long Seq;
        internal readonly Action Fn;
        internal readonly long Period;
        internal long Base;
        internal long K;
        public bool Cancelled { get; private set; }
        public string Label { get; }

        internal Timer(long due, long seq, Action fn, long period, string label)
        {
            Due = due;
            Seq = seq;
            Fn = fn;
            Period = period;
            Label = label;
            Base = due;
        }

        /// <summary>Simulated seconds of the next call.</summary>
        public double DueAt => Due / 1000.0;

        /// <summary>Simulated milliseconds of the next call.</summary>
        public long DueMs => Due;

        public void Cancel() => Cancelled = true;

        internal bool Before(Timer o) => Due < o.Due || (Due == o.Due && Seq < o.Seq);

        public override string ToString() =>
            $"Timer({Label} at {DueAt.ToString("F3", CultureInfo.InvariantCulture)})";
    }

    /// <summary>The simulated clock and the scheduler's timers. Time counts whole milliseconds.</summary>
    public sealed class SimClock : IClock, IClockReader
    {
        public const string Run = "run",
            Paused = "paused",
            Stepped = "stepped";
        public static readonly string[] Modes = { Run, Paused, Stepped };

        /// <summary>100 ns intervals between 1601-01-01 and 1970-01-01.</summary>
        public const long FiletimeUnixOffset = 116444736000000000L;

        /// <summary>MonotonicMs at t = 0, so the values look like a running machine's.</summary>
        public const double MonotonicOriginMs = 1_000_000.0;

        /// <summary>The largest simulated time in ms (about 14,600 years: no FILETIME or
        /// monotonic computation of the clock overflows).</summary>
        public const long MaxMs = long.MaxValue / 20_000;

        /// <summary>The last FILETIME the clock takes: DateTime's last tick, 9999-12-31T23:59:59.9999999Z.</summary>
        public const long MaxFiletime = 2650467743999999999L;

        /// <summary>The FILETIME of Unix seconds.</summary>
        public static long FiletimeFromUnix(double seconds)
        {
            double x = seconds * 10_000_000;
            if (double.IsNaN(x) || double.IsInfinity(x))
                throw new FormatException("not a start time: " + FloatText.Shortest(seconds));
            long ft =
                x >= -FiletimeUnixOffset && x <= (double)(MaxFiletime - FiletimeUnixOffset)
                    ? (long)Math.Round(x) + FiletimeUnixOffset
                    : -1;
            if (ft < 0 || ft > MaxFiletime)
                throw new FormatException(
                    "not a start time: "
                        + FloatText.Shortest(seconds)
                        + " (the clock runs from 1601-01-01 to 9999-12-31 UTC)"
                );
            return ft;
        }

        public static double UnixFromFiletime(long ft) =>
            FloatText.Quotient(ft - FiletimeUnixOffset, 10_000_000);

        /// <summary>Seconds as whole milliseconds: rounded half away from zero, clamped to
        /// +-<see cref="MaxMs"/>; NaN is 0 (a NaN delay fires at the next move, a NaN time is
        /// never a time the clock takes).</summary>
        public static long ToMs(double seconds)
        {
            if (double.IsNaN(seconds))
                return 0;
            double x = Math.Round(seconds * 1000.0, MidpointRounding.AwayFromZero);
            if (x >= MaxMs)
                return MaxMs;
            if (x <= -MaxMs)
                return -MaxMs;
            return (long)x;
        }

        public double StartUnix { get; private set; }
        public long StartFiletime { get; private set; }
        private long _ms;
        private readonly List<Timer> _heap = new List<Timer>();
        private long _seq;
        private readonly Func<double> _wall;
        private double _speed = 1.0;

        // a simulator-only factor on the speed (RuntimeStartup's fast start-up): not the
        // Speed a client set or reads
        private double _boost = 1.0;
        private string _mode = Paused;
        private double _wallBase;
        private long _msBase;
        private long _stepMs = 250;
        public long Fired { get; private set; }

        /// <summary>The step of <see cref="Step"/> in seconds (stored as whole milliseconds, at
        /// least 1 ms).</summary>
        public double StepSize
        {
            get => _stepMs / 1000.0;
            set => _stepMs = Math.Max(1, ToMs(value));
        }

        public long StepMs => _stepMs;

        private static readonly Stopwatch Mono = Stopwatch.StartNew();

        private static double WallMonotonic() => Mono.Elapsed.TotalSeconds;

        /// <summary>start = the simulated wall time at t = 0 (anything ParseStart takes: ISO 8601
        /// text, Unix seconds, null = now); wall = the real monotonic clock run mode follows, in
        /// seconds (tests inject one).</summary>
        public SimClock(
            object? start = null,
            string mode = Paused,
            double speed = 1.0,
            double stepSize = 0.25,
            Func<double>? wall = null
        )
        {
            StartUnix = ParseStart(start);
            StartFiletime = FiletimeFromUnix(StartUnix);
            _wall = wall ?? WallMonotonic;
            _wallBase = _wall();
            StepSize = stepSize;
            SetSpeed(speed);
            SetMode(mode);
        }

        /// <summary>A start time as Unix seconds. ISO 8601 text with 'Z' or an offset is that
        /// instant; naive text or a naive DateTime is LOCAL time; a number is Unix seconds;
        /// null = now. FormatException otherwise.</summary>
        public static double ParseStart(object? value)
        {
            switch (value)
            {
                case null:
                    return UtcTicksToUnix(DateTime.UtcNow.Ticks);
                case bool _:
                    throw new FormatException($"not a start time: {value}");
                case int i:
                    return i;
                case long l:
                    return l;
                case double d:
                    return d;
                case float f:
                    return f;
                case JsonValue j:
                    if (j.Kind == JsonKind.Int || j.Kind == JsonKind.Float)
                        return j.Double;
                    if (j.Kind == JsonKind.String)
                        return ParseIso(j.Str);
                    throw new FormatException($"not a start time: {j.Dump()}");
                case string s:
                    return ParseIso(s);
                case DateTime dt:
                    if (dt.Kind == DateTimeKind.Utc)
                        return UtcTicksToUnix(dt.Ticks);
                    return LocalToUnix(dt);
                case DateTimeOffset dto:
                    return UtcTicksToUnix(dto.UtcTicks);
                default:
                    throw new FormatException($"not a start time: {value}");
            }
        }

        /// <summary>Unix seconds of a UTC time: whole microseconds since 1970 / 1e6.</summary>
        private static double UtcTicksToUnix(long utcTicks)
        {
            long micro = (utcTicks - new DateTime(1970, 1, 1).Ticks) / 10;
            return FloatText.Quotient(micro, 1_000_000);
        }

        private static double LocalToUnix(DateTime local)
        {
            // the local wall time as the C runtime reads it, plus the microseconds
            long micro = (local.Ticks % TimeSpan.TicksPerSecond) / 10;
            try
            {
                return Crt.Mktime64(
                        local.Year,
                        local.Month,
                        local.Day,
                        local.Hour,
                        local.Minute,
                        local.Second
                    )
                    + micro / 1e6;
            }
            catch (OverflowException e)
            {
                throw new FormatException(e.Message, e);
            }
        }

        /// <summary>Unix seconds of an ISO 8601 date and time (<see cref="IsoDateTime"/>).</summary>
        private static double ParseIso(string text)
        {
            var iso = IsoDateTime.Parse(text);
            try
            {
                return iso.ToUnix();
            }
            catch (OverflowException e)
            {
                // a local time the C runtime's _mktime64 refuses (before 1970-01-01 UTC, after
                // the year 3000): no start time, like any other bad text (scenarios: bad-start)
                throw new FormatException(e.Message, e);
            }
        }

        // ------------------------------------------------------------------ time
        /// <summary>Simulated seconds since the start (whole milliseconds / 1000).</summary>
        public double Now() => _ms / 1000.0;

        /// <summary>Simulated milliseconds since the start.</summary>
        public long NowMs => _ms;

        public double Unix() => StartUnix + Now();

        public ulong NowFiletime() => (ulong)(StartFiletime + _ms * 10_000);

        /// <summary>The FILETIME of simulated time t (seconds, rounded to 100 ns).</summary>
        public ulong FiletimeAt(double t) => (ulong)(StartFiletime + (long)Math.Round(t * 10_000_000));

        /// <summary>Move the simulated wall time of t = 0 (a scenario's clock.start); only
        /// before time has moved.</summary>
        public void SetStart(object? start)
        {
            if (_ms != 0 || Fired != 0)
                throw new InvalidOperationException("the start can only be set before the clock has run");
            double unix = ParseStart(start);
            long ft = FiletimeFromUnix(unix); // throws before the start changes
            StartUnix = unix;
            StartFiletime = ft;
        }

        /// <summary>Set the wall clock while preserving elapsed time and pending timers.</summary>
        internal bool SetUnixTime(long seconds)
        {
            long origin = FiletimeFromUnix(seconds) - _ms * 10_000;
            if (origin < 0)
                return false;
            StartFiletime = origin;
            StartUnix = UnixFromFiletime(origin);
            return true;
        }

        public double MonotonicMs() => MonotonicOriginMs + _ms;

        /// <summary>IClockReader: NowFiletime() then MonotonicMs() in one call (the VM's slice check).</summary>
        public void Read(out ulong filetime, out double monotonicMs)
        {
            filetime = NowFiletime();
            monotonicMs = MonotonicMs();
        }

        /// <summary>Whole seconds since 1970 (_time32), what Sleep() wakes on.</summary>
        public long Time32() => (long)Math.Truncate(Unix());

        /// <summary>The simulated local time.</summary>
        public DateTime LocalTime() =>
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddTicks((long)Math.Round(Unix() * 1e7))
                .ToLocalTime();

        public double TOfFiletime(ulong ft) => UnixFromFiletime((long)ft) - StartUnix;

        // ------------------------------------------------------------------ modes
        public string Mode => _mode;
        public double Speed => _speed;

        /// <summary>The start-up boost: run mode moves this many times faster than Speed
        /// (RuntimeStartup: the project's startup code runs at it until it settles); 1 = none.</summary>
        public double Boost => _boost;

        public void SetBoost(double factor)
        {
            if (!(factor > 0) || double.IsInfinity(factor))
                factor = 1.0;
            Rebase();
            _boost = factor;
        }

        public void SetMode(string mode)
        {
            if (Array.IndexOf(Modes, mode) < 0)
                throw new ArgumentException(
                    $"clock mode must be one of ('run', 'paused', 'stepped'): {CitectText.Quote(mode)}"
                );
            Rebase();
            _mode = mode;
        }

        public void RunMode() => SetMode(Run);

        public void Pause() => SetMode(Paused);

        public void SetSpeed(double speed)
        {
            if (!(speed > 0) || double.IsInfinity(speed))
                throw new ArgumentException($"clock speed must be > 0: {FloatText.Shortest(speed)}");
            Rebase();
            _speed = speed;
        }

        private void Rebase()
        {
            _wallBase = _wall();
            _msBase = _ms;
        }

        /// <summary>Where run mode wants the simulated time now (seconds).</summary>
        public double WallTarget() => WallTargetMs() / 1000.0;

        /// <summary>Where run mode wants the simulated time now: whole milliseconds, rounded
        /// down (the clock never runs ahead of the wall).</summary>
        public long WallTargetMs()
        {
            if (_mode != Run)
                return _ms;
            double x = Math.Floor((_wall() - _wallBase) * _speed * _boost * 1000.0 + 1e-6);
            if (!(x > 0))
                return _msBase;
            if (x >= MaxMs - _msBase)
                return MaxMs;
            return _msBase + (long)x;
        }

        // ------------------------------------------------------------------ moving time
        /// <summary>Run mode: move to the wall-derived time, firing due timers; the number
        /// fired (0 in the other modes).</summary>
        public int Poll()
        {
            if (_mode != Run)
                return 0;
            long target = WallTargetMs();
            return target > _ms ? AdvanceToMs(target, rebase: false) : 0;
        }

        public int Advance(double seconds)
        {
            if (seconds < 0)
                throw new ArgumentException("time cannot go backwards");
            return AdvanceMs(ToMs(seconds));
        }

        /// <summary>Advance by whole milliseconds.</summary>
        public int AdvanceMs(long ms)
        {
            if (ms < 0)
                throw new ArgumentException("time cannot go backwards");
            return AdvanceToMs(Plus(_ms, ms));
        }

        public int Step(int count = 1) => AdvanceMs(_stepMs * Math.Max(0, count));

        /// <summary>Move simulated time to t seconds (&gt;= now), firing every timer due on the way.</summary>
        public int AdvanceTo(double t, bool rebase = true)
        {
            long target = ToMs(t);
            if (target < _ms)
            {
                if (t < Now())
                    throw new ArgumentException("time cannot go backwards");
                target = _ms;
            }
            return AdvanceToMs(target, rebase);
        }

        /// <summary>Move simulated time to <paramref name="target"/> ms (&gt;= now), firing
        /// every timer due on the way.</summary>
        public int AdvanceToMs(long target, bool rebase = true)
        {
            if (target < _ms)
                throw new ArgumentException("time cannot go backwards");
            int fired = 0;
            while (_heap.Count > 0)
            {
                Timer top = _heap[0];
                if (top.Cancelled)
                {
                    Pop();
                    continue;
                }
                if (top.Due > target)
                    break;
                Pop();
                if (top.Due > _ms)
                    _ms = top.Due;
                if (top.Period > 0)
                {
                    top.K++;
                    top.Due = Plus(top.Base, top.K * top.Period);
                    _seq++;
                    top.Seq = _seq;
                    Push(top);
                }
                fired++;
                Fired++;
                top.Fn();
            }
            // a timer that advanced the clock itself may have moved it past target: never back
            if (target > _ms)
                _ms = target;
            if (rebase)
                Rebase();
            return fired;
        }

        // ------------------------------------------------------------------ timers
        /// <summary>Call fn when simulated time reaches t seconds (a past t = at the next advance).</summary>
        public Timer CallAt(double t, Action fn, string label = "") => CallAtMs(ToMs(t), fn, label);

        /// <summary>Call fn when simulated time reaches <paramref name="ms"/> milliseconds.</summary>
        public Timer CallAtMs(long ms, Action fn, string label = "")
        {
            _seq++;
            var tm = new Timer(Math.Max(ms, _ms), _seq, fn, 0, label);
            Push(tm);
            return tm;
        }

        public Timer CallLater(double delay, Action fn, string label = "") =>
            CallAtMs(Plus(_ms, Math.Max(0, ToMs(delay))), fn, label);

        private static long Plus(long a, long b) => b >= MaxMs - a ? MaxMs : a + b;

        /// <summary>Call fn every period seconds (at least 1 ms), first at
        /// <paramref name="first"/> (default now + period).</summary>
        public Timer Every(double period, Action fn, double? first = null, string label = "")
        {
            if (!(period > 0))
                throw new ArgumentException("period must be > 0");
            long p = Math.Max(1, ToMs(period));
            _seq++;
            Timer tm;
            if (first == null)
            {
                tm = new Timer(Plus(_ms, p), _seq, fn, p, label);
                tm.Base = _ms;
                tm.K = 1; // call k at now + k * period
            }
            else
            {
                tm = new Timer(Math.Max(ToMs(first.Value), _ms), _seq, fn, p, label);
            }
            Push(tm);
            return tm;
        }

        public static void Cancel(Timer? timer) => timer?.Cancel();

        /// <summary>Simulated time (seconds) of the next live timer, null when there is none.</summary>
        public double? NextDue()
        {
            long? ms = NextDueMs();
            return ms == null ? (double?)null : ms.Value / 1000.0;
        }

        /// <summary>Simulated milliseconds of the next live timer, null when there is none.</summary>
        public long? NextDueMs()
        {
            while (_heap.Count > 0 && _heap[0].Cancelled)
                Pop();
            return _heap.Count > 0 ? _heap[0].Due : (long?)null;
        }

        /// <summary>Run mode: real seconds until the next timer is due.</summary>
        public double? WallSecondsUntilNext()
        {
            long? nd = NextDueMs();
            if (nd == null || _mode != Run)
                return null;
            return WallSecondsUntil(nd.Value);
        }

        /// <summary>Run mode: real seconds until simulated time reaches <paramref name="ms"/>
        /// (0 when it is there; null in the other modes).</summary>
        public double? WallSecondsUntil(long ms)
        {
            if (_mode != Run)
                return null;
            double wallNowMs = _msBase + (_wall() - _wallBase) * _speed * _boost * 1000.0;
            return Math.Max(0.0, (ms - wallNowMs) / 1000.0 / (_speed * _boost));
        }

        public int Pending()
        {
            int n = 0;
            foreach (var tm in _heap)
                if (!tm.Cancelled)
                    n++;
            return n;
        }

        // ------------------------------------------------------------------ the heap
        private void Push(Timer tm)
        {
            _heap.Add(tm);
            int i = _heap.Count - 1;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (!_heap[i].Before(_heap[p]))
                    break;
                (_heap[i], _heap[p]) = (_heap[p], _heap[i]);
                i = p;
            }
        }

        private Timer Pop()
        {
            Timer top = _heap[0];
            int last = _heap.Count - 1;
            _heap[0] = _heap[last];
            _heap.RemoveAt(last);
            int i = 0,
                n = _heap.Count;
            while (true)
            {
                int l = 2 * i + 1,
                    r = l + 1,
                    m = i;
                if (l < n && _heap[l].Before(_heap[m]))
                    m = l;
                if (r < n && _heap[r].Before(_heap[m]))
                    m = r;
                if (m == i)
                    break;
                (_heap[i], _heap[m]) = (_heap[m], _heap[i]);
                i = m;
            }
            return top;
        }
    }
}
