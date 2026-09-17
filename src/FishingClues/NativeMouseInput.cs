using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.Input;

namespace FishingClues;

// KamiToolKit's MouseDown event fires for any button, so anywhere that needs
// to tell left clicks from right clicks (or "still held" from "just
// pressed") has to read the raw cursor state itself. Centralized here so
// that isn't reimplemented at every click handler that needs it.
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
