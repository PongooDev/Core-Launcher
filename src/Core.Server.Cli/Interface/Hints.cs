namespace Core.Server.Cli.Interface;

internal static class Hints
{
    public const string DefaultFooter = "ctrl+c stop servers   n new server   d remove server   c configure   a admin panel   l logs folder";

    public static string FormattedFooter()
    {
        return $"{Theme.Emphasis("ctrl+c", Theme.White)} {Theme.Paint("stop servers", Theme.Muted)}   " +
               $"{Theme.Emphasis("n", Theme.White)} {Theme.Paint("new server", Theme.Muted)}   " +
               $"{Theme.Emphasis("d", Theme.White)} {Theme.Paint("remove server", Theme.Muted)}   " +
               $"{Theme.Emphasis("c", Theme.White)} {Theme.Paint("configure", Theme.Muted)}   " +
               $"{Theme.Emphasis("a", Theme.White)} {Theme.Paint("admin panel", Theme.Muted)}   " +
               $"{Theme.Emphasis("l", Theme.White)} {Theme.Paint("logs folder", Theme.Muted)}";
    }
}

