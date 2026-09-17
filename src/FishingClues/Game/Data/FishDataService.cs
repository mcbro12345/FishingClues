using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FishingClues.Base;
using FishingClues.Game.Models;

namespace FishingClues.Game.Data;

// Owns the loaded fish-condition data and its background refresh from the
// GitHub-hosted dataset. Nothing else mutates FishDataFile directly.
public sealed class FishDataService
{
    private readonly CancellationTokenSource refreshCancellation = new();
    private FishDataFile data;
    private bool refreshing;
    private DateTime nextCheck = DateTime.UtcNow;

    public FishDataFile Data => data;
    public IReadOnlyDictionary<string, string> RegionByZone => regionByZone;
    public bool IsRefreshing => refreshing;
    public string StatusMessage { get; private set; } = "Using bundled fishing data.";

    public event Action? DataRefreshed;

    private readonly Dictionary<string, string> regionByZone;

    private static string DataCachePath => Path.Combine(Services.PluginInterface.GetPluginConfigDirectory(), "FishConditions.json");

    public FishDataService()
    {
        data = Load();
        if (File.Exists(DataCachePath))
            StatusMessage = $"Cached data: {File.GetLastWriteTime(DataCachePath):g}";
        regionByZone = BuildRegionByZone(data);
    }

    private static Dictionary<string, string> BuildRegionByZone(FishDataFile data) => data.Info.Values
        .Where(info => !string.IsNullOrWhiteSpace(info.Zone) && !string.IsNullOrWhiteSpace(info.Region))
        .GroupBy(info => info.Zone, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.GroupBy(info => info.Region)
            .OrderByDescending(regions => regions.Count()).First().Key, StringComparer.OrdinalIgnoreCase);

    private static FishDataFile Load()
    {
        foreach (string path in new[] { DataCachePath, Path.Combine(Services.PluginInterface.AssemblyLocation.DirectoryName!, "FishConditions.json") })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var loaded = JsonSerializer.Deserialize<FishDataFile>(File.ReadAllText(path), FishDataRefresh.JsonOptions)!;
                FishDataRefresh.Validate(loaded);
                return loaded;
            }
            catch (Exception ex) { Services.Log.Warning(ex, "Could not load fishing data; trying bundled fallback."); }
        }
        return new FishDataFile();
    }

    public void CheckAutoRefresh(Configuration configuration)
    {
        if (!configuration.AutoRefreshFishData || DateTime.UtcNow < nextCheck) return;
        nextCheck = DateTime.UtcNow.AddDays(1);
        if (!File.Exists(DataCachePath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(DataCachePath) >= TimeSpan.FromDays(1))
            _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (refreshing || refreshCancellation.IsCancellationRequested) return;
        refreshing = true;
        StatusMessage = "Downloading the latest catch conditions and fish information...";
        nextCheck = DateTime.UtcNow.AddDays(1);
        try
        {
            var updated = await Task.Run(() => FishDataRefresh.Download(refreshCancellation.Token));
            if (refreshCancellation.IsCancellationRequested) return;
            if (updated.Fish.Count < data.Fish.Count * 0.9 || updated.Info.Count < data.Info.Count * 0.9)
                throw new InvalidDataException("The download unexpectedly removed too many fish records.");
            Directory.CreateDirectory(Services.PluginInterface.GetPluginConfigDirectory());
            string temp = DataCachePath + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(updated), refreshCancellation.Token);
            refreshCancellation.Token.ThrowIfCancellationRequested();
            File.Move(temp, DataCachePath, true);
            await Services.Framework.Run(() =>
            {
                if (refreshCancellation.IsCancellationRequested) return;
                data = updated;
                regionByZone.Clear();
                foreach (var info in updated.Info.Values)
                    if (!string.IsNullOrWhiteSpace(info.Zone) && !string.IsNullOrWhiteSpace(info.Region))
                        regionByZone.TryAdd(info.Zone, info.Region);
                StatusMessage = $"Updated {DateTime.Now:g}: {data.Fish.Count:N0} fish. Reopen the journal to refresh all panels.";
                DataRefreshed?.Invoke();
            });
        }
        catch (OperationCanceledException) { StatusMessage = "Refresh cancelled. Previous data kept."; }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Fishing data refresh failed; previous cache retained.");
            StatusMessage = "Refresh failed. Previous data kept; try again later.";
        }
        finally { refreshing = false; }
    }

    public void Dispose() => refreshCancellation.Cancel();
}
