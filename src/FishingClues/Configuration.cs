using Dalamud.Configuration;

namespace FishingClues;

public enum JournalUiMode
{
    Dalamud = 0,
    Native = 1,
}

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 8;
    public System.Collections.Generic.HashSet<uint> FavoriteFishItemIds { get; set; } = new();
    public System.Collections.Generic.Dictionary<ulong, System.Collections.Generic.HashSet<uint>> RevealedFish { get; set; } = new();
    public JournalUiMode UiMode { get; set; } = JournalUiMode.Native;
    public float NativeWindowWidth { get; set; } = 1050.0f;
    public float NativeWindowHeight { get; set; } = 680.0f;
    public float NativeRegionWidth { get; set; } = 180.0f;
    public float NativeAreaWidth { get; set; } = 350.0f;
    public float NativeAreaDropdownWidth { get; set; } = 310.0f;
    public bool OpenUnknownOnLeftClick { get; set; }
    public bool UseNativeFishDetails { get; set; }
    public bool EmbedFishDetails { get; set; } = true;
    public bool AutoRefreshFishData { get; set; } = true;
    public bool UncaughtFishFirst { get; set; }
    public bool LockJournalDividers { get; set; }
    public bool LockRegionDivider { get; set; }
    public bool LockAreaDivider { get; set; }
    public bool LockDetailsDivider { get; set; }
    public bool IsDividerLocked(int kind) => kind switch
    {
        1 => LockRegionDivider,
        2 => LockAreaDivider,
        _ => LockDetailsDivider,
    };

    public void ApplySimplifiedJournalSettings()
    {
        RevealedFish ??= new();
        FavoriteFishItemIds ??= new();
        if (Version < 8)
        {
            LockRegionDivider = LockAreaDivider = LockDetailsDivider = LockJournalDividers;
            Version = 8;
        }
        UiMode = JournalUiMode.Native;
        EmbedFishDetails = true;
        UseNativeFishDetails = false;
        OpenUnknownOnLeftClick = false;
    }
    public float DetailsHeightRatio { get; set; } = 0.38f;
    public bool ShowOpenNormalLogButton { get; set; } = true;
    public bool ReplaceNormalFishingLog { get; set; }
}
