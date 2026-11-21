using System.Collections.Generic;
using System.Threading;
using CicodeSim.Builtins;
using CicodeSim.Host;
using CicodeSim.Values;
using Xunit;

namespace CicodeSim.Tests.Host
{
    /// <summary>ViewerDialogs without a server: a dialog that times out or whose
    /// viewers went is closed for the viewer even when a task waits for it (the task may be gone),
    /// and the shown strings are localised.</summary>
    public sealed class ViewerDialogsTests
    {
        private sealed class Sent
        {
            public readonly List<(string Name, JsonValue Body)> Events = new List<(string, JsonValue)>();

            public void Add(string n, JsonValue b)
            {
                lock (Events)
                    Events.Add((n, b));
            }

            public int Count(string n)
            {
                lock (Events)
                    return Events.FindAll(e => e.Name == n).Count;
            }
        }

        [Fact]
        public void ATimedOutDialogIsClosedForTheViewerEvenWithAWaitingTask()
        {
            var sent = new Sent();
            var vd = new ViewerDialogs(HeadlessInteraction.Instance, () => true, sent.Add) { TimeoutMs = 50 };
            var d = new Dialog
            {
                Kind = DialogKind.Message,
                Title = "T",
                Text = "x",
                Task = 12,
            }; // a task waits (it may be gone)
            vd.Show(d);
            Assert.Equal(1, sent.Count("ctsim/dialog"));
            // (a busy thread pool during the full run can start the 50 ms timer seconds late)
            for (int i = 0; i < 500 && sent.Count("ctsim/dialogClosed") == 0; i++)
                Thread.Sleep(20);
            Assert.True(d.Answered);
            Assert.Equal(1, sent.Count("ctsim/dialogClosed"));
            Assert.Empty(vd.Open());
            // the waiting task's own close later is harmless (no second dialogClosed)
            vd.Closed(d);
            Assert.Equal(1, sent.Count("ctsim/dialogClosed"));
        }

        [Fact]
        public void CancelAllClosesEveryShownDialog()
        {
            var sent = new Sent();
            var vd = new ViewerDialogs(HeadlessInteraction.Instance, () => true, sent.Add);
            var a = new Dialog
            {
                Kind = DialogKind.Input,
                Title = "A",
                Task = 3,
            };
            var b = new Dialog { Kind = DialogKind.Message, Title = "B" };
            vd.Show(a);
            vd.Show(b);
            vd.CancelAll();
            Assert.True(a.Answered && b.Answered);
            Assert.Equal(2, sent.Count("ctsim/dialogClosed"));
            Assert.Empty(vd.Open());
        }

        [Fact]
        public void ShownStringsAreLocalisedValuesAreNot()
        {
            var d = new Dialog
            {
                Kind = DialogKind.Input,
                Title = "@(Recipe)",
                Text = "@(Name)",
                Default = "@(kept)",
            };
            var j = ViewerDialogs.ToJson(d, s =>
                global::CicodeSim.World.Display.DisplayLanguage.Translate(s, n =>
                    n == "Recipe" ? "Rezept" : null
                )
            );
            Assert.Equal("Rezept", j["title"].Str);
            Assert.Equal("Name", j["text"].Str);
            Assert.Equal("Name", j["fields"].Items[0]["label"].Str);
            Assert.Equal("@(kept)", j["fields"].Items[0]["value"].Str);
            var m = new Dialog
            {
                Kind = DialogKind.PopupMenu,
                Menu = new List<MenuItem>
                {
                    new MenuItem { Label = "@(Open)", Id = 1 },
                },
            };
            Assert.Equal(
                "Open",
                ViewerDialogs
                    .ToJson(m, s => global::CicodeSim.World.Display.DisplayLanguage.Translate(s, null))[
                        "items"
                    ]
                    .Items[0]["label"]
                    .Str
            );
        }
    }
}
