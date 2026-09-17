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

namespace FishingClues;

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

public sealed class NativeJournalWindow(
    IReadOnlyList<JournalRegion> regions,
    NativeJournalSessionState sessionState,
    float configuredRegionWidth,
    float configuredAreaWidth,
    float configuredAreaDropdownWidth,
    bool configuredShowNormalLogButton,
    Action<JournalFish> openFish,
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
    public void SubmitSearch(string query, uint itemId = 0)
    {
        requestedSearchItem = itemId;
        searchText = query;
        sessionState.SearchText = query;
        if (searchInput is not null) searchInput.String = query;
        searchPending = true;
    }
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
    private float dropdownWidthSetting = configuredAreaDropdownWidth;
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
            if (!configuration.EmbedFishDetails || configuration.IsDividerLocked(0)) return;
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
        // In guide mode, RefreshSearch (fired below via searchPending) clears
        // details when it (re)builds the result list, so fish selection is
        // restored there instead, once the matching row actually exists.
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

    private void ShowSearchPrompt()
    {
        if (fishList is null) return;
        var favorites = guideFish?.Where(f => configuration.FavoriteFishItemIds.Contains(f.ItemId)).ToArray() ?? Array.Empty<JournalFish>();
        if (favorites.Length > 0) AddFishGroup("FAVORITES", favorites, true);
        else fishList.ContentNode.AddNode(new LabelTextNode {
            Height = 48, FontSize = 14, Width = Math.Max(80, fishList.Width - 24),
            TextFlags = TextFlags.WordWrap | TextFlags.MultiLine,
            String = "No favorites yet. Search for a fish and click its star to save it."
        });
        fishList.RecalculateSizes();
    }

    private void RefreshSearch()
    {
        if (fishList is null || guideFish is null) return;
        ClearDetails();
        fishButtons.Clear();
        availabilityRows.Clear();
        fishList.ContentNode.Clear();
        if (string.IsNullOrWhiteSpace(searchText)) {
            ShowSearchPrompt(); fishList.ScrollToStart(); return;
        }
        var matches = guideFish.Where(f => f.Name.Contains(searchText.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        AddFishGroup($"FISH - {matches.Length} matches", matches, true);
        fishList.RecalculateSizes();
        fishList.ScrollToStart();
        if (requestedSearchItem != 0) {
            var match = matches.FirstOrDefault(f => f.ItemId == requestedSearchItem);
            requestedSearchItem = 0;
            if (match is not null) {
                var row = fishButtons.FirstOrDefault(p => p.Value == match.FishParameterId).Key;
                row?.OnClick?.Invoke();
                if (row is not null) fishList.ScrollBarNode.ScrollPosition = Math.Max(0, row.Y);
            }
        }
        else if (sessionState.SelectedFish != 0) {
            // Restore whichever fish was selected the last time this search was open.
            var row = fishButtons.FirstOrDefault(p => p.Value == sessionState.SelectedFish).Key;
            row?.OnClick?.Invoke();
            if (row is not null) fishList.ScrollBarNode.ScrollPosition = Math.Max(0, row.Y);
        }
    }

    // Anchor to the outer frame, not the padded content area's old footer.
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

    private void SelectRegion(JournalRegion region, bool restoring = false)
    {
        if (!restoring && selectedRegion is not null) SaveViewState();
        sessionState.Regions.TryGetValue(region.Name, out var remembered);
        if (remembered is not null) {
            sessionState.SelectedSpot = remembered.Spot;
            sessionState.SelectedFish = remembered.Fish;
        } else if (!restoring) {
            sessionState.SelectedSpot = 0;
            sessionState.SelectedFish = 0;
        }
        selectedRegion = region;
        foreach (var pair in regionButtons) pair.Key.Selected = ReferenceEquals(pair.Value, region);
        selectedSpot = null;
        if (spotTitle is not null) spotTitle.String = "Select a fishing hole.";
        if (spotSummary is not null) spotSummary.String = "";
        ClearDetails();
        sessionState.SelectedRegion = region.Name;
        if (areaList is null || fishList is null)
            return;

        spotButtons.Clear();
        areaList.ContentNode.Clear();
        var areaHeaders = new List<CollapsingHeaderNode>();
        for (int areaIndex = 0; areaIndex < region.Areas.Count; areaIndex++)
        {
            JournalArea area = region.Areas[areaIndex];
            string areaKey = $"{region.Name}\n{area.Name}";
            var areaDropDown = new AnimatedAreaHeaderNode
            {
                String = region.IsUnlocked ? area.Name : "???",
                Width = EffectiveDropdownWidth(),
                FitWidth = true,
                ItemSpacing = 2.0f,
                FirstItemSpacing = 1.0f,
                IsCollapsed = !sessionState.ExpandedAreas.Contains(areaKey) && !area.Spots.Any(s => s.Id == sessionState.SelectedSpot),
            };
            areaDropDown.OnToggle = expanded =>
            {
                if (!expanded && selectedSpot is not null && area.Spots.Any(s => s.Id == selectedSpot.Id)) {
                    areaDropDown.RestoreExpandedOnNextTick = true;
                    return;
                }
                if (expanded)
                {
                    sessionState.ExpandedAreas.Add(areaKey);
                }
                else
                {
                    sessionState.ExpandedAreas.Remove(areaKey);
                }
                foreach (var header in areaHeaders.OfType<AnimatedAreaHeaderNode>()) header.RecalculateLayout();
                areaList?.RecalculateSizes();
            };
            areaHeaders.Add(areaDropDown);
            foreach (JournalSpot spot in area.Spots)
            {
                JournalSpot captured = spot;
                if (spot.IsUnlocked)
                {
                    var spotButton = new ListButtonNode
                    {
                        Height = 25.0f,
                        String = $"  {spot.Name}",
                        OnClick = () => SelectSpot(captured),
                    };
                    spotButtons.Add(spotButton, spot.Id);
                    areaDropDown.AddNode(spotButton);
                }
                else
                {
                    areaDropDown.AddNode(new LabelTextNode
                    {
                        Height = 25.0f,
                        FontSize = 14,
                        String = "  Undiscovered",
                    });
                }
            }
            areaDropDown.InitializeAnimation();
            areaList.ContentNode.AddNode(areaDropDown);
        }
        areaList.RecalculateSizes();
        fishButtons.Clear();
        fishList.ContentNode.Clear();
        fishList.RecalculateSizes();

        JournalSpot? restoredSpot = region.Areas.SelectMany(a => a.Spots)
            .FirstOrDefault(spot => spot.IsUnlocked && spot.Id == sessionState.SelectedSpot);
        if (restoredSpot is not null) {
            SelectSpot(restoredSpot);
            var restoredRow = fishButtons.FirstOrDefault(p => p.Value == sessionState.SelectedFish).Key;
            restoredRow?.OnClick?.Invoke();
        }
        if (remembered is not null) {
            RestoreScroll(areaList, remembered.AreaScroll);
            RestoreScroll(fishList, remembered.FishScroll);
            if (detailsList is not null) RestoreScroll(detailsList, remembered.DetailsScroll);
        }
    }

    public void ApplyLayout(float windowWidth, float windowHeight, float regionWidth, float areaWidth, float dropdownWidth,
        bool configuredShowButton)
    {
        regionWidthSetting = regionWidth;
        areaWidthSetting = areaWidth;
        dropdownWidthSetting = dropdownWidth;
        showNormalLogButton = configuredShowButton;
        Size = new Vector2(windowWidth, windowHeight);
        if (!IsOpen)
            return;

        SetWindowSize(Size);
        LayoutAttachedNodes();
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
            float detailsScroll = detailsList?.ScrollBarNode.ScrollPosition ?? 0;
            SelectSpot(selectedSpot);
            selectedDetails = details;
            selectedGuide = guide;
            guideLocation = location;
            guidePole = pole;
            guidePoles = poles;
            selectedFish = fish;
            UpdateFishSelection();
            RenderDetails();
            if (detailsList is not null) RestoreScroll(detailsList, detailsScroll);
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
        if (detailsList is not null && detailsDivider is not null)
        {
            bool embedded = configuration.EmbedFishDetails;
            float ratio = float.IsFinite(configuration.DetailsHeightRatio) ? configuration.DetailsHeightRatio : 0.38f;
            float detailsHeight = Math.Clamp(fishBodyHeight * ratio, 100, Math.Max(100, fishBodyHeight - 112));
            float fishHeight = fishBodyHeight - detailsHeight - 12.0f;
            detailsList.IsVisible = embedded;
            detailsDivider.IsVisible = embedded;
            if (dividerHandle is not null)
            {
                dividerHandle.IsVisible = embedded && !configuration.IsDividerLocked(0);
                dividerHandle.ShowClickableCursor = false;
                dividerHandle.Position = fishList.Position + new Vector2(0, fishHeight);
                dividerHandle.Size = new Vector2(fishWidth, 12);
            }
            if (embedded)
            {
                fishList.Height = fishHeight;
                detailsDivider.Position = fishList.Position + new Vector2(0, fishHeight + 3.0f);
                detailsDivider.Width = fishWidth;
                Vector2 panelOrigin = fishList.Position + new Vector2(0, fishHeight + 12.0f);
                detailsList.Position = panelOrigin + new Vector2(8, 8);
                detailsList.Size = new Vector2(fishWidth - 16, detailsHeight - 16);
                ReflowDetails();
            }
            else ClearDetails();
        }

        SetDividerLayout(regionDivider, regionWidth + ColumnGap / 2.0f, false);
        SetDividerLayout(fishDivider, regionWidth + areaWidth + ColumnGap * 1.5f, true);
        if (regionDividerHandle is not null)
        {
            regionDividerHandle.Position = contentOrigin + new Vector2(regionWidth, 0);
            regionDividerHandle.Size = new Vector2(ColumnGap, ContentSize.Y);
            regionDividerHandle.IsVisible = !configuration.IsDividerLocked(1);
            regionDividerHandle.ShowClickableCursor = false;
        }
        if (areaDividerHandle is not null)
        {
            areaDividerHandle.Position = contentOrigin + new Vector2(regionWidth + areaWidth + ColumnGap, 0);
            areaDividerHandle.Size = new Vector2(ColumnGap, ContentSize.Y);
            areaDividerHandle.IsVisible = !configuration.IsDividerLocked(2);
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
        fishList.RecalculateSizes();
        // Native ellipsis can replace the text buffer. Always restore from the
        // journal model after width/layout changes, never from the shortened label.
        foreach (var pair in regionButtons)
            pair.Key.String = pair.Value.IsUnlocked ? pair.Value.Name : "???";
        if (configuration.IsDividerLocked(dragKind)) ReleaseResizeCursor();
    }

    private float EffectiveDropdownWidth()
    {
        float availableWidth = areaList is null ? areaWidthSetting : areaList.ContentNode.Width;
        return Math.Clamp(dropdownWidthSetting, 120.0f, Math.Max(120.0f, availableWidth - 12.0f));
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
    }

    private void SelectSpot(JournalSpot spot)
    {
        ClearDetails();
        selectedSpot = spot;
        foreach (var pair in spotButtons) pair.Key.Selected = pair.Value == spot.Id;
        sessionState.SelectedRegion = selectedRegion?.Name;
        sessionState.SelectedSpot = spot.Id;
        if (selectedRegion is not null) sessionState.ExpandedAreas.Add($"{selectedRegion.Name}\n{spot.Area}");
        if (fishList is null)
            return;

        fishButtons.Clear();
        availabilityRows.Clear();
        fishList.ContentNode.Clear();
        if (spotTitle is not null) spotTitle.String = spot.Name;
        if (spotSummary is not null)
            spotSummary.String = $"Caught {spot.CaughtCount}/{spot.Fish.Count}    |    {spot.MissingCount} remaining";
        IReadOnlyList<JournalFish> caught = spot.Fish.Where(f => f.IsCaught).ToArray();
        IReadOnlyList<JournalFish> missing = spot.Fish.Where(f => !f.IsCaught).ToArray();
        renderedUncaughtFirst = configuration.UncaughtFishFirst;
        if (renderedUncaughtFirst && missing.Count > 0)
            AddFishGroup("NOT CAUGHT", missing, revealNames: false);
        AddFishGroup("CAUGHT", caught, revealNames: true);
        if (!renderedUncaughtFirst && missing.Count > 0)
            AddFishGroup("NOT CAUGHT", missing, revealNames: false);
        fishList.RecalculateSizes();
        fishList.ScrollToStart();
    }

    private void AddFishGroup(string heading, IReadOnlyList<JournalFish> fish, bool revealNames)
    {
        if (fishList is null)
            return;
        if (fishList.ContentNode.Nodes.Count > 0)
            fishList.ContentNode.AddNode(new HorizontalLineNode { Height = 2.0f });
        fishList.ContentNode.AddNode(new CategoryTextNode { String = $"{heading}   {fish.Count}" });
        if (fish.Count == 0)
        {
            fishList.ContentNode.AddNode(new LabelTextNode
            {
                Height = 23.0f,
                FontSize = 14,
                String = "None",
            });
            return;
        }

        for (int i = 0; i < fish.Count; i++)
        {
            JournalFish entry = fish[i];
            string label = revealNames || entry.IdentityVisible ? entry.Name : $"???? #{i + 1}";
            string rowLabel = entry.Level > 0 ? $"{label}   Lv. {entry.Level}" : label;
            var row = new FishEntryRowNode(entry, rowLabel, () =>
            {
                selectedFish = entry.FishParameterId;
                UpdateFishSelection();
                if (!configuration.EmbedFishDetails) { openFish(entry); return; }
                selectedDetails = buildDetails(entry);
                if (guideDetails is not null) {
                    selectedGuide = guideDetails(entry);
                    guideLocation = selectedGuide.Locations.FirstOrDefault();
                    guidePoles = guideLocation is null ? Array.Empty<FishingPole>() : selectedGuide.GetPoles(guideLocation);
                    guidePole = guidePoles.FirstOrDefault();
                }
                RenderDetails();
                detailsList?.ScrollToStart();
            });
            fishButtons.Add(row, entry.FishParameterId);
            var availability = getAvailability?.Invoke(entry);
            if (availability is not null)
            {
                row.SetAvailability(availability.AvailableNow, availability.BadgeText, availability.Tooltip);
                availabilityRows.Add((row, entry));
            }
            if (GuideMode) row.AddFavoriteStar(configuration.FavoriteFishItemIds.Contains(entry.ItemId), () => {
                bool favorite = configuration.FavoriteFishItemIds.Add(entry.ItemId);
                if (!favorite) configuration.FavoriteFishItemIds.Remove(entry.ItemId);
                saveDivider();
                // Defer removals from the empty-query list until the click callback finishes.
                if (string.IsNullOrWhiteSpace(searchText)) searchPending = true;
                return favorite;
            });
            fishList.ContentNode.AddNode(row);
        }
    }

    private void UpdateFishSelection()
    {
        foreach (var pair in fishButtons) pair.Key.Selected = pair.Value == selectedFish;
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

    private void ClearDetails()
    {
        if (partialHover is not null && fishButtons.ContainsKey(partialHover)) partialHover.HideTooltip();
        partialHover = null;
        catchBody = null;
        poleSelector = null;
        writingCatchBody = false;
        selectedDetails = null;
        selectedGuide = null;
        guideLocation = null;
        guidePole = null;
        guideDetailsPending = guideLocationPending = false;
        selectedFish = 0;
        detailsList?.ContentNode.Clear();
        detailsList?.RecalculateSizes();
        UpdateDetailsHint();
    }

    private JournalListNode? catchBody;
    private DetailSelectorRow<FishingPole>? poleSelector;
    private bool writingCatchBody;

    private void RenderDetails()
    {
        if (detailsList is null) return;
        catchBody = null;
        poleSelector = null;
        detailsList.ContentNode.Clear();
        if (selectedGuide is not null) {
            RenderGuideDetails();
        }
        else if (selectedDetails is { } section)
        {
            AddDetailLine(section.Heading);
            foreach (string line in section.Lines) AddDetailLine(line);
        }
        detailsList.RecalculateSizes();
        UpdateDetailsHint();
    }

    private void RenderGuideDetails()
    {
        if (detailsList is null || selectedGuide is null) return;
        AddDetailLine(selectedGuide.Name);
        if (GuideMode) detailsList.ContentNode.AddNode(new DetailSelectorRow<GuideLocation>(
            "Locations:", selectedGuide.Locations, guideLocation, l => l.Label,
            l => { guideLocation = l; guideLocationPending = guideDetailsPending = true; }, "Unknown") { Width = Math.Max(80, detailsList.Width - 24) });
        poleSelector = new DetailSelectorRow<FishingPole>(
            "Fishing Pole:", guidePoles, guidePole, p => p.Label,
            p => { guidePole = p; guideDetailsPending = true; },
            guideLocation?.Spearfishing == true ? "Not used (spearfishing)" : "No eligible poles") { Width = Math.Max(80, detailsList.Width - 24) };
        detailsList.ContentNode.AddNode(poleSelector);
        catchBody = new JournalListNode { Width = Math.Max(80, detailsList.Width - 24), FitContents = true };
        detailsList.ContentNode.AddNode(catchBody);
        RenderCatchBody();
    }

    private void RenderCatchBody()
    {
        if (catchBody is null || selectedGuide is null) return;
        catchBody.Clear();
        writingCatchBody = true;
        if (guideLocation is not null)
            foreach (string line in selectedGuide.GetDetails(guideLocation, guidePole)) AddDetailLine(line);
        else AddDetailLine("Location requirements unknown.");
        if (selectedGuide.Info.Count > 0) {
            AddDetailLine("");
            AddDetailLine("Description:");
            foreach (string line in selectedGuide.Info) AddDetailLine(line);
        }
        writingCatchBody = false;
        catchBody.RecalculateLayout();
        detailsList?.RecalculateSizes();
    }

    private void ReflowDetails()
    {
        if (detailsList is null) return;
        float width = Math.Max(80.0f, detailsList.Width - 24.0f);
        foreach (var label in detailsList.ContentNode.GetNodes<LabelTextNode>())
        {
            if (Math.Abs(label.Width - width) < 1) continue;
            label.Width = width;
            label.Height = Math.Max(24.0f, label.GetTextDrawSize(false).Y + 6.0f);
        }
        foreach (var row in detailsList.ContentNode.GetNodes<ItemDetailRow>()) row.Width = width;
        foreach (var row in detailsList.ContentNode.GetNodes<DetailSelectorRow<GuideLocation>>()) row.Width = width;
        foreach (var row in detailsList.ContentNode.GetNodes<DetailSelectorRow<FishingPole>>()) row.Width = width;
        if (catchBody is not null && Math.Abs(catchBody.Width - width) >= 1) {
            catchBody.Width = width;
            foreach (var label in catchBody.GetNodes<LabelTextNode>()) {
                label.Width = width;
                label.Height = Math.Max(24, label.GetTextDrawSize(false).Y + 6);
            }
            foreach (var row in catchBody.GetNodes<ItemDetailRow>()) row.Width = width;
            catchBody.RecalculateLayout();
        }
        detailsList.RecalculateSizes();
        UpdateDetailsHint();
    }

    private void UpdateDetailsHint()
    {
        if (detailsHint is null || detailsList is null) return;
        detailsHint.Position = detailsList.Position;
        detailsHint.Size = detailsList.Size;
        detailsHint.IsVisible = configuration.EmbedFishDetails && selectedDetails is null;
    }

    private void AddDetailLine(string text)
    {
        if (detailsList is null) return;
        if (selectedGuide is not null && (text.Contains(":") && !selectedGuide.Info.Contains(text) || selectedGuide.ItemLinks.ContainsKey(text))) {
            (writingCatchBody ? catchBody! : detailsList.ContentNode).AddNode(new ItemDetailRow(text, selectedGuide.ItemLinks) { Width = Math.Max(80, detailsList.Width - 24) });
            return;
        }
        var label = new LabelTextNode
        {
            Width = Math.Max(80.0f, detailsList.Width - 24.0f),
            FontSize = 14,
            TextFlags = TextFlags.WordWrap | TextFlags.MultiLine,
            String = text,
        };
        label.Height = Math.Max(24.0f, label.GetTextDrawSize(false).Y + 6.0f);
        (writingCatchBody ? catchBody! : detailsList.ContentNode).AddNode(label);
    }

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

    private FishEntryRowNode? partialHover;
    protected override unsafe void OnDraw(AtkUnitBase* addon)
    {
        var previousPartialHover = partialHover;
        if (partialHover is not null && fishButtons.ContainsKey(partialHover)) partialHover.HoverBackgroundNode.Alpha = 0;
        partialHover = null;
        if (guideDetailsPending && selectedGuide is not null && guideLocation is not null) {
            guideDetailsPending = false;
            if (guideLocationPending) {
                guideLocationPending = false;
                guidePoles = selectedGuide.GetPoles(guideLocation);
                guidePole = guidePoles.FirstOrDefault();
                poleSelector?.SetOptions(guidePoles, guidePole);
            }
            // Preserve dropdown nodes: the native input system can retain their focus pointers.
            // Only rebuild the noninteractive catch text after selection.
            RenderCatchBody();
        }
        if (searchPending) {
            searchPending = false;
            RefreshSearch();
        }
        if (getAvailability is not null && availabilityRows.Count > 0 && Environment.TickCount64 >= nextAvailabilityRefresh)
        {
            nextAvailabilityRefresh = Environment.TickCount64 + 30000;
            foreach (var (row, fish) in availabilityRows)
            {
                var info = getAvailability(fish);
                if (info is not null) row.SetAvailability(info.AvailableNow, info.BadgeText, info.Tooltip);
            }
        }
        var framework = Framework.Instance();
        var mouse = framework == null ? default : framework->CursorInputs;
        var stage = AtkStage.Instance();
        int cursorKind = -1;
        if (mouse.IsGameWindowFocused)
        {
            if (draggingDivider && !configuration.IsDividerLocked(dragKind)) cursorKind = dragKind;
            else if (stage != null && stage->AtkCollisionManager != null
                && stage->AtkCollisionManager->IntersectingAddon == addon)
            {
                if (HitDivider(regionDividerHandle, mouse, addon->Scale)) cursorKind = 1;
                else if (HitDivider(areaDividerHandle, mouse, addon->Scale)) cursorKind = 2;
                else if (HitDivider(dividerHandle, mouse, addon->Scale)) cursorKind = 0;
            }
        }
        if (cursorKind >= 0)
        {
            addonEvents.SetCursor(cursorKind == 0 ? AddonCursorType.ResizeNS : AddonCursorType.ResizeWE);
            ownsResizeCursor = true;
        }
        else ReleaseResizeCursor();
        if (!draggingDivider && fishList is not null && stage != null
            && stage->AtkCollisionManager != null && stage->AtkCollisionManager->IntersectingAddon == addon)
        {
            // Native buttons reject clicks when only part of their bounds is
            // clipped. Permit the visible portion, never the hidden portion.
            float scale = Math.Max(0.1f, addon->Scale);
            Vector2 origin = fishList.ScreenPosition;
            float x = (mouse.PositionX - origin.X) / scale;
            float y = (mouse.PositionY - origin.Y) / scale;
            if (x >= 0 && x < fishList.ContentNode.Width && y >= 0 && y < fishList.Height)
            {
                float offset = fishList.ScrollBarNode.ScrollPosition;
                foreach (var row in fishButtons.Keys)
                {
                    float top = row.Y - offset;
                    float bottom = top + row.Height;
                    bool partial = top < 0 || bottom > fishList.Height;
                    if (partial && y >= Math.Max(0, top) && y < Math.Min(fishList.Height, bottom))
                    {
                        row.HoverBackgroundNode.Alpha = 1;
                        partialHover = row;
                        addonEvents.SetCursor(AddonCursorType.Clickable);
                        ownsResizeCursor = true;
                        if ((mouse.MouseButtonPressedFlags & MouseButtonFlags.LBUTTON) != 0) row.OnClick?.Invoke();
                        break;
                    }
                }
            }
        }
        if (!ReferenceEquals(previousPartialHover, partialHover)) {
            if (previousPartialHover is not null && fishButtons.ContainsKey(previousPartialHover)) previousPartialHover.HideTooltip();
            partialHover?.ShowTooltip();
        }
        // Use the game's mouse state, not ImGui's filtered input for a native window.
        // Scope hit testing to this addon so other windows cannot trigger a drag.
        if (!draggingDivider && stage != null
            && stage->AtkCollisionManager != null && stage->AtkCollisionManager->IntersectingAddon == addon
            && (mouse.MouseButtonPressedFlags & MouseButtonFlags.LBUTTON) != 0)
        {
            if (HitDivider(regionDividerHandle, mouse, addon->Scale)) BeginDividerDrag(1);
            else if (HitDivider(areaDividerHandle, mouse, addon->Scale)) BeginDividerDrag(2);
            else if (HitDivider(dividerHandle, mouse, addon->Scale)) BeginDividerDrag(0);
        }
        if (draggingDivider)
        {
            if (!mouse.IsGameWindowFocused || (mouse.MouseButtonHeldFlags & MouseButtonFlags.LBUTTON) == 0 || configuration.IsDividerLocked(dragKind) || (dragKind == 0 && !configuration.EmbedFishDetails))
            {
                draggingDivider = false;
                saveDivider();
            }
            else
            {
                if (dragKind == 0)
                {
                    float body = Math.Max(212, ContentSize.Y - HeaderHeight - FishSummaryHeight);
                    float delta = (mouse.PositionY - dragStartY) / Math.Max(0.1f, addon->Scale);
                    configuration.DetailsHeightRatio = Math.Clamp(dragStartRatio - delta / body, 100 / body, 1 - 112 / body);
                }
                else
                {
                    float delta = (mouse.PositionX - dragStartX) / Math.Max(0.1f, addon->Scale);
                    if (dragKind == 1)
                        configuration.NativeRegionWidth = regionWidthSetting = Math.Clamp(dragStartWidth + delta, 130, 280);
                    else
                    {
                        float maximum = Math.Min(500, Math.Max(240, ContentSize.X - regionWidthSetting - 380));
                        configuration.NativeAreaWidth = areaWidthSetting = Math.Clamp(dragStartWidth + delta, 240, maximum);
                    }
                }
                float scroll = detailsList?.ScrollBarNode.ScrollPosition ?? 0;
                float fishScroll = fishList?.ScrollBarNode.ScrollPosition ?? 0;
                LayoutAttachedNodes();
                if (detailsList is not null) RestoreScroll(detailsList, scroll);
                if (fishList is not null) RestoreScroll(fishList, fishScroll);
            }
        }
        bool animating = false;
        if (areaList is not null)
        {
            foreach (var header in areaList.ContentNode.GetNodes<AnimatedAreaHeaderNode>())
                animating |= header.Tick();
            float previousHeight = areaList.ContentNode.Height;
            // Collapse callbacks can change a header height without an animation tick.
            // Always reconcile sibling positions before refreshing the scroll range.
            areaList.ContentNode.RecalculateLayout();
            if (animating || previousHeight != areaList.ContentNode.Height)
            {
                areaList.RecalculateSizes();

            }
        }
    }

    protected override unsafe void OnFinalize(AtkUnitBase* addon)
    {
        catchBody = null;
        poleSelector = null;
        ReleaseResizeCursor();
        SaveViewState();
        base.OnFinalize(addon);
        regionList = null;
        areaList = null;
        fishList = null;
        detailsList = null;
        detailsDivider = null;
        dividerHandle = null;
        regionDividerHandle = areaDividerHandle = null;
        draggingDivider = false;
        detailsHint = null;
        regionButtons.Clear();
        spotButtons.Clear();
        fishButtons.Clear();
        selectedDetails = null;
        selectedGuide = null;
        guideLocation = null;
        guidePole = null;
        guidePoles = Array.Empty<FishingPole>();
        guideDetailsPending = guideLocationPending = false;
        regionHeader = null;
        areaHeader = null;
        fishHeader = null;
        spotTitle = null;
        spotSummary = null;
        summaryDivider = null;
        regionDivider = null;
        fishDivider = null;
        normalLogButton = null;
        guideButton = null;
        searchInput = null;
        searchButton = null;
        searchPending = false;
    }

    protected override unsafe void OnHide(AtkUnitBase* addon)
    {
        if (partialHover is not null && fishButtons.ContainsKey(partialHover)) partialHover.HideTooltip();
        partialHover = null;
        ReleaseResizeCursor();
        if (draggingDivider) { draggingDivider = false; saveDivider(); }
        SaveViewState();
    }

    private void ReleaseResizeCursor()
    {
        if (!ownsResizeCursor) return;
        addonEvents.ResetCursor();
        ownsResizeCursor = false;
    }

    private void SaveViewState()
    {
        if (fishList is null) return;
        sessionState.SelectedFish = selectedFish;
        sessionState.RegionScroll = regionList?.ScrollBarNode.ScrollPosition ?? 0;
        sessionState.AreaScroll = areaList?.ScrollBarNode.ScrollPosition ?? 0;
        sessionState.FishScroll = fishList.ScrollBarNode.ScrollPosition;
        sessionState.DetailsScroll = detailsList?.ScrollBarNode.ScrollPosition ?? 0;
        if (!GuideMode && selectedRegion is not null)
            sessionState.Regions[selectedRegion.Name] = new RegionViewState(selectedSpot?.Id ?? 0, selectedFish,
                sessionState.AreaScroll, sessionState.FishScroll, sessionState.DetailsScroll);
    }
}
