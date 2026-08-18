using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using HarmonyLib;

namespace SyncBridge;

internal sealed class LightlessSuppressor : IDisposable
{
    private const string HarmonyId = "Surlako.SyncBridge.LightlessSuppressor";

    private static readonly object OwnershipLock = new();
    private static HashSet<nint> playerSyncOwned = [];
    private static PropertyInfo? playerCharacterProperty;
    private static int suppressedApplications;

    private readonly IPluginLog log;
    private Harmony? harmony;
    private DateTime nextPatchAttemptUtc = DateTime.MinValue;
    private bool disposed;
    private int patchedMethodCount;

    public bool IsOperational => patchedMethodCount > 0;
    public int SuppressedApplications => Volatile.Read(ref suppressedApplications);

    public LightlessSuppressor(IPluginLog log)
    {
        this.log = log;
        TryInstallPatch();
    }

    public void SetPlayerSyncOwned(IEnumerable<nint> addresses)
    {
        lock (OwnershipLock)
        {
            playerSyncOwned = addresses
                .Where(address => address != nint.Zero)
                .ToHashSet();
        }

        if (!IsOperational && DateTime.UtcNow >= nextPatchAttemptUtc)
            TryInstallPatch();
    }

    public bool ShouldSuppress(nint gameObjectAddress)
        => IsPlayerSyncOwned(gameObjectAddress);

    private void TryInstallPatch()
    {
        if (disposed)
            return;

        nextPatchAttemptUtc = DateTime.UtcNow.AddSeconds(2);

        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(IsLightlessAssembly))
            {
                var pairHandlerType = FindPairHandlerType(assembly);
                if (pairHandlerType == null)
                    continue;

                playerCharacterProperty = pairHandlerType.GetProperty(
                    "PlayerCharacter",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (playerCharacterProperty == null)
                {
                    log.Information(
                        "Found Lightless PairHandler in {Assembly}, but PlayerCharacter was not found.",
                        assembly.GetName().Name ?? "unknown");
                    continue;
                }

                harmony ??= new Harmony(HarmonyId);

                var installed = 0;

                // Current Lightless: public void ApplyCharacterData(...)
                foreach (var method in pairHandlerType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(method => method.Name == "ApplyCharacterData" && method.ReturnType == typeof(void)))
                {
                    var prefix = typeof(LightlessSuppressor).GetMethod(
                        nameof(ApplyCharacterDataPrefix),
                        BindingFlags.Static | BindingFlags.NonPublic);

                    if (prefix != null)
                    {
                        harmony.Patch(method, prefix: new HarmonyMethod(prefix));
                        installed++;
                    }
                }

                // Compatibility with older/forked Lightless builds.
                foreach (var method in pairHandlerType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(method => method.Name == "ApplyCharacterDataAsync" && method.ReturnType == typeof(Task)))
                {
                    var prefix = typeof(LightlessSuppressor).GetMethod(
                        nameof(ApplyCharacterDataAsyncPrefix),
                        BindingFlags.Static | BindingFlags.NonPublic);

                    if (prefix != null)
                    {
                        harmony.Patch(method, prefix: new HarmonyMethod(prefix));
                        installed++;
                    }
                }

                if (installed == 0)
                {
                    log.Information(
                        "Found Lightless PairHandler in {Assembly}, but no supported ApplyCharacterData method was found.",
                        assembly.GetName().Name ?? "unknown");
                    continue;
                }

                patchedMethodCount = installed;

                log.Information(
                    "SyncBridge suppression ACTIVE: hooked {Count} Lightless character-apply method(s) in {Assembly}.",
                    patchedMethodCount,
                    assembly.GetName().Name ?? "unknown");

                return;
            }

            log.Debug("Lightless PairHandler is not loaded yet; SyncBridge will retry.");
        }
        catch (Exception ex)
        {
            patchedMethodCount = 0;
            log.Error(ex, "Failed to install the Lightless suppression hook.");
        }
    }

    // Current Lightless void entry point.
    private static bool ApplyCharacterDataPrefix(object __instance)
    {
        if (!ShouldSuppressInstance(__instance))
            return true;

        Interlocked.Increment(ref suppressedApplications);
        return false;
    }

    // Older/forked async entry point.
    private static bool ApplyCharacterDataAsyncPrefix(object __instance, ref Task __result)
    {
        if (!ShouldSuppressInstance(__instance))
            return true;

        __result = Task.CompletedTask;
        Interlocked.Increment(ref suppressedApplications);
        return false;
    }

    private static bool ShouldSuppressInstance(object instance)
    {
        try
        {
            var property = playerCharacterProperty
                ?? instance.GetType().GetProperty(
                    "PlayerCharacter",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (property?.GetValue(instance) is not IntPtr address || address == IntPtr.Zero)
                return false;

            return IsPlayerSyncOwned(address);
        }
        catch
        {
            // Fail open: if Lightless changes, let it continue normally.
            return false;
        }
    }

    private static bool IsPlayerSyncOwned(nint gameObjectAddress)
    {
        if (gameObjectAddress == nint.Zero)
            return false;

        lock (OwnershipLock)
            return playerSyncOwned.Contains(gameObjectAddress);
    }

    private static bool IsLightlessAssembly(Assembly assembly)
    {
        var assemblyName = assembly.GetName().Name ?? string.Empty;

        if (assemblyName.Contains("Lightless", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            return assembly.Location.Contains("Lightless", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static Type? FindPairHandlerType(Assembly assembly)
    {
        foreach (var type in GetLoadableTypes(assembly))
        {
            if (!string.Equals(type.Name, "PairHandler", StringComparison.Ordinal))
                continue;

            var ns = type.Namespace ?? string.Empty;
            if (!ns.Contains("Lightless", StringComparison.OrdinalIgnoreCase))
                continue;

            var hasApplyMethod = type
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(method =>
                    method.Name == "ApplyCharacterData" ||
                    method.Name == "ApplyCharacterDataAsync");

            if (hasApplyMethod)
                return type;
        }

        return null;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null).Cast<Type>();
        }
        catch
        {
            return [];
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;

        try
        {
            harmony?.UnpatchAll(HarmonyId);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to remove SyncBridge Harmony patches during shutdown.");
        }

        patchedMethodCount = 0;
        playerCharacterProperty = null;

        lock (OwnershipLock)
            playerSyncOwned.Clear();

        log.Debug("LightlessSuppressor disposed.");
    }
}
