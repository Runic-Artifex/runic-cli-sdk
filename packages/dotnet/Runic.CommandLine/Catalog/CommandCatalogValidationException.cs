using System;
using System.Collections.Generic;
using System.Text;

namespace Runic.CommandLine;

/// <summary>Represents one deterministic command-catalog validation issue.</summary>
public sealed record CommandCatalogIssue(string Code, string Location, string Message);

/// <summary>Thrown when a catalog cannot be frozen because its definitions are invalid.</summary>
/// <remarks>The message lists the first issues with their codes and command paths; <see cref="Issues"/> holds all of them.</remarks>
public sealed class CommandCatalogValidationException : Exception
{
    private const int MaximumListedIssues = 20;

    internal CommandCatalogValidationException(IReadOnlyList<CommandCatalogIssue> issues)
        : base(CreateMessage(issues))
    {
        Issues = issues;
    }

    /// <summary>Gets issues in deterministic definition order.</summary>
    public IReadOnlyList<CommandCatalogIssue> Issues { get; }

    private static string CreateMessage(IReadOnlyList<CommandCatalogIssue> issues)
    {
        var message = new StringBuilder($"Command catalog validation failed with {issues.Count} issue(s):");
        for (int index = 0; index < issues.Count && index < MaximumListedIssues; index++)
        {
            CommandCatalogIssue issue = issues[index];
            message.Append('\n').Append(issue.Code).Append(" at '").Append(issue.Location).Append("': ").Append(issue.Message);
        }
        if (issues.Count > MaximumListedIssues) message.Append('\n').Append(issues.Count - MaximumListedIssues).Append(" more issue(s) are listed in Issues.");
        return message.ToString();
    }
}
