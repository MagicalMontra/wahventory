using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;
using wahventory.Core;
using wahventory.Models;
using wahventory.Services;
using wahventory.Services.External;

var suite = new PricingTests();
await suite.Run();

sealed class PricingTests
{
    private int _checks;
    private int _scenarios;
    private readonly Log _log = new();
    private void Check(bool value, string message)
    {
        ++_checks;
        if (!value) throw new InvalidOperationException(message);
    }
    private void Scenario() => ++_scenarios;
    private static InventoryItemInfo Item(uint id, bool hq = false) => new() { ItemId = id, IsHQ = hq };
    private PriceService Service(ControlledProvider provider, InventorySettings? settings = null, TimeSpan? timeout = null) =>
        new(_log, settings ?? new(), provider.World, 75, (_, _) => provider, timeout);
    private static async Task Finish(PriceService service) => await service.RefreshTask.WaitAsync(TimeSpan.FromSeconds(3));

    public async Task Run()
    {
        await ProviderParsing();
        await CacheAndCoalescing();
        await MissingAndWrongResults();
        await WorldGeneration();
        await MethodGeneration();
        await TimeoutAndDispose();
        await BatchProgress();
        await ExpirationAndSupersession();
        await LegacyConcurrent();
        Console.WriteLine($"Pricing regression: {_checks} assertions across {_scenarios} scenarios passed.");
    }

    private async Task ProviderParsing()
    {
        Scenario();
        var handler = new FakeHttp();
        using var http = new HttpClient(handler);
        using var client = new UniversalisClient(_log, "Test World", 75, http);
        handler.Json = Payload(new[] { Row(10, 100, 900), Row(11, null, 500) });
        var prices = await client.GetPricesAsync(new[] { new PriceRequest(10, false), new PriceRequest(10, true),
            new PriceRequest(11, false), new PriceRequest(11, true), new PriceRequest(12, false) },
            MarketPriceMethod.LowestListing, CancellationToken.None);
        Check(prices.Count == 3, "Only exact available item/quality matches are returned.");
        Check(prices[new(10, false)].Price == 100, "NQ never uses the cheaper HQ/DC price.");
        Check(prices[new(10, true)].Price == 900, "HQ remains independent.");
        Check(!prices.ContainsKey(new(11, false)), "Missing NQ world data never falls back to HQ/DC.");
        Check(!prices.ContainsKey(new(12, false)), "Unrequested/missing IDs do not appear.");
        Check(handler.Urls.Single().Contains("/aggregated/Test%20World/10,11,12"), "Single/aggregate request uses encoded world path.");
        var result = prices[new(10, false)];
        Check(result.IsHq == false && result.Method == MarketPriceMethod.LowestListing && result.WorldName == "Test World", "Result retains full key.");
        Check(result.MarketDataUpdatedAtUtc == DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            "Upload metadata matches selected world, not newer unrelated world.");
        Check(result.LastUpdated.Kind == DateTimeKind.Utc && result.LastUpdated > result.MarketDataUpdatedAtUtc,
            "Fetch time remains separate from provider upload time.");

        Scenario();
        handler.Json = Payload(new[] { Row(10, 100, 900, 123.2m, 999.5m) });
        prices = await client.GetPricesAsync(new[] { new PriceRequest(10, false), new PriceRequest(10, true) },
            MarketPriceMethod.AverageSalePrice, CancellationToken.None);
        Check(prices[new(10, false)].Price == 124 && prices[new(10, true)].Price == 1000, "Weighted averages conservatively round up per-unit gil.");
        Check(prices.Values.All(p => p.Method == MarketPriceMethod.AverageSalePrice), "Average method is retained.");
        Check(handler.Urls.Count == 2, "A single requested ID retains aggregate-shaped parsing.");

        Scenario();
        handler.Json = Payload(new[] { Row(10, 0, -2), Row(11, 300, 500) }, new[] { 11u });
        prices = await client.GetPricesAsync(new[] { new PriceRequest(10, false), new PriceRequest(10, true), new PriceRequest(11, false) },
            MarketPriceMethod.LowestListing, CancellationToken.None);
        Check(prices.Count == 0, "Zero/negative prices and failedItems remain unknown.");
        handler.Json = "{\"results\":[{\"itemId\":\"10\",\"nq\":null},null],\"failedItems\":[\"bad\"]}";
        prices = await client.GetPricesAsync(new[] { new PriceRequest(10, false) }, MarketPriceMethod.LowestListing, CancellationToken.None);
        Check(prices.Count == 0, "Malformed item data does not become a price.");
        handler.Json = "not json";
        Check((await client.GetPricesAsync(new[] { new PriceRequest(10, false) }, MarketPriceMethod.LowestListing, CancellationToken.None)).Count == 0,
            "Malformed JSON is safely unknown.");
        handler.Status = HttpStatusCode.ServiceUnavailable;
        Check((await client.GetPricesAsync(new[] { new PriceRequest(10, false) }, MarketPriceMethod.LowestListing, CancellationToken.None)).Count == 0,
            "HTTP failures are safely unknown.");

        Scenario();
        handler.Status = HttpStatusCode.OK;
        handler.Json = Payload(new[] { Row(10, 300, 500) });
        using var unknownWorld = new UniversalisClient(_log, "Test World", null, http);
        var price = await unknownWorld.GetMarketPrice(10);
        Check(price?.Price == 300 && price.MarketDataUpdatedAtUtc == null, "No world ID means source age is unknown, not guessed.");
        handler.Urls.Clear();
        handler.Json = Payload(Enumerable.Range(1, 101).Select(id => Row((uint)id, id, id)));
        prices = await client.GetPricesAsync(Enumerable.Range(1, 101).Select(id => new PriceRequest((uint)id, false)).ToArray(),
            MarketPriceMethod.LowestListing, CancellationToken.None);
        Check(handler.Urls.Count == 2 && prices.Count == 101, "Provider splits over 100 IDs and merges exact results.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await client.GetPricesAsync(new[] { new PriceRequest(10, false) }, MarketPriceMethod.LowestListing, cancelled.Token); Check(false, "Expected cancellation."); }
        catch (OperationCanceledException) { Check(true, "External cancellation is propagated."); }
    }

    private async Task CacheAndCoalescing()
    {
        Scenario();
        var provider = new ControlledProvider("Alpha");
        using var service = Service(provider);
        Check(!service.IsRefreshing && !service.HasCompletedRefresh && !service.IsRefreshComplete, "Unstarted is distinct from completed.");
        var nq = Item(1); var hq = Item(1, true);
        var run = service.BeginRefresh(new[] { nq, hq, Item(1), new InventoryItemInfo { ItemId = 2, CanBeTraded = false } });
        Check(service.IsRefreshing && !service.HasCompletedRefresh && service.TotalCount == 2 && service.CompletedCount == 0,
            "Begin synchronously marks the full unique tradable quality snapshot busy.");
        var call = await provider.Next();
        Check(call.Requests.Length == 2 && service.IsFetchingPrice(1) && service.IsFetchingPrice(1, true), "Both qualities have distinct pending keys.");
        Check(service.BeginRefresh(new[] { hq, Item(1), nq }) == run, "Identical reordered/duplicate snapshot coalesces while running.");
        Check(provider.CallCount == 1, "Coalesced refresh does not cancel/restart work.");
        var before = service.ResultsVersion;
        call.Complete(100, 800);
        await Finish(service);
        Check(!service.IsRefreshing && service.HasCompletedRefresh && service.IsRefreshComplete, "Whole batch only becomes ready after settlement.");
        Check(service.CompletedCount == 2 && service.TotalCount == 2 && service.ResultsVersion > before, "Final settlement publishes progress/version.");
        Check(service.GetCachedPrice(1, false) == 100 && service.GetCachedPrice(1, true) == 800, "HQ/NQ cache prices do not collide.");
        Check(nq.MarketPrice == null && hq.MarketPrice == null, "Async completion never writes live inventory rows.");
        service.UpdateItemPrice(nq); service.UpdateItemPrice(hq);
        Check(nq.MarketPrice == 100 && hq.MarketPrice == 800, "Explicit synchronous application selects exact quality.");
        Check(nq.MarketPriceFetchTime?.Kind == DateTimeKind.Utc && nq.MarketPriceWorldName == "Alpha" &&
            nq.MarketPriceMethodName == "Lowest current listing" && nq.MarketDataUpdatedAtUtc.HasValue, "Application retains truthful metadata.");
        Check(service.BeginRefresh(new[] { Item(1, true), Item(1) }) == run && provider.CallCount == 1, "Fresh completed identical snapshot coalesces.");
        var expanded = service.BeginRefresh(new[] { Item(1), Item(1, true), Item(2) });
        var extra = await provider.Next();
        Check(expanded > run && extra.Requests.SequenceEqual(new[] { new PriceRequest(2, false) }) && service.CompletedCount == 2,
            "New stacks/keys reuse exact cached keys and only fetch missing prices.");
        extra.Complete(250); await Finish(service);
        Check(service.CompletedCount == 3 && service.GetCachedPrice(2) == 250, "Expanded full snapshot settles.");

        Scenario();
        var snapshot = Item(3);
        service.BeginRefresh(new[] { snapshot });
        var copied = await provider.Next(); snapshot.ItemId = 999; snapshot.IsHQ = true;
        copied.Complete(450); await Finish(service);
        Check(service.GetCachedPrice(3) == 450 && service.GetCachedPrice(999, true) == null && snapshot.MarketPrice == null,
            "Background work captures immutable keys, not mutable item references.");
        var forced = service.BeginRefresh(new[] { Item(3) }, true);
        var replacement = await provider.Next(); replacement.Complete(700); await Finish(service);
        Check(forced > expanded && service.GetCachedPrice(3) == 700, "Force bypasses fresh cache and refreshes price.");
    }

    private async Task MissingAndWrongResults()
    {
        Scenario();
        var provider = new ControlledProvider("Alpha");
        using var service = Service(provider);
        Check(!service.HasCachedResult(1), "Never attempted has no cache entry.");
        service.BeginRefresh(new[] { Item(1), Item(2) });
        var call = await provider.Next(); call.Completion.SetResult(new Dictionary<PriceRequest, MarketPriceResult>());
        await Finish(service);
        Check(service.HasCachedResult(1) && !service.HasValidCachedPrice(1) && service.GetCachedPrice(1) == null, "Missing data is an attempted unknown, never zero or negative.");
        var item = Item(1); item.MarketPrice = 99; service.UpdateItemPrice(item);
        Check(item.MarketPrice == null && item.MarketPriceFetchTime.HasValue && item.MarketDataUpdatedAtUtc == null,
            "Missing-data application clears stale price and distinguishes No data from never loaded.");
        var calls = provider.CallCount;
        service.BeginRefresh(new[] { Item(1), Item(2) });
        Check(service.IsRefreshComplete && provider.CallCount == calls, "Unknown short cache avoids infinite restart during cleanup.");
        Check(service.GetItemsNeedingPriceFetch(new[] { Item(1) }).Count == 0, "Unknown attempts are not immediately retried.");
        service.ClearCache();
        Check(!service.HasCompletedRefresh && !service.HasCachedResult(1), "Clear invalidates readiness and attempt cache.");
        service.UpdateItemPrice(item);
        Check(item.MarketPriceFetchTime == null && item.MarketPriceWorldName == null && item.MarketPriceMethodName == null,
            "Invalidated metadata is cleared synchronously.");

        Scenario();
        service.BeginRefresh(new[] { Item(1), Item(2), Item(3), Item(4) });
        call = await provider.Next();
        call.Completion.SetResult(new Dictionary<PriceRequest, MarketPriceResult>
        {
            [new(1, false)] = call.Result(new(1, false), 10) with { WorldName = "Wrong" },
            [new(2, false)] = call.Result(new(2, false), 10) with { IsHq = true },
            [new(3, false)] = call.Result(new(3, false), 10) with { Method = MarketPriceMethod.AverageSalePrice },
            [new(4, false)] = call.Result(new(4, false), 0),
        });
        await Finish(service);
        Check(Enumerable.Range(1, 4).All(i => service.GetCachedPrice((uint)i) == null), "Provider key/method/quality/world/positive-value mismatches are rejected.");
        Check(service.IsRefreshComplete && service.CompletedCount == 4, "Invalid data settles unknown without leaving UI busy.");
    }

    private async Task WorldGeneration()
    {
        Scenario();
        var alpha = new ControlledProvider("Alpha"); var beta = new ControlledProvider("Beta");
        using var service = new PriceService(_log, new(), "Alpha", 75, (world, _) => world == "Alpha" ? alpha : beta);
        service.BeginRefresh(new[] { Item(1) }); var old = await alpha.Next(); var oldTask = service.RefreshTask;
        service.UpdateWorld("Beta", 76);
        Check(!service.HasCompletedRefresh && !service.HasCachedResult(1) && !service.IsRefreshing, "World change invalidates old readiness/cache.");
        Check(old.Token.IsCancellationRequested && alpha.Disposed, "World change cancels and disposes old provider.");
        service.BeginRefresh(new[] { Item(1) }); var current = await beta.Next(); current.Complete(900); await Finish(service);
        old.Complete(1); await oldTask.WaitAsync(TimeSpan.FromSeconds(2));
        Check(service.GetCachedPrice(1) == 900 && service.GetCachedResult(1)?.WorldName == "Beta", "Late old-world completion cannot poison new-world cache.");
        Check(service.IsRefreshComplete && service.CompletedCount == 1, "Late generation cannot clear current readiness/progress.");
    }

    private async Task MethodGeneration()
    {
        Scenario();
        var provider = new ControlledProvider("Alpha"); var settings = new InventorySettings();
        using var service = Service(provider, settings);
        service.BeginRefresh(new[] { Item(1) }); var old = await provider.Next(); var oldTask = service.RefreshTask;
        settings.PriceMethod = MarketPriceMethod.AverageSalePrice;
        Check(!service.HasCompletedRefresh && !service.IsRefreshing && old.Token.IsCancellationRequested,
            "Method setting change synchronously invalidates readiness and cancels old generation.");
        service.BeginRefresh(new[] { Item(1) }); var current = await provider.Next();
        Check(current.Method == MarketPriceMethod.AverageSalePrice, "New generation requests selected method.");
        current.Complete(350); await Finish(service); old.Complete(1); await oldTask.WaitAsync(TimeSpan.FromSeconds(2));
        Check(service.GetCachedPrice(1) == 350 && service.GetCachedResult(1)?.Method == MarketPriceMethod.AverageSalePrice,
            "Late old method cannot replace selected method's price.");
        var item = Item(1); service.UpdateItemPrice(item);
        Check(item.MarketPriceMethodName == "Average sale price (4 days)", "Method metadata matches selected cached result.");
        service.NotifyPricingChanged();
        Check(!service.HasCompletedRefresh && service.GetCachedPrice(1) == null, "Explicit pricing invalidation clears readiness and cache.");
    }

    private async Task TimeoutAndDispose()
    {
        Scenario();
        var provider = new ControlledProvider("Alpha");
        using var service = Service(provider, timeout: TimeSpan.FromMilliseconds(75));
        service.BeginRefresh(new[] { Item(1) }); var call = await provider.Next(); await Finish(service);
        Check(call.Token.IsCancellationRequested && !service.IsRefreshing && service.IsRefreshComplete, "Unresponsive provider is bounded and fully settled.");
        Check(service.CompletedCount == 1 && service.HasCachedResult(1) && service.GetCachedPrice(1) == null, "Timeout creates unknown attempt, not cheap price.");
        call.Complete(1); await Task.Yield();
        Check(service.GetCachedPrice(1) == null, "Late post-timeout result is never cached.");

        Scenario();
        var failing = new ControlledProvider("Alpha"); using var failureService = Service(failing);
        failureService.BeginRefresh(new[] { Item(1) }); var failure = await failing.Next(); failure.Completion.SetException(new HttpRequestException("failure"));
        await Finish(failureService);
        Check(failureService.IsRefreshComplete && failureService.GetCachedPrice(1) == null && failureService.HasCachedResult(1), "Provider exceptions settle all work as unknown.");

        Scenario();
        var disposedProvider = new ControlledProvider("Alpha"); var disposed = Service(disposedProvider);
        disposed.BeginRefresh(new[] { Item(1) }); var late = await disposedProvider.Next(); var task = disposed.RefreshTask;
        disposed.Dispose(); disposed.Dispose(); late.Complete(20); await task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(disposedProvider.Disposed && late.Token.IsCancellationRequested && !disposed.IsRefreshing && !disposed.HasCompletedRefresh,
            "Dispose is idempotent, cancels work, and cannot publish ready.");
        Check(disposed.GetCachedPrice(1) == null, "Disposed generation cannot publish late result.");
        try { disposed.BeginRefresh(new[] { Item(1) }); Check(false, "Disposed start must fail."); }
        catch (ObjectDisposedException) { Check(true, "Disposed service rejects new refresh."); }

        Scenario();
        var emptyProvider = new ControlledProvider("Alpha"); using var empty = Service(emptyProvider);
        empty.BeginRefresh(Array.Empty<InventoryItemInfo>());
        Check(empty.IsRefreshComplete && empty.TotalCount == 0 && empty.CompletedCount == 0 && emptyProvider.CallCount == 0,
            "Empty snapshot completes deterministically without a network call.");
    }

    private async Task BatchProgress()
    {
        Scenario();
        var provider = new ControlledProvider("Alpha"); using var service = Service(provider);
        service.BeginRefresh(Enumerable.Range(1, 101).Select(i => Item((uint)i)));
        var first = await provider.Next(); Check(first.Requests.Length == 100, "First service chunk is bounded.");
        first.Complete(100);
        var second = await provider.Next();
        Check(service.IsRefreshing && !service.HasCompletedRefresh && service.CompletedCount == 100 && service.TotalCount == 101,
            "UI remains busy after first response until all current work settles.");
        Check(second.Requests.Length == 1, "Remaining inventory key is not silently dropped.");
        second.Complete(200); await Finish(service);
        Check(service.IsRefreshComplete && service.CompletedCount == 101 && provider.CallCount == 2, "All batches finish before readiness.");

        Scenario();
        var clearProvider = new ControlledProvider("Alpha"); using var cleared = Service(clearProvider);
        cleared.BeginRefresh(new[] { Item(1) }); var old = await clearProvider.Next(); var oldTask = cleared.RefreshTask;
        cleared.ClearCache(); old.Complete(10); await oldTask.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!cleared.HasCompletedRefresh && !cleared.HasCachedResult(1), "Cache invalidation blocks in-flight late writes.");
    }

    private async Task LegacyConcurrent()
    {
        Scenario();
        var provider = new ControlledProvider("Alpha"); using var service = Service(provider);
        var firstItem = Item(1); var secondItem = Item(1, true);
        var first = service.FetchPrice(firstItem); var second = service.FetchPrice(secondItem);
        var firstCall = await provider.Next(); firstCall.Complete(100);
        var secondCall = await provider.Next(); secondCall.Complete(700);
        Check(await first == 100 && await second == 700, "Compatibility concurrent calls serialize without dropping second quality.");
        Check(firstItem.MarketPrice == null && secondItem.MarketPrice == null, "Compatibility async API never writes inventory rows.");
    }

    private async Task ExpirationAndSupersession()
    {
        Scenario();
        var provider = new ControlledProvider("Alpha"); var settings = new InventorySettings();
        using var service = Service(provider, settings);
        var run = service.BeginRefresh(new[] { Item(1) }); var first = await provider.Next(); first.Complete(100); await Finish(service);
        Check(service.GetCachedPrice(1) == 100, "Positive result starts cached.");
        settings.PriceCacheDurationMinutes = 0;
        Check(!service.HasValidCachedPrice(1) && !service.HasCachedResult(1), "Expired successful cache is unavailable, not silently reused.");
        var expiredItem = Item(1); expiredItem.MarketPrice = 999; expiredItem.MarketPriceFetchTime = DateTime.UtcNow;
        service.UpdateItemPrice(expiredItem);
        Check(expiredItem.MarketPrice == null && expiredItem.MarketPriceFetchTime == null, "Synchronous apply clears expired old price.");
        var retry = service.BeginRefresh(new[] { Item(1) }); var replacement = await provider.Next();
        Check(retry > run && service.IsRefreshing && !service.HasCompletedRefresh, "Matching expired snapshot starts actual new work.");
        settings.PriceCacheDurationMinutes = 30; replacement.Complete(200); await Finish(service);
        Check(service.GetCachedPrice(1) == 200 && service.IsRefreshComplete, "Refetched snapshot restores readiness and fresh price.");

        Scenario();
        var forced = service.BeginRefresh(new[] { Item(1) }, true); var old = await provider.Next(); var oldTask = service.RefreshTask;
        var newer = service.BeginRefresh(new[] { Item(1) }, true); var current = await provider.Next();
        Check(newer > forced && old.Token.IsCancellationRequested && service.IsRefreshing, "Forced replacement cancels old generation without clearing current busy state.");
        current.Complete(800); await Finish(service); old.Complete(1); await oldTask.WaitAsync(TimeSpan.FromSeconds(2));
        Check(service.GetCachedPrice(1) == 800 && service.IsRefreshComplete && service.CompletedCount == 1,
            "Late same-world forced generation cannot overwrite or settle its replacement.");

        Scenario();
        service.BeginRefresh(new[] { Item(2) }); var removed = await provider.Next(); var removedTask = service.RefreshTask;
        service.BeginRefresh(Array.Empty<InventoryItemInfo>()); removed.Complete(500); await removedTask.WaitAsync(TimeSpan.FromSeconds(2));
        Check(service.IsRefreshComplete && service.TotalCount == 0 && service.GetCachedPrice(2) == null,
            "Replacing pending work with empty inventory stays complete and rejects removed-key response.");
    }

    private static object Row(uint id, decimal? nq, decimal? hq, decimal? nqAverage = null, decimal? hqAverage = null) => new
    {
        itemId = id,
        nq = Quality(nq, nqAverage), hq = Quality(hq, hqAverage),
        worldUploadTimes = new[] { new { worldId = 99, timestamp = 1900000000000L }, new { worldId = 75, timestamp = 1700000000000L } },
    };
    private static object Quality(decimal? price, decimal? average) => new
    {
        minListing = new { world = price.HasValue ? new { price = price.Value } : null, dc = new { price = 1 }, region = new { price = 2 } },
        averageSalePrice = new { world = average.HasValue ? new { price = average.Value } : null },
    };
    private static string Payload(IEnumerable<object> rows, uint[]? failed = null) => JsonSerializer.Serialize(new { results = rows, failedItems = failed ?? Array.Empty<uint>() });
}

sealed class Log : IPluginLog
{
    public void Warning(string message) { }
    public void Error(Exception exception, string message) { }
}
sealed class FakeHttp : HttpMessageHandler
{
    public string Json = "{}";
    public HttpStatusCode Status = HttpStatusCode.OK;
    public List<string> Urls { get; } = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Urls.Add(request.RequestUri!.AbsoluteUri);
        return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Json, Encoding.UTF8, "application/json") });
    }
}
sealed class ControlledProvider(string world) : IPriceProvider
{
    private readonly ConcurrentQueue<PendingCall> _calls = new();
    private readonly SemaphoreSlim _signal = new(0);
    public string World { get; } = world;
    public bool Disposed { get; private set; }
    public int CallCount;
    public Task<IReadOnlyDictionary<PriceRequest, MarketPriceResult>> GetPricesAsync(IReadOnlyList<PriceRequest> requests,
        MarketPriceMethod method, CancellationToken cancellationToken)
    {
        var call = new PendingCall(World, requests.ToArray(), method, cancellationToken);
        _calls.Enqueue(call); Interlocked.Increment(ref CallCount); _signal.Release();
        // Intentionally ignores cancellation to exercise stale/late response guards.
        return call.Completion.Task;
    }
    public async Task<PendingCall> Next()
    {
        if (!await _signal.WaitAsync(TimeSpan.FromSeconds(3))) throw new TimeoutException("No provider call.");
        if (!_calls.TryDequeue(out var call)) throw new InvalidOperationException("Missing call.");
        return call;
    }
    public void Dispose() => Disposed = true;
}
sealed class PendingCall(string world, PriceRequest[] requests, MarketPriceMethod method, CancellationToken token)
{
    public PriceRequest[] Requests { get; } = requests;
    public MarketPriceMethod Method { get; } = method;
    public CancellationToken Token { get; } = token;
    public TaskCompletionSource<IReadOnlyDictionary<PriceRequest, MarketPriceResult>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public MarketPriceResult Result(PriceRequest request, long price) => new()
    {
        ItemId = request.ItemId, IsHq = request.IsHq, Price = price, WorldName = world, Method = Method,
        LastUpdated = DateTime.UtcNow, MarketDataUpdatedAtUtc = DateTime.UtcNow.AddHours(-2),
    };
    public void Complete(long nq, long? hq = null) => Completion.TrySetResult(Requests.ToDictionary(r => r, r => Result(r, r.IsHq ? hq ?? nq : nq)));
}
