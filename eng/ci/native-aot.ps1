# The native-aot job's checks for one RID: the source NativeAOT smoke, then the isolated
# consumer of the candidate packages in artifacts/packages. The glibc, Windows and macOS legs
# run it on the runner; the musl legs run it in Alpine through eng/ci/native-musl.sh.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $RuntimeIdentifier
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
& (Join-Path $root 'tests/native/Runic.CommandLine.AotSmoke/Invoke-AotSmoke.ps1') -Configuration Release -RuntimeIdentifier $RuntimeIdentifier
[xml] $versions = Get-Content (Join-Path $root 'eng/Versions.props')
$version = [string] $versions.Project.PropertyGroup.RunicCommandLineVersion
& (Join-Path $root 'tests/fixtures/command-line/package-consumer/Runic.CommandLine.PackageConsumer/Invoke-PackageConsumer.ps1') `
    -Configuration Release -RuntimeIdentifier $RuntimeIdentifier -PackageVersion $version -PackageDirectory (Join-Path $root 'artifacts/packages')
