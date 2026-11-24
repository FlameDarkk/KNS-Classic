// g_System's kernel function table: a zeroed touch slot crashed live play ("jump outside code: 0x0
// lr=0x276545", Collection of Allies, 2026-11-23). native_hooks.js checks the table before each event.
const test = require('node:test');
const assert = require('node:assert/strict');
const Headless = require('../headless.js');

test('ker_ConvertEvent restores a zeroed g_System touch slot instead of jumping to 0', () => {
  const h = Headless.game(), layout = h.L.systemTable;
  h.expectLiteral(layout.convertLiteral, layout.gotOffset);
  h.expectLiteral(layout.initLiteral, layout.initGot);
  h.expectLiteral(layout.pointerPressLiteral, layout.pointerPress);
  const table = h.sym('g_System'), slot = table + layout.touchSlot;
  const expected = h.u32(h.got + layout.initGot);
  assert.equal(h.u32(slot), expected, 'InitSystem filled the slot at startup');
  assert.equal(expected, h.addr('C2S_System_GetMultiEventNumber') >>> 0);

  const guard = h.rt.systemGuard;
  assert.ok(guard, 'guard installed on ker_ConvertEvent');
  // ker_ConvertEvent(&code, &a, &b): raw 0x80000004 = pointer press with a = x << 16 | y
  // (@0x17664c); it becomes scene event 6 and reaches the [g_System+0xb8] call.
  const event = h.alloc(4), x = h.alloc(4), y = h.alloc(4);
  const press = () => {
    h.w32(event, layout.pointerPress); h.w32(x, (100 << 16) | 100); h.w32(y, 0);
    return h.call('ker_ConvertEvent', event, x, y);
  };
  press();
  assert.ok(guard.snapshot, 'first event records the startup table');

  h.w32(slot, 0);
  press();
  assert.equal(h.u32(slot), expected, 'slot restored before the native call');
  const repair = guard.repairs.at(-1);
  assert.equal(repair.offset, layout.touchSlot);
  assert.equal(repair.was, 0);
  assert.equal(repair.restored, expected);
  assert.deepEqual(h.stubs, []);
});
