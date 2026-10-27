using System.Windows;
using System.Windows.Controls;

namespace Core.Launcher;

public partial class HomePage : UserControl
{
    public HomePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && Password.Password != vm.Password)
        {
            Password.Password = vm.Password;
        }
    }

    private void OnPlayerNameLostFocus(object sender, RoutedEventArgs e) => (DataContext as MainViewModel)?.SavePlayerNameCommand.Execute(null);

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.Password = Password.Password;
        }
    }

    private void OnPasswordLostFocus(object sender, RoutedEventArgs e) => (DataContext as MainViewModel)?.SavePasswordCommand.Execute(null);
}
