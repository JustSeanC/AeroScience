import {test} from 'node:test';
import assert from 'node:assert/strict';
import {parse} from 'acorn';
import {compileNative,compileStage} from '../scripts/build/native-javascript.mjs';
import {readFileSync,cpSync,mkdtempSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import vm from 'node:vm';
test('native compilation removes unsupported syntax without changing null/zero/false semantics',()=>{
 const source='const value = data?.nested?.value ?? 42; globalThis.result = value;';
 assert.throws(()=>parse(source,{ecmaVersion:2017}));
 assert.throws(()=>compileNative('export const unsupported = 1;'));
 const compiled=compileNative(source);parse(compiled,{ecmaVersion:2017});
 for(const [data,expected]of [[null,42],[{},42],[{nested:{value:0}},0],[{nested:{value:false}},false],[{nested:{value:''}},'']]){
  const context={data};vm.runInNewContext(compiled,context);assert.equal(context.result,expected);
 }
});
test('all native panel source assets compile to ES2017 without altering original files',()=>{
 const folder='msfs-toolbar/PackageSources/html_ui/InGamePanels/AeroScience';
 for(const name of ['AeroScience.js','ProofOfConcept.js','WindowProbe.js'])parse(compileNative(readFileSync(join(folder,name),'utf8'),name),{ecmaVersion:2017});
 parse(compileNative(readFileSync('src/AeroScience.Engine/wwwroot/geographic-view.js','utf8')),{ecmaVersion:2017});
 const html=readFileSync(join(folder,'AeroScience.html'),'utf8');for(const inline of html.matchAll(/<script>([\s\S]*?)<\/script>/g))parse(inline[1],{ecmaVersion:5});
 assert.throws(()=>compileStage('msfs-toolbar'),/original sources/);
});
test('minimal bootstrap reports a missing native application without claiming a connection or mutating a flight',()=>{
 const html=readFileSync('msfs-toolbar/PackageSources/html_ui/InGamePanels/AeroScience/AeroScience.html','utf8'),code=[...html.matchAll(/<script>([\s\S]*?)<\/script>/g)][0][1];
 for(const ready of [false,true]){const status={textContent:'Loading simulator communication service...'};const document={documentElement:{contains:()=>true},querySelector:()=>({getAttribute:()=>ready?'ready':null}),getElementById:()=>status};let pending;vm.runInNewContext(code,{document,setTimeout:callback=>{pending=callback;}});pending();assert.equal(status.textContent.includes('did not initialize'),!ready);assert.ok(!status.textContent.includes('Connected'));}
});
test('SDK staging compiles exact asset bytes and rejects original-source mutation',()=>{
 const temp=mkdtempSync(join(tmpdir(),'as-native-es2017-'));
 try{const stage=join(temp,'stage');cpSync('msfs-toolbar',stage,{recursive:true});const r=compileStage(stage);assert.equal(r.target,'ES2017');assert.ok(r.files.length>=3);for(const f of r.files)parse(readFileSync(join(stage,f.path),'utf8'),{ecmaVersion:2017});}
 finally{rmSync(temp,{recursive:true,force:true});}
});
