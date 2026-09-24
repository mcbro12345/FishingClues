using System;
using System.Numerics;
using Dalamud.Game.Addon.Events;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace FishingClues.UI.Windows;

// draggable dividers
public sealed partial class NativeJournalWindow
{
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

    // a handle with a mouse-down event has collision that blocks clicks behind it, even when invisible, so turn it off with visibility
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

    private unsafe void UpdateResizeCursor(AtkUnitBase* addon, CursorInputData mouse, bool overAddon)
    {
        int cursorKind = -1;
        if (mouse.IsGameWindowFocused)
        {
            if (draggingDivider && !configuration.IsDividerLocked(dragKind)) cursorKind = dragKind;
            else if (overAddon)
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
    }

    private unsafe void BeginDividerDragIfHit(AtkUnitBase* addon, CursorInputData mouse)
    {
        if (HitDivider(regionDividerHandle, mouse, addon->Scale)) BeginDividerDrag(1);
        else if (HitDivider(areaDividerHandle, mouse, addon->Scale)) BeginDividerDrag(2);
        else if (HitDivider(dividerHandle, mouse, addon->Scale)) BeginDividerDrag(0);
    }

    private unsafe void UpdateDividerDrag(AtkUnitBase* addon, CursorInputData mouse)
    {
        if (!mouse.IsGameWindowFocused || (mouse.MouseButtonHeldFlags & MouseButtonFlags.LBUTTON) == 0 || configuration.IsDividerLocked(dragKind))
        {
            draggingDivider = false;
            options.SaveLayout();
            LayoutAttachedNodes(); // settle every panel on its exact final size
            return;
        }

        float scale = Math.Max(0.1f, addon->Scale);
        if (dragKind == 0)
        {
            float body = Math.Max(212, ContentSize.Y - HeaderHeight - FishSummaryHeight);
            float delta = (mouse.PositionY - dragStartY) / scale;
            configuration.DetailsHeightRatio = Math.Clamp(dragStartRatio - delta / body, 100 / body, 1 - 112 / body);
        }
        else
        {
            float delta = (mouse.PositionX - dragStartX) / scale;
            if (dragKind == 1)
                configuration.NativeRegionWidth = regionWidthSetting = Math.Clamp(dragStartWidth + delta, 130, 280);
            else
            {
                float maximum = Math.Min(500, Math.Max(240, ContentSize.X - regionWidthSetting - 380));
                configuration.NativeAreaWidth = areaWidthSetting = Math.Clamp(dragStartWidth + delta, 240, maximum);
            }
        }
        KeepingScroll(LayoutAttachedNodes, detailsList, fishList);
        // Have the game pick up the resized clip and collision areas now, not a frame later.
        addon->UpdateCollisionNodeList(false);
    }

    private void ReleaseResizeCursor()
    {
        if (!ownsResizeCursor) return;
        addonEvents.ResetCursor();
        ownsResizeCursor = false;
    }
}
