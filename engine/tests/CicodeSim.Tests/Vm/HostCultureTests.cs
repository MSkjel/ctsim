using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using CicodeSim.Host;
using Xunit;

namespace CicodeSim.Tests.Vm
{
    /// <summary>ctsim's JSON never takes the user's culture: on .NET (Core) the ICU nb-NO /
    /// sv-SE / fi-FI cultures write a negative number with U+2212 (invalid JSON for a negative
    /// halt code / pc / error).</summary>
    public class HostCultureTests
    {
        private static CultureInfo Hostile()
        {
            var c = (CultureInfo)new CultureInfo("nb-NO").Clone();
            c.NumberFormat.NegativeSign = "−";
            c.NumberFormat.NumberDecimalSeparator = ",";
            return c;
        }

        [Fact]
        public void JsonIntegersAreInvariant()
        {
            var saved = CultureInfo.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = Hostile();
                var sb = new StringBuilder();
                sb.AppendInv(-1).Append(' ').AppendInv(long.MinValue).Append(' ').AppendInv(ulong.MaxValue);
                Assert.Equal("-1 -9223372036854775808 18446744073709551615", sb.ToString());
                // and what the plain Append would have written
                Assert.Equal("−1", new StringBuilder().Append(-1).ToString());
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void MainPinsTheInvariantCulture()
        {
            var saved = (
                CultureInfo.CurrentCulture,
                CultureInfo.CurrentUICulture,
                CultureInfo.DefaultThreadCurrentCulture,
                CultureInfo.DefaultThreadCurrentUICulture
            );
            var outSaved = Console.Out;
            try
            {
                Thread.CurrentThread.CurrentCulture = Hostile();
                Console.SetOut(new StringWriter());
                Assert.Equal(0, CicodeSim.Host.Program.Main(new[] { "--version" }));
                Assert.Same(CultureInfo.InvariantCulture, CultureInfo.CurrentCulture);
                Assert.Same(CultureInfo.InvariantCulture, CultureInfo.DefaultThreadCurrentCulture);
                Assert.Equal("-1", new StringBuilder().Append(-1).ToString());
            }
            finally
            {
                Console.SetOut(outSaved);
                Thread.CurrentThread.CurrentCulture = saved.Item1;
                Thread.CurrentThread.CurrentUICulture = saved.Item2;
                CultureInfo.DefaultThreadCurrentCulture = saved.Item3;
                CultureInfo.DefaultThreadCurrentUICulture = saved.Item4;
            }
        }
    }
}
