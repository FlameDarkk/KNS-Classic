const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const vm = require('node:vm');

// The settings and asset-memory section of web.js, run on its own with a fake storage.
const source = fs.readFileSync(require.resolve('../web.js'), 'utf8');
const block = source.slice(source.indexOf('// ---------- settings:'), source.indexOf('function acceptAsset'));
function load(stored, deviceMemory) {
  const store = new Map(Object.entries(stored));
  const ctx = { assets: new Map(), navigator: { deviceMemory }, performance: { now: () => ctx.now }, now: 100000,
    safeGet: k => (store.has(k) ? store.get(k) : null), safeSet: (k, v) => store.set(k, v), safeDel: k => store.delete(k),
    loadAsset() {} };
  vm.runInNewContext(block + '\nthis.api = { settings, saveSettings, trimAssets, dropAsset, useAsset, cachedNames, acceptedAt, bytes: () => assetBytes, add(n, d) { assets.set(n, d); assetBytes += d.length; } };', ctx);
  return { ...ctx.api, store, assets: ctx.assets, ctx };
}

test('settings default to the behaviour without the panel, and bad saved values are ignored', () => {
  assert.deepEqual({ ...load({}, 8).settings }, { resolution: 'high', memorySaver: false, showFps: false });
  assert.equal(load({}, 4).settings.memorySaver, true);
  assert.equal(load({}, undefined).settings.memorySaver, false);
  const s = load({ 'kns-settings-v1': '{"resolution":"ultra","memorySaver":false,"showFps":true}' }, 2).settings;
  assert.deepEqual({ ...s }, { resolution: 'high', memorySaver: false, showFps: true });
  assert.equal(load({ 'kns-settings-v1': '{broken' }, 8).settings.resolution, 'high');
});

test('the earlier Low-spec checkbox becomes Resolution Low and is replaced on the first save', () => {
  const page = load({ 'kns-lowspec-v1': '1' }, 8);
  assert.equal(page.settings.resolution, 'low');
  page.saveSettings();
  assert.equal(page.store.has('kns-lowspec-v1'), false);
  assert.equal(JSON.parse(page.store.get('kns-settings-v1')).resolution, 'low');
  assert.equal(load({ 'kns-lowspec-v1': '1', 'kns-settings-v1': '{"resolution":"medium"}' }, 8).settings.resolution, 'medium');
});

test('asset memory drops the least recently used cached files above the cap and nothing else', () => {
  const page = load({ 'kns-settings-v1': '{"memorySaver":true}' }, 8); // 96 MB cap
  const mb = n => ({ length: n * 1048576 });
  for (const [name, size] of [['a', 40], ['b', 40], ['uncached', 40], ['fresh', 40], ['c', 40]]) page.add(name, mb(size));
  for (const name of ['a', 'b', 'fresh', 'c']) page.cachedNames.add(name);
  page.acceptedAt.set('fresh', page.ctx.now - 100); // arrived a moment ago: the game is about to open it
  page.useAsset('a');                               // a is now the most recently used
  page.trimAssets('c');
  assert.deepEqual([...page.assets.keys()], ['uncached', 'fresh', 'c']);
  assert.equal(page.bytes(), 120 * 1048576);
  page.ctx.now += 5000;
  page.trimAssets();
  assert.deepEqual([...page.assets.keys()], ['uncached', 'c']);
  page.settings.memorySaver = false;                // 320 MB cap: nothing to drop
  page.add('d', mb(200));
  page.trimAssets();
  assert.equal(page.assets.size, 3);
});
