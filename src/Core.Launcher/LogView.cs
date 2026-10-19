using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Core.Launcher.Hosting.Logging;

namespace Core.Launcher;

/// <summary>A read-only, selectable view of one log channel that follows new lines while scrolled to the bottom.</summary>
public sealed class LogView : ContentControl
{
    private const int MaxCharacters = 1_000_000;

    public static readonly DependencyProperty ChannelProperty =
        DependencyProperty.Register(nameof(Channel), typeof(LogChannel), typeof(LogView), new PropertyMetadata(null, (view, e) => ((LogView)view).Attach((LogChannel?)e.OldValue, (LogChannel?)e.NewValue)));

    private readonly TextBox _text;
    private readonly StringBuilder _pending = new();
    private readonly Lock _gate = new();
    private readonly DispatcherTimer _flush;

    public LogView()
    {
        _text = new TextBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Padding = new Thickness(10, 8, 10, 8),
            TextWrapping = TextWrapping.NoWrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Content = _text;
        _flush = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Flush(), Dispatcher);
        _flush.Start();
    }

    public LogChannel? Channel
    {
        get => (LogChannel?)GetValue(ChannelProperty);
        set => SetValue(ChannelProperty, value);
    }

    private void Attach(LogChannel? previous, LogChannel? channel)
    {
        if (previous is not null)
        {
            previous.LineWritten -= OnLine;
        }

        lock (_gate)
        {
            _pending.Clear();
        }

        _text.Text = channel is null ? "" : string.Join(Environment.NewLine, channel.Snapshot()) + Environment.NewLine;
        _text.ScrollToEnd();
        if (channel is not null)
        {
            channel.LineWritten += OnLine;
        }
    }

    private void OnLine(LogChannel channel, string line)
    {
        lock (_gate)
        {
            _pending.Append(line).Append(Environment.NewLine);
        }
    }

    private void Flush()
    {
        string text;
        lock (_gate)
        {
            if (_pending.Length == 0)
            {
                return;
            }

            text = _pending.ToString();
            _pending.Clear();
        }

        var following = _text.VerticalOffset + _text.ViewportHeight >= _text.ExtentHeight - 2;
        if (_text.Text.Length + text.Length > MaxCharacters)
        {
            var kept = _text.Text + text;
            var cut = kept.IndexOf('\n', kept.Length - MaxCharacters / 2);
            _text.Text = cut < 0 ? kept : kept[(cut + 1)..];
        }
        else
        {
            _text.AppendText(text);
        }

        if (following)
        {
            _text.ScrollToEnd();
        }
    }
}
