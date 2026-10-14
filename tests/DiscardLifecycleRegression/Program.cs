using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using ECommons.Automation.NeoTaskManager;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using wahventory.Models;
using wahventory.Services;
using wahventory.Services.Helpers;

internal static unsafe class Program
{
    private static int _checks, _scenarios;
    private static void Check(bool value, string reason) { _checks++; if (!value) throw new Exception(reason); }
    private static void Elapsed(DiscardService service, int seconds)
    {
        var field=typeof(DiscardService).GetField("_discardStartTime",BindingFlags.NonPublic|BindingFlags.Instance)
            ?? throw new Exception("Timestamp test hook changed; update harness");
        field.SetValue(service,DateTime.Now-TimeSpan.FromSeconds(seconds));
    }
    private static void YesMissingElapsed(DiscardService service, int seconds)
    {
        var field=typeof(DiscardService).GetField("_yesMissingSince",BindingFlags.NonPublic|BindingFlags.Instance)
            ?? throw new Exception("Timestamp test hook changed; update harness");
        field.SetValue(service,(DateTime?)(DateTime.Now-TimeSpan.FromSeconds(seconds)));
    }
    private static void Main()
    {
        Run("success is acknowledged exactly once", f =>
        {
            f.Start();f.Tasks.Step();
            Check(f.Helpers.DiscardCalls==1,"Discard request issued once");
            Check(f.Service.DiscardProgress==0,"Request alone does not count as confirmed discard");
            Elapsed(f.Service,6);f.Tasks.Step(); // no-dialog fallback queues inventory acknowledgment
            InventoryManager.ItemSlot->ItemId=0;
            f.Tasks.Drain();
            Check(f.Completed==1,"Confirmed inventory removal emits completion");
            Check(f.Cancelled==0,"Successful completion does not also report cancellation");
            Check(f.Errors.Count==0,"Success has no failure event");
            Check(!f.Service.IsDiscarding,"Success releases busy state");
        });
        Run("still-present item times out as failure", f =>
        {
            f.Start();f.Tasks.Step();Elapsed(f.Service,6);f.Tasks.Step();Elapsed(f.Service,6);f.Tasks.Drain();
            f.NoDone("Still-present timeout");
            Check(f.Errors.Count==1,"Still-present timeout emits exactly one error");
            Check(f.Service.DiscardProgress==0,"Unremoved item is never counted as discarded");
        });
        Run("confirmation click still waits for inventory acknowledgment", f =>
        {
            f.Start();f.Tasks.Step();f.ShowDialog(true);var clicked=0;
            AtkUnitBase.OnCallback=()=>{clicked++;f.HideDialog();InventoryManager.ItemSlot->ItemId=0;};
            f.Tasks.Step();
            Check(clicked==1,"Discard confirmation clicks once");
            Check(f.Service.DiscardProgress==0&&f.Completed==0,"Yes click alone does not publish completion");
            f.Tasks.Drain();
            Check(f.Completed==1&&f.Cancelled==0,"Closed confirmation plus acknowledged removal completes once");
        });
        Run("open confirmation is not acknowledged even when slot disappears", f =>
        {
            f.Start();f.Tasks.Step();f.ShowDialog(true);f.Tasks.Step();
            InventoryManager.ItemSlot->ItemId=0;Elapsed(f.Service,6);f.Tasks.Drain();
            f.NoDone("Open confirmation timeout");
            Check(f.Errors.Count==1,"Open confirmation timeout reports failure");
        });
        Run("confirmation timeout is terminal failure", f =>
        {
            f.Start();f.Tasks.Step();Elapsed(f.Service,16);f.Tasks.Drain();
            f.NoDone("Confirmation timeout");
            Check(f.Errors.Count==1,"Confirmation timeout reports exactly once");
            Check(!string.IsNullOrWhiteSpace(f.Service.DiscardError),"Failure reason survives cleanup");
        });
        Run("missing Yes button is terminal failure", f =>
        {
            f.Start();f.Tasks.Step();f.ShowDialog(false);YesMissingElapsed(f.Service,2);f.Tasks.Drain();
            f.NoDone("Missing Yes button");
            Check(f.Errors.Count==1,"Missing Yes button reports once");
        });
        Run("native discard request exception is terminal failure", f =>
        {
            f.Helpers.OnDiscard=_=>throw new InvalidOperationException("Item no longer exists");
            f.Start();f.Tasks.Drain();f.NoDone("Native request failure");
            Check(f.Errors.Count==1,"Native request failure reports exactly once");
        });
        foreach(var unavailable in new[]{"manager","container","slot","unloaded"}) Run("missing "+unavailable+" is not inventory acknowledgment", f =>
        {
            f.Start();f.Tasks.Step();Elapsed(f.Service,6);f.Tasks.Step();
            if(unavailable=="manager")InventoryManager.ManagerAvailable=false;
            if(unavailable=="container")InventoryManager.ContainerAvailable=false;
            if(unavailable=="slot")InventoryManager.SlotAvailable=false;
            if(unavailable=="unloaded") { InventoryManager.ContainerLoaded=false; InventoryManager.ItemSlot->ItemId=0; }
            Elapsed(f.Service,6);f.Tasks.Drain();f.NoDone("Unavailable inventory "+unavailable);
            Check(f.Errors.Count==1,"Unavailable inventory reports once");
        });
        Run("cancellation cannot become completed", f =>
        {
            f.Start();f.Tasks.Step();f.Service.CancelDiscard();f.Tasks.Drain();
            Check(f.Completed==0,"Cancelled operation never reports success");
            Check(f.Cancelled==1,"Cancellation emits once");
            Check(!f.Service.IsDiscarding,"Cancellation releases busy state");
            f.Service.CancelDiscard();
            Check(f.Cancelled==1,"Repeated cancel while idle does not publish a second terminal state");
        });
        Run("busy duplicate prepare cannot replace the active list", f =>
        {
            f.Start();f.Tasks.Step();
            try{f.Service.PrepareDiscard(new uint[]{200},new[]{new InventoryItemInfo{ItemId=200,Name="Other",Container=InventoryType.Inventory1,Slot=1,CanBeDiscarded=true}},new(),true);}catch(InvalidOperationException){}
            Check(f.Service.ItemsToDiscard.Count==1&&f.Service.ItemsToDiscard[0].ItemId==100,"Busy prepare preserves active work");
            Check(f.Helpers.DiscardCalls==1,"Busy prepare cannot start another discard");
            Check(f.Completed==0,"Busy prepare is not completion");
        });
        Run("busy duplicate start cannot skip an in-flight item", f =>
        {
            f.Start();f.Tasks.Step();var aborts=f.Tasks.AbortCount;
            try{f.Service.StartDiscarding();}catch(InvalidOperationException){}
            Check(f.Tasks.AbortCount==aborts,"Repeated start does not abort confirmation");
            Check(f.Service.DiscardProgress==0,"Repeated start does not count an unconfirmed request");
            Elapsed(f.Service,6);f.Tasks.Step();InventoryManager.ItemSlot->ItemId=0;f.Tasks.Drain();
            Check(f.Completed==1&&f.Helpers.DiscardCalls==1,"Duplicate start still acknowledges exactly one actual discard");
        });
        Run("duplicate selected IDs do not duplicate a slot", f =>
        {
            f.Service.PrepareDiscard(new uint[]{100,100},new[]{f.Item},new(),true);
            Check(f.Service.TotalItems==1,"Prepared work deduplicates the physical slot");
            f.Tasks.Step();Elapsed(f.Service,6);f.Tasks.Step();InventoryManager.ItemSlot->ItemId=0;f.Tasks.Drain();
            Check(f.Completed==1&&f.Helpers.DiscardCalls==1,"Duplicate IDs produce one confirmed native discard");
        });
        Run("empty filtered selection has no asynchronous operation", f =>
        {
            var actual=f.Service.PrepareDiscard(new uint[]{100},new[]{f.Item},new HashSet<uint>{100},true);
            Check(actual==0&&!f.Service.IsDiscarding&&!f.Service.HasStarted,"Empty safe selection reports zero without entering busy state");
            Check(f.Completed==0&&f.Cancelled==0&&f.Tasks.Count==0,"Empty selection does not emit an unrelated terminal event");
        });
        Run("failure allows a separately confirmed retry", f =>
        {
            f.Helpers.OnDiscard=_=>throw new InvalidOperationException("Transient native failure");
            f.Start();f.Tasks.Drain();f.NoDone("Initial retry failure");
            f.Helpers.OnDiscard=null;f.Start();
            Check(f.Service.DiscardError==null,"New operation clears the previous failure reason");
            f.Tasks.Step();Elapsed(f.Service,6);f.Tasks.Step();InventoryManager.ItemSlot->ItemId=0;f.Tasks.Drain();
            Check(f.Errors.Count==1&&f.Completed==1,"Retry success is distinct from the earlier failure");
        });
        foreach(var observer in new[]{"start","progress","complete"}) Run("throwing "+observer+" observer cannot strand a successful queue", f =>
        {
            if(observer=="start")f.Service.OnDiscardStarted+=()=>throw new Exception("observer error");
            if(observer=="progress")f.Service.OnDiscardProgress+=(_,_)=>throw new Exception("observer error");
            if(observer=="complete")f.Service.OnDiscardCompleted+=()=>throw new Exception("observer error");
            f.Start();f.Tasks.Step();Elapsed(f.Service,6);f.Tasks.Step();InventoryManager.ItemSlot->ItemId=0;f.Tasks.Drain();
            Check(f.Completed==1&&!f.Service.IsDiscarding,"Observer failure does not strand or falsify success");
            Check(f.Cancelled==0&&f.Errors.Count==0,"Observer failure is not a second terminal result");
        });
        Run("throwing error observer does not strand a failed queue", f =>
        {
            f.Service.OnDiscardError+=_=>throw new Exception("observer error");
            f.Helpers.OnDiscard=_=>throw new Exception("native error");f.Start();f.Tasks.Drain();
            f.NoDone("Throwing error observer");Check(f.Errors.Count==1,"Original failure survives observer error");
        });
        Console.WriteLine($"Discard lifecycle regression passed: {_checks} assertions in {_scenarios} scenarios.");
    }
    private static void Run(string name,Action<Fixture> test)
    {
        _scenarios++;
        using var fixture=new Fixture();
        try{test(fixture);Console.WriteLine("PASS "+name);}catch(Exception ex){throw new Exception("FAIL "+name+": "+ex.Message,ex);}
    }
    private sealed class Fixture : IDisposable
    {
        public readonly InventoryHelpers Helpers=new();
        private readonly StubLog Log=new();
        private readonly StubChat Chat=new();
        private readonly StubGameGui GameGui=new();
        public readonly DiscardService Service;
        public readonly InventoryItemInfo Item=new(){ItemId=100,Name="Allowed low-value fish",Quantity=2,Container=InventoryType.Inventory1,Slot=0,CanBeDiscarded=true};
        public TaskManager Tasks=>TaskManager.Latest!;
        public int Completed,Cancelled;
        public readonly List<string> Errors=new();
        public Fixture()
        {
            InventoryManager.ManagerAvailable=InventoryManager.ContainerAvailable=InventoryManager.SlotAvailable=InventoryManager.ContainerLoaded=true;
            InventoryManager.ItemSlot->ItemId=100;InventoryManager.ItemSlot->Quantity=2;
            AtkUnitBase.OnCallback=null;
            Service=new DiscardService(Helpers,Log,Chat,GameGui);
            Service.OnDiscardCompleted+=()=>Completed++;
            Service.OnDiscardCancelled+=()=>Cancelled++;
            Service.OnDiscardError+=Errors.Add;
        }
        public void Start()=>Service.PrepareDiscard(new uint[]{100},new[]{Item},new(),true);
        public void NoDone(string reason)
        {
            Check(Completed==0,reason+" never reports completion");
            Check(!Service.IsDiscarding,reason+" releases busy state");
        }
        public void ShowDialog(bool yesButton)
        {
            var addon=(AddonSelectYesno*)NativeMemory.AllocZeroed((nuint)sizeof(AddonSelectYesno));
            addon->AtkUnitBase.IsVisible=true;
            addon->AtkUnitBase.UldManager.LoadedState=AtkLoadState.Loaded;
            var nodes=(AtkResNode**)NativeMemory.AllocZeroed((nuint)(16*sizeof(AtkResNode*)));
            nodes[15]=(AtkResNode*)NativeMemory.AllocZeroed((nuint)sizeof(AtkResNode));
            addon->AtkUnitBase.UldManager.NodeList=nodes;
            addon->YesButton=yesButton?(AtkComponentButton*)NativeMemory.AllocZeroed((nuint)sizeof(AtkComponentButton)):null;
            GameGui.Addon=(nint)addon;
        }
        public void HideDialog()=>GameGui.Addon=0;
        public void Dispose()=>Service.Dispose();
    }
    private sealed class StubLog:IPluginLog
    {
        public void Information(string message){}
        public void Warning(string message){}
        public void Warning(Exception exception,string message){}
        public void Error(string message){}
        public void Error(Exception exception,string message){}
        public void Verbose(string message){}
    }
    private sealed class StubChat:IChatGui {public void Print(string message){}public void PrintError(string message){}}
    private sealed class StubGameGui:IGameGui
    {
        public nint Addon;
        public AddonPointer GetAddonByName(string name,int index)=>new(index==1?Addon:0);
    }
}
