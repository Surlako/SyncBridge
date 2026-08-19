using Dalamud.Configuration;

namespace SyncBridge;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool SuppressionEnabled { get; set; } = true;

    public void Save()
        => Plugin.PluginInterface.SavePluginConfig(this);
}
