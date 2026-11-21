using System;
using System.Threading;
using CicodeSim.Vm;
using Xunit;

namespace CicodeSim.Tests.Vm
{
    public class ClockTests
    {
        [Fact]
        public void RealClockSharedByThreadsStaysOnTheWallClock()
        {
            // the counter / wall-clock base is swapped as one object: threads that resync
            // (here on every read) never pair a new counter base with an old wall clock
            var clock = new RealClock { ResyncTicks = 0 };
            const long Second = 10000000;
            int bad = 0;
            var threads = new Thread[4];
            for (int k = 0; k < threads.Length; k++)
            {
                threads[k] = new Thread(() =>
                {
                    for (int i = 0; i < 50000; i++)
                    {
                        long before = DateTime.UtcNow.ToFileTimeUtc();
                        long ft = (long)clock.NowFiletime();
                        clock.Read(out ulong ft2, out double _);
                        long after = DateTime.UtcNow.ToFileTimeUtc();
                        if (
                            ft < before - Second
                            || ft > after + Second
                            || (long)ft2 < before - Second
                            || (long)ft2 > after + Second
                        )
                            Interlocked.Increment(ref bad);
                    }
                });
                threads[k].Start();
            }
            foreach (var t in threads)
                t.Join();
            Assert.Equal(0, bad);
            var plain = new RealClock();
            long now = DateTime.UtcNow.ToFileTimeUtc();
            Assert.InRange((long)plain.NowFiletime(), now - Second, now + Second);
        }
    }
}
