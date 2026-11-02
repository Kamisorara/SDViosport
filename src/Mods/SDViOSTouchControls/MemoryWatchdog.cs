using System;
using System.Runtime.InteropServices;
using System.Threading;
using StardewModdingAPI;
using StardewValley;

namespace SDViOSTouchControls
{
    /// <summary>
    /// Background watchdog that samples the real iOS memory headroom (os_proc_available_memory = bytes left
    /// before Jetsam kills us) and forces a compacting GC when headroom gets low. Runs on its own thread so
    /// it keeps working during long synchronous loads (e.g. SMAPI's Load_Locations task or save transitions)
    /// where no UpdateTicking events fire.
    /// </summary>
    internal static class MemoryWatchdog
    {
        [DllImport("/usr/lib/libSystem.B.dylib")]
        private static extern UIntPtr os_proc_available_memory();

        private const long MB = 1024 * 1024;
        private const long LowWaterBytes = 350 * MB;   // force GC below this
        private const long CriticalBytes = 200 * MB;   // log every sample below this
        private const long EmergencyBytes = 50 * MB;   // high-frequency GC below this

        private static Thread? _thread;
        private static IMonitor? _monitor;
        private static bool _nativeAvailable = true;
        private static bool _lohCompactionSupported = true;
        private static long _minSeen = long.MaxValue;
        private static long _lastPostGcManaged = 0;
        private static DateTime _lastBackup = DateTime.MinValue;
        private static DateTime _lastGc = DateTime.MinValue;
        private static readonly object _gcLock = new object();

        public static long MinSeenMB => _minSeen == long.MaxValue ? -1 : _minSeen / MB;
        public static long LastPostGcManaged => Interlocked.Read(ref _lastPostGcManaged);

        public static void Start(IMonitor monitor)
        {
            if (_thread != null) return;
            _monitor = monitor;
            Interlocked.Exchange(ref _lastPostGcManaged, GC.GetTotalMemory(false));
            _thread = new Thread(Loop) { IsBackground = true, Name = "SDViOS-MemWatchdog", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
            monitor.Log($"[MEM_DEBUG] Watchdog started. Available now: {Available() / MB} MB", LogLevel.Info);
        }

        /// <summary>Bytes left before Jetsam, or -1 if unavailable.</summary>
        public static long Available()
        {
            if (!_nativeAvailable) return -1;
            try { return (long)os_proc_available_memory().ToUInt64(); }
            catch { _nativeAvailable = false; return -1; }
        }

        /// <summary>Forces an immediate Gen2 GC and optional Large Object Heap compaction with cooldown deduplication.</summary>
        public static void ForceCollect(string reason, int minIntervalMs = 1500, bool compactLoh = true)
        {
            try
            {
                lock (_gcLock)
                {
                    var now = DateTime.UtcNow;
                    if (minIntervalMs > 0 && (now - _lastGc).TotalMilliseconds < minIntervalMs)
                    {
                        return;
                    }
                    _lastGc = now;

                    long availBefore = Available();
                    if (availBefore >= 0 && availBefore < _minSeen) _minSeen = availBefore;
                    long managedBefore = GC.GetTotalMemory(false);
                    if (compactLoh && _lohCompactionSupported)
                    {
                        try { System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce; }
                        catch (PlatformNotSupportedException) { _lohCompactionSupported = false; }
                        catch (Exception sex) { _lohCompactionSupported = false; LogErr("GCSettings.LOHCompactionMode", sex); }
                    }

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        GC.Collect(2, GCCollectionMode.Forced, true, compactLoh);
                        if (compactLoh)
                        {
                            GC.WaitForPendingFinalizers();
                            GC.Collect(2, GCCollectionMode.Forced, true, compactLoh);
                        }
                    }
                    catch (Exception gex)
                    {
                        LogErr("GC.Collect(compacting)", gex);
                        try { GC.Collect(); } catch (Exception g2) { LogErr("GC.Collect()", g2); }
                    }

                    _lastGc = DateTime.UtcNow;
                    long availAfter = Available();
                    if (availAfter >= 0 && availAfter < _minSeen) _minSeen = availAfter;
                    long managedAfter = GC.GetTotalMemory(false);
                    Interlocked.Exchange(ref _lastPostGcManaged, managedAfter);
                    _monitor?.Log($"[MEM_DEBUG] ForceCollect ({reason}) in {sw.ElapsedMilliseconds} ms: avail {availBefore / MB} -> {availAfter / MB} MB, managed {managedBefore / MB} -> {managedAfter / MB} MB", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                _monitor?.Log($"[MEM_DEBUG] ForceCollect error: {ex.Message}", LogLevel.Warn);
            }
        }

        private static void Loop()
        {
            DateTime lastLog = DateTime.MinValue;

            while (true)
            {
                try
                {
                    bool loading = false;
                    int gameMode = 0;
                    bool worldReady = false;
                    try
                    {
                        gameMode = Game1.gameMode;
                        worldReady = Context.IsWorldReady;
                        loading = gameMode == 6 || !worldReady || ModEntry.IsSavingOrNewDay;
                    }
                    catch { }

                    long avail = Available();
                    long managed = GC.GetTotalMemory(false);
                    long lastManaged = Interlocked.Read(ref _lastPostGcManaged);
                    long managedGrowth = managed - lastManaged;
                    var now = DateTime.UtcNow;

                    if (avail >= 0 && avail < _minSeen) _minSeen = avail;

                    bool shouldCollect = false;
                    int gcInterval = 3500;
                    bool compactLoh = true;

                    if (loading)
                    {
                        // During save loading or screen transitions, collect aggressively when headroom is low
                        if (avail >= 0 && avail < 25 * MB)
                            gcInterval = 400;
                        else if (avail >= 0 && avail < EmergencyBytes)
                            gcInterval = 800;
                        else if (avail >= 0 && avail < 150 * MB)
                            gcInterval = 1500;
                        else
                            gcInterval = 2500;

                        if (avail >= 0 && avail < LowWaterBytes &&
                            (now - _lastGc).TotalMilliseconds > gcInterval &&
                            (managedGrowth >= 15 * MB || avail < EmergencyBytes))
                        {
                            shouldCollect = true;
                        }
                    }
                    else
                    {
                        // During active gameplay, NEVER freeze the game every 1.5s when managed memory is steady!
                        // Only collect if managed memory has actually grown significantly since the last GC.
                        if (avail >= 0 && avail < 35 * MB && managedGrowth >= 15 * MB && (now - _lastGc).TotalMilliseconds >= 4000)
                        {
                            shouldCollect = true;
                            gcInterval = 4000;
                            compactLoh = false; // Fast Gen2 GC without expensive LOH compaction during active gameplay (LOH compaction quarantined to fades/warps/saving)
                        }
                        else if (avail >= 0 && avail < 150 * MB && managedGrowth >= 48 * MB && (now - _lastGc).TotalMilliseconds >= 10000)
                        {
                            shouldCollect = true;
                            gcInterval = 10000;
                            compactLoh = false; // Fast Gen2 GC without expensive LOH compaction during gameplay
                        }
                        else if (avail >= 0 && avail < LowWaterBytes && managedGrowth >= 70 * MB && (now - _lastGc).TotalMilliseconds >= 15000)
                        {
                            shouldCollect = true;
                            gcInterval = 15000;
                            compactLoh = false;
                        }
                    }

                    if (shouldCollect)
                    {
                        if (avail < EmergencyBytes && (now - _lastBackup).TotalSeconds > 15)
                        {
                            _lastBackup = now;
                            LogBackup.BackupLogs("LowMemoryEmergency");
                        }
                        ForceCollect($"Watchdog(avail={avail / MB}MB, +{managedGrowth / MB}MB)", minIntervalMs: gcInterval, compactLoh: compactLoh);
                        lastLog = DateTime.UtcNow;
                    }
                    else
                    {
                        double logEvery = loading ? ((avail >= 0 && avail < CriticalBytes) ? 2.0 : 5.0) : 20.0;
                        if ((now - lastLog).TotalSeconds >= logEvery)
                        {
                            _monitor?.Log($"[MEM_DEBUG] avail={avail / MB} MB (min {_minSeen / MB} MB), managed={managed / MB} MB (+{Math.Max(0, managedGrowth) / MB} MB), gameMode={gameMode}, worldReady={worldReady}", LogLevel.Info);
                            lastLog = now;
                        }
                    }

                    int sleepMs = loading ? ((avail >= 0 && avail < EmergencyBytes) ? 150 : 300) : 1000;
                    Thread.Sleep(sleepMs);
                }
                catch (Exception ex)
                {
                    LogErr("loop", ex);
                    Thread.Sleep(1000);
                }
            }
        }

        private static int _errCount;
        private static void LogErr(string where, Exception ex)
        {
            if (_errCount++ < 10)
                _monitor?.Log($"[MEM_DEBUG] Watchdog error in {where}: {ex.GetType().Name}: {ex.Message}", LogLevel.Warn);
        }
    }
}
