import {test} from 'node:test';
import assert from 'node:assert/strict';
import {parse} from 'acorn';
import {compileNative,compileStage} from '../scripts/build/native-javascript.mjs';
import {readFileSync,cpSync,mkdtempSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import vm from 'node:vm';
import {spawnSync} from 'node:child_process';
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
test('0.7.1-beta.2 staged native sources compile with their isolated source-bound identity',()=>{
 const temp=mkdtempSync(join(tmpdir(),'as-beta2-es2017-'));
 try{
  const stage=join(temp,'stage');cpSync('msfs-toolbar',stage,{recursive:true});
  const result=spawnSync('python',['scripts/release/private_validation.py','stage-toolbar','--folder',stage,'--commit','a'.repeat(40),'--beta-version','0.7.1-beta.2'],{encoding:'utf8'});
  assert.equal(result.status,0,result.stderr);const proof=compileStage(stage);assert.ok(proof.files.length>=3);
  const main=readFileSync(join(stage,'PackageSources/html_ui/InGamePanels/AeroScienceBetaV071Beta2/AeroScience.js'),'utf8');
  parse(main,{ecmaVersion:2017});assert.ok(main.includes('AeroScience.Beta.0_7_1_beta_2.Command.v1'));assert.ok(!main.includes('AeroScience.Validation.0_7_0.Command.v1'));
  assert.ok(main.includes('a'.repeat(40)));assert.ok(main.includes('AEROSCIENCE_BETA_V071_BETA2_TOOLBAR_IDENTITY'));
 }finally{rmSync(temp,{recursive:true,force:true});}
});
