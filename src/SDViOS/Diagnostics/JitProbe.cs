using System;
using System.Runtime.InteropServices;

namespace SDViOS.Diagnostics
{
    /// <summary>
    /// JIT-availability probe (experiment branch, StikDebug investigation): answers two
    /// questions from the very top of Main(), before any game/mod assembly executes, so
    /// the results land in engine-latest.log even when the session later crashes.
    ///
    /// 1. W^X probe — can this process obtain writable+executable pages right now?
    ///    On a plain sideload iOS returns EPERM for PROT_EXEC on anonymous memory; with a
    ///    JIT enabler (StikDebug/AltJIT-style debugger attach) the window opens and both
    ///    variants succeed. Two variants because kernels enforce W^X differently: a direct
    ///    RWX mmap, and mmap-RW followed by mprotect-to-RWX.
    ///
    /// 2. Mono symbol probe — is the JIT backend even present and exported from this
    ///    statically linked Mono runtime? If mono_jit_set_aot_mode / mono_jit_compile_method
    ///    are not resolvable, no runtime mode-flip is possible without rebuilding the
    ///    runtime, regardless of what the OS allows.
    ///
    /// Strictly read-only: never calls into Mono, unmaps everything it maps.
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

        private static readonly IntPtr MapFailed = new IntPtr(-1);

        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr mmap(IntPtr addr, IntPtr length, int prot, int flags, int fd, long offset);

        [DllImport("libc", SetLastError = true)]
        private static extern int mprotect(IntPtr addr, IntPtr length, int prot);

        [DllImport("libc", SetLastError = true)]
        private static extern int munmap(IntPtr addr, IntPtr length);

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
        }

        /// <summary>mmap PROT_READ|WRITE|EXEC directly — the canonical JIT-window check.</summary>
        private static void ProbeRwxDirect()
        {
            IntPtr p = mmap(IntPtr.Zero, (IntPtr)PageSize, PROT_READ | PROT_WRITE | PROT_EXEC,
                MAP_PRIVATE | MAP_ANON, -1, 0);
            if (p == MapFailed)
            {
                EngineLogger.Log($"[JitProbe] mmap RWX: DENIED (errno={Marshal.GetLastWin32Error()}) — no JIT window this session.", "JITPROBE");
                return;
            }
            try
            {
                Marshal.WriteByte(p, 0xD5); // page is real: prove writability before reporting
                EngineLogger.Log("[JitProbe] mmap RWX: OK — W^X window open (JIT enabler active?).", "JITPROBE");
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
                EngineLogger.Log($"[JitProbe] mmap RW: failed (errno={Marshal.GetLastWin32Error()}) — unexpected on iOS.", "JITPROBE");
                return;
            }
            try
            {
                int rc = mprotect(p, (IntPtr)PageSize, PROT_READ | PROT_WRITE | PROT_EXEC);
                EngineLogger.Log(rc == 0
                    ? "[JitProbe] mprotect RW->RWX: OK — W^X window open."
                    : $"[JitProbe] mprotect RW->RWX: DENIED (rc={rc}, errno={Marshal.GetLastWin32Error()}) — W^X enforced.",
                    "JITPROBE");
            }
            finally
            {
                munmap(p, (IntPtr)PageSize);
            }
        }

        /// <summary>
        /// Presence of these exports decides whether a runtime aot-mode flip is even
        /// conceivable: mono_jit_set_aot_mode is the flip API, mono_jit_compile_method means
        /// the JIT backend code is inside the binary at all, mono_interp_exec_method means
        /// the interpreter engine is compiled in. Addresses are logged but never called.
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
                EngineLogger.Log(NativeLibrary.TryGetExport(handle, name, out IntPtr addr)
                    ? $"[JitProbe] symbol {name}: EXPORTED at 0x{addr.ToString("x")}"
                    : $"[JitProbe] symbol {name}: not exported", "JITPROBE");
            }
        }
    }
}
