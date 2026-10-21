using System;
using System.Collections.Generic;
using Runic.CommandLine;
using TUnit.Core;

namespace Runic.CommandLine.Contracts.Tests;

internal sealed class ContractTests
{
    [Test, DisplayName("protocol identity is stable")]
    public void ProtocolIdentityIsStable()
    {
        Assert.Equal("runic.commandline/1", CommandProtocol.Identity);
    }

    [Test, DisplayName("exit category names are stable")]
    public void ExitCategoryNamesAreStable()
    {
        Assert.SequenceEqual(
            [
                nameof(CommandExitCategory.Success),
                nameof(CommandExitCategory.Usage),
                nameof(CommandExitCategory.Validation),
                nameof(CommandExitCategory.Cancelled),
                nameof(CommandExitCategory.Unavailable),
                nameof(CommandExitCategory.CommandFailure),
                nameof(CommandExitCategory.HostFailure),
            ],
            Enum.GetNames<CommandExitCategory>());
    }

    [Test, DisplayName("default exit codes are stable")]
    public void DefaultExitCodesAreStable()
    {
        AssertExitCode(CommandExitCategory.Success, CommandExitCodes.Success, 0);
        AssertExitCode(CommandExitCategory.Usage, CommandExitCodes.Usage, 2);
        AssertExitCode(CommandExitCategory.Validation, CommandExitCodes.Validation, 2);
        AssertExitCode(CommandExitCategory.Cancelled, CommandExitCodes.Cancelled, 130);
        AssertExitCode(CommandExitCategory.Unavailable, CommandExitCodes.Unavailable, 1);
        AssertExitCode(CommandExitCategory.CommandFailure, CommandExitCodes.CommandFailure, 1);
        AssertExitCode(CommandExitCategory.HostFailure, CommandExitCodes.HostFailure, 3);
    }

    [Test, DisplayName("success outcome has no fault")]
    public void SuccessOutcomeHasNoFault()
    {
        CommandOutcome<string> outcome = CommandOutcome.Success("payload");

        Assert.True(outcome.Succeeded);
        Assert.Equal(CommandExitCategory.Success, outcome.ExitCategory);
        Assert.Equal("payload", outcome.Value);
        Assert.Null(outcome.Fault);
    }

    [Test, DisplayName("nullable success payload is supported")]
    public void NullableSuccessPayloadIsSupported()
    {
        CommandOutcome<string?> outcome = CommandOutcome.Success<string?>(null);

        Assert.True(outcome.Succeeded);
        Assert.Equal(CommandExitCategory.Success, outcome.ExitCategory);
        Assert.Null(outcome.Value);
        Assert.Null(outcome.Fault);
    }

    [Test, DisplayName("outcome diagnostics are copied and immutable")]
    public void OutcomeDiagnosticsAreCopiedAndImmutable()
    {
        var diagnostics = new List<CommandDiagnostic> { CreateDiagnostic(CommandDiagnosticSeverity.Warning) };
        CommandOutcome<string> outcome = CommandOutcome.Success("payload", diagnostics);
        diagnostics[0] = CreateDiagnostic(CommandDiagnosticSeverity.Information);

        Assert.Equal(1, outcome.Diagnostics.Count);
        Assert.Equal(CommandDiagnosticSeverity.Warning, outcome.Diagnostics[0].Severity);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<CommandDiagnostic>)outcome.Diagnostics).Add(CreateDiagnostic(CommandDiagnosticSeverity.Warning)));
    }

    [Test, DisplayName("successful outcomes permit warnings but reject errors")]
    public void SuccessfulOutcomeDiagnosticSeverities()
    {
        CommandOutcome<string> outcome = CommandOutcome.Success(
            "payload",
            [CreateDiagnostic(CommandDiagnosticSeverity.Warning), CreateDiagnostic(CommandDiagnosticSeverity.Information)]);

        Assert.Equal(2, outcome.Diagnostics.Count);
        Assert.Throws<ArgumentException>(() => CommandOutcome.Success(
            "payload",
            [CreateDiagnostic(CommandDiagnosticSeverity.Error)]));
    }

    [Test, DisplayName("outcome diagnostics are bounded and non-null")]
    public void OutcomeDiagnosticsAreBoundedAndNonNull()
    {
        var tooMany = new List<CommandDiagnostic>();
        for (int index = 0; index < 33; index++)
        {
            tooMany.Add(CreateDiagnostic(CommandDiagnosticSeverity.Error));
        }

        Assert.Throws<ArgumentException>(() => CommandOutcome.Failure<string>(
            CommandExitCategory.Validation,
            CreateFault(),
            tooMany));
        Assert.Throws<ArgumentException>(() => CommandOutcome.Failure<string>(
            CommandExitCategory.Validation,
            CreateFault(),
            [null!]));
    }

    [Test, DisplayName("failure human output is exact and human-only")]
    public void FailureHumanOutputIsExact()
    {
        string humanOutput = new string("\u001b[31mdry-run report\r\n".AsSpan());
        CommandOutcome<string> outcome = CommandOutcome.Failure<string>(
            CommandExitCategory.Validation,
            CreateFault(),
            [CreateDiagnostic(CommandDiagnosticSeverity.Error)],
            humanOutput);

        Assert.Equal(humanOutput, outcome.HumanOutput);
        Assert.False(ReferenceEquals(humanOutput, outcome.HumanOutput));
        Assert.Null(CommandOutcome.Failure<string>(
            CommandExitCategory.Validation,
            CreateFault(),
            null,
            string.Empty).HumanOutput);
        Assert.Null(CommandOutcome.Success("payload").HumanOutput);
    }

    [Test, DisplayName("failure human output is bounded valid UTF-8")]
    public void FailureHumanOutputIsBoundedValidUtf8()
    {
        Assert.Throws<ArgumentException>(() => CommandOutcome.Failure<string>(
            CommandExitCategory.Validation,
            CreateFault(),
            null,
            new string('x', 1_048_577)));
        Assert.Throws<ArgumentException>(() => CommandOutcome.Failure<string>(
            CommandExitCategory.Validation,
            CreateFault(),
            null,
            "\ud800"));
    }

    [Test, DisplayName("outcomes cannot bypass invariant factories")]
    public void OutcomesCannotBypassInvariantFactories()
    {
        Assert.Equal(0, typeof(CommandOutcome<string>).GetConstructors().Length);
    }

    [Test, DisplayName("unknown exit category has no default")]
    public void UnknownExitCategoryHasNoDefault()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CommandExitCodes.GetDefault((CommandExitCategory)int.MaxValue));
    }

    [Test, DisplayName("failure outcomes have no value")]
    public void FailureOutcomesHaveNoValue()
    {
        CommandFault fault = CreateFault();
        CommandExitCategory[] failureCategories =
        [
            CommandExitCategory.Usage,
            CommandExitCategory.Validation,
            CommandExitCategory.Cancelled,
            CommandExitCategory.Unavailable,
            CommandExitCategory.CommandFailure,
            CommandExitCategory.HostFailure,
        ];

        foreach (CommandExitCategory category in failureCategories)
        {
            CommandOutcome<string> outcome = CommandOutcome.Failure<string>(category, fault);

            Assert.False(outcome.Succeeded);
            Assert.Equal(category, outcome.ExitCategory);
            Assert.Null(outcome.Value);
            Assert.Same(fault, outcome.Fault!);
        }
    }

    [Test, DisplayName("success cannot be constructed as failure")]
    public void SuccessCannotBeConstructedAsFailure()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CommandOutcome.Failure<string>(CommandExitCategory.Success, CreateFault()));
    }

    [Test, DisplayName("unknown category cannot be constructed as failure")]
    public void UnknownCategoryCannotBeConstructedAsFailure()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CommandOutcome.Failure<string>((CommandExitCategory)int.MaxValue, CreateFault()));
    }

    [Test, DisplayName("failure requires a fault")]
    public void FailureRequiresFault()
    {
        Assert.Throws<ArgumentNullException>(
            () => CommandOutcome.Failure<string>(CommandExitCategory.CommandFailure, null!));
    }

    [Test, DisplayName("fault requires a stable code")]
    public void FaultRequiresStableCode()
    {
        Assert.Throws<ArgumentException>(() => _ = new CommandFault(string.Empty, "message"));
        Assert.Throws<ArgumentException>(() => _ = new CommandFault(" ", "message"));
    }

    [Test, DisplayName("fault requires a safe message")]
    public void FaultRequiresSafeMessage()
    {
        Assert.Throws<ArgumentException>(() => _ = new CommandFault("RCLI2001", string.Empty));
        Assert.Throws<ArgumentException>(() => _ = new CommandFault("RCLI2001", " "));
    }

    [Test, DisplayName("fault details are copied and observable")]
    public void FaultDetailsAreCopiedAndObservable()
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["parameter"] = "format",
        };
        var fault = new CommandFault("RCLI2001", "Invalid value.", details, retryable: true);
        details["parameter"] = "mutated";

        Assert.Equal("RCLI2001", fault.Code);
        Assert.Equal("Invalid value.", fault.Message);
        Assert.True(fault.Retryable);
        Assert.Equal("format", fault.Details["parameter"]);
    }

    [Test, DisplayName("fault details reject blank keys")]
    public void FaultDetailsRejectBlankKeys()
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [" "] = "value",
        };

        Assert.Throws<ArgumentException>(
            () => _ = new CommandFault("RCLI2001", "Invalid value.", details));
    }

    [Test, DisplayName("fault details reject null values")]
    public void FaultDetailsRejectNullValues()
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["parameter"] = null!,
        };

        Assert.Throws<ArgumentException>(
            () => _ = new CommandFault("RCLI2001", "Invalid value.", details));
    }

    [Test, DisplayName("output environment name is stable")]
    public void OutputEnvironmentNameIsStable()
    {
        Assert.Equal("RUNIC_COMMANDLINE_OUTPUT", CommandOutputClassifier.EnvironmentVariableName);
    }

    [Test, DisplayName("output mode names are stable")]
    public void OutputModeNamesAreStable()
    {
        Assert.SequenceEqual(
            [nameof(CommandOutputMode.Human), nameof(CommandOutputMode.Json)],
            Enum.GetNames<CommandOutputMode>());
        Assert.SequenceEqual(
            [
                nameof(CommandOutputModeSource.Default),
                nameof(CommandOutputModeSource.Environment),
                nameof(CommandOutputModeSource.ExplicitArgument),
            ],
            Enum.GetNames<CommandOutputModeSource>());
    }

    [Test, DisplayName("explicit human output wins over malformed environment")]
    public void ExplicitHumanWinsOverMalformedEnvironment()
    {
        CommandOutputClassification result = CommandOutputClassifier.Classify(
            CommandOutputMode.Human,
            "not-a-mode",
            CommandOutputMode.Json);

        AssertValidClassification(result, CommandOutputMode.Human, CommandOutputModeSource.ExplicitArgument);
        Assert.Null(result.InvalidEnvironmentValue);
    }

    [Test, DisplayName("explicit JSON output wins over environment")]
    public void ExplicitJsonWinsOverEnvironment()
    {
        CommandOutputClassification result = CommandOutputClassifier.Classify(
            CommandOutputMode.Json,
            "human");

        AssertValidClassification(result, CommandOutputMode.Json, CommandOutputModeSource.ExplicitArgument);
        Assert.Null(result.InvalidEnvironmentValue);
    }

    [Test, DisplayName("environment output classification is case insensitive")]
    public void EnvironmentClassificationIsCaseInsensitive()
    {
        CommandOutputClassification human = CommandOutputClassifier.Classify(null, "HuMaN");
        CommandOutputClassification json = CommandOutputClassifier.Classify(null, "JsOn");

        AssertValidClassification(human, CommandOutputMode.Human, CommandOutputModeSource.Environment);
        AssertValidClassification(json, CommandOutputMode.Json, CommandOutputModeSource.Environment);
    }

    [Test, DisplayName("missing environment uses requested default")]
    public void MissingEnvironmentUsesRequestedDefault()
    {
        CommandOutputClassification result = CommandOutputClassifier.Classify(
            null,
            null,
            CommandOutputMode.Json);

        AssertValidClassification(result, CommandOutputMode.Json, CommandOutputModeSource.Default);
    }

    [Test, DisplayName("empty environment uses requested default")]
    public void EmptyEnvironmentUsesRequestedDefault()
    {
        CommandOutputClassification result = CommandOutputClassifier.Classify(
            null,
            string.Empty,
            CommandOutputMode.Human);

        AssertValidClassification(result, CommandOutputMode.Human, CommandOutputModeSource.Default);
    }

    [Test, DisplayName("malformed environment is invalid")]
    public void MalformedEnvironmentIsInvalid()
    {
        const string invalidValue = "xml";
        CommandOutputClassification result = CommandOutputClassifier.Classify(null, invalidValue);

        Assert.False(result.IsValid);
        Assert.Null(result.Mode);
        Assert.Null(result.Source);
        Assert.Equal(invalidValue, result.InvalidEnvironmentValue);
    }

    [Test, DisplayName("whitespace environment is not silently accepted")]
    public void WhitespaceEnvironmentIsInvalid()
    {
        CommandOutputClassification result = CommandOutputClassifier.Classify(null, " ");

        Assert.False(result.IsValid);
        Assert.Equal(" ", result.InvalidEnvironmentValue);
    }

    [Test, DisplayName("unknown explicit output mode is rejected")]
    public void UnknownExplicitOutputModeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CommandOutputClassifier.Classify((CommandOutputMode)int.MaxValue, "json"));
    }

    [Test, DisplayName("unknown default output mode is rejected")]
    public void UnknownDefaultOutputModeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CommandOutputClassifier.Classify(null, null, (CommandOutputMode)int.MaxValue));
    }

    [Test, DisplayName("higher precedence sources ignore an unknown default")]
    public void HigherPrecedenceSourcesIgnoreUnknownDefault()
    {
        CommandOutputMode invalidDefault = (CommandOutputMode)int.MaxValue;

        CommandOutputClassification explicitResult = CommandOutputClassifier.Classify(
            CommandOutputMode.Json,
            "malformed",
            invalidDefault);
        CommandOutputClassification environmentResult = CommandOutputClassifier.Classify(
            null,
            "human",
            invalidDefault);

        AssertValidClassification(
            explicitResult,
            CommandOutputMode.Json,
            CommandOutputModeSource.ExplicitArgument);
        AssertValidClassification(
            environmentResult,
            CommandOutputMode.Human,
            CommandOutputModeSource.Environment);
    }

    private static void AssertExitCode(CommandExitCategory category, int declaredCode, int expectedCode)
    {
        Assert.Equal(expectedCode, declaredCode);
        Assert.Equal(expectedCode, CommandExitCodes.GetDefault(category));
    }

    private static void AssertValidClassification(
        CommandOutputClassification result,
        CommandOutputMode expectedMode,
        CommandOutputModeSource expectedSource)
    {
        Assert.True(result.IsValid);
        Assert.Equal(expectedMode, result.Mode);
        Assert.Equal(expectedSource, result.Source);
        Assert.Null(result.InvalidEnvironmentValue);
    }

    private static CommandFault CreateFault()
    {
        CommandFault fault = new("RCLI3001", "The command failed.");
        Assert.Equal(0, fault.Details.Count);
        Assert.False(fault.Retryable);
        return fault;
    }

    private static CommandDiagnostic CreateDiagnostic(CommandDiagnosticSeverity severity) => new(
        "RCLI2001",
        "invalid-value",
        "The value is invalid.",
        CommandDiagnosticPhase.Execution,
        severity);
}
