using System;
using System.Collections.Generic;

using FishingClues.Game.Logic;
using FishingClues.Game.Models;

namespace FishingClues.UI.Windows;

// What a journal window is built from. The main journal and the fish guide are
// the same window class; the guide sets GuideFish.
public sealed record NativeJournalOptions
{
    // Layout, from the settings
    public required float RegionWidth { get; init; }
    public required float AreaWidth { get; init; }
    public required float AreaDropdownLeftInset { get; init; }
    public required float AreaDropdownRightInset { get; init; }
    public required bool ShowNormalLogButton { get; init; }

    // What the window calls back into
    public required Action OpenNormalLog { get; init; }
    public required Func<JournalFish, FishClueSection> BuildDetails { get; init; }
    public required Action<Exception> ReportSetupError { get; init; }
    public required Action SaveLayout { get; init; }
    public Func<JournalFish, GuideDetails>? BuildGuideDetails { get; init; }
    public Func<JournalFish, FishAvailabilityInfo?>? GetAvailability { get; init; }
    // Opens the fish guide (the button under the region list).
    public Action? OpenGuide { get; init; }
    // Opens the plugin settings (the gear in the title bar).
    public Action? OpenSettings { get; init; }

    // Set only for the fish guide: the fish it lists.
    public IReadOnlyList<JournalFish>? GuideFish { get; init; }
    // A hole discovered since the journal was last opened; the journal opens straight to it.
    public uint? PendingDiscoveredSpotId { get; init; }
}
