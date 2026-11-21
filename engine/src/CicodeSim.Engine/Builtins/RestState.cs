using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>Per-process state for the remaining built-ins.</summary>
    public sealed class ProcessExtras
    {
        private static readonly ConditionalWeakTable<SimHost, ProcessExtras> All =
            new ConditionalWeakTable<SimHost, ProcessExtras>();

        public static ProcessExtras Of(SimHost host) => All.GetValue(host, h => new ProcessExtras(h));

        public SimHost Host { get; }

        private ProcessExtras(SimHost h)
        {
            Host = h;
        }

        // ------------------------------------------------------------------ clipboard
        /// <summary>The simulated clipboard: the CF_TEXT data and the 'CSV' format's data (null =
        /// the format is not on the clipboard). A host may set or read them; Clip* built-ins work
        /// on these and never on the Windows clipboard.</summary>
        public string? ClipText,
            ClipCsv;

        /// <summary>ClipSetMode's mode (1 text, 2 CSV; initially 1).</summary>
        internal int ClipMode = 1;

        /// <summary>ClipWriteLn's block being written (null = none open).</summary>
        internal System.Text.StringBuilder? ClipWriting;

        /// <summary>ClipReadLn's copy of the clipboard text and its read position (null = none).</summary>
        internal string? ClipReading;
        internal int ClipReadPos;

        // ------------------------------------------------------------------ CtAPI queries
        /// <summary>A CtAPI query a host started on a task (the runtime's pending 'Cicode Query' of
        /// a CtAPI ctCicode call: the task the query runs on); CAPIPost answers it.</summary>
        public sealed class CapiQuery
        {
            public int Task;
            public bool Answered;

            /// <summary>The device CAPIPost names (the query's data is that device's records).</summary>
            public string Device = "";

            /// <summary>The error CAPIPost passed (0 = the device holds the answer).</summary>
            public long Error;
            public double Time;
        }

        internal readonly Dictionary<int, CapiQuery> CapiPending = new Dictionary<int, CapiQuery>();

        /// <summary>Every answered query, oldest first (at most 1000 kept).</summary>
        public readonly List<CapiQuery> CapiPosted = new List<CapiQuery>();

        /// <summary>Make <paramref name="t"/> a pending CtAPI query (a host's ctCicode-like call):
        /// its CAPIPost answers the returned query instead of failing with 289.</summary>
        public CapiQuery BeginCapiQuery(CicodeTask t)
        {
            var q = new CapiQuery { Task = t.Handle };
            CapiPending[t.Handle] = q;
            return q;
        }

        // ------------------------------------------------------------------ language
        /// <summary>GetLanguage's current language name and its language / country code (null =
        /// not set yet: the [Language]LocalLanguage parameter or English).</summary>
        internal string? LanguageName,
            LanguageCode;

        // ------------------------------------------------------------------ I/O devices
        /// <summary>IODeviceControl's settings per device (lower-case name) and type: the Data text
        /// last given (no I/O device runs in the simulator: nothing else follows from them).</summary>
        public readonly Dictionary<(string Device, int Type), string> IoControls =
            new Dictionary<(string, int), string>();

        /// <summary>__ObjectMasterAN's table: (table, index) to the stored value.</summary>
        internal readonly Dictionary<(long, long), long> MasterAn = new Dictionary<(long, long), long>();

        // ------------------------------------------------------------------ clusters, logging, paths
        /// <summary>The clusters ClusterDeactivate made inactive (lower case); every other cluster is active.</summary>
        public readonly HashSet<string> InactiveClusters = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>SetLogging's values ('section.name' in lower case).</summary>
        internal readonly Dictionary<string, string> Logging = new Dictionary<string, string>(
            StringComparer.Ordinal
        );

        /// <summary>rdbSetPath's folders (null = not set: the unit's root folder).</summary>
        internal List<string>? RdbPath;

        /// <summary>ProjectRestartSet's text.</summary>
        internal string RestartProject = "";

        // ------------------------------------------------------------------ alarm journal
        /// <summary>One event SOEEventAdd journals.</summary>
        public sealed class UserEvent
        {
            public ulong Time;
            public string Message = "",
                Tag = "",
                Cluster = "";
        }

        public readonly List<UserEvent> UserEvents = new List<UserEvent>();

        /// <summary>The comments of summary and event entries (AlarmComment, AlmSummarySetFieldValue).</summary>
        internal readonly ConditionalWeakTable<object, string[]> Comments =
            new ConditionalWeakTable<object, string[]>();

        internal string? CommentOf(object entry) => Comments.TryGetValue(entry, out var c) ? c[0] : null;

        internal void SetComment(object entry, string text)
        {
            Comments.Remove(entry);
            Comments.Add(entry, new[] { text });
        }
    }
}
