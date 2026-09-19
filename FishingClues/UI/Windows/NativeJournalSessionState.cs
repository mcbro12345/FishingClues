using System;
using System.Collections.Generic;

namespace FishingClues.UI.Windows;

// What a journal window remembers between openings within a game session: the selection,
// the scroll positions, the search text and the map's view.
public sealed class NativeJournalSessionState
{
    public string? SelectedRegion { get; set; }
    public uint SelectedSpot { get; set; }
    public uint SelectedFish { get; set; }
    public float RegionScroll, AreaScroll, FishScroll, DetailsScroll;
    public Dictionary<string, RegionViewState> Regions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string SearchText { get; set; } = "";
    // Only the first open of a session goes to the player's location.
    public bool HasOpenedOnce { get; set; }
    // The map's view when the journal was last closed. MapZoom 0 means nothing is saved.
    public string? MapArea { get; set; }
    public float MapZoom, MapPanX, MapPanY;
}

public sealed record RegionViewState(uint Spot, uint Fish, float AreaScroll, float FishScroll, float DetailsScroll);
