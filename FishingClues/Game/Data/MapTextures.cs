using MapSheet = Lumina.Excel.Sheets.Map;

namespace FishingClues.Game.Data;

public static class MapTextures
{
    // game path of a map's base terrain texture (Id s1t2/01 -> ui/map/s1t2/01/s1t201_m.tex), null if the row has no Id
    public static string? PathFor(MapSheet map)
    {
        string id = map.Id.ToString();
        int slash = id.IndexOf('/');
        return slash > 0 && slash < id.Length - 1
            ? $"ui/map/{id[..slash]}/{id[(slash + 1)..]}/{id[..slash]}{id[(slash + 1)..]}_m.tex"
            : null;
    }
}
