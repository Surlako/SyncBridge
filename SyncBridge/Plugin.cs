using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace SyncBridge;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/syncbridge";
    private const string ShortCommandName = "/sb";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("SyncBridge");
    private readonly SyncBridgeWindow settingsWindow;

    internal Configuration Configuration { get; }
    internal SyncCoordinator Coordinator { get; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Coordinator = new SyncCoordinator(
            PluginInterface,
            Log,
            Configuration.SuppressionEnabled);

        settingsWindow = new SyncBridgeWindow(this);
        windowSystem.AddWindow(settingsWindow);

        Framework.Update += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleSettingsWindow;
        PluginInterface.UiBuilder.OpenMainUi += ToggleSettingsWindow;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens SyncBridge settings. Use '/syncbridge status' for a chat diagnostic."
        });
        CommandManager.AddHandler(ShortCommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Short alias for /syncbridge. Use '/sb status' for a chat diagnostic."
        });

        Log.Information("SyncBridge loaded.");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        Coordinator.Update();
    }

    private void OnCommand(string command, string args)
    {
        if (string.Equals(args.Trim(), "status", StringComparison.OrdinalIgnoreCase))
        {
            PrintStatusToChat();
            return;
        }

        settingsWindow.Toggle();
    }

    internal void SetSuppressionEnabled(bool enabled)
    {
        Coordinator.Suppressor.SetEnabled(enabled);
        Configuration.SuppressionEnabled = enabled;
        Configuration.Save();
        Log.Information("SyncBridge suppression {State} by user.", enabled ? "enabled" : "disabled");
    }

    internal void PrintStatusToChat()
        => ChatGui.Print(BuildStatusMessage());

    private string BuildStatusMessage()
    {
        var state = Coordinator.State;
        var suppressor = Coordinator.Suppressor;
        var suppressionStatus = !suppressor.IsEnabled
            ? "DISABLED"
            : suppressor.IsOperational
                ? "ACTIVE"
                : "INACTIVE";

        var message =
            $"PlayerSync: {state.PlayerSyncHandled.Count} | " +
            $"Lightless: {state.LightlessHandled.Count} | " +
            $"Overlap: {state.Overlap.Count} | " +
            $"Suppression: {suppressionStatus} | " +
            $"Hook: {(suppressor.IsOperational ? "ACTIVE" : "INACTIVE")} | " +
            $"Observed applies: {suppressor.ObservedApplications} | " +
            $"Blocked applies: {suppressor.SuppressedApplications}";

        if (!suppressor.IsOperational)
            message += $" | Reason: {suppressor.DiagnosticReason}";

        Log.Information(message);
        return message;
    }

    private void ToggleSettingsWindow()
        => settingsWindow.Toggle();

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleSettingsWindow;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleSettingsWindow;
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(ShortCommandName);
        windowSystem.RemoveAllWindows();
        Coordinator.Dispose();
    }
}
