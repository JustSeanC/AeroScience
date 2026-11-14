// Maintenance authoring tool: explicit display boundaries only. Never run on
// commands, enum IDs, comparisons or persisted values. Review the emitted audit.
import{readFileSync,writeFileSync}from'node:fs';import{parse}from'acorn';
import{register,save,english}from'./catalog-authoring.mjs';
const path='msfs-toolbar/PackageSources/html_ui/InGamePanels/AeroScience/AeroScience.js';
const source=readFileSync(path,'utf8'),ast=parse(source,{ecmaVersion:2022,sourceType:'script'}),edits=[],audit=[],dynamicIds=new Set();
const reverse=new Map(Object.entries(english.messages).filter(([,v])=>typeof v==='string').map(([k,v])=>[v,k]));
function walk(node,fn){if(!node||typeof node!=='object')return;fn(node);for(const[k,v]of Object.entries(node))if(k!=='start'&&k!=='end'){if(Array.isArray(v))v.forEach(n=>walk(n,fn));else if(v&&typeof v==='object')walk(v,fn);}}
function keyFor(text,line){let key=reverse.get(text);if(key)return key;const slug=text.replace(/\{value\d+\}/g,'').toLowerCase().match(/[a-z0-9]+/g)?.slice(0,9).map((w,i)=>i?w[0].toUpperCase()+w.slice(1):w).join('')||'message';key='native.'+slug;let n=2;while(english.messages[key]&&english.messages[key]!==text)key='native.'+slug+n++;register(key,text,{surface:'nativeToolbar',context:'MSFS toolbar display. Complete status/control message; preserve original telemetry and command identities.',parameters:[...new Set([...text.matchAll(/\{(value\d+)\}/g)].map(m=>m[1]))],critical:/science|record|confirm|stale|unavail|retri|fail|source|collect|invalid|session|disconnect/i.test(text),locations:[path+':'+line]});reverse.set(text,key);return key;}
const code=n=>source.slice(n.start,n.end),isText=n=>n?.type==='Literal'&&typeof n.value==='string'||n?.type==='TemplateLiteral'||n?.type==='BinaryExpression'&&n.operator==='+'&&(isText(n.left)||isText(n.right));
function display(n){
 if(n.type==='ConditionalExpression')return '('+code(n.test)+'?'+display(n.consequent)+':'+display(n.alternate)+')';
 if(n.type==='LogicalExpression')return '('+display(n.left)+n.operator+display(n.right)+')';
 if(!isText(n))return code(n);
 let values=[];
 function flatten(x){if(x.type==='Literal'&&typeof x.value==='string')return x.value;if(x.type==='TemplateLiteral')return x.quasis.map((q,i)=>q.value.cooked+(x.expressions[i]?param(x.expressions[i]):'')).join('');if(x.type==='BinaryExpression'&&x.operator==='+'&&isText(x))return flatten(x.left)+flatten(x.right);return param(x);}
 function param(x){values.push(display(x));return '{value'+values.length+'}';}
 const text=flatten(n);if(!/[a-z]{2}/i.test(text.replace(/\{value\d+\}/g,''))||/^\s*(?:https?:|AeroScience$)/.test(text))return n.type==='BinaryExpression'&&n.operator==='+'?'('+display(n.left)+'+'+display(n.right)+')':code(n);
 const line=source.slice(0,n.start).split('\n').length,key=keyFor(text,line);audit.push({file:path,line,key,text});return 'nativeText('+JSON.stringify(key)+','+JSON.stringify(text)+(values.length?',{'+values.map((v,i)=>'value'+(i+1)+':'+v).join(',')+'}':'')+')';
}
function select(n){const replacement=display(n);if(replacement!==code(n))edits.push([n.start,n.end,replacement]);}
walk(ast,n=>{
 if(n.type==='AssignmentExpression'&&n.left.type==='MemberExpression'&&['textContent','title','placeholder'].includes(n.left.property.name)){
  select(n.right);const obj=n.left.object;if(obj?.type==='CallExpression'&&obj.callee.property?.name==='byId'&&typeof obj.arguments[0]?.value==='string')dynamicIds.add(obj.arguments[0].value);
 }
 if(n.type==='CallExpression'&&n.callee.type==='MemberExpression'){
  if(n.callee.property.name==='setAttribute'&&['aria-label','aria-description','title','placeholder'].includes(n.arguments[0]?.value))select(n.arguments[1]);
  if(['status','nativeMessage'].includes(n.callee.property.name)&&n.arguments[0])select(n.arguments[0]);
 }
 // Display-only dictionaries and intermediates; never command/state values.
 if(n.type==='VariableDeclarator'&&['phase','requirements','label'].includes(n.id.name)&&n.init)select(n.init);
 if(n.type==='ObjectExpression'&&n.properties.some(p=>['ambient-temperature','airTemperature','pass'].includes(p.key?.value||p.key?.name)))for(const p of n.properties)select(p.value);
 if(n.type==='VariableDeclarator'&&n.id.name==='labels'&&n.init?.type==='ObjectExpression')for(const p of n.init.properties)select(p.value);
 if(n.type==='ArrayExpression'&&n.elements.length===2&&['activate','deactivate','beginAttempt','endAttempt','enableAutomatic','disableAutomatic'].includes(n.elements[0]?.value)&&n.elements[1]?.type==='Literal'&&(/\s/.test(n.elements[1].value)||/^[A-Z][a-z]+$/.test(n.elements[1].value)))select(n.elements[1]);
});
let result=source,last=source.length+1;
// Keep the outer complete message when boundaries nest; parameter rendering is recursive.
const outer=edits.filter(e=>!edits.some(p=>p!==e&&p[0]<=e[0]&&p[1]>=e[1]&&(p[0]<e[0]||p[1]>e[1])));for(const[start,end,text]of outer.sort((a,b)=>b[0]-a[0])){if(end>last)throw Error('overlap');result=result.slice(0,start)+text+result.slice(end);last=start;}
const bridge="\nfunction nativeText(key,fallback,values){return typeof AeroScienceI18n==='undefined'?fallback.replace(/\\{(value\\d+)\\}/g,function(_,name){return String(values&&values[name]!==undefined?values[name]:_);}):AeroScienceI18n.text(key,fallback,values);}\n";
if(process.argv.includes('--apply')){writeFileSync(path,result+(source.includes("function nativeText(")?"":bridge));save();}
writeFileSync('artifacts/p07c-native-display-boundaries.json',JSON.stringify({messages:audit,dynamicIds:[...dynamicIds]},null,2)+'\n');console.log(JSON.stringify({boundaries:outer.length,messages:audit.length,dynamicIds:dynamicIds.size}));
