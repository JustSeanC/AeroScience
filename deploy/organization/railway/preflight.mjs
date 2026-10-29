// Offline publisher/operator check. Never logs in, calls a provider, or treats syntax as ingress proof.
import {readFileSync,statSync,writeFileSync} from 'node:fs';
import {isIP} from 'node:net';
import {parseArgs} from 'node:util';
import {pathToFileURL} from 'node:url';
import {RAILWAY_GRAPH_VERSION,validateGraph} from 'railway/iac';
import {configuration,validateImages} from './.railway/railway.ts';

export function preflight({images,publicDomain,trustedProxies=[]}={}) {
 const checks=[];
 const add=(id,state,message)=>checks.push({id,state,message});
 let graph;
 try {
  validateImages(images);
  const project=configuration(images);
  if(validateGraph({version:RAILWAY_GRAPH_VERSION,project:{name:project.name},environments:[],resources:project.resources,edges:[]}).length)throw Error();
  graph=project;
  add('images','configured','All three role-specific registry manifest digests and the exact source revision are supplied. Publication, signatures and accepted builds still require verification.');
 } catch {add('images','blocked','Accepted immutable application, database and operations image identities are missing or invalid. Docker config IDs and mutable tags are not registry manifest digests.');}
 const domain=typeof publicDomain==='string'&&publicDomain.length<=253&&/^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.up\.railway\.app$/.test(publicDomain);
 add('origin',domain?'configured':'blocked',domain?'A syntactically valid native HTTPS origin is supplied; provider ownership, routing and certificate acceptance remain unverified.':'The exact Railway-generated public HTTPS hostname is missing or invalid. Do not use a placeholder, URL path or an HTTP address.');
 const proxies=Array.isArray(trustedProxies)&&trustedProxies.length>0&&trustedProxies.length<=4&&trustedProxies.every(ip=>typeof ip==='string'&&isIP(ip)>0);
 add('proxies',proxies?'configured':'blocked',proxies?'Explicit proxy addresses have valid syntax. This does not establish that they belong to the authenticated Railway ingress path.':'A verified finite ingress peer configuration is missing. Wildcards, CIDRs, arbitrary forwarding headers and fixture addresses cannot establish the required contract.');
 add('ingress','blocked','No supported authenticated Railway edge-to-service contract has been proven. A gateway alone cannot authenticate its own incoming edge traffic. Public startup remains blocked.');
 add('owner-delivery','blocked','Railway sealed generated values cannot be read from its UI or API. A supported owner-controlled delivery or verified ownership replacement is required; anonymous first-visitor setup remains prohibited.');
 if(graph) {
  const db=graph.resources.find(r=>r.name==='PostgreSQL'),app=graph.resources.find(r=>r.name==='Organization');
  const safe=!db.networking&&!app.networking&&Object.keys(db.volumeAttachments).length===1&&Object.keys(app.volumeAttachments).length===1&&app.deploy.requiredMountPath==='/configuration';
  add('storage',safe?'configured':'blocked',safe?'The offline SDK graph retains private PostgreSQL and database/identity volumes. Native references, mount behavior and actual durability require provider acceptance.':'The resource graph is missing its private database or required persistent volumes.');
 }
 add('independent-recovery','pending-p28','The native Bucket is in the same project. Independent account access, saved recovery keys and external disaster restoration must pass P28; a second URL alone proves no independence.');
 add('railway-account','pending-p28','Railway handles account sign-in, billing setup and charge authorization in its own interfaces. No account action is needed for this offline check.');
 return {format:'aeroscience-railway-preflight',version:1,status:'Blocked — Deployment Candidate',readyForDeployment:false,productionReady:false,providerCalls:0,paidResourcesCreated:false,publicAssetsPublished:false,checks};
}

export function main(args=process.argv.slice(2)) {
 const {values}=parseArgs({args,options:{images:{type:'string'},'public-domain':{type:'string'},'trusted-proxy':{type:'string',multiple:true},output:{type:'string'}}});
 let images;
 if(values.images)try{const file=statSync(values.images);if(!file.isFile()||file.size>16384)throw Error();const data=readFileSync(values.images);if(data.length>16384)throw Error();images=JSON.parse(data.toString('utf8'));}catch{/* Report only the bounded, generic validation result; never echo untrusted file content. */}
 const report=preflight({images,publicDomain:values['public-domain'],trustedProxies:values['trusted-proxy']});
 const text=JSON.stringify(report,null,2)+'\n';
 if(values.output)writeFileSync(values.output,text,{mode:0o600});
 process.stdout.write(text);
 return report.readyForDeployment?0:2;
}
if(process.argv[1]&&import.meta.url===pathToFileURL(process.argv[1]).href)process.exitCode=main();
