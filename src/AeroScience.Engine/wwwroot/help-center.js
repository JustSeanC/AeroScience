import {select,link,disclosure} from './workstation-ui.js';
const node=(tag,text)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;return n;};
const guides=[['v0.4.2-pilot-guide.md','First mission & results'],['v0.4.2-science-visualization.md','Reading scientific maps'],['v0.4.1-pilot-guide.md','Getting started'],['mission-planner.md','Mission planning'],['remote-sensing/index.md','Instruments & scientific data'],['v0.4.1-science-workspace.md','Science Workspace'],['v0.4.1-scientific-comparison.md','Comparisons'],['v0.4.1-scientific-downlink.md','Preparation & downlink'],['v0.4.1-windows-testing.md','Troubleshooting & testing']];
guides.push(['scientific-library.md','Browse & download scientific data']);
guides.push(['remote-sensing/hyperspectral.md','NASA hyperspectral assistant']);
guides.push(['README.md','Documentation index']);
guides.push(['instrument-calibration.md','Calibration & provenance (development)']);
export function documentationUrl(target,guide){
 const source=guide.startsWith('_root/')?'/'+guide.slice(6):'/docs/'+guide;
 const url=new URL(target,location.origin+source);
 if(url.origin!==location.origin)return ['https:','http:'].includes(url.protocol)?url.href:null;
 let local;
 if(url.pathname.startsWith('/help/'))local=url.pathname.slice(6);
 else if(url.pathname.startsWith('/docs/'))local=url.pathname.slice(6);
 else if(['/README.md','/INSTALL.md','/CHANGELOG.md','/THIRD-PARTY-NOTICES.md','/LICENSE'].includes(url.pathname)||url.pathname.startsWith('/licenses/'))local='_root/'+url.pathname.slice(1);
 if(local){
  if(local==='_root/LICENSE')local+='.txt';
  return local.endsWith('.md')?'#help?guide='+encodeURIComponent(local)+(url.hash?'&anchor='+encodeURIComponent(url.hash.slice(1)):''):'/help/'+local+url.hash;
 }
 // Code/branding references remain source links rather than nonexistent offline files.
 return 'https://github.com/JustSeanC/AeroScience/blob/develop/v0.5.0'+url.pathname+url.hash;
}
export function markdown(parent,source,guide){
 const toc=node('nav');toc.setAttribute('aria-label','In this guide');parent.append(toc);let code=null,list=null,table=null;
 function inline(target,value){const regex=/\[([^\]]+)\]\(([^)]+)\)|\*\*([^*]+)\*\*|`([^`]+)`/g;let last=0;for(const m of value.matchAll(regex)){target.append(document.createTextNode(value.slice(last,m.index)));if(m[1]){const a=node('a',m[1]);try{const href=documentationUrl(m[2],guide);if(href){a.href=href;if(!href.startsWith('#help?')){a.target='_blank';a.rel='noopener noreferrer';}}}catch{}target.append(a);}else target.append(node(m[3]?'strong':'code',m[3]||m[4]));last=m.index+m[0].length;}target.append(document.createTextNode(value.slice(last)));}
 for(const line of source.split(/\r?\n/)){
  if(line.startsWith('```')){if(code)code=null;else{const pre=node('pre');code=node('code');pre.append(code);parent.append(pre);}continue;}
  if(code){code.append(document.createTextNode(line+'\n'));continue;}
  if(!line.trim()){list=null;table=null;continue;}
  const heading=/^(#{1,6})\s+(.*)/.exec(line);if(heading){const h=node('h'+Math.min(heading[1].length+1,6));h.id=heading[2].toLowerCase().replace(/[^a-z0-9\s-]/g,'').trim().replace(/\s+/g,'-');inline(h,heading[2]);parent.append(h);if(heading[1].length<=2){const a=node('a',heading[2]);a.href='#help?guide='+encodeURIComponent(guide)+'&anchor='+h.id;toc.append(a);}list=table=null;continue;}
  if(/^\|/.test(line)){if(/^\|[\s:|-]+\|$/.test(line))continue;if(!table){table=node('table');parent.append(table);}const tr=node('tr');for(const cell of line.replace(/^\||\|$/g,'').split('|')){const td=node(table.rows.length?'td':'th');inline(td,cell.trim());tr.append(td);}table.append(tr);continue;}
  const item=/^\s*(?:[-*]|\d+\.)\s+(.*)/.exec(line);if(item){if(!list){list=node(/^\s*\d/.test(line)?'ol':'ul');parent.append(list);}const li=node('li');inline(li,item[1]);list.append(li);continue;}
  const p=node('p');inline(p,line);parent.append(p);list=null;
 }
}
let generation=0,controller;
export function disposeHelpCenter(){generation++;controller?.abort();}
const tasks=[
 ['How do I find and import NASA AVIRIS data?','remote-sensing/hyperspectral.md','nasa aviris classic ng emit earthdata authorization hyperspectral header binary checksum official browser guided import'],
 ['How do I download and reuse scientific data without a flight?','scientific-library.md','library catalog scenes source date bands polygon region download approval queue offline reuse'],
 ['How do I create my first mission?','v0.4.2-pilot-guide.md','plan select start first flight'],
 ['How do I configure scientific instruments?','remote-sensing/index.md','instruments payload source thermal terrain multispectral hyperspectral'],
 ['Why is my scientific data still downloading?','v0.4.2-pilot-guide.md','preparation download partial offline allowance budget'],
 ['What does this measurement represent?','v0.4.2-science-visualization.md','units temperature elevation NDVI source date quality'],
 ['How do I view my results?','v0.4.2-pilot-guide.md','review science map analysis history'],
 ['How do I compare two missions?','v0.4.2-science-visualization.md','comparison shared scales difference overlap compatible'],
 ['How do I export scientific results?','v0.4.2-pilot-guide.md','export CSV JSON GeoTIFF research zip'],
 ['Why is a dataset incomplete?','v0.4.2-science-visualization.md','missing nodata clouds gaps quality masks coverage'],
 ['How does post-flight downlink work?','v0.4.1-scientific-downlink.md','recovery reconstruction processing provisional'],
 ['How do I collect logs or get support?','v0.4.2-pilot-guide.md','feedback troubleshooting Discord GitHub errors']
];
export async function helpCenter(parent,api){
 disposeHelpCenter();const own=generation;controller=new AbortController();
 parent.replaceChildren(node('div'));parent.firstChild.className='page-heading';parent.firstChild.append(node('h1','Help & Support'));
 const searchLabel=node('label','Find a task or question'),search=node('input');search.type='search';search.placeholder='Try “missing data”, “compare”, or “first mission”';search.setAttribute('aria-label','Search Help topics');searchLabel.className='help-search';searchLabel.append(search);parent.append(searchLabel);
 const topicGrid=node('nav');topicGrid.className='help-tasks';topicGrid.setAttribute('aria-label','Help by task');parent.append(topicGrid);
 function topics(){topicGrid.replaceChildren();const query=search.value.trim().toLowerCase();for(const [title,path,keywords] of tasks)if((title+' '+keywords).toLowerCase().includes(query)){const a=node('a',title);a.href='#help?guide='+encodeURIComponent(path);a.className='help-task';topicGrid.append(a);}if(!topicGrid.children.length)topicGrid.append(node('p','No matching topic. Try a shorter phrase, or browse the offline guides below.'));}
 search.oninput=topics;topics();
 const guideNav=node('details');guideNav.className='advanced-details';guideNav.append(node('summary','Browse all offline guides'));const nav=node('nav');nav.className='workspace-subnav';nav.setAttribute('aria-label','Offline help guides');
 for(const [path,label] of guides){const a=node('a',label);a.href='#help?guide='+encodeURIComponent(path);nav.append(a);}guideNav.append(nav);parent.append(guideNav);
 const guide=new URLSearchParams(location.hash.split('?')[1]||'').get('guide')||guides[0][0];
 if(!/^[a-zA-Z0-9_./-]+\.md$/.test(guide)||guide.split('/').includes('..'))throw Error('Choose a bundled guide.');
 const body=node('article');body.className='help-guide';parent.append(body);
 try{const response=await fetch('/help/'+guide,{signal:controller.signal});if(own!==generation)return;if(!response.ok)body.append(node('p','This offline guide is unavailable in this installation. Repair or update AeroScience; your mission archive is unchanged.'));else markdown(body,await response.text(),guide);}catch(error){if(error.name==='AbortError')return;body.append(node('p','The companion could not load this guide. Reconnect and open Help again.'));}
 if(own!==generation)return;
 const anchor=new URLSearchParams(location.hash.split('?')[1]||'').get('anchor');if(anchor)body.querySelector('#'+CSS.escape(anchor))?.scrollIntoView();
 const support=node('section');support.className='panel help-community';support.append(node('h2','Contact & Community'),node('p','Need help with AeroScience? Join our Discord community for support, troubleshooting, questions, feature requests, and discussions with other pilots.'));
 for(const [label,url] of [['Join our Discord','https://discord.gg/mpYyazCqvJ'],['Report an issue on GitHub','https://github.com/JustSeanC/AeroScience/issues']]){const a=node('a',label);a.href=url;a.target='_blank';a.rel='noopener noreferrer';a.className='button secondary';support.append(a);}
 const feedback=node('button','Collect Feedback / Logs');feedback.type='button';feedback.className='secondary';feedback.onclick=async()=>{try{await api('/api/desktop/feedback',{});}catch(error){const message=node('p','Collect logs on the companion PC. '+error.message);message.setAttribute('role','alert');feedback.after(message);}};support.append(feedback,node('p','Discord is optional. Feedback collection remains available on the companion PC. Review private geographic and mission information before sharing.'));parent.append(support);
}
