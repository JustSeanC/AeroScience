#!/usr/bin/env python3
"""Disposable Railway-shaped runtime acceptance. No cloud account, deployment template or billing.
Tests the real pinned PostgreSQL image's SAN/private CA and permanent application privilege drop.
"""
import argparse
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
        root=Path(temporary);os.chmod(root,0o700);trust=secrets.token_hex(32);password=secrets.token_hex(32);appPassword=secrets.token_hex(32);recoveryPassword=secrets.token_hex(32);owner=secrets.token_hex(32);key=secrets.token_hex(32)
        databaseEnv=root/'database.env';databaseEnv.write_text('POSTGRES_DB=aeroscience\nPOSTGRES_USER=postgres\nPGUSER=postgres\nPOSTGRES_PASSWORD='+password+'\nAEROSCIENCE_APPLICATION_DATABASE_PASSWORD='+appPassword+'\nAEROSCIENCE_RECOVERY_DATABASE_PASSWORD='+recoveryPassword+'\nRAILWAY_PRIVATE_DOMAIN=postgres.railway.internal\nAEROSCIENCE_DATABASE_TRUST_SECRET='+trust+'\n');databaseEnv.chmod(0o600)
        applicationEnv=root/'application.env';applicationEnv.write_text('RAILWAY_PUBLIC_DOMAIN=synthetic.up.railway.app\nAEROSCIENCE_DATABASE_HOST=postgres.railway.internal\nAEROSCIENCE_DATABASE_NAME=aeroscience\nAEROSCIENCE_DATABASE_USER=aeroscience_application\nAEROSCIENCE_DATABASE_PASSWORD='+appPassword+'\nAEROSCIENCE_DATABASE_TRUST_SECRET='+trust+'\nOrganization__BootstrapSecret='+owner+'\nOrganization__KeyCertificatePassword='+key+'\nOrganization__TrustedProxies__0=127.0.0.1\n');applicationEnv.chmod(0o600)
        def start():docker('run','--detach','--name',app,'--network',network,'--env-file',str(applicationEnv),'--mount','type=volume,src='+state+',dst=/configuration',application)
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
            positive=docker('exec',db,'openssl','verify','-CAfile','/var/lib/postgresql/data/certs/root.crt','-verify_hostname','postgres.railway.internal','/var/lib/postgresql/data/certs/server.crt')
            negative=docker('exec',db,'openssl','verify','-CAfile','/var/lib/postgresql/data/certs/root.crt','-verify_hostname','wrong.railway.internal','/var/lib/postgresql/data/certs/server.crt',check=False)
            assert positive.returncode==0 and negative.returncode!=0;record('actual pinned Railway PostgreSQL CA validates its exact private hostname and rejects a wrong hostname')
            start();created.append(app);ready(app)
            processes=[row for row in docker('top',app).stdout.splitlines()[1:] if 'AeroScience.Organization.dll --railway-start' in row];assert len(processes)==1 and processes[0].split()[0]=='1654'
            original=docker('exec',db,'psql','-U','postgres','-d','aeroscience','-Atc','SELECT "CertificateSha256" FROM organization."DeploymentState"').stdout.strip();assert len(original)==64
            record('root-owned volume initialized once; actual organization process permanently runs as UID 1654 with VerifyFull database TLS')
            docker('rm','--force',app);created.remove(app);start();created.append(app);ready(app)
            retained=docker('exec',db,'psql','-U','postgres','-d','aeroscience','-Atc','SELECT "CertificateSha256" FROM organization."DeploymentState"').stdout.strip();assert retained==original
            record('application replacement retains original protected identity and database state')
            # Fail closed without a proxy contract, not by trusting X-Railway-Edge or arbitrary forwarded headers.
            docker('rm','--force',app);created.remove(app);applicationEnv.write_text(applicationEnv.read_text().replace('Organization__TrustedProxies__0=127.0.0.1\n',''));start();created.append(app)
            until=time.monotonic()+30
            while time.monotonic()<until:
                if json.loads(docker('inspect',app).stdout)[0]['State']['Status']=='exited':break
                time.sleep(.5)
            else:raise AssertionError('Unverified proxy configuration did not fail closed')
            assert json.loads(docker('inspect',app).stdout)[0]['State']['ExitCode']!=0
            record('missing verified ingress contract refuses public startup; no wildcard proxy or header-only trust')
            logs=docker('logs',app).stdout+docker('logs',app).stderr+docker('logs',db).stdout
            assert all(value not in logs for value in (trust,password,appPassword,recoveryPassword,owner,key))
            record('native setup, database and identity credentials absent from retained process logs')
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
