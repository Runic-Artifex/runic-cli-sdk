using Runic.CommandLine.Tests;

if (SignalTests.IsChildInvocation(args))
    return await SignalTests.RunChildAsync(args[1]);

if (args.Contains("--signals", StringComparer.Ordinal))
    return await TestRunner.RunAsync(SignalTests.All);

if (args.Contains("--completion-help", StringComparer.Ordinal))
    return await TestRunner.RunAsync(CompletionHelpTests.All, HelpLayoutTests.All);

if (args.Contains("--authoring-localization", StringComparer.Ordinal))
    return await TestRunner.RunAsync(AuthoringMetadataTests.All, LocalizationTests.All);

if (args.Contains("--result-contexts", StringComparer.Ordinal))
    return await TestRunner.RunAsync(ResultContextTests.All, GeneratorTests.All);

if (args.Contains("--error-causes", StringComparer.Ordinal))
    return await TestRunner.RunAsync(ErrorCauseTests.All);

if (args.Contains("--failure-data", StringComparer.Ordinal))
    return await TestRunner.RunAsync(FailureDataTests.All, OutputTests.All, ProtocolCorpusTests.All, ResultContextTests.All);

return await TestRunner.RunAsync(
    CompletionHelpTests.All,
    HelpLayoutTests.All,
    AuthoringMetadataTests.All,
    LocalizationTests.All,
    ApplicationTests.All,
    ResultContextTests.All,
    SignalTests.All,
    GrammarCorpusTests.All,
    ParserAdversarialTests.All,
    OutputClassificationCorpusTests.All,
    CatalogTests.All,
    DiagnosticBoundaryTests.All,
    DiagnosticCodeRangeTests.All,
    DispatcherTests.All,
    OutputTests.All,
    FailureDataTests.All,
    ProtocolCorpusTests.All,
    GeneratorTests.All,
    ErrorCauseTests.All);
