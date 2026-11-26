using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    private static byte[] Serialize(JsonElement input)
    {
        string requestId = input.GetProperty("requestId").GetString()!;
        string command = input.GetProperty("command").GetString()!;
        int exitCode = input.GetProperty("exitCode").GetInt32();
        CommandDiagnostic[] diagnostics = [.. input.GetProperty("diagnostics").EnumerateArray().Select(Diagnostic)];
        if (input.GetProperty("success").GetBoolean())
        {
            string payloadType = input.GetProperty("payloadType").GetString()!;
            JsonElement payload = input.GetProperty("payload");
            CommandResponse<JsonElement> success = exitCode == 0
                ? CommandResponse.Success(requestId, command, payloadType, payload, diagnostics)
                : CommandResponse.FromOutcome(
                    requestId,
                    command,
                    exitCode,
                    payloadType,
                    CommandOutcome.Success(payload).WithExitCode(exitCode),
                    diagnostics);
            return CommandJsonEnvelopeWriter.Serialize(success, PayloadJsonContext.Default.JsonElement);
        }

        JsonElement fault = input.GetProperty("fault");
        // The producer's details in the order it gave them: the first of two keys that sanitize alike wins.
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonElement detail in fault.GetProperty("details").EnumerateArray())
        {
            details.Add(detail[0].GetString()!, detail[1].GetString()!);
        }

        CommandFailureData? failureData = input.TryGetProperty("failureData", out JsonElement data) && data.ValueKind != JsonValueKind.Null
            ? CommandFailureData.Create(data.GetProperty("type").GetString()!, data.GetProperty("payload"), PayloadJsonContext.Default.JsonElement)
            : null;
        CommandResponse<JsonElement> failure = CommandResponse.Failure<JsonElement>(
            requestId,
            command,
            exitCode,
            new CommandFault(
                fault.GetProperty("code").GetString()!,
                fault.GetProperty("message").GetString()!,
                details,
                fault.GetProperty("retryable").GetBoolean())
            {
                HelpUri = Uri(fault, "helpUri"),
            },
            diagnostics,
            failureData);
        return CommandJsonEnvelopeWriter.Serialize(failure, PayloadJsonContext.Default.JsonElement);
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
