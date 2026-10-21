# Runic.CommandLine.Spectre

Optional Spectre.Console presentation for Runic Command Line. Core command declarations,
parsing and machine envelopes remain independent of Spectre.

```csharp
return await new CommandApp(GeneratedCommandCatalog.Create())
{
    Name = "hello",
    Console = new SpectreCommandConsole(),
    HelpPresenter = new SpectreHelpPresenter()
}.RunAsync(args);
```

Use `WriteAsync` for Spectre tables, trees and panels. Text is always treated as literal
text, never markup. Redirected output and `NO_COLOR` disable ANSI automatically. Terminal width is detected automatically. Explicit
width, color and Unicode overrides make tests deterministic. Byte output passes through
unchanged, so JSON framing remains owned by Runic. Handlers should use their injected
`ICommandConsole`; in JSON mode this console routes incidental output to stderr and disables
reads.

Use `new SpectreCommandConsole(context.Console).WithProgressAsync(...)` inside a
handler. Progress receives percentages from 0 to 100; interactive terminals use
Spectre's live display on stderr. Noninteractive runs emit one status line and do
not animate. Forward the cancellation token into the operation. `PromptAsync`
returns its fallback when the invocation cannot interact.

For select, multi-select, confirm and text prompts, a spinner or a task log, use
`CommandPrompts` from `Runic.CommandLine`. When the invocation cannot interact, a
`CommandPrompts` question returns its default, and one without a default fails the
command with `RCLI4001` (exit code 2); see
[when prompts ask](https://github.com/Runic-Artifex/runic-cli-sdk/blob/main/packages/dotnet/Runic.CommandLine/README.md#interactive-prompts). With `Console = new
SpectreCommandConsole()` they redraw only when this console would colour its own
output: its `color`, `unicode` and `width` settings apply to them, redirected
streams, `NO_COLOR` and `TERM=dumb` switch them to plain line prompts, and on
Windows Spectre's terminal detection enables ANSI for them. Prompts write
through the wrapped console without Spectre styling.
