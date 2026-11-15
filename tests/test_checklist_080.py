"""Guided checklist fixtures, not installed Windows/MSFS or scientific acceptance."""
import copy
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'scripts/release'))
import checklist_080 as ui
import private_validation_080 as validation

SOURCE='a'*40
INSTANCE='1'*32


class ChecklistTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(prefix='checklist080-fixture-')
        self.root=Path(self.temp.name);self.app=self.root/'App';self.app.mkdir()
        (self.app/validation.RECEIPT).write_text('SYNTHETIC RECORD-FLOW FIXTURE ONLY; NOT A NATIVE PACKAGE',encoding='utf-8')
        self.receipt_hash=validation.sha(self.app/validation.RECEIPT)
        self.receipt={'instance':INSTANCE,'catalog':{'catalogVersion':25},'files':{
            'SimConnect.dll':'2'*64,'LidarRuntime/AeroScience.LidarReader.exe':'3'*64,'Toolbar/sdk-build.json':'4'*64}}
        self.original=validation.inventory(self.app)
        self.verify=mock.patch.object(validation,'verify',return_value=self.receipt).start()
        self.addCleanup(mock.patch.stopall);self.addCleanup(self.temp.cleanup)

    def state(self):return ui.load(self.app,SOURCE,self.receipt_hash)
    def entry(self,key='startup',outcome='Passed',notes='Observed the named test on this fixture, not real native acceptance.'):
        row=next(r for r in self.state()['rows'] if r['id']==key)
        return dict(id=key,outcome=outcome,notes=notes,revision=row['revision'])
    def save(self,rows,observed=True):
        return ui.save(self.app,SOURCE,self.receipt_hash,{'entries':rows,'observed':observed})
    def events(self):return list((self.root/'Results').glob('*.json')) if (self.root/'Results').exists() else []

    def test_all_checks_have_friendly_names_and_start_untested_without_writing(self):
        state=self.state();self.verify.assert_called_once_with(self.app,SOURCE,self.receipt_hash)
        self.assertEqual(set(validation.TESTS),{r['id'] for r in state['rows']})
        self.assertTrue(all(r['title'] and r['instructions'] and r['outcome']=='Not Tested' and r['revision'] is None for r in state['rows']))
        self.assertFalse((self.root/'Results').exists());self.assertEqual(self.original,validation.inventory(self.app))
        self.assertFalse(state['public_release_approved']);self.assertEqual('Pending',state['qualified_human_translation_review'])

    def test_existing_build_receipt_failure_is_not_bypassed(self):
        self.verify.side_effect=ValueError('Fixture package integrity mismatch')
        with self.assertRaisesRegex(ValueError,'integrity'):self.state()
        with self.assertRaisesRegex(ValueError,'integrity'):self.save([dict(id='startup')])
        self.assertFalse((self.root/'Results').exists())

    def test_saves_using_original_event_format_and_resumes_exact_build(self):
        result=self.save([self.entry(),self.entry('panel-lifecycle','Blocked','No fixture display available.')])
        self.assertEqual(2,result['saved']);self.assertEqual(2,len(self.events()))
        rows={r['id']:r for r in self.state()['rows']}
        self.assertEqual('Passed',rows['startup']['outcome']);self.assertEqual('Blocked',rows['panel-lifecycle']['outcome'])
        self.assertEqual('Not Tested',rows['science-lifecycle']['outcome']);self.assertTrue(rows['startup']['revision'])
        for path in self.events():
            event=validation.read(path);self.assertEqual(1,event['format_version']);self.assertEqual(SOURCE,event['source_commit'])
            self.assertEqual(INSTANCE,event['instance']);self.assertEqual(self.receipt_hash,event['receipt_sha256'])
            self.assertEqual('3'*64,event['native_lidar_sha256']);self.assertFalse(event['public_release_approved'])
        self.assertEqual(self.original,validation.inventory(self.app))

    def test_passed_needs_explicit_personal_testing_and_notes(self):
        for notes,observed in [('',True),('   ',True),('A note alone is not confirmation.',False)]:
            with self.subTest(notes=notes,observed=observed),self.assertRaisesRegex(ValueError,'personal-testing'):
                self.save([self.entry(notes=notes)],observed)
        self.assertEqual([],self.events())

    def test_original_invalid_typing_cannot_become_blanket_pass(self):
        for key,outcome in [('tested','Passed'),('startup','passed'),('startup','Everything works')]:
            row=self.entry();row.update(id=key,outcome=outcome)
            with self.subTest(key=key,outcome=outcome),self.assertRaisesRegex(ValueError,'named check'):self.save([row])
        self.assertEqual([],self.events())

    def test_validates_entire_selection_before_writing_any_entry(self):
        with self.assertRaisesRegex(ValueError,'personal-testing'):
            self.save([self.entry(),self.entry('panel-lifecycle',notes='')])
        self.assertEqual([],self.events())

    def test_duplicate_unknown_fields_and_unbounded_notes_are_rejected(self):
        row=self.entry()
        for rows in [[row,row],[{**row,'file':'outside.json'}],[{**row,'notes':'x'*4001}]]:
            with self.subTest(rows=len(rows)),self.assertRaises(ValueError):self.save(rows)
        self.assertEqual([],self.events())

    def test_credential_notes_are_refused_before_writing(self):
        for note in ['password=private','access_token: private','Bearer secret']:
            with self.subTest(note=note),self.assertRaisesRegex(ValueError,'credentials'):self.save([self.entry(notes=note)])
        self.assertEqual([],self.events())

    def test_later_notes_append_and_stale_windows_cannot_replace_newer_results(self):
        stale=self.entry();self.save([stale]);first=self.events()[0];original=first.read_bytes()
        with self.assertRaisesRegex(ValueError,'Newer notes'):self.save([stale])
        self.save([self.entry(outcome='Failed',notes='Later fixture observation failed.')])
        self.assertEqual(2,len(self.events()));self.assertEqual(original,first.read_bytes())
        self.assertEqual('Failed',next(r for r in self.state()['rows'] if r['id']=='startup')['outcome'])

    def test_unrelated_json_and_foreign_build_records_are_not_imported(self):
        results=self.root/'Results';results.mkdir();(results/'reviewed-evidence.json').write_text('{}',encoding='utf-8')
        validation.observation(self.app,'b'*40,results,'startup','Passed','Foreign fixture source.',True)
        self.assertTrue(all(r['outcome']=='Not Tested' for r in self.state()['rows']))
        self.save([self.entry()]);event=self.events()[-1]
        self.assertTrue(event.exists());self.assertEqual('Not Tested',next(r for r in self.state()['rows'] if r['id']=='science-lifecycle')['outcome'])

    def test_missing_or_changed_evidence_refuses_edits_and_retains_old_event(self):
        results=self.root/'Results';results.mkdir();evidence=results/'reviewed.txt';evidence.write_text('fixture reviewed evidence',encoding='utf-8')
        event=Path(validation.observation(self.app,SOURCE,results,'startup','Passed','Existing fixture note.',True,evidence))
        original=event.read_bytes();row=self.entry(notes='Changed note retaining exact evidence.')
        self.save([row]);self.assertEqual(2,len(self.events()))
        evidence.write_text('changed evidence',encoding='utf-8')
        with self.assertRaisesRegex(ValueError,'evidence changed'):self.save([self.entry(notes='Further note.')])
        self.assertEqual(original,event.read_bytes());self.assertEqual(2,len(self.events()))

    def test_linked_results_are_not_followed(self):
        outside=self.root/'outside';outside.mkdir();link=self.root/'Results'
        if os.name=='nt':subprocess.run(['cmd.exe','/d','/c','mklink','/J',str(link),str(outside)],check=True,capture_output=True)
        else:link.symlink_to(outside,target_is_directory=True)
        with self.assertRaisesRegex(ValueError,'Links/junctions'):self.state()
        self.assertEqual([],list(outside.iterdir()))

    def test_unicode_and_markup_notes_are_preserved_as_plain_text(self):
        note='Français, português, Español — <script>no execution</script> & original notes.'
        self.save([self.entry(notes=note)]);self.assertEqual(note,next(r for r in self.state()['rows'] if r['id']=='startup')['notes'])

    def test_powershell_window_entrypoint_parses_without_launching_it(self):
        pwsh=shutil.which('pwsh');self.assertIsNotNone(pwsh)
        script='$errors=$null;$tokens=$null;[void][Management.Automation.Language.Parser]::ParseFile($env:P08B_FORM,[ref]$tokens,[ref]$errors);if($errors){throw ($errors|Out-String)}'
        result=subprocess.run([pwsh,'-NoProfile','-Command',script],env={**os.environ,'P08B_FORM':str(ROOT/'scripts/release/Open-080Checklist.ps1')},capture_output=True,text=True,encoding='utf-8',timeout=30)
        self.assertEqual(0,result.returncode,result.stderr)

    @unittest.skipUnless(os.name=='nt','Native WinForms control smoke runs on Windows CI; installed visual/MSFS acceptance remains separate.')
    def test_actual_windows_form_has_named_checks_dropdowns_notes_and_explicit_pass_confirmation(self):
        state=self.state();state_file=self.root/'state.json';state_file.write_text(json.dumps(state),encoding='utf-8')
        script=r"""
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$errors=$null;$tokens=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($env:P08B_FORM,[ref]$tokens,[ref]$errors)
if($errors){throw ($errors|Out-String)}
$functions=$ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$true)
. ([ScriptBlock]::Create(($functions|ForEach-Object {$_.Extent.Text}) -join "`n"))
$state=Get-Content -LiteralPath $env:P08B_STATE -Raw|ConvertFrom-Json
$view=New-080ChecklistForm $state
try {
    $view.Form.CreateControl();$view.Grid.CreateControl()
    if($view.Grid.Rows.Count -ne $state.rows.Count){throw 'A named check is missing.'}
    if($view.Grid.Columns[1].Items.Count -ne 4 -or $view.Confirmation.Checked){throw 'Result/confirmation defaults changed.'}
    if(-not $view.Grid.AccessibleName -or -not $view.Instructions.AccessibleName){throw 'Accessible names missing.'}
    if((Get-080ChecklistRequest $view).entries.Count){throw 'Untouched rows must not be saved.'}
    $view.Grid.Rows[0].Cells[1].Value='Passed';$view.Grid.Rows[0].Cells[2].Value='Synthetic Windows control smoke only.'
    $blocked=$false;try{$null=Get-080ChecklistRequest $view}catch{$blocked=$true}
    if(-not $blocked){throw 'Passed without personal confirmation was accepted.'}
    $view.Confirmation.Checked=$true;$request=Get-080ChecklistRequest $view
    if($request.entries.Count -ne 1 -or -not $request.observed -or $request.entries[0].id -ne 'isolation'){throw 'Changed row mapping is incorrect.'}
    Write-Output 'Actual Windows controls created; defaults, keyboard-accessible labels and explicit confirmation checked. No installed visual/MSFS acceptance.'
} finally {$view.Form.Dispose()}
"""
        result=subprocess.run([shutil.which('pwsh'),'-NoProfile','-Command',script],env={**os.environ,
            'P08B_FORM':str(ROOT/'scripts/release/Open-080Checklist.ps1'),'P08B_STATE':str(state_file)},capture_output=True,text=True,encoding='utf-8',timeout=60)
        self.assertEqual(0,result.returncode,result.stderr)


if __name__=='__main__':unittest.main()
