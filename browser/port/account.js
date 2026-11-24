// Accounts of the browser build: the panel, and the switch to an account's player.
//
// KnS id + password accounts on this game's own server. The server answers a login with the account's device id and a sign-in
// token; this page stores the device id as web_did, so the game's own Hub login (java_shim.js) signs in as that player, and the
// token in localStorage kns_session, so the next visit starts straight in. Registering while a guest keeps the guest's device id,
// and so its player. The password is never stored.
(function () {
const SERVER = (window.KNS_SERVER || 'http://127.0.0.1:8081').replace(/\/$/, '');
const PROP = 'kns:prop:';
const INSTANCE_KEY = 'kns:instance';
const get = k => { try { return localStorage.getItem(PROP + k); } catch (e) { return null; } };
// Outside the kns: prefix on purpose: forgetLocal() clears kns:* and must not sign the player out.
const SESSION_KEY = 'kns_session';

const storedSession = () => {
  try {
    const s = JSON.parse(localStorage.getItem(SESSION_KEY) || 'null');
    return s && typeof s.token === 'string' && s.token ? s : null;
  } catch (e) { return null; }
};

const saveSession = j => {
  try { localStorage.setItem(SESSION_KEY, JSON.stringify({ token: j.token, login_id: j.login_id })); } catch (e) { /* the game login below still works */ }
};

const dropSession = () => { try { localStorage.removeItem(SESSION_KEY); } catch (e) { /* nothing was stored */ } };

const SAVE_OWNER = 'kns:save_owner';
const SAVE_PREFIX = 'kns:file:';
const forgetLocal = (keepAccount = false, wipeSave = false) => {
  try {
    const keep = new Set([INSTANCE_KEY, EPOCH_KEY, ...(keepAccount ? ['account_id', 'web_did', 'account_guest'].map(k => PROP + k) : [])]);
    const isSave = k => k === SAVE_OWNER || k.startsWith(SAVE_PREFIX);
    Object.keys(localStorage).filter(k => k.startsWith('kns:') && !keep.has(k) && (wipeSave || !isSave(k)))
      .forEach(k => localStorage.removeItem(k));
  } catch (e) { /* nothing was stored */ }
};

const el = (tag, props, ...kids) => {
  const e = Object.assign(document.createElement(tag), props);
  e.append(...kids);
  return e;
};

let panel = null;
let onCancel = null;
let onDone = null;
const close = () => {
  if (!panel) return;
  panel.remove();
  panel = null;
  const cancelled = onCancel;
  onCancel = null;
  onDone = null;
  if (cancelled) cancelled();
};

// What the game needs for a login. Never hub_guest_uid: that would make it treat this account as a guest (see java_shim.js auth).
const store = j => {
  const keys = ['account_id', 'web_did', 'account_guest', 'hub_guest_uid', 'hub_logged_out'];
  const before = new Map();
  try {
    for (const key of keys) before.set(key, localStorage.getItem(PROP + key));
    localStorage.setItem(PROP + 'web_did', j.did);
    localStorage.setItem(PROP + 'account_guest', j.guest_uid);
    localStorage.setItem(PROP + 'account_id', j.login_id);
    localStorage.removeItem(PROP + 'hub_guest_uid');
    localStorage.removeItem(PROP + 'hub_logged_out');
  } catch (err) {
    for (const [key, value] of before) {
      try { if (value === null) localStorage.removeItem(PROP + key); else localStorage.setItem(PROP + key, value); } catch (ignored) { /* best effort */ }
    }
    throw new Error('Browser storage is unavailable. Enable it or free space, then log in again.');
  }
};

// An account answer becomes this page's account. Another player's saved game state goes.
const storeAccount = j => {
  let owner = null;
  try { owner = localStorage.getItem(SAVE_OWNER); } catch (e) { /* no storage */ }
  if ((owner || get('web_did')) !== j.did) forgetLocal(false, true);
  else if (get('web_did') !== j.did) forgetLocal();
  store(j);
  try { localStorage.setItem(SAVE_OWNER, j.did); } catch (e) { /* the save just is not kept for next time */ }
};

const send = async (path, body) => {
  let r;
  try {
    r = await fetch(SERVER + path, { method: 'POST', headers: { 'Content-Type': 'text/plain' }, body: JSON.stringify(body),
      signal: typeof AbortSignal !== 'undefined' && AbortSignal.timeout ? AbortSignal.timeout(20000) : undefined });
  } catch (e) {
    throw new Error('Cannot reach the game server. Is startgame.bat running?');
  }
  const j = await r.json().catch(() => ({}));
  if (!r.ok) throw Object.assign(new Error(j.error || 'The server said no (HTTP ' + r.status + ').'), { code: j.code });
  return j;
};

// Ends the sign-in on the server and here. Clears this page's account; the caller reloads or answers the game.
const signOut = () => {
  const session = storedSession();
  if (session) {
    fetch(SERVER + '/account/logout', { method: 'POST', headers: { 'Content-Type': 'text/plain' }, body: JSON.stringify({ token: session.token }), keepalive: true })
      .catch(() => { /* this browser is signed out either way */ });
  }
  dropSession();
  forgetLocal();
};

const openForm = startMode => {
  let mode = startMode;
  const id = el('input', { name: 'username', autocomplete: 'username', maxLength: 24, required: true, autocapitalize: 'none', spellcheck: false });
  const username = el('input', { name: 'nickname', autocomplete: 'off', maxLength: 24, required: true, autocapitalize: 'none', spellcheck: false });
  const usernameRow = el('label', {}, 'In-game name', username);
  const pass = el('input', { name: 'password', type: 'password', maxLength: 128, required: true });
  const again = el('input', { name: 'password2', type: 'password', maxLength: 128, autocomplete: 'new-password' });
  const againRow = el('label', {}, 'Repeat password', again);
  const note = el('p', { className: 'acct-note' });
  const error = el('p', { className: 'acct-error', role: 'alert' });
  const submit = el('button', { type: 'submit', className: 'acct-primary' });
  const tabLogin = el('button', { type: 'button' }, 'Log In');
  const tabRegister = el('button', { type: 'button' }, 'Register');
  const guest = get('hub_guest_uid');

  const show = () => {
    const registering = mode === 'register';
    tabLogin.className = registering ? '' : 'on';
    tabRegister.className = registering ? 'on' : '';
    tabLogin.setAttribute('aria-pressed', String(!registering));
    tabRegister.setAttribute('aria-pressed', String(registering));
    againRow.hidden = !registering;
    usernameRow.hidden = !registering;
    again.required = registering;
    username.required = registering;
    pass.autocomplete = registering ? 'new-password' : 'current-password';
    submit.textContent = registering ? 'Register' : 'Log In';
    error.textContent = '';
    if (registering) {
      note.textContent = guest ? 'Your current guest player moves to this account.'
        : 'ID: 3 to 24 letters, digits, _ or -. In-game name: 1 to 24 characters, unique. Password: 8 or more characters.';
    } else {
      note.textContent = guest ? 'This browser\'s guest player stays behind. To keep it, use Register instead.' : '';
    }
  };
  tabLogin.onclick = () => { mode = 'login'; show(); id.focus(); };
  tabRegister.onclick = () => { mode = 'register'; show(); id.focus(); };

  const form = el('form', { className: 'acct-form' },
    el('div', { className: 'acct-tabs' }, tabLogin, tabRegister),
    el('label', {}, 'ID', id), usernameRow, el('label', {}, 'Password', pass), againRow, note, error,
    el('div', { className: 'acct-actions' }, submit, el('button', { type: 'button', onclick: close }, 'Close')));
  form.onsubmit = async e => {
    e.preventDefault();
    error.textContent = '';
    if (mode === 'register' && pass.value !== again.value) { error.textContent = 'The passwords do not match.'; return; }
    submit.disabled = true;
    try {
      let j;
      if (mode === 'register') {
        const body = { id: id.value.trim(), password: pass.value, nickname: username.value };
        if (guest) { body.did = get('web_did'); body.guest_uid = guest; }
        j = await send('/account/register', body);
      } else {
        j = await send('/account/login', { id: id.value.trim(), password: pass.value });
      }
      saveSession(j);
      storeAccount(j);
      if (onDone) {
        // The game's Log In button is waiting for the answer: no reload needed.
        const done = onDone;
        onDone = null;
        onCancel = null;
        close();
        window.KnsAccount.refresh();
        done();
      } else {
        location.reload();
      }
    } catch (err) {
      error.textContent = err.message;
      submit.disabled = false;
    }
  };
  show();
  return { form, focus: () => id.focus() };
};

const openSignedIn = account => {
  const nickname = el('input', { name: 'nickname', maxLength: 24, required: true, autocomplete: 'off' });
  const nickError = el('p', { className: 'acct-error', role: 'alert' });
  const nickSubmit = el('button', { type: 'submit', className: 'acct-primary' }, 'Change nickname');
  const nickForm = el('form', { className: 'acct-form' },
    el('p', { className: 'acct-who' }, 'Logged in as ', el('b', {}, account)),
    el('p', { className: 'acct-note' }, 'Change your displayed nickname below; the game reloads after saving.'),
    el('label', {}, 'New nickname', nickname), nickError,
    el('div', { className: 'acct-actions' }, nickSubmit,
      el('button', { type: 'button', onclick: () => { signOut(); location.reload(); } }, 'Log Out'),
      el('button', { type: 'button', onclick: close }, 'Close')));
  const token = () => {
    const session = storedSession();
    if (!session) throw new Error('Your sign-in has expired. Log out and log in again.');
    return session.token;
  };
  nickForm.onsubmit = async event => {
    event.preventDefault(); nickError.textContent = ''; nickSubmit.disabled = true;
    try {
      await send('/account/nickname', { token: token(), nickname: nickname.value });
      location.reload();
    } catch (err) { nickError.textContent = err.message; nickSubmit.disabled = false; }
  };

  // Change password: the current one, then the new one twice. Other browsers signed in to this account are signed out.
  const current = el('input', { name: 'current-password', type: 'password', autocomplete: 'current-password', maxLength: 128, required: true });
  const fresh = el('input', { name: 'new-password', type: 'password', autocomplete: 'new-password', maxLength: 128, required: true });
  const freshAgain = el('input', { name: 'new-password2', type: 'password', autocomplete: 'new-password', maxLength: 128, required: true });
  const passError = el('p', { className: 'acct-error', role: 'alert' });
  const passDone = el('p', { className: 'acct-note', role: 'status' });
  const passSubmit = el('button', { type: 'submit', className: 'acct-primary' }, 'Change password');
  const passForm = el('form', { className: 'acct-form acct-password' },
    el('label', {}, 'Current password', current), el('label', {}, 'New password', fresh), el('label', {}, 'Repeat new password', freshAgain),
    el('p', { className: 'acct-note' }, 'Password: 8 or more characters. Other browsers signed in to this account are signed out.'),
    passError, passDone, el('div', { className: 'acct-actions' }, passSubmit));
  passForm.onsubmit = async event => {
    event.preventDefault(); passError.textContent = ''; passDone.textContent = '';
    if (fresh.value !== freshAgain.value) { passError.textContent = 'The new passwords do not match.'; return; }
    passSubmit.disabled = true;
    try {
      await send('/account/password', { token: token(), password: current.value, new_password: fresh.value });
      current.value = fresh.value = freshAgain.value = '';
      passDone.textContent = 'Password changed.';
    } catch (err) { passError.textContent = err.message; }
    passSubmit.disabled = false;
  };
  return el('div', {}, nickForm, passForm);
};

// cancelled runs if the panel closes without a login: the game is waiting for an answer from its Log In button.
const open = (mode = 'login', cancelled = null, done = null) => {
  close();
  onCancel = cancelled;
  onDone = done;
  const account = get('account_id');
  const content = account ? { form: openSignedIn(account), focus() {} } : openForm(mode);
  panel = el('div', { className: 'acct-back', onclick: e => { if (e.target === panel) close(); } },
    el('div', { className: 'acct-panel', role: 'dialog', ariaLabel: 'Account' }, content.form));
  panel.addEventListener('keydown', e => { e.stopPropagation(); if (e.key === 'Escape') close(); });
  document.body.appendChild(panel);
  content.focus();
};

const button = el('button', { id: 'acct-open', type: 'button', onclick: () => open() });
const refresh = () => { button.textContent = get('account_id') ? 'Account: ' + get('account_id') : 'Log In / Register'; };
refresh();
document.body.appendChild(button);

// After a server-wide progress reset (tools/reset_progress.py), drop this browser's native game save (kns:file:*: tutorial
// state, local flags) once, so it starts as fresh as the server data. An unreachable server changes nothing.
const EPOCH_KEY = 'kns:progress_epoch';
const syncProgressEpoch = async () => {
  let epoch;
  try {
    const r = await fetch(SERVER + '/api/progress', { method: 'POST', headers: { 'Content-Type': 'text/plain' }, body: '{}',
      signal: typeof AbortSignal !== 'undefined' && AbortSignal.timeout ? AbortSignal.timeout(8000) : undefined });
    epoch = Number((await r.json()).progress_epoch);
  } catch (e) { return; }
  if (!Number.isFinite(epoch) || epoch <= 0) return;
  try {
    if (localStorage.getItem(EPOCH_KEY) === String(epoch)) return;
    Object.keys(localStorage).filter(k => k.startsWith('kns:file:')).forEach(k => localStorage.removeItem(k));
    localStorage.setItem(EPOCH_KEY, String(epoch));
  } catch (e) { /* storage blocked: nothing saved locally either */ }
};

// Before the game starts: a stored sign-in decides the account, so the title logs straight in as that player.
const ready = (async () => {
  await syncProgressEpoch();
  const session = storedSession();
  if (!session) {
    // An account id stored without a sign-in token (from an older build): log in again.
    if (get('account_id')) { forgetLocal(); refresh(); }
    return;
  }
  try {
    storeAccount(await send('/account/session', { token: session.token }));
  } catch (err) {
    console.warn('Stored sign-in not used:', err.message);
    // An expired sign-in must not leave the old player logged in; an unreachable server keeps it for next time.
    if (err.code === 'session_expired') { dropSession(); forgetLocal(); }
  }
  refresh();
})();

window.KnsAccount = {
  open,
  refresh,
  ready,
  // The game's own Log Out.
  signOut() { signOut(); refresh(); },
  // The lobby needs the whole screen; accounts are for the title screen. Reload to come back to it.
  hideButton() { button.hidden = true; },
};
})();
