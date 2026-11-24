const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const Runtime = require('../../../apk-cpu-engine/runtime.js');
const Java = require('../../../apk-cpu-engine/jni.js');
const shim = require('../java_shim.js');
const { createControls, createNativeSender, readNativeTargets } = require('../controls.js');

test('keyboard bindings reach native Field/Raid hit testing without a second device conversion', () => {
  const html = fs.readFileSync(path.resolve(__dirname, '../../kns.html'), 'utf8');
  const marker = 'window.KNS_ELF="', begin = html.indexOf(marker) + marker.length;
  const rt = new Runtime({ log() {} });
  rt.brk = 0x08000000;
  const java = new Java(rt); rt.java = java;
  const brk = rt.brk;
  rt.loadElf(Buffer.from(html.slice(begin, html.indexOf('";', begin)), 'base64'));
  rt.brk = brk; rt.startJavaThread(); rt.cpu.r[13] = 0x07000000;
  const cpu = rt.cpu, r = cpu.r;
  const hook = (name, callback) => cpu.hook(rt.syms.get(name), () => {
    r[0] = callback(); r[15] = r[14];
  });
  hook('jGetEnv', () => java.env);
  shim.installJava(rt, java, { w: 2048, h: 1184 }, { sym: name => rt.syms.get(name), cls: name => java.ref(java.cls(name)) });
  const drain = () => {
    for (let i = 0; rt.javaQueue.length || rt.javaJob; i++) {
      assert.ok(i < 100, 'native input queue must finish');
      rt.schedule(50, () => !rt.javaQueue.length && !rt.javaJob);
    }
  };
  const eventData = { cls: 'com/com2us/wrapper/kernel/CEventHandler$EventData', fields: { front: 0, rear: 0 } };
  const buffer = { cap: 0x800 };
  java.eventData = eventData; java.eventBuffer = buffer;
  rt.callJava(rt.syms.get('Java_com_com2us_wrapper_kernel_CEventHandler_nativeEventHandlerInitialize'),
    [java.env, 0, java.ref(eventData), java.ref(buffer), 8, 256]);
  drain();
  cpu.m8[rt.syms.get('g_bStartupDone')] = 0;
  cpu.m8[rt.syms.get('g_bEventLock')] = 0;
  cpu.wu32(rt.syms.get('g_i32CurrentPopupScene'), -1);
  cpu.wu32(rt.syms.get('g_EventUse'), 3);
  // Exercise the real kernel converter with a non-identity device/scene scale.
  // Its original dimensions are 480x320 in this exact ELF.
  cpu.wu32(rt.syms.get('deviceInfo') + 0x1a0, 1024);
  cpu.wu32(rt.syms.get('deviceInfo') + 0x19c, 592);
  const identity = cpu.addTrap('test_identity_touch_adjustment', () => { r[15] = r[14]; });
  cpu.wu32(rt.syms.get('grp') + 0x4a8, identity);
  const eventMode = cpu.addTrap('test_multi_pointer_mode', () => { r[0] = 3; r[15] = r[14]; });
  cpu.wu32(rt.syms.get('g_System') + 0xb8, eventMode);
  const actions = { block: 3, attack: 5, throw: 6, ally1: 8, ally2: 9, ally3: 10 };
  const bindings = { block: 'Space', attack: 'KeyW', throw: 'KeyE', ally1: 'Digit1', ally2: 'Digit2', ally3: 'Digit3' };
  const received = [];
  let touch;
  for (const suffix of ['FIELD', 'RAID']) {
    for (const [verb, type] of [['Press', 1], ['Release', 3]]) {
      hook(`Scene_Pointer${verb}Event_SC_${suffix}`, () => {
        const packed = r[2], id = r[1], x = packed >>> 16, y = packed & 0xffff;
        const action = rt.callSync(rt.syms.get('GetTouchCicleAreaID'), touch, 0, x, y);
        received.push({ type, id, action });
        return -1;
      });
    }
  }
  const send = createNativeSender(rt);
  for (const scene of [6, 13]) {
    cpu.wu16(rt.syms.get('g_ui16CurrentScene'), scene);
    touch = rt.syms.get(scene === 6 ? 'touchField' : 'touchRaid');
    const rows = 0x06000000;
    cpu.wu32(touch + 4, 6); cpu.wu32(touch + 8, rows);
    Object.values(actions).forEach((id, index) => {
      const top = 400, left = 80 + index * 140;
      [top, top + 80, left, left + 80, 0, id].forEach((value, field) => cpu.wu32(rows + index * 24 + field * 4, value));
    });
    const targets = readNativeTargets(rt, scene);
    // Reproduce the old adapter: a valid scene center enters the device ring and misses.
    java.addEvent(1, targets.attack.x, targets.attack.y, 105, 1);
    rt.callJava(rt.syms.get('jProcessEvent'), []); drain();
    assert.ok(received.length > 0);
    assert.ok(received.every(event => event.action !== actions.attack));
    received.length = 0;
    const controls = createControls({ bindings, state: () => ({ scene, popup: -1, targets }), send, physicalCount: () => 0 });
    for (const code of Object.values(bindings)) controls.down({ code, target: { tagName: 'CANVAS' }, preventDefault() {} });
    drain();
    assert.deepEqual(received.map(event => event.action), Object.values(actions));
    assert.equal(new Set(received.map(event => event.id)).size, 6);
    controls.releaseAll(); drain();
    assert.deepEqual(received.slice(6).map(event => [event.type, event.action]), Object.values(actions).map(id => [3, id]));
    received.length = 0;
    send(1, targets.attack.x, targets.attack.y, 105); drain();
    cpu.wu32(rt.syms.get('g_i32CurrentPopupScene'), 27);
    send(3, targets.attack.x, targets.attack.y, 105); drain();
    assert.deepEqual(received.map(event => event.type), [1, 3]);
    received.length = 0;
    cpu.wu32(rt.syms.get('g_i32CurrentPopupScene'), -1);
    send(1, targets.attack.x, targets.attack.y, 105);
    cpu.wu32(rt.syms.get('g_i32CurrentPopupScene'), 27);
    drain(); assert.deepEqual(received, []);
    cpu.wu32(rt.syms.get('g_i32CurrentPopupScene'), -1);
    send(1, targets.attack.x, targets.attack.y, 105);
    cpu.wu16(rt.syms.get('g_ui16CurrentScene'), 4);
    drain(); assert.deepEqual(received, []);
  }
});
