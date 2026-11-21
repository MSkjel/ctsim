using System;
using System.Collections.Generic;
using CicodeSim.Values;

namespace CicodeSim.World
{
    /// <summary>A raw value of a tag's kind (long, double or text).</summary>
    public struct RawVal
    {
        public long L;
        public double D;
        public string? S;

        public static RawVal Int(long l) => new RawVal { L = l };

        public static RawVal Real(double d) => new RawVal { D = d };

        public static RawVal Str(string s) => new RawVal { S = s };
    }

    /// <summary>One tag element's value + quality + timestamps (T, VT, QT); an array holds all
    /// its elements with one quality and one set of timestamps (doc: 1064764).</summary>
    public sealed class TagElem
    {
        public readonly RawKind Kind;
        internal long L;
        internal double D;
        internal string S = "";
        internal long[]? LA;
        internal double[]? DA;
        internal string[]? SA;
        public ulong Q;
        public ulong T,
            Vt,
            Qt;

        public bool IsArray => LA != null || DA != null || SA != null;
        public int Length => LA?.Length ?? DA?.Length ?? SA?.Length ?? 0;

        internal TagElem(RawKind kind, long size, ulong q, ulong ts)
        {
            Kind = kind;
            Q = q;
            T = ts;
            Vt = ts;
            Qt = ts;
            if (size > 0)
            {
                switch (kind)
                {
                    case RawKind.Int:
                        LA = new long[size];
                        break;
                    case RawKind.Real:
                        DA = new double[size];
                        break;
                    default:
                        SA = new string[size];
                        for (long i = 0; i < size; i++)
                            SA[i] = "";
                        break;
                }
            }
        }

        /// <summary>The scalar value as a raw value.</summary>
        public RawVal Scalar =>
            new RawVal
            {
                L = L,
                D = D,
                S = S,
            };

        public RawVal At(int i)
        {
            switch (Kind)
            {
                case RawKind.Int:
                    return RawVal.Int(LA![i]);
                case RawKind.Real:
                    return RawVal.Real(DA![i]);
                default:
                    return RawVal.Str(SA![i]);
            }
        }

        public long IntValue => L;

        private bool Same(in RawVal v)
        {
            switch (Kind)
            {
                case RawKind.Int:
                    return v.L == L;
                case RawKind.Real:
                    return v.D == D; // NaN != NaN: a NaN write always changes
                default:
                    return string.Equals(v.S, S, StringComparison.Ordinal);
            }
        }

        private void Put(in RawVal v)
        {
            switch (Kind)
            {
                case RawKind.Int:
                    L = v.L;
                    break;
                case RawKind.Real:
                    D = v.D;
                    break;
                default:
                    S = v.S ?? "";
                    break;
            }
        }

        /// <summary>A new value of a scalar element.</summary>
        internal bool Update(in RawVal value, ulong? q, ulong ts)
        {
            bool changed = false;
            if (!Same(value))
            {
                Put(value);
                Vt = ts;
                changed = true;
            }
            if (q != null && q.Value != Q)
            {
                Q = q.Value;
                Qt = ts;
                changed = true;
            }
            T = ts;
            return changed;
        }

        /// <summary>A new quality with the value the element already has.</summary>
        internal bool Touch(ulong? q, ulong ts)
        {
            bool changed = false;
            if (!IsArray && Kind == RawKind.Real && double.IsNaN(D))
            {
                // NaN never equals itself: the value counts as changed
                Vt = ts;
                changed = true;
            }
            if (q != null && q.Value != Q)
            {
                Q = q.Value;
                Qt = ts;
                changed = true;
            }
            T = ts;
            return changed;
        }

        /// <summary>Element i of an array element in place.</summary>
        internal bool UpdateItem(int i, in RawVal value, ulong? q, ulong ts)
        {
            bool changed = false;
            bool same;
            switch (Kind)
            {
                case RawKind.Int:
                    same = LA![i] == value.L;
                    if (!same)
                        LA[i] = value.L;
                    break;
                case RawKind.Real:
                    same = DA![i] == value.D;
                    if (!same)
                        DA[i] = value.D;
                    break;
                default:
                    same = string.Equals(SA![i], value.S, StringComparison.Ordinal);
                    if (!same)
                        SA[i] = value.S ?? "";
                    break;
            }
            if (!same)
            {
                Vt = ts;
                changed = true;
            }
            if (q != null && q.Value != Q)
            {
                Q = q.Value;
                Qt = ts;
                changed = true;
            }
            T = ts;
            return changed;
        }

        /// <summary>Take src's value (an array copied; equal arrays leave VT alone, a NaN
        /// element counting as equal to itself).</summary>
        internal bool CopyFrom(TagElem src, ulong? q, ulong ts)
        {
            bool changed = false;
            if (IsArray)
            {
                bool same = true;
                switch (Kind)
                {
                    case RawKind.Int:
                        if (LA!.Length != src.LA!.Length)
                            same = false;
                        else
                            for (int i = 0; i < LA.Length && same; i++)
                                same = LA[i] == src.LA[i];
                        if (!same)
                            LA = (long[])src.LA.Clone();
                        break;
                    case RawKind.Real:
                        if (DA!.Length != src.DA!.Length)
                            same = false;
                        else
                            for (int i = 0; i < DA.Length && same; i++)
                                same = DA[i] == src.DA[i] || (double.IsNaN(DA[i]) && double.IsNaN(src.DA[i]));
                        if (!same)
                            DA = (double[])src.DA.Clone();
                        break;
                    default:
                        if (SA!.Length != src.SA!.Length)
                            same = false;
                        else
                            for (int i = 0; i < SA.Length && same; i++)
                                same = string.Equals(SA[i], src.SA[i], StringComparison.Ordinal);
                        if (!same)
                            SA = (string[])src.SA.Clone();
                        break;
                }
                if (!same)
                {
                    Vt = ts;
                    changed = true;
                }
            }
            else
            {
                var v = src.Scalar;
                if (!Same(v))
                {
                    Put(v);
                    Vt = ts;
                    changed = true;
                }
            }
            if (q != null && q.Value != Q)
            {
                Q = q.Value;
                Qt = ts;
                changed = true;
            }
            T = ts;
            return changed;
        }
    }

    /// <summary>The runtime state of one tag: its elements (raw values for variable tags, an
    /// array for arrays) and who wrote it last.</summary>
    public sealed class TagState
    {
        public TagDef Defn { get; }
        public TagElem Field,
            Valid,
            Override,
            OverrideMode,
            ControlMode,
            Status;
        public long Serial;
        public string By = "";
        public int Pending;

        /// <summary>Locked by the simulator's user (ctsim/lockTag, a simulator-only feature):
        /// every write but the user's own (by "user") is ignored, the value kept.</summary>
        public bool Locked;

        internal TagState(TagDef defn, ulong ts, ulong q)
        {
            Defn = defn;
            long size = defn.Dims.Length > 0 ? defn.Size : 0;
            Field = new TagElem(defn.RawKind, size, q, ts);
            Valid = new TagElem(defn.RawKind, size, q, ts);
            Override = new TagElem(defn.RawKind, size, Quality.QualityGood, 0);
            OverrideMode = new TagElem(RawKind.Int, 0, Quality.QualityGood, 0);
            ControlMode = new TagElem(RawKind.Int, 0, Quality.QualityGood, 0);
            Status = new TagElem(RawKind.Int, 0, Quality.QualityGood, 0);
        }
    }

    /// <summary>What a listener hears: the serial, the tag, who changed it and its new value
    /// (the engineering value, a list for an array; an array element write: that element and
    /// its index).</summary>
    public sealed class TagChange
    {
        public long Serial { get; }
        public string Name { get; }
        public string Kind { get; }
        public string By { get; }
        public string Element { get; }
        public JsonValue Value { get; }
        public ulong Quality { get; }
        public int? Index { get; }

        public TagChange(
            long serial,
            string name,
            string kind,
            string by,
            string element,
            JsonValue value,
            ulong quality,
            int? index
        )
        {
            Serial = serial;
            Name = name;
            Kind = kind;
            By = by;
            Element = element;
            Value = value;
            Quality = quality;
            Index = index;
        }
    }

    /// <summary>A resolved reference: kind 'variable' / 'local' / 'alarm' / null (not found).</summary>
    public sealed class Resolved
    {
        public string? Kind;
        public TagDef? Tag;
        public AlarmDef? Alarm;
        public string Element = "";
        public string Item = "";
        public string Prop = "";
        public int? Index;
        public string Via = "";
        public string Name = "";

        public bool Found => Kind != null;

        // per-database caches (a Resolved belongs to one TagDb): its state, and whether the
        // 'used' list already has it
        internal TagState? CachedState;
        internal bool Noted;

        internal static readonly Resolved NotFound = new Resolved();
    }
}
