using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Core.Launcher.Hosting.Backend;

/// <summary>One of the player's Creative islands on the backend (GET /core/api/creative/islands).</summary>
/// <param name="Name">"Island 2", "Arctic Island 1", "The Block 2", "Frosty Fortress 1".</param>
/// <param name="Title">Its published title, or the one it was imported with; empty when it has none.</param>
/// <param name="Published">Its island code when it is published.</param>
/// <param name="Saved">It holds a save (an island built on); a slot without one is free for an import.</param>
/// <param name="BuildVersion">The build that saved it ("7.20"), or its CL when the backend doesn't know the version.</param>
/// <param name="LoadsOnBuild">Whether the selected build can load it: false for an island saved on a newer build.</param>
public sealed record IslandInfo(
    string PlotItemId,
    string TemplateId,
    int? IslandIndex,
    string Name,
    string Title,
    string? Published,
    bool Saved,
    long Size,
    DateTimeOffset? Updated,
    bool InUse,
    bool Free,
    string? BuildVersion,
    bool Imported,
    bool? LoadsOnBuild);

/// <summary>An island type's slots: how many islands of it an account has, its limit and how many can take an import.</summary>
public sealed record IslandSlots(string TemplateId, string Name, int Limit, int Islands, int Free, string? FirstVersion);

/// <summary>The player's islands and slots, and the largest file an import takes.</summary>
public sealed record IslandList(string DisplayName, IReadOnlyList<IslandInfo> Islands, IReadOnlyList<IslandSlots> Slots, long MaxFileBytes);

/// <summary>An exported island: the file name the backend suggests and the file's bytes (a .coreisland JSON file).</summary>
public sealed record IslandFile(string FileName, byte[] Contents);

/// <summary>An island made by an import: its new id, its slot's name ("Island 3"), its title and the build that saved it.</summary>
public sealed record ImportedIsland(string PlotItemId, string Name, string Title, string? BuildVersion);

/// <summary>A refusal or failure of an island request, in words for the player, with the backend's error code when it gave one.</summary>
public sealed class IslandException(string message, string? errorCode = null, int statusCode = 0) : Exception(message)
{
    public string? ErrorCode { get; } = errorCode;

    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Core-Backend's Creative island files (Core-Launcher #21): the player's islands, an island exported to a .coreisland
/// file to share it, and a file imported as a new island of the player's, in their first free slot of its type, so an
/// island built on one Core backend can be played on another. Each request carries the player's own token
/// (<see cref="BackendAuthClient.SignInAsync"/>); <c>build</c> is the selected build's CL, whose island types and build
/// range the backend applies (an island loads on the build that saved it or a newer one).
/// </summary>
public static class BackendIslands
{
    public const string ListPath = "/core/api/creative/islands";
    public const string ImportPath = "/core/api/creative/islands/import";
    public const string FileExtension = ".coreisland";
    public const string FileFormat = "core-island";

    /// <summary>The backend's own limit (its save's 16 MB, gzipped in base64, and the rest of the file), when the list didn't say.</summary>
    public const long DefaultMaxFileBytes = 22_522_540;

    /// <summary>An import uploads up to about 22 MB.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler { UseProxy = false }) { Timeout = RequestTimeout };

    public static string ListUrl(BackendEndpoint backend, int changelist) => $"{backend.Url}{ListPath}{BuildQuery(changelist)}";

    public static string ExportUrl(BackendEndpoint backend, string plotItemId) => $"{backend.Url}{ListPath}/{Uri.EscapeDataString(plotItemId)}/export";

    public static string ImportUrl(BackendEndpoint backend, int changelist) => $"{backend.Url}{ImportPath}{BuildQuery(changelist)}";

    private static string BuildQuery(int changelist) => changelist > 0 ? $"?build={changelist.ToString(CultureInfo.InvariantCulture)}" : "";

    /// <summary>GET /core/api/creative/islands?build=: the player's islands and slots.</summary>
    public static async Task<IslandList> ListAsync(HttpClient http, BackendEndpoint backend, string token, int changelist, CancellationToken cancellation = default)
    {
        using var request = Request(HttpMethod.Get, ListUrl(backend, changelist), token);
        using var response = await SendAsync(http, request, backend, cancellation);
        var body = await response.Content.ReadAsStringAsync(cancellation);
        return ParseList(body) ?? throw new IslandException($"{backend} answered the island list with something else than a list. Is it a Core-Backend?");
    }

    /// <summary>GET /core/api/creative/islands/&lt;id&gt;/export: the island as a .coreisland file.</summary>
    public static async Task<IslandFile> ExportAsync(HttpClient http, BackendEndpoint backend, string token, string plotItemId, CancellationToken cancellation = default)
    {
        using var request = Request(HttpMethod.Get, ExportUrl(backend, plotItemId), token);
        using var response = await SendAsync(http, request, backend, cancellation);
        var contents = await response.Content.ReadAsByteArrayAsync(cancellation);
        if (Summarize(contents) is null)
        {
            throw new IslandException($"{backend} answered the export with something else than an island file.");
        }

        var suggested = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName;
        return new IslandFile(SafeFileName(suggested?.Trim('"')), contents);
    }

    /// <summary>
    /// POST /core/api/creative/islands/import?build=: the file made a new island of the player's. Throws
    /// <see cref="IslandException"/> with the backend's words when it refuses (a newer build's island, no free slot, a
    /// file that isn't an island's).
    /// </summary>
    public static async Task<ImportedIsland> ImportAsync(HttpClient http, BackendEndpoint backend, string token, byte[] contents, int changelist, CancellationToken cancellation = default)
    {
        using var request = Request(HttpMethod.Post, ImportUrl(backend, changelist), token);
        request.Content = new ByteArrayContent(contents);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await SendAsync(http, request, backend, cancellation);
        var body = await response.Content.ReadAsStringAsync(cancellation);
        return ParseImported(body) ?? throw new IslandException($"{backend} imported the island but its answer couldn't be read; refresh to see it.");
    }

    /// <summary>
    /// What a file says before it is uploaded: (format check, island type, title, build version), or null when it isn't a
    /// Core island file. The backend checks the file again, whole.
    /// </summary>
    public static IslandFileSummary? Summarize(byte[] contents)
    {
        try
        {
            using var document = JsonDocument.Parse(contents);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || TextOf(root, "format") != FileFormat ||
                !root.TryGetProperty("island", out var island) || island.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("save", out var save) || save.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var build = root.TryGetProperty("build", out var b) && b.ValueKind == JsonValueKind.Object ? b : default;
            return new IslandFileSummary(
                TextOf(island, "type") ?? "",
                TextOf(island, "title") ?? "",
                build.ValueKind == JsonValueKind.Object ? TextOf(build, "version") ?? (TextOf(build, "changelist") is { } cl ? $"CL-{cl}" : null) : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Checks a file before it is uploaded: throws <see cref="IslandException"/> when it is too large or isn't a Core island file.</summary>
    public static IslandFileSummary CheckFile(byte[] contents, long maxFileBytes)
    {
        if (contents.Length == 0)
        {
            throw new IslandException("That file is empty.");
        }

        if (contents.LongLength > maxFileBytes)
        {
            throw new IslandException($"That file is {FormatBytes(contents.LongLength)}: an island file can't be more than {FormatBytes(maxFileBytes)}.");
        }

        return Summarize(contents) ?? throw new IslandException($"That isn't a Core island file ({FileExtension}). Export one from the Islands page or the admin panel.");
    }

    /// <summary>{ displayName, islands: [...], slots: [...], maxFileBytes }; null for anything else.</summary>
    public static IslandList? ParseList(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("islands", out var islands) || islands.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var list = islands.EnumerateArray().Where(island => island.ValueKind == JsonValueKind.Object && TextOf(island, "plotItemId") is not null).Select(island => new IslandInfo(
                TextOf(island, "plotItemId")!,
                TextOf(island, "templateId") ?? "",
                IntOf(island, "islandIndex"),
                TextOf(island, "name") ?? "Island",
                TextOf(island, "title") ?? "",
                TextOf(island, "published"),
                BoolOf(island, "saved") ?? false,
                LongOf(island, "size") ?? 0,
                DateTimeOffset.TryParse(TextOf(island, "updated"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var updated) ? updated : null,
                BoolOf(island, "inUse") ?? false,
                BoolOf(island, "free") ?? false,
                island.TryGetProperty("build", out var build) && build.ValueKind == JsonValueKind.Object
                    ? TextOf(build, "version") ?? (TextOf(build, "changelist") is { } cl ? $"CL-{cl}" : null)
                    : null,
                island.TryGetProperty("imported", out var imported) && imported.ValueKind == JsonValueKind.Object,
                BoolOf(island, "loadsOnBuild"))).ToList();
            var slots = root.TryGetProperty("slots", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().Where(slot => slot.ValueKind == JsonValueKind.Object).Select(slot => new IslandSlots(
                    TextOf(slot, "templateId") ?? "",
                    TextOf(slot, "name") ?? "",
                    IntOf(slot, "limit") ?? 0,
                    IntOf(slot, "islands") ?? 0,
                    IntOf(slot, "free") ?? 0,
                    TextOf(slot, "firstVersion"))).ToList()
                : [];
            return new IslandList(TextOf(root, "displayName") ?? "", list, slots, LongOf(root, "maxFileBytes") is { } max && max > 0 ? max : DefaultMaxFileBytes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>{ imported: { plotItemId, name, title, build: { version } }, ... }; null for anything else.</summary>
    public static ImportedIsland? ParseImported(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("imported", out var imported) || imported.ValueKind != JsonValueKind.Object ||
                TextOf(imported, "plotItemId") is not { } plotItemId)
            {
                return null;
            }

            var version = imported.TryGetProperty("build", out var build) && build.ValueKind == JsonValueKind.Object ? TextOf(build, "version") : null;
            return new ImportedIsland(plotItemId, TextOf(imported, "name") ?? "Island", TextOf(imported, "title") ?? "", version);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The player's words for a refused request: the backend's own message for the island errors (a newer build's island,
    /// no free slot, a damaged file), and plain words for an older backend without the routes, a lost sign-in or a file
    /// past the limit.
    /// </summary>
    public static string Describe(HttpStatusCode status, string? errorCode, string? errorMessage, BackendEndpoint backend) => (status, errorCode) switch
    {
        (HttpStatusCode.NotFound, null or "errors.com.epicgames.common.not_found") =>
            $"{backend} doesn't have island export and import yet: it needs a newer Core-Backend.",
        (HttpStatusCode.Unauthorized, _) => "The backend didn't accept your sign-in. Check your player name and password on Home, then try again.",
        (HttpStatusCode.Forbidden, "errors.com.epicgames.common.missing_permission") => "Sign in with a player account (Home's player name) to manage your islands.",
        (HttpStatusCode.RequestEntityTooLarge, _) when string.IsNullOrWhiteSpace(errorMessage) => "That file is too large for an island.",
        _ when !string.IsNullOrWhiteSpace(errorMessage) => errorMessage!,
        _ => $"{backend} answered HTTP {(int)status}.",
    };

    /// <summary>A file name the save dialog can use: the backend's suggestion without path or invalid characters, ending in .coreisland.</summary>
    public static string SafeFileName(string? suggested)
    {
        var name = Path.GetFileName(suggested ?? "");
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '-');
        }

        name = name.Trim().Trim('.');
        if (name.Length == 0)
        {
            name = "Island";
        }

        return name.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase) ? name : name + FileExtension;
    }

    /// <summary>"255 B", "12 KB", "3.4 MB".</summary>
    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1048576.0:0.0} MB"),
        >= 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0} KB"),
        _ => $"{bytes} B",
    };

    private static HttpRequestMessage Request(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("bearer", token);
        return request;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, BackendEndpoint backend, CancellationToken cancellation)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellation);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException || (exception is TaskCanceledException && !cancellation.IsCancellationRequested))
        {
            throw new IslandException($"Couldn't reach {backend}: {exception.Message}");
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellation);
            var (code, message) = BackendAuthClient.ParseError(body);
            throw new IslandException(Describe(response.StatusCode, code, message, backend), code, (int)response.StatusCode);
        }
    }

    private static string? TextOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? IntOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static long? LongOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static bool? BoolOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        } : null;
}

/// <summary>What an island file says of itself: its island type, its title and the build that saved it.</summary>
public sealed record IslandFileSummary(string Type, string Title, string? BuildVersion);
