using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using StardewValley;
using StardewValley.Menus;

namespace SDViOSTouchControls
{
    public class TouchVirtualPad
    {
        public static TouchVirtualPad Instance { get; } = new TouchVirtualPad();

        public TouchOverlaySettings Settings => TouchOverlaySettings.Instance;

        public bool IsVisible { get; set; } = true;
        public bool IsSettingsOpen { get; set; } = false;
        public bool IsEditLayoutMode { get; set; } = false;
        public bool IsHotkeyDrawerOpen { get; set; } = false;
        public bool IsKeyPickerOpen { get; set; } = false;
        public bool PlayerRequestedMenuOpen { get; set; } = false;

        // Current virtual control states
        public Vector2 LeftStick { get; private set; } = Vector2.Zero;
        public bool ButtonA { get; private set; } = false;
        public bool ButtonB { get; private set; } = false;
        public bool ButtonX { get; private set; } = false;
        public bool ButtonY { get; private set; } = false;
        public bool ButtonLT { get; private set; } = false;
        public bool ButtonRT { get; private set; } = false;
        public bool ButtonMenu { get; private set; } = false;

        // Visual states for drawing
        public bool IsVisualButtonA => ButtonA;
        public bool IsVisualButtonX => ButtonX;
        public bool IsVisualButtonY => _yTouchActive || _yPulseFrames > 0 || _yVisualFrames > 0;
        public bool IsVisualButtonB => _bTouchActive || _bPulseFrames > 0 || _bVisualFrames > 0;
        public bool IsVisualButtonLT => ButtonLT || _ltVisualFrames > 0;
        public bool IsVisualButtonRT => ButtonRT || _rtVisualFrames > 0;
        public bool IsVisualButtonMenu => _menuTouchActive || _menuPulseFrames > 0 || _menuVisualFrames > 0;

        // Pulse and debounce state for menu toggle / cancel / trigger buttons
        private bool _yTouchActive = false;
        private int _yPulseFrames = 0;
        private int _yVisualFrames = 0;
        private double _lastYPressTime = 0;

        private bool _bTouchActive = false;
        private int _bPulseFrames = 0;
        private int _bVisualFrames = 0;
        private double _lastBPressTime = 0;

        private bool _ltTouchActive = false;
        private int _ltVisualFrames = 0;
        private double _lastLTPressTime = 0;
        private double _ltNextRepeatTime = 0;

        private bool _rtTouchActive = false;
        private int _rtVisualFrames = 0;
        private double _lastRTPressTime = 0;
        private double _rtNextRepeatTime = 0;

        private bool _comboShiftTriggered = false;
        private double _lastToolbarShiftTime = 0;

        private bool _menuTouchActive = false;
        private int _menuPulseFrames = 0;
        private int _menuVisualFrames = 0;
        private double _lastMenuPressTime = 0;

        private double _lastKeyBtnPressTime = 0;

        // Mod Hotkey Drawer state
        private bool _isHotkeyRebindMode = false;
        private int _editingHotkeySlotIndex = -1;
        private double _lastDrawerToggleTime = 0;
        private double _lastDrawerActionTime = 0;
        private readonly bool[] _hotkeyTouchActive = new bool[TouchOverlaySettings.HotkeySlotCount];
        private readonly int[] _hotkeyPulseFrames = new int[TouchOverlaySettings.HotkeySlotCount];
        private readonly int[] _hotkeyVisualFrames = new int[TouchOverlaySettings.HotkeySlotCount];
        private readonly double[] _lastHotkeyPressTime = new double[TouchOverlaySettings.HotkeySlotCount];

        public bool HasActiveHotkeyInput
        {
            get
            {
                for (int i = 0; i < TouchOverlaySettings.HotkeySlotCount; i++)
                {
                    if (_hotkeyTouchActive[i] || _hotkeyPulseFrames[i] > 0)
                        return true;
                }
                return false;
            }
        }

        public bool DPadUp => LeftStick.Y < -Settings.Deadzone;
        public bool DPadDown => LeftStick.Y > Settings.Deadzone;
        public bool DPadLeft => LeftStick.X < -Settings.Deadzone;
        public bool DPadRight => LeftStick.X > Settings.Deadzone;

        public bool HasActiveInput => LeftStick != Vector2.Zero || ButtonA || ButtonB || ButtonX || ButtonY || ButtonLT || ButtonRT || ButtonMenu || HasActiveHotkeyInput;

        private readonly HashSet<Keys> _simKeysSet = new(16);
        private readonly List<Keys> _activeKeysList = new(16);
        private readonly object[] _setKeysArgs = new object[1];
        private static readonly KeyboardState _emptyKeyboardState = new KeyboardState();

        // Inventory Action Bar & Modifier States
        public bool IsSingleItemMode { get; private set; } = false;
        public bool IsHalfStackMode { get; private set; } = false;
        public bool IsQuickTransferMode { get; private set; } = false;

        private Rectangle _btnInventoryOneRect = Rectangle.Empty;
        private Rectangle _btnInventoryHalfRect = Rectangle.Empty;
        private Rectangle _btnInventoryXferRect = Rectangle.Empty;

        private int _simulatedRightClickFrames = 0;
        private bool _lastMenuOpenState = false;

        // Hold-to-Dispense state (Amendment 3)
        private Vector2 _menuTouchStartPos = Vector2.Zero;
        private double _menuTouchHoldTimer = 0.0;
        private double _dispenseIntervalTimer = 0.0;
        private bool _menuTouchHasDispensed = false;
        private bool _menuTwoFingerTapPending = false;
        private DateTime _lastBaitTapTime = DateTime.MinValue;
        private Point _lastBaitTapPos = new Point(-1000, -1000);

        private static readonly Keys[][] _cachedKeysArrays = new Keys[32][];
        static TouchVirtualPad()
        {
            for (int i = 0; i < _cachedKeysArrays.Length; i++)
            {
                _cachedKeysArrays[i] = new Keys[i];
            }
        }

        public static Keys[] GetKeysArrayBuffer(int count)
        {
            if (count <= 0) return Array.Empty<Keys>();
            if (count < _cachedKeysArrays.Length) return _cachedKeysArrays[count];
            return new Keys[count];
        }

        private static void AddOptionKeys(HashSet<Keys> set, InputButton[]? buttons)
        {
            if (buttons == null) return;
            foreach (var b in buttons)
            {
                if (b.key != Keys.None)
                    set.Add(b.key);
            }
        }

        public KeyboardState CurrentSimulatedKeyboardState
        {
            get
            {
                if (!HasActiveInput)
                    return _emptyKeyboardState;

                _simKeysSet.Clear();
                var opt = Game1.options;

                if (DPadUp)
                {
                    _simKeysSet.Add(Keys.W);
                    _simKeysSet.Add(Keys.Up);
                    AddOptionKeys(_simKeysSet, opt?.moveUpButton);
                }
                if (DPadDown)
                {
                    _simKeysSet.Add(Keys.S);
                    _simKeysSet.Add(Keys.Down);
                    AddOptionKeys(_simKeysSet, opt?.moveDownButton);
                }
                if (DPadLeft)
                {
                    _simKeysSet.Add(Keys.A);
                    _simKeysSet.Add(Keys.Left);
                    AddOptionKeys(_simKeysSet, opt?.moveLeftButton);
                }
                if (DPadRight)
                {
                    _simKeysSet.Add(Keys.D);
                    _simKeysSet.Add(Keys.Right);
                    AddOptionKeys(_simKeysSet, opt?.moveRightButton);
                }

                bool isGamepad = opt != null && opt.gamepadControls;

                if (ButtonA)
                {
                    _simKeysSet.Add(Keys.X);
                    _simKeysSet.Add(Keys.Space);
                    AddOptionKeys(_simKeysSet, opt?.actionButton);
                }
                if (ButtonX)
                {
                    _simKeysSet.Add(Keys.C);
                    AddOptionKeys(_simKeysSet, opt?.useToolButton);
                }
                if (ButtonY && !isGamepad)
                {
                    _simKeysSet.Add(Keys.E);
                    AddOptionKeys(_simKeysSet, opt?.menuButton);
                }
                if ((ButtonB || ButtonMenu) && !isGamepad)
                {
                    _simKeysSet.Add(Keys.Escape);
                    AddOptionKeys(_simKeysSet, opt?.cancelButton);
                }

                if (Game1.activeClickableMenu != null && (IsQuickTransferMode || IsHalfStackMode))
                {
                    _simKeysSet.Add(Keys.LeftShift);
                }

                for (int i = 0; i < TouchOverlaySettings.HotkeySlotCount; i++)
                {
                    if (_hotkeyTouchActive[i] || _hotkeyPulseFrames[i] > 0)
                    {
                        Keys hk = Settings.HotkeyKeys[i];
                        if (hk != Keys.None)
                            _simKeysSet.Add(hk);
                    }
                }

                if (_simKeysSet.Count == 0)
                    return _emptyKeyboardState;

                var arr = GetKeysArrayBuffer(_simKeysSet.Count);
                _simKeysSet.CopyTo(arr);
                return new KeyboardState(arr);
            }
        }

        public GamePadState CurrentSimulatedGamePadState
        {
            get
            {
                bool isGamepad = Game1.options != null && Game1.options.gamepadControls;
                var buttons = new GamePadButtons(
                    (ButtonA ? Buttons.A : 0) |
                    ((ButtonB && isGamepad) ? Buttons.B : 0) |
                    (ButtonX ? Buttons.X : 0) |
                    ((ButtonY && isGamepad) ? Buttons.Y : 0) |
                    ((ButtonMenu && isGamepad) ? Buttons.Start : 0) |
                    (ButtonLT ? Buttons.LeftTrigger : 0) |
                    (ButtonRT ? Buttons.RightTrigger : 0)
                );
                var dpad = new GamePadDPad(
                    DPadUp ? ButtonState.Pressed : ButtonState.Released,
                    DPadDown ? ButtonState.Pressed : ButtonState.Released,
                    DPadLeft ? ButtonState.Pressed : ButtonState.Released,
                    DPadRight ? ButtonState.Pressed : ButtonState.Released
                );
                var thumbsticks = new GamePadThumbSticks(new Vector2(LeftStick.X, -LeftStick.Y), Vector2.Zero);
                var triggers = new GamePadTriggers(ButtonLT ? 1f : 0f, ButtonRT ? 1f : 0f);
                return new GamePadState(thumbsticks, triggers, buttons, dpad);
            }
        }

        /// <summary>
        /// Gamepad state exposed to Game1 and SMAPI SInputState.
        /// Note: Game1.CheckGamepadMode is Harmony-patched out and Game1.options.gamepadControls is locked false,
        /// so reporting LeftTrigger/RightTrigger here allows SMAPI mods to detect SButton.LeftTrigger / SButton.RightTrigger
        /// without triggering vanilla gamepad connect/disconnect notifications.
        /// </summary>
        public GamePadState EffectiveGamePadState
        {
            get
            {
                bool isGamepad = Game1.options != null && Game1.options.gamepadControls;
                if (isGamepad)
                    return CurrentSimulatedGamePadState;
                if (ButtonA || ButtonLT || ButtonRT)
                {
                    var buttons = new GamePadButtons(
                        (ButtonA ? Buttons.A : 0) |
                        (ButtonLT ? Buttons.LeftTrigger : 0) |
                        (ButtonRT ? Buttons.RightTrigger : 0)
                    );
                    var triggers = new GamePadTriggers(ButtonLT ? 1f : 0f, ButtonRT ? 1f : 0f);
                    return new GamePadState(default, triggers, buttons, default);
                }
                return default;
            }
        }


        // Mouse simulation
        public Point SimulatedMousePosition { get; private set; } = new Point(896, 414);
        public bool SimulatedMouseLeftDown { get; private set; } = false;
        public bool SimulatedMouseRightDown { get; private set; } = false;
        private int _pointClickTouchId = -1;
        private int _pointClickHoldFrames = 0;

        public void ResetSimulatedMouse()
        {
            SimulatedMousePosition = new Point(-1000, -1000);
            SimulatedMouseLeftDown = false;
            SimulatedMouseRightDown = false;
            _pointClickTouchId = -1;
            _pointClickHoldFrames = 0;
        }

        public void ResetInputState()
        {
            ResetSimulatedMouse();
            ButtonA = false;
            ButtonB = false;
            ButtonX = false;
            ButtonY = false;
            ButtonLT = false;
            ButtonRT = false;
            LeftStick = Vector2.Zero;
            _stickTouchId = -1;
            _btnXTouchId = -1;
            _btnATouchId = -1;
            _trackpadPrimaryTouchId = -1;
            _trackpadSecondTouchId = -1;
            IsKeyPickerOpen = false;
            IsEditLayoutMode = false;
            IsSettingsOpen = false;
            IsHotkeyDrawerOpen = false;
        }

        public bool IsMouseOverridden => !IsSettingsOpen && !IsEditLayoutMode && !IsKeyPickerOpen && Settings.MouseControlMode != MouseMode.Disabled;

        public bool IsLeftClickActive => SimulatedMouseLeftDown;
        public bool IsRightClickActive => SimulatedMouseRightDown;

        private int _sittingTicks = 0;

        public MouseState CurrentSimulatedMouseState
        {
            get
            {
                int maxX = _rawScreenWidth > 0 ? _rawScreenWidth - 1 : (_viewportWidth > 0 ? _viewportWidth - 1 : 1791);
                int maxY = _rawScreenHeight > 0 ? _rawScreenHeight - 1 : (_viewportHeight > 0 ? _viewportHeight - 1 : 827);
                int mx = Math.Clamp(SimulatedMousePosition.X, 0, maxX);
                int my = Math.Clamp(SimulatedMousePosition.Y, 0, maxY);

                bool isLeft = IsLeftClickActive;
                bool isRight = IsRightClickActive || ButtonA;

                return new MouseState(
                    mx,
                    my,
                    0,
                    isLeft ? ButtonState.Pressed : ButtonState.Released,
                    ButtonState.Released,
                    isRight ? ButtonState.Pressed : ButtonState.Released,
                    ButtonState.Released,
                    ButtonState.Released
                );
            }
        }

        public void OnGameSetMousePosition(int x, int y)
        {
            SimulatedMousePosition = new Point(x, y);
            _trackpadCursorPos = new Vector2(x, y);
        }

        // Layout bounds
        private int _viewportWidth = 1792;
        private int _viewportHeight = 828;
        private int _rawScreenWidth = 1792;
        private int _rawScreenHeight = 828;

        private Vector2 _joystickCenter;
        private float _joystickRadius = 80f;
        private Vector2 _buttonsCenter;
        private Vector2 _ltCenter;
        private Vector2 _rtCenter;

        private Rectangle _joystickBaseRect;
        private Rectangle _btnARect;
        private Rectangle _btnBRect;
        private Rectangle _btnXRect;
        private Rectangle _btnYRect;
        private Rectangle _btnLTRect;
        private Rectangle _btnRTRect;
        private Rectangle _btnToggleRect;
        private Rectangle _btnSettingsRect;
        private Rectangle _btnKeyboardRect;
        private Rectangle _btnMenuRect;
        private Rectangle _btnHotkeyDrawerToggleRect;
        private Rectangle _btnDoneEditRect;

        // Mod Hotkey Drawer rectangles
        private Rectangle _hotkeyDrawerRect;
        private readonly Rectangle[] _hotkeySlotRects = new Rectangle[TouchOverlaySettings.HotkeySlotCount];
        private Rectangle _btnQuickSaveRect;
        private Rectangle _btnQuickLoadRect;
        private Rectangle _btnHotkeyBindModeRect;
        private Rectangle _btnHotkeyAutoCloseRect;
        private Rectangle _btnHotkeyCloseDrawerRect;

        // Key Picker Modal rectangles
        private Rectangle _keyPickerModalRect;
        private Rectangle _keyPickerCloseRect;
        private Rectangle _btnKeyPickerRenameRect;
        private Rectangle _btnKeyPickerPresetRect;
        private Rectangle _btnKeyPickerResetSlotRect;
        private Rectangle _btnKeyPickerCancelRect;

        // Settings modal rectangles
        private Rectangle _settingsModalRect;
        private Rectangle _settingsCloseRect;
        private Rectangle _btnOpacityMinus;
        private Rectangle _btnOpacityPlus;
        private Rectangle _btnScaleMinus;
        private Rectangle _btnScalePlus;
        private Rectangle _btnHandedness;
        private Rectangle _btnDeadzone;
        private Rectangle _btnToggleKeyboard;
        private Rectangle _btnToggleMenu;
        private Rectangle _btnToggleHotkeyDrawer;
        private Rectangle _btnToggleMouseMode;
        private Rectangle _btnSensitivityMinus;
        private Rectangle _btnSensitivityPlus;
        private Rectangle _btnEditLayout;
        private Rectangle _btnResetDefaults;
        private Rectangle _btnSaveClose;

        // Trackpad state
        private Vector2 _trackpadCursorPos = new Vector2(896, 414);
        private int _trackpadPrimaryTouchId = -1;
        private int _trackpadSecondTouchId = -1;
        private Vector2 _trackpadLastTouchPos;
        private float _trackpadTouchStartTime;
        private float _trackpadTotalDistMoved;
        private bool _trackpadHadSecondTouch;
        private int _trackpadLeftClickFrames = 0;
        private int _trackpadRightClickFrames = 0;
        private float _lastLeftClickTime = 0f;
        private float _lastRightClickTime = 0f;

        // Active touches
        private int _stickTouchId = -1;
        private Vector2 _currentStickPos;
        private int _activeDragTarget = 0;
        private int _dragTouchId = -1;
        private int _btnXTouchId = -1;
        private int _btnATouchId = -1;

        private Texture2D? _pixelTexture;

        // Reflection caches for Stardew Valley Game1.input & options
        private static bool _reflectionInitialized = false;
        private static object? _inputInstance = null;
        private static FieldInfo? _currentMouseStateField = null;
        private static FieldInfo? _currentGamepadStateField = null;
        private static FieldInfo? _currentKeyboardStateField = null;
        private static FieldInfo? _simulatedMousePositionField = null;
        private static FieldInfo? _sMouseStateBackingField = null;
        private static FieldInfo? _lastCursorMotionWasMouseField = null;
        private static FieldInfo? _mousePrimaryWindowField = null;
        private static FieldInfo? _gameWindowMouseStateField = null;

        private static Type? _game1Type = null;
        private static PropertyInfo? _optionsProp = null;
        private static FieldInfo? _gamepadControlsField = null;
        private static MethodInfo? _setKeysMethod = null;
        private static Action<List<Keys>>? _cachedSetKeys = null;

        public static Action<string>? LogAction { get; set; }

        private static void Log(string message)
        {
            LogAction?.Invoke(message);
        }

        public void Initialize(GraphicsDevice graphicsDevice)
        {
            Settings.Load();
            _viewportWidth = graphicsDevice.Viewport.Width > 0 ? graphicsDevice.Viewport.Width : 1792;
            _viewportHeight = graphicsDevice.Viewport.Height > 0 ? graphicsDevice.Viewport.Height : 828;
            _rawScreenWidth = _viewportWidth;
            _rawScreenHeight = _viewportHeight;

            _trackpadCursorPos = new Vector2(_rawScreenWidth / 2f, _rawScreenHeight / 2f);
            UpdateLayout(_viewportWidth, _viewportHeight);
            GenerateTextures(graphicsDevice);
            EnsureReflection();
        }

        public void UpdateLayout(int width, int height)
        {
            if (width <= 0) width = 1792;
            if (height <= 0) height = 828;

            _viewportWidth = width;
            _viewportHeight = height;

            float baseScale = Math.Max(1.0f, height / 720.0f) * Settings.Scale;
            float btnSize = 58f * baseScale;
            _joystickRadius = 75f * baseScale;
            float spacing = 58f * baseScale;

            if (Settings.CustomPositionsSet)
            {
                _joystickCenter = new Vector2(Settings.JoystickNormX * width, Settings.JoystickNormY * height);
                _buttonsCenter = new Vector2(Settings.ButtonsNormX * width, Settings.ButtonsNormY * height);
            }
            else
            {
                if (!Settings.LeftHanded)
                {
                    // Default right-handed: Joystick on bottom-left, buttons on bottom-right (ergonomically placed near bottom corners)
                    _joystickCenter = new Vector2(130f * baseScale, height - (95f * baseScale));
                    _buttonsCenter = new Vector2(width - (130f * baseScale), height - (95f * baseScale));
                }
                else
                {
                    // Left-handed: Joystick on bottom-right, buttons on bottom-left
                    _joystickCenter = new Vector2(width - (130f * baseScale), height - (95f * baseScale));
                    _buttonsCenter = new Vector2(130f * baseScale, height - (95f * baseScale));
                }
            }

            if (_stickTouchId == -1)
            {
                _currentStickPos = _joystickCenter;
            }
            _joystickBaseRect = new Rectangle(
                (int)(_joystickCenter.X - _joystickRadius),
                (int)(_joystickCenter.Y - _joystickRadius),
                (int)(_joystickRadius * 2),
                (int)(_joystickRadius * 2)
            );

            // Diamond layout for Action buttons
            _btnARect = new Rectangle((int)(_buttonsCenter.X - btnSize / 2f), (int)(_buttonsCenter.Y + spacing / 1.3f - btnSize / 2f), (int)btnSize, (int)btnSize); // Bottom: A
            _btnXRect = new Rectangle((int)(_buttonsCenter.X - spacing - btnSize / 2f), (int)(_buttonsCenter.Y - btnSize / 2f), (int)btnSize, (int)btnSize);          // Left: X
            _btnYRect = new Rectangle((int)(_buttonsCenter.X - btnSize / 2f), (int)(_buttonsCenter.Y - spacing - btnSize / 2f), (int)btnSize, (int)btnSize);          // Top: Y
            _btnBRect = new Rectangle((int)(_buttonsCenter.X + spacing - btnSize / 2f), (int)(_buttonsCenter.Y - btnSize / 2f), (int)btnSize, (int)btnSize);          // Right: B

            // Left Trigger (LT) and Right Trigger (RT) shoulder layout
            float trigW = 86f * baseScale;
            float trigH = 44f * baseScale;
            if (Settings.CustomTriggersSet)
            {
                _ltCenter = new Vector2(Settings.LeftTriggerNormX * width, Settings.LeftTriggerNormY * height);
                _rtCenter = new Vector2(Settings.RightTriggerNormX * width, Settings.RightTriggerNormY * height);
            }
            else
            {
                bool stickOnLeft = _joystickCenter.X <= _buttonsCenter.X;
                Vector2 leftCluster = stickOnLeft ? _joystickCenter : _buttonsCenter;
                Vector2 rightCluster = stickOnLeft ? _buttonsCenter : _joystickCenter;
                float leftOffset = stickOnLeft ? (_joystickRadius + 46f * baseScale) : (spacing + btnSize / 2f + 42f * baseScale);
                float rightOffset = stickOnLeft ? (spacing + btnSize / 2f + 42f * baseScale) : (_joystickRadius + 46f * baseScale);

                _ltCenter = new Vector2(
                    Math.Clamp(leftCluster.X, trigW / 2f + 16f, width - trigW / 2f - 16f),
                    Math.Clamp(leftCluster.Y - leftOffset, trigH / 2f + 64f, height - trigH / 2f - 16f)
                );
                _rtCenter = new Vector2(
                    Math.Clamp(rightCluster.X, trigW / 2f + 16f, width - trigW / 2f - 16f),
                    Math.Clamp(rightCluster.Y - rightOffset, trigH / 2f + 64f, height - trigH / 2f - 16f)
                );
            }
            _btnLTRect = new Rectangle((int)(_ltCenter.X - trigW / 2f), (int)(_ltCenter.Y - trigH / 2f), (int)trigW, (int)trigH);
            _btnRTRect = new Rectangle((int)(_rtCenter.X - trigW / 2f), (int)(_rtCenter.Y - trigH / 2f), (int)trigW, (int)trigH);

            // Top utility buttons (top-left aligned to avoid notch, rounded corners, and Stardew clock/money HUD)
            float topY = 16f;
            float topBtnH = 34f;
            float topBtnW = 54f;
            float curX = 40f;

            // 1. Toggle visibility button
            _btnToggleRect = new Rectangle((int)curX, (int)topY, (int)topBtnW, (int)topBtnH);
            curX += (topBtnW + 8f);

            // 2. Settings button
            _btnSettingsRect = new Rectangle((int)curX, (int)topY, (int)topBtnW, (int)topBtnH);
            curX += (topBtnW + 8f);

            // 3. Keyboard button
            if (Settings.ShowKeyboardBtn)
            {
                _btnKeyboardRect = new Rectangle((int)curX, (int)topY, (int)topBtnW, (int)topBtnH);
                curX += (topBtnW + 8f);
            }
            else
            {
                _btnKeyboardRect = Rectangle.Empty;
            }

            // 4. Menu button
            if (Settings.ShowMenuBtn)
            {
                float menuW = 64f;
                _btnMenuRect = new Rectangle((int)curX, (int)topY, (int)menuW, (int)topBtnH);
                curX += (menuW + 8f);
            }
            else
            {
                _btnMenuRect = Rectangle.Empty;
            }

            // 5. Mod Hotkey Drawer toggle button
            if (Settings.ShowHotkeyDrawerBtn)
            {
                float modsW = 68f;
                _btnHotkeyDrawerToggleRect = new Rectangle((int)curX, (int)topY, (int)modsW, (int)topBtnH);
                curX += (modsW + 8f);
            }
            else
            {
                _btnHotkeyDrawerToggleRect = Rectangle.Empty;
                IsHotkeyDrawerOpen = false;
            }

            // Mod Hotkey Drawer panel layout (4x2 grid + utility footer under top bar)
            int drawerX = 40;
            int drawerY = (int)(topY + topBtnH + 8f);
            int slotW = 92;
            int slotH = 44;
            int slotGapX = 8;
            int slotGapY = 8;
            int drawerPad = 12;
            int gridW = 4 * slotW + 3 * slotGapX;
            int drawerW = drawerPad * 2 + gridW;

            for (int i = 0; i < TouchOverlaySettings.HotkeySlotCount; i++)
            {
                int col = i % 4;
                int row = i / 4;
                int sx = drawerX + drawerPad + col * (slotW + slotGapX);
                int sy = drawerY + drawerPad + row * (slotH + slotGapY);
                _hotkeySlotRects[i] = new Rectangle(sx, sy, slotW, slotH);
            }

            // Inventory Action Bar buttons (docked at Top-Left Header row 2, y = 56f)
            float invY = topY + topBtnH + 6f;
            float invBtnH = 32f;
            float invBtnW = 54f;
            float invX = 40f;

            _btnInventoryOneRect = new Rectangle((int)invX, (int)invY, (int)invBtnW, (int)invBtnH);
            invX += (invBtnW + 8f);

            _btnInventoryHalfRect = new Rectangle((int)invX, (int)invY, (int)invBtnW, (int)invBtnH);
            invX += (invBtnW + 8f);

            float xferW = 74f;
            _btnInventoryXferRect = new Rectangle((int)invX, (int)invY, (int)xferW, (int)invBtnH);

            int saveRowY = drawerY + drawerPad + 2 * (slotH + slotGapY) + 4;
            int saveRowH = 34;
            int halfW = (gridW - 8) / 2;
            _btnQuickSaveRect = new Rectangle(drawerX + drawerPad, saveRowY, halfW, saveRowH);
            _btnQuickLoadRect = new Rectangle(drawerX + drawerPad + halfW + 8, saveRowY, halfW, saveRowH);

            int footerY = saveRowY + saveRowH + 8;
            int footerH = 30;
            int drawerH = (footerY - drawerY) + footerH + drawerPad;
            _hotkeyDrawerRect = new Rectangle(drawerX, drawerY, drawerW, drawerH);

            int footerX = drawerX + drawerPad;
            _btnHotkeyBindModeRect = new Rectangle(footerX, footerY, 138, footerH);
            _btnHotkeyAutoCloseRect = new Rectangle(footerX + 146, footerY, 148, footerH);
            _btnHotkeyCloseDrawerRect = new Rectangle(footerX + 302, footerY, gridW - 302, footerH);

            // Key Picker Modal layout
            int kpW = Math.Min(720, width - 40);
            int kpH = Math.Min(480, height - 30);
            _keyPickerModalRect = new Rectangle((width - kpW) / 2, (height - kpH) / 2, kpW, kpH);
            _keyPickerCloseRect = new Rectangle(_keyPickerModalRect.Right - 38, _keyPickerModalRect.Top + 8, 30, 30);
            int kpBotY = _keyPickerModalRect.Bottom - 44;
            int kpBtnGap = 10;
            int kpBtnW = (kpW - 40 - 3 * kpBtnGap) / 4;
            int kpStartX = _keyPickerModalRect.Left + 20;
            _btnKeyPickerRenameRect = new Rectangle(kpStartX, kpBotY, kpBtnW, 34);
            _btnKeyPickerPresetRect = new Rectangle(kpStartX + (kpBtnW + kpBtnGap), kpBotY, kpBtnW, 34);
            _btnKeyPickerResetSlotRect = new Rectangle(kpStartX + 2 * (kpBtnW + kpBtnGap), kpBotY, kpBtnW, 34);
            _btnKeyPickerCancelRect = new Rectangle(kpStartX + 3 * (kpBtnW + kpBtnGap), kpBotY, kpBtnW, 34);

            // Edit mode "DONE" button
            _btnDoneEditRect = new Rectangle(width / 2 - 60, 20, 120, 44);

            // Settings Modal layout
            int modalW = Math.Min(680, width - 40);
            int modalH = Math.Min(530, height - 30);
            _settingsModalRect = new Rectangle((width - modalW) / 2, (height - modalH) / 2, modalW, modalH);
            _settingsCloseRect = new Rectangle(_settingsModalRect.Right - 38, _settingsModalRect.Top + 8, 30, 30);

            int startY = _settingsModalRect.Top + 48;
            int rowSpacing = 37;
            int ctrlX = _settingsModalRect.Left + 260;

            _btnOpacityMinus = new Rectangle(ctrlX, startY, 40, 28);
            _btnOpacityPlus = new Rectangle(ctrlX + 110, startY, 40, 28);
            _btnScaleMinus = new Rectangle(ctrlX, startY + rowSpacing, 40, 28);
            _btnScalePlus = new Rectangle(ctrlX + 110, startY + rowSpacing, 40, 28);
            _btnHandedness = new Rectangle(ctrlX, startY + rowSpacing * 2, 170, 28);
            _btnDeadzone = new Rectangle(ctrlX, startY + rowSpacing * 3, 170, 28);
            _btnToggleKeyboard = new Rectangle(ctrlX, startY + rowSpacing * 4, 80, 28);
            _btnToggleMenu = new Rectangle(ctrlX + 88, startY + rowSpacing * 4, 80, 28);
            _btnToggleHotkeyDrawer = new Rectangle(ctrlX + 176, startY + rowSpacing * 4, 88, 28);
            _btnToggleMouseMode = new Rectangle(ctrlX, startY + rowSpacing * 5, 170, 28);
            _btnSensitivityMinus = new Rectangle(ctrlX, startY + rowSpacing * 6, 40, 28);
            _btnSensitivityPlus = new Rectangle(ctrlX + 110, startY + rowSpacing * 6, 40, 28);
            _btnEditLayout = new Rectangle(_settingsModalRect.Left + 28, startY + rowSpacing * 7 + 4, modalW - 56, 32);

            int botBtnW = (modalW - 70) / 2;
            int botY = _settingsModalRect.Bottom - 44;
            _btnResetDefaults = new Rectangle(_settingsModalRect.Left + 28, botY, botBtnW, 34);
            _btnSaveClose = new Rectangle(_settingsModalRect.Left + 42 + botBtnW, botY, botBtnW, 34);
        }

        public void Update(GameTime gameTime)
        {
            if (QuickSaveManager.Instance?.IsReloading == true)
            {
                ResetInputState();
                return;
            }

            TouchCollection touches = TouchPanel.GetState();

            // Detect raw screen dimensions from backbuffer
            int rawW = 1792;
            int rawH = 828;
            try
            {
                if (Game1.graphics?.GraphicsDevice != null)
                {
                    rawW = Game1.graphics.GraphicsDevice.PresentationParameters.BackBufferWidth;
                    rawH = Game1.graphics.GraphicsDevice.PresentationParameters.BackBufferHeight;
                }
            }
            catch { }
            _rawScreenWidth = rawW > 0 ? rawW : _viewportWidth;
            _rawScreenHeight = rawH > 0 ? rawH : _viewportHeight;

            double curTime = gameTime.TotalGameTime.TotalSeconds;

            if (_yPulseFrames > 0) _yPulseFrames--;
            if (_bPulseFrames > 0) _bPulseFrames--;
            if (_menuPulseFrames > 0) _menuPulseFrames--;

            if (_yVisualFrames > 0) _yVisualFrames--;
            if (_bVisualFrames > 0) _bVisualFrames--;
            if (_ltVisualFrames > 0) _ltVisualFrames--;
            if (_rtVisualFrames > 0) _rtVisualFrames--;
            if (_menuVisualFrames > 0) _menuVisualFrames--;

            for (int i = 0; i < TouchOverlaySettings.HotkeySlotCount; i++)
            {
                if (_hotkeyPulseFrames[i] > 0) _hotkeyPulseFrames[i]--;
                if (_hotkeyVisualFrames[i] > 0) _hotkeyVisualFrames[i]--;
            }

            bool yTouchFound = false;
            bool bTouchFound = false;
            bool ltTouchFound = false;
            bool rtTouchFound = false;
            bool menuTouchFound = false;
            Span<bool> hotkeyTouchFound = stackalloc bool[TouchOverlaySettings.HotkeySlotCount];

            // Reset momentary states
            ButtonA = false;
            ButtonX = false;
            ButtonLT = false;
            ButtonRT = false;
            SimulatedMouseRightDown = false;

            // Update hold frames for point-and-click tap recognition
            if (_pointClickHoldFrames > 0)
            {
                SimulatedMouseLeftDown = true;
                _pointClickHoldFrames--;
            }
            else if (_pointClickTouchId == -1)
            {
                SimulatedMouseLeftDown = false;
            }

            // Handle momentary click frames from trackpad taps
            if (_trackpadLeftClickFrames > 0)
            {
                SimulatedMouseLeftDown = true;
                _trackpadLeftClickFrames--;
            }
            if (_trackpadRightClickFrames > 0)
            {
                SimulatedMouseRightDown = true;
                _trackpadRightClickFrames--;
            }

            if (_simulatedRightClickFrames > 0)
            {
                SimulatedMouseRightDown = true;
                _simulatedRightClickFrames--;
            }

            // Reset modifier modes when menu closes
            bool isMenuOpen = Game1.activeClickableMenu != null;
            if (!isMenuOpen && _lastMenuOpenState)
            {
                IsSingleItemMode = false;
                IsHalfStackMode = false;
                IsQuickTransferMode = false;
                _menuTouchHoldTimer = 0.0;
                _dispenseIntervalTimer = 0.0;
                _menuTouchHasDispensed = false;
                _menuTwoFingerTapPending = false;
            }
            _lastMenuOpenState = isMenuOpen;

            // Hold-to-Dispense accumulation using GameTime (Amendment 3)
            if (isMenuOpen && Settings.EnableHoldToDispense && _pointClickTouchId != -1 && _menuTouchHoldTimer >= 0.0)
            {
                _menuTouchHoldTimer += gameTime.ElapsedGameTime.TotalMilliseconds;
                if (_menuTouchHoldTimer >= 250.0)
                {
                    _dispenseIntervalTimer += gameTime.ElapsedGameTime.TotalMilliseconds;
                    if (_dispenseIntervalTimer >= 100.0)
                    {
                        _dispenseIntervalTimer -= 100.0;
                        _menuTouchHasDispensed = true;
                        SimulatedMousePosition = new Point((int)Math.Round(_menuTouchStartPos.X), (int)Math.Round(_menuTouchStartPos.Y));
                        SimulatedMouseRightDown = true;
                        _simulatedRightClickFrames = 2;
                        SimulatedMouseLeftDown = false;
                        _pointClickHoldFrames = 0;
                        Game1.lastCursorMotionWasMouse = true;
                    }
                }
            }

            // 0. Process Key Picker Modal Interaction
            if (IsKeyPickerOpen)
            {
                _trackpadPrimaryTouchId = -1;
                _trackpadSecondTouchId = -1;
                _stickTouchId = -1;
                _btnXTouchId = -1;
                _btnATouchId = -1;
                _pointClickTouchId = -1;
                _pointClickHoldFrames = 0;
                SimulatedMouseLeftDown = false;
                SimulatedMouseRightDown = false;
                for (int i = 0; i < TouchOverlaySettings.HotkeySlotCount; i++)
                    _hotkeyTouchActive[i] = false;
                UpdateKeyPickerTouches(touches, curTime);
                ForwardInputToGame();
                return;
            }

            // 1. Process Settings Menu Interaction
            if (IsSettingsOpen)
            {
                _trackpadPrimaryTouchId = -1;
                _trackpadSecondTouchId = -1;
                _stickTouchId = -1;
                _btnXTouchId = -1;
                _btnATouchId = -1;
                _pointClickTouchId = -1;
                _pointClickHoldFrames = 0;
                SimulatedMouseLeftDown = false;
                SimulatedMouseRightDown = false;
                for (int i = 0; i < TouchOverlaySettings.HotkeySlotCount; i++)
                    _hotkeyTouchActive[i] = false;
                UpdateSettingsTouches(touches);
                ForwardInputToGame();
                return;
            }

            // 2. Process Edit Layout Interaction
            if (IsEditLayoutMode)
            {
                _trackpadPrimaryTouchId = -1;
                _trackpadSecondTouchId = -1;
                _stickTouchId = -1;
                _btnXTouchId = -1;
                _btnATouchId = -1;
                _pointClickTouchId = -1;
                _pointClickHoldFrames = 0;
                SimulatedMouseLeftDown = false;
                SimulatedMouseRightDown = false;
                for (int i = 0; i < TouchOverlaySettings.HotkeySlotCount; i++)
                    _hotkeyTouchActive[i] = false;
                UpdateEditModeTouches(touches);
                ForwardInputToGame();
                return;
            }

            // 3. Process Gamepad & Mouse Touches
            bool stickTouchPresent = false;
            bool btnXTouchPresent = false;
            bool btnATouchPresent = false;
            bool pointClickTouchPresent = false;
            bool trackpadPrimaryPresent = false;
            bool trackpadSecondPresent = false;

            if (Game1.activeClickableMenu != null && touches.Count >= 2)
            {
                foreach (var t in touches)
                {
                    if (t.State == TouchLocationState.Pressed)
                    {
                        _menuTwoFingerTapPending = true;
                        break;
                    }
                }
            }

            foreach (var touch in touches)
            {
                if (touch.Id == _trackpadPrimaryTouchId) trackpadPrimaryPresent = true;
                if (touch.Id == _trackpadSecondTouchId) trackpadSecondPresent = true;

                // Map raw touch coordinate to viewport coordinate
                Point pt = MapTouchToViewport(touch.Position);

                // Inventory Action Bar check (when menu is active, Amendment 2)
                if (Game1.activeClickableMenu != null && Settings.EnableInventoryToolbar)
                {
                    if (_btnInventoryOneRect.Contains(pt))
                    {
                        if (touch.State == TouchLocationState.Pressed)
                        {
                            IsSingleItemMode = !IsSingleItemMode;
                            if (IsSingleItemMode) IsHalfStackMode = false;
                            try { Game1.playSound("drumkit6"); } catch { }
                        }
                        continue;
                    }
                    if (_btnInventoryHalfRect.Contains(pt))
                    {
                        if (touch.State == TouchLocationState.Pressed)
                        {
                            IsHalfStackMode = !IsHalfStackMode;
                            if (IsHalfStackMode) IsSingleItemMode = false;
                            try { Game1.playSound("drumkit6"); } catch { }
                        }
                        continue;
                    }
                    if (_btnInventoryXferRect.Contains(pt))
                    {
                        if (touch.State == TouchLocationState.Pressed)
                        {
                            IsQuickTransferMode = !IsQuickTransferMode;
                            try { Game1.playSound("drumkit6"); } catch { }
                        }
                        continue;
                    }
                }

                // Priority 1: If this touch is already claimed by the joystick, continue tracking it even if dragged across screen
                if (_stickTouchId != -1 && touch.Id == _stickTouchId)
                {
                    if (touch.State == TouchLocationState.Released)
                    {
                        _stickTouchId = -1;
                        LeftStick = Vector2.Zero;
                        _currentStickPos = _joystickCenter;
                    }
                    else
                    {
                        stickTouchPresent = true;
                        UpdateJoystickVector(new Vector2(pt.X, pt.Y));
                    }
                    continue;
                }

                // Priority 1b: If this touch is already claimed by Button X or Button A, keep holding it until Released
                if (_btnXTouchId != -1 && touch.Id == _btnXTouchId)
                {
                    if (touch.State == TouchLocationState.Released)
                    {
                        _btnXTouchId = -1;
                    }
                    else
                    {
                        btnXTouchPresent = true;
                        ButtonX = true;
                    }
                    continue;
                }

                if (_btnATouchId != -1 && touch.Id == _btnATouchId)
                {
                    if (touch.State == TouchLocationState.Released)
                    {
                        _btnATouchId = -1;
                    }
                    else
                    {
                        btnATouchPresent = true;
                        ButtonA = true;
                    }
                    continue;
                }

                // Priority 2: If this touch is already claimed by the trackpad, continue tracking it
                if ((_trackpadPrimaryTouchId != -1 && touch.Id == _trackpadPrimaryTouchId) ||
                    (_trackpadSecondTouchId != -1 && touch.Id == _trackpadSecondTouchId))
                {
                    HandleTrackpadTouch(touch, gameTime);
                    continue;
                }

                // Top utility bar check
                if (touch.State == TouchLocationState.Pressed || touch.State == TouchLocationState.Moved)
                {
                    if (_btnToggleRect.Contains(pt))
                    {
                        if (touch.State == TouchLocationState.Pressed)
                            IsVisible = !IsVisible;
                        continue;
                    }
                    if (_btnSettingsRect.Contains(pt))
                    {
                        if (touch.State == TouchLocationState.Pressed)
                        {
                            IsSettingsOpen = true;
                            _isHotkeyRebindMode = false;
                        }
                        continue;
                    }
                    if (Settings.ShowKeyboardBtn && !_btnKeyboardRect.IsEmpty && _btnKeyboardRect.Contains(pt))
                    {
                        if (touch.State == TouchLocationState.Pressed && (curTime - _lastKeyBtnPressTime > 0.4))
                        {
                            _lastKeyBtnPressTime = curTime;
                            TriggerVirtualKeyboard();
                        }
                        continue;
                    }
                    if (Settings.ShowMenuBtn && !_btnMenuRect.IsEmpty && _btnMenuRect.Contains(pt))
                    {
                        menuTouchFound = true;
                        if (!_menuTouchActive && (curTime - _lastMenuPressTime > 0.25))
                        {
                            _menuVisualFrames = 4;
                            _lastMenuPressTime = curTime;
                            if (ModEntry.VerboseInputLogging)
                            {
                                Log($"[TVP_DEBUG] Touch on Button Menu! touchState={touch.State}, touchId={touch.Id}, pt=({pt.X},{pt.Y}), curMenu={Game1.activeClickableMenu?.GetType().Name ?? "none"}");
                            }
                            if (Game1.activeClickableMenu != null)
                            {
                                PlayerRequestedMenuOpen = false;
                                try
                                {
                                    if (Game1.activeClickableMenu is DialogueBox db)
                                    {
                                        db.closeDialogue();
                                        Game1.activeClickableMenu = null;
                                        Game1.dialogueUp = false;
                                        if (Game1.player != null) Game1.player.CanMove = true;
                                    }
                                    else if (Game1.activeClickableMenu.readyToClose())
                                        Game1.activeClickableMenu.exitThisMenu();
                                }
                                catch (Exception ex)
                                {
                                    if (ModEntry.VerboseInputLogging)
                                        Log($"[TVP_DEBUG] Exception closing menu on MenuBtn: {ex.Message}");
                                }
                                // Only pulse Escape key if menu did not already close
                                if (Game1.activeClickableMenu != null)
                                {
                                    _menuPulseFrames = 1;
                                }
                            }
                            else
                            {
                                PlayerRequestedMenuOpen = true;
                                _menuPulseFrames = 1;
                            }
                        }
                        continue;
                    }
                    if (Settings.ShowHotkeyDrawerBtn && !_btnHotkeyDrawerToggleRect.IsEmpty && _btnHotkeyDrawerToggleRect.Contains(pt))
                    {
                        if (touch.State == TouchLocationState.Pressed && (curTime - _lastDrawerToggleTime > 0.20))
                        {
                            _lastDrawerToggleTime = curTime;
                            IsHotkeyDrawerOpen = !IsHotkeyDrawerOpen;
                            if (!IsHotkeyDrawerOpen)
                                _isHotkeyRebindMode = false;
                        }
                        continue;
                    }
                }

                // Mod Hotkey Drawer check when open
                if (IsHotkeyDrawerOpen && Settings.ShowHotkeyDrawerBtn && _hotkeyDrawerRect.Contains(pt))
                {
                    if (touch.State == TouchLocationState.Pressed || touch.State == TouchLocationState.Moved)
                    {
                        if (touch.State == TouchLocationState.Pressed && (curTime - _lastDrawerActionTime > 0.18))
                        {
                            if (_btnHotkeyBindModeRect.Contains(pt))
                            {
                                _lastDrawerActionTime = curTime;
                                _isHotkeyRebindMode = !_isHotkeyRebindMode;
                                continue;
                            }
                            if (_btnHotkeyAutoCloseRect.Contains(pt))
                            {
                                _lastDrawerActionTime = curTime;
                                Settings.AutoCloseHotkeyDrawer = !Settings.AutoCloseHotkeyDrawer;
                                Settings.Save();
                                continue;
                            }
                            if (_btnHotkeyCloseDrawerRect.Contains(pt))
                            {
                                _lastDrawerActionTime = curTime;
                                IsHotkeyDrawerOpen = false;
                                _isHotkeyRebindMode = false;
                                continue;
                            }
                            if (_btnQuickSaveRect.Contains(pt))
                            {
                                _lastDrawerActionTime = curTime;
                                QuickSaveManager.Instance?.QuickSave();
                                if (Settings.AutoCloseHotkeyDrawer)
                                {
                                    IsHotkeyDrawerOpen = false;
                                }
                                continue;
                            }
                            if (_btnQuickLoadRect.Contains(pt))
                            {
                                _lastDrawerActionTime = curTime;
                                IsHotkeyDrawerOpen = false;
                                QuickSaveManager.Instance?.QuickLoad();
                                continue;
                            }
                        }

                        for (int i = 0; i < TouchOverlaySettings.HotkeySlotCount; i++)
                        {
                            if (_hotkeySlotRects[i].Contains(pt))
                            {
                                if (_isHotkeyRebindMode)
                                {
                                    if (touch.State == TouchLocationState.Pressed && (curTime - _lastDrawerActionTime > 0.20))
                                    {
                                        _lastDrawerActionTime = curTime;
                                        _editingHotkeySlotIndex = i;
                                        _isHotkeyRebindMode = false;
                                        IsKeyPickerOpen = true;
                                    }
                                }
                                else
                                {
                                    hotkeyTouchFound[i] = true;
                                    if (!_hotkeyTouchActive[i] && (curTime - _lastHotkeyPressTime[i] > 0.18))
                                    {
                                        _lastHotkeyPressTime[i] = curTime;
                                        _hotkeyPulseFrames[i] = 3;
                                        _hotkeyVisualFrames[i] = 6;
                                        PlayerRequestedMenuOpen = true;
                                        OnHotkeySlotActivated(i);
                                        if (Settings.AutoCloseHotkeyDrawer)
                                        {
                                            IsHotkeyDrawerOpen = false;
                                        }
                                    }
                                }
                                break;
                            }
                        }
                    }
                    continue;
                }

                // If overlay is hidden, treat all touches as screen clicks
                if (!IsVisible)
                {
                    HandleBackgroundTouch(touch, pt, gameTime);
                    continue;
                }

                // Check Action & Trigger Buttons
                bool hitButton = false;
                if (_btnARect.Contains(pt) && touch.State != TouchLocationState.Released)
                {
                    ButtonA = true;
                    hitButton = true;
                    if (_btnATouchId == -1)
                    {
                        _btnATouchId = touch.Id;
                        btnATouchPresent = true;
                        if (_trackpadPrimaryTouchId == touch.Id) _trackpadPrimaryTouchId = -1;
                        if (_trackpadSecondTouchId == touch.Id) _trackpadSecondTouchId = -1;
                        if (_pointClickTouchId == touch.Id) _pointClickTouchId = -1;
                    }
                }
                if (_btnXRect.Contains(pt) && touch.State != TouchLocationState.Released)
                {
                    ButtonX = true;
                    hitButton = true;
                    if (_btnXTouchId == -1)
                    {
                        _btnXTouchId = touch.Id;
                        btnXTouchPresent = true;
                        if (_trackpadPrimaryTouchId == touch.Id) _trackpadPrimaryTouchId = -1;
                        if (_trackpadSecondTouchId == touch.Id) _trackpadSecondTouchId = -1;
                        if (_pointClickTouchId == touch.Id) _pointClickTouchId = -1;
                    }
                }
                if (_btnLTRect.Contains(pt) && touch.State != TouchLocationState.Released) { ltTouchFound = true; hitButton = true; }
                if (_btnRTRect.Contains(pt) && touch.State != TouchLocationState.Released) { rtTouchFound = true; hitButton = true; }

                if (_btnYRect.Contains(pt))
                {
                    yTouchFound = true;
                    hitButton = true;
                    if (!_yTouchActive && (curTime - _lastYPressTime > 0.25))
                    {
                        _yPulseFrames = 1;
                        _yVisualFrames = 4;
                        _lastYPressTime = curTime;
                        PlayerRequestedMenuOpen = true;
                        if (ModEntry.VerboseInputLogging)
                        {
                            Log($"[TVP_DEBUG] Touch on Button Y! touchState={touch.State}, touchId={touch.Id}, pt=({pt.X},{pt.Y}), curMenu={Game1.activeClickableMenu?.GetType().Name ?? "none"}");
                        }
                    }
                }

                if (_btnBRect.Contains(pt))
                {
                    bTouchFound = true;
                    hitButton = true;
                    if (!_bTouchActive && (curTime - _lastBPressTime > 0.25))
                    {
                        _bVisualFrames = 4;
                        _lastBPressTime = curTime;
                        PlayerRequestedMenuOpen = false;
                        if (ModEntry.VerboseInputLogging)
                        {
                            Log($"[TVP_DEBUG] Touch on Button B! touchState={touch.State}, touchId={touch.Id}, pt=({pt.X},{pt.Y}), curMenu={Game1.activeClickableMenu?.GetType().Name ?? "none"}");
                        }
                        if (Game1.activeClickableMenu != null)
                        {
                            try
                            {
                                if (Game1.activeClickableMenu is DialogueBox db)
                                {
                                    db.closeDialogue();
                                    Game1.activeClickableMenu = null;
                                    Game1.dialogueUp = false;
                                    if (Game1.player != null) Game1.player.CanMove = true;
                                }
                                else if (Game1.activeClickableMenu.readyToClose())
                                {
                                    Game1.activeClickableMenu.exitThisMenu();
                                }
                            }
                            catch (Exception ex)
                            {
                                if (ModEntry.VerboseInputLogging)
                                    Log($"[TVP_DEBUG] Exception closing menu on ButtonB: {ex.Message}");
                            }
                            // Only pulse Escape if menu did not already close
                            if (Game1.activeClickableMenu != null)
                            {
                                _bPulseFrames = 1;
                            }
                        }
                        else if (Game1.eventUp)
                        {
                            _bPulseFrames = 1;
                        }
                    }
                }

                if (hitButton) continue;

                // Acquire Joystick if touch starts in joystick zone
                if (_stickTouchId == -1 && (touch.State == TouchLocationState.Pressed || touch.State == TouchLocationState.Moved))
                {
                    float dist = Vector2.Distance(new Vector2(pt.X, pt.Y), _joystickCenter);
                    if (_joystickBaseRect.Contains(pt) || dist <= _joystickRadius * 1.4f)
                    {
                        _stickTouchId = touch.Id;
                        stickTouchPresent = true;
                        if (_trackpadPrimaryTouchId == touch.Id) _trackpadPrimaryTouchId = -1;
                        if (_trackpadSecondTouchId == touch.Id) _trackpadSecondTouchId = -1;
                        if (_pointClickTouchId == touch.Id) _pointClickTouchId = -1;
                        UpdateJoystickVector(new Vector2(pt.X, pt.Y));
                        continue;
                    }
                }

                // Check active point & click touch
                if (touch.Id == _pointClickTouchId)
                {
                    pointClickTouchPresent = true;
                }

                // Touch on background area (outside virtual pad buttons)
                HandleBackgroundTouch(touch, pt, gameTime);
            }

            if (!stickTouchPresent && _stickTouchId != -1)
            {
                _stickTouchId = -1;
                LeftStick = Vector2.Zero;
                _currentStickPos = _joystickCenter;
            }

            if (!btnXTouchPresent && _btnXTouchId != -1)
            {
                _btnXTouchId = -1;
            }

            if (!btnATouchPresent && _btnATouchId != -1)
            {
                _btnATouchId = -1;
            }

            if (!pointClickTouchPresent && _pointClickTouchId != -1)
            {
                _pointClickTouchId = -1;
            }

            if (!trackpadPrimaryPresent && _trackpadPrimaryTouchId != -1)
            {
                _trackpadPrimaryTouchId = -1;
                if (_trackpadSecondTouchId == -1)
                    _trackpadHadSecondTouch = false;
            }

            if (!trackpadSecondPresent && _trackpadSecondTouchId != -1)
            {
                _trackpadSecondTouchId = -1;
                if (_trackpadPrimaryTouchId == -1)
                    _trackpadHadSecondTouch = false;
            }

            // Process Left Trigger (LT) and Right Trigger (RT) states, hold-repeat, and dual-press row shift
            if (!ltTouchFound && !rtTouchFound)
            {
                _comboShiftTriggered = false;
            }
            else if (ltTouchFound && rtTouchFound)
            {
                ButtonLT = true;
                ButtonRT = true;
                if (!_comboShiftTriggered && (curTime - _lastToolbarShiftTime > 0.30))
                {
                    _comboShiftTriggered = true;
                    _lastToolbarShiftTime = curTime;
                    _ltVisualFrames = 6;
                    _rtVisualFrames = 6;
                    if (Game1.activeClickableMenu == null && Game1.player != null && !Game1.player.UsingTool && !Game1.eventUp && !Game1.freezeControls)
                    {
                        try { Game1.player.shiftToolbar(true); } catch { }
                    }
                }
            }
            else if (ltTouchFound && !_comboShiftTriggered)
            {
                ButtonLT = true;
                if (!_ltTouchActive && (curTime - _lastLTPressTime > 0.12))
                {
                    _lastLTPressTime = curTime;
                    _ltNextRepeatTime = curTime + 0.35;
                    _ltVisualFrames = 4;
                    OnTriggerActivated(isRight: false);
                }
                else if (_ltTouchActive && Game1.activeClickableMenu == null && curTime >= _ltNextRepeatTime)
                {
                    _ltNextRepeatTime = curTime + 0.11;
                    _ltVisualFrames = 2;
                    OnTriggerActivated(isRight: false);
                }
            }
            else if (rtTouchFound && !_comboShiftTriggered)
            {
                ButtonRT = true;
                if (!_rtTouchActive && (curTime - _lastRTPressTime > 0.12))
                {
                    _lastRTPressTime = curTime;
                    _rtNextRepeatTime = curTime + 0.35;
                    _rtVisualFrames = 4;
                    OnTriggerActivated(isRight: true);
                }
                else if (_rtTouchActive && Game1.activeClickableMenu == null && curTime >= _rtNextRepeatTime)
                {
                    _rtNextRepeatTime = curTime + 0.11;
                    _rtVisualFrames = 2;
                    OnTriggerActivated(isRight: true);
                }
            }

            _ltTouchActive = ltTouchFound;
            _rtTouchActive = rtTouchFound;
            _yTouchActive = yTouchFound;
            _bTouchActive = bTouchFound;
            _menuTouchActive = menuTouchFound;
            for (int i = 0; i < TouchOverlaySettings.HotkeySlotCount; i++)
            {
                _hotkeyTouchActive[i] = hotkeyTouchFound[i];
            }

            ButtonY = _yPulseFrames > 0;
            ButtonB = _bPulseFrames > 0;
            ButtonMenu = _menuPulseFrames > 0;

            ForwardInputToGame();
        }

        private void OnHotkeySlotActivated(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= TouchOverlaySettings.HotkeySlotCount) return;
            try
            {
                Keys key = Settings.HotkeyKeys[slotIndex];
                if (key == Keys.None) return;

                if (key == Keys.F5 || key == Keys.K)
                {
                    QuickSaveManager.Instance?.QuickSave();
                    return;
                }
                if (key == Keys.F7)
                {
                    QuickSaveManager.Instance?.QuickLoad();
                    return;
                }

                if (Game1.activeClickableMenu != null)
                {
                    if (key == Keys.Escape)
                    {
                        PlayerRequestedMenuOpen = false;
                        try
                        {
                            if (Game1.activeClickableMenu.readyToClose())
                                Game1.activeClickableMenu.exitThisMenu();
                        }
                        catch { }
                    }
                    else
                    {
                        try { Game1.activeClickableMenu.receiveKeyPress(key); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                if (ModEntry.VerboseInputLogging)
                    Log($"[TVP_DEBUG] OnHotkeySlotActivated({slotIndex}) error: {ex.Message}");
            }
        }

        private void OnTriggerActivated(bool isRight)
        {
            try
            {
                if (Game1.activeClickableMenu != null)
                {
                    Buttons btn = isRight ? Buttons.RightTrigger : Buttons.LeftTrigger;
                    Game1.activeClickableMenu.receiveGamePadButton(btn);
                    return;
                }

                var player = Game1.player;
                if (player != null && !player.UsingTool && !Game1.eventUp && !Game1.freezeControls)
                {
                    int dir = isRight ? 1 : -1;
                    int cur = player.CurrentToolIndex;
                    int next = (cur + dir) % 12;
                    if (next < 0) next += 12;
                    player.CurrentToolIndex = next;
                    try { Game1.playSound("toolSwap"); } catch { }
                }
            }
            catch (Exception ex)
            {
                if (ModEntry.VerboseInputLogging)
                    Log($"[TVP_DEBUG] OnTriggerActivated({(isRight ? "RT" : "LT")}) error: {ex.Message}");
            }
        }

        private void TriggerVirtualKeyboard()
        {
            try
            {
                Type? vkmType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == "SDViOS")
                    {
                        vkmType = asm.GetType("SDViOS.Input.VirtualKeyboardManager");
                        break;
                    }
                }
                if (vkmType == null) return;

                var subscriber = Game1.keyboardDispatcher?.Subscriber;
                if (subscriber != null)
                {
                    var showMethod = vkmType.GetMethod("ShowKeyboard", BindingFlags.Public | BindingFlags.Static);
                    showMethod?.Invoke(null, new object[] { subscriber });
                    return;
                }

                var promptMethod = vkmType.GetMethod("PromptManualInput", BindingFlags.Public | BindingFlags.Static);
                if (promptMethod != null)
                {
                    Action<string> callback = FeedTextToGame;
                    promptMethod.Invoke(null, new object[] { callback });
                }
            }
            catch (Exception ex)
            {
                Log($"[TouchVirtualPad] Error triggering virtual keyboard: {ex.Message}");
            }
        }

        private void PromptRenameHotkeyLabel(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= TouchOverlaySettings.HotkeySlotCount) return;
            try
            {
                Type? vkmType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == "SDViOS")
                    {
                        vkmType = asm.GetType("SDViOS.Input.VirtualKeyboardManager");
                        break;
                    }
                }
                if (vkmType != null)
                {
                    var promptMethod = vkmType.GetMethod("PromptManualInput", BindingFlags.Public | BindingFlags.Static);
                    if (promptMethod != null)
                    {
                        Action<string> callback = text =>
                        {
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                Settings.HotkeyLabels[slotIndex] = TouchOverlaySettings.SanitizeHotkeyLabel(text);
                                Settings.Save();
                            }
                        };
                        promptMethod.Invoke(null, new object[] { callback });
                        return;
                    }
                }
                CyclePresetHotkeyLabel(slotIndex);
            }
            catch
            {
                CyclePresetHotkeyLabel(slotIndex);
            }
        }

        private static readonly string[] PresetHotkeyLabels = new[]
        {
            "CHEAT", "ITEMS", "INFO", "DEBUG", "AUTO", "MAP", "MOD N", "NOCLIP", "TIME", "WARP", "SAVE", "LOAD", "CFG", "MOD 1", "MOD 2", "ESC"
        };

        private void CyclePresetHotkeyLabel(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= TouchOverlaySettings.HotkeySlotCount) return;
            string cur = Settings.HotkeyLabels[slotIndex];
            int idx = Array.IndexOf(PresetHotkeyLabels, cur);
            int nextIdx = (idx + 1) % PresetHotkeyLabels.Length;
            Settings.HotkeyLabels[slotIndex] = PresetHotkeyLabels[nextIdx];
            Settings.Save();
        }

        private void FeedTextToGame(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var subscriber = Game1.keyboardDispatcher?.Subscriber;
                if (subscriber != null)
                {
                    var textProp = subscriber.GetType().GetProperty("Text", BindingFlags.Public | BindingFlags.Instance);
                    if (textProp != null && textProp.CanWrite)
                    {
                        textProp.SetValue(subscriber, text);
                        return;
                    }
                    foreach (char c in text)
                    {
                        subscriber.RecieveTextInput(c);
                    }
                }
                else if (Game1.chatBox != null)
                {
                    Game1.chatBox.activate();
                    if (Game1.chatBox.chatBox != null)
                    {
                        Game1.chatBox.chatBox.Text = text;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[TouchVirtualPad] FeedTextToGame warning: {ex.Message}");
            }
        }

        private Point MapTouchToViewport(Vector2 rawPos)
        {
            if (_rawScreenWidth <= 0 || _rawScreenHeight <= 0 || _viewportWidth <= 0 || _viewportHeight <= 0)
            {
                return new Point((int)rawPos.X, (int)rawPos.Y);
            }

            float normX = rawPos.X / _rawScreenWidth;
            float normY = rawPos.Y / _rawScreenHeight;
            return new Point((int)(normX * _viewportWidth), (int)(normY * _viewportHeight));
        }

        private void HandleBackgroundTouch(TouchLocation touch, Point pt, GameTime gameTime)
        {
            if (Settings.MouseControlMode == MouseMode.Disabled) return;

            // Never process touches on the top bar or open Mod Hotkey Drawer as background mouse events
            if (_btnToggleRect.Contains(pt) || _btnSettingsRect.Contains(pt) ||
                (!_btnKeyboardRect.IsEmpty && _btnKeyboardRect.Contains(pt)) ||
                (!_btnMenuRect.IsEmpty && _btnMenuRect.Contains(pt)) ||
                (!_btnHotkeyDrawerToggleRect.IsEmpty && _btnHotkeyDrawerToggleRect.Contains(pt)) ||
                (IsHotkeyDrawerOpen && Settings.ShowHotkeyDrawerBtn && _hotkeyDrawerRect.Contains(pt)))
            {
                return;
            }

            // Never process touches on the virtual pad as background mouse events!
            if (IsVisible)
            {
                if (_joystickBaseRect.Contains(pt) || Vector2.Distance(new Vector2(pt.X, pt.Y), _joystickCenter) <= _joystickRadius * 1.4f)
                    return;
                if (_btnARect.Contains(pt) || _btnBRect.Contains(pt) || _btnXRect.Contains(pt) || _btnYRect.Contains(pt) ||
                    _btnLTRect.Contains(pt) || _btnRTRect.Contains(pt) ||
                    Vector2.Distance(new Vector2(pt.X, pt.Y), _buttonsCenter) <= 125f)
                    return;
            }

            if (ModEntry.VerboseInputLogging && Game1.activeClickableMenu != null)
            {
                Log($"[TVP_DEBUG] HandleBackgroundTouch while menu={Game1.activeClickableMenu.GetType().Name}! touchState={touch.State}, touchId={touch.Id}, pt=({pt.X},{pt.Y}), mouseMode={Settings.MouseControlMode}");
            }

            if (Game1.activeClickableMenu is DialogueBox db && db.characterDialogue == null && !db.isQuestion)
            {
                if (touch.State == TouchLocationState.Pressed)
                {
                    try
                    {
                        db.closeDialogue();
                    }
                    catch { }
                    Game1.activeClickableMenu = null;
                    Game1.dialogueUp = false;
                    if (Game1.player != null) Game1.player.CanMove = true;
                    return;
                }
            }

            bool inMenu = Game1.activeClickableMenu != null;
            if (inMenu || Settings.MouseControlMode == MouseMode.PointAndClick)
            {
                SimulatedMousePosition = new Point((int)Math.Round(touch.Position.X), (int)Math.Round(touch.Position.Y));

                if (touch.State == TouchLocationState.Pressed)
                {
                    _pointClickTouchId = touch.Id;
                    _menuTouchStartPos = touch.Position;
                    _menuTouchHoldTimer = 0.0;
                    _dispenseIntervalTimer = 0.0;
                    _menuTouchHasDispensed = false;

                    // Check explicit double-tap bait attachment on fishing rod (Amendment 4)
                    bool isBaitDoubleTap = false;
                    if (inMenu)
                    {
                        try
                        {
                            var held = Game1.player?.CursorSlotItem as StardewValley.Object;
                            if (held != null && (held.Category == -21 || held.Category == -22))
                            {
                                var now = DateTime.UtcNow;
                                if ((now - _lastBaitTapTime).TotalMilliseconds < 400.0 &&
                                    Vector2.Distance(touch.Position, new Vector2(_lastBaitTapPos.X, _lastBaitTapPos.Y)) < 40f)
                                {
                                    isBaitDoubleTap = true;
                                    _lastBaitTapTime = DateTime.MinValue;
                                }
                                else
                                {
                                    _lastBaitTapTime = now;
                                    _lastBaitTapPos = new Point((int)Math.Round(touch.Position.X), (int)Math.Round(touch.Position.Y));
                                }
                            }
                        }
                        catch { }
                    }

                    if (inMenu && (IsSingleItemMode || IsHalfStackMode || _menuTwoFingerTapPending || isBaitDoubleTap))
                    {
                        SimulatedMouseRightDown = true;
                        _simulatedRightClickFrames = 2;
                        SimulatedMouseLeftDown = false;
                        _pointClickHoldFrames = 0;
                        _menuTwoFingerTapPending = false;
                    }
                    else
                    {
                        SimulatedMouseLeftDown = true;
                        _pointClickHoldFrames = 2; // Hold for 2 ticks to guarantee clean rising & falling edge
                    }
                    Game1.lastCursorMotionWasMouse = true;
                }
                else if (touch.State == TouchLocationState.Moved)
                {
                    if (_pointClickTouchId == touch.Id || _pointClickTouchId == -1)
                    {
                        _pointClickTouchId = touch.Id;
                        if (Vector2.Distance(touch.Position, _menuTouchStartPos) > 25f)
                        {
                            // Dragged away: cancel hold-to-dispense
                            _menuTouchHoldTimer = -1000.0;
                        }
                        if (!inMenu || (!IsSingleItemMode && !IsHalfStackMode && !_menuTouchHasDispensed))
                        {
                            SimulatedMouseLeftDown = true;
                            _pointClickHoldFrames = 2;
                        }
                        Game1.lastCursorMotionWasMouse = true;
                    }
                }
                else if (touch.State == TouchLocationState.Released)
                {
                    if (_pointClickTouchId == -1)
                    {
                        // Ultra-fast micro-tap delivered directly as Released!
                        if (inMenu && (IsSingleItemMode || IsHalfStackMode || _menuTwoFingerTapPending))
                        {
                            SimulatedMouseRightDown = true;
                            _simulatedRightClickFrames = 2;
                            SimulatedMouseLeftDown = false;
                            _pointClickHoldFrames = 0;
                            _menuTwoFingerTapPending = false;
                        }
                        else
                        {
                            SimulatedMouseLeftDown = true;
                            _pointClickHoldFrames = 2;
                        }
                        Game1.lastCursorMotionWasMouse = true;
                    }
                    else if (_pointClickTouchId == touch.Id)
                    {
                        _pointClickTouchId = -1;
                        if (_menuTouchHasDispensed)
                        {
                            // Suppress release Left-Click so remaining stack is NOT dropped
                            SimulatedMouseLeftDown = false;
                            _pointClickHoldFrames = 0;
                        }
                    }
                }
                return;
            }

            if (Settings.MouseControlMode == MouseMode.Trackpad)
            {
                HandleTrackpadTouch(touch, gameTime);
            }
        }

        private void HandleTrackpadTouch(TouchLocation touch, GameTime gameTime)
        {
            float curTime = (float)gameTime.TotalGameTime.TotalSeconds;

            if (_trackpadPrimaryTouchId == -1)
            {
                if (touch.State == TouchLocationState.Pressed || touch.State == TouchLocationState.Moved)
                {
                    _trackpadPrimaryTouchId = touch.Id;
                    _trackpadLastTouchPos = touch.Position;
                    _trackpadTouchStartTime = curTime;
                    _trackpadTotalDistMoved = 0f;
                    _trackpadHadSecondTouch = false;
                    Game1.lastCursorMotionWasMouse = true;
                }
            }
            else if (touch.Id == _trackpadPrimaryTouchId)
            {
                if (touch.State == TouchLocationState.Moved)
                {
                    Vector2 delta = touch.Position - _trackpadLastTouchPos;
                    _trackpadLastTouchPos = touch.Position;
                    _trackpadTotalDistMoved += delta.Length();

                    _trackpadCursorPos += delta * Settings.TrackpadSensitivity;
                    int maxX = _rawScreenWidth > 0 ? _rawScreenWidth - 1 : 1791;
                    int maxY = _rawScreenHeight > 0 ? _rawScreenHeight - 1 : 827;
                    _trackpadCursorPos.X = Math.Clamp(_trackpadCursorPos.X, 0, maxX);
                    _trackpadCursorPos.Y = Math.Clamp(_trackpadCursorPos.Y, 0, maxY);
                    SimulatedMousePosition = new Point((int)Math.Round(_trackpadCursorPos.X), (int)Math.Round(_trackpadCursorPos.Y));
                    Game1.lastCursorMotionWasMouse = true;
                }
                else if (touch.State == TouchLocationState.Released)
                {
                    float duration = curTime - _trackpadTouchStartTime;
                    if (!_trackpadHadSecondTouch && duration < 0.45f && _trackpadTotalDistMoved < 30f && (curTime - _lastLeftClickTime > 0.15f))
                    {
                        _trackpadLeftClickFrames = 3;
                        SimulatedMouseLeftDown = true;
                        _lastLeftClickTime = curTime;
                        Game1.lastCursorMotionWasMouse = true;
                    }
                    _trackpadPrimaryTouchId = -1;
                }
            }
            else if (_trackpadSecondTouchId == -1)
            {
                if (touch.State == TouchLocationState.Pressed || touch.State == TouchLocationState.Moved)
                {
                    _trackpadSecondTouchId = touch.Id;
                    _trackpadHadSecondTouch = true;
                }
            }
            else if (touch.Id == _trackpadSecondTouchId)
            {
                if (touch.State == TouchLocationState.Released)
                {
                    float duration = curTime - _trackpadTouchStartTime;
                    if (duration < 0.55f && _trackpadTotalDistMoved < 50f && (curTime - _lastRightClickTime > 0.25f))
                    {
                        _trackpadRightClickFrames = 3;
                        SimulatedMouseRightDown = true;
                        _lastRightClickTime = curTime;
                        Game1.lastCursorMotionWasMouse = true;
                    }
                    _trackpadSecondTouchId = -1;
                }
            }
        }

        private void UpdateSettingsTouches(TouchCollection touches)
        {
            foreach (var touch in touches)
            {
                if (touch.State != TouchLocationState.Pressed) continue;
                Point pt = MapTouchToViewport(touch.Position);

                if (_settingsCloseRect.Contains(pt) || _btnSaveClose.Contains(pt))
                {
                    Settings.Save();
                    IsSettingsOpen = false;
                    break;
                }
                if (_btnOpacityMinus.Contains(pt))
                {
                    Settings.Opacity = (float)Math.Round(Math.Clamp(Settings.Opacity - 0.10f, 0.20f, 1.0f), 2);
                    Settings.Save();
                    break;
                }
                if (_btnOpacityPlus.Contains(pt))
                {
                    Settings.Opacity = (float)Math.Round(Math.Clamp(Settings.Opacity + 0.10f, 0.20f, 1.0f), 2);
                    Settings.Save();
                    break;
                }
                if (_btnScaleMinus.Contains(pt))
                {
                    Settings.Scale = (float)Math.Round(Math.Clamp(Settings.Scale - 0.15f, 0.60f, 1.50f), 2);
                    UpdateLayout(_viewportWidth, _viewportHeight);
                    Settings.Save();
                    break;
                }
                if (_btnScalePlus.Contains(pt))
                {
                    Settings.Scale = (float)Math.Round(Math.Clamp(Settings.Scale + 0.15f, 0.60f, 1.50f), 2);
                    UpdateLayout(_viewportWidth, _viewportHeight);
                    Settings.Save();
                    break;
                }
                if (_btnHandedness.Contains(pt))
                {
                    Settings.LeftHanded = !Settings.LeftHanded;
                    Settings.CustomPositionsSet = false;
                    Settings.CustomTriggersSet = false;
                    UpdateLayout(_viewportWidth, _viewportHeight);
                    Settings.Save();
                    break;
                }
                if (_btnDeadzone.Contains(pt))
                {
                    if (Settings.Deadzone <= 0.18f) Settings.Deadzone = 0.25f;
                    else if (Settings.Deadzone <= 0.28f) Settings.Deadzone = 0.35f;
                    else Settings.Deadzone = 0.15f;
                    Settings.Save();
                    break;
                }
                if (_btnToggleKeyboard.Contains(pt))
                {
                    Settings.ShowKeyboardBtn = !Settings.ShowKeyboardBtn;
                    UpdateLayout(_viewportWidth, _viewportHeight);
                    Settings.Save();
                    break;
                }
                if (_btnToggleMenu.Contains(pt))
                {
                    Settings.ShowMenuBtn = !Settings.ShowMenuBtn;
                    UpdateLayout(_viewportWidth, _viewportHeight);
                    Settings.Save();
                    break;
                }
                if (_btnToggleHotkeyDrawer.Contains(pt))
                {
                    Settings.ShowHotkeyDrawerBtn = !Settings.ShowHotkeyDrawerBtn;
                    UpdateLayout(_viewportWidth, _viewportHeight);
                    Settings.Save();
                    break;
                }
                if (_btnToggleMouseMode.Contains(pt))
                {
                    if (Settings.MouseControlMode == MouseMode.PointAndClick)
                        Settings.MouseControlMode = MouseMode.Trackpad;
                    else if (Settings.MouseControlMode == MouseMode.Trackpad)
                        Settings.MouseControlMode = MouseMode.Disabled;
                    else
                        Settings.MouseControlMode = MouseMode.PointAndClick;

                    Settings.Save();
                    break;
                }
                if (_btnSensitivityMinus.Contains(pt))
                {
                    Settings.TrackpadSensitivity = (float)Math.Round(Math.Clamp(Settings.TrackpadSensitivity - 0.20f, 0.40f, 3.0f), 1);
                    Settings.Save();
                    break;
                }
                if (_btnSensitivityPlus.Contains(pt))
                {
                    Settings.TrackpadSensitivity = (float)Math.Round(Math.Clamp(Settings.TrackpadSensitivity + 0.20f, 0.40f, 3.0f), 1);
                    Settings.Save();
                    break;
                }
                if (_btnEditLayout.Contains(pt))
                {
                    IsSettingsOpen = false;
                    IsEditLayoutMode = true;
                    _activeDragTarget = 0;
                    _dragTouchId = -1;
                    break;
                }
                if (_btnResetDefaults.Contains(pt))
                {
                    Settings.ResetToDefaults();
                    _trackpadCursorPos = new Vector2(_rawScreenWidth / 2f, _rawScreenHeight / 2f);
                    UpdateLayout(_viewportWidth, _viewportHeight);
                    break;
                }
            }
        }

        private static readonly Keys[][] KeyPickerRows = new Keys[][]
        {
            new[] { Keys.F1, Keys.F2, Keys.F3, Keys.F4, Keys.F5, Keys.F6, Keys.F7, Keys.F8, Keys.F9, Keys.F10, Keys.F11, Keys.F12 },
            new[] { Keys.A, Keys.B, Keys.C, Keys.D, Keys.E, Keys.F, Keys.G, Keys.H, Keys.I, Keys.J, Keys.K, Keys.L, Keys.M },
            new[] { Keys.N, Keys.O, Keys.P, Keys.Q, Keys.R, Keys.S, Keys.T, Keys.U, Keys.V, Keys.W, Keys.X, Keys.Y, Keys.Z },
            new[] { Keys.D0, Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5, Keys.D6, Keys.D7, Keys.D8, Keys.D9, Keys.OemTilde, Keys.OemComma, Keys.OemPeriod },
            new[] { Keys.Escape, Keys.Tab, Keys.Space, Keys.LeftShift, Keys.LeftControl, Keys.LeftAlt, Keys.Delete, Keys.Insert, Keys.Home, Keys.End, Keys.PageUp, Keys.PageDown }
        };

        public static string FormatKeyShort(Keys key)
        {
            return key switch
            {
                Keys.Escape => "ESC",
                Keys.Space => "SPC",
                Keys.Tab => "TAB",
                Keys.LeftShift or Keys.RightShift => "SHFT",
                Keys.LeftControl or Keys.RightControl => "CTRL",
                Keys.LeftAlt or Keys.RightAlt => "ALT",
                Keys.Delete => "DEL",
                Keys.Insert => "INS",
                Keys.PageUp => "PGUP",
                Keys.PageDown => "PGDN",
                Keys.OemTilde => "TLD",
                Keys.OemComma => ",",
                Keys.OemPeriod => ".",
                >= Keys.D0 and <= Keys.D9 => ((int)(key - Keys.D0)).ToString(),
                _ => key.ToString().ToUpperInvariant()
            };
        }

        private Rectangle GetKeyPickerCellRect(int row, int col, int colsInRow)
        {
            int padX = 20;
            int gridTop = _keyPickerModalRect.Top + 56;
            int availW = _keyPickerModalRect.Width - padX * 2;
            int gapX = 6;
            int gapY = 8;
            int cellH = 44;
            int cellW = (availW - (colsInRow - 1) * gapX) / colsInRow;
            int totalRowW = colsInRow * cellW + (colsInRow - 1) * gapX;
            int rowStartX = _keyPickerModalRect.Left + (_keyPickerModalRect.Width - totalRowW) / 2;
            int x = rowStartX + col * (cellW + gapX);
            int y = gridTop + row * (cellH + gapY);
            return new Rectangle(x, y, cellW, cellH);
        }

        private void UpdateKeyPickerTouches(TouchCollection touches, double curTime)
        {
            if (_editingHotkeySlotIndex < 0 || _editingHotkeySlotIndex >= TouchOverlaySettings.HotkeySlotCount)
            {
                IsKeyPickerOpen = false;
                return;
            }

            foreach (var touch in touches)
            {
                if (touch.State != TouchLocationState.Pressed) continue;
                if (curTime - _lastDrawerActionTime < 0.15) continue;

                Point pt = MapTouchToViewport(touch.Position);

                if (_keyPickerCloseRect.Contains(pt) || _btnKeyPickerCancelRect.Contains(pt))
                {
                    _lastDrawerActionTime = curTime;
                    IsKeyPickerOpen = false;
                    _editingHotkeySlotIndex = -1;
                    break;
                }
                if (_btnKeyPickerRenameRect.Contains(pt))
                {
                    _lastDrawerActionTime = curTime;
                    PromptRenameHotkeyLabel(_editingHotkeySlotIndex);
                    break;
                }
                if (_btnKeyPickerPresetRect.Contains(pt))
                {
                    _lastDrawerActionTime = curTime;
                    CyclePresetHotkeyLabel(_editingHotkeySlotIndex);
                    break;
                }
                if (_btnKeyPickerResetSlotRect.Contains(pt))
                {
                    _lastDrawerActionTime = curTime;
                    TouchOverlaySettings.ResetHotkeySlotToDefault(_editingHotkeySlotIndex, Settings.HotkeyLabels, Settings.HotkeyKeys);
                    Settings.Save();
                    IsKeyPickerOpen = false;
                    _editingHotkeySlotIndex = -1;
                    break;
                }

                bool picked = false;
                for (int r = 0; r < KeyPickerRows.Length && !picked; r++)
                {
                    var rowKeys = KeyPickerRows[r];
                    for (int c = 0; c < rowKeys.Length; c++)
                    {
                        Rectangle cellRect = GetKeyPickerCellRect(r, c, rowKeys.Length);
                        if (cellRect.Contains(pt))
                        {
                            _lastDrawerActionTime = curTime;
                            Settings.HotkeyKeys[_editingHotkeySlotIndex] = rowKeys[c];
                            Settings.Save();
                            IsKeyPickerOpen = false;
                            _editingHotkeySlotIndex = -1;
                            picked = true;
                            break;
                        }
                    }
                }
                if (picked) break;
            }
        }

        private void UpdateEditModeTouches(TouchCollection touches)
        {
            foreach (var touch in touches)
            {
                Point pt = MapTouchToViewport(touch.Position);
                Vector2 pos = new Vector2(pt.X, pt.Y);

                if (touch.State == TouchLocationState.Pressed && _btnDoneEditRect.Contains(pt))
                {
                    IsEditLayoutMode = false;
                    _activeDragTarget = 0;
                    _dragTouchId = -1;
                    Settings.Save();
                    return;
                }

                if (_dragTouchId == touch.Id)
                {
                    if (touch.State == TouchLocationState.Released)
                    {
                        _dragTouchId = -1;
                        _activeDragTarget = 0;
                        Settings.Save();
                    }
                    else
                    {
                        if (_activeDragTarget == 1)
                        {
                            _joystickCenter = new Vector2(
                                Math.Clamp(pos.X, _joystickRadius + 10, _viewportWidth - _joystickRadius - 10),
                                Math.Clamp(pos.Y, _joystickRadius + 10, _viewportHeight - _joystickRadius - 10)
                            );
                            Settings.JoystickNormX = _joystickCenter.X / _viewportWidth;
                            Settings.JoystickNormY = _joystickCenter.Y / _viewportHeight;
                            Settings.CustomPositionsSet = true;
                            UpdateLayout(_viewportWidth, _viewportHeight);
                        }
                        else if (_activeDragTarget == 2)
                        {
                            _buttonsCenter = new Vector2(
                                Math.Clamp(pos.X, 90f + 10, _viewportWidth - 90f - 10),
                                Math.Clamp(pos.Y, 90f + 10, _viewportHeight - 90f - 10)
                            );
                            Settings.ButtonsNormX = _buttonsCenter.X / _viewportWidth;
                            Settings.ButtonsNormY = _buttonsCenter.Y / _viewportHeight;
                            Settings.CustomPositionsSet = true;
                            UpdateLayout(_viewportWidth, _viewportHeight);
                        }
                        else if (_activeDragTarget == 3)
                        {
                            if (!Settings.CustomTriggersSet)
                            {
                                Settings.RightTriggerNormX = _rtCenter.X / _viewportWidth;
                                Settings.RightTriggerNormY = _rtCenter.Y / _viewportHeight;
                            }
                            _ltCenter = new Vector2(
                                Math.Clamp(pos.X, _btnLTRect.Width / 2f + 10, _viewportWidth - _btnLTRect.Width / 2f - 10),
                                Math.Clamp(pos.Y, _btnLTRect.Height / 2f + 10, _viewportHeight - _btnLTRect.Height / 2f - 10)
                            );
                            Settings.LeftTriggerNormX = _ltCenter.X / _viewportWidth;
                            Settings.LeftTriggerNormY = _ltCenter.Y / _viewportHeight;
                            Settings.CustomTriggersSet = true;
                            UpdateLayout(_viewportWidth, _viewportHeight);
                        }
                        else if (_activeDragTarget == 4)
                        {
                            if (!Settings.CustomTriggersSet)
                            {
                                Settings.LeftTriggerNormX = _ltCenter.X / _viewportWidth;
                                Settings.LeftTriggerNormY = _ltCenter.Y / _viewportHeight;
                            }
                            _rtCenter = new Vector2(
                                Math.Clamp(pos.X, _btnRTRect.Width / 2f + 10, _viewportWidth - _btnRTRect.Width / 2f - 10),
                                Math.Clamp(pos.Y, _btnRTRect.Height / 2f + 10, _viewportHeight - _btnRTRect.Height / 2f - 10)
                            );
                            Settings.RightTriggerNormX = _rtCenter.X / _viewportWidth;
                            Settings.RightTriggerNormY = _rtCenter.Y / _viewportHeight;
                            Settings.CustomTriggersSet = true;
                            UpdateLayout(_viewportWidth, _viewportHeight);
                        }
                    }
                }
                else if (_dragTouchId == -1 && (touch.State == TouchLocationState.Pressed || touch.State == TouchLocationState.Moved))
                {
                    if (_btnLTRect.Contains(pt) || Vector2.Distance(pos, _ltCenter) <= 50f)
                    {
                        _activeDragTarget = 3;
                        _dragTouchId = touch.Id;
                    }
                    else if (_btnRTRect.Contains(pt) || Vector2.Distance(pos, _rtCenter) <= 50f)
                    {
                        _activeDragTarget = 4;
                        _dragTouchId = touch.Id;
                    }
                    else if (Vector2.Distance(pos, _joystickCenter) <= _joystickRadius * 1.4f)
                    {
                        _activeDragTarget = 1;
                        _dragTouchId = touch.Id;
                    }
                    else if (Vector2.Distance(pos, _buttonsCenter) <= 120f)
                    {
                        _activeDragTarget = 2;
                        _dragTouchId = touch.Id;
                    }
                }
            }
        }

        public void ForwardInputToGame()
        {
            try
            {
                EnsureReflection();

                var mouseState = CurrentSimulatedMouseState;

                // 1. MonoGame PrimaryWindow.MouseState
                try
                {
                    if (_mousePrimaryWindowField == null)
                    {
                        _mousePrimaryWindowField = typeof(Mouse).GetField("PrimaryWindow", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    }
                    var win = _mousePrimaryWindowField?.GetValue(null);
                    if (win != null)
                    {
                        if (_gameWindowMouseStateField == null)
                        {
                            _gameWindowMouseStateField = win.GetType().GetField("MouseState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                                                      ?? typeof(GameWindow).GetField("MouseState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        }
                        _gameWindowMouseStateField?.SetValue(win, mouseState);
                    }
                }
                catch { }

                // 2. StardewValley Game1.input fields & SMAPI MouseState backing field
                if (_inputInstance != null)
                {
                    _currentMouseStateField?.SetValue(_inputInstance, mouseState);
                    _simulatedMousePositionField?.SetValue(_inputInstance, SimulatedMousePosition);
                    _sMouseStateBackingField?.SetValue(_inputInstance, mouseState);
                }

                // 3. StardewValley Game1 mouse visibility & motion flags
                try
                {
                    if (IsMouseOverridden && (SimulatedMouseLeftDown || SimulatedMouseRightDown || _trackpadPrimaryTouchId != -1 || _pointClickTouchId != -1))
                    {
                        Game1.lastCursorMotionWasMouse = true;
                        Game1.mouseCursorTransparency = 1f;
                        Game1.wasMouseVisibleThisFrame = true;
                    }
                }
                catch { }

                // 4. Hardware Keyboard Emulation (reusing _activeKeysList & _setKeysArgs to avoid per-frame GC allocations)
                _activeKeysList.Clear();
                if (DPadUp) { _activeKeysList.Add(Keys.W); _activeKeysList.Add(Keys.Up); }
                if (DPadDown) { _activeKeysList.Add(Keys.S); _activeKeysList.Add(Keys.Down); }
                if (DPadLeft) { _activeKeysList.Add(Keys.A); _activeKeysList.Add(Keys.Left); }
                if (DPadRight) { _activeKeysList.Add(Keys.D); _activeKeysList.Add(Keys.Right); }

                bool isGamepad = Game1.options != null && Game1.options.gamepadControls;

                if (ButtonA)
                {
                    _activeKeysList.Add(Keys.X);
                    _activeKeysList.Add(Keys.Space);
                    if (Game1.activeClickableMenu == null && !Game1.eventUp)
                    {
                        Game1.lastCursorMotionWasMouse = false;
                    }
                }
                if (ButtonX)
                {
                    _activeKeysList.Add(Keys.C);
                    if (Game1.activeClickableMenu == null && !Game1.eventUp)
                    {
                        Game1.lastCursorMotionWasMouse = false;
                    }
                }
                if (ButtonY) _activeKeysList.Add(Keys.E);
                if (ButtonB || ButtonMenu) _activeKeysList.Add(Keys.Escape);

                if (Game1.activeClickableMenu != null && (IsQuickTransferMode || IsHalfStackMode))
                {
                    if (!_activeKeysList.Contains(Keys.LeftShift))
                    {
                        _activeKeysList.Add(Keys.LeftShift);
                    }
                }

                for (int i = 0; i < TouchOverlaySettings.HotkeySlotCount; i++)
                {
                    if (_hotkeyTouchActive[i] || _hotkeyPulseFrames[i] > 0)
                    {
                        Keys hk = Settings.HotkeyKeys[i];
                        if (hk != Keys.None && !_activeKeysList.Contains(hk))
                            _activeKeysList.Add(hk);
                    }
                }

                if (ModEntry.VerboseInputLogging && (ButtonY || ButtonB || ButtonMenu || HasActiveHotkeyInput))
                {
                    Log($"[TVP_DEBUG] ForwardInputToGame: ButtonY={ButtonY}, ButtonB={ButtonB}, ButtonMenu={ButtonMenu}, isGamepad={isGamepad}, activeKeys=[{string.Join(",", _activeKeysList)}], curMenu={Game1.activeClickableMenu?.GetType().Name ?? "none"}");
                }

                try
                {
                    if (_cachedSetKeys == null)
                    {
                        if (_setKeysMethod == null)
                        {
                            _setKeysMethod = typeof(Microsoft.Xna.Framework.Input.Keyboard).GetMethod("SetKeys", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                        }
                        if (_setKeysMethod != null)
                        {
                            try
                            {
                                _cachedSetKeys = (Action<List<Keys>>)Delegate.CreateDelegate(typeof(Action<List<Keys>>), _setKeysMethod);
                            }
                            catch { }
                        }
                    }

                    if (_cachedSetKeys != null)
                    {
                        _cachedSetKeys(_activeKeysList);
                    }
                    else if (_setKeysMethod != null)
                    {
                        _setKeysArgs[0] = _activeKeysList;
                        _setKeysMethod.Invoke(null, _setKeysArgs);
                    }
                }
                catch { }

                // 5. Game1.input GamePad & Keyboard state
                if (_inputInstance != null)
                {
                    if (_currentKeyboardStateField != null)
                    {
                        KeyboardState kbState;
                        if (_activeKeysList.Count == 0)
                        {
                            kbState = _emptyKeyboardState;
                        }
                        else
                        {
                            var arr = GetKeysArrayBuffer(_activeKeysList.Count);
                            _activeKeysList.CopyTo(arr);
                            kbState = new KeyboardState(arr);
                        }
                        _currentKeyboardStateField.SetValue(_inputInstance, kbState);
                    }
                    if (_currentGamepadStateField != null)
                    {
                        _currentGamepadStateField.SetValue(_inputInstance, EffectiveGamePadState);
                    }
                }

                // 6. Direct Farmer Movement Injection & Bed Waking
                try
                {
                    var player = Game1.player;
                    if (player != null)
                    {
                        if (player.IsSitting())
                        {
                            _sittingTicks++;
                            bool hasDirectionalInput = DPadUp || DPadDown || DPadLeft || DPadRight || (LeftStick != Vector2.Zero && LeftStick.LengthSquared() > 0.05f);
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

                        // Dismiss passive object dialogue (e.g. sign, calendar, clock, text bar) when player moves
                        if (Game1.activeClickableMenu is DialogueBox db && db.characterDialogue == null && !db.isQuestion)
                        {
                            bool hasDirectionalInput = DPadUp || DPadDown || DPadLeft || DPadRight || (LeftStick != Vector2.Zero && LeftStick.LengthSquared() > 0.05f);
                            if (hasDirectionalInput)
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

                        if (player.CanMove && Game1.activeClickableMenu == null && !Game1.eventUp && !Game1.freezeControls)
                        {
                            if (DPadUp) player.SetMovingUp(true);
                            else if (player.movementDirections.Contains(0)) player.SetMovingUp(false);

                            if (DPadRight) player.SetMovingRight(true);
                            else if (player.movementDirections.Contains(1)) player.SetMovingRight(false);

                            if (DPadDown) player.SetMovingDown(true);
                            else if (player.movementDirections.Contains(2)) player.SetMovingDown(false);

                            if (DPadLeft) player.SetMovingLeft(true);
                            else if (player.movementDirections.Contains(3)) player.SetMovingLeft(false);

                            if (!DPadUp && !DPadRight && !DPadDown && !DPadLeft && LeftStick == Vector2.Zero)
                            {
                                if (player.movementDirections.Count > 0)
                                {
                                    player.Halt();
                                }
                            }
                        }
                    }
                }
                catch { }

                // 7. Ensure Game1 cursor remains visible
                if (_lastCursorMotionWasMouseField != null && IsMouseOverridden && (SimulatedMouseLeftDown || SimulatedMouseRightDown || _trackpadPrimaryTouchId != -1 || _pointClickTouchId != -1))
                {
                    _lastCursorMotionWasMouseField.SetValue(null, true);
                }
            }
            catch { }
        }

        private static void EnsureReflection()
        {
            if (_reflectionInitialized && _inputInstance != null) return;

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var g1Type = asm.GetType("StardewValley.Game1");
                    if (g1Type != null)
                    {
                        _game1Type = g1Type;
                        var inputField = g1Type.GetField("input", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        _inputInstance = inputField?.GetValue(null);

                        _lastCursorMotionWasMouseField = g1Type.GetField("lastCursorMotionWasMouse", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        _optionsProp = g1Type.GetProperty("options", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                        if (_optionsProp != null)
                        {
                            var optType = _optionsProp.PropertyType;
                            _gamepadControlsField = optType.GetField("gamepadControls", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        }

                        if (_inputInstance != null)
                        {
                            Type? cur = _inputInstance.GetType();
                            while (cur != null && cur != typeof(object))
                            {
                                if (_currentMouseStateField == null)
                                    _currentMouseStateField = cur.GetField("_currentMouseState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                if (_currentGamepadStateField == null)
                                    _currentGamepadStateField = cur.GetField("_currentGamepadState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                                        ?? cur.GetField("<ControllerState>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                if (_currentKeyboardStateField == null)
                                    _currentKeyboardStateField = cur.GetField("_currentKeyboardState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                                        ?? cur.GetField("<KeyboardState>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                if (_simulatedMousePositionField == null)
                                    _simulatedMousePositionField = cur.GetField("_simulatedMousePosition", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                if (_sMouseStateBackingField == null)
                                    _sMouseStateBackingField = cur.GetField("<MouseState>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                cur = cur.BaseType;
                            }
                            _reflectionInitialized = true;
                            Log($"[TouchVirtualPad] Successfully hooked Stardew Valley input fields (mouseField={_currentMouseStateField != null}, simField={_simulatedMousePositionField != null}, sBackingField={_sMouseStateBackingField != null}).");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[TouchVirtualPad] Reflection hook warning: {ex.Message}");
            }
        }

        private void UpdateJoystickVector(Vector2 touchPos)
        {
            Vector2 delta = touchPos - _joystickCenter;
            float dist = delta.Length();
            if (dist > _joystickRadius)
            {
                delta.Normalize();
                _currentStickPos = _joystickCenter + delta * _joystickRadius;
                LeftStick = delta;
            }
            else
            {
                _currentStickPos = touchPos;
                LeftStick = delta / _joystickRadius;
            }
        }

        public void Draw(SpriteBatch spriteBatch)
        {
            if (_pixelTexture == null)
            {
                GenerateTextures(spriteBatch.GraphicsDevice);
            }
            if (_pixelTexture == null) return;

            if (spriteBatch.GraphicsDevice != null)
            {
                var vp = spriteBatch.GraphicsDevice.Viewport;
                if (vp.Width > 0 && vp.Height > 0 && (_viewportWidth != vp.Width || _viewportHeight != vp.Height))
                {
                    UpdateLayout(vp.Width, vp.Height);
                }
            }

            // -1. Draw QuickLoad Reloading Screen if reloading
            if (QuickSaveManager.Instance?.IsReloading == true)
            {
                DrawFilledRect(spriteBatch, new Rectangle(0, 0, _viewportWidth, _viewportHeight), Color.Black * 0.75f);

                int boxW = 460;
                int boxH = 96;
                int boxX = (_viewportWidth - boxW) / 2;
                int boxY = (_viewportHeight - boxH) / 2;
                var boxRect = new Rectangle(boxX, boxY, boxW, boxH);

                DrawFilledRect(spriteBatch, boxRect, new Color(20, 24, 34) * 0.95f);
                DrawRectBorder(spriteBatch, boxRect, 2, Color.Goldenrod);

                Rectangle titleRect = new Rectangle(boxX, boxY + 20, boxW, 26);
                OverlayFont.DrawCenteredString(spriteBatch, _pixelTexture, "RELOADING SAVE STATE...", titleRect, Color.Gold, 2);

                Rectangle subtitleRect = new Rectangle(boxX, boxY + 52, boxW, 20);
                OverlayFont.DrawCenteredString(spriteBatch, _pixelTexture, "Purging memory & restoring world...", subtitleRect, Color.LightGray, 1);
                return;
            }

            // 0. Draw Key Picker Modal
            if (IsKeyPickerOpen)
            {
                DrawKeyPickerModal(spriteBatch);
                return;
            }

            // 1. Draw Edit Layout Mode
            if (IsEditLayoutMode)
            {
                DrawEditLayoutScreen(spriteBatch);
                return;
            }

            // 2. Draw Settings Modal Dialog
            if (IsSettingsOpen)
            {
                DrawSettingsModal(spriteBatch);
                return;
            }

            // 3. Normal Gameplay Overlay
            float alpha = Settings.Opacity;

            // Top utility buttons
            DrawButton(spriteBatch, _btnToggleRect, IsVisible ? "HIDE" : "PAD", Color.DarkSlateGray * Math.Max(0.5f, alpha), Color.White, 2);
            DrawButton(spriteBatch, _btnSettingsRect, "SET", Color.DarkSlateGray * Math.Max(0.5f, alpha), Color.White, 2);

            if (Settings.ShowKeyboardBtn && !_btnKeyboardRect.IsEmpty)
            {
                DrawButton(spriteBatch, _btnKeyboardRect, "KEY", Color.DarkSlateBlue * Math.Max(0.5f, alpha), Color.White, 2);
            }

            if (Settings.ShowMenuBtn && !_btnMenuRect.IsEmpty)
            {
                DrawButton(spriteBatch, _btnMenuRect, "MENU", IsVisualButtonMenu ? Color.Orange * 0.9f : Color.DarkOrange * Math.Max(0.5f, alpha), Color.White, 2);
            }

            if (Settings.ShowHotkeyDrawerBtn && !_btnHotkeyDrawerToggleRect.IsEmpty)
            {
                Color modsBg = IsHotkeyDrawerOpen
                    ? Color.Indigo * 0.95f
                    : new Color(56, 42, 96) * Math.Max(0.6f, alpha);
                DrawButton(spriteBatch, _btnHotkeyDrawerToggleRect, "MODS", modsBg, Color.White, 2);
                if (IsHotkeyDrawerOpen)
                {
                    DrawRectBorder(spriteBatch, _btnHotkeyDrawerToggleRect, 2, Color.Gold);
                }
            }

            if (IsHotkeyDrawerOpen && Settings.ShowHotkeyDrawerBtn)
            {
                DrawHotkeyDrawer(spriteBatch, alpha);
            }

            // Draw Inventory Action Bar (when menu is active, Amendment 2)
            if (Game1.activeClickableMenu != null && Settings.EnableInventoryToolbar)
            {
                Color x1Bg = IsSingleItemMode ? new Color(218, 165, 32) : new Color(38, 48, 64) * Math.Max(0.7f, alpha);
                DrawButton(spriteBatch, _btnInventoryOneRect, "x1", x1Bg, Color.White, 2);
                if (IsSingleItemMode) DrawRectBorder(spriteBatch, _btnInventoryOneRect, 2, Color.Gold);

                Color halfBg = IsHalfStackMode ? new Color(30, 144, 255) : new Color(38, 48, 64) * Math.Max(0.7f, alpha);
                DrawButton(spriteBatch, _btnInventoryHalfRect, "1/2", halfBg, Color.White, 2);
                if (IsHalfStackMode) DrawRectBorder(spriteBatch, _btnInventoryHalfRect, 2, Color.DeepSkyBlue);

                Color xferBg = IsQuickTransferMode ? new Color(255, 140, 0) : new Color(38, 48, 64) * Math.Max(0.7f, alpha);
                DrawButton(spriteBatch, _btnInventoryXferRect, "XFER", xferBg, Color.White, 2);
                if (IsQuickTransferMode) DrawRectBorder(spriteBatch, _btnInventoryXferRect, 2, Color.Orange);
            }

            if (IsVisible)
            {
                // Draw Left Trigger (LT) and Right Trigger (RT) shoulder buttons
                Color ltBg = IsVisualButtonLT ? Color.DeepSkyBlue * 0.95f : new Color(36, 48, 72) * Math.Max(0.75f, alpha);
                Color rtBg = IsVisualButtonRT ? Color.DeepSkyBlue * 0.95f : new Color(36, 48, 72) * Math.Max(0.75f, alpha);
                DrawButton(spriteBatch, _btnLTRect, "LT", ltBg, Color.White, 2);
                DrawFilledRect(spriteBatch, new Rectangle(_btnLTRect.X, _btnLTRect.Y, _btnLTRect.Width, 3), (IsVisualButtonLT ? Color.White : Color.LightSkyBlue) * Math.Max(0.75f, alpha));
                DrawButton(spriteBatch, _btnRTRect, "RT", rtBg, Color.White, 2);
                DrawFilledRect(spriteBatch, new Rectangle(_btnRTRect.X, _btnRTRect.Y, _btnRTRect.Width, 3), (IsVisualButtonRT ? Color.White : Color.LightSkyBlue) * Math.Max(0.75f, alpha));

                // Draw joystick base & thumb
                DrawFilledRect(spriteBatch, _joystickBaseRect, Color.Black * (alpha * 0.65f));
                DrawRectBorder(spriteBatch, _joystickBaseRect, 2, Color.White * (alpha * 0.7f));

                Rectangle stickThumbRect = new Rectangle((int)(_currentStickPos.X - 28), (int)(_currentStickPos.Y - 28), 56, 56);
                DrawFilledRect(spriteBatch, stickThumbRect, Color.White * Math.Max(0.85f, alpha));
                DrawRectBorder(spriteBatch, stickThumbRect, 2, Color.Black * 0.9f);

                // Draw Action buttons with text labels (A, X, Y, B)
                DrawButton(spriteBatch, _btnARect, "A", ButtonA ? Color.Lime * 0.95f : Color.DarkGreen * Math.Max(0.75f, alpha), Color.White, 3);
                DrawButton(spriteBatch, _btnXRect, "X", ButtonX ? Color.CornflowerBlue * 0.95f : Color.DarkBlue * Math.Max(0.75f, alpha), Color.White, 3);
                DrawButton(spriteBatch, _btnYRect, "Y", IsVisualButtonY ? Color.Yellow * 0.95f : Color.DarkGoldenrod * Math.Max(0.75f, alpha), Color.White, 3);
                DrawButton(spriteBatch, _btnBRect, "B", IsVisualButtonB ? Color.Red * 0.95f : Color.DarkRed * Math.Max(0.75f, alpha), Color.White, 3);
            }

            // Visual feedback for trackpad clicks and point-and-click taps (Native Stardew Valley cursor draws the pointer)
            float uiScale = 1.0f;
            try
            {
                if (Game1.options != null && Game1.options.uiScale > 0.01f)
                {
                    uiScale = Game1.options.uiScale;
                }
            }
            catch { }

            int drawMouseX = (int)Math.Round(SimulatedMousePosition.X / uiScale);
            int drawMouseY = (int)Math.Round(SimulatedMousePosition.Y / uiScale);

            if (Settings.MouseControlMode == MouseMode.Trackpad)
            {
                if (IsLeftClickActive)
                {
                    Rectangle clickDot = new Rectangle(drawMouseX + 2, drawMouseY + 2, 6, 6);
                    DrawFilledRect(spriteBatch, clickDot, Color.Lime * 0.9f);
                }
                else if (IsRightClickActive)
                {
                    Rectangle clickDot = new Rectangle(drawMouseX + 2, drawMouseY + 2, 6, 6);
                    DrawFilledRect(spriteBatch, clickDot, Color.Cyan * 0.9f);
                }
            }
            else if (Settings.MouseControlMode == MouseMode.PointAndClick)
            {
                if (SimulatedMouseLeftDown)
                {
                    DrawFilledRect(spriteBatch, new Rectangle(drawMouseX - 5, drawMouseY - 5, 10, 10), Color.Lime * 0.4f);
                    DrawRectBorder(spriteBatch, new Rectangle(drawMouseX - 8, drawMouseY - 8, 16, 16), 2, Color.White * 0.6f);
                }
            }
        }

        private void DrawHotkeyDrawer(SpriteBatch sb, float alpha)
        {
            if (_pixelTexture == null) return;

            float panelAlpha = Math.Max(0.88f, alpha);
            DrawFilledRect(sb, _hotkeyDrawerRect, new Color(18, 22, 34) * panelAlpha);
            DrawRectBorder(sb, _hotkeyDrawerRect, 2, (_isHotkeyRebindMode ? Color.Gold : Color.MediumPurple) * panelAlpha);

            for (int i = 0; i < TouchOverlaySettings.HotkeySlotCount; i++)
            {
                Rectangle r = _hotkeySlotRects[i];
                bool isActive = _hotkeyTouchActive[i] || _hotkeyPulseFrames[i] > 0 || _hotkeyVisualFrames[i] > 0;
                Color slotBg = isActive
                    ? Color.LimeGreen * 0.92f
                    : (_isHotkeyRebindMode ? new Color(72, 54, 18) * 0.92f : new Color(36, 44, 68) * 0.92f);

                DrawFilledRect(sb, r, slotBg);
                DrawRectBorder(sb, r, 2, _isHotkeyRebindMode ? Color.Gold : (isActive ? Color.Lime : Color.White * 0.3f));

                string label = Settings.HotkeyLabels[i];
                string keyBadge = $"[{FormatKeyShort(Settings.HotkeyKeys[i])}]";

                Rectangle topHalf = new Rectangle(r.X, r.Y + 3, r.Width, 20);
                Rectangle botHalf = new Rectangle(r.X, r.Y + 22, r.Width, 18);
                OverlayFont.DrawCenteredString(sb, _pixelTexture, label, topHalf, Color.White, 2);
                OverlayFont.DrawCenteredString(sb, _pixelTexture, keyBadge, botHalf, isActive ? Color.White : Color.Gold, 2);
            }

            // Quick Save / Quick Load State buttons
            bool hasSavedState = QuickSaveManager.Instance?.HasSavedState() ?? false;
            string loadLabel = hasSavedState ? "LOAD STATE" : "NO SAVE STATE";

            DrawButton(
                sb,
                _btnQuickSaveRect,
                "SAVE STATE",
                new Color(24, 110, 54),
                Color.White,
                2
            );

            DrawButton(
                sb,
                _btnQuickLoadRect,
                loadLabel,
                hasSavedState ? new Color(20, 85, 120) : new Color(45, 55, 65),
                hasSavedState ? Color.White : Color.LightGray * 0.7f,
                2
            );

            // Drawer footer buttons
            DrawButton(
                sb,
                _btnHotkeyBindModeRect,
                _isHotkeyRebindMode ? "TAP A SLOT..." : "REBIND SLOT",
                _isHotkeyRebindMode ? Color.DarkGoldenrod : Color.Indigo,
                Color.White,
                2
            );

            DrawButton(
                sb,
                _btnHotkeyAutoCloseRect,
                Settings.AutoCloseHotkeyDrawer ? "AUTO HIDE: ON" : "STAY OPEN",
                Settings.AutoCloseHotkeyDrawer ? Color.DarkSlateBlue : Color.DarkSlateGray,
                Color.White,
                2
            );

            DrawButton(
                sb,
                _btnHotkeyCloseDrawerRect,
                "CLOSE",
                Color.DarkRed * 0.85f,
                Color.White,
                2
            );
        }

        private void DrawKeyPickerModal(SpriteBatch sb)
        {
            if (_pixelTexture == null) return;

            int slot = Math.Clamp(_editingHotkeySlotIndex, 0, TouchOverlaySettings.HotkeySlotCount - 1);
            Keys currentKey = Settings.HotkeyKeys[slot];
            string currentLabel = Settings.HotkeyLabels[slot];

            DrawFilledRect(sb, new Rectangle(0, 0, _viewportWidth, _viewportHeight), Color.Black * 0.65f);
            DrawFilledRect(sb, _keyPickerModalRect, new Color(20, 24, 36) * 0.97f);
            DrawRectBorder(sb, _keyPickerModalRect, 3, Color.Gold);

            string title = $"BIND SLOT #{slot + 1}: {currentLabel} [{FormatKeyShort(currentKey)}]";
            OverlayFont.DrawString(sb, _pixelTexture, title, new Vector2(_keyPickerModalRect.Left + 20, _keyPickerModalRect.Top + 16), Color.Gold, 2);

            DrawFilledRect(sb, _keyPickerCloseRect, Color.DarkRed * 0.85f);
            DrawRectBorder(sb, _keyPickerCloseRect, 2, Color.Red);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, "X", _keyPickerCloseRect, Color.White, 2);

            for (int r = 0; r < KeyPickerRows.Length; r++)
            {
                var rowKeys = KeyPickerRows[r];
                for (int c = 0; c < rowKeys.Length; c++)
                {
                    Keys k = rowKeys[c];
                    Rectangle cellRect = GetKeyPickerCellRect(r, c, rowKeys.Length);
                    bool isSelected = (k == currentKey);
                    Color bg = isSelected ? Color.DarkGreen : new Color(40, 48, 72);
                    DrawFilledRect(sb, cellRect, bg);
                    DrawRectBorder(sb, cellRect, 2, isSelected ? Color.Lime : Color.White * 0.25f);
                    OverlayFont.DrawCenteredString(sb, _pixelTexture, FormatKeyShort(k), cellRect, isSelected ? Color.Yellow : Color.White, 2);
                }
            }

            DrawButton(sb, _btnKeyPickerRenameRect, "TYPE LABEL", Color.DarkSlateBlue, Color.White, 2);
            DrawButton(sb, _btnKeyPickerPresetRect, $"NAME: {currentLabel}", Color.Indigo, Color.Gold, 2);
            DrawButton(sb, _btnKeyPickerResetSlotRect, "DEFAULT", Color.DarkSlateGray, Color.White, 2);
            DrawButton(sb, _btnKeyPickerCancelRect, "CANCEL", Color.DarkRed * 0.85f, Color.White, 2);
        }

        private void DrawEditLayoutScreen(SpriteBatch sb)
        {
            if (_pixelTexture == null) return;

            DrawFilledRect(sb, new Rectangle(0, 0, _viewportWidth, _viewportHeight), Color.Black * 0.5f);

            Rectangle bannerRect = new Rectangle(_viewportWidth / 2 - 240, 20, 340, 44);
            DrawFilledRect(sb, bannerRect, Color.Black * 0.85f);
            DrawRectBorder(sb, bannerRect, 2, Color.Gold);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, "DRAG CONTROLS TO MOVE", bannerRect, Color.Gold, 2);

            DrawFilledRect(sb, _btnDoneEditRect, Color.DarkGreen * 0.9f);
            DrawRectBorder(sb, _btnDoneEditRect, 2, Color.Lime);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, "DONE", _btnDoneEditRect, Color.White, 2);

            // Highlight LT and RT draggable zones
            DrawFilledRect(sb, _btnLTRect, Color.SlateBlue * 0.4f);
            DrawRectBorder(sb, _btnLTRect, 2, Color.LightSkyBlue);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, "LT", _btnLTRect, Color.White, 2);

            DrawFilledRect(sb, _btnRTRect, Color.SlateBlue * 0.4f);
            DrawRectBorder(sb, _btnRTRect, 2, Color.LightSkyBlue);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, "RT", _btnRTRect, Color.White, 2);

            // Highlight Joystick draggable zone
            DrawFilledRect(sb, _joystickBaseRect, Color.SlateBlue * 0.4f);
            DrawRectBorder(sb, _joystickBaseRect, 2, Color.MediumSlateBlue);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, "STICK", _joystickBaseRect, Color.White, 2);

            // Highlight Buttons draggable zone
            Rectangle buttonsBounds = new Rectangle(
                (int)(_buttonsCenter.X - 110f),
                (int)(_buttonsCenter.Y - 110f),
                220,
                220
            );
            DrawFilledRect(sb, buttonsBounds, Color.SlateBlue * 0.4f);
            DrawRectBorder(sb, buttonsBounds, 2, Color.MediumSlateBlue);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, "BUTTONS", buttonsBounds, Color.White, 2);
        }

        private void DrawSettingsModal(SpriteBatch sb)
        {
            if (_pixelTexture == null) return;

            DrawFilledRect(sb, new Rectangle(0, 0, _viewportWidth, _viewportHeight), Color.Black * 0.5f);

            // Modal box
            DrawFilledRect(sb, _settingsModalRect, new Color(20, 24, 34) * 0.95f);
            DrawRectBorder(sb, _settingsModalRect, 3, Color.Goldenrod);

            // Header
            Rectangle headerRect = new Rectangle(_settingsModalRect.Left + 20, _settingsModalRect.Top + 14, 300, 24);
            OverlayFont.DrawString(sb, _pixelTexture, "TOUCH CONTROLS SETTINGS", new Vector2(headerRect.X, headerRect.Y), Color.Gold, 2);

            // Close (X) button
            DrawFilledRect(sb, _settingsCloseRect, Color.DarkRed * 0.8f);
            DrawRectBorder(sb, _settingsCloseRect, 2, Color.Red);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, "X", _settingsCloseRect, Color.White, 2);

            int startX = _settingsModalRect.Left + 28;
            int startY = _settingsModalRect.Top + 48;
            int rowSpacing = 37;

            // Row 0: Opacity
            OverlayFont.DrawString(sb, _pixelTexture, "OPACITY", new Vector2(startX, startY + 6), Color.White, 2);
            DrawButton(sb, _btnOpacityMinus, "-", Color.DarkSlateGray, Color.White, 2);
            string opStr = $"{(int)(Settings.Opacity * 100)}%";
            Rectangle opTextRect = new Rectangle(_btnOpacityMinus.Right, startY, _btnOpacityPlus.Left - _btnOpacityMinus.Right, 28);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, opStr, opTextRect, Color.Yellow, 2);
            DrawButton(sb, _btnOpacityPlus, "+", Color.DarkSlateGray, Color.White, 2);

            // Row 1: Scale
            OverlayFont.DrawString(sb, _pixelTexture, "PAD SCALE", new Vector2(startX, startY + rowSpacing + 6), Color.White, 2);
            DrawButton(sb, _btnScaleMinus, "-", Color.DarkSlateGray, Color.White, 2);
            string scStr = $"{Settings.Scale:0.00}X";
            Rectangle scTextRect = new Rectangle(_btnScaleMinus.Right, startY + rowSpacing, _btnScalePlus.Left - _btnScaleMinus.Right, 28);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, scStr, scTextRect, Color.Yellow, 2);
            DrawButton(sb, _btnScalePlus, "+", Color.DarkSlateGray, Color.White, 2);

            // Row 2: Handedness
            OverlayFont.DrawString(sb, _pixelTexture, "HANDEDNESS", new Vector2(startX, startY + rowSpacing * 2 + 6), Color.White, 2);
            DrawButton(sb, _btnHandedness, Settings.LeftHanded ? "LEFT HANDED" : "RIGHT HANDED", Color.DarkSlateBlue, Color.White, 2);

            // Row 3: Deadzone
            OverlayFont.DrawString(sb, _pixelTexture, "STICK DEADZONE", new Vector2(startX, startY + rowSpacing * 3 + 6), Color.White, 2);
            string dzStr = Settings.Deadzone <= 0.18f ? "LOW (0.15)" : (Settings.Deadzone <= 0.28f ? "NORMAL (0.25)" : "HIGH (0.35)");
            DrawButton(sb, _btnDeadzone, dzStr, Color.DarkSlateBlue, Color.White, 2);

            // Row 4: Extra Buttons
            OverlayFont.DrawString(sb, _pixelTexture, "TOP BAR BUTTONS", new Vector2(startX, startY + rowSpacing * 4 + 6), Color.White, 2);
            DrawButton(sb, _btnToggleKeyboard, Settings.ShowKeyboardBtn ? "KEY: ON" : "KEY: OFF", Settings.ShowKeyboardBtn ? Color.DarkGreen : Color.DarkSlateGray, Color.White, 2);
            DrawButton(sb, _btnToggleMenu, Settings.ShowMenuBtn ? "MENU: ON" : "MENU: OFF", Settings.ShowMenuBtn ? Color.DarkGreen : Color.DarkSlateGray, Color.White, 2);
            DrawButton(sb, _btnToggleHotkeyDrawer, Settings.ShowHotkeyDrawerBtn ? "MODS: ON" : "MODS: OFF", Settings.ShowHotkeyDrawerBtn ? Color.DarkGreen : Color.DarkSlateGray, Color.White, 2);

            // Row 5: Mouse Mode
            OverlayFont.DrawString(sb, _pixelTexture, "MOUSE MODE", new Vector2(startX, startY + rowSpacing * 5 + 6), Color.White, 2);
            string modeStr = Settings.MouseControlMode switch
            {
                MouseMode.PointAndClick => "POINT & CLICK",
                MouseMode.Trackpad => "TRACKPAD",
                _ => "DISABLED"
            };
            Color modeCol = Settings.MouseControlMode switch
            {
                MouseMode.PointAndClick => Color.DarkGreen,
                MouseMode.Trackpad => Color.Indigo,
                _ => Color.DarkSlateGray
            };
            DrawButton(sb, _btnToggleMouseMode, modeStr, modeCol, Color.White, 2);

            // Row 6: Trackpad Sensitivity
            OverlayFont.DrawString(sb, _pixelTexture, "TRACKPAD SPEED", new Vector2(startX, startY + rowSpacing * 6 + 6), Settings.MouseControlMode == MouseMode.Trackpad ? Color.White : Color.Gray, 2);
            DrawButton(sb, _btnSensitivityMinus, "-", Settings.MouseControlMode == MouseMode.Trackpad ? Color.DarkSlateGray : Color.Black * 0.4f, Color.White, 2);
            string sensStr = $"{Settings.TrackpadSensitivity:0.0}X";
            Rectangle sensTextRect = new Rectangle(_btnSensitivityMinus.Right, startY + rowSpacing * 6, _btnSensitivityPlus.Left - _btnSensitivityMinus.Right, 28);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, sensStr, sensTextRect, Settings.MouseControlMode == MouseMode.Trackpad ? Color.Yellow : Color.Gray, 2);
            DrawButton(sb, _btnSensitivityPlus, "+", Settings.MouseControlMode == MouseMode.Trackpad ? Color.DarkSlateGray : Color.Black * 0.4f, Color.White, 2);

            // Row 7: Reposition Controls
            DrawButton(sb, _btnEditLayout, "REPOSITION CONTROLS (DRAG & DROP)", Color.Indigo, Color.White, 2);
            DrawRectBorder(sb, _btnEditLayout, 2, Color.MediumPurple);

            // Row 8: Reset & Close buttons
            DrawButton(sb, _btnResetDefaults, "RESET TO DEFAULTS", Color.DarkRed * 0.8f, Color.White, 2);
            DrawButton(sb, _btnSaveClose, "SAVE & CLOSE", Color.DarkGreen, Color.White, 2);
            DrawRectBorder(sb, _btnSaveClose, 2, Color.Lime);
        }

        private void DrawButton(SpriteBatch sb, Rectangle rect, string text, Color bgColor, Color textColor, int fontPixelSize)
        {
            if (_pixelTexture == null || rect.IsEmpty) return;
            DrawFilledRect(sb, rect, bgColor);
            DrawRectBorder(sb, rect, 2, Color.White * 0.25f);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, text, rect, textColor, fontPixelSize);
        }

        private void DrawFilledRect(SpriteBatch sb, Rectangle rect, Color color)
        {
            if (_pixelTexture != null && !rect.IsEmpty)
            {
                sb.Draw(_pixelTexture, rect, color);
            }
        }

        private void DrawRectBorder(SpriteBatch sb, Rectangle rect, int thickness, Color color)
        {
            if (_pixelTexture == null || rect.IsEmpty || thickness <= 0) return;
            sb.Draw(_pixelTexture, new Rectangle(rect.X, rect.Y, rect.Width, thickness), color);
            sb.Draw(_pixelTexture, new Rectangle(rect.X, rect.Bottom - thickness, rect.Width, thickness), color);
            sb.Draw(_pixelTexture, new Rectangle(rect.X, rect.Y, thickness, rect.Height), color);
            sb.Draw(_pixelTexture, new Rectangle(rect.Right - thickness, rect.Y, thickness, rect.Height), color);
        }

        private void GenerateTextures(GraphicsDevice gd)
        {
            if (_pixelTexture == null && gd != null)
            {
                _pixelTexture = new Texture2D(gd, 1, 1);
                _pixelTexture.SetData(new[] { Color.White });
            }
        }
    }
}
