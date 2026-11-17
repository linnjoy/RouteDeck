using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace RouteDeck.App.Views;

public partial class RoutesView : UserControl
{
    public RoutesView()
    {
        InitializeComponent();
        FocusFilterCommand = new RelayCommand(() => Keyboard.Focus(FilterBox));
    }

    public IRelayCommand FocusFilterCommand { get; }
}
