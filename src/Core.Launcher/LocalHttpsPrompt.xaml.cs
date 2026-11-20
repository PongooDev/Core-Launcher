using System.IO;
using System.Security.Cryptography;
using System.Windows;
using Core.Launcher.Hosting;
using Core.Launcher.Hosting.Backend;

namespace Core.Launcher;

public partial class LocalHttpsPrompt : Window
{
    private readonly LauncherHost _host;

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

    private async void OnEnable(object sender, RoutedEventArgs e)
    {
        Enable.IsEnabled = false;
        Error.Text = "";
        try
        {
            if (await _host.TrustLocalHttpsAsync())
            {
                DialogResult = true;
                return;
            }

            Error.Text = "Windows didn't add the certificate (the prompt was declined), so local HTTPS still isn't trusted.";
        }
        catch (Exception exception) when (exception is CryptographicException or InvalidOperationException or IOException)
        {
            Error.Text = $"Couldn't trust it ({exception.Message}).";
        }

        Enable.IsEnabled = true;
    }
}
