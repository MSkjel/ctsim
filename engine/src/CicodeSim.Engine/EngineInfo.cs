using System;
using System.Globalization;
using System.Reflection;

namespace CicodeSim
{
    /// <summary>Version and identity of the engine build (PROTOCOL.md 'Versions').</summary>
    public static class EngineInfo
    {
        public const string Name = "cicode-sim";

        /// <summary>
        /// The protocol revision. Raised by one whenever the server gains something a client may
        /// rely on.
        /// </summary>
        public const int ProtocolRevision = 29;

        /// <summary>The assembly version ('0.0.1.0').</summary>
        public static string Version => typeof(EngineInfo).Assembly.GetName().Version?.ToString() ?? "0";

        /// <summary>The git commit the build was made from ('' when the build did not know it):
        /// the SDK appends it to the informational version ('0.0.1+&lt;sha&gt;').</summary>
        public static string Commit
        {
            get
            {
                string info = Attribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
                int plus = info.IndexOf('+');
                return plus >= 0 ? info.Substring(plus + 1) : "";
            }
        }

        /// <summary>When the build was made, UTC ISO 8601 ('' when not stamped): the
        /// 'BuildTime' assembly metadata Directory.Build.props writes.</summary>
        public static string BuildTime
        {
            get
            {
                foreach (
                    var a in typeof(EngineInfo).Assembly.GetCustomAttributes(
                        typeof(AssemblyMetadataAttribute),
                        false
                    )
                )
                    if (a is AssemblyMetadataAttribute m && m.Key == "BuildTime")
                        return m.Value ?? "";
                return "";
            }
        }

        /// <summary>'cicode-sim 0.0.1.0, protocol revision 29, commit 1a2b3c4d5e6f, built
        /// 2026-10-02T12:30:00Z'.</summary>
        public static string Describe()
        {
            string c = Commit;
            string s =
                Name
                + " "
                + Version
                + ", protocol revision "
                + ProtocolRevision.ToString(CultureInfo.InvariantCulture);
            if (c.Length > 0)
                s += ", commit " + (c.Length > 12 ? c.Substring(0, 12) : c);
            if (BuildTime.Length > 0)
                s += ", built " + BuildTime;
            return s;
        }

        private static T? Attribute<T>()
            where T : Attribute
        {
            var a = typeof(EngineInfo).Assembly.GetCustomAttributes(typeof(T), false);
            return a.Length > 0 ? (T)a[0] : null;
        }
    }
}
