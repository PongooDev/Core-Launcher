using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Core.Launcher.Hosting.Dlls;

public sealed record ReleaseAsset(string Name, long Size, string? Sha256, string DownloadUrl);

public sealed record Release(string Tag, DateTimeOffset PublishedAt, IReadOnlyList<ReleaseAsset> Assets);

public sealed class ReleaseClient
{
    private readonly HttpClient _http;

    public ReleaseClient(HttpClient http)
    {
        _http = http;
    }

    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Core-Launcher", "0.1"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>The repository's latest release (drafts and pre-releases excluded by GitHub), or null when it has none.</summary>
    public async Task<Release?> LatestAsync(string repository, CancellationToken cancellation)
    {
        using var response = await _http.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(cancellation));
    }

    public static Release Parse(string json)
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

    /// <summary>Downloads an asset to <paramref name="destination"/>, refusing it unless its size and (when GitHub gives one) SHA-256 match.</summary>
    public async Task DownloadAsync(ReleaseAsset asset, string destination, CancellationToken cancellation)
    {
        var temporary = destination + ".download";
        await using (var file = File.Create(temporary))
        {
            await using var stream = await _http.GetStreamAsync(asset.DownloadUrl, cancellation);
            await stream.CopyToAsync(file, cancellation);
        }

        var length = new FileInfo(temporary).Length;
        if (length != asset.Size)
        {
            File.Delete(temporary);
            throw new InvalidDataException($"{asset.Name}: downloaded {length} bytes, the release says {asset.Size}");
        }

        if (asset.Sha256 is not null)
        {
            var hash = await Sha256Async(temporary, cancellation);
            if (hash != asset.Sha256)
            {
                File.Delete(temporary);
                throw new InvalidDataException($"{asset.Name}: SHA-256 {hash} doesn't match the release's {asset.Sha256}");
            }
        }

        File.Move(temporary, destination, true);
    }

    public static async Task<string> Sha256Async(string path, CancellationToken cancellation = default)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellation));
    }
}
