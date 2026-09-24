using System;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using FishingClues.Base;
using KamiToolKit.Nodes;
using Lumina.Data.Files;

namespace FishingClues.UI.Windows;

// loading game textures into image nodes, shared by the map markers and the map button
public sealed partial class NativeJournalWindow
{
    // stillWanted is checked once the texture has loaded, the node may be gone by then
    private static async Task LoadGameIconAsync(ImGuiImageNode node, uint iconId, Func<bool> stillWanted, Action? onLoaded = null)
    {
        IDalamudTextureWrap texture;
        try
        {
            texture = await Services.TextureProvider.GetFromGameIcon(iconId).RentAsync();
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, $"[FishingClues] failed to load icon {iconId}.");
            return;
        }
        await Services.Framework.Run(() =>
        {
            if (!stillWanted())
            {
                texture.Dispose();
                return;
            }
            node.LoadTexture(texture);
            node.Alpha = 1.0f;
            onLoaded?.Invoke();
        });
    }

    // square RGBA pixels put on a node, same stillWanted rule as above
    private static async Task ShowRgbaAsync(ImGuiImageNode node, byte[] rgba, int size, string name, Func<bool> stillWanted, Action? onLoaded = null)
    {
        IDalamudTextureWrap texture;
        try
        {
            texture = await Services.TextureProvider.CreateFromRawAsync(RawImageSpecification.Rgba32(size, size), rgba, name);
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, $"[FishingClues] failed to create {name}.");
            return;
        }
        await Services.Framework.Run(() =>
        {
            if (!stillWanted())
            {
                texture.Dispose();
                return;
            }
            node.LoadTexture(texture);
            node.TextureSize = new Vector2(size, size);
            node.Alpha = 1.0f;
            onLoaded?.Invoke();
        });
    }

    // a square of a game texture as RGBA (Lumina gives BGRA), null if the file isn't there
    private static byte[]? ReadRgbaSquare(string path, int left, int top, int size)
    {
        var texture = Services.DataManager.GetFile<TexFile>(path);
        if (texture is null) return null;
        int width = texture.Header.Width;
        byte[] source = texture.ImageData;
        var rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int from = ((top + y) * width + left + x) * 4, to = (y * size + x) * 4;
                rgba[to + 0] = source[from + 2];
                rgba[to + 1] = source[from + 1];
                rgba[to + 2] = source[from + 0];
                rgba[to + 3] = source[from + 3];
            }
        return rgba;
    }
}
