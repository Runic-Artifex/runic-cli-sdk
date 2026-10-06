"""Contract tests for publishing the CI package artifact: python3 -m unittest discover -s eng/release"""
import json
import re
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).parent))
import release  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
SHA = 'a' * 40


def run(identifier, **extra):
    return {'id': identifier, 'head_sha': SHA, 'event': 'push', 'head_branch': 'main', 'path': release.CI_WORKFLOW,
            'repository': {'full_name': release.REPOSITORY}, 'head_repository': {'full_name': release.REPOSITORY},
            'status': 'completed', 'conclusion': 'success', 'html_url': f'https://example.test/runs/{identifier}', **extra}


def job(text, name):
    """The YAML text of one job in a workflow (two-space indented job keys)."""
    match = re.search(rf'^  {re.escape(name)}:\n(.*?)(?=^  [\w-]+:\n|\Z)', text, re.M | re.S)
    assert match, name
    return match.group(1)


class CiRunSelection(unittest.TestCase):
    def test_newest_successful_push_run_on_main_for_the_commit(self):
        self.assertEqual(release.select_ci_run([run(1), run(3), run(2, conclusion='failure')], release.REPOSITORY, SHA)['id'], 3)
        for other in [{'head_sha': 'b' * 40}, {'event': 'workflow_dispatch'}, {'event': 'pull_request'}, {'head_branch': 'feature'},
                      {'path': '.github/workflows/publish-preview.yml'}, {'head_repository': {'full_name': 'fork/runic-cli-sdk'}}]:
            with self.assertRaisesRegex(release.ReleaseError, 'No push run'):
                release.select_ci_run([run(9, **other)], release.REPOSITORY, SHA)

    def test_missing_running_or_failed_ci_fails_clearly(self):
        with self.assertRaisesRegex(release.ReleaseError, f'No push run of .github/workflows/ci.yml on main exists for {SHA}'):
            release.select_ci_run([], release.REPOSITORY, SHA)
        with self.assertRaisesRegex(release.ReleaseError, 'still in_progress'):
            release.select_ci_run([run(4, status='in_progress', conclusion=None)], release.REPOSITORY, SHA)
        with self.assertRaisesRegex(release.ReleaseError, 'concluded failure'):
            release.select_ci_run([run(5, conclusion='failure')], release.REPOSITORY, SHA)

    def test_single_unexpired_artifact_of_that_run(self):
        artifact = {'id': 70, 'name': release.ARTIFACT, 'expired': False, 'workflow_run': {'id': 7, 'head_sha': SHA}}
        self.assertIs(release.select_artifact([artifact, {**artifact, 'name': 'other'}], run(7)), artifact)
        for artifacts, message in [([], 'no single'), ([{**artifact, 'expired': True}], 'expired'),
                                   ([{**artifact, 'workflow_run': {'id': 7, 'head_sha': 'c' * 40}}], 'different'),
                                   ([{**artifact, 'id': None}], 'no id')]:
            with self.assertRaisesRegex(release.ReleaseError, message):
                release.select_artifact(artifacts, run(7))

    def test_queries_push_runs_of_the_commit(self):
        calls = []

        def api(repository, path, token):
            calls.append(path)
            if '/artifacts' in path:
                return {'artifacts': [{'id': 80, 'name': release.ARTIFACT, 'expired': False, 'workflow_run': {'id': 8, 'head_sha': SHA}}]}
            return {'workflow_runs': [run(8)]}
        found = release.find_ci_packages(release.REPOSITORY, SHA, 't', api)
        self.assertEqual(found['run-id'], '8')
        self.assertEqual(found['artifact'], release.ARTIFACT)
        self.assertEqual(found['artifact-id'], '80')
        self.assertRegex(calls[0], rf'^actions/workflows/ci.yml/runs\?head_sha={SHA}&event=push&branch=main')
        with self.assertRaisesRegex(release.ReleaseError, 'full commit SHA'):
            release.find_ci_packages(release.REPOSITORY, 'main', 't', api)


class CandidatePackages(unittest.TestCase):
    def pack(self, directory, version='1.2.3-preview.1', commit=SHA, names=release.PACKAGES, **override):
        for name in names:
            meta = {'id': name, 'declared': version, 'url': f'https://github.com/{release.REPOSITORY}', 'commit': commit, **override}
            nuspec = (f'<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><id>{meta["id"]}</id>'
                      f'<version>{meta["declared"]}</version><repository type="git" url="{meta["url"]}" commit="{meta["commit"]}" />'
                      '</metadata></package>')
            with zipfile.ZipFile(Path(directory) / f'{name}.{version}.nupkg', 'w') as package:
                package.writestr(f'{name}.nuspec', nuspec)

    def test_accepts_the_exact_set_and_detects_changed_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            self.pack(directory)
            manifest = release.prepare(directory, '1.2.3-preview.1+build.4', SHA, '42')
            self.assertEqual([p['name'] for p in manifest['packages']], release.PACKAGES)
            release.verify(directory, manifest, SHA, '1.2.3-preview.1+build.4', '42')
            with self.assertRaisesRegex(release.ReleaseError, 'Inventory is for'):
                release.verify(directory, manifest, 'b' * 40, '1.2.3-preview.1+build.4', '42')
            with self.assertRaisesRegex(release.ReleaseError, 'version'):
                release.verify(directory, manifest, SHA, '1.2.3-preview.2', '42')
            with self.assertRaisesRegex(release.ReleaseError, 'CI run'):
                release.verify(directory, manifest, SHA, '1.2.3-preview.1+build.4', '43')
            with zipfile.ZipFile(Path(directory) / 'Runic.CommandLine.1.2.3-preview.1.nupkg', 'a') as package:
                package.writestr('extra.txt', 'changed')
            with self.assertRaisesRegex(release.ReleaseError, 'differ from the candidate'):
                release.verify(directory, manifest, SHA, '1.2.3-preview.1+build.4', '42')

    def test_rejects_wrong_version_commit_repository_or_set(self):
        cases = [({'commit': 'b' * 40}, 'packed from'), ({'url': 'https://github.com/obsolete/repo'}, 'names repository'),
                 ({'declared': '1.2.3-preview.2'}, 'declares'), ({'names': release.PACKAGES[:3]}, 'exactly')]
        for override, message in cases:
            with self.subTest(message), tempfile.TemporaryDirectory() as directory:
                self.pack(directory, **override)
                with self.assertRaisesRegex(release.ReleaseError, message):
                    release.prepare(directory, '1.2.3-preview.1', SHA, '42')
        with tempfile.TemporaryDirectory() as directory:
            self.pack(directory)
            (Path(directory) / 'stale.txt').write_text('stale')
            with self.assertRaisesRegex(release.ReleaseError, 'exactly'):
                release.prepare(directory, '1.2.3-preview.1', SHA, '42')
            with self.assertRaisesRegex(release.ReleaseError, 'SemVer'):
                release.scan(directory, 'main', SHA)


class ReleaseCheck(unittest.TestCase):
    def fake(self, release_exists=False, tag=None, draft=False, assets=()):
        calls = []

        def run_command(args, **_):
            calls.append(args)
            if args[:3] == ['gh', 'release', 'view']:
                body = {'url': 'u', 'isDraft': draft, 'assets': [{'name': name} for name in assets]}
                return SimpleNamespace(returncode=0 if release_exists else 1, stdout=json.dumps(body))
            if args[:2] == ['gh', 'api']:
                return SimpleNamespace(returncode=0 if tag else 1, stdout=f'{tag}\n')
            return SimpleNamespace(returncode=0, stdout='')
        return run_command, calls

    def test_check_only_reads_and_accepts_only_this_commit(self):
        run_command, calls = self.fake()
        self.assertIn('Would create', release.release_check('1.2.3', SHA, run_command))
        self.assertEqual([args[:3] for args in calls], [['gh', 'release', 'view'], ['gh', 'api', f'repos/{release.REPOSITORY}/commits/v1.2.3']])
        self.assertIn('Would create', release.release_check('1.2.3', SHA, self.fake(tag=SHA)[0]))
        self.assertIn('already exists for', release.release_check('1.2.3', SHA, self.fake(release_exists=True, tag=SHA)[0]))
        for fake, message in [(self.fake(release_exists=True, tag='b' * 40), 'belongs to'), (self.fake(tag='b' * 40), 'belongs to'),
                              (self.fake(release_exists=True, tag=SHA, draft=True), 'draft'), (self.fake(release_exists=True), 'no tag')]:
            with self.assertRaisesRegex(release.ReleaseError, message):
                release.release_check('1.2.3', SHA, fake[0])

    def test_rerun_keeps_a_release_of_this_commit_and_uploads_only_missing_assets(self):
        files = ['p/A.1.2.3.nupkg', 'p/B.1.2.3.nupkg']
        run_command, calls = self.fake()
        self.assertIn('Created', release.create_release('1.2.3', SHA, files, run_command))
        self.assertEqual(calls[-1][:3], ['gh', 'release', 'create'])
        self.assertEqual(calls[-1][calls[-1].index('--target') + 1], SHA)
        run_command, calls = self.fake(release_exists=True, tag=SHA, assets=['A.1.2.3.nupkg'])
        self.assertIn('Completed', release.create_release('1.2.3', SHA, files, run_command))
        self.assertEqual(calls[-1][:5], ['gh', 'release', 'upload', 'v1.2.3', 'p/B.1.2.3.nupkg'])
        run_command, calls = self.fake(release_exists=True, tag=SHA, assets=['A.1.2.3.nupkg', 'B.1.2.3.nupkg'])
        self.assertIn('every asset', release.create_release('1.2.3', SHA, files, run_command))
        self.assertFalse(any(args[:3] in (['gh', 'release', 'create'], ['gh', 'release', 'upload']) for args in calls))
        run_command, calls = self.fake(release_exists=True, tag='b' * 40)
        with self.assertRaisesRegex(release.ReleaseError, 'belongs to'):
            release.create_release('1.2.3', SHA, files, run_command)
        self.assertFalse(any(args[:3] == ['gh', 'release', 'create'] for args in calls))


class PublishDryRun(unittest.TestCase):
    def test_dry_run_never_pushes(self):
        with tempfile.TemporaryDirectory() as directory:
            fake_bin = Path(directory) / 'bin'
            fake_bin.mkdir()
            (fake_bin / 'dotnet').write_text('#!/bin/sh\necho pushed >> "$0.calls"\n')
            (fake_bin / 'dotnet').chmod(0o755)
            script = ('import runpy, sys, urllib.request\n'
                      'urllib.request.urlopen = lambda *a, **k: (_ for _ in ()).throw(__import__("urllib.error").error.HTTPError("u", 404, "", {}, None))\n'
                      f'sys.argv = ["publish-nuget.py", {directory!r}, "https://api.nuget.org/v3/index.json", "--dry-run"]\n'
                      f'runpy.run_path({str(Path(__file__).parent / "publish-nuget.py")!r}, run_name="__main__")\n')
            CandidatePackages().pack(directory)
            result = subprocess.run([sys.executable, '-c', script], capture_output=True, text=True,
                                    env={'PATH': f'{fake_bin}:/usr/bin:/bin'})
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(result.stdout.count('Would publish'), 4)
            self.assertFalse((fake_bin / 'dotnet.calls').exists())


class PublishWorkflow(unittest.TestCase):
    text = (ROOT / '.github/workflows/publish-preview.yml').read_text(encoding='utf-8')
    ci = (ROOT / '.github/workflows/ci.yml').read_text(encoding='utf-8')

    def test_reuses_the_ci_artifact_without_rerunning_ci(self):
        self.assertIn(f'name: {release.ARTIFACT}\n', job(self.ci, 'managed-and-pack'))
        for name in ['candidate', 'publish']:
            body = job(self.text, name)
            self.assertNotRegex(body, r'eng/(test|pack|verify|verify-packages|verify-candidate)\.sh')
            self.assertIn('run-id: ${{', body)
            self.assertIn('artifact-ids: ${{', body)
            self.assertIn('merge-multiple: true', body)
            self.assertNotRegex(body, r'\n\s+name: \$\{\{')
            self.assertIn('github-token: ${{ github.token }}', body)
            self.assertIn('actions: read', body)
        self.assertIn('python3 eng/release/release.py find-ci', job(self.text, 'candidate'))
        self.assertIn("github.ref == 'refs/heads/main'", job(self.text, 'candidate'))

    def test_dry_run_stops_before_oidc_publication_and_release(self):
        self.assertRegex(self.text, r'dry-run:\n(?:        .*\n)*        type: boolean\n        default: false')
        candidate, publish = job(self.text, 'candidate'), job(self.text, 'publish')
        self.assertNotIn('environment:', candidate)
        self.assertNotIn('id-token', candidate)
        self.assertNotIn('contents: write', candidate)
        self.assertNotIn('NuGet/login', candidate)
        self.assertNotIn('gh release create', candidate)
        self.assertNotIn('release.py release ', candidate)
        self.assertIn('publish-nuget.py artifacts/packages https://api.nuget.org/v3/index.json --dry-run', candidate)
        self.assertIn('release.py release-check', candidate)
        self.assertIn('if: ${{ !inputs.dry-run }}', publish)
        self.assertIn('environment: preview', publish)
        self.assertIn('id-token: write', publish)
        self.assertNotIn('--dry-run', publish)
        self.assertNotIn('id-token', self.text.split('\njobs:\n')[0])
        self.assertIn('release.py verify artifacts/packages artifacts/release/packages.json "$VERSION" "$CI_RUN_ID"', publish)
        self.assertLess(publish.index('release.py verify'), publish.index('publish-nuget.py'))
        self.assertLess(publish.index('publish-nuget.py'), publish.index('release.py release "$VERSION"'))
        self.assertIn('overwrite: true', candidate)


if __name__ == '__main__':
    unittest.main()
