using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using wahventory.Core;
using wahventory.Services;
using wahventory.UI;

namespace wahventory.Modules.Inventory;

internal class InventorySettingsWindow : Window, IDisposable
{
    private readonly InventoryManagementModule _module;
    private readonly PassiveDiscardService _passiveDiscardService;
    private readonly InventoryState _state;

    private string _syncUrlInput = string.Empty;
    private List<ListBackupInfo> _backups = new();
    private int _selectedBackup;
    private bool _backupsDirty = true;

    public InventorySettingsWindow(
        InventoryManagementModule module,
        PassiveDiscardService passiveDiscardService,
        InventoryState state)
        : base("Settings##InventorySettings", ImGuiWindowFlags.NoCollapse)
    {
        _module = module;
        _passiveDiscardService = passiveDiscardService;
        _state = state;

        Size = new Vector2(480, 700);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 360),
            MaximumSize = new Vector2(720, 1000),
        };
    }

    public override void Draw()
    {
        var settings = _module.Settings;
        bool changed = false;

        DrawSectionHeader(FontAwesomeIcon.Robot, "PASSIVE AUTO-DISCARD");

        var enabled = settings.PassiveDiscard.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            settings.PassiveDiscard.Enabled = enabled;
            changed = true;
        }
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.ColorSubdued))
        {
            ImGui.TextWrapped("Auto-discards items on the auto-discard list while you're idle. Only fires in safe zones (cities, housing, inns, barracks, Gold Saucer).");
        }

        using (ImRaii.Disabled(!settings.PassiveDiscard.Enabled))
        {
            ImGui.Spacing();
            ImGui.SetNextItemWidth(80);
            int idleTime = settings.PassiveDiscard.IdleTimeSeconds;
            if (ImGui.InputInt("Idle time required (seconds)", ref idleTime, 5, 10))
            {
                settings.PassiveDiscard.IdleTimeSeconds = Math.Max(10, Math.Min(300, idleTime));
                changed = true;
            }

            ImGui.Spacing();
            DrawStatusLine();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawSectionHeader(FontAwesomeIcon.Coins, "MARKET PRICES");

        var showPrices = settings.ShowMarketPrices;
        if (ImGui.Checkbox("Show market prices", ref showPrices))
        {
            settings.ShowMarketPrices = showPrices;
            changed = true;
        }

        using (ImRaii.Disabled(!settings.ShowMarketPrices))
        {
            ImGui.SetNextItemWidth(80);
            int cacheMin = settings.PriceCacheDurationMinutes;
            if (ImGui.InputInt("Cache duration (minutes)", ref cacheMin, 1, 10))
            {
                settings.PriceCacheDurationMinutes = Math.Max(1, cacheMin);
                changed = true;
            }

            var autoRefresh = settings.AutoRefreshPrices;
            if (ImGui.Checkbox("Auto-refresh visible items", ref autoRefresh))
            {
                settings.AutoRefreshPrices = autoRefresh;
                changed = true;
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawListSyncSection(ref changed);

        if (changed)
        {
            _module.SaveConfig();
        }
    }

    public override void OnOpen()
    {
        _backupsDirty = true;
        if (string.IsNullOrEmpty(_syncUrlInput))
            _syncUrlInput = _module.Settings.ListSync.Url;
    }

    private void DrawListSyncSection(ref bool changed)
    {
        var sync = _module.Settings.ListSync;
        var controller = _module.ListSync;

        DrawSectionHeader(FontAwesomeIcon.CloudDownloadAlt, "LIST SYNC");

        using (ImRaii.PushColor(ImGuiCol.Text, Theme.ColorSubdued))
        {
            ImGui.TextWrapped("Pull your blacklist and auto-discard list from a URL (e.g. a raw file in a git repo) to share them between installs. While enabled, the remote file replaces your local lists — they're backed up first.");
        }
        ImGui.Spacing();

        using (ImRaii.Disabled(sync.Enabled))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##syncUrl", "https://…/raw/branch/main/wahventory.json", ref _syncUrlInput, 512);
        }

        var enabled = sync.Enabled;
        using (ImRaii.Disabled(!sync.Enabled && !ListSyncService.IsValidUrl(_syncUrlInput)))
        {
            if (ImGui.Checkbox("Sync lists from URL", ref enabled))
            {
                if (enabled)
                {
                    controller.Enable(_syncUrlInput);
                    _backupsDirty = true;
                }
                else
                {
                    ImGui.OpenPopup("DisableListSync");
                }
            }
        }
        if (!sync.Enabled && !string.IsNullOrWhiteSpace(_syncUrlInput) && !ListSyncService.IsValidUrl(_syncUrlInput))
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.ColorWarning, "URL must start with https://");
        }

        using (ImRaii.Disabled(!sync.Enabled))
        {
            ImGui.SetNextItemWidth(80);
            int interval = sync.CheckIntervalMinutes;
            if (ImGui.InputInt("Check every (minutes)", ref interval, 5, 15))
            {
                sync.CheckIntervalMinutes = Math.Clamp(interval, 5, 1440);
                changed = true;
            }

            ImGui.SameLine();
            using (ImRaii.Disabled(controller.IsChecking))
            {
                if (ImGui.Button("Check now"))
                    controller.CheckNow();
            }
        }

        DrawSyncStatus(sync, controller);
        DrawDisableSyncPopup(controller);

        ImGui.Spacing();
        DrawBackups(sync, controller);
    }

    private void DrawSyncStatus(ListSyncSettings sync, ListSyncController controller)
    {
        if (controller.LastError != null)
        {
            ImGui.TextColored(Theme.ColorError, $"● {controller.LastError}");
        }

        if (!sync.Enabled)
            return;

        if (controller.Pending != null)
        {
            ImGui.TextColored(Theme.ColorWarning, "● Update waiting for review");
            ImGui.SameLine();
            if (ImGui.SmallButton("Review"))
                _module.ListSyncReviewWindow.IsOpen = true;
        }
        else if (controller.IsChecking)
        {
            ImGui.TextColored(Theme.ColorInfo, "● Checking…");
        }
        else if (sync.LastAppliedAt.HasValue)
        {
            var name = string.IsNullOrWhiteSpace(sync.RemoteName) ? "Synced" : sync.RemoteName;
            var rev = sync.RemoteRevision.HasValue ? $" rev {sync.RemoteRevision}" : string.Empty;
            ImGui.TextColored(Theme.ColorSuccess, $"● {name}{rev} · applied {sync.LastAppliedAt:MMM d HH:mm}");
        }
        else if (sync.SkippedHash != null)
        {
            ImGui.TextColored(Theme.ColorWarning, "● Latest version skipped");
        }

        if (sync.LastCheckedAt.HasValue)
        {
            ImGui.TextColored(Theme.ColorSubdued, $"Last checked {sync.LastCheckedAt:MMM d HH:mm}");
        }
    }

    private void DrawDisableSyncPopup(ListSyncController controller)
    {
        using var popup = ImRaii.PopupModal("DisableListSync", ImGuiWindowFlags.AlwaysAutoResize);
        if (!popup) return;

        ImGui.Text("Turn off list sync?");
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.ColorSubdued))
        {
            ImGui.Text("Keep the synced lists, or restore the lists you had before enabling sync.");
        }
        ImGui.Spacing();

        if (ImGui.Button("Keep synced lists", new Vector2(150, 0)))
        {
            controller.Disable(restorePreSyncBackup: false);
            _backupsDirty = true;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(_module.Settings.ListSync.PreSyncBackup == null))
        {
            if (ImGui.Button("Restore previous", new Vector2(150, 0)))
            {
                controller.Disable(restorePreSyncBackup: true);
                _backupsDirty = true;
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(80, 0)))
        {
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawBackups(ListSyncSettings sync, ListSyncController controller)
    {
        if (_backupsDirty)
        {
            _backups = _module.ListBackups();
            _selectedBackup = Math.Clamp(_selectedBackup, 0, Math.Max(0, _backups.Count - 1));
            _backupsDirty = false;
        }

        ImGui.Text("Backups");
        if (_backups.Count == 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.ColorSubdued, "none yet");
            return;
        }

        ImGui.SetNextItemWidth(-90);
        using (var combo = ImRaii.Combo("##backups", FormatBackup(_backups[_selectedBackup])))
        {
            if (combo)
            {
                for (var i = 0; i < _backups.Count; i++)
                {
                    if (ImGui.Selectable(FormatBackup(_backups[i]), i == _selectedBackup))
                        _selectedBackup = i;
                }
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(sync.Enabled))
        {
            if (ImGui.Button("Restore", new Vector2(80, 0)))
                ImGui.OpenPopup("RestoreBackupConfirm");
        }
        if (sync.Enabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Turn off list sync to restore a backup.");

        using var popup = ImRaii.PopupModal("RestoreBackupConfirm", ImGuiWindowFlags.AlwaysAutoResize);
        if (!popup) return;

        ImGui.Text($"Replace your lists with the backup from {_backups[_selectedBackup].CreatedAt:yyyy-MM-dd HH:mm}?");
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.ColorSubdued))
        {
            ImGui.Text("Your current lists will be backed up first.");
        }
        ImGui.Spacing();
        if (ImGui.Button("Restore", new Vector2(110, 0)))
        {
            controller.RestoreBackup(_backups[_selectedBackup].FileName);
            _backupsDirty = true;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(110, 0)))
        {
            ImGui.CloseCurrentPopup();
        }
    }

    private static string FormatBackup(ListBackupInfo b) =>
        $"{b.CreatedAt:MMM d HH:mm} · {b.Reason} ({b.BlacklistCount} bl / {b.AutoDiscardCount} ad)";

    private static void DrawSectionHeader(FontAwesomeIcon icon, string title)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(Theme.ColorBlue, icon.ToIconString());
        ImGui.PopFont();
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.ColorBlue))
        {
            ImGui.Text(title);
        }
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawStatusLine()
    {
        var status = _passiveDiscardService.GetStatus(
            _module.AutoDiscardItems,
            _state.SnapshotOriginalItems(),
            _module.BlacklistedItems);

        switch (status.State)
        {
            case PassiveDiscardState.Disabled:
                ImGui.TextColored(Theme.ColorSubdued, "● Disabled");
                break;
            case PassiveDiscardState.NoItems:
                ImGui.TextColored(Theme.ColorSubdued, "● No items to discard");
                break;
            case PassiveDiscardState.PlayerBusy:
                ImGui.TextColored(Theme.ColorWarning, "● Player busy");
                break;
            case PassiveDiscardState.NotInAllowedZone:
                ImGui.TextColored(Theme.ColorWarning, "● Not in allowed zone");
                break;
            case PassiveDiscardState.WaitingForIdle:
                ImGui.TextColored(Theme.ColorInfo, $"● Waiting for idle ({status.IdleSeconds}/{status.RequiredIdleSeconds}s)");
                break;
            case PassiveDiscardState.Cooldown:
                ImGui.TextColored(Theme.ColorSubdued, $"● Cooldown ({status.CooldownSecondsRemaining}s remaining)");
                break;
            case PassiveDiscardState.Ready:
                ImGui.TextColored(Theme.ColorSuccess, "● Ready to execute");
                break;
        }
    }

    public void Dispose() { }
}
