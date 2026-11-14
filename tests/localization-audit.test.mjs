import test from 'node:test';
import assert from 'node:assert/strict';
import {inventoryJavaScript} from '../scripts/build/audit-localization.mjs';
import {strictJson,validUnicode,validateQuality,sourceFingerprint,messageFingerprint} from '../scripts/build/localization-quality.mjs';

test('inventory distinguishes presentation from command IDs, style, original rows and user field values',()=>{
 const src=`node('p','No valid source is available.'); button(root,'Pause Science',run,'button primary'); field(root,'Mission name','text','Pilot original name'); table(root,'Original readings',['Recorded UTC','Value'],[['Original evidence text','12.345 m']]); api('/api/mission/control',{action:'startData',id:'Original.Source.v1'}); node('p',user.name);`;
 const rows=inventoryJavaScript(src,'src/AeroScience.Engine/wwwroot/example.js');
 for(const text of ['No valid source is available.','Pause Science','Mission name','Original readings','Recorded UTC','Value'])assert.equal(rows.find(r=>r.text===text)?.decision,'presentation',text);
 for(const text of ['button primary','Pilot original name','Original evidence text','12.345 m'])assert.ok(rows.find(r=>r.text===text)?.decision.startsWith('protected'),text);
 assert.ok(!rows.some(r=>r.text==='user.name'));
});
test('strict catalog parsing rejects duplicate message keys and malformed Unicode without normalizing evidence',()=>{
 assert.throws(()=>strictJson('{"messages":{"nav.home":"Home","nav.home":"Changed"}}'),/Duplicate JSON key/);
 assert.equal(strictJson('{"value":"Zoë 東京 µm $&"}').value,'Zoë 東京 µm $&');
 for(const value of ['\uD800','\uDC00','bad\u0000value','\u202Ehidden'])assert.equal(validUnicode(value),false);
 assert.equal(validUnicode('Français\nPortuguês — ³ \u{1F6E9}'),true);
});
test('source tracking rejects copied critical English, stale translations and fabricated review',()=>{
 const english={'warning.delete':'Permanently delete {count} files?'},hash=sourceFingerprint(english);
 const data={manifest:{qualityContract:1,catalogIdentity:hash,locales:[{id:'en-US',review:'source'},{id:'es-ES',review:'candidate'}]},schema:{messages:{'warning.delete':{critical:true,parameters:['count'],maximumLength:100}}},catalogs:{'en-US':{messages:english},'es-ES':{messages:{'warning.delete':'¿Eliminar permanentemente {count} archivos?'},sourceHash:hash,sourceMessages:{'warning.delete':messageFingerprint(english['warning.delete'])}}}};
 assert.equal(validateQuality(data).coverage['es-ES'].humanReviewed,false);
 data.catalogs['es-ES'].messages={...english};assert.throws(()=>validateQuality(data),/Copied English|Untranslated critical/);
 data.catalogs['es-ES'].pendingEnglishFallbackKeys=['warning.delete'];assert.equal(validateQuality(data).coverage['es-ES'].explicitEnglishFallbackKeys.length,1);
 data.catalogs['es-ES'].sourceMessages['warning.delete']='outdated';assert.throws(()=>validateQuality(data),/Stale message source/);
 data.manifest.locales[1].review='reviewed';assert.throws(()=>validateQuality(data),/qualified human review/);
});
