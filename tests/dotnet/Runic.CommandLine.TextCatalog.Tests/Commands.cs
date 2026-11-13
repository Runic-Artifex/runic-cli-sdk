using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Runic.CommandLine.Processes;

namespace Runic.CommandLine.TextCatalog.Tests;

// Commands whose help, usage errors, value errors, prompts and faults reach most framework keys.
internal static class Commands
{
    [Command("greet", Description = "Greet a person")]
    public static string Greet(
        [Argument(Description = "Name of the person to greet")] string name,
        [Option("--times", Description = "How often to greet", Minimum = 1, Maximum = 3)] int times = 1) =>
        string.Join(' ', System.Linq.Enumerable.Repeat("Hello " + name, times));

    // A prompt without a default: the test console is not interactive, so it fails with RCLI4001.
    [Command("ask", Description = "Ask for a name")]
    public static async Task<string> Ask(CommandExecutionContext context, CancellationToken cancellationToken) =>
        "Hello " + await new CommandPrompts(context).TextAsync("Name", cancellationToken: cancellationToken).ConfigureAwait(false);

    [Command("pick", Description = "Pick values")]
    public static string Pick(
        [Option("--mode", Description = "Speed", Choices = ["fast", "slow"])] string mode = "fast",
        [Option("--source", Description = "Input", MustExist = true)] FileInfo? source = null,
        [Option("--target", Description = "Output", MustExist = true)] DirectoryInfo? target = null,
        [Option("--id", Description = "Identifier")] Guid? id = null,
        [Option("--ratio", Description = "Ratio")] double? ratio = null,
        [Option("--apply", Description = "Apply", Requires = ["confirm"], ConflictsWith = ["dry-run"])] bool apply = false,
        [Option("--confirm", Description = "Confirm")] bool confirm = false,
        [Option("--dry-run", Description = "Dry run")] bool dryRun = false) =>
        mode + source?.Name + target?.Name + id + ratio + apply + confirm + dryRun;

    [Command("tag", Description = "Tag an item")]
    public static string Tag([Option("--label", Description = "Label", Required = true)] string? label) => label ?? "";

    [Command("fail", Description = "Fail unexpectedly")]
    public static string Fail() => throw new InvalidOperationException("Expected failure.");

    // A Runic.CommandLine.Processes fault, as a command returns it from a ProcessResult or ProcessException.
    [Command("process", Description = "Return a process fault")]
    public static CommandOutcome<string> Process([Argument(Description = "Fault code")] string code) =>
        CommandOutcome.Failure<string>(CommandExitCategory.CommandFailure, new CommandFault(code, ProcessFaultMessages.ByCode[code]));

    // A fault and a diagnostic with help links (hints.help-link).
    [Command("linked", Description = "Fail with help links")]
    public static CommandOutcome<string> Linked() =>
        CommandOutcome.Failure<string>(CommandExitCategory.CommandFailure,
            new CommandFault("APP1001", "Linked.") { HelpUri = new Uri("https://docs.example.com/errors#app1001") },
            [new CommandDiagnostic("RCLI8001", "linked-note", "Note.", CommandDiagnosticPhase.Execution, CommandDiagnosticSeverity.Warning)
                { HelpUri = new Uri("https://docs.example.com/errors#app1002") }]);

    // Messages that output redacts (redaction.*): a fault and a diagnostic with a path, and a diagnostic that is empty
    // after sanitizing.
    [Command("leak", Description = "Fail with unsafe messages")]
    public static CommandOutcome<string> Leak() =>
        CommandOutcome.Failure<string>(CommandExitCategory.CommandFailure, new CommandFault("APP1003", "Cannot read /home/ada/secret."),
            [
                new CommandDiagnostic("RCLI8002", "leak-path", "Read /tmp/ada.", CommandDiagnosticPhase.Execution, CommandDiagnosticSeverity.Warning),
                new CommandDiagnostic("RCLI8003", "leak-empty", "\u0007", CommandDiagnosticPhase.Execution, CommandDiagnosticSeverity.Warning),
            ]);
}
