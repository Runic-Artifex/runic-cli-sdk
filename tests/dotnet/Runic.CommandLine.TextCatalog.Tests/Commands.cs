using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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
}
