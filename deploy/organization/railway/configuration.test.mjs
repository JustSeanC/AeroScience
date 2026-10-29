import assert from 'node:assert/strict';
import test from 'node:test';
import { createRailwayContext, RAILWAY_GRAPH_VERSION, validateGraph } from 'railway/iac';
import {readFileSync,mkdtempSync,writeFileSync,rmSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {preflight} from './preflight.mjs';
import candidate, { configuration, validateImages } from './.railway/railway.ts';
const images={version:1,sourceRevision:'a'.repeat(40),application:'ghcr.io/justseanc/aeroscience-organization-railway@sha256:'+'b'.repeat(64),database:'ghcr.io/justseanc/aeroscience-organization-database@sha256:'+'c'.repeat(64),operations:'ghcr.io/justseanc/aeroscience-organization-operations@sha256:'+'d'.repeat(64)};
test('official SDK accepts the private resource graph without accessing Railway',()=>{
 const p=configuration(images);assert.deepEqual(validateGraph({version:RAILWAY_GRAPH_VERSION,project:{name:p.name},environments:[],resources:p.resources,edges:[]}),[]);
 const db=p.resources.find(r=>r.name==='PostgreSQL'),app=p.resources.find(r=>r.name==='Organization');
 assert.equal(db.source.image,images.database);assert.equal(app.source.image,images.application);assert.equal(app.source.autoUpdates.type,'disabled');
 assert.equal(app.deploy.requiredMountPath,'/configuration');assert.equal(Object.keys(app.volumeAttachments).length,1);assert.equal(Object.keys(db.volumeAttachments).length,1);
 assert.equal(app.variables.AEROSCIENCE_DATABASE_PASSWORD.type,'reference');assert.equal(app.variables.AWS_ACCESS_KEY_ID.resource,'bucket.Recovery');
 assert.equal(app.variables.Organization__BootstrapSecret.value.value,'${{secret(64, "abcdef0123456789")}}');assert.equal(app.variables.Organization__BootstrapSecret.value.isSealed,true);
 assert.equal(app.networking,undefined);assert.equal(db.networking,undefined);assert.equal(app.variables.Organization__TrustedProxies,undefined);
 assert.equal(p.resources.filter(r=>r.type==='bucket').length,1);
});
test('no fake distribution or account operation is substituted for missing release assets',()=>{
 delete process.env.AEROSCIENCE_RAILWAY_IMAGE_SET;assert.throws(()=>candidate(createRailwayContext({environment:'production'})),/cannot be deployed/);
 for(const value of [{...images,version:2},{...images,application:'ghcr.io/justseanc/aeroscience-organization:latest'},{...images,sourceRevision:'main'},{...images,password:'secret'},{...images,database:images.application}])assert.throws(()=>validateImages(value));
});
test('documentary recipe and actual native graph have identical generated variables and service references',()=>{
 const recipe=JSON.parse(readFileSync(new URL('./template.candidate.json',import.meta.url)));
 const graph=configuration(images);
 for(const name of ['PostgreSQL','Organization']){
  const variables=graph.resources.find(r=>r.name===name).variables;
  const rendered=Object.fromEntries(Object.entries(variables).map(([key,v])=>[key,v.type==='reference'?'${{'+v.resource.split('.').slice(1).join('.')+'.'+v.output+'}}':v.type==='raw'?v.value.value:v.value]));
  assert.deepEqual(rendered,recipe.services[name].variables);
 }
 assert.equal(recipe.launchEnabled,false);assert.equal(recipe.validationTiming.liveProviderAcceptance.includes('P28'),true);
});
test('preflight refuses missing and spoofed origin or proxy inputs without claiming provider acceptance',()=>{
 const missing=preflight();assert.equal(missing.readyForDeployment,false);assert.equal(missing.providerCalls,0);assert.equal(missing.checks.filter(c=>c.state==='blocked').length,5);
 for(const publicDomain of ['http://example.up.railway.app','example.up.railway.app/path','example.com','example.up.railway.app:443','example.up.railway.app\nX: spoof'])assert.equal(preflight({images,publicDomain}).checks.find(c=>c.id==='origin').state,'blocked');
 for(const trustedProxies of [['*'],['10.0.0.0/8'],['X-Railway-Edge'],Array(5).fill('127.0.0.1')])assert.equal(preflight({images,trustedProxies}).checks.find(c=>c.id==='proxies').state,'blocked');
 const syntaxOnly=preflight({images,publicDomain:'test-fixture.up.railway.app',trustedProxies:['127.0.0.1']});
 assert.equal(syntaxOnly.checks.find(c=>c.id==='origin').state,'configured');assert.equal(syntaxOnly.checks.find(c=>c.id==='ingress').state,'blocked');assert.equal(syntaxOnly.readyForDeployment,false);assert.equal(syntaxOnly.productionReady,false);assert.equal(syntaxOnly.paidResourcesCreated,false);
 assert.equal(syntaxOnly.checks.find(c=>c.id==='owner-delivery').state,'blocked');
});
test('offline preflight CLI reports bounded generic failures without echoing untrusted secrets',()=>{
 const directory=mkdtempSync(join(tmpdir(),'aeroscience-railway-preflight-'));
 try{
  const path=join(directory,'images.json');
  for(const content of ['{"private":"NEVER_ECHO_THIS_SECRET"',JSON.stringify({...images,token:'NEVER_ECHO_THIS_SECRET'}),'x'.repeat(16385)]){
   writeFileSync(path,content);
   const result=spawnSync(process.execPath,['--experimental-strip-types',fileURLToPath(new URL('./preflight.mjs',import.meta.url)),'--images',path],{encoding:'utf8'});
   assert.equal(result.status,2);assert.equal((result.stdout+result.stderr).includes('NEVER_ECHO_THIS_SECRET'),false);
   const report=JSON.parse(result.stdout);assert.equal(report.readyForDeployment,false);assert.equal(report.checks.find(c=>c.id==='images').state,'blocked');
  }
 }finally{rmSync(directory,{recursive:true,force:true});}
});
