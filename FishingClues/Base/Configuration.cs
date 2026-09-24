using System.Collections.Generic;
using Dalamud.Configuration;

namespace FishingClues.Base;

public sealed class Configuration : IPluginConfiguration
{
    private const int CurrentVersion = 19;

    // The area dropdowns' inset settings read 0 at these preferred pixel insets.
    public const float DropdownLeftInsetBaseline = -1.0f;
    public const float DropdownRightInsetBaseline = -7.0f;

    public int Version { get; set; } = CurrentVersion;

    // Journal window layout
    public float NativeWindowWidth { get; set; } = 1050.0f;
    public float NativeWindowHeight { get; set; } = 680.0f;
    public float NativeRegionWidth { get; set; } = 181.0f;
    public float NativeAreaWidth { get; set; } = 298.0f;
    public float NativeAreaDropdownLeftInset { get; set; }
    public float NativeAreaDropdownRightInset { get; set; }
    public float DetailsHeightRatio { get; set; } = 0.38f;
    public bool LockJournalDividers { get; set; }
    public bool LockRegionDivider { get; set; } = true;
    public bool LockAreaDivider { get; set; } = true;

    // Journal behaviour
    public bool ReplaceNormalFishingLog { get; set; } = true;
    public bool ShowOpenNormalLogButton { get; set; } = true;
    public bool UncaughtFishFirst { get; set; } = true;
    public bool SortBaitByItemLevel { get; set; }
    public bool DisableAvailabilityCountdown { get; set; }
    public bool Use12HourTime { get; set; } = true;
    public bool ShowAreaLocationMap { get; set; } = true;
    public bool MapPanelOpen { get; set; } = true;

    // Fish data
    public bool AutoRefreshFishData { get; set; } = true;
    public HashSet<uint> FavoriteFishItemIds { get; set; } = new();
    public Dictionary<ulong, HashSet<uint>> RevealedFish { get; set; } = new();

    // Keybind
    public bool JournalKeybindEnabled { get; set; }
    public int JournalKeybindKey { get; set; }
    public bool JournalKeybindCtrl { get; set; }
    public bool JournalKeybindAlt { get; set; }
    public bool JournalKeybindShift { get; set; }

    // Debugging
    public bool DebugMode { get; set; }
    public bool DebugRevealEverything { get; set; }

    public bool IsDividerLocked(int kind) => kind switch
    {
        0 => LockJournalDividers,
        1 => LockRegionDivider,
        2 => LockAreaDivider,
        _ => true,
    };

    // migrates a config saved by an older version, resets layout defaults that changed
    public void Migrate()
    {
        RevealedFish ??= new();
        FavoriteFishItemIds ??= new();

        if (Version < 15) NativeWindowWidth = 1050.0f;
        if (Version < 17)
        {
            NativeRegionWidth = 181.0f;
            NativeAreaDropdownRightInset = 0.0f;
        }
        if (Version < 18) NativeAreaDropdownLeftInset = 0.0f;
        if (Version < 19) NativeAreaWidth = 298.0f;
        Version = CurrentVersion;
    }
}
