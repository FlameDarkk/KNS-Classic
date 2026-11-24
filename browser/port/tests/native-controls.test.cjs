const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const Runtime = require('../../../apk-cpu-engine/runtime.js');
const {createControls, readNativeTargets} = require('../controls.js');

test('saved bindings hit six distinct actions through the active native circle hit tester in Field and Raid', () => {
  const html = fs.readFileSync(path.resolve(__dirname, '../../kns.html'), 'utf8');
  const marker = 'window.KNS_ELF="';
  const begin = html.indexOf(marker) + marker.length;
  const elf = Buffer.from(html.slice(begin, html.indexOf('";', begin)), 'base64');
  const rt = new Runtime({log() {}});
  rt.brk = 0x08000000;
  rt.loadElf(elf);
  rt.cpu.r[13] = 0x07000000;
  const bindings = {block:'Space',attack:'KeyE',throw:'KeyR',ally1:'KeyD',ally2:'KeyF',ally3:'KeyC'};
  const actions = {block:3,attack:5,throw:6,ally1:8,ally2:9,ally3:10};
  for (const scene of [6,13]) {
    const touch = rt.syms.get(scene === 6 ? 'touchField' : 'touchRaid');
    const rows = 0x06000000;
    rt.cpu.wu32(touch + 4, 6); rt.cpu.wu32(touch + 8, rows);
    let index = 0;
    for (const id of Object.values(actions)) {
      const row = rows + index * 24, top = scene === 6 ? 600 : 800, left = 100 + index * 250;
      [top,top+120,left,left+120,0,id].forEach((value,i) => rt.cpu.wu32(row+i*4,value));
      index++;
    }
    const state = () => ({scene,popup:0xffffffff,targets:readNativeTargets(rt,scene)});
    let expected;
    const hits = [];
    const controls = createControls({bindings,state,physicalCount:()=>0,send(type,x,y) {
      if (type !== 1) return;
      const actual = rt.callSync(rt.syms.get('GetTouchCicleAreaID'),touch,0,x,y);
      hits.push(actual);
      assert.equal(actual, expected);
    }});
    for (const [action,code] of Object.entries(bindings)) {
      expected = actions[action];
      const event = {code,target:{tagName:'CANVAS'},preventDefault() {}};
      assert.equal(controls.down(event), true);
      controls.up(event);
    }
    assert.equal(new Set(hits).size, 6);
  }
});
