using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using HarmonyLib;

namespace SyncBridge;

internal sealed class LightlessSuppressor : IDisposable
{
    private const string HarmonyId = "Surlako.SyncBridge.LightlessSuppressor";
    private const string ExactPairHandlerName = "LightlessSync.PlayerData.Pairs.PairHandler";
    private const string LegacyPairHandlerName = "LightlessSync.PlayerData.Handlers.PairHandler";
    private const string OwnershipDataKey = HarmonyId + ".PlayerSyncOwned";
    private const string CounterDataKey = HarmonyId + ".SuppressedCounter";
    private const string ObservedCounterDataKey = HarmonyId + ".ObservedCounter";

    private static readonly object OwnershipLock = new();
    private static readonly object PrefixFactoryLock = new();
    private static readonly int[] SuppressedCounter = [0];
    private static readonly int[] ObservedCounter = [0];
    private static readonly Dictionary<MethodBase, DynamicMethod> PrefixFactoryMethods = [];
    private static HashSet<nint> playerSyncOwned = [];

    private readonly FileInfo pluginAssemblyLocation;
    private readonly IPluginLog log;
    private readonly HashSet<MethodBase> patchedMethods = [];
    private readonly Dictionary<MethodBase, DynamicMethod> patchPrefixes = [];
    private object? harmony;
    private Type? harmonyMethodType;
    private MethodInfo? harmonyPatchMethod;
    private MethodInfo? harmonyUnpatchMethod;
    private MethodInfo? prefixFactoryMethod;
    private DateTime nextPatchAttemptUtc = DateTime.MinValue;
    private bool disposed;
    private bool retryAllowed = true;
    private string diagnosticReason = "suppression hook has not been attempted";

    public bool IsOperational => patchedMethods.Count > 0;
    public int SuppressedApplications => Volatile.Read(ref SuppressedCounter[0]);
    public int ObservedApplications => Volatile.Read(ref ObservedCounter[0]);
    public string DiagnosticReason => diagnosticReason;

    public LightlessSuppressor(FileInfo pluginAssemblyLocation, IPluginLog log)
    {
        this.pluginAssemblyLocation = pluginAssemblyLocation;
        this.log = log;
        AppContext.SetData(CounterDataKey, SuppressedCounter);
        AppContext.SetData(ObservedCounterDataKey, ObservedCounter);
        TryInstallPatch();
    }

    public void SetPlayerSyncOwned(IEnumerable<nint> addresses)
    {
        var owned = addresses
            .Where(address => address != nint.Zero)
            .ToHashSet();

        lock (OwnershipLock)
            playerSyncOwned = owned;

        AppContext.SetData(OwnershipDataKey, owned);

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

            EnsureHarmonyRuntime();

            foreach (var pairHandlerType in pairHandlerTypes)
            {
                var playerCharacterProperty = pairHandlerType.GetProperty(
                    "PlayerCharacter",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (playerCharacterProperty == null)
                    continue;

                foundPlayerCharacter = true;

                foreach (var method in GetSupportedApplyMethods(pairHandlerType))
                {
                    foundSupportedMethod = true;

                    if (patchedMethods.Contains(method))
                        continue;

                    try
                    {
                        var prefix = CreateContextLocalPrefix(
                            pairHandlerType,
                            playerCharacterProperty,
                            method);

                        lock (PrefixFactoryLock)
                            PrefixFactoryMethods[method] = prefix;

                        using var reflectionScope = AssemblyLoadContext.EnterContextualReflection(pairHandlerType.Assembly);
                        var harmonyPrefix = Activator.CreateInstance(harmonyMethodType!, [prefixFactoryMethod!])
                            ?? throw new InvalidOperationException("Could not create the default-context Harmony prefix descriptor.");
                        harmonyPatchMethod!.Invoke(harmony, [method, harmonyPrefix, null, null, null]);
                        patchedMethods.Add(method);
                        patchPrefixes[method] = prefix;
                    }
                    catch (Exception ex)
                    {
                        lock (PrefixFactoryLock)
                            PrefixFactoryMethods.Remove(method);

                        hookErrors.Add(ex);
                        log.Error(
                            ex,
                            "Failed to hook {Type}.{Method}.",
                            pairHandlerType.FullName ?? pairHandlerType.Name,
                            method.Name);
                    }
                }
            }

            if (patchedMethods.Count > 0)
            {
                var targets = string.Join(", ", patchedMethods.Select(method => method.Name).Distinct());
                diagnosticReason = $"hooked {patchedMethods.Count} Lightless character-apply method(s): {targets}";
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

    private void EnsureHarmonyRuntime()
    {
        if (harmony != null)
            return;

        // MonoMod cannot build Harmony's wrapper when its engine is loaded in a
        // collectible plugin context. Keep the engine in the default context;
        // the generated prefix itself is still owned by Lightless's module.
        var harmonyAssembly = GetDefaultContextHarmonyAssembly();
        var harmonyType = harmonyAssembly.GetType("HarmonyLib.Harmony", throwOnError: true)
            ?? throw new TypeLoadException("HarmonyLib.Harmony");
        harmonyMethodType = harmonyAssembly.GetType("HarmonyLib.HarmonyMethod", throwOnError: true)
            ?? throw new TypeLoadException("HarmonyLib.HarmonyMethod");
        prefixFactoryMethod = typeof(LightlessSuppressor).GetMethod(
            nameof(CreatePatchPrefix),
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(LightlessSuppressor).FullName, nameof(CreatePatchPrefix));

        harmonyPatchMethod = harmonyType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method =>
            {
                if (method.Name != "Patch")
                    return false;

                var parameters = method.GetParameters();
                return parameters.Length == 5 &&
                       parameters[0].ParameterType == typeof(MethodBase) &&
                       parameters[1].ParameterType.FullName == "HarmonyLib.HarmonyMethod";
            })
            ?? throw new MissingMethodException(harmonyType.FullName, "Patch");

        harmonyUnpatchMethod = harmonyType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method =>
            {
                if (method.Name != "Unpatch")
                    return false;

                var parameters = method.GetParameters();
                return parameters.Length == 2 &&
                       parameters[0].ParameterType == typeof(MethodBase) &&
                       parameters[1].ParameterType == typeof(MethodInfo);
            })
            ?? throw new MissingMethodException(harmonyType.FullName, "Unpatch(MethodBase, MethodInfo)");

        harmony = Activator.CreateInstance(harmonyType, [HarmonyId])
            ?? throw new InvalidOperationException("Could not create the default-context Harmony instance.");
    }

    private Assembly GetDefaultContextHarmonyAssembly()
    {
        var pluginHarmonyAssembly = typeof(Harmony).Assembly;
        var simpleName = pluginHarmonyAssembly.GetName().Name;
        var defaultAssembly = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly =>
            string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));

        if (defaultAssembly != null)
            return defaultAssembly;

        var harmonyPath = pluginHarmonyAssembly.Location;
        if (string.IsNullOrWhiteSpace(harmonyPath))
        {
            var pluginDirectory = pluginAssemblyLocation.Directory
                ?? throw new DirectoryNotFoundException("The SyncBridge installation directory is unavailable.");
            harmonyPath = Path.Combine(pluginDirectory.FullName, "0Harmony.dll");
        }

        if (!File.Exists(harmonyPath))
            throw new FileNotFoundException("The packaged Harmony assembly was not found beside SyncBridge.", harmonyPath);

        return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(harmonyPath));
    }

    private static MethodInfo CreatePatchPrefix(MethodBase originalMethod)
    {
        // Harmony calls this factory while constructing the wrapper. Returning a
        // target-owned DynamicMethod prevents the wrapper from referencing the
        // collectible SyncBridge assembly at runtime.
        lock (PrefixFactoryLock)
        {
            if (PrefixFactoryMethods.TryGetValue(originalMethod, out var prefix))
                return prefix;
        }

        throw new MissingMethodException($"No SyncBridge prefix was prepared for {originalMethod}.");
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

    private static DynamicMethod CreateContextLocalPrefix(
        Type pairHandlerType,
        PropertyInfo playerCharacterProperty,
        MethodInfo targetMethod)
    {
        var getter = playerCharacterProperty.GetMethod
            ?? throw new MissingMethodException(pairHandlerType.FullName, "get_PlayerCharacter");
        var returnsTask = targetMethod.ReturnType == typeof(Task);
        var isDispatchBoundary = targetMethod.Name == "DownloadAndApplyCharacter";
        var targetParameters = targetMethod.GetParameters();

        if (isDispatchBoundary && targetParameters.Length != 1)
            throw new MissingMethodException(pairHandlerType.FullName, "DownloadAndApplyCharacter(ApplyContext)");

        var parameterTypes = isDispatchBoundary
            ? new[] { typeof(object), targetParameters[0].ParameterType }
            : returnsTask
                ? new[] { typeof(object), typeof(Task).MakeByRefType() }
                : new[] { typeof(object) };

        var prefix = new DynamicMethod(
            $"SyncBridge_{pairHandlerType.Name}_{targetMethod.Name}_Prefix",
            typeof(bool),
            parameterTypes,
            pairHandlerType.Module,
            skipVisibility: true);

        prefix.DefineParameter(1, ParameterAttributes.None, "__instance");
        if (isDispatchBoundary)
            prefix.DefineParameter(2, ParameterAttributes.None, "__0");
        else if (returnsTask)
            prefix.DefineParameter(2, ParameterAttributes.None, "__result");

        var getAppContextData = typeof(AppContext).GetMethod(
            nameof(AppContext.GetData),
            BindingFlags.Static | BindingFlags.Public,
            binder: null,
            types: [typeof(string)],
            modifiers: null)
            ?? throw new MissingMethodException(typeof(AppContext).FullName, nameof(AppContext.GetData));

        var containsAddress = typeof(HashSet<nint>).GetMethod(
            nameof(HashSet<nint>.Contains),
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: [typeof(nint)],
            modifiers: null)
            ?? throw new MissingMethodException(typeof(HashSet<nint>).FullName, nameof(HashSet<nint>.Contains));

        var incrementCounter = typeof(Interlocked).GetMethod(
            nameof(Interlocked.Increment),
            BindingFlags.Static | BindingFlags.Public,
            binder: null,
            types: [typeof(int).MakeByRefType()],
            modifiers: null)
            ?? throw new MissingMethodException(typeof(Interlocked).FullName, nameof(Interlocked.Increment));

        var completedTaskGetter = returnsTask
            ? typeof(Task).GetProperty(nameof(Task.CompletedTask), BindingFlags.Static | BindingFlags.Public)?.GetMethod
                ?? throw new MissingMethodException(typeof(Task).FullName, $"get_{nameof(Task.CompletedTask)}")
            : null;

        MethodInfo? restoreApplyIntent = null;
        FieldInfo? pairRedrawManagerField = null;
        MethodInfo? abortPlayerScope = null;
        MethodInfo? realizationGetter = null;
        MethodInfo? consumedForceApplyModsGetter = null;
        Type? realizationType = null;

        if (isDispatchBoundary)
        {
            // Lightless 3.2.4 has already retained the desired character state at
            // this boundary. Mirror its own early-exit cleanup before skipping
            // downloads, Penumbra/Glamourer work, and redraws.
            restoreApplyIntent = pairHandlerType.GetMethod(
                "RestoreApplyIntent",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(bool)],
                modifiers: null)
                ?? throw new MissingMethodException(pairHandlerType.FullName, "RestoreApplyIntent(bool)");
            pairRedrawManagerField = pairHandlerType.GetField(
                "_pairRedrawManager",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(pairHandlerType.FullName, "_pairRedrawManager");
            abortPlayerScope = pairRedrawManagerField.FieldType.GetMethod(
                "AbortPlayerScope",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null)
                ?? throw new MissingMethodException(pairRedrawManagerField.FieldType.FullName, "AbortPlayerScope()");
            realizationGetter = targetParameters[0].ParameterType.GetProperty(
                "Realization",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetMethod
                ?? throw new MissingMemberException(targetParameters[0].ParameterType.FullName, "Realization");
            realizationType = realizationGetter.ReturnType;
            consumedForceApplyModsGetter = realizationType.GetProperty(
                "ConsumedForceApplyMods",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetMethod
                ?? throw new MissingMemberException(realizationType.FullName, "ConsumedForceApplyMods");
        }

        var il = prefix.GetILGenerator();
        var address = il.DeclareLocal(typeof(nint));
        var ownedAddresses = il.DeclareLocal(typeof(HashSet<nint>));
        var counter = il.DeclareLocal(typeof(int[]));
        var observedCounter = il.DeclareLocal(typeof(int[]));
        var realization = realizationType != null ? il.DeclareLocal(realizationType) : null;
        var prefixResult = il.DeclareLocal(typeof(bool));
        var afterObservedIncrement = il.DefineLabel();
        var allowOriginal = il.DefineLabel();
        var returnResult = il.DefineLabel();

        il.BeginExceptionBlock();

        il.Emit(OpCodes.Ldstr, ObservedCounterDataKey);
        il.Emit(OpCodes.Call, getAppContextData);
        il.Emit(OpCodes.Isinst, typeof(int[]));
        il.Emit(OpCodes.Stloc, observedCounter);
        il.Emit(OpCodes.Ldloc, observedCounter);
        il.Emit(OpCodes.Brfalse, afterObservedIncrement);
        il.Emit(OpCodes.Ldloc, observedCounter);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelema, typeof(int));
        il.Emit(OpCodes.Call, incrementCounter);
        il.Emit(OpCodes.Pop);
        il.MarkLabel(afterObservedIncrement);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, pairHandlerType);
        il.Emit(OpCodes.Callvirt, getter);
        il.Emit(OpCodes.Stloc, address);
        il.Emit(OpCodes.Ldloc, address);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Beq, allowOriginal);

        il.Emit(OpCodes.Ldstr, OwnershipDataKey);
        il.Emit(OpCodes.Call, getAppContextData);
        il.Emit(OpCodes.Isinst, typeof(HashSet<nint>));
        il.Emit(OpCodes.Stloc, ownedAddresses);
        il.Emit(OpCodes.Ldloc, ownedAddresses);
        il.Emit(OpCodes.Brfalse, allowOriginal);
        il.Emit(OpCodes.Ldloc, ownedAddresses);
        il.Emit(OpCodes.Ldloc, address);
        il.Emit(OpCodes.Callvirt, containsAddress);
        il.Emit(OpCodes.Brfalse, allowOriginal);

        il.Emit(OpCodes.Ldstr, CounterDataKey);
        il.Emit(OpCodes.Call, getAppContextData);
        il.Emit(OpCodes.Isinst, typeof(int[]));
        il.Emit(OpCodes.Stloc, counter);
        il.Emit(OpCodes.Ldloc, counter);
        il.Emit(OpCodes.Brfalse, allowOriginal);

        if (isDispatchBoundary)
        {
            var applyContextType = targetParameters[0].ParameterType;
            if (applyContextType.IsValueType)
                il.Emit(OpCodes.Ldarga_S, (byte)1);
            else
                il.Emit(OpCodes.Ldarg_1);
            il.Emit(applyContextType.IsValueType ? OpCodes.Call : OpCodes.Callvirt, realizationGetter!);
            il.Emit(OpCodes.Stloc, realization!);

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, pairHandlerType);
            if (realizationType!.IsValueType)
                il.Emit(OpCodes.Ldloca, realization!);
            else
                il.Emit(OpCodes.Ldloc, realization!);
            il.Emit(realizationType.IsValueType ? OpCodes.Call : OpCodes.Callvirt, consumedForceApplyModsGetter!);
            il.Emit(OpCodes.Call, restoreApplyIntent!);

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, pairHandlerType);
            il.Emit(OpCodes.Ldfld, pairRedrawManagerField!);
            il.Emit(OpCodes.Callvirt, abortPlayerScope!);
        }
        else if (returnsTask)
        {
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, completedTaskGetter!);
            il.Emit(OpCodes.Stind_Ref);
        }

        il.Emit(OpCodes.Ldloc, counter);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelema, typeof(int));
        il.Emit(OpCodes.Call, incrementCounter);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, prefixResult);
        il.Emit(OpCodes.Leave, returnResult);

        il.MarkLabel(allowOriginal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, prefixResult);
        il.Emit(OpCodes.Leave, returnResult);

        il.BeginCatchBlock(typeof(Exception));
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, prefixResult);
        il.Emit(OpCodes.Leave, returnResult);
        il.EndExceptionBlock();

        il.MarkLabel(returnResult);
        il.Emit(OpCodes.Ldloc, prefixResult);
        il.Emit(OpCodes.Ret);

        return prefix;
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
        var exactTypes = new List<Type>();
        var seenExactTypes = new HashSet<Type>(ReferenceEqualityComparer.Instance);

        foreach (var exactName in new[] { ExactPairHandlerName, LegacyPairHandlerName })
        {
            foreach (var assembly in assemblyArray)
            {
                try
                {
                    var exactType = assembly.GetType(exactName, throwOnError: false, ignoreCase: false);
                    if (exactType != null && seenExactTypes.Add(exactType))
                        exactTypes.Add(exactType);
                }
                catch
                {
                    // Continue to the compatibility scan below.
                }
            }
        }

        foreach (var type in exactTypes)
            yield return type;

        if (exactTypes.Count > 0)
            yield break;

        foreach (var type in assemblyArray.SelectMany(GetLoadableTypes))
        {
            if (seenExactTypes.Contains(type) || !string.Equals(type.Name, "PairHandler", StringComparison.Ordinal))
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
        var methods = pairHandlerType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        var dispatchBoundary = methods
            .Where(method =>
                method.Name == "DownloadAndApplyCharacter" &&
                method.ReturnType == typeof(void) &&
                method.GetParameters().Length == 1)
            .ToArray();
        if (dispatchBoundary.Length > 0)
            // The async ApplyCharacterData stub can already be inlined before
            // SyncBridge loads. This larger synchronous boundary remains detourable.
            return dispatchBoundary;

        var legacyApply = methods
            .Where(method => method.Name == "ApplyCharacterData" && method.ReturnType == typeof(void))
            .ToArray();
        if (legacyApply.Length > 0)
            return legacyApply;

        return methods.Where(method =>
            method.Name == "ApplyCharacterDataAsync" && method.ReturnType == typeof(Task));
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
        var unpatchFailed = false;

        if (harmony != null && harmonyUnpatchMethod != null && prefixFactoryMethod != null)
        {
            foreach (var method in patchedMethods)
            {
                try
                {
                    using var reflectionScope = AssemblyLoadContext.EnterContextualReflection(method.Module.Assembly);
                    harmonyUnpatchMethod.Invoke(harmony, [method, prefixFactoryMethod]);

                    lock (PrefixFactoryLock)
                        PrefixFactoryMethods.Remove(method);
                }
                catch (Exception ex)
                {
                    unpatchFailed = true;
                    log.Error(ex, "Failed to remove a SyncBridge Harmony patch during shutdown.");
                }
            }
        }

        patchedMethods.Clear();
        patchPrefixes.Clear();
        diagnosticReason = "disposed";

        lock (OwnershipLock)
            playerSyncOwned.Clear();

        AppContext.SetData(OwnershipDataKey, null);
        AppContext.SetData(CounterDataKey, null);
        AppContext.SetData(ObservedCounterDataKey, null);

        if (unpatchFailed)
            log.Warning("LightlessSuppressor disposed fail-open; one or more patches could not be removed.");
        else
            log.Debug("LightlessSuppressor disposed.");
    }
}
