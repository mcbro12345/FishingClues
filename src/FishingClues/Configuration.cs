using Dalamud.Configuration;

namespace FishingClues;

public enum JournalUiMode
{
    Dalamud = 0,
    Native = 1,
}

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 13;
    public System.Collections.Generic.HashSet<uint> FavoriteFishItemIds { get; set; } = new();
    public System.Collections.Generic.Dictionary<ulong, System.Collections.Generic.HashSet<uint>> RevealedFish { get; set; } = new();
    public JournalUiMode UiMode { get; set; } = JournalUiMode.Native;
    public float NativeWindowWidth { get; set; } = 1050.0f;
    public float NativeWindowHeight { get; set; } = 680.0f;
    public float NativeRegionWidth { get; set; } = 162.0f;
    public float NativeAreaWidth { get; set; } = 263.0f;
    public float NativeAreaDropdownWidth { get; set; } = 999.0f;
    public bool OpenUnknownOnLeftClick { get; set; }
    public bool UseNativeFishDetails { get; set; }
    public bool EmbedFishDetails { get; set; } = true;
    public bool AutoRefreshFishData { get; set; } = true;
    public bool DisableAvailabilityCountdown { get; set; }
    public bool UncaughtFishFirst { get; set; } = true;
    public bool LockJournalDividers { get; set; }
    // Only the fish/details divider (kind 0) is ever user-adjustable; the
    // region/area dividers (kind 1/2) stay locked in both the journal and the
    // fish search.
    public bool IsDividerLocked(int kind) => kind != 0 || LockJournalDividers;

    public void ApplySimplifiedJournalSettings()
    {
        RevealedFish ??= new();
        FavoriteFishItemIds ??= new();
        UiMode = JournalUiMode.Native;
        EmbedFishDetails = true;
        UseNativeFishDetails = false;
        OpenUnknownOnLeftClick = false;
    }
    public float DetailsHeightRatio { get; set; } = 0.38f;
    public bool ShowOpenNormalLogButton { get; set; } = true;
    public bool ReplaceNormalFishingLog { get; set; } = true;
    public bool JournalKeybindEnabled { get; set; }
    public int JournalKeybindKey { get; set; }
    public bool JournalKeybindCtrl { get; set; }
    public bool JournalKeybindAlt { get; set; }
    public bool JournalKeybindShift { get; set; }
}
