using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;

namespace wahventory.Core;

public class ConfigurationManager
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly string _configDirectory;
    private Configuration _configuration = null!;
    private readonly IPluginLog _log;

    private const string CONFIG_FILE = "config.json";
    private const string BLACKLIST_FILE = "blacklist.json";
    private const string AUTODISCARD_FILE = "autodiscard.json";
    private const string BACKUP_DIR = "backups";
    private const int MAX_BACKUPS = 10;

    private string BackupDirectory => Path.Combine(_configDirectory, BACKUP_DIR);

    public Configuration Configuration => _configuration;

    public ConfigurationManager(IGameServices services)
    {
        _pluginInterface = services.PluginInterface;
        _log = services.Log;
        _configDirectory = Path.Combine(_pluginInterface.ConfigDirectory.FullName, "wahventory");

        if (!Directory.Exists(_configDirectory))
        {
            Directory.CreateDirectory(_configDirectory);
        }

        LoadConfiguration();
    }
    
    private void LoadConfiguration()
    {
        var configPath = Path.Combine(_configDirectory, CONFIG_FILE);
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                _configuration = JsonConvert.DeserializeObject<Configuration>(json) ?? new Configuration();
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to load configuration");
                _configuration = new Configuration();
            }
        }
        else
        {
            _configuration = _pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        }
        
        _configuration.Save = SaveConfiguration;
    }
    
    public void SaveConfiguration()
    {
        try
        {
            var configPath = Path.Combine(_configDirectory, CONFIG_FILE);
            var json = JsonConvert.SerializeObject(_configuration, Formatting.Indented);
            File.WriteAllText(configPath, json);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to save configuration");
        }
    }
    
    public HashSet<uint> LoadBlacklist()
    {
        var path = Path.Combine(_configDirectory, BLACKLIST_FILE);
        if (!File.Exists(path))
            return new HashSet<uint>();
        
        try
        {
            var json = File.ReadAllText(path);
            var data = JsonConvert.DeserializeObject<ListData>(json);
            return data?.Items?.ToHashSet() ?? new HashSet<uint>();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to load blacklist");
            return new HashSet<uint>();
        }
    }
    
    public void SaveBlacklist(HashSet<uint> items)
    {
        try
        {
            var path = Path.Combine(_configDirectory, BLACKLIST_FILE);
            var data = new ListData
            {
                Version = 1,
                Items = items.ToList()
            };
            var json = JsonConvert.SerializeObject(data, Formatting.Indented);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to save blacklist");
        }
    }
    
    public HashSet<uint> LoadAutoDiscard()
    {
        var path = Path.Combine(_configDirectory, AUTODISCARD_FILE);
        if (!File.Exists(path))
            return new HashSet<uint>();
        
        try
        {
            var json = File.ReadAllText(path);
            var data = JsonConvert.DeserializeObject<ListData>(json);
            return data?.Items?.ToHashSet() ?? new HashSet<uint>();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to load auto-discard list");
            return new HashSet<uint>();
        }
    }
    
    public void SaveAutoDiscard(HashSet<uint> items)
    {
        try
        {
            var path = Path.Combine(_configDirectory, AUTODISCARD_FILE);
            var data = new ListData
            {
                Version = 1,
                Items = items.ToList()
            };
            var json = JsonConvert.SerializeObject(data, Formatting.Indented);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to save auto-discard list");
        }
    }
    
    public string? BackupLists(HashSet<uint> blacklist, HashSet<uint> autoDiscard, string reason, string? keepFile = null)
    {
        try
        {
            Directory.CreateDirectory(BackupDirectory);
            var now = DateTime.Now;
            var fileName = $"lists-{now:yyyyMMdd-HHmmss-fff}.json";
            var backup = new ListBackup
            {
                CreatedAt = now,
                Reason = reason,
                Blacklist = blacklist.OrderBy(i => i).ToList(),
                AutoDiscard = autoDiscard.OrderBy(i => i).ToList(),
            };
            File.WriteAllText(Path.Combine(BackupDirectory, fileName), JsonConvert.SerializeObject(backup, Formatting.Indented));
            PruneBackups(keepFile);
            return fileName;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to back up lists");
            return null;
        }
    }

    public List<ListBackupInfo> GetListBackups()
    {
        var result = new List<ListBackupInfo>();
        if (!Directory.Exists(BackupDirectory))
            return result;

        foreach (var path in Directory.GetFiles(BackupDirectory, "lists-*.json").OrderByDescending(p => p))
        {
            var backup = LoadListBackup(Path.GetFileName(path));
            if (backup == null) continue;
            result.Add(new ListBackupInfo(Path.GetFileName(path), backup.CreatedAt, backup.Reason,
                backup.Blacklist.Count, backup.AutoDiscard.Count));
        }
        return result;
    }

    public ListBackup? LoadListBackup(string fileName)
    {
        var path = Path.Combine(BackupDirectory, Path.GetFileName(fileName));
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonConvert.DeserializeObject<ListBackup>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"Failed to load list backup {fileName}");
            return null;
        }
    }

    private void PruneBackups(string? keepFile)
    {
        var stale = Directory.GetFiles(BackupDirectory, "lists-*.json")
            .OrderByDescending(p => p)
            .Skip(MAX_BACKUPS)
            .Where(p => keepFile == null || !string.Equals(Path.GetFileName(p), keepFile, StringComparison.OrdinalIgnoreCase));

        foreach (var path in stale)
        {
            try { File.Delete(path); }
            catch (Exception ex) { _log.Warning($"Failed to delete old backup {path}: {ex.Message}"); }
        }
    }

    private class ListData
    {
        public int Version { get; set; } = 1;
        public List<uint> Items { get; set; } = new();
    }
}

public class ListBackup
{
    public DateTime CreatedAt { get; set; }
    public string Reason { get; set; } = string.Empty;
    public List<uint> Blacklist { get; set; } = new();
    public List<uint> AutoDiscard { get; set; } = new();
}

public record ListBackupInfo(string FileName, DateTime CreatedAt, string Reason, int BlacklistCount, int AutoDiscardCount);
