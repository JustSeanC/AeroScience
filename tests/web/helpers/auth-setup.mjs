// One paired seed for this single local test run. Browser contexts remain isolated,
// but discarded contexts no longer leak 128 live server sessions. Production session
// limits, authentication and CSRF checks remain unchanged.
import {request} from '@playwright/test';
import {mkdirSync,writeFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
export default async function setup(config){
 const baseURL=config.projects[0].use.baseURL;
 const client=await request.newContext({baseURL});
 try{
  const response=await client.post('/api/session',{data:{},headers:{Origin:baseURL}});
  if(!response.ok())throw Error('Browser test seed pairing failed: '+response.status());
  const target=resolve('artifacts/browser-auth.json');mkdirSync(dirname(target),{recursive:true});
  writeFileSync(target,JSON.stringify(await client.storageState()),{mode:0o600});
 }finally{await client.dispose();}
}
