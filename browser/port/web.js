// Browser side: virtual file system over the kns_data scripts, canvas text, the frame loop.
(function () {
'use strict';
const SCREEN = { w: 2048, h: 1184 };
// Game logic moves a fixed step per tick, so this is also game speed. The phone ran 50 ms in
// menus and 34 ms in battle. Twice the rate would double the speed of battle, not smooth it.
const TICK_MS = 1000 / 30;
const canvas = document.getElementById('game');
canvas.width = SCREEN.w;
canvas.height = SCREEN.h;
const statusEl = document.getElementById('status');
const logLines = [];
const log = s => {
  const line = String(s).replace(/\n$/, '');
  logLines.push(line);
  if (logLines.length > 4000) logLines.shift();
  console.log(line);
};
window.knsLog = logLines;

const b64bytes = b64 => {
  const s = atob(b64);
  const len = s.length;
  const out = new Uint8Array(len);
  let i = 0;
  const end4 = len - 3;
  for (; i < end4; i += 4) {
    out[i] = s.charCodeAt(i);
    out[i + 1] = s.charCodeAt(i + 1);
    out[i + 2] = s.charCodeAt(i + 2);
    out[i + 3] = s.charCodeAt(i + 3);
  }
  for (; i < len; i++) out[i] = s.charCodeAt(i);
  return out;
};

// ---------- files ----------
const INDEX = window.KNS_INDEX;   // asset name -> [size, script number]
const assets = new Map();
const loading = new Map();
const files = new Map();
const SAVE_PREFIX = 'kns:file:';
const safeGet = k => { try { return localStorage.getItem(k); } catch (e) { return null; } };
const safeSet = (k, v) => { try { localStorage.setItem(k, v); return true; } catch (e) { /* page still works, nothing persists */ return false; } };
const safeDel = k => { try { localStorage.removeItem(k); } catch (e) { /* no storage */ } };

// ---------- settings: saved per browser. Every default is the behaviour without the panel. ----------
const SETTINGS_KEY = 'kns-settings-v1';
const RES_SCALE = { high: 1, medium: 0.75, low: 0.6, verylow: 0.45 };
const settings = (() => {
  const mem = typeof navigator !== 'undefined' && navigator.deviceMemory;
  const s = { resolution: 'high', memorySaver: mem > 0 && mem <= 4, showFps: false };
  let saved = null;
  try { saved = JSON.parse(safeGet(SETTINGS_KEY)); } catch (e) { /* defaults */ }
  // The earlier Low-spec checkbox was 60% resolution.
  if (!saved && safeGet('kns-lowspec-v1') === '1') saved = { resolution: 'low' };
  if (saved && typeof saved === 'object') {
    if (Object.hasOwn(RES_SCALE, saved.resolution)) s.resolution = saved.resolution;
    for (const k of ['memorySaver', 'showFps']) if (typeof saved[k] === 'boolean') s[k] = saved[k];
  }
  return s;
})();
const saveSettings = () => { safeSet(SETTINGS_KEY, JSON.stringify(settings)); safeDel('kns-lowspec-v1'); };

// ---------- asset memory: decoded files stay in RAM up to a cap; older ones are read again from Cache Storage ----------
let assetBytes = 0;
const cachedNames = new Set();   // files known to be in Cache Storage
const acceptedAt = new Map();
const assetCap = () => (settings.memorySaver ? 96 : 320) * 1048576;
const dropAsset = name => {
  const d = assets.get(name);
  if (!d) return;
  assetBytes -= d.length;
  assets.delete(name);
  acceptedAt.delete(name);
};
// Oldest use first. Open files keep their own reference, so dropping the map entry never breaks a read in progress.
function trimAssets(keep) {
  if (assetBytes <= assetCap()) return;
  const now = performance.now();
  for (const name of assets.keys()) {
    if (assetBytes <= assetCap()) break;
    if (name === keep || !cachedNames.has(name) || now - (acceptedAt.get(name) || 0) < 3000) continue;
    dropAsset(name);
  }
}
const useAsset = name => {
  const d = assets.get(name);
  if (d) { assets.delete(name); assets.set(name, d); }
  return d;
};
// Callers waiting for a file (sound, movie).
const assetWaiters = new Map();
const whenAsset = (name, fn) => {
  if (assets.has(name)) { fn(); return; }
  const list = assetWaiters.get(name);
  if (list) list.push(fn); else assetWaiters.set(name, [fn]);
  loadAsset(name);
};

// How many times each file arrived. A file can leave the map right after it arrives (a sound moves into its own
// Blob), so "did this script deliver it" is a count, not assets.has().
const arrivals = new Map();
function acceptAsset(name, data) {
  arrivals.set(name, (arrivals.get(name) || 0) + 1);
  dropAsset(name);
  assets.set(name, data);
  assetBytes += data.length;
  acceptedAt.set(name, performance.now());
  const l = loading.get(name);
  if (l) { l.remove(); loading.delete(name); }
  wanted.delete(name);
  credit(name, 1);
  const waiting = assetWaiters.get(name);
  if (waiting) { assetWaiters.delete(name); for (const fn of waiting) fn(); }
  trimAssets(name);
  // A load opens files one after another: carry on now instead of at the next refresh.
  if (game && game.pending && running) {
    try { game.frame(8, false); } catch (e) { fatal(e); }
  }
}
window.KNS_ASSET = (name, b64) => {
  try { acceptAsset(name, window.KnsAssetValidation.decode(name, b64, INDEX, atob)); }
  catch (e) { fatal(new Error('Invalid game data: ' + name + '. Reload and check the game files.')); }
};
const assetName = p => {
  const base = p.split('/').pop();
  if (INDEX[base + '.jpg']) return base + '.jpg';
  if (INDEX[base]) return base;
  return null;
};
const assetUrl = name => (window.KNS_DATA || 'kns_data') + '/' + INDEX[name][1] + '.js';
const scriptAsset = name => {
  const s = document.createElement('script');
  s.src = assetUrl(name);
  const before = arrivals.get(name) || 0;
  s.onerror = () => { loading.delete(name); fatal(new Error('Game data failed to load: ' + name + '. Reload and check the game files.')); };
  s.onload = () => { if ((arrivals.get(name) || 0) === before && running) fatal(new Error('Invalid game data: ' + name + '. Reload and check the game files.')); };
  loading.set(name, s);
  document.head.appendChild(s);
};

// ---------- downloads: fetch reports bytes, which a script tag cannot; file:// has no fetch ----------
const CAN_FETCH = location.protocol === 'http:' || location.protocol === 'https:';
const PRELOAD = window.KNS_PRELOAD || { startup: [], lobby: [] };
const LOBBY = new Set(PRELOAD.lobby);
// Each download holds a few copies of its file while it decodes.
const fetchLimit = () => (settings.memorySaver ? 2 : 4);
const wanted = new Set();   // files the game itself asked for
const bulk = new Set();     // full-download files: stored in Cache Storage, kept in RAM only if the game wants them
const dl ={ total: 0, done: 0, credited: new Map(), queue: [], active: 0, lobby: false, full: false };
const dlEl = document.createElement('div');
dlEl.id = 'dl';
dlEl.hidden = true;
dlEl.innerHTML = '<div id="dl-text"></div><div id="dl-track"><div id="dl-fill"></div></div>';
document.body.appendChild(dlEl);
const dlText = dlEl.querySelector('#dl-text'), dlFill = dlEl.querySelector('#dl-fill');
const expect = name => {
  if (!CAN_FETCH || assets.has(name) || dl.credited.has(name)) return;
  dl.credited.set(name, 0);
  dl.total += INDEX[name][0];
};
// Marks a share of one file as downloaded.
function credit(name, share) {
  const was = dl.credited.get(name);
  if (was === undefined) return;
  const now = Math.max(was, Math.min(1, share));
  dl.credited.set(name, now);
  dl.done += (now - was) * INDEX[name][0];
  renderDownloads();
}
function renderDownloads() {
  // Nothing downloading and nothing shown: the usual case, once per display refresh.
  if (!dl.total && dlEl.hidden && gl.presents) return;
  const idle = !dl.active && !dl.queue.length;
  if (idle && dl.total) {
    dl.total = dl.done = 0;
    dl.credited.clear();
    if (dl.full) dl.fullDone = !dl.fullFailed;
    dl.full = false;
    dl.fullFailed = false;
  }
  // In the game the bar only appears when a download is holding it up, or when the player asked for the full download.
  const stalled = gl.presents === 0 || performance.now() - lastFlip > 300;
  const show = dl.total > 0 && (stalled || dl.full);
  if (show) {
    const mb = v => (v / 1048576).toFixed(1);
    const part = dl.done / dl.total;
    dlText.textContent = `Downloading game data  ${mb(dl.done)} / ${mb(dl.total)} MB  (${Math.floor(part * 100)}%)`;
    dlFill.style.transform = `scaleX(${part})`;
  }
  if (dlEl.hidden === show) dlEl.hidden = !show;
  if (gl.presents === 0 && running) statusEl.hidden = show;
}
// ---------- persistent asset cache: a hard reload (Ctrl+F5) bypasses the HTTP cache, not Cache Storage ----------
// Entries hold validated decoded bytes keyed by the hashed asset URL; a new data folder drops the old entries.
const ASSET_CACHE = 'kns-assets-v1';
const assetCache = (() => {
  if (!CAN_FETCH || typeof caches === 'undefined' || !window.isSecureContext) return Promise.resolve(null);
  return caches.open(ASSET_CACHE).then(cache => {
    const folder = '/' + (window.KNS_DATA || 'kns_data') + '/';
    cache.keys().then(keys => {
      const byPath = new Map(Object.keys(INDEX).map(n => [new URL(assetUrl(n), location.href).pathname, n]));
      for (const req of keys) {
        const path = new URL(req.url).pathname;
        if (!path.includes(folder)) cache.delete(req);
        else if (byPath.has(path)) cachedNames.add(byPath.get(path));
      }
      renderFullButton();
    }).catch(() => {});
    return cache;
  }).catch(() => null);
})();
const cacheKey = name => new URL(assetUrl(name), location.href).href;
const cachedAsset = async name => {
  try {
    const cache = await assetCache;
    const res = cache && await cache.match(cacheKey(name));
    if (!res) { cachedNames.delete(name); return null; }
    const data = new Uint8Array(await res.arrayBuffer());
    if (data.length === INDEX[name][0]) { cachedNames.add(name); return data; }
    cachedNames.delete(name);
    cache.delete(cacheKey(name));
  } catch (e) { /* storage unavailable: download instead */ }
  return null;
};
const isCached = async name => {
  try {
    const cache = await assetCache;
    if (cache && await cache.match(cacheKey(name))) { cachedNames.add(name); return true; }
  } catch (e) { /* storage unavailable */ }
  cachedNames.delete(name);
  return false;
};
const storeAsset = async (name, data) => {
  try {
    const cache = await assetCache;
    if (!cache) return;
    await cache.put(cacheKey(name), new Response(data, { headers: { 'Content-Type': 'application/octet-stream' } }));
    cachedNames.add(name);
  } catch (e) { /* quota or storage blocked: the game still runs, it only downloads again */ }
};
const fetchAsset = async name => {
  loading.set(name, { remove() {} });
  dl.active++;
  try {
    // A full-download file the game has not asked for only needs to be in the cache, not in RAM.
    if (bulk.has(name) && !wanted.has(name) && await isCached(name) && !wanted.has(name)) {
      loading.delete(name);
      credit(name, 1);
    } else {
      const hit = await cachedAsset(name);
      if (hit) acceptAsset(name, hit);
      else await downloadAsset(name);
    }
  } catch (e) {
    loading.delete(name);
    if (bulk.has(name) && !wanted.has(name)) {
      // Optional full download: one failed file must not crash the game. It loads normally if the game opens it,
      // and the Full download button stays available to retry.
      log('full download skipped ' + name + ': ' + e.message);
      dl.fullFailed = true;
      credit(name, 1);
    } else scriptAsset(name);
  } finally {
    dl.active--;
  }
  pumpDownloads();
  renderDownloads();
  renderFullButton();
};
const downloadAsset = async name => {
  const res = await fetch(assetUrl(name));
  if (!res.ok) throw new Error('HTTP ' + res.status);
  // Each script holds the file as base64, about 4/3 of its size.
  const expected = INDEX[name][0] * 4 / 3;
  const reader = res.body.getReader(), chunks = [];
  let got = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    chunks.push(value);
    got += value.length;
    credit(name, got / expected);
  }
  // KNS_ASSET("name","base64"); decoded from the bytes, with no string of the whole file.
  const data = window.KnsAssetValidation.parseChunks(chunks, got, name, INDEX);
  chunks.length = 0;
  if (bulk.has(name) && !wanted.has(name)) {
    await storeAsset(name, data);
    if (!wanted.has(name)) {
      loading.delete(name);
      credit(name, 1);
      return;
    }
  } else {
    // Response copies the bytes, so the game may use its array while the cache write finishes.
    storeAsset(name, data).then(() => trimAssets());
  }
  acceptAsset(name, data);
};
function pumpDownloads() {
  while (dl.active < fetchLimit() && dl.queue.length) {
    const name = dl.queue.shift();
    if (assets.has(name) || loading.has(name)) continue;
    if (bulk.has(name) && !wanted.has(name) && cachedNames.has(name)) { credit(name, 1); continue; }
    fetchAsset(name);
  }
}
// Fetches a known group of files side by side, and tells the bar the total up front.
// keep false (full download): the files go to Cache Storage and stay out of RAM until the game opens them.
function preload(names, keep = true) {
  if (!CAN_FETCH) return;
  for (const name of names) {
    if (!INDEX[name] || assets.has(name) || loading.has(name)) continue;
    if (!keep) {
      if (cachedNames.has(name)) continue;
      bulk.add(name);
    } else wanted.add(name);
    expect(name);
    dl.queue.push(name);
  }
  pumpDownloads();
}
const loadAsset = name => {
  if (assets.has(name)) return;
  wanted.add(name);
  if (loading.has(name)) return;
  if (LOBBY.has(name) && !dl.lobby) {
    dl.lobby = true;
    preload(PRELOAD.lobby);
    if (loading.has(name)) return;
  }
  expect(name);
  if (CAN_FETCH) fetchAsset(name); else scriptAsset(name);
};
const toB64 = bytes => {
  let s = '';
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
  return btoa(s);
};
const assetPaths = new Set();   // paths the game has opened as game files
const vfs = {
  norm:p => p.replace(/\/+/g, '/'),
  open(p, creat, trunc) {
    p = this.norm(p);
    let f = files.get(p);
    if (!f) {
      const saved = safeGet(SAVE_PREFIX + p);
      if (saved !== null) {
        const data = b64bytes(saved);
        f = { data, size: data.length, path: p };
        files.set(p, f);
      }
    }
    if (!f) {
      const a = assetName(p);
      if (a && (!creat || assetPaths.has(p))) {
        // Game files are not kept in `files`: each open takes the bytes from the asset map, which may drop them later.
        const data = useAsset(a);
        if (!data) { loadAsset(a); return 'pending'; }
        assetPaths.add(p);
        f = { data, size: data.length, path: p, asset: true };
      } else if (creat) {
        f = { data: new Uint8Array(1024), size: 0, path: p };
        files.set(p, f);
      } else return null;
    }
    if (trunc) f.size = 0;
    return f;
  },
  ready(p) {
    const a = assetName(this.norm(p));
    return !a || assets.has(a);
  },
  write(f, pos, bytes) {
    if (pos + bytes.length > f.data.length) {
      const n = new Uint8Array(Math.max(pos + bytes.length, f.data.length * 2, 1024));
      n.set(f.data.subarray(0, f.size));
      f.data = n;
    }
    f.data.set(bytes, pos);
    f.size = Math.max(f.size, pos + bytes.length);
  },
  // true: stored, false: the browser refused (storage full or blocked), null: this file is never stored.
  commit(f) { return !f.asset && f.size < 2000000 ? safeSet(SAVE_PREFIX + f.path, toB64(f.data.subarray(0, f.size))) : null; },
  remove(p) {
    p = this.norm(p);
    safeDel(SAVE_PREFIX + p);
    return files.delete(p);
  },
  rename(a, b) {
    a = this.norm(a); b = this.norm(b);
    const f = files.get(a);
    if (!f) return false;
    files.delete(a);
    f.path = b;
    files.set(b, f);
    if (a === b) { this.commit(f); return true; }
    // Write the new name first, then drop the old one. If storage is full, free the old copy's space and try
    // once more (the order used before). A refused write leaves the previous value under the new name intact.
    if (this.commit(f) === true) { safeDel(SAVE_PREFIX + a); return true; }
    safeDel(SAVE_PREFIX + a);
    if (this.commit(f) === false) log('save ' + b + ' could not be stored: browser storage is full or blocked');
    return true;
  },
  mkdir() { return true; },
  stat(p) {
    p = this.norm(p);
    if (files.has(p)) return files.get(p);
    if (safeGet(SAVE_PREFIX + p) !== null) return this.open(p, 0, 0);
    const a = assetName(p);
    if (a) return { size: INDEX[a][0] };
    if (/\/(files|cache|1\.4\.0|game_res)\/?$/.test(p)) return { size: 0, isDir: true };
    return null;
  },
  list() { return []; },
};

// ---------- text: the font the Android port draws through Paint/Canvas ----------
const tc = document.createElement('canvas');
tc.width = 2048;
tc.height = 2048;
const t2 = tc.getContext('2d', { willReadFrequently: true });
const FONT = '"Montserrat", "Segoe UI", "Roboto", sans-serif';
// Montserrat runs about 9% wider than the font on the reference phone.
const FONT_SCALE = 0.91;
let curFontKey = -1;
const setFont = (size, style) => {
  const k = (size << 4) | (style & 15);
  if (curFontKey === k) return;
  curFontKey = k;
  t2.font = `${style & 2 ? 'italic ' : ''}${style & 1 ? '700' : '500'} ${size * FONT_SCALE}px ${FONT}`;
  t2.textBaseline = 'alphabetic';
};
const copyOut = (pixels, w, rows) => {
  if (w <= 0 || rows <= 0) return;
  const img = t2.getImageData(0, 0, w, rows).data;
  for (let i = 0, n = w * rows; i < n; i++) {
    const j = i * 4;
    pixels[i] = (img[j + 3] << 24) | (img[j] << 16) | (img[j + 1] << 8) | img[j + 2];
  }
};
// Only what the last draw touched needs clearing: the canvas is 16 MB of software pixels.
let dirtyW = 2048, dirtyH = 2048;
const clearText = (w, h) => {
  t2.clearRect(0, 0, dirtyW, dirtyH);
  dirtyW = Math.min(2048, Math.ceil(w) + 64);
  dirtyH = Math.min(2048, Math.ceil(h) + 64);
};
// The game rasterises the same labels again and again.
const memo = new Map();
let memoBytes = 0;
const memoMax = () => (settings.memorySaver ? 12 : 32) * 1024 * 1024;
const remember = (key, value, bytes) => {
  if (memoBytes + bytes > memoMax()) { memo.clear(); memoBytes = 0; }
  memo.set(key, value);
  memoBytes += bytes;
  return value;
};
// Anything measured or drawn before the web font arrived used a fallback font.
if (document.fonts && document.fonts.addEventListener) {
  document.fonts.addEventListener('loadingdone', () => { memo.clear(); memoBytes = 0; curFontKey = -1; });
}
const measure = (str, size, style) => {
  const key = 'm' + size + '/' + style + '/' + str;
  const v = memo.get(key);
  if (v !== undefined) return v;
  setFont(size, style);
  return remember(key, t2.measureText(str).width, 64 + str.length);
};
// Browser-only UI overrides (hidden labels and sprites) live in ui_overrides.js.
const UI = window.KnsUiOverrides;
const HIDDEN_TEXT = UI.labels.length ? new RegExp(UI.labels.map(r => r.source).join('|')) : /(?!)/;
// Recent labels the game drew, newest last, for override conditions (see ui.lastLabel in ui_overrides.js).
const recentLabels = [];
const noteLabel = s => { recentLabels.push(s); if (recentLabels.length > 256) recentLabels.splice(0, 128); };
const text = {
  width(str, size, style) { return Math.floor(measure(str, size, style) + 1); },
  width0(str, size, style) { return measure(str, size, style); },
  metrics(size, style) {
    const key = 'mt' + size + '/' + style;
    const hit = memo.get(key);
    if (hit !== undefined) return hit;
    setFont(size, style);
    const m = t2.measureText('Hg');
    const asc = Math.round(m.fontBoundingBoxAscent), desc = Math.round(m.fontBoundingBoxDescent);
    return remember(key, { top: -Math.round(asc * 1.05), ascent: -asc, descent: desc }, 64);
  },
  draw(pixels, w, rows, str, size, style) {
    noteLabel(str);
    if (HIDDEN_TEXT.test(str)) { pixels.fill(0, 0, Math.max(0, w * rows)); return; }
    const key = 'd' + w + 'x' + rows + '/' + size + '/' + style + '/' + str;
    const hit = memo.get(key);
    if (hit) { pixels.set(hit); return; }
    setFont(size, style);
    clearText(Math.max(w, t2.measureText(str).width), Math.max(rows, size * 2));
    t2.textAlign = 'left';
    t2.fillStyle = '#fff';
    t2.fillText(str, 0, size);
    copyOut(pixels, w, rows);
    if (w > 0 && rows > 0 && w * rows < 1 << 20) remember(key, pixels.slice(0, w * rows), w * rows * 4);
  },
  drawLines(pixels, width, lay, align, size, style) {
    const rows = Math.min(lay.height, 2048);
    if (width <= 0 || rows <= 0) return;
    const shown = lay.lines.map(l => l.text).join('\n');
    noteLabel(shown);
    if (HIDDEN_TEXT.test(shown)) { pixels.fill(0, 0, width * rows); return; }
    const key = lay.key ? ('dl' + align + '/' + lay.key) : '';
    if (key) {
      const hit = memo.get(key);
      if (hit) { pixels.set(hit); return; }
    }
    clearText(2048, Math.max(lay.height, 0) + size * 2);
    setFont(size, style);
    t2.fillStyle = '#fff';
    t2.textAlign = align === 1 ? 'center' : align === 2 ? 'right' : 'left';
    const x = align === 1 ? width / 2 : align === 2 ? width : 0;
    for (const l of lay.lines) t2.fillText(l.text, x, l.y);
    copyOut(pixels, width, rows);
    if (key && width * rows < 1 << 19) remember(key, pixels.slice(0, width * rows), width * rows * 4);
  },
};

// ---------- sound: <audio> elements over the game's ogg files ----------
const MUTED = location.hash === '#mute';
const handles = new Set();
// One Blob per sound file, shared by every handle that plays it and kept for the session.
// The Blob holds its own copy, so the decoded bytes leave the asset map when they can be fetched again.
const soundUrls = new Map();
const soundUrl = name => {
  let url = soundUrls.get(name);
  if (!url) {
    url = URL.createObjectURL(new Blob([assets.get(name)], { type: 'audio/ogg' }));
    soundUrls.set(name, url);
    if (CAN_FETCH) dropAsset(name);
  }
  return url;
};
const audio = {
  create(path) {
    const name = assetName(path);
    const h = { name, el: null, vol: 1, wantPlay: false, loop: false, suspended: false, nativePaused: false, blocked: false, dead: false };
    handles.add(h);
    return h;
  },
  // Every start goes through here. Before the first tap or key the browser refuses sound (NotAllowedError);
  // such a handle is retried by unlock() on the next user gesture.
  start(h) {
    const p = h.el.play();
    if (p) p.catch(e => { if (e && e.name === 'NotAllowedError') h.blocked = true; });
  },
  // Called on user gestures (web.js input listeners). Only looping tracks (music) are restarted late; a refused
  // one-shot effect is simply lost, as it would be stale by now.
  unlock() {
    for (const h of handles) {
      if (!h.blocked) continue;
      h.blocked = false;
      if (h.el && h.wantPlay && h.el.loop && h.el.paused && !h.el.ended && !h.nativePaused && !h.suspended
          && running && !document.hidden) this.start(h);
    }
  },
  // Android pauses the app when it leaves the screen: no sound from a hidden tab.
  pauseAll() {
    for (const h of handles) {
      if (h.el && !h.el.paused) { h.suspended = true; h.el.pause(); }
    }
  },
  resumeAll() {
    for (const h of handles) {
      if (!h.suspended) continue;
      h.suspended = false;
      if (h.el && h.wantPlay && !h.el.ended) this.start(h);
    }
  },
  ready(h, then) {
    if (h.el) { then(); return; }
    if (!h.name || h.dead) return;
    const go = () => {
      if (h.dead) return;
      if (!h.el) {
        h.el = new Audio(soundUrl(h.name));
        h.el.volume = h.vol;
      }
      then();
    };
    if (soundUrls.has(h.name)) go(); else whenAsset(h.name, go);
  },
  play(h, loop) {
    if (MUTED) return;
    h.wantPlay = true;
    h.nativePaused = false;
    this.ready(h, () => {
      if (!h.wantPlay || !running) return;
      if (document.hidden) {
        // The game keeps running in a hidden tab. Music it starts there plays when the tab returns (resumeAll);
        // a one-shot effect is dropped.
        if (loop) { h.el.loop = true; h.el.currentTime = 0; h.suspended = true; }
        return;
      }
      h.el.loop = !!loop;
      h.el.currentTime = 0;
      this.start(h);
    });
  },
  stop(h) {
    h.wantPlay = false; h.nativePaused = false; h.suspended = false; h.blocked = false;
    if (h.el) { h.el.pause(); h.el.currentTime = 0; }
  },
  pause(h) {
    if (!h.el) return;
    // Already paused by the hidden tab: the game's own pause wins, so the tab's return must not restart it.
    if (h.suspended) { h.suspended = false; h.nativePaused = true; return; }
    if (!h.el.paused && !h.el.ended) { h.nativePaused = true; h.el.pause(); }
  },
  resume(h) {
    const paused = h.nativePaused;
    h.nativePaused = false;
    if (!paused || !h.el || !h.wantPlay || h.el.ended || !running) return;
    if (document.hidden) { h.suspended = true; return; }   // resumeAll plays it when the tab returns
    this.start(h);
  },
  volume(h, v) { h.vol = Math.max(0, Math.min(1, v)); if (h.el) h.el.volume = h.vol; },
  destroy(h) {
    h.dead = true;
    handles.delete(h);
    // Dropping the source lets the browser free the element's decoder; the shared Blob URL stays.
    if (h.el) { h.el.pause(); h.el.removeAttribute('src'); h.el.load(); h.el = null; }
    h.wantPlay = false;
  },
};

// ---------- text fields and the intro movie sit over the canvas ----------
let liveMovie = null;
const place = (el, frame) => {
  const r = canvas.getBoundingClientRect();
  const k = r.width / SCREEN.w;
  Object.assign(el.style, {
    position: 'fixed', zIndex: 3, boxSizing: 'border-box', left: r.left + frame[0] * k + 'px', top: r.top + frame[1] * k + 'px',
    width: frame[2] * k + 'px', height: frame[3] * k + 'px', font: `${Math.max(12, frame[3] * k * 0.6)}px Montserrat, sans-serif`,
    background: 'transparent', color: '#fff', textShadow: '0 0 2px #000, 0 0 2px #000, 0 0 2px #000', border: '0', outline: 'none', padding: '0 4px',
  });
};
const ui = {
  createField(f, send) {
    const el = document.createElement('input');
    const update = () => {
      place(el, f.frame);
      el.value = f.text;
      el.placeholder = f.props.placeholder || '';
      if (f.props.MaxTextLength > 0) el.maxLength = f.props.MaxTextLength;
      el.type = f.props.Secure ? 'password' : 'text';
    };
    update();
    // ProTextInputCallBack@0x164190: event 1 is the return key (the chat field sends on it), event 2 is editing ended (the invite and message-box fields).
    el.addEventListener('keydown', e => { e.stopPropagation(); if (e.key === 'Enter') { f.text = el.value; send(1); el.blur(); } });
    el.addEventListener('blur', () => { f.text = el.value; send(2); });
    document.body.appendChild(el);
    return {
      update,
      value: () => el.value,
      focus: on => (on ? el.focus() : el.blur()),
      destroy: () => el.remove(),
    };
  },
  playMovie(done) {
    const name = 'imhero_intro.mp4.jpg';
    const show = () => {
      const url = URL.createObjectURL(new Blob([assets.get(name)], { type: 'video/mp4' }));
      const v = document.createElement('video');
      v.src = url;
      v.autoplay = true;
      v.style.cssText = 'position:fixed;inset:0;width:100%;height:100%;background:#000;z-index:4;object-fit:contain';
      liveMovie = v;
      let ended = false;
      const end = () => {
        if (ended) return;
        ended = true;
        if (liveMovie === v) liveMovie = null;
        v.remove();
        URL.revokeObjectURL(url);
        if (cachedNames.has(name)) dropAsset(name); // 10+ MB the game never opens again
        done();
      };
      v.onended = end;
      v.onerror = end;
      v.onclick = end;
      document.body.appendChild(v);
      const p = v.play();
      if (p) p.catch(end);
    };
    whenAsset(name, show);
  },
};

// ---------- the phone's back key ----------
// One DOWN per press (key auto-repeat would close several popups) and an UP only for a press the game saw.
let backDown = false;
addEventListener('keydown', e => {
  if (e.key !== 'Escape' || e.repeat || backDown || !game || controlsPanelOpen) return;
  backDown = true;
  game.java.addEvent(0x1000, 4, -1, -1, -1);
});
addEventListener('keyup', e => {
  if (e.key !== 'Escape' || !backDown) return;
  backDown = false;
  if (game) game.java.addEvent(0x1001, 4, -1, -1, -1);
});
// Escape released in another window never sends keyup here.
addEventListener('blur', () => {
  if (!backDown) return;
  backDown = false;
  if (game) game.java.addEvent(0x1001, 4, -1, -1, -1);
});

// ---------- the browser allows sound only after a user gesture: retry refused music on the next one ----------
for (const type of ['pointerdown', 'pointerup', 'touchend', 'keydown', 'click'])
  addEventListener(type, () => audio.unlock(), { capture: true, passive: true });

// ---------- boot ----------
const fatal = e => {
  log('CRASH ' + (e && e.stack || e));
  dlEl.hidden = true;
  statusEl.textContent = 'Crashed: ' + (e && e.message || e);
  statusEl.hidden = false;
  try { game && game.dumpState(); } catch (e2) { /* the crash message above is what matters */ }
  running = false;
  // Nothing keeps playing over the crash message.
  try {
    audio.pauseAll();
    for (const h of handles) h.wantPlay = false;
    if (liveMovie) liveMovie.pause();
  } catch (e2) { /* best effort */ }
};
let game = null, running = true;
const gl = new window.ApkGLES1(canvas, log);
window.knsGL = gl;
// ---------- browser-only UI overrides: hidden game sprites (rules in ui_overrides.js) ----------
const HIDDEN_SPRITES = UI.sprites;
const uiCtx = { lastLabel: re => { for (let i = recentLabels.length - 1; i >= 0; i--) if (re.test(recentLabels[i])) return recentLabels[i]; return ''; } };
const hideSizes = new Set(HIDDEN_SPRITES.map(h => h.tex.split('/')[0]));
const texHash = (bytes) => {
  let h = 0x811c9dc5;
  for (let i = 0; i < bytes.length; i++) h = Math.imul(h ^ bytes[i], 0x01000193);
  return (h >>> 0).toString(16);
};
let sceneAddr = 0;
const sceneNow = () => {
  if (!sceneAddr) sceneAddr = (game && game.rt.syms.get('g_ui16CurrentScene')) || 0;
  return sceneAddr ? game.rt.cpu.ru16(sceneAddr) : -1;
};
// The screen box of a draw, in game pixels, from the current vertex array, modelview and projection.
// Written into BOX: this runs for every textured draw of a scene with a '*' rule, so it allocates nothing
// on the usual path (aligned fixed or float vertices in guest memory).
const BOX = [0, 0, 0, 0];
const boxInto = (G, first, count) => {
  const v = G.arrays.v, type = v.type, stride = v.stride || v.size * (type === 0x1402 ? 2 : 4);
  const sm = G.stacks[0x1700], sp = G.stacks[0x1701];
  const mv = sm[sm.length - 1], pr = sp[sp.length - 1], vp = G.viewport;
  const direct = !v.vbo && ((v.ptr | stride) & 3) === 0 && (type === 0x140C || type === 0x1406);
  let src = null;
  if (!direct) {
    const b = v.vbo && G.buffers.get(v.vbo).data;
    src = b ? new DataView(b.buffer, b.byteOffset) : new DataView(G.cpu.buffer);
  }
  const m32 = G.cpu.m32, f32 = G.cpu.f32;
  let x0 = 1e9, y0 = 1e9, x1 = -1e9, y1 = -1e9;
  for (let i = 0; i < count; i++) {
    const o = v.ptr + (first + i) * stride;
    let X, Y;
    if (direct) {
      if (type === 0x140C) { X = m32[o >>> 2] / 65536; Y = m32[(o >>> 2) + 1] / 65536; }
      else { X = f32[o >>> 2]; Y = f32[(o >>> 2) + 1]; }
    } else if (type === 0x140C) { X = src.getInt32(o, true) / 65536; Y = src.getInt32(o + 4, true) / 65536; }
    else if (type === 0x1406) { X = src.getFloat32(o, true); Y = src.getFloat32(o + 4, true); }
    else { X = src.getInt16(o, true); Y = src.getInt16(o + 2, true); }
    const ex = mv[0] * X + mv[4] * Y + mv[12], ey = mv[1] * X + mv[5] * Y + mv[13];
    const cx = pr[0] * ex + pr[4] * ey + pr[12], cy = pr[1] * ex + pr[5] * ey + pr[13];
    const sx = (cx + 1) / 2 * vp[2], sy = (1 - cy) / 2 * vp[3]; // the game sets a full-screen viewport in game pixels
    if (sx < x0) x0 = sx; if (sx > x1) x1 = sx; if (sy < y0) y0 = sy; if (sy > y1) y1 = sy;
  }
  BOX[0] = Math.round(x0); BOX[1] = Math.round(y0); BOX[2] = Math.round(x1); BOX[3] = Math.round(y1);
};
const drawBox = (G, first, count) => { boxInto(G, first, count); return BOX.slice(); };
const texImageFn = gl.glTexImage2D;
gl.glTexImage2D = function (t, level, ifmt, w, h, border, format, type, p) {
  texImageFn.call(this, t, level, ifmt, w, h, border, format, type, p);
  const e = this.textures.get(this.bound);
  if (!e || this.texUnit !== 0 || level !== 0) return;
  e.sig = p && (hideSizes.has(w + 'x' + h) || w * h <= 256 * 256 || window.knsUi.probing) ? w + 'x' + h + '/' + texHash(this.pixelView(p, w, h, format, type)) : '';
  e.hide = HIDDEN_SPRITES.filter(r => r.tex === e.sig);
};
const texSubImageFn = gl.glTexSubImage2D;
gl.glTexSubImage2D = function (...args) {
  texSubImageFn.apply(this, args);
  const e = this.textures.get(this.bound);
  if (e && e.sig && this.texUnit === 0) { e.sig = ''; e.hide = null; } // changed pixels: the hash no longer holds
};
const drawArraysFn = gl.glDrawArrays;
const anyTexRules = HIDDEN_SPRITES.filter(r => r.tex === '*');
const nAny = anyTexRules.length;
let drawHidden = false;   // the last draw was hidden by a rule (?perf=3 keeps its reference copy in step)
gl.glDrawArrays = function (mode, first, count) {
  drawHidden = false;
  const e = this.tex2d && this.arrays.t.on ? this.textures.get(this.bound) : null;
  if (e && count > 0) {
    const own = e.hide, nOwn = own ? own.length : 0;
    if (nOwn || nAny) {
      let scene = -2, boxed = false;
      for (let i = 0, n = nOwn + nAny; i < n; i++) {
        const r = i < nOwn ? own[i] : anyTexRules[i - nOwn];
        if (r.scene !== undefined) {
          if (scene === -2) scene = sceneNow();
          if (scene !== r.scene) continue;
        }
        const rb = r.box;
        if (rb) {
          if (!boxed) { boxInto(this, first, count); boxed = true; }
          if (BOX[0] < rb[0] || BOX[1] < rb[1] || BOX[2] > rb[2] || BOX[3] > rb[3]) continue;
        }
        if (r.when && !r.when(uiCtx)) continue;
        drawHidden = true;
        return;
      }
    }
  }
  return drawArraysFn.call(this, mode, first, count);
};
// Native touch rows (ui_overrides.js touchAreas): 24 bytes each, top, bottom, left, right, flag, id.
// A disabled row is moved off screen after every frame, so C2S_Touch_GetAreaID never returns its id.
// The game rebuilds its rows when it opens the screen, so nothing is lost for good.
const OFF = -30000;
const touchRows = table => {
  const t = game && game.rt.syms.get(table);
  if (!t) return [];
  const cpu = game.rt.cpu, count = cpu.ru32(t + 4), rows = cpu.ru32(t + 8);
  if (!rows || count > 256) return [];
  const out = [];
  for (let i = 0; i < count; i++) {
    const row = rows + i * 24;
    out.push({ row, id: cpu.ru32(row + 20), rect: [0, 4, 8, 12].map(o => cpu.ru32(row + o) | 0) });
  }
  return out;
};
const disableTouchAreas = () => {
  if (!UI.touchAreas.length) return;
  const scene = sceneNow(), cpu = game.rt.cpu;
  for (let k = 0; k < UI.touchAreas.length; k++) {
    const a = UI.touchAreas[k];
    if (a.scene !== undefined && a.scene !== scene) continue;
    const t = game.rt.syms.get(a.table);
    if (!t) continue;
    const count = cpu.ru32(t + 4), rows = cpu.ru32(t + 8);
    if (!rows || count > 256) continue;
    for (let i = 0; i < count; i++) {
      const row = rows + i * 24;
      if (!a.ids.includes(cpu.ru32(row + 20)) || (cpu.ru32(row) | 0) === OFF) continue;
      cpu.wu32(row, OFF); cpu.wu32(row + 4, OFF); cpu.wu32(row + 8, OFF); cpu.wu32(row + 12, OFF);
    }
  }
};
// Console helpers for ui_overrides.js. knsUi.probe() lists the textured draws of the next frames.
// knsUi.pick(): the next click on the game is not sent to it; the topmost sprite under it is printed as a
// ready-to-paste ui_overrides.js entry and hidden at once as a preview (knsUi.undo() shows it again).
// Textures bigger than 256x256 are only hashed with #uiprobe in the URL (reload with it to pick those).
window.knsUi = {
  probing: location.hash === '#uiprobe',
  rules: HIDDEN_SPRITES,
  async probe(ms = 300) {
    const seen = new Map(), fn = gl.glDrawArrays;
    gl.glDrawArrays = function (mode, first, count) {
      const e = this.tex2d && this.arrays.t.on ? this.textures.get(this.bound) : null;
      if (e && count > 0) {
        const box = drawBox(this, first, count), key = (e.sig || this.bound + ':' + e.w + 'x' + e.h) + ' ' + box.join(',');
        seen.delete(key); // keep draw order: the last entry is the topmost
        seen.set(key, { tex: e.sig || '(no hash, gl ' + this.bound + ' ' + e.w + 'x' + e.h + ')', box });
      }
      return fn.call(this, mode, first, count);
    };
    await new Promise(r => setTimeout(r, ms));
    gl.glDrawArrays = fn;
    return { scene: sceneNow(), draws: [...seen.values()] };
  },
  pick() {
    pickNext = true;
    console.log('knsUi.pick: click the sprite to hide');
  },
  async pickAt(x, y) {
    const { scene, draws } = await this.probe();
    const hit = draws.filter(d => x >= d.box[0] && x <= d.box[2] && y >= d.box[1] && y <= d.box[3] && d.box[2] - d.box[0] < SCREEN.w * 0.9).pop();
    if (!hit) { console.log('knsUi.pick: no sprite at', x, y); return null; }
    if (!/^\d+x\d+\/[0-9a-f]+$/.test(hit.tex)) { console.log('knsUi.pick: texture not hashed; reload with #uiprobe and pick again', hit); return null; }
    const pad = 10, box = [hit.box[0] - pad, hit.box[1] - pad, hit.box[2] + pad, hit.box[3] + pad];
    const rule = { name: 'picked', tex: hit.tex, scene, box };
    const line = `    { name: 'NAME', tex: '${rule.tex}', scene: ${scene}, box: [${box.join(', ')}] },`;
    HIDDEN_SPRITES.push(rule);
    for (const e of gl.textures.values()) if (e.sig === rule.tex) (e.hide ||= []).push(rule);
    console.log('Add to browser/port/ui_overrides.js sprites:\n' + line);
    return line;
  },
  // Native touch rows of every table with rows, for ui_overrides.js touchAreas: { table: [{ id, rect: [top, bottom, left, right] }] }.
  touchRows() {
    const out = {};
    for (const n of game.rt.syms.keys()) if (/^touch[A-Z]/.test(n)) { const rows = touchRows(n).filter(r => r.id); if (rows.length) out[n] = rows.map(({ id, rect }) => ({ id, rect })); }
    return out;
  },
  undo() {
    const rule = HIDDEN_SPRITES.at(-1);
    if (!rule || rule.name !== 'picked') return;
    HIDDEN_SPRITES.pop();
    for (const e of gl.textures.values()) if (e.hide) e.hide = e.hide.filter(r => r !== rule);
  },
};

// ---------- ?perf=1: per-second cost overlay; ?perf=2 also counts instructions per guest function ----------
// Tick cost, always counted (a few additions per tick); totals since the page opened.
const stat = { ticks: 0, tickMs: 0, tickMax: 0, tickSteps: 0, cur: 0, curSteps: 0 };
// Settings -> Show FPS: game ticks per second and the main-thread time of one tick.
let fpsEl = null, fpsAt = 0, fpsTicks = 0, fpsMs = 0;
const renderFps = now => {
  if (!settings.showFps) { if (fpsEl) fpsEl.hidden = true; return; }
  if (now - fpsAt < 1000) return;
  if (!fpsEl) { fpsEl = document.createElement('div'); fpsEl.id = 'fps'; document.body.appendChild(fpsEl); }
  const n = stat.ticks - fpsTicks;
  if (fpsAt) fpsEl.textContent = `${Math.round(n * 1000 / (now - fpsAt))} fps  ${(n ? (stat.tickMs - fpsMs) / n : 0).toFixed(1)} ms`;
  fpsEl.hidden = false;
  fpsAt = now; fpsTicks = stat.ticks; fpsMs = stat.tickMs;
};
const PERF_MODE = +(new URLSearchParams(location.search).get('perf') || 0);
const perf = PERF_MODE ? (() => {
  const p = { frameMs: 0, glMs: 0, glCalls: 0, presentMs: 0, steps: 0, ticks: 0, idleSteps: 0, last: performance.now(), instr: 0, el: null };
  const proto = Object.getPrototypeOf(gl);
  // ?perf=3: a second, unbatched GL gets every call; each flip compares the two frames pixel by pixel.
  if (PERF_MODE === 3 || PERF_MODE === 4) {
    const c2 = document.createElement('canvas');
    c2.width = canvas.width; c2.height = canvas.height;
    const ref = new window.ApkGLES1(c2, () => {});
    ref.batching = PERF_MODE === 4; // 4: same batching, compares render size only
    p.cmp = { frames: 0, diffFrames: 0, diffPx: 0, maxDiff: 0, worst: null };
    const read = g => {
      const w = g.canvas.width, h = g.canvas.height, px = new Uint8Array(w * h * 4);
      g.gl.bindFramebuffer(g.gl.FRAMEBUFFER, g.fbo);
      g.gl.readPixels(0, 0, w, h, g.gl.RGBA, g.gl.UNSIGNED_BYTE, px);
      return px;
    };
    // ?perf=4: the full-size frame, downscaled the way the browser shows a large canvas, against the small render.
    const c2d = document.createElement('canvas'), small = document.createElement('canvas');
    const downscale = (px, w, h, W, H) => {
      c2d.width = w; c2d.height = h;
      c2d.getContext('2d').putImageData(new ImageData(new Uint8ClampedArray(px.buffer), w, h), 0, 0);
      small.width = W; small.height = H;
      const x = small.getContext('2d', { willReadFrequently: true });
      x.imageSmoothingEnabled = true; x.imageSmoothingQuality = 'low';
      x.drawImage(c2d, 0, 0, W, H);
      return x.getImageData(0, 0, W, H).data;
    };
    for (const name of Object.getOwnPropertyNames(proto)) {
      if (!/^gl[A-Z]|^present$|^attach$/.test(name) || typeof gl[name] !== 'function') continue;
      const fn = gl[name].bind(gl), rf = ref[name].bind(ref);
      gl[name] = name === 'present' ? function () {
        gl.flush();
        const a = read(gl);
        let b = read(ref);
        if (PERF_MODE === 4 && (gl.canvas.width !== ref.canvas.width)) {
          b = downscale(b, ref.canvas.width, ref.canvas.height, gl.canvas.width, gl.canvas.height);
        }
        let n = 0, mx = 0, sum = 0;
        for (let i = 0; i < a.length; i++) { const d = Math.abs(a[i] - b[i]); sum += d; if (d > 24) n++; else if (PERF_MODE === 3 && d) n++; if (d > mx) mx = d; }
        const cm = p.cmp;
        cm.frames++;
        cm.mean = sum / a.length;
        cm.share = n / a.length;
        if (n) { cm.diffFrames++; cm.diffPx += n; if (mx > cm.maxDiff) cm.maxDiff = mx; }
        rf(); return fn();
      } : name === 'glDrawArrays' ? function () {
        // The hidden-sprite rules (ui_overrides.js) decide on the real copy; the reference skips the same draws.
        const out = fn.apply(null, arguments);
        if (!drawHidden) rf.apply(null, arguments);
        return out;
      } : function () {
        // GL calls that write guest memory (glGen*, glGet*) run on the reference first; the real one writes last.
        rf.apply(null, arguments);
        return fn.apply(null, arguments);
      };
    }
    window.knsRefGL = ref;
  }
  for (const name of Object.getOwnPropertyNames(proto)) {
    if (!/^gl[A-Z]|^present$/.test(name) || typeof gl[name] !== 'function') continue;
    const fn = gl[name].bind(gl), isPresent = name === 'present';
    gl[name] = function () {
      const t0 = performance.now();
      try { return fn.apply(null, arguments); } finally {
        const d = performance.now() - t0;
        if (isPresent) p.presentMs += d; else { p.glMs += d; p.glCalls++; }
      }
    };
  }
  p.el = document.createElement('pre');
  p.el.style.cssText = 'position:fixed;right:4px;top:4px;margin:0;padding:4px 6px;background:rgba(0,0,0,.7);color:#0f0;font:11px/1.3 monospace;z-index:99;pointer-events:none';
  document.body.appendChild(p.el);
  window.knsPerf = p;
  p.report = now => {
    const s = (now - p.last) / 1000;
    if (s < 1) return;
    const cpu = game && game.rt.cpu;
    const instr = cpu ? cpu.count - p.instr : 0;
    p.instr = cpu ? cpu.count : 0;
    const per = v => (v / s).toFixed(1);
    const jit = p.frameMs - p.glMs - p.presentMs;
    p.el.textContent = `steps/s ${per(p.steps)} (idle ${per(p.idleSteps)})  ticks/s ${per(p.ticks)}  flips/s ${per(gl.presents - (p.flips || 0))}\n` +
      `ms/s  run ${per(p.frameMs)}  jit+js ${per(jit)}  gl ${per(p.glMs)}  present ${per(p.presentMs)}\n` +
      `Minstr/s ${(instr / s / 1e6).toFixed(2)}  gl calls/s ${per(p.glCalls)}  draws/s ${per(gl.drawCalls - (p.draws || 0))}  canvas ${canvas.width}x${canvas.height}\n` +
      (() => {
        const n = stat.ticks - (p.sTicks || 0), ms = stat.tickMs - (p.sMs || 0), st = stat.tickSteps - (p.sSteps || 0);
        const out = `tick ms avg ${(n ? ms / n : 0).toFixed(1)} max ${stat.tickMax.toFixed(1)}  steps/tick ${(n ? st / n : 0).toFixed(2)}\n` +
          `heap ${performance.memory ? (performance.memory.usedJSHeapSize / 1048576).toFixed(0) : '?'} MB  assets ${(assetBytes / 1048576).toFixed(0)} MB (${assets.size})  ` +
          `tex ${gl.textures.size}  buf ${gl.buffers.size}  snd ${handles.size}/${soundUrls.size}  fds ${game ? game.rt.fds.size : 0}  text ${(memoBytes / 1048576).toFixed(1)} MB`;
        p.sTicks = stat.ticks; p.sMs = stat.tickMs; p.sSteps = stat.tickSteps; stat.tickMax = 0;
        return out;
      })() +
      (p.cmp ? `
compare frames ${p.cmp.frames} differing ${p.cmp.diffFrames} px ${p.cmp.diffPx} max ${p.cmp.maxDiff}` +
        (PERF_MODE === 4 ? `  last: mean |diff| ${(p.cmp.mean || 0).toFixed(2)}/255, channels off by >24: ${((p.cmp.share || 0) * 100).toFixed(2)}%` : '') : '');
    p.draws = gl.drawCalls;
    p.flips = gl.presents;
    p.last = now;
    p.frameMs = p.glMs = p.presentMs = p.steps = p.ticks = p.idleSteps = p.glCalls = 0;
  };
  // Top guest functions by instructions since the last call: knsPerf.hot(20)
  p.hot = (top = 20) => {
    const cpu = game.rt.cpu, syms = [...game.rt.syms].map(([n, a]) => [n, (a & ~1) >>> 0]).sort((x, y) => x[1] - y[1]);
    const fnOf = pc => {
      let lo = 0, hi = syms.length - 1, best = null;
      while (lo <= hi) { const mid = (lo + hi) >> 1; if (syms[mid][1] <= pc) { best = syms[mid]; lo = mid + 1; } else hi = mid - 1; }
      return best ? best[0] : '?';
    };
    const by = new Map();
    let total = 0;
    for (const [pc, c] of cpu.prof) { const f = fnOf((pc & ~1) >>> 0); by.set(f, (by.get(f) || 0) + c); total += c; }
    cpu.prof.clear();
    return [...by].sort((x, y) => y[1] - x[1]).slice(0, top).map(([f, c]) => `${(100 * c / total).toFixed(1).padStart(5)}% ${f}`).join('\n');
  };
  return p;
})() : null;
const assetData = name => assets.get(name);
window.KnsWeb = { assets, loadAsset, assetName, canvas, SCREEN };

// ---------- render size: the device pixels the canvas covers, at most the game's 2048 x 1184 ----------
// Drawing more pixels than the screen shows only costs GPU time (or CPU time with software WebGL).
// Settings -> Resolution (opt-in, saved per browser) draws at a share of that, at least 640 px wide.
const fitRender = () => {
  if (PERF_MODE === 3) return; // the exact frame comparison needs both copies at full size
  const r = canvas.getBoundingClientRect();
  if (!(r.width > 0)) return;
  const w = Math.ceil(r.width * (window.devicePixelRatio || 1));
  const k = RES_SCALE[settings.resolution];
  gl.setRenderWidth(k < 1 ? Math.min(w, Math.max(640, Math.round(w * k))) : w);
};
addEventListener('resize', fitRender);
if (typeof ResizeObserver !== 'undefined') new ResizeObserver(fitRender).observe(canvas);
fitRender();

// ---------- touch ----------
const EV = { DOWN: 1, MOVE: 2, UP: 3 };
canvas.tabIndex = 0;
const touches = new Map();
let controlsPanelOpen = false;
const controlsStorageKey = 'kns-controls-v1';
let savedBindings;
let sendKeyboardTouch;
try { savedBindings = JSON.parse(localStorage.getItem(controlsStorageKey)); } catch (e) { /* use defaults */ }
const controls = window.KnsControls.createControls({
  bindings: savedBindings,
  state() {
    if (!game || !running || document.hidden || controlsPanelOpen) return null;
    const scene = game.rt.syms.get('g_ui16CurrentScene');
    const popup = game.rt.syms.get('g_i32CurrentPopupScene');
    if (!scene || !popup) return null;
    const currentScene = game.rt.cpu.ru16(scene);
    return { scene: currentScene, popup: game.rt.cpu.ru32(popup), targets: window.KnsControls.readNativeTargets(game.rt, currentScene) };
  },
  send: (...args) => {
    if (!game) return;
    sendKeyboardTouch ||= window.KnsControls.createNativeSender(game.rt);
    sendKeyboardTouch(...args);
  },
  physicalCount: () => touches.size,
});
// The Settings panel: keyboard controls (physical key codes, a complete, conflict-free mapping), graphics, performance.
const controlsOpen = document.createElement('button');
controlsOpen.id = 'settings-open';
controlsOpen.textContent = 'Settings';
document.body.appendChild(controlsOpen);
// Mobile browser URL bars cover the game, so offer fullscreen. Settings hide while fullscreen.
const fullscreenOpen = document.createElement('button');
fullscreenOpen.id = 'fullscreen-open';
fullscreenOpen.type = 'button';
document.body.appendChild(fullscreenOpen);
const fullscreenRoot = document.documentElement;
const fullscreenSupported = !!(fullscreenRoot.requestFullscreen || fullscreenRoot.webkitRequestFullscreen);
const isFullscreen = () => !!(document.fullscreenElement || document.webkitFullscreenElement);
const renderFullscreen = () => {
  const on = isFullscreen();
  fullscreenOpen.textContent = on ? 'Exit fullscreen' : 'Fullscreen';
  controlsOpen.hidden = on;
  if (on && controlsPanelOpen) controlsPanel.hidden = true;
  if (on) controlsPanelOpen = false;
  fullscreenOpen.hidden = on || !fullscreenSupported;
};
fullscreenOpen.addEventListener('click', () => {
  try {
    if (isFullscreen()) (document.exitFullscreen || document.webkitExitFullscreen).call(document);
    else (fullscreenRoot.requestFullscreen || fullscreenRoot.webkitRequestFullscreen).call(fullscreenRoot);
  } catch (e) { /* browser refused fullscreen; button stays for retry */ }
});
document.addEventListener('fullscreenchange', renderFullscreen);
document.addEventListener('webkitfullscreenchange', renderFullscreen);
renderFullscreen();
// Full download: fetches every game file now, so the game does not stop to download during play. Files already in the cache are read from it.
const fullOpen = document.createElement('button');
fullOpen.id = 'full-download-open';
fullOpen.type = 'button';
document.body.appendChild(fullOpen);
const allNames = Object.keys(INDEX);
const renderFullButton = () => {
  const busy = dl.full && (dl.active > 0 || dl.queue.length > 0);
  const done = dl.fullDone || allNames.every(n => cachedNames.has(n) || assets.has(n));
  fullOpen.textContent = !CAN_FETCH ? 'Unavailable' : busy ? 'Downloading...' : done ? 'Downloaded' : 'Full download';
  fullOpen.disabled = !CAN_FETCH || busy || done;
  // Gone once everything is downloaded, and never drawn over the game in fullscreen.
  fullOpen.hidden = done || isFullscreen();
};
// Event dungeon calendar from the server (/api/events, edited in Studio), written over the client's bundled event
// dates by native_hooks.js. Polled each minute; an unreachable server keeps the last calendar.
const pollEvents = async () => {
  try {
    const server = (window.KNS_SERVER || 'http://127.0.0.1:8081').replace(/\/$/, '');
    const r = await fetch(server + '/api/events', { method: 'POST', headers: { 'Content-Type': 'text/plain' }, body: '{}',
      signal: typeof AbortSignal !== 'undefined' && AbortSignal.timeout ? AbortSignal.timeout(8000) : undefined });
    const events = (await r.json()).events;
    if (!Array.isArray(events)) return;
    const rows = new Map();
    for (const e of events) if (Array.isArray(e) && e.length === 5 && e.every(Number.isFinite)) rows.set(e[0], e.slice(1));
    window.KnsEventSchedule = { rows };
  } catch (e) { /* keep the previous calendar */ }
};
pollEvents();
setInterval(pollEvents, 60000);
// Pinned chat notice from the server (/api/pins, edited in Studio), written into the client's pinned slot by
// native_hooks.js (KnsChatPin). Applied only when the newest pin changes, so a pin that just arrived over chat is not
// undone by a poll answered a moment before it; a removal or edit lands within one poll, with no new chat line.
let appliedPin = '';
const pollPins = async () => {
  try {
    const server = (window.KNS_SERVER || 'http://127.0.0.1:8081').replace(/\/$/, '');
    const r = await fetch(server + '/api/pins', { method: 'POST', headers: { 'Content-Type': 'text/plain' }, body: '{}',
      signal: typeof AbortSignal !== 'undefined' && AbortSignal.timeout ? AbortSignal.timeout(8000) : undefined });
    const pins = (await r.json()).pins;
    if (!Array.isArray(pins) || !window.KnsChatPin) return;
    const p = pins.find(e => Array.isArray(e) && e.length === 4 && typeof e[3] === 'string');
    const pin = p ? { id: p[0], start: p[1], end: p[2], text: p[3] } : null;
    const key = JSON.stringify(pin);
    if (key !== appliedPin && window.KnsChatPin.apply(pin)) appliedPin = key;
  } catch (e) { /* keep the current pin */ }
};
pollPins();
setInterval(pollPins, 30000);
document.addEventListener('fullscreenchange', renderFullButton);
document.addEventListener('webkitfullscreenchange', renderFullButton);
fullOpen.addEventListener('click', () => {
  if (!CAN_FETCH) return;
  dl.full = true;
  preload(allNames, false);
  renderFullButton();
  renderDownloads();
});
renderFullButton();
const controlsPanel = document.createElement('div');
controlsPanel.className = 'settings-back';
controlsPanel.hidden = true;
const controlsBox = document.createElement('div');
controlsBox.className = 'settings-panel';
controlsBox.setAttribute('role', 'dialog');
controlsBox.setAttribute('aria-label', 'Settings');
controlsPanel.appendChild(controlsBox);
document.body.appendChild(controlsPanel);
const labels = { attack: 'Attack', throw: 'Shield throw', block: 'Block', ally1: 'Ally skill 1', ally2: 'Ally skill 2', ally3: 'Ally skill 3' };
const heading = document.createElement('h2'); heading.textContent = 'Keyboard controls'; controlsBox.appendChild(heading);
const help = document.createElement('p'); help.textContent = 'Choose an action, then press a key. Keys must be different. Close this panel to use your keys in combat.'; controlsBox.appendChild(help);
const keyButtons = {};
const notice = document.createElement('p'); notice.setAttribute('role', 'status');
let waitingFor = null;
const renderBindings = () => {
  const bindings = controls.bindings;
  for (const action of Object.keys(labels)) keyButtons[action].textContent = waitingFor === action ? 'Press a key…' : bindings[action];
};
for (const [action, label] of Object.entries(labels)) {
  const row = document.createElement('div'); row.className = 'settings-row';
  const name = document.createElement('span'); name.textContent = label;
  const button = document.createElement('button'); button.type = 'button';
  button.addEventListener('click', () => { waitingFor = action; notice.textContent = ''; renderBindings(); });
  keyButtons[action] = button; row.append(name, button); controlsBox.appendChild(row);
}
controlsBox.appendChild(notice);
// Graphics and performance rows. Each option is saved at once and applied without a reload.
const settingRows = [];
const sectionHeading = text => { const h = document.createElement('h2'); h.textContent = text; settingRows.push(h); };
const settingRow = (label, hint, control) => {
  const row = document.createElement('label'); row.className = 'settings-row';
  const words = document.createElement('span'); words.textContent = label;
  if (hint) { const small = document.createElement('small'); small.textContent = hint; words.appendChild(small); }
  row.append(words, control); settingRows.push(row);
};
const checkSetting = (key, label, hint, apply) => {
  const box = document.createElement('input'); box.type = 'checkbox'; box.checked = settings[key];
  box.addEventListener('change', () => { settings[key] = box.checked; saveSettings(); if (apply) apply(); });
  settingRow(label, hint, box);
};
sectionHeading('Graphics');
const resolutionSelect = document.createElement('select');
for (const [value, label] of [['high', 'High (100%)'], ['medium', 'Medium (75%)'], ['low', 'Low (60%)'], ['verylow', 'Very low (45%)']]) {
  const option = document.createElement('option'); option.value = value; option.textContent = label;
  option.selected = settings.resolution === value; resolutionSelect.appendChild(option);
}
resolutionSelect.value = settings.resolution;
resolutionSelect.addEventListener('change', () => {
  if (!Object.hasOwn(RES_SCALE, resolutionSelect.value)) return;
  settings.resolution = resolutionSelect.value; saveSettings(); fitRender();
});
settingRow('Resolution', 'Lower is faster on a weak graphics chip.', resolutionSelect);
sectionHeading('Performance');
checkSetting('memorySaver', 'Memory saver', 'Keeps fewer game files in memory. For phones and PCs with 4 GB or less.', () => trimAssets());
checkSetting('showFps', 'Show FPS', '', () => { fpsAt = 0; });
const saveBindings = value => {
  controls.setBindings(value);
  try { localStorage.setItem(controlsStorageKey, JSON.stringify(value)); }
  catch (e) { notice.textContent = 'Browser storage is unavailable; bindings last for this session.'; }
  renderBindings();
};
const resetControls = document.createElement('button'); resetControls.type = 'button'; resetControls.textContent = 'Reset defaults';
resetControls.addEventListener('click', () => { waitingFor = null; notice.textContent = ''; saveBindings(window.KnsControls.DEFAULT_BINDINGS); });
const closeControls = document.createElement('button'); closeControls.type = 'button'; closeControls.textContent = 'Close';
closeControls.addEventListener('click', () => { waitingFor = null; controlsPanel.hidden = true; controlsPanelOpen = false; canvas.focus({ preventScroll: true }); });
controlsBox.append(resetControls, ...settingRows, closeControls);
controlsOpen.addEventListener('click', () => { controls.releaseAll(); controlsPanelOpen = true; controlsPanel.hidden = false; notice.textContent = ''; renderBindings(); closeControls.focus(); });
controlsPanel.addEventListener('keydown', event => {
  if (!waitingFor) return;
  event.preventDefault(); event.stopPropagation();
  if (event.key === 'Escape') { waitingFor = null; renderBindings(); return; }
  const candidate = { ...controls.bindings, [waitingFor]: event.code };
  if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || !window.KnsControls.validBindings(candidate)) {
    notice.textContent = 'Choose an unused letter, number, arrow, function, space, or numpad key.'; return;
  }
  waitingFor = null; notice.textContent = ''; saveBindings(candidate);
});
renderBindings();
addEventListener('keydown', event => controls.down(event));
addEventListener('keyup', event => controls.up(event));
// Leaving the window or tab can swallow a pointerup: release held touches as well as keys.
addEventListener('blur', () => { controls.releaseAll(); releaseTouches(); });
document.addEventListener('visibilitychange', () => { if (document.hidden) { controls.releaseAll(); releaseTouches(); } });
let canvasRect = null, pickNext = false;
const getCanvasRect = () => canvasRect || (canvasRect = canvas.getBoundingClientRect());
addEventListener('resize', () => { canvasRect = null; });
addEventListener('scroll', () => { canvasRect = null; }, true);
if (typeof ResizeObserver !== 'undefined') new ResizeObserver(() => { canvasRect = null; }).observe(canvas);
// A captured pointer keeps reporting outside the canvas; a phone screen never reports outside itself.
const clampTo = (v, max) => Math.min(max - 1, Math.max(0, v));
const toGame = e => {
  const r = getCanvasRect();
  return [clampTo(Math.round((e.clientX - r.left) / r.width * SCREEN.w), SCREEN.w),
    clampTo(Math.round((e.clientY - r.top) / r.height * SCREEN.h), SCREEN.h)];
};
const touchAt = new Map();   // pointerId -> last game position, for a release the browser never delivered
const sendTouch = (kind, e) => {
  if (!game) return;
  const [x, y] = toGame(e);
  let id = touches.get(e.pointerId);
  if (id === undefined) {
    id = 1;
    while ([...touches.values()].includes(id)) id++;
    touches.set(e.pointerId, id);
  }
  touchAt.set(e.pointerId, [x, y]);
  game.java.addEvent(kind, x, y, id, touches.size + controls.count);
  if (kind === EV.UP) { touches.delete(e.pointerId); touchAt.delete(e.pointerId); }
};
const releaseTouch = pointerId => {
  const id = touches.get(pointerId);
  if (id === undefined) return;
  const [x, y] = touchAt.get(pointerId) || [0, 0];
  if (game) game.java.addEvent(EV.UP, x, y, id, touches.size + controls.count);
  touches.delete(pointerId); touchAt.delete(pointerId);
};
const releaseTouches = () => { for (const pointerId of [...touches.keys()]) releaseTouch(pointerId); };
canvas.addEventListener('pointerdown', e => {
  // A mouse plays with its main button only; right and middle clicks are not taps.
  if (e.pointerType === 'mouse' && e.button !== 0) return;
  if (pickNext) { pickNext = false; e.preventDefault(); window.knsUi.pickAt(...toGame(e)); return; }
  // A second down from a pointer the game still holds: close the old press first.
  if (touches.has(e.pointerId)) releaseTouch(e.pointerId);
  canvas.focus({ preventScroll: true }); canvas.setPointerCapture(e.pointerId); sendTouch(EV.DOWN, e); e.preventDefault(); });
canvas.addEventListener('pointermove', e => { if (touches.has(e.pointerId)) sendTouch(EV.MOVE, e); });
canvas.addEventListener('pointerup', e => { if (touches.has(e.pointerId)) sendTouch(EV.UP, e); });
canvas.addEventListener('pointercancel', e => { if (touches.has(e.pointerId)) sendTouch(EV.UP, e); });
// Normally after pointerup (already released). Without one, the press would stay held.
canvas.addEventListener('lostpointercapture', e => releaseTouch(e.pointerId));
canvas.addEventListener('contextmenu', e => e.preventDefault());

function start() {
  const elf = b64bytes(window.KNS_ELF);
  game = window.KnsGame.boot({
    elf, vfs, gl, log, screen: SCREEN,
    text,
    audio,
    ui,
    installJava: (rt, java, screen, ctx) => {
      ctx.text = text;
      ctx.web = window.KnsWeb;
      window.KnsJavaShim.installJava(rt, java, screen, ctx);
    },
    startup: window.KnsJavaShim.startup,
    // Startup needs asset scripts, which only load when the page yields, so it runs in the frame loop.
    startMs: 0,
    pacedByPage: true,
  });
  window.knsGame = game;
  window.KnsAchievementSync.control = window.KnsAchievementSync.install(game.rt);
  if (PERF_MODE === 2) game.rt.cpu.prof = new Map();
  game.start();
  requestAnimationFrame(onRefresh);
  setInterval(watchdog, 16);
}

// The game ticks on whole display refreshes, with its clock frozen at that refresh, so frames
// reach the screen evenly. Refreshes between ticks only finish work still in progress.
let lastStep = 0, lastClock = 0, lastPresents = 0, lastFlip = 0, lastT = 0, tickDue = TICK_MS;
function step(t) {
  if (!running) return;
  lastStep = performance.now();
  controls.validate();
  const dt = lastT ? t - lastT : 0;
  lastT = t;
  // Hidden: the game clock follows real time, so late frames must be made up with extra ticks
  // (below), not dropped; otherwise the stage timer runs on while the game stalls.
  tickDue = Math.min(tickDue + Math.max(0, dt), document.hidden ? TICK_MS * 600 : TICK_MS * 2);
  lastClock = Math.max(lastClock, performance.timeOrigin + t);
  game.rt.clock = lastClock;
  if (gl.presents !== lastPresents) {
    lastPresents = gl.presents;
    lastFlip = lastStep;
    statusEl.hidden = true;
  }
  renderDownloads();
  renderFps(lastStep);
  const tick = tickDue >= TICK_MS - 2 && !game.pending;
  if (perf) { perf.steps++; perf.report(lastStep); }
  if (tick) tickDue -= TICK_MS;
  else if (!game.busy()) { if (perf) perf.idleSteps++; return; }
  if (perf && tick) perf.ticks++;
  // A tick in progress runs to its end in this step when it can: nothing new reaches the screen before its
  // flip, and handing the rest to the next refresh made a 25 ms tick take three refreshes (20 fps, slow game).
  // Other work (queued input, a load between ticks) keeps the short slice.
  const inTick = tick || game.pending;
  try {
    game.frame(inTick ? 28 : lastStep - lastFlip > 100 ? 24 : 11, tick);
    // Catch-up after a throttled or late wake-up, bounded so the page stays responsive.
    if (document.hidden) {
      while (!game.pending && tickDue >= TICK_MS - 2 && performance.now() - lastStep < 150) {
        tickDue -= TICK_MS;
        game.frame(28, true);
      }
    }
    disableTouchAreas();
  } catch (e) { fatal(e); }
  const spent = performance.now() - lastStep;
  if (inTick) {
    stat.cur += spent; stat.curSteps++;
    if (!game.pending) {
      stat.ticks++; stat.tickMs += stat.cur; stat.tickSteps += stat.curSteps;
      if (stat.cur > stat.tickMax) stat.tickMax = stat.cur;
      stat.cur = stat.curSteps = 0;
    }
  }
  if (perf) perf.frameMs += spent;
}
function onRefresh(t) {
  if (!running) return;
  requestAnimationFrame(onRefresh);
  if (!document.hidden) step(t);
}
// requestAnimationFrame stops in some visible but occluded panes.
function watchdog() {
  if (!document.hidden && performance.now() - lastStep > 60) step(performance.now());
}
// A hidden tab keeps playing (stage timer, monsters, idle play), unlike Android, which pauses:
// requestAnimationFrame stops and page timers drop to once a second (or a minute) while hidden,
// so the game clock ran on while no frame did and the stage was "over" on return. A worker's
// timer is not throttled, and its messages drive the frames. Sound stays off while hidden.
let hiddenDriver = null;
function driveHidden() {
  if (!document.hidden || !running) {
    if (hiddenDriver) { hiddenDriver.terminate(); hiddenDriver = null; }
    return;
  }
  if (hiddenDriver) return;
  try {
    const url = URL.createObjectURL(new Blob(['setInterval(() => postMessage(0), ' + Math.floor(TICK_MS / 2) + ');'], { type: 'text/javascript' }));
    hiddenDriver = new Worker(url);
    URL.revokeObjectURL(url);
    hiddenDriver.onmessage = () => {
      if (!running || !document.hidden) { driveHidden(); return; }
      step(performance.now());
    };
  } catch (e) {
    // No worker: a throttled page timer still beats a frozen stage.
    const id = setInterval(() => { if (document.hidden && running) step(performance.now()); else driveHidden(); }, TICK_MS);
    hiddenDriver = { terminate: () => clearInterval(id) };
  }
}
document.addEventListener('visibilitychange', driveHidden);

// ---------- only one live copy: a hidden or older tab stays silent and stops running ----------
const silenceHidden = () => { if (document.hidden) audio.pauseAll(); else audio.resumeAll(); };
document.addEventListener('visibilitychange', silenceHidden);
addEventListener('pagehide', () => audio.pauseAll());
const INSTANCE = String(Date.now()) + Math.random();
const standDown = () => {
  controls.releaseAll();
  running = false;
  audio.pauseAll();
  for (const h of handles) h.wantPlay = false;
  statusEl.textContent = 'The game was opened in another tab. Reload to play here.';
  statusEl.hidden = false;
};
try {
  const bc = new BroadcastChannel('kns-instance');
  bc.onmessage = e => { if (e.data !== INSTANCE) standDown(); };
  bc.postMessage(INSTANCE);
} catch (e) { /* the storage event below still covers it */ }
// KNS Studio (another tab) broadcasts after saving profile/resources. When this tab is that
// player, a reload re-runs the login and picks up the new values without the user pressing anything.
// Only the live copy reloads; a stood-down tab must stay down.
try {
  const sync = new BroadcastChannel('kns-studio-sync');
  sync.onmessage = e => {
    const m = e.data || {};
    const mine = window.KnsSession && window.KnsSession.userNo;
    // user_no -1 is a raid-wide change: every live game tab refreshes.
    if (running && mine > 0 && (m.user_no === mine || m.user_no === -1)) location.reload();
  };
} catch (e) { /* cross-tab sync is best effort */ }
addEventListener('storage', e => { if (e.key === 'kns:instance' && e.newValue && e.newValue !== INSTANCE) standDown(); });
safeSet('kns:instance', INSTANCE);

preload(PRELOAD.startup);
const shown = () => { try { start(); } catch (e) { fatal(e); } };
// Text must not be rasterised with a fallback font, but a missing font must not stop the game.
const fontReady = document.fonts && document.fonts.load
  ? Promise.race([Promise.all([document.fonts.load('500 40px Montserrat'), document.fonts.load('700 40px Montserrat')]), new Promise(r => setTimeout(r, 2500))])
  : Promise.resolve();
// The account (a stored KnS sign-in) is settled first, so the title logs in as the right player.
const accountReady = window.KnsAccount && window.KnsAccount.ready ? window.KnsAccount.ready.catch(() => {}) : Promise.resolve();
Promise.all([fontReady.catch(() => {}), accountReady]).then(shown);
})();
