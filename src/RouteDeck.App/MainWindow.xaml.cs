using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace RouteDeck.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        FocusQueryCommand = new RelayCommand(() =>
        {
            QueryBox.Focus();
            Keyboard.Focus(QueryBox);
        });
        ApplicationTheme theme = ApplicationThemeManager.GetSystemTheme() is SystemTheme.Dark or SystemTheme.HCBlack
            ? ApplicationTheme.Dark
            : ApplicationTheme.Light;
        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, true);
        Loaded += (_, _) => SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
    }

    public IRelayCommand FocusQueryCommand { get; }
}
