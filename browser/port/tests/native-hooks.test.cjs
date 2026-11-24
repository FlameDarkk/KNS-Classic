// Mid-function native hooks must sit where the engine dispatches (a block start), or compiled code runs past them.
const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const Runtime = require('../../../apk-cpu-engine/runtime.js');
const NativeHooks = require('../native_hooks.js');

function boot() {
  const html = fs.readFileSync(path.resolve(__dirname, '../../kns.html'), 'utf8');
  const marker = 'window.KNS_ELF="';
  const begin = html.indexOf(marker) + marker.length;
  const elf = Buffer.from(html.slice(begin, html.indexOf('";', begin)), 'base64');
  const rt = new Runtime({ log() {} });
  rt.brk = 0x08000000;
  rt.loadElf(elf);
  NativeHooks.install(rt);
  rt.cpu.r[13] = 0x07000000;
  return rt;
}

test('event boss card returns normally on success and cleans up without opening on missing map', () => {
  for (const loaded of [0, 1]) {
    const rt = boot(), cpu = rt.cpu, r = cpu.r;
    const base = (rt.syms.get('InitTiledMap') & ~1) - 0x1680e4;
    const draw = 0x06000000, originalR4 = 0x12345678;
    r[4] = originalR4; r[5] = draw;
    const calls = [];
    rt.hookCost('InitTiledMap', 1, () => {
      assert.equal(r[0], draw + 0x310);
      assert.equal(r[1], 0x06001000);
      calls.push('load'); r[0] = loaded;
    });
    for (const [name, offset] of [['DestroyHeroGameData', 0x2c8]]) {
      rt.hookCost(name, 1, () => {
        assert.equal(r[0], draw + offset);
        calls.push(name);
        r[0] = 0; r[1] = 0; r[2] = 0; r[3] = 0; // permitted caller-saved clobbers
      });
    }
    rt.hookCost('ReleaseTiledMap', 1, () => { assert.fail('failed initialization must not release stale map handles twice'); });
    for (const [site, name] of [[0x6ba3c, 'open'], [0x6b00e, 'exit']]) {
      cpu.hook((base + site) | 1, () => {
        assert.equal(r[4], originalR4);
        assert.equal(r[5], draw);
        calls.push(name); r[15] = rt.syncRet;
      });
    }
    // Limit guest work so a broken branch fails instead of hanging the test runner.
    const run = cpu.run.bind(cpu); let batches = 0;
    cpu.run = () => { assert.ok(++batches < 10, 'native card instruction budget'); return run(1000); };
    rt.callSync((base + 0x6ba38) | 1, draw + 0x310, 0x06001000);
    assert.deepEqual(calls, loaded ? ['load', 'open'] : ['load', 'DestroyHeroGameData', 'exit']);
  }
});

test('server event calendar overwrites stage-package table 2 rows, also once the function is compiled', () => {
  const rt = boot(), cpu = rt.cpu;
  // Package object P: [P] = table count, [P+0xc] = data (CS_GETDPTR is the identity). Data: big-endian table offsets
  // at (table+3)*4, then rows of table 2 at data + count*4 + 0x5a + offset: a big-endian row count, rows from +6.
  const P = 0x06000000, D = 0x06001000, tables = 4, base = D + tables * 4 + 0x5a;
  cpu.wu32(P, tables); cpu.wu32(P + 0xc, D);
  for (let i = 0; i < 4; i++) cpu.m8[D + 20 + i] = 0;               // table 2 at offset 0
  cpu.m8[base] = 0; cpu.m8[base + 1] = 2;                            // two rows
  const rows = base + 6, ids = [3250000, 3200120];
  ids.forEach((id, i) => {
    const row = rows + i * 736;
    cpu.wu32(row, id);
    [2014, 8, 1, 0, 2037, 12, 31, 0, 24, 168].forEach((v, k) => cpu.wu32(row + 356 + k * 4, v));
  });
  const field = (row, k) => cpu.m32[(row + 356 + k * 4) >>> 2];
  // Tower of Training 2026-11-13 00:00 KST to 2026-11-14 00:00 KST; Bastille closed at epoch-to-2026-11-13 00:00 KST.
  const open = Date.UTC(2026, 10, 12, 15) / 1000;
  globalThis.KnsEventSchedule = { rows: new Map([[3250000, [open, open + 86400, 86400, 86400]], [3200120, [0, open, 3600, 3600]]]) };
  const count = 0x06002000, fn = rt.syms.get('GetStageDataInPackage');
  for (let run = 0; run < 300; run++) {
    // Restore the bundled dates so every call has to rewrite them.
    [0, 1].forEach(i => cpu.wu32(rows + i * 736 + 356, 2014));
    const got = rt.callSync(fn, P, 2, count) >>> 0;
    assert.equal(got, rows);
    assert.equal(cpu.m32[count >>> 2], 2);
    assert.deepEqual([0, 1, 2, 3, 4, 5, 6, 7, 8, 9].map(k => field(rows, k)), [2026, 11, 13, 0, 2026, 11, 14, 0, 24, 24], 'run ' + run);
    assert.deepEqual([0, 1, 2, 3, 4, 5, 6, 7].map(k => field(rows + 736, k)), [1970, 1, 1, 9, 2026, 11, 13, 0]);
  }
  // Other tables pass through untouched.
  for (let i = 0; i < 4; i++) cpu.m8[D + 16 + i] = 0;
  cpu.wu32(rows + 356, 2014);
  rt.callSync(fn, P, 1, count);
  assert.equal(field(rows, 0), 2014);
  delete globalThis.KnsEventSchedule;
});

test('chat separator hook is installed on the block start of its DrawLine call', () => {
  const rt = boot(), fn = (rt.syms.get('DrawChattingPopup') >>> 0) & ~1;
  assert.equal(rt.cpu.tab[(((fn + 0x234) | 1) - rt.cpu.codeBase) >>> 0]?.hooked, true);
});

test('pinned notice: wrap hook on its block start, and KnsChatPin writes the chattingServer pinned slot', () => {
  const rt = boot(), cpu = rt.cpu, fn = (rt.syms.get('DrawChattingPopup') >>> 0) & ~1;
  assert.equal(cpu.tab[(((fn + 0x56e) | 1) - cpu.codeBase) >>> 0]?.hooked, true);
  const pin = globalThis.KnsChatPin;
  assert.ok(pin, 'KnsChatPin installed');
  // The GOT entry the native code reads ([GOT + 0x1aec]) must point into the loaded image (a static object).
  const view = (rt.syms.get('ProcessChattingView') >>> 0) & ~1;
  const got = (view + 0x1e + cpu.u32[(view + 0x274) >>> 2]) >>> 0;
  const chat = cpu.u32[(got + 0x1aec) >>> 2] >>> 0;
  assert.ok(chat > cpu.codeBase && chat < 0x08000000, 'chattingServer 0x' + chat.toString(16));
  assert.equal(pin.apply({ text: 'Server reset — check mail', start: 1794500000, end: 1794503600 }), true);
  const text = Buffer.from(cpu.m8.subarray(chat + 0x4468, chat + 0x4468 + 64));
  assert.equal(text.subarray(0, text.indexOf(0)).toString('utf8'), 'Server reset — check mail');
  assert.equal(cpu.m32[(chat + 0x4218) >>> 2] >>> 0, 1794503600);
  assert.equal(cpu.m32[(chat + 0x421c) >>> 2], 0);
  assert.equal(cpu.m8[chat + 0x40f5], 0);
  // Long text is cut at a whole UTF-8 character inside the 512-byte slot, leaving the second slot alone.
  cpu.m8[chat + 0x4668] = 0x5a;
  pin.apply({ text: '가'.repeat(200), start: 0, end: 1 });
  const end = cpu.m8.indexOf(0, chat + 0x4468) - (chat + 0x4468);
  assert.equal(end, 510);
  assert.equal(cpu.m8[chat + 0x4668], 0x5a);
  assert.equal(pin.apply(null), true);
  assert.equal(cpu.m8[chat + 0x40f5], 1);
  delete globalThis.KnsChatPin;
});
