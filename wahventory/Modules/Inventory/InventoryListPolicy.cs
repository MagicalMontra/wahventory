using System.Collections.Generic;

namespace wahventory.Modules.Inventory;

public enum InventoryListAddResult
{
    Added,
    AlreadyPresent,
    Blacklisted,
    ListsLocked,
    Busy,
    InvalidItem,
}

internal static class InventoryListPolicy
{
    internal static InventoryListAddResult AddToAutoDiscard(uint itemId,
        HashSet<uint> blacklist, HashSet<uint> autoDiscard, bool listsLocked = false, bool busy = false)
    {
        if (busy) return InventoryListAddResult.Busy;
        if (listsLocked) return InventoryListAddResult.ListsLocked;
        if (itemId == 0) return InventoryListAddResult.InvalidItem;
        if (blacklist.Contains(itemId)) return InventoryListAddResult.Blacklisted;
        return autoDiscard.Add(itemId) ? InventoryListAddResult.Added : InventoryListAddResult.AlreadyPresent;
    }

    internal static InventoryListAddResult AddToBlacklist(uint itemId,
        HashSet<uint> blacklist, HashSet<uint> autoDiscard, bool listsLocked = false, bool busy = false)
    {
        if (busy) return InventoryListAddResult.Busy;
        if (listsLocked) return InventoryListAddResult.ListsLocked;
        if (itemId == 0) return InventoryListAddResult.InvalidItem;
        autoDiscard.Remove(itemId);
        return blacklist.Add(itemId) ? InventoryListAddResult.Added : InventoryListAddResult.AlreadyPresent;
    }

    internal static int Normalize(HashSet<uint> blacklist, HashSet<uint> autoDiscard)
    {
        var count = autoDiscard.Count;
        autoDiscard.ExceptWith(blacklist);
        return count - autoDiscard.Count;
    }
}
