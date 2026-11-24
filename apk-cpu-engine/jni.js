// JNIEnv and JavaVM for the guest. Java classes live in JS: java.classes['a/b/C'] = {method: fn}.
// A method entry is looked up as 'name sig' first, then 'name'. Instance methods get `this` set.
(function (root) {
'use strict';
const Runtime = root.ApkRuntime || require('./runtime.js');
const { d2w, w2d, f2w, w2f, sbig } = Runtime.helpers;

const NAMES = ('reserved0 reserved1 reserved2 reserved3 GetVersion DefineClass FindClass FromReflectedMethod FromReflectedField ToReflectedMethod ' +
'GetSuperclass IsAssignableFrom ToReflectedField Throw ThrowNew ExceptionOccurred ExceptionDescribe ExceptionClear FatalError PushLocalFrame ' +
'PopLocalFrame NewGlobalRef DeleteGlobalRef DeleteLocalRef IsSameObject NewLocalRef EnsureLocalCapacity AllocObject NewObject NewObjectV ' +
'NewObjectA GetObjectClass IsInstanceOf GetMethodID ' +
['Object', 'Boolean', 'Byte', 'Char', 'Short', 'Int', 'Long', 'Float', 'Double', 'Void'].map(t => `Call${t}Method Call${t}MethodV Call${t}MethodA`).join(' ') + ' ' +
['Object', 'Boolean', 'Byte', 'Char', 'Short', 'Int', 'Long', 'Float', 'Double', 'Void'].map(t => `CallNonvirtual${t}Method CallNonvirtual${t}MethodV CallNonvirtual${t}MethodA`).join(' ') + ' ' +
'GetFieldID ' +
['Object', 'Boolean', 'Byte', 'Char', 'Short', 'Int', 'Long', 'Float', 'Double'].map(t => `Get${t}Field`).join(' ') + ' ' +
['Object', 'Boolean', 'Byte', 'Char', 'Short', 'Int', 'Long', 'Float', 'Double'].map(t => `Set${t}Field`).join(' ') + ' ' +
'GetStaticMethodID ' +
['Object', 'Boolean', 'Byte', 'Char', 'Short', 'Int', 'Long', 'Float', 'Double', 'Void'].map(t => `CallStatic${t}Method CallStatic${t}MethodV CallStatic${t}MethodA`).join(' ') + ' ' +
'GetStaticFieldID ' +
['Object', 'Boolean', 'Byte', 'Char', 'Short', 'Int', 'Long', 'Float', 'Double'].map(t => `GetStatic${t}Field`).join(' ') + ' ' +
['Object', 'Boolean', 'Byte', 'Char', 'Short', 'Int', 'Long', 'Float', 'Double'].map(t => `SetStatic${t}Field`).join(' ') + ' ' +
'NewString GetStringLength GetStringChars ReleaseStringChars NewStringUTF GetStringUTFLength GetStringUTFChars ReleaseStringUTFChars GetArrayLength ' +
'NewObjectArray GetObjectArrayElement SetObjectArrayElement ' +
['Boolean', 'Byte', 'Char', 'Short', 'Int', 'Long', 'Float', 'Double'].map(t => `New${t}Array`).join(' ') + ' ' +
['Boolean', 'Byte', 'Char', 'Short', 'Int', 'Long', 'Float', 'Double'].map(t => `Get${t}ArrayElements`).join(' ') + ' ' +
['Boolean', 'Byte', 'Char', 'Short', 'Int', 'Long', 'Float', 'Double'].map(t => `Release${t}ArrayElements`).join(' ') + ' ' +
['Boolean', 'Byte', 'Char', 'Short', 'Int', 'Long', 'Float', 'Double'].map(t => `Get${t}ArrayRegion`).join(' ') + ' ' +
['Boolean', 'Byte', 'Char', 'Short', 'Int', 'Long', 'Float', 'Double'].map(t => `Set${t}ArrayRegion`).join(' ') + ' ' +
'RegisterNatives UnregisterNatives MonitorEnter MonitorExit GetJavaVM GetStringRegion GetStringUTFRegion GetPrimitiveArrayCritical ' +
'ReleasePrimitiveArrayCritical GetStringCritical ReleaseStringCritical NewWeakGlobalRef DeleteWeakGlobalRef ExceptionCheck NewDirectByteBuffer ' +
'GetDirectBufferAddress GetDirectBufferCapacity GetObjectRefType').split(' ');

const ARR = {
  Boolean: Uint8Array, Byte: Int8Array, Char: Uint16Array, Short: Int16Array,
  Int: Int32Array, Long: BigInt64Array, Float: Float32Array, Double: Float64Array,
};

const utf8e = new TextEncoder();
function parseSig(sig) {
  const args = [];
  let i = 1;
  while (sig[i] !== ')') {
    const s = i;
    while (sig[i] === '[') i++;
    if (sig[i] === 'L') i = sig.indexOf(';', i);
    i++;
    args.push(sig.slice(s, i));
  }
  return { args, ret: sig.slice(i + 1) };
}

class JavaObject {
  constructor(cls) { this.cls = cls; this.fields = {}; }
}
class JavaClass {
  constructor(name) { this.name = name; this.statics = {}; }
}

class Java {
  constructor(rt) {
    this.rt = rt;
    rt.java = this;
    this.classes = {};
    this.classObjs = new Map();
    this.refs = new Map();
    this.nextRef = 0x1000;
    this.warned = new Set();
    this.trace = false;
    this.pending = new Map();
    this.install();
  }
  ref(o) {
    if (o === null || o === undefined) return 0;
    const h = this.nextRef++;
    this.refs.set(h, o);
    return h;
  }
  deref(h) { return h ? this.refs.get(h) : null; }
  cls(name) {
    let c = this.classObjs.get(name);
    if (!c) { c = new JavaClass(name); this.classObjs.set(name, c); }
    return c;
  }
  str(s) { return { jstr: s }; }
  // Make a Java String handle for native code
  jstr(s) { return s === null || s === undefined ? 0 : this.ref(this.str(String(s))); }
  bytes(u8) { const a = new Int8Array(u8.length); a.set(u8); return a; }
  warnOnce(s) {
    if (this.warned.has(s)) return;
    this.warned.add(s);
    this.rt.log('JAVA? ' + s);
  }
  findMethod(clsName, name, sig) {
    const table = this.classes[clsName];
    if (!table) return null;
    return table[name + ' ' + sig] || table[name] || null;
  }
  // Decode one Java argument into a JS value
  argFrom(type, take, take64, takeD, isV) {
    switch (type[0]) {
      case 'J': return Number(take64(true));
      case 'D': return takeD();
      case 'F': return isV ? takeD() : w2f(take());
      case 'Z': return !!(take() & 255);
      case 'B': return (take() << 24) >> 24;
      case 'S': return (take() << 16) >> 16;
      case 'C': return take() & 0xffff;
      case 'I': return take();
      default: return this.unwrap(this.deref(take()));
    }
  }
  unwrap(o) { return o && o.jstr !== undefined ? o.jstr : o; }
  wrap(v) { return typeof v === 'string' ? this.str(v) : v; }
  invoke(method, self, readers, isV) {
    const [take, take64, takeD] = readers;
    const sigArgs = method.sig.args, n = sigArgs.length;
    const args = new Array(n);
    for (let i = 0; i < n; i++) args[i] = this.argFrom(sigArgs[i], take, take64, takeD, isV);
    let fn = method.fn;
    if (fn === undefined) fn = method.fn = this.findMethod(method.cls, method.name, method.sigStr);
    let v;
    if (fn) v = fn.apply(self, args);
    else {
      method.calls = (method.calls || 0) + 1;
      if (method.calls <= 3) {
        const show = args.map(a => typeof a === 'string' ? JSON.stringify(a) : a && a.length !== undefined ? '[' + a.length + ']' : String(a));
        this.rt.log(`JAVA? ${method.isStatic ? 'static ' : ''}${method.cls.split('/').pop()}.${method.name}(${show.join(', ')}) ${method.sigStr}`);
      }
    }
    if (this.trace) this.rt.log(`java ${method.cls}.${method.name}(${args.map(a => typeof a === 'string' ? JSON.stringify(a) : a && a.length !== undefined ? '[' + a.length + ']' : String(a)).join(',')}) -> ${v}`);
    return v;
  }
  // Writes the return value registers
  ret(type, v) {
    const r = this.rt.cpu.r;
    switch (type[0]) {
      case 'V': return;
      case 'J': {
        const b = BigInt(Math.trunc(v || 0));
        r[0] = Number(BigInt.asIntN(32, b));
        r[1] = Number(BigInt.asIntN(32, b >> 32n));
        return;
      }
      case 'D': { const w = d2w(v || 0); r[0] = w[0]; r[1] = w[1]; return; }
      case 'F': r[0] = f2w(v || 0); return;
      case 'Z': r[0] = v ? 1 : 0; return;
      case 'B': case 'S': case 'C': case 'I': r[0] = v | 0; return;
      default: r[0] = this.ref(this.wrap(v)); return;
    }
  }

  install() {
    const rt = this.rt, cpu = rt.cpu, r = cpu.r, m32 = cpu.m32, m8 = cpu.m8;
    const self_ = this;
    const impl = {};
    const stubs = {};

    impl.GetVersion = () => 0x00010006;
    impl.FindClass = (env, name) => {
      const n = rt.cstr(name);
      return this.ref(this.cls(n));
    };
    impl.ExceptionOccurred = () => 0;
    impl.ExceptionCheck = () => 0;
    impl.ExceptionClear = () => 0;
    impl.ExceptionDescribe = () => 0;
    impl.FatalError = (env, s) => { throw new Error('JNI FatalError: ' + rt.cstr(s)); };
    impl.NewGlobalRef = (env, h) => this.ref(this.deref(h));
    impl.NewLocalRef = (env, h) => this.ref(this.deref(h));
    impl.NewWeakGlobalRef = (env, h) => this.ref(this.deref(h));
    impl.DeleteGlobalRef = (env, h) => { this.refs.delete(h); return 0; };
    impl.DeleteLocalRef = (env, h) => {
      const o = this.refs.get(h);
      if (o && o._poolKey) {
        let bin = this.arrayPool.get(o._poolKey);
        if (!bin) this.arrayPool.set(o._poolKey, bin = []);
        if (bin.length < 32) bin.push(o);
      }
      this.refs.delete(h);
      return 0;
    };
    impl.DeleteWeakGlobalRef = (env, h) => { this.refs.delete(h); return 0; };
    impl.IsSameObject = (env, a, b) => this.deref(a) === this.deref(b) ? 1 : 0;
    impl.EnsureLocalCapacity = () => 0;
    impl.PushLocalFrame = () => 0;
    impl.PopLocalFrame = (env, h) => h;
    impl.GetObjectClass = (env, h) => {
      const o = this.deref(h);
      return this.ref(o instanceof JavaObject ? this.cls(o.cls) : this.cls(o && o.jstr !== undefined ? 'java/lang/String' : 'java/lang/Object'));
    };
    impl.IsInstanceOf = () => 1;
    impl.IsAssignableFrom = () => 1;
    const getMethod = isStatic => (env, c, name, sig) => {
      const cl = this.deref(c), n = rt.cstr(name), s = rt.cstr(sig);
      return this.ref({ cls: cl ? cl.name : '?', name: n, sigStr: s, sig: parseSig(s), isStatic });
    };
    impl.GetMethodID = getMethod(false);
    impl.GetStaticMethodID = getMethod(true);
    const getField = (env, c, name, sig) => {
      const cl = this.deref(c);
      return this.ref({ cls: cl ? cl.name : '?', name: rt.cstr(name), sig: rt.cstr(sig) });
    };
    impl.GetFieldID = getField;
    impl.GetStaticFieldID = getField;
    impl.AllocObject = (env, c) => this.ref(new JavaObject(this.deref(c).name));

    // Method calls: plain varargs, va_list and jvalue-array forms
    const jvalReaders = p => {
      const take = () => { const v = m32[p >>> 2]; p += 8; return v; };
      const take64 = signed => {
        const lo = m32[p >>> 2], hi = m32[(p + 4) >>> 2];
        p += 8;
        return sbig(lo, hi);
      };
      const takeD = () => {
        const v = w2d(m32[p >>> 2], m32[(p + 4) >>> 2]);
        p += 8;
        return v;
      };
      return [take, take64, takeD];
    };
    const makeCall = (kind, form, first) => () => {
      // first: register index of the first variadic argument
      const objH = r[1];
      const midH = r[first - 1];
      const method = this.deref(midH);
      let readers, isV = true;
      if (form === 'A') { readers = jvalReaders(r[first] >>> 0); isV = false; }
      else if (form === 'V') readers = rt.vaArgs(r[first]);
      else readers = rt.regArgs(first);
      const receiver = kind === 'static' ? null : this.unwrap(this.deref(objH));
      const v = this.invoke(method, receiver, readers, isV);
      if (rt.retry) return;
      this.ret(method.sig.ret, v);
    };
    const families = ['Object', 'Boolean', 'Byte', 'Char', 'Short', 'Int', 'Long', 'Float', 'Double', 'Void'];
    for (const t of families) {
      for (const form of ['', 'V', 'A']) {
        stubs[`Call${t}Method${form}`] = makeCall('virtual', form, 3);
        stubs[`CallNonvirtual${t}Method${form}`] = makeCall('virtual', form, 4);
        stubs[`CallStatic${t}Method${form}`] = makeCall('static', form, 3);
      }
    }
    const newObj = form => () => {
      const cl = this.deref(r[1]);
      const method = this.deref(r[2]);
      const obj = new JavaObject(cl.name);
      let readers, isV = true;
      if (form === 'A') { readers = jvalReaders(r[3] >>> 0); isV = false; }
      else if (form === 'V') readers = rt.vaArgs(r[3]);
      else readers = rt.regArgs(3);
      this.invoke(method, obj, readers, isV);
      r[0] = this.ref(obj);
    };
    stubs.NewObject = newObj('');
    stubs.NewObjectV = newObj('V');
    stubs.NewObjectA = newObj('A');

    // Fields
    const prim = ['Boolean', 'Byte', 'Char', 'Short', 'Int', 'Float'];
    const fieldStore = (obj, stat, fid) => {
      const f = this.deref(fid);
      return { f, holder: stat ? this.cls(f.cls).statics : (obj && obj.fields ? obj.fields : (obj.fields = {})) };
    };
    for (const stat of [false, true]) {
      const G = stat ? 'GetStatic' : 'Get', S = stat ? 'SetStatic' : 'Set';
      const owner = h => stat ? null : this.deref(h);
      impl[`${G}ObjectField`] = (env, h, fid) => {
        const { f, holder } = fieldStore(owner(h), stat, fid);
        return this.ref(this.wrap(holder[f.name]));
      };
      impl[`${S}ObjectField`] = (env, h, fid, v) => {
        const { f, holder } = fieldStore(owner(h), stat, fid);
        holder[f.name] = this.unwrap(this.deref(v));
        return 0;
      };
      for (const t of prim) {
        impl[`${G}${t}Field`] = (env, h, fid) => {
          const { f, holder } = fieldStore(owner(h), stat, fid);
          const v = holder[f.name] || 0;
          return t === 'Float' ? f2w(v) : (v === true ? 1 : v | 0);
        };
        impl[`${S}${t}Field`] = (env, h, fid, v) => {
          const { f, holder } = fieldStore(owner(h), stat, fid);
          holder[f.name] = t === 'Float' ? w2f(v) : v;
          return 0;
        };
      }
      stubs[`${G}LongField`] = () => {
        const { f, holder } = fieldStore(owner(r[1]), stat, r[2]);
        const b = BigInt(Math.trunc(holder[f.name] || 0));
        r[0] = Number(BigInt.asIntN(32, b));
        r[1] = Number(BigInt.asIntN(32, b >> 32n));
      };
      stubs[`${S}LongField`] = () => {
        const { f, holder } = fieldStore(owner(r[1]), stat, r[2]);
        const sp = r[13] >>> 0;
        // A 64-bit argument starts on an even register: r3 is skipped.
        holder[f.name] = Number(sbig(m32[sp >>> 2], m32[(sp + 4) >>> 2]));
      };
    }

    // Strings
    impl.NewStringUTF = (env, p) => p ? this.ref(this.str(rt.cstr(p))) : 0;
    impl.GetStringUTFChars = (env, h, isCopy) => {
      const o = this.deref(h);
      if (isCopy) m8[isCopy] = 1;
      return rt.newCstr(o ? o.jstr : '');
    };
    impl.ReleaseStringUTFChars = (env, h, p) => { rt.free(p); return 0; };
    impl.GetStringUTFLength = (env, h) => utf8e.encode(this.deref(h).jstr).length;
    impl.GetStringLength = (env, h) => this.deref(h).jstr.length;
    impl.GetStringChars = (env, h, isCopy) => {
      const s = this.deref(h).jstr;
      if (isCopy) m8[isCopy] = 1;
      const p = rt.malloc((s.length + 1) * 2);
      for (let i = 0; i < s.length; i++) cpu.m16[(p + i * 2) >>> 1] = s.charCodeAt(i);
      cpu.m16[(p + s.length * 2) >>> 1] = 0;
      return p;
    };
    impl.ReleaseStringChars = (env, h, p) => { rt.free(p); return 0; };
    impl.NewString = (env, p, n) => {
      let s = '';
      for (let i = 0; i < n; i++) s += String.fromCharCode(cpu.m16[(p + i * 2) >>> 1]);
      return this.ref(this.str(s));
    };

    // Arrays
    impl.GetArrayLength = (env, h) => { const a = this.deref(h); return a ? a.length : 0; };
    impl.NewObjectArray = (env, n) => this.ref(new Array(n).fill(null));
    impl.GetObjectArrayElement = (env, h, i) => this.ref(this.wrap(this.deref(h)[i]));
    impl.SetObjectArrayElement = (env, h, i, v) => { this.deref(h)[i] = this.unwrap(this.deref(v)); return 0; };
    this.arrayPool = new Map();
    for (const t of Object.keys(ARR)) {
      const A = ARR[t], B = A.BYTES_PER_ELEMENT;
      impl[`New${t}Array`] = (env, n) => {
        if (n <= 4096) {
          let bin = this.arrayPool.get(t + ':' + n);
          if (bin && bin.length) {
            const a = bin.pop();
            a.fill(0);
            return this.ref(a);
          }
          const p = rt.calloc(Math.max(16, n * B));
          const a = new A(cpu.buffer, p, n);
          a._poolKey = t + ':' + n;
          return this.ref(a);
        }
        return this.ref(new A(n));
      };
      impl[`Get${t}ArrayElements`] = (env, h, isCopy) => {
        const a = this.deref(h);
        if (a.buffer === cpu.buffer) { if (isCopy) m8[isCopy] = 0; return a.byteOffset; }
        if (isCopy) m8[isCopy] = 1;
        const p = rt.malloc(a.byteLength + 8);
        m8.set(new Uint8Array(a.buffer, a.byteOffset, a.byteLength), p);
        return p;
      };
      impl[`Release${t}ArrayElements`] = (env, h, p, mode) => {
        const a = this.deref(h);
        if (a && a.buffer === cpu.buffer) return 0;
        if (a && mode !== 2) new Uint8Array(a.buffer, a.byteOffset, a.byteLength).set(m8.subarray(p, p + a.byteLength));
        if (mode !== 1) rt.free(p);
        return 0;
      };
      impl[`Get${t}ArrayRegion`] = (env, h, start, len, buf) => {
        const a = this.deref(h);
        m8.set(new Uint8Array(a.buffer, a.byteOffset + start * B, len * B), buf);
        return 0;
      };
      impl[`Set${t}ArrayRegion`] = (env, h, start, len, buf) => {
        const a = this.deref(h);
        new Uint8Array(a.buffer, a.byteOffset + start * B, len * B).set(m8.subarray(buf, buf + len * B));
        return 0;
      };
    }
    impl.GetPrimitiveArrayCritical = (env, h, isCopy) => {
      const a = this.deref(h);
      if (isCopy) m8[isCopy] = 1;
      const p = rt.malloc(a.byteLength + 8);
      m8.set(new Uint8Array(a.buffer, a.byteOffset, a.byteLength), p);
      return p;
    };
    impl.ReleasePrimitiveArrayCritical = (env, h, p, mode) => {
      const a = this.deref(h);
      if (mode !== 2) new Uint8Array(a.buffer, a.byteOffset, a.byteLength).set(m8.subarray(p, p + a.byteLength));
      if (mode !== 1) rt.free(p);
      return 0;
    };
    impl.GetStringUTFRegion = (env, h, start, len, buf) => { rt.writeCstr(buf, this.deref(h).jstr.substr(start, len)); return 0; };
    impl.RegisterNatives = () => 0;
    impl.UnregisterNatives = () => 0;
    impl.MonitorEnter = () => 0;
    impl.MonitorExit = () => 0;
    impl.GetJavaVM = (env, out) => { m32[out >>> 2] = this.vm; return 0; };
    impl.NewDirectByteBuffer = (env, p, cap) => this.ref({ direct: p, cap });
    impl.GetDirectBufferAddress = (env, h) => {
      const o = this.deref(h);
      return o ? (o.direct !== undefined ? o.direct : (o.direct = rt.calloc(o.cap))) : 0;
    };
    impl.GetDirectBufferCapacity = (env, h) => this.deref(h).cap;

    // Build the tables
    const table = rt.malloc(NAMES.length * 4);
    NAMES.forEach((name, i) => {
      let addr;
      if (impl[name]) {
        rt.def('jni_' + name, impl[name]);
        addr = rt.fns['jni_' + name];
      } else if (stubs[name]) {
        const fn = stubs[name];
        addr = cpu.addTrap('jni_' + name, () => {
          fn();
          if (rt.retry) { rt.retry = false; cpu.stop = true; return; }
          r[15] = r[14];
        });
      } else {
        addr = cpu.addTrap('jni_' + name, () => { throw new Error('JNI not implemented: ' + name); });
      }
      m32[(table + i * 4) >>> 2] = addr;
    });
    this.env = rt.malloc(4);
    m32[this.env >>> 2] = table;
    const vmTable = rt.malloc(8 * 4);
    const envOut = (vm, out) => { m32[out >>> 2] = self_.env; return 0; };
    rt.def('vm_DestroyJavaVM', () => 0);
    rt.def('vm_AttachCurrentThread', envOut);
    rt.def('vm_DetachCurrentThread', () => 0);
    rt.def('vm_GetEnv', envOut);
    const vmFns = [0, 0, 0, 'vm_DestroyJavaVM', 'vm_AttachCurrentThread', 'vm_DetachCurrentThread', 'vm_GetEnv', 'vm_AttachCurrentThread'];
    vmFns.forEach((n, i) => { m32[(vmTable + i * 4) >>> 2] = n ? rt.fns[n] : 0; });
    this.vm = rt.malloc(4);
    m32[this.vm >>> 2] = vmTable;
  }
}

Java.JavaObject = JavaObject;
root.ApkJava = Java;
if (typeof module !== 'undefined') module.exports = Java;
})(typeof globalThis !== 'undefined' ? globalThis : this);
