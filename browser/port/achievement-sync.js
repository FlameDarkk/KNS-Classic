// Collected achievements from the server after every login, plus the shared AES-128-CBC gateway helper.
//
// The native client keeps which achievements were collected in its own save (kns:file:*) and asks the server for the list
// (protocol 121, whose reply handler sends 122) only on one rare login path, NetworkEvent@0x153068; a normal login chain
// (51, 60, 61, 330, 501, ...) never sends them. A browser whose save belongs to another player, or a new browser, so showed
// every collected achievement as "Collect" again (the server refuses the repeat claims). After the login/load reply (60)
// this asks the server for 122 once per game session and feeds each record through the game's own
// ParsingDataToAchieveFinish@0x14c2e0 (0x30-byte record) into UpdateDataAchieveFinish@0x10218c, which overwrites a known id
// and adds a new one (GetAchieveFinishByID first), the same as the native 122 reply loop at 0x153b10. Repeats are harmless.
(function (root) {
'use strict';

const RECORD_SIZE = 0x30;

// Gateway cipher helper: body = base64(AES-128-CBC(JSON {protocol, params})), matching the native client.
const gateway = (() => {
  const KEY = new TextEncoder().encode('1m!1HUB2@hero#Re');
  const IV = new Uint8Array('de295106d3872f949783942fdc9f01e2'.match(/../g).map(h => parseInt(h, 16)));
  let keyPromise = null;
  const key = () => keyPromise || (keyPromise = crypto.subtle.importKey('raw', KEY, 'AES-CBC', false, ['encrypt', 'decrypt']));
  const toB64 = bytes => { let s = ''; for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000)); return btoa(s); };
  const fromB64 = text => Uint8Array.from(atob(text.trim()), c => c.charCodeAt(0));
  async function decode(text) {
    return JSON.parse(new TextDecoder().decode(await crypto.subtle.decrypt({ name: 'AES-CBC', iv: IV }, await key(), fromB64(text))));
  }
  async function call(protocol, params = {}) {
    const session = root.KnsSession && root.KnsSession.gateway;
    if (!session) throw new Error('not signed in');
    const plain = new TextEncoder().encode(JSON.stringify({ protocol, params }));
    const body = toB64(new Uint8Array(await crypto.subtle.encrypt({ name: 'AES-CBC', iv: IV }, await key(), plain)));
    const response = await fetch(session.url, { method: 'POST', headers: { ...session.headers, TIMESTAMP: String(Math.floor(Date.now() / 1000)) }, body,
      signal: typeof AbortSignal !== 'undefined' && AbortSignal.timeout ? AbortSignal.timeout(15000) : undefined });
    if (!response.ok) throw new Error('HTTP ' + response.status);
    const reply = await decode(await response.text());
    if (reply.errno) throw new Error(reply.error || 'error ' + reply.errno);
    return reply;
  }
  return { call, decode };
})();

function install(rt, call = (protocol, params) => root.KnsGateway.call(protocol, params),
    sessionKey = () => root.KnsSession && root.KnsSession.gateway && root.KnsSession.gateway.headers.SESSIONKEY) {
  const sym = name => rt.syms.get(name);
  const queue = (name, args) => new Promise(resolve => rt.callJava(sym(name), args, resolve));
  let syncedFor = null;

  async function apply(rows) {
    let applied = 0;
    for (const row of rows) {
      if (!row || !Number.isSafeInteger(row.achievement_id) || row.achievement_id <= 0 || row.achievement_id > 0x7fffffff) continue;
      const str = rt.newCstr(JSON.stringify({ achievement_id: row.achievement_id, idx: row.idx | 0, reg_date: String(row.reg_date || '1') }));
      const data = rt.calloc(RECORD_SIZE);
      try {
        const json = await queue('json_loads', [str, 0, 0]);
        if (!json) continue;
        try {
          await queue('ParsingDataToAchieveFinish', [json, data]);
          await queue('UpdateDataAchieveFinish', [data]);
          applied++;
        } finally {
          await queue('json_delete', [json]);
        }
      } finally {
        rt.free(str);
        rt.free(data);
      }
    }
    return applied;
  }

  async function sync() {
    const reply = await call(122, {});
    return apply(Array.isArray(reply.achievement_reward_infos) ? reply.achievement_reward_infos : []);
  }

  return {
    apply,
    sync,
    // java_shim.js calls this after each native gateway reply.
    onGateway(protocol) {
      if (protocol !== 60) return null;
      const key = sessionKey();
      if (!key || key === syncedFor) return null;
      syncedFor = key;
      return sync().catch(error => {
        syncedFor = null; // try again on the next load
        if (rt.log) rt.log('achievement list sync failed: ' + error.message);
      });
    },
  };
}

const api = { install, gateway };
root.KnsGateway = gateway;
root.KnsAchievementSync = api;
if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof globalThis !== 'undefined' ? globalThis : window);
