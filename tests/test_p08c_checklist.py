"""Synthetic recorder/control fixtures, never installed or scientific acceptance."""
import copy
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import uuid
import zipfile

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'scripts/release'))
import checklist_p08c as ui
import private_validation_080 as legacy

SOURCE='a'*40


class ChecklistTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup);self.root=Path(self.temp.name)
        self.app=self.root/'App';self.app.mkdir();(self.app/'sealed.txt').write_bytes(b'SYNTHETIC TEST NOT A PACKAGE')
        self.before=legacy.inventory(self.app)
        self.receipt={'instance':'1'*32,'files':{'AeroScience.exe':'2'*64}}
        self.receipt_hash='3'*64
        self.mock_verify=mock.patch.object(legacy,'verify',return_value=self.receipt).start();self.addCleanup(mock.patch.stopall)

    def state(self):return ui.load(self.app,SOURCE,self.receipt_hash)
    def entry(self,key='restart-results',outcome='Passed',notes='Observed a synthetic fixture check, not actual simulator acceptance.',evidence=''):
        item=next(r for r in self.state()['rows'] if r['id']==key)
        return {'id':key,'outcome':outcome,'notes':notes,'revision':item['revision'],'evidence':evidence}
    def save(self,*rows,observed=True):return ui.save(self.app,SOURCE,self.receipt_hash,{'entries':list(rows),'observed':observed})

    def test_separate_defaults_and_namespace_preserve_all_old_payload_and_events(self):
        old=self.root/'Results';old.mkdir();(old/'old-p08b.json').write_text('{"outcome":"Passed"}')
        before=(old/'old-p08b.json').read_bytes();state=self.state()
        self.assertTrue(all(r['outcome']=='Not Tested' for r in state['rows']))
        result=self.save(self.entry());self.assertEqual(1,result['saved'])
        self.assertEqual(self.before,legacy.inventory(self.app));self.assertEqual(before,(old/'old-p08b.json').read_bytes())
        self.assertEqual(26,len(legacy.TESTS));self.assertEqual('P08c-Results',Path(state['results_directory']).name)

    def test_generic_yes_and_missing_personal_confirmation_are_refused(self):
        for notes,observed in [('yes',True),('A specific synthetic fixture note.',False)]:
            with self.assertRaises(ValueError): self.save(self.entry(notes=notes),observed=observed)

    def test_numerical_pass_cannot_be_a_dropdown_or_screenshot(self):
        with self.assertRaisesRegex(ValueError,'measured receipt'):self.save(self.entry('terrain-reference'))
        screenshot=self.root/'screenshot.json';screenshot.write_text('{"notes":"yes","outcome":"Passed"}')
        with self.assertRaises(ValueError):self.save(self.entry('terrain-reference',evidence=str(screenshot)))
        self.assertFalse(list((self.root/'P08c-Results').glob('p08c-*.json')))

    def test_entire_batch_is_validated_before_first_event(self):
        with self.assertRaises(ValueError):self.save(self.entry(),self.entry('point-reference'))
        self.assertFalse(list((self.root/'P08c-Results').glob('p08c-*.json')))

    def test_stale_edits_do_not_replace_later_notes(self):
        stale=self.entry();self.save(self.entry(notes='First actual fixture note.'))
        with self.assertRaisesRegex(ValueError,'Newer notes'):self.save(stale)
        self.save(self.entry(notes='Second actual fixture note.'))
        row=next(r for r in self.state()['rows'] if r['id']=='restart-results')
        self.assertEqual('Second actual fixture note.',row['notes'])
        self.assertEqual(2,len(list((self.root/'P08c-Results').glob('p08c-*.json'))))

    def test_blocked_unknown_measurement_can_be_saved_without_claiming_a_pass(self):
        self.save(self.entry('large-resources','Blocked','No selected genuine large input; not a passing resource measurement.'),observed=False)
        self.assertEqual('Blocked',next(r for r in self.state()['rows'] if r['id']=='large-resources')['outcome'])

    def test_measured_engineering_source_stays_distinct_from_installed_source(self):
        receipt=self.root/'engineering.json';receipt.write_text('{}')
        measured={'runtime':{'source_commit':'b'*40},'operation':'terrain','inputs':[{'id':'real-input-reference','sha256':'4'*64,'bytes':12}]}
        with mock.patch.object(ui.p08c,'verify_receipt',return_value=measured):
            self.save(self.entry('terrain-reference',evidence=str(receipt)))
        record=ui.p08c.read(next((self.root/'P08c-Results').glob('p08c-*.json')))
        self.assertEqual(SOURCE,record['source_commit'])
        self.assertEqual('b'*40,record['measured_evidence']['tested_source_commit'])
        self.assertEqual('Not Tested',record['measured_evidence']['installed_acceptance'])
        receipt.write_text('{"changed":true}')
        with self.assertRaisesRegex(ValueError,'Earlier evidence changed'):self.save(self.entry('terrain-reference','Blocked',evidence=str(receipt)))

    def test_small_replicated_dataset_cannot_be_larger_workload_credit(self):
        file=self.root/'benchmark.json';file.write_text('{}')
        for count,inputs in [(447556,[{'sha256':'4'*64}]),(1000001,[{'sha256':'4'*64}]*2)]:
            with self.subTest(count=count),mock.patch.object(ui.p08c,'verify_receipt',return_value={'runtime':{'source_commit':'b'*40},'operation':'retained-lidar',
                'evidence_kind':'resource-benchmark','application_metrics':{'wall_seconds':1},'candidate_points':count,'inputs':inputs}):
                with self.assertRaises(ValueError):self.save(self.entry('large-resources',evidence=str(file)))

    def package(self,source='Simulator'):
        files={'mission.json':b'{"syntheticTestOnly":true}','science.csv':b'timestamp_utc\n', 'scientific-review.json':b'{}',
            'flight.csv':('timestamp_utc,source\n2026-11-15T10:00:00+00:00,'+source+'\n2026-11-15T11:01:00+00:00,'+source+'\n').encode()}
        manifest={'formatVersion':1,'missionId':str(uuid.uuid4()),'files':[{'path':k,'bytes':len(v),'sha256':hashlib.sha256(v).hexdigest()} for k,v in files.items()]}
        path=self.root/('package-'+uuid.uuid4().hex+'.zip')
        with zipfile.ZipFile(path,'x') as archive:
            for key,value in files.items():archive.writestr(key,value)
            archive.writestr('manifest.json',json.dumps(manifest))
        return path

    def test_original_research_package_is_read_only_and_hash_verified(self):
        path=self.package();before=path.read_bytes();result=ui.research_package(path)
        self.assertEqual(['Simulator'],result['telemetry_sources']);self.assertEqual(2,result['flight_rows']);self.assertEqual(before,path.read_bytes())
        with zipfile.ZipFile(path,'a') as archive:archive.writestr('flight.csv','changed')
        with self.assertRaises(ValueError):ui.research_package(path)

    def endurance(self,source='Simulator'):
        package=self.package(source)
        return {'schema':'aeroscience-p08c-native-observation-1','process_path':str(self.app/'AeroScience.exe'),'process_identity_stable':True,
            'process_id':1234,'process_started_utc':'2026-11-15T09:58:00+00:00',
            'duration_seconds':3700,'cpu_seconds':24,'sampled_peak_working_set_bytes':1000000,'sample_count':3600,
            'started_utc':'2026-11-15T09:59:00+00:00','finished_utc':'2026-11-15T11:00:40+00:00','package_path':str(package)}

    def test_timer_never_automatically_sets_passed(self):
        saved=ui.record_endurance(self.app,SOURCE,self.receipt_hash,self.endurance())
        record=ui.p08c.read(saved['evidence_file']);self.assertFalse(record['automatic_pass'])
        self.assertTrue(all(r['outcome']=='Not Tested' for r in self.state()['rows']))
        self.save(self.entry('native-endurance',evidence=saved['evidence_file']))

    def test_synthetic_flight_or_idle_timer_cannot_pass_native_endurance(self):
        request=self.endurance('Simulated');saved=ui.record_endurance(self.app,SOURCE,self.receipt_hash,request)
        with self.assertRaisesRegex(ValueError,'actual simulator flight'):self.save(self.entry('native-endurance',evidence=saved['evidence_file']))
        request=self.endurance();request.update(started_utc='2026-11-16T09:59:00+00:00',finished_utc='2026-11-16T11:00:40+00:00')
        saved=ui.record_endurance(self.app,SOURCE,self.receipt_hash,request)
        with self.assertRaises(ValueError):self.save(self.entry('native-endurance',evidence=saved['evidence_file']))

    def test_wrong_build_short_or_nonfinite_counter_cannot_pass(self):
        request=self.endurance();saved=ui.record_endurance(self.app,SOURCE,self.receipt_hash,request)
        with self.assertRaises(ValueError):ui.measured(saved['evidence_file'],'endurance','c'*40,self.receipt_hash,self.receipt)
        for key,value in [('duration_seconds',float('nan')),('cpu_seconds',-1),('sampled_peak_working_set_bytes',0),('process_identity_stable',False)]:
            bad={**request,key:value}
            with self.assertRaises(ValueError):ui.record_endurance(self.app,SOURCE,self.receipt_hash,bad)

    def test_powershell_parses_without_executing_windows_form(self):
        executable=shutil.which('pwsh') or '/workspace/.tools/pwsh/pwsh'
        if not Path(executable).exists():self.skipTest('PowerShell parser unavailable')
        command='$e=$null;$t=$null;[void][Management.Automation.Language.Parser]::ParseFile($env:P08C_FORM,[ref]$t,[ref]$e);if($e){throw ($e|Out-String)}'
        result=subprocess.run([executable,'-NoProfile','-Command',command],env={**os.environ,'P08C_FORM':str(ROOT/'scripts/release/Open-080ScientificChecklist.ps1')},capture_output=True,text=True,encoding='utf-8',timeout=30)
        self.assertEqual(0,result.returncode,result.stderr)

    @unittest.skipUnless(os.name=='nt','Actual native controls require Windows; installed acceptance remains separate')
    def test_actual_windows_controls_reuse_existing_layout_and_separate_evidence(self):
        state=self.root/'state.json';state.write_text(json.dumps(self.state()))
        command=r'''
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$repo=$env:P08C_REPO
$e=$null;$t=$null;$ast=[Management.Automation.Language.Parser]::ParseFile("$repo/scripts/release/Open-080ScientificChecklist.ps1",[ref]$t,[ref]$e)
if($e){throw ($e|Out-String)}
$functions=$ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)
. ([ScriptBlock]::Create(($functions|ForEach-Object {$_.Extent.Text}) -join "`n"))
Import-P08cChecklistControls
$state=Get-Content $env:P08C_STATE -Raw|ConvertFrom-Json
$view=New-P08cChecklistForm $state
try {
 $view.Form.CreateControl();$view.Grid.CreateControl()
 if($view.Grid.Columns.Count -ne 4 -or $view.Confirmation.Checked){throw 'Measured column/defaults missing'}
 if(-not $view.Attach.AccessibleName -or -not $view.FlightTimer.AccessibleName){throw 'Accessible evidence controls missing'}
 if((Get-P08cChecklistRequest $view).entries.Count){throw 'Untouched rows changed'}
 $view.Grid.Rows[0].Cells[3].Value='C:\SYNTHETIC-measured-receipt.json'
 if(-not (Get-080ChecklistRequestForClose $view)){throw 'Unsaved evidence selection lost'}
 if((Get-P08cChecklistRequest $view).entries[0].evidence -ne 'C:\SYNTHETIC-measured-receipt.json'){throw 'Receipt mapping changed'}
} finally {$view.Form.Dispose()}
'''
        result=subprocess.run([shutil.which('pwsh'),'-NoProfile','-Command',command],env={**os.environ,'P08C_REPO':str(ROOT),'P08C_STATE':str(state)},capture_output=True,text=True,encoding='utf-8',timeout=60)
        self.assertEqual(0,result.returncode,result.stderr)


if __name__=='__main__':unittest.main()
