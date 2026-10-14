using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using wahventory.Core;

namespace wahventory.Services.External;

public sealed class UniversalisClient : IPriceProvider
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IPluginLog _log;
    private readonly string _worldName;
    private readonly uint? _worldId;

    public UniversalisClient(IPluginLog log, string worldName, uint? worldId = null, HttpClient? httpClient = null)
    {
        _log = log;
        _worldName = worldName;
        _worldId = worldId;
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("WahventoryFFXIVPlugin/1.0");
    }

    public async Task<MarketPriceResult?> GetMarketPrice(uint itemId, bool hq = false)
    {
        var request = new PriceRequest(itemId, hq);
        var results = await GetPricesAsync(new[] { request }, MarketPriceMethod.LowestListing, CancellationToken.None)
            .ConfigureAwait(false);
        return results.GetValueOrDefault(request);
    }

    public async Task<IReadOnlyDictionary<PriceRequest, MarketPriceResult>> GetPricesAsync(
        IReadOnlyList<PriceRequest> requests, MarketPriceMethod method, CancellationToken cancellationToken)
    {
        var results = new Dictionary<PriceRequest, MarketPriceResult>();
        var wanted = requests.Where(r => r.ItemId != 0).Distinct().ToArray();
        if (wanted.Length == 0 || string.IsNullOrWhiteSpace(_worldName)) return results;
        // Universalis supports up to 100 distinct item IDs per aggregated request.
        foreach (var ids in wanted.Select(r => r.ItemId).Distinct().Chunk(100))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var idList = string.Join(",", ids.Select(id => id.ToString(CultureInfo.InvariantCulture)));
                var url = $"https://universalis.app/api/v2/aggregated/{Uri.EscapeDataString(_worldName)}/{idList}";
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
                    requestTimeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _log.Warning($"Universalis price request failed: {response.StatusCode}");
                    continue;
                }
                await using var stream = await response.Content.ReadAsStreamAsync(requestTimeout.Token).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: requestTimeout.Token)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                ParseResults(document.RootElement, wanted.Where(r => ids.Contains(r.ItemId)), method, results);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _log.Warning("Universalis price request timed out; these prices remain unknown.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Error(ex, "Unable to read Universalis prices; these prices remain unknown.");
            }
        }
        return results;
    }

    private void ParseResults(JsonElement root, IEnumerable<PriceRequest> requests, MarketPriceMethod method,
        Dictionary<PriceRequest, MarketPriceResult> results)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var items) ||
            items.ValueKind != JsonValueKind.Array) return;
        var wanted = requests.ToHashSet();
        var failed = new HashSet<uint>();
        if (root.TryGetProperty("failedItems", out var failures) && failures.ValueKind == JsonValueKind.Array)
            foreach (var failure in failures.EnumerateArray())
                if (failure.ValueKind == JsonValueKind.Number && failure.TryGetUInt32(out var id)) failed.Add(id);
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("itemId", out var idValue) ||
                idValue.ValueKind != JsonValueKind.Number || !idValue.TryGetUInt32(out var itemId) || failed.Contains(itemId)) continue;
            foreach (var request in wanted.Where(r => r.ItemId == itemId))
            {
                if (!item.TryGetProperty(request.IsHq ? "hq" : "nq", out var quality) ||
                    quality.ValueKind != JsonValueKind.Object ||
                    !quality.TryGetProperty(method == MarketPriceMethod.AverageSalePrice ? "averageSalePrice" : "minListing", out var source) ||
                    source.ValueKind != JsonValueKind.Object || !source.TryGetProperty("world", out var world) ||
                    world.ValueKind != JsonValueKind.Object || !world.TryGetProperty("price", out var value) ||
                    value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var price) ||
                    price <= 0 || price > long.MaxValue) continue;
                results[request] = new MarketPriceResult
                {
                    ItemId = itemId, IsHq = request.IsHq, WorldName = _worldName, Method = method,
                    // Keep displayed whole-gil prices and filter comparisons consistent, conservatively rounded up.
                    Price = (long)decimal.Ceiling(price), LastUpdated = DateTime.UtcNow,
                    MarketDataUpdatedAtUtc = ReadUploadTime(item),
                };
            }
        }
    }

    private DateTime? ReadUploadTime(JsonElement item)
    {
        if (!_worldId.HasValue || !item.TryGetProperty("worldUploadTimes", out var uploads) ||
            uploads.ValueKind != JsonValueKind.Array) return null;
        foreach (var upload in uploads.EnumerateArray())
        {
            if (upload.ValueKind != JsonValueKind.Object || !upload.TryGetProperty("worldId", out var id) ||
                id.ValueKind != JsonValueKind.Number || !id.TryGetUInt32(out var worldId) || worldId != _worldId.Value ||
                !upload.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.Number ||
                !timestamp.TryGetInt64(out var millis) || millis <= 0) continue;
            try { return DateTimeOffset.FromUnixTimeMilliseconds(millis).UtcDateTime; }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        return null;
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
