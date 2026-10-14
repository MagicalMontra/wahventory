using wahventory.Core;
using wahventory.Models;
using wahventory.Services;
using wahventory.Services.Helpers;

var service = new ItemFilterService();
var blacklist = new HashSet<uint>();
var checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
void Equal<T>(T actual, T expected, string message) => Check(EqualityComparer<T>.Default.Equals(actual, expected), message);
SafetyFilters Filters() => new()
{
    FilterGearsetItems = false, FilterIndisposableItems = false, FilterHighLevelGear = false,
    FilterUniqueUntradeable = false, FilterHQItems = false, FilterCollectables = false,
};
InventoryItemInfo Item(uint id, long? price, int quantity = 1, bool hq = false) => new()
{
    ItemId = id, Name = "Test material " + id, MarketPrice = price, Quantity = quantity,
    ItemUICategory = 20, CategoryName = "Materials", IsHQ = hq, CanBeDiscarded = true,
};
uint[] Visible(IEnumerable<InventoryItemInfo> items, SafetyFilters filters, bool ready = true) =>
    service.ApplyFilters(items, filters, blacklist, priceFiltersReady: ready).Select(i => i.ItemId).ToArray();
void Ids(IEnumerable<uint> actual, IEnumerable<uint> expected, string message) => Check(actual.SequenceEqual(expected), message);

var defaults = new SafetyFilters();
Check(!defaults.FilterMarketPriceAbove && !defaults.FilterMarketPriceBelow, "New filters default off for existing configurations");
Equal(defaults.MarketPriceAboveThreshold, 1000L, "Above default threshold");
Equal(defaults.MarketPriceBelowThreshold, 100L, "Below default threshold");
Equal(new InventorySettings().PriceMethod, MarketPriceMethod.LowestListing, "Existing lowest-listing pricing remains default");

var values = new long?[] { null, -1, 0, 99, 100, 101, 999, 1000, 1001, long.MaxValue };
var items = values.Select((price, index) => Item((uint)(10000 + index), price, 999)).ToList();
var f = Filters(); f.FilterMarketPriceAbove = true; f.MarketPriceAboveThreshold = 1000;
Ids(Visible(items, f), items.Skip(8).Select(i => i.ItemId), "Above shows only strictly more expensive known unit prices; equality and unknowns do not match");
f.FilterMarketPriceAbove = false; f.FilterMarketPriceBelow = true; f.MarketPriceBelowThreshold = 100;
Ids(Visible(items, f), new uint[] { 10003 }, "Below shows only known positive prices strictly below the threshold");
f.FilterMarketPriceAbove = true;
Ids(Visible(items, f), new uint[] { 10003, 10008, 10009 }, "Both filters show the OR union of strict low/high matches and exclude the middle band and unknowns");
Ids(Visible(items, f, false), items.Select(i => i.ItemId), "Saved enabled price filters are bypassed before refresh is complete");
Ids(service.ApplyFilters(items, f, blacklist).Select(i => i.ItemId), items.Select(i => i.ItemId), "Readiness defaults false, preventing implicit partial-price filtering");

foreach (var ready in new[] { false, true })
foreach (var above in new[] { false, true })
foreach (var below in new[] { false, true })
foreach (var threshold in new long[] { 0, 100, 1000, long.MaxValue })
{
    f.FilterMarketPriceAbove = above; f.FilterMarketPriceBelow = below;
    f.MarketPriceAboveThreshold = threshold; f.MarketPriceBelowThreshold = threshold;
    var visible = Visible(items, f, ready).ToHashSet();
    foreach (var item in items)
    {
        var known = item.MarketPrice is > 0;
        var match = known && ((above && item.MarketPrice > threshold) || (below && item.MarketPrice < threshold));
        var shouldShow = !ready || (!above && !below) || match;
        Equal(visible.Contains(item.ItemId), shouldShow, "Strict threshold matrix visibility");
        Equal(service.IsItemFiltered(item, f, blacklist, ready), !shouldShow, "Single-item filtering is the complement of shown matches");
    }
    Ids(service.GetProtectedItems(items, f, blacklist, ready).Select(i => i.ItemId), items.Where(i => !visible.Contains(i.ItemId)).Select(i => i.ItemId), "Hidden-items view uses the same readiness and thresholds");
}
f = Filters(); f.MarketPriceAboveThreshold = 1000; f.MarketPriceBelowThreshold = 100;
var counts = service.CountHiddenPerFilter(items, f, true);
Equal(counts.MarketPriceAbove, 2, "Above count reports matches independently of checkbox state");
Equal(counts.MarketPriceBelow, 1, "Below count reports matches independently of checkbox state and ignores unknowns");
Equal(service.CountHiddenPerFilter(items, f).MarketPriceAbove, 0, "Unready count does not imply a price filter is active");

f.FilterMarketPriceBelow = true; f.MarketPriceBelowThreshold = -10;
Equal(Visible(items, f).Length, 0, "Corrupt negative below threshold clamps to zero, which no positive price matches");
f.FilterMarketPriceAbove = true; f.MarketPriceAboveThreshold = -10;
Ids(Visible(items, f), items.Skip(3).Select(i => i.ItemId), "Clamped above-zero condition shows all positive prices without inventing unavailable prices");

var examples = new[]
{
    Item(24000, 400, 999), Item(24001, 499, 2), Item(24002, 500),
    Item(24003, 1000), Item(24004, 1001), Item(24005, null), Item(24006, -1),
};
f = Filters(); f.FilterMarketPriceBelow = true; f.MarketPriceBelowThreshold = 500;
Ids(Visible(examples, f), new uint[] { 24000, 24001 }, "User example <500 shows cheap unit prices even when a stack is worth hundreds of thousands");
f.FilterMarketPriceBelow = false; f.FilterMarketPriceAbove = true; f.MarketPriceAboveThreshold = 1000;
Ids(Visible(examples, f), new uint[] { 24004 }, "User example >1000 shows only strict high unit prices");
f.FilterMarketPriceBelow = true;
Ids(Visible(examples, f), new uint[] { 24000, 24001, 24004 }, "User examples together show <500 OR >1000, excluding both equal boundaries and unknowns");
f.FilterMarketPriceBelow = false; f.FilterMarketPriceAbove = false;
Ids(Visible(examples, f), examples.Select(i => i.ItemId), "Completed prices with no enabled price filters preserve unknown rows and all ordinary items");

f = Filters(); f.FilterMarketPriceAbove = true; f.MarketPriceAboveThreshold = 1000;
var normal = Item(20000, 100, 2); var normal2 = Item(20000, 100, 3); var high = Item(20000, 1000, 7, true);
normal.MarketPriceFetchTime = new DateTime(2026, 10, 14, 10, 0, 0);
normal.MarketDataUpdatedAtUtc = new DateTime(2026, 10, 14, 1, 0, 0, DateTimeKind.Utc);
normal.MarketPriceWorldName = "Test World";
normal.MarketPriceMethodName = "Lowest current listing";
var grouped = service.GroupIntoCategories(new[] { normal, normal2, high }).Single();
Equal(grouped.Items.Count, 2, "NQ and HQ are distinct price groups for one base ID");
Equal(grouped.Items.Single(i => !i.IsHQ).Quantity, 5, "Same-quality stacks still combine");
Equal(grouped.Items.Single(i => i.IsHQ).Quantity, 7, "HQ stack keeps its quantity");
Equal(grouped.Items.Single(i => !i.IsHQ).MarketPrice, 100L, "NQ group keeps NQ price");
Equal(grouped.Items.Single(i => i.IsHQ).MarketPrice, 1000L, "HQ group keeps HQ price");
Equal(grouped.TotalValue, 7500L, "Category total uses each quality's unit price");
Check(grouped.Items.All(i => i.ItemId == 20000), "Grouping preserves base IDs for existing selection semantics");
var normalGroup = grouped.Items.Single(i => !i.IsHQ);
Equal(normalGroup.MarketPriceFetchTime, normal.MarketPriceFetchTime, "Grouped row retains local refresh time");
Equal(normalGroup.MarketDataUpdatedAtUtc, normal.MarketDataUpdatedAtUtc, "Grouped row retains independent source-data age");
Equal(normalGroup.MarketPriceWorldName, normal.MarketPriceWorldName, "Grouped row retains world provenance");
Equal(normalGroup.MarketPriceMethodName, normal.MarketPriceMethodName, "Grouped row retains selected price method provenance");
f.MarketPriceAboveThreshold = 500;
var filteredGroup = service.GroupIntoCategories(service.ApplyFilters(new[] { normal, normal2, high }, f, blacklist, priceFiltersReady: true)).Single();
Equal(filteredGroup.Items.Count, 1, "Filtering before grouping retains only the matching quality's visible stacks");
Check(filteredGroup.Items.Single().IsHQ, "Above500 shows the 1000-gil HQ quality rather than the 100-gil NQ quality");
Equal(filteredGroup.TotalValue, 7000L, "Displayed HQ total cannot include nonmatching NQ stacks");
f.FilterMarketPriceAbove = false; f.FilterMarketPriceBelow = true; f.MarketPriceBelowThreshold = 500;
var lowGroup = service.GroupIntoCategories(service.ApplyFilters(new[] { normal, normal2, high }, f, blacklist, priceFiltersReady: true)).Single();
Check(!lowGroup.Items.Single().IsHQ && lowGroup.Items.Single().Quantity == 5, "Below500 shows only the matching NQ stacks for the same base ID");
Equal(lowGroup.TotalValue, 500L, "Displayed NQ total cannot include nonmatching HQ stacks");

f = Filters(); f.FilterHQItems = true; f.FilterMarketPriceBelow = true; f.MarketPriceBelowThreshold = 100;
Check(!Visible(new[] { high }, f, false).Any(), "Existing HQ filter works while price filters are pending");
var protectedCurrency = Item(1, 5000); var protectedCrystal = Item(22000, 5000); protectedCrystal.ItemUICategory = 63;
Check(!Visible(new[] { protectedCurrency, protectedCrystal }, Filters(), false).Any(), "Always-hidden protections are unaffected by readiness");
var gear = Item(23000, 5000); InventoryHelpers.Gearsets.Add(gear.ItemId);
f = Filters(); f.FilterGearsetItems = true;
Check(!Visible(new[] { gear }, f, false).Any(), "Existing gearset filter keeps its independent behavior");
Ids(service.ApplyFilters(new[] { normal, high }, Filters(), blacklist, "TEST MATERIAL", priceFiltersReady: true).Select(i => i.ItemId), new uint[] { 20000, 20000 }, "Case-insensitive search still works with completed prices");
Check(!service.ApplyFilters(new[] { normal }, Filters(), blacklist, equippablesOnly: true, priceFiltersReady: true).Any(), "Equipment-only filter remains independent");
Console.WriteLine($"Passed {checks} assertions for price boundaries, readiness, unknowns, combinations, existing filters, and NQ/HQ grouping.");
