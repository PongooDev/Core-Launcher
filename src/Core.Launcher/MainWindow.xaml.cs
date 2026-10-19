using System.ComponentModel;
using System.Windows;

namespace Core.Launcher;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private bool _closing;

    public MainWindow(MainViewModel model)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        _model = model;
        DataContext = model;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closing)
        {
            return;
        }

        e.Cancel = true;
        if (_model.Host.GamesRunning &&
            MessageBox.Show(this, "Closing the launcher stops the game, the servers and the backend. Close anyway?", "Core Launcher",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

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
