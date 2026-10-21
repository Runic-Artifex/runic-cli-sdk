using System.Text.Json.Serialization;
using Runic.CommandLine;

internal static class TransformCommands
{
    [Command("transform", Description = "Convert a UTF-8 text file to upper case.", Examples = ["hello transform --source input.txt --destination output.txt", "hello transform --source input.txt --dry-run"])]
    [CommandResult("hello.transform/1", typeof(TransformJsonContext))]
    internal static async Task<CommandOutcome<TransformResult>> Transform(
        [Option("--source", "-s", MustExist = true, Description = "Input text file.")] FileInfo source,
        [Option("--destination", "-d", PathKind = CommandPathKind.File, Requires = ["source"], Description = "Output path.")] string? destination = null,
        [Option("--dry-run", ConflictsWith = ["overwrite"], Description = "Report the transformation without writing.")] bool dryRun = false,
        [Option("--overwrite", Requires = ["destination"], Description = "Explicitly permit replacing the destination.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        if (!dryRun && destination is null)
            return CommandOutcome.Failure<TransformResult>(CommandExitCategory.Usage, new("HELLO001", "Supply --destination PATH or --dry-run."));
        string text = (await File.ReadAllTextAsync(source.FullName, cancellationToken)).ToUpperInvariant();
        if (dryRun) return CommandOutcome.Success(new TransformResult("(dry run)", text.Length));
        try
        {
            // CreateNew prevents accidental replacement even if a file appears after validation.
            await using var stream = new FileStream(destination!, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(text.AsMemory(), cancellationToken);
        }
        catch (IOException)
        {
            // The invocation was valid; the file system refused it. That is a command failure, not a usage error.
            return CommandOutcome.Failure<TransformResult>(CommandExitCategory.CommandFailure, new("HELLO002", "Cannot create the destination; check its parent directory or explicitly use --overwrite."));
        }
        return CommandOutcome.Success(new TransformResult(destination!, text.Length));
    }
}

internal sealed record TransformResult(string Output, int Characters);

// Runic payloads use camelCase property names, like the runic.commandline/1 envelope around them; without this
// option System.Text.Json keeps the C# names ("Output") and the generator reports RCLI9044.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TransformResult))]
internal sealed partial class TransformJsonContext : JsonSerializerContext;
