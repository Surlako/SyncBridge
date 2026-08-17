using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace SyncBridge;

internal sealed class SyncCoordinator : IDisposable
{
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<List<nint>> playerSyncHandled;
    private readonly ICallGateSubscriber<List<nint>> lightlessHandled;

    private DateTime nextPollUtc = DateTime.MinValue;

    public SyncState State { get; } = new();
    public LightlessSuppressor Suppressor { get; }

    public SyncCoordinator(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;

        playerSyncHandled =
            pluginInterface.GetIpcSubscriber<List<nint>>("PlayerSync.GetHandledAddresses");

        lightlessHandled =
            pluginInterface.GetIpcSubscriber<List<nint>>("LightlessSync.GetHandledAddresses");

        Suppressor = new LightlessSuppressor(log);
    }

    public void Update()
    {
        if (DateTime.UtcNow < nextPollUtc)
            return;

        nextPollUtc = DateTime.UtcNow.AddMilliseconds(250);

        var playerSync = TryGetAddresses(playerSyncHandled, "PlayerSync");
        var lightless = TryGetAddresses(lightlessHandled, "Lightless");

        State.Replace(playerSync, lightless);
        Suppressor.SetPlayerSyncOwned(State.PlayerSyncHandled);
    }

    private List<nint> TryGetAddresses(
        ICallGateSubscriber<List<nint>> subscriber,
        string serviceName)
    {
        try
        {
            return subscriber.InvokeFunc() ?? [];
        }
        catch (Exception ex)
        {
            log.Verbose(ex, "{ServiceName} IPC unavailable.", serviceName);
            return [];
        }
    }

    public void Dispose()
    {
        Suppressor.Dispose();
    }
}
