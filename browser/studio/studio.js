'use strict';
const $ = id => document.getElementById(id);
const DATA = window.STUDIO_DATA;
const KEY_STORAGE = 'knsclassic-studio-key';
const catalog = DATA.catalog;
const masterById = new Map(catalog.map(item => [item.id, item]));
const state = { admin: false, account: null, connected: false, connecting: false, writing: false, generation: 0,
  pane: 'catalog', kind: 'equipment', category: 'Armor', selection: null, entries: [], events: [], dirty: false, partyPick: null, checked: new Set(), mailBasket: [] };
const kinds = [['equipment','Hero items'],['ally-equipment','Ally items'],['allies','Allies'],['materials','Materials'],['consumables','Consumables']];
const escapeHtml = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const format = value => Number(value || 0).toLocaleString();
const selected = () => Number($('accounts').value);
const fields = form => Object.fromEntries(new FormData(form));
const status = (message, failed = false) => { $('status').textContent = message; $('status').classList.toggle('error', failed); };
// Auto-pick target: the KnS server hosts this page at <base>studio with its API at <base>api; browser/serve.py serves it as
// /kns-studio.html with the /studio-api proxy; file:// has no proxy, so it uses the direct local address.
const isHosted = () => location.protocol !== 'file:' && !location.pathname.endsWith('/kns-studio.html');
const autoTarget = () => isHosted() ? 'hosted' : 'local';
const baseFor = mode => {
  if (mode === 'hosted') return new URL('api', location.href).href;
  if (mode === 'custom') return ($('server').value || '').replace(/\/$/, '');
  return location.protocol === 'file:' ? 'http://127.0.0.1:8081' : '/studio-api';
};
const targetLabel = mode => mode === 'hosted' ? 'This server' : mode === 'custom' ? 'Custom' : 'Local (127.0.0.1:8081)';
const restoreKey = () => {
  const hashMatch = location.hash.match(/#key=([A-Za-z0-9._-]+)/);
  if (hashMatch) {
    try { history.replaceState(null, '', location.pathname + location.search); } catch {}
    return hashMatch[1];
  }
  try { return sessionStorage.getItem(KEY_STORAGE) || ''; } catch { return ''; }
};
const saveKey = value => { try { localStorage.removeItem(KEY_STORAGE); if (value) sessionStorage.setItem(KEY_STORAGE, value); else sessionStorage.removeItem(KEY_STORAGE); } catch {} };
// Studio is a separate tab: tell open game tabs whose player changed so they refresh by themselves.
// user_no -1 means "every player" (raid-wide changes).
const notifySync = (userNo = selected()) => {
  try { const channel = new BroadcastChannel('knsclassic-studio-sync'); channel.postMessage({ user_no: userNo }); channel.close(); } catch {}
};
const context = () => ({ base: baseFor($('target').value), key: $('key').value, generation: state.generation });
const current = ctx => ctx.generation === state.generation && ctx.base === baseFor($('target').value) && ctx.key === $('key').value;
const request = async (query = '', body = null, ctx = context()) => {
  if (!ctx.base) throw new Error('Enter a server address for Custom target.');
  const controller = new AbortController(), timer = setTimeout(() => controller.abort(), 15000);
  try {
    const response = await fetch(ctx.base + '/admin/accounts' + query, { method: body ? 'POST' : 'GET', signal: controller.signal,
      headers: { 'X-Admin-Key': ctx.key, ...(body ? {'Content-Type':'application/json'} : {}) }, ...(body ? {body:JSON.stringify(body)} : {}) });
    let result;
    try { result = JSON.parse(await response.text()); } catch { throw new Error(`Server returned an invalid response (HTTP ${response.status}).`); }
    if (!response.ok) throw new Error(result.error || `HTTP ${response.status}`);
    return result;
  } finally { clearTimeout(timer); }
};
const run = async fn => { try { await fn(); } catch (error) { status(error.name === 'AbortError' ? 'Server request timed out. Check the target and reconnect.' : error.message, true); } };
function enabled() {
  document.querySelectorAll('[data-write]').forEach(button => button.disabled = state.writing || !state.connected || button.hasAttribute('data-protected') || (button.hasAttribute('data-account') && !state.account));
  for (const id of ['accounts','target','server','key','connect','refresh']) $(id).disabled = state.writing || (id === 'connect' && state.connecting);
  document.body.setAttribute('aria-busy', String(state.writing));
}
function record(message) {
  const p = document.createElement('p'); p.textContent = `${new Date().toLocaleTimeString()} · ${message}`;
  $('activity').prepend(p); while ($('activity').children.length > 8) $('activity').lastElementChild.remove();
}
async function mutate(body, message, global = false, reload = null) {
  if (state.writing) return;
  if (!state.admin) throw new Error('Unlock Admin access first.');
  if (!state.connected || (!global && !state.account)) throw new Error('Connect and select an account first.');
  const ctx = context(), user = selected();
  state.writing = true; enabled();
  try {
    await request('', global ? body : {user_no:user, ...body}, ctx);
    if (!current(ctx)) return;
    notifySync(global ? -1 : user); record(message); state.dirty = false;
    try { if (reload) await reload(ctx); else if (!global && selected() === user) await load(ctx); }
    catch (error) { status(`${message} The write succeeded, but refresh failed: ${error.message}`, true); return; }
    status(`${message} ${global ? 'Updated for every account on this server.' : 'Saved to the selected account.'}`);
  } finally { state.writing = false; enabled(); }
}
function getMaster(id, kind = 'equipment') {
  return masterById.get(Number(id)) || { id:Number(id), name:`Master ID ${id}`, kind, category:'Unmapped', grade:0, icon:null, source:'Artwork not mapped in the existing assets' };
}
function art(item) { return item.icon ? `<img src="${item.icon}" alt="${escapeHtml(item.name)}" loading="lazy">` : '<span class="no-art">No mapped<br>game artwork</span>'; }
function stars(item) {
  if (item.kind === 'equipment') return `<span class="tier">Grade ${item.grade}${item.number?' · '+rarityName(item.options?.length||0)+' · '+(item.options?.length||0)+' options':''}</span>`;
  if (item.kind === 'ally-equipment') return `<span class="tier">Tier ${item.grade}</span>`;
  if (!item.grade || ['materials','consumables'].includes(item.kind)) return '';
  return '<span class="stars">' + Array.from({length:Math.min(6,item.grade)}, () => `<img src="${DATA.ui.star}" alt="">`).join('') + '</span>';
}
function capability(name) { return !!state.account?.capabilities?.includes(name); }
function snapshotEntries(data) {
  const entries = [], worn = data.hero_equipment || {}, busy = new Set(data.busy_fellows || []);
  const party = data.party || [];
  for (const row of data.items || []) entries.push({ ...getMaster(row.ItemId), row, options:data.item_options?.[row.ItemNumber]||[], number:row.ItemNumber, key:'item:'+row.ItemNumber, action:'item', locked:!!row.Locked, upgrade:row.Upgrade, equipped:Number(worn[row.ItemNumber] || 0) });
  for (const row of data.starter_items || []) entries.push({ ...getMaster(row.item_id), row, number:row.item_no, key:'item:'+row.item_no, action:'item', starter:true, locked:!!row.item_lock, upgrade:0, equipped:row.hero_no });
  for (const row of data.fellows || []) entries.push({ ...getMaster(row.FellowId,'allies'), row, number:row.FellowNumber, key:'fellow:'+row.FellowNumber, action:'fellow', locked:!!row.Locked, upgrade:row.Upgrade,
    inParty:party.some(p=>p.FellowNumber===row.FellowNumber), busy:busy.has(row.FellowNumber), equipped:(data.fellow_items||[]).some(g=>g.FellowNumber===row.FellowNumber) });
  for (const row of data.fellow_items || []) entries.push({ ...getMaster(row.FellowItemId,'ally-equipment'), row, number:row.FellowItemNumber, key:'fellow_item:'+row.FellowItemNumber, action:'fellow_item', locked:!!row.Locked,
    upgrade:row.Upgrade, equipped:row.FellowNumber, busy:busy.has(row.FellowNumber) });
  for (const row of data.goods || []) entries.push({ ...getMaster(row.ItemId,'consumables'), row, number:row.ItemId, key:'goods:'+row.ItemId, action:'goods', count:row.Count });
  return entries;
}
function hydrate(data) {
  state.account = data; state.entries = snapshotEntries(data);
  const account = data.account;
  for (const [key,value] of Object.entries({nickname:account.Nickname||'',gold:account.Gold,gems:account.Gems,level:account.Level,experience:account.Experience})) $('profile').elements[key].value = value;
  for (const [id,key] of [['goodsList','goods'],['itemsList','items'],['fellowsList','fellows'],['fellowItemsList','fellow_items']]) $(id).textContent = JSON.stringify(data[key],null,2);
  $('accountTitle').textContent = account.Nickname || '(unnamed)'; $('accountLevel').textContent = `Lv.${account.Level} · Account #${account.UserNumber}`;
  const option=Array.from($('accounts').options).find(o=>Number(o.value)===account.UserNumber);
  if(option)option.textContent=`${account.UserNumber} — ${account.Nickname||'(unnamed)'}`;
  $('goldBalance').textContent = format(account.Gold); $('gemBalance').textContent = format(account.Gems);
  $('pointBalance').textContent = format((data.goods||[]).find(g=>g.ItemId===1900016)?.Count);
  const limit = Number(data.player_levels?.[account.Level] || 0);
  $('xpSummary').textContent = limit ? `${format(account.Experience)} / ${format(limit)} EXP` : `${format(account.Experience)} current-level EXP`;
  $('xpFill').style.width = limit ? `${Math.min(100,100*account.Experience/limit)}%` : '0%';
  const members = (data.party||[]).filter(p=>p.SetupType===3).sort((a,b)=>a.Position-b.Position).map(p=>state.entries.find(e=>e.action==='fellow'&&e.number===p.FellowNumber)).filter(Boolean);
  const portrait = members[0]||state.entries.find(e=>e.action==='fellow'); $('avatar').innerHTML = portrait ? art(portrait) : '<span class="muted">No owned ally artwork</span>';
  state.dirty = false; updateXpLimit(); renderResources(); render(); enabled();
}
async function load(ctx = context()) {
  const user = selected();
  if (!user) { state.account=null;state.entries=[];render();enabled();return; }
  const data = await request('?user_no='+user,null,ctx);
  if (!current(ctx) || selected()!==user) return;
  hydrate(data); status(`Account loaded on ${targetLabel($('target').value)}.`);
}
async function loadRaid(ctx = context()) {
  const data = await request('?raid=1',null,ctx); if (!current(ctx)) return;
  const raid=data.raid; if (!raid) throw new Error('Raid status is missing from the server response.');
  $('raidStatus').textContent=`Raid ${raid.raid_no} · boss ${raid.stage_id} · ${format(raid.remain_time)} seconds remaining`;
  $('raidBoss').replaceChildren(...data.bosses.map(b=>new Option(`${b.name} (${b.stage_id})`,b.stage_id)));
  $('raidBoss').value=String(raid.stage_id);$('raid').hidden=false;
}
async function loadArena(ctx = context()) {
  const data=await request('?arena=1',null,ctx); if (!current(ctx)) return;
  const arena=data.arena; if (!arena) throw new Error('Arena status is missing from the server response.');
  const allyMode=arena.featured_ally_override?'pinned':'auto';
  const allyLabel=arena.featured_ally_name?` · ${arena.featured_ally_name} (${allyMode})`:'';
  $('arenaStatus').textContent=`Season ${arena.season} · ${arena.closed?'ended':'active'}${allyLabel} · ${format(arena.remain_time)} seconds remaining`;
  $('arenaNumber').value=arena.season;
  if (Array.isArray(data.arena_allies)) {
    $('arenaAlly').replaceChildren(
      new Option('Auto (cycle by season)', '0'),
      ...data.arena_allies.map(a=>new Option(`6★ ${a.six_star_name} / 5★ ${a.five_star_name}`, a.six_star_id))
    );
    $('arenaAlly').value=String(arena.featured_ally_override||0);
  }
  $('arena').hidden=false;
}
async function connect() {
  if (state.connecting||state.writing) return;
  state.connecting=true; const previous=selected();state.generation++; const ctx=context();
  state.partyPick=null;state.checked.clear();state.account=null;state.entries=[];state.connected=false;state.dirty=false;enabled();render();status('Connecting…');
  try {
    const data=await request('',null,ctx);if(!current(ctx))return;
    saveKey(ctx.key);state.connected=true;state.admin=true;
    const dlg=$('themeSettings');if(dlg&&dlg.open)dlg.close();
    const accounts=data.accounts||[], named=accounts.filter(a=>a.Nickname).sort((a,b)=>a.Nickname.localeCompare(b.Nickname));
    const guests=accounts.filter(a=>!a.Nickname).sort((a,b)=>a.UserNumber-b.UserNumber);
    $('accounts').replaceChildren();
    for(const [label,list] of [['Named (login)',named],['Guests',guests]]) { if(!list.length)continue;const group=document.createElement('optgroup');group.label=label;list.forEach(a=>group.appendChild(new Option(`${a.UserNumber} — ${a.Nickname||'(unnamed)'}`,a.UserNumber)));$('accounts').appendChild(group); }
    if(accounts.some(a=>a.UserNumber===previous))$('accounts').value=String(previous);
    $('targetBadge').textContent=targetLabel($('target').value);$('connectionPanel').open=false;
    const results=await Promise.allSettled([loadRaid(ctx),loadArena(ctx),load(ctx)]);
    if(!current(ctx))return;
    const failures=results.filter(r=>r.status==='rejected').map(r=>r.reason.message);
    if(failures.length)status(failures.join(' · '),true);
    else if(!accounts.length)status('No accounts yet. Register one in the game, then reconnect.');
    if(state.pane==='events')await loadEvents(ctx);
  } catch(error) { if(current(ctx)){state.connected=false;state.admin=false;$('targetBadge').textContent='Disconnected';$('connectionPanel').open=true;status(error.message,true);} }
  finally{state.connecting=false;enabled();}
}
function showPane(pane) {
  state.pane=pane; state.selection=null; state.checked.clear(); $('heroFilter').value=''; $('rarityFilter').value='';
  if(pane==='allies')state.kind='allies';else if(pane==='equipment')state.kind='equipment';else if(pane==='catalog')state.kind='equipment';
  state.category='All';$('search').value='';
  document.querySelectorAll('[data-pane]').forEach(button=>button.classList.toggle('active',button.dataset.pane===pane));
  const inventory=['catalog','allies','equipment'].includes(pane);
  document.querySelectorAll('[data-panel]').forEach(panel=>panel.hidden=inventory?panel.id!=='inventoryPane':panel.dataset.panel!==pane);
  $('rightColumn').hidden=!inventory;$('rawData').hidden=!state.account;
  if(pane==='events'&&state.connected)run(()=>loadEvents());
  if(pane==='broadcast'&&state.connected)run(async()=>{await loadPins();await loadNotices();});
  render();
}
function render() {
  const pane=state.pane, own=pane!=='catalog';
  $('partyPreview').hidden=pane!=='allies';
  $('grid').classList.toggle('ally-grid',state.kind==='allies');
  renderParty();
  $('viewTitle').textContent=pane==='allies'?'Owned allies':pane==='equipment'?'Equipment':'Item catalog';
  $('viewDescription').textContent=own?'Inspect each owned instance, its state and equipment assignments.':'Browse original game items and grant them to the selected account.';
  $('catalogCount').textContent=`${catalog.length} master entries`;
  const tabs=kinds.filter(([kind])=>pane==='catalog'||(pane==='equipment'?['equipment','ally-equipment','materials'].includes(kind):kind===state.kind));
  $('tabs').innerHTML=tabs.map(([kind,label])=>`<button class="${kind===state.kind?'active':''}" data-kind="${kind}">${label}</button>`).join('');
  $('tabs').querySelectorAll('button').forEach(button=>button.onclick=()=>{state.checked.clear();$('heroFilter').value='';$('rarityFilter').value='';state.kind=button.dataset.kind;state.category='All';state.selection=null;render();});
  const categories=['All',...new Set(catalog.filter(e=>e.kind===state.kind).map(e=>e.category))];
  $('subtabs').innerHTML=categories.length>2?`<select aria-label="Filter by category">${categories.map(c=>`<option ${c===state.category?'selected':''}>${escapeHtml(c)}</option>`).join('')}</select>`:'';
  if($('subtabs').firstElementChild)$('subtabs').firstElementChild.onchange=event=>{state.category=event.target.value;state.selection=null;render();};
  const q=$('search').value.trim().toLowerCase(),grade=Number($('grade').value);
  let entries=(own?state.entries:catalog.map(e=>({...e,key:'catalog:'+e.id}))).filter(e=>e.kind===state.kind&&(state.category==='All'||e.category===state.category)&&(!grade||e.grade===grade)&&(!q||`${e.name} ${e.id} ${e.number||''}`.toLowerCase().includes(q)));
  entries=entries.filter(e=>(!$('heroFilter').value||heroType(e)===$('heroFilter').value)&&(!own||$('rarityFilter').value===''||rarityIndex(e.options?.length||0)===Number($('rarityFilter').value)));
  state.visible=entries;
  $('rarityFilter').hidden=!own||state.kind!=='equipment';
  $('heroFilter').hidden=state.kind!=='equipment';
  entries.sort($('sort').value==='name'?(a,b)=>a.name.localeCompare(b.name):$('sort').value==='grade'?(a,b)=>b.grade-a.grade||a.id-b.id:(a,b)=>a.id-b.id||(a.number||0)-(b.number||0));
  if(!entries.some(e=>e.key===state.selection))state.selection=entries[0]?.key||null;
  const counts=new Map();state.entries.forEach(e=>counts.set(e.id,(counts.get(e.id)||0)+(e.count??1)));
  $('grid').innerHTML=entries.length?entries.map(e=>`<div class="card-wrap"><input type="checkbox" class="multi-check" data-check="${e.key}" aria-label="Select ${escapeHtml(e.name)}${e.number?' #'+e.number:''}" ${state.checked.has(e.key)?'checked':''} ${own&&e.kind==='consumables'?'disabled':''}><button class="card ${e.kind==='allies'?'ally-card':''} ${state.selection===e.key?'selected':''}" data-entry="${e.key}" draggable="${own&&e.action==='fellow'&&!e.busy}" aria-label="Inspect ${escapeHtml(e.name)}${e.number?' #'+e.number:''}" aria-pressed="${state.selection===e.key}"><span class="owned">${own?'#'+e.number:counts.has(e.id)?'Owned ×'+counts.get(e.id):'ID '+e.id}</span>${e.upgrade?`<span class="upgrade">+${e.upgrade}</span>`:''}${e.kind==='allies'?art(e):`<span class="art" ${e.kind==='equipment'&&own?`style="background-image:url('${DATA.ui['rarity'+rarityIndex(e.options?.length||0)]}')"`:''}>${art(e)}</span>`}<span class="name">${escapeHtml(e.name)}</span>${stars(e)}${e.kind==='allies'&&own?`<span class="ally-level">Lv.${e.row.Level} · Base ATK ${format(e.attack)}</span><span class="ally-ribbon">${e.busy?'Mission':e.inParty?'In use':'Reserve'}</span>`:''}${own?`<span class="instance-badges">${e.busy?'Mission · ':''}${e.inParty?'Party · ':''}${e.equipped?'Equipped · ':''}${e.locked?'Locked':e.action==='fellow'?'Lv.'+e.row.Level:''}</span>`:''}</button></div>`).join(''):`<div class="empty">${own&&!state.account?'Connect and select an account to view its inventory.':'No entries match these filters.'}</div>`;
  $('grid').querySelectorAll('button').forEach(b=>b.onclick=event=>{
    const key=b.dataset.entry, entry=entries.find(e=>e.key===key);
    if(event.ctrlKey||event.metaKey)state.checked.delete(key);
    else if(event.shiftKey&&entry.kind!=='consumables'&&state.checked.size<100)state.checked.add(key);
    state.selection=key;const e=state.entries.find(e=>e.key===key);
    if(!event.shiftKey&&!event.ctrlKey&&!event.metaKey&&state.pane==='allies'&&e&&!e.busy)state.partyPick=e.number;
    const top=$('grid').scrollTop;render();$('grid').scrollTop=top;
  });
  $('grid').querySelectorAll('[data-check]').forEach(c=>c.onchange=()=>{if(c.checked&&state.checked.size<100)state.checked.add(c.dataset.check);else {state.checked.delete(c.dataset.check);c.checked=false;}updateBulk();});
  bindAllyDrag($('grid'));
  updateBulk();
  $('resultCount').textContent=`${entries.length} ${own?'owned instances':'available entries'}`;
  inspect(entries.find(e=>e.key===state.selection));
}
function inspect(entry) {
  const own=state.pane!=='catalog';$('inspectorTitle').textContent=own?'Inspect & edit':'Inspect & grant';
  if(!entry){$('inspector').innerHTML='<p class="muted">Choose an item or ally to inspect its details.</p>';return;}
  const e=entry,stats=e.row||e;
  $('inspector').innerHTML=`${e.kind==='allies'?art(e):`<div class="inspect-art" ${e.kind==='equipment'&&own?`style="background-image:url('${DATA.ui['rarity'+rarityIndex(e.options?.length||0)]}')"`:''}>${art(e)}</div>`}<h2 class="name outlined">${escapeHtml(e.name)}</h2>${stars(e)}<p class="category">${escapeHtml(e.category)}</p><div class="id-row">MASTER ID ${e.id}${e.number?' · INSTANCE '+e.number:''}</div>${[['Attack',stats.Attack??e.attack],['Defence',stats.Defence??e.defence],['Health',stats.Hp??e.hp]].filter(([,v])=>v).map(([name,v])=>`<div class="statrow"><span>${name}</span><b>${format(v)}</b></div>`).join('')}<p class="hint">${escapeHtml(itemExplanation(e))}</p>${(e.options||[]).map(o=>`<p class="option-text">${escapeHtml(optionText(o))}</p>`).join('')}<div id="itemEditor"></div><div class="provenance">Existing game asset<br>${escapeHtml(e.source)}</div>`;
  if(!own)grantEditor(e);else ownedEditor(e);
  enabled();
}
function grantEditor(e) {
  if(e.kind==='consumables') {
    if([1900008,1900009,1900014].includes(e.id)){$('itemEditor').innerHTML='<p class="hint">Gold and gems use Profile. Stamina cannot be set as a counted item.</p>';return;}
    const count=state.entries.find(x=>x.id===e.id&&x.action==='goods')?.count||0;
    $('itemEditor').innerHTML=`<h3 class="edit-title">Set exact balance</h3><form id="catalogGoods"><label class="formrow">Amount<input name="count" type="number" min="0" max="2147483647" value="${count}" required></label><button class="green action" type="submit" data-write data-account>Set amount</button></form>`;
    $('catalogGoods').onsubmit=ev=>{ev.preventDefault();run(()=>mutate({action:'goods',item_id:e.id,...fields(ev.target)},`${e.name} balance updated.`));};return;
  }
  const action={equipment:'item','ally-equipment':'fellow_item',allies:'fellow',materials:'material'}[e.kind];
  $('itemEditor').innerHTML=`<h3 class="edit-title">Grant to account</h3><form id="grant"><p class="hint">Destination: ${escapeHtml(kinds.find(k=>k[0]===e.kind)?.[1]||e.kind)} · ID ${e.id}</p><input name="action" type="hidden" value="${action}"><input name="item_id" type="hidden" value="${e.id}">${optionControls(e, true)}${capability('grant_quantity')?'<label class="formrow">Quantity<input name="quantity" type="number" min="1" max="100" value="1" required></label>':''}<button class="green action" type="submit" data-write data-account>Grant ${capability('grant_quantity')?'items':'one'}</button></form><p class="hint">Adds real owned instances. This administrative grant does not spend currency.</p>`;
  bindOptions($('grant'),e);
  $('grant').onsubmit=ev=>{ev.preventDefault();const body=optionBody(ev.target);if(body.action==='material'&&!capability('material_grants')){status('This server needs the Studio update before material grants are supported.',true);return;}run(()=>mutate(body,'Inventory granted.'));};
}
function ownedEditor(e) {
  if(!capability('inventory_edit')){$('itemEditor').innerHTML='<p class="hint">This server supports inspection and grants. Install the matching server update to edit instances.</p>';return;}
  const r=e.row, canRemove=!(e.locked||e.equipped||e.inParty||e.busy), ally=e.action==='fellow';
  const upgrade=e.action==='item'&&!e.starter&&e.kind==='equipment';
  $('itemEditor').innerHTML=`<h3 class="edit-title">Owned instance</h3><form id="ownedEdit">${ally?`<label class="formrow">Level<input name="level" type="number" min="1" max="${r.MaxLevel}" value="${r.Level}" required></label><label class="formrow">Current level XP<input name="experience" type="number" min="0" max="2147483647" value="${r.Experience||0}" required></label>`:''}${ally||upgrade?`<label class="formrow">Power-up<input name="upgrade" type="number" min="0" max="${ally?(e.id>=110001&&e.id<=110006?0:5):e.maxUpgrade||0}" value="${e.upgrade||0}" required></label>`:''}${upgrade?optionControls(e,false):''}<label class="formrow">Locked<input name="locked" type="checkbox" ${e.locked?'checked':''}></label><button class="green action" type="submit" data-write data-account>Save instance</button></form><div id="assignment"></div><button id="removeAsset" class="flat action small" type="button" ${canRemove?'':'disabled'}>Remove owned asset</button><p class="hint">${canRemove?'Removal requires confirmation.':e.busy?'Collect the ally mission before editing.':'Unlock, unequip and remove party links before deleting.'}</p>`;
  bindOptions($('ownedEdit'),e);
  $('ownedEdit').onsubmit=ev=>{ev.preventDefault();const body=optionBody(ev.target);body.locked=ev.target.elements.locked.checked?1:0;run(()=>mutate({action:e.action+'_edit',number:e.number,...body},`${e.name} saved.`));};
  if(e.busy)$('ownedEdit').querySelector('button').setAttribute('data-protected','');
  $('removeAsset').onclick=()=>{if(!canRemove)return;$('deleteDescription').textContent=`${e.name} · instance #${e.number} · account #${selected()}`;$('confirmDelete').showModal();$('acceptDelete').onclick=()=>{$('confirmDelete').close();run(()=>mutate({action:e.action+'_delete',number:e.number},`${e.name} removed.`));};};
  if(e.action==='item'&&e.kind==='equipment') {
    $('assignment').innerHTML=`<h3 class="edit-title">Hero equipment</h3><form id="equipForm"><label class="formrow">Hero<select name="hero_no"><option value="0">Unequipped</option>${(state.account.heroes||[]).map(h=>`<option value="${h.HeroNumber}">Hero #${h.HeroNumber} · class ${h.HeroPcid}</option>`).join('')}</select></label><button class="action small" type="submit" data-write data-account>Save equipment</button></form>`;
    $('equipForm').elements.hero_no.value=String(e.equipped||0);
    $('equipForm').onsubmit=ev=>{ev.preventDefault();run(()=>mutate({action:'item_equip',number:e.number,...fields(ev.target)},'Hero equipment updated.'));};
  } else if(e.action==='fellow_item') {
    const allies=state.entries.filter(a=>a.action==='fellow');
    $('assignment').innerHTML=`<h3 class="edit-title">Ally equipment</h3><form id="equipForm"><label>Ally<select name="fellow_no"><option value="0">Unequipped</option>${allies.map(a=>`<option value="${a.number}">${escapeHtml(a.name)} #${a.number}</option>`).join('')}</select></label><label class="formrow">Slot<select name="equip_index"><option value="1">Weapon</option><option value="2">Accessory 1</option><option value="3">Accessory 2</option></select></label><button class="action small" type="submit" data-write data-account>Save equipment</button></form>`;
    $('equipForm').elements.fellow_no.value=String(e.equipped||0);$('equipForm').elements.equip_index.value=String(r.EquipIndex||1);
    $('equipForm').onsubmit=ev=>{ev.preventDefault();run(()=>mutate({action:'fellow_item_equip',number:e.number,...fields(ev.target)},'Ally equipment updated.'));};
  } else if(ally) {
    $('assignment').innerHTML='<h3 class="edit-title">Party assignment</h3><form id="partyForm"><label class="formrow">Setup<select name="setup"><option value="3">Dungeon</option><option value="1">Setup 1</option><option value="2">Setup 2</option></select></label><label class="formrow">Slot<select name="position"><option value="-1">Remove from setup</option><option value="0">Slot 1</option><option value="1">Slot 2</option><option value="2">Slot 3</option></select></label><button class="action small" type="submit" data-write data-account>Save party assignment</button></form>';
    const refreshSlot=()=>{$('partyForm').elements.position.value=String((state.account.party||[]).find(p=>p.SetupType===Number($('partyForm').elements.setup.value)&&p.FellowNumber===e.number)?.Position??-1);};
    refreshSlot();$('partyForm').elements.setup.onchange=refreshSlot;
    $('partyForm').onsubmit=ev=>{ev.preventDefault();run(()=>mutate({action:'fellow_slot',number:e.number,...fields(ev.target)},'Party assignment updated.'));};
  }
}
function renderResources() {
  $('resourceCards').innerHTML=state.entries.filter(e=>e.action==='goods').map(e=>`<button class="resource-card panel" data-id="${e.id}"><span class="art">${art(e)}</span><span><strong>${escapeHtml(e.name)}</strong><small>ID ${e.id}</small></span><b>${format(e.count)}</b></button>`).join('')||'<p class="muted">Connect to load counted resources.</p>';
  $('resourceCards').querySelectorAll('button').forEach(b=>b.onclick=()=>{const e=state.entries.find(x=>x.id===Number(b.dataset.id)&&x.action==='goods');$('goods').elements.item_id.value=e.id;$('goods').elements.count.value=e.count;$('goods').elements.count.focus();});
}
function updateXpLimit() {
  const level=$('profile').elements.level.value, limit=state.account?.player_levels?.[level];
  $('xpLimit').textContent=limit?`Level ${level} requires ${format(limit)} EXP to advance. Current-level XP must be lower.`:'The server validates the selected level and XP.';
  $('profile').elements.experience.max=limit?Math.max(0,limit-1):2147483647;
}
async function loadEvents(ctx=context()) {
  const data=await request('?events=1',null,ctx);if(!current(ctx))return;
  if(!Array.isArray(data.events))throw new Error('Event controls require the matching Studio server update.');
  $('eventAllStatus').textContent=data.all_open?'All event dungeons are open (regular schedule saved).':'';
  const previous=$('eventStage').value;state.events=data.events.map(e=>({...e,...DATA.events[e.StageId],Name:DATA.events[e.StageId]?.name||e.Name,Description:DATA.events[e.StageId]?.description||''}));
  const difficulty=$('eventDifficulty').value;$('eventDifficulty').replaceChildren(new Option('All difficulties',''),...[...new Set(state.events.map(e=>e.difficulty).filter(Boolean))].map(d=>new Option(d,d)));$('eventDifficulty').value=difficulty;
  $('eventStage').replaceChildren(...state.events.map(e=>new Option(`${e.StageId} · ${e.Name} · ${e.Open?'Open':'Closed'}`,e.StageId)));
  if(data.events.some(e=>String(e.StageId)===previous))$('eventStage').value=previous;
  renderEvent();
  renderEventCards();
}
const utcInput=seconds=>seconds?new Date(seconds*1000).toISOString().slice(0,19):'';
function renderEvent() {
  const e=state.events.find(e=>e.StageId===Number($('eventStage').value));
  if(!e){$('eventStatus').textContent='No event schedules returned.';return;}
  $('eventStatus').textContent=e.Playable===false?`Stage ${e.StageId} · Unavailable: ${e.UnavailableReason}`:`Stage ${e.StageId} · ${e.Open?'open':'closed'} · ${e.StartTime?new Date(e.StartTime*1000).toISOString():'unscheduled'} to ${e.EndTime?new Date(e.EndTime*1000).toISOString():'unscheduled'}`;
  $('eventPreview').innerHTML=`<div class="event-hero"><img src="${e.image||eventIcon(e)}" alt="${escapeHtml(e.Name)}"><h3>${escapeHtml(e.Name)}</h3></div><div><span class="event-badge">${escapeHtml(e.difficulty||'')} · ${e.Open?'Open now':'Closed'}</span><p>${escapeHtml(e.Description||'Manage entry availability for this event.')}</p><p>Stamina: ${e.stamina??'—'} · Monster level: ${e.monsterLevel??'—'} · Stage ${e.StageId}</p></div>${eventRewards(e)}`;
  renderEventCards();
  $('eventStart').value=utcInput(e.StartTime);$('eventEnd').value=utcInput(e.EndTime);$('eventDuration').value=e.DurationSeconds||3600;$('eventPeriod').value=e.PeriodSeconds||86400;
}
function bindNumeric(id,callback) { $(id).onclick=()=>{const input=$(callback.input);if(!input.reportValidity())return;run(()=>callback.save(input.value));}; }
bindNumeric('raidSetTime',{input:'raidSeconds',save:seconds=>mutate({action:'raid_time',seconds},'Raid timer updated.',true,loadRaid)});
$('raidSetBoss').onclick=()=>run(()=>mutate({action:'raid_boss',stage_id:$('raidBoss').value},'Raid boss updated.',true,loadRaid));
$('raidNext').onclick=()=>run(()=>mutate({action:'raid_next'},'Next raid started.',true,loadRaid));$('raidRefresh').onclick=()=>run(()=>loadRaid());
bindNumeric('arenaSetTime',{input:'arenaSeconds',save:seconds=>mutate({action:'arena_time',seconds},'Arena timer updated.',true,loadArena)});
bindNumeric('arenaSetNumber',{input:'arenaNumber',save:season=>mutate({action:'arena_number',season},'Arena season number updated.',true,loadArena)});
$('arenaSetAlly').onclick=()=>run(()=>mutate({action:'arena_ally',featured_ally:$('arenaAlly').value},'Arena featured ally updated.',true,loadArena));
$('arenaEnd').onclick=()=>run(()=>mutate({action:'arena_end'},'Arena season ended.',true,loadArena));$('arenaNext').onclick=()=>run(()=>mutate({action:'arena_next'},'Next Arena season started.',true,loadArena));$('arenaRefresh').onclick=()=>run(()=>loadArena());
$('eventRefresh').onclick=()=>run(()=>loadEvents());$('eventStage').onchange=renderEvent;
$('eventQuick').onsubmit=e=>{e.preventDefault();run(()=>mutate({action:'event_open',stage_id:$('eventStage').value,seconds:$('eventSeconds').value},'Event opened.',true,loadEvents));};
$('eventClose').onclick=()=>run(()=>mutate({action:'event_close',stage_id:$('eventStage').value},'Event closed.',true,loadEvents));
$('eventOpenAll').onclick=()=>run(()=>mutate({action:'event_open_all'},'All playable event dungeons opened. Regular schedule saved.',true,loadEvents));
$('eventRestoreAll').onclick=()=>run(()=>mutate({action:'event_restore_all'},'Regular event schedule restored.',true,loadEvents));
$('eventSchedule').onsubmit=e=>{e.preventDefault();const start=Date.parse($('eventStart').value+'Z')/1000,end=Date.parse($('eventEnd').value+'Z')/1000;run(()=>mutate({action:'event_schedule',stage_id:$('eventStage').value,start_time:start,end_time:end,duration_seconds:$('eventDuration').value,period_seconds:$('eventPeriod').value},'Event schedule saved.',true,loadEvents));};
$('profile').onsubmit=e=>{e.preventDefault();run(()=>mutate({action:'profile',...fields(e.target)},'Profile saved.'));};
$('goods').onsubmit=e=>{e.preventDefault();run(()=>mutate({action:'goods',...fields(e.target)},'Counted inventory updated.'));};
$('profile').elements.level.addEventListener('input',updateXpLimit);
$('connect').onclick=connect;$('refresh').onclick=()=>run(()=>load());$('accounts').onchange=()=>{state.partyPick=null;state.checked.clear();state.account=null;state.entries=[];state.selection=null;render();enabled();run(()=>load());};
$('target').onchange=()=>{$('customRow').hidden=$('target').value!=='custom';connect();};
$('connectionToggle').onclick=()=>$('connectionPanel').open=!$('connectionPanel').open;
$('cancelDelete').onclick=()=>$('confirmDelete').close();
document.querySelectorAll('[data-pane]').forEach(b=>b.onclick=()=>showPane(b.dataset.pane));
for(const id of ['search','grade','sort','heroFilter','rarityFilter'])$(id).addEventListener(id==='search'?'input':'change',render);
for(let grade=1;grade<=Math.max(...catalog.map(e=>e.grade||0));grade++)$('grade').add(new Option(`Grade / tier ${grade}`,grade));
document.querySelectorAll('[data-ui]').forEach(im=>im.src=DATA.ui[im.dataset.ui]);
document.addEventListener('input',e=>{if(e.target.closest('form'))state.dirty=true;});
$('assetEvidence').textContent=`${DATA.verifiedTextureCount} existing game textures · ${catalog.filter(e=>!e.icon).length} unmapped entries use text only`;
// One-click: auto-pick target, restore key, connect without extra steps.
document.addEventListener('DOMContentLoaded',()=>{
  $('target').value=autoTarget();$('customRow').hidden=true;$('key').value=restoreKey();
  $('key').onchange=()=>saveKey($('key').value);enabled();showPane('catalog');
  if ($('key').value) connect(); else { const dlg=$('themeSettings'); if (dlg && !dlg.open) dlg.showModal(); }
  // The account list can move under the page (wipe, fresh register): refresh when returning to the tab.
  const refocus=()=>{if(state.connected&&!state.dirty&&!state.writing&&!state.connecting)connect();};
  window.addEventListener('focus',refocus);document.addEventListener('visibilitychange',()=>{if(document.visibilityState==='visible')refocus();});
});
// Native DrawItemSlotEx groups option counts as 0, 1–2, 3–4, and 5–6.
function rarityIndex(count) { return count===0?0:count<=2?1:count<=4?2:3; }
function rarityName(count) { return ['Gray','Purple','Gold','Red'][rarityIndex(count)]; }
function heroType(e) { return [200,300,400].includes(e.subType)?'knight':[201,301,401].includes(e.subType)?'hunter':'shared'; }
function itemExplanation(e) {
  if(e.kind==='equipment')return `${heroType(e)==='shared'?'Shared hero gear':heroType(e)==='knight'?'Knight gear':'Demon Hunter gear'}. Grade ${e.grade} sets the base item. Border color comes from additional option count.${e.fixedOptions?' Special item: fixed option types.':''}`;
  if(e.kind==='ally-equipment')return 'Ally equipment. Tier and weapon/accessory compatibility come from game data; hero rarity options do not apply.';
  if(e.kind==='materials')return 'Stored as individual material/book instances, not hero equipment.';
  return '';
}
function optionText(o) {
  const id=o.OptionId??o.id, a=o.Value1??o.value1, b=o.Value2??o.value2;
  const definition=DATA.options.find(x=>x.option_id===Number(id));
  let values=definition?.value2_max>0?[b,a]:[a];
  return (definition?.name||`Option ${id}`).replace(/%d/g,()=>values.shift()??0).replace(/%%/g,'%');
}
function allowedOptions(e) { return DATA.options.filter(o=>e.fixedOptions?e.fixedOptions.includes(o.option_id):o['allow_'+e.optionBand]&&o.tier>=1&&o.tier<=e.grade); }
function optionControls(e,grant) {
  if(e.kind!=='equipment'||!capability('item_options')||![200000,300000,400000,700000,800000,900000].includes(e.optionBand))return '';
  const count=e.fixedOptions?.length??e.options?.length??0, max=e.fixedOptions?.length??new Set(allowedOptions(e).map(o=>o.family)).size;
  return `<fieldset class="option-editor"><legend>Rarity & additional stats</legend><label>Stats mode<select name="option_mode"><option value="keep">${grant?'Original game roll':'Keep current stats'}</option><option value="random">Random roll at chosen rarity</option><option value="manual">Choose options & values</option></select></label><label>Border / option count<select name="option_count" ${e.fixedOptions?'disabled':''}>${Array.from({length:7},(_,n)=>`<option value="${n}" ${n>max?'disabled':''} ${n===count?'selected':''}>${rarityName(n)} · ${n} option${n===1?'':'s'}</option>`).join('')}</select></label><div class="option-slots"></div><p class="hint">Grade and rarity are separate. Values stay within original game ranges. Random picks use uniform compatible families.</p></fieldset>`;
}
function bindOptions(form,e) {
  if(!form.elements.option_mode)return;
  const mode=form.elements.option_mode,count=form.elements.option_count,slots=form.querySelector('.option-slots');
  const renderSlots=()=>{
    const manual=mode.value==='manual';
    form.closest('#inspector')?.querySelector('.inspect-art')?.style.setProperty('background-image',`url('${DATA.ui['rarity'+rarityIndex(mode.value==='keep'?(e.options?.length||0):Number(count.value))]}')`);
    count.closest('label').hidden=mode.value==='keep';
    const candidates=allowedOptions(e),used=new Set();
    slots.innerHTML=manual?Array.from({length:Number(count.value)},(_,i)=>{
      const old=e.options?.[i],o=candidates.find(o=>o.option_id===old?.OptionId)||candidates.find(o=>!used.has(o.family));
      if(!o)return '<p>No compatible option family available.</p>';used.add(o.family);
      return `<div class="option-slot"><label>Option ${i+1}<select data-option>${candidates.map(x=>`<option value="${x.option_id}" ${x.option_id===o.option_id?'selected':''}>${escapeHtml(optionRangeText(x))}</option>`).join('')}</select></label><div class="row"><label>${o.value2_max?'Second number':'Amount'}<input data-value1 type="number" min="${o.value1_min}" max="${o.value1_max}" value="${old?.Value1??o.value1_min}" required></label><label ${o.value2_max?'':'hidden'}>First number<input data-value2 type="number" min="${o.value2_min}" max="${o.value2_max}" value="${old?.Value2??o.value2_min}" required></label></div></div>`;
    }).join(''):'';
    slots.querySelectorAll('[data-option]').forEach(select=>select.onchange=()=>{
      const o=candidates.find(o=>o.option_id===Number(select.value)),row=select.closest('.option-slot');
      for(const n of [1,2]){const input=row.querySelector('[data-value'+n+']');input.min=o['value'+n+'_min'];input.max=o['value'+n+'_max'];input.value=input.min;input.parentElement.hidden=n===2&&!o.value2_max;}
      row.querySelector('[data-value1]').parentElement.firstChild.textContent=o.value2_max?'Second number':'Amount';
    });
  };
  mode.onchange=renderSlots;count.onchange=renderSlots;renderSlots();
}
function optionBody(form) {
  const body=fields(form);
  if(form.elements.option_mode){
    body.option_count=form.elements.option_count.value;
    if(body.option_mode==='manual')body.options=Array.from(form.querySelectorAll('.option-slot')).map(row=>({id:row.querySelector('[data-option]').value,value1:row.querySelector('[data-value1]').value,value2:row.querySelector('[data-value2]').value}));
  }
  return body;
}
function updateBulk() {
  $('selectionCount').textContent=`${state.checked.size} selected`;
  $('bulkAction').textContent=state.pane==='catalog'?'Grant selected':'Delete selected';
  $('bulkRarityLabel').hidden=state.pane!=='catalog';
  $('bulkAddToMail').hidden=state.pane!=='catalog';
  $('bulkAction').toggleAttribute('data-protected',!state.checked.size||!capability('batch_inventory'));
  $('bulkAddToMail').toggleAttribute('data-protected',!state.checked.size);
  enabled();
}
$('selectVisible').onclick=()=>{for(const e of state.visible||[])if((state.pane==='catalog'||e.kind!=='consumables')&&state.checked.size<100)state.checked.add(e.key);render();};
$('clearSelection').onclick=()=>{state.checked.clear();render();};
for(let n=0;n<=6;n++)$('bulkRarity').add(new Option(`${rarityName(n)} · ${n} options`,n));
$('bulkAction').onclick=()=>{
  const own=state.pane!=='catalog',entries=(own?state.entries:catalog.map(e=>({...e,key:'catalog:'+e.id}))).filter(e=>state.checked.has(e.key)&&(own||e.kind!=='consumables'));
  if(!entries.length)return;
  const bodies=entries.map(e=>own?{action:e.action+'_delete',number:e.number}:{action:{equipment:'item','ally-equipment':'fellow_item',allies:'fellow',materials:'material'}[e.kind],item_id:e.id,quantity:1,...(e.kind==='equipment'&&$('bulkRarity').value!==''?{option_mode:'random',option_count:e.fixedOptions?.length??Number($('bulkRarity').value)}:{})});
  const save=()=>run(async()=>{await mutate({action:'inventory_batch',entries:bodies},own?'Selected inventory deleted.':'Selected inventory granted.');state.checked.clear();render();});
  if(own){$('deleteDescription').textContent=`Delete ${entries.length} selected assets from account #${selected()}? Any protected asset refuses the entire batch.`;$('confirmDelete').showModal();$('acceptDelete').onclick=()=>{$('confirmDelete').close();save();};}else save();
};

function addMailItem(itemId, count) {
  const id = Number(itemId), amount = Math.max(1, Math.floor(Number(count) || 1));
  const item = masterById.get(id);
  if (!item) return;
  const max = item.kind === 'consumables' ? 2147483647 : 1000;
  const existing = state.mailBasket.find(x => x.item_id === id);
  if (existing) {
    existing.count = Math.min(max, existing.count + amount);
  } else if (state.mailBasket.length < 50) {
    state.mailBasket.push({ item_id: id, count: Math.min(max, amount) });
  }
}
function renderMailBasket() {
  const host = $('mailBasket');
  if (!host) return;
  if (!state.mailBasket.length) {
    host.innerHTML = '<p class="muted">No items queued yet. Add items above or select them in the Item catalog.</p>';
    $('sendMassMail').toggleAttribute('data-protected', true);
    enabled();
    return;
  }
  host.innerHTML = state.mailBasket.map((entry, idx) => {
    const item = masterById.get(entry.item_id) || { name: `Item #${entry.item_id}`, kind: 'consumables' };
    const max = item.kind === 'consumables' ? 2147483647 : 1000;
    return `<div class="mail-basket-row">${art(item)}<strong>${escapeHtml(item.name)}<small>ID ${entry.item_id} · ${escapeHtml(item.kind)}</small></strong><input type="number" min="1" max="${max}" value="${entry.count}" data-mail-qty="${idx}" aria-label="Amount for ${escapeHtml(item.name)}"><button type="button" class="small flat" data-mail-remove="${idx}">Remove</button></div>`;
  }).join('');
  host.querySelectorAll('[data-mail-qty]').forEach(input => {
    input.onchange = () => {
      const idx = Number(input.dataset.mailQty);
      const item = masterById.get(state.mailBasket[idx]?.item_id);
      const max = item?.kind === 'consumables' ? 2147483647 : 1000;
      state.mailBasket[idx].count = Math.max(1, Math.min(max, Math.floor(Number(input.value) || 1)));
      input.value = state.mailBasket[idx].count;
    };
  });
  host.querySelectorAll('[data-mail-remove]').forEach(btn => {
    btn.onclick = () => { state.mailBasket.splice(Number(btn.dataset.mailRemove), 1); renderMailBasket(); };
  });
  $('sendMassMail').toggleAttribute('data-protected', false);
  enabled();
}
if ($('mailCatalogSelect')) {
  const sorted = [...catalog].sort((a, b) => (a.kind === 'consumables' ? -1 : 1) - (b.kind === 'consumables' ? -1 : 1) || a.id - b.id);
  $('mailCatalogSelect').replaceChildren(...sorted.map(e => new Option(`${e.name} (${e.kind}, #${e.id})`, e.id)));
  $('mailCatalogSelect').value = '1900014';
}
$('bulkAddToMail').onclick = () => {
  const entries = catalog.filter(e => state.checked.has('catalog:' + e.id));
  if (!entries.length) return;
  for (const e of entries) addMailItem(e.id, 1);
  state.checked.clear();
  renderMailBasket();
  showPane('broadcast');
  status(`Added ${entries.length} item(s) to Mass Mail. Adjust amounts or title/description, then send.`);
};
$('mailAddItem').onclick = () => {
  addMailItem($('mailCatalogSelect').value, $('mailItemAmount').value);
  renderMailBasket();
};
$('mailClearBasket').onclick = () => { state.mailBasket = []; renderMailBasket(); };
document.querySelectorAll('[data-mail-preset]').forEach(btn => {
  btn.onclick = () => { addMailItem(btn.dataset.mailPreset, btn.dataset.mailAmount); renderMailBasket(); };
});
$('sendMassMail').onclick = () => run(async () => {
  if (!state.mailBasket.length) throw new Error('Add at least one item to send.');
  const res = await request('', {
    action: 'mail_all',
    title: $('mailTitle').value.trim(),
    message: $('mailDescription').value.trim(),
    items: state.mailBasket,
  });
  record(`Mass mail sent to ${res.players} player(s) (${res.total_mails} mail rows).`);
  status(`Sent ${state.mailBasket.length} item(s) to ${res.players} player(s) (${res.mails_per_player} mail(s) each).`);
  state.mailBasket = [];
  renderMailBasket();
});
$('chatNoticeForm').onsubmit = event => {
  event.preventDefault();
  run(async () => {
    const message = $('chatNoticeText').value.trim();
    const channel = Number($('chatNoticeChannel').value || 0), pin_seconds = Number($('chatNoticePin').value || 0);
    await request('', { action: 'chat_notice', message, channel: pin_seconds > 0 ? 0 : channel, pin_seconds });
    $('chatNoticeText').value = '';
    record(`Chat [Notice] sent: ${message}`);
    status(pin_seconds > 0 ? 'Chat [Notice] broadcast and pinned.' : 'Chat [Notice] broadcast.');
    if (pin_seconds > 0) await loadPins();
    await loadNotices();
  });
};

// Pinned notices: the game holds one pinned slot and shows the newest active pin (see ChatRelay.BroadcastNotice).
state.pins = []; state.pinClock = 0; state.pinPreview = null;
const utf8Bytes = text => new TextEncoder().encode(text).length;
const pinLeft = pin => Math.max(0, pin.end_time - (Math.floor(Date.now() / 1000) + state.pinClock));
const leftText = seconds => seconds >= 86400 ? `${Math.floor(seconds / 86400)}d ${Math.floor(seconds % 86400 / 3600)}h`
  : seconds >= 3600 ? `${Math.floor(seconds / 3600)}h ${Math.floor(seconds % 3600 / 60)}m` : `${Math.ceil(seconds / 60)} min`;
function renderChatPreview() {
  const draft = $('chatNoticeText').value.trim(), pinning = Number($('chatNoticePin').value || 0) > 0;
  const editing = state.pinPreview, newest = state.pins[0];
  let pin = null, line = null, hint;
  if (editing) { pin = editing; hint = 'editing a pinned notice: it replaces the pin, no new chat line'; }
  else if (draft) { line = draft; pin = pinning ? draft : newest?.message || null; hint = pinning ? 'new pinned notice: pinned line and a chat line' : 'chat line only'; }
  else { pin = newest?.message || null; hint = pin ? 'current pin in game' : 'nothing pinned right now'; }
  $('chatPreviewPin').hidden = !pin; $('chatPreviewPin').textContent = pin || '';
  $('chatPreviewLine').hidden = !line; $('chatPreviewLine').textContent = line || '';
  // Saved notices as a player gets them after F5 (oldest at the top, the last few lines only), with the one being edited live.
  const saved = state.notices.slice(0, 6).reverse();
  $('chatPreviewSaved').innerHTML = saved.length ? saved.map(n => {
    const edit = state.noticePreview && state.noticePreview.key === n.ids[0];
    return `<p class="chat-preview-notice${edit ? ' editing' : ''}">${escapeHtml(edit ? state.noticePreview.text : n.message)}</p>`;
  }).join('') : `<p class="muted">${state.noticesLoaded ? 'No notices saved in chat history.' : 'Connect to see the notices saved in chat history.'}</p>`;
  if (state.noticePreview && !editing && !draft) hint = 'editing a saved notice: players see the change after F5 or reconnect';
  $('chatPreviewHint').textContent = hint;
  $('chatNoticeChannel').disabled = pinning;
  if (pinning) $('chatNoticeChannel').value = '0';
}
function renderPins() {
  const list = $('pinList');
  if (!state.pins.length) { list.innerHTML = '<p class="muted">No notice is pinned right now.</p>'; renderChatPreview(); return; }
  list.innerHTML = state.pins.map((p, i) => `<form class="pin-card" data-pin="${p.id}"><div class="pin-meta"><b>${i === 0 ? 'Showing in game' : 'Queued behind newer pin'}</b><span>#${p.id} · pinned ${new Date(p.start_time * 1000).toLocaleString()} · ends in <span data-left>${leftText(pinLeft(p))}</span></span></div>`
    + `<label class="wide">Text<textarea name="message" rows="2" maxlength="240" required>${escapeHtml(p.message)}</textarea></label>`
    + `<div class="row"><label>Keep pinned for (minutes from now)<input name="minutes" type="number" min="1" max="10080" value="${Math.max(1, Math.ceil(pinLeft(p) / 60))}"></label>`
    + `<button class="green small" type="submit" data-write>Save</button><button class="small flat" type="button" data-remove data-write>Remove</button></div></form>`).join('');
  list.querySelectorAll('.pin-card').forEach(form => {
    const id = Number(form.dataset.pin), text = form.elements.message;
    const preview = () => { state.pinPreview = text.value.trim() || null; renderChatPreview(); };
    text.addEventListener('input', preview); text.addEventListener('focus', preview);
    text.addEventListener('blur', () => { state.pinPreview = null; renderChatPreview(); });
    form.onsubmit = event => {
      event.preventDefault();
      run(async () => {
        const message = text.value.trim();
        if (utf8Bytes(message) > (state.pinMaxBytes || 511)) throw new Error('This text is too long for the game\'s pinned line.');
        applyPins(await request('', { action: 'pin_update', id, message, pin_seconds: Number(form.elements.minutes.value) * 60 }));
        await loadNotices();
        record(`Pinned notice #${id} updated.`); status(`Pinned notice #${id} saved.`);
      });
    };
    form.querySelector('[data-remove]').onclick = () => run(async () => {
      if (!confirm('Remove this pinned notice? Its chat line stays in chat history.')) return;
      applyPins(await request('', { action: 'pin_remove', id }));
      record(`Pinned notice #${id} removed.`); status(`Pinned notice #${id} removed.`);
    });
  });
  enabled();
  renderChatPreview();
}
function applyPins(reply) {
  state.pins = reply.pins || []; state.pinClock = (reply.now || 0) - Math.floor(Date.now() / 1000); state.pinMaxBytes = reply.max_bytes;
  state.pinPreview = null;
  renderPins();
}
async function loadPins() { if (state.connected) applyPins(await request('', { action: 'pins_list' })); }

// Notices saved in chat history (ChatRelay.SavedNotices): one entry per send, covering every channel group it was saved to.
state.notices = []; state.noticesLoaded = false; state.noticePreview = null;
const groupName = g => `${g >= 1000 ? 'EN' : 'KO'} ${g % 1000 + 1}`;
function renderNotices() {
  const list = $('noticeList');
  if (!state.notices.length) { list.innerHTML = '<p class="muted">No notices saved in chat history.</p>'; renderChatPreview(); return; }
  list.innerHTML = state.notices.map(n => {
    const old = n.position !== 0 && !n.pin_id ? ' · <span class="old">sent pinned before pin tracking; replays as a plain line</span>' : '';
    const pin = n.pin_id ? ` · pin #${n.pin_id}` : '';
    return `<form class="pin-card notice-card" data-key="${n.ids[0]}"><div class="pin-meta"><b>${escapeHtml(n.sender || 'Notice')}</b><span>${new Date(n.created_at * 1000).toLocaleString()} · channels ${n.groups.map(groupName).join(', ')}${pin}${old}</span></div>`
      + `<label class="wide">Text<textarea name="message" rows="2" maxlength="240" required>${escapeHtml(n.message)}</textarea></label>`
      + `<div class="row"><button class="green small" type="submit" data-write>Save</button><button class="small flat" type="button" data-delete data-write>Delete</button></div></form>`;
  }).join('');
  list.querySelectorAll('.notice-card').forEach(form => {
    const notice = state.notices.find(n => n.ids[0] === Number(form.dataset.key)), text = form.elements.message;
    const preview = () => { state.noticePreview = { key: notice.ids[0], text: text.value.trim() }; renderChatPreview(); };
    text.addEventListener('input', preview); text.addEventListener('focus', preview);
    text.addEventListener('blur', () => { state.noticePreview = null; renderChatPreview(); });
    form.onsubmit = event => {
      event.preventDefault();
      run(async () => {
        const message = text.value.trim();
        applyNotices(await request('', { action: 'notice_edit', ids: notice.ids, message }));
        if (notice.pin_id) await loadPins();
        record(`Saved notice edited: ${message}`); status('Notice saved. Players see it after they reload or reconnect.');
      });
    };
    form.querySelector('[data-delete]').onclick = () => run(async () => {
      if (!confirm(`Delete this notice from chat history in ${notice.groups.length} channel(s)?${notice.pin_id ? ' Its pin ends too.' : ''} Players who already have it in an open chat keep it until they reload.`)) return;
      applyNotices(await request('', { action: 'notice_delete', ids: notice.ids }));
      if (notice.pin_id) await loadPins();
      record(`Saved notice deleted: ${notice.message}`); status('Notice deleted from chat history.');
    });
  });
  enabled();
  renderChatPreview();
}
function applyNotices(reply) { state.notices = reply.notices || []; state.noticesLoaded = true; state.noticePreview = null; renderNotices(); }
async function loadNotices() { if (state.connected) applyNotices(await request('', { action: 'notices_list' })); }
$('refreshNotices').onclick = () => run(loadNotices);
$('refreshPins').onclick = () => run(loadPins);
$('chatNoticeText').addEventListener('input', renderChatPreview);
$('chatNoticePin').addEventListener('change', renderChatPreview);
setInterval(() => {
  document.querySelectorAll('.pin-card').forEach(form => {
    const pin = state.pins.find(p => p.id === Number(form.dataset.pin)), left = pin && pinLeft(pin);
    if (pin) form.querySelector('[data-left]').textContent = left > 0 ? leftText(left) : 'ended';
  });
}, 15000);
renderChatPreview();
renderMailBasket();

function eventIcon(e) {
  return /angel/i.test(e.Name)?catalog.find(x=>x.id===110006)?.icon||DATA.ui.allies:/treasure|gold/i.test(e.Name)?DATA.ui.coin:DATA.ui.characters;
}
function durationText(seconds) {
  if(!seconds)return 'Unscheduled';
  return seconds%86400===0?`${seconds/86400} day${seconds===86400?'':'s'}`:seconds%3600===0?`${seconds/3600} hour${seconds===3600?'':'s'}`:seconds%60===0?`${seconds/60} minutes`:`${seconds} seconds`;
}
function renderEventCards() {
  const q=$('eventSearch').value.toLowerCase(),filter=$('eventFilter').value,difficulty=$('eventDifficulty').value;
  const events=state.events.filter(e=>(!q||`${e.Name} ${e.StageId}`.toLowerCase().includes(q))&&(!filter||(filter==='open')===e.Open)&&(!difficulty||e.difficulty===difficulty));
  $('eventList').innerHTML=events.map(e=>`<button class="event-card panel ${String(e.StageId)===$('eventStage').value?'active':''}" data-stage="${e.StageId}">${e.banner?`<img class="event-banner" src="${e.banner}" alt="">`:''}<div><div class="event-card-top"><b>${escapeHtml(e.difficulty||'Event')}</b><span class="event-badge ${e.Open?'is-open':''}">${e.Open?'Open now':'Closed'}</span></div><strong>${escapeHtml(e.Name)}</strong><span>Stamina ${e.stamina??'—'} · Lv.${e.monsterLevel??'—'} · ${durationText(e.DurationSeconds)}</span><small>${(e.rewards||[]).length} possible reward entries · Stage ${e.StageId}</small></div></button>`).join('')||'<p>No events match these filters.</p>';
  $('eventList').querySelectorAll('button').forEach(b=>b.onclick=()=>{$('eventStage').value=b.dataset.stage;renderEvent();});
}
function eventRewards(e) {
  const rewards=(e.rewards||[]).map(r=>{const item=getMaster(r.id);return `<div class="reward-chip">${art(item)}<span><strong>${escapeHtml(item.name)}</strong><small>×${r.minimum===r.maximum?r.minimum:r.minimum+'–'+r.maximum} · ${r.chance}% · group ${r.group}</small></span></div>`;}).join('');
  const r=e.randomReward;
  return `<div class="event-rewards"><h3>Available rewards</h3><p class="muted">Possible drops, not guaranteed. Reward groups roll separately.</p>${rewards||'<p class="muted">No fixed reward entries in game data.</p>'}${r?`<p class="hint">Random equipment: grade ${r.gradeMin}–${r.gradeMax}, ×${r.minimum}–${r.maximum}, ${r.chance}% chance.</p>`:''}</div>`;
}
$('eventDifficulty').onchange=renderEventCards;$('eventSearch').oninput=renderEventCards;$('eventFilter').onchange=renderEventCards;
document.querySelectorAll('[data-seconds]').forEach(b=>b.onclick=()=>{$('eventSeconds').value=b.dataset.seconds;});

function optionRangeText(o) {
  const ranges=o.value2_max?[[o.value2_min,o.value2_max],[o.value1_min,o.value1_max]]:[[o.value1_min,o.value1_max]];
  return o.name.replace(/%d/g,()=>{const r=ranges.shift();return r?`[${r[0]}–${r[1]}]`:'';}).replace(/%%/g,'%');
}
function allyTile(e,compact=false) {
  if(!e)return '<span class="empty-slot">Empty slot<br><small>Drop or select an ally here</small></span>';
  const gear=state.entries.filter(g=>g.action==='fellow_item'&&g.row.FellowNumber===e.number).sort((a,b)=>a.row.EquipIndex-b.row.EquipIndex);
  return `<div class="ally-tile ${compact?'compact':''}">${art(e)}${e.upgrade?`<b class="ally-power">+${e.upgrade}</b>`:''}<div class="ally-summary"><strong>${escapeHtml(e.name)}</strong><span>Lv.${e.row.Level} · ${e.grade}★</span><span>Base ATK ${format(e.attack)}</span><small>${e.busy?'On mission':e.locked?'Locked':'Ready'}</small></div>${compact?'':`<div class="ally-loadout-gear">${[1,2,3].map(slot=>{const g=gear.find(g=>g.row.EquipIndex===slot);return `<span title="${escapeHtml(g?.name||['Weapon','Accessory 1','Accessory 2'][slot-1])}">${g?art(g):`<small>${slot===1?'Weapon':'Accessory '+(slot-1)}</small>`}</span>`;}).join('')}</div>`}</div>`;
}
function renderParty() {
  const setup=Number($('partySetup').value),owned=state.entries.filter(e=>e.action==='fellow');
  const slotsFor=type=>(state.account?.party||[]).filter(p=>p.SetupType===type);
  const tiles=(type,compact)=>Array.from({length:3},(_,position)=>{
    const number=slotsFor(type).find(s=>s.Position===position)?.FellowNumber||0,e=owned.find(e=>e.number===number);
    return `<div class="party-slot ${e?.busy?'busy':''}" data-drop-position="${position}" data-drop-setup="${type}"><span class="slot-label">Slot ${position+1}</span><button type="button" class="slot-pick" data-place="${position}" data-setup="${type}" ${e?.busy?'disabled':''} draggable="${!!e&&!e.busy}" data-ally="${number}">${allyTile(e,compact)}</button>${!compact&&e?`<div class="slot-actions"><button type="button" class="small flat" data-move="${position-1}" data-number="${number}" ${position===0||e.busy?'disabled':''} aria-label="Move ${escapeHtml(e.name)} earlier">↑</button><button type="button" class="small flat" data-move="${position+1}" data-number="${number}" ${position===2||e.busy?'disabled':''} aria-label="Move ${escapeHtml(e.name)} later">↓</button><button type="button" class="small flat" data-unassign="${number}" ${e.busy?'disabled':''}>Remove</button></div>`:''}</div>`;
  }).join('');
  $('party').innerHTML=tiles(3,true);$('partySlots').innerHTML=tiles(setup,false);
  const picked=owned.find(e=>e.number===state.partyPick);
  $('partyHint').textContent=picked?`Selected: ${picked.name}. Choose a slot to assign or swap.`:'Drag an owned ally onto a slot, or select an ally from the list and choose a slot. Changes save immediately.';
  for(const root of [$('party'),$('partySlots')]) {
    bindAllyDrag(root);
    root.querySelectorAll('[data-place]').forEach(b=>b.onclick=()=>{
      if(state.partyPick)placeAlly(state.partyPick,Number(b.dataset.setup),Number(b.dataset.place));
      else if(Number(b.dataset.ally)){state.partyPick=Number(b.dataset.ally);state.selection='fellow:'+b.dataset.ally;renderParty();}
    });
    root.querySelectorAll('[data-drop-position]').forEach(slot=>{
      slot.ondragover=ev=>{if(!state.writing&&capability('party_swap')){ev.preventDefault();slot.classList.add('drop-hover');}};
      slot.ondragleave=()=>slot.classList.remove('drop-hover');
      slot.ondrop=ev=>{
        ev.preventDefault();slot.classList.remove('drop-hover');
        try{const data=JSON.parse(ev.dataTransfer.getData('application/x-kns-ally'));if(data.user===selected()&&data.generation===state.generation)placeAlly(data.number,Number(slot.dataset.dropSetup),Number(slot.dataset.dropPosition));}catch{}
      };
    });
    root.querySelectorAll('[data-move]').forEach(b=>b.onclick=()=>placeAlly(Number(b.dataset.number),setup,Number(b.dataset.move)));
    root.querySelectorAll('[data-unassign]').forEach(b=>b.onclick=()=>run(()=>mutate({action:'fellow_slot',number:Number(b.dataset.unassign),setup,position:-1},'Ally removed from this party.')));
  }
}
let suppressedAllyClick=null;
document.addEventListener('click',ev=>{if(suppressedAllyClick&&Date.now()<suppressedAllyClick.until&&ev.composedPath().includes(suppressedAllyClick.button)){ev.preventDefault();ev.stopImmediatePropagation();}},true);
function bindAllyDrag(root) {
  root.querySelectorAll('[draggable=true]').forEach(b=>{
    const number=Number(b.dataset.ally||(b.dataset.entry||'').split(':')[1]);
    if(!state.entries.some(e=>e.action==='fellow'&&e.number===number))return;
    // Pointer capture keeps the same drag active across nested scrolling containers and touch input.
    b.ondragstart=ev=>ev.preventDefault();
    b.style.touchAction='none';
    let drag=null;
    const clear=()=>document.querySelectorAll('.drop-hover').forEach(e=>e.classList.remove('drop-hover'));
    b.onpointerdown=ev=>{
      if(ev.button!==0||state.writing||!capability('party_swap'))return;
      ev.preventDefault();drag={id:ev.pointerId,x:ev.clientX,y:ev.clientY,user:selected(),generation:state.generation,moved:false};b.setPointerCapture(ev.pointerId);
    };
    b.onpointermove=ev=>{
      if(!drag||drag.id!==ev.pointerId)return;
      if(Math.hypot(ev.clientX-drag.x,ev.clientY-drag.y)>6)drag.moved=true;
      if(!drag.moved)return;
      clear();document.elementFromPoint(ev.clientX,ev.clientY)?.closest('[data-drop-position]')?.classList.add('drop-hover');
    };
    b.onpointerup=ev=>{
      if(!drag||drag.id!==ev.pointerId)return;
      const saved=drag;drag=null;clear();
      if(b.hasPointerCapture(ev.pointerId))b.releasePointerCapture(ev.pointerId);
      if(!saved.moved)return;
      suppressedAllyClick={button:b,until:Date.now()+250};
      const target=document.elementFromPoint(ev.clientX,ev.clientY)?.closest('[data-drop-position]');
      if(target&&saved.user===selected()&&saved.generation===state.generation)placeAlly(number,Number(target.dataset.dropSetup),Number(target.dataset.dropPosition));
    };
    b.onpointercancel=()=>{drag=null;clear();};
  });
}
function placeAlly(number,setup,position) {
  if(!capability('party_swap')){status('Party swapping requires the matching Studio server update.',true);return;}
  const expected=(state.account?.party||[]).find(p=>p.SetupType===setup&&p.Position===position)?.FellowNumber||0;
  if(expected===number){state.partyPick=null;renderParty();return;}
  run(async()=>{await mutate({action:'fellow_swap',number,setup,position,expected_target:expected},'Ally loadout saved.');state.partyPick=null;renderParty();});
}
$('partySetup').onchange=()=>{state.partyPick=null;renderParty();};
