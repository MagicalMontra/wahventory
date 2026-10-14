using wahventory.Models;
using wahventory.Modules.Inventory;

internal static class Program
{
    private static int _checks;
    private static int _scenarios;

    private static void Check(bool value, string description)
    {
        _checks++;
        if (!value) throw new Exception(description);
    }

    private static void SameIds(HashSet<uint> actual, IEnumerable<uint> expected, string description) =>
        Check(actual.SetEquals(expected), description);

    private static void Selected(InventoryState state, params uint[] expected) =>
        Check(state.SnapshotSelectedIds().ToHashSet().SetEquals(expected), "Selected IDs match exactly");

    private static void Run(string name, Action work)
    {
        _scenarios++;
        try
        {
            work();
            Console.WriteLine("PASS " + name);
        }
        catch (Exception error)
        {
            throw new Exception("FAIL " + name + ": " + error.Message, error);
        }
    }

    private static void Main()
    {
        Run("auto-discard add reports added and already present", () =>
        {
            var blacklist = new HashSet<uint> { 99 };
            var autoDiscard = new HashSet<uint> { 77 };
            Check(InventoryListPolicy.AddToAutoDiscard(100, blacklist, autoDiscard) == InventoryListAddResult.Added,
                "A new allowed ID is added");
            SameIds(autoDiscard, new uint[] { 77, 100 }, "Auto-discard preserves existing IDs");
            SameIds(blacklist, new uint[] { 99 }, "Auto-discard does not change blacklist");
            Check(InventoryListPolicy.AddToAutoDiscard(100, blacklist, autoDiscard) == InventoryListAddResult.AlreadyPresent,
                "A repeated allowed ID reports already present");
            SameIds(autoDiscard, new uint[] { 77, 100 }, "Repeated add does not duplicate or remove IDs");
        });

        Run("blacklist blocks auto-discard additions", () =>
        {
            var blacklist = new HashSet<uint> { 100 };
            var autoDiscard = new HashSet<uint> { 77 };
            Check(InventoryListPolicy.AddToAutoDiscard(100, blacklist, autoDiscard) == InventoryListAddResult.Blacklisted,
                "A protected ID reports blacklisted");
            SameIds(blacklist, new uint[] { 100 }, "Rejected addition preserves protection");
            SameIds(autoDiscard, new uint[] { 77 }, "Rejected addition never enters auto-discard");
        });

        Run("adding blacklist removes an auto-discard entry", () =>
        {
            var blacklist = new HashSet<uint> { 99 };
            var autoDiscard = new HashSet<uint> { 77, 100 };
            Check(InventoryListPolicy.AddToBlacklist(100, blacklist, autoDiscard) == InventoryListAddResult.Added,
                "New protection reports added");
            SameIds(blacklist, new uint[] { 99, 100 }, "Protection is added without losing existing entries");
            SameIds(autoDiscard, new uint[] { 77 }, "Protection removes matching auto-discard entry only");
        });

        Run("already blacklisted IDs still repair a conflicting auto-discard entry", () =>
        {
            var blacklist = new HashSet<uint> { 99, 100 };
            var autoDiscard = new HashSet<uint> { 77, 100 };
            Check(InventoryListPolicy.AddToBlacklist(100, blacklist, autoDiscard) == InventoryListAddResult.AlreadyPresent,
                "Existing protection reports already present");
            SameIds(blacklist, new uint[] { 99, 100 }, "Existing blacklist entries remain intact");
            SameIds(autoDiscard, new uint[] { 77 }, "Existing protection repairs overlap");
        });

        foreach (var method in new[] { "auto-discard", "blacklist" })
        {
            foreach (var blockedBy in new[] { "locked", "busy" })
            {
                Run(method + " add while " + blockedBy + " does not mutate either list", () =>
                {
                    var blacklist = new HashSet<uint> { 99, 100 };
                    var autoDiscard = new HashSet<uint> { 77, 100 };
                    var locked = blockedBy == "locked";
                    var busy = blockedBy == "busy";
                    foreach (var id in new uint[] { 200, 100 })
                    {
                        var result = method == "auto-discard"
                            ? InventoryListPolicy.AddToAutoDiscard(id, blacklist, autoDiscard, locked, busy)
                            : InventoryListPolicy.AddToBlacklist(id, blacklist, autoDiscard, locked, busy);
                        Check(result == (locked ? InventoryListAddResult.ListsLocked : InventoryListAddResult.Busy),
                            "Blocked operation returns its lock/busy result");
                        SameIds(blacklist, new uint[] { 99, 100 }, "Blocked operation preserves blacklist");
                        SameIds(autoDiscard, new uint[] { 77, 100 }, "Blocked operation preserves auto-discard, including existing overlap");
                    }
                });
            }

            Run(method + " rejects zero without mutation", () =>
            {
                var blacklist = new HashSet<uint> { 99 };
                var autoDiscard = new HashSet<uint> { 77 };
                var result = method == "auto-discard"
                    ? InventoryListPolicy.AddToAutoDiscard(0, blacklist, autoDiscard)
                    : InventoryListPolicy.AddToBlacklist(0, blacklist, autoDiscard);
                Check(result == InventoryListAddResult.InvalidItem, "Zero is not a valid item ID");
                SameIds(blacklist, new uint[] { 99 }, "Invalid item preserves blacklist");
                SameIds(autoDiscard, new uint[] { 77 }, "Invalid item preserves auto-discard");
            });
        }

        Run("normalization removes every overlap and is idempotent", () =>
        {
            var blacklist = new HashSet<uint> { 100, 200, 300 };
            var autoDiscard = new HashSet<uint> { 100, 200, 400, 500 };
            Check(InventoryListPolicy.Normalize(blacklist, autoDiscard) == 2, "Normalization reports removed overlaps");
            SameIds(blacklist, new uint[] { 100, 200, 300 }, "Normalization preserves all blacklist IDs");
            SameIds(autoDiscard, new uint[] { 400, 500 }, "Normalization preserves only unrelated auto-discard IDs");
            Check(InventoryListPolicy.Normalize(blacklist, autoDiscard) == 0, "Repeated normalization changes nothing");
            Check(InventoryListPolicy.Normalize(new(), new()) == 0, "Empty lists normalize cleanly");
        });

        Run("single selection accepts allowed IDs and deduplicates stacks", () =>
        {
            var f = new Fixture();
            Check(f.State.Select(f.Original[0], new()), "Allowed selection reports success");
            Check(f.Original[0].IsSelected, "Selected row receives its visual state");
            Check(f.State.Select(f.Original[1], new()), "Another stack of an allowed ID can be selected");
            Check(f.State.SelectedCount == 1, "Duplicate stacks have one selected item ID");
            Selected(f.State, 101);
        });

        Run("single selection refuses blacklist and removes stale selected ID", () =>
        {
            var f = new Fixture();
            f.SelectWithAllVisuals(101, 202);
            Check(!f.State.Select(f.Original[0], new() { 101 }), "Blacklisted selection reports failure");
            Check(!f.Original[0].IsSelected, "Refused row loses its selected visual state");
            Check(f.AllCopies.Where(i => i.ItemId == 101).All(i => !i.IsSelected), "Refused selection clears every matching stack and copy");
            Check(f.AllCopies.Where(i => i.ItemId == 202).All(i => i.IsSelected), "Refused selection preserves unrelated row visuals");
            Selected(f.State, 202);
            var protectedRow = new InventoryItemInfo { ItemId = 999, IsSelected = true };
            Check(!f.State.Select(protectedRow, new() { 999 }), "Untracked blacklisted row is also refused");
            Check(!protectedRow.IsSelected, "Refused untracked row is reset");
            Selected(f.State, 202);
        });

        Run("bulk selection skips blacklisted IDs and deduplicates stacks", () =>
        {
            var f = new Fixture();
            var category = new CategoryGroup { Items = f.Original.ToList() };
            f.State.SelectAllSelectableInCategory(category, new() { 202 });
            Selected(f.State, 101, 303);
            Check(f.Original.Where(i => i.ItemId != 202).All(i => i.IsSelected), "Every allowed stack is visually selected");
            Check(!f.Original.Single(i => i.ItemId == 202).IsSelected, "Blacklisted row is not visually selected");
            Check(f.State.AreAllSelectableSelected(category, new() { 202 }), "All allowed category IDs count as selected");
            Check(!f.State.AreAllSelectableSelected(category, new() { 101, 202, 303 }), "Fully protected category has no selectable rows");
        });

        Run("bulk selection prunes stale protected IDs across views", () =>
        {
            var f = new Fixture();
            f.SelectWithAllVisuals(202, 303);
            var category = new CategoryGroup { Items = f.CategoryCopies };
            f.State.SelectAllSelectableInCategory(category, new() { 202, 303 });
            Selected(f.State, 101);
            Check(f.AllCopies.Where(i => i.ItemId is 202 or 303).All(i => !i.IsSelected),
                "Bulk selection clears protected visible copies and filtered-out originals");
            Check(category.Items.Where(i => i.ItemId == 101).All(i => i.IsSelected),
                "Bulk selection still selects every allowed duplicate stack in the category");
        });

        Run("targeted deselection clears every matching copy and keeps unrelated IDs", () =>
        {
            var f = new Fixture();
            f.SelectWithAllVisuals(101, 202, 303);
            f.State.DeselectItems(new uint[] { 101, 101, 999 });
            Selected(f.State, 202, 303);
            Check(f.AllCopies.Where(i => i.ItemId == 101).All(i => !i.IsSelected),
                "Both stacks are reset in originals, filtered copies, and category copies");
            Check(f.AllCopies.Where(i => i.ItemId != 101).All(i => i.IsSelected),
                "Targeted deselection preserves unrelated selected visuals, including hidden originals");
            f.State.DeselectItems(Array.Empty<uint>());
            Selected(f.State, 202, 303);
        });

        Run("targeted deselection clears a filtered-out original", () =>
        {
            var f = new Fixture();
            f.SelectWithAllVisuals(101, 303);
            f.State.DeselectItems(new uint[] { 303 });
            Selected(f.State, 101);
            Check(!f.Original.Single(i => i.ItemId == 303).IsSelected, "Filtered-out original visual state is reset");
            Check(f.AllCopies.Where(i => i.ItemId == 101).All(i => i.IsSelected), "Visible unrelated stacks remain selected");
        });

        Run("normalized auto-discard candidates exclude protected duplicate stacks", () =>
        {
            var f = new Fixture();
            var blacklist = new HashSet<uint> { 101 };
            var autoDiscard = new HashSet<uint> { 101, 202 };
            InventoryListPolicy.Normalize(blacklist, autoDiscard);
            var candidates = f.State.SnapshotAutoDiscardCandidates(autoDiscard, blacklist);
            Check(candidates.Count == 1 && candidates[0].ItemId == 202, "Only allowed inventory stacks become candidates");
            Check(f.State.SnapshotAutoDiscardCandidates(new() { 101, 202 }, blacklist).All(i => i.ItemId != 101),
                "Candidate filtering also protects unnormalized overlaps");
        });

        Console.WriteLine($"Inventory list regression passed: {_checks} assertions in {_scenarios} scenarios.");
    }

    private sealed class Fixture
    {
        public readonly InventoryState State = new();
        public readonly List<InventoryItemInfo> Original = new()
        {
            new() { ItemId = 101, Name = "First stack", Slot = 1, CanBeDiscarded = true },
            new() { ItemId = 101, Name = "Second stack", Slot = 2, CanBeDiscarded = true },
            new() { ItemId = 202, Name = "Unrelated visible item", Slot = 3, CanBeDiscarded = true },
            new() { ItemId = 303, Name = "Filtered-out original", Slot = 4, CanBeDiscarded = true }
        };
        public List<InventoryItemInfo> FilteredCopies = new();
        public List<InventoryItemInfo> CategoryCopies = new();
        public IEnumerable<InventoryItemInfo> AllCopies => Original.Concat(FilteredCopies).Concat(CategoryCopies);

        public Fixture()
        {
            State.ApplyRefreshAndRecategorize(Original, _ => { }, items =>
            {
                FilteredCopies = items.Where(i => i.ItemId != 303).Select(Copy).ToList();
                return FilteredCopies;
            }, items =>
            {
                CategoryCopies = items.Select(Copy).ToList();
                return new() { new() { Name = "Visible", Items = CategoryCopies } };
            });
        }

        public void SelectWithAllVisuals(params uint[] ids)
        {
            foreach (var id in ids) State.Select(Original.First(i => i.ItemId == id), new());
            foreach (var item in AllCopies) item.IsSelected = ids.Contains(item.ItemId);
        }

        private static InventoryItemInfo Copy(InventoryItemInfo item) => new()
        {
            ItemId = item.ItemId,
            Name = item.Name,
            Slot = item.Slot,
            CanBeDiscarded = item.CanBeDiscarded,
            IsSelected = item.IsSelected
        };
    }
}
