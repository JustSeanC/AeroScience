#!/usr/bin/env python3
"""Advanced operators only. Generate unique private configuration; never print secrets.
The beginner hosting chooser does not run this script or create paid infrastructure.
"""
import argparse
import os
from pathlib import Path
import re
import secrets
import subprocess


def prepare(directory: Path, hostname: str):
    if not re.fullmatch(r"(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}", hostname):
        raise ValueError("Choose an exact lowercase public DNS hostname, without a scheme or path.")
    if directory.exists():
        raise ValueError("Protected configuration already exists; never overwrite an initialized server.")
    directory.mkdir(mode=0o700, parents=True)
    owner_password, app_password, bootstrap, key_password = (secrets.token_hex(32) for _ in range(4))

    def write(name, value, mode=0o600):
        path=directory/name
        fd=os.open(path, os.O_WRONLY|os.O_CREAT|os.O_EXCL, mode)
        with os.fdopen(fd, 'w', encoding='utf-8') as file: file.write(value)

    def openssl(*args):
        subprocess.run(['openssl', *args], cwd=directory, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)

    write('database-owner-password', owner_password)
    write('initialize.sql', "CREATE ROLE aeroscience LOGIN PASSWORD '"+app_password+"' NOSUPERUSER NOCREATEDB NOCREATEROLE;\nALTER DATABASE aeroscience OWNER TO aeroscience;\nREVOKE ALL ON SCHEMA public FROM PUBLIC;\nALTER SCHEMA public OWNER TO aeroscience;\n")
    openssl('req','-x509','-newkey','rsa:3072','-nodes','-keyout','database-ca.key','-out','database-ca.crt','-days','365','-subj','/CN=AeroScience Private Database CA',
            '-addext','basicConstraints=critical,CA:TRUE,pathlen:0','-addext','keyUsage=critical,keyCertSign,cRLSign','-addext','subjectKeyIdentifier=hash','-addext','authorityKeyIdentifier=keyid:always')
    openssl('req','-newkey','rsa:3072','-nodes','-keyout','database.key','-out','database.csr','-subj','/CN=database')
    write('database.ext', 'subjectAltName=DNS:database\nextendedKeyUsage=serverAuth\nbasicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nsubjectKeyIdentifier=hash\nauthorityKeyIdentifier=keyid,issuer\n')
    openssl('x509','-req','-in','database.csr','-CA','database-ca.crt','-CAkey','database-ca.key','-CAcreateserial','-out','database.crt','-days','90','-extfile','database.ext')
    for name in ('database-ca.key','database.key'):os.chmod(directory/name,0o600)
    for name in ('database-ca.crt','database.crt'):os.chmod(directory/name,0o644)
    write('application.env', '\n'.join([
        'ASPNETCORE_ENVIRONMENT=Production',
        'Organization__PublicOrigin=https://'+hostname,
        'Organization__ConnectionString=Host=database;Database=aeroscience;Username=aeroscience;Password='+app_password+';SSL Mode=VerifyFull;Root Certificate=/trust/database-ca.crt;Maximum Pool Size=20;Timeout=10',
        'Organization__ManagedDeployment=true',
        'Organization__PersistentStorageConfirmed=true',
        'Organization__StateDirectory=/configuration',
        'Organization__DatabaseExposure=private-network',
        'Organization__HostingProvider=self-hosted',
        'Organization__BootstrapSecret='+bootstrap,
        'Organization__BootstrapLifetimeMinutes=30',
        'Organization__KeyCertificatePassword='+key_password,
        'Organization__TrustedProxies__0=172.29.251.2',
        'Logging__LogLevel__Default=Warning',
    ])+'\n')
    write('proxy.env','ORGANIZATION_HOST='+hostname+'\n')
    # Display is deliberately limited to non-secret completion information.
    return {'configured': True, 'origin':'https://'+hostname,'productionReady':False}

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--hostname', required=True)
    parser.add_argument('--directory',type=Path,default=Path(__file__).parent/'protected')
    options=parser.parse_args()
    try:
        prepare(options.directory,options.hostname)
    except (ValueError, subprocess.CalledProcessError, OSError):
        parser.exit(1,'Configuration was not completed. Check the hostname, private directory and OpenSSL installation; no secrets are printed.\n')
    print('Private deployment configuration created. Preserve it and the persistent volumes. Production recovery is not verified.')
