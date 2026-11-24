// The Java half of the Android app, reimplemented: the classes libgame.so calls through JNI.
(function (root) {
'use strict';
const Java = root.ApkJava || require('../../apk-cpu-engine/jni.js');

const PKG = 'com/com2us/imhero/normal/freefull/google/global/android/common';
const K = 'Java_com_com2us_wrapper_kernel_';
const PKG_NAME = 'com.com2us.imhero.normal.freefull.google.global.android.common';
const FONT_SIZE = 2048;
const FILES = '/data/data/' + PKG_NAME + '/files/';
const utf8d = new TextDecoder('utf-8');
const utf8e = new TextEncoder();

function installJava(rt, java, screen, ctx) {
  const C = java.classes;
  const obj = cls => java.ref(new Java.JavaObject(cls));
  ctx.obj = obj;
  const state = java.state = { props: new Map(), data: null };

  C['com/com2us/wrapper/kernel/CWrapperData'] = {
    setDataFromNative(...a) { state.data = a; rt.log('setDataFromNative ' + JSON.stringify(a)); },
  };
  C['com/com2us/wrapper/function/CFunction'] = {
    getPackageName: () => PKG_NAME,
    getApkFilePath: () => '/data/app/kns.apk',
    getUserDir: () => '/data/data/' + PKG_NAME + '/files',
    getLanguage: () => 'en',
    getInitialLanguage: () => 'en',
    getCountry: () => 'US',
    getInitialCountry: () => 'US',
    getSystemVersion: () => '4.4.2',
    getSystemVersionCode: () => 19,
    getDeviceModel: () => 'KnS-Web',
    getDeviceId: () => 'kns-web-device',
    getAndroidId: () => 'kns-web-android',
    getCom2usUniqueId: () => 'kns-web-unique',
    getMIN: () => '',
    getPhoneNumber: () => '',
    getRoaming: () => false,
    getActiveAirplaneMode: () => false,
    isActiveUsim: () => true,
    getNativeAvailableMemory: () => 512 * 1024 * 1024,
    startVibrate() {},
    stopVibrate() {},
  };
  C[PKG + '/CUserDefined'] = {
    GetDeviceWidth: () => screen.w,
    GetDeviceHeight: () => screen.h,
    GetWifiConnection: () => true,
    GetCurrentTimeStamp: () => Date.now(),
    CheckInstall() {},
  };
  Object.assign(C[PKG + '/CUserDefined'], {
    GetDeviceVersion: () => '4.4.2',
    GetDeviceModel: () => 'KnS-Web',
    SetDeviceMacAddress: () => '02:00:00:00:00:00',
    GetVersionSDK: () => 19,
    GetPackageName: () => PKG_NAME,
    GetFilePath: () => FILES,
    GetDownPath: () => FILES,
    getAbsolutePath: () => FILES,
    getMediaPath: () => FILES,
    getPatchPath: () => FILES,
    getPatchPath_Completed: () => FILES,
    getBundleVersion: () => '1.4.0',
    GetSHA1: () => '',
    CheckMinLoad: () => true,
    isSign: () => false,
  });
  const decode = bytes => utf8d.decode(new Uint8Array(bytes.buffer, bytes.byteOffset, bytes.length));
  const text = ctx.text || {
    width: (str, size) => Math.ceil(str.length * size * 0.6) + 1,
    width0: (str, size) => str.length * size * 0.6,
    metrics: size => ({ top: -Math.round(size * 1.06), ascent: -Math.round(size * 0.93), descent: Math.round(size * 0.24) }),
    draw() {},
    drawLines() {},
  };
  const FONT_W = 512;
  const isLatin = c => c !== 0x2d && (c <= 0x24f || (c >= 0x1e00 && c <= 0x1eff) || (c >= 0x2c60 && c <= 0x2c7f) || (c >= 0xa720 && c <= 0xa7ff));
  const isSpace = c => c === 0x20 || c === 0x0a || c === 0x09 || c === 0x0d;
  const layoutMemo = new Map();
  if (root.document && root.document.fonts && root.document.fonts.addEventListener) {
    root.document.fonts.addEventListener('loadingdone', () => layoutMemo.clear());
  }
  // Same steps as CSGLFont: fit by binary search, stop at a newline, keep latin words whole.
  const layoutText = (str, size, style, width, wrap) => {
    const key = size + '/' + style + '/' + width + '/' + wrap + '/' + str;
    const hit = layoutMemo.get(key);
    if (hit !== undefined) return hit;
    const m = text.metrics(size, style);
    const topNeg = -m.top;
    let lineH = m.descent + (m.top - m.ascent) + 2;
    if ((topNeg + lineH) % 2) lineH++;
    const measure = t => Math.round(text.width0(t, size, style) + 1);
    const lines = [];
    let pos = 0, y = 4, maxW = 0;
    while (pos < str.length) {
      let lo = 1, hi = str.length - pos, fit = 0;
      while (lo <= hi) {
        const mid = (lo + hi) >> 1;
        if (measure(str.slice(pos, pos + mid)) <= width) { fit = mid; lo = mid + 1; } else hi = mid - 1;
      }
      if (fit === 0) fit = 1;
      if (wrap === 1) {
        let n = fit;
        while (n > 1 && pos + n < str.length && isLatin(str.charCodeAt(pos + n)) && !isSpace(str.charCodeAt(pos + n))) n--;
        if (n <= 1 && fit > 1) n = fit;
        if (pos + n < str.length && isSpace(str.charCodeAt(pos + n))) n++;
        fit = n;
      }
      let take = fit, skip = fit;
      for (let i = 0; i < fit; i++) {
        if (str.charCodeAt(pos + i) === 0x0a) { take = i; skip = i + 1; break; }
      }
      const line = str.slice(pos, pos + take);
      const base = y + topNeg;
      lines.push({ text: line, y: base - m.descent + 1 });
      maxW = Math.max(maxW, measure(line));
      pos += skip;
      y = base + lineH;
    }
    const res = { lines, height: y, maxW: Math.min(maxW, width), key };
    if (layoutMemo.size >= 2048) layoutMemo.clear();
    layoutMemo.set(key, res);
    return res;
  };
  const pixelsOut = (str, size, style) => {
    const rows = Math.floor(size * 1.3);
    text.draw(java.fontPixels, FONT_W, rows, str, size, style);
  };
  C['com/com2us/wrapper/font/CFont'] = {
    getStringWidth: (b, size, style) => text.width(decode(b), size, style),
    'drawText ([BII)I'(b, size, style) { pixelsOut(decode(b), size, style); return 1; },
    // Draws the part of the string that fits one line; out[0] tracks the position.
    'drawText ([BII[I)I'(b, size, style, out) {
      const s = decode(b);
      if (out[0] >= s.length) { out[0] = -1; return 0; }
      let lo = out[0], hi = s.length, end = out[0];
      while (lo <= hi) {
        const mid = (lo + hi) >> 1;
        if (text.width(s.slice(out[0], mid), size, style) < FONT_W) { end = mid; lo = mid + 1; } else hi = mid - 1;
      }
      const part = s.slice(out[0], end);
      pixelsOut(part, size, style);
      out[0] = end === s.length ? -1 : end;
      return text.width(part, size, style);
    },
    // CSGLFont: line breaking and placement into the shared pixel buffer.
    getStringWidthHeightInRect(b, size, style, width, wrap, out) {
      const lay = layoutText(decode(b), size, style, width, wrap);
      out[0] = lay.maxW;
      out[1] = lay.height;
      return lay.maxW;
    },
    drawTextInRect(b, size, style, align, width, wrap) {
      const lay = layoutText(decode(b), size, style, width, wrap);
      text.drawLines(java.fontPixels, width, lay, align, size, style);
      return 0;
    },
  };
  // ---------- saved settings (Android SharedPreferences) ----------
  const mem = new Map();
  const TITLE_LOG_IN = [1357, 840]; // The title's Log In button, in surface pixels.
  const ACCOUNT_AUTH_PRESSES = 120;
  let accountAuthTimer = 0;
  let accountAuthCalled = false;
  let loggedInGame = false; // Past the title: a Hub login from here on is a guest asking to log in, not the start-up auto login.
  // A quota error may reject writes while reads still return an older value. Keep failed writes and
  // deletes authoritative for this session, including a tombstone for a failed delete.
  const storageOverrides = new Map();
  const store = {
    get(k) {
      if (storageOverrides.has(k)) return storageOverrides.get(k);
      try { return localStorage.getItem('kns:prop:' + k); } catch (e) { return mem.has(k) ? mem.get(k) : null; }
    },
    set(k, v) {
      mem.set(k, v);
      try { localStorage.setItem('kns:prop:' + k, v); storageOverrides.delete(k); }
      catch (e) { storageOverrides.set(k, v); }
    },
    del(k) {
      mem.delete(k);
      try { localStorage.removeItem('kns:prop:' + k); storageOverrides.delete(k); }
      catch (e) { storageOverrides.set(k, null); }
    },
  };
  // The game keeps the Hub's friend list for 24 hours (dt_<id>_hubgamefriend.dat), so players who joined since would not show up in
  // Add Friends. Dropping the file at start makes each page load fetch it again.
  try {
    for (let i = localStorage.length - 1; i >= 0; i--) {
      const key = localStorage.key(i);
      if (key && key.startsWith('kns:file:') && key.includes('_hubgamefriend.dat')) localStorage.removeItem(key);
    }
  } catch (e) { /* no storage: nothing is cached either */ }
  const u8s = b => utf8d.decode(new Uint8Array(b.buffer, b.byteOffset, b.length));
  const s8s = str => java.bytes(utf8e.encode(str));
  C['com/com2us/wrapper/utility/CUtility'] = {
    getAppProperty(k) {
      const name = u8s(k);
      // The game has no remembered login yet but this page has an account (just logged in, then reloaded): CheckAutoLogin@0xeb914
      // reads this and then shows the title's buttons. Press Log In for the player, so the game takes its own path. It retries because
      // a fresh profile first shows the download dialog; a press on anything else on the title does nothing.
      if (name === 'hub_guest_uid' && store.get('account_id') && !accountAuthTimer && !accountAuthCalled) {
        let presses = 0;
        accountAuthTimer = setInterval(() => {
          if (accountAuthCalled || ++presses > ACCOUNT_AUTH_PRESSES) { clearInterval(accountAuthTimer); return; }
          java.addEvent(1, TITLE_LOG_IN[0], TITLE_LOG_IN[1], 1, 1);
          setTimeout(() => java.addEvent(3, TITLE_LOG_IN[0], TITLE_LOG_IN[1], 1, 0), 100);
        }, 1000);
      }
      const v = store.get(name);
      return v === null ? null : s8s(v);
    },
    setAppProperty(k, v) { store.set(u8s(k), u8s(v)); },
    removeAppProperty(k) { store.del(u8s(k)); },
    Shortcut: () => 0,
  };

  // ---------- server ----------
  const SERVER = (root.KNS_SERVER || 'http://127.0.0.1:8081').replace(/\/$/, '');
  const toServer = url => url.replace(/^https?:\/\/[^/]+/, SERVER);
  const post = (url, body, headers) => fetch(toServer(url), { method: 'POST', body, headers }).then(async r => {
    if (!r.ok) throw new Error('HTTP ' + r.status + ' ' + url);
    return r.text();
  });
  const UD = 'Java_com_com2us_imhero_normal_freefull_google_global_android_common_CUserDefined_';
  const nativeCall = (name, ...args) => rt.callJava(ctx.sym(name), [java.env, ...args]);
  const cud = () => ctx.cls(PKG + '/CUserDefined');
  const NET = { SENDING: 6, RECEIVING: 7, RECEIVE: 8 };
  // The real gateway replies with base64(AES-CBC(JSON)); local test servers may reply with plain JSON.
  // The page's own hooks read the reply object; the native parser always gets the original text.
  let replyDecodeWarned = false;
  const readReply = async text => {
    try { return JSON.parse(text); } catch { /* encrypted, or not JSON */ }
    const decode = root.KnsGateway && root.KnsGateway.decode;
    if (!decode || !text) return null;
    try { return await decode(text); } catch (error) {
      if (!replyDecodeWarned) { replyDecodeWarned = true; rt.log('gateway reply decode failed: ' + error.message); }
      return null;
    }
  };

  // The game gateway, as JAVA_Network and ServerPost send it.
  let header = {};
  Object.assign(C[PKG + '/CUserDefined'], {
    SetHeaderInfo(h) {
      header = h.fields;
      const session = root.KnsSession || (root.KnsSession = { userNo: 0 });
      session.userNo = h.fields.i32UserNo || 0;
    },
    RequestServer(send) {
      // Native code reuses send fields; retain this request's protocol until its asynchronous reply arrives.
      const f = { ...send.fields };
      if (f.i32SendProtocol === 51) loggedInGame = true;
      nativeCall(UD + 'nativeSetNetState', cud(), NET.SENDING);
      const headers = {
        'Content-Type': 'text/plain',
        USERNO: String(header.i32UserNo || 0),
        SESSIONKEY: header.strSessionKey || '',
        CLIENTVERSION: header.strClientVersion || '',
        CLIENTBUNDLEIDENTIFIER: header.strBundleID || PKG_NAME,
        CLIENTLANGUAGE: header.strLanguage || 'en',
        TIMESTAMP: String(f.i64SendTime || 0),
        TIMESIGNATURE: f.strSendTimeSignature || '',
      };
      // Expose the active gateway session for achievement list sync (protocol 122).
      if (header.strSessionKey && root.KnsSession) {
        const previous = root.KnsSession.gateway;
        root.KnsSession.gateway = { url: toServer(f.strSendURL), headers };
        if ((!previous || previous.headers.USERNO !== headers.USERNO || previous.headers.SESSIONKEY !== headers.SESSIONKEY)
            && root.dispatchEvent && root.Event) root.dispatchEvent(new root.Event('kns-session-changed'));
      }
      post(f.strSendURL, f.strSendMsg, headers).then(async text => {
        const arena = (f.i32SendProtocol === 402 || f.i32SendProtocol === 403) && ctx.arenaGoods;
        const reply = arena || ctx.achievements ? await readReply(text) : null;
        // Only the lobby polls 701. A page hook failing must not turn a delivered reply into a disconnect.
        if (f.i32SendProtocol === 701 && root.KnsAccount) {
          try { root.KnsAccount.hideButton(); } catch (error) { rt.log('account button hook failed: ' + error.message); }
        }
        const bytes = s8s(text);
        nativeCall(UD + 'nativeResponseServer', cud(), java.ref(bytes), bytes.length, f.i32SendProtocol);
        if (ctx.achievements && reply) {
          try { ctx.achievements(reply); }
          catch (error) { rt.log('achievement sync failed: ' + error.message); }
        }
        if (arena && reply && !reply.errno) {
          try { ctx.arenaGoods(reply); }
          catch (error) { rt.log('arena goods sync failed: ' + error.message); }
        }
        nativeCall(UD + 'nativeSetNetState', cud(), NET.RECEIVE);
        // Collected achievements come back from the server after login (achievement-sync.js).
        if (root.KnsAchievementSync && root.KnsAchievementSync.control) {
          try { root.KnsAchievementSync.control.onGateway(f.i32SendProtocol); }
          catch (error) { rt.log('achievement list hook failed: ' + error.message); }
        }
      }).catch(e => {
        rt.log('gateway error: ' + e.message);
        nativeCall(UD + 'nativeSetNetState', cud(), 10);
      });
      return true;
    },
    OpenURL(url) { if (root.open) root.open(url, '_blank'); },
    PlayMovie() {
      nativeCall(UD + 'nativeSetbyIntroPlay', cud(), 1);
      const done = () => nativeCall(UD + 'nativeSetbyIntroPlay', cud(), 2);
      if (ctx.ui) ctx.ui.playMovie(done); else done();
    },
    DelPatchFolder() {},
    CallGC() {},
    PlaySound: () => 0,
    gpgsIncrementEvents() {},
    gpgsReportScore() {},
    gpgsShowAchievement() {},
    gpgsShowLeaderBoard() {},
    gpgsShowQuests() {},
    gpgsSign() {},
    gpgsSignOut() {},
    gpgsUnlockAchievement() {},
    DownloadNoticeImgWithName() {},
    sendEmail() {},
    ProFilePatch() {},
    ExistFlag: () => false,
    GetFlagLength: () => 0,
    GetImageFlag: () => null,
    IsExistNoticeImg: () => false,
    GetNoticeImgLength: () => 0,
    GetNoticeImg: () => null,
    IsExistAvatar: () => false,
    GetUserAvatarLength: () => 0,
    GetUserAvatar: () => null,
    PatchIsExistFile: () => false,
    PatchGetFileSize: () => 0,
    PatchReadFile: () => null,
    InitFolderClientUpdate: () => true,
    GetPackageZipFileName: () => 'kns.zip',
    // Android (CUserDefined.getBase64encode) encodes the string's UTF-8 bytes; btoa alone throws on any
    // character above U+00FF. ASCII gives the same result either way. Android's 76-column line breaks are not added,
    // as before.
    getBase64encode: str => {
      const bytes = utf8e.encode(String(str));
      let s = '';
      for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
      return btoa(s);
    },
  });

  // The active ELF's jGetUserAvatar/jIsExistAvatar call this Hub class directly, not CUserDefined.
  C['com/com2us/hub2/Hub2AvatarManager'] = {
    isExistAvatar: () => false,
    getUserAvatarLength: () => 0,
    getUserAvatar: () => null,
    downloadAvatarImgWithURL() {},
    removeAvatarByUserNo: () => true,
    deleteCachedAvatar() {},
  };

  // ---------- Hub login ----------
  const hubCallback = (obj_, cb, arg) => {
    nativeCall('Java_com_com2us_peppermint_HubBridge_hubCallbackWithJSON', ctx.cls('com/com2us/peppermint/HubBridge'),
      java.jstr(JSON.stringify(obj_)), cb, arg);
  };
  const hubPost = (path, params) => post('http://hub/hub/' + path, JSON.stringify(params), { 'Content-Type': 'application/json' }).then(t => JSON.parse(t));
  const did = () => {
    let d = store.get('web_did');
    if (!d) { d = 'web-' + Date.now().toString(36) + '-' + Math.random().toString(36).slice(2, 10); store.set('web_did', d); }
    return d;
  };
  const hubError = (name, cb, arg, e) => {
    rt.log('hub ' + name + ' failed: ' + e.message);
    hubCallback({ error_code: 1, error_detail: String(e.message) }, cb, arg);
  };
  const LOGIN_CANCELLED = 2001; // hubCallbackAuth@0x16df80 gives this code no error popup.
  // An account stored by an earlier version of this page kept its Hub key where the game reads it as a guest number.
  if (store.get('account_id') && store.get('hub_guest_uid')) {
    store.set('account_guest', store.get('hub_guest_uid'));
    store.del('hub_guest_uid');
  }
  C['com/com2us/peppermint/HubBridge'] = {
    initialize: () => 0,
    setOption: () => 0,
    getIsPGS: () => 0,
    uninitialize: () => 0,
    pgsLoginProc: () => 0,
    showDialog(name, cb, arg) { rt.log('hub dialog ' + name); hubCallback({ error_code: 0 }, cb, arg); return 0; },
    socialRequest(a, b, c, cb, arg) { hubCallback({ error_code: 0 }, cb, arg); return 0; },
    guestBind(a, b, cb, arg) { hubCallback({ error_code: 1 }, cb, arg); return 0; },
    // The Yes of the game's "You need to log in to use this function" prompts. Register keeps this guest's player. Success reloads the
    // page, which logs in as the account.
    guestAcquireUid(cb, arg) {
      if (root.KnsAccount) root.KnsAccount.open('register', () => hubCallback({ error_code: LOGIN_CANCELLED }, cb, arg));
      else hubCallback({ error_code: 0 }, cb, arg);
      return 0;
    },
    logout(cb, arg) {
      store.del('web_session');
      // An account's player must not carry over to the next guest. A plain guest keeps its did, as before. The server sign-in
      // token is revoked too.
      if (store.get('account_id')) {
        if (root.KnsAccount && root.KnsAccount.signOut) root.KnsAccount.signOut();
        store.del('account_id'); store.del('account_guest'); store.del('web_did'); store.del('hub_guest_uid');
      }
      store.set('hub_logged_out', '1');
      if (root.KnsAccount) root.KnsAccount.refresh();
      hubCallback({ error_code: 0 }, cb, arg);
      return 0;
    },
    // The game treats a player as a guest while the hub_guest_uid property holds a guest number (HubGuestUIDCheck@0x16c918): the guest
    // path sets the flag at gameInfo+7 that locks Friends, Arena, Raid and Ally Missions, and this path (hubCallbackAuth) clears it. So
    // an account must never store hub_guest_uid, and a guest must never finish here. Its Hub key is account_guest instead.
    auth(cb, arg) {
      accountAuthCalled = true;
      const finish = () => hubPost('user/login', { guest_uid: store.get('account_guest'), did: did() }).then(j => {
        // Keep the server's own error code (a missing result used to become a TypeError reported as code 1).
        if (j.error_code || !j.result) {
          hubCallback({ error_code: j.error_code || 1, error_detail: String(j.error_detail || 'no login result') }, cb, arg);
          return;
        }
        hubCallback({
          error_code: j.error_code || 0, uid: String(j.uid), did: String(j.result.did), sessionkey: j.session_key || 'sk_' + j.uid,
        }, cb, arg);
      }).catch(e => hubError('auth', cb, arg, e));
      if (store.get('account_id')) {
        finish();
      } else if (store.get('hub_guest_uid') && !store.get('hub_logged_out') && !loggedInGame) {
        // A returning guest: the game remembers "auto login" and comes here, but it must stay a guest.
        setTimeout(() => rt.callJava(ctx.sym('HubGuestAuth'), []), 0);
      } else if (root.KnsAccount) {
        // The title's Log In button: ask for an id and password, then answer the game's waiting call.
        root.KnsAccount.open('login', () => hubCallback({ error_code: LOGIN_CANCELLED }, cb, arg), finish);
      }
      return 0;
    },
    asyncRequest(name, params, cb, arg) {
      const p = params ? JSON.parse(params) : {};
      if (name === 'device/registered') {
        hubCallback({ error_code: 0, is_registered: !!store.get('hub_guest_uid') && !store.get('hub_logged_out') }, cb, arg);
      } else if (name === 'guest/create') {
        store.del('hub_logged_out');
        hubPost(name, { did: did() }).then(j => {
          // A refused creation must not be remembered: "undefined" would become this browser's guest forever.
          if (j.error_code || j.guest_uid == null) {
            hubCallback({ error_code: j.error_code || 1, error_detail: String(j.error_detail || 'no guest_uid') }, cb, arg);
            return;
          }
          store.set('hub_guest_uid', String(j.guest_uid));
          // The game logs in under this did. auth() below reports did() on every later start, so it must be did() here
          // too: the server's did would make the first reload register a second player.
          hubCallback({ error_code: j.error_code || 0, did: did(), guest_uid: String(j.guest_uid) }, cb, arg);
        }).catch(e => hubError(name, cb, arg, e));
      } else if (name === 'guest/auth') {
        store.set('hub_guest_uid', String(p.guest_uid));
        hubPost(name, { guest_uid: p.guest_uid, did: did() }).then(j => {
          hubCallback({ error_code: j.error_code || 0, is_auth: true }, cb, arg);
        }).catch(e => hubError(name, cb, arg, e));
      } else {
        rt.log('hub request ' + name + ' ' + (params || ''));
        hubPost(name, Object.assign({ did: did() }, p)).then(j => hubCallback(j, cb, arg)).catch(e => hubError(name, cb, arg, e));
      }
      return 0;
    },
  };

  // ---------- ActiveUser / terms: agreement is already given on this device ----------
  C['com/com2us/module/activeuser/ActiveUser'] = {
    setLogged() {},
    start() {},
    setJNIUserAgreeNotifier(ptr) {
      nativeCall('Java_com_com2us_module_activeuser_ActiveUser_jniUserAgreeNotifier', ctx.obj('com/com2us/module/activeuser/ActiveUser'), ptr, 0);
    },
  };
  const quiet = () => undefined;
  const silent = names => Object.fromEntries(names.map(n => [n, quiet]));
  C['com/com2us/module/activeuser/useragree/UserAgreeManager'] = silent(['setEnable', 'showUI', 'reset']);
  C['com/com2us/module/push/Push'] = { ...silent(['unRegisterLocalpush', 'setOperationLocalPushOnRunning', 'setOperationGCMPushOnRunning', 'registerLocalpush', 'unRegisterAllLocalpush']), getPush: () => 0, getOperationLocalPushOnRunning: () => false, getRegistrationId: () => '' };
  C['com/com2us/module/mercury/Mercury'] = silent(['setNativeCallBack', 'setUid', 'setLogged', 'setUseDialog', 'requestShowForNotice', 'mercuryShowEx', 'setGameServer']);
  C['com/com2us/module/inapp/InApp'] = { ...silent(['iapSelectTarget', 'iapUninitialize', 'iapRestoreItem', 'iapBuyItem', 'iapAuthorizeLicense', 'iapStoreStart', 'iapStoreEnd']), iapInitialize: () => 0, iapRequestBalance: () => 0 };
  C['com/com2us/module/offerwall/Offerwall'] = silent(['setNativeCallBack', 'setLogged', 'nativeInitialize', 'nativeInitializeEx']);
  C['com/com2us/wrapper/game/CGameManager'] = { requestRender() { if (ctx.present) ctx.present(); }, setDisplay: () => true };
  C['com/com2us/wrapper/WrapperStatistics'] = silent(['FlurrySetGender', 'FlurrySend', 'FlurryStart']);

  // ---------- pages and HTTP downloads other than the gateway ----------
  const conns = new Map();
  let nextConn = 1;
  const WH = 'Java_com_com2us_wrapper_network_CWrapperHttp_';
  const httpCls = () => ctx.cls('com/com2us/wrapper/network/CWrapperHttp');
  C['com/com2us/wrapper/network/CHttpManager'] = {
    netHttpOpenEx(url) { const id = nextConn++; conns.set(id, { url, method: 'GET', code: 0 }); return id; },
    netHttpIsContains: id => (conns.has(id) ? 1 : 0),
    netHttpSetRequestMethodEx(id, k, v) { const c = conns.get(id); if (c) c.method = v || k; return 1; },
    netHttpConnectEx(id) {
      const c = conns.get(id);
      if (!c) return 0;
      fetch(toServer(c.url), { method: 'GET' }).then(async r => {
        c.code = r.status;
        c.body = new Int8Array(await r.arrayBuffer());
        nativeCall(WH + 'nativeConnectCB', httpCls(), id, r.status);
      }).catch(() => nativeCall(WH + 'nativeConnectCB', httpCls(), id, -1));
      return 1;
    },
    netHttpGetResponseBodyLength: id => ((conns.get(id) || {}).body || []).length,
    netHttpGetLengthEx: id => ((conns.get(id) || {}).body || []).length,
    netHttpGetResponseBody(id, out, n) {
      const b = (conns.get(id) || {}).body;
      if (!b) return 0;
      const c = Math.min(n, b.length, out.length);
      out.set(b.subarray(0, c));
      return c;
    },
    netHttpGetEncodingEx: () => 0,
    netHttpGetTypeEx: () => 0,
    netHttpGetResponseMessageEx: () => 0,
    netHttpGetRequestPropertyEx: () => 0,
    netHttpSetRequestPropertyEx: () => 1,
    netHttpGetProperty: () => 0,
    netHttpSetProperty: () => 1,
    getCookie: () => '',
    setCookie() {},
    netHttpCloseEx(id) { conns.delete(id); return 1; },
    netHttpGetResponseCodeEx: id => (conns.get(id) || {}).code || 0,
    netHttpGetHeaderFieldEx: () => 0,
    netHttpGetRequestMethodEx: () => 0,
  };

  // ---------- sound ----------
  const sounds = new Map();
  let nextSound = 1;
  const audio = ctx.audio || { create: () => ({}), play() {}, stop() {}, pause() {}, resume() {}, volume() {}, destroy() {} };
  const vols = { bgm: 100, se: 100 };
  const applyVol = s => audio.volume(s.h, (s.vol / 100) * (vols[s.bgm ? 'bgm' : 'se'] / 100));
  C['com/com2us/wrapper/media/CSoundManager'] = {
    soundCreate(path, isBgm) {
      const id = nextSound++;
      const s = { path, bgm: !!isBgm, vol: 100, h: audio.create(path) };
      sounds.set(id, s);
      applyVol(s);
      return id;
    },
    soundPrepare: id => sounds.has(id),
    soundPlay(id, loop) { const s = sounds.get(id); if (s) audio.play(s.h, loop); return !!s; },
    soundStop(id) { const s = sounds.get(id); if (s) audio.stop(s.h); return !!s; },
    soundPause(id) { const s = sounds.get(id); if (s) audio.pause(s.h); return !!s; },
    soundResume(id) { const s = sounds.get(id); if (s) audio.resume(s.h); return !!s; },
    soundDestroy(id) { const s = sounds.get(id); if (s) audio.destroy(s.h); sounds.delete(id); return !!s; },
    soundSetVolume(id, v) { const s = sounds.get(id); if (s) { s.vol = v; applyVol(s); } return !!s; },
    soundGetVolume: id => (sounds.get(id) || { vol: 0 }).vol,
    setVolume(v, isBgm) {
      vols[isBgm ? 'bgm' : 'se'] = v;
      for (const s of sounds.values()) if (s.bgm === !!isBgm) applyVol(s);
      return true;
    },
    getVolume: isBgm => vols[isBgm ? 'bgm' : 'se'],
    getCurrentPlaying: () => 0,
    stopAll(isBgm) { for (const s of sounds.values()) if (s.bgm === !!isBgm) audio.stop(s.h); },
    stopCurrent(isBgm) { for (const s of sounds.values()) if (s.bgm === !!isBgm) audio.stop(s.h); },
  };

  // ---------- text fields ----------
  const fields = new Map();
  let nextField = 1;
  const TI = 'Java_com_com2us_wrapper_ui_';
  const fieldEvent = (id, ev) => nativeCall(TI + 'CTextInput_nativeCallback', java.wrappers['com/com2us/wrapper/ui/CTextInput'], id, ev);
  const tiTable = {
    create(x, y, w, h) {
      const id = nextField++;
      const f = { id, frame: [x, y, w, h], text: '', props: {}, ui: null };
      fields.set(id, f);
      if (ctx.ui) f.ui = ctx.ui.createField(f, ev => fieldEvent(id, ev));
      return id;
    },
    destroy(id) { const f = fields.get(id); if (f && f.ui) f.ui.destroy(); fields.delete(id); return true; },
    getPropertyFrame: id => Int32Array.from((fields.get(id) || { frame: [0, 0, 0, 0] }).frame),
    setPropertyFrame(id, a) { const f = fields.get(id); if (!f) return false; f.frame = Array.from(a); if (f.ui) f.ui.update(); return true; },
    getPropertyText(id) { const f = fields.get(id); if (f && f.ui) f.text = f.ui.value(); return s8s(f ? f.text : ''); },
    getPropertyTextBytes(id) { const f = fields.get(id); if (f && f.ui) f.text = f.ui.value(); return f ? utf8e.encode(f.text).length : 0; },
    getPropertyTextLength(id) { const f = fields.get(id); if (f && f.ui) f.text = f.ui.value(); return f ? f.text.length : 0; },
    setPropertyText(id, b) { const f = fields.get(id); if (!f) return false; f.text = u8s(b); if (f.ui) f.ui.update(); return true; },
    setPropertyPlaceHolder(id, b) { const f = fields.get(id); if (!f) return false; f.props.placeholder = u8s(b); if (f.ui) f.ui.update(); return true; },
    getPropertyPlaceHolder(id) { const f = fields.get(id); return s8s((f && f.props.placeholder) || ''); },
    setPropertyFocus(id, on) { const f = fields.get(id); if (f && f.ui) f.ui.focus(on); return !!f; },
    requestReturnEventForcedly(id) { fieldEvent(id, 2); return 1; },
  };
  for (const n of ['AutoCapitalizationType', 'BackColor', 'KeyboardAlwaysShow', 'KeyboardType', 'MaxTextLength', 'ReturnKeyType', 'Secure',
    'TextAlignmentHorizontal', 'TextAlignmentVertical', 'TextColor', 'TextSize', 'TextType']) {
    tiTable['setProperty' + n] = (id, v) => { const f = fields.get(id); if (!f) return false; f.props[n] = v; if (f.ui) f.ui.update(); return true; };
    tiTable['getProperty' + n] = id => { const f = fields.get(id); return f ? +f.props[n] || 0 : 0; };
  }
  tiTable.getPropertyFocus = () => 0;
  tiTable.getAutoCapitalizationType = () => 0;
  C['com/com2us/wrapper/ui/CTextInput'] = tiTable;
  C['com/com2us/wrapper/ui/CUserInput'] = {
    createUserInput(type, bytes) {
      const initial = bytes ? u8s(bytes) : '';
      const answer = root.prompt ? root.prompt('', initial) : initial;
      const out = s8s(answer === null || answer === undefined ? '' : answer);
      nativeCall(TI + 'CUserInput_nativeCallback', java.wrappers['com/com2us/wrapper/ui/CUserInput'], java.ref(out));
    },
  };

  // ---------- touch: the event ring the native side polls ----------
  java.addEvent = (type, a, b, c, d) => {
    const ev = java.eventData, buf = java.eventBuffer;
    if (!ev || buf.direct === undefined) return;
    const front = ev.fields.front, rear = ev.fields.rear;
    const next = (front + 1) % 256, m = rt.cpu.m8;
    let slot = front;
    if (next === rear) {
      // Full (the game is busy loading). A move may be dropped, but a press or release must arrive, or a touch
      // stays held: it takes the newest queued slot when that holds only a touch move (type 2).
      const last = (front + 255) % 256, q = buf.direct + last * 8;
      if (type === 2 || last === rear || ((m[q] << 8) | m[q + 1]) !== 2) return;
      slot = last;
    }
    const p = buf.direct + slot * 8;
    const put16 = (o, v) => { m[p + o] = (v >> 8) & 255; m[p + o + 1] = v & 255; };
    put16(0, type); put16(2, a); put16(4, b);
    m[p + 6] = c & 255;
    m[p + 7] = d & 255;
    if (slot === front) ev.fields.front = next;
  };
  C['com/com2us/wrapper/function/CResource'] = {
    getFileDescriptorFromAsset(name, out) {
      const file = rt.openFile(name, 0);
      if (rt.retry) return;
      if (!file) return null;
      if (out) { out[0] = 0; out[1] = file.size; }
      const o = new Java.JavaObject('java/io/FileDescriptor');
      o.fields.descriptor = rt.newFd(file, 0);
      return o;
    },
  };
}

function startup({ rt, java, call, cls, sym, screen }) {
  const obj = c => java.ref(new Java.JavaObject(c));
  const w = screen.w, h = screen.h;
  const wrapper = name => {
    const c = name.replace(/\./g, '/');
    const h = obj(c);
    java.wrappers = java.wrappers || {};
    java.wrappers[c] = h;
    call(K + 'CWrapper_nativeInitialize', h, java.jstr(name));
    return h;
  };
  call(K + 'CWrapper_nativeSetClass', cls('com/com2us/wrapper/kernel/CWrapper'));
  call('Java_com_com2us_wrapper_common_CCommonAPI_nativeInitialize', cls('com/com2us/wrapper/common/CCommonAPI'));
  wrapper('com.com2us.wrapper.function.CFunctionTaskRunner');
  call(K + 'CWrapperData_nativeInitialize', obj('com/com2us/wrapper/kernel/CWrapperData'), 0);
  wrapper('com.com2us.wrapper.kernel.CEventHandler');
  wrapper('com.com2us.wrapper.game.CGameManager');
  wrapper('com.com2us.wrapper.sensor.CSensor');
  wrapper('com.com2us.wrapper.ui.CUserInput');
  wrapper('com.com2us.wrapper.ui.CTextInput');
  wrapper('com.com2us.wrapper.media.CSoundManager');
  wrapper('com.com2us.wrapper.network.CHttpManager');
  wrapper('com.com2us.wrapper.utility.CUtility');
  wrapper('com.com2us.wrapper.font.CFont');
  wrapper(PKG_NAME + '.CUserDefined');
  call('Java_com_com2us_module_activeuser_ActiveUser_jniInitialize', obj('com/com2us/module/activeuser/ActiveUser'), obj('com/com2us/module/activeuser/useragree/UserAgreeManager'));
  call('Java_com_com2us_module_inapp_InApp_jniInAppInitialize', cls('com/com2us/module/inapp/InApp'), obj('com/com2us/module/inapp/InApp'));
  call('Java_com_com2us_module_mercury_Mercury_nativeMercuryInitialize', obj('com/com2us/module/mercury/Mercury'));
  call('Java_com_com2us_module_push_Push_jniPushInitialize', cls('com/com2us/module/push/Push'), obj('com/com2us/module/push/Push'));
  call('Java_com_com2us_module_offerwall_Offerwall_nativeOfferwallInitialize', obj('com/com2us/module/offerwall/Offerwall'));
  call('Java_com_com2us_peppermint_HubBridge_callCSHubInitialize', cls('com/com2us/peppermint/HubBridge'));
  const ev = { cls: 'com/com2us/wrapper/kernel/CEventHandler$EventData', fields: { front: 0, rear: 0 } };
  const buf = { cap: 0x800 };
  java.eventData = ev;
  java.eventBuffer = buf;
  call(K + 'CEventHandler_nativeEventHandlerInitialize', obj('com/com2us/wrapper/kernel/CEventHandler'), java.ref(ev), java.ref(buf), 8, 256);
  const pixels = new Int32Array(rt.cpu.buffer, rt.calloc(FONT_SIZE * FONT_SIZE * 4), FONT_SIZE * FONT_SIZE);
  java.fontPixels = pixels;
  call('Java_com_com2us_wrapper_font_CFont_nativeFontInitialize', obj('com/com2us/wrapper/font/CFont'), java.ref(pixels), FONT_SIZE, FONT_SIZE, 1);
  call(K + 'CWrapperData_nativeSetScreenSize', obj('com/com2us/wrapper/kernel/CWrapperData'), w, h, w, h, w, h);
  call(K + 'CWrapperKernel_nativeStart', cls('com/com2us/wrapper/kernel/CWrapperKernel'));
  call(K + 'CWrapperKernel_nativeStartClet', cls('com/com2us/wrapper/kernel/CWrapperKernel'));
  call(K + 'CWrapperKernel_nativeResumeClet', cls('com/com2us/wrapper/kernel/CWrapperKernel'));
  call(K + 'CWrapperKernel_nativeResume', cls('com/com2us/wrapper/kernel/CWrapperKernel'));
}

const api = { installJava, startup, PKG, PKG_NAME };
root.KnsJavaShim = api;
if (typeof module !== 'undefined') module.exports = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
