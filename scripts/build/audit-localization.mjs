import {readFileSync,readdirSync,writeFileSync,mkdirSync} from 'node:fs';
import {resolve,relative,dirname} from 'node:path';
import {fileURLToPath} from 'node:url';
import {parse} from 'acorn';
import {createHash} from 'node:crypto';

const root=resolve(dirname(fileURLToPath(import.meta.url)),'../..');
const roots=['src/AeroScience.Engine/wwwroot','src/AeroScience.Organization/wwwroot','src/AeroScience.Desktop','msfs-toolbar/PackageSources/html_ui/InGamePanels/AeroScience'];
const skip=/\/(?:vendor|geography|help|i18n|bin|obj|artifacts)\//;
const camel=s=>s.replace(/[^a-zA-Z0-9]+(.)?/g,(_,c)=>c?c.toUpperCase():'').replace(/^[A-Z]/,c=>c.toLowerCase());
function files(folder){return readdirSync(folder,{withFileTypes:true}).flatMap(e=>{const p=resolve(folder,e.name),n=relative(root,p).replaceAll('\\','/');return skip.test(n)?[]:e.isDirectory()?files(p):/\.(js|html|cs)$/.test(p)?[p]:[];});}
function surface(file){
 if(file.startsWith('msfs-toolbar/'))return 'nativeToolbar';
 if(file.startsWith('src/AeroScience.Desktop/'))return 'windowsCompanion';
 if(file.startsWith('src/AeroScience.Organization/'))return 'organizations';
 if(/organization|submission|campaign|program|research-template/.test(file))return 'organizations';
 if(/library|download|source-|hyperspectral|earthdata|emit-/.test(file))return 'libraryDownloads';
 if(/survey|lidar|science-(?:workspace|map|compare|analysis)|profile|terrain|mission-workspace/.test(file))return 'scientificResults';
 if(/help|context/.test(file))return 'contextualHelp';
 if(/mission|research|planner|readiness/.test(file))return 'missions';
 if(/flight|intelligence|sensor|instrument|operating/.test(file))return 'currentFlight';
 return 'dashboard';
}
const human=text=>typeof text==='string'&&/[A-Za-z]/.test(text)&&(/\s/.test(text)||/^[A-Z][a-z]+/.test(text))&&!/^\s*(?:https?:|\/|#|\.\/|\.\.\/|[A-Za-z]:\\|<|\^)/.test(text);
const critical=text=>/consent|authoriz|permanent|delet|upload|shar(?:e|ing)|recording|collect(?:ing|ion)|expired|stale|unavailable|missing|invalid|recovery|recover|password|credential|cannot be undone|end (?:flight|mission|science)|pause|resume/i.test(text);
const property=n=>n?.type==='Identifier'?n.name:n?.type==='Literal'?String(n.value):n?.type==='MemberExpression'?property(n.property):'';
const sinks=new Set(['node','el','element','button','link','disclosure','informationDetails','scientificNotice','status','metric','table','chart','legend','field','formField','label','option','select','heading','modal','submit','toast','notice','failure','errorMessage','confirm','alert','dialogHeader','text','textContent','innerText','title','placeholder','aria-label','aria-description','aria-describedby','aria-valuetext','alt','caption','description','message','explanation','problem','consequence','action','xLabel','yLabel','label','help','name']);
const protectedProperty=new Set(['href','src','id','className','value','type','role','method','headers','path','url','origin','key','unit','kind','instrumentId','sourceId','scope','policy','code','op','templateId','query','fingerprint','sha256']);
function disposition(node,ancestors,source){
 const parent=ancestors.at(-1),chain=[...ancestors].reverse();
 const call=chain.find(n=>n.type==='CallExpression'),callName=property(call?.callee);
 if(chain.some(n=>n.type==='ImportDeclaration'||n.type==='ExportNamedDeclaration'&&n.source))return ['protected-identifier','module identity'];
 if(callName==='t'||callName==='text'&&source.slice(call.callee.start,call.callee.end).includes('i18n'))return ['catalog-reference','existing translation call'];
 if(parent?.type==='MemberExpression'||parent?.type==='BinaryExpression'&&['===','!==','==','!='].includes(parent.operator)||parent?.type==='SwitchCase')return ['protected-identifier','state/API/selector comparison'];
 const prop=chain.find(n=>n.type==='Property');
 if(prop&&protectedProperty.has(property(prop.key)))return ['protected-value','identifier, unit, command or data value'];
 if(callName==='setAttribute'){
  const attr=call.arguments[0]?.value;
  return ['aria-label','aria-description','aria-valuetext','title','placeholder','alt'].includes(attr)&&call.arguments[1]===node?['presentation','accessible/display attribute']:['protected-identifier','DOM attribute or selector'];
 }
 if(['getElementById','querySelector','querySelectorAll','byId','closest','matches','classList','contains','getAttribute','includes','startsWith','endsWith','split','join','replace','replaceAll','padStart','addEventListener','removeEventListener','dispatchEvent','fetch','api','safeApi','read','write','post','get','put','newIdentity','readableName'].includes(callName))return ['protected-value','selector, wire value, data parsing or method argument'];
 if(chain.some(n=>n.type==='AssignmentExpression'&&['textContent','innerText','placeholder','title','ariaLabel','alt'].includes(property(n.left))))return ['presentation','display property assignment'];
 if(prop&&sinks.has(property(prop.key)))return ['review-presentation','label/description object; exclude source definitions and original values before binding'];
 if(call&&sinks.has(callName)){
  const position=call.arguments.findIndex(arg=>arg.start<=node.start&&arg.end>=node.end);
  if(['node','el','element'].includes(callName)&&position!==1)return ['protected-identifier','element tag/style'];
  const positions={button:[1],link:[1],field:[1],formField:[1],select:[1,2],table:[1,2],chart:[1,3],legend:[1,6],heading:[0,1],modal:[0],submit:[1],status:[1],notice:[0],confirm:[0],alert:[0],dialogHeader:[1]};
  if(positions[callName]&&!positions[callName].includes(position))return ['protected-value','non-presentation helper value, path, style, original rows or callback'];
  if(['button','link','field','formField','select','disclosure','informationDetails','metric','table','chart','legend'].includes(callName)&&position===0)return ['protected-value','target node'];
  return ['presentation','presentation helper argument'];
 }
 if(parent?.type==='ArrayExpression'&&parent.elements.length===2&&parent.elements[1]===node)return ['review-presentation','option/action label tuple; first element is a stable value'];
 if(chain.some(n=>n.type==='ThrowStatement'))return ['review-error','error display; preserve original wire/evidence message'];
 return ['review','manual classification required; never rewrite unclassified source data'];
}
export function inventoryJavaScript(source,file){
 const ast=parse(source,{ecmaVersion:'latest',sourceType:'module',locations:true,allowReturnOutsideFunction:true}),rows=[];
 function walk(node,parents){
  if(!node||typeof node!=='object')return;
  const literal=node.type==='Literal'&&typeof node.value==='string',template=node.type==='TemplateLiteral';
  const text=literal?node.value:template?node.quasis.map((q,i)=>q.value.cooked+(i<node.expressions.length?'{value'+(i+1)+'}':'')).join(''):null;
  if(human(text)){
   const [decision,reason]=disposition(node,parents,source),fn=[...parents].reverse().find(n=>/Function/.test(n.type)),functionName=fn?.id?.name||property(parents.findLast(n=>n.type==='MethodDefinition')?.key)||'view';
   rows.push({file,line:node.loc.start.line,column:node.loc.start.column,start:node.start,end:node.end,surface:surface(file),kind:template?'template':'literal',text,parameters:template?node.expressions.map((e,i)=>({name:'value'+(i+1),expression:source.slice(e.start,e.end)})):[],decision,reason,critical:critical(text),semanticScope:camel(file.split('/').at(-1).replace(/\.[^.]+$/,''))+'.'+camel(functionName)});
  }
  for(const [key,value]of Object.entries(node)){if(['loc','start','end','raw','quasis'].includes(key))continue;if(Array.isArray(value))value.forEach(v=>walk(v,[...parents,node]));else if(value&&typeof value==='object')walk(value,[...parents,node]);}
 }
 walk(ast,[]);return rows;
}
export function inventoryOther(source,file){
 const rows=[],isCs=file.endsWith('.cs');
 const patterns=isCs?[/\$?@?"(?:""|\\.|[^"\\])*"/g]:[/>[^<>]*[A-Za-z][^<>]*</g,/\b(?:title|aria-label|placeholder|alt)="[^"]+"/g];
 for(const pattern of patterns)for(const match of source.matchAll(pattern)){
  const raw=match[0],text=isCs?raw.replace(/^\$?@?"|"$/g,'').replaceAll('\\n','\n'):raw.startsWith('>')?raw.slice(1,-1).trim():raw.slice(raw.indexOf('"')+1,-1);
  if(!human(text))continue;const prefix=source.slice(Math.max(0,match.index-160),match.index),display=isCs?/\b(?:Text|HeaderText|AccessibleName|Description|Title)\s*=\s*$|\b(?:MessageBox\.Show|AddText|Label|Field|Button|AddColumn)\([^;{}]*$/.test(prefix):true;
  rows.push({file,line:source.slice(0,match.index).split('\n').length,start:match.index,end:match.index+raw.length,surface:surface(file),kind:isCs?'csharp':'html',text,parameters:[],decision:display?'presentation':'review',reason:isCs?'C# lexical candidate; verify display boundary versus data/protocol':'HTML text/accessible-name candidate; verify original values',critical:critical(text)});
 }
 return rows;
}
export function audit(){
 const rows=roots.flatMap(dir=>files(resolve(root,dir))).sort().flatMap(path=>{const file=relative(root,path).replaceAll('\\','/'),source=readFileSync(path,'utf8');return path.endsWith('.js')?inventoryJavaScript(source,file):inventoryOther(source,file);});
 const counts=key=>Object.fromEntries([...new Set(rows.map(r=>r[key]))].sort().map(k=>[k,rows.filter(r=>r[key]===k).length]));
 return {version:1,sourcePolicy:'Presentation candidates only. Original evidence, names, IDs, units, UTC, hashes, paths, numeric values and protocol semantics must remain untouched. Review rows are not claimed localized.',sourceDigest:createHash('sha256').update(JSON.stringify(rows)).digest('hex'),totals:{occurrences:rows.length,bySurface:counts('surface'),byDisposition:counts('decision'),criticalCandidates:rows.filter(r=>r.critical&&!r.decision.startsWith('protected')).length},rows};
}
if(process.argv[1]&&resolve(process.argv[1])===fileURLToPath(import.meta.url)){
 const report=audit(),at=process.argv.indexOf('--output'),target=resolve(root,at>=0?process.argv[at+1]:'artifacts/p07-localization-inventory.json');mkdirSync(dirname(target),{recursive:true});writeFileSync(target,JSON.stringify(report,null,2)+'\n');console.log(JSON.stringify({path:relative(root,target),...report.totals}));
}
