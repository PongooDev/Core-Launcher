using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Launcher.Hosting;
using Core.Launcher.Hosting.Backend;
using Core.Launcher.Hosting.Builds;
using Core.Launcher.Hosting.Processes;

namespace Core.Launcher;

/// <summary>
/// The Islands page (Core-Launcher #21): the player's Creative islands on the backend they play on, each exported to a
/// .coreisland file to share it, and a file imported as a new island in their first free slot of its type, so an island
/// built on one Core backend can be played on another. It signs in as Home's player (the same login as Play) and goes
/// by the selected build: an island loads on the build that saved it or a newer one.
/// </summary>
public sealed partial class IslandsViewModel(LauncherHost host, Action<string, string> toast) : ObservableObject
{
    private readonly HttpClient _http = BackendIslands.CreateHttpClient();
    private string? _sessionKey;
    private string? _token;
    private DateTimeOffset? _tokenExpires;
    private long _maxFileBytes = BackendIslands.DefaultMaxFileBytes;

    public ObservableCollection<IslandRowViewModel> Islands { get; } = [];

    /// <summary>Picks where to save an export: suggested file name → path, or null when cancelled.</summary>
    public Func<string, string?>? PickSaveFile { get; set; }

    /// <summary>Picks an island file to import: path, or null when cancelled.</summary>
    public Func<string?>? PickOpenFile { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Idle))]
    public partial bool Loading { get; set; }

    public bool Idle => !Loading;

    /// <summary>"Your islands on this PC's backend, as Player, for 7.20."</summary>
    [ObservableProperty]
    public partial string Description { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; set; } = "";

    public bool HasError => Error.Length > 0;

    /// <summary>"Island: 3 of 4 free · Arctic Island: 4 of 4 free · ...".</summary>
    [ObservableProperty]
    public partial string SlotsText { get; set; } = "";

    /// <summary>Shown instead of the list: no islands yet, or nothing to show until it loads.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyText))]
    public partial string EmptyText { get; set; } = "";

    public bool HasEmptyText => EmptyText.Length > 0;

    /// <summary>A note about the selected build: none for 7.00 and later.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBuildNote))]
    public partial string BuildNote { get; set; } = "";

    public bool HasBuildNote => BuildNote.Length > 0;

    private int Changelist => host.Settings.FindSelectedBuild()?.Changelist ?? 0;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (Loading)
        {
            return;
        }

        Loading = true;
        Error = "";
        try
        {
            await LoadAsync();
        }
        catch (IslandException exception)
        {
            // Nothing from an earlier read stays up as if it were this backend's or this player's.
            Islands.Clear();
            SlotsText = "";
            EmptyText = "";
            Description = "";
            Fail(exception.Message, toastIt: false);
        }
        finally
        {
            Loading = false;
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (Loading || PickOpenFile?.Invoke() is not { } path)
        {
            return;
        }

        Loading = true;
        Error = "";
        try
        {
            var length = new FileInfo(path).Length;
            if (length > _maxFileBytes)
            {
                throw new IslandException($"{Path.GetFileName(path)} is {BackendIslands.FormatBytes(length)}: an island file can't be more than {BackendIslands.FormatBytes(_maxFileBytes)}.");
            }

            var contents = await File.ReadAllBytesAsync(path);
            var summary = BackendIslands.CheckFile(contents, _maxFileBytes);
            var (backend, token) = await SignInAsync();
            var imported = await WithSignInAsync(token, current => BackendIslands.ImportAsync(_http, backend, current, contents, Changelist));
            var title = imported.Title.Length > 0 ? imported.Title : summary.Title.Length > 0 ? summary.Title : Path.GetFileNameWithoutExtension(path);
            toast($"Imported \"{title}\" as {imported.Name}{(imported.BuildVersion is { } version ? $" (saved on {version})" : "")}", "GoodBrush");
            host.Log.Write($"Islands: imported {Path.GetFileName(path)} as {imported.Name} ({imported.PlotItemId})");
            await LoadAsync();
        }
        catch (IslandException exception)
        {
            Fail(exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Fail($"Couldn't read {Path.GetFileName(path)}: {exception.Message}");
        }
        finally
        {
            Loading = false;
        }
    }

    internal async Task ExportAsync(IslandRowViewModel row)
    {
        if (Loading)
        {
            return;
        }

        var suggested = BackendIslands.SafeFileName(row.Title.Length > 0 ? $"{row.Name} - {row.Title}" : row.Name);
        if (PickSaveFile?.Invoke(suggested) is not { } path)
        {
            return;
        }

        Loading = true;
        Error = "";
        try
        {
            var (backend, token) = await SignInAsync();
            var file = await WithSignInAsync(token, current => BackendIslands.ExportAsync(_http, backend, current, row.PlotItemId));
            await File.WriteAllBytesAsync(path, file.Contents);
            toast($"{row.Name} exported to {Path.GetFileName(path)}", "GoodBrush");
            host.Log.Write($"Islands: exported {row.Name} ({row.PlotItemId}) to {path}");
        }
        catch (IslandException exception)
        {
            Fail(exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Fail($"Couldn't save {Path.GetFileName(path)}: {exception.Message}");
        }
        finally
        {
            Loading = false;
        }
    }

    private async Task LoadAsync()
    {
        var build = host.Settings.FindSelectedBuild();
        BuildNote = build is null ? "Add your Fortnite build to see which islands it can load."
            : !KnownBuilds.HasCreative(build.Changelist) ? $"{build.Name} has no Creative: islands are played on 7.00 and later. You can still export your islands; importing needs a 7.00 or later build selected."
            : "";
        var (backend, token) = await SignInAsync();
        var list = await WithSignInAsync(token, current => BackendIslands.ListAsync(_http, backend, current, Changelist));
        _maxFileBytes = list.MaxFileBytes;
        Description = $"{(host.Settings.Backend.UseRemote ? host.BackendDescription : "Your own backend")} · {list.DisplayName}{(build is not null && KnownBuilds.HasCreative(build.Changelist) ? $" · {build.Name}" : "")}";
        SlotsText = string.Join(" · ", list.Slots.Select(slot => $"{slot.Name}: {slot.Free} of {slot.Limit} free"));
        Islands.Clear();
        foreach (var island in list.Islands)
        {
            Islands.Add(new IslandRowViewModel(island, ExportAsync));
        }

        EmptyText = list.Islands.Count > 0 ? ""
            : "No Creative islands yet. An account gets its islands the first time it opens Creative on 7.00 or later; an import gives them too, when the account may play Creative.";
    }

    /// <summary>The backend and the player's token, signing in again only when the backend, login or password changed or the token ran out.</summary>
    private async Task<(BackendEndpoint Backend, string Token)> SignInAsync(bool fresh = false)
    {
        var backend = host.PlayerBackend ?? throw new IslandException(host.Settings.Backend.UseRemote
            ? $"{host.BackendDescription} isn't reachable yet."
            : "Core-Backend isn't running yet. It starts with the launcher; try again in a moment.");
        var login = host.Settings.PlayerName.Trim();
        if (login.Length == 0)
        {
            throw new IslandException("Enter your player name on Home first: the Islands page signs in as you.");
        }

        var password = host.Settings.Password.Trim();
        if (password.Length == 0)
        {
            password = LaunchArguments.Password;
        }

        var key = $"{backend.Url}|{login}|{password}";
        if (!fresh && key == _sessionKey && _token is not null && (_tokenExpires is null || _tokenExpires > DateTimeOffset.UtcNow.AddMinutes(1)))
        {
            return (backend, _token);
        }

        var result = await BackendAuthClient.SignInAsync(_http, backend, login, password);
        if (!result.Result.Succeeded || result.AccessToken is null)
        {
            throw new IslandException(result.Result.ErrorMessage ?? $"Couldn't sign in to {backend} as {login}.");
        }

        (_sessionKey, _token, _tokenExpires) = (key, result.AccessToken, result.ExpiresAt);
        return (backend, _token);
    }

    /// <summary>Runs a request, signing in once more when the backend no longer knows the token (it restarted or the token expired).</summary>
    private async Task<T> WithSignInAsync<T>(string token, Func<string, Task<T>> request)
    {
        try
        {
            return await request(token);
        }
        catch (IslandException exception) when (exception.StatusCode == 401)
        {
            var (_, fresh) = await SignInAsync(fresh: true);
            return await request(fresh);
        }
    }

    private void Fail(string message, bool toastIt = true)
    {
        Error = message;
        if (toastIt)
        {
            toast(message, "BadBrush");
        }

        host.Log.Write($"Islands: {message}");
    }
}

/// <summary>One island on the Islands page.</summary>
public sealed partial class IslandRowViewModel : ObservableObject
{
    private readonly Func<IslandRowViewModel, Task> _export;

    public IslandRowViewModel(IslandInfo island, Func<IslandRowViewModel, Task> export)
    {
        _export = export;
        PlotItemId = island.PlotItemId;
        Name = island.Name;
        Title = island.Title;
        CanExport = island.Saved;
        (State, StateBrush) = island switch
        {
            { InUse: true } => ("On a server", MainViewModel.Brush("BusyBrush")),
            { Saved: true } => ("Built", MainViewModel.Brush("GoodBrush")),
            _ => ("Free", MainViewModel.Brush("IdleBrush")),
        };

        var parts = new List<string>();
        if (island.Saved)
        {
            parts.Add(BackendIslands.FormatBytes(island.Size));
            if (island.Updated is { } updated)
            {
                parts.Add($"saved {Ago(updated)}");
            }

            if (island.BuildVersion is { } version)
            {
                parts.Add($"on {version}");
            }
        }
        else
        {
            parts.Add("Never built on: an import can go here");
        }

        if (island.Imported)
        {
            parts.Add("imported");
        }

        Details = string.Join(" · ", parts);
        Note = island.LoadsOnBuild == false ? "Saved on a newer build than the selected one: it won't load there."
            : island.Published is { } code ? $"Published as {code}"
            : "";
    }

    public string PlotItemId { get; }

    public string Name { get; }

    public string Title { get; }

    public bool HasTitle => Title.Length > 0;

    public string State { get; }

    public Brush StateBrush { get; }

    public string Details { get; }

    public string Note { get; }

    public bool HasNote => Note.Length > 0;

    public bool CanExport { get; }

    [RelayCommand]
    private Task Export() => _export(this);

    internal static string Ago(DateTimeOffset when)
    {
        var elapsed = DateTimeOffset.UtcNow - when;
        return elapsed.TotalDays >= 2 ? $"{(int)elapsed.TotalDays} days ago"
            : elapsed.TotalHours >= 2 ? $"{(int)elapsed.TotalHours} hours ago"
            : elapsed.TotalMinutes >= 2 ? $"{(int)elapsed.TotalMinutes} minutes ago"
            : "just now";
    }
}
