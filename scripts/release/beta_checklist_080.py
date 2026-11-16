"""Basic exact-installer beta acceptance; broad beta QA remains explicitly deferred."""
import argparse
import datetime as dt
import json
from pathlib import Path
import re
import sys
import uuid
import beta_release_080 as beta
from checklist_080 import _save_lock

def state(folder,source,receipt_hash):
    receipt=beta.verify_installer(folder,source)
    beta.require(beta.sha(folder/beta.BUILD)==receipt_hash,'Installer receipt differs from the independently retained hash.')
    events=beta.native.no_links(folder/'Acceptance-Events');latest={}
    if events.exists():
        paths=sorted(events.glob('*.json'));beta.require(len(paths)<=2000,'Too many observations; preserve and review them.')
        for p in paths:
            event=beta.read(beta.native.no_links(p))
            if (event.get('source_commit'),event.get('installer_sha256'),event.get('installer_receipt_sha256'))!=(source,receipt['installer_sha256'],receipt_hash):continue
            beta.require(event.get('schema')=='aeroscience-beta080-observation-1' and event.get('test_id') in beta.REQUIRED,'Foreign/corrupt beta event.')
            beta.require(event.get('outcome') in ['Passed','Failed','Blocked','Not Tested']
                and isinstance(event.get('notes'),str) and len(event['notes'])<=4000
                and type(event.get('personally_observed')) is bool,'Malformed beta observation.')
            beta.require(event['outcome']!='Passed' or event['personally_observed'] and len(event['notes'].strip())>=12,
                'A saved pass must retain actual personal observation and substantive notes.')
            beta.require(isinstance(event.get('observed_utc'),str) and event['observed_utc'].strip(), 'Observation time missing.')
            order=(event['observed_utc'],p.name);key=event['test_id']
            if key not in latest or order>latest[key]['order']:latest[key]={'event':event,'order':order,'revision':beta.sha(p)}
    return {'source_commit':source,'instance':receipt['installer_sha256'][:32],'installer_sha256':receipt['installer_sha256'],
        'receipt_sha256':receipt_hash,'rows':[{'id':key,'title':name,'instructions':name+'. Test these exact Beta 1 installer bytes; no earlier private build pass is inherited.',
            'outcome':latest[key]['event']['outcome'] if key in latest else 'Not Tested','notes':latest[key]['event']['notes'] if key in latest else '',
            'personally_observed':latest[key]['event']['personally_observed'] if key in latest else False,
            'revision':latest[key]['revision'] if key in latest else None} for key,name in beta.REQUIRED.items()]}

def acceptance(folder,source,receipt_hash):
    view=state(folder,source,receipt_hash);value=beta.acceptance(source,view['installer_sha256'],receipt_hash)
    value['tests']={r['id']:{'name':beta.REQUIRED[r['id']],'outcome':r['outcome'],'notes':r['notes'],'personally_observed':r['personally_observed']} for r in view['rows']}
    return value

def save(folder,source,receipt_hash,request):
    beta.require(isinstance(request,dict) and set(request)=={'entries','observed'} and type(request['observed']) is bool
        and isinstance(request['entries'],list) and 1<=len(request['entries'])<=len(beta.REQUIRED),'Choose changed basic beta checks.')
    directory=beta.native.no_links(folder/'Acceptance-Events')
    with _save_lock(directory):
        current=state(folder,source,receipt_hash);rows={r['id']:r for r in current['rows']};prepared=[];seen=set()
        for row in request['entries']:
            beta.require(isinstance(row,dict) and set(row)=={'id','outcome','notes','revision'},'Unexpected observation fields.')
            key=row['id'];beta.require(isinstance(key,str) and key in rows and key not in seen and row['outcome'] in ['Passed','Failed','Blocked','Not Tested'],'Unknown or duplicate required check. Deferred cards do not belong here.');seen.add(key)
            beta.require(row['revision']==rows[key]['revision'],'Newer observation exists. Reopen before saving.')
            notes=row['notes'];beta.require(isinstance(notes,str) and len(notes)<=4000 and not re.search(r'(?i)bearer\s+\S+|password\s*[:=]|access_token\s*[:=]',notes),'Keep notes bounded and credentials out.')
            beta.require(row['outcome']!='Passed' or request['observed'] and len(notes.strip())>=12,'Passed needs actual personal observation and meaningful notes; yes is insufficient.')
            if key=='installer' and row['outcome']=='Passed':
                paths=sorted((folder/'Installed-Checks').glob('*.json'))
                beta.require(0<len(paths)<=2000,'First use Verify installed files for this exact candidate; preserve bounded proof history.')
                proofs=[beta.read(beta.native.no_links(p)) for p in paths];proof=max(proofs,key=lambda v:v['checked_utc'])
                beta.require(proof.get('installer_sha256')==current['installer_sha256'] and proof.get('outcome')=='Passed','Installed proof belongs to another candidate.')
                beta.verify_installed(folder,Path(proof['installed_directory']),source)
            prepared.append(row)
        for row in prepared:
            value={'schema':'aeroscience-beta080-observation-1','source_commit':source,'installer_sha256':current['installer_sha256'],
                'installer_receipt_sha256':receipt_hash,'test_id':row['id'],'outcome':row['outcome'],'notes':row['notes'],
                'personally_observed':request['observed'],'observed_utc':dt.datetime.now(dt.timezone.utc).isoformat(),'publication_approved':False}
            beta.write(directory/(dt.datetime.now(dt.timezone.utc).strftime('%Y%m%dT%H%M%S')+'-'+uuid.uuid4().hex+'.json'),value)
        return {'saved':len(prepared),'state':state(folder,source,receipt_hash)}

def installed(folder,source,receipt_hash,request):
    state(folder,source,receipt_hash);path=beta.native.no_links(request['installed_directory']);receipt=beta.verify_installed(folder,path,source)
    proof={'schema':'aeroscience-beta080-installed-1','source_commit':source,'installer_sha256':receipt['installer_sha256'],'installed_directory':str(path),'outcome':'Passed','checked_utc':beta.native.utc()}
    directory=beta.native.no_links(folder/'Installed-Checks');directory.mkdir(exist_ok=True)
    beta.write(directory/(dt.datetime.now(dt.timezone.utc).strftime('%Y%m%dT%H%M%S')+'-'+uuid.uuid4().hex+'.json'),proof)
    return {'message':'Installed application bytes and decoder verified. Actual Windows/MSFS observations still require your confirmation.'}

def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('action',choices=['load','save','installed','export']);parser.add_argument('--folder',type=Path,required=True);parser.add_argument('--source',required=True);parser.add_argument('--receipt-hash',required=True);args=parser.parse_args()
    try:
        if args.action=='load':result=state(args.folder,args.source,args.receipt_hash)
        elif args.action=='export':
            value=acceptance(args.folder,args.source,args.receipt_hash);path=args.folder/('OBSERVED-ACCEPTANCE-'+uuid.uuid4().hex+'.json');beta.write(path,value);result={'acceptance_path':str(path)}
        else:
            raw=sys.stdin.buffer.read(131073);beta.require(len(raw)<=131072,'Oversized request.');request=json.loads(raw.decode('utf-8'))
            result=(save if args.action=='save' else installed)(args.folder,args.source,args.receipt_hash,request)
        print(json.dumps(result))
    except (ValueError,OSError,KeyError,TypeError) as e:parser.exit(1,'Beta acceptance refused: '+str(e)+'\n')

if __name__=='__main__':main()
