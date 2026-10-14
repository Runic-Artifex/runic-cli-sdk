using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

/// <summary>Connects an application to the process standard streams.</summary>
/// <remarks>
/// As an <see cref="ICommandTerminal"/> it reads single keys from the process console and supports ANSI when standard
/// input and standard error are terminals, <c>NO_COLOR</c> is not set, <c>TERM</c> is set and not <c>dumb</c>, and, on
/// Windows, the console is Windows Terminal (<c>WT_SESSION</c>). Wrap it in <c>SpectreCommandConsole</c> to detect ANSI
/// support in other Windows consoles.
/// </remarks>
public sealed class SystemCommandConsole : ICommandConsole, ICommandTerminal
{
    private static readonly TimeSpan KeyPollInterval = TimeSpan.FromMilliseconds(15);
    private readonly System.IO.TextReader _input = Console.In;
    private readonly System.IO.TextWriter _output = Console.Out;
    private readonly System.IO.TextWriter _error = Console.Error;
    /// <inheritdoc />
    /// <remarks>True when standard input and standard error are terminals; standard output may be redirected.</remarks>
    public bool IsInteractive => !IsInputRedirected && !IsErrorRedirected;
    /// <inheritdoc />
    public bool IsInputRedirected => Console.IsInputRedirected;
    /// <inheritdoc />
    public bool IsOutputRedirected => Console.IsOutputRedirected;
    /// <inheritdoc />
    public bool IsErrorRedirected => Console.IsErrorRedirected;
    /// <inheritdoc />
    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => _input.ReadLineAsync(cancellationToken);
    /// <inheritdoc />
    public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => new(_output.WriteAsync(value, cancellationToken));
    /// <inheritdoc />
    public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => new(_error.WriteAsync(value, cancellationToken));
    /// <inheritdoc />
    public bool SupportsAnsi => !Console.IsInputRedirected && !Console.IsErrorRedirected && TerminalAcceptsAnsi();
    /// <inheritdoc />
    public bool SupportsUnicode => Environment.GetEnvironmentVariable("TERM") != "dumb" && Console.OutputEncoding.CodePage is 65001 or 1200 or 1201;
    /// <inheritdoc />
    public int Width
    {
        get
        {
            if (Console.IsErrorRedirected) return 80;
            try { return Console.WindowWidth >= 20 ? Console.WindowWidth : 80; }
            catch (Exception exception) when (exception is System.IO.IOException or PlatformNotSupportedException) { return 80; }
        }
    }

    /// <inheritdoc />
    public int Height
    {
        get
        {
            if (Console.IsErrorRedirected) return 24;
            try { return Console.WindowHeight >= 4 ? Console.WindowHeight : 24; }
            catch (Exception exception) when (exception is System.IO.IOException or PlatformNotSupportedException) { return 24; }
        }
    }

    /// <inheritdoc />
    /// <remarks>Polls for a key so that the token, which Ctrl+C cancels, ends the wait.</remarks>
    public async ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Console.KeyAvailable) return Console.ReadKey(intercept: true);
            await Task.Delay(KeyPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    async ValueTask ICommandTerminal.WriteAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken)
    {
        await _error.WriteAsync(value, cancellationToken).ConfigureAwait(false);
        await _error.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool TerminalAcceptsAnsi()
    {
        if (Environment.GetEnvironmentVariable("NO_COLOR") is not null) return false;
        string? term = Environment.GetEnvironmentVariable("TERM");
        if (term == "dumb") return false;
        return OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("WT_SESSION") is not null : !string.IsNullOrEmpty(term);
    }

    /// <inheritdoc />
    public async ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
    {
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        await Console.OpenStandardOutput().WriteAsync(value, cancellationToken).ConfigureAwait(false);
    }
}

// The dispatcher retains the original console. Handlers cannot prefix a JSON frame,
// even through the byte API, and cannot accidentally block a machine invocation.
internal sealed class MachineHandlerConsole(ICommandConsole inner) : ICommandConsole
{
    public bool IsInteractive => false;
    public bool IsInputRedirected => true;
    public bool IsOutputRedirected => true;
    public bool IsErrorRedirected => inner.IsErrorRedirected;
    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
    public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => inner.WriteErrorAsync(value, cancellationToken);
    public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken) => inner.WriteErrorAsync(Encoding.UTF8.GetString(value.Span).AsMemory(), cancellationToken);
    public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => inner.WriteErrorAsync(value, cancellationToken);
}
