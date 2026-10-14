using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace wahventory.Core;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public InventorySettings InventorySettings { get; set; } = new();

    [NonSerialized]
    public Action? Save;
}

[Serializable]
public class InventorySettings
{
    public SafetyFilters SafetyFilters { get; set; } = new();

    public bool ShowMarketPrices { get; set; } = false;
    public bool EquippablesOnly { get; set; } = false;
    public int PriceCacheDurationMinutes { get; set; } = 30;
    public bool AutoRefreshPrices { get; set; } = true;
    public MarketPriceMethod PriceMethod { get; set; } = MarketPriceMethod.LowestListing;

    public Dictionary<uint, bool> ExpandedCategories { get; set; } = new();

    public PassiveDiscardSettings PassiveDiscard { get; set; } = new();

    public ListSyncSettings ListSync { get; set; } = new();
}

public enum MarketPriceMethod
{
    LowestListing,
    AverageSalePrice,
}

[Serializable]
public class ListSyncSettings
{
    public bool Enabled { get; set; } = false;
    public string Url { get; set; } = string.Empty;
    public int CheckIntervalMinutes { get; set; } = 30;

    public string? LastAppliedHash { get; set; }
    public string? SkippedHash { get; set; }
    public string? ETag { get; set; }

    public string? PreSyncBackup { get; set; }

    public string? RemoteName { get; set; }
    public int? RemoteRevision { get; set; }
    public DateTime? LastAppliedAt { get; set; }
    public DateTime? LastCheckedAt { get; set; }
}

[Serializable]
public class SafetyFilters
{
    public bool FilterGearsetItems { get; set; } = true;
    public bool FilterIndisposableItems { get; set; } = true;
    public bool FilterHighLevelGear { get; set; } = true;
    public bool FilterUniqueUntradeable { get; set; } = true;
    public bool FilterHQItems { get; set; } = false;
    public bool FilterCollectables { get; set; } = false;
    public bool FilterMarketPriceAbove { get; set; } = false;
    public long MarketPriceAboveThreshold { get; set; } = 1000;
    public bool FilterMarketPriceBelow { get; set; } = false;
    public long MarketPriceBelowThreshold { get; set; } = 100;

    public uint MaxGearItemLevel { get; set; } = 600;
}

[Serializable]
public class PassiveDiscardSettings
{
    public bool Enabled { get; set; } = false;
    public int IdleTimeSeconds { get; set; } = 30;
    public int DiscardIntervalSeconds { get; set; } = 5;
}
