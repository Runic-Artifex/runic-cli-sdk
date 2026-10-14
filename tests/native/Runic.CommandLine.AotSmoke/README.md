# Command Line Native AOT smoke

This package-free executable references the owned Command Line projects
directly and proves the generated method-first parser-to-handler-to-JSON path.
It uses the source generator, an application-owned source-generated JSON
context, and invariant decimal binding without handwritten binders, factories,
or codecs. It then runs itself as a child through `Runic.CommandLine.Processes`,
with live output and a cancellation that ends the child's process group or job
object.

Run `Invoke-AotSmoke.ps1` to perform restore, managed execution, host-runtime
NativeAOT publication, and native execution.
Use `-RuntimeIdentifier <rid>` for an explicit compatible host target. CI checks
`linux-x64`, `win-x64` and `osx-arm64` on their corresponding operating systems.
