const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const vm = require('node:vm');
const controlsModule = require('../controls.js');

test('closing Settings restores canvas focus and native unsigned state permits keyboard combat', () => {
  const listeners = {}, nodes = [], events = [];
  const document = { hidden: false, documentElement: {}, addEventListener() {} };
  const window = { KnsControls: null, addEventListener() {}, parent: null };
  function element(tag) {
    const node = { tagName: tag.toUpperCase(), children: [], listeners: {},
      append(...children) { this.children.push(...children); }, appendChild(child) { this.children.push(child); },
      setAttribute() {}, addEventListener(type, callback) { this.listeners[type] = callback; }, style: {},
      getBoundingClientRect: () => ({ left: 0, right: 0, top: 0, bottom: 0, width: 0, height: 0 }), replaceChildren() {}, contains: () => false,
      focus() { document.activeElement = this; }, setPointerCapture() {} };
    nodes.push(node); return node;
  }
  document.createElement = element; document.body = element('body');
  const canvas = element('canvas');
  const game = { rt: { syms: new Map([['g_ui16CurrentScene', 10], ['g_i32CurrentPopupScene', 20], ['touchField', 32]]),
    cpu: { ru16: () => 6, ru32: address => ({20: 0xffffffff, 36: 1, 40: 64, 64: 1035, 72: 1899, 76: 2049, 84: 5}[address] || 0) } }, java: { addEvent: (...event) => events.push(event) } };
  const source = fs.readFileSync(require.resolve('../web.js'), 'utf8');
  const block = source.slice(source.indexOf('// ---------- touch ----------'), source.indexOf('function start()'));
  const bridge = { ...controlsModule, createNativeSender(rt) {
    assert.equal(rt, game.rt);
    return (...event) => events.push(event);
  } };
  game.java.addEvent = () => { throw new Error('Keyboard scene coordinates must not enter the device event ring'); };
  vm.runInNewContext(block, { window: Object.assign(window, { KnsControls: bridge, parent: window }), document, canvas, game, running: true,
    SCREEN: {w: 2048, h: 1184}, localStorage: {getItem: () => null, setItem() {}},
    // The Graphics and Performance rows of the Settings panel use the settings section near the top of web.js.
    settings: { resolution: 'high',memorySaver: false, showFps: false },
    RES_SCALE: { high: 1, medium: 0.75, low: 0.6, verylow: 0.45 }, saveSettings() {}, fitRender() {}, trimAssets() {},
    // The same block builds the side buttons (full download, online count) and their server polls.
    INDEX: {}, assets: new Map(), cachedNames: new Set(), dl: { active: 0, queue: [] }, CAN_FETCH: false, preload() {}, renderDownloads() {},
    fetch: () => Promise.reject(new Error('offline')), setInterval() {}, setTimeout() {}, clearTimeout() {},
    location: { origin: 'http://localhost', href: 'http://localhost/kns.html' }, sessionStorage: { setItem() {} },
    addEventListener(type, callback) { listeners[type] = callback; } });
  nodes.find(node => node.id === 'settings-open').listeners.click();
  nodes.find(node => node.textContent === 'Close').listeners.click();
  assert.equal(document.activeElement, canvas);
  assert.equal(canvas.tabIndex, 0);
  const key = { code: 'KeyS', target: document.activeElement, preventDefault() {} };
  listeners.keydown(key); listeners.keyup(key);
  assert.deepEqual(events, [[1, 1974, 1110, 105, 1], [3, 1974, 1110, 105, 1]]);
});

// The touch block of web.js with a recording game: canvas 1024 x 592 at (0, 0), half the game's 2048 x 1184.
function touchPage() {
  const winListeners = {}, docListeners = {}, nodes = [], events = [];
  const on = table => (type, callback) => { (table[type] = table[type] || []).push(callback); };
  const document = { hidden: false, documentElement: {}, addEventListener: on(docListeners) };
  const window = { KnsControls: null, addEventListener: on(winListeners), parent: null };
  function element(tag) {
    const node = { tagName: tag.toUpperCase(), children: [], listeners: {},
      append(...children) { this.children.push(...children); }, appendChild(child) { this.children.push(child); },
      setAttribute() {}, addEventListener(type, callback) { this.listeners[type] = callback; }, style: {},
      getBoundingClientRect: () => ({ left: 0, right: 1024, top: 0, bottom: 592, width: 1024, height: 592 }), replaceChildren() {}, contains: () => false,
      focus() { document.activeElement = this; }, setPointerCapture() {} };
    nodes.push(node); return node;
  }
  document.createElement = element; document.body = element('body');
  const canvas = element('canvas');
  const game = { rt: { syms: new Map(), cpu: { ru16: () => 0, ru32: () => 0 } }, java: { addEvent: (...event) => events.push(event) } };
  const source = fs.readFileSync(require.resolve('../web.js'), 'utf8');
  const block = source.slice(source.indexOf('// ---------- touch ----------'), source.indexOf('function start()'));
  vm.runInNewContext(block, { window: Object.assign(window, { KnsControls: controlsModule, parent: window }), document, canvas, game, running: true,
    SCREEN: { w: 2048, h: 1184 }, localStorage: { getItem: () => null, setItem() {} },
    settings: { resolution: 'high', memorySaver: false, showFps: false },
    RES_SCALE: { high: 1, medium: 0.75, low: 0.6, verylow: 0.45 }, saveSettings() {}, fitRender() {}, trimAssets() {},
    INDEX: {}, assets: new Map(), cachedNames: new Set(), dl: { active: 0, queue: [] }, CAN_FETCH: false, preload() {}, renderDownloads() {},
    fetch: () => Promise.reject(new Error('offline')), setInterval() {}, setTimeout() {}, clearTimeout() {},
    location: { origin: 'http://localhost', href: 'http://localhost/kns.html' }, sessionStorage: { setItem() {} },
    addEventListener: on(winListeners) });
  const fire = (table, type, e = {}) => { for (const callback of table[type] || []) callback(e); };
  const pointer = (type, x, y, extra = {}) => canvas.listeners[type]({ pointerId: 1, pointerType: 'touch', button: 0, clientX: x, clientY: y, preventDefault() {}, ...extra });
  return { canvas, events, pointer, blur: () => fire(winListeners, 'blur'),
    hide: () => { document.hidden = true; fire(docListeners, 'visibilitychange'); } };
}

test('a held touch is released when the window loses focus, the tab hides or capture is lost', () => {
  for (const leave of ['blur', 'hide', 'lostpointercapture']) {
    const page = touchPage();
    page.pointer('pointerdown', 100, 50);
    page.pointer('pointermove', 110, 60);
    if (leave === 'lostpointercapture') page.pointer('lostpointercapture', 0, 0);
    else page[leave]();
    assert.deepEqual(page.events, [[1, 200, 100, 1, 1], [2, 220, 120, 1, 1], [3, 220, 120, 1, 1]], leave);
    page.pointer('pointerup', 120, 70);   // a late pointerup for the released touch is ignored
    assert.equal(page.events.length, 3, leave);
  }
  // The normal order (pointerup, then lostpointercapture) releases once.
  const page = touchPage();
  page.pointer('pointerdown', 100, 50);
  page.pointer('pointerup', 100, 50);
  page.pointer('lostpointercapture', 0, 0);
  assert.deepEqual(page.events.map(e => e[0]), [1, 3]);
});

test('a captured pointer outside the canvas stays on the screen edge; right and middle clicks are not taps', () => {
  const page = touchPage();
  page.pointer('pointerdown', 10, 10);
  page.pointer('pointermove', -40, 900);
  page.pointer('pointermove', 5000, -3);
  page.pointer('pointerup', 5000, -3);
  assert.deepEqual(page.events, [[1, 20, 20, 1, 1], [2, 0, 1183, 1, 1], [2, 2047, 0, 1, 1], [3, 2047, 0, 1, 1]]);
  const mouse = touchPage();
  for (const button of [2, 1]) mouse.pointer('pointerdown', 10, 10, { pointerType: 'mouse', button });
  assert.deepEqual(mouse.events, []);
  mouse.pointer('pointerdown', 10, 10, { pointerType: 'mouse', button: 0 });
  assert.equal(mouse.events.length, 1);
  let prevented = false;
  mouse.canvas.listeners.contextmenu({ preventDefault() { prevented = true; } });
  assert.ok(prevented);
});

test('a second press from a pointer the game still holds releases the first one', () => {
  const page = touchPage();
  page.pointer('pointerdown', 100, 50);
  page.pointer('pointerdown', 300, 150);
  assert.deepEqual(page.events, [[1, 200, 100, 1, 1], [3, 200, 100, 1, 1], [1, 600, 300, 1, 1]]);
});

test('Escape sends one Back press per key press and an UP only for a press the game saw', () => {
  const source = fs.readFileSync(require.resolve('../web.js'), 'utf8');
  const block = source.slice(source.indexOf('// ---------- the phone\'s back key'), source.indexOf('// ---------- the browser allows sound'));
  const listeners = {}, events = [];
  const context = { game: { java: { addEvent: (...e) => events.push(e[0]) } }, controlsPanelOpen: false,
    addEventListener: (type, callback) => { (listeners[type] = listeners[type] || []).push(callback); } };
  vm.runInNewContext(block, context);
  const fire = (type, e) => { for (const callback of listeners[type] || []) callback(e); };
  fire('keydown', { key: 'Escape' });
  for (let i = 0; i < 5; i++) fire('keydown', { key: 'Escape', repeat: true });
  fire('keyup', { key: 'Escape' });
  assert.deepEqual(events, [0x1000, 0x1001]);
  fire('keyup', { key: 'Escape' });            // e.g. the down went to a text field or the costume picker
  assert.deepEqual(events, [0x1000, 0x1001]);
  fire('keydown', { key: 'Escape' });
  fire('blur', {});                            // released in another window
  fire('keyup', { key: 'Escape' });
  assert.deepEqual(events, [0x1000, 0x1001, 0x1000, 0x1001]);
});

test('a full touch ring keeps presses and releases by replacing the newest move', () => {
  const { installJava } = require('../java_shim.js');
  const java = { classes: {}, env: 1, ref: value => value, bytes: value => value };
  const m8 = new Uint8Array(4096);
  installJava({ log() {}, callJava() {}, cpu: { m8 } }, java, { w: 2048, h: 1184 }, { sym: name => name, cls: name => name });
  java.eventData = { fields: { front: 0, rear: 0 } };
  java.eventBuffer = { direct: 0 };
  const entry = slot => [(m8[slot * 8] << 8) | m8[slot * 8 + 1], (m8[slot * 8 + 2] << 8) | m8[slot * 8 + 3]];
  java.addEvent(1, 10, 10, 1, 1);
  for (let i = 0; i < 300; i++) java.addEvent(2, 20 + i, 10, 1, 1);
  assert.equal(java.eventData.fields.front, 255);          // full: 255 entries waiting
  assert.deepEqual(entry(254), [2, 273]);                   // later moves were dropped
  java.addEvent(3, 30, 10, 1, 0);
  assert.equal(java.eventData.fields.front, 255);
  assert.deepEqual(entry(254), [3, 30]);                    // the release took the newest move's slot
  java.addEvent(1, 40, 10, 2, 1);                           // newest is now a release: nothing to replace
  assert.deepEqual(entry(254), [3, 30]);
  java.eventData.fields.rear = 10;                          // the game read some
  java.addEvent(1, 50, 10, 1, 1);
  assert.deepEqual(entry(255), [1, 50]);
  assert.equal(java.eventData.fields.front, 0);
});
