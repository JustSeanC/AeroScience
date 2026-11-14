import {presentationTextNode} from './i18n.js';
import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {effectiveLocale} from './i18n.js';
import {select,link,disclosure} from './workstation-ui.js';
const node=(tag,text)=>{const n=document.createElement(tag);if(text!==undefined)setPresentationText(n,text);return n;};
const guides=[['v0.4.2-pilot-guide.md',localizedCopy("ui.helpCenter.messageFirstMissionResults")],['v0.4.2-science-visualization.md',localizedCopy("ui.helpCenter.messageReadingScientificMaps")],['v0.4.1-pilot-guide.md',localizedCopy("ui.helpCenter.messageGettingStarted")],['mission-planner.md',localizedCopy("ui.helpCenter.messageMissionPlanning")],['remote-sensing/index.md',localizedCopy("ui.helpCenter.messageInstrumentsScientificData")],['v0.4.1-science-workspace.md',localizedCopy("html.index.textScienceResultsAnalysis")],['v0.4.1-scientific-comparison.md',localizedCopy("ui.helpCenter.messageComparisons")],['v0.4.1-scientific-downlink.md',localizedCopy("ui.helpCenter.messagePreparationDownlink")],['v0.4.1-windows-testing.md',localizedCopy("ui.helpCenter.messageTroubleshootingTesting")]];
guides.push(['scientific-library.md',localizedCopy("ui.helpCenter.messageBrowseDownloadScientificData")]);
guides.push(['remote-sensing/hyperspectral.md',localizedCopy("ui.helpCenter.messageNASAHyperspectralAssistant")]);
guides.push(['v0.6.0-aviation-reference.md',localizedCopy("ui.helpCenter.messageAviationMapsFAAReferences")]);
guides.push(['v0.7.0-personal-research.md',localizedCopy("ui.helpCenter.messagePersonalResearchFlightRegimeValidation")]);
guides.push(['v0.8.0-research-collaboration.md',localizedCopy("ui.helpCenter.messageResearchCollaborationScientificEvidence")]);
guides.push(['README.md',localizedCopy("ui.helpCenter.messageDocumentationIndex")]);
guides.push(['instrument-calibration.md',localizedCopy("ui.helpCenter.messageCalibrationProvenanceDevelopment")]);
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
 return 'https://github.com/JustSeanC/AeroScience/blob/develop/v0.7.0'+url.pathname+url.hash;
}
export function markdown(parent,source,guide){
 const toc=node('nav');setPresentationText(toc,localizedCopy("ui.helpCenter.messageInThisGuide"),"aria-label");parent.append(toc);let code=null,list=null,table=null;
 function inline(target,value){const regex=/\[([^\]]+)\]\(([^)]+)\)|\*\*([^*]+)\*\*|`([^`]+)`/g;let last=0;for(const m of value.matchAll(regex)){target.append(presentationTextNode(value.slice(last,m.index)));if(m[1]){const a=node('a',m[1]);try{const href=documentationUrl(m[2],guide);if(href){a.href=href;if(!href.startsWith('#help?')){a.target='_blank';a.rel='noopener noreferrer';}}}catch{}target.append(a);}else target.append(node(m[3]?'strong':'code',m[3]||m[4]));last=m.index+m[0].length;}target.append(presentationTextNode(value.slice(last)));}
 for(const line of source.split(/\r?\n/)){
  if(line.startsWith('```')){if(code)code=null;else{const pre=node('pre');code=node('code');pre.append(code);parent.append(pre);}continue;}
  if(code){code.append(presentationTextNode(line+'\n'));continue;}
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
 [localizedCopy("ui.helpCenter.messageHowDoIJoinResearchAndSubmit"),'v0.8.0-research-collaboration.md','organization campaign assignment claim review original derived DEM DSM historical LiDAR datum source privacy sharing permission'],
 [localizedCopy("ui.helpCenter.messageHowDoIFindAndImportNASA"),'remote-sensing/hyperspectral.md','nasa aviris classic ng emit earthdata authorization hyperspectral header binary checksum official browser guided import'],
 [localizedCopy("ui.helpCenter.messageHowDoIUseExternalDataManager"),'scientific-library.md','library catalog scenes source date bands polygon region download approval queue offline reuse'],
 [localizedCopy("ui.helpCenter.messageHowDoICreateMyFirstMission"),'v0.4.2-pilot-guide.md','plan select start first flight'],
 [localizedCopy("ui.helpCenter.messageHowDoIConfigureScientificInstruments"),'remote-sensing/index.md','instruments payload source thermal terrain multispectral hyperspectral'],
 [localizedCopy("ui.helpCenter.messageWhyIsMyScientificDataStillDownloading"),'scientific-library.md','preparation download partial offline allowance budget'],
 [localizedCopy("ui.helpCenter.messageWhatDoesThisMeasurementRepresent"),'v0.4.2-science-visualization.md','units temperature elevation NDVI source date quality'],
 [localizedCopy("ui.helpCenter.messageHowDoIViewMyResults"),'v0.4.2-pilot-guide.md','review science map analysis history'],
 [localizedCopy("ui.helpCenter.messageHowDoICompareTwoMissions"),'v0.4.2-science-visualization.md','comparison shared scales difference overlap compatible'],
 [localizedCopy("ui.helpCenter.messageHowDoIExportScientificResults"),'v0.4.2-pilot-guide.md','export CSV JSON GeoTIFF research zip'],
 [localizedCopy("ui.helpCenter.messageWhyIsADatasetIncomplete"),'v0.4.2-science-visualization.md','missing nodata clouds gaps quality masks coverage'],
 [localizedCopy("ui.helpCenter.messageHowDoesPostFlightDownlinkWork"),'v0.4.1-scientific-downlink.md','recovery reconstruction processing provisional'],
 [localizedCopy("ui.helpCenter.messageHowDoICollectLogsOrGet"),'v0.4.2-pilot-guide.md','feedback troubleshooting Discord GitHub errors']
];
export async function helpCenter(parent,api){
 disposeHelpCenter();const own=generation;controller=new AbortController();
 parent.replaceChildren(node('div'));parent.firstChild.className='page-heading';parent.firstChild.append(node('h1',localizedCopy("nav.help")));
 const quick=node('section');quick.className='help-quick-start';quick.append(node('h2',localizedCopy('help.quickStart.title')));
 const steps=node('ol');for(const step of ['connect','prepare','record','collect','pause','results','sources','organizations'])steps.append(node('li',localizedCopy('help.quickStart.'+step)));quick.append(steps);parent.append(quick);
 const searchLabel=node('label',localizedCopy("ui.helpCenter.messageFindATaskOrQuestion")),search=node('input');search.type='search';setPresentationText(search,localizedCopy("ui.helpCenter.messageTryMissingDataCompareOrFirstMission"),"placeholder",true);setPresentationText(search,localizedCopy("ui.helpCenter.messageSearchHelpTopics"),"aria-label");searchLabel.className='help-search';searchLabel.append(search);parent.append(searchLabel);
 const topicGrid=node('nav');topicGrid.className='help-tasks';setPresentationText(topicGrid,localizedCopy("ui.helpCenter.messageHelpByTask"),"aria-label");parent.append(topicGrid);
 function topics(){topicGrid.replaceChildren();const query=search.value.trim().toLowerCase();for(const [title,path,keywords] of tasks)if((title+' '+keywords).toLowerCase().includes(query)){const a=node('a',title);a.href='#help?guide='+encodeURIComponent(path);a.className='help-task';topicGrid.append(a);}if(!topicGrid.children.length)topicGrid.append(node('p',localizedCopy("ui.helpCenter.messageNoMatchingTopicTryAShorterPhrase")));}
 search.oninput=topics;topics();
 const guideNav=node('details');guideNav.className='advanced-details';guideNav.append(node('summary',localizedCopy("ui.helpCenter.messageBrowseAllOfflineGuides")));const nav=node('nav');nav.className='workspace-subnav';setPresentationText(nav,localizedCopy("ui.helpCenter.messageOfflineHelpGuides"),"aria-label");
 for(const [path,label] of guides){const a=node('a',label);a.href='#help?guide='+encodeURIComponent(path);nav.append(a);}guideNav.append(nav);parent.append(guideNav);
 const guide=new URLSearchParams(location.hash.split('?')[1]||'').get('guide')||guides[0][0];
 if(!/^[a-zA-Z0-9_./-]+\.md$/.test(guide)||guide.split('/').includes('..'))throw Error(textForUI("ui.helpCenter.messageChooseABundledGuide"));
 const body=node('article');body.className='help-guide';parent.append(body);
 if(effectiveLocale()!=='en-US')body.before(node('p',localizedCopy('help.referenceEnglish')));
 body.lang='en-US';
 try{const response=await fetch('/help/'+guide,{signal:controller.signal});if(own!==generation)return;if(!response.ok)body.append(node('p',localizedCopy("ui.helpCenter.messageThisOfflineGuideIsUnavailableInThis")));else markdown(body,await response.text(),guide);}catch(error){if(error.name==='AbortError')return;body.append(node('p',localizedCopy("ui.helpCenter.messageTheCompanionCouldNotLoadThisGuide")));}
 if(own!==generation)return;
 const anchor=new URLSearchParams(location.hash.split('?')[1]||'').get('anchor');if(anchor)body.querySelector('#'+CSS.escape(anchor))?.scrollIntoView();
 const support=node('section');support.className='panel help-community';support.append(node('h2',localizedCopy("ui.helpCenter.messageContactCommunity")),node('p',localizedCopy("ui.app.messageNeedHelpWithAeroScienceJoinOurDiscord")));
 for(const [label,url] of [[textForUI("ui.app.messageJoinOurDiscord"),'https://discord.gg/mpYyazCqvJ'],[textForUI("ui.app.messageReportAnIssueOnGitHub"),'https://github.com/JustSeanC/AeroScience/issues']]){const a=node('a',label);a.href=url;a.target='_blank';a.rel='noopener noreferrer';a.className='button secondary';support.append(a);}
 const feedback=node('button',localizedCopy("ui.helpCenter.messageCollectFeedbackLogs"));feedback.type='button';feedback.className='secondary';feedback.onclick=async()=>{try{await api('/api/desktop/feedback',{});}catch(error){const message=node('p',localizedCopy("ui.helpCenter.messageCollectLogsOnTheCompanionPC",{value1:(String(error.message))}));message.setAttribute('role','alert');feedback.after(message);}};support.append(feedback,node('p',localizedCopy("ui.helpCenter.messageDiscordIsOptionalFeedbackCollectionRemainsAvailable")));parent.append(support);
}
