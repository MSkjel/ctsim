using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using Xunit;

namespace CicodeSim.Tests.Vm
{
    /// <summary>The engine never reads the current culture.</summary>
    public class CultureTests
    {
        private static CultureInfo Hostile()
        {
            var c = (CultureInfo)new CultureInfo("tr-TR").Clone();
            var n = c.NumberFormat;
            n.NumberDecimalSeparator = ",";
            n.NumberGroupSeparator = ".";
            n.NegativeSign = "~";
            n.PositiveSign = "#";
            n.PercentDecimalSeparator = ",";
            n.CurrencyDecimalSeparator = ",";
            n.NaNSymbol = "nicht";
            n.PositiveInfinitySymbol = "unendlich";
            n.NegativeInfinitySymbol = "~unendlich";
            n.NativeDigits = new[] { "٠", "١", "٢", "٣", "٤", "٥", "٦", "٧", "٨", "٩" };
            return c;
        }

        public static IEnumerable<object[]> Cases()
        {
            var asm = typeof(CultureTests).Assembly;
            foreach (var type in asm.GetTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                // HostCultureTests sets the process culture itself
                if (
                    type == typeof(CultureTests)
                    || type == typeof(HostCultureTests)
                    || !type.IsPublic
                    || type.IsAbstract
                    || type.Namespace != typeof(CultureTests).Namespace
                )
                    continue;
                foreach (
                    var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .OrderBy(m => m.Name, StringComparer.Ordinal)
                )
                {
                    if (m.GetCustomAttribute<FactAttribute>() is not { } fact || fact.Skip != null)
                        continue;
                    var inline = m.GetCustomAttributes<InlineDataAttribute>().ToList();
                    if (fact is TheoryAttribute)
                    {
                        int k = 0;
                        foreach (var d in inline)
                        foreach (var row in d.GetData(m))
                            yield return new object[] { type.Name + "." + m.Name + "#" + k++, m, row };
                    }
                    else if (m.GetParameters().Length == 0)
                        yield return new object[] { type.Name + "." + m.Name, m, Array.Empty<object>() };
                }
            }
        }

        [Fact]
        public void EveryTestPassesUnderAHostileCulture()
        {
            var bad = new List<string>();
            int n = 0;
            var saved = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
            var hostile = Hostile();
            try
            {
                foreach (var c in Cases())
                {
                    var name = (string)c[0];
                    var m = (MethodInfo)c[1];
                    var args = (object[])c[2];
                    Thread.CurrentThread.CurrentCulture = hostile;
                    Thread.CurrentThread.CurrentUICulture = hostile;
                    n++;
                    try
                    {
                        object? inst = Activator.CreateInstance(m.DeclaringType!);
                        m.Invoke(inst, args);
                        (inst as IDisposable)?.Dispose();
                    }
                    catch (TargetInvocationException e)
                    {
                        if (bad.Count < 20)
                            bad.Add(
                                name
                                    + ": "
                                    + e.InnerException?.GetType().Name
                                    + " "
                                    + e.InnerException?.Message.Split('\n')[0]
                            );
                    }
                }
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved.CurrentCulture;
                Thread.CurrentThread.CurrentUICulture = saved.CurrentUICulture;
            }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
            Assert.True(n > 150, $"only {n} tests found");
        }
    }
}
