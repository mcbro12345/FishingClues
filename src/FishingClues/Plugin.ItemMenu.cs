using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.NativeWrapper;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using FishParameterSheet = Lumina.Excel.Sheets.FishParameter;
using FishingSpotSheet = Lumina.Excel.Sheets.FishingSpot;
using MainCommandSheet = Lumina.Excel.Sheets.MainCommand;
using ItemSheet = Lumina.Excel.Sheets.Item;
using PlaceNameSheet = Lumina.Excel.Sheets.PlaceName;

namespace FishingClues;

public sealed partial class Plugin
{
    private static uint pendingItemMenu;
    private System.Runtime.InteropServices.GCHandle itemLinkPayload;
    private nint itemLinkData;
    private uint openingLinkedItem;
    internal static void RequestItemMenu(uint itemId) => pendingItemMenu = itemId;
    private unsafe void DrawItemMenu()
    {
        if (pendingItemMenu == 0) return;
        uint itemId = pendingItemMenu;
        pendingItemMenu = 0;
        var panelPtr = GameGui.GetAddonByName("ChatLogPanel_0");
        if (panelPtr.IsNull) { Log.Warning("Cannot open native item menu: chat panel is unavailable."); return; }
        var panel = (AddonChatLogPanel*)panelPtr.Address;
        if (panel->LogViewer.ChatText == null) return;
        ReleaseItemMenuData();
        var payload = new Dalamud.Game.Text.SeStringHandling.SeString(
            new Dalamud.Game.Text.SeStringHandling.Payloads.ItemPayload(itemId, false),
            new Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload(ItemName(itemId)),
            Dalamud.Game.Text.SeStringHandling.Payloads.RawPayload.LinkTerminator).Encode();
        itemLinkPayload = System.Runtime.InteropServices.GCHandle.Alloc(payload, System.Runtime.InteropServices.GCHandleType.Pinned);
        itemLinkData = System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeof(LinkData));
        var link = (LinkData*)itemLinkData;
        *link = new LinkData {
            LinkType = (byte)Lumina.Text.Payloads.LinkMacroPayloadType.Item,
            UIntValue1 = itemId,
            Payload = (byte*)itemLinkPayload.AddrOfPinnedObject(),
            PayloadEnd = checked((ushort)payload.Length),
        };
        // routes through the real chat handler so other plugins' context-menu hooks still fire
        openingLinkedItem = itemId;
        try { panel->LogViewer.HandleLinkClick(link); }
        finally { openingLinkedItem = 0; }
    }
    private void ReleaseItemMenuData()
    {
        if (itemLinkPayload.IsAllocated) itemLinkPayload.Free();
        if (itemLinkData != 0) System.Runtime.InteropServices.Marshal.FreeHGlobal(itemLinkData);
        itemLinkData = 0;
    }

    private unsafe void OnMenuOpened(IMenuOpenedArgs args)
    {
        uint contextItem = args.Target switch {
            MenuTargetInventory inventory => inventory.TargetItem?.BaseItemId ?? 0,
            MenuTargetDefault => (uint)GameGui.HoveredItem,
            _ => 0,
        };
        if (openingLinkedItem != 0) contextItem = openingLinkedItem;
        contextItem %= 500000;
        if (contextItem == 0 && args.Target is MenuTargetInventory) {
            var inventoryContext = AgentInventoryContext.Instance();
            if (inventoryContext != null && inventoryContext->TargetInventorySlot != null)
                contextItem = inventoryContext->TargetInventorySlot->GetBaseItemId();
        }
        if (contextItem != 0 && (data.Locations.Any(l => l.ItemId == contextItem) ||
            DataManager.GetExcelSheet<FishParameterSheet>().Any(f => f.Item.RowId == contextItem))) {
            string query = ItemName(contextItem);
            args.AddMenuItem(new MenuItem {
                Name = "Search Fishing Clues", PrefixChar = 'F', PrefixColor = 43,
                OnClicked = clicked => { _ = OpenGuideAsync(query, contextItem); },
            });
        }
        if (!string.Equals(args.AddonName, "FishingNote", StringComparison.OrdinalIgnoreCase))
            return;
        AgentFishingNote* agent = AgentFishingNote.Instance();
        if (agent == null || agent->Mode != 0 || agent->SelectedSpotIndex < 0 || agent->SelectedSpotIndex >= agent->Spots.Length)
            return;
        List<HiddenFish> missing = CaptureMissingFish(agent);
        args.AddMenuItem(new MenuItem
        {
            Name = missing.Count == 0 ? "Fishing Clues - all fish caught" : $"Fishing Clues - {missing.Count} uncaught",
            PrefixChar = 'F', PrefixColor = 43, IsEnabled = missing.Count > 0,
            OnClicked = clicked => OpenClues(missing),
        });
    }

    private unsafe List<HiddenFish> CaptureMissingFish(AgentFishingNote* agent)
    {
        var result = new List<HiddenFish>();
        int count = Math.Min(agent->FishSlotCount, (byte)agent->FishSlots.Length);
        var fishSheet = DataManager.GetExcelSheet<FishParameterSheet>();
        var itemSheet = DataManager.GetExcelSheet<ItemSheet>();
        AtkUnitBasePtr ptr = GameGui.GetAddonByName("FishingNote");
        AtkUnitBase* addon = ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
        for (int i = 0; i < count; i++)
        {
            AgentFishingNote.FishSlot slot = agent->FishSlots[i];
            if (slot.Id == 0 || slot.IsCaught || !fishSheet.TryGetRow(slot.Id, out FishParameterSheet fish))
                continue;
            uint itemId = fish.Item.RowId;
            string itemName = itemSheet.TryGetRow(itemId, out ItemSheet item) ? item.Name.ToString() : string.Empty;
            string? knownName = !string.IsNullOrWhiteSpace(itemName) && IsNameVisibleInFishingLog(addon, itemName) ? itemName : null;
            result.Add(new HiddenFish(slot.Id, itemId, fish.FishingSpot.IsValid ? fish.FishingSpot.Value.GatheringLevel : (byte)0, knownName));
        }
        return result;
    }

    private unsafe static bool IsNameVisibleInFishingLog(AtkUnitBase* addon, string itemName) => addon != null && FindVisibleText(&addon->UldManager, itemName, 0);
    private unsafe static bool FindVisibleText(AtkUldManager* manager, string expected, int depth)
    {
        if (manager == null || manager->NodeList == null || depth > 12) return false;
        for (int i = 0; i < manager->NodeListCount; i++)
        {
            AtkResNode* node = manager->NodeList[i];
            if (node == null || !node->IsVisible()) continue;
            if (node->Type == NodeType.Text && ((AtkTextNode*)node)->NodeText.ToString().Trim().Equals(expected, StringComparison.OrdinalIgnoreCase)) return true;
            if (node->Type == NodeType.Component)
            {
                AtkComponentBase* component = ((AtkComponentNode*)node)->Component;
                if (component != null && FindVisibleText(&component->UldManager, expected, depth + 1)) return true;
            }
        }
        return false;
    }
}
