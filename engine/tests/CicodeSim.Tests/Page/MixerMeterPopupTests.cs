using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CicodeSim.Page;
using CicodeSim.Tests.Vm;
using CicodeSim.World.Display;
using Xunit;
using Xunit.Abstractions;

namespace CicodeSim.Tests.Page
{
    /// <summary>Mixer meters and pumps open their super genie popups.</summary>
    [Collection("World")]
    public class MixerMeterPopupTests
    {
        private static readonly string Ex = Path.Combine(Corpus.User, "Example");
        private readonly ITestOutputHelper _out;

        public MixerMeterPopupTests(ITestOutputHelper o)
        {
            _out = o;
        }

        private static PageModel Fixture(string name) =>
            PageModel.Load(Path.Combine(AppContext.BaseDirectory, "Page", "Fixtures", name));

        private static Window? Popup(Animator a, string page)
        {
            foreach (var w in a.Display.Windows)
                if (
                    w.Open
                    && w.Number != 0
                    && string.Equals(w.Page?.Name, page, StringComparison.OrdinalIgnoreCase)
                )
                    return w;
            return null;
        }

        /// <summary>The value texts of a window: none shows '#ASC' (an unassociated ?name?).</summary>
        private List<ObjectState> Values(Animator a, Window w)
        {
            var texts = new List<ObjectState>();
            foreach (var s in a.Run(w.Number)!.State.Objects.Values)
                if (s.Text != null || s.Error != null)
                {
                    texts.Add(s);
                    _out.WriteLine(
                        "  AN " + s.An + ": " + s.Text + (s.Error != null ? " (" + s.Error + ")" : "")
                    );
                }
            return texts;
        }

        [CorpusFact(Needs = "engine/tests/CicodeSim.Tests/Page/Fixtures/Example_Mixer_meters.page.json")]
        public void PowerMeterTouchOpensThePM800PopupBoundAndTitled()
        {
            var a = Animator.ForProject(Ex);
            Assert.Equal(0, a.Open("Mixer", 0, Fixture("Example_Mixer_meters.page.json")));
            a.RunScans(2, 0);
            var w0 = a.Display.OpenWindow(0)!;
            // the pointer over the meter's kW text (301, 473, 325, 488): the cursor goes there
            a.Mouse(0, 313, 480);
            Assert.Equal(939, w0.CommandCursor);
            a.Touch(0, 939, Animator.TouchKind.Down);
            Assert.True(a.Touch(0, 939, Animator.TouchKind.Up));
            a.RunFor(4.0);
            var pop = Popup(a, "!PM800Detail");
            Assert.NotNull(pop);
            Assert.Equal(19, pop!.Associations.Count);
            Assert.Equal(@"Raw_SkimMilkOutPump_PM800\MMXU1\TotW", pop.Associations["ToTW"].Tag);
            Assert.False(pop.Associations["ToTW"].Literal);
            Assert.True(pop.Associations["PM800_Equip"].Literal);
            Assert.Equal("Plant.Mixer.SkimMilk.OutletPump.PM800", pop.Associations["PM800_Equip"].Tag);
            var values = Values(a, pop);
            Assert.True(values.Count > 5, "value objects " + values.Count);
            Assert.DoesNotContain(values, s => s.Text == "#ASC" || s.Error == "#ASC");
            // WinTitle(EquipGetProperty(...PM800, "Comment", 0) + " @(Power Meter)") ran on the
            // popup (WinNewAt made it the task's window) and the scans keep it
            Assert.Equal("Mixer Skim Milk Out Pump @(Power Meter)", pop.Title);
            Assert.Equal("@(Plant Mixer)", w0.Title);
        }

        [CorpusFact(Needs = "engine/tests/CicodeSim.Tests/Page/Fixtures/Example_Mixer_meters.page.json")]
        public void PumpTouchWithoutAMouseMoveOpensEnergyStartStopBound()
        {
            var a = Animator.ForProject(Ex);
            Assert.Equal(0, a.Open("Mixer", 0, Fixture("Example_Mixer_meters.page.json")));
            a.RunScans(2, 0);
            var w0 = a.Display.OpenWindow(0)!;
            Assert.Equal(-1, w0.CommandCursor);
            // a click that comes without a pointer position: the touch puts the cursor on its object
            a.Touch(0, 524, Animator.TouchKind.Down);
            Assert.True(a.Touch(0, 524, Animator.TouchKind.Up));
            Assert.Equal(524, w0.CommandCursor);
            a.RunFor(4.0);
            var pop = Popup(a, "!EnergyStartStop");
            Assert.NotNull(pop);
            Assert.Equal("Mixer_Line1Pump_ON", pop!.Associations["1"].Tag);
            Assert.True(pop.Associations["DevName"].Literal);
            Assert.Equal("Mixer_Line1Pump_ACC", pop.Associations["DevName"].Tag);
            var values = Values(a, pop);
            Assert.DoesNotContain(values, s => s.Text == "#ASC" || s.Error == "#ASC");
        }

        /// <summary>A WinTitle title stays over the page scans until a page is displayed in the
        /// window (window +100).</summary>
        [CorpusFact(Needs = "engine/tests/CicodeSim.Tests/Page/Fixtures/Example_Mixer_meters.page.json")]
        public void WinTitleStaysUntilThePageChanges()
        {
            var a = Animator.ForProject(Ex);
            Assert.Equal(0, a.Open("Mixer", 0, Fixture("Example_Mixer_meters.page.json")));
            a.RunScans(1, 0);
            var w0 = a.Display.OpenWindow(0)!;
            // what _WinTitle leaves (the built-in itself: the PM800 test above)
            w0.Title = "Custom";
            w0.TitleSet = true;
            a.RunScans(3, 0);
            Assert.Equal("Custom", w0.Title);
            Assert.Equal(0, a.Open("Mixer", 0, Fixture("Example_Mixer_meters.page.json")));
            a.RunScans(1, 0);
            Assert.Equal("@(Plant Mixer)", w0.Title);
        }
    }
}
