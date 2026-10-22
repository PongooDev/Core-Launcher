using System.Windows;

namespace Core.Launcher;

public partial class LoginPrompt : Window
{
    private readonly Func<string, string?> _validate;

    private LoginPrompt(string suggestion, Func<string, string?> validate)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        _validate = validate;
        Login.Text = suggestion;
        Loaded += (_, _) =>
        {
            Login.Focus();
            Login.SelectAll();
        };
    }

    /// <summary>The username to launch with, or null when cancelled.</summary>
    public static string? Ask(Window owner, string suggestion, Func<string, string?> validate)
    {
        var prompt = new LoginPrompt(suggestion, validate) { Owner = owner };
        return prompt.ShowDialog() == true ? prompt.Login.Text.Trim() : null;
    }

    private void OnLaunch(object sender, RoutedEventArgs e)
    {
        if (_validate(Login.Text) is { } problem)
        {
            Error.Text = problem;
            return;
        }

        DialogResult = true;
    }
}
