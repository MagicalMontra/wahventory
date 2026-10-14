namespace Dalamud.Plugin.Services
{
    public interface IPluginLog
    {
        void Warning(string message);
        void Error(Exception exception, string message);
    }
}
namespace wahventory.Core
{
    public enum MarketPriceMethod { LowestListing, AverageSalePrice }
    public sealed class InventorySettings
    {
        public MarketPriceMethod PriceMethod { get; set; }
        public int PriceCacheDurationMinutes { get; set; } = 30;
    }
}
namespace wahventory.Models
{
    public sealed class InventoryItemInfo
    {
        public uint ItemId { get; set; }
        public bool IsHQ { get; set; }
        public bool CanBeTraded { get; set; } = true;
        public long? MarketPrice { get; set; }
        public DateTime? MarketPriceFetchTime { get; set; }
        public DateTime? MarketDataUpdatedAtUtc { get; set; }
        public string? MarketPriceWorldName { get; set; }
        public string? MarketPriceMethodName { get; set; }
    }
}
