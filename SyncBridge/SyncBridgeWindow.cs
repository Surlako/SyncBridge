using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace SyncBridge;

internal sealed class SyncBridgeWindow : Window
{
    private readonly Plugin plugin;

    public SyncBridgeWindow(Plugin plugin)
        : base("SyncBridge###SyncBridgeSettings")
    {
        this.plugin = plugin;
        Size = new Vector2(470, 310);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoCollapse;
    }

    public override void Draw()
    {
        var enabled = plugin.Configuration.SuppressionEnabled;
        if (ImGui.Checkbox("Enable PlayerSync priority", ref enabled))
            plugin.SetSuppressionEnabled(enabled);

        ImGui.TextWrapped(
            "When enabled, Lightless is blocked only for visible players currently handled by PlayerSync. " +
            "When disabled, Lightless is allowed to work normally for everyone.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("Live status");

        var state = plugin.Coordinator.State;
        var suppressor = plugin.Coordinator.Suppressor;

        ImGui.Text($"PlayerSync handled: {state.PlayerSyncHandled.Count}");
        ImGui.Text($"Lightless handled: {state.LightlessHandled.Count}");
        ImGui.Text($"Overlap: {state.Overlap.Count}");
        ImGui.Text($"Observed Lightless applies: {suppressor.ObservedApplications}");
        ImGui.Text($"Blocked Lightless applies: {suppressor.SuppressedApplications}");

        ImGui.Text("Suppression:");
        ImGui.SameLine();

        if (!suppressor.IsEnabled)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.75f, 0.25f, 1.0f), "DISABLED");
        }
        else if (suppressor.IsOperational)
        {
            ImGui.TextColored(new Vector4(0.35f, 0.9f, 0.45f, 1.0f), "ACTIVE");
        }
        else
        {
            ImGui.TextColored(new Vector4(1.0f, 0.35f, 0.35f, 1.0f), "INACTIVE");
        }

        ImGui.Text($"Hook: {(suppressor.IsOperational ? "ACTIVE" : "INACTIVE")}");

        if (!suppressor.IsOperational)
        {
            ImGui.Spacing();
            ImGui.TextWrapped($"Hook reason: {suppressor.DiagnosticReason}");
        }

        ImGui.Spacing();
        if (ImGui.Button("Print status to chat"))
            plugin.PrintStatusToChat();
    }
}
