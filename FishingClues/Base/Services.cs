using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace FishingClues.Base;

// filled by PluginInterface.Create<Services>() in Plugin's constructor
internal sealed class Services
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; set; } = null!;
    [PluginService] internal static IFramework Framework { get; set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; set; } = null!;
    [PluginService] internal static IClientState ClientState { get; set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; set; } = null!;
    [PluginService] internal static IAddonEventManager AddonEvents { get; set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; set; } = null!;
    [PluginService] internal static IPluginLog Log { get; set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; set; } = null!;
}
