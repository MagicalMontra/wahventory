using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using wahventory.Core;
using wahventory.Services;

namespace wahventory.Modules.Inventory;

internal sealed class PendingListUpdate
{
    public required ListSyncFile File { get; init; }
    public required string Hash { get; init; }
    public string? ETag { get; init; }
    public required ListSyncDiff Diff { get; init; }
}

internal sealed class ListSyncController : IDisposable
{
    private readonly InventoryManagementModule _module;
    private readonly ConfigurationManager _configManager;
    private readonly IChatGui _chat;
    private readonly ListSyncService _service;

    private Task<ListSyncFetchResult>? _inFlight;
    private DateTime _nextCheck = DateTime.MinValue;

    public PendingListUpdate? Pending { get; private set; }
    public string? LastError { get; private set; }
    public bool IsChecking => _inFlight != null;

    public event Action? PendingReviewRequested;

    private ListSyncSettings Settings => _module.Settings.ListSync;

    public ListSyncController(InventoryManagementModule module, ConfigurationManager configManager, IPluginLog log, IChatGui chat)
    {
        _module = module;
        _configManager = configManager;
        _chat = chat;
        _service = new ListSyncService(log);
    }

    public void Update()
    {
        if (!Settings.Enabled)
            return;

        if (_inFlight != null)
        {
            if (!_inFlight.IsCompleted || _module.DiscardService.IsDiscarding)
                return;

            var result = _inFlight.Result;
            _inFlight = null;
            HandleResult(result);
            return;
        }

        if (DateTime.Now >= _nextCheck)
            CheckNow();
    }

    public void CheckNow()
    {
        if (!Settings.Enabled || _inFlight != null)
            return;

        _nextCheck = DateTime.Now.AddMinutes(Math.Max(5, Settings.CheckIntervalMinutes));
        var url = Settings.Url;
        var etag = Settings.ETag;
        _inFlight = Task.Run(() => _service.FetchAsync(url, etag));
    }

    public bool Enable(string url)
    {
        url = url.Trim();
        if (!ListSyncService.IsValidUrl(url))
        {
            LastError = "URL must be an https:// link to a raw list file.";
            return false;
        }

        var backup = _configManager.BackupLists(_module.BlacklistedItems, _module.AutoDiscardItems, "Before enabling list sync");
        if (backup == null)
        {
            LastError = "Couldn't back up current lists; sync not enabled.";
            return false;
        }

        Settings.Url = url;
        Settings.Enabled = true;
        Settings.PreSyncBackup = backup;
        Settings.LastAppliedHash = null;
        Settings.SkippedHash = null;
        Settings.ETag = null;
        Settings.RemoteName = null;
        Settings.RemoteRevision = null;
        Settings.LastAppliedAt = null;
        Settings.LastCheckedAt = null;
        _module.SaveConfig();

        LastError = null;
        Pending = null;
        _nextCheck = DateTime.MinValue;
        CheckNow();
        return true;
    }

    public void Disable(bool restorePreSyncBackup)
    {
        if (restorePreSyncBackup && _module.DiscardService.IsDiscarding)
        {
            _chat.PrintError("[wahventory] Wait for the current discard operation before restoring lists.");
            return;
        }
        Settings.Enabled = false;
        Pending = null;
        LastError = null;
        _inFlight = null;

        if (restorePreSyncBackup && Settings.PreSyncBackup != null)
            RestoreBackup(Settings.PreSyncBackup);

        Settings.PreSyncBackup = null;
        _module.SaveConfig();
    }

    public void ApplyPending()
    {
        if (Pending == null || _module.DiscardService.IsDiscarding)
            return;

        var pending = Pending;
        Pending = null;
        Apply(pending.File, pending.Hash, pending.ETag, pending.Diff);
    }

    public void SkipPending()
    {
        if (Pending == null)
            return;

        Settings.SkippedHash = Pending.Hash;
        Settings.ETag = Pending.ETag;
        _module.SaveConfig();
        _chat.Print($"[wahventory] Skipped synced list update{FormatRevision(Pending.File.Revision)}. You'll be asked again when the list changes.");
        Pending = null;
    }

    public bool RestoreBackup(string fileName)
    {
        if (_module.DiscardService.IsDiscarding)
        {
            _chat.PrintError("[wahventory] Wait for the current discard operation before restoring lists.");
            return false;
        }
        var backup = _configManager.LoadListBackup(fileName);
        if (backup == null)
        {
            _chat.PrintError($"[wahventory] Couldn't read backup {fileName}.");
            return false;
        }

        _configManager.BackupLists(_module.BlacklistedItems, _module.AutoDiscardItems, $"Before restoring {fileName}", Settings.PreSyncBackup);
        _module.ReplaceLists(backup.Blacklist, backup.AutoDiscard);
        _chat.Print($"[wahventory] Restored lists from {backup.CreatedAt:yyyy-MM-dd HH:mm} " +
                    $"({_module.BlacklistedItems.Count} blacklist, {_module.AutoDiscardItems.Count} auto-discard).");
        return true;
    }

    public string ExportCurrentLists()
    {
        if (_module.EnforceListInvariants() > 0) _module.SaveAutoDiscard();
        return ListSyncService.Export(_module.BlacklistedItems, _module.AutoDiscardItems, Settings.RemoteName, Settings.RemoteRevision);
    }

    private void HandleResult(ListSyncFetchResult result)
    {
        if (!Settings.Enabled || result.Url != Settings.Url)
            return;

        Settings.LastCheckedAt = DateTime.Now;

        switch (result.Status)
        {
            case ListSyncFetchStatus.Error:
                LastError = result.Error;
                break;

            case ListSyncFetchStatus.NotModified:
                LastError = null;
                break;

            case ListSyncFetchStatus.Fetched:
                LastError = null;
                HandleFetched(result.File!, result.Hash!, result.ETag);
                break;
        }

        _module.SaveConfig();
    }

    private void HandleFetched(ListSyncFile file, string hash, string? etag)
    {
        if (hash == Settings.LastAppliedHash || hash == Settings.SkippedHash)
        {
            Settings.ETag = etag;
            return;
        }

        if (Pending?.Hash == hash)
            return;

        // Review the effective lists that will actually be persisted: blacklist
        // entries always take precedence over conflicting discard entries.
        var blacklist = (file.Blacklist ?? new List<uint>()).ToHashSet();
        var autoDiscard = (file.AutoDiscard ?? new List<uint>()).ToHashSet();
        InventoryListPolicy.Normalize(blacklist, autoDiscard);
        file.Blacklist = blacklist.OrderBy(id => id).ToList();
        file.AutoDiscard = autoDiscard.OrderBy(id => id).ToList();
        var diff = ListSyncDiff.Compute(_module.BlacklistedItems, _module.AutoDiscardItems, blacklist, autoDiscard);

        if (diff.IsRisky)
        {
            Pending = new PendingListUpdate { File = file, Hash = hash, ETag = etag, Diff = diff };
            _chat.Print($"[wahventory] Synced list update{FormatRevision(file.Revision)} needs review ({diff.Summary}).");
            PendingReviewRequested?.Invoke();
            return;
        }

        Pending = null;
        Apply(file, hash, etag, diff);
    }

    private void Apply(ListSyncFile file, string hash, string? etag, ListSyncDiff diff)
    {
        if (!diff.IsEmpty)
        {
            var backup = _configManager.BackupLists(_module.BlacklistedItems, _module.AutoDiscardItems,
                $"Before sync update{FormatRevision(file.Revision)}", Settings.PreSyncBackup);
            if (backup == null)
            {
                LastError = "Couldn't back up current lists; update not applied.";
                return;
            }

            _module.ReplaceLists(file.Blacklist ?? new List<uint>(), file.AutoDiscard ?? new List<uint>());
            _chat.Print($"[wahventory] Lists synced{FormatRevision(file.Revision)}: {diff.Summary}.");
        }

        Settings.LastAppliedHash = hash;
        Settings.SkippedHash = null;
        Settings.ETag = etag;
        Settings.RemoteName = file.Name;
        Settings.RemoteRevision = file.Revision;
        Settings.LastAppliedAt = DateTime.Now;
        _module.SaveConfig();
    }

    private static string FormatRevision(int? revision) => revision.HasValue ? $" (rev {revision})" : string.Empty;

    public void Dispose()
    {
        _service.Dispose();
    }
}
