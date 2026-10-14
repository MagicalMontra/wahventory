using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Inventory;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using wahventory.Modules.Inventory;

namespace Dalamud.Game.Text
{
    public enum SeIconChar { BoxedLetterA=0xE071, BoxedLetterW=0xE087 }
}
namespace Dalamud.Game.Text.SeStringHandling
{
    public sealed record SeString(string Text)
    {
        public static implicit operator SeString(string value)=>new(value);
        public override string ToString()=>Text;
    }
}
namespace Dalamud.Game.Inventory
{
    public enum GameInventoryType { Inventory1=0,Inventory2=1,Inventory3=2,Inventory4=3,ArmoryMainHand=3500,ArmoryHead=3201,Retainer=10000,SaddleBag=4000 }
    public readonly record struct GameInventoryItem(uint ItemId,uint BaseItemId,GameInventoryType ContainerType,bool IsEmpty=false,uint InventorySlot=0);
}
namespace FFXIVClientStructs.FFXIV.Client.Game
{
    public enum InventoryType { Inventory1=0,Inventory2=1,Inventory3=2,Inventory4=3,ArmoryMainHand=3500,ArmoryHead=3201 }
}
namespace Dalamud.Game.Gui.ContextMenu
{
    public enum ContextMenuType { Default,Inventory }
    public abstract class MenuTarget;
    public sealed class MenuTargetInventory : MenuTarget { public GameInventoryItem? TargetItem {get;set;} }
    public sealed class MenuTargetDefault : MenuTarget;
    public interface IMenuItem
    {
        SeString Name {get;set;}
        SeIconChar? Prefix {get;set;}
        char? PrefixChar {set;}
        ushort PrefixColor {get;set;}
        bool UseDefaultPrefix {get;set;}
        Action<IMenuItemClickedArgs>? OnClicked {get;set;}
        int Priority {get;set;}
        bool IsEnabled {get;set;}
        bool IsSubmenu {get;set;}
    }
    public sealed record MenuItem : IMenuItem
    {
        public SeString Name {get;set;}="";
        public SeIconChar? Prefix {get;set;}
        public char? PrefixChar {set {if(value is { } c){if(!char.IsAsciiLetterUpper(c))throw new ArgumentException();Prefix=(SeIconChar)((int)SeIconChar.BoxedLetterA+c-'A');}else Prefix=null;}}
        public ushort PrefixColor {get;set;}
        public bool UseDefaultPrefix {get;set;}
        public Action<IMenuItemClickedArgs>? OnClicked {get;set;}
        public int Priority {get;set;}
        public bool IsEnabled {get;set;}=true;
        public bool IsSubmenu {get;set;}
    }
    public interface IMenuArgs {ContextMenuType MenuType {get;} MenuTarget? Target {get;} }
    public interface IMenuOpenedArgs : IMenuArgs {void AddMenuItem(MenuItem item);}
    public interface IMenuItemClickedArgs : IMenuArgs {void OpenSubmenu(SeString name,IReadOnlyList<IMenuItem> items);}
}
namespace Dalamud.Plugin.Services
{
    public interface IContextMenu
    {
        public delegate void OnMenuOpenedDelegate(IMenuOpenedArgs args);
        event OnMenuOpenedDelegate? OnMenuOpened;
    }
    public interface IDataManager {FakeSheet<T>? GetExcelSheet<T>() where T:struct;}
    public interface IPluginLog {void Error(Exception error,string message);}
    public interface IChatGui {void Print(string message);void PrintError(string message);}
}
namespace Lumina.Excel.Sheets
{
    public readonly record struct TestText(string Value) {public string ExtractText()=>Value;}
    public readonly record struct Item(uint RowId,TestText Name);
}
namespace wahventory.Core
{
    public interface IGameServices {IDataManager DataManager {get;} IPluginLog Log {get;} IChatGui ChatGui {get;} }
}
namespace wahventory.Services.Helpers
{
    public static class InventoryHelpers
    {
        public static readonly FFXIVClientStructs.FFXIV.Client.Game.InventoryType[] MainInventories=[0,(FFXIVClientStructs.FFXIV.Client.Game.InventoryType)1,(FFXIVClientStructs.FFXIV.Client.Game.InventoryType)2,(FFXIVClientStructs.FFXIV.Client.Game.InventoryType)3];
        public static bool IsArmoryContainer(FFXIVClientStructs.FFXIV.Client.Game.InventoryType type)=>type is FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryMainHand or FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryHead;
    }
}
namespace wahventory.Modules.Inventory
{
    public sealed class FakeDiscardService {public bool IsDiscarding {get;set;} }
    public sealed class InventoryManagementModule
    {
        public bool ListsLocked {get;set;}
        public FakeDiscardService DiscardService {get;}=new();
        public HashSet<uint> BlacklistedItems {get;}=[];
        public HashSet<uint> AutoDiscardItems {get;}=[];
        public List<(bool Blacklist,uint ItemId)> Calls {get;}=[];
        public bool ThrowOnAdd {get;set;}
        public InventoryListAddResult? ForceResult {get;set;}
        public int AutoDiscardExecutions {get;private set;}
        public void ExecuteAutoDiscard()=>AutoDiscardExecutions++;
        public InventoryListAddResult AddItemToAutoDiscard(uint id)
        {
            Calls.Add((false,id));if(ThrowOnAdd)throw new IOException("Save failed");
            return ForceResult??InventoryListPolicy.AddToAutoDiscard(id,BlacklistedItems,AutoDiscardItems,ListsLocked,DiscardService.IsDiscarding);
        }
        public InventoryListAddResult AddItemToBlacklist(uint id)
        {
            Calls.Add((true,id));if(ThrowOnAdd)throw new IOException("Save failed");
            return ForceResult??InventoryListPolicy.AddToBlacklist(id,BlacklistedItems,AutoDiscardItems,ListsLocked,DiscardService.IsDiscarding);
        }
    }
}
public sealed class FakeSheet<T> where T:struct
{
    public Dictionary<uint,T> Rows {get;}=[];
    public bool ThrowOnRead {get;set;}
    public T? GetRowOrDefault(uint id){if(ThrowOnRead)throw new IOException("Sheet failed");return Rows.TryGetValue(id,out var item)?item:null;}
}
public sealed class FakeData : IDataManager
{
    public FakeSheet<Item>? Items {get;set;}=new();
    public FakeSheet<T>? GetExcelSheet<T>() where T:struct=>(FakeSheet<T>?)(object?)Items;
}
public sealed class FakeLog : IPluginLog
{
    public List<string> Errors {get;}=[];
    public void Error(Exception error,string message)=>Errors.Add(message+": "+error.Message);
}
public sealed class FakeChat : IChatGui
{
    public List<string> Messages {get;}=[];
    public List<string> Errors {get;}=[];
    public void Print(string message)=>Messages.Add(message);
    public void PrintError(string message)=>Errors.Add(message);
}
public sealed class FakeServices : wahventory.Core.IGameServices
{
    public FakeData Data {get;}=new();public FakeLog Logger {get;}=new();public FakeChat Chat {get;}=new();
    public IDataManager DataManager=>Data;public IPluginLog Log=>Logger;public IChatGui ChatGui=>Chat;
}
public sealed class FakeContext : IContextMenu
{
    private IContextMenu.OnMenuOpenedDelegate? _handlers;
    public int Subscribers {get;private set;}
    public event IContextMenu.OnMenuOpenedDelegate? OnMenuOpened {add{_handlers+=value;Subscribers++;}remove{_handlers-=value;Subscribers--;}}
    public void Open(FakeOpened args)=>_handlers?.Invoke(args);
}
public sealed class FakeOpened : IMenuOpenedArgs
{
    public ContextMenuType MenuType {get;init;}=ContextMenuType.Inventory;
    public MenuTarget? Target {get;init;}
    public List<MenuItem> Items {get;}=[];
    public bool ThrowOnAdd {get;set;}
    public void AddMenuItem(MenuItem item){if(ThrowOnAdd)throw new IOException("Menu add failed");Items.Add(item);}
}
public sealed class FakeClicked : IMenuItemClickedArgs
{
    public ContextMenuType MenuType {get;init;}=ContextMenuType.Inventory;
    public MenuTarget? Target {get;init;}
    public string? Title {get;private set;}
    public IReadOnlyList<IMenuItem>? Items {get;private set;}
    public bool ThrowOnOpen {get;set;}
    public void OpenSubmenu(SeString name,IReadOnlyList<IMenuItem> items){if(ThrowOnOpen)throw new IOException("Open failed");Title=name.ToString();Items=items;}
}
