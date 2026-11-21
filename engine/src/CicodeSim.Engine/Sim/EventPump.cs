using System;
using System.Collections.Concurrent;
using System.Threading;

namespace CicodeSim.Sim
{
    /// <summary>
    /// One thread that runs posted actions in order, so events are never raised under a lock.
    /// </summary>
    public sealed class EventPump : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _thread;
        private int _disposed;

        /// <summary>A posted action threw (raised on the pump thread).</summary>
        public event Action<Exception>? Error;

        public EventPump(string name = "ctsim-events")
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = name };
            _thread.Start();
        }

        public bool IsPumpThread => Thread.CurrentThread == _thread;

        /// <summary>Report a handler's exception through <see cref="Error"/> (never throws).</summary>
        public void Report(Exception e)
        {
            try
            {
                Error?.Invoke(e);
            }
            catch (Exception) { }
        }

        /// <summary>Call every handler of an event, each on its own: one that throws is reported
        /// and the others still get the event (a multicast delegate stops at the first throw, so
        /// a failing DAP-layer handler would take the viewers' events with it).</summary>
        public void Each(Delegate? handlers, Action<Delegate> call)
        {
            if (handlers == null)
                return;
            foreach (var h in handlers.GetInvocationList())
            {
                try
                {
                    call(h);
                }
                catch (Exception e)
                {
                    Report(e);
                }
            }
        }

        public void Post(Action a)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            try
            {
                _queue.Add(a);
            }
            catch (InvalidOperationException)
            {
                // completed: the pump is ending
            }
        }

        /// <summary>Wait until every action posted before this call has run (false on timeout).
        /// On the pump thread itself it returns at once.</summary>
        public bool Flush(int timeoutMs = 10_000)
        {
            if (IsPumpThread || Volatile.Read(ref _disposed) != 0)
                return true;
            using (var done = new ManualResetEventSlim(false))
            {
                Post(() => done.Set());
                return done.Wait(timeoutMs);
            }
        }

        private void Loop()
        {
            foreach (var a in _queue.GetConsumingEnumerable())
            {
                try
                {
                    a();
                }
                catch (Exception e)
                {
                    Report(e);
                }
            }
        }

        /// <summary>Run what is queued, then end the thread (waits up to 5 s).</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try
            {
                _queue.CompleteAdding();
            }
            catch (Exception) { }
            if (!IsPumpThread)
                _thread.Join(5000);
        }
    }
}
