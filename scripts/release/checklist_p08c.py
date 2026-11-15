"""Separate P08c checklist; never edits sealed P08b payload/events or app state."""
import argparse
import csv
import datetime as dt
import json
import io
import math
import os
from pathlib import Path
import re
import sys
import uuid
import zipfile

import private_validation_080 as validation
from checklist_080 import _save_lock
sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'validation'))
import p08c

SCHEMA = 'aeroscience-p08c-checklist-1'
CASES = {
    'terrain-reference': ('Compare real elevation values', 'Attach an independent P08c terrain/retained-raster receipt. This records the named development build, not installed MSFS acceptance.', 'numerical'),
    'point-reference': ('Compare genuine historical point records', 'Attach the original-record/attribute/coordinate comparison receipt for the named build. Historical returns are not aircraft measurements.', 'points'),
    'small-resources': ('Measure a small retained workload', 'Attach a receipt containing actual processing metrics and its predeclared budgets. A screenshot or yes is insufficient.', 'resources'),
    'medium-resources': ('Measure the retained regional workload', 'Attach the versioned retained-LiDAR benchmark, including independent reference and original hashes.', 'medium'),
    'large-resources': ('Measure a larger genuine workload', 'Requires a distinct retained workload of at least one million original points, within the existing two-million-point limit. No duplicated small fixtures. Leave Blocked without real inputs.', 'large'),
    'restart-results': ('Restart and reopen exact saved results', 'On the selected installed build, reopen a copied flight/session and original evidence. Describe what survived; keep original hashes. Do not test on your only copy.', 'observation'),
    'cancel-retry': ('Interrupt work and recover safely', 'Try explicit cancel/retry versus Continue, offline and budget/consent holds on copies. Check that this never restarts science or grants downloads.', 'observation'),
    'source-deletion': ('Delete only a disposable source copy', 'Use the existing preview/confirm controls on a separate disposable Library. Verify dependent values/recovery copies disappear and independent original science remains.', 'observation'),
    'privacy-permissions': ('Keep sharing and permissions explicit', 'Check no implicit downloads/uploads/live sharing and organization role/isolation/expiry rules. Keep real credentials/private correspondence out of notes.', 'observation'),
    'native-reconnect': ('Reconnect while recording and science differ', 'In real MSFS, recording without science, armed/waiting, collecting, paused science with recording still active, resumed, sessions, stale/reconnect and panel reopen. Report actual observed states.', 'observation'),
    'native-endurance': ('Measure a real longer flight', 'Use the flight timer for at least 60 minutes; then select that completed flight’s Research Package. State actual legs/sessions/reconnects tried. Timer save alone never marks Passed.', 'endurance'),
}
OUTCOMES = ['Not Tested', 'Passed', 'Failed', 'Blocked']


def results_path(folder):
    return validation.no_links(Path(folder).parent/'P08c-Results')


def _latest(folder, source, receipt_hash, instance):
    results = results_path(folder); latest = {}
    if not results.exists(): return latest
    paths = sorted(results.glob('p08c-*.json'))
    p08c.require(len(paths) <= 2000, 'Too many P08c records. Preserve them and review before continuing.')
    for path in paths:
        event = p08c.read(path)
        if (event.get('source_commit'), event.get('instance'), event.get('receipt_sha256')) != (source, instance, receipt_hash): continue
        p08c.require(event.get('schema') == SCHEMA and event.get('test_id') in CASES
            and event.get('outcome') in OUTCOMES and event.get('public_release_approved') is False, 'Unrecognized P08c event; preserve it.')
        p08c.require(isinstance(event.get('notes'),str) and len(event['notes'])<=4000
            and isinstance(event.get('evidence_file'),str) and len(event['evidence_file'])<=4096, 'Invalid existing P08c notes/evidence path; preserve the event.')
        when=dt.datetime.fromisoformat(event['observed_utc']);p08c.require(when.tzinfo is not None,'P08c event needs its actual observation timezone.')
        key = event['test_id']; order=(when,path.name)
        if key not in latest or order>latest[key]['order']:
            latest[key] = {'event':event,'revision':p08c.digest(path),'order':order}
    return latest


def state(folder, source, receipt_hash, receipt):
    latest = _latest(folder,source,receipt_hash,receipt['instance'])
    return {'schema':SCHEMA,'source_commit':source,'instance':receipt['instance'],'receipt_sha256':receipt_hash,
        'results_directory':str(results_path(folder)),'outcomes':OUTCOMES,'public_release_approved':False,
        'rows':[{'id':key,'title':v[0],'instructions':v[1], 'category':v[2],
            'outcome':latest[key]['event']['outcome'] if key in latest else 'Not Tested',
            'notes':latest[key]['event']['notes'] if key in latest else '',
            'evidence':latest[key]['event'].get('evidence_file','') if key in latest else '',
            'revision':latest[key]['revision'] if key in latest else None} for key,v in CASES.items()]}


def load(folder, source, receipt_hash):
    return state(folder,source,receipt_hash,validation.verify(folder,source,receipt_hash))


def _finite(value, minimum=0):
    return type(value) in (int,float) and math.isfinite(value) and value >= minimum


def measured(path, category, source, receipt_hash, receipt):
    p08c.require(isinstance(path,str) and path.strip(), 'Choose a measured receipt using Attach evidence, or leave this check Blocked/Not Tested.')
    if category == 'endurance':
        value = p08c.read(path)
        p08c.require(value.get('schema') == 'aeroscience-p08c-native-observation-1'
            and (value.get('source_commit'),value.get('instance'),value.get('receipt_sha256')) == (source,receipt['instance'],receipt_hash),
            'The timer evidence belongs to another installed build/attempt.')
        p08c.require(_finite(value.get('duration_seconds'),3600) and value.get('sample_count',0)>=360
            and _finite(value.get('sampled_peak_working_set_bytes'),1) and _finite(value.get('cpu_seconds')),
            'Record at least 60 real minutes with actual CPU/memory samples; shorter runs remain evidence, not an endurance pass.')
        package=value['research_package']; p08c.require(p08c.digest(package['path'])==package['sha256'], 'The selected flight evidence changed or is missing.')
        p08c.require(package['mission_id'] == research_package(Path(package['path']))['mission_id'], 'Flight identity changed.')
        start=dt.datetime.fromisoformat(value['started_utc']); end=dt.datetime.fromisoformat(value['finished_utc'])
        first=dt.datetime.fromisoformat(package['first_flight_utc']); last=dt.datetime.fromisoformat(package['last_flight_utc'])
        p08c.require(package['telemetry_sources']==['Simulator'] and (min(last,end)-max(first,start)).total_seconds()>=3600,
                     'At least 60 minutes of actual simulator flight must overlap the timed observation; an idle timer or synthetic replay cannot pass.')
        return {'kind':'owner-installed-timed-observation','tested_source_commit':source,'sha256':p08c.digest(path),
                'duration_seconds':value['duration_seconds'],'mission_id':package['mission_id']}
    value = p08c.verify_receipt(Path(path))
    p08c.require(category not in ['resources','medium','large'] or bool(value.get('application_metrics')), 'Actual measured processing metrics are required.')
    p08c.require(category != 'numerical' or value['operation'] in ['terrain','retained-raster'], 'Choose a raster numerical comparison.')
    p08c.require(category != 'points' or value['operation'] in ['lidar','retained-lidar'], 'Choose an original-point comparison.')
    if category in ['medium','large']:
        p08c.require(value['evidence_kind']=='resource-benchmark' and value['operation']=='retained-lidar', 'Choose a retained-region resource benchmark.')
    if category == 'large':
        p08c.require(value.get('candidate_points',0)>=1000000 and len({i['sha256'] for i in value['inputs']})==len(value['inputs']),
                     'A small or duplicated source is not the larger genuine workload.')
    return {'kind':'engineering-measured-evidence','tested_source_commit':value['runtime']['source_commit'],
        'installed_acceptance':'Not Tested','sha256':p08c.digest(path),'operation':value['operation'],
        'inputs':[{'sha256':i['sha256'],'bytes':i['bytes'],'id':i['id']} for i in value['inputs']]}


def save(folder, source, receipt_hash, request):
    receipt = validation.verify(folder,source,receipt_hash); results = results_path(folder)
    p08c.require(isinstance(request,dict) and set(request)=={'entries','observed'} and type(request['observed']) is bool
        and isinstance(request['entries'],list) and 1<=len(request['entries'])<=len(CASES), 'Choose changed checks to save.')
    with _save_lock(results):
        latest = _latest(folder,source,receipt_hash,receipt['instance']); prepared=[]; seen=set()
        for row in request['entries']:
            p08c.require(isinstance(row,dict) and set(row)=={'id','outcome','notes','revision','evidence'}, 'Unknown checklist fields.')
            key=row['id']; p08c.require(isinstance(key,str) and key in CASES and key not in seen and row['outcome'] in OUTCOMES
                and isinstance(row['evidence'],str) and len(row['evidence'])<=4096, 'Choose each listed check once with a bounded receipt path.')
            seen.add(key); old=latest.get(key)
            p08c.require(row['revision']==(old['revision'] if old else None), 'Newer notes exist. Reopen before saving changes.')
            notes=row['notes']; p08c.require(isinstance(notes,str) and len(notes)<=4000 and not re.search(r'(?i)bearer\s+\S+|password\s*[:=]|access_token\s*[:=]',notes), 'Keep notes bounded and credentials out.')
            p08c.require(row['outcome']!='Passed' or request['observed'] and len(notes.strip())>=12,
                'For Passed, describe the actual check or measured evidence reviewed and confirm it; yes is insufficient.')
            reference=None
            if row['outcome']=='Passed' and CASES[key][2]!='observation': reference=measured(row['evidence'],CASES[key][2],source,receipt_hash,receipt)
            if old and old['event'].get('measured_evidence'):
                p08c.require(p08c.digest(old['event']['evidence_file'])==old['event']['measured_evidence']['sha256'], 'Earlier evidence changed; preserve and review it before editing.')
            prepared.append((row,reference))
        for row,reference in prepared:
            event={'schema':SCHEMA,'source_commit':source,'instance':receipt['instance'],'receipt_sha256':receipt_hash,
                'test_id':row['id'],'outcome':row['outcome'],'notes':row['notes'],'evidence_file':row['evidence'],
                'measured_evidence':reference,'observed_utc':dt.datetime.now(dt.timezone.utc).isoformat(),
                'public_release_approved':False,'p08b_events_modified':False}
            name='p08c-'+dt.datetime.now(dt.timezone.utc).strftime('%Y%m%dT%H%M%S')+'-'+uuid.uuid4().hex+'.json'
            p08c.write_new(results/name,event)
        return {'saved':len(prepared),'state':state(folder,source,receipt_hash,receipt)}


def research_package(path):
    path=p08c.safe_path(path)
    p08c.require(path.stat().st_size<=512*1024**2, 'Use a bounded completed-flight Research Package (≤512 MiB).')
    with zipfile.ZipFile(path) as archive:
        infos=archive.infolist(); p08c.require(len(infos)<=20000 and len({i.filename for i in infos})==len(infos), 'Invalid/oversized Research Package.')
        info=archive.getinfo('manifest.json'); p08c.require(info.file_size<=1024**2, 'Oversized Research Package manifest.')
        manifest=json.loads(archive.read(info).decode('utf-8'))
        p08c.require(manifest.get('formatVersion')==1 and bool(uuid.UUID(manifest['missionId'])), 'Completed Research Package mission identity required.')
        files=manifest['files']; p08c.require(isinstance(files,list) and {'mission.json','flight.csv','science.csv','scientific-review.json'}.issubset({f['path'] for f in files}), 'Select the actual completed-flight Research Package.')
        p08c.require(sum(f['bytes'] for f in files)<=2*1024**3, 'Research Package expansion budget exceeded.')
        import hashlib
        for file in files:
            item=archive.getinfo(file['path']); p08c.require(item.file_size==file['bytes'], 'Research Package entry size differs.')
            with archive.open(item) as stream: actual=hashlib.file_digest(stream,'sha256').hexdigest()
            p08c.require(actual==file['sha256'], 'Research Package content hash differs.')
        first=last=None; sources=set(); count=0
        with archive.open('flight.csv') as stream:
            for row in csv.DictReader(io.TextIOWrapper(stream,encoding='utf-8')):
                count+=1; p08c.require(count<=2000000,'Flight export row budget exceeded.')
                when=dt.datetime.fromisoformat(row['timestamp_utc']);p08c.require(when.tzinfo is not None,'Original UTC timestamps required.')
                p08c.require(last is None or when>=last,'Out-of-order original flight times require review.')
                first=first or when;last=when;sources.add(row['source'])
        p08c.require(first is not None,'Select a completed flight with original telemetry.')
    return {'path':str(path),'sha256':p08c.digest(path),'mission_id':manifest['missionId'],
        'first_flight_utc':first.isoformat(),'last_flight_utc':last.isoformat(),'telemetry_sources':sorted(sources),'flight_rows':count}


def record_endurance(folder,source,receipt_hash,request):
    receipt=validation.verify(folder,source,receipt_hash)
    p08c.require(isinstance(request,dict) and request.get('schema')=='aeroscience-p08c-native-observation-1', 'Use the explicit native timer.')
    p08c.require(set(request)=={'schema','process_id','process_path','process_identity_stable','process_started_utc','started_utc','finished_utc',
        'duration_seconds','cpu_seconds','sampled_peak_working_set_bytes','sample_count','package_path'}, 'Only bounded native timer fields are accepted.')
    p08c.require(_finite(request.get('duration_seconds'),1) and _finite(request.get('cpu_seconds'))
        and _finite(request.get('sampled_peak_working_set_bytes'),1) and type(request.get('sample_count')) is int and request['sample_count']>0,
        'Actual timer/resource counters are required.')
    p08c.require(request.get('process_identity_stable') is True and p08c.safe_path(request['process_path'])==p08c.safe_path(Path(folder)/'AeroScience.exe'),
                 'Observe the exact verified companion process; restarted or unrelated processes cannot inherit the timer.')
    start=dt.datetime.fromisoformat(request['started_utc']);end=dt.datetime.fromisoformat(request['finished_utc'])
    p08c.require(start.tzinfo is not None and end.tzinfo is not None and abs((end-start).total_seconds()-request['duration_seconds'])<5,
                 'Wall-clock and monotonic timer durations disagree. Preserve the observation for review.')
    package=research_package(Path(request['package_path']))
    results=results_path(folder); results.mkdir(exist_ok=True)
    captures=validation.no_links(results/'Captures'); captures.mkdir(exist_ok=True)
    value={**request,'source_commit':source,'instance':receipt['instance'],'receipt_sha256':receipt_hash,
        'research_package':package,'public_release_approved':False,'automatic_pass':False,
        'gpu_measurement':'Not Tested','network_bytes':None,'science_state_verification':'Owner observation; timer issues no commands',
        'build_files_sha256':receipt['files']}
    output=captures/('endurance-'+uuid.uuid4().hex+'.json');p08c.write_new(output,value)
    return {'evidence_file':str(output),'duration_seconds':request['duration_seconds']}


def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('action',choices=['load','save','endurance'])
    parser.add_argument('--folder',type=Path,required=True);parser.add_argument('--source',required=True);parser.add_argument('--receipt-hash',required=True);args=parser.parse_args()
    try:
        if args.action=='load': value=load(args.folder,args.source,args.receipt_hash)
        else:
            raw=sys.stdin.buffer.read(512*1024+1);p08c.require(len(raw)<=512*1024,'Oversized checklist request.')
            request=json.loads(raw.decode('utf-8'));value=(save if args.action=='save' else record_endurance)(args.folder,args.source,args.receipt_hash,request)
        print(json.dumps(value,ensure_ascii=True))
    except (ValueError,OSError,KeyError,zipfile.BadZipFile) as exc:
        parser.exit(1,'P08c checklist refused: '+str(exc)+'\n')


if __name__=='__main__':main()
