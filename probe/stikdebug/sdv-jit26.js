// StikDebug serve-mode JIT script for the SDV iOS port (route-A probe).
// Port17-shaped protocol (rw17-jit26.js / playport-universal.js): the APP asks
// for its JIT memory by executing a brk; this script only answers. The app
// side is src/SDViOS/native/jit26.c (built by CI into libjit26.a):
//   sdv_jit26_prepare_region(len)  mov x16, #1 ; brk #0xf00d ; ret
//   sdv_jit26_detach()             mov x16, #0 ; brk #0xf00d ; ret
// On every brk #0xf00d stop: verify the pc points at the brk, skip it
// (P20 = pc+4; skipping matters for the detach too - resuming AT the brk
// would re-execute it and die on SIGTRAP once detached), then:
//   x16=1 (prepare): x0 is NULL -> allocate x1 bytes RX with the private
//     "_M<len>,rx" packet, write one byte to every 16 KiB page of it (the
//     blessing that keeps the pages executable once the debugger is gone;
//     STS2-exact sweep, ~8192 packets for 128 MiB), and return the address
//     in x0;
//   x16=0 (detach): send "D" and end the script.
// After the detach the session is over and this window can be closed: the
// per-page blessing survives it.
//
// Host API (StikDebug, the same three calls Port17's script uses):
//   get_pid() - the pid StikDebug launched with JIT this session;
//   send_command(packet) - the reply, "" on failure;
//   log(line).
// Every failure throws; StikDebug reports it to the window.
"use strict";

const BRK = "a0013ed4";   // brk #0xf00d, little-endian
const PAGE = 0x4000n;     // 16 KiB
const MAX_STOPS = 256;
const MAX_SAFE = 2n ** 53n;

function fail(what) {
    throw new Error("sdv-jit26: " + what);
}

function command(packet) {
    const reply = send_command(packet);
    if (typeof reply !== "string" || reply === "") fail(packet + ": no reply from debugserver");
    return reply;
}

function expectOK(packet, what) {
    const reply = command(packet);
    if (reply !== "OK") fail(what + ": debugserver replied " + reply.slice(0, 64));
}

// A register or P-packet value: its 8 bytes, little-endian, as hex.
function leHex(value) {
    let out = "";
    for (let i = 0n; i < 8n; i++) out += ((value >> (8n * i)) & 0xffn).toString(16).padStart(2, "0");
    return out;
}

function fromLeHex(field) {
    let value = 0n;
    for (let i = 7; i >= 0; i--) value = (value << 8n) | BigInt(parseInt(field.substr(2 * i, 2), 16));
    return value;
}

function toNumber(value, what) {
    if (value >= MAX_SAFE) fail(what + " 0x" + value.toString(16) + " is not below 2^53");
    return Number(value);
}

// A stop reply: T<signal>, then NN:<value>; register fields and key:value;
// pairs. Apple's debugserver numbers the arm64 registers 00-1c x0-x28, 1d sp,
// 1e lr, 1f fp, 20 pc, 21 pstate.
function parseStop(reply) {
    if (reply[0] === "W" || reply[0] === "X") fail("the target exited: " + reply.slice(0, 64));
    if (reply[0] !== "T") fail("unexpected stop reply: " + reply.slice(0, 128));
    const thread = /thread:([0-9a-fA-F]+);/.exec(reply);
    if (!thread) fail("stop reply has no thread: " + reply.slice(0, 128));
    const regs = {};
    const field = /(?:^T[0-9a-fA-F]{2}|;)([0-9a-fA-F]{2}):([0-9a-fA-F]{16})(?=;)/g;
    for (let m; (m = field.exec(reply)) !== null;) regs[m[1].toLowerCase()] = fromLeHex(m[2]);
    for (const r of ["00", "01", "10", "20"]) {
        if (!(r in regs)) fail("stop reply has no register " + r + " (shape changed?): " + reply.slice(0, 128));
    }
    return { thread: BigInt("0x" + thread[1]).toString(16), x0: regs["00"], x1: regs["01"], x16: regs["10"], pc: regs["20"] };
}

const pid = get_pid();
log("sdv-jit26: session pid = " + pid + " (must be the app StikDebug launched with JIT)");
const attach = command("vAttach;" + pid.toString(16));
if (attach[0] === "E") fail("vAttach to pid " + pid + " failed: " + attach.slice(0, 64));
log("sdv-jit26: attached to pid " + pid + "; waiting for the app's prepare brk (JitProbe gates on a live debugger)");

let prepares = 0;
for (let stop = 1; ; stop++) {
    if (stop > MAX_STOPS) fail("no detach after " + MAX_STOPS + " stops");
    const reply = command("c");
    const s = parseStop(reply);
    const insn = command("m" + s.pc.toString(16) + ",4");
    if (insn.toLowerCase() !== BRK) {
        // Not our trap: a genuine signal (SIGSEGV/SIGBUS/SIGILL...) from
        // anything the app does under attach. Report it, then RE-DELIVER the
        // signal with vCont;S and keep serving; MAX_STOPS bounds pathological
        // loops.
        const m = /^T([0-9a-fA-F]{2})/.exec(reply);
        const sigNo = m ? m[1] : null;
        log("sdv-jit26: signal stop (sig 0x" + (sigNo || "??") + ", pc 0x" + s.pc.toString(16) +
            ", insn " + insn.slice(0, 16) + ") - re-delivering and keep serving");
        if (sigNo && s.thread) send_command("vCont;S" + sigNo + ":" + s.thread);
        continue;
    }
    expectOK("P20=" + leHex(s.pc + 4n) + ";thread:" + s.thread + ";", "skipping the brk");

    if (s.x16 === 1n) {
        if (s.x0 !== 0n) fail("prepare expected x0=NULL, got 0x" + s.x0.toString(16));
        const reply2 = command("_M" + s.x1.toString(16) + ",rx");
        if (reply2[0] === "E" || !/^[0-9a-fA-F]+$/.test(reply2)) fail("allocating 0x" + s.x1.toString(16) + " bytes RX: " + reply2.slice(0, 64));
        const addr = BigInt("0x" + reply2);
        const length = s.x1 > PAGE ? s.x1 : PAGE;
        // STS2-exact page preparation: one 1-byte write per 16 KiB page,
        // every write must succeed. The app sits stopped at the brk the
        // whole time - the progress lines keep the window readable.
        const total = length / PAGE;
        log("sdv-jit26: arena at 0x" + addr.toString(16) + ", sweeping " + total + " pages (1-2 min)...");
        for (let off = 0n; off < length; off += PAGE) {
            if (send_command("M" + (addr + off).toString(16) + ",1:00") !== "OK")
                fail("Unprepared JIT page at offset " + off);
            const done = Number(off / PAGE) + 1;
            if (done % 1024 === 0) log("sdv-jit26: page sweep " + done + "/" + total);
        }
        expectOK("P0=" + leHex(addr) + ";thread:" + s.thread + ";", "returning the region address");
        prepares++;
        log("sdv-jit26: prepared 0x" + addr.toString(16) + " (" + (length / PAGE).toString() + " pages); resuming - the detach brk comes next");
        continue;
    }
    if (s.x16 === 0n) {
        expectOK("D", "detach");
        log("sdv-jit26: detached after " + prepares + " prepare(s) - the blessing keeps the pages executable; THIS WINDOW CAN BE CLOSED, the app continues unattached");
        break;
    }
    fail("unknown call x16=0x" + s.x16.toString(16) + " at stop " + stop);
}
