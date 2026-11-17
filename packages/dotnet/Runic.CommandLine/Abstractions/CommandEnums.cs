using Runic.CommandLine.Hosting;

namespace Runic.CommandLine;

// Explicit range checks for the framework's own enums. Enum.IsDefined and Enum.GetValues<T> read enum metadata through
// reflection at run time and instantiate that code for each enum type in a NativeAOT executable. Every enum below is
// contiguous from zero; a test checks these bounds against the declared members.
internal static class CommandEnums
{
    internal const CommandExitCategory LastExitCategory = CommandExitCategory.HostFailure;

    internal static bool IsDefined(CommandOutputMode value) => (uint)value <= (uint)CommandOutputMode.Json;

    internal static bool IsDefined(JsonOutputShape value) => (uint)value <= (uint)JsonOutputShape.Payload;

    internal static bool IsDefined(CommandDiagnosticPhase value) => (uint)value <= (uint)CommandDiagnosticPhase.Execution;

    internal static bool IsDefined(CommandDiagnosticSeverity value) => (uint)value <= (uint)CommandDiagnosticSeverity.Error;

    internal static bool IsDefined(CommandExitCategory value) => (uint)value <= (uint)LastExitCategory;

    internal static bool IsDefined(CommandPathKind value) => (uint)value <= (uint)CommandPathKind.Directory;

    internal static bool IsDefined(CommandOptionRepeatPolicy value) => (uint)value <= (uint)CommandOptionRepeatPolicy.Append;

    internal static bool IsDefined(CommandJsonEscaping value) => (uint)value <= (uint)CommandJsonEscaping.Ascii;

    internal static bool IsDefined(CommandSyntaxDialect value) => (uint)value <= (uint)CommandSyntaxDialect.WindowsSlash;

    internal static bool IsDefined(CommandHelpOrder value) => (uint)value <= (uint)CommandHelpOrder.Declaration;

    internal static bool IsDefined(EmptyInputFallback value) => (uint)value <= (uint)EmptyInputFallback.UserInterface;

    internal static bool IsDefined(HostedCommandLineDecisionKind value) => (uint)value <= (uint)HostedCommandLineDecisionKind.Completion;
}
