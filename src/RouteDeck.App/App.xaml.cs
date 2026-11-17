using System.Globalization;
using System.Windows;
using System.Windows.Data;
using RouteDeck.App.Services;
using RouteDeck.App.ViewModels;

namespace RouteDeck.App;

public partial class App : Application
{
    private ServiceConnection? _connection;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _connection = new ServiceConnection();
        var window = new MainWindow();
        var viewModel = new MainViewModel(_connection, new WindowDialogs(window), UiSettings.Load());
        window.DataContext = viewModel;
        MainWindow = window;
        window.Show();
        // "RouteDeck.exe 10.0.1.66:81" opens straight into a path lookup.
        string? query = e.Args.FirstOrDefault(a => !a.StartsWith('-'));
        viewModel.InitializeAsync(query).Forget(ex => viewModel.ShowError("启动失败", ex.Message));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _connection?.Dispose();
        base.OnExit(e);
    }
}

public static class TaskExtensions
{
    /// <summary>Fire-and-forget for UI code: failures are reported instead of crashing the process.</summary>
    public static void Forget(this Task task, Action<Exception> onError) =>
        _ = task.ContinueWith(
            t => Report(t.Exception!.GetBaseException(), onError),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    private static void Report(Exception ex, Action<Exception> onError)
    {
        if (Application.Current?.Dispatcher is not { } dispatcher) return;
        System.Windows.Threading.DispatcherOperation operation = dispatcher.BeginInvoke(() => onError(ex));
        _ = operation;
    }
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the value is non-null (and not an empty string).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the value is null.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
