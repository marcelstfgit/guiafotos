#!/usr/bin/env python3
"""Record completed Windows checks, or verify their artifacts before release."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def test_results(folder):
    result = {}
    for suite in ('core', 'advanced', 'grouping', 'installer'):
        text = (folder / f'test-{suite}.log').read_text(encoding='utf-8-sig')
        summary = re.findall(r'^RESULT passed=(\d+) failed=(\d+)', text, re.M)
        if len(summary) != 1 or int(summary[0][0]) == 0 or int(summary[0][1]) != 0 or re.search(r'^(FAIL|SKIP)\b', text, re.M):
            raise ValueError(f'Suite did not complete without failures/skips: {suite}')
        result[suite] = {'passed': int(summary[0][0]), 'failed': 0}
    for suite in ('wpf', 'install-portable'):
        text = (folder / f'test-{suite}.log').read_text(encoding='utf-8-sig')
        if not re.search(r'^PASS ', text, re.M) or re.search(r'^(FAIL|SKIP)\b', text, re.M):
            raise ValueError(f'Windows smoke check missing: {suite}')
        result[suite] = {'passed': 1, 'failed': 0}
    return result


def verify_products(folder, report):
    expected = {
        f'GuiaFotos-{report["version"]}-Setup.exe',
        f'GuiaFotos-{report["version"]}-portatil-windows-x64.zip',
        f'GuiaFotos-{report["version"]}-fonte.zip',
        f'GuiaFotos-{report["version"]}-fontes-dependencias.zip',
        'ImageMagick-7.1.2-33-Windows.7z',
    }
    if {p['file'] for p in report['products']} != expected or len(report['products']) != 5:
        raise ValueError('Release assets are incomplete')
    for product in report['products']:
        path = folder / product['file']
        if path.stat().st_size != product['bytes'] or digest(path) != product['sha256']:
            raise ValueError(f'Artifact changed after packaging: {path.name}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--artifacts', type=Path, default=Path('artifacts'))
    parser.add_argument('--verify-only', action='store_true')
    args = parser.parse_args()
    reports = list(args.artifacts.glob('GuiaFotos-*-validacao-pacotes.json'))
    if len(reports) != 1:
        raise ValueError('Expected exactly one package report')
    report_path = reports[0]
    report = json.loads(report_path.read_text())
    verify_products(args.artifacts, report)
    commit = os.environ['GITHUB_SHA']
    if not re.fullmatch(r'[0-9a-f]{40}', commit):
        raise ValueError('Invalid validation commit')
    if args.verify_only:
        if not report.get('windows_execution_tested') or not report.get('pretrained_face_inference_tested') or report.get('validation_commit') != commit:
            raise ValueError('Release is missing Windows checks for this commit')
        if set(report.get('windows_test_results', {})) != {'core', 'advanced', 'grouping', 'installer', 'wpf', 'install-portable'}:
            raise ValueError('Release is missing a test suite')
        if any(result.get('failed') != 0 or result.get('passed', 0) < 1 for result in report['windows_test_results'].values()):
            raise ValueError('Release contains a failed or empty test suite')
        if os.environ.get('GITHUB_REF_NAME') != 'v' + report['version']:
            raise ValueError('Tag and package version differ')
        sums = args.artifacts / f'GuiaFotos-{report["version"]}-SHA256SUMS.txt'
        rows = {line.split('  ', 1)[1]: line.split('  ', 1)[0] for line in sums.read_text().splitlines()}
        names = {p['file'] for p in report['products']} | {report_path.name}
        if set(rows) != names or any(digest(args.artifacts / name) != rows[name] for name in names):
            raise ValueError('Release checksum inventory differs from artifacts')
        print('PASS release: tag, commit, Windows checks, dependencies and hashes')
        return
    if os.name != 'nt' or os.environ.get('RUNNER_OS') != 'Windows' or os.environ.get('GITHUB_ACTIONS') != 'true':
        raise ValueError('Windows evidence must be recorded by the Windows CI job')
    results = test_results(args.artifacts)
    report.update({
        'windows_execution_tested': True,
        'pretrained_face_inference_tested': True,
        'real_photo_accuracy_tested': False,
        'windows_validation_scope': 'automated integration and smoke tests',
        'windows_test_results': results,
        'validation_commit': commit,
        'validation_run_url': f'{os.environ["GITHUB_SERVER_URL"]}/{os.environ["GITHUB_REPOSITORY"]}/actions/runs/{os.environ["GITHUB_RUN_ID"]}',
    })
    report_path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    names = sorted([p['file'] for p in report['products']] + [report_path.name])
    (args.artifacts / f'GuiaFotos-{report["version"]}-SHA256SUMS.txt').write_text(
        ''.join(f'{digest(args.artifacts / name)}  {name}\n' for name in names), encoding='utf-8')
    print('PASS Windows evidence recorded; real-photo accuracy remains unmeasured')


if __name__ == '__main__':
    main()
