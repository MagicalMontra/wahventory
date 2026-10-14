using System;
using System.Collections.Generic;
using System.Linq;
using wahventory.Core;
using wahventory.Models;
using wahventory.Services;
using wahventory.Services.Helpers;

namespace wahventory.Modules.Inventory;

public class InventoryManagementModule : IDisposable
{
    private readonly Plugin _plugin;
    private readonly IGameServices _services;
    private readonly InventoryHelpers _inventoryHelpers;
    private readonly IconCache _iconCache;

    // Services
    private readonly ItemFilterService _filterService;
    private readonly ItemSearchService _searchService;
    private readonly PriceService _priceService;
    public readonly DiscardService DiscardService;
    public readonly ArmoryMoveService MoveService;
    private readonly PassiveDiscardService _passiveDiscardService;

    // UI
    private readonly InventoryUIRenderer _ui;
    internal InventoryListsWindow ListsWindow { get; }
    internal InventorySettingsWindow SettingsWindow { get; }
    internal InventoryFilteredItemsWindow FilteredItemsWindow { get; }
    internal ListSyncReviewWindow ListSyncReviewWindow { get; }
    internal ListSyncController ListSync { get; }

    // State
    private bool _initialized = false;
    internal readonly InventoryState _state = new();

    public HashSet<uint> BlacklistedItems { get; private set; }
    public HashSet<uint> AutoDiscardItems { get; private set; }

    internal bool ListsLocked => Settings.ListSync.Enabled;

    internal string _searchFilter = string.Empty;
    internal bool _showArmory = false;
    internal string _selectedWorld = "";
    internal List<string> _availableWorlds = new();

    private DateTime _lastRefresh = DateTime.MinValue;
    private readonly TimeSpan _refreshInterval = TimeSpan.FromSeconds(1);
    private bool _expandedCategoriesChanged = false;
    private DateTime _lastConfigSave = DateTime.MinValue;
    private readonly TimeSpan _configSaveInterval = TimeSpan.FromSeconds(2);
    private bool _windowIsOpen = false;
    private long _appliedPriceVersion = -1;
    private string? _lastPlayerWorld;

    internal InventorySettings Settings => _plugin.Configuration.InventorySettings;
    internal Dictionary<uint, bool> ExpandedCategories => Settings.ExpandedCategories;
    internal bool HasActivePriceFilters => Settings.SafetyFilters.FilterMarketPriceAbove || Settings.SafetyFilters.FilterMarketPriceBelow;
    private bool NeedsMarketPrices => Settings.ShowMarketPrices || HasActivePriceFilters;
    internal bool ArePriceFiltersReady => NeedsMarketPrices && _priceService.IsRefreshComplete;
    internal bool CanDiscardWithPriceFilters => !HasActivePriceFilters || ArePriceFiltersReady;
    internal string PriceRefreshStatus => _priceService.IsRefreshing
        ? $"Refreshing prices {_priceService.CompletedCount}/{_priceService.TotalCount}"
        : ArePriceFiltersReady ? "Price refresh complete"
        : "Enable market prices to refresh";

    public InventoryManagementModule(Plugin plugin, IGameServices services)
    {
        _plugin = plugin;
        _services = services;
        _inventoryHelpers = new InventoryHelpers(_services.DataManager, _services.Log);
        _iconCache = new IconCache(_services.TextureProvider);

        // Initialize services
        _filterService = new ItemFilterService();
        _searchService = new ItemSearchService(_services.DataManager, _services.Log);
        _priceService = new PriceService(_services.Log, Settings, "Excalibur");
        DiscardService = new DiscardService(
            _inventoryHelpers,
            _services.Log,
            _services.ChatGui,
            _services.GameGui);
        DiscardService.OnDiscardCompleted += OnInventoryMutated;
        MoveService = new ArmoryMoveService(_services.Log, _services.ChatGui);
        MoveService.OnMoveCompleted += (_, _) => OnInventoryMutated();
        _passiveDiscardService = new PassiveDiscardService(
            _services.ClientState,
            _services.ObjectTable,
            _services.Condition,
            _services.GameGui,
            _services.Log,
            Settings);

        BlacklistedItems = _plugin.ConfigManager.LoadBlacklist();
        AutoDiscardItems = _plugin.ConfigManager.LoadAutoDiscard();
        if (InventoryListPolicy.Normalize(BlacklistedItems, AutoDiscardItems) > 0)
            _plugin.ConfigManager.SaveAutoDiscard(AutoDiscardItems);
        _selectedWorld = "Excalibur";

        PopulateAvailableWorlds();
        InitializeWorld();

        ListsWindow = new InventoryListsWindow(this, _state, _searchService, _iconCache);
        SettingsWindow = new InventorySettingsWindow(this, _passiveDiscardService, _state);
        FilteredItemsWindow = new InventoryFilteredItemsWindow(this, _state, _searchService, _iconCache);

        ListSync = new ListSyncController(this, _plugin.ConfigManager, _services.Log, _services.ChatGui);
        ListSyncReviewWindow = new ListSyncReviewWindow(this, ListSync, _searchService, _iconCache);
        ListSync.PendingReviewRequested += () => ListSyncReviewWindow.IsOpen = true;

        _ui = new InventoryUIRenderer(this, _filterService, _searchService, _priceService, _passiveDiscardService, _iconCache, ListsWindow, SettingsWindow, FilteredItemsWindow);
    }

    internal void SaveConfig() => _plugin.ConfigManager.SaveConfiguration();
    internal void MarkExpansionChanged() => _expandedCategoriesChanged = true;
    
    private void InitializeWorld()
    {
        try
        {
            var currentWorld = _services.PlayerState.IsLoaded ? _services.PlayerState.CurrentWorld.Value.Name.ToString() : null;
            if (!string.IsNullOrEmpty(currentWorld))
            {
                _selectedWorld = currentWorld;
                _lastPlayerWorld = currentWorld;
                _priceService.UpdateWorld(_selectedWorld, FindWorldId(_selectedWorld));
            }
        }
        catch
        {
            // Ignore errors during initialization
        }
    }
    
    private void PopulateAvailableWorlds()
    {
        _availableWorlds.Clear();
        
        var worldSheet = _services.DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>();
        if (worldSheet != null)
        {
            try
            {
                var hasPlayer = _services.PlayerState.IsLoaded;
                var currentWorld = hasPlayer ? (Lumina.Excel.Sheets.World?)_services.PlayerState.CurrentWorld.Value : null;
                var worldName = currentWorld?.Name.ExtractText() ?? "Aether";

                if (currentWorld != null)
                {
                    try
                    {
                        var worldRow = worldSheet.FirstOrDefault(w => w.Name.ExtractText() == worldName);
                        if (worldRow.RowId > 0)
                        {
                            try
                            {
                                var currentDatacenterId = worldRow.DataCenter.RowId;
                                
                                var datacenterWorlds = worldSheet
                                    .Where(w => w.DataCenter.RowId == currentDatacenterId && w.IsPublic)
                                    .Select(w => w.Name.ExtractText())
                                    .Where(name => !string.IsNullOrEmpty(name))
                                    .OrderBy(w => w)
                                    .ToList();
                                
                                _availableWorlds = datacenterWorlds;
                            }
                            catch
                            {
                                _availableWorlds = new List<string> { worldName };
                            }
                        }
                        else
                        {
                            _availableWorlds = new List<string> { worldName };
                        }
                    }
                    catch
                    {
                        _availableWorlds = new List<string> { worldName };
                    }
                }
                else
                {
                    _availableWorlds = new List<string> { worldName };
                }
            }
            catch (Exception ex)
            {
                _services.Log.Warning($"Failed to get datacenter worlds: {ex.Message}");
                _availableWorlds = new List<string> { "Aether" };
            }
        }
    }
    
    public void Initialize()
    {
        if (_initialized)
            return;
        
        InitializeWorld();
        RefreshInventory();
        _initialized = true;
    }
    
    public void Update()
    {
        if (!_initialized)
        {
            Initialize();
        }
        
        // Update price service world if changed
        try
        {
            var currentWorld = _services.PlayerState.IsLoaded ? _services.PlayerState.CurrentWorld.Value.Name.ToString() : null;
            if (!string.IsNullOrEmpty(currentWorld) && currentWorld != _lastPlayerWorld)
            {
                _lastPlayerWorld = currentWorld;
                ChangePriceWorld(currentWorld);
            }
        }
        catch
        {
            // Ignore
        }
        
        // Auto-refresh prices
        if (_windowIsOpen && NeedsMarketPrices && Settings.AutoRefreshPrices && !DiscardService.IsDiscarding && !_priceService.IsRefreshing &&
            DateTime.Now - _lastRefresh > _refreshInterval)
        {
            _lastRefresh = DateTime.Now;

            // Fetch from the inventory snapshot, including rows hidden by a price filter.
            if (_priceService.GetItemsNeedingPriceFetch(_state.SnapshotOriginalItems(), 1).Count > 0)
                RefreshPrices();
        }

        ApplyLatestPrices();

        _windowIsOpen = false;

        // Save config if categories changed
        if (_expandedCategoriesChanged && DateTime.Now - _lastConfigSave > _configSaveInterval)
        {
            _plugin.ConfigManager.SaveConfiguration();
            _expandedCategoriesChanged = false;
            _lastConfigSave = DateTime.Now;
        }

        ListSync.Update();

        // Update passive discard
        if (Settings.PassiveDiscard.Enabled && CanDiscardWithPriceFilters)
            _passiveDiscardService.Update(
                AutoDiscardItems,
                GetDiscardEligibleItems(synchronizePrices: false),
                BlacklistedItems,
                ExecuteAutoDiscard);
    }
    
    public void Draw()
    {
        _windowIsOpen = true;
        if (!_initialized)
        {
            Initialize();
        }
        _ui.Draw();
    }

    private void OnInventoryMutated()
    {
        _state.ClearSelectionAndReset();
        RefreshInventory();
    }

    internal void RefreshInventory(bool requireAvailable = false, bool forcePriceRefresh = false)
    {
        _state.DeselectItems(BlacklistedItems);
        var newItems = _inventoryHelpers.GetAllItems(_showArmory, false, requireAvailable);
        if (NeedsMarketPrices)
            _priceService.BeginRefresh(newItems, forcePriceRefresh);

        _state.ApplyRefreshAndRecategorize(
            newItems,
            initEachItem: item =>
            {
                item.SafetyAssessment = InventoryHelpers.AssessItemSafety(item, Settings, BlacklistedItems);
                _priceService.UpdateItemPrice(item);
            },
            applyFilters: items => _filterService.ApplyFilters(items, Settings.SafetyFilters, BlacklistedItems, _searchFilter, Settings.EquippablesOnly, ArePriceFiltersReady),
            categorize: items => _filterService.GroupIntoCategories(items));
    }

    internal void UpdateCategories()
    {
        _state.Recategorize(
            applyFilters: items => _filterService.ApplyFilters(items, Settings.SafetyFilters, BlacklistedItems, _searchFilter, Settings.EquippablesOnly, ArePriceFiltersReady),
            categorize: items => _filterService.GroupIntoCategories(items));
    }

    internal void RefreshPrices(bool force = false)
    {
        if (NeedsMarketPrices)
            _priceService.BeginRefresh(_state.SnapshotOriginalItems(), force);
        ApplyLatestPrices();
        UpdateCategories();
    }

    internal void ChangePriceWorld(string worldName)
    {
        _selectedWorld = worldName;
        _priceService.UpdateWorld(worldName, FindWorldId(worldName));
        _state.ClearAllPrices();
        RefreshPrices();
    }

    private uint? FindWorldId(string worldName)
    {
        var world = _services.DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>()?
            .FirstOrDefault(row => row.Name.ExtractText() == worldName);
        return world is { RowId: > 0 } ? world.Value.RowId : null;
    }

    private void ApplyLatestPrices(bool force = false)
    {
        var version = _priceService.ResultsVersion;
        if (!force && version == _appliedPriceVersion) return;
        _state.UpdatePrices(_priceService.UpdateItemPrice);
        _appliedPriceVersion = version;
        UpdateCategories();
    }

    internal List<InventoryItemInfo> GetDiscardEligibleItems(bool synchronizePrices = true)
    {
        if (!CanDiscardWithPriceFilters) return new();
        // A batch can finish between the framework update and a menu click.
        // Re-read committed quotes (including expired/unknown ones) before queueing.
        if (synchronizePrices) ApplyLatestPrices(force: HasActivePriceFilters);
        return _state.ApplyFiltersToOriginal(items => _filterService.ApplyFilters(
                items, Settings.SafetyFilters, BlacklistedItems, _searchFilter, Settings.EquippablesOnly, ArePriceFiltersReady)
            .Where(item => !HasActivePriceFilters || item.MarketPrice is > 0));
    }

    internal void PrepareSelectedDiscard(IEnumerable<uint> itemIds)
    {
        if (!CanDiscardWithPriceFilters)
        {
            _services.ChatGui.PrintError("[wahventory] Wait for the market price refresh before discarding with price filters.");
            return;
        }
        DiscardService.PrepareDiscard(itemIds, GetDiscardEligibleItems(), BlacklistedItems);
    }

    internal List<InventoryItemInfo> GetProtectedItems()
    {
        return _state.GetProtectedItems(
            items => _filterService.GetProtectedItems(items, Settings.SafetyFilters, BlacklistedItems, ArePriceFiltersReady));
    }
    
    public void SaveBlacklist()
    {
        var removed = EnforceListInvariants();
        _plugin.ConfigManager.SaveBlacklist(BlacklistedItems);
        if (removed > 0) _plugin.ConfigManager.SaveAutoDiscard(AutoDiscardItems);
    }
    
    public void SaveAutoDiscard()
    {
        EnforceListInvariants();
        _plugin.ConfigManager.SaveAutoDiscard(AutoDiscardItems);
    }

    internal int EnforceListInvariants()
    {
        _state.DeselectItems(BlacklistedItems);
        return InventoryListPolicy.Normalize(BlacklistedItems, AutoDiscardItems);
    }

    private void SaveLists()
    {
        EnforceListInvariants();
        _plugin.ConfigManager.SaveBlacklist(BlacklistedItems);
        _plugin.ConfigManager.SaveAutoDiscard(AutoDiscardItems);
    }

    public InventoryListAddResult AddItemToAutoDiscard(uint itemId)
    {
        var result = InventoryListPolicy.AddToAutoDiscard(itemId, BlacklistedItems, AutoDiscardItems,
            ListsLocked, DiscardService.IsDiscarding);
        if (result == InventoryListAddResult.Added)
        {
            SaveAutoDiscard();
            RefreshInventory();
        }
        return result;
    }

    public InventoryListAddResult AddItemToBlacklist(uint itemId)
    {
        var result = InventoryListPolicy.AddToBlacklist(itemId, BlacklistedItems, AutoDiscardItems,
            ListsLocked, DiscardService.IsDiscarding);
        if (result is InventoryListAddResult.Added or InventoryListAddResult.AlreadyPresent)
        {
            SaveLists();
            RefreshInventory();
        }
        return result;
    }

    internal List<ListBackupInfo> ListBackups() => _plugin.ConfigManager.GetListBackups();

    internal void ReplaceLists(IEnumerable<uint> blacklist, IEnumerable<uint> autoDiscard)
    {
        if (DiscardService.IsDiscarding)
            throw new InvalidOperationException("Wait for the current discard operation before replacing lists.");
        // Materialize first so callers may safely pass the current list sets.
        var nextBlacklist = blacklist.ToHashSet();
        var nextAutoDiscard = autoDiscard.ToHashSet();
        InventoryListPolicy.Normalize(nextBlacklist, nextAutoDiscard);
        BlacklistedItems.Clear();
        BlacklistedItems.UnionWith(nextBlacklist);
        AutoDiscardItems.Clear();
        AutoDiscardItems.UnionWith(nextAutoDiscard);
        SaveLists();
        RefreshInventory();
    }
    
    public void ExecuteAutoDiscard()
    {
        try { StartAutoDiscard(); }
        catch (Exception ex) { _services.ChatGui.PrintError($"[wahventory] {ex.Message}"); }
    }

    internal int StartAutoDiscard()
    {
        if (DiscardService.IsDiscarding)
            throw new InvalidOperationException("A discard operation is already running.");
        if (AutoDiscardItems.Count == 0)
        {
            throw new InvalidOperationException("Auto-discard list is empty. Open Lists (toolbar) to add items.");
        }

        // Do not report an empty successful queue from stale/unloaded inventory.
        RefreshInventory(requireAvailable: true);
        if (!CanDiscardWithPriceFilters)
            throw new InvalidOperationException("Wait for the market price refresh before discarding with price filters.");
        var eligibleItems = GetDiscardEligibleItems();
        var itemsToDiscard = eligibleItems.Where(item => AutoDiscardItems.Contains(item.ItemId)
            && item.CanBeDiscarded && !BlacklistedItems.Contains(item.ItemId)).ToList();

        if (!itemsToDiscard.Any())
        {
            return 0;
        }

        var selectedItemIds = itemsToDiscard.Select(i => i.ItemId).Distinct().ToList();
        // Auto-discard list items are pre-approved by being on the list — skip the confirmation step.
        return DiscardService.PrepareDiscard(selectedItemIds, eligibleItems, BlacklistedItems, autoStart: true);
    }

    internal void AddSelectedToBlacklist()
    {
        var selected = _state.SnapshotSelectedIds();
        if (selected.Count == 0 || ListsLocked || DiscardService.IsDiscarding) return;
        var count = selected.Count(id => InventoryListPolicy.AddToBlacklist(id, BlacklistedItems, AutoDiscardItems) == InventoryListAddResult.Added);
        _state.ClearSelectionAndReset();
        SaveLists();
        RefreshInventory();
        _services.ChatGui.Print($"[wahventory] Added {count} item{(count == 1 ? "" : "s")} to blacklist.");
    }

    internal int CountSelectedArmoryItems()
    {
        var selectedIds = _state.SnapshotSelectedIds();
        if (selectedIds.Count == 0) return 0;
        var ids = new HashSet<uint>(selectedIds);
        return _state.SnapshotOriginalItems()
            .Count(i => ids.Contains(i.ItemId) && InventoryHelpers.IsArmoryContainer(i.Container));
    }

    internal void MoveSelectedArmoryToInventory()
    {
        var selectedIds = _state.SnapshotSelectedIds();
        if (selectedIds.Count == 0) return;
        var ids = new HashSet<uint>(selectedIds);
        var armoryItems = _state.SnapshotOriginalItems()
            .Where(i => ids.Contains(i.ItemId) && InventoryHelpers.IsArmoryContainer(i.Container))
            .ToList();

        if (armoryItems.Count == 0)
        {
            _services.ChatGui.PrintError("[wahventory] No selected items live in the armory.");
            return;
        }

        MoveService.StartMove(armoryItems);
    }

    internal void AddSelectedToAutoDiscard()
    {
        var selected = _state.SnapshotSelectedIds();
        if (selected.Count == 0 || ListsLocked || DiscardService.IsDiscarding) return;
        var count = selected.Count(id => InventoryListPolicy.AddToAutoDiscard(id, BlacklistedItems, AutoDiscardItems) == InventoryListAddResult.Added);
        _state.ClearSelectionAndReset();
        SaveAutoDiscard();
        RefreshInventory();
        _services.ChatGui.Print($"[wahventory] Added {count} item{(count == 1 ? "" : "s")} to auto-discard list.");
    }

    public void Dispose()
    {
        if (_expandedCategoriesChanged)
        {
            _plugin.ConfigManager.SaveConfiguration();
        }

        ListsWindow?.Dispose();
        SettingsWindow?.Dispose();
        FilteredItemsWindow?.Dispose();
        ListSync?.Dispose();
        if (DiscardService != null)
        {
            DiscardService.OnDiscardCompleted -= OnInventoryMutated;
        }
        _priceService?.Dispose();
        DiscardService?.Dispose();
        MoveService?.Dispose();
        _iconCache?.Dispose();
    }
}
