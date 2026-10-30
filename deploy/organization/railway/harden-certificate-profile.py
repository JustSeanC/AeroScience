#!/usr/bin/env python3
"""Build-only correction for the exact digest-pinned upstream PostgreSQL scripts.
Never reads a running database, existing certificate, password or provider account.
"""
from pathlib import Path

ROOT_COMMAND='openssl req -new -x509'
ROOT_PROFILE=ROOT_COMMAND+" -addext 'basicConstraints=critical,CA:TRUE,pathlen:0' -addext 'keyUsage=critical,keyCertSign,cRLSign' -addext 'subjectKeyIdentifier=hash' -addext 'authorityKeyIdentifier=keyid:always'"
OLD_LEAF='basicConstraints = critical, CA:TRUE\nkeyUsage = digitalSignature, nonRepudiation, keyEncipherment, dataEncipherment'
LEAF='basicConstraints = critical, CA:FALSE\nkeyUsage = critical, digitalSignature, keyEncipherment\nextendedKeyUsage = serverAuth\nsubjectKeyIdentifier = hash'

def harden(text,restored=False):
    if len(text)>262144 or text.count(ROOT_COMMAND)!=1 or text.count(OLD_LEAF)!=1:
        raise ValueError('Pinned PostgreSQL certificate generator changed; review the exact source before building.')
    if 'keyUsage=critical,keyCertSign,cRLSign' in text:
        raise ValueError('Certificate generator was already changed; refuse an unknown build input.')
    text=text.replace(ROOT_COMMAND,ROOT_PROFILE).replace(OLD_LEAF,LEAF)
    if restored:
        # The upstream restore path had localhost-only SANs even though normal init
        # already includes the private hostname. Preserve the same hostname contract.
        old='subjectAltName = DNS:localhost\n'
        if text.count(old)!=1:raise ValueError('Pinned restore hostname profile changed; review before building.')
        text=text.replace(old,'subjectAltName = DNS:localhost,DNS:${RAILWAY_PRIVATE_DOMAIN:-localhost}\n')
    return text

def main():
    files=[(Path('/docker-entrypoint-initdb.d/init-ssl.sh'),False),(Path('/usr/local/bin/wrapper.sh'),True)]
    changes=[]
    for path,restored in files:
        if path.is_symlink() or not path.is_file() or path.stat().st_size>262144:
            raise ValueError('Pinned certificate generator is not a bounded regular file.')
        changes.append((path,harden(path.read_text(),restored)))
    # Validate all exact inputs before changing either trusted build source.
    for path,text in changes:path.write_text(text)

if __name__=='__main__':main()
