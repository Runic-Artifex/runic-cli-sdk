using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine.Conformance;

// The cl.* operations (eng/conformance/README.md, "Command Line"). Each calls one public entry point of
// Runic.CommandLine and reports its result as data; none checks the spec. A failure the library reports in terms the
// corpus has no code for is reported as `unmapped:<exception type>`, and the case fails.
internal sealed class Operations
{
    private readonly CatalogFactory _catalogs = new();

    // cl.parse {catalog, argv, env}: the normalized grammar result (grammar.md, "Normalized results").
    public JsonObject Parse(JsonElement arguments)
    {
        ParseOutcome outcome;
        try
        {
            outcome = RunParser(arguments);
        }
        catch (Exception exception) when (exception is not UnsupportedException)
        {
            return Unmapped(exception);
        }

        JsonObject result = outcome.Kind switch
        {
            ParseOutcomeKind.Invocation => new JsonObject
            {
                ["kind"] = "invocation",
                ["commandPath"] = Strings(outcome.Invocation!.Path.Segments),
                ["options"] = Bindings(outcome.Invocation.Options),
                ["arguments"] = Bindings(outcome.Invocation.Arguments),
            },
            ParseOutcomeKind.Help => new JsonObject
            {
                ["kind"] = "help",
                ["commandPath"] = Strings(outcome.HelpRequest!.Path.Segments),
            },
            ParseOutcomeKind.Version => new JsonObject { ["kind"] = "version" },
            ParseOutcomeKind.Error => new JsonObject { ["kind"] = "error" },
            _ => throw new InvalidOperationException($"Unknown parse outcome {outcome.Kind}."),
        };
        result["diagnostics"] = Diagnostics(outcome.Diagnostics);
        return result;
    }

    // cl.validate {catalog, argv, env, conversions}: the normalized result after input validation (grammar.md, "Input
    // validation"). A parse result other than an invocation is the result. An invocation runs through the executor with
    // a binder that accepts everything but converts the values that `conversions` names, so the outcome's diagnostics
    // are the framework's own checks and the binder's: relations and paths before binding, bounds after conversion.
    public JsonObject Validate(JsonElement arguments)
    {
        ParseOutcome outcome;
        try
        {
            outcome = RunParser(arguments);
        }
        catch (Exception exception) when (exception is not UnsupportedException)
        {
            return Unmapped(exception);
        }

        if (outcome.Kind != ParseOutcomeKind.Invocation)
        {
            return Parse(arguments);
        }

        var sink = new DiagnosticSink();
        var conversions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (arguments.TryGetProperty("conversions", out JsonElement given))
        {
            foreach (JsonProperty conversion in given.EnumerateObject())
            {
                conversions.Add(conversion.Name, conversion.Value.GetString()!);
            }
        }

        AdapterBinder.Conversions = conversions;
        try
        {
            var executor = new CommandExecutor(CommandScopes.FromServices(EmptyServices.Instance));
            CommandExecutionResult result = executor
                .ExecuteAsync(new CommandExecutionRequest(outcome.Invocation!, NullConsole.Instance, CultureInfo.InvariantCulture, "conformance"), sink)
                .AsTask().GetAwaiter().GetResult();
            if (!result.Succeeded)
            {
                return new JsonObject { ["kind"] = "error", ["diagnostics"] = Diagnostics(sink.Diagnostics) };
            }
        }
        catch (Exception exception) when (exception is not UnsupportedException)
        {
            return Unmapped(exception);
        }
        finally
        {
            AdapterBinder.Conversions = null;
        }

        return Parse(arguments);
    }

    // cl.catalog {catalog}: whether the catalog builds, and the codes of the issues that reject it (grammar.md,
    // "Catalog validity").
    public static JsonObject Catalog(JsonElement arguments)
    {
        try
        {
            _ = CatalogFactory.Build(arguments.GetProperty("catalog"));
            return new JsonObject { ["kind"] = "valid" };
        }
        catch (CommandCatalogValidationException exception)
        {
            return new JsonObject
            {
                ["kind"] = "invalid",
                ["issues"] = new JsonArray([.. exception.Issues.Select(static issue => (JsonNode)new JsonObject
                {
                    ["code"] = issue.Code,
                    ["location"] = issue.Location,
                })]),
            };
        }
        catch (Exception exception) when (exception is not UnsupportedException)
        {
            return Unmapped(exception);
        }
    }

    // cl.help {catalog, applicationName, path, width}: the help text for a command path, as the help formatter writes it
    // for a terminal of `width` cells (help.md).
    public JsonObject Help(JsonElement arguments)
    {
        try
        {
            CommandCatalog catalog = _catalogs.Get(arguments.GetProperty("catalog"));
            string[] path = [.. arguments.GetProperty("path").EnumerateArray().Select(static item => item.GetString()!)];
            string text = CommandHelpFormatter.Format(
                catalog,
                arguments.GetProperty("applicationName").GetString()!,
                path.Length == 0 ? CommandPath.Root : new CommandPath(path),
                CommandTextContext.English,
                arguments.GetProperty("width").GetInt32());
            return new JsonObject { ["text"] = text };
        }
        catch (Exception exception) when (exception is not UnsupportedException)
        {
            return Unmapped(exception);
        }
    }

    // cl.human {catalog, applicationName, argv, outcome, texts}: what the standalone runner writes in human output when
    // every command's handler returns `outcome` (hosting.md, "Human output"). The runner parses `argv` without an
    // environment, handles no signals and resolves text through `texts` when the row gives any.
    public static JsonObject Human(JsonElement arguments)
    {
        try
        {
            JsonElement outcome = arguments.GetProperty("outcome");
            if (!outcome.GetProperty("success").GetBoolean())
            {
                return RunHuman(arguments, CommandResultCodecs.String, () => Failure<string>(outcome));
            }

            string payloadType = outcome.GetProperty("payloadType").GetString()!;
            CommandDiagnostic[] diagnostics = Diagnostics(outcome);
            return payloadType switch
            {
                "runic.text/1" => RunHuman(arguments, CommandResultCodecs.String,
                    () => CommandOutcome.Success(outcome.GetProperty("payload").GetString()!, diagnostics)),
                "runic.unit/1" => RunHuman(arguments, CommandResultCodecs.Unit, () => CommandOutcome.Success(default(CommandUnit), diagnostics)),
                _ => throw new UnsupportedException($"human output of the payload type {payloadType}"),
            };
        }
        catch (Exception exception) when (exception is not UnsupportedException)
        {
            return Unmapped(exception);
        }
    }

    private static JsonObject RunHuman<T>(JsonElement arguments, ICommandResultCodec<T> codec, Func<CommandOutcome<T>> outcome)
    {
        CommandCatalog catalog = CatalogFactory.Build(arguments.GetProperty("catalog"), codec, outcome);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        if (arguments.TryGetProperty("texts", out JsonElement given))
        {
            foreach (JsonProperty text in given.EnumerateObject())
            {
                texts.Add(text.Name, text.Value.GetString()!);
            }
        }

        var console = new CapturingConsole();
        var app = new CommandApp(catalog)
        {
            Name = arguments.GetProperty("applicationName").GetString()!,
            Console = console,
            Culture = CultureInfo.GetCultureInfo("en"),
            TextResolver = texts.Count > 0 ? new CorpusTextResolver(texts) : null,
            ParseSettings = new ParseSettings { GetEnvironmentVariable = static _ => null },
            HandleTerminationSignals = false,
        };
        int exitCode = app.Run([.. arguments.GetProperty("argv").EnumerateArray().Select(static item => item.GetString()!)]);
        return new JsonObject { ["exitCode"] = exitCode, ["stdout"] = console.Out.ToString(), ["stderr"] = console.Error.ToString() };
    }

    private static CommandOutcome<T> Failure<T>(JsonElement outcome)
    {
        string category = outcome.GetProperty("category").GetString()!;
        JsonElement fault = outcome.GetProperty("fault");
        return CommandOutcome.Failure<T>(
            category switch
            {
                "usage" => CommandExitCategory.Usage,
                "validation" => CommandExitCategory.Validation,
                "unavailable" => CommandExitCategory.Unavailable,
                "command-failure" => CommandExitCategory.CommandFailure,
                "host-failure" => CommandExitCategory.HostFailure,
                _ => throw new UnsupportedException($"the failure category {category}"),
            },
            Fault(fault),
            Diagnostics(outcome),
            outcome.TryGetProperty("humanOutput", out JsonElement human) ? human.GetString() : null);
    }

    private static CommandDiagnostic[] Diagnostics(JsonElement input) =>
        input.TryGetProperty("diagnostics", out JsonElement diagnostics) ? [.. diagnostics.EnumerateArray().Select(Diagnostic)] : [];

    private static CommandFault Fault(JsonElement fault)
    {
        // The producer's details in the order it gave them: the first of two keys that sanitize alike wins.
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonElement detail in fault.GetProperty("details").EnumerateArray())
        {
            details.Add(detail[0].GetString()!, detail[1].GetString()!);
        }

        return new CommandFault(
            fault.GetProperty("code").GetString()!,
            fault.GetProperty("message").GetString()!,
            details,
            fault.GetProperty("retryable").GetBoolean())
        {
            HelpUri = Uri(fault, "helpUri"),
        };
    }

    // cl.classify {catalog, argv, env}: the output mode and its source, or the diagnostics (grammar.md, "Output
    // classification").
    public JsonObject Classify(JsonElement arguments)
    {
        ParseOutcome outcome;
        try
        {
            outcome = RunParser(arguments);
        }
        catch (Exception exception) when (exception is not UnsupportedException)
        {
            return Unmapped(exception);
        }

        if (outcome.Kind == ParseOutcomeKind.Error)
        {
            return new JsonObject { ["kind"] = "error", ["diagnostics"] = Diagnostics(outcome.Diagnostics) };
        }

        CommandOutputClassification? classification = outcome.Invocation?.OutputClassification ?? outcome.OutputClassification;
        if (classification is not { IsValid: true, Mode: { } mode, Source: { } source })
        {
            return new JsonObject { ["error"] = $"no valid output classification for a {outcome.Kind} outcome" };
        }

        return new JsonObject
        {
            ["kind"] = "output-mode",
            ["mode"] = mode switch
            {
                CommandOutputMode.Human => "human",
                CommandOutputMode.Json => "json",
                _ => mode.ToString(),
            },
            ["source"] = source switch
            {
                CommandOutputModeSource.Default => "default",
                CommandOutputModeSource.Environment => "environment",
                CommandOutputModeSource.ExplicitArgument => "explicit-argument",
                _ => source.ToString(),
            },
            ["diagnostics"] = Diagnostics(outcome.Diagnostics),
        };
    }

    // cl.readEnvelope {input, payloadType, contract}: the strict reader's result for the frame in `input` (base64), by
    // a client that supports `payloadType` with its registered metadata. `contract` is the manifest's description of
    // that type; this adapter registers record types for the sample payloads instead.
    public static JsonObject ReadEnvelope(JsonElement arguments)
    {
        byte[] frame = Convert.FromBase64String(arguments.GetProperty("input").GetString()!);
        string payloadType = arguments.GetProperty("payloadType").GetString()!;
        var recorder = new DecodeRecorder();
        try
        {
            JsonObject envelope = payloadType switch
            {
                "sample.status/1" or "sample.optional-status/1" => Envelope(
                    CommandJsonEnvelopeReader.Read(frame, payloadType, recorder.Wrap(PayloadJsonContext.Default.StatusPayload))),
                "sample.export-result/1" => Envelope(
                    CommandJsonEnvelopeReader.Read(frame, payloadType, recorder.Wrap(PayloadJsonContext.Default.ExportPayload))),
                "sample.measurement/1" => Envelope(
                    CommandJsonEnvelopeReader.Read(frame, payloadType, recorder.Wrap(PayloadJsonContext.Default.MeasurementPayload))),
                _ => throw new UnsupportedException($"the payload type {payloadType} is not registered in the .NET adapter"),
            };
            return new JsonObject { ["result"] = "accepted", ["envelope"] = envelope, ["payloadDecodeAttempted"] = recorder.Attempted };
        }
        catch (CommandProtocolException exception)
        {
            return new JsonObject { ["result"] = "rejected", ["reason"] = exception.Kind, ["payloadDecodeAttempted"] = recorder.Attempted };
        }
        catch (Exception exception) when (exception is not UnsupportedException)
        {
            Console.Error.WriteLine(exception);
            return new JsonObject
            {
                ["result"] = "rejected",
                ["reason"] = $"unmapped:{exception.GetType().Name}",
                ["payloadDecodeAttempted"] = recorder.Attempted,
            };
        }
    }

    // cl.write {input}: the framed response the writer produces for a writer case's input (protocol README, "Corpus
    // layout"), as base64.
    public static JsonObject Write(JsonElement arguments)
    {
        try
        {
            return new JsonObject { ["output"] = Convert.ToBase64String(Serialize(arguments.GetProperty("input"))) };
        }
        catch (Exception exception) when (exception is not UnsupportedException)
        {
            return Unmapped(exception);
        }
    }

    private ParseOutcome RunParser(JsonElement arguments)
    {
        CommandCatalog catalog = _catalogs.Get(arguments.GetProperty("catalog"));
        string[] argv = [.. arguments.GetProperty("argv").EnumerateArray().Select(static item => item.GetString()!)];
        // The captured environment: a variable the case does not list, or lists as null, is unset.
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (arguments.TryGetProperty("env", out JsonElement env))
        {
            foreach (JsonProperty variable in env.EnumerateObject())
            {
                if (variable.Value.ValueKind == JsonValueKind.String)
                {
                    environment[variable.Name] = variable.Value.GetString()!;
                }
            }
        }

        environment.TryGetValue(CommandOutputClassifier.EnvironmentVariableName, out string? output);
        var settings = new ParseSettings(output)
        {
            GetEnvironmentVariable = name => environment.TryGetValue(name, out string? value) ? value : null,
        };
        return PortableCommandSyntaxAdapter.Instance.Parse(catalog, argv, settings);
    }

    // A payload type the adapter registers a record for is written through that record's metadata, as an application
    // writes its typed results, so its numbers are the writer's own text. Any other payload is a JSON value, which the
    // writer copies as given.
    private static byte[] Serialize(JsonElement input) =>
        input.GetProperty("success").GetBoolean() && input.GetProperty("payloadType").GetString() == "sample.measurement/1"
            ? Serialize(input, PayloadJsonContext.Default.MeasurementPayload)
            : Serialize(input, PayloadJsonContext.Default.JsonElement);

    private static byte[] Serialize<T>(JsonElement input, JsonTypeInfo<T> payloadInfo)
    {
        string requestId = input.GetProperty("requestId").GetString()!;
        string command = input.GetProperty("command").GetString()!;
        int exitCode = input.GetProperty("exitCode").GetInt32();
        CommandDiagnostic[] diagnostics = Diagnostics(input);
        if (input.GetProperty("success").GetBoolean())
        {
            string payloadType = input.GetProperty("payloadType").GetString()!;
            T payload = input.GetProperty("payload").Deserialize(payloadInfo)!;
            CommandResponse<T> success = exitCode == 0
                ? CommandResponse.Success(requestId, command, payloadType, payload, diagnostics)
                : CommandResponse.FromOutcome(
                    requestId,
                    command,
                    exitCode,
                    payloadType,
                    CommandOutcome.Success(payload).WithExitCode(exitCode),
                    diagnostics);
            return CommandJsonEnvelopeWriter.Serialize(success, payloadInfo);
        }

        CommandFailureData? failureData = input.TryGetProperty("failureData", out JsonElement data) && data.ValueKind != JsonValueKind.Null
            ? CommandFailureData.Create(data.GetProperty("type").GetString()!, data.GetProperty("payload"), PayloadJsonContext.Default.JsonElement)
            : null;
        CommandResponse<T> failure = CommandResponse.Failure<T>(
            requestId,
            command,
            exitCode,
            Fault(input.GetProperty("fault")),
            diagnostics,
            failureData);
        return CommandJsonEnvelopeWriter.Serialize(failure, payloadInfo);
    }

    private static CommandDiagnostic Diagnostic(JsonElement input)
    {
        string phase = input.GetProperty("phase").GetString()!;
        string severity = input.GetProperty("severity").GetString()!;
        string[] path = [.. input.GetProperty("commandPath").EnumerateArray().Select(static item => item.GetString()!)];
        JsonElement tokenIndex = input.GetProperty("tokenIndex");
        return new CommandDiagnostic(
            input.GetProperty("code").GetString()!,
            input.GetProperty("kind").GetString()!,
            input.GetProperty("message").GetString()!,
            phase switch
            {
                "parse" => CommandDiagnosticPhase.Parse,
                "binding" => CommandDiagnosticPhase.Binding,
                "execution" => CommandDiagnosticPhase.Execution,
                _ => throw new UnsupportedException($"the diagnostic phase {phase}"),
            },
            severity switch
            {
                "error" => CommandDiagnosticSeverity.Error,
                "warning" => CommandDiagnosticSeverity.Warning,
                "information" => CommandDiagnosticSeverity.Information,
                _ => throw new UnsupportedException($"the diagnostic severity {severity}"),
            },
            tokenIndex.ValueKind == JsonValueKind.Null ? null : tokenIndex.GetInt32(),
            [.. input.GetProperty("arguments").EnumerateArray().Select(static item => item.GetString()!)],
            path.Length == 0 ? CommandPath.Root : new CommandPath(path),
            input.GetProperty("messageKey").GetString())
        {
            Binding = input.TryGetProperty("binding", out JsonElement binding) ? binding.GetString() : null,
            HelpUri = Uri(input, "helpUri"),
        };
    }

    private static Uri? Uri(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? new Uri(value.GetString()!, UriKind.Absolute)
            : null;

    // The envelope as the reader returned it: its known members, with the optional ones only when present.
    private static JsonObject Envelope<T>(CommandResponse<T> response)
    {
        JsonObject? fault = null;
        if (response.Fault is { } value)
        {
            var details = new JsonObject();
            foreach (KeyValuePair<string, string> detail in value.Details)
            {
                details[detail.Key] = detail.Value;
            }

            fault = new JsonObject
            {
                ["code"] = value.Code,
                ["message"] = value.Message,
                ["details"] = details,
                ["retryable"] = value.Retryable,
            };
            if (value.HelpUri is { } help)
            {
                fault["helpUri"] = help.OriginalString;
            }

            if (response.FailureData is { } data)
            {
                fault["data"] = new JsonObject { ["type"] = data.Type };
            }
        }

        return new JsonObject
        {
            ["requestId"] = response.RequestId,
            ["command"] = response.Command,
            ["success"] = response.Succeeded,
            ["exitCode"] = response.ExitCode,
            ["payloadType"] = response.PayloadType,
            ["fault"] = fault,
            ["diagnostics"] = new JsonArray([.. response.Diagnostics.Select(static diagnostic => (JsonNode)FullDiagnostic(diagnostic))]),
        };
    }

    private static JsonObject FullDiagnostic(CommandDiagnostic diagnostic)
    {
        var result = new JsonObject
        {
            ["code"] = diagnostic.Code,
            ["kind"] = diagnostic.Kind,
            ["commandPath"] = Strings(diagnostic.Path.Segments),
            ["messageKey"] = diagnostic.MessageKey,
            ["message"] = diagnostic.Message,
            ["phase"] = diagnostic.Phase switch
            {
                CommandDiagnosticPhase.Parse => "parse",
                CommandDiagnosticPhase.Binding => "binding",
                CommandDiagnosticPhase.Execution => "execution",
                _ => diagnostic.Phase.ToString(),
            },
            ["severity"] = diagnostic.Severity switch
            {
                CommandDiagnosticSeverity.Error => "error",
                CommandDiagnosticSeverity.Warning => "warning",
                CommandDiagnosticSeverity.Information => "information",
                _ => diagnostic.Severity.ToString(),
            },
            ["tokenIndex"] = diagnostic.TokenIndex,
            ["arguments"] = Strings(diagnostic.Arguments),
        };
        if (diagnostic.Binding is { } binding)
        {
            result["binding"] = binding;
        }

        if (diagnostic.HelpUri is { } help)
        {
            result["helpUri"] = help.OriginalString;
        }

        return result;
    }

    // A grammar diagnostic: the members the grammar corpus compares.
    private static JsonArray Diagnostics(IReadOnlyList<CommandDiagnostic> diagnostics) =>
        new([.. diagnostics.Select(static diagnostic => (JsonNode)new JsonObject
        {
            ["code"] = diagnostic.Code,
            ["kind"] = diagnostic.Kind,
            ["tokenIndex"] = diagnostic.TokenIndex,
            ["arguments"] = Strings(diagnostic.Arguments),
            ["binding"] = diagnostic.Binding,
        })]);

    private static JsonArray Bindings(IReadOnlyList<CommandValueBinding> bindings) =>
        new([.. bindings.Select(static binding => (JsonNode)new JsonObject { ["id"] = binding.Id, ["values"] = Strings(binding.Values) })]);

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(static value => (JsonNode)value)]);

    private static JsonObject Unmapped(Exception exception)
    {
        Console.Error.WriteLine(exception);
        return new JsonObject { ["error"] = $"unmapped:{exception.GetType().Name}: {exception.Message}" };
    }
}

// Records the diagnostics of the one outcome the executor writes.
internal sealed class DiagnosticSink : ICommandOutcomeSink
{
    public IReadOnlyList<CommandDiagnostic> Diagnostics { get; private set; } = [];

    public ValueTask WriteAsync<TResult>(
        CommandDescriptor command,
        CommandExecutionContext context,
        CommandOutcome<TResult> outcome,
        ICommandResultCodec<TResult> codec,
        int exitCode,
        IReadOnlyList<CommandDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        Diagnostics = diagnostics;
        return ValueTask.CompletedTask;
    }
}

internal sealed class EmptyServices : IServiceProvider
{
    public static readonly EmptyServices Instance = new();

    public object? GetService(Type serviceType) => null;
}


// A console that is never interactive and discards everything: validation writes nothing the adapter reports.
internal sealed class NullConsole : ICommandConsole
{
    public static readonly NullConsole Instance = new();

    public bool IsInteractive => false;

    public bool IsInputRedirected => true;

    public bool IsOutputRedirected => true;

    public bool IsErrorRedirected => true;

    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);

    public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

// A console that is never interactive and keeps what each stream receives.
internal sealed class CapturingConsole : ICommandConsole
{
    public StringBuilder Out { get; } = new();

    public StringBuilder Error { get; } = new();

    public bool IsInteractive => false;

    public bool IsInputRedirected => true;

    public bool IsOutputRedirected => true;

    public bool IsErrorRedirected => true;

    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);

    public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken)
    {
        Out.Append(value.Span);
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
    {
        Out.Append(Encoding.UTF8.GetString(value.Span));
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken)
    {
        Error.Append(value.Span);
        return ValueTask.CompletedTask;
    }
}

// A row's `texts`: each key's template, with `{0}`, `{1}` … replaced by the key's arguments in order.
internal sealed class CorpusTextResolver(IReadOnlyDictionary<string, string> texts) : ICommandTextResolver
{
    public string? Resolve(string key, CultureInfo culture, IReadOnlyList<string> arguments) =>
        texts.TryGetValue(key, out string? template) ? string.Format(CultureInfo.InvariantCulture, template, [.. arguments]) : null;
}
