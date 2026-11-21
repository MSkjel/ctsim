using System;

namespace CicodeSim
{
    /// <summary>The environment variables the engine reads (CICODESIM_*).</summary>
    public static class EngineEnv
    {
        public const string Prefix = "CICODESIM_";

        /// <summary>CICODESIM_<paramref name="name"/>; null when it isn't set or is empty.</summary>
        public static string? Get(string name)
        {
            string? v = Environment.GetEnvironmentVariable(Prefix + name);
            return string.IsNullOrEmpty(v) ? null : v;
        }
    }
}
