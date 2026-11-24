const assert = require('node:assert/strict');
const test = require('node:test');
const {parse, decode} = require('../asset-validation.js');
const fs = require('node:fs');
const path = require('node:path');
const index = {'asset.bin': [3, 42]};
const atobFn = value => Buffer.from(value, 'base64').toString('binary');
test('accepts only the indexed asset with the indexed decoded size', () => {
  assert.deepEqual([...parse('KNS_ASSET("asset.bin","YWJj");', 'asset.bin', index, atobFn)], [97, 98, 99]);
  assert.throws(() => parse('<html>error</html>', 'asset.bin', index, atobFn));
  assert.throws(() => parse('KNS_ASSET("other.bin","YWJj");', 'asset.bin', index, atobFn));
  assert.throws(() => parse('KNS_ASSET("asset.bin","YWI=");', 'asset.bin', index, atobFn), /length/);
  assert.throws(() => decode('asset.bin', '!!!!', index, atobFn), /invalid/);
  assert.throws(() => decode('other.bin', 'YWJj', index, atobFn), /invalid/);
});

test('rejects misplaced padding, incomplete quartets and invalid alphabet characters', () => {
  for (const encoded of ['YW=J', 'YWJ=', 'YWJj=', 'Y===', '====', 'YQ=A', 'YQ==\n', 'YWJj!!!!']) {
    // YWJ= is structurally valid Base64, but its decoded length disagrees with this asset.
    assert.throws(() => decode('asset.bin', encoded, index, atobFn));
  }
  assert.deepEqual([...decode('one', 'YQ==', {one: [1, 0]}, atobFn)], [97]);
  assert.deepEqual([...decode('two', 'YWI=', {two: [2, 0]}, atobFn)], [97, 98]);
});

test('real large startup texture validates without exhausting the regexp stack', () => {
  const browserDir = path.resolve(__dirname, '../..');
  const html = fs.readFileSync(path.join(browserDir, 'kns.html'), 'utf8');
  const marker = 'window.KNS_INDEX=';
  const start = html.indexOf(marker) + marker.length;
  const indexed = JSON.parse(html.slice(start, html.indexOf(';', start)));
  const name = 'ui_game_new_p_gl.dat.jpg';
  const [size, number] = indexed[name];
  const script = fs.readFileSync(path.join(browserDir, 'kns_data', number + '.js'), 'ascii');
  const data = parse(script, name, indexed, atob);
  assert.equal(data.length, size);
  assert.ok(size > 15_000_000, 'exercise the texture that triggered the reported crash');
  assert.deepEqual([...data.subarray(0, 8)], [137, 80, 78, 71, 13, 10, 26, 10]);
});

const {parseChunks} = require('../asset-validation.js');
// Splits a script into chunks of the given sizes, as a network reader delivers it.
const chunked = (text, sizes) => {
  const bytes = Buffer.from(text, 'latin1'), chunks = [];
  for (let at = 0, i = 0; at < bytes.length; i++) {
    const n = sizes[i % sizes.length];
    chunks.push(new Uint8Array(bytes.subarray(at, at + n)));
    at += n;
  }
  return [chunks, bytes.length];
};
test('byte-level script parsing matches the text parser for every padding and chunk split', () => {
  for (let size = 0; size < 40; size++) {
    const payload = Buffer.from(Array.from({length: size}, (_, i) => (i * 37 + size) & 255));
    const idx = {'a b.dat': [size, 1]};
    const script = 'KNS_ASSET("a b.dat","' + payload.toString('base64') + '");';
    for (const sizes of [[1], [3], [5, 2], [7], [4096]]) {
      const [chunks, total] = chunked(script, sizes);
      assert.deepEqual([...parseChunks(chunks, total, 'a b.dat', idx)], [...parse(script, 'a b.dat', idx, atobFn)]);
    }
  }
});
test('byte-level script parsing rejects what the text parser rejects', () => {
  const bad = (text, name = 'asset.bin') => assert.throws(() => parseChunks(...chunked(text, [5]), name, index));
  bad('<html>error</html>');
  bad('KNS_ASSET("other.bin","YWJj");');
  bad('KNS_ASSET("asset.bin","YWI=");');
  bad('KNS_ASSET("asset.bin","YWJj");', 'other.bin');
  for (const encoded of ['YW=J', 'YWJ=', 'YWJj=', 'Y===', '====', 'YQ=A', 'YWJj!!!!', 'YW\nJj']) bad('KNS_ASSET("asset.bin","' + encoded + '");');
  bad('KNS_ASSET("asset.bin","YWJj"');
  assert.deepEqual([...parseChunks(...chunked('KNS_ASSET("asset.bin","YWJj");\n', [6]), 'asset.bin', index)], [97, 98, 99]);
});
test('real large startup texture decodes from bytes to the same data', () => {
  const browserDir = path.resolve(__dirname, '../..');
  const html = fs.readFileSync(path.join(browserDir, 'kns.html'), 'utf8');
  const marker = 'window.KNS_INDEX=';
  const start = html.indexOf(marker) + marker.length;
  const indexed = JSON.parse(html.slice(start, html.indexOf(';', start)));
  const name = 'ui_game_new_p_gl.dat.jpg';
  const file = fs.readFileSync(path.join(browserDir, 'kns_data', indexed[name][1] + '.js'));
  const chunks = [];
  for (let at = 0; at < file.length; at += 65536) chunks.push(new Uint8Array(file.subarray(at, at + 65536)));
  const data = parseChunks(chunks, file.length, name, indexed);
  assert.ok(Buffer.from(data).equals(Buffer.from(parse(file.toString('ascii'), name, indexed, atob))));
});
