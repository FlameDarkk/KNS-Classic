const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const vm = require('node:vm');

function page(session, fetch, withPopup = false) {
  const source = fs.readFileSync(require.resolve('../web.js'), 'utf8');
  const listeners = {}, documentListeners = {}, timers = [], hoverTimers = [];
  function element() {
    return {
      children: [], style: {}, listeners: {},
      setAttribute() {},
      append(...children) { this.children.push(...children); },
      appendChild(child) { this.children.push(child); },
      replaceChildren(fragment) { this.children = fragment.children; },
      addEventListener(name, callback) { this.listeners[name] = callback; },
      getBoundingClientRect: () => ({ left: 12, right: 120, bottom: 200 }),
      contains(child) { return this.children.includes(child); },
    };
  }
  const body = element();
  const context = {
    window: { KNS_SERVER: 'https://game.example/kns-api', KnsSession: session,
      addEventListener: (name, callback) => { listeners[name] = callback; } },
    document: { body, hidden: false, createElement: element, createDocumentFragment: element,
      addEventListener: (name, callback) => { documentListeners[name] = listeners[name] = callback; } },
    canvas: { getBoundingClientRect: () => ({ left: 200 }) },
    isFullscreen: () => false,
    onlineCount: null, onlineNames: [], renderOnline() {}, fetch,
    setInterval: (callback, delay) => timers.push({ callback, delay }),
    setTimeout: callback => hoverTimers.push(callback), clearTimeout() {},
  };
  vm.createContext(context);
  const start = withPopup ? '// Online players under' : 'let onlinePolling =';
  vm.runInContext(source.slice(source.indexOf(start), source.indexOf('// Event dungeon calendar')), context);
  return { context, listeners, documentListeners, timers, hoverTimers, body };
}
const flush = () => new Promise(resolve => setImmediate(resolve));
const reply = (online, players) => ({ ok: true, json: async () => ({ online, players }) });

test('open game sends authenticated heartbeats every 30 seconds and refreshes on return', async () => {
  const calls = [];
  const { listeners, timers } = page({ userNo: 42, gateway: { headers: { USERNO: '42', SESSIONKEY: 'key' } } },
    async (url, options) => { calls.push({ url, options }); return reply(1, ['Player']); });
  await flush();
  assert.equal(timers[0].delay, 30000);
  for (const callback of [timers[0].callback, listeners.focus, listeners.pageshow, listeners.visibilitychange]) {
    await callback(); await flush();
  }
  assert.equal(calls.length, 5);
  for (const { url, options } of calls) {
    assert.equal(url, 'https://game.example/kns-api/api/online');
    assert.equal(options.method, 'POST');
    assert.equal(options.headers.USERNO, '42');
    assert.equal(options.headers.SESSIONKEY, 'key');
    assert.equal(options.cache, 'no-store');
  }
});

test('login during an anonymous poll queues a heartbeat instead of leaving the stale zero', async () => {
  const pending = [], calls = [];
  const { context, listeners } = page(null, (url, options) => {
    calls.push(options); return new Promise(resolve => pending.push(resolve));
  });
  context.window.KnsSession = { userNo: 42, gateway: { headers: { USERNO: '42', SESSIONKEY: 'new-key' } } };
  listeners['kns-session-changed']();
  assert.equal(calls.length, 1);
  assert.equal(calls[0].headers.USERNO, undefined);
  pending[0](reply(0, [])); await flush();
  assert.equal(calls.length, 2);
  assert.equal(calls[1].headers.SESSIONKEY, 'new-key');
  pending[1](reply(1, ['Player'])); await flush();
  assert.equal(context.onlineCount, 1);
  assert.equal(context.onlineNames[0], 'Player');
});

test('mismatched saved session headers cannot heartbeat another account', async () => {
  let headers;
  page({ userNo: 42, gateway: { headers: { USERNO: '99', SESSIONKEY: 'old-key' } } },
    async (url, options) => { headers = options.headers; return reply(0, []); });
  await flush();
  assert.equal(headers.USERNO, undefined);
  assert.equal(headers.SESSIONKEY, undefined);
});

test('the online popup shows all names, refreshes while open, and still dismisses', async () => {
  let names = ['RosterName', 'RosterName', 'Squire10101', 'Squire10102', 'Squire10103', 'Squire10104'];
  const { body, documentListeners, timers, hoverTimers } = page(null, async () => reply(names.length, names), true);
  await flush();
  const [count, popup] = body.children;
  const [header, list] = popup.children;
  const [title, close] = header.children;
  const clickCount = () => count.listeners.click({ stopPropagation() {} });
  assert.equal(count.textContent, '6 players online');
  count.listeners.mouseenter();
  hoverTimers[0]();
  assert.equal(popup.hidden, false);
  count.listeners.mouseleave();
  assert.equal(popup.hidden, true);
  clickCount();
  assert.equal(title.textContent, 'Players online (6)');
  assert.deepEqual(list.children.map(row => row.textContent), names);

  names = Array.from({ length: 501 }, (_, i) => 'Squire' + (10101 + i));
  await timers[0].callback();
  assert.equal(popup.hidden, false);
  assert.equal(title.textContent, 'Players online (501)');
  assert.deepEqual(list.children.map(row => row.textContent), names);

  for (const dismiss of [
    clickCount,
    () => close.listeners.click({ stopPropagation() {} }),
    () => documentListeners.keydown({ key: 'Escape' }),
    () => documentListeners.pointerdown({ target: body }),
  ]) {
    dismiss();
    assert.equal(popup.hidden, true);
    clickCount();
    assert.equal(popup.hidden, false);
  }
});
