// Guard presentation migrations against protocol, enum, selector and value edits.
import fs from'node:fs';import{parse}from'acorn';
import{presentationContracts,argumentPosition}from'./presentation-contracts.mjs';
const base='src/AeroScience.Engine/wwwroot/i18n/',en=JSON.parse(fs.readFileSync(base+'locales/en-US.json','utf8')).messages;
const tag=/^(?:a|article|aside|b|button|canvas|caption|code|dd|details|dialog|div|dl|dt|em|fieldset|figcaption|figure|footer|form|h[1-6]|header|hr|i|img|input|label|legend|li|main|nav|ol|option|p|pre|section|select|small|span|strong|summary|svg|table|tbody|td|textarea|th|thead|tr|ul)$/;
const token=/^(?:GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS|CONNECT|TRACE|JSON|JSONL|CSV|PNG|XML|AIXM|NOTAM|KML|GPX|ZIP|DEM|DSM|LAS|LAZ|COPC|EPT|NDVI|NDWI|NDMI|MSL|AGL|IAS|TAS|MSFS|FAA|UTC|ICAO|IATA|AND|OR|GeoJSON|FeatureCollection|Feature|Point|MultiPoint|Polygon|MultiPolygon|LineString|MultiLineString|EPSG:\d+)$/;
const protectedFields=new Set(['method','type','id','value','key','kind','role','scope','code','unit','instrumentId','sensorId','sourceId','namespace','sourceClass','contentType','requestId','category','group','state','status','stage','phase','mode','permission','actionId','trigger','operation','projection','sourceType','sourceKind','productKind','tab','section','view','route','resource','providerId','projectionId','grade','outcome','policy','endpoint','provider','product','service','format','crs','datum','url','href','src','className','prefix','experience','density','theme','locale']);
const prop=n=>n?.type==='MemberExpression'?prop(n.property):n?.name??n?.value;
export function protectedTranslation(source,messages=en){const rows=[],comparisons=new Set(),substrings=new Set(),ast=parse(source,{ecmaVersion:'latest',sourceType:'module'}),contracts=presentationContracts(ast,source);
 function identities(n){if(!n||!n.type)return;if(n.type==='BinaryExpression'&&['===','!==','==','!='].includes(n.operator))for(const value of[n.left,n.right])if(value.type==='Literal'&&typeof value.value==='string')comparisons.add(value.value);
  if(n.type==='SwitchCase'&&n.test?.type==='Literal'&&typeof n.test.value==='string')comparisons.add(n.test.value);
  if(n.type==='CallExpression'&&['includes','startsWith','endsWith'].includes(prop(n.callee))&&n.arguments[0]?.type==='Literal'&&typeof n.arguments[0].value==='string'&&n.arguments[0].value.length>2)substrings.add(n.arguments[0].value);
  for(const[k,v]of Object.entries(n))if(!['start','end'].includes(k)){if(Array.isArray(v))v.forEach(identities);else if(v&&typeof v==='object')identities(v);}}
 identities(ast);
 function walk(n,parents){if(!n||!n.type)return;
 if(n.type==='CallExpression'&&['textForUI','localizedCopy'].includes(n.callee.name)&&n.arguments[0]?.type==='Literal'){
  const key=n.arguments[0].value,text=messages[key],ancestors=[...parents].reverse(),boundary=ancestors.findIndex(p=>/Function/.test(p.type)),chain=boundary<0?ancestors:ancestors.slice(0,boundary);let reason;
  if(typeof text==='string'&&token.test(text))reason='Stable protocol/format/geometry token';
  const parameterProperty=p=>chain.some(call=>call.type==='CallExpression'&&['textForUI','localizedCopy'].includes(prop(call.callee))&&call.arguments[1]?.start<=p.start&&call.arguments[1]?.end>=p.end);
  const getterVariable=ancestors.find(p=>p.type==='VariableDeclarator')?.id?.name||'',displayDictionary=/(?:Names|Labels|Titles|Texts|Copy|^names)$/i.test(getterVariable);
  const property=chain.find(p=>p.type==='Property'&&!parameterProperty(p));if(property&&protectedFields.has(prop(property.key))&&!displayDictionary)reason='Protected object field '+prop(property.key);
  const getter=ancestors.find(p=>p.type==='Property'&&p.kind==='get');if(getter&&protectedFields.has(prop(getter.key))&&!displayDictionary)reason='Protected getter field '+prop(getter.key);
  const assignment=chain.find(p=>p.type==='AssignmentExpression');if(assignment?.left.type==='MemberExpression'&&protectedFields.has(prop(assignment.left)))reason='Protected assignment '+prop(assignment.left);
  for(const call of chain.filter(p=>p.type==='CallExpression')){
   if(call.arguments[0]?.type==='Literal'&&tag.test(call.arguments[0].value)&&!['fetch','api','querySelector','getElementById'].includes(prop(call.callee))){const pos=call.arguments.findIndex(a=>a.start<=n.start&&a.end>=n.end);if(pos===0||pos>=2)reason='DOM tag/style/helper identity';break;}
   const contract=contracts(prop(call.callee),call);
   if(contract?.values?.includes(argumentPosition(call,n)))reason='Protected helper value argument';
   if(contract?.options===argumentPosition(call,n)){const tuple=chain.find(p=>p.type==='ArrayExpression');if(tuple?.elements.length===2&&tuple.elements[0]?.type!=='ArrayExpression'&&tuple.elements[0]?.start<=n.start&&tuple.elements[0]?.end>=n.end)reason='Stable option value';break;}
   if(['api','fetch','querySelector','querySelectorAll','getElementById','addEventListener'].includes(prop(call.callee))){const pos=call.arguments.findIndex(a=>a.start<=n.start&&a.end>=n.end);if(pos===0)reason='Wire path/selector/event identity';break;}
  }
  const tuple=chain.find(p=>p.type==='ArrayExpression');
  const roles=new Set(['Administrator','Manager','Reviewer','Pilot']);
  if(tuple?.elements.length&&tuple.elements.every(element=>element?.type==='Literal'?roles.has(element.value):element?.type==='CallExpression'&&roles.has(messages[element.arguments[0]?.value])))reason='Stable organization role identity array';
  const labelTuple=tuple?.elements.length===2&&tuple.elements[1]?.start<=n.start&&tuple.elements[1]?.end>=n.end&&tuple.elements[0]?.type==='Literal'&&typeof tuple.elements[0]?.value==='string';
  const display=chain.some(p=>p.type==='CallExpression'&&(['localizedCopy','textForUI'].includes(prop(p.callee))||contracts(prop(p.callee),p)?.display.includes(argumentPosition(p,n))))||labelTuple||getter&&/(?:Names|Labels|Titles|Texts|Copy|^names)$/i.test(getterVariable)||['common.unknown','common.unavailable','localization.unknownValue','localization.unavailableValue'].includes(key)||/Title|Label|Display|Text|Name/.test(ancestors.find(p=>/Function/.test(p.type))?.id?.name||'');
  const loop=ancestors.find(p=>p.type==='ForOfStatement');
  if(!reason&&!display&&typeof text==='string'&&(comparisons.has(text)||loop&&text.split(/\s+/).length<=4&&[...substrings].some(part=>text.includes(part))))reason='Original text participates in controller comparison; translate only at display boundary';
  if(reason)rows.push({start:n.start,end:n.end,key,text,reason,parameterized:n.arguments.length>1});
 }
 for(const[k,v]of Object.entries(n))if(!['start','end'].includes(k)){if(Array.isArray(v))v.forEach(c=>walk(c,[...parents,n]));else if(v&&typeof v==='object')walk(v,[...parents,n]);}}
 walk(ast,[]);return rows;}
if(process.argv[1]?.endsWith('audit-display-bindings.mjs')){
 const rows=[];for(const folder of['src/AeroScience.Engine/wwwroot','src/AeroScience.Organization/wwwroot'])for(const name of fs.readdirSync(folder).filter(n=>n.endsWith('.js')&&!/i18n/.test(n))){const file=folder+'/'+name;let source=fs.readFileSync(file,'utf8');const found=protectedTranslation(source);for(const row of found)rows.push({file,...row});
  if(process.argv.includes('--repair')){const safe=found.filter(row=>!row.parameterized&&!found.some(outer=>outer!==row&&outer.start<=row.start&&outer.end>=row.end));for(const row of safe.sort((a,b)=>b.start-a.start))source=source.slice(0,row.start)+JSON.stringify(row.text)+source.slice(row.end);fs.writeFileSync(file,source);}}
 fs.writeFileSync('artifacts/p07-protected-display-bindings.json',JSON.stringify(rows,null,2)+'\n');console.log(JSON.stringify({protectedReferences:rows.length,repaired:process.argv.includes('--repair')}));if(rows.length&&!process.argv.includes('--repair'))process.exitCode=1;
}
