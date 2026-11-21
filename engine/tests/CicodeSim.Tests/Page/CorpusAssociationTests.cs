using System.IO;
using CicodeSim.Compiled;
using CicodeSim.Page;
using CicodeSim.Tests.Vm;
using CicodeSim.Values;
using CicodeSim.Vm;
using CicodeSim.World.Display;
using Xunit;

namespace CicodeSim.Tests.Page
{
    /// <summary>Association functions as the example projects use them.</summary>
    [Collection("World")]
    public class CorpusAssociationTests
    {
        private static readonly string Example = Path.Combine(Corpus.User, "Example");

        private static Value At(Animator a, int window, string name, params object[] args)
        {
            var b = Func0Table.Default.Lookup(name)!;
            var t = new CicodeTask(a.Vm, "test", false);
            TaskContext.Set(t, window, -1);
            var vals = new Value[args.Length];
            for (int i = 0; i < args.Length; i++)
                vals[i] = CicodeVm.ValueOf(args[i]);
            return a.Vm.Builtins!.Call(b.Index, t, ArgList.Of(vals));
        }

        private static readonly string ExampleSA = Path.Combine(Corpus.User, "ExampleSA");

        /// <summary>Ass and AssInfo answer as the live runtime did.</summary>
        [CorpusFact]
        public void AssAndAssInfoAnswerAsTheLiveRuntime()
        {
            if (!File.Exists(Path.Combine(ExampleSA, "FP_PMP_HD1080.RDB")))
                return;
            using var a = Animator.ForProject(ExampleSA);
            // the store's tag check: an equipment and a dotted unknown name 289, a plain unknown
            // name 424, a local variable 0, the same local variable with a cluster prefix 424 (the
            // check does not find local variables), a literal 0 whatever the mode
            Assert.Equal(289, a.Display.Ass(-2, "__EquipmentRef", "Company.TopMilk.Mixing.PMP05", 0));
            Assert.Equal(289, a.Display.Ass(-2, "__EquipmentName", "Company.TopMilk.Mixing.PMP05", 0));
            Assert.Equal(0, a.Display.Ass(-2, "LV", "SA_DisableLabel", 0));
            Assert.Equal(424, a.Display.Ass(-2, "LC", "Cluster1.SA_DisableLabel", 0));
            Assert.Equal(424, a.Display.Ass(-2, "NV", "NoSuchTag", 0));
            Assert.Equal(289, a.Display.Ass(-2, "ND", "No.Such", 0));
            Assert.Equal(0, a.Display.Ass(-2, "LIT", "'42'", 1));
            Assert.Equal(0, a.Open("FP_PMP_HD1080", 0));
            a.RunScans(1, 0);
            string Info(string arg, string type) => At(a, 0, "AssInfo", arg, type).S ?? "";
            Assert.Equal("Cluster1.Company.TopMilk.Mixing.PMP05", Info("?__EquipmentRef?", "19"));
            Assert.Equal("", Info("?__EquipmentRef?", "0"));
            Assert.Equal("", Info("?__EquipmentRef?", "17"));
            Assert.Equal("", Info("?__EquipmentRef?", "18"));
            Assert.Equal("TopMilk_PMP05_PV", Info("?__EquipmentRef?.PV", "0"));
            Assert.Equal("Cluster1.TopMilk_PMP05_PV", Info("?__EquipmentRef?.PV", "18"));
            Assert.Equal("DegC", Info("?__EquipmentRef?.PV", "1"));
            // a local variable has no cluster
            Assert.Equal("SA_DisableLabel", Info("?LV?", "0"));
            Assert.Equal("", Info("?LV?", "17"));
            Assert.Equal("SA_DisableLabel", Info("?LV?", "18"));
            Assert.Equal("SA_DisableLabel", Info("?LV?", "19"));
            Assert.Equal("", At(a, 0, "AssGetProperty", "?LV?", "ClusterName").S);
            Assert.Equal("SA_DisableLabel", At(a, 0, "AssGetProperty", "?LV?", "FullName").S);
            // the local variable named with a cluster: no subscription, its 424 blanks 0 / 17
            Assert.Equal("", Info("?LC?", "0"));
            Assert.Equal("", Info("?LC?", "17"));
            Assert.Equal("Cluster1.SA_DisableLabel", Info("?LC?", "19"));
            Assert.Equal("", Info("?NV?", "0"));
            Assert.Equal("Cluster1.NoSuchTag", Info("?NV?", "19"));
            Assert.Equal("Cluster1.No.Such", Info("?ND?", "19"));
            Assert.Equal("42", Info("?LIT?", "0"));
            Assert.Equal("x42y", Info("x?LIT?y", "0"));
            Assert.Equal("", Info("?LIT?", "17"));
            Assert.Equal("", Info("?Missing?", "0"));
            // AssScaleStr / AssGetScale = TagScaleStr of the reference: the same text
            Assert.Equal(
                At(a, 0, "TagScaleStr", "TopMilk_PMP05_PV", 50, 1).S,
                At(a, 0, "AssScaleStr", "?__EquipmentRef?.PV", 50, 1).S
            );
            Assert.Equal(
                At(a, 0, "TagScaleStr", "TopMilk_PMP05_PV", 50, 1).S,
                At(a, 0, "AssGetScale", "?__EquipmentRef?.PV", 50, 1).S
            );
            // (live, not yet the simulator's: the FORMAT-less tag's format is 0x200206 = 6.2, so
            // TagScaleStr / AssScaleStr ' 75.00 DegC', AssInfo 6 / 7 / 8 = '6' / '2' / '2097670';
            // the simulated tag database uses '####.#')
        }

        [CorpusFact]
        public void ThePidSuperGenieChainsItsEquipmentToTheLoopTunePopup()
        {
            if (!File.Exists(Path.Combine(Example, "!PID.RDB")))
                return;
            using var a = Animator.ForProject(Example);
            // an equipment association: 289 from the store's tag check, stored all the same
            Assert.Equal(289, a.Display.Ass(-2, "PID_EQUIP", "Loops.Level", 0));
            Assert.Equal(0, a.Open("!PID", 0));
            a.RunScans(1, 0);
            // Example has one cluster: AssResolve's AssInfo(19) answers it with the equipment
            Assert.Equal("Cluster1.Loops.Level", At(a, 0, "AssInfo", "?PID_EQUIP?", "19").S);
            Assert.Equal("Cluster1.Loops.Level", a.Vm.Call("AssResolve", "?PID_EQUIP?")!.Value.S);
            Assert.Equal("", At(a, 0, "AssInfo", "?PID_EQUIP?", "0").S);
            // the page's 'AssChainPopUp("!LoopTune")': Include tag.ci's AssChainWin chains window
            // 0's associations to -2 and opens the popup, which takes them
            a.Vm.Call("AssChainPopUp", "!LoopTune");
            int popup = -1;
            for (int w = 1; w < a.Display.Windows.Length; w++)
                if (a.Display.Windows[w].Open && a.Display.Windows[w].Page?.Name == "!LoopTune")
                    popup = w;
            Assert.True(popup > 0, "the popup opened");
            var e = a.Display.Windows[popup].Associations["PID_EQUIP"];
            Assert.Equal("Loops.Level", e.Tag);
            Assert.Equal("Cluster1", e.Cluster);
            Assert.Equal(289, e.Error);
            Assert.Equal("Cluster1.Loops.Level", At(a, popup, "AssInfo", "PID_EQUIP", "AssFullName").S);
            Assert.DoesNotContain(a.Display.Pending, p => p.Arg == "PID_EQUIP");
            // Include's AssPage: numbered arguments ('1') for the window, then the page change
            a.Vm.Call("AssPage", "!PID", "Loops.Temperature");
            Assert.Equal("Loops.Temperature", a.Display.Windows[0].Associations["1"].Tag);
        }
    }
}
