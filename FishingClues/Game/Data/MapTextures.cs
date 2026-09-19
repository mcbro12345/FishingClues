using MapSheet = Lumina.Excel.Sheets.Map;

namespace FishingClues.Game.Data;

public static class MapTextures
{
    // The game path of a map's terrain texture: "ui/map/s1t2/01/s1t201_m.tex" for
    // the Map row whose Id is "s1t2/01". Always the base name; the game picks the
    // high-resolution variant itself. Null when the row has no usable Id.
    public static string? PathFor(MapSheet map)
    {
        string id = map.Id.ToString();
        int slash = id.IndexOf('/');
        return slash > 0 && slash < id.Length - 1
            ? $"ui/map/{id[..slash]}/{id[(slash + 1)..]}/{id[..slash]}{id[(slash + 1)..]}_m.tex"
            : null;
    }
}
