#!/usr/bin/env python3
"""Isolated real HTTPS/VerifyFull PostgreSQL/Compose acceptance. No public provider or billing.
Secrets stay in a temporary protected fixture. Reports contain only non-secret results.
"""
import argparse
import base64
import concurrent.futures
import hashlib
import hmac
import struct
import http.client
from http.cookies import SimpleCookie
import importlib.util
import json
import os
from pathlib import Path
import shutil
import socket
import ssl
import subprocess
import tempfile
import time
import uuid

ROOT=Path(__file__).resolve().parents[2]
DOCKER=['docker','--host=unix:///var/run/docker.sock']

def execute(command, *, stdin=None, timeout=180, check=True, environment=None):
    env=os.environ.copy()
    for name in ('DOCKER_HOST','DOCKER_CONTEXT','DOCKER_TLS','DOCKER_TLS_VERIFY','DOCKER_CERT_PATH'):env.pop(name,None)
    if environment:env.update(environment)
    result=subprocess.run(command,input=stdin,text=True,capture_output=True,timeout=timeout,env=env,cwd=ROOT)
    if check and result.returncode:raise RuntimeError('Fixture operation failed: '+command[0]+' '+command[1]+' (diagnostics retained privately)')
    return result

class FixtureHttpsConnection(http.client.HTTPSConnection):
    def connect(self):
        # TCP stays on loopback, but SNI and certificate verification use the reserved fixture origin.
        raw=socket.create_connection((self.host,self.port),self.timeout)
        self.sock=self._context.wrap_socket(raw,server_hostname='organization.test')

class Browser:
    def __init__(self,port,context):self.port=port;self.context=context;self.cookies={};self.csrf=''
    def request(self,path,data=None, *, bearer=None, expected=200, method=None):
        connection=FixtureHttpsConnection('127.0.0.1',self.port,context=self.context,timeout=20)
        headers={'Host':'organization.test'}
        if bearer:headers['Authorization']='Bearer '+bearer
        elif self.cookies:headers['Cookie']='; '.join(k+'='+v for k,v in self.cookies.items())
        body=None
        if data is not None:
            body=json.dumps(data,ensure_ascii=False).encode();headers['Content-Type']='application/json'
            if not bearer and not path.startswith('/api/device/'):
                headers['Origin']='https://organization.test';headers['X-AeroScience-Organization-CSRF']=self.csrf
        connection.request(method or ('POST' if data is not None else 'GET'),path,body,headers)
        response=connection.getresponse();text=response.read().decode();status=response.status
        for key,value in response.getheaders():
            if key.lower()=='set-cookie':
                jar=SimpleCookie();jar.load(value)
                self.cookies.update({k:v.value for k,v in jar.items()})
        connection.close()
        if status!=expected:raise AssertionError('Unexpected response '+str(status)+' for '+path+' (expected '+str(expected)+')')
        return json.loads(text) if text else None
    def session(self):
        value=self.request('/api/session');self.csrf=value['csrf'];return value
    def post(self,path,value):self.session();return self.request(path,value)

def command(**values):return {'version':1,'requestId':str(uuid.uuid4()),**values}

def verify(image, output):
    report={'productionReady':False,'fixture':'Synthetic local infrastructure and pilot-documentation evidence; not cloud/MSFS/NASA acceptance','checks':[]}
    with tempfile.TemporaryDirectory(prefix='aeroscience-p25-deployment-') as temporary:
        scratch=Path(temporary);os.chmod(scratch,0o700)
        folder=scratch/'self-hosted';shutil.copytree(ROOT/'deploy/organization/self-hosted',folder,ignore=shutil.ignore_patterns('protected'))
        spec=importlib.util.spec_from_file_location('prepare',folder/'prepare.py');module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        module.prepare(folder/'protected','organization.test');protected=folder/'protected'
        env=dict(line.split('=',1) for line in (protected/'application.env').read_text().splitlines())
        # Reserved local test HTTPS certificate: verify against its CA, never disable TLS checks.
        def openssl(*args):execute(['openssl',*args],timeout=30)
        openssl('req','-newkey','rsa:2048','-nodes','-keyout',str(protected/'web.key'),'-out',str(protected/'web.csr'),'-subj','/CN=organization.test')
        (protected/'web.ext').write_text('subjectAltName=DNS:organization.test,IP:127.0.0.1\nextendedKeyUsage=serverAuth\nbasicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nsubjectKeyIdentifier=hash\nauthorityKeyIdentifier=keyid,issuer\n')
        openssl('x509','-req','-in',str(protected/'web.csr'),'-CA',str(protected/'database-ca.crt'),'-CAkey',str(protected/'database-ca.key'),'-CAcreateserial','-out',str(protected/'web.crt'),'-days','1','-extfile',str(protected/'web.ext'))
        (folder/'Caddyfile').write_text('organization.test {\n tls /configuration/web.crt /configuration/web.key\n reverse_proxy organization:8080\n}\n')
        with socket.socket() as listener:listener.bind(('127.0.0.1',0));port=listener.getsockname()[1]
        project='aeroscience-p25-'+uuid.uuid4().hex[:12]
        compose=DOCKER+['compose','--project-name',project,'-f',str(folder/'compose.yaml')]
        config=json.loads(execute(compose+['config','--format','json'],environment={'AEROSCIENCE_ORGANIZATION_IMAGE':image}).stdout)
        config['services']['https']['ports']=[{'target':443,'published':str(port),'host_ip':'127.0.0.1','protocol':'tcp'}]
        config['services']['https']['volumes'] += [{'type':'bind','source':str(protected/'web.crt'),'target':'/configuration/web.crt','read_only':True},{'type':'bind','source':str(protected/'web.key'),'target':'/configuration/web.key','read_only':True}]
        path=folder/'resolved.private.json';path.write_text(json.dumps(config));os.chmod(path,0o600)
        compose=DOCKER+['compose','--project-name',project,'-f',str(path)]
        private_log=scratch/'diagnostics.log'
        def compose_run(*args,timeout=180):
            result=execute(compose+list(args),timeout=timeout,check=False)
            with private_log.open('a') as log:log.write(result.stdout+result.stderr)
            if result.returncode:raise RuntimeError('Compose fixture '+args[0]+' failed')
            return result
        def record(name):report['checks'].append(name);print('PASS: '+name,flush=True)
        try:
            info=json.loads(execute(DOCKER+['image','inspect',image]).stdout)[0];report['imageId']=info['Id'];report['sourceRevision']=info['Config'].get('Labels',{}).get('org.opencontainers.image.revision')
            assert info['Config']['User']=='1654:1654'
            assert not any('Organization__' in item or 'password=' in item.lower() for item in info['Config']['Env'])
            record('non-root versioned image contains no configured deployment credentials')
            unsafe=execute(DOCKER+['run','--rm',image],timeout=30,check=False)
            assert unsafe.returncode!=0 and 'startup stopped safely' in unsafe.stderr
            record('unconfigured production startup refused without exception/secret disclosure')
            compose_run('up','--detach','--wait','--wait-timeout','120',timeout=160)
            record('Compose starts with no published PostgreSQL port, private VerifyFull TLS and protected persistent volumes')
            db_id=compose_run('ps','--quiet','database').stdout.strip();db_info=json.loads(execute(DOCKER+['inspect',db_id]).stdout)[0]
            assert not db_info['HostConfig'].get('PortBindings')
            context=ssl.create_default_context(cafile=str(protected/'database-ca.crt'))
            context.verify_flags|=ssl.VERIFY_X509_STRICT
            browser=Browser(port,context);assert not browser.session()['initialized']
            browser.request('/api/organization/server-health',expected=401)
            payload={'secret':'wrong-owner-code','organizationName':'P25 Synthetic Institute','description':'Local deployment acceptance only','userName':'administrator','password':'Synthetic-Only!P25-2026'}
            browser.request('/api/bootstrap',payload,expected=403)
            payload['secret']=env['Organization__BootstrapSecret']
            def own():
                client=Browser(port,context);client.cookies=browser.cookies.copy();client.csrf=browser.csrf
                try:client.request('/api/bootstrap',payload);return 200
                except AssertionError as e:
                    if 'response 409' in str(e):return 409
                    raise
            with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:assert sorted(pool.map(lambda _:own(),range(2)))==[200,409]
            record('anonymous takeover refused; concurrent owner bootstrap creates exactly one administrator')
            browser.post('/api/sign-in',{'userName':'administrator','password':payload['password']});assert browser.session()['user']['name']=='administrator'
            browser.request('/api/organization/server-health',expected=428)
            enrollment=browser.post('/api/account-security/enroll',{'password':payload['password']})
            key=base64.b32decode(enrollment['setupKey'],casefold=True);step=int(time.time()/30)+1
            digest=hmac.new(key,struct.pack('>Q',step),hashlib.sha1).digest();index=digest[-1]&15
            code=str((struct.unpack('>I',digest[index:index+4])[0]&0x7fffffff)%1000000).zfill(6)
            codes=browser.post('/api/account-security/enable',{'code':code})['recoveryCodes']
            browser.post('/api/account-security/confirm-recovery',{'code':codes[0]})
            record('password-only administration blocked; Identity authenticator and actual saved recovery-code verification required')
            health=browser.request('/api/organization/server-health');assert not health['productionReady'];assert any(c['name']=='Database transport' and c['state']=='healthy' for c in health['checks'])
            assert not any(value in json.dumps(health) for value in (payload['secret'],env['Organization__KeyCertificatePassword'],env['Organization__ConnectionString']))
            record('secure identity cookies, authenticated real schema/TLS/evidence checks and explicit production blockers')
            program=browser.post('/api/organization/programs',command(expectedRevision=0,name='Synthetic Atmospheric Program',description='Deployment fixture'))
            campaign=browser.post('/api/organization/programs/'+program['id']+'/campaigns',command(expectedRevision=0,name='Documented Atmosphere',description='Pilot observations',objective='Document actual pilot observations without treating them as measured scientific truth.',focus='scientificResearch',template={'id':'scientific-observation-flight','version':1}))
            campaign=browser.post('/api/organization/campaigns/'+campaign['id']+'/publish',command(expectedRevision=campaign['revision']))
            card=browser.post('/api/organization/opportunities/campaigns/'+campaign['id'],command(expectedRevision=0,campaignRevision=campaign['revision'],name='Synthetic Documentation Card',description='Deployment acceptance only',objective=campaign['objective'],instructions='Document three original notes.',policy={'participation':'exclusive','requiredDistinctPilots':1,'maximumActiveClaims':1,'allowVolunteers':True,'eligibleRoles':['Pilot'],'reviewPolicy':'Review original evidence'}))
            card=browser.post('/api/organization/opportunities/'+card['id']+'/publish',command(expectedRevision=card['revision']))
            invite=browser.post('/api/organization/members/invitations',command(expiresHours=1));pilot=Browser(port,context);pilot.session();identity=pilot.request('/api/public/organization')
            pilot.post('/api/enrollment',{'version':1,'organizationId':identity['id'],'userName':'deployment-pilot','password':'Synthetic-Pilot!P25-2026','invitation':invite['link'].split('#join=')[1],'statement':'Synthetic research fixture','confirmOrganization':True})
            pilot.post('/api/sign-in',{'userName':'deployment-pilot','password':'Synthetic-Pilot!P25-2026'})
            pilot.request('/api/organization/server-health',expected=403)
            record('published campaign and single-use expiring invitation use original membership authorization')
            claim=pilot.post('/api/membership/research/claim-command',command(organizationId=identity['id'],opportunityId=card['id'],publishedRevision=card['revision'],action='claim'))
            # Exercise the exact browser-approved desktop protocol, with no browser bearer or grant elevation.
            verifier=os.urandom(32).hex();challenge=base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b'=').decode()
            native=Browser(port,context)
            pending=native.request('/api/device/start',{'organizationId':identity['id'],'clientName':'P25 isolated deployment fixture','challenge':challenge,'permission':'submitEvidence'})
            pilot.post('/api/connection/approval',{'id':pending['id'],'organizationId':identity['id'],'approve':True,'confirmCode':True,'confirmClaimManagement':False,'confirmSubmission':True})
            grant=native.request('/api/device/poll',{'id':pending['id'],'organizationId':identity['id'],'deviceSecret':pending['deviceSecret'],'verifier':verifier})
            bearer=grant['credential'];original=native.request('/api/member/claims/'+claim['id']+'/original',bearer=bearer);capabilities=native.request('/api/member/claims/'+claim['id']+'/submission-policy',bearer=bearer)
            record('independent pilot claim and separately browser-approved Submit Evidence grant')
            generated=execute([os.environ.get('DOTNET','dotnet'),str(ROOT/'artifacts/browser-test-host/AeroScience.TestHost.dll'),'--deployment-evidence',str(scratch/'local-originals')],stdin=json.dumps({'origin':'https://organization.test','connectionId':grant['connectionId'],'original':original,'capabilities':capabilities}),timeout=30,check=False)
            if generated.returncode:
                with private_log.open('a') as log:log.write(generated.stderr)
                raise RuntimeError('Retained evidence fixture failed; inspect the sanitized private diagnostic.')
            package=json.loads(generated.stdout);receipt=native.request('/api/member/submissions/receive',package,bearer=bearer)
            assert receipt['status']=='Received / Awaiting Review';assert receipt==native.request('/api/member/submissions/receive',package,bearer=bearer)
            inventory=browser.request('/api/organization/submissions');assert len(inventory['items'])==1
            review=browser.request('/api/organization/reviews');assert len(review['items'])==1
            pilot.request('/api/organization/submissions',expected=403)
            record('real selected synthetic retained pilot evidence, explicit P17 consent, idempotent receipt and protected reviewer inventory')
            stats={}
            for service in ('organization','database'):
                identity=compose_run('ps','--quiet',service).stdout.strip()
                value=json.loads(execute(DOCKER+['stats','--no-stream','--format','{{json .}}',identity]).stdout)
                stats[service]={'memory':value['MemUsage'],'cpuSnapshot':value['CPUPerc']}
            report['singleSyntheticSubmissionSnapshot']=stats
            before=browser.request('/api/organization/audit');before_cookies=browser.cookies.copy()
            compose_run('restart','database',timeout=45)
            compose_run('up','--detach','--force-recreate','--no-deps','--wait','--wait-timeout','90','organization',timeout=120)
            assert browser.session()['user']['name']=='administrator';assert before_cookies.keys()<=browser.cookies.keys()
            assert native.request('/api/member/submissions/'+package['document']['manifest']['id']+'?manifestSha='+receipt['manifestSha256'],bearer=bearer)['receipt']==receipt
            assert browser.request('/api/organization/audit')==before
            record('PostgreSQL restart and app-container replacement preserve cookies, exact receipt, received evidence, claims and audit history')
            browser.session();browser.request('/api/bootstrap',payload,expected=403)
            record('consumed owner code cannot initialize another organization after replacement')
            logs=compose_run('logs','--no-color').stdout
            for value in (payload['secret'],payload['password'],env['Organization__KeyCertificatePassword'],env['Organization__ConnectionString'],bearer,enrollment['setupKey'],*codes):assert value not in logs
            record('deployment secrets and desktop bearer absent from application/proxy/database logs')
            report['passed']=len(report['checks']);report['failed']=0
        except Exception:
            # Keep diagnostics access-controlled; do not echo provider credentials or received evidence.
            output.parent.mkdir(parents=True,exist_ok=True)
            try:
                logs=(private_log.read_text() if private_log.exists() else '')+'\n'+execute(compose+['logs','--no-color'],check=False).stdout
                for value in env.values():
                    if len(value)>20:logs=logs.replace(value,'[redacted]')
                (output.parent/'container-failure.log').write_text(logs);os.chmod(output.parent/'container-failure.log',0o600)
            except Exception:pass
            report['failed']=1;output.write_text(json.dumps(report,indent=2)+'\n');raise
        finally:
            execute(compose+['down','--volumes','--remove-orphans'],timeout=60,check=False)
    output.parent.mkdir(parents=True,exist_ok=True);output.write_text(json.dumps(report,indent=2)+'\n')

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--image',required=True);parser.add_argument('--output',type=Path,required=True);options=parser.parse_args()
    verify(options.image,options.output)
