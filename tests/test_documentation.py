"""Repository/package documentation closure and frozen QA/scientific evidence protection."""
import csv
import hashlib
import importlib.util
import io
from pathlib import Path
import re
import subprocess
import tarfile
import tempfile
import unittest
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parents[1]


def committed_blobs(root, paths):
    """Read canonical object bytes, never archive/checkout-filtered content."""
    data = subprocess.check_output(['git', 'cat-file', '--batch'], cwd=root,
                                   input=''.join('HEAD:'+path+'\n' for path in paths).encode('utf-8'))
    offset = 0
    result = {}
    for path in paths:
        end = data.index(b'\n', offset)
        oid, kind, size = data[offset:end].split()
        if kind != b'blob':
            raise ValueError('Protected path is not a Git blob: '+path)
        start = end+1; finish = start+int(size)
        result[path] = data[start:finish]
        if data[finish:finish+1] != b'\n':
            raise ValueError('Invalid Git object framing: '+path)
        offset = finish+1
    if offset != len(data):
        raise ValueError('Unexpected Git object output')
    return result


def links(text):
    text = re.sub(r'^```.*?^```[^\n]*$', '', text, flags=re.M | re.S)
    return re.findall(r'!?\[[^\]\n]*\]\((<[^>]+>|[^\s)]+)(?:\s+"[^"]*")?\)', text)


def anchors(text):
    found = set(re.findall(r'<a\s+(?:name|id)=["\']([^"\']+)', text))
    counts = {}
    for heading in re.findall(r'^#{1,6}\s+(.+?)\s*#*$', text, re.M):
        heading = re.sub(r'\[([^]]+)\]\([^)]+\)', r'\1', heading)
        value = re.sub(r'[^\w -]', '', re.sub(r'<[^>]+>', '', heading).lower()).strip().replace(' ', '-')
        count = counts.get(value, 0); counts[value] = count+1
        found.add(value if not count else value+'-'+str(count))
    return found


def broken(text, parent):
    failures = []
    for target in links(text):
        parsed = urlsplit(target.strip('<>'))
        if parsed.scheme or parsed.netloc or target.startswith('/'):
            continue
        if not parsed.path:
            if parsed.fragment and unquote(parsed.fragment) not in anchors(text):
                failures.append(target+' (missing local anchor)')
            continue
        path = (parent/unquote(parsed.path)).resolve()
        if not path.exists():
            failures.append(target+' (missing path)')
        elif parsed.fragment and path.suffix.lower() == '.md' and unquote(parsed.fragment) not in anchors(path.read_text(encoding='utf-8')):
            failures.append(target+' (missing anchor)')
    return failures


class DocumentationTests(unittest.TestCase):
    def test_current_markdown_paths_and_anchors_resolve(self):
        names = subprocess.check_output(['git', 'ls-files', '-co', '--exclude-standard', '*.md'], cwd=ROOT, text=True).splitlines()
        failures = []
        for name in sorted(set(names)):
            path = ROOT/name
            if path.is_file():
                failures.extend(name+': '+value for value in broken(path.read_text(encoding='utf-8'), path.parent))
        self.assertEqual([], failures)

    def test_protected_qa_reference_science_and_legal_evidence_keeps_its_original_bytes(self):
        with (ROOT/'docs/roadmap/v0.5.0-documentation-inventory.csv').open(encoding='utf-8', newline='') as stream:
            protected = [row for row in csv.DictReader(stream) if row['category'] in {'qa-protected', 'qa-reference-baseline', 'scientific-evidence', 'legal-runtime'}]
        self.assertGreaterEqual(len(protected), 68)
        paths = [row['path'] for row in protected]
        # Compare exact repository blobs and reject current edits. Windows Git may
        # legitimately checkout text as CRLF; do not confuse that with altered evidence.
        self.assertEqual('', subprocess.check_output(['git', 'diff', 'HEAD', '--name-only', '--', *paths], cwd=ROOT, text=True))
        blobs = committed_blobs(ROOT, paths)
        for row in protected:
            with self.subTest(path=row['path']):
                self.assertEqual(row['sha256'], hashlib.sha256(blobs[row['path']]).hexdigest())

    def test_raw_evidence_hashing_is_independent_of_windows_archive_conversion(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            def git(*args):
                return subprocess.check_output(['git', *args], cwd=root, stderr=subprocess.DEVNULL)
            git('init'); git('config', 'core.autocrlf', 'false')
            original = b'protected text\nwith original LF bytes\n'
            binary = b'\x00\xff\r\noriginal binary\n'
            (root/'record.txt').write_bytes(original); (root/'image.bin').write_bytes(binary)
            (root/'.gitattributes').write_text('record.txt text\nimage.bin -text\n', encoding='utf-8')
            git('add', '.'); git('-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '-qm', 'Synthetic evidence bytes')
            git('config', 'core.autocrlf', 'true')
            # Demonstrate the original bug rather than normalizing the expected evidence.
            archive = git('archive', 'HEAD', 'record.txt')
            with tarfile.open(fileobj=io.BytesIO(archive)) as files:
                self.assertEqual(original.replace(b'\n', b'\r\n'), files.extractfile('record.txt').read())
            self.assertEqual({'record.txt': original, 'image.bin': binary}, committed_blobs(root, ['record.txt', 'image.bin']))

    def test_distribution_template_is_valid_in_source_and_rendered_contexts(self):
        spec = importlib.util.spec_from_file_location('docs_package_release', ROOT/'scripts/release/package_release.py')
        package = importlib.util.module_from_spec(spec); spec.loader.exec_module(package)
        source = ROOT/'docs/release-readme.md'
        self.assertEqual([], broken(source.read_text(encoding='utf-8'), source.parent))
        rendered = package.distribution_readme()
        self.assertEqual([], broken(rendered, ROOT))
        self.assertIn('](docs/README.md)', rendered)
        self.assertIn('](INSTALL.md)', rendered)

    def test_retired_files_are_not_consumed_by_source_build_tests_or_workflows(self):
        with (ROOT/'docs/roadmap/v0.5.0-documentation-inventory.csv').open(encoding='utf-8', newline='') as stream:
            retired = [row['path'] for row in csv.DictReader(stream) if row['category'] in {'obsolete-handoff', 'historical-development', 'historical-image'} or row['path'] == 'docs/release.md']
        failures = []
        names = subprocess.check_output(['git', 'ls-files', '-co', '--exclude-standard'], cwd=ROOT, text=True).splitlines()
        for name in sorted(set(names)):
            path = ROOT/name
            if path == Path(__file__).resolve():
                continue  # This policy test names tombstones; it does not consume those files.
            if not name.startswith(('src/', 'scripts/', 'tests/', '.github/')) or not path.is_file() or path.suffix not in {'.py', '.ps1', '.mjs', '.js', '.cs', '.csproj', '.yml'}:
                continue
            text = path.read_text(encoding='utf-8')
            for removed in retired:
                # Screenshots generated anew under ignored artifacts are not dependencies
                # on the retired committed illustration. Match paths and quoted docs.
                if removed in text or re.search(r'["\']'+re.escape(Path(removed).name)+r'["\']', text):
                    failures.append(name+' consumes '+removed)
        self.assertEqual([], failures)


if __name__ == '__main__':
    unittest.main()
