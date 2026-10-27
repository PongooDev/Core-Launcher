using System.Net;
using System.Text;
using Core.Launcher.Hosting;
using Core.Launcher.Hosting.Backend;
using Core.Launcher.Hosting.Processes;

namespace Core.Launcher.Tests;

public sealed class BackendAuthTests
{
    [Theory]
    [InlineData("1234567")] // 7 chars (< 8)
    [InlineData("short")]
    [InlineData("Core")]
    [InlineData("  1234567  ")] // trimmed length < 8
    public void PasswordShorterThanEightCharactersIsRejected(string password)
    {
        var error = PasswordValidation.Validate(password);
        Assert.NotNull(error);
        Assert.Contains("at least 8 characters", error);
    }

    [Fact]
    public void PasswordLongerThan128CharactersIsRejected()
    {
        var password = new string('a', 129);
        var error = PasswordValidation.Validate(password);
        Assert.NotNull(error);
        Assert.Contains("not exceed 128 characters", error);
    }

    [Theory]
    [InlineData("pass\"word123")]
    [InlineData("\"Secret123\"")]
    public void PasswordWithQuotesIsRejected(string password)
    {
        var error = PasswordValidation.Validate(password);
        Assert.NotNull(error);
        Assert.Contains("can't contain quotes", error);
    }

    [Theory]
    [InlineData("12345678")]
    [InlineData("SecretPassword123!")]
    [InlineData("  ValidPassword123  ")]
    public void ValidPasswordPasses(string password)
    {
        Assert.Null(PasswordValidation.Validate(password));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void OptionalPasswordCanBeEmpty(string? password)
    {
        Assert.Null(PasswordValidation.Validate(password, required: false));
        Assert.NotNull(PasswordValidation.Validate(password, required: true));
    }

    [Fact]
    public async Task PlayAsyncRejectsPasswordShorterThanEightCharacters()
    {
        using var folder = new TempFolder();
        var host = new LauncherHost(new LauncherPaths(folder.Combine("Launcher")));
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.PlayAsync("Player", "short"));
            Assert.Contains("at least 8 characters", exception.Message);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public void DedicatedServerCredentialsAreDefined()
    {
        Assert.Equal("ec684b8caddf4013849166972d1ca549", LaunchArguments.DedicatedServerClientId);
        Assert.Equal("e1f31c211f28413186262d37a13fc1d0", LaunchArguments.DedicatedServerClientSecret);
        Assert.True(LaunchArguments.Password.Length >= 8);
    }

    [Fact]
    public async Task PortalStatusReadsOnlineAndPolicies()
    {
        using var http = new HttpClient(new AuthMockHandler
        {
            StatusResponse = """{"status":"online","requirePassword":true,"allowRegistration":true}"""
        });

        var endpoint = BackendEndpoint.Local(5595);
        var status = await BackendAuthClient.CheckPortalStatusAsync(http, endpoint, TestContext.Current.CancellationToken);

        Assert.NotNull(status);
        Assert.True(status.Online);
        Assert.True(status.RequirePassword);
        Assert.True(status.AllowRegistration);
    }

    [Fact]
    public async Task AuthenticateSucceedsOnHttp200()
    {
        using var http = new HttpClient(new AuthMockHandler
        {
            TokenStatusCode = HttpStatusCode.OK,
            TokenResponse = """{"access_token":"dummy_token","token_type":"bearer"}"""
        });

        var endpoint = BackendEndpoint.Local(5595);
        var result = await BackendAuthClient.AuthenticateAsync(http, endpoint, "PlayerOne", "SecretPassword123!", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task AuthenticateHandlesHttp429RateLimit()
    {
        using var http = new HttpClient(new AuthMockHandler
        {
            TokenStatusCode = (HttpStatusCode)429,
            TokenResponse = """{"errorCode":"errors.com.epicgames.common.throttled","errorMessage":"Too many failed password attempts. Please wait a few minutes before trying again."}"""
        });

        var endpoint = BackendEndpoint.Local(5595);
        var result = await BackendAuthClient.AuthenticateAsync(http, endpoint, "PlayerOne", "WrongPassword123!", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(429, result.StatusCode);
        Assert.Contains("Too many", result.ErrorMessage);
        Assert.Contains("wait", result.ErrorMessage);
    }

    [Fact]
    public async Task AuthenticateHandlesLegacyPasswordlessAccount()
    {
        using var http = new HttpClient(new AuthMockHandler
        {
            TokenStatusCode = HttpStatusCode.BadRequest,
            TokenResponse = """{"errorCode":"errors.com.epicgames.account.invalid_account_credentials","errorMessage":"This account does not have a password configured and is admin-managed. Please request a Password Reset Link from your host."}"""
        });

        var endpoint = BackendEndpoint.Local(5595);
        var result = await BackendAuthClient.AuthenticateAsync(http, endpoint, "LegacyPlayer", "AnyPassword123!", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("before passwords were required", result.ErrorMessage);
        Assert.Contains("Password Reset Link", result.ErrorMessage);
    }

    [Fact]
    public async Task AuthenticateHandlesUnregisteredAccountPromptingWebPortal()
    {
        using var http = new HttpClient(new AuthMockHandler
        {
            TokenStatusCode = HttpStatusCode.BadRequest,
            TokenResponse = """{"errorCode":"errors.com.epicgames.account.invalid_account_credentials","errorMessage":"Your e-mail and/or password are incorrect, or the account does not exist. Please register on the web portal first."}"""
        });

        var endpoint = BackendEndpoint.Local(5595);
        var result = await BackendAuthClient.AuthenticateAsync(http, endpoint, "NewPlayer", "Password123!", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("register on the web portal first", result.ErrorMessage);
    }

    [Fact]
    public async Task AuthenticateHandlesBannedAccount()
    {
        using var http = new HttpClient(new AuthMockHandler
        {
            TokenStatusCode = HttpStatusCode.Forbidden,
            TokenResponse = """{"errorCode":"errors.com.epicgames.account.account_not_active","errorMessage":"This account is banned"}"""
        });

        var endpoint = BackendEndpoint.Local(5595);
        var result = await BackendAuthClient.AuthenticateAsync(http, endpoint, "BannedPlayer", "Password123!", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("banned by the server administrator", result.ErrorMessage);
    }

    [Fact]
    public async Task AuthenticateHandlesUnauthorizedDedicatedServerClient()
    {
        using var http = new HttpClient(new AuthMockHandler
        {
            TokenStatusCode = HttpStatusCode.Forbidden,
            TokenResponse = """{"errorCode":"errors.com.epicgames.account.oauth.unauthorized_client","errorMessage":"This IP address is not authorized to log in as a dedicated server account."}"""
        });

        var endpoint = BackendEndpoint.Local(5595);
        var result = await BackendAuthClient.AuthenticateAsync(http, endpoint, "CoreServer1", "CoreBackend123!", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("not authorized to host dedicated server accounts", result.ErrorMessage);
    }

    private sealed class AuthMockHandler : HttpMessageHandler
    {
        public string StatusResponse { get; init; } = """{"status":"online","requirePassword":true,"allowRegistration":true}""";
        public HttpStatusCode TokenStatusCode { get; init; } = HttpStatusCode.OK;
        public string TokenResponse { get; init; } = "{}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == BackendAuthClient.PortalStatusPath)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(StatusResponse, Encoding.UTF8, "application/json")
                });
            }

            if (path == BackendAuthClient.OAuthTokenPath)
            {
                return Task.FromResult(new HttpResponseMessage(TokenStatusCode)
                {
                    Content = new StringContent(TokenResponse, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
