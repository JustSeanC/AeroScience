import assert from 'node:assert/strict';
import test from 'node:test';
import { createRailwayContext, RAILWAY_GRAPH_VERSION, validateGraph } from 'railway/iac';
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
