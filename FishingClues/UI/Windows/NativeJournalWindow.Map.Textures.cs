using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Lumina.Data.Files;

using FishingClues.Base;
using FishingClues.Game.Data;

using MapSheet = Lumina.Excel.Sheets.Map;
using TerritoryTypeSheet = Lumina.Excel.Sheets.TerritoryType;

namespace FishingClues.UI.Windows;

// Loading and generating the map's textures.
public sealed partial class NativeJournalWindow
{
    private const uint WorldMapRowId = 1;
    private const int MapBackdropTextureSize = 4;
    private const int MapBorderPatternSize = 6;
    private const int MapAreaCircleTextureSize = 128;
    private const int MapTextureCacheSize = 4;

    // Dark bronze edge, the base colour (#977645), a highlight, then back again.
    private static readonly (byte R, byte G, byte B)[] BronzeBorderRamp =
    {
        (85, 64, 38), (151, 118, 69), (196, 160, 105), (222, 188, 132), (151, 118, 69), (85, 64, 38),
    };

    // built textures are cached across openings, each window gets its own wrap since nodes dispose theirs
    private static readonly Dictionary<string, Task<IDalamudTextureWrap?>> MapTextureCache = new();
    private static readonly List<string> MapTextureCacheOrder = new();

    private static string? WorldMapTexturePath()
        => Services.DataManager.GetExcelSheet<MapSheet>().TryGetRow(WorldMapRowId, out MapSheet map) ? MapTextures.PathFor(map) : null;

    private void EnsureMapTexture(string? texturePath)
    {
        if (mapImage is null || string.IsNullOrEmpty(texturePath) || texturePath == mapLoadedTexturePath)
            return;
        mapLoadedTexturePath = texturePath;
        _ = LoadMapTextureAsync(texturePath);
    }

    private async Task LoadMapTextureAsync(string texturePath)
    {
        IDalamudTextureWrap texture;
        try
        {
            var cached = await GetCachedMapTextureAsync(texturePath);
            if (cached is null) return;
            texture = cached.CreateWrapSharingLowLevelResource();
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, $"[FishingClues] Area map: failed to load '{texturePath}'");
            return;
        }

        await Services.Framework.Run(() =>
        {
            // player may have moved on
            if (mapImage is null || mapLoadedTexturePath != texturePath)
            {
                texture.Dispose();
                return;
            }
            mapImage.LoadTexture(texture);
            mapImage.TextureSize = new Vector2(2048.0f, 2048.0f);
            mapImage.Alpha = 1.0f;
            if (mapBackdrop is not null) mapBackdrop.Alpha = 1.0f;
        });
    }

    // start building the current zone's map early
    public static void PrewarmMapCache(uint territoryId)
    {
        if (territoryId == 0) return;
        if (!Services.DataManager.GetExcelSheet<TerritoryTypeSheet>().TryGetRow(territoryId, out var territory)
            || territory.Map.ValueNullable is not { } map || map.RowId == 0) return;
        if (MapTextures.PathFor(map) is { } path) _ = GetCachedMapTextureAsync(path);
    }

    public static void ClearMapTextureCache()
    {
        areaCircleTexture?.Dispose();
        areaCircleTexture = null;
        lock (MapTextureCache)
        {
            foreach (var task in MapTextureCache.Values) DisposeWhenBuilt(task);
            MapTextureCache.Clear();
            MapTextureCacheOrder.Clear();
        }
    }

    private static void DisposeWhenBuilt(Task<IDalamudTextureWrap?> task)
        => _ = task.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result?.Dispose(); });

    private static Task<IDalamudTextureWrap?> GetCachedMapTextureAsync(string path)
    {
        lock (MapTextureCache)
        {
            if (MapTextureCache.TryGetValue(path, out var existing)) return existing;
            var task = BuildMapTextureAsync(path);
            MapTextureCache[path] = task;
            MapTextureCacheOrder.Add(path);
            while (MapTextureCacheOrder.Count > MapTextureCacheSize)
            {
                string oldest = MapTextureCacheOrder[0];
                MapTextureCacheOrder.RemoveAt(0);
                if (MapTextureCache.Remove(oldest, out var evicted)) DisposeWhenBuilt(evicted);
            }
            return task;
        }
    }

    private static async Task<IDalamudTextureWrap?> BuildMapTextureAsync(string path)
    {
        try
        {
            return await CreateMapCompositeAsync(path) ?? await Services.TextureProvider.GetFromGame(path).RentAsync();
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, $"[FishingClues] Area map: failed to load '{path}'");
            lock (MapTextureCache) { MapTextureCache.Remove(path); MapTextureCacheOrder.Remove(path); }
            return null;
        }
    }

    // the game multiplies two textures: <id>_m.tex (terrain) and <id>m_m.tex (parchment). null if there's no parchment or the size differs
    private static async Task<IDalamudTextureWrap?> CreateMapCompositeAsync(string texturePath)
    {
        const string suffix = "_m.tex";
        if (!texturePath.EndsWith(suffix, StringComparison.Ordinal)) return null;
        string paperPath = texturePath[..^suffix.Length] + "m_m.tex";
        if (!Services.DataManager.FileExists(paperPath)) return null;

        (int Width, int Height, byte[] Pixels)? composite = await Task.Run(() =>
        {
            var map = Services.DataManager.GetFile<TexFile>(texturePath);
            var paper = Services.DataManager.GetFile<TexFile>(paperPath);
            if (map is null || paper is null) return ((int, int, byte[])?)null;
            int width = map.Header.Width, height = map.Header.Height;
            if (paper.Header.Width != width || paper.Header.Height != height) return null;
            byte[] top = map.ImageData, bottom = paper.ImageData;
            var pixels = new byte[width * height * 4];
            // BGRA -> RGBA
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i + 0] = (byte)(top[i + 2] * bottom[i + 2] / 255);
                pixels[i + 1] = (byte)(top[i + 1] * bottom[i + 1] / 255);
                pixels[i + 2] = (byte)(top[i + 0] * bottom[i + 0] / 255);
                pixels[i + 3] = 255;
            }
            return (width, height, pixels);
        });
        if (composite is not var (w, h, data)) return null;
        return await Services.TextureProvider.CreateFromRawAsync(RawImageSpecification.Rgba32(w, h), data, "FishingCluesMapComposite");
    }

    private static IDalamudTextureWrap CreateSquareTexture(int size, string name, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                int i = (y * size + x) * 4;
                pixels[i + 0] = r;
                pixels[i + 1] = g;
                pixels[i + 2] = b;
                pixels[i + 3] = a;
            }
        return Services.TextureProvider.CreateFromRaw(RawImageSpecification.Rgba32(size, size), pixels, name);
    }

    // one side of the border, coloured from BronzeBorderRamp across its thickness
    private static IDalamudTextureWrap CreateBronzeBorderTexture(bool vertical, bool outerFirst)
        => CreateSquareTexture(MapBorderPatternSize, "FishingCluesMapBorder", (x, y) =>
        {
            int across = vertical ? x : y;
            var (r, g, b) = BronzeBorderRamp[outerFirst ? across : MapBorderPatternSize - 1 - across];
            return (r, g, b, 255);
        });

    private static IDalamudTextureWrap CreateBackdropTexture()
        => CreateSquareTexture(MapBackdropTextureSize, "FishingCluesMapBackdrop", (_, _) => (0, 0, 0, 153));

    // built once, each marker gets a wrap sharing its GPU texture
    private static IDalamudTextureWrap? areaCircleTexture;

    private static IDalamudTextureWrap SharedAreaCircleTexture()
        => (areaCircleTexture ??= CreateAreaCircleTexture()).CreateWrapSharingLowLevelResource();

    // range circle behind a marker
    private static IDalamudTextureWrap CreateAreaCircleTexture()
    {
        const float center = MapAreaCircleTextureSize / 2.0f;
        const float radius = center - 2.0f;
        return CreateSquareTexture(MapAreaCircleTextureSize, "FishingCluesMapAreaCircle", (x, y) =>
        {
            float dx = x + 0.5f - center, dy = y + 0.5f - center;
            return (130, 220, 215, MathF.Sqrt(dx * dx + dy * dy) <= radius ? (byte)70 : (byte)0);
        });
    }
}