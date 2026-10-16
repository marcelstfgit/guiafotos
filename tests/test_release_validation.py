import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

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

    def report(self):
        products = []
        names = ['GuiaFotos-0.2.0-' + suffix for suffix in ('Setup.exe', 'portatil-windows-x64.zip', 'fonte.zip', 'fontes-dependencias.zip')]
        names.append('ImageMagick-7.1.2-33-Windows.7z')
        for name in names:
            data = name.encode()
            (self.folder / name).write_bytes(data)
            products.append({'file': name, 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()})
        return {'version': '0.2.0', 'products': products}

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
        report.update(windows_execution_tested=True, pretrained_face_inference_tested=True,
                      validation_commit='a' * 40, windows_test_results=validation.test_results(self.folder))
        report_path = self.folder / 'GuiaFotos-0.2.0-validacao-pacotes.json'
        report_path.write_text(json.dumps(report))
        names = [p['file'] for p in report['products']] + [report_path.name]
        (self.folder / 'GuiaFotos-0.2.0-SHA256SUMS.txt').write_text(''.join(
            f'{validation.digest(self.folder / n)}  {n}\n' for n in names))
        env = os.environ.copy()
        env.update(GITHUB_SHA='a' * 40, GITHUB_REF_NAME='v0.2.0')
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
