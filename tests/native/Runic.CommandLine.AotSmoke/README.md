# Command Line Native AOT smoke

This package-free executable references the owned Command Line projects
directly and proves the generated method-first parser-to-handler-to-JSON path.
It uses the source generator, an application-owned source-generated JSON
context, and invariant decimal binding without handwritten binders, factories,
or codecs. It checks a JSON output alias of payload shape and a continuation that
a command hands back to the thread that called `CommandApp.Run`. It then runs itself as a child through `Runic.CommandLine.Processes`,
with live output and a cancellation that ends the child's process group or job
object.

Run `Invoke-AotSmoke.ps1` to perform restore, managed execution, host-runtime
NativeAOT publication, and native execution.
Use `-RuntimeIdentifier <rid>` for an explicit compatible host target. CI checks
`linux-x64`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm64`, `win-x64`,
`win-arm64` and `osx-arm64` on their corresponding operating systems and
architectures, the musl RIDs in Alpine.

The script also reports the size of the native executable. On `linux-x64` Release it fails when the executable is
larger than the `limit` in `size-budget.json`, the recorded `measured` size plus 2 %; the other RIDs only report it,
in the CI step summary as well. When an intended change moves the size, publish on `linux-x64` and update `measured`
and `limit` in the same pull request. Find the cause of an unexpected change with `-p:IlcGenerateMstatFile=true`.
