// Headless native test harness: runs the browser build's libgame.so under Node so game logic can be
// asserted on without the browser pane or screenshots. Test-only; never loaded by the page.
//
//   const Headless = require('../headless.js');
//   const h = Headless.game();      // full startup (packages loaded by the game's own code), cached per process
//   const h = Headless.bare();      // ELF + native hooks only, no startup: fastest, globals are zeroed
//   h.call('GetStageDataByID', 3000001)
//
// KnS symbols, addresses and struct offsets live here (LAYOUT), not in apk-cpu-engine/. Every raw
// address a test relies on must be pinned with expectCall()/expectLiteral() so an ELF where the code
// moved fails loudly instead of testing the wrong bytes.
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

const ROOT = path.resolve(__dirname, '..', '..');
// g_ui16CurrentScene values seen headless (handler names from the Scene_*_SC_* symbols).
const SCENE = { TITLE: 3, MAP: 4, FIELD: 6, HUB: 17 };
const STOP = Symbol('stop');

// Image-relative. Evidence: disassembly of the ELF with sha256 a6f81a1f...28ef8d (2026-11-15).
const LAYOUT = {
  got: 0x1fb838,                 // GetEquipItemOptionValueByCategory: add sl,pc @0xf9d5e + lit@0xf9e5c
  gotGame: 0x1a8c,               // [got+0x1a8c] = game info (packages, account)
  gotFight: 0x1aec,              // [got+0x1aec] = fight/map state (combo, chat)
  packages: {                    // package objects inside game info: [P] = table count, [P+0xc] = data
    itemOption: 0x1f58,          // GetEquipItemOptionValueByCategory @0xf9d80
    shop: 0x21e0,                // UpdateUserInfo @0x14ad88 -> GetShopStaminaData
    stage: 0x2228,               // GetStageDataByID @0xf504c
  },
  fight: {
    combo: 0x31e8,               // secure value; SetDamageCharToMon lit@0x145214, CheckCombo, DrawCombo
    comboMark: 0x31ec,           // plain int; only SetDamageCharToMon uses it (lit@0x145218)
  },
  hero: { equipOptions: 0xa20 }, // SetDamageCharToMon @0x144f0c: r7 = hero + 0xa20
  achievementItem: {
    itemLookupCall: 0xb9780, hpReadCall: 0xb97fc, hpDiffReadCall: 0xb980c,
    hpLiteral: 0xb9940, hpGot: 0xc68, hpDiffLiteral: 0xb9944, hpDiffGot: 0x1720,
    equippedOffset: 0x74,
  },
  optionPreview: { start: 0x12dec8, end: 0x12db6c }, // DrawEquItemStatusWithParam option gains
  // achievement-sync.js: the native 122 reply parses each record (0x30 bytes) and upserts it into the finish list.
  achievementSync: { parseCall: 0x15c214, finishLoopCall: 0x153b10, login121Request: 0x153068 },
  fastPowerUp: {
    detailDrawCall: 0xade3c, pickerDrawCall: 0xade0a,
    detailOpen: 0x958, detailOpenLiteral: 0x1335e4,
    detailFrame: 0x13546e, detailInset: 0x1354a0, tutorialFlagLiteral: 0x130b28, tutorialFlag: 0x3384,
    applyGoodsCall: 0x154054, applyFellowCall: 0x15405c, applyGearCall: 0x15406a,
    questCall: 0x15408e, questOffsetLiteral: 0x154338, questOffset: 0x24b0,
    deleteMaterialCall: 0xa666c, refreshListCall: 0xa667c,
    evolutionAwakenCall: 0xad528, evolutionAwakenOffset: 0x34,
  },
  stageDropPopup: {
    weeklyPreviewCall: 0x6c06c, // event Available Items builds temporary weekly reward lists
    previewSortCall: 0x700de,
    stageDrawCall: 0x72ab6, helperDrawCall: 0x74fc6, boxDrawCall: 0x6979c,
    weeklyListLiteral: 0x69ac8, weeklyListOffset: 0x19b4, fontLiteral: 0x69aa8, fontValue: 0x15e89e,
  },
  stamina: {
    upgradeCall: 0x14ad90, shopLiteral: 0x14ae8c, shopOffset: 0x21e0,
    refillUserCall: 0x15d870, replyCurrent: 0x144,
    replyBase: 0x148, replyUpgrade: 0x158, displayedMax: 0xb50,
  },
  raidEntry: {
    guard: 0xe28dc, allowed: 0xe28f2, // Scene_PointerReleaseEvent_SC_RAID_READY
    used: 0x2564, limit: 0x2934,    // fight used count; gameInfo configured daily limit
    gameLiteral: 0xe2984, usedLiteral: 0xe2988, limitLiteral: 0xe298c,
    exhaustedTextLiteral: 0xe3188, exhaustedTextCall: 0xe2ed6, exhaustedText: 9999012,
  },
  arenaParty: {
    parseStart: 0x15cc32, parseFellowCall: 0x15cc66, nextFieldCall: 0x15cc7a,
    partyLiteral: 0x15cf28, replyParty: 0xcdb8, stride: 0x78,
    createStart: 0x4e0c8, initFellowCall: 0x4e160, destroyCall: 0x4e10e,
    fightLiteral: 0x4e238, spawnedLiteral: 0x4e23c, spawnedOffset: 0x231c,
    friendParty: 0x740, fellowId: 0x20, parseIdCall: 0x14d124,
  },
  mailClaim: {
    fellowStart: 0x15d9f8, fellowCall: 0x15da08,
    fellowOffsetLiteral: 0x15db50, fellowOffset: 0xc9f8,
  },
  systemTable: {                  // ker_ConvertEvent: ldr r0,[got+0x774] (g_System); ldr r3,[r0,#0xb8]; blx r3
    convertLiteral: 0x1766d0, gotOffset: 0x774, touchSlot: 0xb8,
    pointerPressLiteral: 0x1766c0, pointerPress: 0x80000004,
    initLiteral: 0x1829bc, initGot: 0x1874, // InitSystem: [g_System+0xb8] = got[0x1874] (GetMultiEventNumber)
  },
  bookClaim: {
    start: 0x7db7c, requestCall: 0x7dba0, epilogueLiteral: 0x7bf98, frame: 0x508c,
    gems: 0xaf0, // UpdateUserGoods @0x14a4d4..0x14a4e2: parsed cash enters fight+0xaf0
  },
  gotReply: 0x1964,              // JanssonParser lit@0x15a618: [got+0x1964] = reply struct
  arenaLimitedReward: {
    // DrawRankingReward @0xdf120: draws fellow face + grade stars for item_id 100000..199999
    drawFellowLookupCall: 0xdf1ce, drawFellowFaceCall: 0xdf1fe, drawGradeStarCall: 0xdf212,
    drawEtcItemCall: 0xdf18c,
    // Protocol 405 parser @0x15eca0..0x15ee22: pvp_ranking_reward_info -> reply+0xc450 (count) / +0xc45c (rows, stride 0x18)
    parse405Start: 0x15eca0, parseRewardRowCall: 0x15ed80, parseExit: 0x15a3cc,
    countLiteral: 0x15ef04, countOffset: 0xc450, rowsLiteral: 0x15ef08, rowsOffset: 0xc45c, rowStride: 0x18,
  },
  bookOfSecrets: {
    // Protocol 311 parser @0x15f7f0..0x15f87c (+ type 2 branch @0x15f91e): buff_etc_info & use_item_no
    parse311Start: 0x15f7f0, parseItemNoCall: 0x15f86e, parseExit: 0x15a3cc,
    expBuffEndLiteral: 0x15fb30, expBuffEndOffset: 0x3fb8,
    useItemNoLiteral: 0x15fb1c, useItemNoOffset: 0xc4b0,
    // Protocol 311 NetworkEvent @0x1530fc..0x15313a: deletes use_item_no & sets fight+0x33d7 = 1
    ne311Start: 0x1530fc, lookupItemCall: 0x153118, neExit: 0x15100a,
    redrawFlagLiteral: 0x15327c, redrawFlagOffset: 0x33d7,
    // DrawResult EXP display @0xe7b42..0xe7c6e: computes base + (base / 2) when fight+0x3fb8 > server_now
    drawExpStart: 0xe7b42, sprintfCall: 0xe7c6a,
    expEndLiteral: 0xe7be8, syncMsLiteral: 0xe7bec, syncMsOffset: 0x3238,
    serverSecLiteral: 0xe7fc8, serverSecOffset: 0x3230,
  },
  tutorialChain: {
    // Protocols 603 (@0x15d60e) & 602 (@0x15d65c): parse quest_infos[0] into reply+0xc420 (quest_id at +0xc428)
    parse603Start: 0x15d60e, parse602Start: 0x15d65c, parseQuestCall603: 0x161872, parseQuestCall602: 0x161842,
    questOffsetLiteral: 0x161aa8, questOffset: 0xc420, questIdOffset: 0xc428, parseExit: 0x15a3cc,
    // Protocol 603 NetworkEvent @0x153456: reads reply+0xc428 -> StartTutorial(quest_id) @0x15a0ac or clear @0x159b16
    ne603Dispatch: 0x153456, questIdLiteral: 0x15367c, startTutorialCall: 0x15a0ac,
    activeQuestLiteral: 0x159dd0, activeQuestOffset: 0x338c, neExit: 0x15100a,
  },
};

function readElf() {
  if (process.env.KNS_NATIVE_TEST_ELF) return new Uint8Array(fs.readFileSync(process.env.KNS_NATIVE_TEST_ELF));
  const html = require('./node_boot.js').html;
  const marker = 'window.KNS_ELF="', at = html.indexOf(marker) + marker.length;
  return new Uint8Array(Buffer.from(html.slice(at, html.indexOf('";', at)), 'base64'));
}

class Harness {
  constructor(rt, game, elf) {
    this.rt = rt; this.cpu = rt.cpu; this.r = rt.cpu.r; this.gameHost = game || null;
    this.base = require('../../apk-cpu-engine/runtime.js').helpers.LOAD_BASE;
    this.got = this.base + LAYOUT.got;
    this.elfSha256 = crypto.createHash('sha256').update(elf).digest('hex');
    this.L = LAYOUT;
    this.STOP = STOP;
    this.maxBatches = 50;        // x 1e6 guest instructions per call
    this.stubs = [];             // names replaced by stub(); report them so a test stays honest
    this.stubCalls = [];         // { name, args: [r0..r3] } in call order
    this.logs = [];
  }

  // ---- addresses ----
  sym(name) {
    const a = this.rt.syms.get(name);
    if (!a) throw new Error('no native symbol ' + name);
    return a >>> 0;
  }
  // 'Name' -> symbol (Thumb bit kept); a number is always an image-relative Thumb address, as
  // printed by tools/analyze/disasm.py.
  addr(target) {
    if (typeof target === 'string') return this.sym(target);
    return ((this.base + target) | 1) >>> 0;
  }
  get game() { return this.u32(this.got + LAYOUT.gotGame); }
  get fight() { return this.u32(this.got + LAYOUT.gotFight); }

  // ---- memory ----
  u32(a) { return this.cpu.ru32(a) >>> 0; }
  i32(a) { return this.cpu.ru32(a) | 0; }
  w32(a, v) { this.cpu.wu32(a, v); }
  ints(a, n) { return Array.from({ length: n }, (_, i) => this.i32(a + i * 4)); }
  bytes(a, n) { return Buffer.from(this.cpu.m8.subarray(a, a + n)); }
  cstr(a) { return this.rt.cstr(a); }
  alloc(n) { return this.rt.calloc(n); }

  // ---- calling native code ----
  _sync(addr, args) {
    const cpu = this.cpu, run = cpu.run;
    let batches = 0;
    cpu.run = budget => {
      if (++batches > this.maxBatches) throw new Error('native instruction budget exceeded at 0x' + (this.r[15] >>> 0).toString(16));
      return run.call(cpu, Math.min(budget, 1000000));
    };
    try { return this.rt.callSync(addr, ...args); } finally { delete cpu.run; }
  }
  // Call a whole native function by symbol name or image-relative address. Returns r0 as a signed int.
  call(target, ...args) { return this._sync(this.addr(target), args) | 0; }
  ptr(target, ...args) { return this.call(target, ...args) >>> 0; }

  // Run part of a function: enter at `start` with chosen registers and stack slots, and run until a
  // tap() returns h.STOP. Returns { r: registers at the stop, sp: the fragment's stack pointer }.
  // regs/frame may be functions of sp. frame maps stack offset -> 32-bit value.
  // returnAt: stack offset of the saved pc the function's own epilogue will pop; the fragment then
  // ends by returning normally (no STOP tap needed) and the result has r = null.
  fragment({ start, regs = {}, frame = {}, frameSize = 0x400, returnAt = null }) {
    const r = this.r, keep = r[13];
    r[13] = (keep - frameSize) & ~7;
    const sp = (r[13] - 64) & ~7;          // what callSync gives the callee
    try {
      const f = typeof frame === 'function' ? frame(sp) : frame;
      for (const [off, v] of Object.entries(f)) this.w32(sp + +off, v);
      if (returnAt !== null) this.w32(sp + returnAt, this.rt.syncRet);
      const set = typeof regs === 'function' ? regs(sp) : regs;
      for (const [i, v] of Object.entries(set)) if (+i > 3) r[+i] = v;
      this._snap = null;
      this._sync(this.addr(start), [set[0], set[1], set[2], set[3]]);
      if (!this._snap && returnAt === null) throw new Error('fragment returned without reaching a STOP tap');
      return { r: this._snap, sp };
    } finally { r[13] = keep; }
  }

  // Observe a native function without replacing it: fn(r, h) runs at entry, then the real code runs.
  // Return h.STOP from fn to end the current call()/fragment() there (registers are captured).
  // Works on Thumb functions that start with `push {..., lr}`; the push is replayed in JS.
  tap(name, fn) {
    const a = this.sym(name), entry = a & ~1, cpu = this.cpu, r = this.r;
    const op = cpu.m8[entry] | (cpu.m8[entry + 1] << 8);
    if (!(a & 1) || (op & 0xfe00) !== 0xb400) throw new Error('tap(' + name + '): not a Thumb push prologue');
    const regs = [0, 1, 2, 3, 4, 5, 6, 7].filter(i => op & (1 << i));
    if (op & 0x100) regs.push(14);
    cpu.hook(a, () => {
      if (fn(r, this) === STOP) { this._snap = r.slice(); r[15] = this.rt.syncRet; return; }
      const sp = (r[13] - regs.length * 4) >>> 0;
      regs.forEach((x, i) => cpu.wu32(sp + i * 4, r[x]));
      r[13] = sp;
      r[15] = (entry + 2) | 1;
    });
  }

  // Stop a fragment at a pinned branch target without needing a function push prologue.
  // Only use dispatch boundaries (function entries or branch/call targets), never mid-block.
  stopAt(target, observe = () => {}) {
    this.cpu.hook(this.addr(target), () => {
      observe(this.r, this);
      this._snap = this.r.slice();
      this.r[15] = this.rt.syncRet;
    });
  }

  // Replace a side-effect function (draw, sound, effects...) with `return ret`. Every stub is recorded.
  stub(names, ret = 0) {
    for (const name of [].concat(names)) {
      this.sym(name);
      this.stubs.push(name);
      this.rt.hookCost(name, 1, () => {
        const r = this.r;
        this.stubCalls.push({ name, args: [r[0], r[1], r[2], r[3]] });
        r[0] = typeof ret === 'function' ? ret(r, this) : ret;
      });
    }
  }

  // ---- pinning raw addresses to the loaded code ----
  // The Thumb BL/BLX at image-relative `site` must call `name`.
  expectCall(site, name) {
    const a = this.base + site, m = this.cpu.m8;
    const hi = m[a] | (m[a + 1] << 8), lo = m[a + 2] | (m[a + 3] << 8);
    const isBl = (hi & 0xf800) === 0xf000 && (lo & 0xe800) === 0xe800;
    let target = (a + 4 + (((hi & 0x7ff) << 21 >> 9) | ((lo & 0x7ff) << 1))) >>> 0;
    if (!(lo & 0x1000)) target &= ~3;
    const want = this.sym(name) & ~1;
    if (!isBl || target !== want) {
      throw new Error(`native code moved: 0x${site.toString(16)} does not call ${name}; re-verify offsets with tools/analyze/disasm.py`);
    }
  }
  expectLiteral(at, value) {
    const got = this.u32(this.base + at);
    if (got !== value >>> 0) {
      throw new Error(`native code moved: literal at 0x${at.toString(16)} is 0x${got.toString(16)}, expected 0x${(value >>> 0).toString(16)}`);
    }
  }

  // ---- KnS helpers ----
  // Secure values are XORed with a session key ([[GetSecureValue GOT slot]]); only the low word is stored.
  get secureKeyAddr() {
    const fn = this.sym('GetSecureValue') & ~1;
    return this.u32((fn + 6 + this.u32(fn + 0x10)) >>> 0);
  }
  setSecureKey(key) { this.w32(this.secureKeyAddr, key); }
  secure(v) { return this.call('SetSecureValue', v, v >> 31); }
  unsecure(v) { return this.call('GetSecureValue', v, v >> 31); }

  // Table `index` of a loaded data package (LAYOUT.packages.*) through the game's own reader.
  table(pkg, index) {
    const out = this.alloc(0x48);
    const rows = this.ptr('GetDataMngByIndexInPackage', this.game + pkg, index, out);
    const t = { rows, count: this.i32(out), rowSize: this.i32(out + 4) };
    t.row = i => rows + i * t.rowSize;
    return t;
  }

  // A hero's summed equipment options, the structure GetEquipItemOptionValueByCategory reads:
  // [set] = entry count, [set+0xc] = entries {secure value1, secure value2}, one per row of
  // item-option table 0. values: { optionId: value1 | [value1, value2] }.
  equipOptions(values = {}) {
    const t = this.table(LAYOUT.packages.itemOption, 0);
    if (t.count <= 0) throw new Error('item option package is not loaded; use Headless.game()');
    const set = this.alloc(0x10), entries = this.alloc(t.count * 8), zero = this.secure(0);
    for (let i = 0; i < t.count * 2; i++) this.w32(entries + i * 4, zero);
    const index = new Map();
    for (let i = 0; i < t.count; i++) index.set(this.i32(t.row(i)), i);
    for (const [id, v] of Object.entries(values)) {
      if (!index.has(+id)) throw new Error('option ' + id + ' is not in native item-option table 0');
      const [v1, v2] = [].concat(v, 0);
      this.w32(entries + index.get(+id) * 8, this.secure(v1));
      this.w32(entries + index.get(+id) * 8 + 4, this.secure(v2));
    }
    this.w32(set, t.count);
    this.w32(set + 0xc, entries);
    return set;
  }
  // Native lookup: sums value1/value2 over every equipped option of a category ((id / 100) % 10000).
  optionValue(set, category) {
    const out = this.alloc(8);
    const found = this.call('GetEquipItemOptionValueByCategory', set, category, out, out + 4);
    return { found, value1: this.i32(out), value2: this.i32(out + 4) };
  }

  get combo() { return this.unsecure(this.i32(this.fight + LAYOUT.fight.combo)); }
  set combo(n) { this.w32(this.fight + LAYOUT.fight.combo, this.secure(n)); }
  get comboMark() { return this.i32(this.fight + LAYOUT.fight.comboMark); }
  set comboMark(n) { this.w32(this.fight + LAYOUT.fight.comboMark, n); }

  // ---- running the game loop (Headless.boot/game only) ----
  // Current scene id (g_ui16CurrentScene): 17 is the lobby.
  get scene() { const a = this.sym('g_ui16CurrentScene'); return this.cpu.m8[a] | (this.cpu.m8[a + 1] << 8); }
  // Run game ticks; between ticks the event loop turns so scripted replies and timers arrive.
  // Stops early when until(h) is true. Returns the number of ticks run.
  async run(frames, until) {
    const g = this.gameHost;
    for (let i = 0; i < frames; i++) {
      if (until && until(this)) return i;
      if (this.vclock) this.vclock.now += this.tickMs;
      g.frame(2000);
      await new Promise(done => (this.vclock ? setImmediate(done) : setTimeout(done, 16)));
    }
    if (until && !until(this)) throw new Error('run(' + frames + '): condition not reached, scene ' + this.scene);
    return frames;
  }
  get popup() { return this.i32(this.sym('g_i32CurrentPopupScene')); }
  // Native touch tables that currently hold rows: the clickable areas, in scene coordinates.
  // { touchTitle: [{ id, top, bottom, left, right }], ... }. Rows parked off screen are skipped.
  touchAreas(only) {
    const out = {};
    for (const [name, a] of this.rt.syms) {
      if (!/^(touch[A-Z]|g_TouchAnvil$)/.test(name) || (only && name !== only)) continue;
      const count = this.u32(a + 4), rows = this.u32(a + 8);
      if (!rows || !count || count > 256) continue;
      const list = [];
      for (let i = 0; i < count; i++) {
        const [top, bottom, left, right, , id] = this.ints(rows + i * 24, 6);
        if (top > -10000) list.push({ id, top, bottom, left, right });
      }
      if (list.length) out[name] = list;
    }
    return only ? out[only] || [] : out;
  }
  // What the game is drawing, as text: runs `frames` ticks and returns the names of the native
  // Draw*/Scene_Draw*/Pro* functions that executed (sampled from the CPU's block ring), sorted.
  // Use it like a screenshot: 'DrawPopupDailyReward' in the list means that popup is up.
  async screen(frames = 2, pattern = /^(Draw|Scene_Draw|Pro[A-Z])/) {
    if (!this._symIndex) this._symIndex = [...this.rt.syms].map(([n, a]) => [(a & ~1) >>> 0, n]).sort((x, y) => x[0] - y[0]);
    const idx = this._symIndex, cpu = this.cpu, run = cpu.run, seen = new Set();
    const name = pc => {
      let lo = 0, hi = idx.length - 1;
      while (lo < hi) { const m = (lo + hi + 1) >> 1; if (idx[m][0] <= pc) lo = m; else hi = m - 1; }
      return idx[lo][1];
    };
    cpu.run = function (budget) {
      let left = budget, used = 0;
      while (left > 0 && !(used && this.stop)) {
        const n = run.call(this, Math.min(left, 4000));
        for (let i = 0; i < 256; i++) { const pc = this.ring[i] >>> 0; if (pc >= idx[0][0]) seen.add(name(pc)); }
        left -= 4000; used++;
        if (this.stop) break;
      }
    };
    try { await this.run(frames); } finally { delete cpu.run; }
    return [...seen].filter(n => pattern.test(n)).sort();
  }
  // Strings the game rendered since the last call (text is cached natively, so each appears once).
  texts() { const t = this.textLog || []; this.textLog = []; return t; }

  // A tap in scene coordinates (the 1304x712 space of the touch rows): press, a few ticks, release.
  // Events go straight to the current scene (ker_CallCurrentSceneEvent: 2 press, 3 release, r1 = x,
  // r2 = y; Scene_Event_SC_TITLE@0xec454), queued on the Java thread like real input. The wrapper's
  // device-to-scene conversion is skipped: under the null GL it yields INT_MAX for every touch.
  async touch(x, y, hold = 3) {
    const rt = this.rt, event = this.sym('ker_CallCurrentSceneEvent');
    rt.callJava(event, [2, x | 0, y | 0]);
    await this.run(hold);
    rt.callJava(event, [3, x | 0, y | 0]);
    await this.run(2);
  }
  // Tap the centre of a native touch area, e.g. tapArea('touchTitle', 1).
  async tapArea(table, id, hold) {
    const a = this.touchAreas(table).find(row => row.id === id);
    if (!a) throw new Error(`no touch area ${table} #${id} on scene ${this.scene}`);
    await this.touch((a.left + a.right) >> 1, (a.top + a.bottom) >> 1, hold);
  }

  // ---- flows: the shortest proven path through the real UI (needs a Net that answers) ----
  // Title -> guest login -> hub screen (scene 17, SC_POPUP_CHAR), daily reward popup closed.
  async login() {
    await this.run(400, h => h.touchAreas('touchTitle').length > 0);
    await this.run(20);
    await this.tapArea('touchTitle', 1);                         // guest start: Hub guest/create, then 51, 60...
    await this.run(600, h => h.net.protocols.includes(67));
    await this.run(30);
    await this.touch(652, 356);                                  // "touch to start"
    await this.run(600, h => h.scene === SCENE.HUB);
    await this.run(30);
    await this.closeDailyReward();
  }
  // GetTouchDailyID@0x8a848: the calendar's OK is centred at (W/2, H/2+100) while [got+0x10d8] is set,
  // then Close at (W/2-230, H/2+238). fight+0x3850 is the popup flag (release handler @0x91888).
  async closeDailyReward() {
    const view = this.u32(this.got + 0x1430), w = this.i32(view + 0xfc) >> 1, hh = this.i32(view + 0x100) >> 1;
    for (let i = 0; i < 6 && this.cpu.m8[this.fight + 0x3850]; i++) {
      if (this.cpu.m8[this.u32(this.got + 0x10d8)]) await this.touch(w, hh + 100); else await this.touch(w - 230, hh + 238);
      await this.run(30);
    }
    if (this.cpu.m8[this.fight + 0x3850]) throw new Error('daily reward popup did not close');
  }
  // Hub -> dungeon map (scene 4, SC_LOBBY) through the Dungeons button (touchPopChar #6).
  async openDungeons() {
    await this.tapArea('touchPopChar', 6);
    await this.run(300, h => h.scene === SCENE.MAP && h.touchAreas('touchLobby').length > 0);
    await this.run(20);
  }
  // Start a stage from the dungeon map by running the Start button's own code
  // (Scene_PointerReleaseEvent_SC_LOBBY 0x73d96..0x73ec8: fills the request at gameInfo+0x8328, sends
  // protocol 71, sets scenario 6, pushes the loading popup, returns through the epilogue at 0x72fb2).
  // The stage picker and helper message box before it are skipped: the selected stage id global
  // ([[got+0xd40]]) is written directly. Ends in the field (scene 6) with combat touch areas.
  async startStage(stageId) {
    if (this.scene !== SCENE.MAP) throw new Error('startStage needs the dungeon map, scene is ' + this.scene);
    this.expectCall(0x73de0, 'GetStageDataByID');
    this.expectCall(0x73e7c, 'Request');
    this.expectCall(0x73e90, 'ker_SetScenario');
    this.expectLiteral(0x74038, 0xd40);
    this.expectLiteral(0x7325c, 0x5094);
    this.w32(this.u32(this.got + 0xd40), stageId);
    this.fragment({ start: 0x73d96, frameSize: 0x6000, returnAt: 0x5094 + 0x20, regs: { 4: this.got, 5: 0x14c8, 6: LAYOUT.gotFight } });
    await this.run(900, h => h.scene === SCENE.FIELD && h.touchAreas('touchField').length > 0);
  }
  // Press or release a combat button (touchField ids: block 3, attack 5, throw 6, allies 8-10), the
  // way controls.js does: multi-pointer scene events 6 (press) / 7 (release), x << 16 | y.
  combat(id, down, pointer = 1) {
    const a = this.touchAreas('touchField').find(row => row.id === id);
    if (!a) throw new Error('no combat area ' + id);
    const radius = (a.right - a.left) >> 1, packed = ((a.left + radius) << 16) | (a.top + radius);
    this.rt.callJava(this.sym('ker_CallCurrentSceneEvent'), [down ? 6 : 7, pointer, packed]);
  }

  // Parse a JS value with the game's own jansson; returns the json_t pointer for reply-parser fragments.
  json(value) { return this.ptr('json_loads', this.rt.newCstr(JSON.stringify(value)), 0, 0); }
}

// Scripted network: every request the port makes (Hub login, game gateway, downloads) goes through
// the global fetch, so the harness answers it here. No socket is opened unless record() is used.
//   net.on(71, req => ({...}))            answer gateway protocol 71 (an object is sent as JSON)
//   net.on('/hub/guest/create', {...})    answer by URL path
//   net.replay(tape) / net.record(origin) play back, or capture from a disposable backend
// Every exchange is kept in net.log; a request nobody answers fails and is listed in net.unanswered.
// The game gateway body is base64(AES-128-CBC(json)) with constants from libgame.so
// (server-dotnet/src/KnS.Protocol/Crypto/GameGatewayCipher.cs). The log, handlers and tapes use plain JSON.
const GATEWAY = {
  key: Buffer.from('1m!1HUB2@hero#Re'), iv: Buffer.from('de295106d3872f949783942fdc9f01e2', 'hex'),
  is: path => /\/gateway\.php$/.test(path),
  decrypt(base64) {
    const d = crypto.createDecipheriv('aes-128-cbc', GATEWAY.key, GATEWAY.iv);
    d.setAutoPadding(false);
    const b = Buffer.concat([d.update(Buffer.from(base64, 'base64')), d.final()]), pad = b[b.length - 1];
    return b.subarray(0, pad >= 1 && pad <= 16 ? b.length - pad : b.length).toString('utf8');
  },
  encrypt(text) {
    const c = crypto.createCipheriv('aes-128-cbc', GATEWAY.key, GATEWAY.iv);
    return Buffer.concat([c.update(Buffer.from(text, 'utf8')), c.final()]).toString('base64');
  },
};

class Net {
  constructor() { this.log = []; this.unanswered = []; this.handlers = []; this.tape = null; this.queues = null; this.upstream = null; this.realFetch = globalThis.fetch; }
  install() { globalThis.fetch = (url, init) => this.fetch(String(url), init || {}); return this; }
  on(match, reply) { this.handlers.unshift({ match, reply }); return this; }
  record(origin) {
    if (!/^http:\/\/127\.0\.0\.1:\d+$/.test(origin)) throw new Error('record() only talks to a local disposable backend');
    this.upstream = origin; this.tape = [];
    return this;
  }
  replay(tape) {
    this.queues = new Map();
    this.recordedAt = tape.recordedAt || 0;
    for (const e of tape.entries || tape) {
      if (!this.queues.has(e.key)) this.queues.set(e.key, { list: [], next: 0 });
      this.queues.get(e.key).list.push(e);
    }
    return this;
  }
  static describe(url, init) {
    const path = url.replace(/^https?:\/\/[^/]+/, ''), method = init.method || 'GET';
    let body = typeof init.body === 'string' ? init.body : '', json = null, protocol = 0;
    const gateway = GATEWAY.is(path);
    try { if (gateway) body = GATEWAY.decrypt(body); json = JSON.parse(body); } catch { /* not JSON */ }
    if (json && typeof json.protocol === 'number') protocol = json.protocol;
    return { method, path, body, json, protocol, gateway, key: method + ' ' + path + (protocol ? ' #' + protocol : '') };
  }
  async fetch(url, init) {
    const req = Net.describe(url, init), entry = { key: req.key, request: req.json || req.body, status: 0, response: null };
    this.log.push(entry);
    const answer = (status, text) => {
      entry.status = status; entry.response = text;
      if (this.afterReply) this.afterReply(req, text);     // e.g. a recorder preparing its disposable database
      const wire = req.gateway && status === 200 ? GATEWAY.encrypt(text) : text, bytes = Buffer.from(wire, 'utf8');
      return { ok: status >= 200 && status < 300, status, text: async () => wire, json: async () => JSON.parse(wire),
        arrayBuffer: async () => bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.length) };
    };
    for (const h of this.handlers) {
      const hit = typeof h.match === 'number' ? h.match === req.protocol
        : h.match instanceof RegExp ? h.match.test(req.path) : h.match === req.path;
      if (!hit) continue;
      const value = typeof h.reply === 'function' ? await h.reply(req, this) : h.reply;
      if (value === undefined) continue;                      // handler declined: fall through
      return answer(200, typeof value === 'string' ? value : JSON.stringify(value));
    }
    if (this.queues) {
      const q = this.queues.get(req.key);
      if (q) {
        const e = q.list[Math.min(q.next++, q.list.length - 1)];   // polls repeat their last answer
        return answer(e.status, e.response);
      }
    }
    if (this.upstream) {
      const r = await this.realFetch(this.upstream + req.path, init), wire = await r.text();
      const text = req.gateway && r.status === 200 ? GATEWAY.decrypt(wire) : wire;
      this.tape.push({ key: req.key, status: r.status, response: text });
      return answer(r.status, text);
    }
    this.unanswered.push(req.key);
    return answer(599, '');
  }
  // Gateway protocols in the order they were sent, e.g. [51, 60, 701].
  get protocols() { return this.log.map(e => +(/#(\d+)$/.exec(e.key) || [])[1]).filter(Boolean); }
}

// ELF and native hooks only. Nothing is initialised: fake every structure the code under test reads.
function bare({ hooks = true } = {}) {
  const Runtime = require('../../apk-cpu-engine/runtime.js');
  const elf = readElf();
  const rt = new Runtime({ log() {} });
  rt.brk = 0x08000000;
  rt.loadElf(elf);
  if (hooks) require('./native_hooks.js').install(rt);
  rt.cpu.r[13] = 0x07000000;
  return new Harness(rt, null, elf);
}

// The real startup path with a virtual filesystem and a null GL: JNI_OnLoad plus the wrapper startup,
// which loads the data packages. `frames` then runs that many game ticks (title screen only: there
// is no server). No account, no player database.
// net: a Net to answer requests (without one, any request fails). clock: 'virtual' replaces Date.now
// with a clock that advances `tickMs` per run() frame, so runs are fast and repeatable.
function boot({ frames = 0, net = null, clock = 'real', tickMs = 34 } = {}) {
  if (net) net.install();
  // No real sockets from tests: without WebSocket the engine's chat connection is a local no-op.
  Object.defineProperty(globalThis, 'WebSocket', { value: undefined, configurable: true, writable: true });
  let vclock = null;
  if (clock === 'virtual') {
    // A replayed session starts at the moment it was recorded, so server times in the tape still fit.
    vclock = { now: (net && net.recordedAt) || Date.UTC(2026, 10, 15, 12), real: Date.now };
    Date.now = () => vclock.now;
  }
  global.ApkCPU = require('../../apk-cpu-engine/cpu.js');
  global.ApkRuntime = require('../../apk-cpu-engine/runtime.js');
  global.ApkJava = require('../../apk-cpu-engine/jni.js');
  const nodeBoot = require('./node_boot.js');
  const shim = require('./java_shim.js');
  const { makeNullGL } = require('../../apk-cpu-engine/gl_null.js');
  const elf = readElf(), logs = [], textLog = [];
  const game = require('./game.js').boot({
    elf, vfs: nodeBoot.vfs, gl: makeNullGL({}),
    installJava: shim.installJava, startup: shim.startup,
    log: s => logs.push(String(s)), screen: { w: 2048, h: 1184 },
    text: {
      width: (str, size) => Math.ceil(str.length * size * 0.6) + 1,
      width0: (str, size) => str.length * size * 0.6,
      metrics: size => ({ top: -Math.round(size * 1.06), ascent: -Math.round(size * 0.93), descent: Math.round(size * 0.24) }),
      draw(pixels, width, rows, str) { textLog.push(str); },
      drawLines(pixels, width, lay) { textLog.push((lay.lines || []).map(l => (typeof l === 'string' ? l : l.text || '')).join(' / ')); },
    },
  });
  game.start();
  const nap = new Int32Array(new SharedArrayBuffer(4));
  for (let i = 0; i < frames; i++) { game.frame(2000); Atomics.wait(nap, 0, 0, 16); }
  const h = new Harness(game.rt, game, elf);
  h.logs = logs; h.textLog = textLog; h.net = net; h.vclock = vclock; h.tickMs = tickMs;
  return h;
}

// A recorded session from browser/port/tests/fixtures, replayed with a virtual clock: fast and repeatable.
function replay(fixture, opts = {}) {
  const tape = JSON.parse(fs.readFileSync(path.join(__dirname, 'tests', 'fixtures', fixture + '.json'), 'utf8'));
  return boot({ net: new Net().replay(tape), clock: 'virtual', ...opts });
}

let shared = null;
function game() { return shared || (shared = boot()); }

// server-dotnet/seed/<name>.csv as an array of objects (quoted fields supported).
function seed(name) {
  const text = fs.readFileSync(path.join(ROOT, 'server-dotnet', 'seed', name + '.csv'), 'utf8').replace(/^﻿/, '');
  const rows = [];
  let row = [], field = '', quoted = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quoted) {
      if (c === '"' && text[i + 1] === '"') { field += '"'; i++; }
      else if (c === '"') quoted = false;
      else field += c;
    } else if (c === '"') quoted = true;
    else if (c === ',') { row.push(field); field = ''; }
    else if (c === '\n') { row.push(field); rows.push(row); row = []; field = ''; }
    else if (c !== '\r') field += c;
  }
  if (field || row.length) { row.push(field); rows.push(row); }
  const [head, ...body] = rows;
  return body.filter(r => r.length === head.length).map(r => Object.fromEntries(head.map((k, i) => [k, r[i]])));
}

module.exports = { game, boot, bare, replay, seed, Net, LAYOUT, SCENE, STOP, Harness };
