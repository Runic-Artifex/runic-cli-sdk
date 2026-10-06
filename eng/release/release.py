#!/usr/bin/env python3
"""Reuse the package artifact of the successful CI push run for a release.

find-ci        find the green ci.yml push run on main for GITHUB_SHA and its artifact
prepare        check the downloaded packages against the version and commit; write an inventory
verify         check packages against an inventory written by prepare
release-check  check that tag and GitHub release do not belong to another commit (read-only)
"""
import hashlib
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
import zipfile
import xml.etree.ElementTree as ElementTree
from pathlib import Path

REPOSITORY = 'Runic-Artifex/runic-cli-sdk'
CI_WORKFLOW = '.github/workflows/ci.yml'
ARTIFACT = 'command-line-packages'
PACKAGES = ['Runic.CommandLine', 'Runic.CommandLine.Processes', 'Runic.CommandLine.Spectre', 'Runic.CommandLine.Testing']
SCHEMA = 'runic.command-line.preview/1'


class ReleaseError(Exception):
    pass


def select_ci_run(runs, repository, sha):
    candidates = [run for run in runs if run.get('head_sha') == sha and run.get('event') == 'push'
                  and run.get('head_branch') == 'main' and (run.get('path') or '').split('@')[0] == CI_WORKFLOW
                  and (run.get('repository') or {}).get('full_name') == repository
                  and (run.get('head_repository') or {}).get('full_name') == repository]
    candidates.sort(key=lambda run: run['id'], reverse=True)
    for run in candidates:
        if run.get('status') == 'completed' and run.get('conclusion') == 'success':
            return run
    if not candidates:
        raise ReleaseError(f'No push run of {CI_WORKFLOW} on main exists for {sha}. Publish only a commit on main whose CI succeeded.')
    latest = candidates[0]
    if latest.get('status') != 'completed':
        raise ReleaseError(f"CI for {sha} is still {latest.get('status')} ({latest.get('html_url')}). Dispatch again after it succeeds.")
    raise ReleaseError(f"CI for {sha} concluded {latest.get('conclusion')} ({latest.get('html_url')}). Rerun its failed jobs; publish needs a successful run.")


def select_artifact(artifacts, run):
    found = [artifact for artifact in artifacts if artifact.get('name') == ARTIFACT]
    if len(found) != 1:
        raise ReleaseError(f"CI run {run.get('html_url')} has no single {ARTIFACT} artifact.")
    artifact = found[0]
    if artifact.get('expired'):
        raise ReleaseError(f"The {ARTIFACT} artifact of {run.get('html_url')} has expired. Rerun all jobs of that CI run to regenerate it, or prepare a new version.")
    origin = artifact.get('workflow_run') or {}
    if origin.get('id') != run['id'] or origin.get('head_sha') != run['head_sha']:
        raise ReleaseError('The artifact belongs to a different run or commit.')
    return artifact


def github_api(repository, path, token, opener=urllib.request.urlopen):
    request = urllib.request.Request(f'https://api.github.com/repos/{repository}/{path}', headers={
        'Accept': 'application/vnd.github+json', 'Authorization': f'Bearer {token}', 'X-GitHub-Api-Version': '2022-11-28'})
    try:
        with opener(request, timeout=30) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        raise ReleaseError(f"GitHub API {path.split('?')[0]} failed: {error.code}") from None


def find_ci_packages(repository, sha, token, api=github_api):
    if not re.fullmatch(r'[0-9a-f]{40}', sha or ''):
        raise ReleaseError('Expected a full commit SHA')
    query = urllib.parse.urlencode({'head_sha': sha, 'event': 'push', 'branch': 'main', 'per_page': 100})
    run = select_ci_run(api(repository, f'actions/workflows/ci.yml/runs?{query}', token)['workflow_runs'], repository, sha)
    artifact = select_artifact(api(repository, f"actions/runs/{run['id']}/artifacts?name={ARTIFACT}", token)['artifacts'], run)
    return {'run-id': str(run['id']), 'run-url': run['html_url'], 'artifact': artifact['name']}


def nuspec(path):
    with zipfile.ZipFile(path) as package:
        names = [name for name in package.namelist() if name.endswith('.nuspec') and '/' not in name]
        if len(names) != 1:
            raise ReleaseError(f'{path.name} must contain one root .nuspec')
        root = ElementTree.fromstring(package.read(names[0]))
    metadata = next((child for child in root if child.tag.endswith('metadata')), None)
    field = lambda name: next((child for child in metadata if child.tag.endswith(name)), None) if metadata is not None else None
    repository = field('repository')
    return {'id': getattr(field('id'), 'text', None), 'version': getattr(field('version'), 'text', None),
            'repository': repository.get('url') if repository is not None else None,
            'commit': repository.get('commit') if repository is not None else None}


def package_version(version):
    if not re.fullmatch(r'(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?', version):
        raise ReleaseError(f'Version must be SemVer: {version}')
    return version.split('+', 1)[0]


def scan(directory, version, commit):
    directory = Path(directory)
    expected = package_version(version)
    entries = sorted(path.name for path in directory.iterdir())
    wanted = sorted(f'{name}.{expected}.nupkg' for name in PACKAGES)
    if entries != wanted:
        raise ReleaseError(f'Artifact must contain exactly {wanted}; found {entries}')
    packages = []
    for name in PACKAGES:
        path = directory / f'{name}.{expected}.nupkg'
        if not path.is_file() or path.is_symlink():
            raise ReleaseError(f'{path.name} must be a regular file')
        meta = nuspec(path)
        if meta['id'] != name or meta['version'] != expected:
            raise ReleaseError(f"{path.name} declares {meta['id']} {meta['version']}")
        if (meta['repository'] or '').rstrip('/').removesuffix('.git') != f'https://github.com/{REPOSITORY}':
            raise ReleaseError(f"{path.name} names repository {meta['repository']}")
        if meta['commit'] != commit:
            raise ReleaseError(f"{path.name} was packed from {meta['commit']}, not {commit}")
        packages.append({'name': name, 'file': path.name, 'sha256': hashlib.sha256(path.read_bytes()).hexdigest()})
    return packages


def prepare(directory, version, commit, ci_run_id):
    if not re.fullmatch(r'[1-9]\d*', ci_run_id or ''):
        raise ReleaseError('Expected a CI run id')
    return {'schema': SCHEMA, 'repository': REPOSITORY, 'version': version, 'source': commit,
            'ciRunId': ci_run_id, 'packages': scan(directory, version, commit)}


def verify(directory, manifest, commit):
    if manifest.get('schema') != SCHEMA or manifest.get('repository') != REPOSITORY:
        raise ReleaseError('Not a Command Line release inventory')
    if manifest.get('source') != commit:
        raise ReleaseError(f"Inventory is for {manifest.get('source')}, not {commit}")
    if scan(directory, manifest['version'], commit) != manifest['packages']:
        raise ReleaseError('Packages differ from the candidate inventory')


def release_check(version, commit, run=subprocess.run):
    tag = f'v{version}'
    release = run(['gh', 'release', 'view', tag, '--repo', REPOSITORY, '--json', 'isDraft,url'], capture_output=True, text=True)
    if release.returncode == 0:
        raise ReleaseError(f'GitHub release {tag} already exists ({json.loads(release.stdout)["url"]}); publish a new version.')
    tagged = run(['gh', 'api', f'repos/{REPOSITORY}/commits/{tag}', '--jq', '.sha'], capture_output=True, text=True)
    if tagged.returncode == 0 and tagged.stdout.strip() != commit:
        raise ReleaseError(f'Tag {tag} belongs to {tagged.stdout.strip()}, not {commit}.')
    return f'Would create the prerelease {tag} at {commit}.'


def head():
    return subprocess.run(['git', 'rev-parse', 'HEAD'], check=True, capture_output=True, text=True).stdout.strip()


def main(argv):
    command, *args = argv or ['']
    if command == 'find-ci':
        found = find_ci_packages(os.environ['GITHUB_REPOSITORY'], os.environ['GITHUB_SHA'], os.environ['GH_TOKEN'])
        with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as output:
            output.write(f"run-id={found['run-id']}\nartifact={found['artifact']}\n")
        print(f"Reusing {found['artifact']} from {found['run-url']}")
    elif command == 'prepare' and len(args) == 4:
        directory, version, ci_run_id, output = args
        manifest = prepare(directory, version, head(), ci_run_id)
        Path(output).write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
        print(f"Verified {len(manifest['packages'])} packages for {version} at {manifest['source']}")
    elif command == 'verify' and len(args) == 2:
        verify(args[0], json.loads(Path(args[1]).read_text(encoding='utf-8')), head())
    elif command == 'release-check' and len(args) == 1:
        print(release_check(args[0], head()))
    else:
        raise ReleaseError('Use find-ci, prepare <packages> <version> <ci-run-id> <inventory>, verify <packages> <inventory>, or release-check <version>')


if __name__ == '__main__':
    try:
        main(sys.argv[1:])
    except ReleaseError as error:
        print(f'::error title=Release check failed::{error}')
        sys.exit(1)
