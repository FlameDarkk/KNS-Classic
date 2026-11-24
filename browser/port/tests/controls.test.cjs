const assert = require('node:assert/strict');
const test = require('node:test');
const { createControls, readNativeTargets } = require('../controls.js');
const targets = { block: {x:76,y:1109}, attack: {x:1974,y:1110}, throw: {x:238,y:1125}, ally1: {x:2001,y:974}, ally2: {x:1876,y:1003}, ally3: {x:1838,y:1125} };
function key(code, props = {}) { return { code, target: { tagName: 'CANVAS' }, preventDefault() { this.prevented = true; }, ...props }; }
test('field and raid keys send independent touch presses and releases at supplied live target locations', () => {
  const events = []; let scene = 6, popup = -1, physical = 1;
  const controls = createControls({ screen: {w: 2048, h: 1184}, state: () => ({scene, popup, targets}), physicalCount: () => physical, send: (...event) => events.push(event) });
  const attack = key('KeyS'); assert.equal(controls.down(attack), true); assert.equal(attack.prevented, true);
  controls.down(key('KeyS', {repeat: true}));
  controls.down(key('Digit1'));
  assert.deepEqual(events, [[1, 1974, 1110, 105, 2], [1, 2001, 974, 108, 3]]);
  controls.up(key('KeyS')); assert.deepEqual(events.at(-1), [3, 1974, 1110, 105, 3]);
  popup = 2; controls.validate(); assert.deepEqual(events.at(-1), [3, 2001, 974, 108, 2]);
  assert.equal(controls.count, 0);
  scene = 13; popup = -1; physical = 0;
  controls.down(key('KeyA')); controls.up(key('KeyA'));
  assert.deepEqual(events.slice(-2), [[1, 76, 1109, 103, 1], [3, 76, 1109, 103, 1]]);
});
test('typing, menus, modifiers, and repeated keys do not trigger combat', () => {
  let current = {scene: 1, popup: -1, targets}; const events = [];
  const controls = createControls({ screen: {w: 2048, h: 1184}, state: () => current, physicalCount: () => 0, send: (...event) => events.push(event) });
  assert.equal(controls.down(key('KeyS')), false);
  current = {scene: 6, popup: 0, targets}; assert.equal(controls.down(key('KeyS')), false);
  current.popup = -1;
  assert.equal(controls.down(key('KeyS', {target: {tagName: 'INPUT'}})), false);
  assert.equal(controls.down(key('KeyS', {ctrlKey: true})), false);
  controls.down(key('KeyS', {repeat: true}));
  assert.deepEqual(events, []);
});

test('bindings can be changed and invalid or duplicate keys are rejected', () => {
  const events = [];
  const controls = createControls({ screen: {w: 960, h: 640}, state: () => ({scene: 6, popup: -1, targets}), physicalCount: () => 0, send: (...event) => events.push(event) });
  const changed = { ...controls.bindings, attack: 'KeyQ' };
  assert.equal(controls.setBindings(changed), true);
  assert.equal(controls.down(key('KeyS')), false);
  assert.equal(controls.down(key('KeyQ')), true);
  assert.equal(controls.setBindings({ ...changed, ally1: 'KeyQ' }), false);
  assert.equal(controls.setBindings({ ...changed, attack: 'Escape' }), false);
  controls.setBindings({ ...changed, attack: 'KeyE' });
  assert.equal(events.at(-1)[0], 3);
  assert.equal(controls.count, 0);
  assert.equal(controls.down(key('KeyE')), true);
});

// ru32 returns the native -1 sentinel as an unsigned value in the actual web bridge.
test('all combat bindings work with the unsigned native no-popup sentinel', () => {
  const events = [];
  let popup = 0xffffffff;
  const controls = createControls({ screen: {w: 2048, h: 1184}, state: () => ({scene: 6, popup, targets}), physicalCount: () => 0, send: (...event) => events.push(event) });
  for (const code of Object.values(controls.bindings)) {
    assert.equal(controls.down(key(code)), true);
    assert.equal(controls.up(key(code)), true);
  }
  assert.equal(events.length, 12);
  assert.equal(controls.count, 0);
  controls.down(key('KeyS'));
  popup = 47;
  controls.validate();
  assert.equal(events.at(-1)[0], 3);
  assert.equal(controls.count, 0);
  assert.equal(controls.down(key('KeyS')), false);
});

test('saved E/R/Space/D/F/C bindings select distinct live targets and ignore unbound keys', () => {
  const events = [];
  const bindings = { attack: 'KeyE', throw: 'KeyR', block: 'Space', ally1: 'KeyD', ally2: 'KeyF', ally3: 'KeyC' };
  const liveTargets = Object.fromEntries(Object.entries(targets).map(([action, point]) => [action, {x: point.x - 20, y: point.y - 100}]));
  const current = {scene: 6, popup: 0xffffffff, targets: liveTargets};
  const controls = createControls({bindings, state: () => current, send: (...event) => events.push(event), physicalCount: () => 0});
  assert.equal(controls.down(key('KeyS')), false);
  for (const [action, code] of Object.entries(bindings)) {
    controls.down(key(code)); controls.up(key(code));
    assert.deepEqual(events.at(-2).slice(1,3), [liveTargets[action].x, liveTargets[action].y]);
  }
  current.targets = {};
  assert.equal(controls.down(key('KeyE')), false);
  assert.equal(events.length, 12);
});

test('scene 10 allows bindings but uninitialized native layouts never invent touch centers', () => {
  const events = [];
  const controls = createControls({ state: () => ({ scene: 10, popup: -1, targets }), physicalCount: () => 0, send: (...e) => events.push(e) });
  assert.equal(controls.down(key('KeyS')), true);
  controls.up(key('KeyS'));
  assert.deepEqual(events, [[1, 1974, 1110, 105, 1], [3, 1974, 1110, 105, 1]]);

  for (const scene of [6, 10, 13]) {
    const fallback = readNativeTargets({ syms: new Map(), cpu: { ru32: () => 0 } }, scene);
    assert.deepEqual(fallback, {});
  }
  assert.deepEqual(readNativeTargets({}, 3), {});
});
