const assert = require('node:assert/strict');
const test = require('node:test');
require('../../../apk-cpu-engine/gl.js');

test('native level-up brightness selects texture addition/subtraction and restores modulation', () => {
  const gl = Object.create(globalThis.ApkGLES1.prototype);
  Object.assign(gl, {texUnit: 0, env: 0, envMode: 0x2100, combineRgb: 0x2100, env1: {}});
  gl.glTexEnvx(0x2300, 0x2200, 0x8570);
  gl.glTexEnvx(0x2300, 0x8571, 0x0104);
  assert.equal(gl.env, 3); // TEXTURE + PRIMARY_COLOR, not black * texture.
  gl.glTexEnvx(0x2300, 0x8571, 0x84e7);
  assert.equal(gl.env, 5); // TEXTURE - PRIMARY_COLOR.
  gl.glTexEnvx(0x2300, 0x2200, 0x2100);
  assert.equal(gl.env, 0);
  gl.glTexEnvx(0x2300, 0x8571, 0x0104);
  assert.equal(gl.env, 0); // COMBINE_RGB does not override a non-COMBINE environment.
});
