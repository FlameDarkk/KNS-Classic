const assert = require('node:assert/strict');
const test = require('node:test');
const Runtime = require('../../../apk-cpu-engine/runtime.js');

test('native UTC environment controls event dates and unset restores host time', () => {
  const originalZone = process.env.TZ;
  process.env.TZ = 'Europe/Istanbul';
  try {
    const memory = new ArrayBuffer(4096), functions = new Map();
    const rt = Object.create(Runtime.prototype);
    rt.cpu = { m8: new Uint8Array(memory), m32: new Int32Array(memory), r: new Int32Array(16), addTrap: () => 0 };
    rt.def = (name, callback) => functions.set(name, callback);
    rt.staticBlock = () => 256;
    let stringPointer = 1024;
    const string = text => {
      const pointer = stringPointer;
      stringPointer += 64;
      rt.cpu.m8.set(new TextEncoder().encode(text + '\0'), pointer);
      return pointer;
    };
    rt.installLibc();
    const zone = string('TZ'), utc = string('UTC');
    const date = 128, fields = rt.cpu.m32;
    [0, 0, 0, 31, 11, 137].forEach((value, index) => fields[(date >>> 2) + index] = value);
    const utcSeconds = Date.UTC(2037, 11, 31) / 1000;
    const localSeconds = new Date(2037, 11, 31).getTime() / 1000;
    assert.notEqual(utcSeconds, localSeconds, 'test requires a non-UTC host timezone');
    assert.equal(functions.get('mktime')(date), localSeconds);
    functions.get('setenv')(zone, utc);
    assert.equal(functions.get('mktime')(date), utcSeconds);
    assert.ok(utcSeconds < 2 ** 31, 'event end must fit signed 32-bit guest time_t');
    fields[512 >>> 2] = Date.UTC(2026, 10, 3, 23, 30) / 1000;
    functions.get('localtime')(512);
    assert.equal(fields[(256 >>> 2) + 3], 3);
    functions.get('unsetenv')(zone);
    assert.equal(functions.get('mktime')(date), localSeconds);
    functions.get('localtime')(512);
    assert.equal(fields[(256 >>> 2) + 3], 4);
  } finally {
    if (originalZone === undefined) delete process.env.TZ;
    else process.env.TZ = originalZone;
  }
});
