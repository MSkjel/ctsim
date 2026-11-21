using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CicodeSim.Builtins;
using CicodeSim.Tests.Vm;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World;
using Xunit;

namespace CicodeSim.Tests.Builtins
{
    /// <summary>Forms, message and input boxes through the dialog host.</summary>
    [Collection("World")]
    public sealed class FormTests : BuiltinTestBase
    {
        /// <summary>A dialog host that records and answers through <see cref="Answer"/>.</summary>
        private sealed class Recorder : IInteractionHost
        {
            public readonly List<Dialog> Shown = new List<Dialog>();
            public readonly List<(int Dialog, int Field)> Changes = new List<(int, int)>();
            public readonly List<int> ClosedIds = new List<int>();
            public readonly List<(int Window, string Text)> Prompts = new List<(int, string)>();
            public Action<Dialog>? Answer;

            public void Show(Dialog d)
            {
                Shown.Add(d);
                Answer?.Invoke(d);
            }

            public void Changed(Dialog d, int field) => Changes.Add((d.Id, field));

            public void Closed(Dialog d) => ClosedIds.Add(d.Id);

            public void Prompt(int window, string text) => Prompts.Add((window, text));
        }

        private Sim Forms(IInteractionHost? host = null)
        {
            var s = Make(pb =>
            {
                pb.Pools.CodeVar("Recipe", Types.String, init: "old");
                pb.Pools.CodeVar("Check", Types.String, init: "0");
                pb.Pools.CodeVar("Pick", Types.String, init: "");
                pb.Pools.CodeVar("CurForm", Types.Long, init: -9);
                pb.Pools.CodeVar("CurField", Types.Long, init: -9);
                // ask(mode): a recipe form; RETURN FormRead(mode) (mode 1: the form handle * 1000 + its result)
                pb.Function("ask", "4", Types.Long)
                    .Begin()
                    .StackVar(Types.Long)
                    .ConstStr("@(Recipe)")
                    .ConstInt(30)
                    .ConstInt(6)
                    .ConstInt(0)
                    .Func("FormNew", 4)
                    .StoreStack(1)
                    .ConstInt(1)
                    .ConstInt(1)
                    .ConstStr("Recipe No")
                    .AddrVar("Recipe", 7)
                    .ConstInt(10)
                    .Func("FormInput", 5)
                    .Op("Pop")
                    .ConstInt(1)
                    .ConstInt(2)
                    .ConstStr("Organic")
                    .AddrVar("Check", 7)
                    .Func("FormCheckBox", 4)
                    .Op("Pop")
                    .ConstInt(1)
                    .ConstInt(3)
                    .ConstInt(10)
                    .ConstInt(2)
                    .AddrVar("Pick", 7)
                    .ConstInt(0)
                    .Func("FormListBox", 6)
                    .Op("Pop")
                    .ConstStr("Apple")
                    .Func("FormAddList", 1)
                    .Op("Pop")
                    .ConstStr("Pear")
                    .Func("FormAddList", 1)
                    .Op("Pop")
                    .ConstInt(1)
                    .ConstInt(5)
                    .ConstStr("Go")
                    .ConstStr("OnGo")
                    .ConstInt(0)
                    .Func("FormButton", 5)
                    .Op("Pop")
                    .ConstInt(10)
                    .ConstInt(5)
                    .ConstStr("OK")
                    .ConstInt(0)
                    .ConstInt(1)
                    .Func("FormButton", 5)
                    .Op("Pop")
                    .LoadStack(0)
                    .Func("FormRead", 1)
                    .LoadStack(1)
                    .ConstInt(1000)
                    .Op("Mul")
                    .Op("Add")
                    .RetValue()
                    .End();
                pb.Function("OnGo")
                    .AddrVar("CurForm", 4)
                    .AddrVar("CurField", 4)
                    .Func("FormCurr", 2)
                    .Op("Pop")
                    .Ret()
                    .End();
                pb.Function("recipe", "", Types.String)
                    .LoadVar("Recipe")
                    .ConstStr("|")
                    .Op("Add")
                    .LoadVar("Check")
                    .Op("Add")
                    .ConstStr("|")
                    .Op("Add")
                    .LoadVar("Pick")
                    .Op("Add")
                    .RetValue()
                    .End();
                pb.Function("curr", "", Types.String)
                    .LoadVar("CurForm")
                    .Convert(Types.String)
                    .ConstStr(",")
                    .Op("Add")
                    .LoadVar("CurField")
                    .Convert(Types.String)
                    .Op("Add")
                    .RetValue()
                    .End();
                pb.Function("active", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("FormActive", 1)
                    .RetValue()
                    .End();
                pb.Function("gettext", "44", Types.String)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .Func("FormGetText", 2)
                    .RetValue()
                    .End();
                pb.Function("settext", "447", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .LoadStack(1)
                    .LoadStack(2)
                    .Func("FormSetText", 3)
                    .RetValue()
                    .End();
                pb.Function("getdata", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("FormGetData", 1)
                    .RetValue()
                    .End();
                pb.Function("destroy", "4", Types.Long)
                    .Begin()
                    .LoadStack(0)
                    .Func("FormDestroy", 1)
                    .RetValue()
                    .End();
                // twice(errset): FormNew twice at once (within 2 s)
                pb.Function("twice", "4", Types.String)
                    .Begin()
                    .LoadStack(0)
                    .Func("ErrSet", 1)
                    .ConstStr("A")
                    .ConstInt(10)
                    .ConstInt(2)
                    .ConstInt(0)
                    .Func("FormNew", 4)
                    .Op("Pop")
                    .ConstStr("B")
                    .ConstInt(10)
                    .ConstInt(2)
                    .ConstInt(0)
                    .Func("FormNew", 4)
                    .Convert(Types.String)
                    .ConstStr(";")
                    .Op("Add")
                    .Func("IsError", 0)
                    .Convert(Types.String)
                    .Op("Add")
                    .RetValue()
                    .End();
                Ret(pb, "badsize", "FormNew", "X", 222, 2, 0);
                Ret(pb, "one", "FormNew", "C", 10, 2, 0);
                pb.Function("msg", "4", Types.Long)
                    .Begin()
                    .ConstStr("Title")
                    .ConstStr("Really?")
                    .LoadStack(0)
                    .Func("Message", 3)
                    .RetValue()
                    .End();
                Ret(pb, "input", "Input", "Value", "Enter it", "7");
                Ret(pb, "open", "FormOpenFile", "Open", "C:\\a.txt", "*.txt", 0);
                Ret(pb, "prompt", "Prompt", "@(Busy)");
                // inst(): FormSetInst / FormGetInst on a form being built
                pb.Function("inst", "", Types.String)
                    .StackVar(Types.Long, Types.Long, Types.String)
                    .ConstStr("I")
                    .ConstInt(10)
                    .ConstInt(2)
                    .ConstInt(0)
                    .Func("FormNew", 4)
                    .StoreStack(0)
                    .ConstInt(0)
                    .ConstInt(0)
                    .ConstStr("p")
                    .Func("FormPrompt", 3)
                    .Op("Pop")
                    .LoadStack(0)
                    .ConstInt(0)
                    .ConstInt(42)
                    .ConstStr("data")
                    .Func("FormSetInst", 4)
                    .Op("Pop")
                    .LoadStack(0)
                    .ConstInt(0)
                    .AddrStack(1)
                    .AddrStack(2)
                    .Func("FormGetInst", 4)
                    .Convert(Types.String)
                    .ConstStr(":")
                    .Op("Add")
                    .LoadStack(1)
                    .Convert(Types.String)
                    .Op("Add")
                    .ConstStr(":")
                    .Op("Add")
                    .LoadStack(2)
                    .Op("Add")
                    .LoadStack(0)
                    .Func("FormDestroy", 1)
                    .Op("Pop")
                    .RetValue()
                    .End();
            });
            if (host != null)
                s.Host.Interaction = host;
            return s;
        }

        [Fact]
        public void HeadlessFormsAreCancelledAndWriteNothing()
        {
            var s = Forms();
            var r = s.Call("ask", 0);
            Assert.Equal("stop", r.Outcome);
            Assert.Equal(299, r.Task.Result!.Value.I % 1000); // FormRead(0): Cancel
            Assert.Equal("old|0|", S(s.Result("recipe")));
            Assert.Contains(s.Log, l => l == "form 'Recipe': cancelled (no UI)");
        }

        [Fact]
        public void ScenarioAnswersWriteTheVariablesOnOkOnly()
        {
            var answers = ScenarioInteraction.FromJson(
                JsonValue.Parse(
                    "{\"forms\": [{\"title\": \"Recipe\", \"fields\": {\"Recipe No\": \"12\", \"Organic\": \"1\", \"#3\": \"Pear\"}},"
                        + " {\"title\": \"recipe\", \"result\": \"cancel\"}]}"
                )
            );
            var s = Forms(answers);
            Assert.Equal(0, s.Result("ask", 0).I % 1000); // OK
            Assert.Equal("12|1|Pear", S(s.Result("recipe")));
            Assert.Equal(299, s.Result("ask", 0).I % 1000); // the second answer: Cancel
            Assert.Equal("12|1|Pear", S(s.Result("recipe")));
            Assert.Equal(299, s.Result("ask", 0).I % 1000); // none left: the fallback cancels
        }

        [Fact]
        public void ShownFormTextsButtonsAndALaterOk()
        {
            var rec = new Recorder();
            var s = Forms(rec);
            long v = s.Result("ask", 1).I; // mode 1: 0 at once, stays shown
            long h = v / 1000;
            Assert.Equal(0, v % 1000);
            var d = rec.Shown.Single();
            Assert.Equal(DialogKind.Form, d.Kind);
            Assert.Equal("Recipe", d.Title); // @(Recipe) translated
            var edit = d.Fields.Single(f => f.Kind == "edit");
            Assert.Equal("Recipe No", edit.Label);
            Assert.Equal("old", edit.Text);
            Assert.Equal(1 + "Recipe No".Length + 2, edit.Col); // after the prompt
            Assert.Equal(new[] { "Apple", "Pear" }, d.Fields.Single(f => f.Kind == "list").Items);
            Assert.Equal(1, s.Result("active", h).I);
            Assert.Equal("old", S(s.Result("gettext", h, edit.Index)));
            d.SetText(edit.Index, "typed");
            Assert.Equal("typed", S(s.Result("gettext", h, edit.Index)));
            Assert.Equal(0, s.Result("settext", h, edit.Index, "set").I);
            Assert.Contains((d.Id, edit.Index), rec.Changes);
            Assert.Equal("set", d.Fields[edit.Index].Text);
            Assert.Equal(269, s.Result("settext", h, 99, "x").I);
            // a button runs its function with FormCurr naming the form and the button
            var go = d.Fields.Single(f => f.Label == "Go");
            d.Press(go.Index);
            s.Host.PumpDialogs();
            s.Run.Scheduler.Run();
            Assert.Equal(h + "," + go.Index, S(s.Result("curr")));
            // FormGetData copies the texts into the variables while shown
            Assert.Equal(0, s.Result("getdata", h).I);
            Assert.Equal("set|0|", S(s.Result("recipe")));
            // the host's OK later: applied at the next pump
            d.Ok(new Dictionary<int, string> { [edit.Index] = "final" });
            s.Host.PumpDialogs();
            Assert.Equal("final|0|", S(s.Result("recipe")));
            Assert.Equal(0, s.Result("active", h).I);
            Assert.Contains(d.Id, rec.ClosedIds);
            Assert.Equal(274, s.Result("destroy", h).I); // gone
        }

        [Fact]
        public void AHostThreadReadsTheFieldsWhileTheCodeChangesThem()
        {
            // FormSetText never changes a list a host enumerates on its UI thread
            var rec = new Recorder();
            var s = Forms(rec);
            long h = s.Result("ask", 1).I / 1000;
            var d = rec.Shown.Single();
            int edit = d.Fields.Single(f => f.Kind == "edit").Index;
            Exception? failed = null;
            bool stop = false;
            var reader = new System.Threading.Thread(() =>
            {
                try
                {
                    while (!System.Threading.Volatile.Read(ref stop))
                        foreach (var f in d.Fields)
                            if (f.Text == null)
                                throw new InvalidOperationException("no text");
                }
                catch (Exception e)
                {
                    failed = e;
                }
            });
            reader.Start();
            for (int i = 0; i < 2000; i++)
                Assert.Equal(0, s.Result("settext", h, edit, "v" + i).I);
            System.Threading.Volatile.Write(ref stop, true);
            reader.Join();
            Assert.Null(failed);
            Assert.Equal("v1999", d.Fields[edit].Text);
        }

        [Fact]
        public void ButtonsOfAWaitingFormRunAndItsOkButtonClosesIt()
        {
            // the waiting task's poll starts the pressed button's function while the scheduler
            // walks its tasks
            var rec = new Recorder();
            var s = Forms(rec);
            var task = s.Run.Scheduler.Spawn("ask", new[] { Value.Long(0) });
            s.Run.Scheduler.Run();
            Assert.Equal(TaskState.Wait, task.State); // FormRead(0) waits
            var d = rec.Shown.Single();
            d.Press(d.Fields.Single(f => f.Label == "Go").Index);
            d.Press(d.Fields.Single(f => f.Label == "OK").Index);
            for (int i = 0; i < 20 && !task.Done; i++)
                s.Run.Scheduler.Run();
            Assert.Equal(TaskState.Stop, task.State);
            Assert.Equal(0, task.Result!.Value.I % 1000); // OK
            long h = task.Result!.Value.I / 1000;
            Assert.Equal(h + "," + d.Fields.Single(f => f.Label == "Go").Index, S(s.Result("curr")));
            Assert.Equal("old|0|", S(s.Result("recipe"))); // OK wrote the variables
        }

        [Fact]
        public void FormNewTwiceWithinTwoSecondsHalts()
        {
            var s = Forms();
            var r = s.Call("twice", 0);
            Assert.Equal(TaskState.Halt, r.Task.State); // raise(274, 1) at ErrSet 0
            Assert.Equal(274, r.Task.HaltCode);
            s = Forms();
            Assert.Equal("-1;274", S(s.Result("twice", 1)));
            // after two seconds the form being built is destroyed and a new one made (handle 0 again)
            s.W.Clock.Advance(2.5);
            Assert.Equal("0", S(s.Result("one")));
            // a bad size: -1 with 274, not halting
            s = Forms();
            var b = s.Call("badsize");
            Assert.Equal("-1", S(b.Task.Result!.Value));
            Assert.Equal(274, b.Task.Error);
        }

        [Fact]
        public void MessageInputPromptAndPickers()
        {
            var rec = new Recorder();
            var s = Forms(rec);
            rec.Answer = d => d.Ok("chosen");
            Assert.Equal(0, s.Result("msg", 1).I); // OK
            Assert.Equal("Really?", rec.Shown.Last().Text);
            Assert.True(rec.Shown.Last().HasCancel);
            rec.Answer = d => d.Cancel();
            Assert.Equal(299, s.Result("msg", 1).I); // Cancel
            var bad = s.Call("msg", 4); // MB_YESNO is refused
            Assert.Equal(0, bad.Task.Result!.Value.I);
            Assert.Equal(274, bad.Task.Error);
            var fg = s.Run.Vm.TaskForCall("msg", new[] { Value.Long(0) }, foreground: true);
            s.Run.Vm.Run(fg);
            Assert.Equal(282, fg.Error); // foreground: raise(282, 0), 0
            Assert.Equal(TaskState.Stop, fg.State);
            Assert.Equal(0, fg.Result!.Value.I);
            rec.Answer = d => d.Ok("typed");
            Assert.Equal("typed", S(s.Result("input")));
            rec.Answer = d => d.Ok();
            Assert.Equal("7", S(s.Result("input"))); // OK without a text: the default
            rec.Answer = d => d.Cancel();
            var c = s.Call("input");
            Assert.Equal("", S(c.Task.Result!.Value));
            Assert.Equal(299, c.Task.Error);
            var fgi = s.Run.Vm.TaskForCall("input", Array.Empty<Value>(), foreground: true);
            s.Run.Vm.Run(fgi);
            Assert.Equal(TaskState.Halt, fgi.State); // 282, halted
            Assert.Equal(282, fgi.HaltCode);
            rec.Answer = d => d.Ok(@"C:\b.txt");
            Assert.Equal(@"C:\b.txt", S(s.Result("open")));
            Assert.Equal(DialogKind.OpenFile, rec.Shown.Last().Kind);
            Assert.Equal("*.txt", rec.Shown.Last().Filter);
            Assert.Equal(0, s.Result("prompt").I);
            Assert.Equal("Busy", rec.Prompts.Last().Text);
            Assert.Equal("0:42:data", S(s.Result("inst")));
        }

        [Fact]
        public void DialogsGoToTheTrace()
        {
            var s = Forms();
            var sw = new StringWriter();
            s.W.OpenTrace(stream: sw);
            s.Call("ask", 0);
            s.W.Trace!.Flush();
            string t = sw.ToString();
            Assert.Contains("\"ev\":\"dialog\"", t);
            Assert.Contains("\"op\":\"show\"", t);
            Assert.Contains("\"result\":\"cancel\"", t);
        }

        [CorpusFact]
        public void IncludeLoginFormTakesTheScenariosAnswer()
        {
            // Include citect.ci LoginForm: FormPrompt + FormInput("", sName) rows, OK / Cancel buttons
            var run = WorldRun.ForProject(Corpus.ExSA, null, "2026-10-01T08:00:00Z");
            var host = WorldBuiltins.HostOf(run.Vm.Builtins)!;
            var r = run.Call("LoginForm", new[] { Value.Str(""), Value.Str(""), Value.Str("") });
            Assert.Equal("stop", r.Outcome);
            Assert.Equal(298, r.Task.Result!.Value.I); // headless: Cancel -> 298
            var rec = new Recorder();
            var answers = ScenarioInteraction.FromJson(
                JsonValue.Parse(
                    "[{\"title\": \"Login Form\", \"fields\": {\"Name\": \"Engineer\", \"Password\": \"pw\"}}]"
                )
            );
            answers.Fallback = rec;
            var shown = new List<Dialog>();
            host.Interaction = new Spy(answers, shown);
            run.Call("LoginForm", new[] { Value.Str(""), Value.Str(""), Value.Str("") }, maxSeconds: 5);
            var login = shown[0];
            Assert.Equal("Login Form", login.Title);
            Assert.Equal("Name", login.Fields.Single(f => f.HasVariable && f.Kind == "edit").Label);
            Assert.Equal("Password", login.Fields.Single(f => f.Kind == "password").Label);
            // OK took the login path: the 'User Login' progress form came next
            Assert.True(
                shown.Count >= 2 && shown[1].Title == "User Login",
                string.Join(", ", shown.Select(d => d.Title))
            );
        }

        private sealed class Spy : IInteractionHost
        {
            private readonly IInteractionHost _inner;
            private readonly List<Dialog> _shown;

            public Spy(IInteractionHost inner, List<Dialog> shown)
            {
                _inner = inner;
                _shown = shown;
            }

            public void Show(Dialog d)
            {
                _shown.Add(d);
                _inner.Show(d);
            }

            public void Changed(Dialog d, int f) => _inner.Changed(d, f);

            public void Closed(Dialog d) => _inner.Closed(d);

            public void Prompt(int w, string t) => _inner.Prompt(w, t);
        }
    }
}
