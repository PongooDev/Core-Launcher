using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Core.Launcher.Hosting.Processes;

namespace Core.Launcher.Hosting.Backend;

/// <summary>Status reported by Core-Backend's web portal (/api/portal/status).</summary>
public sealed record PortalStatus(bool Online, bool RequirePassword, bool AllowRegistration);

/// <summary>Authentication requirements reported by Core-Backend (/core/api/status?username=... or /core/api/auth-requirements?username=...).</summary>
public sealed record AuthRequirements(bool RequirePassword, bool PasswordRequired, bool AccountExists = false, bool HasPassword = false);
/// <summary>Result of pre-authenticating credentials against Core-Backend's OAuth endpoint.</summary>
public sealed record AuthResult(bool Succeeded, string? ErrorMessage = null, string? ErrorCode = null, int StatusCode = 200)
{
    public static AuthResult Success() => new(true);

    public static AuthResult Failure(string message, string? code = null, int statusCode = 400) =>
        new(false, message, code, statusCode);
}

/// <summary>
/// Client for Core-Backend's portal and authentication endpoints (/api/portal/status, /core/api/status, and /account/api/oauth/token).
/// Translates Epic-formatted errors and HTTP status codes (such as 429 and 400 legacy accounts) into user-friendly launcher messages.
/// </summary>
public static class BackendAuthClient
{
    public const string PortalStatusPath = "/api/portal/status";
    public const string StatusPath = "/core/api/status";
    public const string AuthRequirementsPath = "/core/api/auth-requirements";
    public const string OAuthTokenPath = "/account/api/oauth/token";

    /// <summary>
    /// Checks authentication requirements for a specific username (GET /core/api/status?username={username}).
    /// Returns null if the backend is offline or doesn't support requirement detection.
    /// </summary>
    public static async Task<AuthRequirements?> GetAuthRequirementsAsync(
        HttpClient http,
        BackendEndpoint endpoint,
        string username,
        CancellationToken cancellation = default)
    {
        try
        {
            var trimmedUser = (username ?? "").Trim();
            var url = $"{endpoint.Url}{StatusPath}?username={Uri.EscapeDataString(trimmedUser)}";
            using var response = await http.GetAsync(url, cancellation);
            if (response.IsSuccessStatusCode)
            {
                return await ParseRequirementsAsync(response, cancellation);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var altUrl = $"{endpoint.Url}{AuthRequirementsPath}?username={Uri.EscapeDataString(trimmedUser)}";
                using var altResponse = await http.GetAsync(altUrl, cancellation);
                if (altResponse.IsSuccessStatusCode)
                {
                    return await ParseRequirementsAsync(altResponse, cancellation);
                }
            }

            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static async Task<AuthRequirements?> ParseRequirementsAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var requirePassword = root.TryGetProperty("requirePassword", out var rp) && rp.ValueKind is JsonValueKind.True;
        var passwordRequired = root.TryGetProperty("passwordRequired", out var pr) && pr.ValueKind is JsonValueKind.True;
        var accountExists = root.TryGetProperty("accountExists", out var ae) && ae.ValueKind is JsonValueKind.True;
        var hasPassword = root.TryGetProperty("hasPassword", out var hp) && hp.ValueKind is JsonValueKind.True;

        if (requirePassword)
        {
            passwordRequired = true;
        }

        return new AuthRequirements(requirePassword, passwordRequired, accountExists, hasPassword);
    }
    /// <summary>Queries the portal status endpoint (GET /api/portal/status).</summary>
    public static async Task<PortalStatus?> CheckPortalStatusAsync(HttpClient http, BackendEndpoint endpoint, CancellationToken cancellation = default)
    {
        try
        {
            using var response = await http.GetAsync(endpoint.Url + PortalStatusPath, cancellation);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);
            var root = document.RootElement;
            var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : "";
            var requirePassword = root.TryGetProperty("requirePassword", out var rp) && rp.ValueKind is JsonValueKind.True;
            var allowRegistration = root.TryGetProperty("allowRegistration", out var ar) && ar.ValueKind is JsonValueKind.True;

            return new PortalStatus(string.Equals(status, "online", StringComparison.OrdinalIgnoreCase), requirePassword, allowRegistration);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Authenticates a player's credentials against Core-Backend's OAuth token endpoint.
    /// Returns an <see cref="AuthResult"/> indicating success or a friendly error message.
    /// </summary>
    public static async Task<AuthResult> AuthenticateAsync(
        HttpClient http,
        BackendEndpoint endpoint,
        string username,
        string password,
        CancellationToken cancellation = default)
    {
        var trimmedUser = (username ?? "").Trim();
        var trimmedPass = (password ?? "").Trim();

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = trimmedUser,
            ["password"] = trimmedPass,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url + OAuthTokenPath)
        {
            Content = new FormUrlEncodedContent(form)
        };

        // Standard client credentials for Fortnite / DedicatedServer
        var authBytes = Encoding.UTF8.GetBytes($"{LaunchArguments.DedicatedServerClientId}:{LaunchArguments.DedicatedServerClientSecret}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        try
        {
            using var response = await http.SendAsync(request, cancellation);
            var statusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                return AuthResult.Success();
            }

            var body = await response.Content.ReadAsStringAsync(cancellation);
            var (errorCode, errorMessage) = ParseError(body);

            if (response.StatusCode == (HttpStatusCode)429)
            {
                return AuthResult.Failure(
                    !string.IsNullOrWhiteSpace(errorMessage) ? errorMessage : "Too many attempts. Please wait 5 minutes before trying again.",
                    errorCode ?? "errors.com.epicgames.common.throttled",
                    statusCode);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                if (errorCode == "errors.com.epicgames.account.oauth.unauthorized_client" || (errorMessage?.Contains("dedicated server", StringComparison.OrdinalIgnoreCase) == true))
                {
                    return AuthResult.Failure(
                        "This machine is not authorized to host dedicated server accounts.",
                        errorCode,
                        statusCode);
                }

                return AuthResult.Failure(
                    "This account has been banned by the server administrator.",
                    errorCode,
                    statusCode);
            }

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                if (errorMessage?.Contains("does not have a password configured", StringComparison.OrdinalIgnoreCase) == true ||
                    errorMessage?.Contains("admin-managed", StringComparison.OrdinalIgnoreCase) == true ||
                    errorMessage?.Contains("reset link", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return AuthResult.Failure(
                        "This account was created before passwords were required. Please ask your server host for a Password Reset Link to secure your account.",
                        errorCode,
                        statusCode);
                }

                if (errorMessage?.Contains("register on the web portal", StringComparison.OrdinalIgnoreCase) == true ||
                    errorMessage?.Contains("does not exist", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return AuthResult.Failure(
                        errorMessage,
                        errorCode,
                        statusCode);
                }

                return AuthResult.Failure(
                    errorMessage ?? "Incorrect username or password. (Must be 8+ characters).",
                    errorCode,
                    statusCode);
            }

            return AuthResult.Failure(
                !string.IsNullOrWhiteSpace(errorMessage) ? errorMessage : $"Authentication failed (HTTP {statusCode}).",
                errorCode,
                statusCode);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Network failure or timeout: let the caller handle it (or allow launch if backend check was offline)
            return AuthResult.Failure($"Couldn't reach {endpoint}: {exception.Message}", null, 0);
        }
    }

    private static (string? ErrorCode, string? ErrorMessage) ParseError(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var code = root.TryGetProperty("errorCode", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var message = root.TryGetProperty("errorMessage", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            return (code, message);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
