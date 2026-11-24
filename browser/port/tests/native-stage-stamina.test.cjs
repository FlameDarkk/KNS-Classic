// Stage and stamina data through the game's own readers, checked against the server seed and rules.
// GetStageDataByID@0xf5040 (stage package, 736-byte rows), GetShopStaminaData@0xf93a4 (shop table 10).
const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const Headless = require('../headless.js');

test('every seed stage is found by GetStageDataByID with the same 736-byte row', () => {
  const h = Headless.game(), stages = Headless.seed('master_stages');
  assert.ok(stages.length > 100);
  for (const s of stages) {
    const row = h.ptr('GetStageDataByID', +s.stage_id);
    assert.ok(row, 'stage ' + s.stage_id + ' missing from the native package');
    assert.equal(h.i32(row), +s.stage_id);
    assert.equal(s.raw_hex.length, 736 * 2);
    assert.equal(h.bytes(row, 736).toString('hex'), s.raw_hex, 'stage ' + s.stage_id);
    assert.equal(h.cstr(row + 4), s.map);
  }
  assert.equal(h.ptr('GetStageDataByID', 3999999), 0, 'unknown stage');
});

test('Hero Point stamina upgrades: native shop table 10 equals PlayerRules', () => {
  const h = Headless.game(), shop = h.game + h.L.packages.shop;
  const rules = fs.readFileSync(path.resolve(__dirname, '../../../server-dotnet/src/KnS.Game/Rules/PlayerRules.cs'), 'utf8');
  const rule = name => +rules.match(new RegExp('const long ' + name + ' = ([0-9]+);'))[1];
  const limit = rule('StaminaUpgradeLimit'), price = rule('StaminaUpgradePrice');
  assert.equal(h.table(h.L.packages.shop, 10).count, limit);
  for (let n = 1; n <= limit; n++) {
    const row = h.ptr('GetShopStaminaData', shop, n);
    // (purchase count, MAX Stamina bonus, its price, recovery seconds saved, its price); UpdateUserInfo@0x14ad98 adds row+4.
    assert.deepEqual(h.ints(row, 5), [n, 2 * n, price, 10 * n, price]);
  }
  assert.equal(h.ptr('GetShopStaminaData', shop, 0), 0);
  assert.equal(h.ptr('GetShopStaminaData', shop, limit + 1), 0);
  assert.deepEqual(h.stubs, []);
});

test('native user info shows the level-based stamina cap plus purchased upgrades', () => {
  const h = Headless.game(), layout = h.L.stamina;
  h.expectCall(layout.upgradeCall, 'GetShopStaminaData');
  h.expectLiteral(layout.shopLiteral, layout.shopOffset);
  for (const [base, upgrade, total] of [[100, 0, 100], [101, 0, 101], [117, 3, 123]]) {
    const row = h.alloc(0x400);
    const json = h.json({ user_no: 1, user_level: 18, user_max_stamina: base, user_max_stamina_buff: upgrade });
    h.call('ParsingDataToUserInfo', json, row);
    assert.equal(h.i32(row + layout.replyBase), base);
    assert.equal(h.i32(row + layout.replyUpgrade), upgrade);
    h.call('UpdateUserInfo', row);
    assert.equal(h.i32(h.fight + layout.displayedMax), total);
  }
  assert.deepEqual(h.stubs, []);
});

test('native refill user-info parser retains stamina above the cap', () => {
  const h = Headless.game(), layout = h.L.stamina;
  h.expectCall(layout.refillUserCall, 'ParsingDataToUserInfo');
  const row = h.alloc(0x400);
  h.call('ParsingDataToUserInfo', h.json({ user_no: 1, user_stamina: 120, user_max_stamina: 100 }), row);
  assert.equal(h.i32(row + layout.replyCurrent), 120);
  assert.equal(h.i32(row + layout.replyBase), 100);
  assert.deepEqual(h.stubs, []);
});
