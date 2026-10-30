#!/usr/bin/env python3
"""Disposable Railway-shaped runtime acceptance. No cloud account, deployment template or billing.
Tests the real pinned PostgreSQL image's SAN/private CA and permanent application privilege drop.
"""
import argparse
import base64
import subprocess
import json
import os
from pathlib import Path
import secrets
import tempfile
import time
import uuid
from verify_organization_container import DOCKER,execute

def verify(application,database,output):
    report={'productionReady':False,'fiveStageAccepted':False,'scope':'Disposable local native-runtime fixture, not Railway provider or novice acceptance','checks':[]}
    identity='aeroscience-p26-'+uuid.uuid4().hex[:10];network=identity+'-network';state=identity+'-identity';records=identity+'-database';app=identity+'-app';db=identity+'-postgres'
    created=[]
    def docker(*args,**kwargs):return execute(DOCKER+list(args),**kwargs)
    def record(name):report['checks'].append(name);print('PASS: '+name,flush=True)
    with tempfile.TemporaryDirectory(prefix='aeroscience-p26-railway-') as temporary:
        root=Path(temporary);os.chmod(root,0o700);trust=secrets.token_hex(32);password=secrets.token_hex(32);appPassword=secrets.token_hex(32);recoveryPassword=secrets.token_hex(32);key=secrets.token_hex(32)
        ownerKey=root/'owner.pem';subprocess.run(['openssl','ecparam','-name','prime256v1','-genkey','-noout','-out',str(ownerKey)],check=True,capture_output=True);ownerKey.chmod(0o600)
        public=subprocess.run(['openssl','pkey','-in',str(ownerKey),'-pubout','-outform','DER'],check=True,capture_output=True).stdout
        owner=str(uuid.uuid4())+'.'+base64.b64encode(public).decode()
        project,environment,service=(str(uuid.uuid4()) for _ in range(3))
        databaseEnv=root/'database.env';databaseEnv.write_text('POSTGRES_DB=aeroscience\nPOSTGRES_USER=postgres\nPGUSER=postgres\nPOSTGRES_PASSWORD='+password+'\nAEROSCIENCE_APPLICATION_DATABASE_PASSWORD='+appPassword+'\nAEROSCIENCE_RECOVERY_DATABASE_PASSWORD='+recoveryPassword+'\nRAILWAY_PRIVATE_DOMAIN=postgres.railway.internal\nAEROSCIENCE_DATABASE_TRUST_SECRET='+trust+'\n');databaseEnv.chmod(0o600)
        applicationEnv=root/'application.env';applicationEnv.write_text('RAILWAY_PUBLIC_DOMAIN=synthetic.up.railway.app\nAEROSCIENCE_DATABASE_HOST=postgres.railway.internal\nAEROSCIENCE_DATABASE_NAME=aeroscience\nAEROSCIENCE_DATABASE_USER=aeroscience_application\nAEROSCIENCE_DATABASE_PASSWORD='+appPassword+'\nAEROSCIENCE_DATABASE_TRUST_SECRET='+trust+'\nAEROSCIENCE_OWNER_PAIRING='+owner+'\nOrganization__KeyCertificatePassword='+key+'\nRAILWAY_PROJECT_ID='+project+'\nRAILWAY_ENVIRONMENT_ID='+environment+'\nRAILWAY_SERVICE_ID='+service+'\n');applicationEnv.chmod(0o600)
        def start(image=application):docker('run','--detach','--name',app,'--network',network,'--env-file',str(applicationEnv),'--mount','type=volume,src='+state+',dst=/configuration',image)
        def ready(name):
            until=time.monotonic()+90
            while time.monotonic()<until:
                info=json.loads(docker('inspect',name).stdout)[0]
                if info['State']['Status']=='exited':raise RuntimeError('Restricted runtime refused its configuration; inspect sanitized fixture diagnostics')
                if info['State'].get('Health',{}).get('Status')=='healthy':return
                time.sleep(1)
            raise RuntimeError('Runtime health did not complete within the original 90-second bound')
        try:
            docker('network','create','--internal',network);docker('volume','create',state);docker('volume','create',records)
            docker('run','--detach','--name',db,'--network',network,'--network-alias','postgres.railway.internal','--env-file',str(databaseEnv),'--mount','type=volume,src='+records+',dst=/var/lib/postgresql/data',database);created.append(db)
            until=time.monotonic()+90
            while time.monotonic()<until:
                if docker('exec',db,'pg_isready','-h','127.0.0.1','-U','postgres','-d','aeroscience',check=False).returncode==0:break
                time.sleep(1)
            else:raise RuntimeError('Pinned Railway PostgreSQL fixture did not become ready')
            # Verify actual certificate chain and private hostname, not the claim that SSL was enabled.
            certificate=docker('exec',db,'openssl','x509','-in','/var/lib/postgresql/data/certs/server.crt','-noout','-ext','subjectAltName').stdout
            assert 'DNS:postgres.railway.internal' in certificate
            assert docker('exec',db,'psql','-U','postgres','-d','aeroscience','-Atc',"SELECT rolsuper OR rolcreatedb OR rolcreaterole FROM pg_roles WHERE rolname='aeroscience_application'").stdout.strip()=='f'
            positive=docker('exec',db,'openssl','verify','-x509_strict','-purpose','sslserver','-CAfile','/var/lib/postgresql/data/certs/root.crt','-verify_hostname','postgres.railway.internal','/var/lib/postgresql/data/certs/server.crt')
            negative=docker('exec',db,'openssl','verify','-x509_strict','-purpose','sslserver','-CAfile','/var/lib/postgresql/data/certs/root.crt','-verify_hostname','wrong.railway.internal','/var/lib/postgresql/data/certs/server.crt',check=False)
            assert positive.returncode==0 and negative.returncode!=0;record('actual pinned Railway PostgreSQL CA validates its exact private hostname and rejects a wrong hostname')
            start();created.append(app);ready(app)
            processes=[row for row in docker('top',app).stdout.splitlines()[1:] if 'AeroScience.Organization.dll --railway-start' in row];assert len(processes)==1 and processes[0].split()[0]=='1654'
            original=docker('exec',db,'psql','-U','postgres','-d','aeroscience','-Atc','SELECT "CertificateSha256" FROM organization."DeploymentState"').stdout.strip();assert len(original)==64
            record('root-owned volume initialized once; actual organization process permanently runs as UID 1654 with VerifyFull database TLS')
            docker('rm','--force',app);created.remove(app);start();created.append(app);ready(app)
            retained=docker('exec',db,'psql','-U','postgres','-d','aeroscience','-Atc','SELECT "CertificateSha256" FROM organization."DeploymentState"').stdout.strip();assert retained==original
            record('application replacement retains original protected identity and database state')
            # Exercise the distinct pinned upstream restored-certificate branch on this
            # disposable database only. Never remove a real organization's identity PFX.
            docker('rm','--force',app);created.remove(app);docker('stop','--time','10',db)
            docker('run','--rm','--network','none','--user','postgres','--mount','type=volume,src='+records+',dst=/var/lib/postgresql/data','--entrypoint','python3',database,'-c',"from pathlib import Path; Path('/var/lib/postgresql/data/certs/server.crt').unlink()")
            docker('start',db)
            until=time.monotonic()+90
            while time.monotonic()<until:
                if docker('exec',db,'pg_isready','-h','127.0.0.1','-U','postgres','-d','aeroscience',check=False).returncode==0:break
                time.sleep(1)
            else:raise RuntimeError('Restored-certificate fixture did not become ready within the original database readiness bound')
            docker('exec',db,'openssl','verify','-x509_strict','-purpose','sslserver','-CAfile','/var/lib/postgresql/data/certs/root.crt','-verify_hostname','postgres.railway.internal','/var/lib/postgresql/data/certs/server.crt')
            start();created.append(app);ready(app)
            assert docker('exec',db,'psql','-U','postgres','-d','aeroscience','-Atc','SELECT "CertificateSha256" FROM organization."DeploymentState"').stdout.strip()==original
            record('restored native database certificate retains strict private-hostname verification and original organization identity')
            # An informational edge header never authenticates a user. The public socket
            # remains untrusted and the separate application socket is loopback-only.
            probe="""import http.client,json,sys
x=json.load(sys.stdin);c=http.client.HTTPConnection(x['host'],x.get('port',8080),timeout=10)
c.request(x.get('method','GET'),x['path'],json.dumps(x['body']) if 'body' in x else None,x.get('headers',{}))
r=c.getresponse();print(json.dumps({'status':r.status,'headers':r.getheaders(),'body':r.read().decode()}))
"""
            def wire(path,body=None,headers=None):
                value={'host':app,'path':path,'headers':{'Host':'synthetic.up.railway.app',**(headers or {})}}
                if body is not None:value.update(method='POST',body=body);value['headers']['Content-Type']='application/json'
                return json.loads(docker('exec','-i',db,'python3','-c',probe,stdin=json.dumps(value)).stdout)
            assert wire('/api/session',headers={'Host':'attacker.invalid','X-Forwarded-Host':'synthetic.up.railway.app'})['status']==400
            assert wire('/api/organization/overview',headers={'X-Forwarded-For':'127.0.0.1','X-Forwarded-Proto':'https','X-Railway-Edge':'forged','X-AeroScience-Ingress-Proof':'forged'})['status']==401
            direct=docker('exec',db,'python3','-c',"import socket;socket.create_connection(('"+app+"',9001),timeout=3)",check=False)
            assert direct.returncode!=0
            record('hostile forwarded headers grant no identity; wrong Host and private direct application access are refused')
            initial=wire('/api/session');assert initial['status']==200
            session=json.loads(initial['body']);assert session['user'] is None and session['ownerPairing']
            cookies='; '.join(v.split(';')[0] for k,v in initial['headers'] if k.lower()=='set-cookie')
            browser={'Cookie':cookies,'Origin':'https://synthetic.up.railway.app','X-AeroScience-Organization-CSRF':session['csrf']}
            pending=wire('/api/owner/setup',{'organizationName':'Native Runtime Fixture','description':'Synthetic, not provider acceptance','userName':'fixture-owner','password':'FixtureOnly!2026Ownership'},browser)
            assert pending['status']==200;c=json.loads(pending['body'])
            text='\n'.join(['AeroScience.owner-pairing.v1',c['id'],c['intentId'],c['origin'],c['projectId'],c['environmentId'],c['serviceId'],c['publicKeySha256'],c['organizationName'],c['userName'],c['payloadSha256'],c['nonce'],str(int(__import__('datetime').datetime.fromisoformat(c['expiresUtc']).timestamp()))])
            signature=subprocess.run(['openssl','dgst','-sha256','-sign',str(ownerKey)],input=text.encode(),capture_output=True,check=True).stdout
            assert signature[0]==48 and signature[2]==2
            length=signature[3];r=int.from_bytes(signature[4:4+length],'big');position=4+length;assert signature[position]==2
            length=signature[position+1];t=int.from_bytes(signature[position+2:position+2+length],'big')
            approval={'id':c['id'],'signature':base64.b64encode(r.to_bytes(32,'big')+t.to_bytes(32,'big')).decode()}
            assert wire('/api/owner/approve',approval,browser)['status']==403
            assert wire('/api/owner/approve',approval)['status']==200
            assert wire('/api/owner/approve',approval)['status']==200
            assert json.loads(wire('/api/session')['body'])['initialized']
            record('real restricted container and PostgreSQL complete exact signed owner initialization once; browser approval bypass refused')
            docker('rm','--force',app);created.remove(app);start();created.append(app);ready(app)
            assert json.loads(wire('/api/session')['body'])['initialized']
            record('organization ownership and protected keys survive real container replacement')
            # No fallback to legacy sealed setup, anonymous claiming, or a forged pairing.
            acceptedConfiguration=applicationEnv.read_text()
            docker('rm','--force',app);created.remove(app);applicationEnv.write_text(acceptedConfiguration.replace('AEROSCIENCE_OWNER_PAIRING='+owner+'\n',''));start();created.append(app)
            until=time.monotonic()+30
            while time.monotonic()<until:
                if json.loads(docker('inspect',app).stdout)[0]['State']['Status']=='exited':break
                time.sleep(.5)
            else:raise AssertionError('Missing protected ownership configuration did not fail closed')
            assert json.loads(docker('inspect',app).stdout)[0]['State']['ExitCode']!=0
            record('missing public ownership pairing refuses startup without legacy-secret or anonymous fallback')
            logs=docker('logs',app).stdout+docker('logs',app).stderr+docker('logs',db).stdout
            assert all(value not in logs for value in (trust,password,appPassword,recoveryPassword,owner,key))
            record('native setup, database and identity credentials absent from retained process logs')
            # An actually different, deliberately unbootable LOCAL image must not corrupt
            # persistence. This is image-start failure recovery, not a schema rollback.
            docker('rm','--force',app);created.remove(app);applicationEnv.write_text(acceptedConfiguration)
            build=root/'unbootable-build';build.mkdir(mode=0o700)
            # The official chiseled image intentionally has no shell. Override only
            # its entrypoint; retain the real non-root image and persistence contract.
            (build/'Dockerfile').write_text('FROM '+application+'\nUSER 1654:1654\nENTRYPOINT ["dotnet", "Unavailable.Upgrade.Fixture.dll"]\n')
            badImage=identity+'-unbootable:fixture'
            try:
                docker('build','--network=none','-t',badImage,str(build));start(badImage);created.append(app)
                until=time.monotonic()+30
                while time.monotonic()<until:
                    if json.loads(docker('inspect',app).stdout)[0]['State']['Status']=='exited':break
                    time.sleep(.5)
                else:raise AssertionError('Unbootable candidate did not stop within the original startup refusal bound')
                assert json.loads(docker('inspect',app).stdout)[0]['State']['ExitCode']!=0
                docker('rm','--force',app);created.remove(app);start();created.append(app);ready(app)
                assert json.loads(wire('/api/session')['body'])['initialized']
                assert docker('exec',db,'psql','-U','postgres','-d','aeroscience','-Atc','SELECT "CertificateSha256" FROM organization."DeploymentState"').stdout.strip()==original
                record('deliberately unbootable local image refused; prior application restored with protected identity and ownership intact, without claiming schema rollback')
            finally:docker('image','rm',badImage,check=False)
            report.update(passed=len(report['checks']),failed=0)
        except Exception:
            report['failed']=1
            output.parent.mkdir(parents=True,exist_ok=True)
            logs=''
            for name in created:
                result=docker('logs',name,check=False);logs+=name+'\n'+result.stdout+result.stderr+'\n'
            for value in (trust,password,appPassword,recoveryPassword,owner,key):logs=logs.replace(value,'[redacted]')
            diagnostic=output.with_suffix('.failure.log');diagnostic.write_text(logs);diagnostic.chmod(0o600)
            raise
        finally:
            for name in reversed(created):docker('rm','--force',name,check=False)
            for name in (state,records):docker('volume','rm',name,check=False)
            docker('network','rm',network,check=False)
            output.parent.mkdir(parents=True,exist_ok=True);output.write_text(json.dumps(report,indent=2)+'\n')
if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--application',required=True);parser.add_argument('--database',required=True);parser.add_argument('--output',required=True,type=Path);args=parser.parse_args();verify(args.application,args.database,args.output)
