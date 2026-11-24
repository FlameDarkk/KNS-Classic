const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const Runtime = require('../../../apk-cpu-engine/runtime.js');

test('native friend refresh copies the server cooldown into the existing friend record', () => {
  const html = fs.readFileSync(path.resolve(__dirname, '../../kns.html'), 'utf8');
  const marker = 'window.KNS_ELF="', begin = html.indexOf(marker) + marker.length;
  const rt = new Runtime({log() {}});
  rt.brk = 0x08000000;
  rt.loadElf(Buffer.from(html.slice(begin, html.indexOf('";', begin)), 'base64'));
  rt.cpu.r[13] = 0x07000000;
  const list = rt.calloc(0x3000), record = list + 0x100, update = list + 0x1100;
  // Active ELF: list count at +0, data pointer at +0xc, record size 0x8d8; cooldown at +0x8c8.
  rt.cpu.wu32(list, 1);
  rt.cpu.wu32(list + 0xc, record);
  rt.writeCstr(record, '42');
  const json = rt.callSync(rt.syms.get('json_loads'), rt.newCstr(JSON.stringify({
    user_no: 42, user_hub_uid: '42', send_friendship_point_remain_time: 86400,
  })), 0, 0);
  assert.ok(json);
  rt.callSync(rt.syms.get('ParsingDataToFriendInfo'), json, update);
  assert.equal(rt.cpu.ru32(update + 0x8c8), 86400);
  assert.equal(rt.cpu.ru32(record + 0x8c8), 0);
  rt.callSync(rt.syms.get('UpDateDataFriendInfo'), list, update);
  assert.equal(rt.cpu.ru32(record + 0x8c8), 86400);
});
