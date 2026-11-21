using System.IO;
using System.Security.Cryptography;
using System.Windows;
using Core.Launcher.Hosting;
using Core.Launcher.Hosting.Backend;

namespace Core.Launcher;

public partial class LocalHttpsPrompt : Window
{
    private readonly LauncherHost _host;

    // Cancelled when the window closes, so "Not now", Escape or the close button during Enable's check never leads to
    // Windows' trust dialog afterwards (the owner's rule: no security change without the player's consent).
    private readonly CancellationTokenSource _closed = new();

    private LocalHttpsPrompt(LauncherHost host, LocalTrustStatus status)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        _host = host;
        Message.Text = status.Message;
    }

    /// <summary>Shows the prompt; true when the player asked not to be asked again.</summary>
    public static bool Ask(Window owner, LauncherHost host, LocalTrustStatus status)
    {
        var prompt = new LocalHttpsPrompt(host, status) { Owner = owner };
        prompt.ShowDialog();
        return prompt.DontAsk.IsChecked == true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed.Cancel();
        base.OnClosed(e);
    }

    private async void OnEnable(object sender, RoutedEventArgs e)
    {
        Enable.IsEnabled = false;
        Error.Text = "";
        try
        {
            var added = await _host.TrustLocalHttpsAsync(_closed.Token);
            if (_closed.IsCancellationRequested)
            {
                return;
            }

            if (added)
            {
                DialogResult = true;
                return;
            }

            Error.Text = "Windows didn't add the certificate (the prompt was declined), so local HTTPS still isn't trusted.";
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is CryptographicException or InvalidOperationException or IOException)
        {
            Error.Text = $"Couldn't trust it ({exception.Message}).";
        }

        Enable.IsEnabled = true;
    }
}
