using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Runic.CommandLine.Examples.ReportApp;

/// <summary>One inventory row shown in the window and listed by the command line.</summary>
public sealed record InventoryItem(string Name, int Quantity);

/// <summary>The outcome of an export.</summary>
public sealed record ExportResult(string Path, int Rows);

/// <summary>The use cases the WPF application already has. Neither front end owns the logic.</summary>
public interface IReportService
{
    /// <summary>Lists inventory items, optionally only those at or below a quantity.</summary>
    Task<ImmutableArray<InventoryItem>> ListItemsAsync(int? maxQuantity, CancellationToken cancellationToken);

    /// <summary>Writes the report as CSV. Refuses to replace an existing file unless asked.</summary>
    Task<ExportResult> ExportAsync(string path, bool overwrite, CancellationToken cancellationToken);
}

/// <summary>An in-memory implementation standing in for the application's real data access.</summary>
public sealed class ReportService : IReportService
{
    private static readonly ImmutableArray<InventoryItem> Items =
    [
        new("Iron ingot", 40), new("Oak plank", 7), new("Rune stone", 3), new("Silver thread", 12),
    ];

    /// <inheritdoc />
    public Task<ImmutableArray<InventoryItem>> ListItemsAsync(int? maxQuantity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(maxQuantity is { } max ? [.. Items.Where(item => item.Quantity <= max)] : Items);
    }

    /// <inheritdoc />
    public async Task<ExportResult> ExportAsync(string path, bool overwrite, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var items = await ListItemsAsync(null, cancellationToken).ConfigureAwait(false);
        var csv = new StringBuilder("name,quantity\n");
        foreach (var item in items)
            csv.Append(item.Name).Append(',').Append(item.Quantity.ToString(CultureInfo.InvariantCulture)).Append('\n');
        await using var stream = new FileStream(path, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(csv.ToString()), cancellationToken).ConfigureAwait(false);
        return new ExportResult(path, items.Length);
    }
}
