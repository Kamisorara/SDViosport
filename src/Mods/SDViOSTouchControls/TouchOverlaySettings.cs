using System;
using System.Globalization;
using System.IO;

namespace SDViOSTouchControls
{
    public enum MouseMode
    {
        PointAndClick = 0,
        Trackpad = 1,
        Disabled = 2
    }

    public class TouchOverlaySettings
    {
        public static TouchOverlaySettings Instance { get; } = new TouchOverlaySettings();

        public float Opacity { get; set; } = 0.6f;
        public float Scale { get; set; } = 1.0f;
        public bool LeftHanded { get; set; } = false;
        public float Deadzone { get; set; } = 0.25f;
        public bool ShowKeyboardBtn { get; set; } = true;
        public bool ShowMenuBtn { get; set; } = true;
        public bool ShowHotkeyDrawerBtn { get; set; } = true;
        public bool AutoCloseHotkeyDrawer { get; set; } = false;
        public bool SimulateMouseOnTap { get; set; } = true;
        public bool EnableDeferredMapLoading { get; set; } = true;
        public bool EnableInventoryToolbar { get; set; } = true;
        public bool EnableHoldToDispense { get; set; } = true;
        public bool EnableLockedFramePacing { get; set; } = true;
        public bool EnableMenuFrameThrottling { get; set; } = true;
        public bool EnableWarpTilesheetPrewarm { get; set; } = true;
        public bool EnableBufferedLogging { get; set; } = true;
        public bool EnableDisabledModsRelocation { get; set; } = true;

        public const int HotkeySlotCount = 8;

        private static readonly string[] DefaultHotkeyLabels = new string[HotkeySlotCount]
        {
            "CHEAT",
            "ITEMS",
            "MOD N",
            "DEBUG",
            "INFO",
            "AUTO",
            "MAP",
            "ESC"
        };

        private static readonly Microsoft.Xna.Framework.Input.Keys[] DefaultHotkeyKeys = new Microsoft.Xna.Framework.Input.Keys[HotkeySlotCount]
        {
            Microsoft.Xna.Framework.Input.Keys.P,
            Microsoft.Xna.Framework.Input.Keys.I,
            Microsoft.Xna.Framework.Input.Keys.N,
            Microsoft.Xna.Framework.Input.Keys.F2,
            Microsoft.Xna.Framework.Input.Keys.F1,
            Microsoft.Xna.Framework.Input.Keys.U,
            Microsoft.Xna.Framework.Input.Keys.M,
            Microsoft.Xna.Framework.Input.Keys.Escape
        };

        public string[] HotkeyLabels { get; } = new string[HotkeySlotCount]
        {
            "CHEAT", "ITEMS", "MOD N", "DEBUG", "INFO", "AUTO", "MAP", "ESC"
        };

        public Microsoft.Xna.Framework.Input.Keys[] HotkeyKeys { get; } = new Microsoft.Xna.Framework.Input.Keys[HotkeySlotCount]
        {
            Microsoft.Xna.Framework.Input.Keys.P,
            Microsoft.Xna.Framework.Input.Keys.I,
            Microsoft.Xna.Framework.Input.Keys.N,
            Microsoft.Xna.Framework.Input.Keys.F2,
            Microsoft.Xna.Framework.Input.Keys.F1,
            Microsoft.Xna.Framework.Input.Keys.U,
            Microsoft.Xna.Framework.Input.Keys.M,
            Microsoft.Xna.Framework.Input.Keys.Escape
        };

        public static void ResetHotkeySlotToDefault(int index, string[] labels, Microsoft.Xna.Framework.Input.Keys[] keys)
        {
            if (index < 0 || index >= HotkeySlotCount) return;
            labels[index] = DefaultHotkeyLabels[index];
            keys[index] = DefaultHotkeyKeys[index];
        }

        public static string SanitizeHotkeyLabel(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "KEY";
            var chars = new System.Collections.Generic.List<char>(7);
            foreach (char c in raw.Trim().ToUpperInvariant())
            {
                if (c == '"' || c == ':' || c == ',' || c == '\r' || c == '\n' || c == '\\')
                    continue;
                chars.Add(c);
                if (chars.Count >= 7) break;
            }
            return chars.Count > 0 ? new string(chars.ToArray()) : "KEY";
        }

        // Mobile UI Scaling
        public float TargetUIScale { get; set; } = 1.35f;
        public float TargetZoomLevel { get; set; } = 1.25f;

        // Mouse modes
        public MouseMode MouseControlMode { get; set; } = MouseMode.PointAndClick;
        public float TrackpadSensitivity { get; set; } = 1.2f;

        public bool CustomPositionsSet { get; set; } = false;
        public float JoystickNormX { get; set; } = 0.15f;
        public float JoystickNormY { get; set; } = 0.82f;
        public float ButtonsNormX { get; set; } = 0.85f;
        public float ButtonsNormY { get; set; } = 0.82f;

        public bool CustomTriggersSet { get; set; } = false;
        public float LeftTriggerNormX { get; set; } = 0.085f;
        public float LeftTriggerNormY { get; set; } = 0.68f;
        public float RightTriggerNormX { get; set; } = 0.915f;
        public float RightTriggerNormY { get; set; } = 0.67f;

        public static Action<string>? LogAction { get; set; }

        private static void Log(string message)
        {
            LogAction?.Invoke(message);
        }

        public static string GetConfigPath()
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(docs, "touch_overlay_config.json");
        }

        public void Load()
        {
            try
            {
                string path = GetConfigPath();
                if (!File.Exists(path))
                {
                    Log("[TouchOverlaySettings] No config file found; using default settings.");
                    return;
                }

                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return;

                foreach (var rawLine in json.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var line = rawLine.Trim().TrimEnd(',');
                    var parts = line.Split(':');
                    if (parts.Length != 2) continue;

                    string key = parts[0].Trim().Trim('"');
                    string val = parts[1].Trim().Trim('"');

                    if (key.Equals("Opacity", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float op))
                        Opacity = Math.Clamp(op, 0.15f, 1.0f);
                    else if (key.Equals("Scale", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float sc))
                        Scale = Math.Clamp(sc, 0.5f, 2.0f);
                    else if (key.Equals("LeftHanded", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool lh))
                        LeftHanded = lh;
                    else if (key.Equals("Deadzone", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float dz))
                        Deadzone = Math.Clamp(dz, 0.05f, 0.6f);
                    else if (key.Equals("ShowKeyboardBtn", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool sk))
                        ShowKeyboardBtn = sk;
                    else if (key.Equals("ShowMenuBtn", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool sm))
                        ShowMenuBtn = sm;
                    else if (key.Equals("ShowHotkeyDrawerBtn", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool shd))
                        ShowHotkeyDrawerBtn = shd;
                    else if (key.Equals("AutoCloseHotkeyDrawer", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool achd))
                        AutoCloseHotkeyDrawer = achd;
                    else if (key.Equals("SimulateMouseOnTap", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool smt))
                        SimulateMouseOnTap = smt;
                    else if (key.Equals("EnableDeferredMapLoading", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool edml))
                        EnableDeferredMapLoading = edml;
                    else if (key.Equals("EnableInventoryToolbar", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool eit))
                        EnableInventoryToolbar = eit;
                    else if (key.Equals("EnableHoldToDispense", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool ehtd))
                        EnableHoldToDispense = ehtd;
                    else if (key.Equals("EnableLockedFramePacing", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool elfp))
                        EnableLockedFramePacing = elfp;
                    else if (key.Equals("EnableMenuFrameThrottling", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool emft))
                        EnableMenuFrameThrottling = emft;
                    else if (key.Equals("EnableWarpTilesheetPrewarm", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool ewtp))
                        EnableWarpTilesheetPrewarm = ewtp;
                    else if (key.Equals("EnableBufferedLogging", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool ebl))
                        EnableBufferedLogging = ebl;
                    else if (key.Equals("EnableDisabledModsRelocation", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool edmr))
                        EnableDisabledModsRelocation = edmr;
                    else if (key.Equals("TargetUIScale", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float tuis))
                        TargetUIScale = Math.Clamp(tuis, 0.75f, 2.0f);
                    else if (key.Equals("TargetZoomLevel", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float tzl))
                        TargetZoomLevel = Math.Clamp(tzl, 0.75f, 2.0f);
                    else if (key.Equals("MouseControlMode", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Enum.TryParse<MouseMode>(val, true, out var mm)) MouseControlMode = mm;
                        else if (int.TryParse(val, out int mmi)) MouseControlMode = (MouseMode)Math.Clamp(mmi, 0, 2);
                    }
                    else if (key.Equals("TrackpadSensitivity", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float ts))
                        TrackpadSensitivity = Math.Clamp(ts, 0.4f, 3.0f);
                    else if (key.Equals("CustomPositionsSet", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool cps))
                        CustomPositionsSet = cps;
                    else if (key.Equals("JoystickNormX", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float jx))
                        JoystickNormX = Math.Clamp(jx, 0.05f, 0.95f);
                    else if (key.Equals("JoystickNormY", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float jy))
                        JoystickNormY = Math.Clamp(jy, 0.05f, 0.95f);
                    else if (key.Equals("ButtonsNormX", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float bx))
                        ButtonsNormX = Math.Clamp(bx, 0.05f, 0.95f);
                    else if (key.Equals("ButtonsNormY", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float by))
                        ButtonsNormY = Math.Clamp(by, 0.05f, 0.95f);
                    else if (key.Equals("CustomTriggersSet", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool cts))
                        CustomTriggersSet = cts;
                    else if (key.Equals("LeftTriggerNormX", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float ltx))
                        LeftTriggerNormX = Math.Clamp(ltx, 0.04f, 0.96f);
                    else if (key.Equals("LeftTriggerNormY", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float lty))
                        LeftTriggerNormY = Math.Clamp(lty, 0.05f, 0.95f);
                    else if (key.Equals("RightTriggerNormX", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float rtx))
                        RightTriggerNormX = Math.Clamp(rtx, 0.04f, 0.96f);
                    else if (key.Equals("RightTriggerNormY", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float rty))
                        RightTriggerNormY = Math.Clamp(rty, 0.05f, 0.95f);
                    else if (key.StartsWith("Hotkey", StringComparison.OrdinalIgnoreCase) && key.Length >= 10)
                    {
                        if (int.TryParse(key.Substring(6, 1), out int slotIdx) && slotIdx >= 0 && slotIdx < HotkeySlotCount)
                        {
                            string suffix = key.Substring(7);
                            if (suffix.Equals("Label", StringComparison.OrdinalIgnoreCase))
                            {
                                HotkeyLabels[slotIdx] = SanitizeHotkeyLabel(val);
                            }
                            else if (suffix.Equals("Key", StringComparison.OrdinalIgnoreCase) &&
                                     Enum.TryParse<Microsoft.Xna.Framework.Input.Keys>(val, true, out var parsedKey))
                            {
                                HotkeyKeys[slotIdx] = parsedKey;
                            }
                        }
                    }
                }

                Log($"[TouchOverlaySettings] Loaded settings: Opacity={Opacity:F2}, Scale={Scale:F2}, UIScale={TargetUIScale:F2}, Zoom={TargetZoomLevel:F2}, LeftHanded={LeftHanded}, Deadzone={Deadzone:F2}, MouseMode={MouseControlMode}, Sensitivity={TrackpadSensitivity:F1}");
            }
            catch (Exception ex)
            {
                Log($"[TouchOverlaySettings] Error loading config: {ex.Message}");
            }
        }

        public void Save()
        {
            try
            {
                string path = GetConfigPath();
                var sb = new System.Text.StringBuilder(1024);
                sb.Append("{\n");
                sb.Append($"  \"Opacity\": {Opacity.ToString("0.00", CultureInfo.InvariantCulture)},\n");
                sb.Append($"  \"Scale\": {Scale.ToString("0.00", CultureInfo.InvariantCulture)},\n");
                sb.Append($"  \"LeftHanded\": {(LeftHanded ? "true" : "false")},\n");
                sb.Append($"  \"Deadzone\": {Deadzone.ToString("0.00", CultureInfo.InvariantCulture)},\n");
                sb.Append($"  \"ShowKeyboardBtn\": {(ShowKeyboardBtn ? "true" : "false")},\n");
                sb.Append($"  \"ShowMenuBtn\": {(ShowMenuBtn ? "true" : "false")},\n");
                sb.Append($"  \"ShowHotkeyDrawerBtn\": {(ShowHotkeyDrawerBtn ? "true" : "false")},\n");
                sb.Append($"  \"AutoCloseHotkeyDrawer\": {(AutoCloseHotkeyDrawer ? "true" : "false")},\n");
                sb.Append($"  \"SimulateMouseOnTap\": {(SimulateMouseOnTap ? "true" : "false")},\n");
                sb.Append($"  \"EnableDeferredMapLoading\": {(EnableDeferredMapLoading ? "true" : "false")},\n");
                sb.Append($"  \"EnableInventoryToolbar\": {(EnableInventoryToolbar ? "true" : "false")},\n");
                sb.Append($"  \"EnableHoldToDispense\": {(EnableHoldToDispense ? "true" : "false")},\n");
                sb.Append($"  \"EnableLockedFramePacing\": {(EnableLockedFramePacing ? "true" : "false")},\n");
                sb.Append($"  \"EnableMenuFrameThrottling\": {(EnableMenuFrameThrottling ? "true" : "false")},\n");
                sb.Append($"  \"EnableWarpTilesheetPrewarm\": {(EnableWarpTilesheetPrewarm ? "true" : "false")},\n");
                sb.Append($"  \"EnableBufferedLogging\": {(EnableBufferedLogging ? "true" : "false")},\n");
                sb.Append($"  \"EnableDisabledModsRelocation\": {(EnableDisabledModsRelocation ? "true" : "false")},\n");
                sb.Append($"  \"TargetUIScale\": {TargetUIScale.ToString("0.00", CultureInfo.InvariantCulture)},\n");
                sb.Append($"  \"TargetZoomLevel\": {TargetZoomLevel.ToString("0.00", CultureInfo.InvariantCulture)},\n");
                sb.Append($"  \"MouseControlMode\": \"{MouseControlMode}\",\n");
                sb.Append($"  \"TrackpadSensitivity\": {TrackpadSensitivity.ToString("0.00", CultureInfo.InvariantCulture)},\n");
                sb.Append($"  \"CustomPositionsSet\": {(CustomPositionsSet ? "true" : "false")},\n");
                sb.Append($"  \"JoystickNormX\": {JoystickNormX.ToString("0.000", CultureInfo.InvariantCulture)},\n");
                sb.Append($"  \"JoystickNormY\": {JoystickNormY.ToString("0.000", CultureInfo.InvariantCulture)},\n");
                sb.Append($"  \"ButtonsNormX\": {ButtonsNormX.ToString("0.000", CultureInfo.InvariantCulture)},\n");
                sb.Append($"  \"ButtonsNormY\": {ButtonsNormY.ToString("0.000", CultureInfo.InvariantCulture)},\n");
                sb.Append($"  \"CustomTriggersSet\": {(CustomTriggersSet ? "true" : "false")},\n");
                sb.Append($"  \"LeftTriggerNormX\": {LeftTriggerNormX.ToString("0.000", CultureInfo.InvariantCulture)},\n");
                sb.Append($"  \"LeftTriggerNormY\": {LeftTriggerNormY.ToString("0.000", CultureInfo.InvariantCulture)},\n");
                sb.Append($"  \"RightTriggerNormX\": {RightTriggerNormX.ToString("0.000", CultureInfo.InvariantCulture)},\n");
                sb.Append($"  \"RightTriggerNormY\": {RightTriggerNormY.ToString("0.000", CultureInfo.InvariantCulture)},\n");
                for (int i = 0; i < HotkeySlotCount; i++)
                {
                    string safeLabel = SanitizeHotkeyLabel(HotkeyLabels[i]);
                    string comma = (i == HotkeySlotCount - 1) ? "" : ",";
                    sb.Append($"  \"Hotkey{i}Label\": \"{safeLabel}\",\n");
                    sb.Append($"  \"Hotkey{i}Key\": \"{HotkeyKeys[i]}\"{comma}\n");
                }
                sb.Append("}");

                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(path, sb.ToString());
                Log("[TouchOverlaySettings] Successfully saved settings.");
            }
            catch (Exception ex)
            {
                Log($"[TouchOverlaySettings] Error saving config: {ex.Message}");
            }
        }

        public void ResetToDefaults()
        {
            Opacity = 0.6f;
            Scale = 1.0f;
            LeftHanded = false;
            Deadzone = 0.25f;
            ShowKeyboardBtn = true;
            ShowMenuBtn = true;
            ShowHotkeyDrawerBtn = true;
            AutoCloseHotkeyDrawer = false;
            SimulateMouseOnTap = true;
            EnableDeferredMapLoading = true;
            TargetUIScale = 1.35f;
            TargetZoomLevel = 1.25f;
            MouseControlMode = MouseMode.PointAndClick;
            TrackpadSensitivity = 1.2f;
            CustomPositionsSet = false;
            JoystickNormX = 0.15f;
            JoystickNormY = 0.82f;
            ButtonsNormX = 0.85f;
            ButtonsNormY = 0.82f;
            CustomTriggersSet = false;
            LeftTriggerNormX = 0.085f;
            LeftTriggerNormY = 0.68f;
            RightTriggerNormX = 0.915f;
            RightTriggerNormY = 0.67f;
            for (int i = 0; i < HotkeySlotCount; i++)
            {
                HotkeyLabels[i] = DefaultHotkeyLabels[i];
                HotkeyKeys[i] = DefaultHotkeyKeys[i];
            }
            Save();
        }
    }
}
