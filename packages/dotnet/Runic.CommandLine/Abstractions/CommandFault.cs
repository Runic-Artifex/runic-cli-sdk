using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Runic.CommandLine;

/// <summary>
/// Describes a stable, consumer-safe command fault.
/// </summary>
/// <remarks>
/// Faults are suitable for a machine response. Messages and details must not
/// contain stack traces, exception type names, secrets, environment variables,
/// or internal absolute paths.
/// </remarks>
public sealed record CommandFault
{
    private static readonly IReadOnlyDictionary<string, string> EmptyDetails =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(0, StringComparer.Ordinal));

    /// <summary>
    /// Initializes a new stable command fault.
    /// </summary>
    /// <param name="code">A stable, non-empty machine-readable fault code.</param>
    /// <param name="message">A safe, non-empty presentation message.</param>
    /// <param name="details">Optional safe string details. The values are defensively copied.</param>
    /// <param name="retryable">Whether retrying the same logical operation may succeed.</param>
    /// <exception cref="ArgumentException">
    /// A required value or detail key is empty, or a detail value is <see langword="null"/>.
    /// </exception>
    public CommandFault(
        string code,
        string message,
        IReadOnlyDictionary<string, string>? details = null,
        bool retryable = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Code = code;
        Message = message;
        Details = CopyDetails(details);
        Retryable = retryable;
    }

    /// <summary>Gets the stable machine-readable fault code.</summary>
    public string Code { get; }

    /// <summary>Gets the safe presentation message.</summary>
    public string Message { get; }

    /// <summary>Gets the immutable view of safe fault details.</summary>
    public IReadOnlyDictionary<string, string> Details { get; }

    /// <summary>Gets a value indicating whether the operation may succeed when retried.</summary>
    public bool Retryable { get; }

    /// <summary>
    /// Gets an absolute <c>https</c> link to the documentation of this fault's code, or
    /// <see langword="null"/> when there is none.
    /// </summary>
    /// <remarks>
    /// JSON output writes the link as <c>helpUri</c> and human output writes a
    /// <c>Help for {Code}: {link}</c> line after the fault. The link is presented as given:
    /// unlike <see cref="Message"/> and <see cref="Details"/>, it is not checked for technical
    /// content, so it must be a fixed documentation address, never built from user input.
    /// Set it with an object initializer or a <see langword="with"/> expression.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The value is not an absolute <c>https</c> URI, carries user information, or exceeds
    /// 2,048 characters.
    /// </exception>
    public Uri? HelpUri
    {
        get => _helpUri;
        init => _helpUri = CommandHelpUri.Validate(value, nameof(HelpUri));
    }

    private readonly Uri? _helpUri;

    private static IReadOnlyDictionary<string, string> CopyDetails(
        IReadOnlyDictionary<string, string>? details)
    {
        if (details is null || details.Count == 0)
        {
            return EmptyDetails;
        }

        var copy = new Dictionary<string, string>(details.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> detail in details)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(detail.Key, nameof(details));
            if (detail.Value is null)
            {
                throw new ArgumentException("Fault detail values cannot be null.", nameof(details));
            }

            copy.Add(detail.Key, detail.Value);
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }
}

// Validates the HelpUri of faults and diagnostics. A help link is the command author's fixed
// documentation address, so presentation keeps it although messages redact URI-like text; it
// is still bounded, absolute https, without credentials, and written in its escaped ASCII form.
internal static class CommandHelpUri
{
    internal const int MaximumLength = 2_048;

    internal static Uri? Validate(Uri? value, string parameterName)
    {
        if (value is not null && !IsValid(value))
        {
            throw new ArgumentException(
                "A help URI must be an absolute https URI without user information of at most 2,048 characters.",
                parameterName);
        }

        return value;
    }

    internal static bool IsValid(Uri value) =>
        value.IsAbsoluteUri &&
        string.Equals(value.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
        value.Host.Length != 0 &&
        value.UserInfo.Length == 0 &&
        value.AbsoluteUri.Length <= MaximumLength;

    // Readers accept only printable ASCII, which is what writers produce.
    internal static bool TryParse(string text, out Uri? value)
    {
        value = null;
        if (text.Length is 0 or > MaximumLength)
        {
            return false;
        }

        foreach (char character in text)
        {
            if (character is < '!' or > '~')
            {
                return false;
            }
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? parsed) || !IsValid(parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }
}
