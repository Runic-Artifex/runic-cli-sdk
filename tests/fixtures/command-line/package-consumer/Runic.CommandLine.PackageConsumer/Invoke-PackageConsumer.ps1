[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $RuntimeIdentifier = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier,

    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$')]
    [string] $PackageVersion,

    [Parameter(Mandatory)]
    [string] $PackageDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# NuGet ignores build metadata when identifying packages and naming archives.
$PackageVersion = $PackageVersion.Split('+')[0]
$feed = (Resolve-Path -LiteralPath $PackageDirectory).Path
foreach ($package in @('Runic.CommandLine', 'Runic.CommandLine.Processes', 'Runic.CommandLine.Spectre', 'Runic.CommandLine.Testing')) {
    $candidate = Join-Path $feed "$package.$PackageVersion.nupkg"
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Candidate package is missing: $candidate. This check never packs or substitutes a published Runic package."
    }
}
$runRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'runic-cli-consumer-' + [Guid]::NewGuid().ToString('N'))
$consumerDirectory = Join-Path $runRoot 'consumer'
$packageCache = Join-Path $runRoot 'packages'
$publishDirectory = Join-Path $runRoot "publish/$RuntimeIdentifier"
$consumerProject = Join-Path $consumerDirectory 'Consumer.csproj'

function Invoke-DotNet {
    param([Parameter(Mandatory, Position = 0, ValueFromRemainingArguments)][string[]] $Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

$previousPackageCache = $env:NUGET_PACKAGES
try {
New-Item -ItemType Directory -Path $consumerDirectory, $packageCache, $publishDirectory | Out-Null

$projectTemplate = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Consumer.csproj.in') -Raw
$projectTemplate.Replace('@PACKAGE_VERSION@', $PackageVersion) |
    Set-Content -LiteralPath $consumerProject -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Program.cs.in') -Destination (Join-Path $consumerDirectory 'Program.cs')

$escapedFeed = [System.Security.SecurityElement]::Escape($feed)
$nugetConfiguration = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="owned-command-line-feed" value="$escapedFeed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="owned-command-line-feed">
      <package pattern="Runic.CommandLine" />
      <package pattern="Runic.CommandLine.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="Microsoft.*" />
      <package pattern="runtime.*" />
      <package pattern="System.*" />
      <package pattern="Spectre.*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@
$nugetConfiguration | Set-Content -LiteralPath (Join-Path $consumerDirectory 'NuGet.Config') -Encoding utf8NoBOM

    $env:NUGET_PACKAGES = $packageCache

    Invoke-DotNet @('restore', $consumerProject)
    Invoke-DotNet @('build', $consumerProject, '--configuration', $Configuration, '--no-restore')
    Invoke-DotNet @('run', '--project', $consumerProject, '--configuration', $Configuration, '--no-build')

    Invoke-DotNet @(
        'publish', $consumerProject,
        '--configuration', $Configuration,
        '--runtime', $RuntimeIdentifier,
        '--self-contained', 'true',
        '--output', $publishDirectory,
        '-p:PublishAot=true',
        '-p:PublishTrimmed=true',
        '-p:TrimMode=full',
        '-p:IlcTreatWarningsAsErrors=true'
    )

    $nativeExecutableName = if ($RuntimeIdentifier.StartsWith('win-', [StringComparison]::OrdinalIgnoreCase)) {
        'Runic.CommandLine.PackageConsumer.exe'
    } else {
        'Runic.CommandLine.PackageConsumer'
    }
    $nativeExecutable = Join-Path $publishDirectory $nativeExecutableName
    if (-not (Test-Path -LiteralPath $nativeExecutable -PathType Leaf)) {
        throw "Native AOT package consumer was not produced at $nativeExecutable."
    }

    & $nativeExecutable
    if ($LASTEXITCODE -ne 0) {
        throw "Native AOT package consumer failed with exit code $LASTEXITCODE."
    }

    # Compile the maintained tutorial sources outside the checkout. Only the
    # examples reference each other; every SDK dependency comes from the feed.
    $exampleSource = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../../../examples/command-line'))
    $exampleDirectory = Join-Path $runRoot 'examples'
    New-Item -ItemType Directory -Path $exampleDirectory | Out-Null
    Copy-Item (Join-Path $consumerDirectory 'NuGet.Config') $exampleDirectory
    # Keep the documented tutorial snippet identical to the maintained source.
    $readme = Get-Content -LiteralPath (Join-Path $exampleSource '../../README.md') -Raw
    $snippet = [regex]::Match($readme, '(?s)```csharp\r?\n(.*?)\r?\n```').Groups[1].Value
    $helloSource = Get-Content -LiteralPath (Join-Path $exampleSource 'hello-world/Program.cs') -Raw
    if ($snippet.Replace("`r`n", "`n").Trim() -ne $helloSource.Replace("`r`n", "`n").Trim()) {
        throw 'README quick-start source differs from the maintained hello-world example.'
    }
    foreach ($example in @('hello-world', 'application')) {
        $destination = Join-Path $exampleDirectory $example
        if ($example -eq 'hello-world') {
            # Exercise the documented install flow, not repository build imports.
            Invoke-DotNet @('new', 'console', '--framework', 'net10.0', '--name', 'HelloCli', '--output', $destination)
            Invoke-DotNet @('add', (Join-Path $destination 'HelloCli.csproj'), 'package', 'Runic.CommandLine', '--version', $PackageVersion, '--source', $feed)
        } else {
            New-Item -ItemType Directory -Path $destination | Out-Null
            @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Runic.CommandLine" Version="$PackageVersion" />
    <PackageReference Include="Runic.CommandLine.Spectre" Version="$PackageVersion" />
  </ItemGroup>
</Project>
"@ | Set-Content (Join-Path $destination 'Example.csproj') -Encoding utf8NoBOM
        }
        $source = if ($example -eq 'hello-world') { Join-Path $exampleSource 'hello-world' } else { $exampleSource }
        Copy-Item (Join-Path $source '*.cs') $destination
        Invoke-DotNet @('build', $destination, '-c', $Configuration, '-p:TreatWarningsAsErrors=true')
    }
    $helloProject = Join-Path $exampleDirectory 'hello-world'
    $applicationProject = Join-Path $exampleDirectory 'application'
    # These are the human/JSON/help commands from the package-only quick-start.
    $human = & dotnet run --project $helloProject -c $Configuration --no-build -- Ada --count 2
    if ($LASTEXITCODE -ne 0 -or ($human -join "`n") -ne "Hello, Ada!`nHello, Ada!") {
        throw 'Package-only quick-start human output did not match.'
    }
    $json = & dotnet run --project $helloProject -c $Configuration --no-build -- Ada --count 2 --output=json
    if ($LASTEXITCODE -ne 0) { throw 'Package-only quick-start JSON execution failed.' }
    $envelope = ($json -join "`n") | ConvertFrom-Json
    if ($envelope.protocol -ne 'runic.commandline/1' -or -not $envelope.success -or
        $envelope.command -ne 'greet' -or $envelope.payloadType -ne 'runic.text/1' -or
        $envelope.payload -ne "Hello, Ada!`nHello, Ada!" -or $envelope.exitCode -ne 0) {
        throw 'Package-only quick-start JSON envelope did not match.'
    }
    $help = & dotnet run --project $helloProject -c $Configuration --no-build -- --help
    if ($LASTEXITCODE -ne 0 -or ($help -join "`n") -notmatch 'greet') {
        throw 'Package-only quick-start help did not describe greet.'
    }
    Invoke-DotNet @('run', '--project', $applicationProject, '-c', $Configuration, '--no-build', '--', '--hosted', 'application', 'info')
    Invoke-DotNet @('run', '--project', $applicationProject, '-c', $Configuration, '--no-build', '--', '--hosted', 'help', 'transform')
    Invoke-DotNet @('run', '--project', $applicationProject, '-c', $Configuration, '--no-build', '--', '--hosted')

    Write-Host "Package consumer passed from isolated feed: $feed"
    Write-Host "Native AOT package consumer passed: $nativeExecutable"
}
finally {
    $env:NUGET_PACKAGES = $previousPackageCache
    if (Test-Path -LiteralPath $runRoot) {
        Remove-Item -LiteralPath $runRoot -Recurse -Force
    }
}
