using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CicodeSim.Project;
using CicodeSim.Values;

namespace CicodeSim.World
{
    /// <summary>The World's tag change log, kept for trends.</summary>
    public sealed class TagHistory
    {
        private static readonly ConditionalWeakTable<World, TagHistory> All =
            new ConditionalWeakTable<World, TagHistory>();

        public const int MaxPerTag = 65536;

        /// <summary>Changes kept over all tags (about 50 MB): past it every tag keeps its newer half.</summary>
        public const int MaxTotal = 2_000_000;
        private int _total;

        private readonly World _world;

        /// <summary>Simulated time of the attach.</summary>
        public double AttachT { get; }
        private readonly Dictionary<string, List<(double T, double V, ulong Q)>> _log = new Dictionary<
            string,
            List<(double, double, ulong)>
        >(StringComparer.Ordinal);

        /// <summary>Tags that had changed before they were first watched: their value then and since when.</summary>
        private readonly Dictionary<string, (double Since, double V, ulong Q)> _before = new Dictionary<
            string,
            (double, double, ulong)
        >(StringComparer.Ordinal);

        /// <summary>Tags whose oldest changes were dropped: the time of the oldest kept one.</summary>
        private readonly Dictionary<string, double> _cut = new Dictionary<string, double>(
            StringComparer.Ordinal
        );

        /// <summary>The names the trend expressions read (any case, an Equip.Item as written),
        /// read at the attach.</summary>
        private readonly NameHashes _trendTags;

        /// <summary>Per changed tag not named as such in a trend expression (lower case): does
        /// an expression read it by its Equip.Item?</summary>
        private readonly Dictionary<string, bool> _isTrendTag = new Dictionary<string, bool>(
            StringComparer.Ordinal
        );

        /// <summary>Other watched tags (lower case): the ones a trend object or a Trn* call named.</summary>
        private readonly HashSet<string> _watched = new HashSet<string>(StringComparer.Ordinal);

        private TagHistory(World w)
        {
            _world = w;
            AttachT = w.Clock.Now();
            _trendTags = TrendTags.Of(w).ExpressionTags();
            foreach (string name in w.Tags.ChangesSince(-1))
            {
                string k = Key(name);
                if (IsTrendTag(k, name))
                    Before(k, name);
            }
            w.Tags.Subscribe(OnChange);
        }

        /// <summary>The log of <paramref name="w"/>, made (and subscribed) on the first call.</summary>
        public static TagHistory Attach(World w) => All.GetValue(w, x => new TagHistory(x));

        /// <summary>The log of <paramref name="w"/> when one is attached.</summary>
        public static TagHistory? Of(World w) => All.TryGetValue(w, out var h) ? h : null;

        private static string Key(string name) => CitectText.Lower(name);

        /// <summary>The tags whose changes are kept (trend expression tags and watched ones).</summary>
        public int WatchedCount => _trendTags.Count + _watched.Count;

        /// <summary>The changes kept now, over all tags.</summary>
        public int Entries => _total;

        /// <summary>The tags with a change kept.</summary>
        public int LoggedTags => _log.Count;

        public bool IsWatched(string name)
        {
            string k = Key(name);
            return IsTrendTag(k, name) || _watched.Contains(k);
        }

        /// <summary>A trend expression reads the tag: by its name, or by its Equip.Item.</summary>
        private bool IsTrendTag(string k, string name)
        {
            if (_trendTags.Count == 0)
                return false;
            if (_trendTags.Contains(name))
                return true;
            if (_isTrendTag.TryGetValue(k, out bool yes))
                return yes;
            var d = _world.Tags.Find(name);
            yes =
                d != null
                && (
                    _trendTags.Contains(d.Name)
                    || (
                        d.Equip.Length > 0 && d.Item.Length > 0 && _trendTags.Contains(d.Equip + "." + d.Item)
                    )
                );
            _isTrendTag[k] = yes;
            return yes;
        }

        /// <summary>Keep the changes of <paramref name="name"/> (a variable tag) from now on; its
        /// value since its last change is known from then (a tag that never changed: since the
        /// start).</summary>
        public void Watch(string name)
        {
            string k = Key(name);
            if (k.Length == 0 || IsTrendTag(k, name) || !_watched.Add(k))
                return;
            Before(k, name);
        }

        private void Before(string k, string name)
        {
            Value v = _world.Tags.Get(name);
            if (v.Vts == 0)
                return; // never written: the initial value holds from 0
            double since = _world.Clock.TOfFiletime(v.Vts);
            _before[k] = (since, Number(v), v.Q);
        }

        internal static double Number(in Value v0)
        {
            Value v = v0.T == Types.Variant ? Value.Deref(v0) : v0;
            if (Types.IsRealType(v.T))
                return v.D;
            if (v.T == Types.String)
                return FloatText.TryParseNumber(v.S ?? "", out double d) ? d : 0.0;
            return v.PayloadIsInt ? (int)v.I : 0.0;
        }

        private void OnChange(TagChange ch)
        {
            if (ch.Index != null || !(ch.Element.Length == 0 || ch.Element == "field"))
                return;
            string k = Key(ch.Name);
            if (!_watched.Contains(k) && !IsTrendTag(k, ch.Name))
                return;
            if (!_log.TryGetValue(k, out var l))
                _log[k] = l = new List<(double, double, ulong)>();
            double v =
                ch.Value.Kind == JsonKind.String
                    ? (FloatText.TryParseNumber(ch.Value.Str, out double d) ? d : 0.0)
                    : ch.Value.Double;
            l.Add((_world.Clock.Now(), v, ch.Quality));
            _total++;
            if (l.Count > MaxPerTag)
                Halve(k, l);
            if (_total > MaxTotal)
                foreach (var kv in new List<KeyValuePair<string, List<(double, double, ulong)>>>(_log))
                    if (kv.Value.Count > 1)
                        Halve(kv.Key, kv.Value);
        }

        private void Halve(string k, List<(double T, double V, ulong Q)> l)
        {
            int n = l.Count / 2;
            l.RemoveRange(0, n);
            _total -= n;
            _cut[k] = l[0].T;
        }

        /// <summary>The oldest value the history knows of the tag (TrendTags.SeedHistory: what a
        /// seeded trend shows before it): the value since its last write before it was watched,
        /// else its first logged change (after a cut: the oldest change kept); false when none.</summary>
        public bool TryEarliest(string name, out double value, out ulong quality)
        {
            string k = Key(name);
            value = 0;
            quality = 0;
            bool has = _log.TryGetValue(k, out var l) && l.Count > 0;
            if (!_cut.ContainsKey(k) && _before.TryGetValue(k, out var b))
            {
                value = b.V;
                quality = b.Q;
                return true;
            }
            if (!has)
                return false;
            value = l![0].V;
            quality = l[0].Q;
            return true;
        }

        /// <summary>The tag's value at simulated time <paramref name="t"/> (the last change at or
        /// before it); false when it is not known. A tag not watched yet is watched from now on
        /// (its value since its last change is then known).</summary>
        public bool TryValueAt(string name, double t, out double value, out ulong quality)
        {
            string k = Key(name);
            value = 0;
            quality = Quality.QualityGood;
            if (t < 0)
                return false;
            if (!_watched.Contains(k) && !IsTrendTag(k, name))
                Watch(name);
            if (_log.TryGetValue(k, out var l) && l.Count > 0)
            {
                // the last change at or before t
                int lo = 0,
                    hi = l.Count - 1,
                    at = -1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (l[mid].T <= t)
                    {
                        at = mid;
                        lo = mid + 1;
                    }
                    else
                        hi = mid - 1;
                }
                if (at >= 0)
                {
                    value = l[at].V;
                    quality = l[at].Q;
                    return true;
                }
                if (_cut.ContainsKey(k))
                    return false;
            }
            if (_before.TryGetValue(k, out var b))
            {
                if (t < b.Since)
                    return false;
                value = b.V;
                quality = b.Q;
                return true;
            }
            // no change before it was watched: the initial value, or the current one when it never changed
            if (l == null || l.Count == 0)
            {
                var (pv, pq, _) = _world.Tags.Peek(name);
                value =
                    pv.Kind == JsonKind.String
                        ? (FloatText.TryParseNumber(pv.Str, out double pd) ? pd : 0.0)
                        : pv.Double;
                quality = pq;
                return true;
            }
            value = 0;
            quality = _world.Tags.InitialQuality;
            return true;
        }
    }
}
