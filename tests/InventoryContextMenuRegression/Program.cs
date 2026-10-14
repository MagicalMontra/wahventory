using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Inventory;
using Dalamud.Game.Text;
using Lumina.Excel.Sheets;
using wahventory.Modules.Inventory;

var checks=0;var scenarios=0;
void Check(bool value,string description){checks++;if(!value)throw new Exception(description);}
void Scenario(string name,Action<Fixture> test)
{
    scenarios++;using var f=new Fixture();
    try{test(f);Console.WriteLine("PASS "+name);}catch(Exception error){throw new Exception(name+": "+error.Message,error);}
}
Scenario("only valid inventory and armory targets are offered",f=>
{
    foreach(var args in new[]{
        new FakeOpened{MenuType=ContextMenuType.Default,Target=f.Target()},
        new FakeOpened{Target=new MenuTargetDefault()},
        new FakeOpened{Target=null},
        new FakeOpened{Target=new MenuTargetInventory()},
        new FakeOpened{Target=f.Target(isEmpty:true)},
        new FakeOpened{Target=f.Target(baseId:0)},
        new FakeOpened{Target=f.Target(container:GameInventoryType.Retainer)},
        new FakeOpened{Target=f.Target(container:GameInventoryType.SaddleBag)},
        new FakeOpened{Target=f.Target(baseId:999)},
    }){f.Context.Open(args);Check(args.Items.Count==0,"Unsupported target cannot receive submenu");}
    foreach(var container in new[]{GameInventoryType.Inventory1,GameInventoryType.Inventory2,GameInventoryType.Inventory3,GameInventoryType.Inventory4,GameInventoryType.ArmoryMainHand,GameInventoryType.ArmoryHead})
        Check(f.Open(f.Target(container:container)).Items.Count==1,"Supported inventory/armory target receives one parent");
    Check(f.Inventory.Calls.Count==0&&f.Inventory.AutoDiscardExecutions==0,"Menu opening never edits or discards");
});
Scenario("W prefix, bottom priority and ordered unprefixed children",f=>
{
    var parent=f.Open().Items.Single();
    Check(parent.Name.ToString()=="Wahventory","Correct parent label");
    Check(parent.Prefix==SeIconChar.BoxedLetterW,"Parent uses native boxed W");
    Check(parent.Priority==int.MaxValue&&parent.IsSubmenu&&parent.IsEnabled,"Parent is enabled submenu with bottom priority");
    var click=new FakeClicked();parent.OnClicked!(click);
    Check(click.Title=="Wahventory"&&click.Items?.Count==2,"Parent opens exactly two submenu actions");
    var children=click.Items!;
    Check(children[0].Name.ToString()=="Add to wahventory discard list"&&children[0].Priority==0,"Discard is first");
    Check(children[1].Name.ToString()=="Add to wahventory blacklist"&&children[1].Priority==1,"Blacklist is second");
    Check(children.All(x=>x.Prefix==null&&!x.UseDefaultPrefix&&!x.IsSubmenu&&x.IsEnabled),"Children have no prefix or submenu and are initially enabled");
    Check(f.Inventory.Calls.Count==0,"Opening child menu does not mutate a list");
});
foreach(var raw in new uint[]{100,1_000_100,500_100})Scenario("captured base item survives retargeting: "+raw,f=>
{
    var original=f.Target(raw:raw);var parent=f.Open(original).Items.Single();
    original.TargetItem=new GameInventoryItem(200,200,GameInventoryType.Inventory1);
    var click=new FakeClicked{Target=f.Target(baseId:200,raw:200)};parent.OnClicked!(click);
    click.Items![0].OnClicked!(new FakeClicked{Target=f.Target(baseId:200,raw:200)});
    Check(f.Inventory.Calls.Single()==(false,100u),"Callback uses captured base ID, not raw offset or live target");
    Check(f.Inventory.AutoDiscardItems.SetEquals([100u]),"Only clicked item identity is added");
    Check(f.Services.Chat.Messages.Single().Contains("Fish 100"),"Name is captured with the same item");
    Check(f.Inventory.AutoDiscardExecutions==0,"Adding list membership never executes discard");
});
foreach(var state in new[]{"busy","locked","blacklisted","duplicate"})Scenario("disabled state: "+state,f=>
{
    if(state=="busy")f.Inventory.DiscardService.IsDiscarding=true;
    if(state=="locked")f.Inventory.ListsLocked=true;
    if(state=="blacklisted")f.Inventory.BlacklistedItems.Add(100);
    if(state=="duplicate")f.Inventory.AutoDiscardItems.Add(100);
    var children=f.Children();
    Check(!children[0].IsEnabled,"Discard item disabled for conflicting/blocked state");
    Check(children[1].IsEnabled==(state=="duplicate"),"Only existing discard membership permits the blacklist action");
    Check(f.Inventory.Calls.Count==0&&f.Inventory.AutoDiscardExecutions==0,"Disabled state opens without action");
});
foreach(var state in new[]{"busy","locked","blacklisted","duplicate"})Scenario("click rechecks policy changed after opening: "+state,f=>
{
    var child=f.Children()[0];Check(child.IsEnabled,"Action begins enabled");
    if(state=="busy")f.Inventory.DiscardService.IsDiscarding=true;
    if(state=="locked")f.Inventory.ListsLocked=true;
    if(state=="blacklisted")f.Inventory.BlacklistedItems.Add(100);
    if(state=="duplicate")f.Inventory.AutoDiscardItems.Add(100);
    child.OnClicked!(new FakeClicked());
    Check(f.Inventory.Calls.Count==1,"Stale action rechecks through shared policy method");
    Check(f.Inventory.AutoDiscardItems.Count==(state=="duplicate"?1:0),"Blocked stale click cannot add membership");
    Check(state=="duplicate"?f.Services.Chat.Messages.Single().Contains("already"):f.Services.Chat.Errors.Count==1,"Reason is reported without throwing");
    Check(f.Inventory.AutoDiscardExecutions==0,"Failed stale click does not discard");
});
Scenario("blacklist action replaces existing discard membership",f=>
{
    f.Inventory.AutoDiscardItems.Add(100);var child=f.Children()[1];Check(child.IsEnabled,"Blacklist action remains available for discard-listed item");
    child.OnClicked!(new FakeClicked());
    Check(f.Inventory.Calls.Single()==(true,100u),"Blacklist callback invokes shared blacklist method");
    Check(f.Inventory.BlacklistedItems.Contains(100)&&!f.Inventory.AutoDiscardItems.Contains(100),"Blacklist wins over discard membership");
    Check(f.Services.Chat.Messages.Single().Contains("blacklist"),"Blacklist success reported");
    Check(f.Inventory.AutoDiscardExecutions==0,"Blacklist action never initiates discard");
});
Scenario("parent click reads current edit policy",f=>
{
    var parent=f.Open().Items.Single();f.Inventory.ListsLocked=true;
    var click=new FakeClicked();parent.OnClicked!(click);
    Check(click.Items!.All(x=>!x.IsEnabled),"Submenu evaluates policy when actually opened");
});
Scenario("invalid, duplicate and blocked blacklist clicks report their result",f=>
{
    foreach(var result in new[]{InventoryListAddResult.AlreadyPresent,InventoryListAddResult.Blacklisted,InventoryListAddResult.ListsLocked,InventoryListAddResult.Busy,InventoryListAddResult.InvalidItem})
    {
        f.Inventory.ForceResult=result;var before=f.Services.Chat.Messages.Count+f.Services.Chat.Errors.Count;
        f.Children()[1].OnClicked!(new FakeClicked());
        Check(f.Services.Chat.Messages.Count+f.Services.Chat.Errors.Count==before+1,"Each policy result reports exactly one message");
    }
});
Scenario("menu and save exceptions are isolated",f=>
{
    f.Services.Data.Items!.ThrowOnRead=true;Check(f.Open().Items.Count==0,"Sheet failure does not leak a menu entry");
    f.Services.Data.Items.ThrowOnRead=false;
    var badOpen=new FakeOpened{Target=f.Target(),ThrowOnAdd=true};f.Context.Open(badOpen);
    var parent=f.Open().Items.Single();parent.OnClicked!(new FakeClicked{ThrowOnOpen=true});
    f.Inventory.ThrowOnAdd=true;f.Children()[0].OnClicked!(new FakeClicked());
    Check(f.Services.Logger.Errors.Count==4,"All four error paths are logged");
    Check(f.Services.Chat.Errors.Count==1,"Save failure shows a useful message");
    Check(f.Inventory.AutoDiscardExecutions==0,"Exception cannot initiate discard");
});
Scenario("unload unsubscribes context callback",f=>
{
    Check(f.Context.Subscribers==1,"One subscription on construction");f.Menu.Dispose();
    Check(f.Context.Subscribers==0,"Subscription removed on dispose");
    f.Menu.Dispose();
    Check(f.Context.Subscribers==0,"Repeated disposal does not remove another subscription");
    Check(f.Open().Items.Count==0,"No new entries appear after disposal");
    f.SkipDispose=true;
});
Scenario("cached parent and child callbacks cannot act after unload",f=>
{
    var parent=f.Open().Items.Single();
    var children=f.Children();
    f.Menu.Dispose();
    var reopened=new FakeClicked();
    parent.OnClicked!(reopened);
    Check(reopened.Items==null&&reopened.Title==null,"Cached parent cannot open a submenu after disposal");
    children[0].OnClicked!(new FakeClicked());
    children[1].OnClicked!(new FakeClicked());
    Check(f.Inventory.Calls.Count==0,"Cached child callbacks cannot invoke list mutation after disposal");
    Check(f.Inventory.AutoDiscardItems.Count==0&&f.Inventory.BlacklistedItems.Count==0,"Disposed callbacks leave both lists unchanged");
    Check(f.Inventory.AutoDiscardExecutions==0,"Disposed callbacks cannot initiate discard");
    Check(f.Services.Chat.Messages.Count==0&&f.Services.Chat.Errors.Count==0,"Disposed callbacks do not report spurious results");
    Check(f.Services.Logger.Errors.Count==0,"Disposed callbacks are safely ignored");
    f.SkipDispose=true;
});
Console.WriteLine($"Inventory context-menu regression passed: {checks} assertions in {scenarios} scenarios.");

internal sealed class Fixture : IDisposable
{
    public FakeContext Context {get;}=new();public InventoryManagementModule Inventory {get;}=new();public FakeServices Services {get;}=new();
    public InventoryContextMenu Menu {get;}
    public bool SkipDispose {get;set;}
    public Fixture(){Services.Data.Items!.Rows.Add(100,new Item(100,new TestText("Fish 100")));Services.Data.Items.Rows.Add(200,new Item(200,new TestText("Fish 200")));Menu=new InventoryContextMenu(Context,Inventory,Services);}
    public MenuTargetInventory Target(uint baseId=100,uint raw=100,GameInventoryType container=GameInventoryType.Inventory1,bool isEmpty=false)=>new(){TargetItem=new GameInventoryItem(raw,baseId,container,isEmpty)};
    public FakeOpened Open(MenuTargetInventory? target=null){var args=new FakeOpened{Target=target??Target()};Context.Open(args);return args;}
    public IReadOnlyList<IMenuItem> Children(){var clicked=new FakeClicked();Open().Items.Single().OnClicked!(clicked);return clicked.Items!;}
    public void Dispose(){if(!SkipDispose)Menu.Dispose();}
}
