using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Core.Launcher.Hosting.Processes;

namespace Core.Launcher;

/// <summary>
/// The window EasyAntiCheat's launcher showed on real Fortnite from 3.2: the build's own splash image and a progress bar.
/// Core doesn't run EasyAntiCheat, so this is only the picture, shown while the game starts (Core-Launcher #12). It closes
/// once the game has a window, when the game exits, or after <see cref="Timeout"/>.
/// </summary>
internal sealed class EacSplashWindow : Window
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MinimumShown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

    private readonly RunningGame _game;
    private readonly ProgressBar _progress;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _shown = Stopwatch.StartNew();

    /// <exception cref="IOException">The image can't be read.</exception>
    /// <exception cref="NotSupportedException">The file isn't an image.</exception>
    public EacSplashWindow(string image, RunningGame game)
    {
        _game = game;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(image, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();

        _progress = new ProgressBar
        {
            Height = 6,
            Minimum = 0,
            Maximum = 1,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        var content = new Grid { Width = bitmap.Width, Height = bitmap.Height };
        content.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Fill });
        content.Children.Add(_progress);

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Title = "Fortnite";
        Content = content;

        _timer = new DispatcherTimer(Tick, DispatcherPriority.Background, (_, _) => Update(), Dispatcher);
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Moves the bar toward the end of <see cref="Timeout"/>, and closes the window when the game is up.</summary>
    private void Update()
    {
        var elapsed = _shown.Elapsed;
        _progress.Value = Math.Min(elapsed / Timeout, 0.95);
        if (elapsed >= Timeout || _game.Exited.IsCompleted || (elapsed >= MinimumShown && HasWindow()))
        {
            Close();
        }
    }

    private bool HasWindow()
    {
        try
        {
            using var process = Process.GetProcessById(_game.Id);
            return process.MainWindowHandle != 0;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
