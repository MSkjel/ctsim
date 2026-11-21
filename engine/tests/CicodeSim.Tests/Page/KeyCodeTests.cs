using System.Collections.Generic;
using CicodeSim.Page;
using CicodeSim.Sim;
using CicodeSim.Tests.Host;
using Xunit;

namespace CicodeSim.Tests.Page
{
    /// <summary>Key codes as the runtime makes and matches them.</summary>
    public class KeyCodeTests
    {
        private static int Label(Simulation sim, string name) =>
            sim.Invoke(() => (int)(sim.World.Unit.LabelValue(name) ?? -1));

        [ProjectFact]
        public void KeysAreCodedAsTheRuntimeCodesThem()
        {
            using (var sim = SimTestUtil.Start(SimCorpus.Example))
            {
                var anim = sim.Invoke(() => sim.Pages.EnsureAnimator());
                int Code(string key, string text, int mods) =>
                    sim.Invoke(() => SimPages.KeyCode(anim, key, text, mods));
                int Typed(string key, string text, int mods) => KeyMatch.Normalize(Code(key, text, mods));

                // a letter: its character (the matcher compares it upper-cased)
                Assert.Equal(0x66, Code("F", "f", 0));
                Assert.Equal('F', Typed("F", "f", 0));
                // Shift / Ctrl / Alt with a letter: the lower-case character + the modifier bits,
                // compared upper-cased: the KEY_x_SHIFT / _CTRL / _ALT labels
                Assert.Equal(0x1066, Code("F", "F", 1));
                Assert.Equal(Label(sim, "KEY_F_SHIFT"), Typed("F", "F", 1));
                Assert.Equal(0x2073, Code("S", "\u0013", 2));
                Assert.Equal(Label(sim, "KEY_S_CTRL"), Typed("S", "\u0013", 2));
                Assert.Equal(Label(sim, "KEY_F_ALT"), Typed("F", "f", 4));
                Assert.Equal(0x3066, Code("F", "\u0006", 3)); // Ctrl+Shift
                // the keys coded on key-down: the virtual key + 0x100 and the bits
                Assert.Equal(Label(sim, "KEY_F1"), Code("F1", "", 0));
                Assert.Equal(Label(sim, "KEY_F1_SHIFT"), Code("F1", "", 1));
                Assert.Equal(Label(sim, "KEY_F12_CTRL"), Code("F12", "", 2));
                Assert.Equal(Label(sim, "KEY_PGUP_CTRL"), Code("PgUp", "", 2));
                Assert.Equal(Label(sim, "KEY_UP_SHIFT"), Code("Up", "", 1));
                Assert.Equal(Label(sim, "KEY_SNAPSHOT_CTRL"), Code("Print", "", 2));
                // Enter, Escape, Backspace, Tab are characters
                Assert.Equal(Label(sim, "KEY_ENTER"), Code("Return", "\r", 0));
                Assert.Equal(0x100D, Code("Return", "\r", 1));
                Assert.Equal(0x200D, Code("Return", "\n", 2)); // (Ctrl+Enter types LF)
                Assert.Equal(0x400D, Code("Return", "\r", 4));
                Assert.Equal(0x1009, Code("Backtab", "", 1)); // Shift+Tab
                Assert.Equal(Label(sim, "KEY_ESC"), Code("Escape", "\u001b", 0));
                // Ctrl with a digit, Ctrl+Alt with a letter: coded on key-down
                Assert.Equal(0x2031, Code("1", "", 2));
                Assert.Equal(0x6066, Code("F", "", 6));
                // a scenario names the code
                Assert.Equal(Label(sim, "KEY_ENTER"), Code("ENTER", "", 0));
                Assert.Equal(Label(sim, "KEY_F_SHIFT"), Code("KEY_F_SHIFT", "", 0));
                Assert.Equal(-1, Code("NoSuchKeyName", "", 0));
            }
        }

        [Fact]
        public void TheMatcherComparesTheTypedKeyNormalised()
        {
            int sep = ',';
            // KEY_F_SHIFT typed as Shift+F (0x1066)
            Assert.Equal(
                KeyMatch.Result.Complete,
                KeyMatch.Match(new List<int> { 0x1046 }, new List<int> { 0x1066 }, sep, out _)
            );
            // KEY_S_CTRL typed as Ctrl+S (0x2073)
            Assert.Equal(
                KeyMatch.Result.Complete,
                KeyMatch.Match(new List<int> { 0x2053 }, new List<int> { 0x2073 }, sep, out _)
            );
            // a plain letter in either case
            Assert.Equal(
                KeyMatch.Result.Complete,
                KeyMatch.Match(new List<int> { 'A' }, new List<int> { 'a' }, sep, out _)
            );
            // Shift+F is not F
            Assert.Equal(
                KeyMatch.Result.None,
                KeyMatch.Match(new List<int> { 'F' }, new List<int> { 0x1066 }, sep, out _)
            );
            // '#' data then Enter
            var r = KeyMatch.Match(
                new List<int> { '#', '#', 0x0D },
                new List<int> { '4', '2', 0x0D },
                sep,
                out string[] args
            );
            Assert.Equal(KeyMatch.Result.Complete, r);
            Assert.Equal("42", args[0]);
        }
    }
}
