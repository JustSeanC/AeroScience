import {message as localizedCopy,setPresentationText} from './i18n.js';
// Shared presentation only. Scientific values, units and provenance remain server-owned.
import {t as textForUI,t,effectiveLocale,onLocaleChange} from './i18n.js';
export function node(tag, text, className) {
  const element = document.createElement(tag);
  if (text !== undefined) setPresentationText(element,text);
  if (className) element.className = className;
  return element;
}
// Leaflet interprets string tooltip content as HTML. Pass an owned text node
// instead; original values and translated copy remain text, including < and $&.
export const presentationTooltip=value=>node('span',value);
export const numeric = (value, digits = 3) => Number.isFinite(value)
  ? value.toLocaleString(effectiveLocale(), {maximumFractionDigits: digits}) : textForUI("localization.unavailableValue");
export const dateTime = value => value ? new Date(value).toLocaleString(effectiveLocale()) : textForUI("localization.unknownValue");
export function bytes(value) {
  if (!Number.isFinite(value)) return textForUI("localization.unknownValue");
  if (value === 0) return '0 B';
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
  const index = Math.min(units.length - 1, Math.max(0, Math.floor(Math.log(Math.abs(value)) / Math.log(1024))));
  return `${numeric(value / 1024 ** index, index ? 2 : 0)} ${units[index]}`;
}
const instruments = {get 'terrain-scanner'(){return textForUI("ui.scientificAccess.messageTerrainElevation");},get terrain(){return textForUI("ui.scientificAccess.messageTerrainElevation");},
  get 'thermal-imager'(){return textForUI("ui.scientificSources.messageThermalImaging");},get thermal(){return textForUI("ui.scientificSources.messageThermalImaging");},
  get 'multispectral-imager'(){return textForUI("ui.scientificSources.messageMultispectralImaging");},get multispectral(){return textForUI("ui.scientificSources.messageMultispectralImaging");},
  get 'hyperspectral-imager'(){return textForUI("ui.workstationUi.messageHyperspectralResearch");},get hyperspectral(){return textForUI("ui.workstationUi.messageHyperspectralResearch");}};
export const readableName = (id, catalog = []) => catalog.find(item => item.id === id)?.name
  || instruments[id] || String(id || textForUI("ui.workstationUi.messageScientificInstrument")).replace(/([a-z])([A-Z])/g,'$1 $2').replaceAll('-', ' ').replace(/\b\w/g, c => c.toUpperCase());
export function link(parent, title, href, className = 'button secondary') {
  const element = node('a', title, className); element.href = href; parent.append(element); return element;
}
export function errorMessage(parent, error, retry) {
  let box = parent.querySelector(':scope > .action-error');
  if (!box) { box = node('div', undefined, 'action-error'); box.setAttribute('role', 'alert'); parent.prepend(box); }
  box.replaceChildren(node('strong', localizedCopy('error.loadingTitle')), node('p',
    error?.message === 'Failed to fetch' ? localizedCopy('error.connectionLost') : error?.message || localizedCopy('error.notConfirmed')));
  if (retry) button(box, localizedCopy('common.tryAgain'), async () => { box.remove(); await retry(); });
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
  const label = node('label', title), control = node('select'); setPresentationText(control,title,"aria-label");
  for (const [id, text] of options) { const option = node('option', text); option.value = id; control.append(option); }
  if (value !== undefined && [...control.options].some(option=>option.value===String(value))) control.value = value; label.append(control); parent.append(label); return control;
}
export function disclosure(parent, title, text) {
  const details = node('details', undefined, 'advanced-details'); details.append(node('summary', title));
  if (text) details.append(node('p', text)); parent.append(details); return details;
}
export function status(parent, title = t('common.loadingSavedResults')) {
  const element = node('p', title, 'workspace-status'); element.setAttribute('role', 'status');
  element.setAttribute('aria-live', 'polite'); element.setAttribute('aria-atomic', 'true'); parent.append(element); return element;
}
export function metric(parent, title, value, detail) {
  const card = node('article', undefined, 'finding-card'); card.append(node('span', title, 'finding-label'), node('strong', value));
  if (detail) card.append(node('p', detail)); parent.append(card); return card;
}
export function table(parent, title, headings, rows, {pageSize = 25, onSelect} = {}) {
  const container = node('section', undefined, 'data-table-section'); setPresentationText(container,title,"aria-label");
  const wrap = node('div', undefined, 'data-table-scroll'), grid = node('table'), caption = node('caption', title);
  wrap.tabIndex=0;wrap.setAttribute('role','group');setPresentationText(wrap,localizedCopy('localization.scrollableTable',{title}),"aria-label");
  grid.append(caption); const head = node('thead'), header = node('tr');
  for (const title of headings) { const cell = node('th', title); cell.scope = 'col'; header.append(cell); }
  if (onSelect) { const cell = node('th', localizedCopy("localization.inspect")); cell.scope = 'col'; header.append(cell); }
  head.append(header); grid.append(head); const body = node('tbody'); grid.append(body); wrap.append(grid);
  const tools = node('div', undefined, 'table-pagination'), summary = node('span');
  let page = 0;
  const previous = button(tools, localizedCopy("localization.previousValues"), () => { page--; render(); });
  tools.append(summary); const next = button(tools, localizedCopy("localization.nextValues"), () => { page++; render(); });
  const render = () => { body.replaceChildren(); const start = page * pageSize;
    for (const [offset, row] of rows.slice(start, start + pageSize).entries()) {
      const tr = node('tr'); for (const value of row) { const cell = node('td'); if (value instanceof Node) cell.append(value); else setPresentationText(cell,value ?? localizedCopy("localization.unavailableValue")); tr.append(cell); }
      if (onSelect) { const td = node('td'); button(td, localizedCopy("ui.workstationUi.messageInspectSample",{value1:(String(start + offset + 1))}), () => onSelect(start + offset)); tr.append(td); } body.append(tr);
    }
    if (!rows.length) { const tr = node('tr'), td = node('td', localizedCopy("localization.noValidRecordedValues")); td.colSpan = headings.length; tr.append(td); body.append(tr); }
    setPresentationText(summary,rows.length ? localizedCopy("ui.workstationUi.messageOfDisplayedValues",{value1:(String(start + 1)),value2:(String(Math.min(rows.length, start + pageSize))),value3:(String(rows.length))}) : localizedCopy("localization.noRecordedValues"));
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
  if (!Number.isFinite(low) || !Number.isFinite(high)) { parent.append(node('p', localizedCopy("ui.workstationUi.messageNoValidRecordedValuesToScale"))); return; }
  const swatch = node('div', undefined, 'legend-swatch'); swatch.setAttribute('aria-hidden', 'true');
  if (low === high) { swatch.style.background = color(low, low, high); parent.append(swatch, node('p', localizedCopy("ui.workstationUi.messageConstantValueNoNumericGradient",{value1:(String(numeric(low))),value2:(String(unit))}))); }
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
export function chart(parent, title, datasets, {xLabel = textForUI("ui.workstationUi.messageRecordedPosition"), yLabel = textForUI("ui.scienceAnalysis.messageValue"), type = 'line', categories,onSelect,xTick} = {}) {
  const holder = node('figure', undefined, 'scientific-chart'), caption = node('figcaption', title), canvas = node('canvas');
  canvas.setAttribute('role', 'img'); setPresentationText(canvas,title,"aria-label"); holder.append(caption, canvas); parent.append(holder);
  let instance;const ink=getComputedStyle(parent).getPropertyValue('--text').trim()||'#c9d6e4',muted=getComputedStyle(parent).getPropertyValue('--muted').trim()||'#a6bbce';
  if (window.Chart) instance = new window.Chart(canvas, {type, data:{datasets,labels:categories}, options:{animation:false,maintainAspectRatio:false,parsing:false,
    onClick:onSelect?(_event,elements)=>{const hit=elements[0];if(hit)onSelect(datasets[hit.datasetIndex].data[hit.index]);}:undefined,
    spanGaps:false,plugins:{legend:{labels:{color:ink}}},scales:{x:{type:categories?'category':'linear',title:{display:true,text:xLabel,color:ink},ticks:{color:muted,callback:xTick}},y:{title:{display:true,text:yLabel,color:ink},ticks:{color:muted}}}}});
  const observer = new MutationObserver(() => { if (!holder.isConnected) { instance?.destroy(); observer.disconnect(); } });
  observer.observe(document.body, {childList:true,subtree:true});holder.disposeChart=()=>{instance?.destroy();observer.disconnect();};return holder;
}

// randomUUID is limited to secure contexts; paired LAN HTTP clients still expose
// cryptographic getRandomValues. These IDs identify requests/plans, not credentials.
export function newIdentity(){
 const provider=globalThis.crypto;
 if(typeof provider?.randomUUID==='function')return provider.randomUUID();
 if(typeof provider?.getRandomValues!=='function')throw new Error(textForUI("ui.workstationUi.messageThisBrowserCannotGenerateASafeRequest"));
 const bytes=provider.getRandomValues(new Uint8Array(16));bytes[6]=(bytes[6]&15)|64;bytes[8]=(bytes[8]&63)|128;
 const value=Array.from(bytes,n=>n.toString(16).padStart(2,'0')).join('');
 return `${value.slice(0,8)}-${value.slice(8,12)}-${value.slice(12,16)}-${value.slice(16,20)}-${value.slice(20)}`;
}

// Three presentation levels; callers use authoritative state, never prose matching
// or a new scientific evaluator. Critical notices must explain the next action.
export function scientificNotice(element,level,{problem='',consequence='',action=''}={}){
 const value=[problem,consequence,action].filter(Boolean).join(' ');
 element.classList.add('science-notice');if(element.dataset.scienceLevel!==level)element.dataset.scienceLevel=level;
 const role=level==='critical'?'alert':level==='important'?'status':'note';if(element.getAttribute('role')!==role)element.setAttribute('role',role);
 element.hidden=!value;if(element.textContent!==value)setPresentationText(element,value);
}
export function informationDetails(parent,title,...content){
 const details=disclosure(parent,title);details.dataset.scienceLevel='information';details.classList.add('science-information');details.append(...content);return details;
}
export function revealDetails(element){
 for(let parent=element?.parentElement;parent;parent=parent.parentElement)if(parent.tagName==='DETAILS')parent.open=true;
}

// Additive primitives: existing scientific renderers and command handlers keep
// ownership. Callers supply confirmed meaning; colors do not determine validity.
export function statusPill(parent,title,{tone='neutral',live=false}={}) {
  const element=node('span',title,'status-pill');
  element.dataset.tone=['neutral','warning','danger'].includes(tone)?tone:'neutral';
  if(live){element.setAttribute('role','status');element.setAttribute('aria-atomic','true');}
  parent.append(element);return element;
}
export function emptyState(parent,{title=t('components.emptyTitle'),description='',action}={}) {
  const element=node('section',undefined,'empty-state');setPresentationText(element,title,"aria-label");
  element.append(node('h2',title));if(description)element.append(node('p',description));
  if(action)nextAction(element,action.title,action.run||action.href);
  parent.append(element);return element;
}
export function nextAction(parent,title,runOrHref) {
  const element=node('div',undefined,'next-action');parent.append(element);
  return typeof runOrHref==='string'?link(element,title,runOrHref,'button primary'):button(element,title,runOrHref,'primary');
}
export function loadingState(parent,title=t('common.loading')) {
  const element=status(parent,title);element.classList.add('loading-state');element.setAttribute('aria-busy','true');return element;
}
let fieldSequence=0;
export function formField(parent,title,control=node('input'),{help='',error=''}={}) {
  const element=node('div',undefined,'form-field'),label=node('label',title);
  if(!control.id)control.id='as-field-'+(++fieldSequence);
  label.htmlFor=control.id;const description=node('p',help,'form-field-help'),feedback=node('p',undefined,'form-field-error');
  description.id=control.id+'-help';feedback.id=control.id+'-error';feedback.setAttribute('aria-live','polite');
  const described=[control.getAttribute('aria-describedby'),help?description.id:null,feedback.id].filter(Boolean);
  control.setAttribute('aria-describedby',described.join(' '));element.append(label,control);
  if(help)element.append(description);element.append(feedback);parent.append(element);
  const setError=message=>{setPresentationText(feedback,message);control.setAttribute('aria-invalid',String(!!message));};
  setError(error);return {element,control,label,setError};
}
export function sourceBadge(parent,category) {
  const known=['simulator','historical','modeled','pilot','unavailable','synthetic'];
  const kind=known.includes(category)?category:'unavailable';
  const badge=statusPill(parent,localizedCopy('source.'+kind));badge.classList.add('source-badge');badge.dataset.sourceCategory=kind;return badge;
}
export function evidenceBadge(parent,assessment) {
  const known=['valid','invalid','partial','pending','unassessed','unavailable'];
  const state=known.includes(assessment)?assessment:'unassessed';
  const badge=statusPill(parent,localizedCopy('evidence.'+state),{tone:state==='invalid'?'danger':['partial','unavailable'].includes(state)?'warning':'neutral'});
  badge.classList.add('evidence-badge');badge.dataset.evidenceState=state;return badge;
}
export function notification(parent,message,{critical=false}={}) {
  const element=node('p',message,'workspace-notification');element.setAttribute('role',critical?'alert':'status');
  element.setAttribute('aria-atomic','true');parent.append(element);return element;
}
