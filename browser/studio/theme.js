/* Studio appearance controls. Color preferences are local to this browser. */
document.addEventListener('DOMContentLoaded', () => {
  const navigation = document.querySelector('.left nav');
  const formerNavigationPanel = navigation.parentElement;
  const bar = document.createElement('div');
  bar.className = 'studio-navigation';
  const settings = document.createElement('button');
  settings.type = 'button'; settings.className = 'studio-settings';
  settings.textContent = 'Settings'; settings.setAttribute('aria-haspopup', 'dialog');
  settings.setAttribute('aria-controls', 'themeSettings');
  const account = document.createElement('div'); account.className = 'studio-account';
  account.append(document.querySelector('#accounts'), document.querySelector('#refresh'));
  account.querySelector('#refresh').textContent = '↻';
  account.querySelector('#refresh').setAttribute('aria-label', 'Reload account');
  account.querySelector('#refresh').title = 'Reload account';
  bar.append(navigation, account, settings);
  formerNavigationPanel.hidden = true;
  document.querySelector('#site-header').after(bar);
  const controls = document.createElement('dialog');
  controls.id = 'themeSettings'; controls.className = 'theme-settings';
  controls.setAttribute('aria-labelledby', 'themeSettingsTitle');
  controls.innerHTML = '<div class="theme-heading"><div><h2 id="themeSettingsTitle">Appearance</h2><p>Change Studio colors live.</p></div><button type="button" class="flat small" aria-label="Close settings">Close</button></div><div class="palette"></div><p class="draft-caption">Your color choices stay in this browser.</p>';
  document.body.append(controls);
  const connection = document.querySelector('#connectionPanel');
  controls.append(connection);
  connection.open = true;
  document.querySelector('#connectionToggle').hidden = true;
  document.querySelector('.workspace > .left').hidden = true;
  const inventory = document.querySelector('.inventory');
  const toolbar = document.createElement('div'); toolbar.className = 'inventory-toolbar';
  inventory.prepend(toolbar);
  toolbar.append(document.querySelector('.filters'), document.querySelector('#subtabs'), document.querySelector('.bulkbar'));
  for (const [id, icon, label] of [['selectVisible','☑','Select visible'],['clearSelection','×','Clear selection']]) {
    const button = document.querySelector('#'+id); button.textContent = icon;
    button.title = label; button.setAttribute('aria-label',label);
  }
  document.querySelector('#search').placeholder = 'Search items…';
  const help = document.querySelector('.bulkbar p');
  help.id = 'selectionHelp'; help.textContent = 'Shift-click adds; Ctrl-click removes. Up to 100 entries. Protected inventory cannot be deleted.';
  toolbar.title = help.textContent;
  document.querySelector('#grid').setAttribute('aria-describedby','selectionHelp');
  settings.onclick = () => controls.showModal();
  controls.querySelector('[aria-label="Close settings"]').onclick = () => controls.close();
  controls.addEventListener('click', event => {
    if (event.target !== controls) return;
    const bounds = controls.getBoundingClientRect();
    if (event.clientX < bounds.left || event.clientX > bounds.right || event.clientY < bounds.top || event.clientY > bounds.bottom) controls.close();
  });
  const defaults = {page:'#24170f',panel:'#784124',card:'#61371f',input:'#342013',accent:'#86ad29',border:'#c88b46',text:'#ffedc6'};
  const presets = {
    Classic: defaults,
    Charcoal: {page:'#171918',panel:'#2c302b',card:'#232721',input:'#171c17',accent:'#648d32',border:'#a58c59',text:'#f5eedc'},
    Parchment: {page:'#4a3321',panel:'#e2cba0',card:'#f2dfb8',input:'#f7e8ca',accent:'#7d9f32',border:'#aa773d',text:'#342315'}
  };
  let colors = {...defaults};
  try {
    const saved = JSON.parse(localStorage.getItem('knsclassic-studio-design-colors-v1'));
    for (const key in defaults) if (/^#[0-9a-f]{6}$/i.test(saved?.[key])) colors[key] = saved[key];
  } catch {}
  function apply() {
    for (const [key,value] of Object.entries(colors)) document.documentElement.style.setProperty('--draft-'+key,value);
    try { localStorage.setItem('knsclassic-studio-design-colors-v1',JSON.stringify(colors)); } catch {}
    for (const picker of controls.querySelectorAll('input[data-color]')) picker.value = colors[picker.dataset.color];
  }
  for (const [key,label] of Object.entries({page:'Page background',panel:'Panel background',card:'Card background',input:'Field background',accent:'Active controls',border:'Borders',text:'Text'})) {
    const wrapper = document.createElement('label'); wrapper.textContent = label;
    const picker = document.createElement('input'); picker.type = 'color'; picker.dataset.color = key;
    picker.setAttribute('aria-label',label); picker.value = colors[key];
    picker.oninput = () => { colors[key] = picker.value; apply(); };
    wrapper.append(picker); controls.querySelector('.palette').append(wrapper);
  }
  for (const [label,preset] of Object.entries(presets)) {
    const button = document.createElement('button'); button.type = 'button'; button.className = 'small flat'; button.textContent = label;
    button.onclick = () => { colors = {...preset}; apply(); };
    controls.querySelector('.palette').append(button);
  }
  apply();
  document.querySelector('#bulkRarity').setAttribute('aria-label', 'Grant options');
});
