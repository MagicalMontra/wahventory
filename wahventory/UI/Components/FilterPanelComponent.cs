using System;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using wahventory.Core;
using wahventory.Services;

namespace wahventory.UI.Components;

public sealed class FilterPanelOptions
{
    public bool PriceFiltersReady { get; init; }
    public string PriceRefreshStatus { get; init; } = "Refresh market prices to use price filters.";
}

public class FilterPanelComponent
{
    public event Action? OnFiltersChanged;

    /// <summary>
    /// Vertical-sidebar layout: one filter per row, short labels, thresholds inline.
    /// Optional counts argument shows per-filter "(N)" indicators on the right.
    /// </summary>
    public void DrawSidebar(InventorySettings settings, FilterHiddenCounts? counts = null, FilterPanelOptions? options = null)
    {
        options ??= new FilterPanelOptions();
        var filters = settings.SafetyFilters;
        var active = CountActiveFilters(filters, options.PriceFiltersReady);

        using (ImRaii.PushColor(ImGuiCol.Text, Theme.ColorBlue))
        {
            ImGui.Text("FILTERS");
        }
        ImGui.SameLine();
        ImGui.TextColored(Theme.ColorSubdued, $"{active} / 8 active");
        ImGui.Separator();
        ImGui.Spacing();

        bool changed = false;

        changed |= SidebarCheckbox("In Gearset",          () => filters.FilterGearsetItems,     v => filters.FilterGearsetItems = v,     "Equipment in any saved gearset",        counts?.InGearset);
        changed |= SidebarCheckbox("Indisposable",        () => filters.FilterIndisposableItems, v => filters.FilterIndisposableItems = v, "Items the game won't let you discard", counts?.Indisposable);
        changed |= SidebarCheckbox("HQ Items",             () => filters.FilterHQItems,           v => filters.FilterHQItems = v,           "High-Quality flagged items",   counts?.HQ);
        changed |= SidebarCheckbox("Collectables",         () => filters.FilterCollectables,      v => filters.FilterCollectables = v,      "Collectability turn-in items", counts?.Collectables);
        changed |= SidebarCheckbox("Unique & Untradeable", () => filters.FilterUniqueUntradeable, v => filters.FilterUniqueUntradeable = v, "Cannot be reacquired or sold", counts?.UniqueUntradeable);

        var high = filters.FilterHighLevelGear;
        if (ImGui.Checkbox("##HighLvlGear", ref high))
        {
            filters.FilterHighLevelGear = high;
            changed = true;
        }
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.Text("High-Level Gear");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(50);
        int maxLvl = (int)filters.MaxGearItemLevel;
        if (ImGui.InputInt("##MaxIlvl", ref maxLvl, 0, 0))
        {
            filters.MaxGearItemLevel = (uint)Math.Max(1, Math.Min(999, maxLvl));
            changed = true;
        }

        ImGui.Spacing();
        changed |= SidebarPriceFilter("Market price >", "MarketPriceAbove",
            filters.FilterMarketPriceAbove, v => filters.FilterMarketPriceAbove = v,
            filters.MarketPriceAboveThreshold, v => filters.MarketPriceAboveThreshold = v,
            options.PriceFiltersReady, counts?.MarketPriceAbove);
        changed |= SidebarPriceFilter("Market price <", "MarketPriceBelow",
            filters.FilterMarketPriceBelow, v => filters.FilterMarketPriceBelow = v,
            filters.MarketPriceBelowThreshold, v => filters.MarketPriceBelowThreshold = v,
            options.PriceFiltersReady, counts?.MarketPriceBelow);
        if (!string.IsNullOrWhiteSpace(options.PriceRefreshStatus))
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Theme.ColorSubdued))
                ImGui.TextWrapped(options.PriceRefreshStatus);
        }

        if (changed)
        {
            OnFiltersChanged?.Invoke();
        }
    }

    private static int CountActiveFilters(SafetyFilters filters, bool priceFiltersReady)
    {
        var count = 0;
        if (filters.FilterGearsetItems) count++;
        if (filters.FilterIndisposableItems) count++;
        if (filters.FilterHighLevelGear) count++;
        if (filters.FilterUniqueUntradeable) count++;
        if (filters.FilterHQItems) count++;
        if (filters.FilterCollectables) count++;
        if (priceFiltersReady && filters.FilterMarketPriceAbove) count++;
        if (priceFiltersReady && filters.FilterMarketPriceBelow) count++;
        return count;
    }

    private static bool SidebarPriceFilter(string label, string id, bool enabled, Action<bool> setEnabled,
        long threshold, Action<long> setThreshold, bool ready, int? count)
    {
        var changed = false;
        using (ImRaii.Disabled(!ready))
        {
            if (ImGui.Checkbox(label, ref enabled))
            {
                setEnabled(enabled);
                changed = true;
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(ready
                    ? $"Show matching items using gil per item, not the stack total. Equal prices do not match this condition. Unknown prices are excluded.\n{count.GetValueOrDefault()} items match this threshold."
                    : "Wait until the current inventory price refresh finishes. Saved price filters are paused while prices are loading.");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Math.Max(60, ImGui.GetContentRegionAvail().X - 4));
            var value = (int)Math.Clamp(threshold, 0, int.MaxValue);
            if (ImGui.InputInt("##" + id, ref value, 0, 0))
            {
                var nextThreshold = Math.Max(0, value);
                if (nextThreshold != threshold)
                {
                    setThreshold(nextThreshold);
                    changed = true;
                }
            }
        }
        return changed;
    }

    private static bool SidebarCheckbox(string label, Func<bool> get, Action<bool> set, string tooltip, int? count = null)
    {
        var v = get();
        var changed = ImGui.Checkbox(label, ref v);
        if (changed) set(v);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }

        if (count.HasValue && count.Value > 0)
        {
            var text = count.Value.ToString();
            var width = ImGui.CalcTextSize(text).X;
            var rowEnd = ImGui.GetWindowContentRegionMax().X;
            ImGui.SameLine(rowEnd - width - 4);
            ImGui.TextColored(Theme.ColorSubdued, text);
        }

        return changed;
    }
}
