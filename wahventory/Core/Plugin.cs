using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using System.IO;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using wahventory.Services.Helpers;
using wahventory.UI.Windows;
using wahventory.Modules.Inventory;
using Dalamud.Game;
using ECommons;

namespace wahventory.Core;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] private static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] private static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] private static IDataManager DataManager { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;
    [PluginService] private static IClientState ClientState { get; set; } = null!;
    [PluginService] private static ITextureProvider TextureProvider { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;
    [PluginService] private static IGameInteropProvider GameInteropProvider { get; set; } = null!;
    [PluginService] private static IChatGui ChatGui { get; set; } = null!;
    [PluginService] private static IGameGui GameGui { get; set; } = null!;
    [PluginService] private static ICondition Condition { get; set; } = null!;
    [PluginService] private static IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] private static IKeyState KeyState { get; set; } = null!;
    [PluginService] private static IPlayerState PlayerState { get; set; } = null!;

    private const string CommandName = "/wahventory";

    public IGameServices Services { get; }
    public ConfigurationManager ConfigManager { get; }
    public Configuration Configuration => ConfigManager.Configuration;
    public readonly WindowSystem WindowSystem = new("wahventory");
    private MainWindow MainWindow { get; init; }
    private DiscardConfirmationWindow DiscardConfirmationWindow { get; init; }

    private InventoryManagementModule InventoryModule { get; init; }
    private readonly AutomationStatusStore _automationStatus;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        Services = new GameServices(
            PluginInterface,
            CommandManager,
            DataManager,
            Framework,
            ClientState,
            TextureProvider,
            Log,
            GameInteropProvider,
            ChatGui,
            GameGui,
            Condition,
            ObjectTable,
            KeyState,
            PlayerState);

        ConfigManager = new ConfigurationManager(Services);

        InventoryModule = new InventoryManagementModule(this, Services);
        _automationStatus = new AutomationStatusStore();
        InventoryModule.DiscardService.OnDiscardStarted += ReportDiscardStarted;
        InventoryModule.DiscardService.OnDiscardProgress += ReportDiscardProgress;
        InventoryModule.DiscardService.OnDiscardCompleted += ReportDiscardCompleted;
        InventoryModule.DiscardService.OnDiscardCancelled += ReportDiscardCancelled;
        InventoryModule.DiscardService.OnDiscardError += ReportDiscardError;
        MainWindow = new MainWindow(this, InventoryModule);

        // Create discard confirmation window with icon cache from module
        var iconCache = new IconCache(Services.TextureProvider);
        DiscardConfirmationWindow = new DiscardConfirmationWindow(
            InventoryModule.DiscardService,
            iconCache);
        
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(DiscardConfirmationWindow);
        WindowSystem.AddWindow(InventoryModule.ListsWindow);
        WindowSystem.AddWindow(InventoryModule.SettingsWindow);
        WindowSystem.AddWindow(InventoryModule.FilteredItemsWindow);
        WindowSystem.AddWindow(InventoryModule.ListSyncReviewWindow);

        Services.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the wahventory window\n/wahventory refresh - Refresh inventory and report completion\n/wahventory auto - Execute auto-discard and report its result\n/wahventory status - Show refresh and discard status"
        });

        Services.PluginInterface.UiBuilder.Draw += DrawUI;
        Services.PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUI;
        Services.PluginInterface.UiBuilder.OpenMainUi += ToggleMainUI;
        Services.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Services.Framework.Update -= OnFrameworkUpdate;

        InventoryModule.DiscardService.CancelDiscard();
        SafeStatus(() => _automationStatus.Cancel("discard", "Wahventory was unloaded."));
        SafeStatus(() => _automationStatus.Cancel("refresh", "Wahventory was unloaded."));
        InventoryModule.DiscardService.OnDiscardStarted -= ReportDiscardStarted;
        InventoryModule.DiscardService.OnDiscardProgress -= ReportDiscardProgress;
        InventoryModule.DiscardService.OnDiscardCompleted -= ReportDiscardCompleted;
        InventoryModule.DiscardService.OnDiscardCancelled -= ReportDiscardCancelled;
        InventoryModule.DiscardService.OnDiscardError -= ReportDiscardError;

        WindowSystem.RemoveAllWindows();

        InventoryModule.Dispose();

        Services.CommandManager.RemoveHandler(CommandName);
        ECommonsMain.Dispose();
    }
    
    private void OnFrameworkUpdate(IFramework framework)
    {
        InventoryModule.Update();
    }

    private void OnCommand(string command, string args)
    {
        var trimmedArgs = args?.Trim().ToLowerInvariant() ?? "";

        if (trimmedArgs == "refresh")
        {
            RefreshWithStatus();
        }
        else if (trimmedArgs == "auto")
        {
            DiscardWithStatus();
        }
        else if (trimmedArgs == "status")
        {
            foreach (var operation in new[] { "refresh", "discard" })
            {
                var state = _automationStatus.ReadState(operation);
                Services.ChatGui.Print($"[wahventory] {operation} run {state.RunId}: {state.Status} ({state.Completed}/{state.Total}) {state.Message}");
            }
        }
        else
        {
            ToggleMainUI();
        }
    }

    private void RefreshWithStatus()
    {
        if (_automationStatus.IsRunning("refresh"))
        {
            Services.ChatGui.PrintError("[wahventory] A refresh is already running.");
            return;
        }
        try
        {
            _automationStatus.Begin("refresh");
            Services.ChatGui.Print("[wahventory] Refresh RUNNING.");
            if (!Services.PlayerState.IsLoaded || InventoryModule.DiscardService.IsDiscarding)
                throw new InvalidOperationException("Wait until the character is ready and the current discard finishes before refreshing.");
            InventoryModule.RefreshInventory(requireAvailable: true);
            _automationStatus.Complete("refresh", "Inventory refreshed.");
            Services.ChatGui.Print("[wahventory] Refresh DONE: inventory refreshed.");
        }
        catch (Exception ex)
        {
            SafeStatus(() => _automationStatus.Fail("refresh", ex.Message));
            Services.Log.Error(ex, "Inventory refresh failed");
            Services.ChatGui.PrintError($"[wahventory] Refresh FAILED: {ex.Message}");
        }
    }

    private void DiscardWithStatus()
    {
        if (InventoryModule.DiscardService.IsDiscarding || _automationStatus.IsRunning("discard"))
        {
            Services.ChatGui.PrintError("[wahventory] A discard operation is already running.");
            return;
        }
        try
        {
            _automationStatus.Begin("discard");
            if (!Services.PlayerState.IsLoaded)
                throw new InvalidOperationException("Wait until the character is ready before discarding.");
            var queued = InventoryModule.StartAutoDiscard();
            if (queued == 0)
            {
                _automationStatus.Complete("discard", "No eligible items to discard.", 0, 0);
                Services.ChatGui.Print("[wahventory] Discard DONE: no eligible items to discard.");
            }
        }
        catch (Exception ex)
        {
            SafeStatus(() => _automationStatus.Fail("discard", ex.Message));
            Services.Log.Error(ex, "Auto-discard could not start");
            Services.ChatGui.PrintError($"[wahventory] Discard FAILED: {ex.Message}");
        }
    }

    private void ReportDiscardStarted() => SafeStatus(() =>
    {
        var total = InventoryModule.DiscardService.TotalItems;
        if (!_automationStatus.IsRunning("discard")) _automationStatus.Begin("discard", total);
        else _automationStatus.Progress("discard", 0, total);
        Services.ChatGui.Print($"[wahventory] Discard RUNNING: {total} stack(s) queued.");
    });

    private void ReportDiscardProgress(int completed, int total) =>
        SafeStatus(() => _automationStatus.Progress("discard", completed, total));

    private void ReportDiscardCompleted() => SafeStatus(() =>
    {
        var service = InventoryModule.DiscardService;
        _automationStatus.Complete("discard", "Discard queue finished.", service.DiscardProgress, service.TotalItems);
        Services.ChatGui.Print($"[wahventory] Discard DONE: {service.DiscardProgress} stack(s) removed.");
    });

    private void ReportDiscardCancelled() => SafeStatus(() =>
    {
        _automationStatus.Cancel("discard", "Discard operation was cancelled.");
        Services.ChatGui.Print("[wahventory] Discard CANCELLED.");
    });

    private void ReportDiscardError(string message) =>
        SafeStatus(() => _automationStatus.Fail("discard", message));

    private void SafeStatus(Action publish)
    {
        try { publish(); }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Could not publish Wahventory automation status");
            Services.ChatGui.PrintError("[wahventory] Could not publish completion status; waiting macros will not assume success.");
        }
    }

    private void DrawUI()
    {
        WindowSystem.Draw();
    }

    public void ToggleConfigUI() => InventoryModule.SettingsWindow.Toggle();
    public void ToggleMainUI() => MainWindow.Toggle();
}
