using System.Net;
using System.Text;
using Core.Launcher.Hosting;
using Core.Launcher.Hosting.Backend;
using Core.Launcher.Hosting.Builds;
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
        Assert.Equal("Core", LaunchArguments.Password);
    }

    [Fact]
    public async Task GetAuthRequirementsDetectsPasswordRequiredWhenAccountHasPassword()
    {
        using var http = new HttpClient(new AuthMockHandler
        {
            RequirementsResponse = """{"name":"Core-Backend","version":"1.0.0","requirePassword":false,"passwordRequired":true,"accountExists":true,"hasPassword":true}"""
        });

        var endpoint = BackendEndpoint.Local(5595);
        var reqs = await BackendAuthClient.GetAuthRequirementsAsync(http, endpoint, "SecurePlayer", TestContext.Current.CancellationToken);

        Assert.NotNull(reqs);
        Assert.False(reqs.RequirePassword);
        Assert.True(reqs.PasswordRequired);
        Assert.True(reqs.AccountExists);
        Assert.True(reqs.HasPassword);
    }

    [Fact]
    public async Task GetAuthRequirementsDetectsPasswordOptionalForPasswordlessAccount()
    {
        using var http = new HttpClient(new AuthMockHandler
        {
            RequirementsResponse = """{"name":"Core-Backend","version":"1.0.0","requirePassword":false,"passwordRequired":false,"accountExists":true,"hasPassword":false}"""
        });

        var endpoint = BackendEndpoint.Local(5595);
        var reqs = await BackendAuthClient.GetAuthRequirementsAsync(http, endpoint, "LegacyPlayer", TestContext.Current.CancellationToken);

        Assert.NotNull(reqs);
        Assert.False(reqs.RequirePassword);
        Assert.False(reqs.PasswordRequired);
        Assert.True(reqs.AccountExists);
        Assert.False(reqs.HasPassword);
    }

    [Fact]
    public async Task GetAuthRequirementsEnforcesPasswordWhenServerWideRequirementIsTrue()
    {
        using var http = new HttpClient(new AuthMockHandler
        {
            RequirementsResponse = """{"name":"Core-Backend","version":"1.0.0","requirePassword":true,"passwordRequired":false,"accountExists":false,"hasPassword":false}"""
        });

        var endpoint = BackendEndpoint.Local(5595);
        var reqs = await BackendAuthClient.GetAuthRequirementsAsync(http, endpoint, "NewPlayer", TestContext.Current.CancellationToken);

        Assert.NotNull(reqs);
        Assert.True(reqs.RequirePassword);
        Assert.True(reqs.PasswordRequired);
    }

    [Fact]
    public async Task GetAuthRequirementsReturnsDefaultFalseOnLegacyBackend()
    {
        using var http = new HttpClient(new AuthMockHandler
        {
            RequirementsResponse = """{"name":"Core-Backend","version":"1.0.0"}"""
        });

        var endpoint = BackendEndpoint.Local(5595);
        var reqs = await BackendAuthClient.GetAuthRequirementsAsync(http, endpoint, "Player", TestContext.Current.CancellationToken);

        Assert.NotNull(reqs);
        Assert.False(reqs.RequirePassword);
        Assert.False(reqs.PasswordRequired);
    }

    [Fact]
    public void LaunchArgumentsFallsBackToCoreWhenPasswordEmpty()
    {
        var build = KnownBuilds.Create(@"F:\Builds\++Fortnite+Cert-CL-3532353\Fortnite", new BuildVersion(4, 16, 0, 3532353, "++Fortnite+Release-Cert"), allowUnsupported: false);
        var args = LaunchArguments.Client(build, 5595, "Player", null);
        Assert.Contains("-AUTH_PASSWORD=Core", args);

        var argsEmpty = LaunchArguments.Client(build, 5595, "Player", "");
        Assert.Contains("-AUTH_PASSWORD=Core", argsEmpty);

        var argsCustom = LaunchArguments.Client(build, 5595, "Player", "MySecretPass123!");
        Assert.Contains("-AUTH_PASSWORD=MySecretPass123!", argsCustom);
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
        Assert.Equal("This account is banned", result.ErrorMessage);
    }

    [Fact]
    public async Task AuthenticateShowsTheBackendsBanMessageWithItsEnd()
    {
        const string message = "This PC is banned until 2026-11-21 12:00 UTC: aimbot";
        using var http = new HttpClient(new AuthMockHandler
        {
            TokenStatusCode = HttpStatusCode.Forbidden,
            TokenResponse = $$"""{"errorCode":"errors.com.epicgames.account.account_not_active","errorMessage":"{{message}}"}"""
        });

        var result = await BackendAuthClient.AuthenticateAsync(http, BackendEndpoint.Local(5595), "Cheater", "Password123!", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(message, result.ErrorMessage);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task AuthenticateFallsBackToTheOldBanTextWithoutAMessage()
    {
        using var http = new HttpClient(new AuthMockHandler { TokenStatusCode = HttpStatusCode.Forbidden, TokenResponse = "{}" });

        var result = await BackendAuthClient.AuthenticateAsync(http, BackendEndpoint.Local(5595), "BannedPlayer", "Password123!", TestContext.Current.CancellationToken);

        Assert.Contains("banned by the server administrator", result.ErrorMessage);
    }

    [Fact]
    public void HardwareIdIsAPrefixedSha256OfTheMachineGuidNeverTheGuid()
    {
        const string guid = "3F2504E0-4F89-11D3-9A0C-0305E82C3301";
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("CoreLauncher-HWID:3f2504e0-4f89-11d3-9a0c-0305e82c3301")));

        Assert.Equal(expected, HardwareId.FromMachineGuid(guid));
        Assert.Equal(expected, HardwareId.FromMachineGuid("  3f2504e0-4f89-11d3-9a0c-0305e82c3301 "));
        Assert.Matches("^[0-9a-f]{64}$", HardwareId.FromMachineGuid(guid)!);
        Assert.DoesNotContain("3f2504e0", HardwareId.FromMachineGuid(guid)!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(HardwareId.FromMachineGuid(null));
        Assert.Null(HardwareId.FromMachineGuid("   "));
    }

    [Fact]
    public void HardwareIdOfThisPcIsStable()
    {
        var first = HardwareId.Current;
        Assert.Equal(first, HardwareId.Current);
        if (first is not null)
        {
            Assert.Matches("^[0-9a-f]{64}$", first);
        }
    }

    [Fact]
    public async Task SignInSendsTheHardwareIdHeader()
    {
        var handler = new AuthMockHandler { TokenResponse = """{"access_token":"abc","expires_in":28800}""" };
        using var http = new HttpClient(handler);
        var id = HardwareId.FromMachineGuid("test-guid")!;

        var result = await BackendAuthClient.SignInAsync(http, BackendEndpoint.Local(5595), "Player", "Password123!", TestContext.Current.CancellationToken, id);

        Assert.True(result.Result.Succeeded);
        Assert.Equal(id, handler.HardwareHeader);
    }

    [Fact]
    public async Task SignInSendsThisPcsHardwareIdByDefault()
    {
        var handler = new AuthMockHandler { TokenResponse = """{"access_token":"abc","expires_in":28800}""" };
        using var http = new HttpClient(handler);

        await BackendAuthClient.AuthenticateAsync(http, BackendEndpoint.Local(5595), "Player", "Password123!", TestContext.Current.CancellationToken);

        Assert.Equal(HardwareId.Current, handler.HardwareHeader);
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
        public string RequirementsResponse { get; init; } = """{"name":"Core-Backend","version":"1.0.0","requirePassword":false,"passwordRequired":true,"accountExists":true,"hasPassword":true}""";
        public HttpStatusCode TokenStatusCode { get; init; } = HttpStatusCode.OK;
        public string TokenResponse { get; init; } = "{}";
        public string? HardwareHeader { get; private set; }

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

            if (path is BackendAuthClient.StatusPath or BackendAuthClient.AuthRequirementsPath)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(RequirementsResponse, Encoding.UTF8, "application/json")
                });
            }
            if (path == BackendAuthClient.OAuthTokenPath)
            {
                HardwareHeader = request.Headers.TryGetValues(HardwareId.HeaderName, out var values) ? values.Single() : null;
                return Task.FromResult(new HttpResponseMessage(TokenStatusCode)
                {
                    Content = new StringContent(TokenResponse, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
