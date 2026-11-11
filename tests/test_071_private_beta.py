"""Synthetic packaging/control-flow fixtures; never SDK, signature or installed acceptance."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
import test_beta_installer as legacy
import installer_fixtures

beta=legacy.beta
from private_beta_identity import identity, BETA_VERSION, ENGINEERING_SOURCE


class PrivateBeta2Tests(unittest.TestCase):
    def setUp(self):
        self.fixture=legacy.BetaInstallerTests('test_maintenance_payload_cannot_enter_the_legacy_v070_installer')
        self.addCleanup(self.fixture.doCleanups)
        self.fixture.setUp()
        self.app=self.fixture.app;self.root=self.fixture.flow.root;self.source=legacy.flow.COMMIT
        self.profile=identity(BETA_VERSION);self.output=self.root/'beta2'
        old=self.app/'Toolbar/aeroscience-validation-v0-7-0-toolbar'
        package=self.app/'Toolbar'/self.profile['toolbar_package'];old.rename(package)
        for file in list(package.rglob('*')):
            if not file.is_file():continue
            relative=file.relative_to(package).as_posix()
            new=relative.replace('aeroscience_validation_v070_panel',self.profile['registration']).replace('AeroScienceValidationV070',self.profile['toolbar_folder']).replace('ICON_AEROSCIENCE_VALIDATION_V070',self.profile['icon'])
            data=file.read_bytes()
            if file.suffix=='.html':data=data.replace(b'PANEL_AEROSCIENCE_VALIDATION_V070',self.profile['panel'].encode())
            if file.name=='AeroScience.js':data=data.replace(b'AeroScience.Validation.0_7_0',self.profile['channel'].encode())
            target=package/new;target.parent.mkdir(parents=True,exist_ok=True)
            if target!=file:file.unlink()
            target.write_bytes(data)
        beta.write(package/'manifest.json',{**beta.p.read_json(package/'manifest.json'),'title':self.profile['title'],'package_version':'0.7.1'})
        descriptor=package/'html_ui/InGamePanels'/self.profile['toolbar_folder']/'toolbar-compatibility.json'
        beta.write(descriptor,dict(protocol=1,applicationVersion=BETA_VERSION,minimumCompanionVersion=BETA_VERSION,privateBetaVersion=BETA_VERSION,profile='validation',sourceCommit=self.source))
        beta.write(package/'layout.json',dict(content=[dict(path=f.relative_to(package).as_posix(),size=f.stat().st_size,date=1) for f in package.rglob('*') if f.is_file() and f.name not in {'manifest.json','layout.json'}]))
        sdk=beta.p.read_json(self.app/'Toolbar/sdk-build.json')
        sdk.update(private_beta_version=BETA_VERSION,files=beta.private.hashes(package))
        sdk['native_javascript']['files']=[dict(path='PackageSources/'+name,sourceSha256='a'*64,compiledSha256=digest) for name,digest in sdk['files'].items() if name.endswith('.js')]
        beta.write(self.app/'Toolbar/sdk-build.json',sdk)
        info=beta.p.read_json(self.app/'build-info.json');info.update(version=BETA_VERSION,private_beta_version=BETA_VERSION,scientific_release_approved=False,installed_acceptance='Not tested')
        beta.write(self.app/'build-info.json',info)
        proof=dict(version=BETA_VERSION,source_commit=self.source,binaries={name:dict(product_version=BETA_VERSION+'+'+self.source,file_version='0.7.1.2',sha256=beta.installer.sha(self.app/name)) for name in ('AeroScience.dll','AeroScience.Engine.dll')})
        beta.write(self.app/'PRIVATE-PROFILE.json',proof)
        (self.app/'VALIDATION-RECEIPT.json').unlink()
        beta.private.record(self.app,self.source,BETA_VERSION)

    def stage(self):
        return beta.stage(self.app,self.output/'payload',self.source,self.fixture.flow.report,BETA_VERSION)

    def refresh(self):
        self.fixture.refresh_receipt()

    def test_beta2_projection_preserves_originals_and_cannot_enter_public_or_legacy_pipeline(self):
        before=beta.private.hashes(self.app);self.stage()
        self.assertEqual(before,beta.private.hashes(self.app))
        payload=self.output/'payload';beta.payload(payload,self.source,BETA_VERSION)
        meta=beta.p.read_json(payload/'BETA-BUILD.json')
        self.assertEqual(BETA_VERSION,meta['version']);self.assertEqual(ENGINEERING_SOURCE,meta['accepted_engineering_source'])
        self.assertFalse(meta['public_release_approved']);self.assertFalse(meta['scientific_release_approved'])
        self.assertEqual('Not verified',meta['installed_acceptance'])
        self.assertEqual(before['VALIDATION-RECEIPT.json'],beta.installer.sha(payload/beta.SOURCE_RECEIPT))
        with self.assertRaisesRegex(ValueError,'Private beta identity'):beta.payload(payload,self.source)
        with self.assertRaisesRegex(ValueError,'not a production release'):beta.p.validate_companion(payload/'application')
        with self.assertRaisesRegex(ValueError,'Unsupported private beta'):identity('0.7.1-beta.1')

    def test_source_binary_version_sdk_proof_and_runtime_remain_mandatory(self):
        proof_file=self.app/'PRIVATE-PROFILE.json';original=proof_file.read_bytes()
        proof=beta.p.read_json(proof_file);proof['binaries']['AeroScience.Engine.dll']['product_version']='0.7.1+'+self.source
        beta.write(proof_file,proof);self.refresh()
        with self.assertRaisesRegex(ValueError,'binary version/source/hash'):self.stage()
        proof_file.write_bytes(original);self.refresh()
        sdk_file=self.app/'Toolbar/sdk-build.json';sdk=beta.p.read_json(sdk_file);sdk['sdk_contract']='not tested'
        beta.write(sdk_file,sdk);self.refresh()
        with self.assertRaisesRegex(ValueError,'SDK build evidence'):self.stage()
        sdk['sdk_contract']='passed';beta.write(sdk_file,sdk);self.refresh()
        report=beta.p.read_json(self.fixture.flow.report);report['export_check']='not run';beta.write(self.fixture.flow.report,report)
        with self.assertRaisesRegex(ValueError,'loading/export'):self.stage()
        self.assertFalse(self.output.exists())

    def test_old_beta_paths_sources_and_scientific_data_cannot_be_relabelled(self):
        info_file=self.app/'build-info.json';info=beta.p.read_json(info_file);info['version']='0.7.0'
        beta.write(info_file,info);self.refresh()
        with self.assertRaisesRegex(ValueError,'version/source/public policy'):self.stage()
        info['version']=BETA_VERSION;beta.write(info_file,info);self.refresh()
        (self.app/'settings.json').write_text('{"credential":"SYNTHETIC MUST NOT SHIP"}')
        self.refresh()
        with self.assertRaisesRegex(ValueError,'personal mission data'):self.stage()
        self.assertFalse((self.output/beta.RECEIPT).exists())

    def test_source_output_overlap_is_rejected_before_any_copy(self):
        before=beta.private.hashes(self.app)
        with self.assertRaisesRegex(ValueError,'must not overlap'):
            beta.stage(self.app,self.app/'nested',self.source,self.fixture.flow.report,BETA_VERSION)
        self.assertEqual(before,beta.private.hashes(self.app))

    def test_exact_exe_checksum_receipt_and_signed_prerequisite_are_bound_to_beta2(self):
        self.stage();setup=self.output/f'AeroScience-Setup-v{BETA_VERSION}.exe';setup.write_bytes(installer_fixtures.executable_fixture())
        compiler=self.root/'ISCC.exe';compiler.write_text('SYNTHETIC COMPILER FIXTURE')
        vc=self.root/'VC_redist.x64.exe';vc.write_bytes(installer_fixtures.executable_fixture())
        tools=self.root/'tools.json'
        report=dict(compiler_sha256=beta.installer.sha(compiler),compiler_version='6.3.3',vc_sha256=beta.installer.sha(vc),vc_version='14.0 SYNTHETIC',vc_signature='NotSigned',vc_publisher='Microsoft Corporation - SYNTHETIC CONTROL FLOW ONLY',vc_architecture='x64',setup_product_version=BETA_VERSION,setup_product_name=self.profile['title'],setup_numeric_version='0.7.1.2',setup_sha256=beta.installer.sha(setup))
        beta.write(tools,report)
        with self.assertRaisesRegex(ValueError,'signed Microsoft'):beta.record(self.output,self.source,compiler,vc,tools,BETA_VERSION)
        report['vc_signature']='Valid' # Fake control-flow value; never an Authenticode result.
        report['setup_product_version']='0.7.1-beta.1';beta.write(tools,report)
        with self.assertRaisesRegex(ValueError,'installer version/source'):beta.record(self.output,self.source,compiler,vc,tools,BETA_VERSION)
        report['setup_product_version']=BETA_VERSION;beta.write(tools,report)
        beta.record(self.output,self.source,compiler,vc,tools,BETA_VERSION);beta.verify(self.output,self.source,BETA_VERSION)
        receipt=beta.p.read_json(self.output/beta.RECEIPT)
        self.assertEqual(self.profile['app_id'],receipt['installer_app_id']);self.assertFalse(receipt['scientific_release_approved'])
        self.assertEqual(BETA_VERSION,receipt['setup_metadata']['setup_product_version'])
        setup.write_bytes(setup.read_bytes()+b'changed')
        with self.assertRaisesRegex(ValueError,'checksum'):beta.verify(self.output,self.source,BETA_VERSION)

    def test_staged_native_namespace_and_metadata_are_beta2_without_changing_templates(self):
        template=beta.p.REPO/'msfs-toolbar';before=beta.private.hashes(template)
        stage=self.root/'native';shutil.copytree(template,stage)
        beta.private.stage_toolbar(stage,self.source,BETA_VERSION)
        base=stage/'PackageSources/html_ui/InGamePanels'/self.profile['toolbar_folder']
        js=(base/'AeroScience.js').read_text();html=(base/'AeroScience.html').read_text()
        self.assertIn(self.profile['channel']+'.Command.v1',js);self.assertNotIn('AeroScience.Validation.0_7_0',js)
        self.assertIn('validationSource:"'+self.source+'"',js);self.assertIn(self.profile['title'],html)
        self.assertEqual(BETA_VERSION,beta.p.read_json(base/'toolbar-compatibility.json')['applicationVersion'])
        import xml.etree.ElementTree as ET
        self.assertEqual('0.7.1',ET.parse(stage/'PackageDefinitions'/ (self.profile['toolbar_package']+'.xml')).getroot().attrib['Version'])
        self.assertEqual(before,beta.private.hashes(template))

    @unittest.skipUnless(shutil.which('pwsh') and sys.platform!='win32','Linux PowerShell preflight')
    def test_entrypoint_rejects_linux_before_creating_output(self):
        output=self.root/'must-not-exist'
        result=subprocess.run(['pwsh','-NoProfile','-File',str(beta.p.REPO/'scripts/release/Build-071PrivateBeta.ps1'),'-OutputDirectory',str(output),'-RuntimeApprovalRecord',str(self.fixture.flow.approval),'-RuntimeRedistributionConfirmed'],capture_output=True,text=True)
        self.assertNotEqual(0,result.returncode);self.assertIn('0.7.1-beta.2 on Windows x64',result.stdout+result.stderr);self.assertFalse(output.exists())

    def test_public_gate_files_are_unchanged_from_accepted_engineering_source(self):
        for name in ('scripts/release/verify_scientific_acceptance.py','scripts/release/Build-ReleaseCandidate.ps1'):
            expected=subprocess.run(['git','show',ENGINEERING_SOURCE+':'+name],cwd=beta.p.REPO,capture_output=True,check=True).stdout
            self.assertEqual(expected.decode().replace('\r\n','\n'),(beta.p.REPO/name).read_text(encoding='utf-8').replace('\r\n','\n'))


if __name__=='__main__':unittest.main()
