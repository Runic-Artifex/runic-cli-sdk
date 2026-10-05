using Runic.CommandLine.Tests;

if (args.Contains("--completion-help", StringComparer.Ordinal))
    return await TestRunner.RunAsync(CompletionHelpTests.All);

if (args.Contains("--authoring-localization", StringComparer.Ordinal))
    return await TestRunner.RunAsync(AuthoringMetadataTests.All, LocalizationTests.All);

return await TestRunner.RunAsync(
    CompletionHelpTests.All,
    AuthoringMetadataTests.All,
    LocalizationTests.All,
    ApplicationTests.All,
    GrammarCorpusTests.All,
    ParserAdversarialTests.All,
    OutputClassificationCorpusTests.All,
    CatalogTests.All,
    DiagnosticBoundaryTests.All,
    DispatcherTests.All,
    OutputTests.All,
    ProtocolCorpusTests.All,
    GeneratorTests.All);
