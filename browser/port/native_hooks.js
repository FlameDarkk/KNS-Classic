// KnS-only native fast paths: Com2uS SGL text cache and CFont measuring, installed on the engine after loadElf.
// They replace guest functions with JS bodies of the same result; the engine itself stays game-agnostic.
(function (root) {
'use strict';
const Runtime = root.ApkRuntime || require('../../apk-cpu-engine/runtime.js');
const { LOAD_BASE } = Runtime.helpers;

function install(rt) {
  rt.systemGuard = installSystemGuard(rt);
  const cpu = rt.cpu, r = cpu.r, syms = rt.syms;
  const m8 = cpu.m8, m32 = cpu.m32, u32 = cpu.u32;
  const hookCost = (name, cost, fn) => rt.hookCost(name, cost, fn);
  const cmpStdStr = (sA, sB) => rt.stdStrCmp(sA, sB);

  hookCost('_ZNK16CachedStringLessclERKP12CachedStringS3_', 4, () => {
    const a = u32[(r[1] >>> 0) >>> 2], b = u32[(r[2] >>> 0) >>> 2];
    const szA = m32[(a + 0x20) >>> 2], szB = m32[(b + 0x20) >>> 2];
    if (szA !== szB) { r[0] = szA < szB ? 1 : 0; return; }
    const fA = u32[(a + 0x28) >>> 2], fB = u32[(b + 0x28) >>> 2];
    if (fA !== fB) {
      const fc = cmpStdStr(fA + 4, fB + 4);
      if (fc !== 0) { r[0] = fc < 0 ? 1 : 0; return; }
    }
    const sc = cmpStdStr(a, b);
    if (sc !== 0) { r[0] = sc < 0 ? 1 : 0; return; }
    const wA = m32[(a + 0x18) >>> 2], wB = m32[(b + 0x18) >>> 2];
    if (wA !== wB) { r[0] = wA < wB ? 1 : 0; return; }
    const hA = m32[(a + 0x1c) >>> 2], hB = m32[(b + 0x1c) >>> 2];
    r[0] = hA < hB ? 1 : 0;
  });

  // Fast-path for SGL_grpCreateStringImageInRect: check the LRU cache at 0x30499c without heap/STL allocations.
  // The cache's address is a static of the KnS 1.4.0 libgame.so; another build needs it looked up again.
  const sglStrCache = (LOAD_BASE + 0x20499c) >>> 0;
  const origCreateStrAddr = syms.get('SGL_grpCreateStringImageInRect');
  if (origCreateStrAddr) {
    const origCreateStr = cpu.compile(origCreateStrAddr);
    cpu.tab[(origCreateStrAddr >>> 0) - cpu.codeBase] = () => {
      const width = r[0] | 0, strPtr = r[1] >>> 0, strLen = r[2] | 0, font = r[3] >>> 0;
      const wrap = m32[(r[13] >>> 0) >>> 2] | 0;
      const rootNode = u32[(sglStrCache + 4) >>> 2];
      if (rootNode && font && strPtr) {
        let e = strPtr;
        while (m8[e]) e++;
        const wantLen = (e - strPtr) >>> 0;
        if (strLen < 0 || (strLen >>> 0) === wantLen) {
          const wantSize = m32[(font + 0x1c) >>> 2];
          // Search red-black tree at sglStrCache
          let cur = rootNode, cand = sglStrCache;
          while (cur) {
            const c = u32[(cur + 0x10) >>> 2];
            let less = false;
            const cSize = m32[(c + 0x20) >>> 2];
            if (cSize !== wantSize) {
              less = cSize < wantSize;
            } else {
              const cFont = u32[(c + 0x28) >>> 2];
              const fc = cFont === font ? 0 : cmpStdStr(cFont + 4, font + 4);
              if (fc !== 0) {
                less = fc < 0;
              } else {
                const cStart = u32[(c + 0x14) >>> 2], cLen = (u32[(c + 0x10) >>> 2] - cStart) >>> 0;
                const minL = cLen < wantLen ? cLen : wantLen;
                let sc = 0;
                for (let i = 0; i < minL; i++) {
                  sc = m8[cStart + i] - m8[strPtr + i];
                  if (sc !== 0) break;
                }
                if (sc === 0) sc = cLen < wantLen ? -1 : cLen > wantLen ? 1 : 0;
                if (sc !== 0) {
                  less = sc < 0;
                } else {
                  const cW = m32[(c + 0x18) >>> 2];
                  if (cW !== width) less = cW < width;
                  else less = m32[(c + 0x1c) >>> 2] < wrap;
                }
              }
            }
            if (!less) {
              cand = cur;
              cur = u32[(cur + 8) >>> 2];
            } else {
              cur = u32[(cur + 12) >>> 2];
            }
          }
          if (cand !== sglStrCache) {
            const c = u32[(cand + 0x10) >>> 2];
            if (m32[(c + 0x20) >>> 2] === wantSize && m32[(c + 0x18) >>> 2] === width && m32[(c + 0x1c) >>> 2] === wrap) {
              const cFont = u32[(c + 0x28) >>> 2];
              if (cFont === font || cmpStdStr(cFont + 4, font + 4) === 0) {
                const cStart = u32[(c + 0x14) >>> 2], cLen = (u32[(c + 0x10) >>> 2] - cStart) >>> 0;
                if (cLen === wantLen) {
                  let match = true;
                  for (let i = 0; i < cLen; i++) {
                    if (m8[cStart + i] !== m8[strPtr + i]) { match = false; break; }
                  }
                  if (match) {
                    // Move to back of LRU list at sglStrCache + 0x18
                    const head = (sglStrCache + 0x18) >>> 0;
                    let lnode = u32[head >>> 2];
                    while (lnode !== head && u32[(lnode + 8) >>> 2] !== c) lnode = u32[lnode >>> 2];
                    const next = u32[lnode >>> 2];
                    if (lnode !== head && next !== head) {
                      const prev = u32[(lnode + 4) >>> 2];
                      u32[prev >>> 2] = next;
                      u32[(next + 4) >>> 2] = prev;
                      const tail = u32[(head + 4) >>> 2];
                      u32[tail >>> 2] = lnode;
                      u32[(lnode + 4) >>> 2] = tail;
                      u32[lnode >>> 2] = head;
                      u32[(head + 4) >>> 2] = lnode;
                    }
                    r[0] = u32[(c + 0x24) >>> 2];
                    r[15] = r[14];
                    return 8;
                  }
                }
              }
            }
          }
        }
      }
      return origCreateStr();
    };
    // A hook like cpu.hook's: regions leave it out and a code flush keeps it.
    cpu.tab[(origCreateStrAddr >>> 0) - cpu.codeBase].hooked = true;
  }

  // Native C++ fast-path for jGetStringWidthHeightInRect(char* str, int len, int size, int style, int width, int* outW, int* outH, int wrap)
  hookCost('jGetStringWidthHeightInRect', 8, () => {
    const strPtr = r[0] >>> 0, len = r[1] | 0, size = r[2] | 0, style = r[3] | 0;
    if (len <= 0) { r[0] = 0; return; }
    const sp = r[13] >>> 0;
    const width = m32[sp >>> 2], outW = u32[(sp + 4) >>> 2], outH = u32[(sp + 8) >>> 2], wrap = m32[(sp + 12) >>> 2];
    const cfont = rt.java && rt.java.classes && rt.java.classes['com/com2us/wrapper/font/CFont'];
    if (cfont && cfont.getStringWidthHeightInRect) {
      const sub = m8.subarray(strPtr, strPtr + len);
      const out = [0, 0];
      cfont.getStringWidthHeightInRect(sub, size, style, width, wrap, out);
      if (outW) m32[outW >>> 2] = out[0];
      if (outH) m32[outH >>> 2] = out[1];
      r[0] = 0;
    }
  });

  // Top bar stamina timer: when stamina >= max, DrawUIFrameTop writes the literal "00:00" and draws
  // it (fn+0x7e0: ldr r3,=0x303a3030 ... b fn+0x488). Restoration choice: skip that text draw so
  // the timer is hidden while full or over full, and resume at fn+0x4b2 (after the timer draw and
  // its flush) with r6 = the draw table the skipped block would have loaded ([r5 + [sp+0x28]]).
  // The countdown path for stamina < max is untouched. Thumb PCs carry the low bit.
  const frameTop = syms.get('DrawUIFrameTop');
  if (frameTop) {
    const fn = (frameTop >>> 0) & ~1;
    if (u32[(fn + 0x8f8) >>> 2] === 0x303a3030) {
      cpu.hook((fn + 0x7e0) | 1, () => {
        const sp = r[13] >>> 0;
        r[6] = u32[((r[5] >>> 0) + u32[(sp + 0x28) >>> 2]) >>> 2];
        r[15] = (fn + 0x4b2) | 1;
      });
    }
  }

  // The engine looks a hook up only where a basic block starts (a function entry, a branch target or the return
  // address after a call); an address inside a block runs as compiled code and never reaches the hook. Mid-function
  // hooks below therefore sit on a block start and run the instructions they replace up to the point of interest.

  // Event dungeon calendar: RefreshEventStagelist (re-run every 2 s) and the stage screens read event windows from
  // stage-package table 2 rows (736 bytes, stage id at +0): start year/month/day/hour KST at +356, end at +372, open
  // hours at +388 and repeat hours at +392 (GetTimeFromDate subtracts 9 h). The server admits entries by its own
  // master_event_schedule, which Studio edits, so the page polls /api/events into root.KnsEventSchedule
  // ({ rows: Map(stage -> [start, end, duration, period] unix seconds) }) and this writes those rows over the
  // table each time GetStageDataInPackage hands it out. Hook at fn+0x30 (0xf4d4c, return from bl ByteToWord):
  // adds r5, #6; str r0, [r6] (count); adds r0, r4, r5 (rows); then pop at fn+0x36. r7 = table + 3.
  // Rows the server does not list keep their bundled dates.
  const stageData = syms.get('GetStageDataInPackage');
  if (stageData) {
    const fn = (stageData >>> 0) & ~1;
    if (u32[(fn + 0x30) >>> 2] === 0x60303506 && (u32[(fn + 0x34) >>> 2] & 0xffff) === 0x1960) {
      const kst = t => { const d = new Date((t + 32400) * 1000); return [d.getUTCFullYear(), d.getUTCMonth() + 1, d.getUTCDate(), d.getUTCHours()]; };
      cpu.hook((fn + 0x30) | 1, () => {
        r[5] = (r[5] + 6) | 0;
        m32[(r[6] >>> 0) >>> 2] = r[0];
        const count = r[0], rows = (r[4] + r[5]) >>> 0;
        r[0] = rows;
        r[15] = (fn + 0x36) | 1;
        const schedule = root.KnsEventSchedule;
        if (r[7] !== 5 || !schedule) return;
        for (let i = 0; i < count; i++) {
          const row = rows + i * 736, entry = schedule.rows.get(m32[row >>> 2]);
          if (!entry) continue;
          const [start, end, duration, period] = entry;
          const from = kst(Math.floor(start / 3600) * 3600), to = kst(Math.ceil(end / 3600) * 3600);
          for (let k = 0; k < 4; k++) { m32[(row + 356 + k * 4) >>> 2] = from[k]; m32[(row + 372 + k * 4) >>> 2] = to[k]; }
          m32[(row + 388) >>> 2] = Math.max(1, Math.round(duration / 3600));
          m32[(row + 392) >>> 2] = Math.max(1, Math.round(period / 3600));
        }
      });
    }
  }

  // Chat UI: DrawChattingPopup draws a horizontal blue separator line above the chat messages at fn+0x25c
  // (0x130074: blx r5 -> DrawLine(30, y, screenWidth-30, y)). That call sits inside the block starting at fn+0x234
  // (0x13004c, return from the previous blx r5), so the hook runs that block's register setup and resumes after the
  // skipped call at fn+0x25e.
  const chatPopup = syms.get('DrawChattingPopup');
  if (chatPopup) {
    const fn = (chatPopup >>> 0) & ~1;
    if (u32[(fn + 0x234) >>> 2] === 0x23809a0e && u32[(fn + 0x238) >>> 2] === 0x58e1005b
        && u32[(fn + 0x25c) >>> 2] === 0x6d2447a8 && u32[(fn + 0x308) >>> 2] === 0xf5c) {
      cpu.hook((fn + 0x234) | 1, () => {
        const sp = r[13] >>> 0, r4 = r[4] >>> 0;
        const r1 = (m32[(r4 + 0x100) >>> 2] - 0x46) | 0;         // ldr r1, [r4, #0x100]; subs r1, #0x46
        const r3 = m32[((r[6] >>> 0) + u32[(sp + 0x38) >>> 2]) >>> 2] >>> 0; // ldr r2, [sp, #0x38]; ldr r3, [r6, r2]
        const y = (r1 + Math.imul(-0x23, m32[r3 >>> 2])) | 0;   // ldr r0, [r3]; r3 = r1 + -0x23 * r0
        r[7] = 0xf5c;                                            // ldr r7, =0xf5c
        r[5] = m32[(r4 + 0x88) >>> 2];                           // ldr r5, [r4, #0x88]
        r[2] = (m32[(r4 + 0xfc) >>> 2] - 0x1e) | 0;             // ldr r2, [r4, #0xfc]; subs r2, #0x1e
        r[3] = y; r[1] = y; r[0] = 0x1e;
        r[15] = (fn + 0x25e) | 1;                                // skip blx r5 (DrawLine)
      });
    }

    // Pinned notice wrap: the pinned line is split at fn+0x56e..0x582 (0x130386: SplitLineStringEx(src, 480,
    // g_NoticelineString)), so a notice longer than 480 px wraps into a block over the lobby buttons. Restoration
    // choice: wrap at the chat's own width (screenWidth - 60, the separator's 30..W-30). fn+0x56e follows an
    // unconditional branch, so it is a block start; the hook runs the block's setup and makes the call itself.
    const split = syms.get('SplitLineStringEx');
    const h16 = a => cpu.m16[a >>> 1];
    if (split && h16(fn + 0x56e) === 0x9e05 && h16(fn + 0x570) === 0x9f07 && h16(fn + 0x572) === 0x9a0c
        && h16(fn + 0x574) === 0x2380 && h16(fn + 0x57a) === 0x21f0 && h16(fn + 0x57c) === 0x18d0) {
      cpu.hook((fn + 0x56e) | 1, () => {
        const sp = r[13] >>> 0;
        const got = u32[(sp + 0x14) >>> 2] >>> 0;                // ldr r6, [sp, #0x14]
        const lines = u32[(got + u32[(sp + 0x1c) >>> 2]) >>> 2]; // ldr r7, [sp, #0x1c]; ldr r4, [r6, r7]
        const grp = u32[(got + u32[(sp + 0x24) >>> 2]) >>> 2] >>> 0;
        const width = (m32[(grp + 0xfc) >>> 2] | 0) - 60;
        r[6] = got; r[7] = u32[(sp + 0x1c) >>> 2]; r[4] = lines;
        r[0] = (u32[(sp + 0x30) >>> 2] + 0x800) | 0;             // ldr r2, [sp, #0x30]; adds r0, r2, #0x800
        r[1] = width > 480 ? width : 480;                        // movs r1, #0xf0; lsls r1, #1
        r[2] = lines;
        r[14] = (fn + 0x586) | 1;                                // return to after the bl
        r[15] = split;
      });
    }
  }

  // Ally "New" badge: CheckNewFellow / DrawInvenFellow read byte +0x70 of each 0x78-byte fellow record. Every reply
  // that carries one fellow_info is parsed by ParsingDataToFellowInfo into a stack buffer that never writes +0x70,
  // then UpdateDataFellowInfo copies all 0x78 bytes over the stored record, so the badge took stack garbage after
  // power-ups, locks, missions, equips. Hook fn+0xc (return from GetFellowInfoByNo; cmp r0,#0; beq fn+0x1a): for an
  // ally already listed, keep its stored badge before the copy. A new ally keeps the native path untouched.
  const updFellow = syms.get('UpdateDataFellowInfo');
  if (updFellow) {
    const fn = (updFellow >>> 0) & ~1, h16 = a => cpu.m16[a >>> 1];
    if (h16(fn + 0xc) === 0x2800 && h16(fn + 0xe) === 0xd004 && h16(fn + 0x10) === 0x1c21 && h16(fn + 0x12) === 0x2278) {
      cpu.hook((fn + 0xc) | 1, () => {
        const stored = r[0] >>> 0;
        if (!stored) { r[15] = (fn + 0x1a) | 1; return; }
        m8[(r[4] >>> 0) + 0x70] = m8[stored + 0x70];
        r[15] = (fn + 0x10) | 1;
      });
    }
  }

  // Pinned chat notice slot (chattingServer: text +0x4468, 512 bytes up to the second slot; start/end unix seconds as
  // int64 at +0x4200/+0x4218; flag +0x40f5, 0 = shown, set to 1 by ProcessChattingView once time() passes the end).
  // The client fills it only from a chat type-10 position-2 message, which also adds a chat line, so the page polls
  // /api/pins and calls KnsChatPin.apply with the newest active pin (or null): Studio edits and removals then show
  // without a new chat line. chattingServer = [GOT + 0x1aec] (NetSocketReceiveProc literal 0x162e68); the GOT base
  // comes from ProcessChattingView's PIC load (fn+0x14 ldr r4, =0xf8c2a; fn+0x1a add r4, pc).
  const chatView = syms.get('ProcessChattingView'), netRecv = syms.get('NetSocketReceiveProc');
  if (chatView && netRecv) {
    const fn = (chatView >>> 0) & ~1, recv = (netRecv >>> 0) & ~1;
    if (cpu.m16[(fn + 0x1a) >>> 1] === 0x447c && u32[(recv + 0xd88) >>> 2] === 0x1aec) {
      const got = (fn + 0x1a + 4 + u32[(fn + 0x274) >>> 2]) >>> 0;
      const encoder = new TextEncoder();
      root.KnsChatPin = {
        apply(pin) {
          const chat = u32[(got + 0x1aec) >>> 2] >>> 0;
          if (!chat) return false;
          if (!pin) { m8[chat + 0x40f5] = 1; return true; }
          let bytes = encoder.encode(String(pin.text));
          if (bytes.length > 511) {
            let n = 511;
            while (n > 0 && (bytes[n] & 0xc0) === 0x80) n--;      // keep whole UTF-8 characters
            bytes = bytes.subarray(0, n);
          }
          m8.set(bytes, chat + 0x4468);
          m8[chat + 0x4468 + bytes.length] = 0;
          const put64 = (at, v) => { m32[at >>> 2] = v | 0; m32[(at + 4) >>> 2] = Math.floor(v / 4294967296) | 0; };
          put64(chat + 0x4200, pin.start);
          put64(chat + 0x4218, pin.end);
          m8[chat + 0x40f5] = 0;
          return true;
        },
      };
    }
  }

}

// g_System holds the kernel's function table: InitSystem@0x18286c fills +0x74..+0xd8 once at startup
// (+0xb8 = C2S_System_GetMultiEventNumber). ker_ConvertEvent@0x176410 calls [g_System+0xb8] on every
// touch (blx r3 @0x176542), so a zeroed slot crashes the page with "jump outside code: 0x0
// lr=0x276545" (seen live on the Collection of Allies screen, 2026-11-23). What overwrites the slot
// is not found yet; until it is, check the table before each event, log the change with the recent
// code blocks, and put the startup values back.
const SYSTEM_TABLE = { first: 0x74, last: 0xd8 };
function installSystemGuard(rt) {
  const cpu = rt.cpu, u32 = cpu.u32, syms = rt.syms;
  const table = syms.get('g_System'), convert = syms.get('ker_ConvertEvent');
  if (!table || !convert) return null;
  const slots = [];
  for (let off = SYSTEM_TABLE.first; off <= SYSTEM_TABLE.last; off += 4) slots.push(off);
  const state = { table: table >>> 0, snapshot: null, repairs: [] };
  const read = () => slots.map(off => u32[(state.table + off) >>> 2] >>> 0);
  state.check = () => {
    const now = read();
    if (!state.snapshot) {
      if (now.every(v => v)) state.snapshot = now;
      return 0;
    }
    let fixed = 0;
    for (let i = 0; i < slots.length; i++) {
      if (now[i] === state.snapshot[i]) continue;
      const recent = [];
      for (let k = 0; k < 16; k++) recent.push('0x' + (cpu.ring[(cpu.ringPos - k) & 255] >>> 0).toString(16));
      const repair = { offset: slots[i], was: now[i], restored: state.snapshot[i], recent };
      state.repairs.push(repair);
      if (state.repairs.length > 50) state.repairs.shift();
      rt.log(`g_System+0x${slots[i].toString(16)} changed to 0x${now[i].toString(16)}; restored 0x${state.snapshot[i].toString(16)} (recent ${recent.join(' ')})`);
      u32[(state.table + slots[i]) >>> 2] = state.snapshot[i];
      fixed++;
    }
    return fixed;
  };
  const original = cpu.compile(convert);
  cpu.hook(convert, () => { state.check(); return original(); });
  return state;
}

const KnsNativeHooks = { install, installSystemGuard, SYSTEM_TABLE };
root.KnsNativeHooks = KnsNativeHooks;
if (typeof module !== 'undefined') module.exports = KnsNativeHooks;
})(typeof globalThis !== 'undefined' ? globalThis : this);
