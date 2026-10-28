using System.Diagnostics;
using System.Windows;
using Core.Launcher.Hosting.Backend;
using Core.Launcher.Hosting.Processes;

namespace Core.Launcher;

public partial class LoginPrompt : Window
{
    private readonly Func<string, string?> _validate;
    private readonly string? _registerUrl;

    private LoginPrompt(string suggestion, Func<string, string?> validate, string? registerUrl = null)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        _validate = validate;
        _registerUrl = registerUrl;
        RegisterButton.Visibility = string.IsNullOrWhiteSpace(_registerUrl) ? Visibility.Collapsed : Visibility.Visible;
        Login.Text = suggestion;
        Loaded += (_, _) =>
        {
            Login.Focus();
            Login.SelectAll();
        };
    }

    /// <summary>The username to launch with, or null when cancelled.</summary>
    public static (string Login, string? Password)? Ask(Window owner, string suggestion, Func<string, string?> validate, string? registerUrl = null)
    {
        var prompt = new LoginPrompt(suggestion, validate, registerUrl) { Owner = owner };
        return prompt.ShowDialog() == true
            ? (prompt.Login.Text.Trim(), string.IsNullOrWhiteSpace(prompt.Password.Password) ? null : prompt.Password.Password.Trim())
            : null;
    }

    private void OnRegister(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_registerUrl))
        {
            Process.Start(new ProcessStartInfo(_registerUrl) { UseShellExecute = true });
        }
    }

    private void OnLaunch(object sender, RoutedEventArgs e)
    {
        if (_validate(Login.Text) is { } problem)
        {
            Error.Text = problem;
            return;
        }

        var pass = Password.Password.Trim();
        if (!string.IsNullOrWhiteSpace(pass) &&
            !string.Equals(pass, LaunchArguments.Password, StringComparison.OrdinalIgnoreCase) &&
            PasswordValidation.Validate(pass) is { } passwordProblem)
        {
            Error.Text = passwordProblem;
            return;
        }

        DialogResult = true;
    }
}
