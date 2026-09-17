using Dalamud.Game.NativeWrapper;
using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Gui.ContextMenu;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FishParameterSheet = Lumina.Excel.Sheets.FishParameter;
using ItemSheet = Lumina.Excel.Sheets.Item;

using FishingClues.Base;
using FishingClues.Game.Data;
using FishingClues.Game.Models;

namespace FishingClues.Game.Logic;

// Adds "Search Fishing Clues" to item context menus, and the native item
// link click-through KamiToolKit's item rows use to open the game's own
// item context menu.
public sealed class ItemContextMenuService
{
    private static uint pendingItemMenu;

    private readonly FishDataService fishData;
    private readonly FishDetailsFormatter formatter;
    private readonly Func<string, uint, System.Threading.Tasks.Task> openGuide;
    private readonly Action<IReadOnlyList<HiddenFish>> openClues;
    private System.Runtime.InteropServices.GCHandle itemLinkPayload;
    private nint itemLinkData;
    private uint openingLinkedItem;

    public ItemContextMenuService(FishDataService fishData, FishDetailsFormatter formatter,
        Func<string, uint, System.Threading.Tasks.Task> openGuide, Action<IReadOnlyList<HiddenFish>> openClues)
    {
        this.fishData = fishData;
        this.formatter = formatter;
        this.openGuide = openGuide;
        this.openClues = openClues;
        Services.ContextMenu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose()
    {
        Services.ContextMenu.OnMenuOpened -= OnMenuOpened;
        ReleaseItemMenuData();
    }

    internal static void RequestItemMenu(uint itemId) => pendingItemMenu = itemId;

    public unsafe void DrawItemMenu()
    {
        if (pendingItemMenu == 0) return;
        uint itemId = pendingItemMenu;
        pendingItemMenu = 0;
        var panelPtr = Services.GameGui.GetAddonByName("ChatLogPanel_0");
        if (panelPtr.IsNull) { Services.Log.Warning("Cannot open native item menu: chat panel is unavailable."); return; }
        var panel = (AddonChatLogPanel*)panelPtr.Address;
        if (panel->LogViewer.ChatText == null) return;
        ReleaseItemMenuData();
        var payload = new Dalamud.Game.Text.SeStringHandling.SeString(
            new Dalamud.Game.Text.SeStringHandling.Payloads.ItemPayload(itemId, false),
            new Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload(formatter.ItemName(itemId)),
            Dalamud.Game.Text.SeStringHandling.Payloads.RawPayload.LinkTerminator).Encode();
        itemLinkPayload = System.Runtime.InteropServices.GCHandle.Alloc(payload, System.Runtime.InteropServices.GCHandleType.Pinned);
        itemLinkData = System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeof(LinkData));
        var link = (LinkData*)itemLinkData;
        *link = new LinkData
        {
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
        uint contextItem = args.Target switch
        {
            MenuTargetInventory inventory => inventory.TargetItem?.BaseItemId ?? 0,
            MenuTargetDefault => (uint)Services.GameGui.HoveredItem,
            _ => 0,
        };
        if (openingLinkedItem != 0) contextItem = openingLinkedItem;
        contextItem %= 500000;
        if (contextItem == 0 && args.Target is MenuTargetInventory)
        {
            var inventoryContext = AgentInventoryContext.Instance();
            if (inventoryContext != null && inventoryContext->TargetInventorySlot != null)
                contextItem = inventoryContext->TargetInventorySlot->GetBaseItemId();
        }
        if (contextItem != 0 && (fishData.Data.Locations.Any(l => l.ItemId == contextItem) ||
            Services.DataManager.GetExcelSheet<FishParameterSheet>().Any(f => f.Item.RowId == contextItem)))
        {
            string query = formatter.ItemName(contextItem);
            args.AddMenuItem(new MenuItem
            {
                Name = "Search Fishing Clues", PrefixChar = 'F', PrefixColor = 43,
                OnClicked = clicked => { _ = openGuide(query, contextItem); },
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
            OnClicked = clicked => openClues(missing),
        });
    }

    private unsafe List<HiddenFish> CaptureMissingFish(AgentFishingNote* agent)
    {
        var result = new List<HiddenFish>();
        int count = Math.Min(agent->FishSlotCount, (byte)agent->FishSlots.Length);
        var fishSheet = Services.DataManager.GetExcelSheet<FishParameterSheet>();
        var itemSheet = Services.DataManager.GetExcelSheet<ItemSheet>();
        AtkUnitBasePtr ptr = Services.GameGui.GetAddonByName("FishingNote");
        AtkUnitBase* addon = ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
        for (int i = 0; i < count; i++)
        {
            AgentFishingNote.FishSlot slot = agent->FishSlots[i];
            if (slot.Id == 0 || slot.IsCaught || !fishSheet.TryGetRow(slot.Id, out FishParameterSheet fish))
                continue;
            uint itemId = fish.Item.RowId;
            string itemName = itemSheet.TryGetRow(itemId, out ItemSheet item) ? item.Name.ToString() : string.Empty;
            string? knownName = !string.IsNullOrWhiteSpace(itemName) && FishingNoteAddon.IsNameVisible(addon, itemName) ? itemName : null;
            result.Add(new HiddenFish(slot.Id, itemId, fish.FishingSpot.IsValid ? fish.FishingSpot.Value.GatheringLevel : (byte)0, knownName));
        }
        return result;
    }
}
