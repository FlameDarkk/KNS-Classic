const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const Runtime = require('../../../apk-cpu-engine/runtime.js');
const { makeAchievementSync } = require('../game.js');

test('authoritative achievements enter the real native list after response buffer reset', () => {
  const html = fs.readFileSync(path.resolve(__dirname, '../../kns.html'), 'utf8');
  const marker = 'window.KNS_ELF="', begin = html.indexOf(marker) + marker.length;
  const rt = new Runtime({ log() {} });
  rt.brk = 0x08000000;
  rt.loadElf(Buffer.from(html.slice(begin, html.indexOf('";', begin)), 'base64'));
  rt.cpu.r[13] = 0x07000000;
  rt.startJavaThread();
  const info = rt.syms.get('gameData');
  const sync = makeAchievementSync(rt);
  const types = [40006, 40007, 40009, 40031, 40057, 40067, 41025, 42025, 40082];
  sync({ errno: 0, update_achievement_info: types.map((type, i) => ({ type, sub_type: 0, count: i + 2 })) });
  // Actual response buffers may be cleared before queued work runs. Records must be independent.
  rt.cpu.m8.fill(0, info + 0x2468, info + 0x24b0);
  while (rt.javaQueue.length || rt.javaJob) rt.schedule(50);
  for (let i = 0; i < types.length; i++) {
    assert.equal(rt.callSync(rt.syms.get('GetAchievementProgressValue'), types[i] * 100 + 1), i + 2);
  }
  sync({ errno: 0, update_achievement_info: [{ type: 40009, sub_type: 0, count: 5000000000 }] });
  while (rt.javaQueue.length || rt.javaJob) rt.schedule(50);
  const record = rt.callSync(rt.syms.get('GetAchieveDoingByType'), info + 0x2420, 40009);
  assert.equal(rt.cpu.ru32(record + 8), 5000000000 >>> 0);
  assert.equal(rt.cpu.ru32(record + 12), 1);
  sync({ errno: 1, update_achievement_info: [{ type: 40082, sub_type: 0, count: 999 }] });
  assert.equal(rt.javaQueue.length, 0);
  assert.equal(rt.callSync(rt.syms.get('GetAchievementProgressValue'), 4008201), 10);
});

