"""Selection safety: targeted development never substitutes for full integration."""
import importlib.util
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('development_validation', ROOT/'scripts/build/development_validation.py')
ci = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ci)


class DevelopmentValidationTests(unittest.TestCase):
    def test_main_and_release_integration_keep_full_validation(self):
        for event, ref, base in [('push', 'refs/heads/main', ''), ('pull_request', 'refs/pull/42/merge', 'main'), ('push', 'refs/tags/v0.5.0', '')]:
            self.assertEqual('full', ci.validation_tier(event, ref, base))
        self.assertEqual('fast', ci.validation_tier('push', 'refs/heads/develop/v0.5.0'))
        self.assertEqual('full', ci.validation_tier('push', 'refs/heads/develop/v0.5.0', message='P02 completed [full-validation]'))
        self.assertEqual('full', ci.validation_tier('workflow_dispatch', 'refs/heads/develop/v0.5.0', requested='full'))
        self.assertEqual('fast', ci.validation_tier('workflow_dispatch', 'refs/heads/develop/v0.5.0', requested='fast'))
        self.assertEqual('full', ci.validation_tier('workflow_dispatch', 'refs/heads/main', requested='fast'))
        with self.assertRaises(ValueError):
            ci.validation_tier('workflow_dispatch', 'refs/heads/develop/v0.5.0', requested='none')

    def test_evidence_changes_keep_migration_archive_and_contract_coverage(self):
        selected = ci.plan(['src/AeroScience.Missions/MissionEvidenceStore.cs'])
        for name in ('EvidenceContractsTests', 'MissionEvidenceStoreTests', 'AlphaDataTests', 'ResearchTests', 'ScientificWorkspaceTests'):
            self.assertIn(name, selected['dotnetFilter'])
        self.assertIn('not full integration', selected['scope'])

    def test_changed_tests_and_browser_specs_are_never_omitted(self):
        selected = ci.plan(['tests/AeroScience.Tests/RemoteSensingTests.cs', 'tests/web/new-feature.spec.js', 'tests/test_packaging.py'])
        self.assertIn('RemoteSensingTests', selected['dotnetFilter'])
        self.assertIn('tests/web/new-feature.spec.js', selected['browserSpecs'])
        self.assertIn('test_packaging', selected['helperModules'])

    def test_unknown_dependencies_and_missing_diff_expand_safely(self):
        for paths in (['src/AeroScience.Shared/Models.cs'], ['tests/AeroScience.Tests/RemoteFixtureFiles.cs'], ['Directory.Build.props']):
            self.assertIsNone(ci.plan(paths)['dotnetFilter'])
        selected = ci.plan([], uncertain=True)
        self.assertIsNone(selected['dotnetFilter'])
        self.assertEqual(['*'], selected['helperModules'])
        self.assertEqual(['tests/web'], selected['browserSpecs'])

    def test_release_script_changes_keep_all_helper_checks(self):
        self.assertIn('*', ci.plan(['scripts/release/package_release.py'])['helperModules'])
        self.assertNotIn('test_scientific_acceptance', ci.PORTABLE_ONLY)
        self.assertNotIn('test_offline_assets', ci.PORTABLE_ONLY)

    def test_shared_browser_host_or_unknown_ui_dependencies_expand_browser_coverage(self):
        for path in ('tests/AeroScience.TestHost/Program.cs', 'tests/AeroScience.TestHost/BrowserTelemetryProvider.cs',
                     'playwright.config.js', 'tests/web/helpers/auth-setup.mjs', 'tests/web/helpers/auth-teardown.mjs',
                     'src/AeroScience.Engine/wwwroot/shared-new-module.js', 'src/AeroScience.Engine/wwwroot/app.css'):
            self.assertEqual(['tests/web'], ci.plan([path])['browserSpecs'])

    def test_session_controls_and_collection_planner_keep_their_functional_browser_checks(self):
        for path in ('src/AeroScience.Engine/wwwroot/science-sessions.js','src/AeroScience.Engine/wwwroot/collection-planner.js'):
            self.assertIn('tests/web/science-sessions.spec.js',ci.plan([path])['browserSpecs'])

    def test_lock_changes_keep_owner_shutdown_and_offline_tests(self):
        selected = ci.plan(['src/AeroScience.Engine/DataDirectoryLease.cs'])
        for name in ('ApiTests', 'OfflineApiTests', 'StorageTests'):
            self.assertIn(name, selected['dotnetFilter'])

    def test_full_and_release_commands_are_not_modified_by_selector(self):
        release = (ROOT/'scripts/release/Build-ReleaseCandidate.ps1').read_text(encoding='utf-8')
        self.assertIn('verify_scientific_acceptance.py', release)
        self.assertIn('smoke_evidence_recovery.py', release)
        self.assertIn('npm run test:web', release)
