using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FishingClues;

public sealed class DiagnosticWindow(Action refresh) : Window("Fishing Clues Diagnostics###FishingCluesDiagnostics")
{
    private string report = "No report captured.";
    private bool copied;

    public void Show(string text)
    {
        report = text;
        copied = false;
        IsOpen = true;
    }

    public override void PreDraw() => SizeConstraints = new WindowSizeConstraints
    {
        MinimumSize = new Vector2(520, 350),
        MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
    };

    public override void Draw()
    {
        ImGui.TextWrapped("Open the vanilla Fishing Log to the area that disagrees, then press Refresh. Copy the report and paste it into our conversation.");
        if (ImGui.Button("Refresh")) refresh();
        ImGui.SameLine();
        if (ImGui.Button("Copy report"))
        {
            ImGui.SetClipboardText(report);
            copied = true;
        }
        if (copied) { ImGui.SameLine(); ImGui.TextUnformatted("Copied!"); }
        ImGui.BeginChild("report", Vector2.Zero, true, ImGuiWindowFlags.HorizontalScrollbar);
        ImGui.TextUnformatted(report);
        ImGui.EndChild();
    }
}
