#!/usr/bin/env python3
"""Private public-CA attestation for the pinned Railway PostgreSQL image.
No database or private signing key is exposed. No organization backend or account exists here.
"""
import base64
import hashlib
import hmac
from http.server import BaseHTTPRequestHandler, HTTPServer
import json
import os
from pathlib import Path
import re
import socket

CERTIFICATE=Path('/var/lib/postgresql/data/certs/root.crt')

def attest(host,nonce,secret,certificate):
    if not re.fullmatch(r'[a-z0-9][a-z0-9-]{0,62}\.railway\.internal',host) or not re.fullmatch(r'[a-f0-9]{64}',nonce) or not 32<=len(secret)<=256 or not 1<=len(certificate)<=16384:
        raise ValueError('Unsupported private trust challenge')
    encoded=base64.b64encode(certificate).decode()
    message=('aeroscience-railway-database-trust-v1\n'+host+'\n'+nonce+'\n'+encoded).encode()
    return {'certificate':encoded,'authentication':hmac.new(secret.encode(),message,hashlib.sha256).hexdigest()}

class TrustHandler(BaseHTTPRequestHandler):
    def log_message(self,*args):pass
    def do_POST(self):
        self.connection.settimeout(5)
        try:
            if self.path!='/trust' or not 0<int(self.headers.get('Content-Length','0'))<=1024:raise ValueError()
            value=json.loads(self.rfile.read(int(self.headers['Content-Length'])))
            if set(value)!={'host','nonce'} or value['host']!=os.environ['RAILWAY_PRIVATE_DOMAIN']:raise ValueError()
            certificate=CERTIFICATE.read_bytes()
            result=attest(value['host'],value['nonce'],os.environ['AEROSCIENCE_DATABASE_TRUST_SECRET'],certificate)
            body=json.dumps(result,separators=(',',':')).encode();self.send_response(200);self.send_header('Content-Type','application/json');self.send_header('Cache-Control','no-store');self.send_header('Content-Length',str(len(body)));self.end_headers();self.wfile.write(body)
        except Exception:
            self.send_response(503);self.end_headers()

class PrivateHttpServer(HTTPServer):
    address_family=socket.AF_INET6
    def server_bind(self):
        self.socket.setsockopt(socket.IPPROTO_IPV6,socket.IPV6_V6ONLY,0)
        super().server_bind()

if __name__=='__main__':
    if not 32<=len(os.environ.get('AEROSCIENCE_DATABASE_TRUST_SECRET',''))<=256:raise SystemExit('Private database trust configuration is missing')
    PrivateHttpServer(('::',8081),TrustHandler).serve_forever()
