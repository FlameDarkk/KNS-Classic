const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const Runtime = require('../../../apk-cpu-engine/runtime.js');

test('native achievement ally and book parsers require single objects', () => {
  const html = fs.readFileSync(path.resolve(__dirname, '../../kns.html'), 'utf8');
  const marker = 'window.KNS_ELF="', begin = html.indexOf(marker) + marker.length;
  const rt = new Runtime({ log() {} });
  rt.brk = 0x08000000;
  rt.loadElf(Buffer.from(html.slice(begin, html.indexOf('";', begin)), 'base64'));
  rt.cpu.r[13] = 0x07000000;
  const ally = { fellow_no: 2500, fellow_id: 100444, fellow_level: 1, fellow_max_level: 20 };
  const book = { fellow_id: 100444, recv_reward: 0, exp: 0, level: 1 };
  const parse = (symbol, value, size) => {
    const json = rt.callSync(rt.syms.get('json_loads'), rt.newCstr(JSON.stringify(value)), 0, 0);
    assert.ok(json);
    const buffer = rt.calloc(size);
    rt.callSync(rt.syms.get(symbol), json, buffer);
    return buffer;
  };
  // The old array response silently parsed as an ally with number and ID zero.
  const broken = parse('ParsingDataToFellowInfo', [ally], 0x100);
  assert.equal(rt.cpu.ru32(broken), 0);
  assert.equal(rt.cpu.ru32(broken + 0x20), 0);
  const fixed = parse('ParsingDataToFellowInfo', ally, 0x100);
  assert.equal(rt.cpu.ru32(fixed), 2500);
  assert.equal(rt.cpu.ru32(fixed + 0x20), 100444);
  const brokenBook = parse('ParsingDataToFellowBookInfo', [book], 0x40);
  assert.equal(rt.cpu.ru32(brokenBook + 0xc), 0);
  const fixedBook = parse('ParsingDataToFellowBookInfo', book, 0x40);
  assert.equal(rt.cpu.ru32(fixedBook + 0xc), 100444);
});
