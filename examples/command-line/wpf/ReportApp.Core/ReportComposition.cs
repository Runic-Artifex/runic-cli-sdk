namespace Runic.CommandLine.Examples.ReportApp;

/// <summary>
/// The single place that decides how the application's services are built. The WPF process and the
/// CLI process each call it, so they share code and configuration, not instances.
/// </summary>
public static class ReportComposition
{
    /// <summary>Creates the report service. A real application would also read shared configuration here.</summary>
    public static IReportService CreateReportService() => new ReportService();
}
