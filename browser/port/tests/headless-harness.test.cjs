// The harness itself: taps must not change native results, stubs must be recorded, moved code must fail.
const assert = require('node:assert/strict');
const test = require('node:test');
const Headless = require('../headless.js');

test('bare mode loads the ELF without starting the game', () => {
  const h = Headless.bare();
  assert.match(h.elfSha256, /^[0-9a-f]{64}$/);
  assert.equal(h.table(h.L.packages.itemOption, 0).count, 0, 'packages are not loaded in bare mode');
  assert.equal(h.unsecure(h.secure(-123456)), -123456);
  h.setSecureKey(0x1234abcd);
  assert.notEqual(h.secure(77), 77);
  assert.equal(h.unsecure(h.secure(77)), 77);
});

test('a tap observes a call and leaves its result unchanged', () => {
  const h = Headless.bare(), seen = [];
  const before = h.call('GetStageDifficult', 3000001);
  h.tap('GetDataMngByIndexInPackage', r => { seen.push(r[1]); });
  assert.equal(h.table(h.L.packages.shop, 10).count, 0);
  assert.deepEqual(seen, [10]);
  assert.equal(h.call('GetStageDifficult', 3000001), before);
  assert.throws(() => h.tap('GetSecureValue', () => {}), /push prologue/);
});

test('stubs replace a function and are logged', () => {
  const h = Headless.bare();
  h.stub('GetDataMngByIndexInPackage', 0x1234);
  assert.equal(h.call('GetDataMngByIndexInPackage', 1, 2, 3), 0x1234);
  assert.deepEqual(h.stubs, ['GetDataMngByIndexInPackage']);
  assert.deepEqual(h.stubCalls, [{ name: 'GetDataMngByIndexInPackage', args: [1, 2, 3, 0] }]);
  assert.throws(() => h.stub('NoSuchNativeFunction'), /no native symbol/);
});

test('pinned sites fail loudly when the code is not what the test expects', () => {
  const h = Headless.bare();
  h.expectCall(0x1450d4, 'GetEquipItemOptionValueByCategory');
  assert.throws(() => h.expectCall(0x1450d4, 'GetSecureValue'), /native code moved/);
  assert.throws(() => h.expectCall(0x1450d6, 'GetEquipItemOptionValueByCategory'), /native code moved/);
  h.expectLiteral(0x145214, 0x31e8);
  assert.throws(() => h.expectLiteral(0x145214, 0x31ec), /native code moved/);
});

test('a runaway call hits the instruction budget instead of hanging', () => {
  const h = Headless.bare();
  h.maxBatches = 2;
  h.cpu.hook(h.sym('GetStageDifficult'), () => {});   // never returns: pc stays on the hook
  assert.throws(() => h.call('GetStageDifficult', 1), /instruction budget/);
});
