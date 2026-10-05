#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
solution=Runic.CommandLine.slnx
dotnet restore "$solution"
dotnet build "$solution" -c Release --no-restore -p:RunicCommandLineBuildMode=Verification
for project in \
  tests/dotnet/Runic.CommandLine.Contracts.Tests/Runic.CommandLine.Contracts.Tests.csproj \
  tests/dotnet/Runic.CommandLine.Hosting.Tests/Runic.CommandLine.Hosting.Tests.csproj \
  tests/dotnet/Runic.CommandLine.Processes.Tests/Runic.CommandLine.Processes.Tests.csproj \
  tests/dotnet/Runic.CommandLine.Tests/Runic.CommandLine.Tests.csproj \
  examples/command-line/Tests/HelloCli.ExampleTests.csproj; do
  dotnet run --project "$project" -c Release --no-build
done
