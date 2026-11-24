const test = require('node:test');
const assert = require('node:assert/strict');
const Headless = require('../headless.js');

test('native equipped-item power-up reports cached HP plus the cached preview difference', () => {
  const h = Headless.bare(), layout = h.L.achievementItem;
  h.expectCall(layout.itemLookupCall, 'GetItemNodeByUniqueItemID');
  h.expectCall(layout.hpReadCall, 'GetSecureValue');
  h.expectCall(layout.hpDiffReadCall, 'GetSecureValue');
  h.expectLiteral(layout.hpLiteral, layout.hpGot);
  h.expectLiteral(layout.hpDiffLiteral, layout.hpDiffGot);
  const item = h.alloc(256), reported = [];
  h.w32(item + layout.equippedOffset, 1);
  h.stub('GetItemNodeByUniqueItemID', item);
  h.tap('UpdateDataAchieveDoing', r => {
    const row = r[1] >>> 0;
    if (h.i32(row) === 40005) reported.push(h.i32(row + 8));
  });
  h.w32(h.sym('i32EquipHp'), h.secure(3475));
  h.w32(h.sym('i32HpDif'), h.secure(0));
  h.call('UpdateAchieveItem', 81, 0, 1, 0);
  h.w32(h.sym('i32HpDif'), h.secure(2000));
  h.call('UpdateAchieveItem', 81, 0, 1, 0);
  assert.deepEqual(reported, [3475, 5475]);
  assert.deepEqual(h.stubs, ['GetItemNodeByUniqueItemID']);
});
