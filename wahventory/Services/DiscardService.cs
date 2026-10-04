using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.Automation.NeoTaskManager;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Plugin.Services;
using wahventory.Models;
using wahventory.Services.Helpers;

namespace wahventory.Services;

public class DiscardService : IDisposable
{
    private readonly InventoryHelpers _inventoryHelpers;
    private readonly IPluginLog _log;
    private readonly IChatGui _chatGui;
    private readonly IGameGui _gameGui;
    private readonly TaskManager _taskManager;
    
    private List<InventoryItemInfo> _itemsToDiscard = new();
    private int _discardProgress = 0;
    private string? _discardError = null;
    private DateTime _discardStartTime = DateTime.MinValue;
    private DateTime? _yesMissingSince = null;
    private bool _isDiscarding = false;
    
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan NoDialogTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan YesButtonTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ItemGoneTimeout = TimeSpan.FromSeconds(5);
    private const int SettleDelayMs = 100;
    
    private static readonly TaskManagerConfiguration PollConfig = new(timeLimitMS: 60000);
    
    public event Action? OnDiscardStarted;
    public event Action? OnDiscardCompleted;
    public event Action? OnDiscardCancelled;
    public event Action<int, int>? OnDiscardProgress; // current, total
    public event Action<string>? OnDiscardError;
    
    public bool IsDiscarding => _isDiscarding;
    public int DiscardProgress => _discardProgress;
    public int TotalItems => _itemsToDiscard.Count;
    public string? DiscardError => _discardError;
    public List<InventoryItemInfo> ItemsToDiscard => new(_itemsToDiscard);
    
    public DiscardService(
        InventoryHelpers inventoryHelpers,
        IPluginLog log,
        IChatGui chatGui,
        IGameGui gameGui)
    {
        _inventoryHelpers = inventoryHelpers;
        _log = log;
        _chatGui = chatGui;
        _gameGui = gameGui;
        _taskManager = new TaskManager();
    }
    
    public void PrepareDiscard(
        IEnumerable<uint> selectedItemIds,
        IEnumerable<InventoryItemInfo> allItems,
        HashSet<uint> blacklistedItems,
        bool autoStart = false)
    {
        var actualItemsToDiscard = new List<InventoryItemInfo>();

        foreach (var selectedItemId in selectedItemIds)
        {
            var actualItems = allItems.Where(i =>
                i.ItemId == selectedItemId &&
                InventoryHelpers.IsSafeToDiscard(i, blacklistedItems)).ToList();

            _log.Information($"Found {actualItems.Count} instances of item {selectedItemId} to discard");
            actualItemsToDiscard.AddRange(actualItems);
        }

        _itemsToDiscard = actualItemsToDiscard;
        _log.Information($"Items to discard after filtering: {_itemsToDiscard.Count}");

        if (_itemsToDiscard.Count > 0)
        {
            _isDiscarding = true;
            _discardProgress = 0;
            _discardError = null;
            _log.Information("Discard preparation successful");
            OnDiscardStarted?.Invoke();
            if (autoStart)
            {
                StartDiscarding();
            }
        }
        else
        {
            _log.Warning("No items to discard after filtering");
            _chatGui.PrintError("No items selected for discard or all selected items cannot be safely discarded.");
        }
    }
    
    public void StartDiscarding()
    {
        if (_itemsToDiscard.Count == 0)
        {
            _chatGui.PrintError("No items to discard.");
            return;
        }
        
        _log.Information($"StartDiscarding called. Items to discard: {_itemsToDiscard.Count}");
        _taskManager.Abort();
        _taskManager.Enqueue(() => DiscardNextItem());
        _log.Information("Discard task enqueued");
    }
    
    public void CancelDiscard()
    {
        _taskManager.Abort();
        _isDiscarding = false;
        _itemsToDiscard.Clear();
        _discardProgress = 0;
        _discardError = null;
        _yesMissingSince = null;
        _discardStartTime = DateTime.MinValue;
        
        OnDiscardCancelled?.Invoke();
    }
    
    private unsafe void DiscardNextItem()
    {
        _log.Information($"DiscardNextItem called. Progress: {_discardProgress}/{_itemsToDiscard.Count}");
        
        if (_discardProgress >= _itemsToDiscard.Count)
        {
            _chatGui.Print("Finished discarding items.");
            _isDiscarding = false;
            OnDiscardCompleted?.Invoke();
            CancelDiscard();
            return;
        }
        
        var item = _itemsToDiscard[_discardProgress];
        _log.Information($"Attempting to discard item: {item.Name} (ID: {item.ItemId})");
        
        try
        {
            _inventoryHelpers.DiscardItem(item);
            _discardProgress++;
            OnDiscardProgress?.Invoke(_discardProgress, _itemsToDiscard.Count);
            _log.Information($"Discard call completed for {item.Name}");
            
            _discardStartTime = DateTime.Now;
            _yesMissingSince = null;
            
            _taskManager.Enqueue(ConfirmDiscard, PollConfig);
        }
        catch (Exception ex)
        {
            _discardError = $"Failed to discard {item.Name}: {ex.Message}";
            OnDiscardError?.Invoke(_discardError);
            _log.Error(ex, $"Failed to discard {item.Name}");
        }
    }
    
    private unsafe bool ConfirmDiscard()
    {
        var elapsed = DateTime.Now - _discardStartTime;
        if (elapsed > ConfirmTimeout)
        {
            _log.Warning("Discard confirmation timed out");
            _discardError = "Discard confirmation timed out";
            OnDiscardError?.Invoke(_discardError);
            CancelDiscard();
            return true;
        }
        
        var addon = GetDiscardAddon();
        if (addon == null)
        {
            if (elapsed > NoDialogTimeout)
            {
                _log.Warning("No discard dialog found, assuming no confirmation needed");
                _discardStartTime = DateTime.Now;
                _taskManager.Enqueue(WaitForItemGone, PollConfig);
            }
            return elapsed > NoDialogTimeout;
        }
        
        var selectYesno = (AddonSelectYesno*)addon;
        if (selectYesno->YesButton == null)
        {
            _yesMissingSince ??= DateTime.Now;
            if (DateTime.Now - _yesMissingSince > YesButtonTimeout)
            {
                _log.Error("Yes button never appeared in discard dialog");
                _discardError = "Could not find Yes button in dialog";
                OnDiscardError?.Invoke(_discardError);
                CancelDiscard();
                return true;
            }
            return false;
        }
        
        _log.Information("Found discard dialog, clicking Yes");
        selectYesno->YesButton->AtkComponentBase.SetEnabledState(true);
        addon->FireCallbackInt(0);
        
        _discardStartTime = DateTime.Now;
        _taskManager.Enqueue(WaitForItemGone, PollConfig);
        return true;
    }
    
    private unsafe bool WaitForItemGone()
    {
        var item = _itemsToDiscard[_discardProgress - 1];
        var gone = GetDiscardAddon() == null && !IsItemStillInSlot(item);
        var timedOut = DateTime.Now - _discardStartTime > ItemGoneTimeout;
        
        if (!gone && !timedOut)
            return false;
        
        if (timedOut)
            _log.Warning($"{item.Name} still in its slot after discard, continuing anyway");
        
        _taskManager.EnqueueDelay(SettleDelayMs);
        _taskManager.Enqueue(DiscardNextItem);
        return true;
    }
    
    private static unsafe bool IsItemStillInSlot(InventoryItemInfo item)
    {
        var container = InventoryManager.Instance()->GetInventoryContainer(item.Container);
        if (container == null) return false;
        var slot = container->GetInventorySlot(item.Slot);
        return slot != null && slot->ItemId == item.ItemId;
    }
    
    private unsafe AtkUnitBase* GetDiscardAddon()
    {
        for (int i = 1; i < 100; i++)
        {
            try
            {
                var addonPtr = _gameGui.GetAddonByName("SelectYesno", i);
                if (addonPtr.IsNull) break;
                var addon = (AtkUnitBase*)addonPtr.Address;
                if (addon == null) return null;
                
                if (addon->IsVisible && addon->UldManager.LoadedState == AtkLoadState.Loaded)
                {
                    var textNode = addon->UldManager.NodeList[15]->GetAsAtkTextNode();
                    if (textNode != null)
                    {
                        var text = textNode->NodeText.ToString();
                        _log.Verbose($"YesNo dialog text: {text}");
                        
                        if (text.Contains("Discard", StringComparison.OrdinalIgnoreCase) || 
                            text.Contains("discard", StringComparison.OrdinalIgnoreCase))
                        {
                            _log.Verbose("Found discard confirmation dialog");
                            return addon;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, $"Error checking addon {i}");
                return null;
            }
        }
        
        _log.Verbose("No discard dialog found");
        return null;
    }
    
    public void Dispose()
    {
        CancelDiscard();
        _taskManager?.Dispose();
    }
}

