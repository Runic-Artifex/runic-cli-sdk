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
    /// Gets or sets the factory of each run's console, for example an interactive one with queued prompt answers.
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
}

/// <summary>Captured process-facing application behavior.</summary>
public sealed record CommandAppTestResult(int ExitCode, string StandardOutput, string StandardError);
