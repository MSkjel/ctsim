using CicodeSim.Builtins;
using CicodeSim.Values;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>Special days (Builtins.SpecialDays): Example's Scheduler.ci adds a holiday group
    /// and its days, then lists them through the special-day sessions.</summary>
    public sealed class SpecialDayTests : DataTestBase
    {
        [Fact]
        public void AHolidayGroupAndItsDaysAreKeptAndBrowsed()
        {
            var s = MakeData();
            long day = 864000000000L;
            long christmas = 133800000000000000L / day * day;
            Value Ts(long ft) => Value.Int(Types.Timestamp, ft);
            long group = Call(s, "SchdSpecialAdd", "Cluster1", "Holidays").I;
            Assert.True(group > 0);
            long first = Call(
                s,
                "SchdSpecialItemAdd",
                "Cluster1",
                group,
                "Christmas Day",
                Ts(christmas + 3600)
            ).I;
            Call(s, "SchdSpecialItemAdd", "Cluster1", group, "Boxing Day", Ts(christmas + day));
            Assert.Equal(274, Call(s, "SchdSpecialItemAdd", "Cluster1", 9999, "x", Ts(christmas)).Error);

            // (Scheduler.ci) the groups, then the items with their group and day
            long g = Call(s, "SchdSpecialOpen", "", "Name,ID").I;
            Assert.True(g >= 0); // (browse sessions are numbered from 0)
            Assert.Equal(1, Call(s, "SchdSpecialNumRecords", g).I);
            Call(s, "SchdSpecialFirst", g);
            Assert.Equal("Holidays", Call(s, "SchdSpecialGetField", g, "Name").S);
            Call(s, "SchdSpecialClose", g);
            long it = Call(s, "SchdSpecialItemOpen", "", "GroupName,Name,Day,ID").I;
            Assert.Equal(2, Call(s, "SchdSpecialItemNumRecords", it).I);
            Call(s, "SchdSpecialItemFirst", it);
            Assert.Equal("Holidays", Call(s, "SchdSpecialItemGetField", it, "GroupName").S);
            Assert.Equal("Christmas Day", Call(s, "SchdSpecialItemGetField", it, "Name").S);
            // (the day's date: its time of day dropped)
            Assert.Equal(christmas.ToString(), Call(s, "SchdSpecialItemGetField", it, "Day").S);
            Call(s, "SchdSpecialItemClose", it);

            // a day removed, then the group with what is left
            Assert.Equal(0, Call(s, "SchdSpecialItemDelete", "Cluster1", first).I);
            it = Call(s, "SchdSpecialItemOpen", "", "Name").I;
            Assert.Equal(1, Call(s, "SchdSpecialItemNumRecords", it).I);
            Call(s, "SchdSpecialItemClose", it);
            Assert.Equal(0, Call(s, "SchdSpecialDelete", "Cluster1", group).I);
            it = Call(s, "SchdSpecialItemOpen", "", "Name").I;
            Assert.Equal(0, Call(s, "SchdSpecialItemNumRecords", it).I);
            Assert.Equal(Codes.FgBlock, CallFg(s, "SchdSpecialAdd", "Cluster1", "x").Error);
        }
    }
}
