using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Mods;
using StardewValley.Tools;
using StardewModdingAPI.Utilities;

namespace SDViOSTouchControls
{
    public class TouchDelegatingModHooks : DelegatingModHooks
    {
        private readonly HashSet<Keys> _mergedKeys = new HashSet<Keys>();

        private int _sittingTicks = 0;
        private bool _prevButtonA = false;
        private bool _prevButtonX = false;
        private static FieldInfo? _anotherClickField;

        public TouchDelegatingModHooks(ModHooks parent) : base(parent)
        {
        }

        private static void SetAnotherClick(MeleeWeapon weapon)
        {
            try
            {
                _anotherClickField ??= typeof(MeleeWeapon).GetField("anotherClick", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                _anotherClickField?.SetValue(weapon, true);
            }
            catch { }
        }

        public override void OnGame1_UpdateControlInput(ref KeyboardState keyboardState, ref MouseState mouseState, ref GamePadState gamePadState, Action action)
        {
            var pad = TouchVirtualPad.Instance;
            if (pad != null)
            {
                var player = Game1.player;
                if (player != null)
                {
                    // Sitting logic: allow sitting naturally, stand up with animation only after cooldown when moving
                    if (player.IsSitting())
                    {
                        _sittingTicks++;
                        bool hasDirectionalInput = pad.DPadUp || pad.DPadDown || pad.DPadLeft || pad.DPadRight ||
                                                  (pad.LeftStick != Vector2.Zero && pad.LeftStick.LengthSquared() > 0.05f);
                        if (!player.isStopSitting && _sittingTicks > 20 && hasDirectionalInput)
                        {
                            try
                            {
                                player.StopSitting(true);
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        _sittingTicks = 0;
                    }

                    // Dismiss passive object dialogue (e.g. sign, calendar, clock, text bar) when player moves or cancels
                    if (Game1.activeClickableMenu is DialogueBox db && db.characterDialogue == null && !db.isQuestion)
                    {
                        bool hasDirectionalInput = pad.DPadUp || pad.DPadDown || pad.DPadLeft || pad.DPadRight ||
                                                  (pad.LeftStick != Vector2.Zero && pad.LeftStick.LengthSquared() > 0.05f);
                        if (hasDirectionalInput || pad.ButtonB)
                        {
                            try
                            {
                                db.closeDialogue();
                            }
                            catch { }
                            Game1.activeClickableMenu = null;
                            Game1.dialogueUp = false;
                            player.CanMove = true;
                        }
                    }

                    // Watchdog: If activeClickableMenu is null but dialogueUp remained stuck true, clear it and restore movement!
                    if (Game1.activeClickableMenu == null && Game1.dialogueUp && !Game1.eventUp)
                    {
                        Game1.dialogueUp = false;
                        if (!player.CanMove)
                        {
                            player.CanMove = true;
                        }
                    }
                }

                // Inject simulated keyboard keys (WASD, Arrows, Action buttons) without unnecessary allocations
                if (pad.HasActiveInput)
                {
                    var simState = pad.CurrentSimulatedKeyboardState;
                    if (keyboardState.GetPressedKeyCount() == 0)
                    {
                        keyboardState = simState;
                    }
                    else
                    {
                        _mergedKeys.Clear();
                        int existingCount = keyboardState.GetPressedKeyCount();
                        Keys[] existingArr = TouchVirtualPad.GetKeysArrayBuffer(existingCount);
                        keyboardState.GetPressedKeys(existingArr);
                        for (int i = 0; i < existingCount; i++)
                            _mergedKeys.Add(existingArr[i]);

                        int simCount = simState.GetPressedKeyCount();
                        Keys[] simArr = TouchVirtualPad.GetKeysArrayBuffer(simCount);
                        simState.GetPressedKeys(simArr);
                        for (int i = 0; i < simCount; i++)
                            _mergedKeys.Add(simArr[i]);

                        Keys[] mergedArr = TouchVirtualPad.GetKeysArrayBuffer(_mergedKeys.Count);
                        _mergedKeys.CopyTo(mergedArr);
                        keyboardState = new KeyboardState(mergedArr);
                    }
                }

                // Inject simulated mouse state if overridden
                if (pad.IsMouseOverridden)
                {
                    mouseState = pad.CurrentSimulatedMouseState;
                }

                // Forward gamepad buttons and triggers so SMAPI and GamePadState consumers register them
                if (pad.ButtonA || pad.ButtonX || pad.ButtonLT || pad.ButtonRT)
                {
                    gamePadState = pad.EffectiveGamePadState;
                }
            }

            bool buttonA = pad != null && pad.ButtonA;
            bool justPressedA = buttonA && !_prevButtonA;
            _prevButtonA = buttonA;

            bool buttonX = pad != null && pad.ButtonX;
            bool justPressedX = buttonX && !_prevButtonX;
            _prevButtonX = buttonX;
            bool wasUsingToolBeforeInput = Game1.player != null && Game1.player.UsingTool;

            if (justPressedA && Game1.activeClickableMenu == null)
            {
                // Pulse simulated right-click on rising edge of Button A to satisfy didPlayerJustRightClick for containers
                mouseState = new MouseState(mouseState.X, mouseState.Y, mouseState.ScrollWheelValue, mouseState.LeftButton, ButtonState.Pressed, mouseState.MiddleButton, mouseState.XButton1, mouseState.XButton2);
            }

            bool actionOldMouseVis = Game1.wasMouseVisibleThisFrame;
            if ((buttonA || buttonX) && Game1.activeClickableMenu == null)
            {
                Game1.lastCursorMotionWasMouse = false;
                Game1.wasMouseVisibleThisFrame = false;
            }

            try
            {
                base.OnGame1_UpdateControlInput(ref keyboardState, ref mouseState, ref gamePadState, action);
            }
            finally
            {
                if ((buttonA || buttonX) && Game1.activeClickableMenu == null)
                {
                    Game1.wasMouseVisibleThisFrame = actionOldMouseVis;
                }
            }

            // Post-input handling for Button A container / object interaction fallback
            if (justPressedA && Game1.activeClickableMenu == null && !Game1.eventUp && !Game1.freezeControls)
            {
                var player = Game1.player;
                if (player != null && !player.isEating && !player.isRidingHorse() && !player.IsSitting() && !player.UsingTool && player.freezePause <= 0)
                {
                    Vector2 grabTile = player.GetGrabTile();
                    var loc = Game1.currentLocation;
                    if (loc != null && Game1.activeClickableMenu == null && !Game1.dialogueUp)
                    {
                        try
                        {
                            bool opened = Game1.tryToCheckAt(grabTile, player);
                            if (!opened && loc.objects.TryGetValue(grabTile, out var directObj) && directObj is StardewValley.Objects.Chest directChest)
                            {
                                opened = directChest.checkForAction(player);
                            }

                            // If facing tile did not interact with a chest/container, check adjacent tiles (North, East, South, West)
                            if (!opened && Game1.activeClickableMenu == null && !Game1.dialogueUp)
                            {
                                Vector2[] adjacent = new Vector2[]
                                {
                                    new Vector2(player.Tile.X, player.Tile.Y - 1),
                                    new Vector2(player.Tile.X + 1, player.Tile.Y),
                                    new Vector2(player.Tile.X, player.Tile.Y + 1),
                                    new Vector2(player.Tile.X - 1, player.Tile.Y)
                                };

                                for (int i = 0; i < adjacent.Length; i++)
                                {
                                    Vector2 tile = adjacent[i];
                                    if (loc.objects.TryGetValue(tile, out var adjObj) && adjObj is StardewValley.Objects.Chest adjChest)
                                    {
                                        player.faceGeneralDirection(tile * 64f);
                                        if (Game1.tryToCheckAt(tile, player) || adjChest.checkForAction(player))
                                        {
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }

            // Post-input handling for Button X tool / melee weapon swinging
            if (buttonX && Game1.activeClickableMenu == null && !Game1.eventUp && !Game1.freezeControls)
            {
                Game1.lastCursorMotionWasMouse = false;
                var player = Game1.player;
                if (player != null && !player.isEating && !player.isRidingHorse() && !player.IsSitting())
                {
                    if (player.CurrentTool is MeleeWeapon weapon)
                    {
                        if (player.CanMove && !player.UsingTool)
                        {
                            try
                            {
                                player.Halt();
                                player.BeginUsingTool();
                            }
                            catch { }
                        }
                        else if (player.UsingTool)
                        {
                            SetAnotherClick(weapon);
                            try
                            {
                                weapon.leftClick(player);
                            }
                            catch { }
                        }
                    }
                    else if (player.CurrentTool is FishingRod rod)
                    {
                        // Only invoke pressUseToolButton if base.OnGame1_UpdateControlInput did not already handle it:
                        // 1. Starting a cast on rising edge (justPressedX) when not yet UsingTool
                        // 2. Reeling in / hooking a nibbling fish on rising edge (justPressedX) when already fishing/reeling (not while charging/casting)
                        // 3. Dismissing a caught fish display on rising edge (justPressedX) when fishCaught is true
                        bool shouldPressFishing = false;
                        if (justPressedX)
                        {
                            if (!wasUsingToolBeforeInput && player.CanMove && !player.UsingTool)
                            {
                                shouldPressFishing = true;
                            }
                            else if (wasUsingToolBeforeInput && player.UsingTool && !rod.isTimingCast && !rod.isCasting && !rod.pullingOutOfWater)
                            {
                                if ((rod.isFishing || rod.isNibbling) && !rod.hit)
                                {
                                    shouldPressFishing = true;
                                }
                            }
                        }

                        if (shouldPressFishing)
                        {
                            bool oldMouseVis = Game1.wasMouseVisibleThisFrame;
                            Game1.wasMouseVisibleThisFrame = false;
                            try
                            {
                                Game1.pressUseToolButton();
                            }
                            catch { }
                            finally
                            {
                                Game1.wasMouseVisibleThisFrame = oldMouseVis;
                            }
                        }
                    }
                    else if (player.CurrentTool != null)
                    {
                        if (player.CanMove && !player.UsingTool)
                        {
                            bool oldMouseVis = Game1.wasMouseVisibleThisFrame;
                            Game1.wasMouseVisibleThisFrame = false;
                            try
                            {
                                Game1.pressUseToolButton();
                            }
                            catch { }
                            finally
                            {
                                Game1.wasMouseVisibleThisFrame = oldMouseVis;
                            }
                        }
                    }
                }
            }
        }

        public override void OnGameLocation_ResetForPlayerEntry(GameLocation location, Action action)
        {
            if (location != null)
            {
                ModEntry.EnsureLocationMapAndTileSheetsLoaded(location);
                base.OnGameLocation_ResetForPlayerEntry(location, action);
            }
            else
            {
                action?.Invoke();
            }
        }
    }
}
