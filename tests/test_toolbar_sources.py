"""Check the diagnostic's native bootstrap parity, not simulator behavior."""
from html.parser import HTMLParser
from pathlib import Path
import unittest

PANEL = Path(__file__).resolve().parents[1]/'msfs-toolbar/PackageSources/html_ui/InGamePanels/AeroScience'


class BootstrapParser(HTMLParser):
    def __init__(self):
        super().__init__()
        self.native = []
        self.app_seen = False

    def handle_starttag(self, tag, attributes):
        attributes = dict(attributes)
        if tag == 'script':
            url = attributes.get('src')
        elif tag == 'link' and attributes.get('rel') == 'import':
            url = attributes.get('href')
        else:
            return
        if not url:
            return
        if url.startswith('/'):
            if self.app_seen:
                raise AssertionError(f'Native dependency follows application entry: {url}')
            self.native.append((tag, url))
        elif tag == 'script':
            self.app_seen = True


class ToolbarBootstrapTests(unittest.TestCase):
    def test_window_probe_loads_the_full_panels_native_dependencies_in_order(self):
        full, probe = BootstrapParser(), BootstrapParser()
        full.feed((PANEL/'AeroScience.html').read_text(encoding='utf-8'))
        probe.feed((PANEL/'WindowProbe.html').read_text(encoding='utf-8'))
        self.assertTrue(full.app_seen and probe.app_seen)
        for bootstrap in (full, probe):
            storage = ('script', '/JS/dataStorage.js')
            self.assertEqual(bootstrap.native.count(storage), 1,
                             'Native drag/resize initialization needs the SDK storage-ready bootstrap.')
            self.assertLess(bootstrap.native.index(('script', '/JS/common.js')),
                            bootstrap.native.index(storage))
            self.assertLess(bootstrap.native.index(storage),
                            bootstrap.native.index(('link', '/templates/ingameUi/ingameUi.html')))
        self.assertIn(('script', '/JS/buttons.js'), full.native)
        self.assertIn(('link', '/templates/NewPushButton/NewPushButton.html'), full.native)
        self.assertEqual(probe.native, full.native,
                         'A probe missing native storage/button/header dependencies is not a controlled comparison.')


if __name__ == '__main__':
    unittest.main()
