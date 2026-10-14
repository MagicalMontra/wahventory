using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using wahventory.Core;

namespace wahventory.Services.External;

public readonly record struct PriceRequest(uint ItemId, bool IsHq);

/// <summary>A per-unit price for one quality, method, and selected world.</summary>
public sealed record MarketPriceResult
{
    public uint ItemId { get; init; }
    public bool IsHq { get; init; }
    public long Price { get; init; }
    public string WorldName { get; init; } = string.Empty;
    public MarketPriceMethod Method { get; init; }
    // Fetch time and the market board's upload time are deliberately separate.
    public DateTime LastUpdated { get; init; }
    public DateTime? MarketDataUpdatedAtUtc { get; init; }
}

public interface IPriceProvider : IDisposable
{
    Task<IReadOnlyDictionary<PriceRequest, MarketPriceResult>> GetPricesAsync(
        IReadOnlyList<PriceRequest> requests, MarketPriceMethod method, CancellationToken cancellationToken);
}
