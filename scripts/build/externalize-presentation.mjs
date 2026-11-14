// Maintenance tool: explicit display boundaries only. Review its diff before commit.
// It never traverses fetched source records, original evidence or user values.
import {parse} from 'acorn';
import {readFileSync,writeFileSync} from 'node:fs';
import {createHash} from 'node:crypto';
import {audit,inventoryJavaScript} from './audit-localization.mjs';
import {sourceFingerprint} from './localization-quality.mjs';

const folder='src/AeroScience.Engine/wwwroot/i18n/',read=p=>JSON.parse(readFileSync(folder+p,'utf8'));
const schema=read('message-schema.json'),english=read('locales/en-US.json'),manifest=read('locale-manifest.json');
const camel=s=>s.replace(/[^a-zA-Z0-9]+(.)?/g,(_,c)=>c?c.toUpperCase():'').replace(/^[A-Z]/,c=>c.toLowerCase());
const existing=new Map(Object.entries(english.messages).filter(([,v])=>typeof v==='string').map(([k,v])=>[v,k]));
const quote=JSON.stringify,report=[];
function catalog(text,file,line,surface,parameters){
 if(!/[A-Za-z]{2}/.test(text)||/^\s*(?:rgb|rgba|hsl|<|https?:|\.\/|\/|#)/.test(text)||text.trim().length<2)return null;
 let key=existing.get(text);
 if(key&&!schema.messages[key].parameters.every((p,i)=>p===parameters[i]))key=null;
 if(!key){
  const scope=camel(file.split('/').at(-1).replace(/\.js$/,''));
  const words=('message '+text.replace(/\{[^}]+\}/g,' ').split(/\s+/).slice(0,7).join(' '));
  key='ui.'+scope+'.'+camel(words).slice(0,80);if(english.messages[key]&&english.messages[key]!==text)key+=createHash('sha256').update(text).digest('hex').slice(0,8);
  english.messages[key]=text;existing.set(text,key);schema.messages[key]={context:'Presentation in '+file+':'+line+'. Whole display message; parameters retain original runtime values.',parameters,surface,critical:/consent|authoriz|permanent|delet|upload|shar(?:e|ing)|recording|collect(?:ing|ion)|expired|stale|unavailable|missing|invalid|recover|password|credential|end (?:flight|mission|science)|pause|resume/i.test(text),maximumLength:4096};
 }
 const rule=schema.messages[key];rule.locations??=[];const location=file+':'+line;if(!rule.locations.includes(location))rule.locations.push(location);
 report.push({file,line,key,surface});return key;
}
function rewrite(file){
 const source=readFileSync(file,'utf8'),ast=parse(source,{ecmaVersion:'latest',sourceType:'module',locations:true}),nodes=new Map(),parents=new Map();
 function walk(n,p){if(!n||typeof n!=='object')return;if(n.type){nodes.set(n.start+':'+n.end,n);parents.set(n,p);}for(const [k,v]of Object.entries(n))if(!['loc','start','end'].includes(k)){if(Array.isArray(v))v.forEach(c=>walk(c,n));else if(v&&typeof v==='object')walk(v,n);}}
 walk(ast,null);const rows=inventoryJavaScript(source,file),chosen=new Map();
 for(const row of rows){
  const reviewed=process.argv.includes('--review-ui')&&row.decision.startsWith('review')&&!file.startsWith('msfs-toolbar/')
   &&/^(?:[A-Z][a-z]|[A-Z]{3,}(?:\s|$)|\{value\d+\})/.test(row.text)
   &&!/^[\w.-]+\.[\w.-]+$|\b(?:px|rem|sans-serif)\b|^View changed\.$|^Failed to fetch$|^AbortError$|^NetworkError$|^SyntaxError$|^TypeError$/.test(row.text)
   &&!/^[A-Z][a-z]+[A-Z]/.test(row.text)
   &&!/\b(?:Content-Type|Content-Disposition|Authorization|Bearer|X-CSRF|AeroScience\.[A-Za-z]+\.v\d)/.test(row.text);
  if(row.decision!=='presentation'&&!reviewed)continue;let n=nodes.get(row.start+':'+row.end);if(!n)continue;
  if(reviewed){const parent=parents.get(n);if(parent?.type==='Property'&&['name','provider','product','source','sourceClass','state','status','value','id','kind','unit','code','version','type','category','namespace'].includes(parent.key.name??parent.key.value))continue;}
  while(parents.get(n)?.type==='BinaryExpression'&&parents.get(n).operator==='+')n=parents.get(n);
  // Do not rewrite strings used as syntax, markup, data types or drawing formulas.
  if(n.type==='TemplateLiteral'&&source.slice(n.start,n.end).includes('<'))continue;
  chosen.set(n.start,n);
 }
 const edits=[];
 for(const n of chosen.values()){
  if([...chosen.values()].some(other=>other!==n&&other.start<=n.start&&other.end>=n.end))continue;
  const parameters=[],values=[];let hasEnglish=false;
  function flatten(e){
   if(e.type==='Literal'&&typeof e.value==='string'){hasEnglish||=/[A-Za-z]{2}/.test(e.value);return e.value;}
   if(e.type==='TemplateLiteral')return e.quasis.map((q,i)=>q.value.cooked+(i<e.expressions.length?parameter(e.expressions[i]):'')).join('');
   if(e.type==='BinaryExpression'&&e.operator==='+'&&containsString(e))return flatten(e.left)+flatten(e.right);
   return parameter(e);
  }
  function containsString(e){return e.type==='Literal'&&typeof e.value==='string'||e.type==='TemplateLiteral'||e.type==='BinaryExpression'&&e.operator==='+'&&(containsString(e.left)||containsString(e.right));}
  function parameter(e){const name='value'+(parameters.length+1);parameters.push(name);values.push(name+':('+source.slice(e.start,e.end)+')');return '{'+name+'}';}
  const text=flatten(n);hasEnglish||=/[A-Za-z]{2}/.test(text.replace(/\{[^}]+\}/g,''));if(!hasEnglish)continue;
  const key=catalog(text,file,n.loc.start.line,rows.find(r=>r.start>=n.start&&r.end<=n.end)?.surface||'dashboard',parameters);if(!key)continue;
  edits.push({start:n.start,end:n.end,value:'textForUI('+quote(key)+(values.length?',{'+values.join(',')+'}':'')+')'});
 }
 if(!edits.length)return;
 let updated=source;for(const e of edits.sort((a,b)=>b.start-a.start))updated=updated.slice(0,e.start)+e.value+updated.slice(e.end);
 if(!/import\s*\{[^}]*\btextForUI\b[^}]*\}\s*from\s*['"]\.\/i18n.js['"]/.test(updated)){
  if(/import\s*\{[^}]*\}\s*from\s*['"]\.\/i18n.js['"]/.test(updated))updated=updated.replace(/(import\s*\{)([^}]*)(\}\s*from\s*['"]\.\/i18n.js['"])/,(_m,a,b,c)=>a+'t as textForUI,'+b+c);
  else updated="import {t as textForUI} from './i18n.js';\n"+updated;
 }
 try{parse(updated,{ecmaVersion:'latest',sourceType:'module'});}catch(error){throw Error(file+': '+error.message);}
 if(process.argv.includes('--apply'))writeFileSync(file,updated);
}
const files=[...new Set(audit().rows.filter(r=>r.file.endsWith('.js')&&!r.file.startsWith('msfs-toolbar/')&&!/i18n|presentation-settings|geographic-view/.test(r.file)).map(r=>r.file))];
for(const file of files)rewrite(file);
if(process.argv.includes('--apply')){
 manifest.catalogVersion=19;english.version=19;manifest.catalogIdentity=sourceFingerprint(english.messages);
 for(const [path,value]of [['message-schema.json',schema],['locales/en-US.json',english],['locale-manifest.json',manifest]])writeFileSync(folder+path,JSON.stringify(value,null,2)+'\n');
}
writeFileSync('artifacts/p07-externalization.json',JSON.stringify(report,null,2)+'\n');console.log(JSON.stringify({bindings:report.length,keys:Object.keys(english.messages).length,files:new Set(report.map(r=>r.file)).size,applied:process.argv.includes('--apply')}));
