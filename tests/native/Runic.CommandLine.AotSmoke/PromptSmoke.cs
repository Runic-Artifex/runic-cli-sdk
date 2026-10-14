using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Runic.CommandLine;
using Runic.CommandLine.Generated;

namespace Runic.CommandLine.AotSmoke;

/// <summary>
/// Answers the generated <c>ask</c> command's prompts on a scripted redrawing terminal and on plain lines, then checks
/// that JSON output and a noninteractive console fail with <c>RCLI4001</c> instead of asking. Exit codes 31 to 38.
/// </summary>
internal static class PromptSmoke
{
    private static readonly CommandPromptChoice<string>[] Templates = [new("web", "web", "Web app"), new("cli", "cli")];

    [Command("ask")]
    internal static async Task<string> Ask(CommandExecutionContext context, CancellationToken cancellationToken)
    {
        var prompts = new CommandPrompts(context);
        string template = await prompts.SelectAsync("Template", Templates, cancellationToken: cancellationToken).ConfigureAwait(false);
        string name = await prompts.TextAsync("Name", validate: static value => value.Length < 2 ? "Too short." : null, cancellationToken: cancellationToken).ConfigureAwait(false);
        bool confirmed = await prompts.ConfirmAsync("Continue?", cancellationToken: cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> parts = await prompts.MultiSelectAsync("Parts", Templates, required: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        await using CommandTaskLog log = await prompts.StartTaskLogAsync("Build", 2, cancellationToken).ConfigureAwait(false);
        await log.WriteLineAsync("compiling", cancellationToken).ConfigureAwait(false);
        await log.SucceedAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return $"{template}|{name}|{confirmed}|{string.Join(',', parts)}";
    }

    internal static async Task<int> RunAsync()
    {
        const string Expected = "cli|ok|True|web";
        var terminal = new ScriptedConsole(interactive: true, ansi: true);
        foreach (ConsoleKey key in new[] { ConsoleKey.DownArrow, ConsoleKey.Enter }) terminal.Key(key, '\0');
        terminal.Key(ConsoleKey.O, 'o').Key(ConsoleKey.K, 'k').Key(ConsoleKey.Enter, '\r').Key(ConsoleKey.Y, 'y').Key(ConsoleKey.Spacebar, ' ').Key(ConsoleKey.Enter, '\r');
        if (await App(terminal).RunAsync(["ask"]).ConfigureAwait(false) != 0 || !terminal.StandardOutput.Contains(Expected, StringComparison.Ordinal)) return 31;
        if (!terminal.StandardError.EndsWith("\u001b[?25h", StringComparison.Ordinal) || !terminal.StandardError.Contains("Build", StringComparison.Ordinal)) return 32;

        var lines = new ScriptedConsole(interactive: true, ansi: false);
        foreach (string line in new[] { "2", "x", "ok", "y", "1" }) lines.Lines.Enqueue(line);
        if (await App(lines).RunAsync(["ask"]).ConfigureAwait(false) != 0 || !lines.StandardOutput.Contains(Expected, StringComparison.Ordinal)) return 33;
        if (!lines.StandardError.Contains("Too short.", StringComparison.Ordinal)) return 34;

        var json = new ScriptedConsole(interactive: true, ansi: true);
        if (await App(json).RunAsync(["ask", "--output=json"]).ConfigureAwait(false) != CommandExitCodes.Usage) return 35;
        if (!json.StandardOutput.Contains("\"RCLI4001\"", StringComparison.Ordinal) || !json.StandardOutput.Contains("prompt-json-output", StringComparison.Ordinal)) return 36;

        var redirected = new ScriptedConsole(interactive: false, ansi: false);
        if (await App(redirected).RunAsync(["ask"]).ConfigureAwait(false) != CommandExitCodes.Usage) return 37;
        if (!redirected.StandardError.StartsWith("RCLI4001: Template needs an answer", StringComparison.Ordinal)) return 38;
        return 0;
    }

    private static CommandApp App(ICommandConsole console) =>
        new(GeneratedCommandCatalog.Create()) { Console = console, HandleCancelKeyPress = false, ParseSettings = ParseSettings.Default };

    private sealed class ScriptedConsole(bool interactive, bool ansi) : ICommandConsole, ICommandTerminal
    {
        private readonly StringBuilder _standardOutput = new();
        private readonly StringBuilder _standardError = new();
        private readonly Channel<ConsoleKeyInfo> _keys = Channel.CreateUnbounded<ConsoleKeyInfo>();
        public Queue<string> Lines { get; } = new();
        public string StandardOutput => _standardOutput.ToString();
        public string StandardError => _standardError.ToString();
        public bool IsInteractive => interactive;
        public bool IsInputRedirected => !interactive;
        public bool IsOutputRedirected => !interactive;
        public bool IsErrorRedirected => !interactive;
        public bool SupportsAnsi => ansi;
        public bool SupportsUnicode => true;
        public int Width => 60;

        public ScriptedConsole Key(ConsoleKey key, char character)
        {
            _keys.Writer.TryWrite(new ConsoleKeyInfo(character, key, false, false, false));
            return this;
        }

        public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken) => _keys.Reader.ReadAsync(cancellationToken);
        public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Lines.TryDequeue(out string? line) ? line : null);
        public ValueTask WriteAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => WriteErrorAsync(value, cancellationToken);
        public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) { _standardOutput.Append(value.Span); return ValueTask.CompletedTask; }
        public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken) { _standardOutput.Append(Encoding.UTF8.GetString(value.Span)); return ValueTask.CompletedTask; }
        public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) { _standardError.Append(value.Span); return ValueTask.CompletedTask; }
    }
}
