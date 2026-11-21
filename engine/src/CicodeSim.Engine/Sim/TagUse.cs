using System;
using System.Collections.Generic;
using System.IO;
using CicodeSim.Compiled;
using CicodeSim.Values;
using Program = CicodeSim.Compiled.Program;

namespace CicodeSim.Sim
{
    /// <summary>Guess a tag's type from how compiled code uses it (simulator only).</summary>
    public static class TagUse
    {
        public const string Digital = "DIGITAL",
            Int = "INT",
            Real = "REAL",
            Str = "STRING";

        private sealed class Votes
        {
            public int S,
                R,
                I,
                D;
            public string? Best =>
                S > 0 ? Str
                : R > 0 ? Real
                : I > 0 ? Int
                : D > 0 ? Digital
                : null;
        }

        /// <summary>Lower-case tag name -> guessed type, for every tag the code blocks reference
        /// with evidence. <paramref name="library"/> answers the result type of user functions
        /// (null: none known).</summary>
        public static Dictionary<string, string> Guess(
            Program program,
            IEnumerable<byte[]> blocks,
            Program? library = null
        )
        {
            var votes = new Dictionary<string, Votes>(StringComparer.Ordinal);
            foreach (var code in blocks)
                Scan(program, code, library, votes);
            var r = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in votes)
            {
                string? b = kv.Value.Best;
                if (b != null)
                    r[kv.Key] = b;
            }
            return r;
        }

        /// <summary>Guesses from a library's functions (their code and pools).</summary>
        public static Dictionary<string, string> GuessLibrary(Program library)
        {
            var blocks = new List<byte[]>(library.Functions.Count);
            foreach (var f in library.Functions)
                if (f.Code != null)
                    blocks.Add(f.Code);
            return Guess(library, blocks, library);
        }

        /// <summary>The code blocks of a compiled page: the .RDB's code fields when the page came
        /// from one, else every [u16 length][code] block of its CODE pool.</summary>
        public static List<byte[]> PageBlocks(CicodeSim.Page.CompiledPage cp)
        {
            var r = new List<byte[]>();
            if (cp.Path.EndsWith(".rdb", StringComparison.OrdinalIgnoreCase) && File.Exists(cp.Path))
            {
                try
                {
                    foreach (var b in Rdb.PageFromRdb(RdbFile.Open(cp.Path)).Blocks)
                        r.Add(b.Code);
                    return r;
                }
                catch (Exception e)
                    when (e is IOException
                        || e is InvalidDataException
                        || e is DecodeError
                        || e is IndexOutOfRangeException
                        || e is ArgumentException
                    )
                {
                    r.Clear();
                }
            }
            byte[] c = cp.Code;
            int off = 2;
            while (off <= c.Length)
            {
                int n = c[off - 2] | (c[off - 1] << 8);
                if (n == 0)
                {
                    off += 2; // a pad word (the rows compiler's offset 0)
                    continue;
                }
                if (off + n > c.Length)
                    break;
                var b = new byte[n];
                Array.Copy(c, off, b, 0, n);
                r.Add(b);
                off += n + 2;
            }
            return r;
        }

        private static bool IsCompare(int op) =>
            op == Op.Eq || op == Op.Ne || op == Op.Lt || op == Op.Le || op == Op.Gt || op == Op.Ge;

        private static List<Instr> Decode(byte[] code)
        {
            var r = new List<Instr>();
            int pc = 0;
            while (pc < code.Length)
            {
                Instr ins;
                try
                {
                    ins = Opcodes.Decode(code, pc);
                }
                catch (Exception e) when (e is DecodeError || e is IndexOutOfRangeException)
                {
                    break;
                }
                r.Add(ins);
                if (ins.Op == Op.End || ins.Size <= 0)
                    break;
                pc += ins.Size;
            }
            return r;
        }

        private static void ConstVote(Program prog, in Instr c, Votes v)
        {
            int sub = c.Sub;
            if (sub == Types.String)
                v.S++;
            else if (sub == Types.Real || sub == Types.LongReal)
                v.R++;
            else if (sub == 0x01 || sub == 0x04 || sub == 0x08 || sub == 0x0C || sub == 0x11)
            {
                if (c.IntValue == 0 || c.IntValue == 1)
                    v.D++;
                else
                    v.I++;
            }
        }

        private static void Scan(Program prog, byte[] code, Program? library, Dictionary<string, Votes> votes)
        {
            var ins = Decode(code);
            for (int k = 0; k < ins.Count; k++)
            {
                var x = ins[k];
                if (x.Op != Op.Load && x.Op != Op.Store)
                    continue;
                if (!prog.TagRefs.TryGetValue(x.Offset, out var tr))
                    continue;
                if (
                    tr.Tag.Length == 0
                    || tr.Tag.IndexOf('?') >= 0
                    || tr.Element.Length > 0
                    || (
                        tr.Item.Length > 0 && !string.Equals(tr.Item, "v", StringComparison.OrdinalIgnoreCase)
                    )
                )
                    continue;
                string key = tr.Tag.Trim().ToLowerInvariant();
                if (!votes.TryGetValue(key, out var v))
                    votes[key] = v = new Votes();
                if (x.Op == Op.Store)
                {
                    if (k == 0)
                        continue;
                    var p = ins[k - 1];
                    if (p.Op == Op.Const)
                        ConstVote(prog, p, v);
                    else if (p.Op == Op.Convert)
                    {
                        if (p.A == Types.String)
                            v.S++;
                        else if (p.A == Types.Real || p.A == Types.LongReal)
                            v.R++;
                    }
                    else if (p.Op == Op.StringFmt)
                        // a value formatted to text before the store: the tag holds text (a
                        // CtCmp32 compile of 'Tag = ""' into a STRING tag stores Const "",
                        // StringFmt, Store; the format's width / decimals are not the tag's)
                        v.S++;
                    else if (
                        p.Op == Op.FuncCallByLookup
                        && library != null
                        && prog.FuncLookups.TryGetValue(p.Offset, out var fl)
                        && library.Function(fl.Name) is Function fn
                    )
                    {
                        if (fn.Result == Types.String)
                            v.S++;
                        else if (fn.Result == Types.Real || fn.Result == Types.LongReal)
                            v.R++;
                    }
                    continue;
                }
                // a Load: what the next instruction does with the value (or a constant before it
                // and a comparison after it)
                if (k + 1 >= ins.Count)
                {
                    v.D++;
                    continue;
                }
                var n = ins[k + 1];
                switch (n.Op)
                {
                    case Op.Const:
                        if (k + 2 < ins.Count && IsCompare(ins[k + 2].Op))
                            ConstVote(prog, n, v);
                        break;
                    case Op.Convert:
                        if (n.A == Types.String)
                            v.S++;
                        else if (n.A == Types.Real || n.A == Types.LongReal)
                            v.R++;
                        break;
                    case Op.Bar:
                        if (n.U32 == 0xFFFFFFFFu)
                            v.R++;
                        else if (prog.Scales.TryGetValue(n.U32, out var sc))
                        {
                            double lo = Math.Min(sc.EngZero, sc.EngFull),
                                hi = Math.Max(sc.EngZero, sc.EngFull);
                            if (lo == 0 && hi == 1)
                                v.D++;
                            else
                                v.R++;
                        }
                        break;
                    case Op.Mul:
                    case Op.Div:
                        v.R++;
                        break;
                    case Op.StringFmt:
                        if (((n.U32 >> 8) & 0xFF) > 0)
                            v.R++;
                        else
                            v.I++;
                        break;
                    case Op.And:
                    case Op.Or:
                    case Op.Not:
                    case Op.Brfalse:
                    case Op.BrTrue:
                    case Op.End:
                        v.D++;
                        break;
                    default:
                        if (IsCompare(n.Op) && k > 0 && ins[k - 1].Op == Op.Const)
                            ConstVote(prog, ins[k - 1], v);
                        break;
                }
            }
        }
    }
}
