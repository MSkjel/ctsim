using System;
using System.Collections.Generic;
using System.IO;
using CicodeSim.Compiled;
using CicodeSim.Project;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;

namespace CicodeSim.Page
{
    /// <summary>Samples trends whose expression isn't a single tag.</summary>
    public sealed class TrendSampler : IDisposable
    {
        private readonly Animator _a;
        private CompiledPage? _rdb;
        private bool _loaded;

        /// <summary>Lower-case trend name to (its EXPR code, its sample period).</summary>
        private readonly Dictionary<string, (byte[]? code, double period)> _trends = new Dictionary<
            string,
            (byte[]?, double)
        >(StringComparer.Ordinal);
        private readonly Dictionary<string, World.Timer> _timers = new Dictionary<string, World.Timer>(
            StringComparer.Ordinal
        );

        public TrendSampler(Animator a)
        {
            _a = a;
        }

        /// <summary>The trends sampled now.</summary>
        public int Active => _timers.Count;

        private void Load()
        {
            if (_loaded)
                return;
            _loaded = true;
            string path = Path.Combine(_a.World.Unit.RootDir, "_TREND.RDB");
            if (!File.Exists(path))
                return;
            try
            {
                var f = RdbFile.Open(path);
                _rdb = CompiledPage.FromRdb(f);
                if (!f.Sections.TryGetValue("TREND", out var sec))
                    return;
                FieldDesc? fn = sec.Fields.Find(x => x.Name == "NAME"),
                    fe = sec.Fields.Find(x => x.Name == "EXPR"),
                    fp = sec.Fields.Find(x => x.Name == "SAMPLEPER");
                if (fn == null || fe == null)
                    return;
                foreach (var (_, start) in f.RawRecords("TREND"))
                {
                    byte[] b = f.Data;
                    if (start + fn.Offset + 4 > b.Length || start + fe.Offset + 4 > b.Length)
                        continue;
                    string? name = f.Text(BitConverter.ToUInt32(b, start + fn.Offset));
                    if (name == null || name.Length == 0)
                        continue;
                    string k = CitectText.Lower(CitectText.Trim(name));
                    if (_trends.ContainsKey(k))
                        continue;
                    byte[]? code = _rdb.CodeAt(BitConverter.ToUInt32(b, start + fe.Offset));
                    double per =
                        fp != null && fp.Size == 4 && start + fp.Offset + 4 <= b.Length
                            ? BitConverter.ToSingle(b, start + fp.Offset)
                            : 0;
                    _trends[k] = (code, per);
                }
            }
            catch (Exception e) when (e is RdbError || e is IOException)
            {
                _a.LogLine("_TREND.RDB: " + e.Message);
                _rdb = null;
            }
        }

        /// <summary>Sample the trend a pen names from now on, when it is an expression trend
        /// (no single tag) compiled in _TREND.RDB.</summary>
        public void Want(string pen)
        {
            var tt = TrendTags.Of(_a.World);
            // (an SPC series pen samples its SPC tag)
            var r = tt.Find(pen) ?? tt.SpcTrend(pen);
            if (r == null || tt.PenTag(r.Name) != null)
                return;
            string k = CitectText.Lower(r.Name);
            if (_timers.ContainsKey(k))
                return;
            Load();
            if (_rdb == null || !_trends.TryGetValue(k, out var e) || e.code == null)
                return;
            double per = e.period > 0 ? e.period : r.SamplePeriod;
            if (!(per > 0))
                per = 1.0;
            var clock = _a.World.Clock;
            // the first sample now, then on multiples of the period (guess for the server's grid)
            Sample(r.Name, e.code);
            double next = (Math.Floor(clock.Now() / per) + 1) * per;
            string name = r.Name;
            byte[] code = e.code;
            _timers[k] = clock.Every(
                per,
                () => Sample(name, code),
                first: next - clock.Now(),
                label: "trend " + name
            );
        }

        private void Sample(string trend, byte[] code)
        {
            var t = _a.RunForeground(code, _rdb!.Program, _a.Display.DefaultWindow, -1, "trend.sample");
            double v = Trends_Invalid;
            ulong q = 0;
            if (t.State == TaskState.Stop && t.Result != null)
            {
                Value r = Conv.Convert(t.Result.Value, Types.Real, NoErr.Instance);
                v =
                    r.T == Types.Real ? r.D
                    : r.PayloadIsInt ? r.I
                    : 0.0;
                q = t.Result.Value.Q;
            }
            _a.StatsOf("trend_sample").Evaluations++;
            TrendTags.Of(_a.World).RecordSample(trend, _a.World.Clock.Now(), v, q);
        }

        private static readonly double Trends_Invalid = CicodeSim.Builtins.Trends.Invalid;

        public void Dispose()
        {
            foreach (var tm in _timers.Values)
                tm.Cancel();
            _timers.Clear();
        }
    }
}
