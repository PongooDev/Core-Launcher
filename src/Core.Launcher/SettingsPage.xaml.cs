using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Core.Launcher;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is SettingsViewModel model)
            {
                model.PickFolder = PickFolder;
            }
        };
    }

    private string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FolderName : null;
    }
}
