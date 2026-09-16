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
}

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
    string journalButtonPath,
    Action<Exception> reportSetupError,
    Action saveDivider,
    IAddonEventManager addonEvents) : NativeAddon
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
        summaryDivider = new HorizontalLineNode { Height = 7.0f };
        summaryDivider.AttachNode(this);

        float regionListHeight = ContentSize.Y - HeaderHeight - (showNormalLogButton ? 48.0f : 0.0f);
        regionList = CreateList(contentOrigin + new Vector2(0, HeaderHeight), new Vector2(regionWidth, regionListHeight));
        areaList = CreateList(contentOrigin + new Vector2(regionWidth + ColumnGap, HeaderHeight), new Vector2(areaWidth, ContentSize.Y - HeaderHeight));
        areaList.ContentNode.FitWidth = false;
        fishList = CreateList(contentOrigin + new Vector2(fishX, HeaderHeight), new Vector2(ContentSize.X - fishX, ContentSize.Y - HeaderHeight));
        detailsList = CreateList(contentOrigin, new Vector2(200, 150));
        detailsDivider = new HorizontalLineNode { Height = 7.0f };
        detailsList.AttachNode(this);
        detailsDivider.AttachNode(this);
        dividerHandle = new CollisionNode { ShowClickableCursor = false };
        dividerHandle.AddEvent(AtkEventType.MouseDown, () =>
        {
            if (!configuration.EmbedFishDetails || configuration.LockDetailsDivider) return;
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

        if (regions.Count > 0)
        {
            JournalRegion initialRegion = regions.FirstOrDefault(region => region.Name == sessionState.SelectedRegion) ?? regions[0];
            SelectRegion(initialRegion, true);
        }
        InitializeJournalButton();
        var restoredFish = selectedSpot?.Fish.FirstOrDefault(f => f.FishParameterId == sessionState.SelectedFish);
        if (restoredFish is not null)
        {
            selectedFish = restoredFish.FishParameterId;
            UpdateFishSelection();
            selectedDetails = buildDetails(restoredFish);
            RenderDetails();
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

    private void InitializeJournalButton()
    {
        try
        {
            normalLogButton = File.Exists(journalButtonPath)
                ? new TextureButtonNode
                {
                    TexturePath = journalButtonPath,
                    TextureCoordinates = new Vector2(16, 22),
                    TextureSize = new Vector2(26, 26),
                }
                : new TextButtonNode { String = "Log" };
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
        normalLogButton.Position = contentOrigin + new Vector2(0.0f, ContentSize.Y - 44.0f);
        normalLogButton.Size = new Vector2(28.0f, 28.0f);
        normalLogButton.IsVisible = showNormalLogButton;
        normalLogButton.IsEnabled = true;
        normalLogButton.TextTooltip = "Open the normal Fishing Log";
        normalLogButton.OnClick = openNormalLog;
        normalLogButton.AttachNode(this);
    }

    private void SelectRegion(JournalRegion region, bool restoring = false)
    {
        selectedRegion = region;
        foreach (var pair in regionButtons) pair.Key.Selected = ReferenceEquals(pair.Value, region);
        selectedSpot = null;
        if (spotTitle is not null) spotTitle.String = "Select a fishing hole.";
        if (spotSummary is not null) spotSummary.String = "";
        ClearDetails();
        if (!restoring && !string.Equals(sessionState.SelectedRegion, region.Name, StringComparison.OrdinalIgnoreCase))
            sessionState.SelectedSpot = 0;
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
                IsCollapsed = !sessionState.ExpandedAreas.Contains(areaKey),
            };
            areaDropDown.OnToggle = expanded =>
            {
                if (expanded)
                {
                    foreach (CollapsingHeaderNode other in areaHeaders)
                    {
                        if (!ReferenceEquals(other, areaDropDown))
                            other.IsCollapsed = true;
                    }
                    foreach (string key in sessionState.ExpandedAreas
                                 .Where(key => key.StartsWith($"{region.Name}\n", StringComparison.OrdinalIgnoreCase)).ToArray())
                        sessionState.ExpandedAreas.Remove(key);
                    sessionState.ExpandedAreas.Add(areaKey);
                }
                else
                {
                    sessionState.ExpandedAreas.Remove(areaKey);
                }
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
        if (restoredSpot is not null)
            SelectSpot(restoredSpot);
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
            uint fish = selectedFish;
            float detailsScroll = detailsList?.ScrollBarNode.ScrollPosition ?? 0;
            SelectSpot(selectedSpot);
            selectedDetails = details;
            selectedFish = fish;
            UpdateFishSelection();
            RenderDetails();
            if (detailsList is not null) RestoreScroll(detailsList, detailsScroll);
        }

        contentOrigin = ContentStartPosition;
        float regionWidth = Math.Clamp(regionWidthSetting, 130.0f, 280.0f);
        float maximumAreaWidth = Math.Max(240.0f, ContentSize.X - regionWidth - 380.0f);
        float areaWidth = Math.Clamp(areaWidthSetting, 240.0f, Math.Min(500.0f, maximumAreaWidth));
        float fishX = regionWidth + areaWidth + ColumnGap * 2.0f;
        float listHeight = Math.Max(100.0f, ContentSize.Y - HeaderHeight);
        float regionListHeight = Math.Max(100.0f, listHeight - (showNormalLogButton ? 48.0f : 0.0f));
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
        float fishBodyHeight = listHeight - FishSummaryHeight;
        fishList.Position = contentOrigin + new Vector2(fishX, HeaderHeight + FishSummaryHeight);
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
                dividerHandle.IsVisible = embedded && !configuration.LockDetailsDivider;
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
            regionDividerHandle.IsVisible = !configuration.LockRegionDivider;
            regionDividerHandle.ShowClickableCursor = false;
        }
        if (areaDividerHandle is not null)
        {
            areaDividerHandle.Position = contentOrigin + new Vector2(regionWidth + areaWidth + ColumnGap, 0);
            areaDividerHandle.Size = new Vector2(ColumnGap, ContentSize.Y);
            areaDividerHandle.IsVisible = !configuration.LockAreaDivider;
            areaDividerHandle.ShowClickableCursor = false;
        }
        if (normalLogButton is not null)
        {
            normalLogButton.Position = contentOrigin + new Vector2(0.0f, ContentSize.Y - 44.0f);
            normalLogButton.IsVisible = showNormalLogButton;
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
        if (fishList is null)
            return;

        fishButtons.Clear();
        fishList.ContentNode.Clear();
        if (spotTitle is not null) spotTitle.String = spot.Name;
        if (spotSummary is not null)
            spotSummary.String = $"Caught {spot.CaughtCount}/{spot.Fish.Count}   •   {spot.MissingCount} remaining";
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
            fishList.ContentNode.AddNode(new HorizontalLineNode { Height = 7.0f });
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
            string label = revealNames ? entry.Name : $"???? #{i + 1}";
            string rowLabel = entry.Level > 0 ? $"{label}   Lv. {entry.Level}" : label;
            var row = new FishEntryRowNode(entry, rowLabel, () =>
            {
                selectedFish = entry.FishParameterId;
                UpdateFishSelection();
                if (!configuration.EmbedFishDetails) { openFish(entry); return; }
                selectedDetails = buildDetails(entry);
                RenderDetails();
                detailsList?.ScrollToStart();
            });
            fishButtons.Add(row, entry.FishParameterId);
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
        selectedDetails = null;
        selectedFish = 0;
        detailsList?.ContentNode.Clear();
        detailsList?.RecalculateSizes();
        UpdateDetailsHint();
    }

    private void RenderDetails()
    {
        if (detailsList is null) return;
        detailsList.ContentNode.Clear();
        if (selectedDetails is { } section)
        {
            AddDetailLine(section.Heading);
            foreach (string line in section.Lines) AddDetailLine(line);
        }
        detailsList.RecalculateSizes();
        UpdateDetailsHint();
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
        var label = new LabelTextNode
        {
            Width = Math.Max(80.0f, detailsList.Width - 24.0f),
            FontSize = 14,
            TextFlags = TextFlags.WordWrap | TextFlags.MultiLine,
            String = text,
        };
        label.Height = Math.Max(24.0f, label.GetTextDrawSize(false).Y + 6.0f);
        detailsList.ContentNode.AddNode(label);
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
            Position = contentOrigin + new Vector2(x - (emphasized ? 3.0f : 1.0f), 0),
            Height = ContentSize.Y,
            Width = emphasized ? 7.0f : 3.0f,
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
        line.Position = contentOrigin + new Vector2(x - (emphasized ? 3.0f : 1.0f), 0.0f);
        line.Height = ContentSize.Y;
    }

    protected override unsafe void OnDraw(AtkUnitBase* addon)
    {
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
            && stage->AtkCollisionManager != null && stage->AtkCollisionManager->IntersectingAddon == addon
            && (mouse.MouseButtonPressedFlags & MouseButtonFlags.LBUTTON) != 0)
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
                        row.OnClick?.Invoke();
                        break;
                    }
                }
            }
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
            if (animating) areaList.RecalculateSizes();
        }
        // The asynchronously loaded PNG contains the user's button reference.
        // Select only its button rectangle, not the surrounding screenshot.
        if (normalLogButton is TextureButtonNode button)
        {
            button.TextureCoordinates = new Vector2(16, 22);
            button.TextureSize = new Vector2(26, 26);
        }
    }

    protected override unsafe void OnFinalize(AtkUnitBase* addon)
    {
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
        regionHeader = null;
        areaHeader = null;
        fishHeader = null;
        spotTitle = null;
        spotSummary = null;
        summaryDivider = null;
        regionDivider = null;
        fishDivider = null;
        normalLogButton = null;
    }

    protected override unsafe void OnHide(AtkUnitBase* addon)
    {
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
    }
}
