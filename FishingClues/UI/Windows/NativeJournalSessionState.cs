using System;
using System.Collections.Generic;

namespace FishingClues.UI.Windows;

// what a window remembers between openings in a session
public sealed class NativeJournalSessionState
{
    public string? SelectedRegion { get; set; }
    public uint SelectedSpot { get; set; }
    public uint SelectedFish { get; set; }
    public float RegionScroll, AreaScroll, FishScroll, DetailsScroll;
    public Dictionary<string, RegionViewState> Regions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string SearchText { get; set; } = "";
    public bool HasOpenedOnce { get; set; }
    // map view at last close, MapZoom 0 = nothing saved
    public string? MapArea { get; set; }
    public float MapZoom, MapPanX, MapPanY;
}

public sealed record RegionViewState(uint Spot, uint Fish, float AreaScroll, float FishScroll, float DetailsScroll);
