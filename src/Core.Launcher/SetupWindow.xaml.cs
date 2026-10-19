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
    }

    private string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }
}
