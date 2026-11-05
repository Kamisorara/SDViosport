using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Tools;

namespace SDViOSTouchControls
{
    public class ModEntry : Mod
    {
        private bool _initialized = false;
        private int _nativeWidth = 1792;
        private int _nativeHeight = 828;
        private static int _staticNativeWidth = 1792;
        private static int _staticNativeHeight = 828;

        private static int _assetEventCount = 0;

        public static IMonitor? ModMonitor { get; private set; }
        public static bool VerboseInputLogging { get; set; } = false;
        private static IClickableMenu? _lastTickMenu = null;
        public static bool IsSavingOrNewDay { get; set; } = false;

        public static string GetCompactStackTrace(int skipFrames = 2, int maxFrames = 6)
        {
            try
            {
                var st = new System.Diagnostics.StackTrace(skipFrames, false);
                var frames = st.GetFrames();
                if (frames == null || frames.Length == 0) return "unknown";
                var list = new System.Collections.Generic.List<string>();
                for (int i = 0; i < Math.Min(frames.Length, maxFrames); i++)
                {
                    var m = frames[i].GetMethod();
                    if (m != null)
                    {
                        list.Add($"{m.DeclaringType?.Name}.{m.Name}");
                    }
                }
                return string.Join(" -> ", list);
            }
            catch
            {
                return "stack_err";
            }
        }

        public override void Entry(IModHelper helper)
        {
            ModMonitor = Monitor;
            TouchOverlaySettings.LogAction = msg => Monitor.Log(msg, LogLevel.Info);
            TouchVirtualPad.LogAction = msg => Monitor.Log(msg, LogLevel.Info);

            Monitor.Log("[SDViOSTouchControls] Initializing native touch controls mod...", LogLevel.Info);

            // 1. Determine native hardware display resolution
            DetermineNativeResolution();

            // 2. Neutralize legacy SDViOS.Input.TouchVirtualPad from fighting with input
            NeutralizeOldTouchVirtualPad();

            // 3. Apply Harmony patches to route Mouse, Keyboard, and Gamepad to TouchVirtualPad & force CaseInsensitivePaths
            ApplyHarmonyPatches();

            // Force native full-screen resolution immediately at mod startup
            EnsureNativeResolution(force: true);

            // 4. Disable raw MonoGame touch-to-mouse translation to stop cursor spazzing
            try
            {
                TouchPanel.EnableMouseTouchPoint = false;
                TouchPanel.EnableMouseGestures = false;
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Warning setting TouchPanel flags: {ex.Message}", LogLevel.Warn);
            }

            // 5. Wrap Game1.hooks with TouchDelegatingModHooks
            EnsureModHooksWrapped();

            // 6. Register SMAPI events
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.UpdateTicking += OnUpdateTicking; // Critical: Runs BEFORE Game1.Update()!
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.Display.MenuChanged += OnMenuChanged;
            helper.Events.Display.RenderedStep += OnRenderedStep;
            helper.Events.Display.Rendered += OnRendered;
            helper.Events.Content.AssetReady += OnAssetReady;
            helper.Events.GameLoop.DayEnding += OnDayEnding;
            helper.Events.GameLoop.Saving += OnSaving;
            helper.Events.GameLoop.Saved += OnSaved;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.DayStarted += OnDayStarted;
            helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
            helper.Events.Content.AssetsInvalidated += OnAssetsInvalidated;
            helper.Events.Input.ButtonPressed += OnButtonPressed;

            // 7. Initialize QuickSave (midday save state manager)
            QuickSaveManager.Initialize(helper, Monitor);

            // Register Console Commands for QuickSave / QuickLoad
            helper.ConsoleCommands.Add("quick_save", "Quick save the game mid-day from anywhere.", (cmd, args) => QuickSaveManager.Instance?.QuickSave());
            helper.ConsoleCommands.Add("quick_load", "Quick load the midday save state for this farm.", (cmd, args) => QuickSaveManager.Instance?.QuickLoad());

            // 7.5 Deferred Map Loader (optimization #5: defer non-essential maps during save load)
            DeferredMapLoader.Initialize(Monitor);
            DeferredMapLoader.Enabled = TouchOverlaySettings.Instance.EnableDeferredMapLoading;
            helper.ConsoleCommands.Add("toggle_deferred_maps", "Toggle deferred map loading optimization on/off.", (cmd, args) =>
            {
                DeferredMapLoader.Enabled = !DeferredMapLoader.Enabled;
                TouchOverlaySettings.Instance.EnableDeferredMapLoading = DeferredMapLoader.Enabled;
                TouchOverlaySettings.Instance.Save();
                Monitor.Log($"[DeferredMapLoader] Deferred map loading is now {(DeferredMapLoader.Enabled ? "ENABLED" : "DISABLED")}.", LogLevel.Info);
            });

            // 8. Background memory watchdog (real iOS headroom + emergency GC during synchronous loads)
            try { MemoryWatchdog.Start(Monitor); }
            catch (Exception ex) { Monitor.Log($"[MEM_DEBUG] Watchdog failed to start: {ex.Message}", LogLevel.Warn); }

            // 9. Core Optimizations: Relocate .disabled_* mods, buffer EngineLogger I/O, and initialize CADisplayLink frame pacing
            RelocateDisabledModsOnBoot();
            ConfigureBufferedEngineLogger();
            UpdateDisplayLinkFrameRate(force: true);

            Monitor.Log("[SDViOSTouchControls] Mod initialized successfully.", LogLevel.Info);
        }

        private void DetermineNativeResolution()
        {
            try
            {
                var dm = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode;
                int maxDim = Math.Max(dm.Width, dm.Height);
                int minDim = Math.Min(dm.Width, dm.Height);

                if (maxDim > 1000 && minDim > 500)
                {
                    _nativeWidth = maxDim;
                    _nativeHeight = minDim;
                }
                else
                {
                    _nativeWidth = 1792;
                    _nativeHeight = 828;
                }

                _staticNativeWidth = _nativeWidth;
                _staticNativeHeight = _nativeHeight;

                Monitor.Log($"[SDViOSTouchControls] Detected hardware display resolution: {_nativeWidth}x{_nativeHeight}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _nativeWidth = 1792;
                _nativeHeight = 828;
                _staticNativeWidth = 1792;
                _staticNativeHeight = 828;
                Monitor.Log($"[SDViOSTouchControls] Using default iPhone landscape resolution: 1792x828 ({ex.Message})", LogLevel.Info);
            }
        }

        private void RemoveLegacyComponentsFrom(GameComponentCollection? components, string ownerName)
        {
            if (components == null) return;
            for (int i = components.Count - 1; i >= 0; i--)
            {
                var c = components[i];
                string? fullName = c?.GetType().FullName;
                if (c != null && fullName != null && fullName.Contains("TouchOverlay", StringComparison.Ordinal))
                {
                    if (c is GameComponent gc) gc.Enabled = false;
                    if (c is DrawableGameComponent dgc) dgc.Visible = false;
                    components.RemoveAt(i);
                    Monitor.Log($"[SDViOSTouchControls] Removed legacy {fullName} from {ownerName}.Components.", LogLevel.Info);
                }
            }
        }

        private void NeutralizeOldTouchVirtualPad()
        {
            try
            {
                var sdvIosAsm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SDViOS");
                if (sdvIosAsm != null)
                {
                    // 1. Remove TouchOverlay from both Game1.game1.Components and GameRunner.instance.Components
                    try
                    {
                        RemoveLegacyComponentsFrom(Game1.game1?.Components, "Game1");
                        RemoveLegacyComponentsFrom(StardewValley.GameRunner.instance?.Components, "GameRunner");
                    }
                    catch (Exception cex)
                    {
                        Monitor.Log($"[SDViOSTouchControls] Note removing legacy component: {cex.Message}", LogLevel.Trace);
                    }

                    // 2. Neutralize TouchOverlay & TouchVirtualPad
                    var overlayType = sdvIosAsm.GetType("SDViOS.Input.TouchOverlay");
                    var tvpType = sdvIosAsm.GetType("SDViOS.Input.TouchVirtualPad");

                    if (tvpType != null)
                    {
                        // Nullify static reflection hooks so legacy TVP never forwards input
                        tvpType.GetField("_reflectionInitialized", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, true);
                        tvpType.GetField("_inputInstance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, null);
                        tvpType.GetField("_currentMouseStateField", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, null);
                        tvpType.GetField("_currentGamepadStateField", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, null);
                        tvpType.GetField("_currentKeyboardStateField", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, null);
                        tvpType.GetField("_lastCursorMotionWasMouseField", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, null);
                        tvpType.GetField("_mousePrimaryWindowField", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, null);
                        tvpType.GetField("_gameWindowMouseStateField", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, null);
                        tvpType.GetField("_gamepadControlsField", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, null);
                        tvpType.GetField("_setKeysMethod", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, null);

                        Monitor.Log("[SDViOSTouchControls] Successfully neutralized legacy SDViOS.Input.TouchVirtualPad fields.", LogLevel.Info);
                    }

                    // 3. Harmony detours to skip all legacy execution
                    try
                    {
                        var harmony = new Harmony("com.deadakj.sdviostouchcontrols");

                        if (overlayType != null)
                        {
                            var mOverlayUpdate = overlayType.GetMethod("Update", BindingFlags.Public | BindingFlags.Instance);
                            if (mOverlayUpdate != null)
                                harmony.Patch(mOverlayUpdate, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixDisableLegacyForward), BindingFlags.Static | BindingFlags.NonPublic)));

                            var mOverlayDraw = overlayType.GetMethod("Draw", BindingFlags.Public | BindingFlags.Instance);
                            if (mOverlayDraw != null)
                                harmony.Patch(mOverlayDraw, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixDisableLegacyForward), BindingFlags.Static | BindingFlags.NonPublic)));
                        }

                        if (tvpType != null)
                        {
                            var forwardMethod = tvpType.GetMethod("ForwardInputToGame", BindingFlags.Public | BindingFlags.Instance);
                            if (forwardMethod != null)
                                harmony.Patch(forwardMethod, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixDisableLegacyForward), BindingFlags.Static | BindingFlags.NonPublic)));

                            var mTvpUpdate = tvpType.GetMethod("Update", BindingFlags.Public | BindingFlags.Instance);
                            if (mTvpUpdate != null)
                                harmony.Patch(mTvpUpdate, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixDisableLegacyForward), BindingFlags.Static | BindingFlags.NonPublic)));

                            var mTvpDraw = tvpType.GetMethod("Draw", BindingFlags.Public | BindingFlags.Instance);
                            if (mTvpDraw != null)
                                harmony.Patch(mTvpDraw, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixDisableLegacyForward), BindingFlags.Static | BindingFlags.NonPublic)));
                        }

                        Monitor.Log("[SDViOSTouchControls] Successfully applied Harmony detours to disable legacy TouchOverlay & TouchVirtualPad.", LogLevel.Info);
                    }
                    catch (Exception hex)
                    {
                        Monitor.Log($"[SDViOSTouchControls] Harmony legacy patch note: {hex.Message}", LogLevel.Trace);
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Error neutralizing legacy touch pad: {ex.Message}", LogLevel.Warn);
            }
        }

        private static bool PrefixDisableLegacyForward()
        {
            return false;
        }

        private void ApplyHarmonyPatches()
        {
            var harmony = new Harmony("com.deadakj.sdviostouchcontrols");

            // 1. Mouse Patches
            try
            {
                var mGetState0 = typeof(Mouse).GetMethod("GetState", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (mGetState0 != null)
                {
                    harmony.Patch(mGetState0, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixMouseGetState), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Mouse.GetState().", LogLevel.Info);
                }

                var mGetStateWin = typeof(Mouse).GetMethod("GetState", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameWindow) }, null);
                if (mGetStateWin != null)
                {
                    harmony.Patch(mGetStateWin, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixMouseGetState), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Mouse.GetState(GameWindow).", LogLevel.Info);
                }

                var mPlatformGetState = typeof(Mouse).GetMethod("PlatformGetState", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(GameWindow) }, null);
                if (mPlatformGetState != null)
                {
                    harmony.Patch(mPlatformGetState, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixMouseGetState), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Mouse.PlatformGetState(GameWindow).", LogLevel.Info);
                }

                var mInputStateGetMouseState = typeof(InputState).GetMethod("GetMouseState", BindingFlags.Public | BindingFlags.Instance);
                if (mInputStateGetMouseState != null)
                {
                    harmony.Patch(mInputStateGetMouseState, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixMouseGetState), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched InputState.GetMouseState().", LogLevel.Info);
                }

                var mSetMousePosRaw = typeof(Game1).GetMethod("setMousePositionRaw", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int), typeof(int) }, null);
                if (mSetMousePosRaw != null)
                {
                    harmony.Patch(mSetMousePosRaw, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixSetMousePositionRaw), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Game1.setMousePositionRaw().", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Error applying Harmony mouse patches: {ex.Message}", LogLevel.Error);
            }

            // 2. Keyboard Focus & State Patches
            try
            {
                var mHasKbFocus = typeof(Game1).GetMethod("HasKeyboardFocus", BindingFlags.Public | BindingFlags.Instance);
                if (mHasKbFocus != null)
                {
                    harmony.Patch(mHasKbFocus, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixTrue), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Game1.HasKeyboardFocus -> true.", LogLevel.Info);
                }

                var mG1GetKb = typeof(Game1).GetMethod("GetKeyboardState", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (mG1GetKb != null)
                {
                    harmony.Patch(mG1GetKb, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixGetKeyboardState), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Game1.GetKeyboardState().", LogLevel.Info);
                }

                var mInputGetKb = typeof(InputState).GetMethod("GetKeyboardState", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (mInputGetKb != null)
                {
                    harmony.Patch(mInputGetKb, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixGetKeyboardState), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched InputState.GetKeyboardState().", LogLevel.Info);
                }

                var mXnaKb = typeof(Microsoft.Xna.Framework.Input.Keyboard).GetMethod("GetState", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (mXnaKb != null)
                {
                    harmony.Patch(mXnaKb, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixGetKeyboardState), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Microsoft.Xna.Framework.Input.Keyboard.GetState().", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Error in keyboard patches: {ex.Message}", LogLevel.Error);
            }

            // 3. GamePad State Patches
            try
            {
                var mInputGetPad = typeof(InputState).GetMethod("GetGamePadState", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (mInputGetPad != null)
                {
                    harmony.Patch(mInputGetPad, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixGetGamePadState), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched InputState.GetGamePadState().", LogLevel.Info);
                }

                var mXnaPad = typeof(Microsoft.Xna.Framework.Input.GamePad).GetMethod("GetState", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(PlayerIndex) }, null);
                if (mXnaPad != null)
                {
                    harmony.Patch(mXnaPad, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixGetGamePadState), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Microsoft.Xna.Framework.Input.GamePad.GetState(PlayerIndex).", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Error in GamePad patches: {ex.Message}", LogLevel.Error);
            }

            // 4. ModHooks wrapping handled via TouchDelegatingModHooks (non-Harmony native subclass)
            EnsureModHooksWrapped();

            // 5. SMAPI SInputState Patches
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var sInputStateType = asm.GetType("StardewModdingAPI.Framework.Input.SInputState");
                    if (sInputStateType != null)
                    {
                        var sGetKb = sInputStateType.GetMethod("GetKeyboardState", BindingFlags.Public | BindingFlags.Instance);
                        if (sGetKb != null)
                            harmony.Patch(sGetKb, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixGetKeyboardState), BindingFlags.Static | BindingFlags.NonPublic)));

                        var sPropKb = sInputStateType.GetProperty("KeyboardState", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                        if (sPropKb != null)
                            harmony.Patch(sPropKb, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixGetKeyboardState), BindingFlags.Static | BindingFlags.NonPublic)));

                        var sGetMouse = sInputStateType.GetMethod("GetMouseState", BindingFlags.Public | BindingFlags.Instance);
                        if (sGetMouse != null)
                            harmony.Patch(sGetMouse, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixMouseGetState), BindingFlags.Static | BindingFlags.NonPublic)));

                        var sPropMouse = sInputStateType.GetProperty("MouseState", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                        if (sPropMouse != null)
                            harmony.Patch(sPropMouse, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixMouseGetState), BindingFlags.Static | BindingFlags.NonPublic)));

                        var sGetPad = sInputStateType.GetMethod("GetGamePadState", BindingFlags.Public | BindingFlags.Instance);
                        if (sGetPad != null)
                            harmony.Patch(sGetPad, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixGetGamePadState), BindingFlags.Static | BindingFlags.NonPublic)));

                        var sPropPad = sInputStateType.GetProperty("GamePadState", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                        if (sPropPad != null)
                            harmony.Patch(sPropPad, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixGetGamePadState), BindingFlags.Static | BindingFlags.NonPublic)));

                        Monitor.Log("[SDViOSTouchControls] Harmony patched SInputState (Mouse, Keyboard, GamePad).", LogLevel.Info);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Note patching SInputState: {ex.Message}", LogLevel.Warn);
            }

            // 6. SMAPI Case-Insensitive Path Resolution
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var sConfigType = asm.GetType("StardewModdingAPI.Framework.Models.SConfig");
                    if (sConfigType != null)
                    {
                        var prop = sConfigType.GetProperty("UseCaseInsensitivePaths", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                        if (prop != null)
                        {
                            harmony.Patch(prop, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixTrue), BindingFlags.Static | BindingFlags.NonPublic)));
                            Monitor.Log("[SDViOSTouchControls] Harmony patched SConfig.UseCaseInsensitivePaths -> true.", LogLevel.Info);
                        }
                    }

                    var scoreType = asm.GetType("StardewModdingAPI.Framework.SCore");
                    if (scoreType != null)
                    {
                        var instProp = scoreType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        var scoreInst = instProp?.GetValue(null);
                        if (scoreInst != null)
                        {
                            var settingsProp = scoreType.GetProperty("Settings", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            var settings = settingsProp?.GetValue(scoreInst);
                            if (settings != null)
                            {
                                var prop = settings.GetType().GetProperty("UseCaseInsensitivePaths", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                prop?.SetValue(settings, true);
                                Monitor.Log("[SDViOSTouchControls] Successfully enforced SCore.Instance.Settings.UseCaseInsensitivePaths = true via reflection!", LogLevel.Info);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Note setting case insensitive paths: {ex.Message}", LogLevel.Warn);
            }

            // 7. GraphicsDeviceManager and Window Resolution Patches
            try
            {
                var gdmType = typeof(GraphicsDeviceManager);
                var pWidthGet = gdmType.GetProperty("PreferredBackBufferWidth", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                if (pWidthGet != null)
                {
                    harmony.Patch(pWidthGet, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixPreferredBackBufferWidth), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched GraphicsDeviceManager.get_PreferredBackBufferWidth -> native.", LogLevel.Info);
                }

                var pWidthSet = gdmType.GetProperty("PreferredBackBufferWidth", BindingFlags.Public | BindingFlags.Instance)?.GetSetMethod();
                if (pWidthSet != null)
                {
                    harmony.Patch(pWidthSet, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixSetPreferredWidth), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched GraphicsDeviceManager.set_PreferredBackBufferWidth -> native.", LogLevel.Info);
                }

                var pHeightGet = gdmType.GetProperty("PreferredBackBufferHeight", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                if (pHeightGet != null)
                {
                    harmony.Patch(pHeightGet, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixPreferredBackBufferHeight), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched GraphicsDeviceManager.get_PreferredBackBufferHeight -> native.", LogLevel.Info);
                }

                var pHeightSet = gdmType.GetProperty("PreferredBackBufferHeight", BindingFlags.Public | BindingFlags.Instance)?.GetSetMethod();
                if (pHeightSet != null)
                {
                    harmony.Patch(pHeightSet, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixSetPreferredHeight), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched GraphicsDeviceManager.set_PreferredBackBufferHeight -> native.", LogLevel.Info);
                }

                var pFull = gdmType.GetProperty("IsFullScreen", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                if (pFull != null)
                {
                    harmony.Patch(pFull, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixFalseBool), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched GraphicsDeviceManager.IsFullScreen -> false.", LogLevel.Info);
                }

                var mSetWindowSize = typeof(Game1).GetMethod("SetWindowSize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(int), typeof(int) }, null);
                if (mSetWindowSize != null)
                {
                    harmony.Patch(mSetWindowSize, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixSetWindowSize), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Game1.SetWindowSize -> locked to 1792x828.", LogLevel.Info);
                }

                var mClientChanged = typeof(Game1).GetMethod("Window_ClientSizeChanged", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(object), typeof(EventArgs) }, null);
                if (mClientChanged != null)
                {
                    harmony.Patch(mClientChanged, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixWindowClientSizeChanged), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Game1.Window_ClientSizeChanged.", LogLevel.Info);
                }

                var mRunnerClientChanged = typeof(GameRunner).GetMethod("Window_ClientSizeChanged", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(object), typeof(EventArgs) }, null);
                if (mRunnerClientChanged != null)
                {
                    harmony.Patch(mRunnerClientChanged, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixWindowClientSizeChanged), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched GameRunner.Window_ClientSizeChanged.", LogLevel.Info);
                }

                var mApplyChanges = gdmType.GetMethod("ApplyChanges", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (mApplyChanges != null)
                {
                    harmony.Patch(mApplyChanges, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixApplyChanges), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched GraphicsDeviceManager.ApplyChanges -> skip redundant calls.", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Note patching GraphicsDeviceManager: {ex.Message}", LogLevel.Warn);
            }

            // 8. Content Patcher Animations Exception Silencer
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var cpAnimType = asm.GetType("ContentPatcherAnimations.Mod");
                    if (cpAnimType != null)
                    {
                        var mUpdateTicked = cpAnimType.GetMethod("OnUpdateTicked", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (mUpdateTicked != null)
                        {
                            harmony.Patch(mUpdateTicked, finalizer: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(FinalizerSilenceException), BindingFlags.Static | BindingFlags.NonPublic)));
                            Monitor.Log("[SDViOSTouchControls] Harmony patched ContentPatcherAnimations.Mod.OnUpdateTicked with exception silencer.", LogLevel.Info);
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Note patching ContentPatcherAnimations: {ex.Message}", LogLevel.Trace);
            }

            // 9. Memory Optimization: Defer Tilesheet Preloading for Non-Essential Maps
            DeferredMapLoader.ApplyHarmonyPatches(harmony);

            try
            {
                // CoreAssetPropagator, ModContentManager, ContentManager, and TitleContainer
                try
                {
                    var mOpenStream = typeof(Microsoft.Xna.Framework.TitleContainer).GetMethod("OpenStream", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                    if (mOpenStream != null)
                    {
                        harmony.Patch(mOpenStream, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixTitleContainerOpenStream), BindingFlags.Static | BindingFlags.NonPublic)));
                        Monitor.Log("[SDViOSTouchControls] Harmony patched TitleContainer.OpenStream -> support absolute iOS mod paths.", LogLevel.Info);
                    }

                    var mCmOpenStream = typeof(Microsoft.Xna.Framework.Content.ContentManager).GetMethod("OpenStream", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                    if (mCmOpenStream != null)
                    {
                        harmony.Patch(mCmOpenStream, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixContentManagerOpenStream), BindingFlags.Static | BindingFlags.NonPublic)));
                        Monitor.Log("[SDViOSTouchControls] Harmony patched ContentManager.OpenStream -> support absolute iOS mod paths.", LogLevel.Info);
                    }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"[SDViOSTouchControls] Note patching TitleContainer/ContentManager.OpenStream: {ex.Message}", LogLevel.Trace);
                }

                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var propType = asm.GetType("StardewModdingAPI.Metadata.CoreAssetPropagator");
                        if (propType != null)
                        {
                            var mPropMap = propType.GetMethod("PropagateMap", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (mPropMap != null)
                            {
                                harmony.Patch(mPropMap, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixPropagateMap), BindingFlags.Static | BindingFlags.NonPublic)));
                                Monitor.Log("[SDViOSTouchControls] Harmony patched CoreAssetPropagator.PropagateMap -> skip during save load.", LogLevel.Info);
                            }

                            var mPropTex = propType.GetMethod("PropagateTexture", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (mPropTex != null)
                            {
                                harmony.Patch(mPropTex, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixPropagateMap), BindingFlags.Static | BindingFlags.NonPublic)));
                                Monitor.Log("[SDViOSTouchControls] Harmony patched CoreAssetPropagator.PropagateTexture -> skip during save load.", LogLevel.Info);
                            }
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"[SDViOSTouchControls] Note patching CoreAssetPropagator: {ex.Message}", LogLevel.Trace);
                }

                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var mcmType = asm.GetType("StardewModdingAPI.Framework.ContentManagers.ModContentManager");
                        if (mcmType != null)
                        {
                            var mTryGetTilesheet = mcmType.GetMethod("TryGetTilesheetAssetName", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (mTryGetTilesheet != null)
                            {
                                harmony.Patch(mTryGetTilesheet, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixTryGetTilesheetAssetName), BindingFlags.Static | BindingFlags.NonPublic)));
                                Monitor.Log("[SDViOSTouchControls] Harmony patched ModContentManager.TryGetTilesheetAssetName -> skip eager texture load during save load.", LogLevel.Info);
                            }
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"[SDViOSTouchControls] Note patching ModContentManager: {ex.Message}", LogLevel.Trace);
                }

                try
                {
                    foreach (var method in typeof(GameLocation).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (method.Name == "loadMap")
                        {
                            try
                            {
                                harmony.Patch(method, postfix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PostfixLocationLoadMap), BindingFlags.Static | BindingFlags.NonPublic)));
                                Monitor.Log("[SDViOSTouchControls] Harmony patched GameLocation.loadMap -> periodic GC during save load.", LogLevel.Info);
                            }
                            catch (Exception ex)
                            {
                                Monitor.Log($"[SDViOSTouchControls] Could not patch GameLocation.loadMap: {ex.Message}", LogLevel.Trace);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"[SDViOSTouchControls] Error patching GameLocation map hooks: {ex.Message}", LogLevel.Warn);
                }

                try
                {
                    foreach (var method in typeof(NPC).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (method.Name == "reloadSprite" || method.Name == "reloadData")
                        {
                            try
                            {
                                harmony.Patch(method, postfix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PostfixNpcReloadSprite), BindingFlags.Static | BindingFlags.NonPublic)));
                                Monitor.Log($"[SDViOSTouchControls] Harmony patched NPC.{method.Name} -> periodic GC during save load.", LogLevel.Info);
                            }
                            catch (Exception ex)
                            {
                                Monitor.Log($"[SDViOSTouchControls] Could not patch NPC.{method.Name}: {ex.Message}", LogLevel.Trace);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"[SDViOSTouchControls] Error patching NPC load hooks: {ex.Message}", LogLevel.Warn);
                }

                try
                {
                    foreach (var method in typeof(Game1).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                    {
                        if (method.Name == "newDayAfterFade" || method.Name == "_newDayAfterFade")
                        {
                            try
                            {
                                harmony.Patch(method,
                                    prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixNewDayAfterFade), BindingFlags.Static | BindingFlags.NonPublic)),
                                    postfix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PostfixNewDayAfterFade), BindingFlags.Static | BindingFlags.NonPublic)));
                                Monitor.Log($"[SDViOSTouchControls] Harmony patched Game1.{method.Name} -> LOH compaction around new day transition.", LogLevel.Info);
                            }
                            catch (Exception ex)
                            {
                                Monitor.Log($"[SDViOSTouchControls] Could not patch Game1.{method.Name}: {ex.Message}", LogLevel.Trace);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"[SDViOSTouchControls] Error patching Game1.newDayAfterFade: {ex.Message}", LogLevel.Warn);
                }

                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name?.IndexOf("FarmTypeManager", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            foreach (var type in asm.GetTypes())
                            {
                                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                                {
                                    if (m.Name == "ForageGeneration" || m.Name == "MonsterGeneration" || m.Name == "OreGeneration" || m.Name == "LargeObjectGeneration" || m.Name == "ProcessObjectExpiration")
                                    {
                                        try
                                        {
                                            harmony.Patch(m, postfix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PostfixForceGC), BindingFlags.Static | BindingFlags.NonPublic)));
                                            Monitor.Log($"[SDViOSTouchControls] Harmony patched FarmTypeManager.{type.Name}.{m.Name} -> LOH compaction.", LogLevel.Info);
                                        }
                                        catch { }
                                    }
                                }
                            }
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"[SDViOSTouchControls] Note hooking FarmTypeManager: {ex.Message}", LogLevel.Trace);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Error patching memory hooks: {ex.Message}", LogLevel.Error);
            }

            // 10. Diagnostics for Menu Lifecycle and Input Interception
            try
            {
                var mExitActiveMenu = typeof(Game1).GetMethod("exitActiveMenu", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (mExitActiveMenu != null)
                {
                    harmony.Patch(mExitActiveMenu, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixExitActiveMenu), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Game1.exitActiveMenu for diagnostics.", LogLevel.Info);
                }

                var mExitThisMenu = typeof(IClickableMenu).GetMethod("exitThisMenu", BindingFlags.Public | BindingFlags.Instance);
                if (mExitThisMenu != null)
                {
                    harmony.Patch(mExitThisMenu, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixExitThisMenu), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched IClickableMenu.exitThisMenu for diagnostics.", LogLevel.Info);
                }

                var mExitNoSound = typeof(IClickableMenu).GetMethod("exitThisMenuNoSound", BindingFlags.Public | BindingFlags.Instance);
                if (mExitNoSound != null)
                {
                    harmony.Patch(mExitNoSound, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixExitThisMenuNoSound), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched IClickableMenu.exitThisMenuNoSound for diagnostics.", LogLevel.Info);
                }

                var mRecvLeftClick = typeof(IClickableMenu).GetMethod("receiveLeftClick", BindingFlags.Public | BindingFlags.Instance);
                if (mRecvLeftClick != null)
                {
                    harmony.Patch(mRecvLeftClick, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixReceiveLeftClick), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched IClickableMenu.receiveLeftClick for diagnostics.", LogLevel.Info);
                }

                var mRecvKey = typeof(IClickableMenu).GetMethod("receiveKeyPress", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Keys) }, null);
                if (mRecvKey != null)
                {
                    harmony.Patch(mRecvKey, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixReceiveKeyPress), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched IClickableMenu.receiveKeyPress for diagnostics.", LogLevel.Info);
                }

                var mRecvPad = typeof(IClickableMenu).GetMethod("receiveGamePadButton", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Buttons) }, null);
                if (mRecvPad != null)
                {
                    harmony.Patch(mRecvPad, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixReceiveGamePadButton), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched IClickableMenu.receiveGamePadButton for diagnostics.", LogLevel.Info);
                }

                var mUpdateActiveMenu = typeof(Game1).GetMethod("updateActiveMenu", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameTime) }, null);
                if (mUpdateActiveMenu != null)
                {
                    harmony.Patch(mUpdateActiveMenu,
                        prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixUpdateActiveMenu), BindingFlags.Static | BindingFlags.NonPublic)),
                        postfix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PostfixUpdateActiveMenu), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Game1.updateActiveMenu for diagnostics.", LogLevel.Info);
                }

                // Input: neutralize vanilla connect/disconnect transition (notification + pause GameMenu on "unplug")
                var mCheckPadMode = typeof(Game1).GetMethod("CheckGamepadMode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (mCheckPadMode != null)
                {
                    harmony.Patch(mCheckPadMode, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixCheckGamepadMode), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Game1.CheckGamepadMode -> skipped completely.", LogLevel.Info);
                }

                // Notifications: suppress "Gamepad mode activated" / "Gamepad disconnected" HUD spam
                var mShowMsg = typeof(Game1).GetMethod("showGlobalMessage", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (mShowMsg != null)
                {
                    harmony.Patch(mShowMsg, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixShowGlobalMessage), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Game1.showGlobalMessage -> suppressing gamepad spam.", LogLevel.Info);
                }

                // Active Menu: intercept and prevent auto-opening GameMenu on disconnect
                var propActiveMenu = typeof(Game1).GetProperty("activeClickableMenu", BindingFlags.Public | BindingFlags.Static);
                var mSetActiveMenu = propActiveMenu?.GetSetMethod();
                if (mSetActiveMenu != null)
                {
                    harmony.Patch(mSetActiveMenu, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixSetActiveClickableMenu), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Game1.set_activeClickableMenu for stack tracing & pause suppression.", LogLevel.Info);
                }

                // Memory: compact heap right before GameMenu allocates all its pages (prevents Jetsam kill on Y press)
                foreach (var ctor in typeof(GameMenu).GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                {
                    try
                    {
                        harmony.Patch(ctor, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixGameMenuCtor), BindingFlags.Static | BindingFlags.NonPublic)));
                        Monitor.Log($"[SDViOSTouchControls] Harmony patched GameMenu ctor ({ctor.GetParameters().Length} params) -> pre-allocation GC.", LogLevel.Info);
                    }
                    catch (Exception cex)
                    {
                        Monitor.Log($"[SDViOSTouchControls] Could not patch GameMenu ctor: {cex.Message}", LogLevel.Warn);
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Note patching menu diagnostics: {ex.Message}", LogLevel.Warn);
            }

            // 7. Action Button & Container / Chest Interaction Patch
            try
            {
                var mDidPlayerRightClick = typeof(Game1).GetMethod("didPlayerJustRightClick", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool) }, null);
                if (mDidPlayerRightClick != null)
                {
                    harmony.Patch(mDidPlayerRightClick, postfix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PostfixDidPlayerJustRightClick), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Game1.didPlayerJustRightClick.", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Error patching didPlayerJustRightClick: {ex.Message}", LogLevel.Error);
            }

            // 8. Fishing Reeling Sound Cleanup Patch
            try
            {
                foreach (var m in typeof(FishingRod).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name == "pullFishFromWater" || m.Name == "doDoneFishing")
                    {
                        try
                        {
                            harmony.Patch(m, postfix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PostfixStopFishingReelSounds), BindingFlags.Static | BindingFlags.NonPublic)));
                            Monitor.Log($"[SDViOSTouchControls] Harmony patched FishingRod.{m.Name} -> stop stuck reel sounds.", LogLevel.Info);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Note patching FishingRod sound cleanup: {ex.Message}", LogLevel.Warn);
            }
        }

        private static void PostfixStopFishingReelSounds()
        {
            StopStuckFishingSounds(forceSweepPool: true);
        }

        private static void PostfixDidPlayerJustRightClick(bool ignoreNonMouseHeldInput, ref bool __result)
        {
            if (__result) return;
            var pad = TouchVirtualPad.Instance;
            if (pad != null && (pad.ButtonA || pad.SimulatedMouseRightDown))
            {
                __result = true;
            }
        }

        private static MethodInfo? _mcmGetModFileGeneric;
        private static MethodInfo? _mcmGetInternalAssetKey;
        private static MethodInfo? _mcmGetContentKey;
        private static FieldInfo? _mcmCoordinatorField;
        private static MethodInfo? _coordParseAssetNameMethod;

        private static bool PrefixTitleContainerOpenStream(string name, ref System.IO.Stream __result)
        {
            if (!string.IsNullOrEmpty(name) && System.IO.Path.IsPathRooted(name))
            {
                try
                {
                    string normalized = name.Replace('\\', System.IO.Path.DirectorySeparatorChar);
                    if (!System.IO.File.Exists(normalized) && !normalized.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase))
                    {
                        string withXnb = normalized + ".xnb";
                        if (System.IO.File.Exists(withXnb))
                            normalized = withXnb;
                    }
                    __result = System.IO.File.OpenRead(normalized);
                    return false;
                }
                catch (Exception ex)
                {
                    throw new Microsoft.Xna.Framework.Content.ContentLoadException("Opening stream error.", ex);
                }
            }
            return true;
        }

        private static bool PrefixContentManagerOpenStream(Microsoft.Xna.Framework.Content.ContentManager __instance, string assetName, ref System.IO.Stream __result)
        {
            try
            {
                string rootDir = __instance.RootDirectory ?? string.Empty;
                if (System.IO.Path.IsPathRooted(rootDir) || System.IO.Path.IsPathRooted(assetName))
                {
                    string fullPath = System.IO.Path.Combine(rootDir, assetName).Replace('\\', System.IO.Path.DirectorySeparatorChar);
                    if (!fullPath.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase))
                    {
                        string withXnb = fullPath + ".xnb";
                        if (System.IO.File.Exists(withXnb))
                            fullPath = withXnb;
                    }
                    if (System.IO.File.Exists(fullPath))
                    {
                        __result = System.IO.File.OpenRead(fullPath);
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Microsoft.Xna.Framework.Content.ContentLoadException("Opening stream error.", ex);
            }
            return true;
        }

        private static bool PrefixTryGetTilesheetAssetName(
            object __instance,
            string modRelativeMapFolder,
            string relativePath,
            ref IAssetName? assetName,
            ref string? error,
            ref bool __result)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                assetName = null;
                __result = true;
                return false;
            }

            try
            {
                // Normalize leading ./
                string fileName = System.IO.Path.GetFileName(relativePath);
                if (fileName.StartsWith('.'))
                {
                    string? dir = System.IO.Path.GetDirectoryName(relativePath);
                    relativePath = System.IO.Path.Combine(dir ?? "", fileName.TrimStart('.'));
                }

                var mcmType = __instance.GetType();

                // 1. If it's a mod-relative file (does not start with or contain ..)
                if (!relativePath.StartsWith("..") && !relativePath.Contains(".."))
                {
                    string modPath = System.IO.Path.Combine(modRelativeMapFolder ?? "", relativePath);
                    if (_mcmGetModFileGeneric == null)
                    {
                        var mGetModFile = mcmType.GetMethod("GetModFile", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (mGetModFile != null)
                        {
                            _mcmGetModFileGeneric = mGetModFile.MakeGenericMethod(typeof(Texture2D));
                        }
                    }
                    if (_mcmGetModFileGeneric != null)
                    {
                        var fileInfo = _mcmGetModFileGeneric.Invoke(__instance, new object[] { modPath }) as System.IO.FileInfo;
                        if (fileInfo != null && fileInfo.Exists)
                        {
                            _mcmGetInternalAssetKey ??= mcmType.GetMethod("GetInternalAssetKey", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            assetName = _mcmGetInternalAssetKey?.Invoke(__instance, new object[] { modPath }) as IAssetName;
                            __result = true;
                            return false;
                        }
                    }
                }

                // 2. Otherwise it's a content asset (e.g. Maps/ZCCC_Entrance_Tilesheet)
                // Resolve asset name WITHOUT loading the full Texture2D through GameContentManager!
                _mcmGetContentKey ??= mcmType.GetMethod("GetContentKeyForTilesheetImageSource", BindingFlags.Instance | BindingFlags.NonPublic);
                string? contentKey = _mcmGetContentKey?.Invoke(__instance, new object[] { relativePath }) as string;
                if (contentKey != null)
                {
                    _mcmCoordinatorField ??= mcmType.BaseType?.GetField("Coordinator", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var coord = _mcmCoordinatorField?.GetValue(__instance);
                    if (coord != null)
                    {
                        _coordParseAssetNameMethod ??= coord.GetType().GetMethod("ParseAssetName", new[] { typeof(string), typeof(bool) });
                        if (_coordParseAssetNameMethod != null)
                        {
                            assetName = _coordParseAssetNameMethod.Invoke(coord, new object[] { contentKey, false }) as IAssetName;
                            __result = true;
                            return false; // Return success immediately without loading texture into RAM!
                        }
                    }
                }
            }
            catch
            {
                return true; // Fall back to original method if reflection fails
            }
            return true;
        }

        private static bool _forceMapLoad = false;
        private static int _loadedMapsCount = 0;
        private static int _loadedNpcCount = 0;

        private static bool IsEssentialLocation(GameLocation? loc)
        {
            if (loc == null) return false;
            if (loc is StardewValley.Locations.FarmHouse || loc is Farm) return true;
            string? name = loc.Name;
            if (name != null)
            {
                if (name == "FarmHouse" || name == "Farm" || name.StartsWith("Cabin")) return true;
            }
            if (Game1.player != null)
            {
                string? sleepLoc = Game1.player.lastSleepLocation?.Value;
                if (!string.IsNullOrEmpty(sleepLoc) && string.Equals(name, sleepLoc, StringComparison.OrdinalIgnoreCase)) return true;
                string? currLoc = Game1.player.currentLocation?.Name;
                if (!string.IsNullOrEmpty(currLoc) && string.Equals(name, currLoc, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool IsEssentialMap(xTile.Map? map)
        {
            if (map == null) return false;
            string? id = map.Id ?? map.assetPath;
            if (!string.IsNullOrEmpty(id))
            {
                if (id.IndexOf("FarmHouse", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.IndexOf("Cabin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.EndsWith("Farm", StringComparison.OrdinalIgnoreCase) ||
                    id.EndsWith("Farm_Shadow", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            if (Game1.currentLocation != null && map == Game1.currentLocation.Map)
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Ensures a location's map and its tilesheet textures are loaded on-demand when the player enters it.
        /// Locations loaded during save load (gameMode == 6) may have had their map or tilesheet textures deferred
        /// to keep peak RAM under the iOS Jetsam limit.
        /// </summary>
        public static void EnsureLocationMapAndTileSheetsLoaded(GameLocation? location)
        {
            if (location == null) return;
            try
            {
                DeferredMapLoader.EnsureLocationRealized(location);
                LazyIosDisplayDevice.EnsureInstalled();
                _forceMapLoad = true;
                if (location.map == null)
                {
                    location.reloadMap();
                }

                // Prune previous locations' tilesheet references from the display device
                LazyIosDisplayDevice.Instance?.PruneTileSheets(location.map);

                // Pre-warm destination tilesheets while screen is black (guarded by 300 MB / 250 MB memory check)
                LazyIosDisplayDevice.Instance?.PrewarmMapTileSheets(location.map);

                long availMb = MemoryWatchdog.Available() / (1024 * 1024);
                if (availMb < 120)
                {
                    MemoryWatchdog.ForceCollect($"Warp({location.Name})", minIntervalMs: 2500, compactLoh: true);
                }
            }
            catch (Exception ex)
            {
                ModMonitor?.Log($"[SDViOSTouchControls] Note loading map/tilesheets for '{location.Name}': {ex.Message}", LogLevel.Trace);
            }
            finally
            {
                _forceMapLoad = false;
            }
        }

        private static void PostfixLocationLoadMap(GameLocation __instance)
        {
            _loadedMapsCount++;
            if (_loadedMapsCount % 8 == 0)
            {
                try
                {
                    GC.Collect(1, GCCollectionMode.Optimized);
                }
                catch { }
            }
        }

        private static void PostfixNpcReloadSprite(NPC __instance)
        {
            _loadedNpcCount++;
            if (_loadedNpcCount % 15 == 0)
            {
                try
                {
                    GC.Collect(1, GCCollectionMode.Optimized);
                }
                catch { }
            }
        }

        private static void PrefixNewDayAfterFade()
        {
            MemoryWatchdog.ForceCollect("PrefixNewDayAfterFade", minIntervalMs: 1000);
        }

        private static void PostfixNewDayAfterFade()
        {
            MemoryWatchdog.ForceCollect("PostfixNewDayAfterFade", minIntervalMs: 1000);
        }

        private static void PostfixForceGC()
        {
            MemoryWatchdog.ForceCollect("FarmTypeManager", minIntervalMs: 1500);
        }

        public static void ForceLoadTileSheet(xTile.Display.XnaDisplayDevice device, xTile.Tiles.TileSheet tileSheet)
        {
            bool prev = _forceMapLoad;
            try
            {
                _forceMapLoad = true;
                device.LoadTileSheet(tileSheet);
            }
            finally
            {
                _forceMapLoad = prev;
            }
        }

        private static bool PrefixPropagateMap(ref bool __result)
        {
            __result = false;
            return false;
        }

        private static Exception? FinalizerSilenceException(Exception? __exception)
        {
            return null; // Suppresses repeating exceptions from broken mods like ContentPatcherAnimations
        }

        private static bool PrefixSetWindowSize(ref int w, ref int h)
        {
            w = _staticNativeWidth;
            h = _staticNativeHeight;
            if (Game1.game1 != null && Game1.game1.screen != null && Game1.graphics?.GraphicsDevice != null)
            {
                return false; // Lock window size; prevent recreating GameMenu or RenderTargets during gameplay
            }
            return true;
        }

        private static bool PrefixWindowClientSizeChanged(object sender, EventArgs e)
        {
            return false;
        }

        private static bool PrefixApplyChanges(GraphicsDeviceManager __instance)
        {
            try
            {
                var gd = __instance?.GraphicsDevice;
                if (gd != null && !gd.IsDisposed &&
                    gd.PresentationParameters.BackBufferWidth == _staticNativeWidth &&
                    gd.PresentationParameters.BackBufferHeight == _staticNativeHeight &&
                    Game1.game1?.screen != null)
                {
                    return false; // Skip redundant ApplyChanges when GraphicsDevice is already initialized at native resolution
                }
            }
            catch { }
            return true;
        }

        private static void PrefixSetPreferredWidth(ref int value)
        {
            value = _staticNativeWidth;
        }

        private static void PrefixSetPreferredHeight(ref int value)
        {
            value = _staticNativeHeight;
        }

        private static bool PrefixPreferredBackBufferWidth(ref int __result)
        {
            __result = _staticNativeWidth;
            return false;
        }

        private static bool PrefixPreferredBackBufferHeight(ref int __result)
        {
            __result = _staticNativeHeight;
            return false;
        }

        private static bool PrefixFalseBool(ref bool __result)
        {
            __result = false;
            return false;
        }

        private static bool PrefixTrue(ref bool __result)
        {
            __result = true;
            return false;
        }

        private static bool PrefixFalse()
        {
            return false;
        }

        private static bool PrefixGetKeyboardState(ref KeyboardState __result)
        {
            var pad = TouchVirtualPad.Instance;
            if (pad != null && pad.HasActiveInput)
            {
                __result = pad.CurrentSimulatedKeyboardState;
                if (VerboseInputLogging && (pad.ButtonY || pad.ButtonB || pad.ButtonMenu))
                {
                    ModMonitor?.Log($"[PAD_DEBUG] PrefixGetKeyboardState: keys=[{string.Join(",", __result.GetPressedKeys())}], menu={Game1.activeClickableMenu?.GetType().Name ?? "none"}", LogLevel.Trace);
                }
                return false;
            }
            return true;
        }

        private static bool PrefixGetGamePadState(ref GamePadState __result)
        {
            var pad = TouchVirtualPad.Instance;
            if (pad == null)
                return true;

            // Always return a state with a STABLE IsConnected value. Previously we returned a
            // connected state only while touching and fell back to native (disconnected) on release,
            // which made the game spam "gamepad connected/disconnected" notifications.
            __result = pad.EffectiveGamePadState;
            if (VerboseInputLogging && (pad.ButtonY || pad.ButtonB || pad.ButtonMenu))
            {
                ModMonitor?.Log($"[PAD_DEBUG] PrefixGetGamePadState: connected={__result.IsConnected}, buttons={__result.Buttons}, menu={Game1.activeClickableMenu?.GetType().Name ?? "none"}", LogLevel.Trace);
            }
            return false;
        }

        private static bool PrefixCheckGamepadMode(Game1 __instance)
        {
            // Skip CheckGamepadMode completely on iOS:
            // Prevents "Gamepad mode activated." HUD spam, controller auto-toggle flapping,
            // and automatic GameMenu reopening (pause-on-disconnect).
            return false;
        }

        private static bool PrefixShowGlobalMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return true;

            // Suppress gamepad connect/disconnect notifications that spam the screen on iOS
            if (message.Contains("Gamepad") || message.Contains("gamepad") || message.Contains("manette"))
            {
                if (VerboseInputLogging)
                {
                    ModMonitor?.Log($"[MSG_DEBUG] Suppressed gamepad notification: '{message}' | CallStack: {GetCompactStackTrace(2, 6)}", LogLevel.Trace);
                }
                return false;
            }
            return true;
        }

        private static bool PrefixSetActiveClickableMenu(ref IClickableMenu? value)
        {
            if (value is GameMenu)
            {
                string stack = Environment.StackTrace;
                if (VerboseInputLogging)
                {
                    ModMonitor?.Log($"[MENU_DEBUG] set_activeClickableMenu(GameMenu)! CallStack:\n{stack}", LogLevel.Trace);
                }

                // If GameMenu was triggered by CheckGamepadMode (pause-on-disconnect glitch), block it!
                if (stack.Contains("CheckGamepadMode"))
                {
                    ModMonitor?.Log("[MENU_DEBUG] BLOCKED automatic GameMenu from CheckGamepadMode pause-on-disconnect!", LogLevel.Warn);
                    return false;
                }
            }
            return true;
        }

        private static void PrefixGameMenuCtor()
        {
            try
            {
                long availMb = MemoryWatchdog.Available() / (1024 * 1024);
                long growthMb = (GC.GetTotalMemory(false) - MemoryWatchdog.LastPostGcManaged) / (1024 * 1024);
                if (availMb < 80 || (availMb < 350 && growthMb >= 25))
                {
                    MemoryWatchdog.ForceCollect("Pre-GameMenu", minIntervalMs: 5000, compactLoh: availMb < 100);
                }
            }
            catch { }
        }

        private static void PrefixExitActiveMenu()
        {
            if (!VerboseInputLogging) return;
            var menu = Game1.activeClickableMenu;
            string menuName = menu != null ? menu.GetType().Name : "null";
            ModMonitor?.Log($"[MENU_DEBUG] Game1.exitActiveMenu() called! activeClickableMenu={menuName}, CallStack: {GetCompactStackTrace(2, 8)}", LogLevel.Trace);
        }

        private static void PrefixExitThisMenu(IClickableMenu __instance)
        {
            if (!VerboseInputLogging) return;
            string menuName = __instance != null ? __instance.GetType().Name : "unknown";
            ModMonitor?.Log($"[MENU_DEBUG] IClickableMenu.exitThisMenu() on {menuName}! CallStack: {GetCompactStackTrace(2, 8)}", LogLevel.Trace);
        }

        private static void PrefixExitThisMenuNoSound(IClickableMenu __instance)
        {
            if (!VerboseInputLogging) return;
            string menuName = __instance != null ? __instance.GetType().Name : "unknown";
            ModMonitor?.Log($"[MENU_DEBUG] IClickableMenu.exitThisMenuNoSound() on {menuName}! CallStack: {GetCompactStackTrace(2, 8)}", LogLevel.Trace);
        }

        private static void PrefixReceiveLeftClick(IClickableMenu __instance, int x, int y)
        {
            if (!VerboseInputLogging) return;
            string menuName = __instance != null ? __instance.GetType().Name : "unknown";
            bool inside = __instance != null && __instance.isWithinBounds(x, y);
            ModMonitor?.Log($"[MENU_DEBUG] {menuName}.receiveLeftClick({x}, {y}, inside={inside})", LogLevel.Trace);
        }

        private static void PrefixReceiveKeyPress(IClickableMenu __instance, Keys key)
        {
            if (!VerboseInputLogging) return;
            string menuName = __instance != null ? __instance.GetType().Name : "unknown";
            ModMonitor?.Log($"[MENU_DEBUG] {menuName}.receiveKeyPress({key})! CallStack: {GetCompactStackTrace(2, 6)}", LogLevel.Trace);
        }

        private static void PrefixReceiveGamePadButton(IClickableMenu __instance, Buttons b)
        {
            if (!VerboseInputLogging) return;
            string menuName = __instance != null ? __instance.GetType().Name : "unknown";
            ModMonitor?.Log($"[MENU_DEBUG] {menuName}.receiveGamePadButton({b})! CallStack: {GetCompactStackTrace(2, 6)}", LogLevel.Trace);
        }

        private static void PrefixUpdateActiveMenu(out IClickableMenu? __state)
        {
            __state = Game1.activeClickableMenu;
        }

        private static void PostfixUpdateActiveMenu(IClickableMenu? __state)
        {
            if (VerboseInputLogging && __state != null && Game1.activeClickableMenu == null)
            {
                ModMonitor?.Log($"[MENU_DEBUG] Game1.updateActiveMenu CLOSED menu! Was: {__state.GetType().Name}, Now: null!", LogLevel.Trace);
            }
        }

        private static FieldInfo? _hooksField = null;

        private void EnsureModHooksWrapped()
        {
            try
            {
                if (_hooksField == null)
                {
                    _hooksField = typeof(Game1).GetField("hooks", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                }
                if (_hooksField != null)
                {
                    var curHooks = _hooksField.GetValue(null) as StardewValley.Mods.ModHooks;
                    if (curHooks != null && !(curHooks is TouchDelegatingModHooks))
                    {
                        var wrapped = new TouchDelegatingModHooks(curHooks);
                        _hooksField.SetValue(null, wrapped);
                        Monitor.Log("[SDViOSTouchControls] Successfully wrapped Game1.hooks with TouchDelegatingModHooks!", LogLevel.Info);
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Error wrapping Game1.hooks: {ex.Message}", LogLevel.Error);
            }
        }

        private static bool PrefixMouseGetState(ref MouseState __result)
        {
            if (TouchVirtualPad.Instance != null && TouchVirtualPad.Instance.IsMouseOverridden)
            {
                __result = TouchVirtualPad.Instance.CurrentSimulatedMouseState;
                return false; // Skip original method, return our simulated mouse state!
            }
            return true;
        }

        private static bool PrefixSetMousePositionRaw(int x, int y)
        {
            if (TouchVirtualPad.Instance != null && TouchVirtualPad.Instance.IsMouseOverridden)
            {
                // In Trackpad or Point & Click mode, do NOT let Game1 forcibly snap the mouse to center or reset lastCursorMotionWasMouse!
                return false;
            }
            return true;
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            try
            {
                EnsureNativeResolution(true);
                EnsurePadInitialized();
                EnsureModHooksWrapped();
                Monitor.Log("[SDViOSTouchControls] GameLaunched: Full native resolution locked & touch overlay ready.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Error in GameLaunched: {ex.Message}", LogLevel.Error);
            }
        }

        private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
        {
            if (VerboseInputLogging)
            {
                string oldMenu = e.OldMenu != null ? e.OldMenu.GetType().Name : "null";
                string newMenu = e.NewMenu != null ? e.NewMenu.GetType().Name : "null";
                Monitor.Log($"[MENU_DEBUG] OnMenuChanged: {oldMenu} -> {newMenu}", LogLevel.Trace);
            }

            // Free the closed GameMenu (MapPage/SocialPage textures etc.) when headroom is low and heap grew
            if (e.OldMenu is GameMenu && e.NewMenu == null)
            {
                try
                {
                    long availMb = MemoryWatchdog.Available() / (1024 * 1024);
                    long growthMb = (GC.GetTotalMemory(false) - MemoryWatchdog.LastPostGcManaged) / (1024 * 1024);
                    if (availMb < 80 || (availMb < 350 && growthMb >= 20))
                    {
                        MemoryWatchdog.ForceCollect("Post-GameMenu-Close", minIntervalMs: 5000, compactLoh: availMb < 100);
                    }
                }
                catch { }
            }
        }

        private void OnAssetReady(object? sender, AssetReadyEventArgs e)
        {
            _assetEventCount++;

            if (Game1.gameMode == 6)
            {
                if (_assetEventCount % 6 == 0)
                {
                    try
                    {
                        long availMb = MemoryWatchdog.Available() / (1024 * 1024);
                        if (availMb < 450)
                        {
                            MemoryWatchdog.ForceCollect("SaveLoadAssetReady", minIntervalMs: 1200);
                        }
                        else
                        {
                            GC.Collect(1, GCCollectionMode.Optimized);
                        }
                    }
                    catch { }
                }
            }
            else if (Game1.activeClickableMenu is StardewValley.Menus.TitleMenu)
            {
                if (_assetEventCount % 16 == 0)
                {
                    try
                    {
                        long availMb = MemoryWatchdog.Available() / (1024 * 1024);
                        if (availMb < 400)
                        {
                            MemoryWatchdog.ForceCollect("TitleMenuAssetReady", minIntervalMs: 2000);
                        }
                    }
                    catch { }
                }
            }
        }

        private int _menuClosedTick = -100;

        private void OnDayEnding(object? sender, DayEndingEventArgs e)
        {
            try
            {
                Monitor.Log("[SDViOSTouchControls] DayEnding: purging transient assets, compacting memory, and backing up logs...", LogLevel.Info);
                PurgeTransientAssets();
                LogBackup.BackupLogs("DayEnding");
                MemoryWatchdog.ForceCollect("DayEnding", minIntervalMs: 500);
                QuickSaveManager.SanitizeNonVanillaMonsters(Monitor, "DayEnding");
                QuickSaveManager.Instance?.OnDayEnding();
            }
            catch { }
        }

        private void OnSaving(object? sender, SavingEventArgs e)
        {
            try
            {
                Monitor.Log("[SDViOSTouchControls] Saving: compacting memory before XML serialization...", LogLevel.Info);
                LogBackup.BackupLogs("Saving");
                MemoryWatchdog.ForceCollect("Saving", minIntervalMs: 500);
                QuickSaveManager.SanitizeNonVanillaMonsters(Monitor, "Saving");
                QuickSaveManager.Instance?.OnSaving();
            }
            catch { }
        }

        private void OnSaved(object? sender, SavedEventArgs e)
        {
            try
            {
                Monitor.Log("[SDViOSTouchControls] Saved: save complete, running GC before day transition...", LogLevel.Info);
                LogBackup.BackupLogs("Saved");
                MemoryWatchdog.ForceCollect("Saved", minIntervalMs: 500);
                QuickSaveManager.Instance?.OnSaved();
            }
            catch { }
        }

        internal static bool IsCoreAsset(string key)
        {
            if (string.IsNullOrEmpty(key)) return true;
            key = key.Replace('\\', '/');

            // Audio assets MUST NEVER be touched or evicted
            if (key.IndexOf("Audio", StringComparison.OrdinalIgnoreCase) >= 0 ||
                key.IndexOf("Sound", StringComparison.OrdinalIgnoreCase) >= 0 ||
                key.IndexOf("Wave", StringComparison.OrdinalIgnoreCase) >= 0 ||
                key.IndexOf("Cue", StringComparison.OrdinalIgnoreCase) >= 0 ||
                key.IndexOf("Music", StringComparison.OrdinalIgnoreCase) >= 0 ||
                key.IndexOf("Jukebox", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            // Title screen sprites, UI, and Fonts
            if (key.StartsWith("LooseSprites/", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("Fonts/", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("Minigames/Title", StringComparison.OrdinalIgnoreCase)) return true;

            // Core static game sprites on Game1, FarmerRenderer, HoeDirt, Farm, etc.
            if (key.Equals("Maps/springobjects", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("Characters/Farmer/", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("TileSheets/", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("TerrainFeatures/", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("Buildings/", StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        private static bool IsPurgeableAsset(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            key = key.Replace('\\', '/');

            // Protected core assets, fonts, loose sprites, farmer sprites, shared tilesheets, and audio must never be purged
            if (IsCoreAsset(key)) return false;

            // Never touch Game data tables (Data/AudioChanges, Data/Locations, Data/Objects, etc.)
            // Only Data/Events is transient cutscene data that can be safely evicted
            if (key.StartsWith("Data/", StringComparison.OrdinalIgnoreCase) &&
                !key.StartsWith("Data/Events", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Never touch UI strings needed by TitleMenu / HUD
            if (key.StartsWith("Strings/UI", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("Strings/StringsFromCSFiles", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Game map assets and mine textures
            if (key.StartsWith("Maps/", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("Mines/", StringComparison.OrdinalIgnoreCase)) return true;

            // NPC Character, portrait, animal, monster textures (Characters/Farmer/* is protected by IsCoreAsset)
            if (key.StartsWith("Characters/", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("Portraits/", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("Animals/", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("Monsters/", StringComparison.OrdinalIgnoreCase)) return true;

            // Transient dialogue, schedule, and event strings
            if (key.StartsWith("Strings/Events", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("Strings/schedules", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("Data/Events", StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        private static bool IsCoreDisposable(IDisposable d)
        {
            if (d == null) return true;
            try
            {
                // Only Texture2D objects are candidates for eviction/disposal
                if (d is not Microsoft.Xna.Framework.Graphics.Texture2D tex) return true;

                // If already disposed, consider protected
                if (tex.IsDisposed) return true;

                if (ReferenceEquals(d, Game1.mouseCursors)) return true;
                if (ReferenceEquals(d, Game1.mouseCursors2)) return true;
                if (ReferenceEquals(d, Game1.mouseCursors_1_6)) return true;
                if (ReferenceEquals(d, Game1.objectSpriteSheet)) return true;
                if (ReferenceEquals(d, Game1.objectSpriteSheet_2)) return true;
                if (ReferenceEquals(d, Game1.cropSpriteSheet)) return true;
                if (ReferenceEquals(d, Game1.emoteSpriteSheet)) return true;
                if (ReferenceEquals(d, Game1.debrisSpriteSheet)) return true;
                if (ReferenceEquals(d, Game1.bigCraftableSpriteSheet)) return true;
                if (ReferenceEquals(d, Game1.toolSpriteSheet)) return true;
                if (ReferenceEquals(d, Game1.menuTexture)) return true;
                if (ReferenceEquals(d, Game1.uncoloredMenuTexture)) return true;
                if (ReferenceEquals(d, Game1.shadowTexture)) return true;
                if (ReferenceEquals(d, Game1.daybg)) return true;
                if (ReferenceEquals(d, Game1.nightbg)) return true;
                if (ReferenceEquals(d, Game1.rainTexture)) return true;
                if (ReferenceEquals(d, Game1.buffsIcons)) return true;
                if (ReferenceEquals(d, Game1.animations)) return true;
                if (ReferenceEquals(d, Game1.birdsSpriteSheet)) return true;
                if (ReferenceEquals(d, Game1.concessionsSpriteSheet)) return true;
                if (Game1.activeClickableMenu is TitleMenu tm)
                {
                    if (ReferenceEquals(d, tm.titleButtonsTexture)) return true;
                    if (ReferenceEquals(d, tm.cloudsTexture)) return true;
                }
                if (ReferenceEquals(d, Game1.controllerMaps)) return true;
                if (ReferenceEquals(d, Game1.giftboxTexture)) return true;
                if (ReferenceEquals(d, Game1.bobbersTexture)) return true;
                if (ReferenceEquals(d, FarmerRenderer.hairStylesTexture)) return true;
                if (ReferenceEquals(d, FarmerRenderer.shirtsTexture)) return true;
                if (ReferenceEquals(d, FarmerRenderer.hatsTexture)) return true;
                if (ReferenceEquals(d, FarmerRenderer.accessoriesTexture)) return true;
                if (ReferenceEquals(d, FarmerRenderer.pantsTexture)) return true;
                if (ReferenceEquals(d, StardewValley.TerrainFeatures.HoeDirt.lightTexture)) return true;
                if (ReferenceEquals(d, StardewValley.TerrainFeatures.HoeDirt.darkTexture)) return true;
                if (ReferenceEquals(d, StardewValley.TerrainFeatures.HoeDirt.snowTexture)) return true;
                if (ReferenceEquals(d, Farm.houseTextures)) return true;
                if (ReferenceEquals(d, MapSeat.mapChairTexture)) return true;
                if (ReferenceEquals(d, Tool.weaponsTexture)) return true;
                if (ReferenceEquals(d, StardewValley.Projectiles.Projectile.projectileSheet)) return true;
            }
            catch { }
            return false;
        }

        public static void RepairStaticTextures()
        {
            try
            {
                var content = Game1.content;
                if (content == null) return;

                if (StardewValley.TerrainFeatures.HoeDirt.lightTexture != null && StardewValley.TerrainFeatures.HoeDirt.lightTexture.IsDisposed)
                    StardewValley.TerrainFeatures.HoeDirt.lightTexture = content.Load<Texture2D>("TerrainFeatures\\hoeDirt");
                if (StardewValley.TerrainFeatures.HoeDirt.darkTexture != null && StardewValley.TerrainFeatures.HoeDirt.darkTexture.IsDisposed)
                    StardewValley.TerrainFeatures.HoeDirt.darkTexture = content.Load<Texture2D>("TerrainFeatures\\hoeDirtDark");
                if (StardewValley.TerrainFeatures.HoeDirt.snowTexture != null && StardewValley.TerrainFeatures.HoeDirt.snowTexture.IsDisposed)
                    StardewValley.TerrainFeatures.HoeDirt.snowTexture = content.Load<Texture2D>("TerrainFeatures\\hoeDirtSnow");

                if (FarmerRenderer.pantsTexture != null && FarmerRenderer.pantsTexture.IsDisposed)
                    FarmerRenderer.pantsTexture = content.Load<Texture2D>("Characters\\Farmer\\pants");
                if (FarmerRenderer.shirtsTexture != null && FarmerRenderer.shirtsTexture.IsDisposed)
                    FarmerRenderer.shirtsTexture = content.Load<Texture2D>("Characters\\Farmer\\shirts");
                if (FarmerRenderer.hatsTexture != null && FarmerRenderer.hatsTexture.IsDisposed)
                    FarmerRenderer.hatsTexture = content.Load<Texture2D>("Characters\\Farmer\\hats");
                if (FarmerRenderer.hairStylesTexture != null && FarmerRenderer.hairStylesTexture.IsDisposed)
                    FarmerRenderer.hairStylesTexture = content.Load<Texture2D>("Characters\\Farmer\\hairstyles");
                if (FarmerRenderer.accessoriesTexture != null && FarmerRenderer.accessoriesTexture.IsDisposed)
                    FarmerRenderer.accessoriesTexture = content.Load<Texture2D>("Characters\\Farmer\\accessories");

                if (Farm.houseTextures != null && Farm.houseTextures.IsDisposed)
                    Farm.houseTextures = content.Load<Texture2D>("Buildings\\houses");
                if (MapSeat.mapChairTexture != null && MapSeat.mapChairTexture.IsDisposed)
                    MapSeat.mapChairTexture = content.Load<Texture2D>("TileSheets\\ChairTiles");
                if (Tool.weaponsTexture != null && Tool.weaponsTexture.IsDisposed)
                    Tool.weaponsTexture = content.Load<Texture2D>("TileSheets\\weapons");
                if (StardewValley.Projectiles.Projectile.projectileSheet != null && StardewValley.Projectiles.Projectile.projectileSheet.IsDisposed)
                    StardewValley.Projectiles.Projectile.projectileSheet = content.Load<Texture2D>("TileSheets\\Projectiles");

                var fr = Game1.player?.FarmerRenderer;
                if (fr != null)
                {
                    var baseTexField = typeof(FarmerRenderer).GetField("baseTexture", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (baseTexField?.GetValue(fr) is Texture2D bt && bt.IsDisposed)
                    {
                        baseTexField.SetValue(fr, null);
                    }
                    typeof(FarmerRenderer).GetField("_baseTextureDirty", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(fr, true);
                    typeof(FarmerRenderer).GetField("_spriteDirty", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(fr, true);
                    typeof(FarmerRenderer).GetField("_shirtDirty", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(fr, true);
                    typeof(FarmerRenderer).GetField("_pantsDirty", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(fr, true);
                    typeof(FarmerRenderer).GetField("_shoesDirty", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(fr, true);
                    typeof(FarmerRenderer).GetField("_skinDirty", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(fr, true);
                    typeof(FarmerRenderer).GetField("_eyesDirty", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(fr, true);
                }
            }
            catch (Exception ex)
            {
                ModMonitor?.Log($"[SDViOSTouchControls] Note in RepairStaticTextures: {ex.Message}", LogLevel.Trace);
            }
        }

        public static void PurgeTransientAssets()
        {
            try
            {
                var content = Game1.content;
                if (content == null) return;
                var field = typeof(Microsoft.Xna.Framework.Content.ContentManager).GetField("loadedAssets", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (field?.GetValue(content) is System.Collections.IDictionary dict)
                {
                    var keysToRemove = new System.Collections.Generic.List<object>();
                    lock (content)
                    {
                        foreach (var key in dict.Keys)
                        {
                            if (key is string s)
                            {
                                if (s.StartsWith("Characters/schedules", StringComparison.OrdinalIgnoreCase) ||
                                    s.StartsWith("Characters/Dialogue", StringComparison.OrdinalIgnoreCase) ||
                                    s.StartsWith("Strings/schedules", StringComparison.OrdinalIgnoreCase) ||
                                    s.StartsWith("Strings/Events", StringComparison.OrdinalIgnoreCase) ||
                                    s.StartsWith("Data/Events", StringComparison.OrdinalIgnoreCase))
                                {
                                    keysToRemove.Add(key);
                                }
                            }
                        }
                        foreach (var k in keysToRemove) dict.Remove(k);
                    }
                    ModMonitor?.Log($"[MEM_PURGE] Evicted {keysToRemove.Count} transient assets from Game1.content.", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                ModMonitor?.Log($"[MEM_PURGE] Error in PurgeTransientAssets: {ex.Message}", LogLevel.Trace);
            }
        }

        public static void PurgeNonCoreAssets(IModHelper? helper = null)
        {
            try
            {
                long managedBefore = GC.GetTotalMemory(false);
                int evictedAssets = 0;
                int disposedAssets = 0;

                LazyIosDisplayDevice.Instance?.PruneTileSheets(null);

                // NOTE: DO NOT invoke helper.GameContent.InvalidateCache here!
                // Invalidating SMAPI caches when returning to title triggers CoreAssetPropagator
                // to propagate Data/AudioChanges, causing MissingFieldException in AudioCueModificationManager
                // and a massive memory spike that kills the app under iOS Jetsam.

                // 1. Discover all ContentManager instances from MonoGame
                var cmListField = typeof(Microsoft.Xna.Framework.Content.ContentManager)
                    .GetField("ContentManagers", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                var loadedField = typeof(Microsoft.Xna.Framework.Content.ContentManager)
                    .GetField("loadedAssets", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var disposableField = typeof(Microsoft.Xna.Framework.Content.ContentManager)
                    .GetField("disposableAssets", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                var managers = new List<Microsoft.Xna.Framework.Content.ContentManager>();
                if (Game1.content != null) managers.Add(Game1.content);
                if (Game1.game1?.Content != null && !managers.Contains(Game1.game1.Content)) managers.Add(Game1.game1.Content);
                if (Game1.game1?.xTileContent != null && !managers.Contains(Game1.game1.xTileContent)) managers.Add(Game1.game1.xTileContent);

                if (cmListField?.GetValue(null) is System.Collections.IEnumerable rawList)
                {
                    foreach (var item in rawList)
                    {
                        if (item is WeakReference wr && wr.Target is Microsoft.Xna.Framework.Content.ContentManager cm)
                        {
                            if (!managers.Contains(cm)) managers.Add(cm);
                        }
                        else if (item is Microsoft.Xna.Framework.Content.ContentManager directCm)
                        {
                            if (!managers.Contains(directCm)) managers.Add(directCm);
                        }
                    }
                }

                // 2. Safely evict unneeded game map/character textures and dispose textures
                foreach (var cm in managers)
                {
                    if (cm == null) continue;
                    try
                    {
                        var dict = loadedField?.GetValue(cm) as System.Collections.IDictionary;
                        var disposables = disposableField?.GetValue(cm) as System.Collections.IList;

                        if (dict != null)
                        {
                            var keysToRemove = new List<object>();
                            lock (cm)
                            {
                                foreach (var key in dict.Keys)
                                {
                                    if (key is string s && IsPurgeableAsset(s))
                                    {
                                        keysToRemove.Add(key);
                                    }
                                }

                                foreach (var key in keysToRemove)
                                {
                                    var val = dict[key];
                                    dict.Remove(key);
                                    evictedAssets++;

                                    if (val is IDisposable disp && !IsCoreDisposable(disp))
                                    {
                                        try
                                        {
                                            // Do NOT call tex.Dispose() directly on Texture2D instances!
                                            // Mods, ParsedItemData caches, or static game fields may still hold references
                                            // to the Texture2D; deleting its GL texture ID turns sprites into black boxes
                                            // and causes LazyIosDisplayDevice.DrawTile to re-run LoadTileSheet every frame.
                                            // Instead, remove it from disposableAssets so when no references remain,
                                            // GC finalization / GraphicsDevice cleanup reclaims it safely.
                                            if (disp is not Microsoft.Xna.Framework.Graphics.Texture2D)
                                            {
                                                disp.Dispose();
                                                disposedAssets++;
                                            }
                                            disposables?.Remove(disp);
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        ModMonitor?.Log($"[MEM_PURGE] Error purging manager {cm.GetType().Name}: {ex.Message}", LogLevel.Trace);
                    }
                }

                // 3. Force immediate full-heap garbage collection and finalizer execution
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true, true);

                long managedAfter = GC.GetTotalMemory(false);
                ModMonitor?.Log($"[MEM_PURGE] Safe texture purge complete: evicted {evictedAssets} assets ({disposedAssets} textures disposed). Managed heap: {managedBefore / (1024 * 1024)} MB -> {managedAfter / (1024 * 1024)} MB.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                ModMonitor?.Log($"[MEM_PURGE] Error in PurgeNonCoreAssets: {ex.Message}", LogLevel.Warn);
            }
        }

        private void OnAssetsInvalidated(object? sender, AssetsInvalidatedEventArgs e)
        {
            // Do not force GC on every asset invalidation to avoid GC lockups during mass Content Patcher loads
        }

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            _loadedMapsCount = 0;
            _loadedNpcCount = 0;
            try
            {
                DeferredMapLoader.OnSaveLoaded();

                if (Game1.options != null)
                {
                    Game1.options.gamepadMode = Options.GamepadModes.ForceOff;
                    Game1.options.gamepadControls = false;
                }
                if (Game1.activeClickableMenu is GameMenu)
                {
                    Game1.exitActiveMenu();
                }

                RepairStaticTextures();
                EnsureLocationMapAndTileSheetsLoaded(Game1.currentLocation);
                MemoryWatchdog.ForceCollect("SaveLoaded", minIntervalMs: 500);
                LogBackup.BackupLogs("SaveLoaded");
                QuickSaveManager.Instance?.OnSaveLoaded();
                RepairStaticTextures();
                EnsureLocationMapAndTileSheetsLoaded(Game1.currentLocation);
                Monitor.Log("[SDViOSTouchControls] Save loaded successfully. Gamepad locked ForceOff & memory compacted.", LogLevel.Info);
            }
            catch { }
        }

        private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
        {
            try
            {
                DeferredMapLoader.OnReturnedToTitle();
                TouchVirtualPad.Instance?.ResetInputState();
                PurgeNonCoreAssets();
                MemoryWatchdog.ForceCollect("ReturnedToTitle", minIntervalMs: 50);
                Monitor.Log("[SDViOSTouchControls] Returned to title: Input reset, non-core assets purged, and memory compacted.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Error in OnReturnedToTitle: {ex.Message}", LogLevel.Trace);
            }
        }

        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            try
            {
                DeferredMapLoader.OnDayStarted();

                if (Game1.options != null)
                {
                    Game1.options.gamepadMode = Options.GamepadModes.ForceOff;
                    Game1.options.gamepadControls = false;
                }
                if (Game1.activeClickableMenu is GameMenu)
                {
                    Game1.exitActiveMenu();
                }

                RepairStaticTextures();
                EnsureLocationMapAndTileSheetsLoaded(Game1.currentLocation);
                MemoryWatchdog.ForceCollect("DayStarted", minIntervalMs: 1000);
                LogBackup.BackupLogs("DayStarted");
                Monitor.Log("[SDViOSTouchControls] Day started: Large Object Heap compacted.", LogLevel.Info);
            }
            catch { }
        }

        private bool _legacyNeutralized = false;
        private bool _drawnThisFrame = false;
        private RenderTarget2D? _trackedScreen;
        private RenderTarget2D? _trackedUiScreen;

        private void OnUpdateTicking(object? sender, UpdateTickingEventArgs e)
        {
            try
            {
                _drawnThisFrame = false;

                LazyIosDisplayDevice.EnsureInstalled();

                if (Game1.game1 != null)
                {
                    var curScreen = Game1.game1.screen;
                    if (curScreen != null && _trackedScreen != null && !ReferenceEquals(curScreen, _trackedScreen))
                    {
                        try { if (!_trackedScreen.IsDisposed) _trackedScreen.Dispose(); } catch { }
                    }
                    _trackedScreen = curScreen;

                    var curUiScreen = Game1.game1.uiScreen;
                    if (curUiScreen != null && _trackedUiScreen != null && !ReferenceEquals(curUiScreen, _trackedUiScreen))
                    {
                        try { if (!_trackedUiScreen.IsDisposed) _trackedUiScreen.Dispose(); } catch { }
                    }
                    _trackedUiScreen = curUiScreen;
                }

                // 1. Lock GamepadMode to ForceOff on iOS:
                // Prevents Game1.CheckGamepadMode from flapping between mouse and controller,
                // which causes infinite "Gamepad mode activated" HUD spam and pause-on-disconnect menu loops.
                if (Game1.options != null)
                {
                    if (Game1.options.gamepadMode != Options.GamepadModes.ForceOff)
                    {
                        Game1.options.gamepadMode = Options.GamepadModes.ForceOff;
                    }
                    if (Game1.options.gamepadControls)
                    {
                        Game1.options.gamepadControls = false;
                    }
                }

                // 2. Purge any lingering gamepad notification cards from HUD
                if (Game1.hudMessages != null && Game1.hudMessages.Count > 0)
                {
                    Game1.hudMessages.RemoveAll(msg => msg.message != null && (msg.message.Contains("Gamepad") || msg.message.Contains("gamepad")));
                }

                // Periodically compact LOH during loading when memory headroom drops below 450 MB
                if (Game1.gameMode == 6 && e.IsMultipleOf(15))
                {
                    try
                    {
                        long availMb = MemoryWatchdog.Available() / (1024 * 1024);
                        if (availMb < 450)
                        {
                            MemoryWatchdog.ForceCollect("LoadingTick", minIntervalMs: 1200);
                        }
                    }
                    catch { }
                }

                // Ensure native resolution remains locked
                EnsureNativeResolution(false);
                UpdateDisplayLinkFrameRate(force: false);

                if (!_legacyNeutralized && Game1.game1 != null)
                {
                    NeutralizeOldTouchVirtualPad();
                    _legacyNeutralized = true;
                }

                EnsurePadInitialized();
                EnsureModHooksWrapped();

                if (_lastTickMenu != null && Game1.activeClickableMenu == null)
                {
                    _menuClosedTick = Game1.ticks;
                    if (VerboseInputLogging)
                    {
                        Monitor.Log($"[MENU_DEBUG] Menu became null before UpdateTicking! Was: {_lastTickMenu.GetType().Name}", LogLevel.Trace);
                    }
                }
                _lastTickMenu = Game1.activeClickableMenu;

                // Process virtual pad touches, point & click, and trackpad BEFORE Game1.Update runs!
                TouchVirtualPad.Instance.Update(Game1.currentGameTime);

                // Ensure stuck fishing reel sounds ("fastReel", "slowReel", "SinWave") are stopped when not actively reeling/casting
                StopStuckFishingSounds(forceSweepPool: false);
            }
            catch (Exception ex)
            {
                if (e.IsOneSecond)
                {
                    Monitor.Log($"[SDViOSTouchControls] Error in UpdateTicking: {ex.Message}", LogLevel.Warn);
                }
            }
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (_lastTickMenu != null && Game1.activeClickableMenu == null)
            {
                _menuClosedTick = Game1.ticks;
                if (VerboseInputLogging)
                {
                    Monitor.Log($"[MENU_DEBUG] Menu became null during Game1.Update! Was: {_lastTickMenu.GetType().Name}", LogLevel.Trace);
                }
            }
            else if (Game1.activeClickableMenu is GameMenu && Game1.ticks - _menuClosedTick <= 10 && !TouchVirtualPad.Instance.PlayerRequestedMenuOpen)
            {
                Monitor.Log("[MENU_DEBUG] Suppressed instant auto-reopen of GameMenu!", LogLevel.Warn);
                Game1.exitActiveMenu();
            }

            _lastTickMenu = Game1.activeClickableMenu;

            StopStuckFishingSounds(forceSweepPool: false);

            // Process any pending midday QuickSave state sync
            QuickSaveManager.Instance?.Update();

            if (e.IsMultipleOf(90))
            {
                FlushEngineLogger();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeCAFrameRateRange
        {
            public float Minimum;
            public float Maximum;
            public float Preferred;
        }

        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "sel_registerName")]
        private static extern IntPtr sel_registerName(string name);

        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool objc_msgSend_bool_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend_CAFrameRateRange(IntPtr receiver, IntPtr selector, NativeCAFrameRateRange range);

        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend_nint(IntPtr receiver, IntPtr selector, nint arg1);

        private static FieldInfo? _hostDisplayLinkField;
        private static PropertyInfo? _nsObjectHandleProp;
        private static FieldInfo? _nativeHandleField;
        private static IntPtr _selSetPreferredFrameRateRange;
        private static IntPtr _selSetPreferredFramesPerSecond;
        private static IntPtr _selRespondsToSelector;
        private static int _currentAppliedDisplayFps = 0;
        private static object? _engineLoggerLockObj;
        private static System.IO.StreamWriter? _engineLoggerWriter;

        private void RelocateDisabledModsOnBoot()
        {
            try
            {
                if (!TouchOverlaySettings.Instance.EnableDisabledModsRelocation)
                    return;

                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string modsDir = System.IO.Path.Combine(docs, "Mods");
                if (!System.IO.Directory.Exists(modsDir))
                    return;

                string disabledRoot = System.IO.Path.Combine(docs, "DisabledMods");
                int moved = 0;
                foreach (string dir in System.IO.Directory.GetDirectories(modsDir))
                {
                    string folderName = System.IO.Path.GetFileName(dir);
                    if (folderName.StartsWith(".disabled_", StringComparison.OrdinalIgnoreCase) ||
                        folderName.StartsWith(".disabled-", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            System.IO.Directory.CreateDirectory(disabledRoot);
                            string dest = System.IO.Path.Combine(disabledRoot, folderName);
                            if (System.IO.Directory.Exists(dest))
                            {
                                System.IO.Directory.Delete(dest, recursive: true);
                            }
                            System.IO.Directory.Move(dir, dest);
                            moved++;
                        }
                        catch { }
                    }
                }
                if (moved > 0)
                {
                    Monitor.Log($"[SDViOSTouchControls] Relocated {moved} disabled mod folder(s) from /Documents/Mods/ to /Documents/DisabledMods/.", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Note relocating disabled mods: {ex.Message}", LogLevel.Trace);
            }
        }

        private void ConfigureBufferedEngineLogger()
        {
            try
            {
                if (!TouchOverlaySettings.Instance.EnableBufferedLogging)
                    return;

                var sdvIosAsm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SDViOS");
                if (sdvIosAsm == null) return;

                var loggerType = sdvIosAsm.GetType("SDViOS.Diagnostics.EngineLogger");
                if (loggerType != null)
                {
                    _engineLoggerLockObj = loggerType.GetField("_lock", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null);
                    _engineLoggerWriter = loggerType.GetField("_fileWriter", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null) as System.IO.StreamWriter;
                    if (_engineLoggerWriter != null)
                    {
                        _engineLoggerWriter.AutoFlush = false;
                        Monitor.Log("[SDViOSTouchControls] Enabled buffered EngineLogger (AutoFlush = false, synchronous flush on warn/error/backgrounding).", LogLevel.Info);
                    }

                    var harmony = new Harmony("com.deadakj.sdviostouchcontrols.logger");
                    var flushPostfix = new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PostfixFlushEngineLogger), BindingFlags.Static | BindingFlags.NonPublic));

                    foreach (string methodName in new[] { "LogWarning", "LogError", "LogFatal" })
                    {
                        var m = loggerType.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (m != null)
                        {
                            try { harmony.Patch(m, postfix: flushPostfix); } catch { }
                        }
                    }

                    var appDelegateType = sdvIosAsm.GetType("SDViOS.AppDelegate");
                    if (appDelegateType != null)
                    {
                        foreach (string methodName in new[] { "DidEnterBackground", "WillTerminate", "ReceiveMemoryWarning" })
                        {
                            var m = appDelegateType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (m != null)
                            {
                                try { harmony.Patch(m, postfix: flushPostfix); } catch { }
                            }
                        }
                    }

                    var gameHostType = sdvIosAsm.GetType("SDViOS.Loader.GameHost");
                    var mDidBg = gameHostType?.GetMethod("OnDidEnterBackground", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (mDidBg != null)
                    {
                        try { harmony.Patch(mDidBg, postfix: flushPostfix); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Note configuring buffered EngineLogger: {ex.Message}", LogLevel.Trace);
            }
        }

        private static void PostfixFlushEngineLogger()
        {
            FlushEngineLogger();
        }

        public static void FlushEngineLogger()
        {
            try
            {
                if (_engineLoggerWriter == null) return;
                if (_engineLoggerLockObj != null)
                {
                    lock (_engineLoggerLockObj)
                    {
                        _engineLoggerWriter.Flush();
                    }
                }
                else
                {
                    _engineLoggerWriter.Flush();
                }
            }
            catch { }
        }

        private void UpdateDisplayLinkFrameRate(bool force)
        {
            try
            {
                if (!TouchOverlaySettings.Instance.EnableLockedFramePacing)
                    return;

                // 1. Lock IsFixedTimeStep = true alongside CAFrameRateRange (Auditor Revision #1)
                if (Game1.game1 != null && !Game1.game1.IsFixedTimeStep)
                {
                    Game1.game1.IsFixedTimeStep = true;
                    Game1.game1.TargetElapsedTime = TimeSpan.FromTicks(166667);
                }
                if (GameRunner.instance != null && !GameRunner.instance.IsFixedTimeStep)
                {
                    GameRunner.instance.IsFixedTimeStep = true;
                    GameRunner.instance.TargetElapsedTime = TimeSpan.FromTicks(166667);
                }

                // 2. Determine target frame rate: 30Hz in static menus (Auditor Revision #6), 60Hz in gameplay/minigames
                bool inStaticMenu = TouchOverlaySettings.Instance.EnableMenuFrameThrottling &&
                                    Game1.gameMode == 3 &&
                                    Game1.activeClickableMenu != null &&
                                    !(Game1.activeClickableMenu is BobberBar) &&
                                    Game1.currentMinigame == null;

                int targetFps = inStaticMenu ? 30 : 60;
                if (!force && _currentAppliedDisplayFps == targetFps)
                    return;

                if (_hostDisplayLinkField == null)
                {
                    var sdvIosAsm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SDViOS");
                    var ghType = sdvIosAsm?.GetType("SDViOS.Loader.GameHost");
                    _hostDisplayLinkField = ghType?.GetField("_engineDisplayLink", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                }

                var dlObj = _hostDisplayLinkField?.GetValue(null);
                if (dlObj == null) return;

                _nsObjectHandleProp ??= dlObj.GetType().GetProperty("Handle", BindingFlags.Instance | BindingFlags.Public);
                var nativeHandleVal = _nsObjectHandleProp?.GetValue(dlObj);
                if (nativeHandleVal == null) return;

                IntPtr dlHandle = IntPtr.Zero;
                if (nativeHandleVal is IntPtr directPtr)
                {
                    dlHandle = directPtr;
                }
                else
                {
                    _nativeHandleField ??= nativeHandleVal.GetType().GetField("handle", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (_nativeHandleField?.GetValue(nativeHandleVal) is IntPtr extractedPtr)
                        dlHandle = extractedPtr;
                }

                if (dlHandle == IntPtr.Zero) return;

                if (_selSetPreferredFrameRateRange == IntPtr.Zero)
                    _selSetPreferredFrameRateRange = sel_registerName("setPreferredFrameRateRange:");
                if (_selSetPreferredFramesPerSecond == IntPtr.Zero)
                    _selSetPreferredFramesPerSecond = sel_registerName("setPreferredFramesPerSecond:");
                if (_selRespondsToSelector == IntPtr.Zero)
                    _selRespondsToSelector = sel_registerName("respondsToSelector:");

                if (objc_msgSend_bool_IntPtr(dlHandle, _selRespondsToSelector, _selSetPreferredFramesPerSecond))
                {
                    objc_msgSend_nint(dlHandle, _selSetPreferredFramesPerSecond, (nint)targetFps);
                }

                if (objc_msgSend_bool_IntPtr(dlHandle, _selRespondsToSelector, _selSetPreferredFrameRateRange))
                {
                    var range = inStaticMenu
                        ? new NativeCAFrameRateRange { Minimum = 30f, Maximum = 60f, Preferred = 30f }
                        : new NativeCAFrameRateRange { Minimum = 60f, Maximum = 60f, Preferred = 60f };
                    objc_msgSend_CAFrameRateRange(dlHandle, _selSetPreferredFrameRateRange, range);
                }

                _currentAppliedDisplayFps = targetFps;
            }
            catch { }
        }

        private static bool _wasInBobberBar = false;
        private static bool _wasActivelyCasting = false;
        private static bool _wasFishCaughtOrPulling = false;
        private static bool _audioReflectionInitialized = false;
        private static FieldInfo? _audioEngineWrapperField;
        private static FieldInfo? _soundBankWrapperField;
        private static FieldInfo? _soundBankSoundsField;
        private static Type? _xactSoundType;
        private static MethodInfo? _xactSoundStopMethod;
        private static FieldInfo? _xactSoundWaveField;
        private static FieldInfo? _xactSoundClipsField;
        private static FieldInfo? _xactClipEventsField;
        private static FieldInfo? _playWaveEventWavField;
        private static FieldInfo? _seiEffectField;
        private static FieldInfo? _poolLockerField;
        private static FieldInfo? _poolPlayingInstancesField;
        private static FieldInfo? _seiIsXActField;
        private static MethodInfo? _seiStopBoolMethod;
        private static readonly HashSet<SoundEffect> _reelSoundEffects = new HashSet<SoundEffect>();

        private static void InitAudioReflection()
        {
            if (_audioReflectionInitialized) return;
            _audioReflectionInitialized = true;
            try
            {
                var mgAsm = typeof(AudioEngine).Assembly;
                var aewType = typeof(Game1).Assembly.GetType("StardewValley.Audio.AudioEngineWrapper");
                if (aewType != null)
                {
                    _audioEngineWrapperField = aewType.GetField("audioEngine", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }
                _soundBankWrapperField = typeof(SoundBankWrapper).GetField("soundBank", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                _soundBankSoundsField = typeof(SoundBank).GetField("_sounds", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                _xactSoundType = mgAsm.GetType("Microsoft.Xna.Framework.Audio.XactSound");
                if (_xactSoundType != null)
                {
                    _xactSoundStopMethod = _xactSoundType.GetMethod("Stop", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(AudioStopOptions) }, null);
                    _xactSoundWaveField = _xactSoundType.GetField("_wave", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    _xactSoundClipsField = _xactSoundType.GetField("_soundClips", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }

                var xactClipType = mgAsm.GetType("Microsoft.Xna.Framework.Audio.XactClip");
                if (xactClipType != null)
                {
                    _xactClipEventsField = xactClipType.GetField("_events", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }

                var playWaveEventType = mgAsm.GetType("Microsoft.Xna.Framework.Audio.PlayWaveEvent");
                if (playWaveEventType != null)
                {
                    _playWaveEventWavField = playWaveEventType.GetField("_wav", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }

                _seiEffectField = typeof(SoundEffectInstance).GetField("_effect", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                var poolType = mgAsm.GetType("Microsoft.Xna.Framework.Audio.SoundEffectInstancePool");
                if (poolType != null)
                {
                    _poolLockerField = poolType.GetField("_locker", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    _poolPlayingInstancesField = poolType.GetField("_playingInstances", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                }

                _seiIsXActField = typeof(SoundEffectInstance).GetField("_isXAct", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                _seiStopBoolMethod = typeof(SoundEffectInstance).GetMethod("Stop", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(bool) }, null);
            }
            catch (Exception ex)
            {
                ModMonitor?.Log($"[SDViOSTouchControls] Note initializing audio reflection: {ex.Message}", LogLevel.Trace);
            }
        }

        private static void RecordReelSoundEffect(SoundEffectInstance? inst)
        {
            if (inst == null || _seiEffectField == null) return;
            try
            {
                if (_seiEffectField.GetValue(inst) is SoundEffect eff)
                {
                    _reelSoundEffects.Add(eff);
                }
            }
            catch { }
        }

        private static void HarvestReelSoundEffectsFromXact(System.Collections.IDictionary soundDict, string cueName)
        {
            try
            {
                if (!soundDict.Contains(cueName)) return;
                if (soundDict[cueName] is Array xactSounds)
                {
                    foreach (var xs in xactSounds)
                    {
                        if (xs == null) continue;
                        if (_xactSoundWaveField?.GetValue(xs) is SoundEffectInstance wave)
                            RecordReelSoundEffect(wave);
                        if (_xactSoundClipsField?.GetValue(xs) is Array clips)
                        {
                            foreach (var clip in clips)
                            {
                                if (clip != null && _xactClipEventsField?.GetValue(clip) is Array events)
                                {
                                    foreach (var ev in events)
                                    {
                                        if (ev != null && _playWaveEventWavField?.GetValue(ev) is SoundEffectInstance clipWav)
                                            RecordReelSoundEffect(clipWav);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static void StopStuckFishingSounds(bool forceSweepPool)
        {
            try
            {
                bool inBobberBar = Game1.activeClickableMenu is BobberBar;
                var rod = Game1.player?.CurrentTool as FishingRod;
                bool isCasting = rod != null && (rod.isCasting || rod.castedButBobberStillInAir);
                bool isTimingCast = rod != null && rod.isTimingCast;
                bool isFishCaughtOrPulling = rod != null && (rod.fishCaught || rod.pullingOutOfWater || rod.showingTreasure);

                bool justLeftBobberBar = _wasInBobberBar && !inBobberBar;
                bool justStoppedCasting = _wasActivelyCasting && !isCasting;
                bool justCaughtOrPulled = !_wasFishCaughtOrPulling && isFishCaughtOrPulling;

                _wasInBobberBar = inBobberBar;
                _wasActivelyCasting = isCasting;
                _wasFishCaughtOrPulling = isFishCaughtOrPulling;

                // While in BobberBar or casting, opportunistically record the SoundEffect references used by fastReel / slowReel / SinWave
                if (inBobberBar || isCasting || isTimingCast)
                {
                    InitAudioReflection();
                    if (Game1.soundBank is SoundBankWrapper sbw0 &&
                        _soundBankWrapperField?.GetValue(sbw0) is SoundBank sb0 &&
                        _soundBankSoundsField?.GetValue(sb0) is System.Collections.IDictionary sd0)
                    {
                        HarvestReelSoundEffectsFromXact(sd0, "fastReel");
                        HarvestReelSoundEffectsFromXact(sd0, "slowReel");
                        HarvestReelSoundEffectsFromXact(sd0, "SinWave");
                    }
                }

                bool stoppedAnyCue = false;

                // 1. Stop BobberBar static reel sounds whenever not in BobberBar
                if (!inBobberBar)
                {
                    if (BobberBar.reelSound != null)
                    {
                        try { BobberBar.reelSound.Stop(AudioStopOptions.Immediate); } catch { }
                        BobberBar.reelSound = null;
                        stoppedAnyCue = true;
                    }
                    if (BobberBar.unReelSound != null)
                    {
                        try { BobberBar.unReelSound.Stop(AudioStopOptions.Immediate); } catch { }
                        BobberBar.unReelSound = null;
                        stoppedAnyCue = true;
                    }
                }

                // 2. Stop FishingRod.reelSound whenever not actively casting (and not in BobberBar)
                if (!inBobberBar && !isCasting)
                {
                    if (FishingRod.reelSound != null)
                    {
                        try { FishingRod.reelSound.Stop(AudioStopOptions.Immediate); } catch { }
                        FishingRod.reelSound = null;
                        stoppedAnyCue = true;
                    }
                }

                // 3. Stop FishingRod.chargeSound whenever not charging cast meter
                if (!isTimingCast)
                {
                    if (FishingRod.chargeSound != null)
                    {
                        try { FishingRod.chargeSound.Stop(AudioStopOptions.Immediate); } catch { }
                        FishingRod.chargeSound = null;
                        stoppedAnyCue = true;
                    }
                }

                // 4. Deep XACT + OpenAL sweep when leaving BobberBar, catching a fish, or stopping a cast/cue
                if (forceSweepPool || justLeftBobberBar || justStoppedCasting || justCaughtOrPulled || stoppedAnyCue)
                {
                    InitAudioReflection();

                    // 4a. Stop any active Cues in AudioEngine.ActiveCues for "fastReel", "slowReel", or "SinWave"
                    if (Game1.audioEngine != null && _audioEngineWrapperField?.GetValue(Game1.audioEngine) is AudioEngine ae)
                    {
                        lock (ae.UpdateLock)
                        {
                            var activeCues = ae.ActiveCues;
                            if (activeCues != null)
                            {
                                for (int i = activeCues.Count - 1; i >= 0; i--)
                                {
                                    var cue = activeCues[i];
                                    if (cue == null) continue;
                                    string? name = cue.Name;
                                    if ((!inBobberBar && name == "fastReel") ||
                                        (!inBobberBar && !isCasting && name == "slowReel") ||
                                        (!isTimingCast && name == "SinWave"))
                                    {
                                        try { cue.Stop(AudioStopOptions.Immediate); } catch { }
                                    }
                                }
                            }
                        }
                    }

                    // 4b. Stop shared XactSound instances in SoundBank._sounds["fastReel" / "slowReel" / "SinWave"]
                    if (Game1.soundBank is SoundBankWrapper sbw &&
                        _soundBankWrapperField?.GetValue(sbw) is SoundBank sb &&
                        _soundBankSoundsField?.GetValue(sb) is System.Collections.IDictionary soundDict)
                    {
                        if (!inBobberBar)
                        {
                            StopXactSoundEntry(soundDict, "fastReel");
                        }
                        if (!inBobberBar && !isCasting)
                        {
                            StopXactSoundEntry(soundDict, "slowReel");
                        }
                        if (!isTimingCast)
                        {
                            StopXactSoundEntry(soundDict, "SinWave");
                        }
                    }

                    // 4c. Sweep SoundEffectInstancePool._playingInstances for orphaned looped or reel-matching SoundEffectInstances
                    var locker = _poolLockerField?.GetValue(null);
                    var playingList = _poolPlayingInstancesField?.GetValue(null) as List<SoundEffectInstance>;
                    if (locker != null && playingList != null)
                    {
                        lock (locker)
                        {
                            for (int i = playingList.Count - 1; i >= 0; i--)
                            {
                                var inst = playingList[i];
                                if (inst == null) continue;
                                try
                                {
                                    bool isOrphanedLoop = false;
                                    if (inst.IsLooped)
                                    {
                                        bool isXAct = _seiIsXActField != null && _seiIsXActField.GetValue(inst) is bool b && b;
                                        if (!isXAct) isOrphanedLoop = true;
                                    }

                                    bool isReelEffect = false;
                                    if (!inBobberBar && !isCasting && !isTimingCast &&
                                        _seiEffectField?.GetValue(inst) is SoundEffect eff &&
                                        _reelSoundEffects.Contains(eff))
                                    {
                                        isReelEffect = true;
                                    }

                                    if (isOrphanedLoop || isReelEffect)
                                    {
                                        try { _seiIsXActField?.SetValue(inst, false); } catch { }
                                        if (_seiStopBoolMethod != null)
                                            _seiStopBoolMethod.Invoke(inst, new object[] { true });
                                        else
                                            inst.Stop();
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static void StopXactSoundEntry(System.Collections.IDictionary soundDict, string cueName)
        {
            try
            {
                if (!soundDict.Contains(cueName)) return;
                if (soundDict[cueName] is Array xactSounds)
                {
                    foreach (var xs in xactSounds)
                    {
                        if (xs == null) continue;
                        try
                        {
                            if (_xactSoundWaveField?.GetValue(xs) is SoundEffectInstance wave)
                            {
                                RecordReelSoundEffect(wave);
                                try { _seiIsXActField?.SetValue(wave, false); } catch { }
                                try
                                {
                                    if (_seiStopBoolMethod != null)
                                        _seiStopBoolMethod.Invoke(wave, new object[] { true });
                                    else
                                        wave.Stop();
                                }
                                catch { }
                            }
                            if (_xactSoundClipsField?.GetValue(xs) is Array clips)
                            {
                                foreach (var clip in clips)
                                {
                                    if (clip != null && _xactClipEventsField?.GetValue(clip) is Array events)
                                    {
                                        foreach (var ev in events)
                                        {
                                            if (ev != null && _playWaveEventWavField?.GetValue(ev) is SoundEffectInstance clipWav)
                                            {
                                                RecordReelSoundEffect(clipWav);
                                                try { _seiIsXActField?.SetValue(clipWav, false); } catch { }
                                                try
                                                {
                                                    if (_seiStopBoolMethod != null)
                                                        _seiStopBoolMethod.Invoke(clipWav, new object[] { true });
                                                    else
                                                        clipWav.Stop();
                                                }
                                                catch { }
                                            }
                                        }
                                    }
                                }
                            }
                            _xactSoundStopMethod?.Invoke(xs, new object[] { AudioStopOptions.Immediate });
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            try
            {
                if (!Context.IsWorldReady) return;

                if (e.Button == SButton.F5 || e.Button == SButton.K)
                {
                    QuickSaveManager.Instance?.QuickSave();
                }
                else if (e.Button == SButton.F7)
                {
                    QuickSaveManager.Instance?.QuickLoad();
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[QuickSave] Error handling button press {e.Button}: {ex.Message}", LogLevel.Trace);
            }
        }

        private void OnRenderedStep(object? sender, RenderedStepEventArgs e)
        {
            try
            {
                // RenderSteps.Overlays runs inside Game1.DrawOverlays while uiScreen is the active render target,
                // right after drawMouseCursor(). This draws OVER EVERYTHING (including all HUD, chat, and dialogs)!
                if (e.Step == StardewValley.Mods.RenderSteps.Overlays)
                {
                    EnsurePadInitialized();
                    TouchVirtualPad.Instance.Draw(e.SpriteBatch);
                    _drawnThisFrame = true;
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Error in RenderedStep: {ex.Message}", LogLevel.Warn);
            }
        }

        private void OnRendered(object? sender, RenderedEventArgs e)
        {
            try
            {
                // Fallback for screens where RenderSteps.Overlays is not invoked (e.g. Title screen, load menu)
                if (!_drawnThisFrame)
                {
                    EnsurePadInitialized();
                    TouchVirtualPad.Instance.Draw(e.SpriteBatch);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Error in Rendered: {ex.Message}", LogLevel.Warn);
            }
        }

        private void EnsureNativeResolution(bool force)
        {
            try
            {
                var gd = Game1.graphics?.GraphicsDevice;
                if (Game1.graphics == null || gd == null || Game1.game1 == null) return;

                int targetW = _nativeWidth;
                int targetH = _nativeHeight;

                bool sizeNeedsSync = (Game1.graphics.PreferredBackBufferWidth != targetW ||
                                     Game1.graphics.PreferredBackBufferHeight != targetH ||
                                     gd.PresentationParameters.BackBufferWidth != targetW ||
                                     gd.PresentationParameters.BackBufferHeight != targetH ||
                                     Game1.graphics.IsFullScreen);

                if (sizeNeedsSync || force)
                {
                    Game1.graphics.PreferredBackBufferWidth = targetW;
                    Game1.graphics.PreferredBackBufferHeight = targetH;
                    Game1.graphics.IsFullScreen = false;
                    gd.PresentationParameters.BackBufferWidth = targetW;
                    gd.PresentationParameters.BackBufferHeight = targetH;

                    // Let Stardew Valley natively allocate screen & uiScreen render targets
                    // and update viewport & uiViewport with zoom & uiScale calculations!
                    Game1.game1.SetWindowSize(targetW, targetH);

                    TouchVirtualPad.Instance?.UpdateLayout(targetW, targetH);

                    Monitor.Log($"[SDViOSTouchControls] Synchronized game native resolution to {targetW}x{targetH} via SetWindowSize.", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Error in EnsureNativeResolution: {ex.Message}", LogLevel.Warn);
            }
        }

        private void EnsurePadInitialized()
        {
            if (!_initialized && Game1.graphics?.GraphicsDevice != null)
            {
                try
                {
                    var gd = Game1.graphics.GraphicsDevice;
                    TouchVirtualPad.Instance.Initialize(gd);
                    TouchVirtualPad.Instance.UpdateLayout(_nativeWidth, _nativeHeight);
                    _initialized = true;
                    Monitor.Log($"[SDViOSTouchControls] TouchVirtualPad initialized for Viewport={_nativeWidth}x{_nativeHeight}.", LogLevel.Info);
                }
                catch (Exception ex)
                {
                    Monitor.Log($"[SDViOSTouchControls] Error initializing TouchVirtualPad: {ex.Message}", LogLevel.Error);
                }
            }
        }
    }
}
