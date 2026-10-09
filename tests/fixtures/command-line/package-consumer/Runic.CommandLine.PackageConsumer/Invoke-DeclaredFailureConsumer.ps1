[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$')]
    [string] $PackageVersion,
    [Parameter(Mandatory)]
    [string] $PackageDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PackageVersion -eq '0.6.0-preview.2') {
    throw 'Use a distinct candidate version so the published baseline remains independently resolvable.'
}
$feed = (Resolve-Path -LiteralPath $PackageDirectory).Path
foreach ($package in @('Runic.CommandLine', 'Runic.CommandLine.Processes', 'Runic.CommandLine.Spectre')) {
    if (-not (Test-Path -LiteralPath (Join-Path $feed "$package.$PackageVersion.nupkg") -PathType Leaf)) {
        throw "Candidate package is missing: $package.$PackageVersion.nupkg"
    }
}
$runRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('runic-cli-failure-consumer-' + [Guid]::NewGuid().ToString('N'))
function Invoke-DotNet {
    param([Parameter(Mandatory, Position = 0, ValueFromRemainingArguments)][string[]] $Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}
try {
    $candidate = Join-Path $runRoot 'candidate'
    $baseline = Join-Path $runRoot 'baseline'
    New-Item -ItemType Directory -Path $candidate, $baseline | Out-Null
    (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Consumer.csproj.in') -Raw).Replace('@PACKAGE_VERSION@', $PackageVersion) |
        Set-Content -LiteralPath (Join-Path $candidate 'Consumer.csproj') -Encoding utf8NoBOM
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Program.cs.in') -Destination (Join-Path $candidate 'Program.cs')
    $escapedFeed = [System.Security.SecurityElement]::Escape($feed)
    @"
<configuration>
  <packageSources><clear /><add key="candidate" value="$escapedFeed" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <packageSourceMapping>
    <packageSource key="candidate"><package pattern="Runic.CommandLine" /><package pattern="Runic.CommandLine.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="Microsoft.*" /><package pattern="runtime.*" /><package pattern="System.*" /><package pattern="Spectre.*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath (Join-Path $candidate 'NuGet.Config') -Encoding utf8NoBOM
    $project = Join-Path $candidate 'Consumer.csproj'
    Invoke-DotNet @('build', $project, '-c', 'Release', '--disable-build-servers', '-m:1', '-p:UseSharedCompilation=false')
    $frame = & dotnet run --project $project -c Release --no-build -- --emit-recovery-frame
    if ($LASTEXITCODE -ne 0) { throw "Candidate consumer failed with exit code $LASTEXITCODE." }
    $framePath = Join-Path $runRoot 'recovery.json'
    (($frame -join "`n") + "`n") | Set-Content -LiteralPath $framePath -Encoding utf8NoBOM -NoNewline

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'BaselineReader.csproj.in') -Destination (Join-Path $baseline 'BaselineReader.csproj')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'BaselineReader.cs.in') -Destination (Join-Path $baseline 'Program.cs')
    '<configuration><packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>' |
        Set-Content -LiteralPath (Join-Path $baseline 'NuGet.Config') -Encoding utf8NoBOM
    $baselineProject = Join-Path $baseline 'BaselineReader.csproj'
    Invoke-DotNet @('build', $baselineProject, '-c', 'Release', '--disable-build-servers', '-m:1', '-p:UseSharedCompilation=false')
    Invoke-DotNet @('run', '--project', $baselineProject, '-c', 'Release', '--no-build', '--', $framePath)
    Write-Host "Declared failure package consumer passed from candidate feed: $feed"
}
finally {
    if (Test-Path -LiteralPath $runRoot) { Remove-Item -LiteralPath $runRoot -Recurse -Force }
}
