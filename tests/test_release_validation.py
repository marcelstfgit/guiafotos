import hashlib
import importlib.util
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest
import zipfile
import zlib

SCRIPT = Path(__file__).resolve().parents[1] / 'scripts/record-windows-validation.py'
spec = importlib.util.spec_from_file_location('release_validation', SCRIPT)
validation = importlib.util.module_from_spec(spec)
spec.loader.exec_module(validation)


class ReleaseValidationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.folder = Path(self.temp.name)
        for suite in ('core', 'advanced', 'grouping', 'installer'):
            (self.folder / f'test-{suite}.log').write_text('PASS fixture\nRESULT passed=1 failed=0\n')
        for suite in ('wpf', 'install-portable'):
            (self.folder / f'test-{suite}.log').write_text('PASS smoke\n')

    def test_all_suites_required(self):
        self.assertEqual(len(validation.test_results(self.folder)), 6)
        (self.folder / 'test-wpf.log').unlink()
        with self.assertRaises(FileNotFoundError):
            validation.test_results(self.folder)

    def test_skipped_models_cannot_be_published_as_validated(self):
        with (self.folder / 'test-advanced.log').open('a') as f:
            f.write('SKIP pretrained models\n')
        with self.assertRaises(ValueError):
            validation.test_results(self.folder)

    def test_failed_and_interrupted_suites_are_rejected(self):
        for text in ('FAIL fixture\nRESULT passed=3 failed=1\n', 'PASS partial\n'):
            (self.folder / 'test-core.log').write_text(text)
            with self.assertRaises(ValueError):
                validation.test_results(self.folder)

    def ui_archive(self, commit='a' * 40):
        images = []
        png = b'\x89PNG\r\n\x1a\n'
        def chunk(kind, data):
            return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data))
        png += chunk(b'IHDR', struct.pack('>IIBBBBB', 1366, 768, 8, 2, 0, 0, 0))
        png += chunk(b'IDAT', zlib.compress((b'\0' + b'\0' * (1366 * 3)) * 768)) + chunk(b'IEND', b'')
        path = self.folder / 'GuiaFotos-0.2.1-interface.zip'
        with zipfile.ZipFile(path, 'w') as archive:
            for name in sorted(validation.UI_IMAGES):
                archive.writestr(name, png)
                images.append({'file': name, 'pixels': [1366, 768], 'sha256': hashlib.sha256(png).hexdigest()})
            archive.writestr('validacao-interface.json', json.dumps({'commit': commit, 'passed': 1, 'failed': 0, 'checks': ['fixture'], 'screenshots': images}))
            archive.writestr('ui-sample-photos.json', '{}')
            archive.writestr('LEIA-ME.txt', 'test fixture')
        return path

    def test_missing_failed_or_stale_ui_evidence_is_rejected(self):
        path = self.ui_archive()
        with zipfile.ZipFile(path) as archive:
            self.assertEqual(validation.ui_evidence(archive.read, 'a' * 40)['failed'], 0)
            with self.assertRaises(ValueError):
                validation.ui_evidence(archive.read, 'b' * 40)
            report = json.loads(archive.read('validacao-interface.json'))
            def read(name):
                return json.dumps(report).encode() if name == 'validacao-interface.json' else archive.read(name)
            report['failed'] = 1
            with self.assertRaises(ValueError): validation.ui_evidence(read, 'a' * 40)
            report['failed'] = 0
            report['screenshots'].pop()
            with self.assertRaises(ValueError): validation.ui_evidence(read, 'a' * 40)
            report['screenshots'].append({'file': 'recorte-1366.png', 'pixels': [1366, 768], 'sha256': '0' * 64})
            with self.assertRaises(ValueError): validation.ui_evidence(read, 'a' * 40)

    def report(self):
        products = []
        names = ['GuiaFotos-0.2.1-' + suffix for suffix in ('Setup.exe', 'portatil-windows-x64.zip', 'fonte.zip', 'fontes-dependencias.zip')]
        names.append('ImageMagick-7.1.2-33-Windows.7z')
        for name in names:
            data = name.encode()
            (self.folder / name).write_bytes(data)
            products.append({'file': name, 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()})
        path = self.ui_archive()
        products.append({'file': path.name, 'bytes': path.stat().st_size, 'sha256': validation.digest(path)})
        return {'version': '0.2.1', 'products': products}

    def test_corresponding_sources_and_unchanged_binaries_are_required(self):
        report = self.report()
        validation.verify_products(self.folder, report)
        report['products'].pop()
        with self.assertRaises(ValueError):
            validation.verify_products(self.folder, report)
        report = self.report()
        (self.folder / report['products'][0]['file']).write_bytes(b'changed')
        with self.assertRaises(ValueError):
            validation.verify_products(self.folder, report)

    def test_release_requires_checks_for_the_exact_tag_and_commit(self):
        report = self.report()
        results = validation.test_results(self.folder)
        results['desktop-ui'] = {'passed': 1, 'failed': 0}
        report.update(windows_execution_tested=True, pretrained_face_inference_tested=True, desktop_ui_flow_tested=True,
                      validation_commit='a' * 40, windows_test_results=results)
        report_path = self.folder / 'GuiaFotos-0.2.1-validacao-pacotes.json'
        report_path.write_text(json.dumps(report))
        names = [p['file'] for p in report['products']] + [report_path.name]
        (self.folder / 'GuiaFotos-0.2.1-SHA256SUMS.txt').write_text(''.join(
            f'{validation.digest(self.folder / n)}  {n}\n' for n in names))
        env = os.environ.copy()
        env.update(GITHUB_SHA='a' * 40, GITHUB_REF_NAME='v0.2.1')
        def run():
            return subprocess.run([sys.executable, str(SCRIPT), '--artifacts', str(self.folder), '--verify-only'],
                                  env=env, capture_output=True).returncode
        self.assertEqual(run(), 0)
        env['GITHUB_SHA'] = 'b' * 40
        self.assertNotEqual(run(), 0)
        env.update(GITHUB_SHA='a' * 40, GITHUB_REF_NAME='v0.3.0')
        self.assertNotEqual(run(), 0)


if __name__ == '__main__':
    unittest.main()
