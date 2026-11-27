using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SDViOS.Diagnostics
{
    /// <summary>
    /// JIT-availability probe, v4 (experiment branch, route-A investigation).
    ///
    /// v4 replaces probe v3's self-RWX execution test with the jit26 arena
    /// handshake proven in rimworld-ios-port (Port17) / playport: a StikDebug
    /// session allocates and page-blesses a 128 MiB RX arena via a brk
    /// #0xf00d handshake, the app aliases it RW, detaches, and only then
    /// executes self-written machine code FROM THE ARENA. iOS 26+ refuses to
    /// execute self-created RWX mappings (probe v3's SIGKILL), so executable
    /// memory must come from the debug session; the brk stubs live in the
    /// app's __TEXT segment (src/SDViOS/native/jit26.c, built by CI).
    ///
    /// Probes, in order:
    /// 1. W^X mapping probes (evidence: mapping is NOT the enforcement point).
    /// 2. Mono symbol probe (mono_jit_* are absent from the shipped runtime —
    ///    proof that the bundled Mono cannot JIT and a new runtime build is
    ///    required; see docs/JIT-ROUTE.md).
    /// 3. jit26 arena probe on a background thread (never blocks game startup):
    ///    gates on a live debugger (120 s), waits for the script, verifies the
    ///    arena with the 42/43 execution test. VERDICT line at the end.
    ///
    /// CRITICAL LOGGING NOTE: EngineLogger buffers (AutoFlush=false, 16 KB);
    /// a SIGKILL loses the buffer, so every line is flushed immediately.
    /// Strictly read-only towards Mono: never calls into Mono, unmaps its own
    /// mappings.
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
        private const int AcquireWaitSeconds = 120;

        private static readonly IntPtr MapFailed = new IntPtr(-1);

        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr mmap(IntPtr addr, IntPtr length, int prot, int flags, int fd, long offset);

        [DllImport("libc", SetLastError = true)]
        private static extern int mprotect(IntPtr addr, IntPtr length, int prot);

        [DllImport("libc", SetLastError = true)]
        private static extern int munmap(IntPtr addr, IntPtr length);

        [StructLayout(LayoutKind.Sequential)]
        private struct Jit26Result
        {
            public int Status;      // 0 ok; 10 no debugger; 11 prepare NULL; 12 alias failed; 13 probe failed
            public IntPtr Rx;
            public IntPtr Rw;
            public UIntPtr Capacity;
            public int Probe1;
            public int Probe2;
        }

        [DllImport("__Internal", CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe int sdv_jit26_acquire(
            out Jit26Result result,
            int waitSeconds,
            delegate* unmanaged[Cdecl]<IntPtr, void> logLine);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Jit26LogFromNative(IntPtr linePtr)
        {
            try
            {
                string? line = Marshal.PtrToStringUTF8(linePtr);
                if (!string.IsNullOrEmpty(line))
                {
                    EngineLogger.Log(line, "JIT26");
                    EngineLogger.Flush();
                }
            }
            catch
            {
                // never let a managed exception cross the native boundary
            }
        }

        public static void Run()
        {
            try
            {
                ProbeRwxDirect();
                ProbeRwThenMprotect();
                ProbeMonoSymbols();
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

            LogAndFlush($"[3] jit26 arena handshake starting on a background thread (waits up to {AcquireWaitSeconds} s for a live debugger — launch via StikDebug and run probe/stikdebug/sdv-jit26.js).");
            var t = new Thread(AcquireArena) { IsBackground = true, Name = "jit26-acquire" };
            t.Start();
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
        /// Presence of these exports decides whether the SHIPPED runtime could
        /// ever flip into JIT mode (v3 verdict: none are exported — a new
        /// runtime build is required; docs/JIT-ROUTE.md). Addresses are logged
        /// but never called.
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

        private static unsafe void AcquireArena()
        {
            try
            {
                var res = new Jit26Result();
                int rc = sdv_jit26_acquire(out res, AcquireWaitSeconds,
                    (delegate* unmanaged[Cdecl]<IntPtr, void>)&Jit26LogFromNative);
                LogAndFlush(
                    $"jit26 finished: rc={rc} status={res.Status} rx=0x{res.Rx.ToInt64():x} rw=0x{res.Rw.ToInt64():x} " +
                    $"cap=0x{res.Capacity.ToInt64():x} probe1={res.Probe1} probe2={res.Probe2}");
                bool go = rc == 0 && res.Status == 0 && res.Probe1 == 42 && res.Probe2 == 43;
                LogAndFlush(go
                    ? "VERDICT: dynamic code EXECUTES from a blessed arena — route A foundation CONFIRMED. Next milestone: build a JIT-enabled runtime and feed it this arena (docs/JIT-ROUTE.md, phase 2)."
                    : "VERDICT: arena grant FAILED in this session (not launched via StikDebug, or the kernel refused). The interpreter path remains the product; retry with a StikDebug launch.");
            }
            catch (Exception ex)
            {
                LogAndFlush($"jit26 acquire failed unexpectedly: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
