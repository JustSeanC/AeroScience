const node=(tag,text)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;return n;};
const guides=[['v0.4.1-pilot-guide.md','Getting started'],['mission-planner.md','Mission planning'],['remote-sensing/index.md','Instruments & scientific data'],['v0.4.1-science-workspace.md','Science Workspace'],['v0.4.1-scientific-comparison.md','Comparisons'],['v0.4.1-scientific-downlink.md','Preparation & downlink'],['v0.4.1-windows-testing.md','Troubleshooting & testing']];
export function markdown(parent,source,guide){
 const toc=node('nav');toc.setAttribute('aria-label','In this guide');parent.append(toc);let code=null,list=null,table=null;
 function inline(target,value){const regex=/\[([^\]]+)\]\(([^)]+)\)|\*\*([^*]+)\*\*|`([^`]+)`/g;let last=0;for(const m of value.matchAll(regex)){target.append(document.createTextNode(value.slice(last,m.index)));if(m[1]){const a=node('a',m[1]);try{const url=new URL(m[2],location.origin+'/help/'+guide);if(url.origin===location.origin&&url.pathname.startsWith('/help/')&&url.pathname.endsWith('.md'))a.href='#help?guide='+encodeURIComponent(url.pathname.slice(6))+(url.hash?'&anchor='+encodeURIComponent(url.hash.slice(1)):'');else if(m[2].startsWith('#'))a.href='#help?guide='+encodeURIComponent(guide)+'&anchor='+encodeURIComponent(m[2].slice(1));else if(['https:','http:'].includes(url.protocol)){a.href=url.href;a.target='_blank';a.rel='noopener noreferrer';}else a.removeAttribute('href');}catch{}target.append(a);}else target.append(node(m[3]?'strong':'code',m[3]||m[4]));last=m.index+m[0].length;}target.append(document.createTextNode(value.slice(last)));}
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
export async function helpCenter(parent,api){
 parent.replaceChildren(node('h1','Help & Support'));const nav=node('nav');nav.className='workspace-subnav';for(const [path,label] of guides){const a=node('a',label);a.href='#help?guide='+encodeURIComponent(path);nav.append(a);}parent.append(nav);
 const guide=new URLSearchParams(location.hash.split('?')[1]||'').get('guide')||guides[0][0];
 if(!/^[a-zA-Z0-9_./-]+\.md$/.test(guide)||guide.split('/').includes('..'))throw Error('Choose a bundled guide.');
 const body=node('article');body.className='help-guide';parent.append(body);const response=await fetch('/help/'+guide);if(!response.ok)body.append(node('p','This guide is unavailable in this installation. Repair or update AeroScience.'));else markdown(body,await response.text(),guide);
 const anchor=new URLSearchParams(location.hash.split('?')[1]||'').get('anchor');if(anchor)body.querySelector('#'+CSS.escape(anchor))?.scrollIntoView();
 const support=node('section');support.className='panel';support.append(node('h2','Contact & Community'),node('p','Need help with AeroScience? Join our Discord community for support, troubleshooting, questions, feature requests, and discussions with other pilots.'));
 for(const [label,url] of [['Join our Discord','https://discord.gg/mpYyazCqvJ'],['Report an issue on GitHub','https://github.com/JustSeanC/AeroScience/issues']]){const a=node('a',label);a.href=url;a.target='_blank';a.rel='noopener noreferrer';a.className='button secondary';support.append(a);}
 const feedback=node('button','Collect Feedback / Logs');feedback.type='button';feedback.className='secondary';feedback.onclick=async()=>{try{await api('/api/desktop/feedback',{});}catch(e){feedback.after(node('p',e.message));}};support.append(feedback,node('p','Discord is optional. Collect logs on the companion PC; review private geographic and mission information before sharing.'));parent.append(support);
}
