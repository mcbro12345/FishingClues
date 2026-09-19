using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FishingClues.Game.Data;

// Reads text the game's own FishingNote addon has already drawn, so we only
// ever surface a fish or region name the vanilla log has itself revealed.
internal static unsafe class FishingNoteAddon
{
    public static bool IsNameVisible(AtkUnitBase* addon, string expected) => addon != null && FindVisibleText(&addon->UldManager, expected, 0);

    private static bool FindVisibleText(AtkUldManager* manager, string expected, int depth)
    {
        if (manager == null || manager->NodeList == null || depth > 12) return false;
        for (int i = 0; i < manager->NodeListCount; i++)
        {
            AtkResNode* node = manager->NodeList[i];
            if (node == null || !node->IsVisible()) continue;
            if (node->Type == NodeType.Text && ((AtkTextNode*)node)->NodeText.ToString().Trim().Equals(expected, System.StringComparison.OrdinalIgnoreCase)) return true;
            if (node->Type == NodeType.Component)
            {
                AtkComponentBase* component = ((AtkComponentNode*)node)->Component;
                if (component != null && FindVisibleText(&component->UldManager, expected, depth + 1)) return true;
            }
        }
        return false;
    }
}
