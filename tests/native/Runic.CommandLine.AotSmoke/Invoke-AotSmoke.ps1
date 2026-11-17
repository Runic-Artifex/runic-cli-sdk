[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $RuntimeIdentifier = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$project = Join-Path $PSScriptRoot 'Runic.CommandLine.AotSmoke.csproj'
$publishDirectory = Join-Path $PSScriptRoot "artifacts/$Configuration/$RuntimeIdentifier/native"

function Invoke-DotNet {
    param([Parameter(Mandatory, Position = 0, ValueFromRemainingArguments)][string[]] $Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

Invoke-DotNet @('restore', $project)
Invoke-DotNet @('build', $project, '--configuration', $Configuration, '--no-restore')
Invoke-DotNet @('run', '--project', $project, '--configuration', $Configuration, '--no-build')

Invoke-DotNet @(
    'publish', $project,
    '--configuration', $Configuration,
    '--runtime', $RuntimeIdentifier,
    '--self-contained', 'true',
    '--output', $publishDirectory,
    '-p:PublishAot=true'
)

$nativeExecutableName = if ($RuntimeIdentifier.StartsWith('win-', [StringComparison]::OrdinalIgnoreCase)) {
    'Runic.CommandLine.AotSmoke.exe'
} else {
    'Runic.CommandLine.AotSmoke'
}
$nativeExecutable = Join-Path $publishDirectory $nativeExecutableName
if (-not (Test-Path -LiteralPath $nativeExecutable -PathType Leaf)) {
    throw "Native AOT executable was not produced at $nativeExecutable."
}

& $nativeExecutable
if ($LASTEXITCODE -ne 0) {
    throw "Native AOT smoke executable failed with exit code $LASTEXITCODE."
}

# The size budget gates one RID (size-budget.json); the others only report, as the toolchains differ.
$size = (Get-Item -LiteralPath $nativeExecutable).Length
$budget = Get-Content -Raw (Join-Path $PSScriptRoot 'size-budget.json') | ConvertFrom-Json
$gated = $Configuration -eq 'Release' -and $RuntimeIdentifier -eq $budget.runtimeIdentifier
$sizeLine = if ($gated) {
    "NativeAOT smoke on ${RuntimeIdentifier}: $size bytes (limit $($budget.limit), measured $($budget.measured))."
} else {
    "NativeAOT smoke on ${RuntimeIdentifier}: $size bytes (not gated; the budget gates $($budget.runtimeIdentifier) Release)."
}
Write-Host $sizeLine
if ($env:GITHUB_STEP_SUMMARY) {
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $sizeLine
}
if ($gated -and $size -gt [long] $budget.limit) {
    throw "The NativeAOT smoke executable is $size bytes, over its limit of $($budget.limit) bytes in size-budget.json. Find the cause with -p:IlcGenerateMstatFile=true; raise the budget only for an intended change."
}

Write-Host "Native AOT smoke passed: $nativeExecutable"
