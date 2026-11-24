// OpenGL ES 1.1 fixed function on WebGL 1. Each draw reads the guest's client arrays or its
// shadowed VBOs, converts GL_FIXED data to float, and streams it to the GPU.
(function (root) {
'use strict';
const f2 = new DataView(new ArrayBuffer(4));
const bitsF = w => { f2.setInt32(0, w, true); return f2.getFloat32(0, true); };
const FIX = 1 / 65536;

const VS = `
attribute vec4 a_pos; attribute vec4 a_color; attribute vec4 a_tex;
uniform mat4 u_mv; uniform mat4 u_proj; uniform mat4 u_texm; uniform float u_psize;
varying vec4 v_color; varying vec2 v_tex;
void main(){
  gl_Position = u_proj * (u_mv * a_pos);
  v_color = a_color;
  vec4 t = u_texm * a_tex;
  v_tex = t.xy;
  gl_PointSize = u_psize;
}`;
const FS = `
precision mediump float;
varying vec4 v_color; varying vec2 v_tex;
uniform sampler2D u_s; uniform int u_useTex; uniform int u_env; uniform int u_fmtA;
uniform int u_alphaFn; uniform float u_alphaRef; uniform vec4 u_envColor;
uniform int u_c1; uniform int u_c1Rgb; uniform int u_c1A; uniform ivec3 u_c1Src; uniform ivec3 u_c1Op; uniform ivec3 u_c1SrcA; uniform ivec3 u_c1OpA;
vec4 c1Source(int src, vec4 prev){
  if (src == 1) return u_envColor;
  if (src == 3) return v_color;
  return prev;
}
vec3 c1Arg(int src, int op, vec4 prev){
  vec4 v = c1Source(src, prev);
  if (op == 0) return v.rgb;
  if (op == 1) return 1.0 - v.rgb;
  if (op == 2) return vec3(v.a);
  return vec3(1.0 - v.a);
}
float c1ArgA(int src, int op, vec4 prev){
  float a = c1Source(src, prev).a;
  return op == 3 ? 1.0 - a : a;
}
void main(){
  vec4 c = v_color;
  if (u_useTex == 1) {
    vec4 t = texture2D(u_s, v_tex);
    if (u_fmtA == 1) t = vec4(1.0, 1.0, 1.0, t.a);
    if (u_env == 0) c = c * t;
    else if (u_env == 1) c = t;
    else if (u_env == 2) c = vec4(mix(c.rgb, t.rgb, t.a), c.a);
    else if (u_env == 3) c = vec4(clamp(c.rgb + t.rgb, 0.0, 1.0), c.a * t.a);
    else if (u_env == 4) c = vec4(mix(c.rgb, u_envColor.rgb, t.rgb), c.a * t.a);
    else if (u_env == 5) c = vec4(clamp(t.rgb - c.rgb, 0.0, 1.0), c.a * t.a);
  }
  if (u_c1 == 1) {
    vec4 prev = c;
    vec3 a0 = c1Arg(u_c1Src.x, u_c1Op.x, prev), a1 = c1Arg(u_c1Src.y, u_c1Op.y, prev), a2 = c1Arg(u_c1Src.z, u_c1Op.z, prev);
    if (u_c1Rgb == 0) c.rgb = a0;
    else if (u_c1Rgb == 1) c.rgb = a0 * a1;
    else if (u_c1Rgb == 2) c.rgb = clamp(a0 + a1, 0.0, 1.0);
    else if (u_c1Rgb == 3) c.rgb = clamp(a0 + a1 - 0.5, 0.0, 1.0);
    else if (u_c1Rgb == 4) c.rgb = a0 * a2 + a1 * (1.0 - a2);
    else c.rgb = clamp(a0 - a1, 0.0, 1.0);
    float b0 = c1ArgA(u_c1SrcA.x, u_c1OpA.x, prev), b1 = c1ArgA(u_c1SrcA.y, u_c1OpA.y, prev), b2 = c1ArgA(u_c1SrcA.z, u_c1OpA.z, prev);
    if (u_c1A == 0) c.a = b0;
    else if (u_c1A == 1) c.a = b0 * b1;
    else if (u_c1A == 2) c.a = clamp(b0 + b1, 0.0, 1.0);
    else if (u_c1A == 3) c.a = clamp(b0 + b1 - 0.5, 0.0, 1.0);
    else if (u_c1A == 4) c.a = b0 * b2 + b1 * (1.0 - b2);
    else c.a = clamp(b0 - b1, 0.0, 1.0);
  }
  if (u_alphaFn != 0) {
    bool pass = true;
    if (u_alphaFn == 1) pass = false;
    else if (u_alphaFn == 2) pass = c.a < u_alphaRef;
    else if (u_alphaFn == 3) pass = abs(c.a - u_alphaRef) < 0.002;
    else if (u_alphaFn == 4) pass = c.a <= u_alphaRef;
    else if (u_alphaFn == 5) pass = c.a > u_alphaRef;
    else if (u_alphaFn == 6) pass = abs(c.a - u_alphaRef) >= 0.002;
    else if (u_alphaFn == 7) pass = c.a >= u_alphaRef;
    if (!pass) discard;
  }
  gl_FragColor = c;
}`;

const FN1 = { 0x1E01: 0, 0x2100: 1, 0x0104: 2, 0x8574: 3, 0x8575: 4, 0x84E7: 5 };
const SRC1 = { 0x8578: 0, 0x1702: 0, 0x8576: 1, 0x8577: 3 };
const OP1 = { 0x300: 0, 0x301: 1, 0x302: 2, 0x303: 3 };
const opA1 = v => (v === 0x303 ? 3 : 2);
const scratchMul = new Float32Array(16), scratchM = new Float32Array(16);
function mat() { const m = new Float32Array(16); m[0] = m[5] = m[10] = m[15] = 1; return m; }
function identity(m) { m.fill(0); m[0] = m[5] = m[10] = m[15] = 1; return m; }
function mul(a, b, out) {
  const r = scratchMul;
  for (let c = 0; c < 4; c++) {
    for (let w = 0; w < 4; w++) {
      r[c * 4 + w] = a[w] * b[c * 4] + a[w + 4] * b[c * 4 + 1] + a[w + 8] * b[c * 4 + 2] + a[w + 12] * b[c * 4 + 3];
    }
  }
  out.set(r);
}

const NO_TEX = [0, 0, 0, 1];
const IDENT = mat();
// Batched vertex: position after modelview (x, y, z, w), color (r, g, b, a), texcoord (s, t).
const BATCH_FLOATS = 10;
const SIG_LEN = 48;
const fr = Math.fround;
const PRESENT_OFF = new Set([0x0BE2, 0x0C11, 0x0B71, 0x0B90, 0x0B44]);

class GLES1 {
  constructor(canvas, log) {
    const gl = canvas.getContext('webgl', {
      alpha: false, antialias: false, depth: false, stencil: false, premultipliedAlpha: false,
      preserveDrawingBuffer: false, powerPreference: 'high-performance',
    });
    if (!gl) throw new Error('WebGL not available');
    this.gl = gl;
    this.log = log;
    this.canvas = canvas;
    const sh = (type, src) => {
      const s = gl.createShader(type);
      gl.shaderSource(s, src);
      gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s));
      return s;
    };
    const p = gl.createProgram();
    gl.attachShader(p, sh(gl.VERTEX_SHADER, VS));
    gl.attachShader(p, sh(gl.FRAGMENT_SHADER, FS));
    gl.bindAttribLocation(p, 0, 'a_pos');
    gl.bindAttribLocation(p, 1, 'a_color');
    gl.bindAttribLocation(p, 2, 'a_tex');
    gl.linkProgram(p);
    if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(p));
    gl.useProgram(p);
    this.u = {};
    for (const n of ['u_mv', 'u_proj', 'u_texm', 'u_psize', 'u_s', 'u_useTex', 'u_env', 'u_alphaFn', 'u_alphaRef', 'u_envColor', 'u_fmtA', 'u_c1', 'u_c1Rgb', 'u_c1A', 'u_c1Src', 'u_c1Op', 'u_c1SrcA', 'u_c1OpA']) {
      this.u[n] = gl.getUniformLocation(p, n);
    }
    gl.uniform1i(this.u.u_s, 0);
    gl.uniform1f(this.u.u_psize, 1);
    this.stacks = { 0x1700: [mat()], 0x1701: [mat()], 0x1702: [mat()] };
    this.mode = 0x1700;
    this.texUnit = 0;
    this.clientUnit = 0;
    this.color = [1, 1, 1, 1];
    this.arrays = {
      v: { on: false, size: 4, type: 0x1406, stride: 0, ptr: 0, vbo: 0 },
      c: { on: false, size: 4, type: 0x1406, stride: 0, ptr: 0, vbo: 0 },
      t: { on: false, size: 4, type: 0x1406, stride: 0, ptr: 0, vbo: 0 },
      n: { on: false, size: 3, type: 0x1406, stride: 0, ptr: 0, vbo: 0 },
    };
    this.tex2d = false;
    this.tex2d1 = false;
    this.env1 = {};
    this.envColor1 = [0, 0, 0, 0];
    this.env = 0;
    this.envMode = 0x2100;
    this.combineRgb = 0x2100;
    this.alphaOn = false;
    this.alphaFn = 8;
    this.alphaRef = 0;
    this.textures = new Map();
    this.nextTex = 1;
    this.bound = 0;
    this.buffers = new Map();
    this.nextBuf = 1;
    this.boundArray = 0;
    this.boundElement = 0;
    this.stream = gl.createBuffer();
    this.istream = gl.createBuffer();
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, this.istream);
    this.err = 0;
    // The game always sees a SW x SH screen. The offscreen framebuffer and the canvas may be
    // smaller (render scale): viewport, scissor, copies and line/point sizes are scaled to it.
    this.SW = canvas.width;
    this.SH = canvas.height;
    this.sx = 1;
    this.sy = 1;
    this.wantW = canvas.width;
    this.viewport = [0, 0, canvas.width, canvas.height];
    this.drawCalls = 0;
    this.seenCap = new Set();
    this.caps = new Map();   // the capabilities the game has switched on
    this.mask = [true, true, true, true];
    this.uv = new Map();     // uniform values last sent
    this.mv = [new Float32Array(16).fill(NaN), new Float32Array(16).fill(NaN), new Float32Array(16).fill(NaN)];
    this.attribOn = [undefined, undefined, undefined];
    this.attribVal = [null, null, null];
    this.floats = new Float32Array(4096);
    // Batching: consecutive triangle draws with the same state go to the GPU as one draw.
    // Positions are moved by the modelview on the CPU, colors become per-vertex. ApkGLES1.batching = false turns it off.
    this.batching = GLES1.batching !== false;
    this.bverts = new Float32Array(BATCH_FLOATS * 6 * 512);
    this.bn = 0;                      // vertices waiting
    this.bsig = new Float64Array(SIG_LEN);
    this.sig = new Float64Array(SIG_LEN);
    this.batchDraws = 0;
    this.batchFlushes = 0;
    this.ptVer = 0;                   // bumped by every projection / texture matrix change
    this.sigPtVer = -1;
    this.bPtVer = -1;
    gl.pixelStorei(gl.UNPACK_ALIGNMENT, 4);
    gl.enable(gl.DITHER);
    this.prog = p;
    this.setupFrameBuffer();
  }
  // The game draws offscreen; the canvas only ever shows finished frames. Without this the
  // browser composites whatever part of a frame exists when the page yields.
  setupFrameBuffer() {
    const gl = this.gl, W = this.canvas.width, H = this.canvas.height;
    this.fboTex = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, this.fboTex);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, W, H, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
    for (const [k, v] of [[gl.TEXTURE_MIN_FILTER, gl.LINEAR], [gl.TEXTURE_MAG_FILTER, gl.LINEAR], [gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE], [gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE]]) {
      gl.texParameteri(gl.TEXTURE_2D, k, v);
    }
    this.fbo = gl.createFramebuffer();
    gl.bindFramebuffer(gl.FRAMEBUFFER, this.fbo);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, this.fboTex, 0);
    const rb = gl.createRenderbuffer();
    this.fboDepth = rb;
    gl.bindRenderbuffer(gl.RENDERBUFFER, rb);
    gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH_STENCIL, W, H);
    gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_STENCIL_ATTACHMENT, gl.RENDERBUFFER, rb);
    if (gl.checkFramebufferStatus(gl.FRAMEBUFFER) !== gl.FRAMEBUFFER_COMPLETE) throw new Error('offscreen framebuffer incomplete');
    gl.clearColor(0, 0, 0, 1);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT | gl.STENCIL_BUFFER_BIT);
    gl.bindTexture(gl.TEXTURE_2D, null);
    const sh = (type, src) => { const o = gl.createShader(type); gl.shaderSource(o, src); gl.compileShader(o); return o; };
    const pp = gl.createProgram();
    gl.attachShader(pp, sh(gl.VERTEX_SHADER, 'attribute vec2 a_pos;varying vec2 v;void main(){v=a_pos*0.5+0.5;gl_Position=vec4(a_pos,0.0,1.0);}'));
    gl.attachShader(pp, sh(gl.FRAGMENT_SHADER, 'precision mediump float;varying vec2 v;uniform sampler2D s;void main(){gl_FragColor=vec4(texture2D(s,v).rgb,1.0);}'));
    gl.bindAttribLocation(pp, 0, 'a_pos');
    gl.linkProgram(pp);
    this.presentProg = pp;
    this.quad = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, this.quad);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
    this.presents = 0;
  }
  // Render width in device pixels, at most the game's own width; applied at the next flip.
  setRenderWidth(w) {
    this.wantW = Math.max(64, Math.min(this.SW, Math.round(w)));
  }
  // Between frames: resize the framebuffer and canvas, then send viewport and scissor again.
  applyRenderWidth() {
    const gl = this.gl, W = this.wantW, H = Math.round(this.SH * W / this.SW);
    if (W === this.canvas.width && H === this.canvas.height) return;
    this.canvas.width = W;
    this.canvas.height = H;
    gl.bindTexture(gl.TEXTURE_2D, this.fboTex);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, W, H, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
    gl.bindRenderbuffer(gl.RENDERBUFFER, this.fboDepth);
    gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH_STENCIL, W, H);
    const e = this.textures.get(this.bound);
    gl.bindTexture(gl.TEXTURE_2D, e ? e.tex : null);
    this.sx = W / this.SW;
    this.sy = H / this.SH;
    const v = this.viewport;
    gl.viewport(this.scX(v[0]), this.scY(v[1]), this.scX(v[0] + v[2]) - this.scX(v[0]), this.scY(v[1] + v[3]) - this.scY(v[1]));
    const sc = this.scis;
    if (sc) gl.scissor(this.scX(sc[0]), this.scY(sc[1]), this.scX(sc[0] + sc[2]) - this.scX(sc[0]), this.scY(sc[1] + sc[3]) - this.scY(sc[1]));
  }
  scX(x) { return this.sx === 1 ? x : Math.round(x * this.sx); }
  scY(y) { return this.sy === 1 ? y : Math.round(y * this.sy); }
  // The game's frame flip (its eglSwapBuffers / requestRender equivalent).
  present() {
    if (this.bn) this.flush();
    const gl = this.gl;
    const caps = this.caps, mask = this.mask;
    for (const [c, on] of caps) if (on && PRESENT_OFF.has(c)) gl.disable(c);
    gl.colorMask(true, true, true, true);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    gl.viewport(0, 0, this.canvas.width, this.canvas.height);
    gl.useProgram(this.presentProg);
    gl.bindTexture(gl.TEXTURE_2D, this.fboTex);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.quad);
    gl.enableVertexAttribArray(0);
    gl.disableVertexAttribArray(1);
    gl.disableVertexAttribArray(2);
    this.attribOn[0] = true;
    this.attribOn[1] = this.attribOn[2] = false;
    gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
    gl.bindFramebuffer(gl.FRAMEBUFFER, this.fbo);
    gl.useProgram(this.prog);
    const e = this.textures.get(this.bound);
    gl.bindTexture(gl.TEXTURE_2D, e ? e.tex : null);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.stream);
    this.curArrayBuf = this.stream;
    gl.colorMask(mask[0], mask[1], mask[2], mask[3]);
    const v = this.viewport;
    gl.viewport(this.scX(v[0]), this.scY(v[1]), this.scX(v[0] + v[2]) - this.scX(v[0]), this.scY(v[1] + v[3]) - this.scY(v[1]));
    for (const [c, on] of caps) if (on && PRESENT_OFF.has(c)) gl.enable(c);
    this.presents++;
    if (this.wantW !== this.canvas.width) this.applyRenderWidth();
    if (this.debugClear) {
      // Test aid: paints the buffer so anything the next frame leaves undrawn stands out.
      const sc = gl.isEnabled(gl.SCISSOR_TEST);
      gl.disable(gl.SCISSOR_TEST);
      const old = gl.getParameter(gl.COLOR_CLEAR_VALUE);
      gl.clearColor(this.debugClear[0], this.debugClear[1], this.debugClear[2], 1);
      gl.clear(gl.COLOR_BUFFER_BIT);
      gl.clearColor(old[0], old[1], old[2], old[3]);
      if (sc) gl.enable(gl.SCISSOR_TEST);
    }
  }
  attach(rt) {
    this.rt = rt;
    this.cpu = rt.cpu;
  }

  top() { const s = this.stacks[this.mode]; return s[s.length - 1]; }
  glMatrixMode(m) { this.mode = m; }
  ptTouch() { if (this.mode !== 0x1700) this.ptVer++; }
  glLoadIdentity() { this.ptTouch(); identity(this.top()); }
  glPushMatrix() { this.ptTouch(); const s = this.stacks[this.mode]; s.push(s[s.length - 1].slice()); }
  glPopMatrix() { this.ptTouch(); const s = this.stacks[this.mode]; if (s.length > 1) s.pop(); }
  glLoadMatrixf(p) { this.ptTouch(); this.top().set(this.cpu.f32.subarray(p >>> 2, (p >>> 2) + 16)); }
  readX(p) { const m = scratchM; for (let i = 0; i < 16; i++) m[i] = this.cpu.m32[(p >>> 2) + i] * FIX; return m; }
  glLoadMatrixx(p) { this.ptTouch(); this.top().set(this.readX(p)); }
  glMultMatrixx(p) { this.ptTouch(); mul(this.top(), this.readX(p), this.top()); }
  translate(x, y, z) { this.ptTouch(); const m = identity(scratchM); m[12] = x; m[13] = y; m[14] = z; mul(this.top(), m, this.top()); }
  scale(x, y, z) { this.ptTouch(); const m = identity(scratchM); m[0] = x; m[5] = y; m[10] = z; mul(this.top(), m, this.top()); }
  glTranslatex(x, y, z) { this.translate(x * FIX, y * FIX, z * FIX); }
  glTranslatef(x, y, z) { this.translate(bitsF(x), bitsF(y), bitsF(z)); }
  glScalex(x, y, z) { this.scale(x * FIX, y * FIX, z * FIX); }
  glScalef(x, y, z) { this.scale(bitsF(x), bitsF(y), bitsF(z)); }
  glRotatex(a, x, y, z) {
    const rad = a * FIX * Math.PI / 180;
    let X = x * FIX, Y = y * FIX, Z = z * FIX;
    const l = Math.hypot(X, Y, Z) || 1;
    X /= l; Y /= l; Z /= l;
    const c = Math.cos(rad), s = Math.sin(rad), i = 1 - c;
    const m = identity(scratchM);
    m[0] = X * X * i + c; m[1] = Y * X * i + Z * s; m[2] = X * Z * i - Y * s;
    m[4] = X * Y * i - Z * s; m[5] = Y * Y * i + c; m[6] = Y * Z * i + X * s;
    m[8] = X * Z * i + Y * s; m[9] = Y * Z * i - X * s; m[10] = Z * Z * i + c;
    this.ptTouch();
    mul(this.top(), m, this.top());
  }
  glOrthox(l, r_, b, t, n, f) {
    l *= FIX; r_ *= FIX; b *= FIX; t *= FIX; n *= FIX; f *= FIX;
    const m = identity(scratchM);
    m[0] = 2 / (r_ - l); m[5] = 2 / (t - b); m[10] = -2 / (f - n);
    m[12] = -(r_ + l) / (r_ - l); m[13] = -(t + b) / (t - b); m[14] = -(f + n) / (f - n);
    this.ptTouch();
    mul(this.top(), m, this.top());
  }
  glFrustumx(l, r_, b, t, n, f) {
    l *= FIX; r_ *= FIX; b *= FIX; t *= FIX; n *= FIX; f *= FIX;
    const m = new Float32Array(16);
    m[0] = 2 * n / (r_ - l); m[5] = 2 * n / (t - b);
    m[8] = (r_ + l) / (r_ - l); m[9] = (t + b) / (t - b); m[10] = -(f + n) / (f - n); m[11] = -1;
    m[14] = -2 * f * n / (f - n);
    this.ptTouch();
    mul(this.top(), m, this.top());
  }

  glEnable(c) { this.cap(c, true); }
  glDisable(c) { this.cap(c, false); }
  cap(c, on) {
    const gl = this.gl;
    switch (c) {
      case 0x0DE1: if (this.texUnit === 0) this.tex2d = on; else this.tex2d1 = on; return;
      case 0x0BC0: this.alphaOn = on; return;
      case 0x0BE2: case 0x0B71: case 0x0B44: case 0x0C11: case 0x0B90: case 0x0BD0: case 0x8037: case 0x809E: case 0x80A0:
        if (this.caps.get(c) === on) return;
        if (this.bn) this.flush();
        this.caps.set(c, on);
        on ? gl.enable(c) : gl.disable(c);
        return;
      default:
        if (on && !this.seenCap.has(c)) { this.seenCap.add(c); this.log('gl: unhandled cap 0x' + c.toString(16)); }
    }
  }
  glIsEnabled(c) {
    if (c === 0x0DE1) return (this.texUnit === 0 ? this.tex2d : this.tex2d1) ? 1 : 0;
    if (c === 0x0BC0) return this.alphaOn ? 1 : 0;
    return this.caps.has(c) ? (this.caps.get(c) ? 1 : 0) : this.gl.isEnabled(c) ? 1 : 0;
  }
  glEnableClientState(c) { this.clientState(c, true); }
  glDisableClientState(c) { this.clientState(c, false); }
  clientState(c, on) {
    if (this.clientUnit !== 0 && c === 0x8078) return;
    const k = { 0x8074: 'v', 0x8075: 'n', 0x8076: 'c', 0x8078: 't' }[c];
    if (k) this.arrays[k].on = on;
  }
  setPtr(k, size, type, stride, ptr) {
    if (k === 't' && this.clientUnit !== 0) return;
    const a = this.arrays[k];
    a.size = size; a.type = type; a.stride = stride; a.ptr = ptr >>> 0; a.vbo = this.boundArray;
  }
  glVertexPointer(s, t, st, p) { this.setPtr('v', s, t, st, p); }
  glColorPointer(s, t, st, p) { this.setPtr('c', s, t, st, p); }
  glTexCoordPointer(s, t, st, p) { this.setPtr('t', s, t, st, p); }
  glNormalPointer(t, st, p) { this.setPtr('n', 3, t, st, p); }
  glColor4x(r_, g, b, a) { const c = this.color; c[0] = r_ * FIX; c[1] = g * FIX; c[2] = b * FIX; c[3] = a * FIX; }
  glClientActiveTexture(u) { this.clientUnit = u - 0x84C0; }
  glActiveTexture(u) { this.texUnit = u - 0x84C0; }
  glTexEnvf(t, p, v) { this.texEnv(p, bitsF(v)); }
  glTexEnvx(t, p, v) { this.texEnv(p, v); }
  glTexEnvxv(t, p, v) {
    if (p !== 0x2201 || this.texUnit !== 1) return;
    for (let i = 0; i < 4; i++) this.envColor1[i] = this.cpu.m32[(v >>> 2) + i] * FIX;
  }
  texEnv(p, v) {
    if (this.texUnit === 1) { this.env1[p] = v; return; }
    if (p === 0x2200) this.envMode = v;
    else if (p === 0x8571) this.combineRgb = v;
    else return;
    // Native level-up portraits use unit 0 COMBINE with TEXTURE +/- PRIMARY_COLOR.
    // Treating COMBINE as MODULATE turns the neutral black brightness color into a silhouette.
    const mode = this.envMode === 0x8570 ? this.combineRgb : this.envMode;
    const e = { 0x2100: 0, 0x1E01: 1, 0x2101: 2, 0x0104: 3, 0x0BE2: 4, 0x84E7: 5 }[mode];
    if (this.texUnit === 0 && e !== undefined) this.env = e;
  }
  glAlphaFunc(fn, ref) { this.alphaFn = fn - 0x0200 + 1; this.alphaRef = bitsF(ref); }
  glBlendFunc(s, d) {
    if (this.blendS === s && this.blendD === d) return;
    if (this.bn) this.flush();
    this.blendS = s; this.blendD = d;
    if (this.capture) this.bf = [s.toString(16), d.toString(16)].join();
    this.gl.blendFunc(s, d);
  }
  glClear(m) { if (this.bn) this.flush(); this.gl.clear(m); }
  glClearColor(r_, g, b, a) { this.gl.clearColor(bitsF(r_), bitsF(g), bitsF(b), bitsF(a)); }
  glClearColorx(r_, g, b, a) { this.gl.clearColor(r_ * FIX, g * FIX, b * FIX, a * FIX); }
  glColorMask(r_, g, b, a) {
    const R = !!r_, G = !!g, B = !!b, A = !!a;
    const m = this.mask;
    if (m[0] === R && m[1] === G && m[2] === B && m[3] === A) return;
    if (this.bn) this.flush();
    m[0] = R; m[1] = G; m[2] = B; m[3] = A;
    this.gl.colorMask(R, G, B, A);
  }
  glDepthMask(v) { const d = !!v; if (this.depthM === d) return; if (this.bn) this.flush(); this.depthM = d; this.gl.depthMask(d); }
  glStencilMask(v) { if (this.stencilM === v) return; if (this.bn) this.flush(); this.stencilM = v; this.gl.stencilMask(v); }
  glScissor(x, y, w, h) {
    const s = this.scis;
    if (s && s[0] === x && s[1] === y && s[2] === w && s[3] === h) return;
    if (this.bn) this.flush();
    if (s) { s[0] = x; s[1] = y; s[2] = w; s[3] = h; } else this.scis = [x, y, w, h];
    this.gl.scissor(this.scX(x), this.scY(y), this.scX(x + w) - this.scX(x), this.scY(y + h) - this.scY(y));
  }
  glViewport(x, y, w, h) {
    const v = this.viewport;
    if (v[0] === x && v[1] === y && v[2] === w && v[3] === h) return;
    if (this.bn) this.flush();
    v[0] = x; v[1] = y; v[2] = w; v[3] = h;
    this.gl.viewport(this.scX(x), this.scY(y), this.scX(x + w) - this.scX(x), this.scY(y + h) - this.scY(y));
  }
  glShadeModel() {}
  glFinish() { if (this.bn) this.flush(); this.gl.flush(); }
  glLineWidthx(w) { if (this.bn) this.flush(); this.gl.lineWidth(w * FIX * this.sx); }
  glPointSizex(s) { if (this.bn) this.flush(); this.gl.uniform1f(this.u.u_psize, s * FIX * this.sx); }
  glLightxv() {}
  glGetError() { const e = this.err; this.err = 0; return e; }

  queryInt(p) {
    switch (p) {
      case 0x0BA0: return [this.mode];
      case 0x0BA2: return this.viewport;
      case 0x0D33: return [2048];
      case 0x8069: return [this.bound];
      case 0x84E2: return [2];
      case 0x8894: return [this.boundArray];
      case 0x8895: return [this.boundElement];
      case 0x84E0: return [0x84C0 + this.texUnit];
      case 0x84E1: return [0x84C0 + this.clientUnit];
      case 0x0D57: return [8];
      case 0x0D52: case 0x0D53: case 0x0D54: case 0x0D55: return [8];
      case 0x0D56: return [16];
      case 0x0D3A: return [2048, 2048];
    }
    this.log('gl: glGet 0x' + p.toString(16));
    return [0];
  }
  queryMatrix(p) {
    const s = { 0x0BA6: 0x1700, 0x0BA7: 0x1701, 0x0BA8: 0x1702 }[p];
    if (!s) return null;
    const st = this.stacks[s];
    return st[st.length - 1];
  }
  glGetIntegerv(p, out) { this.queryInt(p).forEach((v, i) => { this.cpu.m32[(out >>> 2) + i] = v; }); }
  glGetFloatv(p, out) {
    const m = this.queryMatrix(p);
    if (m) { this.cpu.f32.set(m, out >>> 2); return; }
    this.queryInt(p).forEach((v, i) => { this.cpu.f32[(out >>> 2) + i] = v; });
  }
  glGetFixedv(p, out) {
    const m = this.queryMatrix(p);
    if (m) { for (let i = 0; i < 16; i++) this.cpu.m32[(out >>> 2) + i] = Math.round(m[i] * 65536); return; }
    this.queryInt(p).forEach((v, i) => { this.cpu.m32[(out >>> 2) + i] = v * 65536; });
  }
  glGetPointerv(p, out) { this.cpu.m32[out >>> 2] = 0; }
  glGetTexParameteriv(t, p, out) { this.cpu.m32[out >>> 2] = 0; }

  // ---------- textures ----------
  glGenTextures(n, out) {
    for (let i = 0; i < n; i++) {
      const id = this.nextTex++;
      this.textures.set(id, { tex: this.gl.createTexture(), w: 0, h: 0, mip: false });
      this.cpu.m32[(out >>> 2) + i] = id;
    }
  }
  glDeleteTextures(n, p) {
    if (this.bn) this.flush();
    for (let i = 0; i < n; i++) {
      const id = this.cpu.m32[(p >>> 2) + i];
      const t = this.textures.get(id);
      if (t) { this.gl.deleteTexture(t.tex); this.textures.delete(id); }
      if (this.bound === id) this.bound = 0;
    }
  }
  glIsTexture(id) { return this.textures.has(id) ? 1 : 0; }
  glBindTexture(t, id) {
    if (this.texUnit !== 0) return;
    if (this.bound === id) return;
    if (this.bn) this.flush();
    let e = this.textures.get(id);
    if (!e && id) {
      e = { tex: this.gl.createTexture(), w: 0, h: 0, mip: false };
      this.textures.set(id, e);
    }
    this.bound = id;
    this.gl.bindTexture(this.gl.TEXTURE_2D, e ? e.tex : null);
  }
  texParam(p, v) {
    if (this.texUnit !== 0) return;
    if (this.bn) this.flush();
    const gl = this.gl;
    if (p === 0x8191) { const e = this.textures.get(this.bound); if (e) e.mip = !!v; return; }
    if (p === 0x2802 || p === 0x2803) {
      if (v === 0x2900) v = gl.CLAMP_TO_EDGE;
      gl.texParameteri(gl.TEXTURE_2D, p, v);
      return;
    }
    if (p === 0x2800 || p === 0x2801) gl.texParameteri(gl.TEXTURE_2D, p, v);
  }
  glTexParameteri(t, p, v) { this.texParam(p, v); }
  glTexParameterx(t, p, v) { this.texParam(p, v); }
  glTexParameterf(t, p, v) { this.texParam(p, bitsF(v)); }
  pixelBytes(w, h, format, type) {
    let bpp;
    if (type === 0x1401) bpp = format === 0x1908 ? 4 : format === 0x1907 ? 3 : format === 0x190A ? 2 : 1;
    else bpp = 2;
    const row = (w * bpp + 3) & ~3;
    return row * h;
  }
  pixelView(p, w, h, format, type) {
    if (!p) return null;
    const n = this.pixelBytes(w, h, format, type);
    const start = p >>> 0;
    if (type === 0x1401) return this.cpu.m8.subarray(start, start + n);
    return start & 1 ? new Uint16Array(this.cpu.m8.slice(start, start + n).buffer)
      : new Uint16Array(this.cpu.buffer, start, n >>> 1);
  }
  glTexImage2D(t, level, ifmt, w, h, border, format, type, p) {
    if (this.texUnit !== 0) return;
    if (this.bn) this.flush();
    const gl = this.gl;
    gl.texImage2D(gl.TEXTURE_2D, level, format, w, h, 0, format, type, this.pixelView(p, w, h, format, type));
    const e = this.textures.get(this.bound);
    if (e) {
      e.w = w; e.h = h; e.format = format; e.fmt = format.toString(16) + '/' + type.toString(16);
      e.sx = e.sy = undefined;
      if (e.mip) gl.generateMipmap(gl.TEXTURE_2D);
    }
  }
  glTexSubImage2D(t, level, x, y, w, h, format, type, p) {
    if (this.texUnit !== 0) return;
    if (this.bn) this.flush();
    const gl = this.gl;
    gl.texSubImage2D(gl.TEXTURE_2D, level, x, y, w, h, format, type, this.pixelView(p, w, h, format, type));
  }
  glCompressedTexImage2D() { this.log('gl: compressed texture ignored'); }
  glCopyTexImage2D(t, level, fmt, x, y, w, h, b) {
    if (this.bn) this.flush();
    const X = this.scX(x), Y = this.scY(y);
    this.gl.copyTexImage2D(this.gl.TEXTURE_2D, level, fmt, X, Y, this.scX(x + w) - X, this.scY(y + h) - Y, b);
    const e = this.textures.get(this.bound);
    if (e) { e.w = w; e.h = h; e.sx = this.sx; e.sy = this.sy; }
  }
  glCopyTexSubImage2D(t, level, xo, yo, x, y, w, h) {
    if (this.bn) this.flush();
    // Offsets are in the texture's own pixels: scaled when a screen copy made it at this scale.
    const e = this.textures.get(this.bound);
    const tx = e && e.sx === this.sx ? this.sx : 1, ty = e && e.sy === this.sy ? this.sy : 1;
    if ((this.sx !== 1 || this.sy !== 1) && tx === 1 && !this.warnedCopy) { this.warnedCopy = true; this.log('gl: screen copy into an unscaled texture at render scale ' + this.sx.toFixed(3)); }
    const X = this.scX(x), Y = this.scY(y);
    this.gl.copyTexSubImage2D(this.gl.TEXTURE_2D, level, Math.round(xo * tx), Math.round(yo * ty), X, Y, this.scX(x + w) - X, this.scY(y + h) - Y);
  }

  // ---------- VBOs: kept in JS, streamed at draw time ----------
  glGenBuffers(n, out) {
    for (let i = 0; i < n; i++) {
      const id = this.nextBuf++;
      this.buffers.set(id, { data: new Uint8Array(0) });
      this.cpu.m32[(out >>> 2) + i] = id;
    }
  }
  glDeleteBuffers(n, p) {
    for (let i = 0; i < n; i++) this.buffers.delete(this.cpu.m32[(p >>> 2) + i]);
  }
  glBindBuffer(t, id) {
    if (t === 0x8892) this.boundArray = id; else this.boundElement = id;
    if (id && !this.buffers.has(id)) this.buffers.set(id, { data: new Uint8Array(0) });
  }
  glBufferData(t, size, p, usage) {
    const id = t === 0x8892 ? this.boundArray : this.boundElement;
    const b = this.buffers.get(id);
    if (!b) return;
    size >>>= 0;
    b.data = p ? this.cpu.m8.slice(p >>> 0, (p >>> 0) + size) : new Uint8Array(size);
  }

  // ---------- drawing ----------
  readFloats(a, first, count, dst, dstOff) {
    const cpu = this.cpu, size = a.size;
    if (a.type === 0x140C) {
      const stride = a.stride || size * 4;
      if (a.vbo) {
        const src = this.buffers.get(a.vbo);
        const dv = new DataView(src.data.buffer, src.data.byteOffset, src.data.byteLength);
        for (let i = 0; i < count; i++) {
          const base = a.ptr + (first + i) * stride;
          for (let j = 0; j < size; j++) dst[dstOff++] = dv.getInt32(base + j * 4, true) * FIX;
        }
      } else {
        const m = cpu.m32;
        for (let i = 0; i < count; i++) {
          const base = (a.ptr + (first + i) * stride) >>> 2;
          for (let j = 0; j < size; j++) dst[dstOff++] = m[base + j] * FIX;
        }
      }
      return dstOff;
    }
    if (a.type === 0x1406) {
      const stride = a.stride || size * 4;
      if (a.vbo) {
        const src = this.buffers.get(a.vbo);
        const dv = new DataView(src.data.buffer, src.data.byteOffset, src.data.byteLength);
        for (let i = 0; i < count; i++) {
          const base = a.ptr + (first + i) * stride;
          for (let j = 0; j < size; j++) dst[dstOff++] = dv.getFloat32(base + j * 4, true);
        }
      } else {
        const f = cpu.f32;
        for (let i = 0; i < count; i++) {
          const base = (a.ptr + (first + i) * stride) >>> 2;
          for (let j = 0; j < size; j++) dst[dstOff++] = f[base + j];
        }
      }
      return dstOff;
    }
    return -1;
  }
  bindArray(loc, a, first, count, normalizeBytes) {
    const gl = this.gl, cpu = this.cpu;
    if (a.type === 0x140C) {
      // GL_FIXED becomes float
      const need = count * a.size;
      if (need > this.floats.length) this.floats = new Float32Array(need * 2);
      this.readFloats(a, first, count, this.floats, 0);
      gl.bufferData(gl.ARRAY_BUFFER, this.floats.subarray(0, need), gl.STREAM_DRAW);
      gl.vertexAttribPointer(loc, a.size, gl.FLOAT, false, 0, 0);
      return;
    }
    const elem = a.type === 0x1406 ? 4 : (a.type === 0x1402 || a.type === 0x1403) ? 2 : 1;
    const stride = a.stride || a.size * elem;
    const n = stride * count;
    let bytes;
    if (a.vbo) {
      const src = this.buffers.get(a.vbo);
      bytes = src.data.subarray(a.ptr + first * stride, a.ptr + first * stride + n);
    } else {
      bytes = cpu.m8.subarray(a.ptr + first * stride, a.ptr + first * stride + n);
    }
    gl.bufferData(gl.ARRAY_BUFFER, bytes, gl.STREAM_DRAW);
    gl.vertexAttribPointer(loc, a.size, a.type, normalizeBytes && a.type === 0x1401, stride, 0);
  }
  // Uniform and attribute calls each cross into the GPU process: send only what changed.
  setMat(i, loc, m) {
    const last = this.mv[i];
    for (let k = 0; k < 16; k++) {
      if (last[k] !== m[k]) {
        last.set(m);
        this.gl.uniformMatrix4fv(loc, false, m);
        return;
      }
    }
  }
  set1i(loc, v) {
    if (this.uv.get(loc) === v) return;
    this.uv.set(loc, v);
    this.gl.uniform1i(loc, v);
  }
  set3i(loc, x, y, z) {
    const key = x | (y << 8) | (z << 16);
    if (this.uv.get(loc) === key) return;
    this.uv.set(loc, key);
    this.gl.uniform3i(loc, x, y, z);
  }
  attrib(i, on) {
    if (this.attribOn[i] === on) return;
    this.attribOn[i] = on;
    if (on) this.gl.enableVertexAttribArray(i); else this.gl.disableVertexAttribArray(i);
  }
  constAttrib(i, c) {
    // A copy: the caller's array (this.color) is rewritten in place by the next glColor4x.
    let last = this.attribVal[i];
    if (!last) last = this.attribVal[i] = [NaN, NaN, NaN, NaN];
    else if (last[0] === c[0] && last[1] === c[1] && last[2] === c[2] && last[3] === c[3]) return;
    last[0] = c[0]; last[1] = c[1]; last[2] = c[2]; last[3] = c[3];
    this.gl.vertexAttrib4f(i, c[0], c[1], c[2], c[3]);
  }
  // Uniforms for one draw (or one batch, whose positions already carry the modelview).
  setUniforms(useTex, mv) {
    const gl = this.gl, u = this.u, s = this.stacks;
    const sp = s[0x1701], st = s[0x1702];
    this.setMat(0, u.u_mv, mv);
    this.setMat(1, u.u_proj, sp[sp.length - 1]);
    this.setMat(2, u.u_texm, st[st.length - 1]);
    this.set1i(u.u_useTex, useTex ? 1 : 0);
    this.set1i(u.u_env, this.env);
    const te = useTex ? this.textures.get(this.bound) : null;
    this.set1i(u.u_fmtA, te && te.format === 0x1906 ? 1 : 0);
    const E = this.env1;
    const combine = this.tex2d1 && E[0x2200] === 0x8570;
    this.set1i(u.u_c1, combine ? 1 : 0);
    if (combine) {
      this.set1i(u.u_c1Rgb, FN1[E[0x8571]] || 0);
      this.set1i(u.u_c1A, FN1[E[0x8572]] || 0);
      this.set3i(u.u_c1Src, SRC1[E[0x8580]] || 0, SRC1[E[0x8581]] || 0, SRC1[E[0x8582]] || 0);
      this.set3i(u.u_c1Op, OP1[E[0x8590]] || 0, OP1[E[0x8591]] || 0, OP1[E[0x8592]] || 0);
      this.set3i(u.u_c1SrcA, SRC1[E[0x8588]] || 0, SRC1[E[0x8589]] || 0, SRC1[E[0x858A]] || 0);
      this.set3i(u.u_c1OpA, opA1(E[0x8598]), opA1(E[0x8599]), opA1(E[0x859A]));
      const ec = this.envColor1;
      const lastEc = this.lastEnvColor;
      if (!lastEc || lastEc[0] !== ec[0] || lastEc[1] !== ec[1] || lastEc[2] !== ec[2] || lastEc[3] !== ec[3]) {
        this.lastEnvColor = [ec[0], ec[1], ec[2], ec[3]];
        gl.uniform4fv(u.u_envColor, ec);
      }
    }
    this.set1i(u.u_alphaFn, this.alphaOn ? this.alphaFn : 0);
    if (this.uv.get(u.u_alphaRef) !== this.alphaRef) {
      this.uv.set(u.u_alphaRef, this.alphaRef);
      gl.uniform1f(u.u_alphaRef, this.alphaRef);
    }
  }
  prepare(first, count) {
    const gl = this.gl, A = this.arrays, sv = this.stacks[0x1700];
    const useTex = this.tex2d && A.t.on;
    this.setUniforms(useTex, sv[sv.length - 1]);
    this.attrib(0, A.v.on);
    this.attrib(1, A.c.on);
    if (!A.c.on) this.constAttrib(1, this.color);
    this.attrib(2, useTex);
    if (!useTex) this.constAttrib(2, NO_TEX);

    // Fast path: pack pos + tex (+ optional float/fixed color) into a single buffer upload
    const isAlignedFloat = a => (a.type === 0x140C || a.type === 0x1406) && (((a.ptr | a.stride) & 3) === 0);
    const vFloat = A.v.on && isAlignedFloat(A.v);
    const tFloat = !useTex || isAlignedFloat(A.t);
    const cFloat = !A.c.on || isAlignedFloat(A.c);
    if (vFloat && tFloat && cFloat) {
      const vLen = count * A.v.size;
      const cLen = A.c.on ? count * A.c.size : 0;
      const tLen = useTex ? count * A.t.size : 0;
      const total = vLen + cLen + tLen;
      if (total > this.floats.length) this.floats = new Float32Array(total * 2);
      const out = this.floats;
      let off = this.readFloats(A.v, first, count, out, 0);
      const cOff = off;
      if (A.c.on) off = this.readFloats(A.c, first, count, out, off);
      const tOff = off;
      if (useTex) off = this.readFloats(A.t, first, count, out, off);
      if (this.curArrayBuf !== this.stream) {
        gl.bindBuffer(gl.ARRAY_BUFFER, this.stream);
        this.curArrayBuf = this.stream;
      }
      gl.bufferData(gl.ARRAY_BUFFER, out.subarray(0, total), gl.STREAM_DRAW);
      gl.vertexAttribPointer(0, A.v.size, gl.FLOAT, false, 0, 0);
      if (A.c.on) gl.vertexAttribPointer(1, A.c.size, gl.FLOAT, false, 0, cOff * 4);
      if (useTex) gl.vertexAttribPointer(2, A.t.size, gl.FLOAT, false, 0, tOff * 4);
      this.drawCalls++;
      return;
    }

    gl.bindBuffer(gl.ARRAY_BUFFER, this.stream);
    this.curArrayBuf = this.stream;
    if (A.v.on) this.bindArray(0, A.v, first, count, false);
    if (A.c.on) {
      if (!this.cbuf) this.cbuf = gl.createBuffer();
      gl.bindBuffer(gl.ARRAY_BUFFER, this.cbuf);
      this.curArrayBuf = this.cbuf;
      this.bindArray(1, A.c, first, count, true);
    }
    if (useTex) {
      if (!this.tbuf) this.tbuf = gl.createBuffer();
      gl.bindBuffer(gl.ARRAY_BUFFER, this.tbuf);
      this.curArrayBuf = this.tbuf;
      this.bindArray(2, A.t, first, count, false);
    }
    this.drawCalls++;
  }
  // Debug: <GLES1 instance>.capture = [] records the draws of the next frames.
  record(mode, first, count) {
    const A = this.arrays, v = A.v, gl = this.gl;
    const stride = v.stride || v.size * (v.type === 0x1402 ? 2 : 4);
    const src = v.vbo ? new DataView(this.buffers.get(v.vbo).data.buffer, this.buffers.get(v.vbo).data.byteOffset) : new DataView(this.cpu.buffer);
    let x0 = 1e9, y0 = 1e9, x1 = -1e9, y1 = -1e9;
    const mv = this.stacks[0x1700].at(-1), pr = this.stacks[0x1701].at(-1);
    for (let i = 0; i < count; i++) {
      const o = v.ptr + (first + i) * stride;
      const rd = j => v.type === 0x140C ? src.getInt32(o + j * 4, true) / 65536 : v.type === 0x1406 ? src.getFloat32(o + j * 4, true) : src.getInt16(o + j * 2, true);
      const X = rd(0), Y = rd(1);
      const ex = mv[0] * X + mv[4] * Y + mv[12], ey = mv[1] * X + mv[5] * Y + mv[13];
      const cx = pr[0] * ex + pr[4] * ey + pr[12], cy = pr[1] * ex + pr[5] * ey + pr[13];
      const sx = (cx + 1) / 2 * this.viewport[2], sy = (1 - cy) / 2 * this.viewport[3];
      x0 = Math.min(x0, sx); x1 = Math.max(x1, sx); y0 = Math.min(y0, sy); y1 = Math.max(y1, sy);
    }
    const e = this.textures.get(this.bound);
    this.capture.push({
      box: [x0, y0, x1, y1].map(Math.round), mode, count, tex: this.tex2d && A.t.on ? this.bound : 0, texW: e && e.w, texH: e && e.h,
      fmt: e && e.fmt, color: A.c.on ? 'array' : this.color.map(c => +c.toFixed(2)).join(), env: this.env, blend: gl.isEnabled(gl.BLEND), bf: this.bf,
      alpha: this.alphaOn ? [this.alphaFn, this.alphaRef] : 0, scissor: gl.isEnabled(gl.SCISSOR_TEST),
    });
  }
  glDrawArrays(mode, first, count) {
    if (count <= 0) return;
    if (this.capture) this.record(mode, first, count);
    if (this.batching && count >= 3 && (mode === 4 || mode === 5 || mode === 6) && this.batchable()) {
      this.addBatch(mode, first, count);
      return;
    }
    if (this.bn) this.flush();
    this.prepare(first, count);
    this.gl.drawArrays(mode, 0, count);
  }

  // ---------- batching ----------
  batchable() {
    const A = this.arrays, v = A.v, t = A.t, c = A.c;
    const flt = a => (a.type === 0x140C || a.type === 0x1406) && ((a.ptr | a.stride) & 3) === 0;
    if (!v.on || v.size < 2 || !flt(v)) return false;
    if (this.tex2d && t.on && (t.size !== 2 || !flt(t))) return false;
    if (c.on && (c.size !== 4 || !(flt(c) || c.type === 0x1401))) return false;
    return true;
  }
  // Everything setUniforms sends except the modelview, so draws with equal signatures can share a draw call.
  signature(useTex) {
    const g = this.sig, s = this.stacks;
    const sp = s[0x1701], st = s[0x1702];
    if (this.sigPtVer !== this.ptVer) {
      g.set(sp[sp.length - 1], 0);
      g.set(st[st.length - 1], 16);
      this.sigPtVer = this.ptVer;
    }
    g[32] = useTex ? 1 : 0;
    g[33] = this.env;
    const te = useTex ? this.textures.get(this.bound) : null;
    g[34] = te && te.format === 0x1906 ? 1 : 0;
    const E = this.env1;
    const combine = this.tex2d1 && E[0x2200] === 0x8570;
    g[35] = combine ? 1 : 0;
    if (combine) {
      g[36] = FN1[E[0x8571]] || 0; g[37] = FN1[E[0x8572]] || 0;
      g[38] = (SRC1[E[0x8580]] || 0) | ((SRC1[E[0x8581]] || 0) << 8) | ((SRC1[E[0x8582]] || 0) << 16);
      g[39] = (OP1[E[0x8590]] || 0) | ((OP1[E[0x8591]] || 0) << 8) | ((OP1[E[0x8592]] || 0) << 16);
      g[40] = (SRC1[E[0x8588]] || 0) | ((SRC1[E[0x8589]] || 0) << 8) | ((SRC1[E[0x858A]] || 0) << 16);
      g[41] = opA1(E[0x8598]) | (opA1(E[0x8599]) << 8) | (opA1(E[0x859A]) << 16);
      const ec = this.envColor1;
      g[42] = ec[0]; g[43] = ec[1]; g[44] = ec[2]; g[45] = ec[3];
    } else {
      for (let i = 36; i < 46; i++) g[i] = 0;
    }
    g[46] = this.alphaOn ? this.alphaFn : 0;
    g[47] = this.alphaRef;
  }
  addBatch(mode, first, count) {
    const A = this.arrays;
    const useTex = this.tex2d && A.t.on;
    this.signature(useTex);
    const tris = mode === 4 ? Math.floor(count / 3) : count - 2;
    const add = tris * 3;
    if (this.bn) {
      const a = this.sig, b = this.bsig;
      let same = this.bUseTex === useTex && (this.bn + add) * BATCH_FLOATS <= this.bverts.length;
      // The first 32 values are the projection and texture matrices: equal for certain while neither was touched.
      for (let i = this.bPtVer === this.ptVer ? 32 : 0; same && i < SIG_LEN; i++) if (a[i] !== b[i]) same = false;
      if (same) this.bPtVer = this.ptVer;
      if (!same) this.flush();
    }
    if (!this.bn) {
      this.bsig.set(this.sig);
      this.bPtVer = this.ptVer;
      this.bUseTex = useTex;
      this.setUniforms(useTex, IDENT);
      if (add * BATCH_FLOATS > this.bverts.length) this.bverts = new Float32Array(add * BATCH_FLOATS * 2);
    }
    // Source vertices: position, color, texcoord, read once.
    const vs = A.v.size;
    const need = count * (vs + 4 + 2);
    if (need > this.floats.length) this.floats = new Float32Array(need * 2);
    const src = this.floats;
    this.readFloats(A.v, first, count, src, 0);
    const cOff = count * vs, tOff = cOff + count * 4;
    if (A.c.on) {
      if (A.c.type === 0x1401) {
        const m8 = A.c.vbo ? this.buffers.get(A.c.vbo).data : this.cpu.m8;
        const stride = A.c.stride || 4;
        for (let i = 0, o = cOff; i < count; i++) {
          const b = A.c.ptr + (first + i) * stride;
          src[o++] = m8[b] / 255; src[o++] = m8[b + 1] / 255; src[o++] = m8[b + 2] / 255; src[o++] = m8[b + 3] / 255;
        }
      } else this.readFloats(A.c, first, count, src, cOff);
    } else {
      const col = this.color;
      for (let i = 0, o = cOff; i < count; i++) { src[o++] = col[0]; src[o++] = col[1]; src[o++] = col[2]; src[o++] = col[3]; }
    }
    if (useTex) this.readFloats(A.t, first, count, src, tOff);
    // The modelview, applied on the CPU in float32 steps as the shader would.
    const sv = this.stacks[0x1700], m = sv[sv.length - 1];
    const out = this.bverts;
    let o = this.bn * BATCH_FLOATS;
    const m0 = m[0], m1 = m[1], m2 = m[2], m3 = m[3], m4 = m[4], m5 = m[5], m6 = m[6], m7 = m[7];
    const m8_ = m[8], m9 = m[9], m10 = m[10], m11 = m[11], m12 = m[12], m13 = m[13], m14 = m[14], m15 = m[15];
    // Triangles in GL order and winding: list as is, strip alternating, fan (0, i+1, i+2).
    for (let t = 0; t < tris; t++) {
      let ia, ib, ic;
      if (mode === 6) { ia = 0; ib = t + 1; ic = t + 2; }
      else if (mode === 5) {
        if (t & 1) { ia = t + 1; ib = t; ic = t + 2; } else { ia = t; ib = t + 1; ic = t + 2; }
      } else { ia = t * 3; ib = ia + 1; ic = ia + 2; }
      for (let k = 0; k < 3; k++) {
        const i = k === 0 ? ia : k === 1 ? ib : ic;
        const p = i * vs;
        const x = src[p], y = src[p + 1], z = vs > 2 ? src[p + 2] : 0, w = vs > 3 ? src[p + 3] : 1;
        out[o] = fr(fr(fr(fr(m0 * x) + fr(m4 * y)) + fr(m8_ * z)) + fr(m12 * w));
        out[o + 1] = fr(fr(fr(fr(m1 * x) + fr(m5 * y)) + fr(m9 * z)) + fr(m13 * w));
        out[o + 2] = fr(fr(fr(fr(m2 * x) + fr(m6 * y)) + fr(m10 * z)) + fr(m14 * w));
        out[o + 3] = fr(fr(fr(fr(m3 * x) + fr(m7 * y)) + fr(m11 * z)) + fr(m15 * w));
        const c = cOff + i * 4;
        out[o + 4] = src[c]; out[o + 5] = src[c + 1]; out[o + 6] = src[c + 2]; out[o + 7] = src[c + 3];
        if (useTex) { const q = tOff + i * 2; out[o + 8] = src[q]; out[o + 9] = src[q + 1]; }
        else { out[o + 8] = 0; out[o + 9] = 0; }
        o += BATCH_FLOATS;
      }
    }
    this.bn = o / BATCH_FLOATS;
    this.batchDraws++;
  }
  flush() {
    const gl = this.gl, n = this.bn;
    this.bn = 0;
    if (!n) return;
    if (this.curArrayBuf !== this.stream) {
      gl.bindBuffer(gl.ARRAY_BUFFER, this.stream);
      this.curArrayBuf = this.stream;
    }
    gl.bufferData(gl.ARRAY_BUFFER, this.bverts.subarray(0, n * BATCH_FLOATS), gl.STREAM_DRAW);
    const S = BATCH_FLOATS * 4;
    this.attrib(0, true);
    this.attrib(1, true);
    this.attrib(2, this.bUseTex);
    if (!this.bUseTex) this.constAttrib(2, NO_TEX);
    gl.vertexAttribPointer(0, 4, gl.FLOAT, false, S, 0);
    gl.vertexAttribPointer(1, 4, gl.FLOAT, false, S, 16);
    if (this.bUseTex) gl.vertexAttribPointer(2, 2, gl.FLOAT, false, S, 32);
    gl.drawArrays(gl.TRIANGLES, 0, n);
    this.drawCalls++;
    this.batchFlushes++;
  }
  glDrawElements(mode, count, type, p) {
    if (count <= 0) return;
    if (this.bn) this.flush();
    const gl = this.gl;
    const size = type === 0x1403 ? 2 : 1;
    let idx;
    if (this.boundElement) {
      const b = this.buffers.get(this.boundElement);
      idx = b.data.subarray(p >>> 0, (p >>> 0) + count * size);
    } else {
      idx = this.cpu.m8.subarray(p >>> 0, (p >>> 0) + count * size);
    }
    let copy;
    if (size === 2) {
      copy = (idx.byteOffset & 1) ? new Uint16Array(idx.slice().buffer) : new Uint16Array(idx.buffer, idx.byteOffset, count);
    } else copy = idx;
    let max = 0;
    for (let i = 0; i < count; i++) if (copy[i] > max) max = copy[i];
    this.prepare(0, max + 1);
    gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, copy, gl.STREAM_DRAW);
    gl.drawElements(mode, count, type, 0);
  }
}

root.ApkGLES1 = GLES1;
if (typeof module !== 'undefined') module.exports = GLES1;
})(typeof globalThis !== 'undefined' ? globalThis : this);
