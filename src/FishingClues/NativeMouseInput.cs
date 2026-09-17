using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.Input;

namespace FishingClues;

// KamiToolKit's MouseDown fires for any button, so click handlers that care which
// one (or whether it's still held) need to read the raw cursor state instead.
internal static class NativeMouseInput
{
    public static unsafe bool IsLeftButtonPressed()
    {
        var framework = Framework.Instance();
        return framework != null && (framework->CursorInputs.MouseButtonPressedFlags & MouseButtonFlags.LBUTTON) != 0;
    }

    public static unsafe bool IsRightButtonHeld()
    {
        var framework = Framework.Instance();
        return framework != null && (framework->CursorInputs.MouseButtonHeldFlags & MouseButtonFlags.RBUTTON) != 0;
    }
}
