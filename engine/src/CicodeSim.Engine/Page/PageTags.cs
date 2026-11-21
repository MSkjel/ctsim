using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;
using CicodeSim.World.Display;
using Quality = CicodeSim.World.Quality;
using TagRef = CicodeSim.Compiled.TagRef;

namespace CicodeSim.Page
{
    /// <summary>The VM's tag access while pages run.</summary>
    public sealed class PageTags : ITagAccess, ITagElementAccess, ITagScale, IAssociations, IAssociationFormat
    {
        public readonly TagDb Db;
        public readonly DisplayState Display;

        /// <summary>323 Super Genie not associated.</summary>
        public const int NotAssociated = 323;

        private sealed class RefInfo
        {
            public bool Assoc;
            public bool Percent;
        }

        private readonly ConditionalWeakTable<TagRef, RefInfo> _info =
            new ConditionalWeakTable<TagRef, RefInfo>();
        private readonly Dictionary<(int, TagRef), (TagRef? Ref, Value? Literal, int Error)> _resolved =
            new Dictionary<(int, TagRef), (TagRef?, Value?, int)>();
        private int _serial = -1;

        /// <summary>Bumped when a window's associations change: the resolution cache restarts.</summary>
        public int Serial;

        public PageTags(TagDb db, DisplayState display)
        {
            Db = db;
            Display = display;
        }

        private RefInfo Info(TagRef r) =>
            _info.GetValue(r, x => new RefInfo
            {
                Assoc =
                    x.Tag.IndexOf('?') >= 0
                    || x.Element.IndexOf('?') >= 0
                    || x.Item.IndexOf('?') >= 0
                    || x.Cluster.IndexOf('?') >= 0,
                Percent = x.Percentage != 0,
            });

        private static readonly Regex Token = new Regex(@"\?[^?]*\?", RegexOptions.CultureInvariant);
        private static readonly Regex Index = new Regex(
            @"^(.*?)\[\s*(-?\d+)\s*\]$",
            RegexOptions.CultureInvariant
        );

        /// <summary>A reference with its association tokens substituted in window
        /// <paramref name="window"/>: (the resolved TAGREF, or a literal's value, or the error).</summary>
        public (TagRef? Ref, Value? Literal, int Error) Resolve(TagRef r, int window)
        {
            if (_serial != Serial)
            {
                _resolved.Clear();
                _serial = Serial;
            }
            var key = (window, r);
            if (_resolved.TryGetValue(key, out var hit))
                return hit;
            var w = Display.OpenWindow(window);
            (TagRef?, Value?, int) res;
            if (w == null)
                res = (null, null, NotAssociated);
            else
                res = Substitute(r, w);
            if (_resolved.Count > 100_000)
                _resolved.Clear();
            _resolved[key] = res;
            return res;
        }

        private (TagRef?, Value?, int) Substitute(TagRef r, Window w)
        {
            // a reference that is exactly one literal association is its constant
            var whole = Token.Match(r.Tag);
            if (whole.Success && whole.Length == r.Tag.Length && r.Element.Length == 0 && r.Item.Length == 0)
            {
                var e0 = DisplayState.Find(w, whole.Value);
                if (e0 != null && e0.Literal)
                {
                    string lit = e0.Tag;
                    Value lv = FloatText.TryParseNumber(lit, out double d) ? Value.Real(d) : Value.Str(lit);
                    return (null, lv, 0);
                }
            }
            int err = 0;
            string Sub(string text)
            {
                if (text.IndexOf('?') < 0)
                    return text;
                return Token.Replace(text, m =>
                {
                    var e = DisplayState.Find(w, m.Value);
                    if (e == null)
                    {
                        err = NotAssociated;
                        return m.Value;
                    }
                    return e.Tag;
                });
            }
            // the TAGREF's name: ONE association,
            // named by the text between the first and the last '?', with the text before and after
            // them around its tag
            string tag = r.Tag;
            AssEntry? te = null;
            if (tag.IndexOf('?') >= 0)
            {
                te = DisplayState.Find(w, tag);
                if (te == null)
                    return (null, null, NotAssociated);
                int a0 = tag.IndexOf('?');
                tag = tag.Substring(0, a0) + te.Tag + tag.Substring(tag.LastIndexOf('?') + 1);
            }
            string elem = Sub(r.Element),
                item = Sub(r.Item),
                cluster = Sub(r.Cluster);
            if (err == NotAssociated)
                return (null, null, NotAssociated);
            // the association's cluster, where it can name another one (a project of several)
            if (
                cluster.Length == 0
                && te != null
                && te.Cluster.Length > 0
                && AssRules.Clusters(Display.World).Count > 1
            )
                cluster = te.Cluster;
            // an association to an array element plus an offset: 'Tag1[1][2]' -> 'Tag1[3]' (doc: 1172251)
            int k = tag.IndexOf("][", StringComparison.Ordinal);
            if (k > 0)
            {
                var a = Index.Match(tag.Substring(0, k + 1));
                var b = Index.Match("x" + tag.Substring(k + 1));
                if (
                    a.Success
                    && b.Success
                    && int.TryParse(
                        a.Groups[2].Value,
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture,
                        out int i1
                    )
                    && int.TryParse(
                        b.Groups[2].Value,
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture,
                        out int i2
                    )
                )
                    tag = a.Groups[1].Value + "[" + (i1 + i2).ToString(CultureInfo.InvariantCulture) + "]";
            }
            var nr = new TagRef(r.Offset)
            {
                Cluster = cluster,
                Tag = tag,
                Element = elem,
                Item = item,
                Percentage = r.Percentage,
                ValueHandle = r.ValueHandle,
                Lightweight = r.Lightweight,
            };
            return (nr, null, err);
        }

        private (TagRef? Ref, Value? Literal, int Error) Target(TagRef r, CicodeTask task, RefInfo info)
        {
            if (!info.Assoc)
                return (r, null, 0);
            return Resolve(r, Display.WindowOf(task));
        }

        private bool Range(TagRef r, CicodeTask task, out double zero, out double full)
        {
            if (Db.TryScale(r, task, out zero, out full) && full != zero)
                return true;
            // a tag without an engineering range: 0..32000 (doc: 1172161) (verify)
            zero = 0.0;
            full = 32000.0;
            return Db.ResolveRef(r).Found;
        }

        private Value ToPercent(TagRef r, Value v, CicodeTask task)
        {
            if (!Range(r, task, out double z, out double f) || v.T == Types.String)
                return v;
            double x =
                Types.IsRealType(v.T) ? v.D
                : v.PayloadIsInt ? v.I
                : 0.0;
            return Value.Real((x - z) * 100.0 / (f - z), v.Q, v.Ts, v.Vts, v.Qts);
        }

        private Value FromPercent(TagRef r, Value v, CicodeTask task)
        {
            if (!Range(r, task, out double z, out double f))
                return v;
            double p;
            if (Types.IsRealType(v.T))
                p = v.D;
            else if (v.T == Types.String)
                p = Conv.ParseReal(v.S, out _);
            else if (v.PayloadIsInt)
                p = v.I;
            else
                return v;
            return Value.Real(z + p * (f - z) / 100.0, v.Q, v.Ts, v.Vts, v.Qts);
        }

        // ------------------------------------------------------------------ ITagAccess
        public Value Read(TagRef r, CicodeTask task)
        {
            var info = Info(r);
            if (!info.Assoc && !info.Percent)
                return Db.Read(r, task);
            var (t, lit, err) = Target(r, task, info);
            if (lit != null)
                return lit.Value;
            if (t == null)
            {
                // not associated: 323 with no subscription behind it (good quality, so #ASC shows)
                task.TagError = err;
                return Value.Real(0.0);
            }
            Value v = Db.Read(t, task);
            if (err != 0 && task.TagError == 0)
                task.TagError = err;
            return info.Percent ? ToPercent(t, v, task) : v;
        }

        public int Write(TagRef r, Value value, CicodeTask task)
        {
            var info = Info(r);
            if (!info.Assoc && !info.Percent)
                return Db.Write(r, value, task);
            var (t, lit, err) = Target(r, task, info);
            if (lit != null)
                return 0;
            if (t == null)
            {
                task.TagError = err;
                return err;
            }
            return Db.Write(t, info.Percent ? FromPercent(t, value, task) : value, task);
        }

        public Value ReadElement(TagRef r, long index, CicodeTask task)
        {
            var info = Info(r);
            if (!info.Assoc)
                return Db.ReadElement(r, index, task);
            var (t, lit, err) = Target(r, task, info);
            if (lit != null)
                return lit.Value;
            if (t == null)
            {
                // as Read: 323 with good quality, so the text shows #ASC (a bad quality showed #BAD)
                task.TagError = err;
                return Value.Real(0.0);
            }
            return Db.ReadElement(t, index, task);
        }

        public int WriteElement(TagRef r, long index, Value value, CicodeTask task)
        {
            var info = Info(r);
            if (!info.Assoc)
                return Db.WriteElement(r, index, value, task);
            var (t, lit, err) = Target(r, task, info);
            if (lit != null)
                return 0;
            if (t == null)
            {
                task.TagError = err;
                return err;
            }
            return Db.WriteElement(t, index, value, task);
        }

        public bool TryScale(TagRef? r, CicodeTask task, out double engZero, out double engFull)
        {
            if (r != null && Info(r).Assoc)
            {
                var (t, _, _) = Resolve(r, Display.WindowOf(task));
                return Db.TryScale(t, task, out engZero, out engFull);
            }
            return Db.TryScale(r, task, out engZero, out engFull);
        }

        // ------------------------------------------------------------------ associations (AssFmt)
        /// <summary>The value of an association reference ('?5?', '5', '?Equip?.Item') in the
        /// task's window: the association named between the first and last '?', its tag with the
        /// text around (AssRules.Resolve).</summary>
        public bool Resolve(string name, CicodeTask task, out Value value)
        {
            value = Value.Str("");
            var w = Display.OpenWindow(Display.WindowOf(task));
            if (w == null)
                return false;
            var e = DisplayState.Find(w, name);
            if (e == null)
                return false;
            var r = AssRules.Resolve(Display.World, name, e);
            if (e.Literal)
            {
                value = Value.Str(r.Tag);
                return true;
            }
            string tag =
                r.Index >= 0 ? r.Tag + "[" + r.Index.ToString(CultureInfo.InvariantCulture) + "]" : r.Tag;
            value = Db.Get(tag, cluster: r.Cluster);
            return true;
        }

        /// <summary>AssFmt's format for an association reference.</summary>
        public bool Format(string name, CicodeTask task, out long word, out string eu)
        {
            word = 0x206;
            eu = "";
            var w = Display.OpenWindow(Display.WindowOf(task));
            if (w == null)
                return false;
            var e = DisplayState.Find(w, name);
            if (e == null)
                return false;
            if (e.Literal)
            {
                word = Math.Max(1, e.Tag.Length) & 0xff;
                return true;
            }
            var r = AssRules.Resolve(Display.World, name, e);
            if (r.Error != 0)
                return false;
            var d = Db.Find(r.Tag, r.Cluster);
            if (d == null)
                return true;
            // the association's subscription starts at 0x206 / "" and takes the tag's "Units" and
            // "Format" properties when their reads answer (the runtime's property
            // reads); a tag without a FORMAT keeps 0x206 (live:
            // ExampleSA FP_PMP_HD1080 / FP_Wind_HD1080 show the FORMAT-less SA tags as '0.00' and
            // '50.00', not the '####.#' a page field compiles for them; verify the callback)
            word = CicodeSim.Project.CitectText.IsBlank(d.Format)
                ? 0x206
                : TagTypes.FormatWord(d.DisplayFormat);
            eu = d.EngUnits;
            return true;
        }
    }
}
