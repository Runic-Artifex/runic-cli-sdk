# Contributing

Build and verify through the pinned environment described in [README.md](README.md#develop):
`direnv exec . ./eng/test.sh` runs the managed suites and the examples, and
`direnv exec . ./eng/verify.sh` adds the NativeAOT smoke and the candidate package checks.

## Tests

The managed suites under `tests/dotnet` (Contracts, Hosting, Processes and the main `Runic.CommandLine.Tests`) use
[TUnit](https://tunit.dev) on Microsoft.Testing.Platform (MTP). They are executables run with
`dotnet run --project <suite> --configuration Release`. A new suite of independent cases follows the same pattern:

- **Project.** Keep `OutputType` `Exe`. Reference `TUnit.Core`, `TUnit.Engine`, `Microsoft.Testing.Extensions.TrxReport`
  and `Microsoft.Testing.Extensions.CodeCoverage`, whose versions are pinned in `Directory.Packages.props`. The `TUnit`
  metapackage is not used: it adds TUnit's assertion library and the MTP telemetry extension, and the suites keep their
  own assertion helpers. Set `TUnitImplicitUsings` to `false` and import `TUnit.Core`, as the repository does not use
  implicit usings. TUnit generates the entry point, so a suite has no `Program.cs`. The Processes and main suites start
  their own executable as a child process (the process fixture, and the signal and prompt terminal cases); they set
  `GenerateTestingPlatformEntryPoint` to `false`, and their `Program.cs` runs the child before it builds the test
  application the way the generated entry point does.
- **Lock file.** Set `RestorePackagesWithLockFile` to `true` and `RestoreLockedMode` to `true` when `CI` is `true`, and
  commit `packages.lock.json`. CI then fails with NU1004 if the lock file is stale, for example after a package version
  or a referenced project's dependency changes. Update it with `dotnet restore <suite> --force-evaluate` in the
  development shell.
- **Cases.** Each case is a non-static `[Test]` method in a non-static class, with a `[DisplayName]` that keeps its
  readable name in console output, TRX and the IDE, such as `dispatcher/typed-handler-receives-bound-options-and-context`.
  A case fails when it throws, so the suite-local `Assert`, `AssertEx` and `TestAssert` helpers keep working. The corpus
  cases of the main suite take one row each from a `[MethodDataSource]`; `[CorpusCaseName]` names them
  `<prefix><row id>`, for example `grammar/grammar.option.long-flag`, and records the row id as the `CaseId` property.
- **Running and filtering.** Arguments after `--` go to the test application: `--list-tests` lists the cases, and
  `--treenode-filter "/<assembly>/<namespace>/<class>/<method>"` selects them, with `*` as a wildcard, for example
  `--treenode-filter "/*/*/DispatcherTests/TypedHandlerContext"`. Select one corpus row with
  `--treenode-filter "/*/*/*/*[CaseId=grammar.option.long-flag]"`. `--filter` is not supported. The exit code is 0 when
  all selected cases pass, 2 when one fails and 8 when none ran.
- **`dotnet test`.** `global.json` opts in to the Microsoft.Testing.Platform mode of `dotnet test` (`"test": { "runner":
  "Microsoft.Testing.Platform" }`), so `dotnet test -c Release` in the repository root builds the solution and runs the
  four managed suites; the probe programs and the example suites are not test applications and are skipped. Select one
  suite with `--project <suite>`. In this mode the test application options follow directly, without `--`, for
  example `dotnet test --project tests/dotnet/Runic.CommandLine.Tests -c Release --treenode-filter
  "/*/*/*/*[CaseId=grammar.option.long-flag]"`. Without `--results-directory` the reports go to `TestResults` in the
  repository root, which Git ignores. `eng/test.sh` and CI keep `dotnet run --project`, which also runs the examples.
- **Reports.** `eng/test.sh` passes `--report-trx --coverage --coverage-output-format cobertura` and writes `<suite>.trx`
  and `<suite>.cobertura.xml` to `artifacts/test-results/<suite>`, which CI uploads as `command-line-test-results`, also
  when a case failed. CI also runs the managed suites on Windows and macOS with `eng/test.sh --managed-only`, which skips
  the examples, and uploads `command-line-test-results-Windows` and `command-line-test-results-macOS`. Without
  `--results-directory`, TUnit writes its HTML and JSON reports to `bin/<configuration>/net10.0/TestResults`.
- **Parallelism.** TUnit runs cases in parallel, and in the directory of the test executable. Before you allow
  parallelism for a suite, make sure no case shares mutable static state, the current directory, environment variables,
  ports or files with another case. The Contracts, Hosting and main suites limit themselves to one case per processor
  with `ParallelLimiter<ProcessorCountParallelLimit>` in `AssemblyInfo.cs`. A case or class that cannot run alongside
  others is `[NotInParallel]`: the Hosting case that sets `HOST_VALUE`, the main suite's signal, prompt terminal and
  native shell completion cases, which start processes with fixed deadlines, and its two cases that count calls in
  static fields. The prompt terminal cases answer a prompt on a real pseudo-terminal through util-linux `script` on
  Linux, and return early where `script` is missing.
  Every Processes case starts child processes and races them against timeouts, so that suite runs `[NotInParallel]` as
  a whole. Pass `--maximum-parallel-tests 1` to run cases one at a time while debugging.
- **IDE.** Visual Studio, Rider and the C# Dev Kit discover MTP test projects when their Microsoft.Testing.Platform
  support is enabled. See [running TUnit tests](https://tunit.dev/docs/getting-started/running-your-tests/).

### Probe programs

These executables keep their own `Main`, because the program itself is what they check. None has a case list or an
execution loop of its own.

- `tests/native/Runic.CommandLine.AotSmoke` is one parser-to-handler-to-JSON, Spectre and Hosting scenario that
  `Invoke-AotSmoke.ps1` runs managed and then as a host-runtime NativeAOT publication on every supported RID in CI.
  It proves that a trimmed, AOT-compiled application of the owned projects works; adding the test framework would put
  TUnit's own AOT closure into the program under test. Its numbered exit codes locate a failing step. Move it to a TUnit
  NativeAOT application when it grows independent cases.
- `tests/fixtures/command-line/package-consumer` is a template consumer that `Invoke-PackageConsumer.ps1` restores
  from the candidate packages in a fresh package cache, then runs managed and as NativeAOT. It proves the published
  package surface from the outside, including its own process entry point and exit codes, so it stays a plain program.
- `tests/fixtures/command-line/CommandLine.Kernel` is a sample command-line application that composes the kernel by
  hand; its `Main` is the application, not a test runner. The solution build compiles it.
- The example suites `examples/command-line/Tests` and `examples/command-line/wpf/Tests` show application authors
  `CommandAppTester` from `Runic.CommandLine.Testing` without a test framework. `eng/test.sh` runs them as plain
  executables.
