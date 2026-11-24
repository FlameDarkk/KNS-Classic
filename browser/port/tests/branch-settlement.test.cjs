// Real native stage-reply parsing; no account or player database is used.
const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const Runtime = require('../../../apk-cpu-engine/runtime.js');

function boot() {
  const html = fs.readFileSync(path.resolve(__dirname, '../../kns.html'), 'utf8');
  const marker = 'window.KNS_ELF="', at = html.indexOf(marker) + marker.length;
  const rt = new Runtime({log() {}});
  rt.brk = 0x08000000;
  rt.loadElf(process.env.KNS_NATIVE_TEST_ELF ? fs.readFileSync(process.env.KNS_NATIVE_TEST_ELF)
    : Buffer.from(html.slice(at, html.indexOf('";', at)), 'base64'));
  const cpu = rt.cpu, r = cpu.r;
  r[13] = 0x07000000;
  const base = (rt.syms.get('InitTiledMap') & ~1) - 0x1680e4;
  const got = 0x06000000, game = 0x06010000, map = 0x06030000;
  cpu.wu32(got + 0x100, game);
  cpu.wu32(got + 0x1424, map);
  const run = cpu.run.bind(cpu); let batches = 0;
  cpu.run = () => { assert.ok(++batches < 300, 'native reply instruction budget'); return run(10000); };
  const json = value => rt.callSync(rt.syms.get('json_loads'), rt.newCstr(JSON.stringify(value)), 0, 0);
  const parse = (start, stop, value) => {
    const doc = json(value);
    assert.ok(doc);
    r[4] = got; r[5] = doc; r[8] = 0x100;
    cpu.hook((base + stop) | 1, () => { r[15] = rt.syncRet; });
    rt.callSync((base + start) | 1);
  };
  return {rt, cpu, r, base, got, game, map, parse};
}

test('native start parses two, one, then zero event kinds without retaining the last run', () => {
  const f = boot(), {cpu, game, map, parse} = f;
  for (const drops of [
    [{item_id: 1901007, count: 4, item_no: 10001}, {item_id: 1901008, count: 2, item_no: 10002}],
    [{item_id: 1901007, count: 3, item_no: 10003}],
    [],
  ]) {
    cpu.m8.fill(0x55, map + 0xa38, map + 0xa70);
    parse(0x15e588, 0x15e5fa, {drop_event_item_list: drops});
    assert.deepEqual([0, 4, 8, 12, 16, 20].map(n => cpu.ru32(game + 0xe2dc + n)),
      [drops[0]?.item_id || 0, drops[1]?.item_id || 0, drops[0]?.count || 0, drops[1]?.count || 0,
       drops[0]?.item_no || 0, drops[1]?.item_no || 0]);
    assert.ok(cpu.m8.subarray(map + 0xa38, map + 0xa70).every(n => n === 0), 'map pickup state reset');
  }
});

test('native result shows all offered branches on clear, including zero pickups; failure shows none', () => {
  const {rt, cpu, r, base, got, game, map, parse} = boot();
  // Other parsers are outside this regression. Keep their native call boundaries,
  // but isolate their large user-info records from the branch result fields.
  for (const name of ['ParsingDataToUserInfo']) {
    rt.hookCost(name, 1, () => { r[0] = 0; });
  }
  // Aggregate multi-clear credit can exceed the maximum seven drops of one run.
  for (const [offered, picked, result] of [[4, 2, 1], [3, 1, 1], [4, 0, 1],
    [24, 2, 1], [42, 0, 1], [4, 2, 0], [4, 2, 2], [0, 0, 1]]) {
    parse(0x15e588, 0x15e5fa, {drop_event_item_list: offered ? [{item_id: 1901007, count: offered, item_no: 10004}] : []});
    cpu.wu32(map + 0xa38, picked); cpu.wu32(map + 0xa3c, 1901007);
    const count = result === 1 ? offered : 0;
    // JanssonParser clears this entire reply area before EVERY protocol. The old
    // fixture omitted that lifecycle step, retaining stage-71 fields illegally.
    cpu.m8.fill(0, game + 0xc420, game + 0xc420 + 0x22e8);
    parse(0x15fcd0, 0x15fd0a, {stage_result: result, user_info: {},
      update_item_etc_info: [{item_id: 1901007, count: 590 + count}],
      drop_event_item_list: count ? [{item_id: 1901007, count}] : []});
    assert.equal(cpu.ru32(game + 0xc428 + 0x11c), 590 + count, 'native wallet reply parser');
    rt.callSync(rt.syms.get('UpdateUserGoods'), game + 0xc428);
    const goods = rt.syms.get('UpdateUserGoods') & ~1;
    const realGot = (goods + 12 + cpu.ru32(goods + 0xe8)) >>> 0;
    const player = cpu.ru32(realGot + 0x1aec);
    assert.equal(cpu.ru32(player + 0xb24), 590 + count, 'native wallet receives the absolute branch balance');
    assert.deepEqual([0, 4, 8, 12].map(n => cpu.ru32(map + 0xa38 + n)),
      [count, count ? 1901007 : 0, 0, 0]);

    // Execute DrawResult's actual first-slot count formatter, then inspect its text.
    const buffer = 0x06040000;
    cpu.wu32(got + 0x104, buffer);
    cpu.wu32(r[13] - 64 + 0x38, 0x104);
    r[4] = got; r[6] = map;
    cpu.hook((base + 0xe837a) | 1, () => { r[15] = rt.syncRet; });
    rt.callSync((base + 0xe8364) | 1);
    assert.equal(rt.cstr(buffer), String(count));
  }
});
