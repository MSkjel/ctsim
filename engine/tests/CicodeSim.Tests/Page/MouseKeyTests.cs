using System.Linq;
using CicodeSim.Page;
using CicodeSim.Sim;
using CicodeSim.Tests.Host;
using CicodeSim.Values;
using Xunit;

namespace CicodeSim.Tests.Page
{
    /// <summary>Mouse buttons are keys.</summary>
    public class MouseKeyTests
    {
        [ProjectFact]
        public void ARightClickRunsAnRButtonUpCommand()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                // a page whose object (AN 7) has a right-button keyboard command (no stock page has one)
                var p = new PageFixture("RightClickPage");
                p.Row(
                    "PGKEY",
                    ("AN", 7L),
                    ("KEY_SEQ", "RBUTTON_UP"),
                    ("CMD", p.Command(c => c.ConstInt(1).Op("Pop")))
                );
                var page = p.Build();
                Assert.Equal(
                    0,
                    sim.Invoke(() => sim.Pages.EnsureAnimator().Open("RightClickPage", 0, compiled: page))
                );
                var key = sim.Invoke(() =>
                    sim
                        .Pages.EnsureAnimator()
                        .Run(0)!
                        .Keys.FirstOrDefault(k =>
                            k.An > 0 && k.Sequence.Trim().ToUpperInvariant() == "RBUTTON_UP"
                        )
                );
                Assert.NotNull(key);
                var a = JsonValue.NewObject();
                a.Set("kind", "click");
                a.Set("button", "right");
                a.Set("an", JsonValue.Of(key!.An));
                var body = sim.Invoke(() => sim.Pages.Input(0, a));
                Assert.True(body["ran"].Bool, "the RBUTTON_UP command of AN " + key.An + " did not run");
            }
        }

        [Fact]
        public void ThePlainButtonCodesAreTheRuntimes()
        {
            Assert.Equal(0x0202, KeyTable.RButtonUp);
            Assert.Equal(0x0802, KeyTable.RButtonDown);
            Assert.Equal(0x0402, KeyTable.RButtonDbl);
            Assert.Equal(0x0204, KeyTable.MButtonUp);
            Assert.Equal(0x0804, KeyTable.MButtonDown);
        }
    }
}
