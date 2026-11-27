// jit26.c — SDV route-A foundation test: receive a blessed executable arena
// from a StikDebug debug session (brk #0xf00d handshake), alias it RW,
// detach, then execute self-written machine code from it.
//
// Adapted from rimworld-ios-port probe/app/host.c (MIT, Port17 project),
// where the same protocol is proven on device, and in playport/universal.js:
//   1. live-debugger gate (csops CS_DEBUGGED + sysctl P_TRACED, fail-closed):
//      an unanswered brk raises SIGTRAP and kills the process, so never brk
//      without the gate;
//   2. prepare brk (x16=1): x0=NULL, x1=capacity — the StikDebug script
//      allocates RX memory via the debugserver "_M<len>,rx" packet, writes
//      one byte to every 16 KiB page ("blessing"; keeps the pages
//      executable after the debugger leaves) and returns the address in x0;
//   3. vm_remap an RW alias (mprotect, falling back to vm_protect);
//   4. detach brk (x16=0) — the debug session ends and faults become ours;
//   5. execution probe from the arena (sigsetjmp-guarded): 42, then 43.
//
// WHY THIS SHAPE (probe v2/v3 lessons, full analysis in docs/JIT-ROUTE.md):
//   - iOS 26+ refuses to EXECUTE self-created RWX mappings. Probe v3 died at
//     the icache flush / first call exactly there, with or without a JIT
//     entitlement. Executable memory must be granted by the debug session —
//     the brk stubs below live in the app's own __TEXT segment (this static
//     library), the only executable memory the process legitimately owns.
//   - While a debugger is attached, every fault is dispatched to the
//     debugserver first and an in-process handler never runs; hence the
//     detach happens BEFORE the probe.
//   - mono_jit_* symbols are absent from the shipped Mono runtime (probed in
//     JitProbe.cs). This library proves only the arena mechanism; feeding a
//     JIT-enabled runtime with it is the next milestone (docs/JIT-ROUTE.md).
//
// Built by CI (macOS runner, xcrun clang) into native/libjit26.a and linked
// into the app binary via NativeReference. Apple targets only:

#if !defined(__aarch64__)
#error "jit26.c is arm64-only (iOS devices)"
#endif

#include <setjmp.h>
#include <signal.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <unistd.h>
#include <stdint.h>
#include <sys/sysctl.h>
#include <sys/mman.h>
#include <libkern/OSCacheControl.h> // sys_icache_invalidate(start, len) — Apple's real signature; GameHost.cs declares (start, len) correctly, an older probe comment claimed (start, end) — that was wrong
#include <mach/mach.h>

#define SDV_JIT26_CAP 0x8000000ull // 128 MiB — must match sdv-jit26.js "_M" size (carried in x1)

#ifndef P_TRACED
#define P_TRACED 0x00000800
#endif
#ifndef CS_OPS_STATUS
#define CS_OPS_STATUS 0
#endif
#ifndef CS_DEBUGGED
#define CS_DEBUGGED 0x10000000 // set when launched/attached under a debugger; survives detach (not a live credential)
#endif

// csops has no public header (the lean SDK lacks sys/codesign.h); the syscall
// body lives in libSystem.
extern int csops(pid_t pid, unsigned int ops, void *useraddr, size_t usersize);

typedef void (*sdv_jit26_log_fn)(const char *line);

typedef struct {
    int status;       // 0 = ok; 10 = no debugger; 11 = prepare NULL; 12 = alias failed; 13 = probe failed
    void *rx;         // executable (blessed) view
    void *rw;         // writable alias
    size_t capacity;
    int probe1;
    int probe2;
} sdv_jit26_result;

static sdv_jit26_log_fn g_log;
static void slog(const char *line) { if (g_log) g_log(line); }

static int debugger_attached(void)
{
    pid_t pid = getpid();
    uint32_t flags = 0;
    if (csops(pid, CS_OPS_STATUS, &flags, sizeof(flags)) != 0 || !(flags & CS_DEBUGGED))
        return 0;
    int mib[4] = {CTL_KERN, KERN_PROC, KERN_PROC_PID, pid};
    struct kinfo_proc info;
    size_t size = sizeof(info);
    memset(&info, 0, sizeof(info));
    if (sysctl(mib, 4, &info, &size, NULL, 0) != 0 || size != sizeof(info))
        return 0;
    return (info.kp_proc.p_flag & P_TRACED) != 0;
}

// The protocol's two-argument trampoline: x0=NULL (script allocates) and the
// capacity in x1. A single-argument stub would put the capacity into x0 and
// the script's x0==NULL check would fail (Port17's real-device lesson).
__attribute__((noinline, optnone, naked)) static void *jit26_prepare_region(void *addr, size_t len)
{
    __asm__ volatile("mov x16, #1\n"
                     "brk #0xf00d\n"
                     "ret\n");
    (void)addr; (void)len;
}

__attribute__((noinline, optnone, naked)) static void jit26_detach(void)
{
    __asm__ volatile("mov x16, #0\n"
                     "brk #0xf00d\n"
                     "ret\n");
}

static int wait_debugger(int wait_s)
{
    slog("[jit26] waiting for a live debugger — launch SDV through StikDebug and run probe/stikdebug/sdv-jit26.js");
    char b[96];
    for (int waited = 0; waited <= wait_s; waited++) {
        if (debugger_attached())
            return 1;
        if (waited == wait_s)
            break;
        sleep(1);
        if (waited > 0 && waited % 15 == 0) {
            snprintf(b, sizeof(b), "[jit26] still waiting for a live debugger (%d/%d s)...", waited, wait_s);
            slog(b);
        }
    }
    return 0;
}

static uint8_t *map_alias_rw(void *source, size_t size)
{
    vm_address_t alias = 0;
    vm_prot_t cur = VM_PROT_NONE, mx = VM_PROT_NONE;
    kern_return_t kr = vm_remap(mach_task_self(), &alias, size, 0, VM_FLAGS_ANYWHERE,
                                mach_task_self(), (vm_address_t)source, FALSE,
                                &cur, &mx, VM_INHERIT_NONE);
    char b[96];
    if (kr != KERN_SUCCESS) {
        snprintf(b, sizeof(b), "[jit26] vm_remap failed: kr=%d (0x%x)", (int)kr, (unsigned)kr);
        slog(b);
        return NULL;
    }
    snprintf(b, sizeof(b), "[jit26] RW alias at %p (remap cur=%x max=%x)", (void *)alias, (unsigned)cur, (unsigned)mx);
    slog(b);
    if (mprotect((void *)alias, size, PROT_READ | PROT_WRITE) == 0)
        return (uint8_t *)alias;
    snprintf(b, sizeof(b), "[jit26] mprotect RW denied (errno=%d) — falling back to vm_protect", errno);
    slog(b);
    kr = vm_protect(mach_task_self(), alias, size, FALSE, VM_PROT_READ | VM_PROT_WRITE);
    if (kr == KERN_SUCCESS) {
        slog("[jit26] vm_protect RW ok");
        return (uint8_t *)alias;
    }
    snprintf(b, sizeof(b), "[jit26] vm_protect also denied: kr=%d (0x%x)", (int)kr, (unsigned)kr);
    slog(b);
    vm_deallocate(mach_task_self(), alias, size);
    return NULL;
}

static sigjmp_buf g_probe_jmp;
static volatile sig_atomic_t g_probe_faulted;

static void probe_fault_handler(int sig, siginfo_t *info, void *ctx)
{
    (void)ctx;
    char b[96];
    g_probe_faulted = 1;
    snprintf(b, sizeof(b), "[jit26] probe fault (sig=%d, addr=%p) — treated as clean failure", sig, info ? info->si_addr : NULL);
    slog(b);
    siglongjmp(g_probe_jmp, 1);
}

// Write via the RW view, execute through the RX view. Runs ONLY after the
// detach: with a debugger attached, faults reach debugserver first and this
// handler would never fire.
static int probe_execute(uint8_t *rxView, uint8_t *rwView, int *p1, int *p2)
{
    struct sigaction fa, oldBus, oldSegv, oldIll;
    memset(&fa, 0, sizeof(fa));
    fa.sa_sigaction = probe_fault_handler;
    fa.sa_flags = SA_SIGINFO;
    sigemptyset(&fa.sa_mask);
    if (sigaction(SIGBUS, &fa, &oldBus) != 0 || sigaction(SIGSEGV, &fa, &oldSegv) != 0 ||
        sigaction(SIGILL, &fa, &oldIll) != 0)
        return 0;
    const uint32_t g42[2] = {0x52800540u, 0xD65F03C0u}; // movz w0,#42 ; ret
    const uint32_t g43 = 0x52800560u;                   // movz w0,#43
    char b[64];
    int ok = 0;
    g_probe_faulted = 0;
    if (sigsetjmp(g_probe_jmp, 1) == 0) {
        memcpy(rwView, g42, sizeof(g42));
        sys_icache_invalidate(rwView, sizeof(g42));
        sys_icache_invalidate(rxView, sizeof(g42));
        int32_t (*fn)(void) = (int32_t (*)(void))rxView;
        int32_t r1 = fn();
        *p1 = r1;
        snprintf(b, sizeof(b), "[jit26] probe #1 -> %d (expect 42)", r1);
        slog(b);
        if (r1 == 42) {
            memcpy(rwView, &g43, sizeof(g43));
            sys_icache_invalidate(rwView, sizeof(g43));
            sys_icache_invalidate(rxView, sizeof(g43));
            int32_t r2 = fn();
            *p2 = r2;
            snprintf(b, sizeof(b), "[jit26] probe #2 -> %d (expect 43)", r2);
            slog(b);
            ok = (r2 == 43);
        }
    }
    sigaction(SIGBUS, &oldBus, NULL);
    sigaction(SIGSEGV, &oldSegv, NULL);
    sigaction(SIGILL, &oldIll, NULL);
    return !g_probe_faulted && ok;
}

int sdv_jit26_acquire(sdv_jit26_result *out, int wait_seconds, sdv_jit26_log_fn log_fn)
{
    memset(out, 0, sizeof(*out));
    out->status = 10;
    g_log = log_fn;
    char b[128];

    if (!wait_debugger(wait_seconds)) {
        slog("[jit26] no live debugger within the wait window — refusing to brk (an unanswered brk = SIGTRAP)");
        return -1;
    }
    // Check again immediately before the brk (Port17 discipline: the session
    // can disappear between the wait loop and the trap).
    if (!debugger_attached()) {
        slog("[jit26] debugger vanished right before the brk — aborting");
        return -1;
    }
    slog("[jit26] prepare brk issued (128 MiB; the process now stops at the brk while the script sweeps pages, ~1-2 min)");
    void *arena = jit26_prepare_region(NULL, (size_t)SDV_JIT26_CAP);
    if (arena == NULL) {
        slog("[jit26] prepare returned NULL — no JIT memory");
        out->status = 11;
        return -1;
    }
    snprintf(b, sizeof(b), "[jit26] arena granted at %p", arena);
    slog(b);
    {
        vm_address_t q = (vm_address_t)arena;
        vm_size_t qSize = 0;
        vm_region_basic_info_data_64_t qi;
        mach_msg_type_number_t qc = VM_REGION_BASIC_INFO_COUNT_64;
        mach_port_t qo = MACH_PORT_NULL;
        if (vm_region_64(mach_task_self(), &q, &qSize, VM_REGION_BASIC_INFO_64,
                         (vm_region_info_t)&qi, &qc, &qo) == KERN_SUCCESS) {
            if (qo != MACH_PORT_NULL)
                mach_port_deallocate(mach_task_self(), qo);
            snprintf(b, sizeof(b), "[jit26] kernel view: start=%p size=%llu cur=%x max=%x",
                     (void *)q, (unsigned long long)qSize, (unsigned)qi.protection, (unsigned)qi.max_protection);
            slog(b);
        }
    }
    uint8_t *rwAlias = map_alias_rw(arena, (size_t)SDV_JIT26_CAP);
    if (rwAlias == NULL) {
        slog("[jit26] RW alias failed — no usable JIT memory");
        out->status = 12;
        return -1;
    }
    slog("[jit26] RW alias ready — requesting detach");
    if (debugger_attached()) {
        jit26_detach();
        slog("[jit26] detached — the blessing keeps the pages executable; faults are ours now");
    } else {
        slog("[jit26] debugger already gone — skipping the detach brk");
    }

    out->rx = arena;
    out->rw = rwAlias;
    out->capacity = (size_t)SDV_JIT26_CAP;
    int ok = probe_execute((uint8_t *)arena, rwAlias, &out->probe1, &out->probe2);
    if (!ok) {
        slog("[jit26] execution probe FAILED — no usable JIT memory");
        out->status = 13;
        return -1;
    }
    // Deep-page spot checks: vm_region may only report the first 16 KiB submap
    // entry, but vm_remap(copy=FALSE) maps the whole 128 MiB and the script
    // swept every page — prove it.
    {
        static const size_t offs[] = {0x400000, 0x2000000, 0x7ff0000}; // +4 MiB, +32 MiB, tail-64 KiB
        const uint32_t mark = 0x4a554e4bu; // "JUNK"
        for (int i = 0; i < 3; i++) {
            memcpy(rwAlias + offs[i], &mark, sizeof(mark));
            uint32_t back = 0;
            memcpy(&back, (uint8_t *)arena + offs[i], sizeof(back));
            snprintf(b, sizeof(b), "[jit26] deep page 0x%llx: alias-write/arena-read %s",
                     (unsigned long long)offs[i], back == mark ? "match" : "MISMATCH");
            slog(b);
        }
    }
    out->status = 0;
    slog("[jit26] arena acquired and VERIFIED");
    return 0;
}
