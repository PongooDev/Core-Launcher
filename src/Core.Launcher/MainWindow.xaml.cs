using System.ComponentModel;
using System.Windows;

namespace Core.Launcher;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private bool _closing;
    private bool _stopping;
    private bool _asked;

    public MainWindow(MainViewModel model)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        _model = model;
        DataContext = model;
        model.PickFolder = PickFolder;
        model.PromptLogin = (suggestion, validate) => LoginPrompt.Ask(this, suggestion, validate);
    }

    private string? PickFolder(string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    /// <summary>Closes and stops everything without the "close anyway?" question (the caller already asked).</summary>
    public void CloseWithoutAsking()
    {
        _asked = true;
        Close();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closing)
        {
            return;
        }

        e.Cancel = true;
        if (_stopping)
        {
            return;
        }

        if (!_asked && _model.Host.GamesRunning &&
            MessageBox.Show(this, "Closing the launcher stops the game, the servers and the backend. Close anyway?", "Core Launcher",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _stopping = true;
        IsEnabled = false;
        Title = "Core Launcher (stopping…)";
        try
        {
            await _model.Host.DisposeAsync();
        }
        finally
        {
            _closing = true;
            Close();
        }
    }
}
