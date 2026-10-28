// Shared presentation only. Scientific values, units and provenance remain server-owned.
export function node(tag, text, className) {
  const element = document.createElement(tag);
  if (text !== undefined) element.textContent = text;
  if (className) element.className = className;
  return element;
}
export const numeric = (value, digits = 3) => Number.isFinite(value)
  ? value.toLocaleString(undefined, {maximumFractionDigits: digits}) : 'Unavailable';
export const dateTime = value => value ? new Date(value).toLocaleString() : 'Unknown';
export function bytes(value) {
  if (!Number.isFinite(value)) return 'Unknown';
  if (value === 0) return '0 B';
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
  const index = Math.min(units.length - 1, Math.max(0, Math.floor(Math.log(Math.abs(value)) / Math.log(1024))));
  return `${numeric(value / 1024 ** index, index ? 2 : 0)} ${units[index]}`;
}
const instruments = {'terrain-scanner':'Terrain / Elevation',terrain:'Terrain / Elevation',
  'thermal-imager':'Thermal Imaging',thermal:'Thermal Imaging',
  'multispectral-imager':'Multispectral Imaging',multispectral:'Multispectral Imaging',
  'hyperspectral-imager':'Hyperspectral Research',hyperspectral:'Hyperspectral Research'};
export const readableName = (id, catalog = []) => catalog.find(item => item.id === id)?.name
  || instruments[id] || String(id || 'Scientific instrument').replace(/([a-z])([A-Z])/g,'$1 $2').replaceAll('-', ' ').replace(/\b\w/g, c => c.toUpperCase());
export function link(parent, title, href, className = 'button secondary') {
  const element = node('a', title, className); element.href = href; parent.append(element); return element;
}
export function errorMessage(parent, error, retry) {
  let box = parent.querySelector(':scope > .action-error');
  if (!box) { box = node('div', undefined, 'action-error'); box.setAttribute('role', 'alert'); parent.prepend(box); }
  box.replaceChildren(node('strong', 'This view could not finish loading.'), node('p',
    error?.message === 'Failed to fetch' ? 'The companion connection was lost. Your saved data has not been deleted. Reconnect to the companion and try again.' : error?.message || 'The operation was not confirmed. Your saved records remain available.'));
  if (retry) button(box, 'Try again', async () => { box.remove(); await retry(); });
  return box;
}
export function button(parent, title, run, className = 'secondary') {
  const element = node('button', title, className); element.type = 'button';
  element.onclick = async () => { if (element.disabled || element.getAttribute('aria-busy') === 'true') return; element.setAttribute('aria-busy', 'true');
    try { await run(); } catch (error) { if (error.name !== 'AbortError' && error.message !== 'View changed.') errorMessage(parent, error); }
    finally { element.removeAttribute('aria-busy'); } };
  parent.append(element); return element;
}
export function select(parent, title, options, value) {
  const label = node('label', title), control = node('select'); control.setAttribute('aria-label', title);
  for (const [id, text] of options) { const option = node('option', text); option.value = id; control.append(option); }
  if (value !== undefined && [...control.options].some(option=>option.value===String(value))) control.value = value; label.append(control); parent.append(label); return control;
}
export function disclosure(parent, title, text) {
  const details = node('details', undefined, 'advanced-details'); details.append(node('summary', title));
  if (text) details.append(node('p', text)); parent.append(details); return details;
}
export function status(parent, title = 'Loading saved results…') {
  const element = node('p', title, 'workspace-status'); element.setAttribute('role', 'status');
  element.setAttribute('aria-live', 'polite'); element.setAttribute('aria-atomic', 'true'); parent.append(element); return element;
}
export function metric(parent, title, value, detail) {
  const card = node('article', undefined, 'finding-card'); card.append(node('span', title, 'finding-label'), node('strong', value));
  if (detail) card.append(node('p', detail)); parent.append(card); return card;
}
export function table(parent, title, headings, rows, {pageSize = 25, onSelect} = {}) {
  const container = node('section', undefined, 'data-table-section'); container.setAttribute('aria-label', title);
  const wrap = node('div', undefined, 'data-table-scroll'), grid = node('table'), caption = node('caption', title);
  wrap.tabIndex=0;wrap.setAttribute('role','group');wrap.setAttribute('aria-label',title+' — scrollable table');
  grid.append(caption); const head = node('thead'), header = node('tr');
  for (const title of headings) { const cell = node('th', title); cell.scope = 'col'; header.append(cell); }
  if (onSelect) { const cell = node('th', 'Inspect'); cell.scope = 'col'; header.append(cell); }
  head.append(header); grid.append(head); const body = node('tbody'); grid.append(body); wrap.append(grid);
  const tools = node('div', undefined, 'table-pagination'), summary = node('span');
  let page = 0;
  const previous = button(tools, 'Previous values', () => { page--; render(); });
  tools.append(summary); const next = button(tools, 'Next values', () => { page++; render(); });
  const render = () => { body.replaceChildren(); const start = page * pageSize;
    for (const [offset, row] of rows.slice(start, start + pageSize).entries()) {
      const tr = node('tr'); for (const value of row) { const cell = node('td'); if (value instanceof Node) cell.append(value); else cell.textContent = value ?? 'Unavailable'; tr.append(cell); }
      if (onSelect) { const td = node('td'); button(td, `Inspect sample ${start + offset + 1}`, () => onSelect(start + offset)); tr.append(td); } body.append(tr);
    }
    if (!rows.length) { const tr = node('tr'), td = node('td', 'No valid recorded values in this selection.'); td.colSpan = headings.length; tr.append(td); body.append(tr); }
    summary.textContent = rows.length ? `${start + 1}–${Math.min(rows.length, start + pageSize)} of ${rows.length} displayed values` : 'No recorded values';
    previous.disabled = page === 0; next.disabled = start + pageSize >= rows.length; tools.hidden = rows.length <= pageSize;
  };
  container.append(wrap, tools); parent.append(container); render(); return container;
}
// Perceptually ordered viridis stops. The map legend uses this exact interpolation.
const ramp = [[68,1,84],[59,82,139],[33,145,140],[94,201,98],[253,231,37]];
export function color(value, low, high) {
  const t = high === low ? .5 : Math.max(0, Math.min(1, (value - low) / (high - low)));
  const position = t * (ramp.length - 1), index = Math.min(ramp.length - 2, Math.floor(position)), f = position - index;
  return `rgb(${ramp[index].map((v, i) => Math.round(v + (ramp[index + 1][i] - v) * f)).join(',')})`;
}
export function legend(parent, title, low, high, unit, context = '') {
  parent.replaceChildren(node('strong', title)); parent.classList.add('scientific-legend');
  if (!Number.isFinite(low) || !Number.isFinite(high)) { parent.append(node('p', 'No valid recorded values to scale.')); return; }
  const swatch = node('div', undefined, 'legend-swatch'); swatch.setAttribute('aria-hidden', 'true');
  if (low === high) { swatch.style.background = color(low, low, high); parent.append(swatch, node('p', `Constant value: ${numeric(low)} ${unit}. No numeric gradient.`)); }
  else { swatch.style.background = 'linear-gradient(90deg,' + ramp.map(v => `rgb(${v.join(',')})`).join(',') + ')'; parent.append(swatch, node('p', `${numeric(low)} → ${numeric(high)} ${unit}`)); }
  if (context) parent.append(node('small', context));
}
export function pointInCell(point, feature) {
  const ring = feature.geometry.coordinates[0]; let inside = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const [xi, yi] = ring[i], [xj, yj] = ring[j];
    if ((yi > point.lat) !== (yj > point.lat) && point.lng < (xj - xi) * (point.lat - yi) / (yj - yi) + xi) inside = !inside;
  } return inside;
}
export function chart(parent, title, datasets, {xLabel = 'Recorded position', yLabel = 'Value', type = 'line', categories,onSelect,xTick} = {}) {
  const holder = node('figure', undefined, 'scientific-chart'), caption = node('figcaption', title), canvas = node('canvas');
  canvas.setAttribute('role', 'img'); canvas.setAttribute('aria-label', title); holder.append(caption, canvas); parent.append(holder);
  let instance;
  if (window.Chart) instance = new window.Chart(canvas, {type, data:{datasets,labels:categories}, options:{animation:false,maintainAspectRatio:false,parsing:false,
    onClick:onSelect?(_event,elements)=>{const hit=elements[0];if(hit)onSelect(datasets[hit.datasetIndex].data[hit.index]);}:undefined,
    spanGaps:false,plugins:{legend:{labels:{color:'#c9d6e4'}}},scales:{x:{type:categories?'category':'linear',title:{display:true,text:xLabel,color:'#c9d6e4'},ticks:{color:'#a6bbce',callback:xTick}},y:{title:{display:true,text:yLabel,color:'#c9d6e4'},ticks:{color:'#a6bbce'}}}}});
  const observer = new MutationObserver(() => { if (!holder.isConnected) { instance?.destroy(); observer.disconnect(); } });
  observer.observe(document.body, {childList:true,subtree:true}); return holder;
}

// randomUUID is limited to secure contexts; paired LAN HTTP clients still expose
// cryptographic getRandomValues. These IDs identify requests/plans, not credentials.
export function newIdentity(){
 const provider=globalThis.crypto;
 if(typeof provider?.randomUUID==='function')return provider.randomUUID();
 if(typeof provider?.getRandomValues!=='function')throw new Error('This browser cannot generate a safe request identity. Use a current browser.');
 const bytes=provider.getRandomValues(new Uint8Array(16));bytes[6]=(bytes[6]&15)|64;bytes[8]=(bytes[8]&63)|128;
 const value=Array.from(bytes,n=>n.toString(16).padStart(2,'0')).join('');
 return `${value.slice(0,8)}-${value.slice(8,12)}-${value.slice(12,16)}-${value.slice(16,20)}-${value.slice(20)}`;
}
