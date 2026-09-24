using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

using FishingClues.Base;
using FishingClues.Game.Data;
using FishingClues.Game.Models;
using FishingClues.UI.Components;

namespace FishingClues.UI.Windows;

public sealed partial class NativeJournalWindow(
    IReadOnlyList<JournalRegion> regions,
    NativeJournalSessionState sessionState,
    Configuration configuration,
    NativeJournalOptions options) : NativeAddon
{
    private static IAddonEventManager addonEvents => Services.AddonEvents;

    private const float HeaderHeight = 28.0f;
    private const float FishSummaryHeight = 54.0f;
    private const float SearchDividerY = 33.0f;
    private const float SearchButtonWidth = 90.0f;
    private const float CollapsedDetailsHeight = 22.0f;
    private const float ColumnGap = 10.0f;
    private static readonly Vector4 RegionListTextColor = new(0.80f, 0.66f, 0.40f, 1.0f);
    private ScrollingNode<JournalListNode>? regionList;
    private ScrollingNode<JournalListNode>? areaList;
    private ScrollingNode<JournalListNode>? fishList;
    private ScrollingNode<JournalListNode>? detailsList;
    private HorizontalLineNode? detailsDivider;
    private HorizontalLineNode? detailsBottomDivider;
    private float previousFishHeight = float.NaN, previousDetailsHeight = float.NaN;
    private bool detailsLaidOutOpen;
    private bool layingOut;
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
    private readonly Dictionary<JournalArea, AnimatedAreaHeaderNode> areaHeaderByArea = new();
    private LabelTextNode? detailsHint;
    private CategoryTextNode? regionHeader;
    private CategoryTextNode? areaHeader;
    private CategoryTextNode? fishHeader;
    private CategoryTextNode? spotTitle;
    private LabelTextNode? spotSummary;
    private HorizontalLineNode? summaryDivider;
    private HorizontalLineNode? areaHeaderDivider;
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
    private bool guideDetailsPending;
    private bool GuideMode => options.GuideFish is not null;
    private JournalRegion? selectedRegion;
    private JournalSpot? selectedSpot;
    private Vector2 contentOrigin;
    private float regionWidthSetting = options.RegionWidth;
    private float areaWidthSetting = options.AreaWidth;
    private float dropdownLeftInsetSetting = options.AreaDropdownLeftInset;
    private float dropdownRightInsetSetting = options.AreaDropdownRightInset;
    private bool showNormalLogButton = options.ShowNormalLogButton;

    protected override unsafe void OnSetup(AtkUnitBase* addon, Span<AtkValue> values)
    {
        contentOrigin = ContentStartPosition;
        var (regionWidth, areaWidth) = ColumnWidths();
        float fishX = regionWidth + areaWidth + ColumnGap * 2;

        CreateSettingsButton();
        CreateHeaders(regionWidth, areaWidth, fishX);
        CreateLocateButton();
        spotTitle = new CategoryTextNode { Height = 24.0f, String = "Select a fishing hole." };
        spotSummary = new LabelTextNode { Height = 24.0f, FontSize = 14, String = "" };
        spotTitle.AttachNode(this);
        spotSummary.AttachNode(this);
        summaryDivider = new HorizontalLineNode { Height = 2.0f };

        CreateLists(regionWidth, areaWidth, fishX);
        CreateDividerHandles();
        detailsHint = new LabelTextNode
        {
            FontSize = 14,
            AlignmentType = AlignmentType.Center,
            String = "Click a fish to see catch details",
        };
        detailsHint.AttachNode(this);
        regionDivider = AddDivider(regionWidth + ColumnGap / 2);
        fishDivider = AddDivider(regionWidth + areaWidth + ColumnGap * 1.5f);
        PopulateRegionList();
        regionList!.AttachNode(this);
        areaList!.AttachNode(this);
        fishList!.AttachNode(this);
        // horizontal dividers last so nothing draws over them
        summaryDivider.AttachNode(this);
        areaHeaderDivider!.AttachNode(this);
        detailsDivider!.AttachNode(this);
        detailsBottomDivider!.AttachNode(this);
        ApplyAreaDropdownWidths();
        if (!GuideMode) BuildMapPanel();

        LayoutAttachedNodes();

        if (!GuideMode && regions.Count > 0) OpenInitialRegion();
        if (GuideMode) CreateGuideSearch();
        else
        {
            InitializeJournalButton();
            RestoreSelectedFish();
        }
        RestoreScroll(regionList, sessionState.RegionScroll);
        RestoreScroll(areaList, sessionState.AreaScroll);
        RestoreScroll(fishList, sessionState.FishScroll);
        RestoreScroll(detailsList, sessionState.DetailsScroll);
    }

    // cog left of the close button, opens the plugin settings
    private void CreateSettingsButton()
    {
        if (options.OpenSettings is null) return;
        settingsButton = new TextureButtonNode
        {
            Size = new Vector2(SettingsButtonSize, SettingsButtonSize),
            TexturePath = "ui/uld/CircleButtons.tex",
            TextureCoordinates = new Vector2(0.0f, 0.0f),
            TextureSize = new Vector2(28.0f, 28.0f),
            TextTooltip = "Open the Fishing Clues settings",
            OnClick = options.OpenSettings,
        };
        settingsButton.AttachNode(this);
        PositionSettingsButton();
    }

    // Jupiter (serif small caps) draws mixed case as small capitals
    private void CreateHeaders(float regionWidth, float areaWidth, float fishX)
    {
        regionHeader = AddHeader("Region", 0, regionWidth, FontType.Jupiter, 16);
        areaHeader = AddHeader("Area", regionWidth + ColumnGap, areaWidth, FontType.Jupiter, 16);
        fishHeader = AddHeader("Fish", fishX, ContentSize.X - fishX, FontType.Jupiter, 16);
        areaHeaderDivider = new HorizontalLineNode { Height = 2.0f };
    }

    private void CreateLists(float regionWidth, float areaWidth, float fishX)
    {
        float regionListHeight = ContentSize.Y - HeaderHeight - (showNormalLogButton || options.OpenGuide is not null ? 48.0f : 0.0f);
        regionList = CreateList(contentOrigin + new Vector2(0, HeaderHeight), new Vector2(regionWidth, regionListHeight));
        areaList = CreateList(contentOrigin + new Vector2(regionWidth + ColumnGap, HeaderHeight),
            new Vector2(areaWidth, Math.Max(100.0f, ContentSize.Y - HeaderHeight - ReservedMapHeight())));
        areaList.ContentNode.FitWidth = false;
        fishList = CreateList(contentOrigin + new Vector2(fishX, HeaderHeight), new Vector2(ContentSize.X - fishX, ContentSize.Y - HeaderHeight));
        detailsList = CreateList(contentOrigin, new Vector2(200, 150));
        detailsList.ContentNode.ItemSpacing = 0.0f;
        detailsList.ContentNode.FirstItemSpacing = 8.0f;
        detailsList.AttachNode(this);
    }

    private void CreateDividerHandles()
    {
        detailsDivider = new HorizontalLineNode { Height = 2.0f };
        detailsBottomDivider = new HorizontalLineNode { Height = 2.0f };
        dividerHandle = CreateColumnHandle(0);
        regionDividerHandle = CreateColumnHandle(1);
        areaDividerHandle = CreateColumnHandle(2);
    }

    private void PopulateRegionList()
    {
        foreach (JournalRegion region in regions)
        {
            var regionButton = new ListButtonNode
            {
                Height = 25.0f,
                String = region.IsUnlocked ? region.Name : "???",
                OnClick = () => SelectRegion(region),
            };
            regionButton.LabelNode.TextColor = RegionListTextColor;
            regionButtons.Add(regionButton, region);
            regionList!.ContentNode.AddNode(regionButton);
        }
        regionList!.RecalculateSizes();
    }

    // newly discovered hole, else the player's location on first open, else where it was left
    private void OpenInitialRegion()
    {
        JournalRegion? forceRegion = null;
        JournalArea? forceArea = null;
        JournalSpot? forceSpot = null;
        // only for a newly discovered hole: zoom to it like a click would
        bool zoomToForcedSpot = false;

        if (options.PendingDiscoveredSpotId is uint discoveredId)
        {
            foreach (JournalRegion candidateRegion in regions)
            {
                JournalArea? candidateArea = candidateRegion.Areas.FirstOrDefault(a => a.Spots.Any(s => s.Id == discoveredId && s.IsUnlocked));
                if (candidateArea is null) continue;
                forceRegion = candidateRegion;
                forceArea = candidateArea;
                forceSpot = candidateArea.Spots.First(s => s.Id == discoveredId);
                zoomToForcedSpot = true;
                break;
            }
        }

        if (!sessionState.HasOpenedOnce)
        {
            sessionState.HasOpenedOnce = true;
            if (forceRegion is null)
            {
                var (currentRegion, currentArea, currentSpot) = JournalBuilder.LocateCurrentLocation(regions);
                if (currentRegion is not null)
                {
                    forceRegion = currentRegion;
                    forceArea = currentArea;
                    forceSpot = currentSpot;
                }
            }
        }

        JournalRegion initialRegion = forceRegion
            ?? regions.FirstOrDefault(region => region.Name == sessionState.SelectedRegion)
            ?? regions[0];
        SelectRegion(initialRegion, true, forceArea, forceSpot, zoomToForcedSpot);
    }

    private void CreateGuideSearch()
    {
        searchText = sessionState.SearchText;
        searchInput = new TextInputNode
        {
            Size = new Vector2(Math.Max(200, ContentSize.X), 28),
            PlaceholderString = "Search all fish...",
            MaxCharacters = 100,
            String = searchText,
            OnInputComplete = text => SubmitSearchText(text.ToString()),
        };
        searchInput.AttachNode(this);
        searchButton = new TextButtonNode
        {
            String = "Search",
            Size = new Vector2(80, 28),
            OnClick = () => SubmitSearchText(searchInput.String.ToString()),
        };
        searchButton.AttachNode(this);
        if (!string.IsNullOrWhiteSpace(searchText)) searchPending = true;
        else FillFishList(ShowSearchPrompt);
        LayoutAttachedNodes();
    }

    private void SubmitSearchText(string text)
    {
        searchText = text;
        sessionState.SearchText = text;
        searchPending = true;
    }

    // reopens the fish selected last time (the guide does it after its search)
    private void RestoreSelectedFish()
    {
        var restoredFish = selectedSpot?.Fish.FirstOrDefault(f => f.FishParameterId == sessionState.SelectedFish);
        if (restoredFish is null) return;
        selectedFish = restoredFish.FishParameterId;
        UpdateFishSelection();
        selectedDetails = options.BuildDetails(restoredFish);
        if (options.BuildGuideDetails is not null)
        {
            selectedGuide = options.BuildGuideDetails(restoredFish);
            guideLocation = selectedGuide.Locations.FirstOrDefault();
        }
        RenderDetails();
    }

    private static void RestoreScroll(ScrollingNode<JournalListNode>? list, float position)
    {
        if (list is null) return;
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
            options.ReportSetupError(ex);
            normalLogButton?.Dispose();
            normalLogButton = new TextButtonNode { String = "Log" };
            AttachJournalButton();
        }
    }

    private void AttachJournalButton()
    {
        if (normalLogButton is null) return;
        normalLogButton.Position = FooterPosition;
        normalLogButton.Size = new Vector2(28.0f, 28.0f);
        normalLogButton.IsVisible = showNormalLogButton;
        normalLogButton.IsEnabled = true;
        normalLogButton.TextTooltip = "Open the normal Fishing Log";
        normalLogButton.OnClick = options.OpenNormalLog;
        normalLogButton.AttachNode(this);
        if (options.OpenGuide is not null && guideButton is null)
        {
            guideButton = new TextureButtonNode {
                Position = FooterPosition + new Vector2(34, 0), Size = new Vector2(28, 28),
                TexturePath = "ui/uld/FishingNoteBook.tex",
                TextureCoordinates = new Vector2(88, 0), TextureSize = new Vector2(28, 28),
                TextTooltip = "Search all fish", OnClick = options.OpenGuide,
            };
            guideButton.AttachNode(this);
        }
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

    private CategoryTextNode AddHeader(string text, float x, float width, FontType? font = null, uint? fontSize = null)
    {
        var header = new CategoryTextNode
        {
            Position = contentOrigin + new Vector2(x, 0),
            Size = new Vector2(width, 26.0f),
            String = text,
        };
        if (font is FontType headerFont) header.FontType = headerFont;
        if (fontSize is uint headerSize) header.FontSize = headerSize;
        header.AttachNode(this);
        return header;
    }

    private VerticalLineNode AddDivider(float x)
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
}
