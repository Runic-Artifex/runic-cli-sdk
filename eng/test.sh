#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
solution=Runic.CommandLine.slnx
dotnet restore "$solution"
dotnet build "$solution" -c Release --no-restore -p:RunicCommandLineBuildMode=Verification
# TUnit test applications on Microsoft.Testing.Platform. Each writes a TRX report and Cobertura coverage to
# artifacts/test-results/<suite>, which CI uploads; see CONTRIBUTING.md.
for project in \
  tests/dotnet/Runic.CommandLine.Contracts.Tests/Runic.CommandLine.Contracts.Tests.csproj \
  tests/dotnet/Runic.CommandLine.Hosting.Tests/Runic.CommandLine.Hosting.Tests.csproj \
  tests/dotnet/Runic.CommandLine.Processes.Tests/Runic.CommandLine.Processes.Tests.csproj \
  tests/dotnet/Runic.CommandLine.Tests/Runic.CommandLine.Tests.csproj; do
  suite="$(basename "$project" .csproj)"
  results="$root/artifacts/test-results/$suite"
  rm -rf "$results"
  dotnet run --project "$project" -c Release --no-build -- \
    --results-directory "$results" --report-trx --report-trx-filename "$suite.trx" \
    --coverage --coverage-output-format cobertura --coverage-output "$suite.cobertura.xml"
done
# The example suites show Runic.CommandLine.Testing without a test framework.
for project in \
  examples/command-line/Tests/HelloCli.ExampleTests.csproj \
  examples/command-line/wpf/Tests/ReportApp.Tests.csproj; do
  dotnet run --project "$project" -c Release --no-build
done
dotnet run --project examples/command-line/ProcessInput/ProcessInput.csproj -c Release --no-build -- stone and gold
python3 examples/command-line/localized/verify.py --configuration Release
