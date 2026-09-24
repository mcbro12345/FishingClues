using FishingClues.Base;

namespace FishingClues.Game.Logic;

// the live Intuition buff: the timed window after catching the right predator fish. Player-triggered, so it can only be read from status effects
public sealed record IntuitionStatus(ushort Param, float RemainingSeconds);

public static class IntuitionService
{
    private const string IntuitionStatusName = "Intuition";

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
