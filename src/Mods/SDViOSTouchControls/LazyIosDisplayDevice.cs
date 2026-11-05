using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using xTile;
using xTile.Dimensions;
using xTile.Display;
using xTile.Layers;
using xTile.Tiles;

namespace SDViOSTouchControls
{
    /// <summary>
    /// Drop-in replacement for SMAPI's SDisplayDevice / XnaDisplayDevice on iOS.
    /// Explicitly implements IDisplayDevice.LoadTileSheet as a no-op during bulk Map.LoadTileSheets() calls
    /// so that background locations and unused tilesheets do not eagerly allocate uncompressed Texture2D
    /// assets in RAM/VRAM. Instead, tilesheet textures are loaded strictly on-demand in DrawTile() when
    /// a tile is actually rendered on screen, with automatic pruning when warping between locations.
    /// </summary>
    public sealed class LazyIosDisplayDevice : XnaDisplayDevice, IDisplayDevice
    {
        public static LazyIosDisplayDevice? Instance { get; private set; }
        private readonly HashSet<TileSheet> _failedTileSheets = new();
        private static System.Reflection.FieldInfo? _loadedAssetsField;
        private static System.Reflection.FieldInfo? _disposableAssetsField;

        public LazyIosDisplayDevice(ContentManager contentManager, GraphicsDevice graphicsDevice)
            : base(contentManager, graphicsDevice)
        {
            Instance = this;
        }

        public static void EnsureInstalled()
        {
            try
            {
                if (Game1.mapDisplayDevice is LazyIosDisplayDevice)
                    return;

                var content = Game1.game1?.xTileContent ?? Game1.content;
                var gd = Game1.graphics?.GraphicsDevice;
                if (content != null && gd != null && !gd.IsDisposed)
                {
                    Game1.mapDisplayDevice = new LazyIosDisplayDevice(content, gd);
                }
            }
            catch { }
        }

        /// <summary>
        /// Explicit interface implementation intercepted when xTile.Map.LoadTileSheets(IDisplayDevice) runs.
        /// Skips eager texture loading for all 120+ locations; DrawTile() loads only visible tilesheets on demand.
        /// </summary>
        void IDisplayDevice.LoadTileSheet(TileSheet tileSheet)
        {
            // Intentionally deferred until DrawTile() renders a tile from this TileSheet.
        }

        void IDisplayDevice.DisposeTileSheet(TileSheet tileSheet)
        {
            if (tileSheet != null)
            {
                m_tileSheetTextures.Remove(tileSheet);
                _failedTileSheets.Remove(tileSheet);
            }
        }

        public void PruneTileSheets(Map? keepMap)
        {
            try
            {
                _failedTileSheets.Clear();
                if (m_tileSheetTextures == null || m_tileSheetTextures.Count == 0)
                    return;

                if (keepMap == null)
                {
                    m_tileSheetTextures.Clear();
                    return;
                }

                // 1. Collect normalized ImageSource strings still needed by keepMap so shared tilesheets are preserved
                HashSet<string>? keepSources = null;
                if (keepMap.TileSheets != null)
                {
                    keepSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var ts in keepMap.TileSheets)
                    {
                        if (ts != null && !string.IsNullOrEmpty(ts.ImageSource))
                        {
                            keepSources.Add(ts.ImageSource.Replace('\\', '/'));
                        }
                    }
                }

                List<TileSheet>? toRemove = null;
                HashSet<string>? sourcesToEvict = null;
                foreach (var kvp in m_tileSheetTextures)
                {
                    if (kvp.Key == null || !ReferenceEquals(kvp.Key.Map, keepMap))
                    {
                        toRemove ??= new List<TileSheet>();
                        toRemove.Add(kvp.Key!);

                        string? src = kvp.Key?.ImageSource;
                        if (!string.IsNullOrEmpty(src))
                        {
                            string norm = src.Replace('\\', '/');
                            if ((keepSources == null || !keepSources.Contains(norm)) && !ModEntry.IsCoreAsset(norm))
                            {
                                sourcesToEvict ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                sourcesToEvict.Add(norm);
                            }
                        }
                    }
                }

                if (toRemove != null)
                {
                    for (int i = 0; i < toRemove.Count; i++)
                    {
                        m_tileSheetTextures.Remove(toRemove[i]);
                    }
                }

                // 2. Evict unshared outgoing location tilesheet textures from ContentManager before pre-warming destination
                if (sourcesToEvict != null && sourcesToEvict.Count > 0 && m_contentManager != null)
                {
                    _loadedAssetsField ??= typeof(ContentManager).GetField("loadedAssets", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    _disposableAssetsField ??= typeof(ContentManager).GetField("disposableAssets", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    if (_loadedAssetsField?.GetValue(m_contentManager) is System.Collections.IDictionary dict)
                    {
                        var disposables = _disposableAssetsField?.GetValue(m_contentManager) as System.Collections.IList;
                        List<object>? keysToRemove = null;
                        lock (m_contentManager)
                        {
                            foreach (var k in dict.Keys)
                            {
                                if (k is string s && sourcesToEvict.Contains(s.Replace('\\', '/')))
                                {
                                    keysToRemove ??= new List<object>();
                                    keysToRemove.Add(k);
                                }
                            }
                            if (keysToRemove != null)
                            {
                                for (int i = 0; i < keysToRemove.Count; i++)
                                {
                                    var key = keysToRemove[i];
                                    var val = dict[key];
                                    dict.Remove(key);
                                    if (val is IDisposable disp)
                                    {
                                        disposables?.Remove(disp);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Pre-warms destination map tilesheets during black-screen warp transitions after outgoing textures are pruned.
        /// Aborts immediately if available OS memory is below 300 MB (or drops below 250 MB mid-warm), falling back to lazy DrawTile loading.
        /// </summary>
        public void PrewarmMapTileSheets(Map? targetMap)
        {
            try
            {
                if (!TouchOverlaySettings.Instance.EnableWarpTilesheetPrewarm)
                    return;
                if (targetMap?.TileSheets == null || targetMap.TileSheets.Count == 0)
                    return;

                // Low-memory abort guard: require at least 300 MB free OS headroom to start pre-warming
                long availMb = MemoryWatchdog.Available() / (1024 * 1024);
                if (availMb < 300)
                    return;

                foreach (var ts in targetMap.TileSheets)
                {
                    if (ts == null || string.IsNullOrEmpty(ts.ImageSource))
                        continue;

                    // Skip editor-only Paths sheet (never drawn in gameplay)
                    if (string.Equals(ts.Id, "Paths", StringComparison.OrdinalIgnoreCase) ||
                        ts.ImageSource.EndsWith("paths", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Per-sheet low-memory abort guard (250 MB floor)
                    if (MemoryWatchdog.Available() / (1024 * 1024) < 250)
                        break;

                    TryGetOrLoadTexture(ts);
                }
            }
            catch { }
        }

        private Texture2D? TryGetOrLoadTexture(TileSheet ts)
        {
            if (!m_tileSheetTextures.TryGetValue(ts, out Texture2D? value) || value == null || value.IsDisposed)
            {
                if (_failedTileSheets.Contains(ts))
                    return null;

                try
                {
                    ModEntry.ForceLoadTileSheet(this, ts);
                    m_tileSheetTextures.TryGetValue(ts, out value);

                    // If the ContentManager returned a cached Texture2D that was previously disposed,
                    // evict it from the ContentManager's loadedAssets dictionary and reload a fresh GPU texture.
                    if (value != null && value.IsDisposed && m_contentManager != null && !string.IsNullOrEmpty(ts.ImageSource))
                    {
                        _loadedAssetsField ??= typeof(ContentManager).GetField("loadedAssets", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                        if (_loadedAssetsField?.GetValue(m_contentManager) is System.Collections.IDictionary dict)
                        {
                            string norm = ts.ImageSource.Replace('\\', '/');
                            object? matchKey = null;
                            foreach (var k in dict.Keys)
                            {
                                if (k is string s && string.Equals(s.Replace('\\', '/'), norm, StringComparison.OrdinalIgnoreCase))
                                {
                                    matchKey = k;
                                    break;
                                }
                            }
                            if (matchKey != null)
                                dict.Remove(matchKey);
                        }
                        ModEntry.ForceLoadTileSheet(this, ts);
                        m_tileSheetTextures.TryGetValue(ts, out value);
                    }

                    if (value == null || value.IsDisposed)
                    {
                        _failedTileSheets.Add(ts);
                        return null;
                    }
                }
                catch
                {
                    _failedTileSheets.Add(ts);
                    return null;
                }
            }
            return value;
        }

        public override void DrawTile(Tile? tile, Location location, float layerDepth)
        {
            if (tile == null || tile.TileSheet == null)
                return;

            var ts = tile.TileSheet;
            Texture2D? value = TryGetOrLoadTexture(ts);

            if (value != null && !value.IsDisposed)
            {
                xTile.Dimensions.Rectangle tileImageBounds = ts.GetTileImageBounds(tile.TileIndex);
                m_tilePosition.X = location.X;
                m_tilePosition.Y = location.Y;
                m_sourceRectangle.X = tileImageBounds.X;
                m_sourceRectangle.Y = tileImageBounds.Y;
                m_sourceRectangle.Width = tileImageBounds.Width;
                m_sourceRectangle.Height = tileImageBounds.Height;
                DrawImpl(tile, location, layerDepth, value);
            }
        }

        protected override void DrawImpl(Tile tile, Location location, float layerDepth, Texture2D tileSheetTexture)
        {
            float rotation = GetRotation(tile);
            SpriteEffects spriteEffects = GetSpriteEffects(tile);
            Microsoft.Xna.Framework.Rectangle sourceRectangle = m_sourceRectangle;
            Vector2 origin = new Vector2(sourceRectangle.Width / 2f, sourceRectangle.Height / 2f);
            m_tilePosition.X += origin.X * Layer.zoom;
            m_tilePosition.Y += origin.X * Layer.zoom;
            m_spriteBatchAlpha.Draw(tileSheetTexture, m_tilePosition, sourceRectangle, m_modulationColour, rotation, origin, Layer.zoom, spriteEffects, layerDepth);
        }

        private static SpriteEffects GetSpriteEffects(Tile tile)
        {
            if (!tile.Properties.TryGetValue("@Flip", out var value) || !int.TryParse(value, out int result))
            {
                return SpriteEffects.None;
            }
            return (SpriteEffects)result;
        }

        private static float GetRotation(Tile tile)
        {
            if (!tile.Properties.TryGetValue("@Rotation", out var value) || !int.TryParse(value, out int result))
            {
                return 0f;
            }
            result %= 360;
            if (result == 0)
            {
                return 0f;
            }
            return (float)(Math.PI / (180.0 / result));
        }
    }
}
