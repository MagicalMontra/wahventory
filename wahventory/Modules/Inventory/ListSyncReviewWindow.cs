using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using wahventory.Services;
using wahventory.Services.Helpers;
using wahventory.UI;

namespace wahventory.Modules.Inventory;

internal class ListSyncReviewWindow : Window, IDisposable
{
    private readonly InventoryManagementModule _module;
    private readonly ListSyncController _sync;
    private readonly ItemSearchService _searchService;
    private readonly IconCache _iconCache;

    public ListSyncReviewWindow(
        InventoryManagementModule module,
        ListSyncController sync,
        ItemSearchService searchService,
        IconCache iconCache)
        : base("Review list update##ListSyncReview", ImGuiWindowFlags.NoCollapse)
    {
        _module = module;
        _sync = sync;
        _searchService = searchService;
        _iconCache = iconCache;

        Size = new Vector2(520, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 300),
            MaximumSize = new Vector2(900, 900),
        };
    }

    public override void Draw()
    {
        var pending = _sync.Pending;
        if (pending == null)
        {
            IsOpen = false;
            return;
        }

        var file = pending.File;
        var diff = pending.Diff;

        var title = string.IsNullOrWhiteSpace(file.Name) ? "Synced list" : file.Name;
        ImGui.TextColored(Theme.ColorBlue, title);
        if (file.Revision.HasValue)
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.ColorInfo, $"· rev {file.Revision}");
        }
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.ColorSubdued))
        {
            ImGui.TextWrapped("This update could cause items to be discarded that weren't before. Your current lists will be backed up before applying.");
        }
        ImGui.Spacing();

        var listHeight = ImGui.GetContentRegionAvail().Y - 40;
        using (ImRaii.Child("ListSyncDiff", new Vector2(0, listHeight), true))
        {
            DrawSection("Added to auto-discard", diff.AutoDiscardAdded, Theme.ColorWarning, defaultOpen: true);
            DrawSection("Removed from blacklist", diff.BlacklistRemoved, Theme.ColorWarning, defaultOpen: true);
            DrawSection("Added to blacklist", diff.BlacklistAdded, Theme.ColorSuccess, defaultOpen: false);
            DrawSection("Removed from auto-discard", diff.AutoDiscardRemoved, Theme.ColorSuccess, defaultOpen: false);
        }

        ImGui.Spacing();
        var discarding = _module.DiscardService.IsDiscarding;
        using (ImRaii.Disabled(discarding))
        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.541f, 0.341f, 0.127f, 1f))
                              .Push(ImGuiCol.ButtonHovered, new Vector4(0.641f, 0.441f, 0.227f, 1f)))
        {
            if (ImGui.Button("Apply update", new Vector2(130, 0)))
            {
                _sync.ApplyPending();
            }
        }
        if (discarding && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Wait for the current discard to finish.");

        ImGui.SameLine();
        if (ImGui.Button("Skip this version", new Vector2(140, 0)))
        {
            _sync.SkipPending();
        }
        ImGui.SameLine();
        if (ImGui.Button("Later", new Vector2(80, 0)))
        {
            IsOpen = false;
        }
    }

    private void DrawSection(string label, List<uint> ids, Vector4 color, bool defaultOpen)
    {
        if (ids.Count == 0)
            return;

        using (ImRaii.PushColor(ImGuiCol.Text, color))
        {
            if (!ImGui.CollapsingHeader($"{label} ({ids.Count})", defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
                return;
        }

        foreach (var id in ids)
        {
            var info = _searchService.GetItemInfo(id);
            if (info.HasValue && info.Value.IconId > 0)
            {
                var icon = _iconCache.GetIcon(info.Value.IconId);
                if (icon != null)
                {
                    ImGui.Image(icon.Handle, new Vector2(18, 18));
                    ImGui.SameLine(0, 6);
                }
            }
            ImGui.Text(info.HasValue ? info.Value.Name : $"Unknown item ({id})");
            ImGui.SameLine();
            ImGui.TextColored(Theme.ColorSubdued, $"#{id}");
        }
        ImGui.Spacing();
    }

    public void Dispose() { }
}
