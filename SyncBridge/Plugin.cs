using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace SyncBridge;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/syncbridge";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly SyncCoordinator coordinator;

    public Plugin()
    {
        coordinator = new SyncCoordinator(PluginInterface, Log);
        Framework.Update += OnFrameworkUpdate;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Shows SyncBridge status."
        });

        Log.Information("SyncBridge loaded.");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        coordinator.Update();
    }

    private void OnCommand(string command, string args)
    {
        var state = coordinator.State;
        Log.Information(
            "PlayerSync: {PlayerSyncCount}, Lightless: {LightlessCount}, overlap: {OverlapCount}, suppression: {SuppressionEnabled}",
            state.PlayerSyncHandled.Count,
            state.LightlessHandled.Count,
            state.Overlap.Count,
            coordinator.Suppressor.IsOperational);

        PluginInterface.UiBuilder.AddNotification(
            $"PlayerSync: {state.PlayerSyncHandled.Count}\n" +
            $"Lightless: {state.LightlessHandled.Count}\n" +
            $"Overlap: {state.Overlap.Count}\n" +
            $"Suppression: {(coordinator.Suppressor.IsOperational ? "ACTIVE" : "NOT IMPLEMENTED")}",
            "SyncBridge");
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        CommandManager.RemoveHandler(CommandName);
        coordinator.Dispose();
    }
}
