// Official Railway IaC authoring API (SDK locked in ../package-lock.json).
// This declares a private candidate graph; it never authenticates, applies or publishes it.
import { readFileSync, statSync } from 'node:fs';
import {createPublicKey} from 'node:crypto';
import { bucket, defineRailway, image, project, ref, service, volume } from 'railway/iac';

const fields = ['version', 'sourceRevision', 'application', 'database', 'operations'];
export function validateImages(input) {
  if (!input || Object.keys(input).some(k => !fields.includes(k)) || input.version !== 1 || !/^[a-f0-9]{40}$/.test(input.sourceRevision ?? '')) throw Error('Supply the exact accepted source and immutable distribution identities.');
  for (const key of ['application', 'database', 'operations']) {
    const suffix = {application:'railway',database:'database',operations:'operations'}[key];
    if (!new RegExp('^ghcr\\.io/justseanc/aeroscience-organization-'+suffix+'@sha256:[a-f0-9]{64}$').test(input[key] ?? '')) throw Error('Only role-specific digest-pinned official distribution identities are supported.');
  }
  if (new Set([input.application, input.database, input.operations]).size !== 3) throw Error('Application, database and operations must have independent image identities.');
  return input;
}
export function validateOwnerPairing(value) {
  if(typeof value!=='string'||value.length>256||!value.match(/^[a-f0-9-]{36}\.[A-Za-z0-9+/=]{100,200}$/))throw Error('Supply the public ownership pairing created in AeroScience. No private key or sealed setup secret belongs here.');
  const [id,encoded]=value.split('.');if(!/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/.test(id)||id==='00000000-0000-0000-0000-000000000000')throw Error('Invalid ownership intent.');
  const key=createPublicKey({key:Buffer.from(encoded,'base64'),format:'der',type:'spki'});
  if(key.asymmetricKeyType!=='ec'||key.asymmetricKeyDetails.namedCurve!=='prime256v1')throw Error('Only supported public ownership keys can be deployed.');return value;
}
export function configuration(images,ownerPairing,templatePrompt=false) {
  validateImages(images);
  if(!templatePrompt)validateOwnerPairing(ownerPairing);
  else if(ownerPairing!==undefined)throw Error('A reusable template must never bake in an author ownership verifier.');
  const secret = () => ({ value: '${{secret(64, "abcdef0123456789")}}', isSealed: true });
  const data = volume('organization-database', { sizeMB: 4096 });
  const identity = volume('organization-identity', { sizeMB: 1024 });
  // A private service with the existing verified-CA/role initializer, not a default database
  // helper which could substitute a different image or expose a public database endpoint.
  const db = service('PostgreSQL', {
    source: image(images.database, { autoUpdates: { type: 'disabled' } }), replicas: 1,
    volumeMounts: { '/var/lib/postgresql/data': data },
    env: {
      POSTGRES_DB: 'aeroscience', POSTGRES_USER: 'postgres', PGUSER: 'postgres',
      POSTGRES_PASSWORD: secret(), POSTGRES_INITDB_ARGS: '--encoding=UTF8 --auth-host=scram-sha-256',
      AEROSCIENCE_DATABASE_TRUST_SECRET: secret(),
      AEROSCIENCE_APPLICATION_DATABASE_PASSWORD: secret(), AEROSCIENCE_RECOVERY_DATABASE_PASSWORD: secret()
    }
  });
  const recovery = bucket('Recovery', { region: 'iad' });
  const app = service('Organization', {
    source: image(images.application, { autoUpdates: { type: 'disabled' } }), replicas: 1,
    healthcheck: '/healthz/ready', healthcheckTimeout: 120,
    volumeMounts: { '/configuration': identity },
    deploy: { requiredMountPath: '/configuration', restartPolicyType: 'ON_FAILURE', restartPolicyMaxRetries: 3, overlapSeconds: 0, drainingSeconds: 30 },
    env: {
      RAILWAY_RUN_UID: '0', // fixed volume initializer permanently drops to UID/GID 1654 before serving
      AEROSCIENCE_OWNER_PAIRING: templatePrompt?{value:null,defaultValue:null,isOptional:false,description:'Ownership Key: paste the public value from Create Organization Server in AeroScience. A new key is required for your own server. No private key belongs here.'}:ownerPairing, Organization__KeyCertificatePassword: secret(),
      AEROSCIENCE_DATABASE_HOST: db.env.RAILWAY_PRIVATE_DOMAIN,
      AEROSCIENCE_DATABASE_NAME: db.env.POSTGRES_DB, AEROSCIENCE_DATABASE_USER: 'aeroscience_application',
      AEROSCIENCE_DATABASE_PASSWORD: db.env.AEROSCIENCE_APPLICATION_DATABASE_PASSWORD,
      AEROSCIENCE_DATABASE_TRUST_SECRET: db.env.AEROSCIENCE_DATABASE_TRUST_SECRET,
      AEROSCIENCE_RECOVERY_DATABASE_USER: 'aeroscience_recovery',
      AEROSCIENCE_RECOVERY_DATABASE_PASSWORD: db.env.AEROSCIENCE_RECOVERY_DATABASE_PASSWORD,
      AEROSCIENCE_RECOVERY_REPOSITORY: 's3:${{Recovery.ENDPOINT}}/${{Recovery.BUCKET}}/aeroscience',
      AEROSCIENCE_RECOVERY_REPOSITORY_PASSWORD: secret(), AEROSCIENCE_RECOVERY_BUCKET_LOOKUP: 'dns',
      AWS_ACCESS_KEY_ID: ref(recovery, 'ACCESS_KEY_ID'), AWS_SECRET_ACCESS_KEY: ref(recovery, 'SECRET_ACCESS_KEY'), AWS_DEFAULT_REGION: ref(recovery, 'REGION')
    }
  });
  // Public generated domains are not authored by the documented IaC DSL. No fabricated
  // domain, proxy, template ID or independent-account credential is silently supplied.
  // The authenticated local gateway needs no provider proxy-address configuration.
  // Template publication must still supply native generated-domain configuration.
  return project('AeroScience Organization', { resources: [db, app, data, identity, recovery] });
}
// Native SDK VariableConfig metadata for the publisher's reusable-template draft.
// NOT a published marketplace template/import format. Native HTTPS domain/prompt
// behavior must be checked in the authorized Railway composer before publication.
export function templateDraft(images){return configuration(images,undefined,true);}
export default defineRailway(() => {
  const path = process.env.AEROSCIENCE_RAILWAY_IMAGE_SET;
  if (!path) throw Error('No accepted immutable image distribution is configured. This candidate cannot be deployed.');
  const stat=statSync(path);if(!stat.isFile()||stat.size>16384)throw Error('Image distribution metadata must be a bounded regular file.');
  const bytes = readFileSync(path); if (bytes.length > 16384) throw Error('Image distribution metadata is too large.');
  return configuration(validateImages(JSON.parse(bytes.toString('utf8'))),process.env.AEROSCIENCE_OWNER_PAIRING);
});
