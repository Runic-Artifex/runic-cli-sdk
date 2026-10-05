#!/usr/bin/env python3
"""Publish only missing NuGet packages, rejecting same-version content drift."""
import hashlib
import re
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import zipfile
from pathlib import Path


def archive(path):
    with zipfile.ZipFile(path) as package:
        names = [entry.filename for entry in package.infolist() if not entry.is_dir()]
        if len(names) != len(set(names)):
            raise RuntimeError(f'Package archive contains duplicate entries: {path}')
        result = {}
        for entry in package.infolist():
            if entry.is_dir() or entry.filename == '.signature.p7s':
                continue
            result[entry.filename] = hashlib.sha256(package.read(entry)).hexdigest()
    return result


def download(url, destination):
    try:
        with urllib.request.urlopen(url, timeout=30) as response:
            if response.status != 200:
                raise RuntimeError(f'Unexpected registry response: {response.status}')
            destination.write_bytes(response.read())
            return True
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return False
        raise RuntimeError(f'Registry lookup failed: HTTP {error.code}') from error


def identity(path):
    match = re.match(r'^(?P<name>.+)\.(?P<version>\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?)\.nupkg$', path.name)
    if not match:
        raise RuntimeError(f'Cannot derive package identity from {path.name}')
    return match.group('name'), match.group('version')


def equal(local, registry):
    return archive(local) == archive(registry)


def main():
    if len(sys.argv) != 4:
        raise RuntimeError('Usage: publish-nuget.py <package-directory> <source> <api-key>')
    directory = Path(sys.argv[1])
    source = sys.argv[2]
    api_key = sys.argv[3]
    for package in sorted(directory.glob('*.nupkg')):
        name, version = identity(package)
        package_id = name.lower()
        url = f'https://api.nuget.org/v3-flatcontainer/{package_id}/{version}/{package_id}.{version}.nupkg'
        with tempfile.TemporaryDirectory(prefix='runic-cli-registry-') as temporary:
            registered = Path(temporary) / 'registry.nupkg'
            if download(url, registered):
                if not equal(package, registered):
                    raise RuntimeError(f'Registry content differs for {name} {version}; choose a new version.')
                print(f'{name} {version} already matches NuGet.org.')
                continue
            result = subprocess.run([
                'dotnet', 'nuget', 'push', str(package), '--source', source, '--api-key', api_key
            ], check=False)
            if result.returncode == 0:
                print(f'Published {name} {version}.')
                continue
            for _ in range(12):
                if download(url, registered):
                    if not equal(package, registered):
                        raise RuntimeError(f'Registry content differs for {name} {version}; choose a new version.')
                    print(f'{name} {version} was published concurrently and matches.')
                    break
                time.sleep(2)
            else:
                raise RuntimeError(f'Could not publish or verify {name} {version}.')


if __name__ == '__main__':
    main()
