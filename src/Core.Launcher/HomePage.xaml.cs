using System.Windows;
using System.Windows.Controls;

namespace Core.Launcher;

public partial class HomePage : UserControl
{
    public HomePage()
    {
        InitializeComponent();
    }

    private void OnPlayerNameLostFocus(object sender, RoutedEventArgs e) => (DataContext as MainViewModel)?.SavePlayerNameCommand.Execute(null);
}
