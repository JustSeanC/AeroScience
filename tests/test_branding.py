"""Approved-artwork and package checks; no Windows shell/MSFS visual acceptance claim."""
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import subprocess
import tempfile
import unittest
ROOT=Path(__file__).resolve().parents[1]
BRAND=ROOT/'src/AeroScience.Engine/wwwroot/branding'
spec=importlib.util.spec_from_file_location('desktop_branding',ROOT/'scripts/build/verify_desktop_branding.py')
compiled=importlib.util.module_from_spec(spec);spec.loader.exec_module(compiled)
class BrandingTests(unittest.TestCase):
    def test_checked_in_assets_regenerate_exactly_from_pinned_renderer(self):
        result=subprocess.run(['node','scripts/build/generate-branding.mjs','--check'],cwd=ROOT,capture_output=True,text=True)
        self.assertEqual(0,result.returncode,result.stdout+result.stderr)
    def test_transparent_png_dimensions_and_ico_frames_match_approved_manifest(self):
        manifest=json.loads((BRAND/'manifest.json').read_text())
        for path,entry in manifest['files'].items():
            data=(ROOT/path).read_bytes();self.assertEqual(entry['sha256'],hashlib.sha256(data).hexdigest());self.assertEqual(entry['bytes'],len(data))
            if path.endswith('.png'):
                size=int(path.rsplit('-',1)[1][:-4]);self.assertEqual(b'\x89PNG\r\n\x1a\n',data[:8]);self.assertEqual((size,size),struct.unpack_from('>II',data,16));self.assertEqual(6,data[25],path+' must retain RGBA')
        frames=compiled.icon_frames((BRAND/'aeroscience.ico').read_bytes());self.assertEqual([16,24,32,48,64,128,256],sorted(frames))
        for size,data in frames.items():self.assertEqual((BRAND/f'primary-{size}.png').read_bytes(),data)
    def test_approved_svg_sources_are_exact_live_copies_and_sdk_registration_stays_stable(self):
        source=ROOT/'assets/branding'
        self.assertEqual((source/'orbital-arc-primary.svg').read_bytes(),(ROOT/'src/AeroScience.Engine/wwwroot/icon.svg').read_bytes())
        self.assertEqual((source/'orbital-arc-toolbar-white.svg').read_bytes(),(ROOT/'msfs-toolbar/PackageSources/html_ui/icons/toolbar/ICON_AEROSCIENCE.svg').read_bytes())
        panel=(ROOT/'msfs-toolbar/PackageSources/InGamePanels/aeroscience_panel.xml').read_text()
        self.assertIn('ICON_AEROSCIENCE',panel);self.assertIn('PANEL_AEROSCIENCE',panel)
    def test_packaging_consumers_use_generated_icon_without_maskable_or_redraw_claim(self):
        project=(ROOT/'src/AeroScience.Desktop/AeroScience.Desktop.csproj').read_text();recipe=(ROOT/'scripts/installer/AeroScience.iss').read_text()
        self.assertIn('<ApplicationIcon>../AeroScience.Engine/wwwroot/branding/aeroscience.ico</ApplicationIcon>',project)
        self.assertIn('SetupIconFile=..\\..\\src\\AeroScience.Engine\\wwwroot\\branding\\aeroscience.ico',recipe)
        self.assertIn('UninstallDisplayIcon={app}\\AeroScience.exe',recipe)
        manifest=json.loads((ROOT/'src/AeroScience.Engine/wwwroot/manifest.webmanifest').read_text())
        self.assertEqual({'any'},set(icon['purpose'] for icon in manifest['icons']))
        for icon in manifest['icons']:self.assertTrue((ROOT/'src/AeroScience.Engine/wwwroot'/icon['src'].lstrip('/')).is_file())
    def test_branding_files_are_byte_preserved_on_windows_checkouts(self):
        paths=['assets/branding/orbital-arc-primary.svg','assets/branding/orbital-arc-toolbar-white.svg','src/AeroScience.Engine/wwwroot/icon.svg','src/AeroScience.Engine/wwwroot/branding/manifest.json','msfs-toolbar/PackageSources/html_ui/icons/toolbar/ICON_AEROSCIENCE.svg']
        result=subprocess.run(['git','check-attr','text','--',*paths],cwd=ROOT,capture_output=True,text=True,check=True)
        self.assertEqual(len(paths),result.stdout.count(': text: unset'))
