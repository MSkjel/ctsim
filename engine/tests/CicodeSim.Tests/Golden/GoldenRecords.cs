using System.Collections.Generic;
using CicodeSim.Values;
using CicodeSim.Vm;
using Obj = CicodeSim.Tests.Golden.GoldenJson.Obj;

namespace CicodeSim.Tests.Golden
{
    /// <summary>The semantic fields of a VM run and of a value, as golden records hold them.</summary>
    public static class GoldenRecords
    {
        /// <summary>A value as a golden record object.</summary>
        public static object? ValueObj(Value? rv)
        {
            if (!rv.HasValue)
                return null;
            Value v = rv.Value;
            object? payload;
            if (Types.IsRealType(v.T))
                payload = v.D;
            else if (v.T == Types.String)
                payload = v.S;
            else if (v.T == Types.Variant && v.O != null)
                payload = "<ref>";
            else if (v.T == Types.Timestamp || v.T == Types.Quality)
                payload = (ulong)v.I;
            else
                payload = v.I;
            return new Obj()
                .Add("t", v.T)
                .Add("v", payload)
                .Add("q", v.Q)
                .Add("ts", v.Ts)
                .Add("vts", v.Vts)
                .Add("qts", v.Qts);
        }

        /// <summary>The halt reason a run's outcome implies; a record stores the reason only when
        /// it is another text.</summary>
        public static string ImpliedReason(string outcome, int code, string notSimulated)
        {
            if (outcome == "not simulated")
                return "built-in function " + notSimulated + " is not simulated";
            if (outcome.StartsWith("halt ", System.StringComparison.Ordinal))
                return "hardware error " + code.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return "";
        }

        /// <summary>A run's fields: outcome, state, halt code, the not simulated built-in, pc,
        /// line, instruction count, IsError state, expression error and the result.</summary>
        public static void AddRun(Obj o, CicodeTask t, string outcome, string notSimulated)
        {
            o.Add("outcome", outcome).Add("state", t.State).Add("code", t.HaltCode);
            if (outcome == "not simulated")
                o.Add("nsfn", notSimulated);
            if (t.HaltReason != ImpliedReason(outcome, t.HaltCode, notSimulated))
                o.Add("reason", t.HaltReason);
            o.Add("pc", t.Pc)
                .Add("line", t.Line)
                .Add("instr", t.Instructions)
                .Add("error", t.Error)
                .Add("expr", t.ExprError);
            o.Add("result", ValueObj(t.Result));
        }

        /// <summary>The key of a golden line: the named fields of its record, joined by '|'.</summary>
        public static string KeyOf(string line, params string[] fields)
        {
            var j = JsonValue.Parse(line);
            var parts = new List<string>();
            foreach (string f in fields)
            {
                var x = j.Get(f);
                parts.Add(
                    x == null ? ""
                    : x.Kind == JsonKind.String ? x.Str
                    : x.Dump()
                );
            }
            return string.Join("|", parts);
        }
    }
}
