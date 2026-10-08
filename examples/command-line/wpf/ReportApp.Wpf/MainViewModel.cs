using System.Collections.ObjectModel;
using System.Windows.Input;
using Runic.CommandLine.Examples.ReportApp;

namespace Runic.CommandLine.Examples.ReportApp.Wpf;

/// <summary>Calls the same <see cref="IReportService"/> the CLI handlers call.</summary>
public sealed class MainViewModel
{
    private readonly IReportService _reports;

    /// <summary>Initializes the view model over the application's service.</summary>
    public MainViewModel(IReportService reports)
    {
        _reports = reports;
        LoadCommand = new AsyncRelay(LoadAsync);
    }

    /// <summary>Gets the loaded items.</summary>
    public ObservableCollection<InventoryItem> Items { get; } = [];

    /// <summary>Gets the command that loads items from the service.</summary>
    public ICommand LoadCommand { get; }

    /// <summary>Loads items into <see cref="Items"/>.</summary>
    public async Task LoadAsync()
    {
        Items.Clear();
        foreach (var item in await _reports.ListItemsAsync(null, CancellationToken.None))
            Items.Add(item);
    }
}

// Not an MVVM template: just enough of a ViewModel to show a service call from the UI.
internal sealed class AsyncRelay(Func<Task> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;

    // ICommand.Execute is void, so this is an event-handler-style async void. Exceptions are caught because
    // an unobserved one would crash the process; a real application would report them to the user.
    public async void Execute(object? parameter)
    {
        try { await execute(); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            System.Windows.MessageBox.Show(exception.Message, "Inventory");
        }
    }
}
