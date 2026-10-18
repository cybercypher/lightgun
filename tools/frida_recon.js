'use strict';
// Recon inside pcsx2-qtx64.exe: locate guncon2 code anchors so phase-2 hooks
// can be placed precisely. Everything defensive; results via send() to the
// driver log. No behavior is modified.

const MAIN = "pcsx2-qtx64.exe";
const ANCHORS = [
  "GunCon2 Set Param %04X %d %d",
  "guncon2: req %04X val: %04X idx: %04X len: %d\n",
  "(GunCon2) No automatic config found for '{}'",
  "SLUS-20645",
  "SCES-51844",
  "Namco GunCon2",
  "Creating a Light Gun in port 1",
];

function out(o) { send(o); }

function getMain() { return Process.getModuleByName(MAIN); }

function modRanges(prot) {
  const m = getMain();
  const end = m.base.add(m.size);
  return Process.enumerateRanges({ protection: prot, coalesce: false })
    .filter(function (r) { return r.base.compare(m.base) >= 0 && r.base.compare(end) < 0; });
}

function asciiPat(s) {
  const r = [];
  for (let i = 0; i < s.length; i++) r.push(("0" + s.charCodeAt(i).toString(16)).slice(-2));
  return r.join(" ");
}

// ---- 1. module info ----
try {
  const m = getMain();
  out({ recon: "module", base: m.base.toString(), size: m.size, path: m.path });
  const sdl = Process.findModuleByName("SDL3.dll");
  out({ recon: "sdl3", found: !!sdl, base: sdl ? sdl.base.toString() : null });
  for (const n of ["SDL_GetGamepadAxis", "SDL_GetGamepadButton", "SDL_GetGamepadButtonLabel"]) {
    try { out({ recon: "sdl3_export", name: n, addr: Module.getExportByName("SDL3.dll", n).toString() }); }
    catch (e) { out({ recon: "sdl3_export_missing", name: n }); }
  }
} catch (e) { out({ recon: "module_error", err: String(e) }); }

// ---- 2. anchor strings in readable memory of the main module ----
const anchorAddrs = {};
try {
  for (const s of ANCHORS) {
    let hits = [];
    for (const r of modRanges("r--")) {
      try { hits = hits.concat(Memory.scanSync(r.base, r.size, asciiPat(s))); } catch (e) {}
    }
    anchorAddrs[s] = hits.map(function (h) { return h.address; });
    out({ recon: "anchor", str: s, count: hits.length, addrs: anchorAddrs[s].map(String) });
  }
} catch (e) { out({ recon: "anchors_error", err: String(e) }); }

// ---- 3. cmp imm32 0x2109 sites in code (ClassInterfaceOutRequest|0x09) ----
let cmpHits = [];
try {
  for (const r of modRanges("--x")) {
    try { cmpHits = cmpHits.concat(Memory.scanSync(r.base, r.size, "09 21 00 00")); } catch (e) {}
  }
  out({ recon: "cmp_2109", count: cmpHits.length, addrs: cmpHits.map(function (h) { return h.address.toString(); }) });
} catch (e) { out({ recon: "cmp_error", err: String(e) }); }

// ---- 4. disasm context around each cmp site (registers/offsets reveal `us`) ----
function disasmWindow(start, end) {
  const lines = [];
  let a = start;
  let guard = 0;
  while (a.compare(end) < 0 && guard++ < 512) {
    try {
      const ins = Instruction.parse(a);
      lines.push(ins.address + " " + ins.mnemonic + " " + ins.opStr);
      a = ins.address.add(ins.size);
    } catch (e) { a = a.add(1); }
  }
  return lines;
}

try {
  for (const h of cmpHits) {
    const lo = h.address.sub(200);
    out({ recon: "cmp_ctx", at: h.address.toString(), disasm: disasmWindow(lo, h.address.add(400)) });
  }
} catch (e) { out({ recon: "ctx_error", err: String(e) }); }

// ---- 5. lea rip-relative refs to anchor strings, swept around the cmp sites ----
// (guncon2 TU is contiguous; the cmp sites localize it)
function leaRefs(winStart, winEnd, targets) {
  const refs = [];
  let a = winStart;
  let guard = 0;
  while (a.compare(winEnd) < 0 && guard++ < 400000) {
    let ins = null;
    try { ins = Instruction.parse(a); } catch (e) { a = a.add(1); continue; }
    if (ins.mnemonic === "lea" && ins.opStr.indexOf("rip") >= 0) {
      const m = /rip ([+-]) (0x[0-9a-f]+)/.exec(ins.opStr);
      if (m) {
        const off = parseInt(m[2], 16) * (m[1] === "-" ? -1 : 1);
        const target = ins.address.add(ins.size).add(off);
        for (const t of targets) {
          if (target.equals(t.addr)) refs.push({ at: ins.address.toString(), to: t.tag });
        }
      }
    }
    a = ins.address.add(ins.size);
  }
  return refs;
}

try {
  const targets = [];
  for (const s of ANCHORS) for (const a of anchorAddrs[s]) targets.push({ addr: a, tag: s });
  if (targets.length > 0 && cmpHits.length > 0) {
    const lo = cmpHits[0].address.sub(0x30000);
    const hi = cmpHits[cmpHits.length - 1].address.add(0x10000);
    out({ recon: "lea_refs", refs: leaRefs(lo, hi, targets) });
    for (const r of (leaRefs(lo, hi, targets))) {
      const at = ptr(r.at);
      out({ recon: "lea_ctx", to: r.to, at: r.at, disasm: disasmWindow(at.sub(160), at.add(240)) });
    }
  } else {
    out({ recon: "lea_refs", skipped: "no targets or no cmp hits" });
  }
} catch (e) { out({ recon: "lea_error", err: String(e) }); }

out({ recon: "done" });
