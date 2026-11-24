const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const Runtime = require('../../../apk-cpu-engine/runtime.js');
const {makeArenaGoodsSync} = require('../game.js');

function runtime() {
  const html = fs.readFileSync(path.resolve(__dirname, '../../kns.html'), 'utf8');
  const marker = 'window.KNS_ELF="', begin = html.indexOf(marker) + marker.length;
  const rt = new Runtime({log() {}});
  rt.brk = 0x08000000;
  rt.loadElf(Buffer.from(html.slice(begin, html.indexOf('";', begin)), 'base64'));
  rt.cpu.r[13] = 0x07000000;
  return rt;
}

test('arena balance reaches native display through the Java queue, including zero and reward overflow', () => {
  const rt = runtime(), info = rt.syms.get('gameInfo') >>> 0;
  rt.cpu.wu32(info, 0x06000000);   // the struct's first word is not a pointer: it must stay untouched
  rt.javaQueue = [];
  const sync = makeArenaGoodsSync(rt);
  for (const count of [9, 8, 0, 13]) {
    sync({update_item_etc_info: [{item_id: 1900017, count, user_last_stamina_gain_time: 1794000000}]});
    const job = rt.javaQueue.shift();
    assert.ok(job);
    rt.callSync(job.addr, ...job.args);
    assert.equal(rt.cpu.ru32(info + 0xb68), count);
    assert.equal(rt.cpu.ru32(info + 0xb70), 1794000000);
    assert.equal(rt.cpu.ru32(info + 0xb74), 0);
    assert.equal(rt.cpu.ru32(info), 0x06000000);
    assert.equal(rt.cpu.ru32(0x06000000 + 0xb68), 0);
  }
  sync({update_item_etc_info: [{item_id: 1900017, count: -1}]});
  assert.equal(rt.javaQueue.length, 0);
});

test('native arena ranks 1, 2, 3 select gold, silver, bronze badge symbols; 100 uses numeric digits', () => {
  const rt = runtime(), cpu = rt.cpu, r = cpu.r;
  const base = rt.syms.get('DrawBattleRanking') - 0x50839;
  const entry = 0x06000000, scroll = entry + 0x1000, rect = entry + 0x2000, grp = entry + 0x3000;
  const symbols = [];
  const capture = cpu.addTrap('arena_badge_capture', () => { symbols.push(r[1]); r[15] = r[14]; });
  cpu.wu32(grp + 0xe8, capture);
  // Stop after badge/digits, before avatar/profile rendering. Run the actual native selection instructions.
  cpu.hook(base + 0x512e7, () => { r[15] = rt.syncRet; });
  for (const [rank, expected] of [[1, [322]], [2, [323]], [3, [324]], [100, [313, 312, 312]]]) {
    symbols.length = 0;
    const stack = (r[13] - 64) & ~7;
    cpu.wu32(stack + 0x20, entry);
    cpu.wu32(stack + 0x28, 0);
    cpu.wu32(entry + 0x8b4, rank);
    r[4] = scroll; r[6] = rect; r[7] = 0; r[11] = grp;
    rt.callSync(base + 0x51373);
    assert.deepEqual(symbols, expected, `rank ${rank}`);
  }
});

test('native rank grades follow position and population, including small leaderboards', () => {
  const rt = runtime(), grade = rt.syms.get('GetPvpGradeByRank');
  for (const [rank, total, expected] of [[1, 3, 1], [2, 3, 2], [3, 3, 3], [4, 200, 4], [10, 200, 4],
      [11, 200, 5], [20, 200, 5], [21, 200, 6], [30, 200, 6], [31, 200, 7], [50, 200, 7],
      [51, 200, 8], [100, 200, 8], [101, 200, 9], [102, 200, 10], [200, 200, 20], [1, 0, 20]]) {
    assert.equal(rt.callSync(grade, rank, total), expected, `rank ${rank}/${total}`);
  }
});
