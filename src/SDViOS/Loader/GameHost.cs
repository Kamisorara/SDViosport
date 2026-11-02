using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Foundation;
using Microsoft.Xna.Framework;
using UIKit;
using SDViOS.Diagnostics;
using SDViOS.Input;
using SDViOS.Compatibility;
using MonoMod.RuntimeDetour;

// Global-namespace HarmonySharedState to satisfy 0Harmony.dll Type.GetType("HarmonySharedState", false).
// If this type is present, Harmony completely skips dynamic module emission via Mono.Cecil and Assembly.Load(byte[]),
// which otherwise crashes on iOS AOT / W^X environment.
public static class HarmonySharedState
{
    public static int version = 102;
    public static System.Collections.Generic.Dictionary<System.Reflection.MethodBase, byte[]> state = new();
    public static System.Collections.Generic.Dictionary<System.Reflection.MethodInfo, System.Reflection.MethodBase> originals = new();
}

// Custom safe IDetourRuntimePlatform to satisfy MonoMod.RuntimeDetour.DetourHelper.Runtime.
// MonoMod's default platforms run _HookSelftest() and RuntimeHelpers.PrepareMethod() which crash
// on iOS AOT/W^X. Providing this safe platform bypasses self-testing and allows Harmony to run stably.
public class SafeDetourRuntimePlatform : IDetourRuntimePlatform
{
    public bool OnMethodCompiledWillBeCalled => false;
    public event OnMethodCompiledEvent? OnMethodCompiled { add { } remove { } }

    public MethodBase GetIdentifiable(MethodBase method) => method;
    public IntPtr GetNativeStart(MethodBase method) => method.MethodHandle.GetFunctionPointer();
    public MethodInfo CreateCopy(MethodBase method) => (method as MethodInfo) ?? throw new NotSupportedException();
    public bool TryCreateCopy(MethodBase method, out MethodInfo dm)
    {
        dm = (method as MethodInfo)!;
        return dm != null;
    }
    public void Pin(MethodBase method) { }
    public void Unpin(MethodBase method) { }
    public MethodBase GetDetourTarget(MethodBase from, MethodBase to) => to;
    public uint TryMemAllocScratchCloseTo(IntPtr target, out IntPtr allocated, int size)
    {
        allocated = IntPtr.Zero;
        return 0;
    }
}

// Custom safe IDetourNativePlatform to satisfy MonoMod.RuntimeDetour.DetourHelper.Native.
// Bypasses MonoMod's dangerous ARM64 shellcode in GC heap which violates iOS W^X / codesigning.
// Uses Apple's official sys_icache_invalidate from libSystem.dylib.
public class SafeDetourNativePlatform : IDetourNativePlatform
{
    [System.Runtime.InteropServices.DllImport("libSystem.dylib", EntryPoint = "sys_icache_invalidate")]
    private static extern void sys_icache_invalidate(IntPtr start, nuint len);

    [System.Runtime.InteropServices.DllImport("libSystem.dylib", EntryPoint = "mprotect")]
    private static extern int mprotect(IntPtr addr, nuint len, int prot);

    private readonly IDetourNativePlatform _innerArm = new MonoMod.RuntimeDetour.Platforms.DetourNativeARMPlatform();
    private static readonly long _pageSize = Environment.SystemPageSize;

    public SafeDetourNativePlatform()
    {
        if (_innerArm is MonoMod.RuntimeDetour.Platforms.DetourNativeARMPlatform arm)
        {
            arm.ShouldFlushICache = false;
        }
    }

    public NativeDetourData Create(IntPtr from, IntPtr to, byte? type) => _innerArm.Create(from, to, type);
    public void Free(NativeDetourData data) => _innerArm.Free(data);
    public void Apply(NativeDetourData data) => _innerArm.Apply(data);
    public void Copy(IntPtr src, IntPtr dst, byte type) => _innerArm.Copy(src, dst, type);

    public void MakeWritable(IntPtr addr, uint size) => SetMemPerms(addr, size, 7);
    public void MakeExecutable(IntPtr addr, uint size) => SetMemPerms(addr, size, 5);
    public void MakeReadWriteExecutable(IntPtr addr, uint size) => SetMemPerms(addr, size, 7);

    public void FlushICache(IntPtr addr, uint size)
    {
        try
        {
            sys_icache_invalidate(addr, (nuint)size);
        }
        catch (Exception ex)
        {
            EngineLogger.LogWarning($"[SafeDetourNativePlatform] sys_icache_invalidate warning: {ex.Message}");
        }
    }

    public IntPtr MemAlloc(uint size) => _innerArm.MemAlloc(size);
    public void MemFree(IntPtr ptr) => _innerArm.MemFree(ptr);

    private void SetMemPerms(IntPtr addr, uint size, int prot)
    {
        try
        {
            long start = addr.ToInt64() & ~(_pageSize - 1);
            long end = (addr.ToInt64() + size + _pageSize - 1) & ~(_pageSize - 1);
            mprotect((IntPtr)start, (nuint)(end - start), prot);
        }
        catch (Exception ex)
        {
            EngineLogger.LogWarning($"[SafeDetourNativePlatform] SetMemPerms error: {ex.Message}");
        }
    }
}

namespace SDViOS.Loader
{
    public static class GameHost
    {
        public static string DocumentsDir { get; private set; } = string.Empty;
        public static string GameRootDir { get; private set; } = string.Empty;
        public static string ContentDir { get; private set; } = string.Empty;
        public static string ModsDir { get; private set; } = string.Empty;
        public static string SavesDir { get; private set; } = string.Empty;
        public static string LogsDir { get; private set; } = string.Empty;
        public static string BundleDir { get; private set; } = string.Empty;
        public static object? SMAPICoreInstance { get; set; }
        public static bool DisableBuiltInTouchInput { get; set; }
        private static CoreAnimation.CADisplayLink? _engineDisplayLink;
        private static int _tickLogCount = 0;
        private static UIView? _activeGameView;
        private static UIViewController? _activeGameVC;
        private static MethodInfo? _makeCurrentMethod;
        private static MethodInfo? _presentMethod;
        private static MethodInfo? _threadingRunMethod;
        private static MethodInfo? _platformTickMethod;
        private static Action? _cachedMakeCurrent;
        private static UIView? _cachedMakeCurrentView;
        private static Action? _cachedPlatformTick;
        private static Action? _cachedThreadingRun;
        private static FieldInfo? _runnerIsActiveField;
        private static FieldInfo? _gameRunnerInstanceField;
        private static FieldInfo? _gameRunnerInstancesListField;
        private static int _directTickCount = 0;
        private static FieldInfo? _game1TicksField;
        private static bool _windowSizeSynchronized = false;
        private static bool _menuLayoutSynchronized = false;
        private static bool _touchOverlayAttached = false;
        private static bool _viewHierarchyAudited = false;

        public static void InitializeFileSystem()
        {
            DocumentsDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            BundleDir = NSBundle.MainBundle.BundlePath;

            // Check if game files are placed directly in Documents/, Documents/StardewValley/, or in Bundle
            string directDll = Path.Combine(DocumentsDir, "Stardew Valley.dll");
            string subDirDll = Path.Combine(DocumentsDir, "StardewValley", "Stardew Valley.dll");
            string bundleDll = Path.Combine(BundleDir, "Stardew Valley.dll");

            if (File.Exists(directDll))
            {
                GameRootDir = DocumentsDir;
            }
            else if (File.Exists(subDirDll))
            {
                GameRootDir = Path.Combine(DocumentsDir, "StardewValley");
            }
            else if (File.Exists(bundleDll))
            {
                // Bundled inside the IPA!
                GameRootDir = DocumentsDir; // Keep user data in Documents
            }
            else
            {
                GameRootDir = DocumentsDir;
            }

            ContentDir = Path.Combine(GameRootDir, "Content");
            ModsDir = Path.Combine(DocumentsDir, "Mods");
            SavesDir = Path.Combine(DocumentsDir, "Saves");
            LogsDir = Path.Combine(DocumentsDir, "ErrorLogs");

            Directory.CreateDirectory(GameRootDir);
            Directory.CreateDirectory(ModsDir);
            Directory.CreateDirectory(SavesDir);
            Directory.CreateDirectory(LogsDir);

            EngineLogger.Initialize(LogsDir);
            EngineLogger.Log($"[Engine] Initialized file system.");
            EngineLogger.Log($"BundleDir: {BundleDir}");
            EngineLogger.Log($"DocumentsDir: {DocumentsDir}");
            EngineLogger.Log($"ModsDir: {ModsDir}");

            // If bundled Mods directory exists and user Documents/Mods is empty, copy default bundled mods (only a few KB)
            string bundledMods = Path.Combine(BundleDir, "Mods");
            if (Directory.Exists(bundledMods) && Directory.GetFileSystemEntries(ModsDir).Length == 0)
            {
                try
                {
                    SyncBundledDirectory(bundledMods, ModsDir);
                    EngineLogger.Log("[Engine] Seeded default mods to Documents/Mods.");
                }
                catch (Exception ex)
                {
                    EngineLogger.LogWarning($"[Engine] Failed to seed default mods: {ex.Message}");
                }
            }

            // Always ensure built-in SDViOSTouchControls mod is deployed
            EnsureDefaultModsDeployed();

            // Clean up any stale BCL DLL that may have been placed into Documents in older builds
            string staleCoreLib = Path.Combine(GameRootDir, "System.Private.CoreLib.dll");
            if (File.Exists(staleCoreLib))
            {
                try { File.Delete(staleCoreLib); } catch { }
            }

            Environment.SetEnvironmentVariable("APPDATA", DocumentsDir);
            Environment.SetEnvironmentVariable("STARDEW_VALLEY_MODS_PATH", ModsDir);
            Environment.SetEnvironmentVariable("SMAPI_MODS_PATH", ModsDir);
            Environment.SetEnvironmentVariable("SMAPI_NO_TERMINAL", "1");
            Environment.SetEnvironmentVariable("MONO_STRICT_MS_COMPLIANT", "yes");
            Directory.SetCurrentDirectory(GameRootDir);

            try
            {
                var locProp = typeof(Microsoft.Xna.Framework.TitleContainer).GetProperty("Location", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                locProp?.SetValue(null, GameRootDir);
                var val = locProp?.GetValue(null);
                EngineLogger.Log($"[GameHost] Configured TitleContainer.Location = '{val}'");
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[GameHost] Could not set TitleContainer.Location: {ex.Message}");
            }

            SetupAssemblyResolver(BundleDir);
        }

        public static void EnsureDefaultModsDeployed()
        {
            try
            {
                if (string.IsNullOrEmpty(ModsDir)) return;
                string targetDir = Path.Combine(ModsDir, "SDViOSTouchControls");
                Directory.CreateDirectory(targetDir);

                string targetDll = Path.Combine(targetDir, "SDViOSTouchControls.dll");
                string targetModDll = Path.Combine(targetDir, "SDViOSTouchControlsMod.dll");
                string targetManifest = Path.Combine(targetDir, "manifest.json");
                string hashMarkerFile = Path.Combine(targetDir, ".embedded_sha256");

                bool needDeploy = !File.Exists(targetDll) || !File.Exists(targetModDll);
                var currentAsm = Assembly.GetExecutingAssembly();
                using (var dllStream = currentAsm.GetManifestResourceStream("SDViOS.Resources.Mods.SDViOSTouchControls.SDViOSTouchControls.dll"))
                {
                    if (dllStream != null)
                    {
                        using var ms = new MemoryStream();
                        dllStream.CopyTo(ms);
                        byte[] dllBytes = ms.ToArray();
                        string embeddedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(dllBytes));
                        string prevEmbeddedHash = File.Exists(hashMarkerFile) ? File.ReadAllText(hashMarkerFile).Trim() : "";

                        // Deploy if target DLL is missing OR the IPA's embedded resource hash changed (new IPA build installed)
                        needDeploy = needDeploy || !string.Equals(embeddedHash, prevEmbeddedHash, StringComparison.OrdinalIgnoreCase);
                        if (needDeploy)
                        {
                            File.WriteAllBytes(targetDll, dllBytes);
                            File.WriteAllBytes(targetModDll, dllBytes);
                            File.WriteAllText(hashMarkerFile, embeddedHash);
                            EngineLogger.Log($"[GameHost] Deployed embedded SDViOSTouchControls ({dllBytes.Length} bytes, sha256={embeddedHash[..12]}).");
                        }
                    }
                }

                using (var mfStream = currentAsm.GetManifestResourceStream("SDViOS.Resources.Mods.SDViOSTouchControls.manifest.json"))
                {
                    if (mfStream != null && (needDeploy || !File.Exists(targetManifest)))
                    {
                        using var fs = new FileStream(targetManifest, FileMode.Create, FileAccess.Write, FileShare.None);
                        mfStream.CopyTo(fs);
                        EngineLogger.Log("[GameHost] Deployed manifest.json to Documents/Mods/SDViOSTouchControls.");
                    }
                }
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[GameHost] Could not deploy embedded default mods: {ex.Message}");
            }
        }

        private static void SyncBundledDirectory(string sourceDir, string targetDir, bool forceOverwrite = false)
        {
            if (!Directory.Exists(sourceDir)) return;
            Directory.CreateDirectory(targetDir);

            foreach (string file in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(sourceDir, file);
                string dest = Path.Combine(targetDir, relative);
                string? destFolder = Path.GetDirectoryName(dest);
                if (destFolder != null && !Directory.Exists(destFolder))
                {
                    Directory.CreateDirectory(destFolder);
                }

                if (forceOverwrite || !File.Exists(dest) || File.GetLastWriteTimeUtc(file) > File.GetLastWriteTimeUtc(dest))
                {
                    try { File.Copy(file, dest, true); } catch { }
                }
            }
        }

        private static void SetupAssemblyResolver(string bundlePath)
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string asmName = new AssemblyName(args.Name).Name + ".dll";

                // Never resolve core runtime libraries from user directories
                if (asmName.Equals("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase) ||
                    asmName.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                string p1 = Path.Combine(bundlePath, asmName);
                if (File.Exists(p1)) return Assembly.LoadFrom(p1);

                string p2 = Path.Combine(bundlePath, "smapi-internal", asmName);
                if (File.Exists(p2)) return Assembly.LoadFrom(p2);

                string p3 = Path.Combine(GameRootDir, asmName);
                if (File.Exists(p3)) return Assembly.LoadFrom(p3);

                string p4 = Path.Combine(GameRootDir, "smapi-internal", asmName);
                if (File.Exists(p4)) return Assembly.LoadFrom(p4);

                string p5 = Path.Combine(DocumentsDir, asmName);
                if (File.Exists(p5)) return Assembly.LoadFrom(p5);

                EngineLogger.LogWarning($"[AssemblyResolve] Unresolved assembly: {args.Name}");
                return null;
            };

            AppDomain.CurrentDomain.TypeResolve += (sender, args) =>
            {
                if (args.Name == "System.Private.CoreLib" || args.Name.StartsWith("System.Private.CoreLib,"))
                {
                    return typeof(object).Assembly;
                }

                if (args.Name == "Mono.Runtime" || args.Name.StartsWith("Mono.Runtime,"))
                {
                    return typeof(GameHost).Assembly;
                }

                if (args.Name == "HarmonySharedState" || args.Name.StartsWith("HarmonySharedState,"))
                {
                    EngineLogger.Log($"[TypeResolve] Resolved '{args.Name}' -> returning typeof(HarmonySharedState).Assembly ({typeof(HarmonySharedState).Assembly.GetName().Name})");
                    return typeof(HarmonySharedState).Assembly;
                }

                // If a type failed to resolve because of a trimmed or forwarded assembly,
                // scan loaded assemblies (like System.Private.Xml) for the type.
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var t = asm.GetType(args.Name, false);
                        if (t != null)
                        {
                            EngineLogger.Log($"[TypeResolve] Resolved '{args.Name}' from assembly '{asm.GetName().Name}'");
                            return asm;
                        }
                    }
                    catch { }
                }

                EngineLogger.LogWarning($"[TypeResolve] Unresolved type: {args.Name}");
                return null;
            };

            try
            {
                MonoMod.RuntimeDetour.DetourHelper.Runtime = new SafeDetourRuntimePlatform();
                MonoMod.RuntimeDetour.DetourHelper.Native = new SafeDetourNativePlatform();
                EngineLogger.Log("[GameHost] Pre-initialized DetourHelper.Runtime and Native to Safe platforms.");
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[GameHost] Failed to pre-initialize DetourHelper: {ex.Message}");
            }
        }

        public static void AttachTouchOverlay(Game game)
        {
            if (game != null)
            {
                EngineLogger.Log("Attaching TouchOverlay to Game instance.");
                var overlay = new TouchOverlay(game);
                game.Components.Add(overlay);
            }
        }

        public static bool TryFindGameBinary(out string sdvPath, out string smapiPath)
        {
            sdvPath = string.Empty;
            smapiPath = string.Empty;

            string[] searchPaths = new[] { BundleDir, GameRootDir, DocumentsDir, Path.Combine(DocumentsDir, "StardewValley") };
            foreach (var dir in searchPaths)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string checkSdv = Path.Combine(dir, "Stardew Valley.dll");
                if (File.Exists(checkSdv) && string.IsNullOrEmpty(sdvPath))
                {
                    sdvPath = checkSdv;
                }
                string checkSmapi = Path.Combine(dir, "StardewModdingAPI.dll");
                if (File.Exists(checkSmapi) && string.IsNullOrEmpty(smapiPath))
                {
                    smapiPath = checkSmapi;
                }
            }

            return !string.IsNullOrEmpty(sdvPath);
        }

        public static void Launch(string[] args)
        {
            AppDelegate.ConfigureAudioSession();
            PreInitAudio();

            if (!TryFindGameBinary(out string sdvPath, out string smapiPath))
            {
                EngineLogger.LogError($"Stardew Valley.dll not found in any search path!");
                return;
            }

            bool forceVanilla = File.Exists(Path.Combine(GameRootDir, "force_vanilla.txt")) ||
                                File.Exists(Path.Combine(DocumentsDir, "force_vanilla.txt"));

            if (forceVanilla)
            {
                EngineLogger.Log("force_vanilla.txt detected. Launching Pure Vanilla.");
                LaunchVanilla(sdvPath, args);
                return;
            }

            if (!string.IsNullOrEmpty(smapiPath) && File.Exists(smapiPath))
            {
                EngineLogger.Log($"Found StardewModdingAPI.dll at '{smapiPath}'. Bootstrapping SMAPI...");
                try
                {
                    string smapiInternalDir = Path.Combine(DocumentsDir, "smapi-internal");
                    try { Directory.CreateDirectory(smapiInternalDir); } catch { }

                    string bundledInternal = Path.Combine(BundleDir, "smapi-internal");
                    if (Directory.Exists(bundledInternal))
                    {
                        try
                        {
                            SyncBundledDirectory(bundledInternal, smapiInternalDir, forceOverwrite: true);
                            EngineLogger.Log("[GameHost] Synced bundled smapi-internal to Documents/smapi-internal.");
                        }
                        catch (Exception ex)
                        {
                            EngineLogger.LogWarning($"[GameHost] Could not sync bundled smapi-internal: {ex.Message}");
                        }
                    }

                    // Auto-patch 0Harmony.dll in both Documents and Bundle if needed
                    RuntimeHarmonyPatcher.EnsureHarmonyPatched(Path.Combine(smapiInternalDir, "0Harmony.dll"));
                    RuntimeHarmonyPatcher.EnsureHarmonyPatched(Path.Combine(bundledInternal, "0Harmony.dll"));

                    // Auto-patch SMAPI and Stardew Valley dlls if in writable location to prevent premature disposal / kill
                    sdvPath = RuntimeSmapiPatcher.EnsureGameRunnerPatched(sdvPath);
                    smapiPath = RuntimeSmapiPatcher.EnsureSmapiPatched(smapiPath);
                    RuntimeSmapiPatcher.EnsureModsPatched(ModsDir);

                    Environment.SetEnvironmentVariable("SMAPI_INTERNAL_PATH", smapiInternalDir);
                    Environment.SetEnvironmentVariable("SMAPI_MODS_PATH", ModsDir);
                    Environment.SetEnvironmentVariable("SMAPI_NO_TERMINAL", "1");
                    Environment.SetEnvironmentVariable("STARDEW_VALLEY_MODS_PATH", ModsDir);

                    string[] smapiArgs = new string[] { "--no-terminal", "--mods-path", ModsDir };
                    var smapiAsm = Assembly.LoadFrom(smapiPath);
                    System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

                    try
                    {
                        MonoMod.RuntimeDetour.DetourHelper.Runtime = new SafeDetourRuntimePlatform();
                        MonoMod.RuntimeDetour.DetourHelper.Native = new SafeDetourNativePlatform();
                        EngineLogger.Log("[GameHost] Confirmed DetourHelper.Runtime and Native set to Safe platforms.");
                    }
                    catch (Exception ex)
                    {
                        EngineLogger.LogWarning($"[GameHost] Could not confirm DetourHelper: {ex.Message}");
                    }

                    // Redirect SMAPI Constants.InternalFilesPath and Constants.LogDir to Documents to prevent sandbox violations
                    try
                    {
                        var constType = smapiAsm.GetType("StardewModdingAPI.Constants");
                        if (constType != null)
                        {
                            for (Type? t = constType; t != null; t = t.BaseType)
                            {
                                var ipf = t.GetField("InternalFilesPath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                                       ?? t.GetField("<InternalFilesPath>k__BackingField", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                                       ?? t.GetField("<InternalPath>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)
                                       ?? t.GetField("_internalPath", BindingFlags.NonPublic | BindingFlags.Static)
                                       ?? t.GetField("InternalPath", BindingFlags.NonPublic | BindingFlags.Static);
                                if (ipf != null)
                                {
                                    ipf.SetValue(null, smapiInternalDir);
                                    EngineLogger.Log($"[GameHost] Overrode Constants.{ipf.Name} = {smapiInternalDir}");
                                }
                            }

                            if (!string.IsNullOrEmpty(LogsDir))
                            {
                                for (Type? t = constType; t != null; t = t.BaseType)
                                {
                                    var lpf = t.GetField("<LogDir>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)
                                           ?? t.GetField("_logDir", BindingFlags.NonPublic | BindingFlags.Static)
                                           ?? t.GetField("LogDir", BindingFlags.NonPublic | BindingFlags.Static);
                                    if (lpf != null)
                                    {
                                        lpf.SetValue(null, LogsDir);
                                        EngineLogger.Log($"[GameHost] Overrode Constants.LogDir = {LogsDir}");
                                    }
                                }
                            }

                            // Override Platform and TargetPlatform to Windows (3) to prevent SMAPI
                            // from halting with "Oops! You're running Windows, but this version of SMAPI is for Linux or macOS."
                            // when bundled Windows SMAPI runs in iOS Unix environment.
                            for (Type? t = constType; t != null; t = t.BaseType)
                            {
                                var pf = t.GetField("<Platform>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)
                                      ?? t.GetField("_platform", BindingFlags.NonPublic | BindingFlags.Static)
                                      ?? t.GetField("Platform", BindingFlags.NonPublic | BindingFlags.Static);
                                if (pf != null)
                                {
                                    var val = Enum.ToObject(pf.FieldType, 3);
                                    pf.SetValue(null, val);
                                    EngineLogger.Log($"[GameHost] Overrode Constants.Platform = {val} ({pf.FieldType.FullName})");
                                }

                                var tpf = t.GetField("<TargetPlatform>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)
                                       ?? t.GetField("_targetPlatform", BindingFlags.NonPublic | BindingFlags.Static)
                                       ?? t.GetField("TargetPlatform", BindingFlags.NonPublic | BindingFlags.Static);
                                if (tpf != null)
                                {
                                    var val = Enum.ToObject(tpf.FieldType, 3);
                                    tpf.SetValue(null, val);
                                    EngineLogger.Log($"[GameHost] Overrode Constants.TargetPlatform = {val} ({tpf.FieldType.FullName})");
                                }
                            }
                        }

                        var earlyConstType = smapiAsm.GetType("StardewModdingAPI.EarlyConstants");
                        if (earlyConstType != null)
                        {
                            for (Type? t = earlyConstType; t != null; t = t.BaseType)
                            {
                                var eipf = t.GetField("InternalFilesPath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                                        ?? t.GetField("<InternalFilesPath>k__BackingField", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                                if (eipf != null)
                                {
                                    eipf.SetValue(null, smapiInternalDir);
                                    EngineLogger.Log($"[GameHost] Overrode EarlyConstants.{eipf.Name} = {smapiInternalDir}");
                                }

                                var epf = t.GetField("<Platform>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)
                                       ?? t.GetField("_platform", BindingFlags.NonPublic | BindingFlags.Static)
                                       ?? t.GetField("Platform", BindingFlags.NonPublic | BindingFlags.Static);
                                if (epf != null)
                                {
                                    var val = Enum.ToObject(epf.FieldType, 3);
                                    epf.SetValue(null, val);
                                    EngineLogger.Log($"[GameHost] Overrode EarlyConstants.Platform = {val} ({epf.FieldType.FullName})");
                                }
                            }
                        }

                        // Ensure SMAPI config uses ConsoleColorScheme = DarkBackground.
                        // When Platform is Windows, AutoDetect calls Console.BackgroundColor which throws PlatformNotSupportedException on iOS.
                        string[] internalDirs = new string[]
                        {
                            smapiInternalDir,
                            Path.Combine(DocumentsDir, "smapi-internal"),
                            Path.Combine(GameRootDir, "smapi-internal"),
                            Path.Combine(BundleDir, "smapi-internal")
                        };
                        foreach (var dir in internalDirs)
                        {
                            if (string.IsNullOrEmpty(dir)) continue;
                            try
                            {
                                Directory.CreateDirectory(dir);
                                string userConfigPath = Path.Combine(dir, "config.user.json");
                                File.WriteAllText(userConfigPath, "{\"ConsoleColorScheme\":\"DarkBackground\",\"ListenForConsoleInput\":false,\"CheckForUpdates\":false,\"CheckForBlacklistUpdates\":false,\"UseCaseInsensitivePaths\":true}");

                                string configPath = Path.Combine(dir, "config.json");
                                if (File.Exists(configPath))
                                {
                                    string cfg = File.ReadAllText(configPath);
                                    bool modified = false;
                                    if (cfg.Contains("\"AutoDetect\""))
                                    {
                                        cfg = cfg.Replace("\"AutoDetect\"", "\"DarkBackground\"");
                                        modified = true;
                                    }
                                    if (cfg.Contains("\"UseCaseInsensitivePaths\": false"))
                                    {
                                        cfg = cfg.Replace("\"UseCaseInsensitivePaths\": false", "\"UseCaseInsensitivePaths\": true");
                                        modified = true;
                                    }
                                    else if (cfg.Contains("\"UseCaseInsensitivePaths\": null"))
                                    {
                                        cfg = cfg.Replace("\"UseCaseInsensitivePaths\": null", "\"UseCaseInsensitivePaths\": true");
                                        modified = true;
                                    }
                                    if (modified)
                                    {
                                        File.WriteAllText(configPath, cfg);
                                    }
                                }
                            }
                            catch { }
                        }

                        if (!string.IsNullOrEmpty(ModsDir))
                        {
                            try
                            {
                                Directory.CreateDirectory(ModsDir);
                                string smapiConfigPath = Path.Combine(ModsDir, "SMAPI-config.json");
                                File.WriteAllText(smapiConfigPath, "{\"ConsoleColorScheme\":\"DarkBackground\",\"ListenForConsoleInput\":false,\"CheckForUpdates\":false,\"CheckForBlacklistUpdates\":false,\"UseCaseInsensitivePaths\":true}");
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        EngineLogger.LogWarning($"[GameHost] Constants redirect warning: {ex.Message}");
                    }



                    // Register SMAPI internal assembly resolver if available
                    try
                    {
                        var progType = smapiAsm.GetType("StardewModdingAPI.Program");
                        var resolveMethod = progType?.GetMethod("CurrentDomain_AssemblyResolve", BindingFlags.Static | BindingFlags.NonPublic);
                        if (resolveMethod != null)
                        {
                            var handler = (ResolveEventHandler)Delegate.CreateDelegate(typeof(ResolveEventHandler), resolveMethod);
                            AppDomain.CurrentDomain.AssemblyResolve += handler;
                            EngineLogger.Log("[GameHost] Registered SMAPI CurrentDomain_AssemblyResolve.");
                        }
                    }
                    catch (Exception ex)
                    {
                        EngineLogger.LogWarning($"[GameHost] Could not hook SMAPI resolver: {ex.Message}");
                    }

                    // On iOS, MonoGame Game.Run() is asynchronous (DefaultRunBehavior = Asynchronous).
                    // When running via Program.Main(), SCore is instantiated inside a `using (var core = new SCore(...))`
                    // block which calls core.Dispose() as soon as Game.Run() returns, disposing the entire Game and GamePlatform!
                    // By instantiating SCore directly and keeping a static reference, we ensure SCore and Game remain alive forever.
                    var scoreType = smapiAsm.GetType("StardewModdingAPI.Framework.SCore");
                    if (scoreType != null)
                    {
                        EngineLogger.Log("[GameHost] Instantiating persistent SCore (non-disposing)...");
                        // public SCore(string modsPath, bool writeToConsole, bool? overrideDeveloperMode)
                        var core = Activator.CreateInstance(scoreType, new object?[] { ModsDir, false, (bool?)false });
                        SMAPICoreInstance = core;

                        // 1. Configure Settings before RunInteractively to prevent background console thread and update checks
                        try
                        {
                            var settingsField = scoreType.GetField("Settings", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            var settings = settingsField?.GetValue(core);
                            if (settings != null)
                            {
                                var st = settings.GetType();
                                st.GetProperty("ListenForConsoleInput", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(settings, false);
                                st.GetProperty("CheckForUpdates", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(settings, false);
                                st.GetProperty("CheckForBlacklistUpdates", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(settings, false);
                                st.GetProperty("UseCaseInsensitivePaths", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(settings, true);
                                EngineLogger.Log("[GameHost] Configured SCore.Settings: ListenForConsoleInput=false, CheckForUpdates=false, UseCaseInsensitivePaths=true.");
                            }
                        }
                        catch (Exception ex)
                        {
                            EngineLogger.LogWarning($"[GameHost] Failed to configure SCore.Settings: {ex.Message}");
                        }

                        // 2. Install NonDisposingStreamWriter on SMAPI LogFileManager before launch
                        ReviveSMAPILogFile(core);

                        // Pre-set Program._sdk to NullSDKHelper to prevent Steam/Galaxy native crashes during initialization
                        try
                        {
                            var sdvAsm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Stardew Valley");
                            var progType = sdvAsm?.GetType("StardewValley.Program");
                            var nullSdkType = sdvAsm?.GetType("StardewValley.SDKs.NullSDKHelper");
                            if (progType != null && nullSdkType != null)
                            {
                                var sdkField = progType.GetField("_sdk", BindingFlags.NonPublic | BindingFlags.Static);
                                if (sdkField != null)
                                {
                                    var nullSdk = Activator.CreateInstance(nullSdkType);
                                    sdkField.SetValue(null, nullSdk);
                                    EngineLogger.Log("[GameHost] Pre-initialized Program._sdk to NullSDKHelper.");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            EngineLogger.LogWarning($"[GameHost] Failed to pre-set NullSDKHelper: {ex.Message}");
                        }

                        try
                        {
                            var runMethod = scoreType.GetMethod("RunInteractively", BindingFlags.Public | BindingFlags.Instance);
                            runMethod?.Invoke(core, null);
                            EngineLogger.Log("[GameHost] SMAPI SCore.RunInteractively launched successfully.");
                        }
                        catch (TargetInvocationException tie)
                        {
                            var inner = tie.InnerException ?? tie;
                            EngineLogger.LogError($"[GameHost] SCore.RunInteractively caught TargetInvocationException: {inner.GetType().FullName}: {inner.Message}\n{inner.StackTrace}");
                        }
                        catch (Exception ex)
                        {
                            EngineLogger.LogError($"[GameHost] SCore.RunInteractively exception: {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
                        }

                        // 3. Post-RunInteractively: Neutralize SGameRunner.OnGameExiting and revive SCore state
                        try
                        {
                            var gameField = scoreType.GetField("Game", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            var game = gameField?.GetValue(core);
                            if (game != null)
                            {
                                var onExitingField = game.GetType().GetField("OnGameExiting", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                                if (onExitingField != null)
                                {
                                    Action noopExit = () =>
                                    {
                                        EngineLogger.Log("[GameHost] SGameRunner.OnGameExiting intercepted and suppressed.");
                                    };
                                    onExitingField.SetValue(game, noopExit);
                                    EngineLogger.Log("[GameHost] Replaced SGameRunner.OnGameExiting with no-op handler.");
                                }
                            }

                            // Check and log SCore fields
                            var exitStateField = scoreType.GetField("ExitState", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            var exitVal = exitStateField?.GetValue(core);
                            var isInitField = scoreType.GetField("IsInitialized", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            var initVal = isInitField?.GetValue(core);
                            EngineLogger.Log($"[GameHost] Post-RunInteractively SCore: ExitState={exitVal}, IsInitialized={initVal}");

                            // If ExitState was set to Crash or GameExit, reset it to None (0) so game loop and asset interception aren't aborted!
                            if (exitStateField != null && exitVal != null && (int)exitVal != 0)
                            {
                                exitStateField.SetValue(core, 0); // 0 = ExitState.None
                                EngineLogger.Log($"[GameHost] Reset SCore.ExitState from {exitVal} to ExitState.None (0).");
                            }

                            var isDispField = scoreType.GetField("IsDisposed", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            isDispField?.SetValue(core, false);

                            var isRunningField = scoreType.GetField("IsGameRunning", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            isRunningField?.SetValue(core, true);

                            // If not initialized, trigger InitializeBeforeFirstAssetLoaded
                            if (isInitField != null && false.Equals(initVal))
                            {
                                var initMethod = scoreType.GetMethod("InitializeBeforeFirstAssetLoaded", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                                if (initMethod != null)
                                {
                                    try
                                    {
                                        initMethod.Invoke(core, null);
                                        EngineLogger.Log($"[GameHost] Invoked SCore.InitializeBeforeFirstAssetLoaded(). IsInitialized is now {isInitField.GetValue(core)}.");
                                    }
                                    catch (Exception initEx)
                                    {
                                        EngineLogger.LogWarning($"[GameHost] InitializeBeforeFirstAssetLoaded invocation warning: {initEx.Message}");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            EngineLogger.LogWarning($"[GameHost] Post-RunInteractively patch warning: {ex.Message}");
                        }

                        ReviveSMAPILogFile(core);
                    }
                    else
                    {
                        EngineLogger.LogWarning("[GameHost] SCore type not found, falling back to EntryPoint...");
                        var entry = smapiAsm.EntryPoint;
                        if (entry != null)
                        {
                            object?[] invokeArgs = entry.GetParameters().Length > 0 ? new object?[] { smapiArgs } : Array.Empty<object>();
                            entry.Invoke(null, invokeArgs);
                        }
                    }

                    DisableBuiltInTouchInput = !string.IsNullOrEmpty(ModsDir) &&
                        File.Exists(Path.Combine(ModsDir, "SDViOSTouchControls", "SDViOSTouchControls.dll"));

                    LinkGameWindowToScene();
                    if (!DisableBuiltInTouchInput)
                    {
                        AttachTouchOverlayToGameRunner();
                    }
                    UIApplication.SharedApplication.BeginInvokeOnMainThread(LinkGameWindowToScene);
                    NSTimer.CreateScheduledTimer(0.25, false, _ => LinkGameWindowToScene());
                    NSTimer.CreateScheduledTimer(1.0, false, _ => LinkGameWindowToScene());
                    NSTimer.CreateScheduledTimer(2.5, false, _ => LinkGameWindowToScene());
                    NSTimer.CreateScheduledTimer(5.0, false, _ => LinkGameWindowToScene());
                    return;
                }
                catch (Exception ex)
                {
                    EngineLogger.LogFatal("SMAPI Launch", ex);
                }
            }

            LaunchVanilla(sdvPath, args);
        }

        public static void LaunchVanilla(string sdvPath, string[] args)
        {
            EngineLogger.Log("Launching Pure Vanilla Stardew Valley from: " + sdvPath);
            try
            {
                sdvPath = RuntimeSmapiPatcher.EnsureGameRunnerPatched(sdvPath);
                var sdvAsm = Assembly.LoadFrom(sdvPath);
                var runnerType = sdvAsm.GetType("StardewValley.GameRunner");
                if (runnerType != null)
                {
                    EngineLogger.Log("Instantiating StardewValley.GameRunner with TouchOverlay...");
                    System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
                    var runner = (Game)Activator.CreateInstance(runnerType)!;

                    var instanceField = runnerType.GetField("instance", BindingFlags.Static | BindingFlags.Public);
                    instanceField?.SetValue(null, runner);

                    AttachTouchOverlay(runner);
                    runner.Run();

                    LinkGameWindowToScene();
                    UIApplication.SharedApplication.BeginInvokeOnMainThread(LinkGameWindowToScene);
                    NSTimer.CreateScheduledTimer(0.25, false, _ => LinkGameWindowToScene());
                    NSTimer.CreateScheduledTimer(1.0, false, _ => LinkGameWindowToScene());
                }
                else
                {
                    var entry = sdvAsm.EntryPoint;
                    object?[] invokeArgs = entry != null && entry.GetParameters().Length > 0 ? new object?[] { args } : Array.Empty<object>();
                    entry?.Invoke(null, invokeArgs);
                    LinkGameWindowToScene();
                    UIApplication.SharedApplication.BeginInvokeOnMainThread(LinkGameWindowToScene);
                }
            }
            catch (Exception ex)
            {
                EngineLogger.LogFatal("Vanilla Launch", ex);
                throw;
            }
        }

        public static UIWindowScene? GetActiveWindowScene()
        {
            try
            {
                var scenes = UIApplication.SharedApplication.ConnectedScenes;
                if (scenes == null) return null;

                foreach (var scene in scenes)
                {
                    if (scene is UIWindowScene ws && ws.ActivationState == UISceneActivationState.ForegroundActive)
                        return ws;
                }

                foreach (var scene in scenes)
                {
                    if (scene is UIWindowScene ws && ws.ActivationState == UISceneActivationState.ForegroundInactive)
                        return ws;
                }

                foreach (var scene in scenes)
                {
                    if (scene is UIWindowScene ws)
                        return ws;
                }
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[GameHost] Error getting UIWindowScene: {ex.Message}");
            }
            return null;
        }

        public static void LinkGameWindowToScene()
        {
            try
            {
                EngineLogger.Log("[GameHost] LinkGameWindowToScene: linking window...");
                ReviveSMAPILogFile(SMAPICoreInstance);

                Game? runner = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var runnerType = asm.GetType("StardewValley.GameRunner");
                    if (runnerType != null)
                    {
                        var instanceField = runnerType.GetField("instance", BindingFlags.Static | BindingFlags.Public);
                        runner = instanceField?.GetValue(null) as Game;
                        if (runner != null) break;
                    }
                }

                // 1. Locate GamePlatform
                object? plat = null;
                if (runner != null)
                {
                    // Check Services dictionary
                    try
                    {
                        var svcProp = runner.GetType().GetProperty("Services", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                   ?? typeof(Game).GetProperty("Services");
                        var svcContainer = svcProp?.GetValue(runner) as GameServiceContainer;
                        if (svcContainer != null)
                        {
                            var dictField = typeof(GameServiceContainer).GetField("services", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (dictField?.GetValue(svcContainer) is System.Collections.IDictionary dict)
                            {
                                foreach (var k in dict.Keys)
                                {
                                    var v = dict[k];
                                    if (v != null && v.GetType().Name.Contains("Platform"))
                                    {
                                        plat = v;
                                        EngineLogger.Log($"[GameHost] Found GamePlatform via Services: {v.GetType().FullName}");
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    catch { }

                    // Check fields on runner
                    if (plat == null)
                    {
                        for (Type? currType = runner.GetType(); currType != null; currType = currType.BaseType)
                        {
                            foreach (var f in currType.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
                            {
                                if (f.FieldType.Name.Contains("Platform") || f.Name.ToLower().Contains("platform"))
                                {
                                    try
                                    {
                                        var val = f.GetValue(runner);
                                        if (val != null)
                                        {
                                            plat = val;
                                            EngineLogger.Log($"[GameHost] Found GamePlatform on {currType.Name}.{f.Name} ({val.GetType().FullName})");
                                            break;
                                        }
                                    }
                                    catch { }
                                }
                            }
                            if (plat != null) break;
                        }
                    }
                }

                // 2. Locate UIWindow and UIViewController
                // 2. Locate or instantiate UIViewController and iOSGameView
                UIWindow? window = null;
                UIViewController? vc = null;

                if (plat != null)
                {
                    for (Type? t = plat.GetType(); t != null; t = t.BaseType)
                    {
                        if (window == null)
                        {
                            var wf = t.GetField("_mainWindow", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            if (wf?.GetValue(plat) is UIWindow w) window = w;
                        }
                        if (vc == null)
                        {
                            var vcf = t.GetField("_viewController", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            if (vcf?.GetValue(plat) is UIViewController v) vc = v;
                        }
                    }
                }

                if (runner != null)
                {
                    if (window == null) window = runner.Services.GetService(typeof(UIWindow)) as UIWindow;
                    if (vc == null) vc = runner.Services.GetService(typeof(UIViewController)) as UIViewController;
                }

                // If vc is still null on plat, instantiate iOSGameViewController(plat)
                if (plat != null && vc == null)
                {
                    for (Type? t = plat.GetType(); t != null; t = t.BaseType)
                    {
                        var vcf = t.GetField("_viewController", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        if (vcf != null)
                        {
                            try
                            {
                                var newVC = Activator.CreateInstance(vcf.FieldType, new object[] { plat }) as UIViewController;
                                if (newVC != null)
                                {
                                    vcf.SetValue(plat, newVC);
                                    vc = newVC;
                                    EngineLogger.Log($"[GameHost] Instantiated and assigned new {vcf.FieldType.FullName} to plat._viewController");
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                EngineLogger.LogWarning($"[GameHost] Could not instantiate {vcf.FieldType.FullName}: {ex.Message}");
                            }
                        }
                    }
                }

                if (vc != null)
                {
                    _activeGameVC = vc;
                }

                // Discover _activeGameView from vc.View
                if (vc != null)
                {
                    try
                    {
                        var gv = vc.View;
                        if (gv != null && (gv.GetType().Name.Contains("GameView") || gv.GetType().FullName.Contains("iOSGameView")))
                        {
                            _activeGameView = gv;
                            EngineLogger.Log($"[GameHost] Discovered _activeGameView from vc.View: {gv.GetType().FullName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        EngineLogger.LogWarning($"[GameHost] Querying vc.View: {ex.Message}");
                    }
                }

                if (_activeGameView == null && window?.Subviews != null)
                {
                    foreach (var sv in window.Subviews)
                    {
                        if (sv != null && (sv.GetType().Name.Contains("GameView") || sv.GetType().FullName.Contains("iOSGameView")))
                        {
                            _activeGameView = sv;
                            EngineLogger.Log($"[GameHost] Discovered _activeGameView in window.Subviews: {sv.GetType().FullName}");
                            break;
                        }
                    }
                }

                // Ensure plat._viewController is set
                if (plat != null && _activeGameVC != null)
                {
                    for (Type? t = plat.GetType(); t != null; t = t.BaseType)
                    {
                        var vcf = t.GetField("_viewController", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        if (vcf != null && vcf.GetValue(plat) != _activeGameVC)
                        {
                            vcf.SetValue(plat, _activeGameVC);
                            EngineLogger.Log($"[GameHost] Assigned _activeGameVC to {t.Name}._viewController");
                        }
                    }
                }

                EngineLogger.Log($"[GameHost] Status: Window={window != null} (Bounds={window?.Bounds}), VC={vc != null}, Platform={plat != null}, GameView={_activeGameView != null}");

                // 3. Determine scene and screen geometry
                var activeScene = GetActiveWindowScene();
                CGRect screenBounds = CGRect.Empty;
                try
                {
                    if (activeScene?.Screen != null && !activeScene.Screen.Bounds.IsEmpty)
                        screenBounds = activeScene.Screen.Bounds;
                    else if (activeScene?.CoordinateSpace != null && !activeScene.CoordinateSpace.Bounds.IsEmpty)
                        screenBounds = activeScene.CoordinateSpace.Bounds;
                    else if (!UIScreen.MainScreen.Bounds.IsEmpty)
                        screenBounds = UIScreen.MainScreen.Bounds;
                }
                catch { }

                nfloat screenW = (nfloat)Math.Max((double)screenBounds.Width, (double)screenBounds.Height);
                nfloat screenH = (nfloat)Math.Min((double)screenBounds.Width, (double)screenBounds.Height);
                if (screenW <= 0 || screenH <= 0)
                {
                    screenW = 896;
                    screenH = 414;
                }
                var landscapeFrame = new CGRect(0, 0, screenW, screenH);
                EngineLogger.Log($"[GameHost] Target landscape frame: {landscapeFrame.Width}x{landscapeFrame.Height}");

                // 4. Scene attachment, Window Recreation & GameView Hierarchy Attachment
                if (_activeGameView != null && _activeGameView.Superview != null)
                {
                    // CRITICAL: NEVER detach _activeGameView if it is already in the active window hierarchy!
                    // Detaching it when window.RootViewController.View is _activeGameView rips it out of UIDropShadowView,
                    // leaving it orphaned with Superview=null and Window=null.
                    bool belongsToCurrentWindow = window != null && _activeGameView.Window != null && (_activeGameView.Window == window || _activeGameView.Window.Handle == window.Handle);
                    if (!belongsToCurrentWindow)
                    {
                        try
                        {
                            _activeGameView.RemoveFromSuperview();
                            EngineLogger.Log("[GameHost] Detached _activeGameView from stale/foreign superview.");
                        }
                        catch (Exception ex)
                        {
                            EngineLogger.LogWarning($"[GameHost] Detach _activeGameView: {ex.Message}");
                        }
                    }
                    else
                    {
                        EngineLogger.Log($"[GameHost] Preserving _activeGameView hierarchy inside current window (Superview={_activeGameView.Superview.GetType().Name}).");
                    }
                }

                if (vc != null && _activeGameView != null && vc.View != _activeGameView)
                {
                    try
                    {
                        vc.View = _activeGameView;
                        EngineLogger.Log("[GameHost] Assigned vc.View = _activeGameView.");
                    }
                    catch (Exception ex)
                    {
                        EngineLogger.LogWarning($"[GameHost] Setting vc.View: {ex.Message}");
                    }
                }

                bool windowNeedsRecreation = window == null || window.Bounds.IsEmpty || window.Bounds.Width <= 0 || window.Bounds.Height <= 0;
                if (activeScene != null && windowNeedsRecreation)
                {
                    EngineLogger.Log("[GameHost] Recreating UIWindow bound to active UIWindowScene...");
                    try
                    {
                        var newWindow = new UIWindow(activeScene);
                        newWindow.Frame = landscapeFrame;
                        newWindow.Bounds = landscapeFrame;
                        newWindow.AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight;
                        newWindow.Hidden = false;

                        if (vc != null)
                        {
                            try { vc.DangerousRetain(); } catch { }
                            newWindow.RootViewController = vc;
                        }

                        newWindow.MakeKeyAndVisible();
                        window = newWindow;

                        // Update runner services
                        if (runner != null)
                        {
                            try { runner.Services.RemoveService(typeof(UIWindow)); } catch { }
                            runner.Services.AddService(typeof(UIWindow), newWindow);
                            if (vc != null)
                            {
                                try { runner.Services.RemoveService(typeof(UIViewController)); } catch { }
                                runner.Services.AddService(typeof(UIViewController), vc);
                            }
                        }

                        // Update plat._mainWindow
                        if (plat != null)
                        {
                            for (Type? t = plat.GetType(); t != null; t = t.BaseType)
                            {
                                var wf = t.GetField("_mainWindow", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                                if (wf != null)
                                {
                                    wf.SetValue(plat, newWindow);
                                    EngineLogger.Log($"[GameHost] Updated {t.Name}._mainWindow to new UIWindow.");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        EngineLogger.LogError($"[GameHost] Failed to recreate UIWindow: {ex}");
                    }
                }
                else if (window != null)
                {
                    if (activeScene != null && window.WindowScene != activeScene)
                    {
                        window.WindowScene = activeScene;
                        EngineLogger.Log($"[GameHost] Assigned window.WindowScene to {activeScene.Description}");
                    }
                    window.AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight;
                    window.Frame = landscapeFrame;
                    window.Bounds = landscapeFrame;
                    window.Hidden = false;

                    if (vc != null)
                    {
                        try { vc.DangerousRetain(); } catch { }
                        if (window.RootViewController != vc)
                        {
                            window.RootViewController = vc;
                        }
                    }
                    window.MakeKeyAndVisible();
                }

                if (window != null && UIApplication.SharedApplication.Delegate is AppDelegate appDelegate)
                {
                    if (appDelegate.Window != window)
                    {
                        appDelegate.Window = window;
                        EngineLogger.Log("[GameHost] Set AppDelegate.Window to UIWindow.");
                    }
                }

                // Ensure _activeGameView is directly attached into active window hierarchy, visible, and has its OpenGL framebuffer allocated
                if (_activeGameView != null && window != null)
                {
                    try
                    {
                        _activeGameView.DangerousRetain();
                        _activeGameView.AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight;
                        _activeGameView.Frame = landscapeFrame;
                        _activeGameView.Bounds = landscapeFrame;
                        _activeGameView.Hidden = false;
                        _activeGameView.Alpha = 1.0f;
                        _activeGameView.Opaque = true;
                        _activeGameView.UserInteractionEnabled = true;

                        var scale = window.Screen?.Scale ?? UIScreen.MainScreen.Scale;
                        if (scale <= 0) scale = 2.0f;
                        _activeGameView.ContentScaleFactor = scale;

                        if (_activeGameView.Layer != null)
                        {
                            _activeGameView.Layer.Hidden = false;
                            _activeGameView.Layer.Opaque = true;
                            _activeGameView.Layer.Frame = landscapeFrame;
                            _activeGameView.Layer.Bounds = landscapeFrame;
                            _activeGameView.Layer.ContentsScale = scale;
                        }

                        // Attach into active window view hierarchy if not already root view
                        var targetParent = window.RootViewController?.View ?? window;
                        bool isRootView = targetParent != null && (_activeGameView == targetParent || _activeGameView.Handle == targetParent.Handle);

                        if (isRootView)
                        {
                            EngineLogger.Log($"[GameHost] _activeGameView is already RootViewController.View. (Window={_activeGameView.Window != null}, Superview={_activeGameView.Superview?.GetType().Name})");
                            if (_activeGameView.Superview == null || _activeGameView.Window == null)
                            {
                                // If UIKit detached the root view or hasn't embedded it into window yet, ensure window has it or re-trigger RootViewController assignment
                                EngineLogger.LogWarning("[GameHost] _activeGameView is RootViewController.View but has NO Superview/Window! Re-linking window.RootViewController...");
                                window.RootViewController = null;
                                window.RootViewController = vc;
                                window.MakeKeyAndVisible();
                            }
                            _activeGameView.Superview?.BringSubviewToFront(_activeGameView);
                        }
                        else if (_activeGameView.Superview != targetParent)
                        {
                            _activeGameView.RemoveFromSuperview();
                            targetParent.AddSubview(_activeGameView);
                            targetParent.BringSubviewToFront(_activeGameView);
                            EngineLogger.Log($"[GameHost] Attached _activeGameView to {targetParent.GetType().Name}. (Window={_activeGameView.Window != null})");
                        }
                        else
                        {
                            targetParent.BringSubviewToFront(_activeGameView);
                        }

                        // Ensure SupportedOrientations on vc and plat is landscape before CreateFramebuffer
                        if (vc != null)
                        {
                            try
                            {
                                var supProp = vc.GetType().GetProperty("SupportedOrientations", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                supProp?.SetValue(vc, DisplayOrientation.LandscapeLeft | DisplayOrientation.LandscapeRight);
                            }
                            catch { }
                        }

                        var fbField = _activeGameView.GetType().GetField("_framebuffer", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        var cbField = _activeGameView.GetType().GetField("_colorbuffer", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        int existingFb = Convert.ToInt32(fbField?.GetValue(_activeGameView) ?? 0);
                        bool boundsLandscape = _activeGameView.Layer.Bounds.Width > _activeGameView.Layer.Bounds.Height && _activeGameView.Layer.Bounds.Width > 0;

                        if (existingFb == 0 || !boundsLandscape)
                        {
                            // Notify DidMoveToWindow so MonoGame initializes scale and context
                            var didMoveMethod = _activeGameView.GetType().GetMethod("DidMoveToWindow", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            didMoveMethod?.Invoke(_activeGameView, null);

                            // Explicitly recreate / allocate framebuffer with the active landscape frame
                            var destroyFbMethod = _activeGameView.GetType().GetMethod("DestroyFramebuffer", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            var createFbMethod = _activeGameView.GetType().GetMethod("CreateFramebuffer", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            destroyFbMethod?.Invoke(_activeGameView, null);
                            createFbMethod?.Invoke(_activeGameView, null);

                            var fbId = fbField?.GetValue(_activeGameView);
                            var cbId = cbField?.GetValue(_activeGameView);
                            EngineLogger.Log($"[GameHost] _activeGameView Framebuffer allocated: _framebuffer={fbId}, _colorbuffer={cbId}, Layer.Bounds={_activeGameView.Layer.Bounds.Width}x{_activeGameView.Layer.Bounds.Height}, Superview={_activeGameView.Superview?.GetType().Name}, Window={_activeGameView.Window != null}");
                        }
                    }
                    catch (Exception ex)
                    {
                        EngineLogger.LogWarning($"[GameHost] _activeGameView layout / framebuffer error: {ex.Message}");
                    }
                }

                // 5. Connect and repair Game <-> GamePlatform bidirectional link & IsActive
                Game? targetGame = runner;

                if (plat != null)
                {
                    // Check what Game the platform currently points to
                    var gProp = plat.GetType().GetProperty("Game", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                             ?? plat.GetType().BaseType?.GetProperty("Game", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var pGame = gProp?.GetValue(plat) as Game;
                    if (pGame != null) targetGame = pGame;

                    if (targetGame != null)
                    {
                        // Ensure plat.<Game>k__BackingField points to targetGame
                        for (Type? t = plat.GetType(); t != null; t = t.BaseType)
                        {
                            var gf = t.GetField("<Game>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (gf != null)
                            {
                                gf.SetValue(plat, targetGame);
                            }
                        }
                    }

                    // Reset disposed = false on GamePlatform
                    for (Type? t = plat.GetType(); t != null; t = t.BaseType)
                    {
                        var dispF = t.GetField("disposed", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        dispF?.SetValue(plat, false);
                    }

                    // Set _isActive = true on GamePlatform
                    for (Type? t = plat.GetType(); t != null; t = t.BaseType)
                    {
                        var actField = t.GetField("_isActive", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        actField?.SetValue(plat, true);
                    }

                    // Synchronize iOSGameWindow._viewController if present
                    try
                    {
                        var winField = plat.GetType().GetField("_window", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                                    ?? plat.GetType().BaseType?.GetField("_window", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        var pWindow = winField?.GetValue(plat);
                        if (pWindow != null)
                        {
                            var wVcf = pWindow.GetType().GetField("_viewController", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            if (wVcf != null && vc != null && wVcf.GetValue(pWindow) != vc)
                            {
                                wVcf.SetValue(pWindow, vc);
                                EngineLogger.Log($"[GameHost] Synchronized iOSGameWindow._viewController to {vc.GetType().FullName}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        EngineLogger.LogWarning($"[GameHost] Set iOSGameWindow._viewController warning: {ex.Message}");
                    }

                    try
                    {
                        var didBecomeAct = plat.GetType().GetMethod("Application_DidBecomeActive", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        didBecomeAct?.Invoke(plat, new object?[] { null });
                    }
                    catch (Exception ex)
                    {
                        EngineLogger.LogWarning($"[GameHost] Application_DidBecomeActive error: {ex.Message}");
                    }
                }

                // Repair Platform field on targetGame and runner
                var gamesToFix = new System.Collections.Generic.HashSet<Game>();
                if (targetGame != null) gamesToFix.Add(targetGame);
                if (runner != null) gamesToFix.Add(runner);

                foreach (var g in gamesToFix)
                {
                    for (Type? t = g.GetType(); t != null; t = t.BaseType)
                    {
                        var pf = t.GetField("Platform", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        if (pf != null && plat != null)
                        {
                            pf.SetValue(g, plat);
                        }

                        var dispF = t.GetField("_isDisposed", BindingFlags.NonPublic | BindingFlags.Instance);
                        dispF?.SetValue(g, false);
                    }

                    // Also ensure Game._instance is set
                    try
                    {
                        var instField = typeof(Game).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static);
                        instField?.SetValue(null, g);
                    }
                    catch { }
                }

                // 6. Create modern CADisplayLink driving game tick & direct render pipeline
                if (plat != null && _engineDisplayLink == null)
                {
                    EngineLogger.Log("[GameHost] Creating modern CADisplayLink for game tick pipeline...");
                    _engineDisplayLink = CoreAnimation.CADisplayLink.Create(() =>
                    {
                        ExecuteDirectGameTick(runner, plat, _activeGameView);
                    });
                    try { _engineDisplayLink.PreferredFramesPerSecond = 60; } catch { }
                    _engineDisplayLink.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Common);
                    _engineDisplayLink.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Default);
                    _engineDisplayLink.Paused = false;
                    EngineLogger.Log("[GameHost] Modern CADisplayLink registered and unpaused.");

                    for (Type? t = plat.GetType(); t != null; t = t.BaseType)
                    {
                        var dlf = t.GetField("_displayLink", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        if (dlf != null)
                        {
                            dlf.SetValue(plat, _engineDisplayLink);
                            EngineLogger.Log($"[GameHost] Assigned _engineDisplayLink to {t.Name}._displayLink");
                        }
                    }
                }

                // 7. Revive GraphicsDevice, iOSGameWindow, and Game1 instances
                ReviveGraphicsDeviceAndInstances(runner, plat, vc, _activeGameView);

                // 8. Force View Layout, OpenGL Framebuffer Allocation & View Hierarchy Audit
                if (window != null)
                {
                    try
                    {
                        window.SetNeedsLayout();
                        window.LayoutIfNeeded();
                        if (!_viewHierarchyAudited)
                        {
                            _viewHierarchyAudited = true;
                            EngineLogger.Log($"[GameHost] === Window View Hierarchy Audit (Bounds={window.Bounds}, Frame={window.Frame}, Key={window.IsKeyWindow}) ===");
                            LogViewHierarchy(window, 0);
                            EngineLogger.Log("[GameHost] ===================================================================");
                        }
                    }
                    catch (Exception ex)
                    {
                        EngineLogger.LogWarning($"[GameHost] LayoutSubviews / Hierarchy Audit error: {ex.Message}");
                    }
                }

                try
                {
                    var gd = runner?.GraphicsDevice;
                    if (gd != null)
                    {
                        EngineLogger.Log($"[GameHost] GraphicsDevice: Viewport={gd.Viewport.Width}x{gd.Viewport.Height}, BackBuffer={gd.PresentationParameters.BackBufferWidth}x{gd.PresentationParameters.BackBufferHeight}");
                    }
                }
                catch (Exception ex)
                {
                    EngineLogger.LogWarning($"[GameHost] GraphicsDevice query: {ex.Message}");
                }

                EngineLogger.Log("[GameHost] MonoGame UIWindow and CADisplayLink successfully linked.");
            }
            catch (Exception ex)
            {
                EngineLogger.LogError($"[GameHost] LinkGameWindowToScene failed: {ex}");
            }
        }

        private static void LogViewHierarchy(UIView? view, int depth)
        {
            if (view == null) return;
            string indent = new string(' ', depth * 2);
            string superType = view.Superview != null ? view.Superview.GetType().Name : "none";
            string winDesc = view.Window != null ? $"Window(Key={view.Window.IsKeyWindow})" : "none";
            string nativeClass = "unknown";
            try { nativeClass = view.Class?.Name ?? view.GetType().Name; } catch { }
            bool isGV = (_activeGameView != null && (view == _activeGameView || view.Handle == _activeGameView.Handle));
            string isGVStr = isGV ? " [ACTIVE_GAME_VIEW]" : "";
            EngineLogger.Log($"[GameHost] {indent}-> [{view.GetType().FullName} ({nativeClass})]{isGVStr} Frame={view.Frame}, Bounds={view.Bounds}, Hidden={view.Hidden}, Alpha={view.Alpha}, Opaque={view.Opaque}, Superview={superType}, Window={winDesc}, Subviews={view.Subviews?.Length ?? 0}");
            if (view.Subviews != null)
            {
                foreach (var child in view.Subviews)
                {
                    if (child != null)
                    {
                        LogViewHierarchy(child, depth + 1);
                    }
                }
            }
        }

        public static void OnDidEnterBackground()
        {
            try
            {
                if (_engineDisplayLink != null)
                {
                    _engineDisplayLink.Paused = true;
                }
                EngineLogger.Flush();
            }
            catch { }
        }

        public static void OnWillEnterForeground()
        {
            try
            {
                if (_engineDisplayLink != null)
                {
                    _engineDisplayLink.Paused = false;
                }
                ReviveSMAPILogFile(SMAPICoreInstance);
                LinkGameWindowToScene();
            }
            catch { }
        }

        private static object? GetPrimaryGame1Instance()
        {
            try
            {
                if (_gameRunnerInstanceField == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var runnerType = asm.GetType("StardewValley.GameRunner");
                        if (runnerType != null)
                        {
                            _gameRunnerInstanceField = runnerType.GetField("instance", BindingFlags.Static | BindingFlags.Public);
                            _gameRunnerInstancesListField = runnerType.GetField("gameInstances", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                                         ?? runnerType.BaseType?.GetField("gameInstances", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            break;
                        }
                    }
                }

                var runner = _gameRunnerInstanceField?.GetValue(null);
                if (runner != null)
                {
                    if (_gameRunnerInstancesListField == null)
                    {
                        _gameRunnerInstancesListField = runner.GetType().GetField("gameInstances", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                                     ?? runner.GetType().BaseType?.GetField("gameInstances", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                    if (_gameRunnerInstancesListField?.GetValue(runner) is System.Collections.IList list && list.Count > 0)
                    {
                        return list[0];
                    }
                }
            }
            catch { }
            return null;
        }

        private static int GetGame1Ticks()
        {
            try
            {
                if (_game1TicksField == null)
                {
                    var g1Type = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                        .FirstOrDefault(t => t.FullName == "StardewValley.Game1");
                    _game1TicksField = g1Type?.GetField("ticks", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                                    ?? g1Type?.GetField("ticks", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                if (_game1TicksField != null)
                {
                    object? target = _game1TicksField.IsStatic ? null : GetPrimaryGame1Instance();
                    if (_game1TicksField.IsStatic || target != null)
                    {
                        return Convert.ToInt32(_game1TicksField.GetValue(target));
                    }
                }
            }
            catch { }
            return -1;
        }

        private static void IntrospectGameState(Game? runner, object? core)
        {
            try
            {
                EngineLogger.Log("=== Game State Introspection ===");
                if (core != null)
                {
                    var ct = core.GetType();
                    foreach (var f in ct.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (f.FieldType == typeof(bool) || f.FieldType.IsPrimitive || f.FieldType == typeof(string) || f.FieldType.IsEnum)
                        {
                            try { EngineLogger.Log($"[SMAPI SCore] {f.Name} = {f.GetValue(core)}"); } catch { }
                        }
                    }
                }

                if (runner != null)
                {
                    var rt = runner.GetType();
                    var instField = rt.GetField("gameInstances", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                 ?? rt.BaseType?.GetField("gameInstances", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (instField?.GetValue(runner) is System.Collections.IList instances)
                    {
                        EngineLogger.Log($"[GameRunner] gameInstances.Count = {instances.Count}");
                        for (int i = 0; i < instances.Count; i++)
                        {
                            var inst = instances[i];
                            if (inst == null) continue;
                            EngineLogger.Log($"[GameRunner] instance[{i}]: {inst.GetType().FullName}");
                        }
                    }
                    else
                    {
                        EngineLogger.LogWarning("[GameRunner] gameInstances is null or not IList!");
                    }
                }

                var g1Type = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                    .FirstOrDefault(t => t.FullName == "StardewValley.Game1");
                if (g1Type != null)
                {
                    var menuProp = g1Type.GetProperty("activeClickableMenu", BindingFlags.Public | BindingFlags.Static);
                    var modeField = g1Type.GetField("gameMode", BindingFlags.Public | BindingFlags.Static);
                    EngineLogger.Log($"[Game1 State] gameMode = {modeField?.GetValue(null)}, activeClickableMenu = {menuProp?.GetValue(null)?.GetType().FullName ?? "null"}");
                }
                EngineLogger.Log("================================");
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[GameHost] IntrospectGameState error: {ex.Message}");
            }
        }

        private static void ExecuteDirectGameTick(Game? runner, object? plat, UIView? gameView)
        {
            try
            {
                if (runner == null) return;

                // 1. Ensure runner is marked active (cached reflection)
                if (!runner.IsActive)
                {
                    if (_runnerIsActiveField == null)
                    {
                        for (Type? t = runner.GetType(); t != null; t = t.BaseType)
                        {
                            var actField = t.GetField("_isActive", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            if (actField != null)
                            {
                                _runnerIsActiveField = actField;
                                break;
                            }
                        }
                    }
                    _runnerIsActiveField?.SetValue(runner, true);
                }

                // If gameView is null, look for it in key window
                if (gameView == null || gameView.Handle == IntPtr.Zero)
                {
                    var kw = UIApplication.SharedApplication.KeyWindow ?? UIApplication.SharedApplication.Windows.FirstOrDefault(w => w != null);
                    if (kw?.Subviews != null)
                    {
                        foreach (var sv in kw.Subviews)
                        {
                            if (sv != null && (sv.GetType().Name.Contains("GameView") || sv.GetType().FullName.Contains("iOSGameView")))
                            {
                                _activeGameView = sv;
                                gameView = sv;
                                break;
                            }
                        }
                    }
                }

                // Revive graphics device, window viewController, and instance options
                if (_directTickCount == 0 || runner.GraphicsDevice == null || runner.GraphicsDevice.IsDisposed)
                {
                    ReviveGraphicsDeviceAndInstances(runner, plat, null, gameView);
                }

                // Built-in TouchOverlay is only needed when SMAPI SDViOSTouchControls mod is not active
                if (!DisableBuiltInTouchInput)
                {
                    if (!_touchOverlayAttached)
                    {
                        AttachTouchOverlayToGameRunner();
                        _touchOverlayAttached = true;
                    }
                    TouchVirtualPad.Instance.ForwardInputToGame();
                }
                VirtualKeyboardManager.Update();

                // 2. Introspect gameView and game state on tick 0
                if (_directTickCount == 0)
                {
                    if (gameView != null)
                    {
                        try
                        {
                            var methodNames = gameView.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(m => m.Name).Distinct();
                            EngineLogger.Log($"[GameHost] _activeGameView ({gameView.GetType().FullName}) methods: {string.Join(", ", methodNames)}");
                            var fieldNames = gameView.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(f => $"{f.Name} ({f.FieldType.Name})");
                            EngineLogger.Log($"[GameHost] _activeGameView fields: {string.Join(", ", fieldNames)}");
                        }
                        catch { }
                    }
                    IntrospectGameState(runner, SMAPICoreInstance);
                }

                // 3. MakeCurrent on iOSGameView and verify window attachment
                bool makeCurrentSuccess = false;
                if (gameView != null && gameView.Handle != IntPtr.Zero)
                {
                    if (gameView.Window == null)
                    {
                        var kw = UIApplication.SharedApplication.KeyWindow ?? UIApplication.SharedApplication.Windows.FirstOrDefault(w => w != null);
                        if (kw != null)
                        {
                            var targetParent = kw.RootViewController?.View ?? kw;
                            if (targetParent != null && targetParent != gameView && targetParent.Handle != gameView.Handle)
                            {
                                if (_directTickCount < 5) EngineLogger.LogWarning("[GameHost] Direct tick: gameView detached from window! Re-attaching to targetParent...");
                                gameView.RemoveFromSuperview();
                                targetParent.AddSubview(gameView);
                                targetParent.BringSubviewToFront(gameView);
                            }
                            else if (gameView.Superview == null)
                            {
                                if (_directTickCount < 5) EngineLogger.LogWarning("[GameHost] Direct tick: gameView has no superview! Adding directly to key window...");
                                kw.AddSubview(gameView);
                                kw.BringSubviewToFront(gameView);
                            }
                        }
                    }

                    if (_cachedMakeCurrent == null || _cachedMakeCurrentView != gameView)
                    {
                        _cachedMakeCurrentView = gameView;
                        if (_makeCurrentMethod == null)
                        {
                            _makeCurrentMethod = gameView.GetType().GetMethod("MakeCurrent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        }
                        if (_makeCurrentMethod != null)
                        {
                            try
                            {
                                _cachedMakeCurrent = (Action)Delegate.CreateDelegate(typeof(Action), gameView, _makeCurrentMethod);
                            }
                            catch { }
                        }
                    }
                    try
                    {
                        if (_cachedMakeCurrent != null)
                        {
                            _cachedMakeCurrent();
                            makeCurrentSuccess = true;
                        }
                        else if (_makeCurrentMethod != null)
                        {
                            _makeCurrentMethod.Invoke(gameView, null);
                            makeCurrentSuccess = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_directTickCount < 5) EngineLogger.LogWarning($"[GameHost] Direct MakeCurrent error: {ex.Message}");
                    }
                }

                // 4. Tick Game (SMAPI + Stardew Valley Update & Draw)
                if (_directTickCount == 0 || _directTickCount % 120 == 0)
                {
                    ReviveSMAPILogFile(SMAPICoreInstance);
                }

                int ticksBefore = 0;
                if (_directTickCount <= 5)
                {
                    ticksBefore = GetGame1Ticks();
                }

                bool platTickRan = false;
                try
                {
                    if (plat != null)
                    {
                        if (_cachedPlatformTick == null)
                        {
                            if (_platformTickMethod == null)
                            {
                                _platformTickMethod = plat.GetType().GetMethod("Tick", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            }
                            if (_platformTickMethod != null)
                            {
                                try
                                {
                                    _cachedPlatformTick = (Action)Delegate.CreateDelegate(typeof(Action), plat, _platformTickMethod);
                                }
                                catch { }
                            }
                        }

                        if (_cachedPlatformTick != null)
                        {
                            _cachedPlatformTick();
                            platTickRan = true;
                        }
                        else if (_platformTickMethod != null)
                        {
                            _platformTickMethod.Invoke(plat, null);
                            platTickRan = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_directTickCount < 5) EngineLogger.LogWarning($"[GameHost] plat.Tick error: {ex.InnerException ?? ex}");
                }

                bool runnerTickRan = false;
                if (_directTickCount <= 5)
                {
                    int ticksAfter = GetGame1Ticks();
                    if (ticksAfter == ticksBefore && runner.GraphicsDevice != null)
                    {
                        try
                        {
                            runner.Tick();
                            runnerTickRan = true;
                        }
                        catch (Exception ex)
                        {
                            if (_directTickCount < 5) EngineLogger.LogWarning($"[GameHost] runner.Tick error: {ex.InnerException ?? ex}");
                        }
                    }
                }
                else if (!platTickRan && runner.GraphicsDevice != null)
                {
                    try
                    {
                        runner.Tick();
                        runnerTickRan = true;
                    }
                    catch { }
                }

                // 5. Threading.Run
                if (_cachedThreadingRun == null)
                {
                    if (_threadingRunMethod == null)
                    {
                        var threadingType = typeof(Game).Assembly.GetType("Microsoft.Xna.Framework.Threading");
                        _threadingRunMethod = threadingType?.GetMethod("Run", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                    }
                    if (_threadingRunMethod != null)
                    {
                        try
                        {
                            _cachedThreadingRun = (Action)Delegate.CreateDelegate(typeof(Action), _threadingRunMethod);
                        }
                        catch { }
                    }
                }
                try
                {
                    if (_cachedThreadingRun != null)
                    {
                        _cachedThreadingRun();
                    }
                    else
                    {
                        _threadingRunMethod?.Invoke(null, null);
                    }
                }
                catch { }

                // 6. Diagnostic visual clear: only on first 2 frames during cold boot
                bool diagClearSuccess = false;
                if (_directTickCount < 2 && runner.GraphicsDevice != null)
                {
                    try
                    {
                        runner.GraphicsDevice.Clear(new Microsoft.Xna.Framework.Color(100, 149, 237));
                        diagClearSuccess = true;
                    }
                    catch (Exception ex)
                    {
                        if (_directTickCount < 2) EngineLogger.LogWarning($"[GameHost] Diagnostic Clear error: {ex.Message}");
                    }
                }

                // 7. Present cleanly: plat.Tick() and runner.Tick() already call EndDraw() -> GraphicsDevice.Present().
                // Only invoke manual Present() when diagnostic clear ran or when neither tick method presented.
                bool gdPresentSuccess = platTickRan || runnerTickRan;
                if (_directTickCount < 2 || (!platTickRan && !runnerTickRan))
                {
                    try
                    {
                        if (runner.GraphicsDevice != null)
                        {
                            runner.GraphicsDevice.Present();
                            gdPresentSuccess = true;
                        }
                        else if (gameView != null && gameView.Handle != IntPtr.Zero)
                        {
                            if (_presentMethod == null)
                            {
                                _presentMethod = gameView.GetType().GetMethod("Present", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                              ?? gameView.GetType().GetMethod("SwapBuffers", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            }
                            _presentMethod?.Invoke(gameView, null);
                            gdPresentSuccess = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_directTickCount < 5) EngineLogger.LogWarning($"[GameHost] Presentation error: {ex.Message}");
                    }
                }

                _directTickCount++;
                if (_directTickCount <= 10)
                {
                    int finalTicks = GetGame1Ticks();
                    EngineLogger.Log($"[GameHost] Frame #{_directTickCount}: MC={makeCurrentSuccess}, DiagClear={diagClearSuccess}, PlatTick={platTickRan}, RunnerTick={runnerTickRan}, G1.ticks={finalTicks}, Present={gdPresentSuccess}, GD={runner.GraphicsDevice != null}, View={gameView != null}");
                }
            }
            catch (Exception ex)
            {
                if (_directTickCount < 15)
                {
                    _directTickCount++;
                    EngineLogger.LogError($"[GameHost] Direct tick pipeline error: {ex}");
                }
            }
        }

        public static void ReviveGraphicsDeviceAndInstances(Game? runner, object? plat, UIViewController? vc, UIView? gameView)
        {
            if (runner == null) return;
            try
            {
                Microsoft.Xna.Framework.Graphics.GraphicsDevice? currentGD = runner.GraphicsDevice;
                var effectiveVC = vc ?? _activeGameVC;
                if (plat != null && effectiveVC != null)
                {
                    for (Type? t = plat.GetType(); t != null; t = t.BaseType)
                    {
                        var vcf = t.GetField("_viewController", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        if (vcf != null && vcf.GetValue(plat) != effectiveVC)
                        {
                            vcf.SetValue(plat, effectiveVC);
                            EngineLogger.Log($"[GameHost] Restored {t.Name}._viewController = {effectiveVC.GetType().FullName}");
                        }
                    }
                }

                // 1. Repair and synchronize iOSGameWindow._viewController and TouchPanel/Mouse PrimaryWindow
                Microsoft.Xna.Framework.GameWindow? xnaWindow = null;
                if (plat != null)
                {
                    try
                    {
                        var winField = plat.GetType().GetField("_window", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                                    ?? plat.GetType().BaseType?.GetField("_window", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        var pWindow = winField?.GetValue(plat);
                        if (pWindow != null)
                        {
                            xnaWindow = pWindow as Microsoft.Xna.Framework.GameWindow;
                            var wVcf = pWindow.GetType().GetField("_viewController", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            if (wVcf != null && (wVcf.GetValue(pWindow) == null || (effectiveVC != null && wVcf.GetValue(pWindow) != effectiveVC)))
                            {
                                if (effectiveVC != null)
                                {
                                    wVcf.SetValue(pWindow, effectiveVC);
                                    EngineLogger.Log($"[GameHost] Synchronized iOSGameWindow._viewController to {effectiveVC.GetType().FullName}");
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (xnaWindow == null && runner != null)
                {
                    try
                    {
                        xnaWindow = runner.Window as Microsoft.Xna.Framework.GameWindow;
                    }
                    catch { }
                }

                // Ensure TouchPanel.PrimaryWindow and Mouse.PrimaryWindow are initialized
                if (xnaWindow != null)
                {
                    try
                    {
                        var tpType = typeof(Microsoft.Xna.Framework.Input.Touch.TouchPanel);
                        var tpPwField = tpType.GetField("PrimaryWindow", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                        if (tpPwField != null && tpPwField.GetValue(null) == null)
                        {
                            tpPwField.SetValue(null, xnaWindow);
                            EngineLogger.Log($"[GameHost] Set TouchPanel.PrimaryWindow = {xnaWindow.GetType().FullName}");
                        }

                        var mouseType = typeof(Microsoft.Xna.Framework.Input.Mouse);
                        var mPwField = mouseType.GetField("PrimaryWindow", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                        if (mPwField != null && mPwField.GetValue(null) == null)
                        {
                            mPwField.SetValue(null, xnaWindow);
                            EngineLogger.Log($"[GameHost] Set Mouse.PrimaryWindow = {xnaWindow.GetType().FullName}");
                        }

                        // Verify xnaWindow.TouchPanelState is present
                        var winType = xnaWindow.GetType();
                        FieldInfo? tpsField = null;
                        for (Type? t = winType; t != null; t = t.BaseType)
                        {
                            tpsField = t.GetField("TouchPanelState", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            if (tpsField != null) break;
                        }

                        if (tpsField != null && tpsField.GetValue(xnaWindow) == null)
                        {
                            var tpsType = tpsField.FieldType;
                            var tpsCtor = tpsType.GetConstructor(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, new Type[] { typeof(Microsoft.Xna.Framework.GameWindow) }, null);
                            if (tpsCtor != null)
                            {
                                var newTps = tpsCtor.Invoke(new object[] { xnaWindow });
                                tpsField.SetValue(xnaWindow, newTps);
                                EngineLogger.Log("[GameHost] Instantiated and assigned TouchPanelState to GameWindow.");
                            }
                        }
                    }
                    catch (Exception winEx)
                    {
                        EngineLogger.LogWarning($"[GameHost] TouchPanel/Mouse PrimaryWindow initialization warning: {winEx.Message}");
                    }
                }

                // 2. Discover and revive Game1.graphics (GraphicsDeviceManager)
                object? gdm = null;
                FieldInfo? sdvGraphicsField = null;
                Type? game1Type = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    game1Type = asm.GetType("StardewValley.Game1");
                    if (game1Type != null)
                    {
                        sdvGraphicsField = game1Type.GetField("graphics", BindingFlags.Static | BindingFlags.Public);
                        gdm = sdvGraphicsField?.GetValue(null);
                        if (gdm != null) break;
                    }
                }

                if (gdm != null)
                {
                    var gdmType = gdm.GetType();
                    var dispField = gdmType.GetField("disposed", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    if (dispField != null && (bool)(dispField.GetValue(gdm) ?? false))
                    {
                        dispField.SetValue(gdm, false);
                        EngineLogger.Log("[GameHost] Cleared GraphicsDeviceManager.disposed = false");
                    }

                    try
                    {
                        var isFsProp = gdmType.GetProperty("IsFullScreen", BindingFlags.Public | BindingFlags.Instance);
                        isFsProp?.SetValue(gdm, true);

                        var pbbwProp = gdmType.GetProperty("PreferredBackBufferWidth", BindingFlags.Public | BindingFlags.Instance);
                        pbbwProp?.SetValue(gdm, 1792);

                        var pbbhProp = gdmType.GetProperty("PreferredBackBufferHeight", BindingFlags.Public | BindingFlags.Instance);
                        pbbhProp?.SetValue(gdm, 828);
                    }
                    catch { }

                    var gdField = gdmType.GetField("_graphicsDevice", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    currentGD = (gdField?.GetValue(gdm) as Microsoft.Xna.Framework.Graphics.GraphicsDevice) ?? currentGD;

                    if (currentGD == null)
                    {
                        EngineLogger.Log("[GameHost] GraphicsDeviceManager._graphicsDevice is null! Attempting revival...");
                        try
                        {
                            var createDevMethod = gdmType.GetMethod("CreateDevice", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            createDevMethod?.Invoke(gdm, null);
                            currentGD = gdField?.GetValue(gdm) as Microsoft.Xna.Framework.Graphics.GraphicsDevice;
                            EngineLogger.Log($"[GameHost] CreateDevice result: GD={currentGD != null}");
                        }
                        catch (Exception cEx)
                        {
                            EngineLogger.LogWarning($"[GameHost] CreateDevice invocation warning: {cEx.Message}");
                        }

                        if (currentGD == null)
                        {
                            try
                            {
                                var applyChangesMethod = gdmType.GetMethod("ApplyChanges", BindingFlags.Public | BindingFlags.Instance);
                                applyChangesMethod?.Invoke(gdm, null);
                                currentGD = gdField?.GetValue(gdm) as Microsoft.Xna.Framework.Graphics.GraphicsDevice;
                                EngineLogger.Log($"[GameHost] ApplyChanges result: GD={currentGD != null}");
                            }
                            catch (Exception aEx)
                            {
                                EngineLogger.LogWarning($"[GameHost] ApplyChanges invocation warning: {aEx.Message}");
                            }
                        }

                        if (currentGD == null)
                        {
                            try
                            {
                                var adapter = Microsoft.Xna.Framework.Graphics.GraphicsAdapter.DefaultAdapter;
                                var profile = Microsoft.Xna.Framework.Graphics.GraphicsProfile.Reach;
                                Microsoft.Xna.Framework.Graphics.PresentationParameters? pp = null;

                                try
                                {
                                    pp = new Microsoft.Xna.Framework.Graphics.PresentationParameters();
                                }
                                catch (Exception ppEx)
                                {
                                    EngineLogger.LogWarning($"[GameHost] PresentationParameters ctor warning: {ppEx.Message}. Using uninitialized allocation.");
                                    try
                                    {
                                        pp = (Microsoft.Xna.Framework.Graphics.PresentationParameters)
                                            System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Microsoft.Xna.Framework.Graphics.PresentationParameters));
                                    }
                                    catch { }
                                }

                                if (pp != null)
                                {
                                    pp.BackBufferWidth = 1792;
                                    pp.BackBufferHeight = 828;
                                    pp.BackBufferFormat = Microsoft.Xna.Framework.Graphics.SurfaceFormat.Color;
                                    pp.DepthStencilFormat = Microsoft.Xna.Framework.Graphics.DepthFormat.Depth24Stencil8;
                                    pp.IsFullScreen = true;
                                    if (gameView != null && gameView.Handle != IntPtr.Zero)
                                    {
                                        pp.DeviceWindowHandle = gameView.Handle;
                                    }
                                    currentGD = new Microsoft.Xna.Framework.Graphics.GraphicsDevice(adapter, profile, pp);
                                    gdField?.SetValue(gdm, currentGD);
                                    EngineLogger.Log("[GameHost] Instantiated and assigned fresh GraphicsDevice to GraphicsDeviceManager.");
                                }
                            }
                            catch (Exception devEx)
                            {
                                EngineLogger.LogError($"[GameHost] Direct GraphicsDevice instantiation error: {devEx}");
                            }
                        }
                    }

                    // 3. Ensure runner's internal graphics services point to gdm
                    for (Type? t = runner.GetType(); t != null; t = t.BaseType)
                    {
                        var gdmField = t.GetField("_graphicsDeviceManager", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        if (gdmField != null && gdmField.GetValue(runner) == null)
                        {
                            gdmField.SetValue(runner, gdm);
                            EngineLogger.Log("[GameHost] Set runner._graphicsDeviceManager = Game1.graphics");
                        }
                        var gdsField = t.GetField("_graphicsDeviceService", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        if (gdsField != null && gdsField.GetValue(runner) == null)
                        {
                            gdsField.SetValue(runner, gdm);
                            EngineLogger.Log("[GameHost] Set runner._graphicsDeviceService = Game1.graphics");
                        }
                    }
                }

                // 4. Ensure gameInstances have valid localMultiplayerWindow, instanceOptions, and Game1.game1
                object? defaultOptions = null;
                Type? optionsType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    optionsType = asm.GetType("StardewValley.Options");
                    if (optionsType != null)
                    {
                        try
                        {
                            defaultOptions = Activator.CreateInstance(optionsType);
                            // Set zoomLevel = 1.0f, uiScale = 1.0f if present
                            var zoomProp = optionsType.GetProperty("zoomLevel", BindingFlags.Public | BindingFlags.Instance)
                                        ?? optionsType.GetProperty("ZoomLevel", BindingFlags.Public | BindingFlags.Instance);
                            zoomProp?.SetValue(defaultOptions, 1.0f);

                            var uiScaleProp = optionsType.GetProperty("uiScale", BindingFlags.Public | BindingFlags.Instance)
                                           ?? optionsType.GetProperty("UiScale", BindingFlags.Public | BindingFlags.Instance);
                            uiScaleProp?.SetValue(defaultOptions, 1.0f);
                            break;
                        }
                        catch { }
                    }
                }

                // Ensure Game1.options static property/field has a non-null Options instance
                if (game1Type != null && defaultOptions != null)
                {
                    try
                    {
                        var g1OptProp = game1Type.GetProperty("options", BindingFlags.Static | BindingFlags.Public)
                                     ?? game1Type.GetProperty("Options", BindingFlags.Static | BindingFlags.Public);
                        if (g1OptProp != null && g1OptProp.GetValue(null) == null)
                        {
                            g1OptProp.SetValue(null, defaultOptions);
                            EngineLogger.Log("[GameHost] Set Game1.options static property to defaultOptions.");
                        }

                        var g1OptField = game1Type.GetField("options", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                                      ?? game1Type.GetField("Options", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (g1OptField != null && g1OptField.GetValue(null) == null)
                        {
                            g1OptField.SetValue(null, defaultOptions);
                            EngineLogger.Log("[GameHost] Set Game1.options static field to defaultOptions.");
                        }
                    }
                    catch (Exception optEx)
                    {
                        EngineLogger.LogWarning($"[GameHost] Game1.options initialization warning: {optEx.Message}");
                    }
                }

                var instancesField = runner.GetType().GetField("gameInstances", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                                  ?? runner.GetType().BaseType?.GetField("gameInstances", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (instancesField?.GetValue(runner) is System.Collections.IList instances && instances.Count > 0)
                {
                    foreach (var inst in instances)
                    {
                        if (inst == null) continue;
                        var instType = inst.GetType();
                        var lmwField = instType.GetField("localMultiplayerWindow", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (lmwField != null)
                        {
                            currentGD = runner.GraphicsDevice ?? (gdm as GraphicsDeviceManager)?.GraphicsDevice;
                            int targetW = (currentGD != null && currentGD.PresentationParameters.BackBufferWidth > 0) ? currentGD.PresentationParameters.BackBufferWidth : 1792;
                            int targetH = (currentGD != null && currentGD.PresentationParameters.BackBufferHeight > 0) ? currentGD.PresentationParameters.BackBufferHeight : 828;

                            var rect = (Rectangle)(lmwField.GetValue(inst) ?? Rectangle.Empty);
                            if (rect.Width <= 0 || rect.Height <= 0 || (rect.Width == 896 && targetW > 896))
                            {
                                lmwField.SetValue(inst, new Rectangle(0, 0, targetW, targetH));
                                EngineLogger.Log($"[GameHost] Initialized instance localMultiplayerWindow to {targetW}x{targetH}");
                            }
                        }

                        // Check instance field "instanceOptions"
                        FieldInfo? instOptField = null;
                        for (Type? t = instType; t != null; t = t.BaseType)
                        {
                            instOptField = t.GetField("instanceOptions", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (instOptField != null) break;
                        }

                        if (instOptField != null && instOptField.GetValue(inst) == null)
                        {
                            try
                            {
                                var newOpt = defaultOptions ?? Activator.CreateInstance(instOptField.FieldType);
                                instOptField.SetValue(inst, newOpt);
                                EngineLogger.Log("[GameHost] Assigned instanceOptions to game instance.");
                            }
                            catch (Exception ioEx)
                            {
                                EngineLogger.LogWarning($"[GameHost] instanceOptions field set error: {ioEx.Message}");
                            }
                        }

                        // Check property "options" or "Options"
                        var optProp = instType.GetProperty("options", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                   ?? instType.GetProperty("Options", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (optProp != null && optProp.GetValue(inst) == null)
                        {
                            try
                            {
                                var optInstance = defaultOptions ?? Activator.CreateInstance(optProp.PropertyType);
                                optProp.SetValue(inst, optInstance);
                                EngineLogger.Log($"[GameHost] Initialized instance options: {optProp.PropertyType.Name}");
                            }
                            catch { }
                        }

                        // Ensure Game1.game1 static field points to inst if null
                        if (game1Type != null)
                        {
                            try
                            {
                                var g1Field = game1Type.GetField("game1", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                                if (g1Field != null && g1Field.GetValue(null) == null && game1Type.IsInstanceOfType(inst))
                                {
                                    g1Field.SetValue(null, inst);
                                    EngineLogger.Log("[GameHost] Set Game1.game1 = instance.");
                                }
                            }
                            catch { }
                        }

                        // Ensure instance _screen and _uiScreen render targets are allocated
                        currentGD = runner.GraphicsDevice ?? (gdm as GraphicsDeviceManager)?.GraphicsDevice;
                        if (currentGD != null)
                        {
                            int targetW = currentGD.PresentationParameters.BackBufferWidth > 0 ? currentGD.PresentationParameters.BackBufferWidth : 1792;
                            int targetH = currentGD.PresentationParameters.BackBufferHeight > 0 ? currentGD.PresentationParameters.BackBufferHeight : 828;

                            var screenProp = instType.GetProperty("screen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            var screenField = instType.GetField("_screen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                           ?? instType.GetField("screen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            var currentScreen = screenProp?.GetValue(inst) ?? screenField?.GetValue(inst);
                            if (currentScreen == null)
                            {
                                try
                                {
                                    var rt = new Microsoft.Xna.Framework.Graphics.RenderTarget2D(
                                        currentGD, targetW, targetH, false,
                                        Microsoft.Xna.Framework.Graphics.SurfaceFormat.Color,
                                        Microsoft.Xna.Framework.Graphics.DepthFormat.None,
                                        0,
                                        Microsoft.Xna.Framework.Graphics.RenderTargetUsage.PreserveContents);
                                    rt.Name = "@Game1.screen";
                                    if (screenProp?.GetSetMethod(true) != null) screenProp.SetValue(inst, rt);
                                    else screenField?.SetValue(inst, rt);
                                    EngineLogger.Log($"[GameHost] Initialized instance screen render target ({targetW}x{targetH}).");
                                }
                                catch (Exception sEx)
                                {
                                    EngineLogger.LogWarning($"[GameHost] Failed to initialize instance screen: {sEx.Message}");
                                }
                            }

                            var uiScreenProp = instType.GetProperty("uiScreen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            var uiScreenField = instType.GetField("_uiScreen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                             ?? instType.GetField("uiScreen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            var currentUiScreen = uiScreenProp?.GetValue(inst) ?? uiScreenField?.GetValue(inst);
                            if (currentUiScreen == null)
                            {
                                try
                                {
                                    var rt = new Microsoft.Xna.Framework.Graphics.RenderTarget2D(
                                        currentGD, targetW, targetH, false,
                                        Microsoft.Xna.Framework.Graphics.SurfaceFormat.Color,
                                        Microsoft.Xna.Framework.Graphics.DepthFormat.None,
                                        0,
                                        Microsoft.Xna.Framework.Graphics.RenderTargetUsage.PreserveContents);
                                    rt.Name = "@Game1.uiScreen";
                                    if (uiScreenProp?.GetSetMethod(true) != null) uiScreenProp.SetValue(inst, rt);
                                    else uiScreenField?.SetValue(inst, rt);
                                    EngineLogger.Log($"[GameHost] Initialized instance uiScreen render target ({targetW}x{targetH}).");
                                }
                                catch (Exception uiEx)
                                {
                                    EngineLogger.LogWarning($"[GameHost] Failed to initialize instance uiScreen: {uiEx.Message}");
                                }
                            }

                            // 4b. Synchronize Game1.viewport, Game1.uiViewport, and activeClickableMenu to full Retina resolution (1792x828)
                            if (game1Type != null)
                            {
                                if (!_windowSizeSynchronized)
                                {
                                    try
                                    {
                                        var vpField = game1Type.GetField("viewport", BindingFlags.Static | BindingFlags.Public);
                                        var uiVpField = game1Type.GetField("uiViewport", BindingFlags.Static | BindingFlags.Public);
                                        if (vpField != null)
                                        {
                                            var newVpObj = Activator.CreateInstance(vpField.FieldType, new object[] { 0, 0, targetW, targetH });
                                            vpField.SetValue(null, newVpObj);
                                            if (uiVpField != null) uiVpField.SetValue(null, newVpObj);

                                            var swsMethod = inst.GetType().GetMethod("SetWindowSize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                            swsMethod?.Invoke(inst, new object[] { targetW, targetH });
                                            _windowSizeSynchronized = true;
                                            EngineLogger.Log($"[GameHost] Initial window and viewport size synchronized to {targetW}x{targetH}.");
                                        }
                                    }
                                    catch (Exception vpEx)
                                    {
                                        EngineLogger.LogWarning($"[GameHost] Viewport sync warning: {vpEx.Message}");
                                        _windowSizeSynchronized = true;
                                    }
                                }

                                if (!_menuLayoutSynchronized)
                                {
                                    try
                                    {
                                        var menuProp = game1Type.GetProperty("activeClickableMenu", BindingFlags.Static | BindingFlags.Public);
                                        var menu = menuProp?.GetValue(null);
                                        if (menu != null)
                                        {
                                            var gwscMethod = menu.GetType().GetMethod("gameWindowSizeChanged", BindingFlags.Public | BindingFlags.Instance);
                                            gwscMethod?.Invoke(menu, new object[] { new Rectangle(0, 0, targetW, targetH), new Rectangle(0, 0, targetW, targetH) });
                                            EngineLogger.Log($"[GameHost] Synchronized activeClickableMenu ({menu.GetType().Name}) to {targetW}x{targetH}.");
                                            _menuLayoutSynchronized = true;
                                        }
                                    }
                                    catch (Exception mEx)
                                    {
                                        EngineLogger.LogWarning($"[GameHost] Menu sync warning: {mEx.Message}");
                                    }
                                }
                            }

                        }
                    }
                }

                // 5. Ensure Game1.spriteBatch is valid and reset if left begun
                if (game1Type != null)
                {
                    try
                    {
                        var sbField = game1Type.GetField("spriteBatch", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (sbField != null)
                        {
                            var sbObj = sbField.GetValue(null);
                            if (sbObj == null)
                            {
                                currentGD = runner.GraphicsDevice ?? (gdm as GraphicsDeviceManager)?.GraphicsDevice;
                                if (currentGD != null)
                                {
                                    var sb = new Microsoft.Xna.Framework.Graphics.SpriteBatch(currentGD);
                                    sbField.SetValue(null, sb);
                                    EngineLogger.Log("[GameHost] Initialized Game1.spriteBatch static instance.");
                                }
                            }
                            else if (sbObj is Microsoft.Xna.Framework.Graphics.SpriteBatch sbInstance)
                            {
                                Input.TouchOverlay.SafeResetSpriteBatch(sbInstance);
                            }
                        }
                    }
                    catch (Exception sbEx)
                    {
                        EngineLogger.LogWarning($"[GameHost] Game1.spriteBatch initialization warning: {sbEx.Message}");
                    }
                }

                // 6. Ensure Game1.defaultDeviceViewport is valid and matches graphics backbuffer
                if (game1Type != null)
                {
                    var ddvField = game1Type.GetField("defaultDeviceViewport", BindingFlags.Static | BindingFlags.Public);
                    if (ddvField != null)
                    {
                        currentGD = runner.GraphicsDevice ?? (gdm as GraphicsDeviceManager)?.GraphicsDevice;
                        if (currentGD != null)
                        {
                            int vpW = Math.Max(currentGD.PresentationParameters.BackBufferWidth, currentGD.PresentationParameters.BackBufferHeight);
                            int vpH = Math.Min(currentGD.PresentationParameters.BackBufferWidth, currentGD.PresentationParameters.BackBufferHeight);
                            if (vpW <= 0) vpW = 1792;
                            if (vpH <= 0) vpH = 828;

                            if (currentGD.Viewport.Width < currentGD.Viewport.Height)
                            {
                                currentGD.Viewport = new Microsoft.Xna.Framework.Graphics.Viewport(0, 0, vpW, vpH);
                                currentGD.PresentationParameters.BackBufferWidth = vpW;
                                currentGD.PresentationParameters.BackBufferHeight = vpH;
                                currentGD.PresentationParameters.DisplayOrientation = Microsoft.Xna.Framework.DisplayOrientation.LandscapeLeft;
                                EngineLogger.Log($"[GameHost] Corrected GraphicsDevice Viewport to Landscape ({vpW}x{vpH})");
                            }

                            var vp = (Microsoft.Xna.Framework.Graphics.Viewport)(ddvField.GetValue(null) ?? default(Microsoft.Xna.Framework.Graphics.Viewport));
                            if (vp.Width <= 0 || vp.Height <= 0 || vp.Width < vp.Height)
                            {
                                ddvField.SetValue(null, currentGD.Viewport);
                                EngineLogger.Log($"[GameHost] Synchronized Game1.defaultDeviceViewport to {currentGD.Viewport.Width}x{currentGD.Viewport.Height}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[GameHost] ReviveGraphicsDeviceAndInstances warning: {ex.Message}");
            }
        }

        public static void AttachTouchOverlayToGameRunner()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var runnerType = asm.GetType("StardewValley.GameRunner");
                    if (runnerType != null)
                    {
                        var instanceField = runnerType.GetField("instance", BindingFlags.Static | BindingFlags.Public);
                        var runner = instanceField?.GetValue(null) as Game;
                        if (runner != null && runner.Components != null)
                        {
                            foreach (var comp in runner.Components)
                            {
                                if (comp is TouchOverlay)
                                {
                                    EngineLogger.Log("[GameHost] TouchOverlay already attached to GameRunner.");
                                    return;
                                }
                            }

                            AttachTouchOverlay(runner);
                            EngineLogger.Log("[GameHost] Successfully attached TouchOverlay to GameRunner.");
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[GameHost] Could not attach TouchOverlay to GameRunner: {ex.Message}");
            }
        }

        public static void PreInitAudio()
        {
            try
            {
                AppDelegate.ConfigureAudioSession();

                // Pre-initialize OpenALSoundController if present
                var mgAsm = typeof(Microsoft.Xna.Framework.Game).Assembly;
                var oalType = mgAsm.GetType("Microsoft.Xna.Framework.Audio.OpenALSoundController");
                if (oalType != null)
                {
                    var ensureInitMethod = oalType.GetMethod("EnsureInitialized", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    ensureInitMethod?.Invoke(null, null);
                    EngineLogger.Log("[GameHost] OpenALSoundController pre-initialized successfully.");
                }
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[GameHost] PreInitAudio warning: {ex.Message}");
            }
        }

        public static UIViewController? GetActiveRootViewController()
        {
            var kw = UIApplication.SharedApplication.KeyWindow ?? UIApplication.SharedApplication.Windows.FirstOrDefault(w => w != null);
            return kw?.RootViewController ?? _activeGameVC;
        }

        public class NonDisposingStreamWriter : StreamWriter
        {
            private readonly System.Text.StringBuilder _buffer = new System.Text.StringBuilder();

            public NonDisposingStreamWriter(Stream stream, System.Text.Encoding encoding) : base(stream, encoding) { }

            public override void WriteLine(string? value)
            {
                base.WriteLine(value);
                try
                {
                    string fullLine;
                    lock (_buffer)
                    {
                        if (_buffer.Length > 0)
                        {
                            _buffer.Append(value ?? string.Empty);
                            fullLine = _buffer.ToString();
                            _buffer.Clear();
                        }
                        else
                        {
                            fullLine = value ?? string.Empty;
                        }
                    }
                    if (!string.IsNullOrEmpty(fullLine))
                    {
                        EngineLogger.Log($"[SMAPI] {fullLine}");
                    }
                }
                catch { }
            }

            public override void Write(string? value)
            {
                base.Write(value);
                if (string.IsNullOrEmpty(value)) return;
                try
                {
                    lock (_buffer)
                    {
                        _buffer.Append(value);
                        string str = _buffer.ToString();
                        int lastNewline = str.LastIndexOf('\n');
                        if (lastNewline >= 0)
                        {
                            string ready = str.Substring(0, lastNewline).TrimEnd('\r');
                            _buffer.Remove(0, lastNewline + 1);
                            foreach (var line in ready.Split('\n'))
                            {
                                string trimmed = line.TrimEnd('\r');
                                if (!string.IsNullOrEmpty(trimmed))
                                {
                                    EngineLogger.Log($"[SMAPI] {trimmed}");
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            public override void Write(char[] buffer, int index, int count)
            {
                base.Write(buffer, index, count);
                if (buffer != null && count > 0)
                {
                    Write(new string(buffer, index, count));
                }
            }

            public override void Write(char value)
            {
                base.Write(value);
                Write(value.ToString());
            }

            protected override void Dispose(bool disposing)
            {
                try { Flush(); } catch { }
                // Keep stream alive forever - do not call base.Dispose(disposing)
            }

            public override void Close()
            {
                try { Flush(); } catch { }
                // Keep stream alive forever - do not close
            }
        }

        public static void ReviveSMAPILogFile(object? core)
        {
            if (core == null) return;
            try
            {
                var coreType = core.GetType();
                var lmField = coreType.GetField("LogManager", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                var lm = lmField?.GetValue(core);
                if (lm != null)
                {
                    var lfmField = lm.GetType().GetField("LogFile", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    var lfm = lfmField?.GetValue(lm);
                    if (lfm != null)
                    {
                        var streamField = lfm.GetType().GetField("Stream", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        var stream = streamField?.GetValue(lfm) as StreamWriter;
                        var pathProp = lfm.GetType().GetProperty("Path", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        string? logPath = pathProp?.GetValue(lfm) as string;

                        // Redirect SMAPI log from hidden .config folder to visible Documents/ErrorLogs/SMAPI-latest.txt
                        string visibleLogPath = Path.Combine(LogsDir, "SMAPI-latest.txt");
                        if (!string.IsNullOrEmpty(LogsDir))
                        {
                            try
                            {
                                if (pathProp != null && pathProp.CanWrite)
                                {
                                    pathProp.SetValue(lfm, visibleLogPath);
                                }
                            }
                            catch { }

                            try
                            {
                                for (Type? t = lfm.GetType(); t != null; t = t.BaseType)
                                {
                                    var pf = t.GetField("<Path>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
                                          ?? t.GetField("_path", BindingFlags.NonPublic | BindingFlags.Instance)
                                          ?? t.GetField("Path", BindingFlags.NonPublic | BindingFlags.Instance);
                                    pf?.SetValue(lfm, visibleLogPath);
                                }
                            }
                            catch { }

                            logPath = visibleLogPath;
                        }

                        bool needsRevival = false;
                        if (stream == null || !(stream is NonDisposingStreamWriter))
                        {
                            needsRevival = true;
                        }
                        else
                        {
                            try
                            {
                                if (stream.BaseStream == null || !stream.BaseStream.CanWrite)
                                {
                                    needsRevival = true;
                                }
                            }
                            catch
                            {
                                needsRevival = true;
                            }
                        }

                        if (needsRevival && !string.IsNullOrEmpty(logPath))
                        {
                            var dir = Path.GetDirectoryName(logPath);
                            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            {
                                Directory.CreateDirectory(dir);
                            }
                            var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                            var newWriter = new NonDisposingStreamWriter(fs, System.Text.Encoding.UTF8) { AutoFlush = true };
                            streamField?.SetValue(lfm, newWriter);
                            EngineLogger.Log($"[GameHost] Successfully installed NonDisposingStreamWriter to '{logPath}'.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[GameHost] ReviveSMAPILogFile error: {ex.Message}");
            }
        }
    }
}

namespace Mono
{
    public static class Runtime
    {
        public static string GetDisplayName() => "Mono on .NET 8 (iOS)";
    }
}
