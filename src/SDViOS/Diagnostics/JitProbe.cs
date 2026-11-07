using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace SDViOS.Diagnostics
{
    /// <summary>
    /// JIT-availability probe (experiment branch, StikDebug / route-A investigation).
    ///
    /// CRITICAL LOGGING NOTE: upstream EngineLogger (7fbd9b6) buffers writes (AutoFlush=false,
    /// 16 KB buffer). A kernel SIGKILL loses the entire buffer, so EVERY line this class logs
    /// is followed by an explicit Flush() — probe v2 produced empty logs precisely because
    /// the process was killed while everything was still buffered.
    ///
    /// Probes, in order:
    /// 1. W^X mapping probes (immediate, at startup) — mmap RWX direct, and mmap-RW +
    ///    mprotect-to-RWX. Both succeeded on device even without any JIT enabler, which
    ///    proves mapping is NOT the enforcement point.
    /// 2. Mono symbol probe (immediate) — mono_jit_set_aot_mode etc. are not exported from
    ///    the statically linked runtime (verified on device and by Mach-O scan: the JIT
    ///    compiler frontend is not even compiled into the shipped binary).
    /// 3. Execution probe (DELAYED 90 s) — writes a real ARM64 function (mov w0, #42; ret)
    ///    into a W+X mapping, flushes the instruction cache, and calls it, with each step
    ///    logged and flushed separately so the exact killing step is identifiable.
    ///    Delayed because AltJIT/StikDebug can only grant JIT to an ALREADY RUNNING process
    ///    (debugger attach after launch): a startup-time probe can never observe the
    ///    JIT-enabled state — probe v2 died at ~launch before any enabler could attach.
    ///
    /// Strictly read-only towards Mono: never calls into Mono, unmaps everything it maps.
    /// </summary>
    public static class JitProbe
    {
        // Darwin mmap constants (sys/mman.h). MAP_ANON is 0x1000 on Darwin — NOT the Linux
        // 0x20 value — and MAP_FAILED is (void*)-1.
        private const int PROT_READ = 0x1;
        private const int PROT_WRITE = 0x2;
        private const int PROT_EXEC = 0x4;
        private const int MAP_PRIVATE = 0x02;
        private const int MAP_ANON = 0x1000;
        private const int PageSize = 16384; // iOS arm64 devices use 16 KB pages
        private const int ExecProbeDelayMs = 90_000;

        private static readonly IntPtr MapFailed = new IntPtr(-1);
        private static Timer? _execTimer; // rooted so the delayed probe cannot be GC'd

        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr mmap(IntPtr addr, IntPtr length, int prot, int flags, int fd, long offset);

        [DllImport("libc", SetLastError = true)]
        private static extern int mprotect(IntPtr addr, IntPtr length, int prot);

        [DllImport("libc", SetLastError = true)]
        private static extern int munmap(IntPtr addr, IntPtr length);

        // Apple's real signature is (start, end) — NOT (start, len). GameHost.cs:55 declares
        // it as (start, len), which silently no-ops; do not copy that here.
        [DllImport("libSystem.dylib", EntryPoint = "sys_icache_invalidate")]
        private static extern void sys_icache_invalidate(IntPtr start, IntPtr end);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ExecFn();

        public static void Run()
        {
            try
            {
                ProbeRwxDirect();
                ProbeRwThenMprotect();
                ProbeMonoSymbols();
                ScheduleDelayedExecProbe();
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[JitProbe] probe failed unexpectedly: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                // Startup probes are pure evidence — never let the 16 KB buffer hold them
                // hostage against a later kill.
                EngineLogger.Flush();
            }
        }

        private static void LogAndFlush(string message, string tag = "JITPROBE")
        {
            EngineLogger.Log(message, tag);
            EngineLogger.Flush();
        }

        /// <summary>mmap PROT_READ|WRITE|EXEC directly — the canonical JIT-window check.</summary>
        private static void ProbeRwxDirect()
        {
            IntPtr p = mmap(IntPtr.Zero, (IntPtr)PageSize, PROT_READ | PROT_WRITE | PROT_EXEC,
                MAP_PRIVATE | MAP_ANON, -1, 0);
            if (p == MapFailed)
            {
                LogAndFlush($"mmap RWX: DENIED (errno={Marshal.GetLastWin32Error()}) — no JIT window this session.");
                return;
            }
            try
            {
                Marshal.WriteByte(p, 0xD5); // page is real: prove writability before reporting
                LogAndFlush("mmap RWX: OK — W+X pages are mappable (mapping is not the enforcement point).");
            }
            finally
            {
                munmap(p, (IntPtr)PageSize);
            }
        }

        /// <summary>mmap RW then mprotect to RWX — second signal, caught differently by some kernels.</summary>
        private static void ProbeRwThenMprotect()
        {
            IntPtr p = mmap(IntPtr.Zero, (IntPtr)PageSize, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANON, -1, 0);
            if (p == MapFailed)
            {
                LogAndFlush($"mmap RW: failed (errno={Marshal.GetLastWin32Error()}) — unexpected on iOS.");
                return;
            }
            try
            {
                int rc = mprotect(p, (IntPtr)PageSize, PROT_READ | PROT_WRITE | PROT_EXEC);
                LogAndFlush(rc == 0
                    ? "mprotect RW->RWX: OK — W+X pages are mappable via mprotect too."
                    : $"mprotect RW->RWX: DENIED (rc={rc}, errno={Marshal.GetLastWin32Error()}) — W^X enforced at mprotect.");
            }
            finally
            {
                munmap(p, (IntPtr)PageSize);
            }
        }

        /// <summary>
        /// Presence of these exports decides whether a runtime aot-mode flip is even
        /// conceivable. Addresses are logged but never called.
        /// </summary>
        private static void ProbeMonoSymbols()
        {
            string[] symbols =
            {
                "mono_jit_set_aot_mode",
                "mono_jit_compile_method",
                "mono_interp_exec_method",
                "mono_aot_get_method",
            };
            IntPtr handle = NativeLibrary.GetMainProgramHandle();
            foreach (string name in symbols)
            {
                LogAndFlush(NativeLibrary.TryGetExport(handle, name, out IntPtr addr)
                    ? $"symbol {name}: EXPORTED at 0x{addr.ToString("x")}"
                    : $"symbol {name}: not exported");
            }
        }

        /// <summary>Schedules the execution probe 90 s after startup (see class doc).</summary>
        private static void ScheduleDelayedExecProbe()
        {
            LogAndFlush($"exec probe scheduled in {ExecProbeDelayMs / 1000} s (time to enable JIT via StikDebug on the RUNNING app).");
            _execTimer = new Timer(_ =>
            {
                try
                {
                    ProbeExecDynamicCode();
                }
                catch (Exception ex)
                {
                    LogAndFlush($"exec probe failed unexpectedly: {ex.GetType().Name}: {ex.Message}", "JITPROBE");
                }
            }, null, ExecProbeDelayMs, Timeout.Infinite);
        }

        /// <summary>
        /// Route-A foundation test: prove or refute that dynamically generated code actually
        /// EXECUTES on this device. Every step is logged AND flushed, so wherever the process
        /// dies, the log names the exact forbidden step:
        ///   dies after [2/4]  -> the icache flush (making written pages executable) is denied;
        ///   dies after [3/4]  -> the execution itself is denied;
        ///   [4/4] appears     -> dynamic code runs, route A (custom JIT runtime) is GO.
        /// </summary>
        private static void ProbeExecDynamicCode()
        {
            LogAndFlush("exec test [1/4]: mapping fresh RWX page...");
            IntPtr p = mmap(IntPtr.Zero, (IntPtr)PageSize, PROT_READ | PROT_WRITE | PROT_EXEC,
                MAP_PRIVATE | MAP_ANON, -1, 0);
            if (p == MapFailed)
            {
                LogAndFlush($"exec test: skipped — mmap RWX denied at T+90s (errno={Marshal.GetLastWin32Error()}).");
                return;
            }
            try
            {
                // ARM64: mov w0, #42 ; ret  — returns a recognizable constant, nothing else.
                byte[] code = { 0x40, 0x05, 0x80, 0x52, 0xC0, 0x03, 0x5F, 0xD6 };
                Marshal.Copy(code, 0, p, code.Length);

                byte[] readBack = new byte[code.Length];
                Marshal.Copy(p, readBack, 0, code.Length);
                bool dataPathOk = readBack[0] == code[0] && readBack[4] == code[4];

                LogAndFlush($"exec test [2/4]: code written={dataPathOk}; about to sys_icache_invalidate — if the process dies after this line, the i-cache flush (making written pages executable) is the forbidden step.");
                sys_icache_invalidate(p, p + code.Length);

                LogAndFlush("exec test [3/4]: icache flush SURVIVED; about to CALL the generated function — if the process dies after this line, executing dynamic code is the forbidden step.");
                var fn = Marshal.GetDelegateForFunctionPointer<ExecFn>(p);
                int result = fn();

                LogAndFlush($"exec test [4/4]: returned {result} (expected 42) — dynamic code EXECUTES. Route A foundation CONFIRMED.");
            }
            catch (Exception ex)
            {
                LogAndFlush($"exec test: marshaling/call failed before execution: {ex.GetType().Name}: {ex.Message}", "JITPROBE");
            }
            finally
            {
                munmap(p, (IntPtr)PageSize);
            }
        }
    }
}
