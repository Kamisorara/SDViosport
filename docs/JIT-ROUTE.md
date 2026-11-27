# JIT on iOS for SDViosport — route decision record

Status: **investigation** (branch `experiment/jit-probe`). The shipped product is the
**Mono interpreter** build (`UseInterpreter=true`) and stays the product until a JIT
path is proven end-to-end on device. This document records what we know, what route
was chosen, and what phase 1 of that route ships.

## TL;DR

- iOS 26+ **refuses to execute self-created RWX memory** (kernel SIGKILL). Probe v3
  proved this on device, twice, with and without JIT entitlements.
- Executable memory **can** still be obtained: a debugger-launched process (StikDebug,
  launch-with-JIT) may ask its debug session for RX memory over a `brk` handshake and
  have the debugger **bless** it page-by-page. The blessing survives the detach —
  proven in production by the RimWorld iOS port (Port17).
- The **shipped Mono runtime has no JIT frontend** (`mono_jit_*` symbols are absent).
  So even with a working arena, JIT requires **swapping the runtime**.
- Route chosen (user decision): **CoreCLR hybrid, Port17-style** (route 2) instead of a
  self-patched Mono (route 1). Phase 1 = validate the arena handshake inside this app;
  phase 2 (future) = build/ship a JIT-enabled CoreCLR and feed it the arena.

## What the on-device probes established (v1 → v4)

Evidence logs live in `temp_jitprobe_ipa/日志/开启jit/engine-latest.log` and
`.../未开jit/engine-latest.log` (local only, not tracked).

| Probe | Result on device | Meaning |
|---|---|---|
| `mmap(PROT_READ\|WRITE\|EXEC)` | OK | Mapping RWX is **not** the enforcement point |
| `mmap RW` → `mprotect RWX` | OK | Same signal via the second path |
| `mono_jit_set_aot_mode` / `mono_jit_compile_method` / `mono_interp_exec_method` / `mono_aot_get_method` exports | **all absent** | The bundled Mono was built without a JIT frontend; no flag can turn it on |
| v3 self-RWX execution test (write code, `sys_icache_invalidate`, call it) | **kernel SIGKILL** after `[2/4]`, in both IPA variants | iOS 26+ kills execution of **self-created** RWX pages; entitlements do not change this |

Corrections carried into v4/`jit26.c`:

- `sys_icache_invalidate`'s real Apple signature is **`(start, len)`**
  (libkern/OSCacheControl.h; Port17's working code passes `sizeof`). An earlier probe
  comment claimed `(start, end)` — wrong (and harmless: `p, p+8` invalidates 8 bytes).
  v3's death was the **execution attempt**, not the icache call.
- EngineLogger is buffered (`AutoFlush=false`, 16 KB); v2 lost every line to the
  SIGKILL. Every probe line is now flushed immediately.

## The mechanism that works (Port17 / playport reference)

Reference implementation: `D:/Data/Code/reverse/rimworld-ios-port` (self-built
dotnet/runtime **v11.0.0** CoreCLR + RyuJIT + interpreter hybrid; its IPA really ships
`libclrjit*.dylib` + `libclrinterpreter.dylib`; arena consumption patch
`patches/dualjit-arena.patch` rewrites `minipal/Unix/doublemapping.cpp`; protocol
script `probe/stikdebug/rw17-jit26.js`; Chinese design doc `docs/PLAN-JIT26.md`).

1. **Gate (fail-closed):** the app only issues the `brk` when a *live* debugger is
   attached — `csops(pid, CS_OPS_STATUS) & CS_DEBUGGED (0x10000000)` **AND**
   `sysctl(CTL_KERN, KERN_PROC, KERN_PROC_PID) → kp_proc.p_flag & P_TRACED (0x800)`.
   `CS_DEBUGGED` alone survives detach, so the P_TRACED check is what proves liveness.
   An unanswered `brk` raises SIGTRAP and kills the process — never `brk` un-gated.
2. **Prepare** (`mov x16, #1 ; brk #0xf00d`, two-argument stub so x0=NULL and the
   capacity sits in x1): the StikDebug script allocates RX memory with the private
   debugserver packet `_M<len>,rx`, then writes **one byte to every 16 KiB page**
   (`M<addr>,1:00` — the "blessing", ~8192 packets for 128 MiB) and returns the base
   address in x0 (`P0=addr`).
3. **RW alias:** `vm_remap(..., copy=FALSE)` of the arena (or `vm_protect` fallback),
   `mprotect` to RW. Writes go through the alias, execution through the RX view.
4. **Detach** (`mov x16, #0 ; brk #0xf00d`, skip the brk first with `P20=pc+4`, then
   `D`): the debug session ends; **the blessing keeps the pages executable**. While
   attached, faults are dispatched to the debugserver first, so in-process
   `sigsetjmp` handlers never run — detach **before** any probing.
5. **Verify:** write `movz w0,#42 ; ret` via the RW view, flush icache, call through
   the RX view → expect 42; rewrite to 43 → expect 43. Spot-check deep pages
   (+4 MiB, +32 MiB, tail) to prove the whole 128 MiB is aliased, not just the first
   submap entry.

Ownership model (phase 2): the **host** acquires the arena and hands it to the runtime
via environment variables (`PORT17_JIT_RX`/`RW`/`SIZE` in Port17). Host-side iteration
is a ~3-minute IPA rebuild, not a 40-minute runtime CI build.

## Route comparison (why route 2)

| | Route 1: self-built Mono with JIT | Route 2: CoreCLR hybrid (Port17-style) — **chosen** |
|---|---|---|
| Runtime | Mono with a code-manager patch to consume an external arena — uncharted territory, no reference implementation | Port17's `dualjit-arena.patch` + build scripts can be reused nearly as-is |
| SMAPI compatibility | SMAPI 4.x targets .NET 6+; Mono is the lineage Stardew/SMAPI always ran on, but the patch is novel | SMAPI 4.x supports CoreCLR; **Harmony is native on CoreCLR** (no detour hacks), so most of `src/SDViOS/Compatibility` shrinks or vanishes |
| Game compatibility | high | must be validated: `Stardew Valley.dll` under CoreCLR + interpreter fallback for unsupported code paths |
| Engineering risk | runtime-internals patching with no reference | reuse of a proven reference; main new work is MonoGame/MonoMod on CoreCLR |
| Fallback | interpreter IPA stays the product in both cases | same |

## Phases

- **Phase 0 (done):** probes v1–v3 — W^X mapping OK, Mono symbols absent, self-RWX
  execution killed. Established *why* the arena route is the only route.
- **Phase 1 (this commit — validation harness, no runtime change):**
  - `src/SDViOS/native/jit26.c` — the host side of the protocol as a static library
    (entry point `int sdv_jit26_acquire(sdv_jit26_result *out, int wait_seconds,
    sdv_jit26_log_fn log_fn)`; status 0/10/11/12/13; 120 s gate wait; RW alias;
    42/43 execution probe; deep-page spot checks). Built by CI into
    `native/libjit26.a` (`xcrun clang -arch arm64`) and linked via
    `<NativeReference>` in `SDViOS.csproj` (`AllowUnsafeBlocks` enabled).
  - `probe/stikdebug/sdv-jit26.js` — the StikDebug serve-mode script that answers
    the handshake (allocate+bless RX, return the address, handle the detach).
  - `src/SDViOS/Diagnostics/JitProbe.cs` v4 — keeps the W^X and Mono-symbol probes,
    replaces the suicidal self-RWX test with the arena handshake, runs it on a
    background thread (startup never blocks) and logs every line with `Flush()`.
    `GameHost.cs` calls `JitProbe.Run()` during file-system init, unchanged.
  - **This phase changes no runtime behaviour.** Without a StikDebug launch the
    handshake just times out and logs `status=10`.
- **Phase 2 (future):** build the JIT-enabled CoreCLR (Port17 patches), ship it in the
  IPA, feed it the arena via env vars, boot SMAPI + the game on it, re-audit
  `Compatibility/` (most detour hacks become unnecessary on CoreCLR).

## On-device runbook (phase 1 verdict run)

1. Push this branch; let the **Build Stardew Valley iOS IPA** workflow finish; download
   the `StardewValley-iOS` artifact (the new `Build jit26 native stub` step runs
   before the .NET build).
2. Sideload (TrollStore / AltStore / SideStore — any installer works; the probe needs
   no special signing).
3. Open **StikDebug** and launch *Stardew Valley* **with JIT enabled** (launch-through-
   debugger is required — attaching to an already-running app cannot grant the arena).
4. In StikDebug's serve-mode window run the bundled `probe/stikdebug/sdv-jit26.js`
   within 120 s of app start.
5. Pull `python tools/pull_smapi_log.py` and read `engine-latest.log` (`JITPROBE` /
   `JIT26` tags). Expected healthy tail:
   `[JITPROBE] mmap RWX: OK` → `symbol mono_jit_*: not exported` (expected — the
   bundled Mono is interpreter-only) → `[JIT26] prepare brk issued` →
   `[JIT26] arena granted at ...` → `[JIT26] RW alias ready — requesting detach` →
   `[JIT26] probe #1 -> 42`, `probe #2 -> 43` → deep-page `match` lines →
   `[JIT26] arena acquired and VERIFIED` →
   `VERDICT: dynamic code EXECUTES from a blessed arena — route A foundation CONFIRMED.`
   Any `VERDICT: arena grant FAILED` line means the run was not StikDebug-launched or
   the handshake failed mid-way (the `status=` code says where).

## Known corrections (carry into future docs)

- `sys_icache_invalidate(start, len)` — Apple's real signature; the older
  "(start, end) is correct" note (probe v3 comment and the AGENTS.md gotcha) is wrong.
- `tools/wifi_pull_log.py` and the Wi-Fi mode of `deploy_touch_controls.py` target a
  removed debug server; use the USB tools for log pulls.
