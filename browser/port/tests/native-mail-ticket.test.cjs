const assert = require('node:assert/strict');
const test = require('node:test');
const Headless = require('../headless.js');

test('mail claim response parses the delivered ally into the native fellow array', () => {
  const h = Headless.bare();
  const m = h.L.mailClaim;
  h.expectCall(m.fellowCall, 'ParsingDataToFellowInfoArray');
  h.expectLiteral(m.fellowOffsetLiteral, m.fellowOffset);
  const response = h.json({ fellow_info: [{ fellow_no: 9575, fellow_id: 100472, fellow_level: 1, fellow_max_level: 20 }], item_info: [] });
  const buffer = h.alloc(0xd000);
  h.tap('ParsingDataToItemArray', () => h.STOP);
  h.fragment({ start: m.fellowStart, regs: { 5: response, 6: buffer, 7: 0 } });
  const array = buffer + m.fellowOffset;
  assert.equal(h.u32(array), 1);
  const fellow = h.u32(array + 0xc);
  assert.equal(h.u32(fellow), 9575);
  assert.equal(h.u32(fellow + 0x20), 100472);
  assert.deepEqual(h.stubs, []);
});
