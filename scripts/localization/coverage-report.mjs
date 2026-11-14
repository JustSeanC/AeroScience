// Reproducible source/reference and sampled-browser coverage. Never counts
// complete catalogs as proof of rendered UI, human or installed acceptance.
import fs from'node:fs';import{join}from'node:path';
import{localeBundle,validateReferences,nativeLocaleBundle,nativeCatalogAssets}from'../build/build-locales.mjs';
import{audit,surface}from'../build/audit-localization.mjs';
import{createHash}from'node:crypto';
const bundle=localeBundle(),report=audit(),output='docs/roadmap/p07-review/';fs.mkdirSync(output,{recursive:true});
const roots=['src/AeroScience.Engine/wwwroot','src/AeroScience.Organization/wwwroot','src/AeroScience.Desktop','msfs-toolbar/PackageSources/html_ui/InGamePanels/AeroScience'];
const files=roots.flatMap(root=>fs.readdirSync(root).filter(name=>/\.(?:js|html|cs)$/.test(name)&&!name.includes('generated')).map(name=>join(root,name))),sources=Object.fromEntries(files.map(path=>[path,fs.readFileSync(path,'utf8')]));
const references=validateReferences(bundle.schema,sources),bySurface={};
for(const[file,source]of Object.entries(sources)){
 const key=surface(file),keys=bySurface[key]??=new Set();
 for(const id of Object.keys(bundle.schema.messages))if(source.includes("'"+id+"'")||source.includes('"'+id+'"'))keys.add(id);
 // Stable namespace suffixes/state families are explicit source contracts.
 for(const match of source.matchAll(/['"]([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9]+)*\.)['"]/g))for(const id of Object.keys(bundle.schema.messages))if(id.startsWith(match[1]))keys.add(id);
}
for(const family of ['consentSafety','errorRecovery'])bySurface[family]=new Set(Object.entries(bundle.schema.messages).filter(([key,rule])=>family==='consentSafety'?rule.surface==='consentSafety'||rule.critical&&/consent|delet|permission|shar|submit|approval|authorize/i.test(key+' '+rule.context):/error|recovery|recover|unavailable|failure|expired|invalid/i.test(key+' '+rule.context)).map(([key])=>key));
let samples=[];if(fs.existsSync('artifacts/web-results.json')){
 const results=JSON.parse(fs.readFileSync('artifacts/web-results.json','utf8'));
 function visit(node){if(!node||typeof node!=='object')return;if(node.name==='rendered-coverage.json'){const raw=node.path&&fs.existsSync(node.path)?fs.readFileSync(node.path,'utf8'):node.body?Buffer.from(node.body,'base64').toString('utf8'):null;if(raw)samples.push(JSON.parse(raw));}for(const value of Object.values(node))if(Array.isArray(value))value.forEach(visit);else if(value&&typeof value==='object')visit(value);}
 visit(results);
}
const surfaces=['dashboard','missions','currentFlight','scientificResults','libraryDownloads','organizations','nativeToolbar','windowsCompanion','consentSafety','errorRecovery','contextualHelp'];
const matrix=surfaces.map(name=>({surface:name,staticReferenceKeys:bySurface[name]?.size||0,locales:Object.fromEntries(bundle.manifest.locales.map(locale=>{
 const keys=[...(bySurface[name]||[])],pending=new Set(bundle.catalogs[locale.id].pendingEnglishFallbackKeys||[]),rendered=samples.filter(sample=>sample.locale===locale.id).flatMap(sample=>sample.samples.filter(s=>s.surface===name)),bound=[...new Set(rendered.flatMap(s=>s.boundMessageKeys))];
 return[locale.id,{bundledKeys:keys.filter(k=>bundle.catalogs[locale.id].messages[k]!==undefined).length,explicitEnglishFallbackKeys:keys.filter(k=>pending.has(k)),sampledBoundMessageKeys:bound,browserSamples:rendered.length,automatedQA:name==='windowsCompanion'?'Resource/API tests and Windows cross-compilation; GUI not tested':name==='nativeToolbar'?'Classic ES2017, real handler and synthetic native panel tests':rendered.length?'Sampled real browser controllers; focused and milestone suites':'Source references and targeted tests; not exhaustive rendered acceptance',humanReview:locale.id==='en-US'?'Canonical source; no translation approval inferred':'PENDING — no qualified fluent review',installedWindows:'NOT TESTED',installedMSFS:'NOT TESTED'}];}))}));
const assets=nativeCatalogAssets(bundle),identity={version:bundle.manifest.catalogVersion,sourceIdentity:bundle.manifest.catalogIdentity,bundleIdentity:bundle.manifest.bundleIdentity,canonicalKeys:Object.keys(bundle.schema.messages).length,nativeProjectedKeys:Object.keys(nativeLocaleBundle(bundle).schema.messages).length,nativeAssets:Object.fromEntries(Object.entries(assets).map(([name,text])=>[name,{bytes:Buffer.byteLength(text),sha256:createHash('sha256').update(text).digest('hex')}]))};
const ambiguity=report.rows.filter(row=>row.decision.startsWith('review'));
fs.writeFileSync(output+'coverage-matrix.json',JSON.stringify({identity,sourcePolicy:report.sourcePolicy,limits:'Static references and sampled explicit DOM bindings are separate measurements. Original source/evidence text and English methodology are not claimed translated. Review classifications are an audit queue, not silently counted as covered.',matrix,sourceAudit:{...report.totals,sourceDigest:report.sourceDigest,manualClassificationQueue:ambiguity.length},unreferencedKeys:references.unusedKeys.map(key=>({key,context:bundle.schema.messages[key].context,status:'No direct static call found: review computed/legacy consumers before removal; not claimed rendered.'})),browserSampleSets:samples.length},null,2)+'\n');
const quote=v=>'"'+String(v??'').replaceAll('"','""').replaceAll('\r',' ')+'"';
fs.writeFileSync(output+'source-inventory-final.tsv',['file\tline\tsurface\tdisposition\trequires_localization\ttext\treason',...report.rows.map(row=>[row.file,row.line,row.surface,row.decision,row.decision==='catalog-reference'?'Explicit binding':row.decision.startsWith('protected')?'No: original identity/value':row.decision.startsWith('review')?'Review classification; not counted covered':'Display/structural candidate; inspect context',row.text,row.reason].map(quote).join('\t'))].join('\n')+'\n');
fs.writeFileSync(output+'bundle-identities.json',JSON.stringify(identity,null,2)+'\n');console.log(JSON.stringify({identity,browserSampleSets:samples.length,manualClassificationQueue:ambiguity.length,unreferencedKeys:references.unusedKeys.length}));
