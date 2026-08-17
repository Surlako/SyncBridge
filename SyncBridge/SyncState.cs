namespace SyncBridge;

internal sealed class SyncState
{
    public HashSet<nint> PlayerSyncHandled { get; } = [];
    public HashSet<nint> LightlessHandled { get; } = [];
    public HashSet<nint> Overlap { get; } = [];

    public void Replace(IEnumerable<nint> playerSync, IEnumerable<nint> lightless)
    {
        PlayerSyncHandled.Clear();
        LightlessHandled.Clear();
        Overlap.Clear();

        foreach (var address in playerSync)
        {
            if (address != nint.Zero)
                PlayerSyncHandled.Add(address);
        }

        foreach (var address in lightless)
        {
            if (address != nint.Zero)
                LightlessHandled.Add(address);
        }

        foreach (var address in PlayerSyncHandled)
        {
            if (LightlessHandled.Contains(address))
                Overlap.Add(address);
        }
    }
}
