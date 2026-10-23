using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Core.Launcher.Hosting.Backend;

/// <summary>What checking a friend's backend found: whether the game can use it, and one sentence saying so.</summary>
public sealed record RemoteBackendStatus(bool Reachable, string Message, string? Version = null);

/// <summary>
/// Checks a friend's Core-Backend with GET /core/api/status, the one route Core-Backend answers other PCs without a
/// login. An older Core-Backend without it answers its JSON 404, which still means a Core-Backend is there.
/// </summary>
public static class RemoteBackendCheck
{
    public const string StatusPath = "/core/api/status";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = Timeout }) { Timeout = Timeout };

    public static async Task<RemoteBackendStatus> CheckAsync(HttpClient http, BackendEndpoint endpoint, CancellationToken cancellation = default)
    {
        try
        {
            using var response = await http.GetAsync(endpoint.Url + StatusPath, cancellation);
            var body = await response.Content.ReadAsStringAsync(cancellation);
            if (response.StatusCode == HttpStatusCode.OK && Read(body, "name") == "Core-Backend")
            {
                var version = Read(body, "version");
                return new(true, $"Core-Backend {version} is answering at {endpoint}", version);
            }

            if (response.StatusCode == HttpStatusCode.NotFound && Read(body, "errorCode")?.StartsWith("errors.com.epicgames.", StringComparison.Ordinal) == true)
            {
                return new(true, $"An older Core-Backend is answering at {endpoint}");
            }

            return new(false, $"Something answered at {endpoint} (HTTP {(int)response.StatusCode}), but it isn't Core-Backend. Check the address and port.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            return new(false, Describe(exception, endpoint));
        }
    }

    private static string Describe(Exception exception, BackendEndpoint endpoint)
    {
        var socket = exception.InnerException as SocketException ?? exception.InnerException?.InnerException as SocketException;
        return socket?.SocketErrorCode switch
        {
            SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => $"Couldn't find {endpoint.Host}: check the address.",
            SocketError.ConnectionRefused => $"Nothing is accepting connections at {endpoint}. Your friend needs Core-Backend running with remote access on (admin panel → Friends), and the port right.",
            _ when exception is TaskCanceledException || socket?.SocketErrorCode == SocketError.TimedOut =>
                $"{endpoint} didn't answer within {Timeout.TotalSeconds:0} s. Check the address, that your friend turned on remote access, and (over the internet) that they forwarded TCP port {endpoint.Port} and allowed Node.js through Windows Firewall.",
            _ => $"Couldn't reach {endpoint}: {exception.Message}",
        };
    }

    private static string? Read(string json, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
