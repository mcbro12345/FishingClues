using System;
using System.Collections.Generic;

using FishingClues.Game.Logic;
using FishingClues.Game.Models;

namespace FishingClues.UI.Windows;

// what a journal window is built from, the guide is the same class with GuideFish set
public sealed record NativeJournalOptions
{
    public required float RegionWidth { get; init; }
    public required float AreaWidth { get; init; }
    public required float AreaDropdownLeftInset { get; init; }
    public required float AreaDropdownRightInset { get; init; }
    public required bool ShowNormalLogButton { get; init; }

    public required Action OpenNormalLog { get; init; }
    public required Func<JournalFish, FishClueSection> BuildDetails { get; init; }
    public required Action<Exception> ReportSetupError { get; init; }
    public required Action SaveLayout { get; init; }
    public Func<JournalFish, GuideDetails>? BuildGuideDetails { get; init; }
    public Func<JournalFish, FishAvailabilityInfo?>? GetAvailability { get; init; }
    public Action? OpenGuide { get; init; }
    public Action? OpenSettings { get; init; }

    public IReadOnlyList<JournalFish>? GuideFish { get; init; }
    // hole discovered since the last open, the journal opens to it
    public uint? PendingDiscoveredSpotId { get; init; }
}
