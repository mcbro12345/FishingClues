using FishingClues.Base;

namespace FishingClues.Game.Logic;

// The live "Intuition" fishing buff: catching the right predator fish back-to-back opens
// a timed window where a specific rarer fish becomes catchable. Unlike the plugin's other
// availability math (pure Eorzea time/weather, calculable without touching the game), this
// is player-triggered and can only be read from the player's own status effects.
public sealed record IntuitionStatus(ushort Param, float RemainingSeconds);

public static class IntuitionService
{
    private const string IntuitionStatusName = "Intuition";

    // Null when no Intuition window is currently open on the player.
    public static IntuitionStatus? GetActive()
    {
        var player = Services.ObjectTable.LocalPlayer;
        if (player is null) return null;
        foreach (var status in player.StatusList)
        {
            if (status.GameData.ValueNullable is { } data && data.Name.ToString() == IntuitionStatusName)
                return new IntuitionStatus(status.Param, status.RemainingTime);
        }
        return null;
    }
}
