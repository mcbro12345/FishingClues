using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using FishingClues.Game.Models;

namespace FishingClues.Game.Data;

// Remote files are parsed as data only; downloaded JavaScript/C# is never executed.
public static class FishDataRefresh
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    public static void Validate(FishDataFile data)
    {
        if (data.SchemaVersion != 2 || data.Fish.Count < 1000 || data.Info.Count < 1000)
            throw new InvalidDataException("The fishing dataset is incomplete or has an unsupported format.");
        if (data.SpotBaits is null || data.SpotBaits.Count < 1000 ||
            data.SpotBaits.Values.Any(spots => spots is null || spots.Values.Any(s => s is null || s.Recommended is null || s.Observed is null)))
            throw new InvalidDataException("Per-hole bait data is incomplete; keeping the previous cache.");
        foreach (var row in data.Fish.Values)
            if (row is null || row.BaitPath is null || row.Weather is null || row.PreviousWeather is null ||
                row.AlternativeBaits is null || row.Predators is null || row.Predators.Any(p => p is null || p.Count != 2) ||
                !double.IsFinite(row.StartHour) || !double.IsFinite(row.EndHour) ||
                row.StartHour < 0 || row.StartHour > 24 || row.EndHour < 0 || row.EndHour > 24)
                throw new InvalidDataException("Invalid fishing conditions; keeping the previous cache.");
    }

    public static async Task<FishDataFile> Download(CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90), MaxResponseContentBufferSize = 32 * 1024 * 1024 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FishingClues/3.12.0");
        const string tracker = "https://raw.githubusercontent.com/icykoneko/ff14-fish-tracker-app/master/js/app/";
        var conditions = client.GetStringAsync(tracker + "data.js", token);
        var info = client.GetStringAsync(tracker + "fish_info_data.js", token);
        var buddy = client.GetByteArrayAsync("https://codeload.github.com/Ottermandias/GatherBuddy/zip/refs/heads/main", token);
        var spotSources = client.GetStringAsync("https://raw.githubusercontent.com/ffxiv-teamcraft/ffxiv-teamcraft/staging/libs/data/src/lib/json/fishing-sources.json", token);
        var baitRecords = DownloadBaitRecords(client, token);
        const string tc = "https://raw.githubusercontent.com/ffxiv-teamcraft/ffxiv-teamcraft/staging/libs/data/src/lib/json/";
        var spotCatalog = client.GetStringAsync(tc + "fishing-spots.json", token);
        var baitCatalog = client.GetStringAsync(tc + "baits.json", token);
        var spearCatalog = client.GetStringAsync(tc + "spear-fishing-log.json", token);
        await Task.WhenAll(conditions, info, buddy, spotSources, baitRecords, spotCatalog, baitCatalog, spearCatalog);
        var sources = new List<string>();
        using var zip = new ZipArchive(new MemoryStream(await buddy), ZipArchiveMode.Read);
        long total = 0;
        foreach (var entry in zip.Entries.Where(e => Regex.IsMatch(e.FullName, @"/GatherBuddy.GameData/Data/Fish/Data\d[^/]*\.cs$")))
        {
            total += entry.Length;
            if (total > 32 * 1024 * 1024) throw new InvalidDataException("Fishing data exceeds the expected size.");
            using var reader = new StreamReader(entry.Open());
            sources.Add(await reader.ReadToEndAsync(token));
        }
        if (sources.Count == 0) throw new InvalidDataException("The ordinary-fish source format has changed.");
        return Parse(await conditions, await info, sources, await spotSources, await baitRecords, await spotCatalog, await baitCatalog, await spearCatalog);
    }

    public static FishDataFile Parse(string tracker, string metadata, IEnumerable<string> ordinarySources, string spotSources, string baitRecords, string spotCatalog, string baitCatalog, string spearCatalog)
    {
        // DATA has unquoted top-level keys; each value is strict JSON.
        var json = tracker[tracker.IndexOf('{')..(tracker.LastIndexOf('}') + 1)];
        json = Regex.Replace(json, @"(?m)^(\s*)([A-Z_]+):", "$1\"$2\":");
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
        var root = doc.RootElement;
        var result = new FishDataFile { SchemaVersion = 2 };
        foreach (var p in root.GetProperty("FISH").EnumerateObject())
        {
            var r = p.Value;
            var c = JsonSerializer.Deserialize<FishCondition>(r.GetRawText(), JsonOptions)!;
            c.RequirementsKnown = r.TryGetProperty("dataMissing", out var missing) &&
                (missing.ValueKind == JsonValueKind.False || missing.ValueKind == JsonValueKind.Null);
            c.PreviousWeather = Read<List<uint>>(r, "previousWeatherSet") ?? new();
            c.Weather = Read<List<uint>>(r, "weatherSet") ?? new();
            c.IntuitionSeconds = Read<int?>(r, "intuitionLength");
            if (r.TryGetProperty("bestCatchPath", out var path))
                foreach (var v in path.EnumerateArray())
                    if (v.ValueKind == JsonValueKind.Array)
                    {
                        c.AlternativeBaits = v.Deserialize<List<uint>>()!;
                        if (c.AlternativeBaits.Count > 0) c.BaitPath.Add(c.AlternativeBaits[0]);
                    }
                    else c.BaitPath.Add(v.GetUInt32());
            result.Fish.Add(uint.Parse(p.Name), c);
        }
        foreach (var source in ordinarySources)
        foreach (Match match in Regex.Matches(source, @"data\.Apply\s*\(\s*(\d+)\s*,[\s\S]*?;"))
        {
            uint id = uint.Parse(match.Groups[1].Value);
            if (result.Fish.ContainsKey(id)) continue;
            string block = match.Value;
            var c = new FishCondition();
            string Args(string name) => Regex.Match(block, @"\." + name + @"\s*\(\s*(?:data\s*,\s*)?([^)]+)\)").Groups[1].Value;
            List<uint> Ids(string name) => Regex.Matches(Args(name), @"\d+").Select(m => uint.Parse(m.Value)).ToList();
            c.BaitPath = Ids("Bait");
            if (c.BaitPath.Count == 0) {
                c.BaitPath = Ids("Mooch"); c.IsMooch = c.BaitPath.Count > 0;
            }
            c.RequirementsKnown = false; // Omitted requirements are not proof that none exist.
            var time = Ids("Time");
            if (time.Count == 2) { c.StartHour = time[0] / 60.0; c.EndHour = time[1] / 60.0; }
            c.Weather = Ids("Weather"); c.PreviousWeather = Ids("Transition");
            string bite = Args("Bite");
            c.Hookset = bite.Contains("Precise") ? "Precision" : bite.Contains("Powerful") ? "Powerful" : null;
            c.Tug = bite.Contains("Weak") ? "light" : bite.Contains("Strong") ? "medium" : bite.Contains("Legendary") ? "heavy" : null;
            string snag = Args("Snag"); if (snag.Length > 0) c.Snagging = snag.Contains("Required");
            string lure = Args("Lure"); if (lure.Length > 0) c.Lure = lure.Split('.').Last().Trim();
            var predator = Regex.Match(block, @"\.Predators\s*\(\s*data\s*,\s*(\d+)\s*,([\s\S]*?)(?=\n\s*\.|;)");
            if (predator.Success)
            {
                c.IntuitionSeconds = int.Parse(predator.Groups[1].Value);
                c.Predators = Regex.Matches(predator.Groups[2].Value, @"\(\s*(\d+)\s*,\s*(\d+)\s*\)")
                    .Select(m => new List<uint> { uint.Parse(m.Groups[1].Value), uint.Parse(m.Groups[2].Value) }).ToList();
            }
            var spear = Regex.Match(Args("Spear"), @"SpearfishSize\.(\w+)");
            if (spear.Success) c.Gig = spear.Groups[1].Value;
            var speed = Regex.Match(Args("Spear"), @"SpearfishSpeed\.(\w+)");
            if (speed.Success) c.SpearSpeed = Regex.Replace(speed.Groups[1].Value, "([a-z])([A-Z])", "$1 $2");
            result.Fish[id] = c;
        }
        foreach (var p in root.GetProperty("ITEMS").EnumerateObject()) result.Items[uint.Parse(p.Name)] = Read<string>(p.Value, "name_en") ?? p.Name;
        foreach (var p in root.GetProperty("WEATHER_TYPES").EnumerateObject()) result.Weather[uint.Parse(p.Name)] = Read<string>(p.Value, "name_en") ?? p.Name;
        foreach (var p in root.GetProperty("FOLKLORE").EnumerateObject()) result.Folklore[uint.Parse(p.Name)] = Read<string>(p.Value, "book_en") ?? Read<string>(p.Value, "name_en") ?? p.Name;
        using var infoDoc = JsonDocument.Parse(metadata[metadata.IndexOf('[')..(metadata.LastIndexOf(']') + 1)]);
        foreach (var r in infoDoc.RootElement.EnumerateArray())
        {
            var levels = Read<List<int>>(r, "level") ?? new();
            result.Info[r.GetProperty("id").GetUInt32()] = new FishInfo {
                Name = Read<string>(r, "name_en") ?? "", Description = Read<string>(r, "desc_en") ?? "",
                Icon = uint.TryParse(r.GetProperty("icon").ToString(), out var icon) ? icon : 0,
                Level = levels.FirstOrDefault(), Stars = levels.Skip(1).FirstOrDefault(),
                Waters = Read<string>(r, "record_en") ?? "", Region = Read<string>(r, "region_en") ?? "",
                Zone = Read<string>(r, "zone_en") ?? "", Collectable = Read<bool?>(r, "collectable") == true,
                Rarity = Read<int?>(r, "rarity") ?? 0
            };
        }
        ParseSpotBaits(result, spotSources, baitRecords);
        ValidateLocations(result, spotCatalog, baitCatalog, spearCatalog);
        Validate(result);
        return result;
    }
    private static async Task<string> DownloadBaitRecords(HttpClient client, CancellationToken token)
    {
        // Read only aggregated catch evidence, never player/user records.
        const string query = "{ baits_per_fish_per_spot(where: {occurences: {_gt: 1}, itemId: {_gt: 0}, baitId: {_gt: 0}}, distinct_on: [itemId,spot,baitId]) { itemId spot baitId } }";
        using var body = new StringContent(JsonSerializer.Serialize(new { query }), System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("https://gubal.ffxivteamcraft.com/graphql", body, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(token);
    }

    public static void ParseSpotBaits(FishDataFile data, string sources, string records)
    {
        SpotBaitInfo Get(uint item, uint spot) {
            if (!data.SpotBaits.TryGetValue(item, out var spots)) data.SpotBaits[item] = spots = new();
            if (!spots.TryGetValue(spot, out var value)) spots[spot] = value = new();
            return value;
        }
        using var sourceDoc = JsonDocument.Parse(sources);
        foreach (var pair in sourceDoc.RootElement.EnumerateObject())
        foreach (var row in pair.Value.EnumerateArray()) {
            var entry = Get(uint.Parse(pair.Name), row.GetProperty("spot").GetUInt32());
            uint bait = row.TryGetProperty("bait", out var b) ? b.GetUInt32() : 0;
            if (bait != 0 && !entry.Recommended.Contains(bait)) entry.Recommended.Add(bait);
            entry.MinimumGathering = Math.Max(entry.MinimumGathering, Read<int>(row, "minGathering"));
        }
        using var recordDoc = JsonDocument.Parse(records);
        if (recordDoc.RootElement.TryGetProperty("errors", out _)) throw new InvalidDataException("Bait records could not be downloaded.");
        var rows = recordDoc.RootElement.GetProperty("data").GetProperty("baits_per_fish_per_spot");
        if (rows.GetArrayLength() < 1000) throw new InvalidDataException("Bait catch records are incomplete.");
        foreach (var row in rows.EnumerateArray()) {
            uint item = row.GetProperty("itemId").GetUInt32(), spot = row.GetProperty("spot").GetUInt32(), bait = row.GetProperty("baitId").GetUInt32();
            if (item == 0 || spot == 0 || bait == 0) continue;
            var entry = Get(item, spot);
            if (!entry.Observed.Contains(bait)) entry.Observed.Add(bait);
        }
    }

    public static void ValidateLocations(FishDataFile data, string spotCatalog, string baitCatalog, string spearCatalog)
    {
        data.Locations.Clear();
        using var spots = JsonDocument.Parse(spotCatalog);
        using var baits = JsonDocument.Parse(baitCatalog);
        using var spears = JsonDocument.Parse(spearCatalog);
        var categories = baits.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("id").GetUInt32(),
            r => r.GetProperty("categories").EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number).Select(v => v.GetInt32()).ToArray());
        foreach (var spot in spots.RootElement.EnumerateArray()) {
            uint id = spot.GetProperty("id").GetUInt32();
            int category = spot.GetProperty("category").GetInt32();
            var fish = spot.GetProperty("fishes").Deserialize<List<uint>>()!;
            foreach (uint item in fish) data.Locations.Add(new FishLocation {
                ItemId = item, SpotId = id, MapId = Read<uint>(spot, "mapId"),
                PlaceId = Read<uint>(spot, "placeId"), ZoneId = Read<uint>(spot, "zoneId")
            });
            foreach (var pair in data.SpotBaits) {
                if (!pair.Value.TryGetValue(id, out var entry)) continue;
                // Discard records for the wrong fish/hole, incompatible water type,
                // or a mooch fish that cannot be caught in this hole.
                if (!fish.Contains(pair.Key)) { pair.Value.Remove(id); continue; }
                bool Invalid(uint bait) {
                    if (categories.TryGetValue(bait, out var allowed))
                        return allowed.Length > 0 && category > 0 && !allowed.Contains(category);
                    return (data.Info.ContainsKey(bait) || data.Fish.ContainsKey(bait)) && !fish.Contains(bait);
                }
                entry.Recommended.RemoveAll(Invalid);
                entry.Observed.RemoveAll(Invalid);
            }
        }
        foreach (var row in spears.RootElement.EnumerateArray()) data.Locations.Add(new FishLocation {
            ItemId = row.GetProperty("itemId").GetUInt32(), SpotId = row.GetProperty("id").GetUInt32(),
            MapId = Read<uint>(row, "mapId"), PlaceId = Read<uint>(row, "placeId"), ZoneId = Read<uint>(row, "zoneId"), Spearfishing = true
        });
        if (data.Locations.Count < 1000) throw new InvalidDataException("Fishing locations are incomplete.");
    }

    private static T? Read<T>(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.Deserialize<T>(JsonOptions) : default;
}
