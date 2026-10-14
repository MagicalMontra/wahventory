using Dalamud.Bindings.ImGui;
using wahventory.Core;
using wahventory.Services;
using wahventory.UI.Components;

var assertions = 0;
var failures = new List<string>();
void Check(bool ok, string reason)
{
    assertions++;
    if (!ok) failures.Add(reason);
}
var priceLabels = new[] { "Market price >", "Market price <", "##MarketPriceAbove", "##MarketPriceBelow" };
var settings = new InventorySettings();
var panel = new FilterPanelComponent();
var events = 0;
panel.OnFiltersChanged += () => events++;
var counts = new FilterHiddenCounts { MarketPriceAbove = 3, MarketPriceBelow = 5 };
void Frame(bool? ready, string? status = null)
{
    panel.DrawSidebar(settings, counts, ready.HasValue
        ? new FilterPanelOptions { PriceFiltersReady = ready.Value, PriceRefreshStatus = status ?? "Test status" }
        : null);
    Check(ImGui.DisabledDepth == 0, "All production disabled scopes close after drawing");
    foreach (var label in priceLabels)
        Check(ImGui.Visits.Count(v => v.Label == label) == 1, "Exactly one production price control: " + label);
}
void AttemptAllPriceEdits()
{
    ImGui.CheckboxEdits["Market price >"] = !settings.SafetyFilters.FilterMarketPriceAbove;
    ImGui.CheckboxEdits["Market price <"] = !settings.SafetyFilters.FilterMarketPriceBelow;
    ImGui.InputEdits["##MarketPriceAbove"] = 4321;
    ImGui.InputEdits["##MarketPriceBelow"] = 123;
}

// A persisted enabled filter stays saved but is inactive/greyed during loading.
settings.SafetyFilters.FilterMarketPriceAbove = true;
settings.SafetyFilters.FilterMarketPriceBelow = true;
foreach (var ready in new bool?[] { false, null })
{
    ImGui.Reset(); events = 0;
    AttemptAllPriceEdits();
    ImGui.Hovered.UnionWith(new[] { "Market price >", "Market price <" });
    Frame(ready, "Refreshing 1/7 · unknown prices stay visible.");
    foreach (var label in priceLabels)
    {
        var visit = ImGui.Visits.Single(v => v.Label == label);
        Check(visit.Disabled && visit.InteractionAttempted && !visit.Changed, "Disabled production control blocks attempted edit: " + label);
    }
    Check(settings.SafetyFilters.FilterMarketPriceAbove && settings.SafetyFilters.FilterMarketPriceBelow,
        "Loading preserves saved checkbox settings");
    Check(settings.SafetyFilters.MarketPriceAboveThreshold == 1000 && settings.SafetyFilters.MarketPriceBelowThreshold == 100,
        "Loading preserves saved thresholds");
    Check(events == 0, "Blocked interactions emit no change events");
    Check(ImGui.Texts.Contains("4 / 8 active"), "Paused saved price filters are excluded from active count");
    Check(ImGui.Tooltips.Count == 2 && ImGui.Tooltips.All(t => t.Contains("paused while prices are loading")),
        "Disabled hover explains price filters are paused");
    Check(ImGui.Texts.Contains(ready.HasValue ? "Refreshing 1/7 · unknown prices stay visible." : "Refresh market prices to use price filters."),
        "Refresh/default unknown readiness status remains visible");
}

// Safety controls outside the disabled block remain usable while prices load.
ImGui.Reset(); events = 0; ImGui.CheckboxEdits["HQ Items"] = true;
Frame(false);
Check(settings.SafetyFilters.FilterHQItems && events == 1, "Ordinary safety control remains interactive during price refresh");
Check(!ImGui.Visits.Single(v => v.Label == "HQ Items").Disabled, "Price loading does not disable the entire sidebar");
settings.SafetyFilters.FilterHQItems = false;

// Ready makes both checkbox/input pairs usable; a multi-edit frame emits once.
ImGui.Reset(); events = 0; AttemptAllPriceEdits();
Frame(true, "7/7 checked · 2 unknown prices stay visible.");
foreach (var label in priceLabels)
    Check(!ImGui.Visits.Single(v => v.Label == label).Disabled && ImGui.Visits.Single(v => v.Label == label).Changed,
        "Ready production control accepts actual edit: " + label);
Check(!settings.SafetyFilters.FilterMarketPriceAbove && !settings.SafetyFilters.FilterMarketPriceBelow,
    "Edited checkbox settings persist");
Check(settings.SafetyFilters.MarketPriceAboveThreshold == 4321 && settings.SafetyFilters.MarketPriceBelowThreshold == 123,
    "Edited thresholds persist");
Check(events == 1, "Multiple genuine edits emit one change event per frame");
Check(ImGui.Texts.Contains("7/7 checked · 2 unknown prices stay visible."), "Completed refresh still explains unknown values");
Check(!ImGui.Visits.Any(v => v.Label.Contains(">=") || v.Label.Contains("<=")), "Production labels preserve strict greater/less comparisons");

// No-edit and attempted-identical values leave config/event state unchanged.
ImGui.Reset(); events = 0;
ImGui.CheckboxEdits["Market price >"] = false; ImGui.CheckboxEdits["Market price <"] = false;
ImGui.InputEdits["##MarketPriceAbove"] = 4321; ImGui.InputEdits["##MarketPriceBelow"] = 123;
Frame(true);
Check(events == 0, "Identical attempted values emit no change events");
Check(settings.SafetyFilters.MarketPriceAboveThreshold == 4321 && settings.SafetyFilters.MarketPriceBelowThreshold == 123,
    "Drawing does not reset thresholds");

// Ready tooltip spells out units, equality, and unknown-price behavior.
ImGui.Reset(); events = 0;
ImGui.Hovered.UnionWith(new[] { "Market price >", "Market price <" });
Frame(true, "");
Check(ImGui.Tooltips.Count == 2 && ImGui.Tooltips.All(t => t.Contains("gil per item") && t.Contains("Equal prices stay visible") && t.Contains("Unknown prices stay visible")),
    "Ready tooltips explain units, strict equality and unknown status");
Check(ImGui.Tooltips.Any(t => t.Contains("3 items match")) && ImGui.Tooltips.Any(t => t.Contains("5 items match")),
    "Independent production count fields reach their matching price tooltips");
Check(!ImGui.Texts.Contains(""), "Empty custom refresh status is omitted");

// Negative user input is clamped, and changing a nonzero threshold fires once.
ImGui.Reset(); events = 0;
ImGui.InputEdits["##MarketPriceAbove"] = -7; ImGui.InputEdits["##MarketPriceBelow"] = int.MinValue;
Frame(true);
Check(settings.SafetyFilters.MarketPriceAboveThreshold == 0 && settings.SafetyFilters.MarketPriceBelowThreshold == 0,
    "Both threshold edits clamp negative values to zero");
Check(events == 1, "Clamping changed thresholds emits one change event");

// An edit to another negative number while already clamped at0 changes no saved
// configuration. A change event should only report an effective setting change.
ImGui.Reset(); events = 0;
ImGui.InputEdits["##MarketPriceAbove"] = -1; ImGui.InputEdits["##MarketPriceBelow"] = -2;
Frame(true);
Check(settings.SafetyFilters.MarketPriceAboveThreshold == 0 && settings.SafetyFilters.MarketPriceBelowThreshold == 0,
    "Repeated negative edits retain nonnegative saved values");
Check(events == 0, "Repeated negative edits at zero emit no effective-setting change event");

// Large positive UI edits persist without becoming negative through overflow.
ImGui.Reset(); events = 0;
ImGui.InputEdits["##MarketPriceAbove"] = int.MaxValue;
ImGui.InputEdits["##MarketPriceBelow"] = int.MaxValue;
Frame(true);
Check(settings.SafetyFilters.MarketPriceAboveThreshold == int.MaxValue
    && settings.SafetyFilters.MarketPriceBelowThreshold == int.MaxValue,
    "Both thresholds preserve the largest supported positive UI value");
Check(events == 1, "Large positive edits emit one setting-change event");

// Rendering a persisted long value never truncates the stored configuration.
settings.SafetyFilters.MarketPriceAboveThreshold = long.MaxValue;
settings.SafetyFilters.MarketPriceBelowThreshold = long.MaxValue;
ImGui.Reset(); events = 0; Frame(false);
Check(settings.SafetyFilters.MarketPriceAboveThreshold == long.MaxValue
    && settings.SafetyFilters.MarketPriceBelowThreshold == long.MaxValue,
    "Disabled rendering preserves saved long thresholds without mutation");
Check(events == 0, "Display clamping alone cannot emit a configuration event");

// Each saved filter contributes only when readiness is true.
foreach (var above in new[] { false, true })
foreach (var below in new[] { false, true })
foreach (var ready in new[] { false, true })
{
    settings.SafetyFilters.FilterMarketPriceAbove = above;
    settings.SafetyFilters.FilterMarketPriceBelow = below;
    ImGui.Reset(); events = 0; Frame(ready);
    var expected = 4 + (ready && above ? 1 : 0) + (ready && below ? 1 : 0);
    Check(ImGui.Texts.Contains($"{expected} / 8 active"), $"Active count respects saved flags/readiness: {above}/{below}/{ready}");
    Check(events == 0, "Readiness redraw emits no filter-change event");
    foreach (var label in priceLabels)
        Check(ImGui.Visits.Single(v => v.Label == label).Disabled == !ready, "Readiness alone gates both price controls: " + label);
}

Console.WriteLine($"Price filter UI regression: {assertions} assertions; {failures.Count} failures.");
foreach (var failure in failures) Console.Error.WriteLine("FAIL: " + failure);
return failures.Count == 0 ? 0 : 1;
