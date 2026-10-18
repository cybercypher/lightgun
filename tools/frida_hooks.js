'use strict';
// guncon2 live instrumentation (pcsx2-qtx64.exe) — addresses from recon 2026-10-18.
// Two hooks + a 100ms state poll; nothing is modified, only observed.
// Verify byte prefixes before hooking: same process as recon, base unchanged.

const HANDLE_DATA = ptr("0x7ff770c2c690");   // guncon2_handle_data entry; rcx = GunCon2State* us
const SETPARAM_BODY = ptr("0x7ff770c2c609"); // Set Param branch body; rsi = us, rdi = data
const SETPARAM_NEXT = ptr("0x7ff770c2c60c"); // movzx ecx, byte ptr [rdi+1] (second insn of branch)

const F = {
  has_rel: 0x1704, custom_cfg: 0x1705,
  screen_w: 0x1710, screen_h: 0x1714,
  center_x: 0x1718, center_y: 0x171c,
  scale_x: 0x1720, scale_y: 0x1724,
  button_state: 0x1728,
  rel: [0x1758, 0x175c, 0x1760, 0x1764],
  param_x: 0x1768, param_y: 0x176a, param_mode: 0x176c,
  calib_timer: 0x176e, calib_pos_x: 0x1770, calib_pos_y: 0x1772,
  auto_done: 0x1774,
};

function out(o) { send(o); }

function checkBytes(p, expect, tag) {
  for (let i = 0; i < expect.length; i++) {
    if (p.add(i).readU8() !== expect[i]) {
      out({ event: "verify_fail", tag: tag, at: p.toString(), got: p.add(i).readU8(), want: expect[i] });
      return false;
    }
  }
  return true;
}

const okData = checkBytes(HANDLE_DATA, [0x41, 0x56, 0x56, 0x57, 0x53], "handle_data");        // push r14;push rsi;push rdi;push rbx
const okParam = checkBytes(SETPARAM_BODY, [0x0f, 0xb6, 0x07], "set_param") &&                  // movzx eax, byte ptr [rdi]
                 checkBytes(SETPARAM_NEXT, [0x0f, 0xb6, 0x4f, 0x01], "set_param_next");         // movzx ecx, byte ptr [rdi+1]
out({ event: "verify", handle_data: okData, set_param: okParam });

let us = null;
let dataCalls = 0;
let lastBtn = -1;
let lastPx = null, lastPy = null, lastPm = null;

if (okData) {
  Interceptor.attach(HANDLE_DATA, {
    onEnter: function (args) {
      if (us === null) {
        us = args[0];
        out({ event: "us_found", us: us.toString(),
              has_rel: us.add(F.has_rel).readU8(), custom_cfg: us.add(F.custom_cfg).readU8(),
              screen: [us.add(F.screen_w).readU32(), us.add(F.screen_h).readU32()],
              center: [us.add(F.center_x).readFloat(), us.add(F.center_y).readFloat()],
              scale: [us.add(F.scale_x).readFloat(), us.add(F.scale_y).readFloat()] });
      }
      dataCalls++;
    }
  });
}

if (okParam) {
  Interceptor.attach(SETPARAM_BODY, {
    onEnter: function () {
      try {
        const d = this.context.rdi;
        const b = [];
        for (let i = 0; i < 6; i++) b.push(d.add(i).readU8());
        const px = (b[0] | (b[1] << 8)) << 16 >> 16;
        const py = (b[2] | (b[3] << 8)) << 16 >> 16;
        const pm = b[4] | (b[5] << 8);
        out({ event: "set_param", t: (Date.now() / 1000).toFixed(3), param_x: px, param_y: py, param_mode: pm, raw: b });
      } catch (e) { out({ event: "set_param_error", err: String(e) }); }
    }
  });
}

setInterval(function () {
  try {
    if (us === null) return;
    const btn = us.add(F.button_state).readU32();
    const px = us.add(F.param_x).readS16();
    const py = us.add(F.param_y).readS16();
    const pm = us.add(F.param_mode).readU16();
    if (btn !== lastBtn || px !== lastPx || py !== lastPy || pm !== lastPm) {
      out({ event: "state", t: (Date.now() / 1000).toFixed(3),
            btn: btn.toString(16), btnbits: {
              trigger: (btn >> 13) & 1, recalibrate: (btn >> 17) & 1, offscreen: (btn >> 16) & 1,
              a: (btn >> 3) & 1, b: (btn >> 2) & 1, start: (btn >> 15) & 1 },
            px: px, py: py, pm: pm.toString(16),
            ct: us.add(F.calib_timer).readU16(),
            rel: [us.add(F.rel[0]).readFloat(), us.add(F.rel[1]).readFloat(),
                  us.add(F.rel[2]).readFloat(), us.add(F.rel[3]).readFloat()] });
      lastBtn = btn; lastPx = px; lastPy = py; lastPm = pm;
    }
  } catch (e) { out({ event: "poll_error", err: String(e) }); us = null; }
}, 100);

out({ event: "hooks_ready", data_hook: okData, param_hook: okParam });
