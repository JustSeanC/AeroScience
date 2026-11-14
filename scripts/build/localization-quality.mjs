import {parse} from 'acorn';
import {createHash} from 'node:crypto';

export const sourceFingerprint=messages=>createHash('sha256').update(JSON.stringify(Object.fromEntries(Object.keys(messages).sort().map(key=>[key,messages[key]])))).digest('hex');
export const messageFingerprint=message=>createHash('sha256').update(JSON.stringify(message)).digest('hex');
export function strictJson(source,name='catalog'){
 const value=JSON.parse(source),ast=parse('('+source+')',{ecmaVersion:2022});
 function visit(n){if(!n||typeof n!=='object')return;if(n.type==='ObjectExpression'){const keys=new Set();for(const p of n.properties){const key=p.key.value??p.key.name;if(keys.has(key))throw Error('Duplicate JSON key in '+name+': '+key);keys.add(key);}}
  for(const [key,v]of Object.entries(n))if(key!=='start'&&key!=='end'){if(Array.isArray(v))v.forEach(visit);else if(v&&typeof v==='object')visit(v);}}
 visit(ast);return value;
}
export function validUnicode(text){
 for(let i=0;i<text.length;i++){const c=text.charCodeAt(i);if(c>=0xD800&&c<=0xDBFF){const next=text.charCodeAt(++i);if(!(next>=0xDC00&&next<=0xDFFF))return false;}else if(c>=0xDC00&&c<=0xDFFF)return false;}
 return !/[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F\u202A-\u202E\u2066-\u2069\uFEFF]/u.test(text);
}
export function validateQuality({manifest,schema,catalogs}){
 const failures=[],require=(ok,reason)=>{if(!ok)failures.push(reason);},english=catalogs['en-US'],fingerprint=sourceFingerprint(english.messages),coverage={};
 const enabled=manifest.qualityContract===1;
 if(enabled){require(manifest.catalogIdentity===fingerprint,'Stale canonical catalog identity.');require(manifest.locales.filter(l=>l.review==='reviewed').every(l=>l.humanReview?.qualifiedReviewer&&l.humanReview?.evidence&&l.humanReview?.sourceHash===fingerprint),'Reviewed locale requires documented qualified human review of this source.');}
 for(const [locale,catalog]of Object.entries(catalogs)){
  const metadata=manifest.locales.find(l=>l.id===locale),pending=new Set(catalog.pendingEnglishFallbackKeys||[]);let translated=0,retained=0,critical=0;
  if(enabled&&locale!=='en-US'){
   require(catalog.sourceHash===fingerprint,'Changed English source requires translation review: '+locale);
   require(metadata?.review==='candidate'||metadata?.review==='reviewed'||metadata?.review==='preview','Locale requires an explicit review state: '+locale);
   require([...pending].every(k=>schema.messages[k]),'Unknown pending English fallback key: '+locale);
  }
  for(const [key,message]of Object.entries(catalog.messages)){
   const rule=schema.messages[key];if(!rule)continue;
   for(const text of typeof message==='string'?[message]:Object.values(message||{})){
    require(typeof text==='string'&&validUnicode(text),'Malformed Unicode/control character: '+locale+'/'+key);
    require(typeof text==='string'&&[...text].length<=(rule.maximumLength||4096),'Message exceeds declared text limit: '+locale+'/'+key);
   }
   if(locale!=='en-US'){
    const identical=JSON.stringify(message)===JSON.stringify(english.messages[key]),allowed=typeof rule.sameTextAllowed?.[locale]==='string'&&rule.sameTextAllowed[locale].trim().length>10;
    if(identical&&allowed)retained++;else if(!identical)translated++;
    if(rule.critical){critical++;require(!enabled||!identical||allowed||pending.has(key),'Untranslated critical message without explicit English fallback/review status: '+locale+'/'+key);}
    if(enabled){require(catalog.sourceMessages?.[key]===messageFingerprint(english.messages[key]),'Stale message source: '+locale+'/'+key);require(!identical||allowed||pending.has(key),'Copied English is not a completed translation: '+locale+'/'+key);}
   }
  }
  coverage[locale]={keys:Object.keys(catalog.messages).length,translated,retainedTerms:retained,explicitEnglishFallbackKeys:[...pending],criticalKeys:critical,humanReviewed:metadata?.review==='reviewed'};
 }
 if(failures.length)throw Error(failures.join('\n'));return {catalogIdentity:fingerprint,coverage};
}
