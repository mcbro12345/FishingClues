using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Numerics;
using Dalamud.Game.Addon.Events;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

using FishingClues.Base;
using FishingClues.Game.Models;
using FishingClues.Game.Logic;
using FishingClues.UI.Components;

namespace FishingClues.UI.Windows;

public sealed class NativeJournalSessionState
{
    public string? SelectedRegion { get; set; }
    public uint SelectedSpot { get; set; }
    public uint SelectedFish { get; set; }
    public float RegionScroll, AreaScroll, FishScroll, DetailsScroll;
    public HashSet<string> ExpandedAreas { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RegionViewState> Regions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string SearchText { get; set; } = "";
}

public sealed record RegionViewState(uint Spot, uint Fish, float AreaScroll, float FishScroll, float DetailsScroll);

public sealed partial class NativeJournalWindow(
    IReadOnlyList<JournalRegion> regions,
    NativeJournalSessionState sessionState,
    float configuredRegionWidth,
    float configuredAreaWidth,
    float configuredAreaDropdownLeftInset,
    float configuredAreaDropdownRightInset,
    bool configuredShowNormalLogButton,
    Action openNormalLog,
    Configuration configuration,
    Func<JournalFish, FishClueSection> buildDetails,
    Action<Exception> reportSetupError,
    Action saveDivider,
    IAddonEventManager addonEvents,
    Action? openGuide = null,
    IReadOnlyList<JournalFish>? guideFish = null, Func<JournalFish, GuideDetails>? guideDetails = null,
    Func<JournalFish, FishAvailabilityInfo?>? getAvailability = null) : NativeAddon
{
    private const float HeaderHeight = 28.0f;
    private const float FishSummaryHeight = 61.0f;
    private const float ColumnGap = 10.0f;
    private ScrollingNode<JournalListNode>? regionList;
    private ScrollingNode<JournalListNode>? areaList;
    private ScrollingNode<JournalListNode>? fishList;
    private ScrollingNode<JournalListNode>? detailsList;
    private HorizontalLineNode? detailsDivider;
    private CollisionNode? dividerHandle;
    private CollisionNode? regionDividerHandle, areaDividerHandle;
    private int dragKind;
    private float dragStartX, dragStartWidth;
    private bool draggingDivider;
    private bool ownsResizeCursor;
    private float dragStartY, dragStartRatio;
    private FishClueSection? selectedDetails;
    private uint selectedFish;
    private bool renderedUncaughtFirst;
    private readonly Dictionary<FishEntryRowNode, uint> fishButtons = new();
    private readonly List<(FishEntryRowNode Row, JournalFish Fish)> availabilityRows = new();
    private long nextAvailabilityRefresh;
    private readonly Dictionary<ListButtonNode, JournalRegion> regionButtons = new();
    private readonly Dictionary<ListButtonNode, uint> spotButtons = new();
    private LabelTextNode? detailsHint;
    private CategoryTextNode? regionHeader;
    private CategoryTextNode? areaHeader;
    private CategoryTextNode? fishHeader;
    private CategoryTextNode? spotTitle;
    private LabelTextNode? spotSummary;
    private HorizontalLineNode? summaryDivider;
    private VerticalLineNode? regionDivider;
    private VerticalLineNode? fishDivider;
    private ButtonBase? normalLogButton;
    private TextureButtonNode? guideButton;
    private TextInputNode? searchInput;
    private string searchText = "";
    private uint requestedSearchItem;
    private TextButtonNode? searchButton;
    private bool searchPending;
    private GuideDetails? selectedGuide;
    private GuideLocation? guideLocation;
    private FishingPole? guidePole;
    private IReadOnlyList<FishingPole> guidePoles = Array.Empty<FishingPole>();
    private bool guideDetailsPending;
    private bool guideLocationPending;
    private bool GuideMode => guideFish is not null;
    private JournalRegion? selectedRegion;
    private JournalSpot? selectedSpot;
    private Vector2 contentOrigin;
    private float regionWidthSetting = configuredRegionWidth;
    private float areaWidthSetting = configuredAreaWidth;
    private float dropdownLeftInsetSetting = configuredAreaDropdownLeftInset;
    private float dropdownRightInsetSetting = configuredAreaDropdownRightInset;
    private bool showNormalLogButton = configuredShowNormalLogButton;

    protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> values)
    {
        float regionWidth = Math.Clamp(regionWidthSetting, 130.0f, 280.0f);
        float maximumAreaWidth = Math.Max(240.0f, ContentSize.X - regionWidth - 380.0f);
        float areaWidth = Math.Clamp(areaWidthSetting, 240.0f, Math.Min(500.0f, maximumAreaWidth));
        float fishX = regionWidth + areaWidth + ColumnGap * 2;
        contentOrigin = ContentStartPosition;

        regionHeader = AddHeader("REGION", 0, regionWidth);
        areaHeader = AddHeader("AREA", regionWidth + ColumnGap, areaWidth);
        fishHeader = AddHeader("FISH", fishX, ContentSize.X - fishX);
        spotTitle = new CategoryTextNode { Height = 24.0f, String = "Select a fishing hole." };
        spotSummary = new LabelTextNode { Height = 24.0f, FontSize = 14, String = "" };
        spotTitle.AttachNode(this);
        spotSummary.AttachNode(this);
        summaryDivider = new HorizontalLineNode { Height = 2.0f };
        summaryDivider.AttachNode(this);

        float regionListHeight = ContentSize.Y - HeaderHeight - (showNormalLogButton || openGuide is not null ? 48.0f : 0.0f);
        regionList = CreateList(contentOrigin + new Vector2(0, HeaderHeight), new Vector2(regionWidth, regionListHeight));
        areaList = CreateList(contentOrigin + new Vector2(regionWidth + ColumnGap, HeaderHeight), new Vector2(areaWidth, ContentSize.Y - HeaderHeight));
        areaList.ContentNode.FitWidth = false;
        fishList = CreateList(contentOrigin + new Vector2(fishX, HeaderHeight), new Vector2(ContentSize.X - fishX, ContentSize.Y - HeaderHeight));
        detailsList = CreateList(contentOrigin, new Vector2(200, 150));
        detailsDivider = new HorizontalLineNode { Height = 2.0f };
        detailsList.AttachNode(this);
        detailsDivider.AttachNode(this);
        dividerHandle = new CollisionNode { ShowClickableCursor = false };
        dividerHandle.AddEvent(AtkEventType.MouseDown, () =>
        {
            if (configuration.IsDividerLocked(0)) return;
            BeginDividerDrag(0);
        });
        dividerHandle.AttachNode(this);
        regionDividerHandle = CreateColumnHandle(1);
        areaDividerHandle = CreateColumnHandle(2);
        detailsHint = new LabelTextNode
        {
            FontSize = 14,
            AlignmentType = AlignmentType.Center,
            String = "Click a fish to see catch details",
        };
        detailsHint.AttachNode(this);

        regionDivider = AddDivider(regionWidth + ColumnGap / 2, false);
        fishDivider = AddDivider(regionWidth + areaWidth + ColumnGap * 1.5f, true);

        foreach (JournalRegion region in regions)
        {
            JournalRegion captured = region;
            var regionButton = new ListButtonNode
            {
                Height = 25.0f,
                String = region.IsUnlocked ? region.Name : "???",
                OnClick = () => SelectRegion(captured),
            };
            regionButtons.Add(regionButton, captured);
            regionList.ContentNode.AddNode(regionButton);
        }
        regionList.RecalculateSizes();
        regionList.AttachNode(this);
        areaList.AttachNode(this);
        fishList.AttachNode(this);
        ApplyAreaDropdownWidths();

        LayoutAttachedNodes();

        if (!GuideMode && regions.Count > 0)
        {
            JournalRegion initialRegion = regions.FirstOrDefault(region => region.Name == sessionState.SelectedRegion) ?? regions[0];
            SelectRegion(initialRegion, true);
        }
        if (!GuideMode) InitializeJournalButton();
        else
        {
            searchText = sessionState.SearchText;
            searchInput = new TextInputNode {
                Size = new Vector2(Math.Max(200, ContentSize.X), 28),
                PlaceholderString = "Search all fish...", MaxCharacters = 100,
                String = searchText,
                OnInputComplete = text => { searchText = text.ToString(); sessionState.SearchText = searchText; searchPending = true; }
            };
            searchInput.AttachNode(this);
            searchButton = new TextButtonNode {
                String = "Search", Size = new Vector2(80, 28),
                OnClick = () => { searchText = searchInput.String.ToString(); sessionState.SearchText = searchText; searchPending = true; }
            };
            searchButton.AttachNode(this);
            if (!string.IsNullOrWhiteSpace(searchText)) searchPending = true;
            else ShowSearchPrompt();
            LayoutAttachedNodes();
        }
        // guide mode restores selection later, after RefreshSearch rebuilds the list
        if (!GuideMode)
        {
            var restoredFish = selectedSpot?.Fish.FirstOrDefault(f => f.FishParameterId == sessionState.SelectedFish);
            if (restoredFish is not null)
            {
                selectedFish = restoredFish.FishParameterId;
                UpdateFishSelection();
                selectedDetails = buildDetails(restoredFish);
                if (guideDetails is not null) {
                    selectedGuide = guideDetails(restoredFish);
                    guideLocation = selectedGuide.Locations.FirstOrDefault();
                    guidePoles = guideLocation is null ? Array.Empty<FishingPole>() : selectedGuide.GetPoles(guideLocation);
                    guidePole = guidePoles.FirstOrDefault();
                }
                RenderDetails();
            }
        }
        RestoreScroll(regionList, sessionState.RegionScroll);
        RestoreScroll(areaList, sessionState.AreaScroll);
        RestoreScroll(fishList, sessionState.FishScroll);
        RestoreScroll(detailsList, sessionState.DetailsScroll);
    }

    private static void RestoreScroll(ScrollingNode<JournalListNode> list, float position)
    {
        list.RecalculateSizes();
        list.ScrollBarNode.ScrollPosition = float.IsFinite(position)
            ? Math.Clamp(position, 0, Math.Max(0, list.ScrollBarNode.ScrollMaxPosition)) : 0;
    }

    private Vector2 FooterPosition => new(22, Size.Y - 56);

    private void InitializeJournalButton()
    {
        try
        {
            normalLogButton = new TextureButtonNode {
                TexturePath = "ui/uld/FishingNoteBook.tex",
                TextureCoordinates = new Vector2(60, 0), TextureSize = new Vector2(28, 28),
            };
            AttachJournalButton();
        }
        catch (Exception ex)
        {
            reportSetupError(ex);
            normalLogButton?.Dispose();
            normalLogButton = new TextButtonNode { String = "Log" };
            AttachJournalButton();
        }
    }

    private CollisionNode CreateColumnHandle(int kind)
    {
        var handle = new CollisionNode { ShowClickableCursor = false };
        handle.AddEvent(AtkEventType.MouseDown, () =>
        {
            if (configuration.IsDividerLocked(kind)) return;
            BeginDividerDrag(kind);
        });
        handle.AttachNode(this);
        return handle;
    }

    // AddEvent(MouseDown, ...) marks a node HasCollision so it blocks whatever's
    // behind it from ever seeing the click - by design, so dragging a divider
    // doesn't also click through to the fish list underneath it. That collision
    // flag isn't tied to IsVisible, though, so a "locked" (invisible) handle was
    // still silently swallowing clicks meant for anything under its strip - which
    // is exactly where the area dropdown boxes sit once they're inset far enough
    // to reach the region/area gap. This turns collision off along with
    // visibility so a locked handle stops intercepting clicks entirely.
    private static void SetDividerHandleInteractive(CollisionNode handle, bool interactive)
    {
        handle.IsVisible = interactive;
        if (interactive)
            handle.AddNodeFlags(NodeFlags.EmitsEvents, NodeFlags.RespondToMouse, NodeFlags.HasCollision);
        else
            handle.RemoveNodeFlags(NodeFlags.HasCollision, NodeFlags.RespondToMouse, NodeFlags.EmitsEvents);
    }

    private unsafe void BeginDividerDrag(int kind)
    {
        var framework = Framework.Instance();
        if (framework == null || configuration.IsDividerLocked(kind)) return;
        var mouse = framework->CursorInputs;
        if ((mouse.MouseButtonHeldFlags & MouseButtonFlags.LBUTTON) == 0) return;
        draggingDivider = true;
        dragKind = kind;
        dragStartX = mouse.PositionX;
        dragStartY = mouse.PositionY;
        dragStartRatio = configuration.DetailsHeightRatio;
        dragStartWidth = kind == 1 ? regionList?.Width ?? regionWidthSetting : areaList?.Width ?? areaWidthSetting;
    }

    private static bool HitDivider(CollisionNode? handle, CursorInputData mouse, float scale)
    {
        if (handle is null || !handle.IsVisible) return false;
        Vector2 position = handle.ScreenPosition;
        return mouse.PositionX >= position.X && mouse.PositionX <= position.X + handle.Width * scale
            && mouse.PositionY >= position.Y && mouse.PositionY <= position.Y + handle.Height * scale;
    }

    private void AttachJournalButton()
    {
        if (normalLogButton is null) return;
        normalLogButton.Position = FooterPosition;
        normalLogButton.Size = new Vector2(28.0f, 28.0f);
        normalLogButton.IsVisible = showNormalLogButton;
        normalLogButton.IsEnabled = true;
        normalLogButton.TextTooltip = "Open the normal Fishing Log";
        normalLogButton.OnClick = openNormalLog;
        normalLogButton.AttachNode(this);
        if (openGuide is not null && guideButton is null)
        {
            guideButton = new TextureButtonNode {
                Position = FooterPosition + new Vector2(34, 0), Size = new Vector2(28, 28),
                TexturePath = "ui/uld/FishingNoteBook.tex",
                TextureCoordinates = new Vector2(88, 0), TextureSize = new Vector2(28, 28),
                TextTooltip = "Search all fish", OnClick = openGuide,
            };
            guideButton.AttachNode(this);
        }
    }

    public void ApplyLayout(float windowWidth, float windowHeight, float regionWidth, float areaWidth,
        float dropdownLeftInset, float dropdownRightInset, bool configuredShowButton)
    {
        regionWidthSetting = regionWidth;
        areaWidthSetting = areaWidth;
        dropdownLeftInsetSetting = dropdownLeftInset;
        dropdownRightInsetSetting = dropdownRightInset;
        showNormalLogButton = configuredShowButton;
        Size = new Vector2(windowWidth, windowHeight);
        nextAvailabilityRefresh = 0;
        if (!IsOpen)
            return;

        // Relaying out the lists below resets their scroll to the top as a side
        // effect, and a settings change doesn't otherwise touch the currently open
        // fish - preserve both here so toggling a setting doesn't visibly reset
        // the journal the player is looking at.
        float regionScroll = regionList?.ScrollBarNode.ScrollPosition ?? 0;
        float areaScroll = areaList?.ScrollBarNode.ScrollPosition ?? 0;
        float fishScroll = fishList?.ScrollBarNode.ScrollPosition ?? 0;
        float detailsScroll = detailsList?.ScrollBarNode.ScrollPosition ?? 0;

        SetWindowSize(Size);
        LayoutAttachedNodes();
        RefreshSelectedDetails();

        if (regionList is not null) RestoreScroll(regionList, regionScroll);
        if (areaList is not null) RestoreScroll(areaList, areaScroll);
        if (fishList is not null) RestoreScroll(fishList, fishScroll);
        if (detailsList is not null) RestoreScroll(detailsList, detailsScroll);
    }

    // Settings such as the 12-hour time format or the availability countdown only
    // affect an already-open fish's details when re-rendered here; otherwise the
    // open panel keeps showing whatever text was built when the fish was clicked.
    private void RefreshSelectedDetails()
    {
        if (selectedFish == 0) return;
        JournalFish? fish = GuideMode
            ? guideFish?.FirstOrDefault(f => f.FishParameterId == selectedFish)
            : selectedSpot?.Fish.FirstOrDefault(f => f.FishParameterId == selectedFish);
        if (fish is null) return;

        selectedDetails = buildDetails(fish);
        if (guideDetails is not null)
        {
            string? locationLabel = guideLocation?.Label;
            string? poleLabel = guidePole?.Label;
            selectedGuide = guideDetails(fish);
            guideLocation = locationLabel is null ? selectedGuide.Locations.FirstOrDefault()
                : selectedGuide.Locations.FirstOrDefault(l => l.Label == locationLabel) ?? selectedGuide.Locations.FirstOrDefault();
            guidePoles = guideLocation is null ? Array.Empty<FishingPole>() : selectedGuide.GetPoles(guideLocation);
            guidePole = poleLabel is null ? guidePoles.FirstOrDefault()
                : guidePoles.FirstOrDefault(p => p.Label == poleLabel) ?? guidePoles.FirstOrDefault();
        }
        RenderDetails();
    }

    private void LayoutAttachedNodes()
    {
        if (regionList is null || areaList is null || fishList is null)
            return;

        if (selectedSpot is not null && renderedUncaughtFirst != configuration.UncaughtFishFirst)
        {
            var details = selectedDetails;
            var guide = selectedGuide;
            var location = guideLocation;
            var pole = guidePole;
            var poles = guidePoles;
            uint fish = selectedFish;
            SelectSpot(selectedSpot);
            selectedDetails = details;
            selectedGuide = guide;
            guideLocation = location;
            guidePole = pole;
            guidePoles = poles;
            selectedFish = fish;
            UpdateFishSelection();
        }

        contentOrigin = ContentStartPosition;
        float regionWidth = Math.Clamp(regionWidthSetting, 130.0f, 280.0f);
        float maximumAreaWidth = Math.Max(240.0f, ContentSize.X - regionWidth - 380.0f);
        float areaWidth = Math.Clamp(areaWidthSetting, 240.0f, Math.Min(500.0f, maximumAreaWidth));
        float fishX = GuideMode ? 0 : regionWidth + areaWidth + ColumnGap * 2.0f;
        float listHeight = Math.Max(100.0f, ContentSize.Y - HeaderHeight);
        float regionListHeight = Math.Max(100.0f, listHeight - (showNormalLogButton || openGuide is not null ? 48.0f : 0.0f));
        float fishWidth = Math.Max(100.0f, ContentSize.X - fishX);

        SetHeaderLayout(regionHeader, 0.0f, regionWidth);
        SetHeaderLayout(areaHeader, regionWidth + ColumnGap, areaWidth);
        SetHeaderLayout(fishHeader, fishX, fishWidth);

        regionList.Position = contentOrigin + new Vector2(0.0f, HeaderHeight);
        regionList.Size = new Vector2(regionWidth, regionListHeight);
        areaList.Position = contentOrigin + new Vector2(regionWidth + ColumnGap, HeaderHeight);
        areaList.Size = new Vector2(areaWidth, listHeight);
        if (spotTitle is not null)
        {
            spotTitle.Position = contentOrigin + new Vector2(fishX, HeaderHeight);
            spotTitle.Width = fishWidth - 16;
        }
        if (spotSummary is not null)
        {
            spotSummary.Position = contentOrigin + new Vector2(fishX, HeaderHeight + 26);
            spotSummary.Width = fishWidth - 16;
        }
        if (summaryDivider is not null)
        {
            summaryDivider.Position = contentOrigin + new Vector2(fishX, HeaderHeight + 52);
            summaryDivider.Width = fishWidth - 10;
        }
        regionList.IsVisible = areaList.IsVisible = !GuideMode;
        if (regionHeader is not null) regionHeader.IsVisible = !GuideMode;
        if (areaHeader is not null) areaHeader.IsVisible = !GuideMode;
        if (fishHeader is not null) fishHeader.IsVisible = !GuideMode;
        if (spotTitle is not null) spotTitle.IsVisible = !GuideMode;
        if (spotSummary is not null) spotSummary.IsVisible = !GuideMode;
        if (summaryDivider is not null) summaryDivider.IsVisible = !GuideMode;
        if (searchInput is not null) {
            searchInput.Position = contentOrigin;
            searchInput.Width = Math.Max(100, fishWidth - 90);
            if (searchButton is not null) searchButton.Position = contentOrigin + new Vector2(fishWidth - 80, 0);
        }
        float fishBodyHeight = listHeight - (GuideMode ? 8 : FishSummaryHeight);
        fishList.Position = contentOrigin + new Vector2(fishX, HeaderHeight + (GuideMode ? 8 : FishSummaryHeight));
        fishList.Size = new Vector2(fishWidth, fishBodyHeight);
        RefreshFishListLayout();
        if (detailsList is not null && detailsDivider is not null)
        {
            float ratio = float.IsFinite(configuration.DetailsHeightRatio) ? configuration.DetailsHeightRatio : 0.38f;
            float detailsHeight = Math.Clamp(fishBodyHeight * ratio, 100, Math.Max(100, fishBodyHeight - 112));
            float fishHeight = fishBodyHeight - detailsHeight - 12.0f;
            detailsList.IsVisible = true;
            detailsDivider.IsVisible = true;
            if (dividerHandle is not null)
            {
                SetDividerHandleInteractive(dividerHandle, !configuration.IsDividerLocked(0));
                dividerHandle.ShowClickableCursor = false;
                dividerHandle.Position = fishList.Position + new Vector2(0, fishHeight);
                dividerHandle.Size = new Vector2(fishWidth, 12);
            }
            fishList.Height = fishHeight;
            detailsDivider.Position = fishList.Position + new Vector2(0, fishHeight + 3.0f);
            detailsDivider.Width = fishWidth;
            Vector2 panelOrigin = fishList.Position + new Vector2(0, fishHeight + 12.0f);
            detailsList.Position = panelOrigin + new Vector2(8, 8);
            detailsList.Size = new Vector2(fishWidth - 16, detailsHeight - 16);
            ReflowDetails();
        }

        SetDividerLayout(regionDivider, regionWidth + ColumnGap / 2.0f, false);
        SetDividerLayout(fishDivider, regionWidth + areaWidth + ColumnGap * 1.5f, true);
        if (regionDividerHandle is not null)
        {
            regionDividerHandle.Position = contentOrigin + new Vector2(regionWidth, 0);
            regionDividerHandle.Size = new Vector2(ColumnGap, ContentSize.Y);
            SetDividerHandleInteractive(regionDividerHandle, !configuration.IsDividerLocked(1));
            regionDividerHandle.ShowClickableCursor = false;
        }
        if (areaDividerHandle is not null)
        {
            areaDividerHandle.Position = contentOrigin + new Vector2(regionWidth + areaWidth + ColumnGap, 0);
            areaDividerHandle.Size = new Vector2(ColumnGap, ContentSize.Y);
            SetDividerHandleInteractive(areaDividerHandle, !configuration.IsDividerLocked(2));
            areaDividerHandle.ShowClickableCursor = false;
        }
        if (normalLogButton is not null)
        {
            normalLogButton.Position = FooterPosition;
            normalLogButton.IsVisible = showNormalLogButton;
        }
        if (guideButton is not null) guideButton.Position = FooterPosition + new Vector2(34, 0);
        if (GuideMode) {
            if (regionDivider is not null) regionDivider.IsVisible = false;
            if (fishDivider is not null) fishDivider.IsVisible = false;
            if (regionDividerHandle is not null) regionDividerHandle.IsVisible = false;
            if (areaDividerHandle is not null) areaDividerHandle.IsVisible = false;
        }
        ApplyAreaDropdownWidths();
        regionList.RecalculateSizes();
        RefreshFishListLayout();
        // native ellipsis overwrites the text buffer, so restore from the model, not the label
        foreach (var pair in regionButtons)
            pair.Key.String = pair.Value.IsUnlocked ? pair.Value.Name : "???";
        if (configuration.IsDividerLocked(dragKind)) ReleaseResizeCursor();
    }

    private const float MinDropdownWidth = 80.0f;
    private const float MaxDropdownInset = 150.0f;

    // The sliders are relative to a preferred baseline (0 on the slider = that
    // baseline, not "fills the column"). Negative insets (from the baseline) let
    // the dropdown box extend past that edge of the area column instead of only
    // ever shrinking in from it.
    private float EffectiveLeftInset() => Math.Clamp(
        dropdownLeftInsetSetting + Configuration.DropdownLeftInsetBaseline, -MaxDropdownInset, MaxDropdownInset);
    private float EffectiveRightInset() => Math.Clamp(
        dropdownRightInsetSetting + Configuration.DropdownRightInsetBaseline, -MaxDropdownInset, MaxDropdownInset);

    private float EffectiveDropdownWidth()
    {
        float availableWidth = areaList is null ? areaWidthSetting : areaList.ContentNode.Width;
        float usable = availableWidth - 12.0f - EffectiveLeftInset() - EffectiveRightInset();
        return Math.Max(MinDropdownWidth, usable);
    }

    private void ApplyAreaDropdownWidths()
    {
        if (areaList is null)
            return;
        float width = EffectiveDropdownWidth();
        foreach (CollapsingHeaderNode header in areaList.ContentNode.GetNodes<CollapsingHeaderNode>())
            header.Width = width;
        areaList.ContentNode.RecalculateLayout();
        areaList.RecalculateSizes();
        // ScrollingNode.RecalculateSizes() (via its own OnSizeChanged) ends by
        // calling ContentNode.RecalculateLayout() again internally, which resets
        // every header back to X=0 (its default left alignment) - so the inset has
        // to be reapplied after RecalculateSizes(), not before it, or it's wiped
        // out before this method even returns and the dropdown stays unclickable
        // at the spot it's actually drawn.
        ReapplyDropdownLeftInset();
    }

    private void ReapplyDropdownLeftInset()
    {
        if (areaList is null) return;
        float leftInset = EffectiveLeftInset();
        foreach (CollapsingHeaderNode header in areaList.ContentNode.GetNodes<CollapsingHeaderNode>())
            header.X = leftInset;
    }

    private ScrollingNode<JournalListNode> CreateList(Vector2 position, Vector2 size)
        => new JournalScrollingNode()
        {
            Position = position,
            Size = size,
            AutoHideScrollBar = true,
            ScrollSpeed = 31,
            ContentNode =
            {
                FitWidth = true,
                FitContents = true,
                ItemSpacing = 2.0f,
            },
        };

    private CategoryTextNode AddHeader(string text, float x, float width)
    {
        var header = new CategoryTextNode
        {
            Position = contentOrigin + new Vector2(x, 0),
            Size = new Vector2(width, 26.0f),
            String = text,
        };
        header.AttachNode(this);
        return header;
    }

    private VerticalLineNode AddDivider(float x, bool emphasized)
    {
        var line = new VerticalLineNode
        {
            Position = contentOrigin + new Vector2(x - 1.0f, 0),
            Height = ContentSize.Y,
            Width = 2.0f,
        };
        line.AttachNode(this);
        return line;
    }

    private void SetHeaderLayout(CategoryTextNode? header, float x, float width)
    {
        if (header is null)
            return;
        header.Position = contentOrigin + new Vector2(x, 0.0f);
        header.Size = new Vector2(width, 26.0f);
    }

    private void SetDividerLayout(VerticalLineNode? line, float x, bool emphasized)
    {
        if (line is null)
            return;
        line.Position = contentOrigin + new Vector2(x - 1.0f, 0.0f);
        line.Height = ContentSize.Y;
    }

}
