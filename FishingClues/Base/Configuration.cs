using Dalamud.Configuration;

namespace FishingClues.Base;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 18;
    public System.Collections.Generic.HashSet<uint> FavoriteFishItemIds { get; set; } = new();
    public System.Collections.Generic.Dictionary<ulong, System.Collections.Generic.HashSet<uint>> RevealedFish { get; set; } = new();
    public float NativeWindowWidth { get; set; } = 1050.0f;
    public float NativeWindowHeight { get; set; } = 680.0f;
    public float NativeRegionWidth { get; set; } = 181.0f;
    public float NativeAreaWidth { get; set; } = 286.0f;
    // How far the area dropdown boxes are inset from the left/right edges of the
    // area column, relative to DropdownLeftInsetBaseline/DropdownRightInsetBaseline
    // below - the sliders read 0 at that preferred baseline, not at "fills the column".
    public float NativeAreaDropdownLeftInset { get; set; }
    public float NativeAreaDropdownRightInset { get; set; }
    // The actual left/right inset (in pixels) when the sliders above read 0.
    public const float DropdownLeftInsetBaseline = -1.0f;
    public const float DropdownRightInsetBaseline = -7.0f;
    public bool AutoRefreshFishData { get; set; } = true;
    public bool DisableAvailabilityCountdown { get; set; }
    public bool Use12HourTime { get; set; }
    public bool DebugRevealEverything { get; set; }
    public bool UncaughtFishFirst { get; set; } = true;
    public bool LockJournalDividers { get; set; }
    public bool LockRegionDivider { get; set; } = true;
    public bool LockAreaDivider { get; set; } = true;
    public bool DebugMode { get; set; }
    public bool IsDividerLocked(int kind) => kind switch
    {
        0 => LockJournalDividers,
        1 => LockRegionDivider,
        2 => LockAreaDivider,
        _ => true,
    };

    public void ApplySimplifiedJournalSettings()
    {
        RevealedFish ??= new();
        FavoriteFishItemIds ??= new();
    }
    public float DetailsHeightRatio { get; set; } = 0.38f;
    public bool ShowOpenNormalLogButton { get; set; } = true;
    public bool ShowAreaLocationMap { get; set; } = true;
    public bool ReplaceNormalFishingLog { get; set; } = true;
    public bool JournalKeybindEnabled { get; set; }
    public int JournalKeybindKey { get; set; }
    public bool JournalKeybindCtrl { get; set; }
    public bool JournalKeybindAlt { get; set; }
    public bool JournalKeybindShift { get; set; }
}
