// Validate game-data script contents before the native file system can read them.
(function (root) {
'use strict';
function decode(name, encoded, index, atobFn) {
  if (!Object.hasOwn(index, name) || typeof encoded !== 'string') throw new Error('invalid asset data');
  // Repeating a grouped quartet can overflow V8's regexp stack on large textures.
  // A flat character search keeps validation linear without weakening padding checks.
  const padding = encoded.endsWith('==') ? 2 : encoded.endsWith('=') ? 1 : 0;
  const firstInvalid = encoded.search(/[^A-Za-z0-9+/]/);
  if (encoded.length % 4 !== 0 || (firstInvalid !== -1 && firstInvalid !== encoded.length - padding)) throw new Error('invalid asset data');
  const binary = atobFn(encoded);
  const len = binary.length;
  if (len !== index[name][0]) throw new Error('asset length mismatch');
  const data = new Uint8Array(len);
  let i = 0;
  const end4 = len - 3;
  for (; i < end4; i += 4) {
    data[i] = binary.charCodeAt(i);
    data[i + 1] = binary.charCodeAt(i + 1);
    data[i + 2] = binary.charCodeAt(i + 2);
    data[i + 3] = binary.charCodeAt(i + 3);
  }
  for (; i < len; i++) data[i] = binary.charCodeAt(i);
  return data;
}
function parse(text, expectedName, index, atobFn) {
  const match = /^\s*KNS_ASSET\((.*)\);?\s*$/s.exec(text);
  if (!match) throw new Error('invalid asset script');
  const args = JSON.parse('[' + match[1] + ']');
  if (args.length !== 2 || args[0] !== expectedName) throw new Error('asset name mismatch');
  return decode(args[0], args[1], index, atobFn);
}
// The same checks on the downloaded bytes of a script, given as the chunks the network delivered.
// Decodes straight into the result: no string of the whole file, which for a 20 MB texture held
// three more copies of it during a download.
const B64 = new Int8Array(256).fill(-1);
for (let i = 0; i < 64; i++) B64['ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/'.charCodeAt(i)] = i;
function parseChunks(chunks, total, expectedName, index) {
  if (!Object.hasOwn(index, expectedName)) throw new Error('invalid asset data');
  const at = pos => {
    for (const c of chunks) { if (pos < c.length) return c[pos]; pos -= c.length; }
    return -1;
  };
  let head = '';
  for (let i = 0, n = Math.min(total, 2048); i < n; i++) head += String.fromCharCode(at(i));
  const match = /^\s*KNS_ASSET\(("(?:[^"\\]|\\.)*"),"/.exec(head);
  if (!match) throw new Error('invalid asset script');
  if (JSON.parse(match[1]) !== expectedName) throw new Error('asset name mismatch');
  const start = match[0].length, tailAt = Math.max(start, total - 16);
  let tail = '';
  for (let i = tailAt; i < total; i++) tail += String.fromCharCode(at(i));
  const quote = tail.lastIndexOf('"');
  if (quote < 0 || !/^"\);?\s*$/.test(tail.slice(quote))) throw new Error('invalid asset script');
  const end = tailAt + quote, len = end - start;
  if (len % 4 !== 0) throw new Error('invalid asset data');
  const padding = len && at(end - 1) === 61 ? (at(end - 2) === 61 ? 2 : 1) : 0;
  const size = len / 4 * 3 - padding;
  if (size !== index[expectedName][0]) throw new Error('asset length mismatch');
  const data = new Uint8Array(size), dataEnd = end - padding;
  let acc = 0, n = 0, o = 0, base = 0;
  for (const c of chunks) {
    const hi = Math.min(dataEnd - base, c.length);
    for (let i = Math.max(start - base, 0); i < hi; i++) {
      const v = B64[c[i]];
      if (v < 0) throw new Error('invalid asset data');
      acc = (acc << 6) | v;
      if (++n === 4) { data[o] = acc >> 16; data[o + 1] = acc >> 8; data[o + 2] = acc; o += 3; acc = 0; n = 0; }
    }
    base += c.length;
  }
  if (n !== (padding ? 4 - padding : 0)) throw new Error('invalid asset data');
  if (n === 3) { data[o] = acc >> 10; data[o + 1] = acc >> 2; } else if (n === 2) data[o] = acc >> 4;
  return data;
}
if (typeof module !== 'undefined' && module.exports) module.exports = {decode, parse, parseChunks};
else root.KnsAssetValidation = {decode, parse, parseChunks};
})(typeof window !== 'undefined' ? window : globalThis);
