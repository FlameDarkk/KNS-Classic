// Keyboard controls use the native combat touch areas after device-coordinate conversion.
(function (root) {
'use strict';
const ACTION_IDS = Object.freeze({ block: 3, attack: 5, throw: 6, ally1: 8, ally2: 9, ally3: 10 });
// GetTouchCicleAreaID reads count/+4, direct rows/+8, and 24-byte rows with ID/+20.
// Native circle centers are left + width/2, top + width/2 after layout scaling.
function readNativeTargets(rt, scene) {
  if (scene !== 6 && scene !== 10 && scene !== 13) return {};
  const targets = {};
  const sym = scene === 6 ? 'touchField' : scene === 10 ? 'touchBattle' : 'touchRaid';
  const touch = rt?.syms?.get ? rt.syms.get(sym) : 0;
  if (!touch) return targets;
  const cpu = rt.cpu, count = cpu.ru32(touch + 4), rows = cpu.ru32(touch + 8);
  if (!rows || count > 128) return targets;
  for (let i = 0; i < count; i++) {
    const row = rows + i * 24;
    const id = cpu.ru32(row + 20);
    const action = Object.keys(ACTION_IDS).find(name => ACTION_IDS[name] === id);
    if (!action) continue;
    const top = cpu.ru32(row) | 0, left = cpu.ru32(row + 8) | 0, right = cpu.ru32(row + 12) | 0;
    const radius = Math.trunc((right - left) / 2);
    if (radius <= 0) continue;
    targets[action] = { x: left + radius, y: top + radius };
  }
  return targets;
}
// Canvas pointers contain device coordinates. Native touch rows already contain scene
// coordinates: sending those through java.addEvent would convert them a second time.
// Queue on the Java thread so input never interrupts a partially executed native frame.
function createNativeSender(rt) {
  const sceneAddress = rt.syms.get('g_ui16CurrentScene');
  const popupAddress = rt.syms.get('g_i32CurrentPopupScene');
  const dispatch = rt.syms.get('ker_CallCurrentSceneEvent');
  const active = new Map();
  const entry = rt.cpu.addTrap('keyboard_combat_touch', () => {
    const cpu = rt.cpu, r = cpu.r;
    const [type, id, packed, expectedScene] = r;
    const scene = cpu.ru16(sceneAddress);
    const allowed = scene === expectedScene && (type === 3 || (cpu.ru32(popupAddress) | 0) < 0);
    if (type === 1 && allowed) active.set(id, { scene, packed });
    const press = active.get(id);
    if (type === 3) active.delete(id);
    if (!press || !allowed || press.scene !== scene) {
      r[0] = -1; r[15] = r[14]; return;
    }
    // Native events 6/7 are multi-pointer press/release. Their position is packed
    // as x in the upper half and y in the lower half; pointer IDs stay independent.
    r[0] = type === 1 ? 6 : 7;
    r[1] = id;
    r[2] = type === 1 ? packed : press.packed;
    r[15] = dispatch;
  });
  return (type, x, y, id) => {
    const scene = rt.cpu.ru16(sceneAddress);
    rt.callJava(entry, [type, id, ((x & 0xffff) << 16) | (y & 0xffff), scene]);
  };
}
const DEFAULT_BINDINGS = Object.freeze({ block: 'KeyA', attack: 'KeyS', throw: 'KeyD', ally1: 'Digit1', ally2: 'Digit2', ally3: 'Digit3' });
function validBindings(value) {
  return !!value && typeof value === 'object' && Object.keys(DEFAULT_BINDINGS).every(action =>
    typeof value[action] === 'string' && /^(Key[A-Z]|Digit[0-9]|F(?:[1-9]|1[0-2])|Space|Arrow(?:Up|Down|Left|Right)|Numpad[0-9])$/.test(value[action])) &&
    new Set(Object.keys(DEFAULT_BINDINGS).map(action => value[action])).size === Object.keys(DEFAULT_BINDINGS).length;
}
function createControls({ state, send, physicalCount, bindings = DEFAULT_BINDINGS }) {
  let currentBindings = validBindings(bindings) ? { ...bindings } : { ...DEFAULT_BINDINGS };
  const held = new Map();
  // ru32 reads native -1 (no popup) as 0xffffffff; compare it as a signed int.
  const enabled = s => !!s && (s.scene === 6 || s.scene === 10 || s.scene === 13) && (s.popup | 0) < 0;
  const release = code => {
    const press = held.get(code);
    if (!press) return;
    send(3, press.x, press.y, press.id, physicalCount() + held.size);
    held.delete(code);
  };
  return {
    down(event) {
      const action = Object.keys(currentBindings).find(name => currentBindings[name] === event.code);
      const current = state();
      const target = current?.targets?.[action];
      const tag = event.target && event.target.tagName;
      if (!target || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey ||
          event.target?.isContentEditable || /^(INPUT|TEXTAREA|SELECT|BUTTON)$/.test(tag || '') || !enabled(current)) return false;
      event.preventDefault();
      if (held.has(event.code) || event.repeat) return true;
      const press = { id: 100 + ACTION_IDS[action], x: target.x, y: target.y };
      held.set(event.code, press);
      send(1, press.x, press.y, press.id, physicalCount() + held.size);
      return true;
    },
    up(event) {
      if (!held.has(event.code)) return false;
      event.preventDefault();
      release(event.code);
      return true;
    },
    setBindings(value) {
      if (!validBindings(value)) return false;
      this.releaseAll();
      currentBindings = { ...value };
      return true;
    },
    get bindings() { return { ...currentBindings }; },
    releaseAll() { for (const code of [...held.keys()]) release(code); },
    validate() { if (held.size && !enabled(state())) this.releaseAll(); },
    get count() { return held.size; },
  };
}
if (typeof module !== 'undefined' && module.exports) module.exports = { createControls, createNativeSender, DEFAULT_BINDINGS, validBindings, readNativeTargets };
else root.KnsControls = { createControls, createNativeSender, DEFAULT_BINDINGS, validBindings, readNativeTargets };
})(typeof window !== 'undefined' ? window : globalThis);
