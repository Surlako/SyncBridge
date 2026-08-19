using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using HarmonyLib;

namespace SyncBridge;

internal sealed class LightlessSuppressor : IDisposable
{
    private const string HarmonyId = "Surlako.SyncBridge.LightlessSuppressor";
    private const string ExactPairHandlerName = "LightlessSync.PlayerData.Handlers.PairHandler";

    private static readonly object OwnershipLock = new();
    private static readonly object PropertyLock = new();
    private static readonly Dictionary<Type, PropertyInfo> PlayerCharacterProperties = [];
    private static HashSet<nint> playerSyncOwned = [];
    private static int suppressedApplications;

    private readonly IPluginLog log;
    private readonly HashSet<MethodBase> patchedMethods = [];
    private Harmony? harmony;
    private DateTime nextPatchAttemptUtc = DateTime.MinValue;
    private bool disposed;
    private bool retryAllowed = true;
    private string diagnosticReason = "suppression hook has not been attempted";

    public bool IsOperational => patchedMethods.Count > 0;
    public int SuppressedApplications => Volatile.Read(ref suppressedApplications);
    public string DiagnosticReason => diagnosticReason;

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

        if (retryAllowed && !IsOperational && DateTime.UtcNow >= nextPatchAttemptUtc)
            TryInstallPatch();
    }

    public bool ShouldSuppress(nint gameObjectAddress)
        => IsPlayerSyncOwned(gameObjectAddress);

    private void TryInstallPatch()
    {
        if (disposed)
            return;

        nextPatchAttemptUtc = DateTime.UtcNow.AddSeconds(10);

        try
        {
            var assemblies = GetLoadedAssemblies().ToArray();
            var lightlessAssemblies = assemblies.Where(IsLightlessAssembly).ToArray();

            if (lightlessAssemblies.Length == 0)
            {
                SetInactiveReason("no Lightless assembly found", retry: true);
                return;
            }

            var pairHandlerTypes = FindPairHandlerTypes(lightlessAssemblies).ToArray();
            if (pairHandlerTypes.Length == 0)
            {
                SetInactiveReason("Lightless assembly found but PairHandler missing", retry: false);
                return;
            }

            var foundPlayerCharacter = false;
            var foundSupportedMethod = false;
            var hookErrors = new List<Exception>();

            harmony ??= new Harmony(HarmonyId);

            foreach (var pairHandlerType in pairHandlerTypes)
            {
                var playerCharacterProperty = pairHandlerType.GetProperty(
                    "PlayerCharacter",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (playerCharacterProperty == null)
                    continue;

                foundPlayerCharacter = true;

                var installedForType = 0;

                foreach (var method in GetSupportedApplyMethods(pairHandlerType))
                {
                    foundSupportedMethod = true;

                    if (patchedMethods.Contains(method))
                        continue;

                    try
                    {
                        var prefixName = method.ReturnType == typeof(void)
                            ? nameof(ApplyCharacterDataPrefix)
                            : nameof(ApplyCharacterDataAsyncPrefix);

                        var prefix = typeof(LightlessSuppressor).GetMethod(
                            prefixName,
                            BindingFlags.Static | BindingFlags.NonPublic)
                            ?? throw new MissingMethodException(typeof(LightlessSuppressor).FullName, prefixName);

                        lock (PropertyLock)
                            PlayerCharacterProperties[pairHandlerType] = playerCharacterProperty;

                        using var reflectionScope = AssemblyLoadContext.EnterContextualReflection(pairHandlerType.Assembly);
                        harmony.Patch(method, prefix: new HarmonyMethod(prefix));
                        patchedMethods.Add(method);
                        installedForType++;
                    }
                    catch (Exception ex)
                    {
                        hookErrors.Add(ex);
                        log.Error(
                            ex,
                            "Failed to hook {Type}.{Method}.",
                            pairHandlerType.FullName ?? pairHandlerType.Name,
                            method.Name);
                    }
                }

                if (installedForType == 0)
                {
                    lock (PropertyLock)
                        PlayerCharacterProperties.Remove(pairHandlerType);
                }
            }

            if (patchedMethods.Count > 0)
            {
                diagnosticReason = $"hooked {patchedMethods.Count} Lightless character-apply method(s)";
                log.Information("SyncBridge suppression ACTIVE: {Reason}.", diagnosticReason);
                return;
            }

            if (!foundPlayerCharacter)
                SetInactiveReason("PairHandler found but PlayerCharacter missing", retry: false);
            else if (!foundSupportedMethod)
                SetInactiveReason("PairHandler found but no supported apply method", retry: false);
            else
                SetInactiveReason(
                    $"hook error: {hookErrors.FirstOrDefault()?.GetBaseException().Message ?? "unknown error"}",
                    retry: false);
        }
        catch (Exception ex)
        {
            SetInactiveReason($"hook error: {ex.GetBaseException().Message}", retry: false);
            log.Error(ex, "Failed to install the Lightless suppression hook.");
        }
    }

    private void SetInactiveReason(string reason, bool retry)
    {
        diagnosticReason = reason;
        retryAllowed = retry;

        if (retry)
            log.Debug("SyncBridge suppression INACTIVE: {Reason}; will retry in 10 seconds.", reason);
        else
            log.Warning("SyncBridge suppression INACTIVE: {Reason}; retries disabled to protect frame time.", reason);
    }

    private static bool ApplyCharacterDataPrefix(object __instance)
    {
        if (!ShouldSuppressInstance(__instance))
            return true;

        Interlocked.Increment(ref suppressedApplications);
        return false;
    }

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
            var instanceType = instance.GetType();
            PropertyInfo? property;

            lock (PropertyLock)
                PlayerCharacterProperties.TryGetValue(instanceType, out property);

            property ??= instanceType.GetProperty(
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

    private static IEnumerable<Assembly> GetLoadedAssemblies()
    {
        var seen = new HashSet<Assembly>(ReferenceEqualityComparer.Instance);

        foreach (var loadContext in AssemblyLoadContext.All)
        {
            IEnumerable<Assembly> assemblies;
            try
            {
                assemblies = loadContext.Assemblies.ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var assembly in assemblies)
            {
                if (seen.Add(assembly))
                    yield return assembly;
            }
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (seen.Add(assembly))
                yield return assembly;
        }
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

    private static IEnumerable<Type> FindPairHandlerTypes(IEnumerable<Assembly> assemblies)
    {
        var assemblyArray = assemblies.ToArray();
        var exactTypes = new HashSet<Type>(ReferenceEqualityComparer.Instance);

        foreach (var assembly in assemblyArray)
        {
            try
            {
                var exactType = assembly.GetType(ExactPairHandlerName, throwOnError: false, ignoreCase: false);
                if (exactType != null)
                    exactTypes.Add(exactType);
            }
            catch
            {
                // Continue to the compatibility scan below.
            }
        }

        foreach (var type in exactTypes)
            yield return type;

        if (exactTypes.Count > 0)
            yield break;

        foreach (var type in assemblyArray.SelectMany(GetLoadableTypes))
        {
            if (exactTypes.Contains(type) || !string.Equals(type.Name, "PairHandler", StringComparison.Ordinal))
                continue;

            var assemblyName = type.Assembly.GetName().Name ?? string.Empty;
            var typeNamespace = type.Namespace ?? string.Empty;
            if (assemblyName.Contains("Lightless", StringComparison.OrdinalIgnoreCase) ||
                typeNamespace.Contains("Lightless", StringComparison.OrdinalIgnoreCase))
                yield return type;
        }
    }

    private static IEnumerable<MethodInfo> GetSupportedApplyMethods(Type pairHandlerType)
    {
        return pairHandlerType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method =>
                (method.Name == "ApplyCharacterData" && method.ReturnType == typeof(void)) ||
                (method.Name == "ApplyCharacterDataAsync" && method.ReturnType == typeof(Task)));
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
            if (harmony != null)
            {
                foreach (var method in patchedMethods)
                {
                    using var reflectionScope = AssemblyLoadContext.EnterContextualReflection(method.Module.Assembly);
                    harmony.Unpatch(method, HarmonyPatchType.All, HarmonyId);
                }
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to remove SyncBridge Harmony patches during shutdown.");
        }

        patchedMethods.Clear();
        diagnosticReason = "disposed";

        lock (PropertyLock)
            PlayerCharacterProperties.Clear();

        lock (OwnershipLock)
            playerSyncOwned.Clear();

        log.Debug("LightlessSuppressor disposed.");
    }
}
