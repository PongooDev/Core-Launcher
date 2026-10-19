using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Core.Launcher.Hosting.Components;

public sealed record ReleaseAsset(string Name, long Size, string? Sha256, string DownloadUrl);

public sealed record Release(string Tag, DateTimeOffset PublishedAt, IReadOnlyList<ReleaseAsset> Assets)
{
    public ReleaseAsset? Asset(string name) => Assets.FirstOrDefault(asset => asset.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The few GitHub REST calls the launcher makes, all unauthenticated (60 requests an hour).</summary>
public sealed class GitHubClient
{
    private readonly HttpClient _http;

    public GitHubClient(HttpClient http)
    {
        _http = http;
    }

    public HttpClient Http => _http;

    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Core-Launcher", typeof(GitHubClient).Assembly.GetName().Version?.ToString(3) ?? "0"));
        return http;
    }

    /// <summary>The repository's latest release (drafts and pre-releases excluded by GitHub), or null when it has none.</summary>
    public async Task<Release?> LatestReleaseAsync(string repository, CancellationToken cancellation)
    {
        using var request = Api($"repos/{repository}/releases/latest", "application/vnd.github+json");
        using var response = await _http.SendAsync(request, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(cancellation));
    }

    /// <summary>The default branch's name and its head commit.</summary>
    public async Task<(string Branch, string Commit)> DefaultBranchHeadAsync(string repository, CancellationToken cancellation)
    {
        using var repositoryRequest = Api($"repos/{repository}", "application/vnd.github+json");
        using var repositoryResponse = await _http.SendAsync(repositoryRequest, cancellation);
        repositoryResponse.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await repositoryResponse.Content.ReadAsStringAsync(cancellation));
        var branch = document.RootElement.GetProperty("default_branch").GetString()!;

        using var commitRequest = Api($"repos/{repository}/commits/{Uri.EscapeDataString(branch)}", "application/vnd.github.sha");
        using var commitResponse = await _http.SendAsync(commitRequest, cancellation);
        commitResponse.EnsureSuccessStatusCode();
        return (branch, (await commitResponse.Content.ReadAsStringAsync(cancellation)).Trim());
    }

    /// <summary>The source archive (zip) of a tag or commit; it holds one top folder named after the repository and commit.</summary>
    public static string SourceArchiveUrl(string repository, string reference) => $"https://api.github.com/repos/{repository}/zipball/{Uri.EscapeDataString(reference)}";

    public static Release ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var list))
        {
            foreach (var asset in list.EnumerateArray())
            {
                var digest = asset.TryGetProperty("digest", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                assets.Add(new ReleaseAsset(
                    asset.GetProperty("name").GetString()!,
                    asset.GetProperty("size").GetInt64(),
                    digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null,
                    asset.GetProperty("browser_download_url").GetString()!));
            }
        }

        var published = root.TryGetProperty("published_at", out var date) && date.ValueKind == JsonValueKind.String
            ? date.GetDateTimeOffset()
            : DateTimeOffset.MinValue;
        return new Release(root.GetProperty("tag_name").GetString()!, published, assets);
    }

    /// <summary>Downloads a release asset, refusing it unless its size and (when GitHub gives one) SHA-256 match.</summary>
    public Task DownloadAssetAsync(ReleaseAsset asset, string destination, IProgress<string>? progress, CancellationToken cancellation) =>
        Downloads.FileAsync(_http, asset.DownloadUrl, destination, asset.Size, asset.Sha256, progress, cancellation);

    private static HttpRequestMessage Api(string path, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/{path}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        return request;
    }
}
