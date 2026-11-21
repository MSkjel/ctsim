using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using CicodeSim.Compiler.Semantics;
using CicodeSim.Compiler.Syntax;
using Xunit;

namespace CicodeSim.Tests.Compiler
{
    /// <summary>The front end never reads the current culture.</summary>
    public class CompilerCultureTests
    {
        private static CultureInfo Hostile()
        {
            var c = (CultureInfo)new CultureInfo("tr-TR").Clone();
            var n = c.NumberFormat;
            n.NumberDecimalSeparator = ",";
            n.NumberGroupSeparator = ".";
            n.NegativeSign = "~";
            n.PositiveSign = "#";
            n.NaNSymbol = "nicht";
            n.PositiveInfinitySymbol = "unendlich";
            n.NegativeInfinitySymbol = "~unendlich";
            n.NativeDigits = new[] { "٠", "١", "٢", "٣", "٤", "٥", "٦", "٧", "٨", "٩" };
            return c;
        }

        private static IEnumerable<(string name, MethodInfo m, object?[] args)> Cases()
        {
            var asm = typeof(CompilerCultureTests).Assembly;
            foreach (var type in asm.GetTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                if (
                    type == typeof(CompilerCultureTests)
                    || !type.IsPublic
                    || type.IsAbstract
                    || type.Namespace != typeof(CompilerCultureTests).Namespace
                )
                    continue;
                foreach (
                    var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .OrderBy(m => m.Name, StringComparer.Ordinal)
                )
                {
                    if (!(m.GetCustomAttribute<FactAttribute>() is FactAttribute fact) || fact.Skip != null)
                        continue;
                    if (fact is TheoryAttribute)
                    {
                        int k = 0;
                        foreach (var d in m.GetCustomAttributes<InlineDataAttribute>())
                        foreach (var row in d.GetData(m))
                            yield return (type.Name + "." + m.Name + "#" + k++, m, row);
                    }
                    else if (m.GetParameters().Length == 0)
                        yield return (type.Name + "." + m.Name, m, Array.Empty<object?>());
                }
            }
        }

        private static T Under<T>(CultureInfo c, Func<T> f)
        {
            var saved = (Thread.CurrentThread.CurrentCulture, Thread.CurrentThread.CurrentUICulture);
            Thread.CurrentThread.CurrentCulture = c;
            Thread.CurrentThread.CurrentUICulture = c;
            try
            {
                return f();
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved.CurrentCulture;
                Thread.CurrentThread.CurrentUICulture = saved.CurrentUICulture;
            }
        }

        [Fact]
        public void EveryCompilerTestPassesUnderAHostileCulture()
        {
            var bad = new List<string>();
            int n = 0;
            var hostile = Hostile();
            foreach (var (name, m, args) in Cases())
            {
                n++;
                string? err = Under(hostile, () =>
                {
                    try
                    {
                        m.Invoke(Activator.CreateInstance(m.DeclaringType!), args);
                        return null;
                    }
                    catch (TargetInvocationException e)
                    {
                        return name
                            + ": "
                            + e.InnerException?.GetType().Name
                            + " "
                            + e.InnerException?.Message.Split('\n')[0];
                    }
                });
                if (err != null && bad.Count < 20)
                    bad.Add(err);
            }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
            Assert.True(n > 100, $"only {n} tests found");
        }

        [Fact]
        public void TheDumpIsTheSameTextUnderEveryCulture()
        {
            string src =
                "GLOBAL REAL g = -1.5;\nMODULE INT m[2] = -7, 0x1F;\n"
                + "REAL FUNCTION F(REAL a = 1e-7, INT b = -2147483648, STRING s = \"ä^0x41\")\n"
                + "\tREAL r = 1.25E+3 + .5 - 12b + 0o17;\n\tSTRING t = r:#0.##EU + a:-3.25 + a:~;\n"
                + "\tIF r > -0.001 AND NOT b THEN RETURN r * 2.5 MOD 3; END\n\tRETURN -1e308 * 10;\nEND\n";
            var lt = new LabelTable();
            lt.Add(Label.FromRow("TWICE(x)", "x*2", "P", 1));
            string Dump()
            {
                var m = Parser.ParseSource(
                    src + "INT FUNCTION G()\n\tRETURN TWICE(-3) + 4294967295;\nEND\n",
                    lt,
                    "t.ci",
                    "P"
                );
                var lines = AstDump.Lines(m, "t").ToList();
                foreach (var d in m.Diagnostics.Concat(m.DeclDiagnostics))
                    lines.Add(AstDump.DiagLine("d", d) + d);
                var reg = new Registry(null);
                reg.Add(
                    new SourceProject(
                        "Main",
                        null,
                        new List<KeyValuePair<string, string>>
                        {
                            new KeyValuePair<string, string>("a.ci", src),
                        }
                    )
                );
                var (order, od) = reg.CompileOrder("Main");
                var u = new CompileUnit(order, UnitTests.SmallFunc0, od).Compile();
                foreach (var d in u.AllDiagnostics())
                    lines.Add(AstDump.DiagLine("u", d) + d);
                foreach (var row in u.Funcsym())
                    lines.Add(string.Join("|", row.Select(kv => kv.Key + "=" + kv.Value)));
                return string.Join("\n", lines);
            }
            string inv = Under(CultureInfo.InvariantCulture, Dump);
            string hos = Under(Hostile(), Dump);
            Assert.Equal(inv, hos);
            Assert.Contains("\"-1.5\"", inv);
            Assert.Contains("\"1e-07\"", inv);
        }
    }
}
