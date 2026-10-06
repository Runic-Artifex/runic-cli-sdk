"""Tests for the release SBOM: python3 -m unittest discover -s eng/release"""
import base64
import hashlib
import io
import json
import sys
import tarfile
import tempfile
import unittest
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import release  # noqa: E402
import sbom  # noqa: E402

SHA = 'a' * 40
VERSION = '1.2.3-preview.1'
EPOCH = 1790000000


def nupkg(directory, name, version=VERSION, extra='', files=None):
    nuspec = (f'<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><id>{name}</id>'
              f'<version>{version}</version><license type="expression">MIT</license>'
              f'<repository type="git" url="https://github.com/{release.REPOSITORY}" commit="{SHA}" />{extra}</metadata></package>')
    path = Path(directory) / f'{name}.{version}.nupkg'
    with zipfile.ZipFile(path, 'w') as package:
        package.writestr(f'{name}.nuspec', nuspec)
        for entry, content in (files or {}).items():
            package.writestr(entry, content)
    return path


def release_packages(directory):
    deps = {'runtimeTarget': {'name': 'net10.0'},
            'targets': {'net10.0': {'Runic.CommandLine.Testing/' + VERSION: {'dependencies': {'Bundled.Lib': '2.0.0'}}, 'Bundled.Lib/2.0.0': {}}},
            'libraries': {'Runic.CommandLine.Testing/' + VERSION: {'type': 'project'},
                          'Bundled.Lib/2.0.0': {'type': 'package', 'sha512': 'sha512-' + base64.b64encode(bytes(range(64))).decode()},
                          'Microsoft.NETCore.App/10.0.0': {'type': 'runtimepack'}}}
    for name in release.PACKAGES:
        extra, files = '', None
        if name == 'Runic.CommandLine.Spectre':
            extra = (f'<dependencies><group targetFramework="net10.0"><dependency id="Runic.CommandLine" version="[{VERSION}]" />'
                     '<dependency id="Spectre.Console" version="0.50.0" /></group></dependencies>')
        if name == 'Runic.CommandLine.Testing':
            files = {'lib/net10.0/Runic.CommandLine.Testing.deps.json': json.dumps(deps)}
        nupkg(directory, name, extra=extra, files=files)


class ReleaseSbom(unittest.TestCase):
    def test_describes_each_package_by_hash_with_declared_and_bundled_dependencies(self):
        with tempfile.TemporaryDirectory() as directory:
            release_packages(directory)
            path = release.describe(directory, VERSION + '+build.4', SHA, Path(directory) / 'out', EPOCH)
            self.assertEqual(path.name, f'runic-cli-sdk-{VERSION}.cdx.json')
            bom = json.loads(path.read_text(encoding='utf-8'))
            self.assertEqual((bom['bomFormat'], bom['specVersion'], bom['version']), ('CycloneDX', '1.6', 1))
            self.assertEqual(bom['metadata']['timestamp'], '2026-09-21T14:13:20Z')
            self.assertEqual(bom['metadata']['component']['purl'], f'pkg:github/runic-artifex/runic-cli-sdk@v{VERSION}')
            components = {item['bom-ref']: item for item in bom['components']}
            edges = {item['ref']: item['dependsOn'] for item in bom['dependencies']}
            refs = {name: f'pkg:nuget/{name}@{VERSION}' for name in release.PACKAGES}
            self.assertEqual(edges['release'], sorted(refs.values()))
            for name, ref in refs.items():
                digest = hashlib.sha256((Path(directory) / f'{name}.{VERSION}.nupkg').read_bytes()).hexdigest()
                self.assertEqual(components[ref]['hashes'], [{'alg': 'SHA-256', 'content': digest}])
                self.assertEqual(components[ref]['licenses'], [{'expression': 'MIT'}])
                self.assertIn({'name': 'runic:source-commit', 'value': SHA}, components[ref]['properties'])
            # An exact pin on another package of the release links to it; other ranges stay requested ranges.
            self.assertEqual(edges[refs['Runic.CommandLine.Spectre']], [refs['Runic.CommandLine'], 'pkg:nuget/Spectre.Console#0.50.0'])
            self.assertEqual(components['pkg:nuget/Spectre.Console#0.50.0']['properties'], [{'name': 'runic:requested-range', 'value': '0.50.0'}])
            self.assertEqual(edges[refs['Runic.CommandLine.Testing']], ['pkg:nuget/Bundled.Lib@2.0.0'])
            self.assertEqual(components['pkg:nuget/Bundled.Lib@2.0.0']['hashes'], [{'alg': 'SHA-512', 'content': bytes(range(64)).hex()}])
            self.assertFalse(any('Microsoft.NETCore.App' in ref for ref in components))
            known = set(components) | {'release'}
            self.assertTrue(all(ref in known for item in bom['dependencies'] for ref in [item['ref'], *item['dependsOn']]))

    def test_is_deterministic_and_describes_only_the_release_set(self):
        with tempfile.TemporaryDirectory() as directory:
            packages = Path(directory) / 'packages'
            packages.mkdir()
            release_packages(packages)
            first = release.describe(packages, VERSION, SHA, Path(directory) / 'first', EPOCH)
            second = release.describe(packages, VERSION, SHA, Path(directory) / 'second', EPOCH)
            self.assertEqual(first.read_bytes(), second.read_bytes())
            (packages / 'stale.txt').write_text('stale')
            with self.assertRaisesRegex(release.ReleaseError, 'exactly'):
                release.describe(packages, VERSION, SHA, Path(directory) / 'third', EPOCH)
            with self.assertRaisesRegex(ValueError, 'is version'):
                sbom.build(release.REPOSITORY, '9.9.9', SHA, EPOCH, [nupkg(directory, 'Other')])

    def test_npm_packages_and_vs_code_extensions(self):
        with tempfile.TemporaryDirectory() as directory:
            npm = Path(directory) / 'fixture.tgz'
            with tarfile.open(npm, 'w:gz') as archive:
                data = json.dumps({'name': '@runic-artifex/fixture', 'version': VERSION, 'license': 'MIT',
                                   'peerDependencies': {'svelte': '>=5 <6'}}).encode()
                info = tarfile.TarInfo('package/package.json')
                info.size = len(data)
                archive.addfile(info, io.BytesIO(data))
            vsix = Path(directory) / 'fixture.vsix'
            with zipfile.ZipFile(vsix, 'w') as archive:
                archive.writestr('extension.vsixmanifest', '<PackageManifest xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011">'
                                 '<Metadata><Identity Id="fixture" Version="0.0.1" Publisher="runic-artifex" /></Metadata></PackageManifest>')
                archive.writestr('extension/package.json', json.dumps({'dependencies': {'vscode-languageclient': '10.1.2'}}))
            bom = sbom.build(release.REPOSITORY, VERSION, SHA, EPOCH, [npm, vsix])
            edges = {item['ref']: item['dependsOn'] for item in bom['dependencies']}
            npm_ref = f'pkg:npm/%40runic-artifex/fixture@{VERSION}'
            self.assertEqual(edges['release'], [npm_ref, 'vsix:runic-artifex/fixture@0.0.1'])
            self.assertEqual(edges[npm_ref], ['pkg:npm/svelte#>=5 <6'])
            self.assertEqual(edges['vsix:runic-artifex/fixture@0.0.1'], ['pkg:npm/vscode-languageclient@10.1.2'])


if __name__ == '__main__':
    unittest.main()
