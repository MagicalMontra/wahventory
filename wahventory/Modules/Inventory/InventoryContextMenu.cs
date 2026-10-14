using System;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using wahventory.Core;
using wahventory.Services.Helpers;

namespace wahventory.Modules.Inventory;

internal sealed class InventoryContextMenu : IDisposable
{
    private readonly IContextMenu _contextMenu;
    private readonly InventoryManagementModule _inventory;
    private readonly IGameServices _services;
    private bool _disposed;

    public InventoryContextMenu(IContextMenu contextMenu, InventoryManagementModule inventory, IGameServices services)
    {
        _contextMenu = contextMenu;
        _inventory = inventory;
        _services = services;
        _contextMenu.OnMenuOpened += OnMenuOpened;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (_disposed) return;
        try
        {
            if (args.MenuType != ContextMenuType.Inventory
                || args.Target is not MenuTargetInventory { TargetItem: { } item }
                || item.IsEmpty || item.BaseItemId == 0)
                return;

            var container = (InventoryType)item.ContainerType;
            if (Array.IndexOf(InventoryHelpers.MainInventories, container) < 0
                && !InventoryHelpers.IsArmoryContainer(container))
                return;

            // Capture the base ID now: the live context target can change before a click.
            var itemId = item.BaseItemId;
            var row = _services.DataManager.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
            if (row == null || row.Value.RowId == 0)
                return;
            var itemName = row.Value.Name.ExtractText();

            args.AddMenuItem(new MenuItem
            {
                Name = "Wahventory",
                PrefixChar = 'W',
                Priority = int.MaxValue,
                IsSubmenu = true,
                OnClicked = clicked => OpenSubmenu(clicked, itemId, itemName),
            });
        }
        catch (Exception ex)
        {
            _services.Log.Error(ex, "Could not add the Wahventory inventory context menu");
        }
    }

    private void OpenSubmenu(IMenuItemClickedArgs args, uint itemId, string itemName)
    {
        if (_disposed) return;
        try
        {
            var canEdit = !_inventory.ListsLocked && !_inventory.DiscardService.IsDiscarding;
            args.OpenSubmenu("Wahventory", new IMenuItem[]
            {
                new MenuItem
                {
                    Name = "Add to wahventory discard list",
                    UseDefaultPrefix = false,
                    Priority = 0,
                    IsEnabled = canEdit && !_inventory.BlacklistedItems.Contains(itemId)
                        && !_inventory.AutoDiscardItems.Contains(itemId),
                    OnClicked = _ => AddToList(itemId, itemName, blacklist: false),
                },
                new MenuItem
                {
                    Name = "Add to wahventory blacklist",
                    UseDefaultPrefix = false,
                    Priority = 1,
                    IsEnabled = canEdit && !_inventory.BlacklistedItems.Contains(itemId),
                    OnClicked = _ => AddToList(itemId, itemName, blacklist: true),
                },
            });
        }
        catch (Exception ex)
        {
            _services.Log.Error(ex, "Could not open the Wahventory inventory submenu");
        }
    }

    private void AddToList(uint itemId, string itemName, bool blacklist)
    {
        if (_disposed) return;
        try
        {
            // Recheck policy at click time even if the menu was opened before a list change.
            var result = blacklist
                ? _inventory.AddItemToBlacklist(itemId)
                : _inventory.AddItemToAutoDiscard(itemId);
            var listName = blacklist ? "blacklist" : "discard list";
            switch (result)
            {
                case InventoryListAddResult.Added:
                    _services.ChatGui.Print($"[wahventory] Added {itemName} to the {listName}.");
                    break;
                case InventoryListAddResult.AlreadyPresent:
                    _services.ChatGui.Print($"[wahventory] {itemName} is already on the {listName}.");
                    break;
                case InventoryListAddResult.Blacklisted:
                    _services.ChatGui.PrintError($"[wahventory] {itemName} is blacklisted. Remove it from the blacklist before adding it to the discard list.");
                    break;
                case InventoryListAddResult.ListsLocked:
                    _services.ChatGui.PrintError("[wahventory] List sync is enabled. Disable list sync before editing lists.");
                    break;
                case InventoryListAddResult.Busy:
                    _services.ChatGui.PrintError("[wahventory] Wait until the current discard finishes before editing lists.");
                    break;
                case InventoryListAddResult.InvalidItem:
                    _services.ChatGui.PrintError("[wahventory] This item cannot be added to a list.");
                    break;
            }
        }
        catch (Exception ex)
        {
            _services.Log.Error(ex, "Could not update a Wahventory item list");
            _services.ChatGui.PrintError("[wahventory] Could not save the list change. Check the plugin log.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _contextMenu.OnMenuOpened -= OnMenuOpened;
    }
}
