using System;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine.Testing;

/// <summary>Runs the real application pipeline with isolated, captured IO.</summary>
public sealed class CommandAppTester
{
    private readonly Func<TestCommandConsole, CommandApp> _createApp;
    /// <summary>Initializes an application factory. Set deterministic ParseSettings in the factory.</summary>
    public CommandAppTester(Func<TestCommandConsole, CommandApp> createApp) => _createApp = createApp ?? throw new ArgumentNullException(nameof(createApp));
    /// <summary>
    /// Gets the factory of each run's console, for example an interactive one with queued prompt answers.
    /// By default each run gets a new noninteractive <see cref="TestCommandConsole"/>.
    /// </summary>
    public Func<TestCommandConsole> CreateConsole { get; init; } = static () => new TestCommandConsole();

    /// <summary>Runs one invocation with a fresh console from <see cref="CreateConsole"/>.</summary>
    public async Task<CommandAppTestResult> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        TestCommandConsole console = CreateConsole() ?? throw new InvalidOperationException("CreateConsole returned null.");
        int exitCode = await _createApp(console).RunAsync(args, cancellationToken).ConfigureAwait(false);
        return new CommandAppTestResult(exitCode, console.StandardOutput, console.StandardError);
    }

    /// <summary>
    /// Runs one invocation with a fresh console from <see cref="CreateConsole"/> through <see cref="CommandApp.Run"/>,
    /// so a command can hand a continuation back with <see cref="CommandExecutionContext.ContinueOnCallingThread"/>.
    /// The continuation runs on the thread that calls this method, and the result reports its exit code and the
    /// output captured once it has returned.
    /// </summary>
    public CommandAppTestResult Run(string[] args, CancellationToken cancellationToken = default)
    {
        TestCommandConsole console = CreateConsole() ?? throw new InvalidOperationException("CreateConsole returned null.");
        int exitCode = _createApp(console).Run(args, cancellationToken);
        return new CommandAppTestResult(exitCode, console.StandardOutput, console.StandardError);
    }
}

/// <summary>Captured process-facing application behavior.</summary>
public sealed record CommandAppTestResult(int ExitCode, string StandardOutput, string StandardError);
