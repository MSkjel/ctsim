using System;
using System.Collections.Generic;

namespace CicodeSim
{
    /// <summary>
    /// Failures the engine deliberately passes over, counted and kept so a host can show them.
    /// </summary>
    public static class EngineNotes
    {
        private static readonly object Lock = new object();
        private static readonly Dictionary<string, int> Counts = new Dictionary<string, int>(
            StringComparer.Ordinal
        );
        private static readonly List<string> Recent = new List<string>();
        private const int Keep = 50;

        /// <summary>Record that <paramref name="where"/> passed over <paramref name="e"/>.</summary>
        public static void Swallowed(string where, Exception e)
        {
            string line = where + ": " + e.GetType().Name + ": " + e.Message;
            lock (Lock)
            {
                Counts.TryGetValue(where, out int n);
                Counts[where] = n + 1;
                if (n >= 20)
                    return; // a site that fails every time is counted, not repeated
                Recent.Add(line);
                if (Recent.Count > Keep)
                    Recent.RemoveAt(0);
            }
            System.Diagnostics.Trace.WriteLine("cicode-sim: " + line);
        }

        /// <summary>The count per site so far.</summary>
        public static Dictionary<string, int> CountsBySite()
        {
            lock (Lock)
                return new Dictionary<string, int>(Counts, StringComparer.Ordinal);
        }

        /// <summary>The last notes (oldest first).</summary>
        public static List<string> Last()
        {
            lock (Lock)
                return new List<string>(Recent);
        }
    }
}
