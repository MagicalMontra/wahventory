using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;

namespace wahventory.Services;

public class ListSyncFile
{
    public const int CurrentSchema = 1;

    [JsonProperty("schema")]
    public int Schema { get; set; } = CurrentSchema;

    [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
    public string? Name { get; set; }

    [JsonProperty("revision", NullValueHandling = NullValueHandling.Ignore)]
    public int? Revision { get; set; }

    [JsonProperty("blacklist")]
    public List<uint>? Blacklist { get; set; }

    [JsonProperty("autoDiscard")]
    public List<uint>? AutoDiscard { get; set; }
}

public class ListSyncDiff
{
    public List<uint> BlacklistAdded { get; init; } = new();
    public List<uint> BlacklistRemoved { get; init; } = new();
    public List<uint> AutoDiscardAdded { get; init; } = new();
    public List<uint> AutoDiscardRemoved { get; init; } = new();

    public bool IsEmpty => BlacklistAdded.Count == 0 && BlacklistRemoved.Count == 0 &&
                           AutoDiscardAdded.Count == 0 && AutoDiscardRemoved.Count == 0;

    public bool IsRisky => AutoDiscardAdded.Count > 0 || BlacklistRemoved.Count > 0;

    public string Summary =>
        $"blacklist +{BlacklistAdded.Count}/−{BlacklistRemoved.Count}, " +
        $"auto-discard +{AutoDiscardAdded.Count}/−{AutoDiscardRemoved.Count}";

    public static ListSyncDiff Compute(
        HashSet<uint> currentBlacklist, HashSet<uint> currentAutoDiscard,
        HashSet<uint> newBlacklist, HashSet<uint> newAutoDiscard)
    {
        return new ListSyncDiff
        {
            BlacklistAdded = newBlacklist.Except(currentBlacklist).OrderBy(i => i).ToList(),
            BlacklistRemoved = currentBlacklist.Except(newBlacklist).OrderBy(i => i).ToList(),
            AutoDiscardAdded = newAutoDiscard.Except(currentAutoDiscard).OrderBy(i => i).ToList(),
            AutoDiscardRemoved = currentAutoDiscard.Except(newAutoDiscard).OrderBy(i => i).ToList(),
        };
    }
}

public enum ListSyncFetchStatus
{
    NotModified,
    Fetched,
    Error,
}

public class ListSyncFetchResult
{
    public ListSyncFetchStatus Status { get; init; }
    public string Url { get; init; } = string.Empty;
    public ListSyncFile? File { get; init; }
    public string? Hash { get; init; }
    public string? ETag { get; init; }
    public string? Error { get; init; }
}

public class ListSyncService : IDisposable
{
    private const int MaxBodyBytes = 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly IPluginLog _log;

    public ListSyncService(IPluginLog log)
    {
        _log = log;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "WahventoryFFXIVPlugin/1.0");
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    public static bool IsValidUrl(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    public async Task<ListSyncFetchResult> FetchAsync(string url, string? etag)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(etag))
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            if (response.StatusCode == HttpStatusCode.NotModified)
                return new ListSyncFetchResult { Status = ListSyncFetchStatus.NotModified, Url = url };

            if (!response.IsSuccessStatusCode)
                return Error(url, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            if (response.Content.Headers.ContentLength > MaxBodyBytes)
                return Error(url, "List file is too large (over 1 MB).");

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length > MaxBodyBytes)
                return Error(url, "List file is too large (over 1 MB).");

            ListSyncFile? file;
            try
            {
                file = JsonConvert.DeserializeObject<ListSyncFile>(Encoding.UTF8.GetString(bytes));
            }
            catch (JsonException ex)
            {
                return Error(url, $"Invalid JSON: {ex.Message}");
            }

            if (file == null || (file.Blacklist == null && file.AutoDiscard == null))
                return Error(url, "Not a wahventory list file (no \"blacklist\" or \"autoDiscard\").");

            if (file.Schema > ListSyncFile.CurrentSchema)
                return Error(url, $"List file uses schema {file.Schema}; update wahventory to read it.");

            return new ListSyncFetchResult
            {
                Status = ListSyncFetchStatus.Fetched,
                Url = url,
                File = file,
                Hash = Convert.ToHexString(SHA256.HashData(bytes)),
                ETag = response.Headers.ETag?.ToString(),
            };
        }
        catch (TaskCanceledException)
        {
            return Error(url, "Request timed out.");
        }
        catch (Exception ex)
        {
            _log.Warning($"List sync fetch failed: {ex.Message}");
            return Error(url, ex.Message);
        }
    }

    public static string Export(HashSet<uint> blacklist, HashSet<uint> autoDiscard, string? name, int? revision)
    {
        var file = new ListSyncFile
        {
            Name = name,
            Revision = revision,
            Blacklist = blacklist.OrderBy(i => i).ToList(),
            AutoDiscard = autoDiscard.OrderBy(i => i).ToList(),
        };
        return JsonConvert.SerializeObject(file, Formatting.Indented);
    }

    private static ListSyncFetchResult Error(string url, string message) =>
        new() { Status = ListSyncFetchStatus.Error, Url = url, Error = message };

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
