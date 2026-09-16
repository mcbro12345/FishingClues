using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace FishingClues;

public sealed class DalamudClueWindow : Window
{
    private IReadOnlyList<FishClueSection> sections = [];
    public DalamudClueWindow() : base("Fish Details###FishingCluesDetails") { }
    public void Show(IReadOnlyList<FishClueSection> value) { sections = value; IsOpen = true; }
    public override void PreDraw()
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }
    public override void Draw()
    {
        ImGui.BeginChild("details", Vector2.Zero, true);
        for (int i = 0; i < sections.Count; i++)
        {
            if (i > 0) ImGui.Separator();
            ImGui.TextUnformatted(sections[i].Heading);
            foreach (string line in sections[i].Lines)
            {
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.TextWrapped(line);
            }
        }
        ImGui.EndChild();
    }
}
