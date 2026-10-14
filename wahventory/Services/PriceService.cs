using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using wahventory.Core;
using wahventory.Models;
using wahventory.Services.External;

namespace wahventory.Services;

/// <summary>Fetches immutable snapshots. Inventory rows only change on UpdateItemPrice's calling thread.</summary>
public sealed class PriceService : IDisposable
{
    private readonly object _sync = new();
    private readonly IPluginLog _log;
    private readonly InventorySettings _settings;
    private readonly Func<string, uint?, IPriceProvider> _providerFactory;
    private readonly TimeSpan _refreshTimeout;
    private readonly TimeSpan _unknownCacheDuration = TimeSpan.FromSeconds(30);
    private readonly Dictionary<CacheKey, CacheEntry> _cache = new();
    private readonly HashSet<PriceRequest> _pending = new();
    private readonly SemaphoreSlim _legacyFetchGate = new(1, 1);
    private IPriceProvider _provider;
    private string _currentWorld;
    private uint? _worldId;
    private MarketPriceMethod _method;
    private CancellationTokenSource? _refreshCancellation;
    private Task _refreshTask = Task.CompletedTask;
    private HashSet<PriceRequest>? _snapshot;
    private long _generation;
    private long _runId;
    private long _resultsVersion;
    private bool _isRefreshing;
    private bool _hasCompletedRefresh;
    private int _completedCount;
    private int _totalCount;
    private bool _disposed;

    private readonly record struct CacheKey(string World, uint ItemId, bool IsHq, MarketPriceMethod Method);
    private sealed record CacheEntry(MarketPriceResult? Result, DateTime FetchedAtUtc);

    public PriceService(IPluginLog log, InventorySettings settings, string initialWorld,
        uint? initialWorldId = null, Func<string, uint?, IPriceProvider>? providerFactory = null,
        TimeSpan? refreshTimeout = null)
    {
        _log = log;
        _settings = settings;
        _currentWorld = initialWorld;
        _worldId = initialWorldId;
        _method = settings.PriceMethod;
        _providerFactory = providerFactory ?? ((world, id) => new UniversalisClient(log, world, id));
        _provider = _providerFactory(initialWorld, initialWorldId);
        _refreshTimeout = refreshTimeout ?? TimeSpan.FromSeconds(90);
    }

    public bool IsRefreshing { get { SynchronizeMethod(); lock (_sync) return _isRefreshing; } }
    public bool HasCompletedRefresh { get { SynchronizeMethod(); lock (_sync) return _hasCompletedRefresh; } }
    public bool IsRefreshComplete => HasCompletedRefresh && !IsRefreshing;
    public int CompletedCount { get { lock (_sync) return _completedCount; } }
    public int TotalCount { get { lock (_sync) return _totalCount; } }
    public long ResultsVersion { get { SynchronizeMethod(); lock (_sync) return _resultsVersion; } }
    public Task RefreshTask { get { lock (_sync) return _refreshTask; } }

    public void UpdateWorld(string worldName, uint? worldId = null)
    {
        if (string.IsNullOrWhiteSpace(worldName)) throw new ArgumentException("A world is required.", nameof(worldName));
        CancellationTokenSource? cancellation;
        IPriceProvider previous;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (string.Equals(worldName, _currentWorld, StringComparison.OrdinalIgnoreCase) && worldId == _worldId) return;
            var replacement = _providerFactory(worldName, worldId);
            previous = _provider;
            _provider = replacement;
            _currentWorld = worldName;
            _worldId = worldId;
            cancellation = InvalidateLocked();
        }
        Cancel(previous: cancellation);
        previous.Dispose();
    }

    public void NotifyPricingChanged() => ClearCache();

    private static void Cancel(CancellationTokenSource? previous)
    {
        try { previous?.Cancel(); }
        catch (ObjectDisposedException) { } // A finishing task can dispose its token before the replacement cancels it.
    }

    private void SynchronizeMethod()
    {
        CancellationTokenSource? cancellation = null;
        lock (_sync)
        {
            if (!_disposed && _method != _settings.PriceMethod)
            {
                _method = _settings.PriceMethod;
                cancellation = InvalidateLocked();
            }
        }
        Cancel(cancellation);
    }

    private CancellationTokenSource? InvalidateLocked()
    {
        var cancellation = _refreshCancellation;
        _refreshCancellation = null;
        ++_generation;
        _cache.Clear();
        _pending.Clear();
        _snapshot = null;
        _isRefreshing = false;
        _hasCompletedRefresh = false;
        _completedCount = _totalCount = 0;
        ++_resultsVersion;
        return cancellation;
    }

    public long BeginRefresh(IEnumerable<InventoryItemInfo> snapshot, bool force = false)
    {
        // Copy on the calling thread; background work never keeps mutable inventory objects.
        var requests = snapshot.Where(i => i.CanBeTraded && i.ItemId != 0)
            .Select(i => new PriceRequest(i.ItemId, i.IsHQ)).ToHashSet();
        return BeginRequests(requests, force);
    }

    private long BeginRequests(HashSet<PriceRequest> requests, bool force)
    {
        SynchronizeMethod();
        CancellationTokenSource? previous;
        long runId;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!force && _snapshot != null && _snapshot.SetEquals(requests) &&
                (_isRefreshing || (_hasCompletedRefresh && requests.All(HasFreshAttemptLocked)))) return _runId;
            previous = _refreshCancellation;
            var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(_refreshTimeout);
            _refreshCancellation = cancellation;
            var generation = ++_generation;
            runId = ++_runId;
            _snapshot = requests;
            _pending.Clear();
            foreach (var request in requests)
                if (force || !HasFreshAttemptLocked(request)) _pending.Add(request);
            _totalCount = requests.Count;
            _completedCount = _totalCount - _pending.Count;
            _hasCompletedRefresh = false;
            _isRefreshing = _pending.Count != 0;
            if (!_isRefreshing)
            {
                _hasCompletedRefresh = true;
                ++_resultsVersion;
                _refreshTask = Task.CompletedTask;
                _refreshCancellation = null;
                cancellation.Dispose();
            }
            else
            {
                var work = _pending.ToArray();
                var provider = _provider;
                var world = _currentWorld;
                var method = _method;
                _refreshTask = Task.Run(() => RunRefreshAsync(generation, work, provider, world, method, cancellation));
            }
        }
        Cancel(previous);
        return runId;
    }

    private async Task RunRefreshAsync(long generation, PriceRequest[] requests, IPriceProvider provider,
        string world, MarketPriceMethod method, CancellationTokenSource cancellation)
    {
        try
        {
            // At most 100 quality pairs per chunk. Calls are paced, never silently dropped.
            var chunks = requests.Chunk(100).ToArray();
            for (var i = 0; i < chunks.Length; ++i)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var results = await provider.GetPricesAsync(chunks[i], method, cancellation.Token)
                    .WaitAsync(cancellation.Token).ConfigureAwait(false);
                cancellation.Token.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    if (!CanCommitLocked(generation, world, method)) return;
                    foreach (var request in chunks[i])
                    {
                        results.TryGetValue(request, out var result);
                        if (result != null && (result.Price <= 0 || result.ItemId != request.ItemId ||
                            result.IsHq != request.IsHq || result.Method != method ||
                            !string.Equals(result.WorldName, world, StringComparison.OrdinalIgnoreCase))) result = null;
                        _cache[Key(request)] = new CacheEntry(result, DateTime.UtcNow);
                        _pending.Remove(request);
                    }
                    _completedCount = _totalCount - _pending.Count;
                    ++_resultsVersion;
                }
                if (i + 1 < chunks.Length)
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Error(ex, "Price refresh failed; unavailable prices remain unknown."); }
        finally
        {
            lock (_sync)
            {
                if (CanCommitLocked(generation, world, method))
                {
                    // Failed or timed out requests are settled unknown, never a zero/cheap price.
                    foreach (var request in _pending) _cache[Key(request)] = new CacheEntry(null, DateTime.UtcNow);
                    _pending.Clear();
                    _completedCount = _totalCount;
                    _isRefreshing = false;
                    _hasCompletedRefresh = true;
                    _refreshCancellation = null;
                    ++_resultsVersion;
                }
            }
            cancellation.Dispose();
        }
    }

    private bool CanCommitLocked(long generation, string world, MarketPriceMethod method) =>
        !_disposed && generation == _generation && method == _method && method == _settings.PriceMethod &&
        string.Equals(world, _currentWorld, StringComparison.OrdinalIgnoreCase);

    private CacheKey Key(PriceRequest request) => new(_currentWorld.ToUpperInvariant(), request.ItemId, request.IsHq, _method);
    private bool IsFresh(CacheEntry entry) => DateTime.UtcNow - entry.FetchedAtUtc <=
        (entry.Result == null ? _unknownCacheDuration : TimeSpan.FromMinutes(Math.Max(0, _settings.PriceCacheDurationMinutes)));
    private bool HasFreshAttemptLocked(PriceRequest request) => _cache.TryGetValue(Key(request), out var entry) && IsFresh(entry);

    public MarketPriceResult? GetCachedResult(uint itemId, bool hq = false)
    {
        SynchronizeMethod();
        lock (_sync) return _cache.TryGetValue(Key(new PriceRequest(itemId, hq)), out var entry) && IsFresh(entry) ? entry.Result : null;
    }
    public bool HasCachedResult(uint itemId, bool hq = false)
    {
        SynchronizeMethod();
        lock (_sync) return HasFreshAttemptLocked(new PriceRequest(itemId, hq));
    }
    public bool HasValidCachedPrice(uint itemId, bool hq = false) => GetCachedResult(itemId, hq) != null;
    public long? GetCachedPrice(uint itemId, bool hq = false) => GetCachedResult(itemId, hq)?.Price;
    public bool IsFetchingPrice(uint itemId)
    {
        SynchronizeMethod();
        lock (_sync) return _pending.Any(r => r.ItemId == itemId);
    }
    public bool IsFetchingPrice(uint itemId, bool hq)
    {
        SynchronizeMethod();
        lock (_sync) return _pending.Contains(new PriceRequest(itemId, hq));
    }

    public async Task<long?> FetchPrice(InventoryItemInfo item)
    {
        var request = new PriceRequest(item.ItemId, item.IsHQ);
        if (!item.CanBeTraded || item.ItemId == 0) return null;
        await _legacyFetchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Task current;
            lock (_sync) { if (_disposed) return null; current = _refreshTask; }
            await current.ConfigureAwait(false);
            if (!HasCachedResult(request.ItemId, request.IsHq))
            {
                BeginRequests(new HashSet<PriceRequest> { request }, false);
                await RefreshTask.ConfigureAwait(false);
            }
            return GetCachedPrice(request.ItemId, request.IsHq);
        }
        catch (ObjectDisposedException) { return null; }
        finally { _legacyFetchGate.Release(); }
    }

    public void UpdateItemPrice(InventoryItemInfo item)
    {
        SynchronizeMethod();
        lock (_sync)
        {
            _cache.TryGetValue(Key(new PriceRequest(item.ItemId, item.IsHQ)), out var entry);
            if (entry != null && !IsFresh(entry)) entry = null;
            item.MarketPrice = entry?.Result?.Price;
            item.MarketPriceFetchTime = entry?.FetchedAtUtc;
            item.MarketDataUpdatedAtUtc = entry?.Result?.MarketDataUpdatedAtUtc;
            item.MarketPriceWorldName = entry != null ? _currentWorld : null;
            item.MarketPriceMethodName = entry != null ? (_method == MarketPriceMethod.LowestListing
                ? "Lowest current listing" : "Average sale price (4 days)") : null;
        }
    }

    public void CleanupStuckFetches() => SynchronizeMethod(); // Actual requests own cancellation and a deadline.
    public void ClearCache()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (_disposed) return;
            _method = _settings.PriceMethod;
            cancellation = InvalidateLocked();
        }
        Cancel(cancellation);
    }
    public List<InventoryItemInfo> GetItemsNeedingPriceFetch(IEnumerable<InventoryItemInfo> visibleItems, int maxCount = 2) =>
        visibleItems.Where(i => i.CanBeTraded && !IsFetchingPrice(i.ItemId, i.IsHQ) && !HasCachedResult(i.ItemId, i.IsHQ))
            .Take(maxCount).ToList();
    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            cancellation = InvalidateLocked();
        }
        Cancel(cancellation);
        _provider.Dispose();
    }
}
