# Command Line package consumer

Run `pwsh -NoProfile -File Invoke-PackageConsumer.ps1 -PackageVersion <version>
-PackageDirectory <candidate-nuget-directory>` (optionally `-RuntimeIdentifier <rid>`).
The script consumes the existing candidate feed and restores the template consumer
into a fresh package cache. It proves managed and host-runtime NativeAOT execution
on the selected platform. It does not rebuild or repack the candidates.
All four exact-version candidate package files must exist before the run starts.
The committed version in `eng/Versions.props` is a candidate authority, not evidence
of NuGet publication. Source mapping prevents fallback to a published Command Line
package, even if that version also exists on NuGet.

The consumer uses the optional Spectre package and CommandApp with a built-in string codec,
as well as the kernel's packaged method-first analyzer,
the Hosting adapter, application-owned JSON metadata, and a bounded
`ProcessRunner` child command. It also proves an application-owned `--output`
option alongside a configured `--runic-output` transport option and variadic
generated `IReadOnlyList<string>` option and trailing positional binding,
including literal application arguments after `--`, and required generated
scalar/repeated options. It also proves a handler-owned warning diagnostic is
preserved in the JSON envelope and written to human stderr by both managed and
NativeAOT executions. It also exercises a nonzero handler failure with an
application-owned human stdout report and diagnostics/fault on stderr, while
proving that the same report is absent from the JSON failure envelope.
It also proves a generated-catalog parse failure retains an explicit JSON
transport classification while redacting the unknown option value.
It also exercises declared typed recovery data with exact path/target identities,
nonzero cancellation, identity-checked reading, and generated JSON context defaults.
It also proves that a fault's `HelpUri` survives JSON and human output while the
URL in its message is still redacted. Before the run, the script checks that each
packed README links to the candidate's release tag and to no main branch.

Per-run artifacts use a short uniquely named directory below the OS temporary
directory so the isolated package cache and NativeAOT output stay bounded.

The fresh package cache and isolated feed prove that the consumer resolves only
the packed artifacts and their declared dependencies before managed and NativeAOT
execution.

The same run copies the maintained `hello-world` and command-tree tutorial C#
sources outside the checkout and builds them with PackageReferences. It executes
the [package-only quick-start](../../../../../README.md#package-only-quick-start)
greeting in human and JSON modes and help, with assertions on the output. The
hello-world project references only the core package. It also executes hosted
service, help and empty-input UI-selection paths in the command-tree example. This
checks that the tutorials need no repository-only generator/build imports.
The application UI branch is a console fixture; it does not open a desktop window.

For focused managed verification of declared failure data and protocol
compatibility, run `pwsh -NoProfile -File Invoke-DeclaredFailureConsumer.ps1
-PackageVersion <distinct-candidate-version> -PackageDirectory <candidate-feed>`.
This runs the package consumer above, emits its typed failure frame, and reads it
with an independent project pinned to the published `0.6.0-preview.2` package.
The earlier reader must ignore `fault.data` while retaining the safe fault,
null success payload, and nonzero exit. Candidate source mapping prevents a
published-package fallback. Use a distinct candidate version with the existing
NuGet cache (including the caller's `NUGET_PACKAGES` setting); this check creates
no fresh dependency cache and removes its temporary projects/frame afterward.
It packs no packages and performs no NativeAOT publish or tutorial matrix.
