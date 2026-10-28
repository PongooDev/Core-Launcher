using System.Windows;
using Microsoft.Win32;

namespace Core.Launcher;

public partial class SetupWindow : Window
{
    public SetupWindow(SetupViewModel model)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        DataContext = model;
        model.PickFolder = PickFolder;
        model.Finished += () => DialogResult = true;
        Loaded += (_, _) =>
        {
            if (model.Password.Length > 0)
            {
                SetupPassword.Password = model.Password;
            }
        };
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SetupViewModel vm)
        {
            vm.Password = SetupPassword.Password;
        }
    }

    private string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }
}
