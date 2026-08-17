using Dalamud.Plugin.Services;

namespace SyncBridge;

/// <summary>
/// Runtime arbitration boundary.
///
/// The coordinator tells this component which game-object addresses are already
/// owned by PlayerSync. The final implementation will intercept Lightless at the
/// narrowest per-character synchronization/apply boundary and reject work for
/// these addresses while leaving every other Lightless character untouched.
///
/// IMPORTANT:
/// No suppression hook is enabled in this initial scaffold.
/// </summary>
internal sealed class LightlessSuppressor : IDisposable
{
    private readonly IPluginLog log;
    private readonly HashSet<nint> playerSyncOwned = [];

    public bool IsOperational => false;

    public LightlessSuppressor(IPluginLog log)
    {
        this.log = log;
    }

    public void SetPlayerSyncOwned(IEnumerable<nint> addresses)
    {
        playerSyncOwned.Clear();

        foreach (var address in addresses)
        {
            if (address != nint.Zero)
                playerSyncOwned.Add(address);
        }
    }

    public bool ShouldSuppress(nint gameObjectAddress)
        => gameObjectAddress != nint.Zero && playerSyncOwned.Contains(gameObjectAddress);

    public void Dispose()
    {
        playerSyncOwned.Clear();
        log.Debug("LightlessSuppressor disposed.");
    }
}
