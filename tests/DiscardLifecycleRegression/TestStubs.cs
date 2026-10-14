using System.Runtime.InteropServices;
using wahventory.Models;

namespace ECommons.Automation.NeoTaskManager
{
    public sealed class TaskManagerConfiguration
    {
        public TaskManagerConfiguration(int timeLimitMS) { }
    }
    public sealed class TaskManager : IDisposable
    {
        public static TaskManager? Latest { get; private set; }
        private readonly LinkedList<Func<bool>> _queue = new();
        public int AbortCount { get; private set; }
        public int Count => _queue.Count;
        public TaskManager() => Latest = this;
        public void Enqueue(Action action) => _queue.AddLast(() => { action(); return true; });
        public void Enqueue(Func<bool> action, TaskManagerConfiguration configuration) => _queue.AddLast(action);
        public void EnqueueDelay(int milliseconds) => Enqueue(() => { });
        public void Abort() { AbortCount++; _queue.Clear(); }
        public void Step()
        {
            var node = _queue.First ?? throw new InvalidOperationException("No queued task");
            var done = node.Value();
            if (done && node.List == _queue) _queue.Remove(node);
        }
        public void Drain(int limit = 20)
        {
            for (var i = 0; _queue.Count > 0 && i < limit; i++) Step();
            if (_queue.Count > 0) throw new InvalidOperationException("Queue did not settle");
        }
        public void Dispose() => Abort();
    }
}
namespace Dalamud.Plugin.Services
{
    public interface IPluginLog
    {
        void Information(string message);
        void Warning(string message);
        void Warning(Exception exception, string message);
        void Error(string message);
        void Error(Exception exception, string message);
        void Verbose(string message);
    }
    public interface IChatGui { void Print(string message); void PrintError(string message); }
    public readonly struct AddonPointer(nint address)
    {
        public bool IsNull => address == 0;
        public nint Address => address;
    }
    public interface IGameGui { AddonPointer GetAddonByName(string name, int index); }
}
namespace FFXIVClientStructs.FFXIV.Client.Game
{
    public enum InventoryType { Inventory1, Inventory2, Inventory3, Inventory4 }
    public unsafe struct InventoryItem
    {
        public uint ItemId;
        public uint Quantity;
        public enum ItemFlags { HighQuality = 1 }
        public ItemFlags Flags;
    }
    public unsafe struct InventoryContainer
    {
        public int Size => 20;
        public bool IsLoaded => InventoryManager.ContainerLoaded;
        public InventoryItem* GetInventorySlot(int index) => InventoryManager.SlotAvailable ? InventoryManager.ItemSlot : null;
    }
    public unsafe struct InventoryManager
    {
        public static InventoryManager* Manager = (InventoryManager*)NativeMemory.AllocZeroed((nuint)sizeof(InventoryManager));
        public static InventoryContainer* Container = (InventoryContainer*)NativeMemory.AllocZeroed((nuint)sizeof(InventoryContainer));
        public static InventoryItem* ItemSlot = (InventoryItem*)NativeMemory.AllocZeroed((nuint)sizeof(InventoryItem));
        public static bool ManagerAvailable = true, ContainerAvailable = true, SlotAvailable = true, ContainerLoaded = true;
        public static InventoryManager* Instance() => ManagerAvailable ? Manager : null;
        public InventoryContainer* GetInventoryContainer(InventoryType type) => ContainerAvailable ? Container : null;
    }
}
namespace FFXIVClientStructs.FFXIV.Component.GUI
{
    public enum AtkLoadState { Unloaded, Loaded }
    public struct NodeText
    {
        public static string Text = "Discard item?";
        public override string ToString() => Text;
    }
    public struct AtkTextNode { public NodeText NodeText; }
    public unsafe struct AtkResNode
    {
        public static AtkTextNode* TextNode = (AtkTextNode*)NativeMemory.AllocZeroed((nuint)sizeof(AtkTextNode));
        public AtkTextNode* GetAsAtkTextNode() => TextNode;
    }
    public unsafe struct AtkUldManager { public AtkLoadState LoadedState; public AtkResNode** NodeList; }
    public struct AtkComponentBase { public void SetEnabledState(bool enabled) { } }
    public struct AtkComponentButton { public AtkComponentBase AtkComponentBase; }
    public unsafe struct AtkUnitBase
    {
        public bool IsVisible;
        public AtkUldManager UldManager;
        public static Action? OnCallback;
        public void FireCallbackInt(int value) => OnCallback?.Invoke();
    }
}
namespace FFXIVClientStructs.FFXIV.Client.UI
{
    using FFXIVClientStructs.FFXIV.Component.GUI;
    public unsafe struct AddonSelectYesno { public AtkUnitBase AtkUnitBase; public AtkComponentButton* YesButton; }
}
namespace wahventory.Services.Helpers
{
    public sealed class InventoryHelpers
    {
        public Action<InventoryItemInfo>? OnDiscard;
        public int DiscardCalls;
        public static bool IsSafeToDiscard(InventoryItemInfo item, HashSet<uint> blacklist) => item.CanBeDiscarded && !blacklist.Contains(item.ItemId);
        public void DiscardItem(InventoryItemInfo item) { DiscardCalls++; OnDiscard?.Invoke(item); }
    }
}
