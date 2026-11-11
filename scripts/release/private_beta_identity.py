"""Explicit private identities. These never approve scientific or public releases."""
LEGACY_VERSION = '0.7.0-beta.1'
BETA_VERSION = '0.7.1-beta.2'
ENGINEERING_SOURCE = '331dbf20d9c443f088eb0f97de1363c4d7af26b7'


def identity(version=LEGACY_VERSION):
    if version == LEGACY_VERSION:
        return dict(version=version, application_version='0.7.0', package_version='0.7.0',
                    title='AeroScience 0.7.0 Beta 1', profile_title='AeroScience 0.7.0 Validation',
                    branch='develop/v0.7.0', folder='AeroScience-Validation-v0.7.0', port=5086,
                    toolbar_package='aeroscience-validation-v0-7-0-toolbar', toolbar_folder='AeroScienceValidationV070',
                    registration='aeroscience_validation_v070_panel', icon='ICON_AEROSCIENCE_VALIDATION_V070',
                    panel='PANEL_AEROSCIENCE_VALIDATION_V070', tag='ingamepanel-aeroscience-validation-v070',
                    native_class='IngamePanelAeroScienceValidationV070', symbol='AEROSCIENCE_VALIDATION_V070_TOOLBAR_IDENTITY',
                    channel='AeroScience.Validation.0_7_0', stage='toolbar-validation-v070',
                    guide='docs/v0.7.0-beta1-testers.md', app_id='6F9DE035-3672-4865-8EE2-E8E38573D14A')
    if version != BETA_VERSION:
        raise ValueError('Unsupported private beta identity; use exactly 0.7.1-beta.2 or the historical 0.7.0-beta.1.')
    return dict(version=version, application_version=version, package_version='0.7.1',
                title='AeroScience '+version, profile_title='AeroScience '+version,
                branch='build/v0.7.1-beta2', folder='AeroScience-Beta-v'+version, port=5087,
                toolbar_package='aeroscience-beta-v0-7-1-beta-2-toolbar', toolbar_folder='AeroScienceBetaV071Beta2',
                registration='aeroscience_beta_v071_beta2_panel', icon='ICON_AEROSCIENCE_BETA_V071_BETA2',
                panel='PANEL_AEROSCIENCE_BETA_V071_BETA2', tag='ingamepanel-aeroscience-beta-v071-beta2',
                native_class='IngamePanelAeroScienceBetaV071Beta2', symbol='AEROSCIENCE_BETA_V071_BETA2_TOOLBAR_IDENTITY',
                channel='AeroScience.Beta.0_7_1_beta_2', stage='toolbar-0.7.1-beta.2',
                guide='docs/v0.7.1-beta2-testers.md', app_id='54CD1188-2D18-4E77-B8F5-CAD530C332F6')
