// Assemble auditable, offline candidate resources. English fallbacks are explicit;
// neither machine output nor developer overrides constitute qualified human review.
import fs from'node:fs';import{createHash}from'node:crypto';
import{sourceFingerprint,messageFingerprint,bundleFingerprint,validUnicode,preservedTokens}from'../build/localization-quality.mjs';
const base='src/AeroScience.Engine/wwwroot/i18n/',read=p=>JSON.parse(fs.readFileSync(base+p,'utf8'));
const english=read('locales/en-US.json'),schema=read('message-schema.json'),manifest=read('locale-manifest.json');
const overrides={...JSON.parse(fs.readFileSync('scripts/localization/candidate-overrides.json','utf8')),...JSON.parse(fs.readFileSync('scripts/localization/critical-candidate-overrides.json','utf8'))},sourceHash=sourceFingerprint(english.messages),pending={},receipts={};
const foldedOverrides=new Map(Object.entries(overrides).map(([source,translations])=>[source.toLowerCase(),translations]));
if(manifest.locales.some(locale=>locale.review==='reviewed'))throw Error('Do not overwrite a human-reviewed catalog with machine candidates. Preserve the reviewed edits and evidence.');
for(const [key,source]of Object.entries(english.messages))schema.messages[key].preservedTokens=[...new Set((typeof source==='string'?[source]:Object.values(source)).flatMap(preservedTokens))];
const shared={
 'es-ES':['Color','Control','Error','Experimental','Local','Total','Variable','Zoom'],
 'fr-FR':['Science','Sources','Source','Altitude','Latitude','Longitude','Classification','Description','Version','Position','Distance','Options','Action','Information','Mode','Zoom'],
 'de-DE':['Downloads','Status','Version','Name','Instrument','Parameter','Sensor','Signal','Zoom'],
 'pt-BR':['Downloads','Status','Total','Local','Experimental','Material','Zoom']};
const common=['AeroScience','MSFS','MSFS 2024','NASA','NASA Earthdata','DEM','DSM','LiDAR','COPC','EPT','LAS','LAZ','GeoTIFF','SHA-256','UTC','Discord','GitHub','ICAO','IATA','NoData','Auto','Offline','Online','Mach','NDVI','NDWI','NDMI'];
const negatives={'es-ES':/\b(?:no|nunca|sin|tampoco|ning[uú]n\w*|imposible)\b/i,'fr-FR':/\b(?:ne|n’|pas|non|sans|aucun\w*|jamais|impossible|inconnu\w*|indisponible\w*|introuvable\w*)\b/i,'de-DE':/\b(?:nicht|kein\w*|ohne|nie|niemals|unmöglich|unbekannt\w*|unverfügbar\w*)\b/i,'pt-BR':/\b(?:não|nunca|sem|nenhum\w*|impossível)\b/i};
const corrections={
'es-ES':[['colección científica','adquisición de datos científicos'],['colección de ciencia','adquisición de datos científicos'],['colección de ciencias','adquisición de datos científicos'],['colección de datos científicos','adquisición de datos científicos'],['rendimientos de LiDAR','retornos LiDAR'],['rendimientos históricos de LiDAR','retornos LiDAR históricos'],['desdone','deshecho']],
'fr-FR':[['collection scientifique','collecte scientifique'],['collection de données scientifiques','collecte de données scientifiques'],['enregistrement de vol','enregistrement du vol']],
'de-DE':[['wissenschaftliche Sammlung','wissenschaftliche Datenerfassung'],['wissenschaftlichen Sammlung','wissenschaftlichen Datenerfassung']],
'pt-BR':[['colecção','coleta'],['coleção científica','coleta científica'],['recolha científica','coleta científica'],['recolha de dados científicos','coleta de dados científicos'],['ficheiros','arquivos'],['ficheiro','arquivo'],['utilizadores','usuários'],['utilizador','usuário'],['ecrã','tela'],['registo de voo','gravação do voo']]};
for(const [index,locale]of ['es-ES','fr-FR','de-DE','pt-BR'].entries()){
 const cacheFile='artifacts/p07-drafts-'+locale+'.json',cache=fs.existsSync(cacheFile)?JSON.parse(fs.readFileSync(cacheFile,'utf8')):{},messages={},fallbacks=new Map();let explicit=0,drafted=0;
 for(const [key,source]of Object.entries(english.messages)){
  function translate(text){
   const override=overrides[text]||foldedOverrides.get(text.toLowerCase());
   if(override){explicit++;return override[index];}
   const entry=cache[createHash('sha256').update(text).digest('hex')];let translated=entry?.draft;
   const reason=!entry?'Candidate draft pending':entry.needsEnglishFallback?entry.reason:null;
   if(reason){fallbacks.set(key,reason);return text;}
   for(const [from,to]of corrections[locale])translated=translated.replaceAll(from,to);
   if(preservedTokens(text).some(token=>!translated.includes(token))){fallbacks.set(key,'Scientific identifier or unit changed in draft; English retained');return text;}
   const parameters=s=>[...new Set([...s.matchAll(/\{([a-z][a-zA-Z0-9]*)\}/g)].map(m=>m[1]))].sort();
   if(!validUnicode(translated)||/(.{2,50}?)\1{3,}/u.test(translated)||/(\p{L}{2,})(?:[^\p{L}]+\1){4,}/iu.test(translated)||[...translated].length>(schema.messages[key].maximumLength||4096)||JSON.stringify(parameters(text))!==JSON.stringify(parameters(translated))||/[{}]/.test(translated.replace(/\{[a-z][a-zA-Z0-9]*\}/g,''))){fallbacks.set(key,'Unicode, repetition, layout or placeholder QA requires review');return text;}
   if(schema.messages[key].critical&&/\b(?:not|never|cannot|no)\b/i.test(text)&&!negatives[locale].test(translated)){fallbacks.set(key,'Critical negation requires fluent review; English retained');return text;}
   drafted++;return translated;
  }
  const translated=typeof source==='string'?translate(source):Object.fromEntries(Object.entries(source).map(([form,text])=>[form,translate(text)]));
  const same=JSON.stringify(source)===JSON.stringify(translated),allowed=[...common,...shared[locale]].includes(source)||typeof schema.messages[key].sameTextAllowed?.[locale]==='string';
  if(same&&allowed){schema.messages[key].sameTextAllowed??={};schema.messages[key].sameTextAllowed[locale]??='Established scientific identifier, product name, aviation notation, loanword or identical target-language term; retained intentionally.';fallbacks.delete(key);}
  else if(same)fallbacks.set(key,fallbacks.get(key)||'Unchanged English requires translation or a documented terminology decision');
  if(fallbacks.has(key))messages[key]=source;else messages[key]=translated;
  if(typeof source==='object'&&['es-ES','fr-FR','pt-BR'].includes(locale))messages[key]={...messages[key],many:messages[key].other};
 }
 const catalog={version:manifest.catalogVersion,locale,review:'candidate',sourceHash,sourceMessages:Object.fromEntries(Object.entries(english.messages).map(([k,v])=>[k,messageFingerprint(v)])),pendingEnglishFallbackKeys:[...fallbacks.keys()].sort(),messages};
 // Fallback entries preserve original form shape for exact copied-English accounting.
 for(const key of fallbacks.keys())catalog.messages[key]=english.messages[key];
 fs.writeFileSync(base+'locales/'+locale+'.json',JSON.stringify(catalog,null,2)+'\n');
 manifest.locales.find(l=>l.id===locale).review='candidate';pending[locale]=Object.fromEntries(fallbacks);receipts[locale]={keys:Object.keys(messages).length,explicitCandidateForms:explicit,machineDraftForms:drafted,englishFallbackKeys:fallbacks.size,humanReview:'PENDING — no qualified human review supplied',installedWindows:'NOT TESTED',installedMSFS:'NOT TESTED'};
}
manifest.catalogIdentity=sourceHash;manifest.qualityContract=1;
manifest.bundleIdentity=bundleFingerprint(manifest,Object.fromEntries(manifest.locales.map(l=>[l.id,read("locales/"+l.id+".json")])));
for(const [path,value]of[['locale-manifest.json',manifest],['message-schema.json',schema]])fs.writeFileSync(base+path,JSON.stringify(value,null,2)+'\n');
fs.mkdirSync('docs/roadmap/p07-review',{recursive:true});fs.writeFileSync('artifacts/p07-candidate-qa.json',JSON.stringify({version:manifest.catalogVersion,sourceHash,receipts,pending},null,2)+'\n');
console.log(JSON.stringify(receipts));
