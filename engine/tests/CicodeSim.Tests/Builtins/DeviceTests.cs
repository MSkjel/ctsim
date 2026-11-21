using System.IO;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>Devices over the sandbox (Builtins.Devices): what the shared projects' export,
    /// audit and alarm log code does with them, written and read back.</summary>
    public sealed class DeviceTests : DataTestBase
    {
        private Sim MakeDevices() =>
            MakeData(() =>
                Dbf(
                    Path.Combine(MainDir, "devices.dbf"),
                    new[] { "NAME", "FORMAT", "HEADER", "FILENAME", "TYPE", "FILES" },
                    new[] { "Scratch", "", "", "[RUN]:scratch.dbf", "dBASE_DEV", "1" },
                    new[] { "AuditLog", "", "Audit log", "[RUN]:audit.txt", "ASCII_DEV", "1" },
                    new[] { "OperLog", "{Date,10}^t{Msg,20}", "", "[RUN]:oper.txt", "ASCII_DEV", "1" },
                    new[] { "Db", "", "", "x", "SQL_DEV", "1" }
                )
            );

        [Fact]
        public void ADbaseDeviceWritesRecordsAndReadsThemBack()
        {
            var s = MakeDevices();
            // (export.ci: the fields for this export, then a record for each sample)
            Assert.Equal(0, Call(s, "DevModify", "Scratch", "{TIME,8}{DATE,10}{PUMP,12}", "", "*", 2).I);
            long h = Call(s, "DevOpen", "Scratch", 1).I;
            Assert.True(h > 0);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(0, Call(s, "DevAppend", h).I);
                Assert.Equal(0, Call(s, "DevSetField", h, "TIME", "10:0" + i).I);
                Assert.Equal(
                    0,
                    Call(
                        s,
                        "DevSetField",
                        h,
                        "PUMP",
                        (i * 1.5).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    ).I
                );
            }
            Assert.Equal(266, Call(s, "DevSetField", h, "NOSUCH", "x").Error);
            Assert.Equal(3, Call(s, "DevSize", h).I);
            Assert.Equal(0, Call(s, "DevClose", h).I);
            Assert.Equal(269, Call(s, "DevClose", h).Error);

            // read back from the dBASE file in the sandbox: first, next, find, end
            h = Call(s, "DevOpen", "Scratch", 4).I;
            Assert.Equal(3, Call(s, "DevSize", h).I);
            Assert.Equal(1, Call(s, "DevRecNo", h).I);
            Assert.Equal("10:00", Call(s, "DevGetField", h, "time").S);
            Call(s, "DevNext", h);
            Assert.Equal("1.5", Call(s, "DevGetField", h, "PUMP").S);
            Assert.Equal(0, Call(s, "DevFind", h, "3.0", "PUMP").I);
            Assert.Equal(3, Call(s, "DevRecNo", h).I);
            Assert.Equal(0, Call(s, "DevEOF", h).I);
            Call(s, "DevNext", h);
            Assert.Equal(1, Call(s, "DevEOF", h).I);
            Assert.Equal(1, Call(s, "DevFind", h, "nope", "PUMP").I);
            // (read only: nothing written)
            Assert.Equal(274, Call(s, "DevAppend", h).Error);
            Assert.Equal("PUMP", Call(s, "DevInfo", h, -3).S);
            Call(s, "DevClose", h);
        }

        [Fact]
        public void ATextDeviceKeepsItsLinesAndHeader()
        {
            var s = MakeDevices();
            // (MultiSignature.ci: one line per approval)
            long h = Call(s, "DevOpen", "AuditLog", 0).I;
            Assert.Equal(0, Call(s, "DevWriteLn", h, "approved by Ann").I);
            Assert.Equal(0, Call(s, "DevWriteLn", h, "approved by Bob").I);
            Call(s, "DevClose", h);
            h = Call(s, "DevOpen", "AuditLog", 0).I;
            Call(s, "DevWriteLn", h, "approved by Cy");
            Assert.Equal("Audit log", Call(s, "DevReadLn", h).S); // (the header, once)
            Assert.Equal("approved by Ann", Call(s, "DevReadLn", h).S);
            Assert.Equal("appr", Call(s, "DevRead", h, 4).S);
            Assert.Equal("oved by Bob", Call(s, "DevReadLn", h).S);
            Assert.Equal("approved by Cy", Call(s, "DevReadLn", h).S);
            Assert.Equal(0, Call(s, "DevEOF", h).I);
            Assert.Equal("", Call(s, "DevReadLn", h).S);
            Assert.Equal(1, Call(s, "DevEOF", h).I);
            Call(s, "DevClose", h);
        }

        [Fact]
        public void ATextDeviceWithAFormatHasFixedWidthRecords()
        {
            var s = MakeDevices();
            long h = Call(s, "DevOpen", "OperLog").I;
            Call(s, "DevAppend", h);
            Call(s, "DevSetField", h, "Date", "19/11/2026");
            Call(s, "DevSetField", h, "Msg", "Pump started");
            Call(s, "DevClose", h);
            h = Call(s, "DevOpen", "OperLog").I;
            Assert.Equal("Pump started", Call(s, "DevGetField", h, "msg").S);
            Assert.Equal("19/11/2026\tPump started        ", Call(s, "DevReadLn", h).S);
            Call(s, "DevClose", h);
        }

        [Fact]
        public void DevicesThatCannotOpenSaySo()
        {
            var s = MakeDevices();
            var r = Call(s, "DevOpen", "NoSuchDevice");
            Assert.Equal(-1, r.I);
            Assert.Equal(289, r.Error);
            r = Call(s, "DevOpen", "Db");
            Assert.Equal(-1, r.I);
            Assert.Equal(262, r.Error); // (no database)
            Assert.Equal(289, Call(s, "DevModify", "NoSuchDevice", "*", "*", "*", -1).Error);
            Assert.Equal(269, Call(s, "DevWriteLn", 999, "x").Error);
        }
    }
}
