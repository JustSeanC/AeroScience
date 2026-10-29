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

    def test_v060_development_is_fast_but_milestones_and_other_branches_remain_full(self):
        self.assertEqual('fast', ci.validation_tier('push', 'refs/heads/develop/v0.6.0'))
        self.assertEqual('fast', ci.validation_tier('workflow_dispatch', 'refs/heads/develop/v0.6.0', requested='fast'))
        self.assertEqual('full', ci.validation_tier('push', 'refs/heads/develop/v0.6.0', message='P01 [full-validation]'))
        self.assertEqual('full', ci.validation_tier('workflow_dispatch', 'refs/heads/develop/v0.6.0', requested='full'))
        self.assertEqual('windows-profile', ci.validation_tier('workflow_dispatch', 'refs/heads/develop/v0.6.0', requested='windows-profile'))
        for ref in ('refs/heads/release/v0.5.0-acceptance', 'refs/heads/main', 'refs/tags/v0.6.0'):
            self.assertEqual('full', ci.validation_tier('push', ref))

    def test_v070_keeps_explicit_full_milestone_validation(self):
        self.assertEqual('fast', ci.validation_tier('push', 'refs/heads/develop/v0.7.0'))
        self.assertEqual('full', ci.validation_tier('push', 'refs/heads/develop/v0.7.0', message='P01 [full-validation]'))
        self.assertEqual('full', ci.validation_tier('pull_request', 'refs/pull/23/merge', 'develop/v0.7.0', message='[full-validation]'))

    def test_evidence_changes_keep_migration_archive_and_contract_coverage(self):
        selected = ci.plan(['src/AeroScience.Missions/MissionEvidenceStore.cs'])
        for name in ('EvidenceContractsTests', 'MissionEvidenceStoreTests', 'AlphaDataTests', 'ResearchTests', 'ScientificWorkspaceTests'):
            self.assertIn(name, selected['dotnetFilter'])
        self.assertIn('not full integration', selected['scope'])

    def test_forward_integration_pr_requires_full_but_routine_development_stays_fast(self):
        self.assertEqual('full',ci.validation_tier('pull_request','refs/pull/20/merge','develop/v0.6.0',head='integration/v0.6.0-frozen-v0.5.0'))
        self.assertEqual('fast',ci.validation_tier('pull_request','refs/pull/21/merge','develop/v0.6.0',head='feature/ordinary-development'))

    def test_windows_profile_diagnostic_is_manual_development_only(self):
        self.assertEqual('windows-profile', ci.validation_tier('workflow_dispatch', 'refs/heads/develop/v0.5.0', requested='windows-profile'))
        for ref in ('refs/heads/main', 'refs/tags/v0.5.0'):
            self.assertEqual('full', ci.validation_tier('workflow_dispatch', ref, requested='windows-profile'))
        self.assertEqual('fast', ci.validation_tier('push', 'refs/heads/develop/v0.5.0', requested='windows-profile'))
        workflow = (ROOT/'.github/workflows/ci.yml').read_text(encoding='utf-8')
        self.assertIn("if: needs.validation-scope.outputs.tier == 'windows-profile'", workflow)
        self.assertEqual(3, workflow.count("if: needs.validation-scope.outputs.tier == 'full'"))
        self.assertIn("  organization-deployment:", workflow)
        self.assertIn("verify_organization_container.py", workflow)
        self.assertEqual(2, workflow.count('./scripts/build/Publish-ValidationCompanion.ps1 -ArtifactsPath artifacts/validation-probe'))

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

    def test_each_windows_recovery_failure_is_checked_before_the_next_native_command(self):
        workflow = (ROOT/'.github/workflows/ci.yml').read_text(encoding='utf-8')
        windows = workflow.split('  windows-companion:', 1)[1]
        lines = [line.strip() for line in windows.splitlines() if line.strip()]
        for probe in ('smoke_recovery.py', 'smoke_evidence_recovery.py', 'smoke_library_recovery.py'):
            index = next(i for i, line in enumerate(lines) if line.startswith('python scripts/build/'+probe))
            self.assertTrue(lines[index+1].startswith('if ($LASTEXITCODE -ne 0)'), probe)
            self.assertIn('throw ', lines[index+1], probe)
