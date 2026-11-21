using System.Globalization;
using System.Text;
using System.Threading;

namespace CicodeSim.Host
{
    /// <summary>ctsim never writes in the user's culture: on .NET (Core) the ICU cultures of
    /// Norway, Sweden and Finland format a negative number with U+2212, which breaks the JSON a
    /// client reads.</summary>
    internal static class Invariant
    {
        /// <summary>Pin the process to the invariant culture (Main calls it first; threads the
        /// host starts later inherit it through the Default* properties).</summary>
        public static void Pin()
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
        }

        public static StringBuilder AppendInv(this StringBuilder sb, long v) =>
            sb.Append(v.ToString(CultureInfo.InvariantCulture));

        public static StringBuilder AppendInv(this StringBuilder sb, ulong v) =>
            sb.Append(v.ToString(CultureInfo.InvariantCulture));
    }
}
