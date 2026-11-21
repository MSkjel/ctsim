using System;
using CicodeSim.Values;
using CicodeSim.Vm;

namespace CicodeSim.Builtins
{
    /// <summary>CAPIPost: the answer to a CtAPI Cicode query.</summary>
    public static class CtApiQueries
    {
        [Builtin(
            "CAPIPost",
            Doc = "",
            Effects = "answers the task's pending CtAPI query: queued with the device name and the error (ProcessExtras.CapiPosted)",
            Errors = "289 no pending query on this task (returned, not raised: 'No Pending Query on this thread, Aborted')",
            Notes = "Undocumented (Include citect.ci CTAPITrend / CTAPIAlarm ...: CAPIPost(sDevName, nError) after filling the query device). With a pending query: iError other than 0 completes it with that error, 0 with the device sName to read; 0 returned (the runtime's device search errors, 347, are not simulated). The query is the task's: a host's ProcessExtras.BeginCapiQuery(task)."
        )]
        private static Value CAPIPost(SimHost h, CicodeTask t, ArgList a)
        {
            var x = ProcessExtras.Of(h);
            if (!x.CapiPending.TryGetValue(t.Handle, out var q) || q.Answered)
            {
                h.Note(t, "CAPIPost: No Pending Query on this thread, Aborted");
                return W.I(289);
            }
            q.Answered = true;
            q.Device = A.S(a.Get(0));
            q.Error = A.Short(A.I(a.Get(1)));
            q.Time = h.World?.Clock.Now() ?? 0;
            x.CapiPending.Remove(t.Handle);
            x.CapiPosted.Add(q);
            if (x.CapiPosted.Count > 1000)
                x.CapiPosted.RemoveRange(0, x.CapiPosted.Count - 1000);
            h.World?.Trace?.Event(
                "capiPost",
                null,
                t.Handle >= 0 ? (object)t.Handle : null,
                ("device", q.Device),
                ("error", q.Error)
            );
            return W.I(0);
        }
    }
}
