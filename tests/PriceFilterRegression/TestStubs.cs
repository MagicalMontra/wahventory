namespace Dalamud.Configuration
{
    public interface IPluginConfiguration { int Version { get; set; } }
}
namespace FFXIVClientStructs.FFXIV.Client.Game
{
    public enum InventoryType { Inventory1, Inventory2, Inventory3, Inventory4 }
}
namespace wahventory.Services.Helpers
{
    public static class InventoryHelpers
    {
        public static readonly HashSet<uint> Gearsets = new();
        public static bool IsInGearset(uint itemId) => Gearsets.Contains(itemId);
    }
}
