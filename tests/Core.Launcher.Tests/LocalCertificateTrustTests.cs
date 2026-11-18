using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Core.Launcher.Hosting.Backend;

namespace Core.Launcher.Tests;

/// <summary>
/// The opt-in trust of the local Core-Backend's https root (Core-Backend #377), against a store in memory: nothing here
/// touches a Windows certificate store.
/// </summary>
public sealed class LocalCertificateTrustTests
{
    // A root Core-Backend's src/tls/certificates.js made (public certificate only: that root's private key was dropped
    // when it signed its leaf, and never written anywhere). The launcher has to take exactly what the backend makes.
    private const string BackendRoot = """
        -----BEGIN CERTIFICATE-----
        MIIDcjCCAlqgAwIBAgIQHVhC1X+N7zZG5fDk3vQ3kjANBgkqhkiG9w0BAQsFADA9
        MSwwKgYDVQQDEyNDb3JlIGxvY2FsIGJhY2tlbmQgKDEyNy4wLjAuMSBvbmx5KTEN
        MAsGA1UEChMEQ29yZTAeFw0yNjExMTYwMzAxNTNaFw0zNjExMTcwMzAxNTNaMD0x
        LDAqBgNVBAMTI0NvcmUgbG9jYWwgYmFja2VuZCAoMTI3LjAuMC4xIG9ubHkpMQ0w
        CwYDVQQKEwRDb3JlMIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAqIN2
        9BSSDCJKrj5PLKUUqoL3HAcc8nydzWtzPMZGBABy/aYHclSTcN2lIKUdzK8PnRNv
        GfSP05HJLL/okgWNTMgrEciKYZc1L4kdC/kslpLusY9QYOKoJ5Fz/UNjNB3OEu/p
        8kU3RW8GtTFXTn6KZxK3tLngCb9BbMSU1r8aflxA8W3hj4UuWgBMFXIoeSMaq4P7
        KEzXug2XbMaw5RaanVxdE8qQ34t40iP/w9ACXl9JfhuXhKxdYMd7UnrGJujKcIlt
        aMcKrBMXJg0VfwhD6Js4j4lEK3ieDn1Pd9otoQPWHA87HQBl71xWVokI/QOZidI5
        R+SCZTdbUvuVt1fzVwIDAQABo24wbDASBgNVHRMBAf8ECDAGAQH/AgEAMA4GA1Ud
        DwEB/wQEAwIBBjAnBgNVHR4BAf8EHTAboBkwCocIfwAAAf////8wC4IJbG9jYWxo
        b3N0MB0GA1UdDgQWBBTHDtgStv5jPfCATfv+ofPay0aIeDANBgkqhkiG9w0BAQsF
        AAOCAQEAhVy+fFqf06vEWFBRDFVcA0ejp/Fzs5qk7UwEJtlEqSy0jxc8E2JMe0cZ
        bkcxDELVecrjUxEQXAlmPWQnVIOiEbMK8gi+NUfcb11nP7K3q0ufPx+ZGD30NPWw
        kid17XnCmX6/AImZRVifyaKcwWel/wjmivp6NV2OU4SSnofBWNpFWRq3nt3o18Wq
        bZERJxE6R9U69I829JaUfuBIapy6IS3wGI6pgQ/la+QThcwdN00Np4Te/wwugq41
        yllCuYr/AmtghANGiHWhvABPVFlHuiOHNamlpxYIuKcPZ9q9D63CBqrBlLhG5MeV
        dkT+59Q/ukHlJw5dQIlyVFab1vyqKA==
        -----END CERTIFICATE-----
        """;

    private static readonly byte[] CoreConstraints = Convert.FromHexString("301ba019300a87087f000001ffffffff300b82096c6f63616c686f7374");

    // NameConstraints permitting 127.0.0.0/8 instead of 127.0.0.1 alone.
    private static readonly byte[] WiderConstraints = Convert.FromHexString("301ba019300a87087f000000ff000000300b82096c6f63616c686f7374");

    private static readonly BackendEndpoint Local = BackendEndpoint.Local(5711);

    private static X509Certificate2 Root(string commonName = LocalCertificateTrust.RootCommonName, byte[]? constraints = null, bool critical = true,
        bool ca = true, int pathLength = 0, bool keepKey = false)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}, O=Core", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, ca && pathLength >= 0, Math.Max(pathLength, 0), true));
        if (constraints is not null)
        {
            request.CertificateExtensions.Add(new X509Extension("2.5.29.30", constraints, critical));
        }

        var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        if (keepKey)
        {
            return made;
        }

        using (made)
        {
            return X509CertificateLoader.LoadCertificate(made.Export(X509ContentType.Cert));
        }
    }

    private static X509Certificate2 CoreRoot() => Root(constraints: CoreConstraints);

    private static string Pem(X509Certificate2 certificate) => certificate.ExportCertificatePem();

    [Fact]
    public void TheBackendsOwnRootIsACoreLocalRoot()
    {
        using var root = X509Certificate2.CreateFromPem(BackendRoot);
        Assert.True(LocalCertificateTrust.IsCoreLocalRoot(root));
        Assert.True(LocalCertificateTrust.IsCoreLocalRoot(CoreRoot()));
    }

    [Fact]
    public void AnyOtherCertificateIsNot()
    {
        // No name constraint, a non-critical one, a wider one, another name, no CA, a CA for more levels, one with its key.
        Assert.False(LocalCertificateTrust.IsCoreLocalRoot(Root()));
        Assert.False(LocalCertificateTrust.IsCoreLocalRoot(Root(constraints: CoreConstraints, critical: false)));
        Assert.False(LocalCertificateTrust.IsCoreLocalRoot(Root(constraints: WiderConstraints)));
        Assert.False(LocalCertificateTrust.IsCoreLocalRoot(Root("Some Other Root", CoreConstraints)));
        Assert.False(LocalCertificateTrust.IsCoreLocalRoot(Root(constraints: CoreConstraints, ca: false)));
        Assert.False(LocalCertificateTrust.IsCoreLocalRoot(Root(constraints: CoreConstraints, pathLength: 1)));
        Assert.False(LocalCertificateTrust.IsCoreLocalRoot(Root(constraints: CoreConstraints, pathLength: -1)));
        using var withKey = Root(constraints: CoreConstraints, keepKey: true);
        Assert.True(withKey.HasPrivateKey);
        Assert.False(LocalCertificateTrust.IsCoreLocalRoot(withKey));

        // Core's name and constraints on a certificate another key signed: not self-signed.
        using var issuerKey = RSA.Create(2048);
        using var subjectKey = RSA.Create(2048);
        var issuerRequest = new CertificateRequest($"CN={LocalCertificateTrust.RootCommonName}, O=Core", issuerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        issuerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var issuer = issuerRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var request = new CertificateRequest($"CN={LocalCertificateTrust.RootCommonName}, O=Core", subjectKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509Extension("2.5.29.30", CoreConstraints, true));
        using var forged = request.Create(issuer, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1), [1, 2, 3, 4]);
        Assert.False(LocalCertificateTrust.IsCoreLocalRoot(forged));
    }

    [Theory]
    [InlineData("127.0.0.1", 5595, false, true)]
    [InlineData("127.0.0.1", 5711, false, true)]
    [InlineData("::1", 5595, false, true)]
    [InlineData("203.0.113.7", 5595, false, false)]
    [InlineData("friend.example.com", 5595, false, false)]
    [InlineData("127.0.0.1", 443, true, false)]
    public void OnlyThisPcsOwnBackendIsOffered(string host, int port, bool secure, bool local)
    {
        Assert.Equal(local, LocalCertificateTrust.IsLocal(new BackendEndpoint(host, port, secure)));
    }

    [Fact]
    public async Task TheRootIsFetchedFromTheLocalBackendOnly()
    {
        var root = CoreRoot();
        var routes = new Routes { ["http://127.0.0.1:5711/core/api/tls/root.crt"] = () => Text(HttpStatusCode.OK, Pem(root)) };
        var trust = new LocalCertificateTrust(new MemoryStore(), new HttpClient(routes));

        using var fetched = await trust.FetchRootAsync(Local, TestContext.Current.CancellationToken);
        Assert.Equal(root.Thumbprint, fetched!.Thumbprint);
        Assert.False(fetched.HasPrivateKey);

        await Assert.ThrowsAsync<InvalidOperationException>(() => trust.FetchRootAsync(new BackendEndpoint("203.0.113.7", 5595), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => trust.FetchRootAsync(new BackendEndpoint("core.example.com", 443, Secure: true), TestContext.Current.CancellationToken));
        Assert.Equal(["http://127.0.0.1:5711/core/api/tls/root.crt"], routes.Asked);
    }

    [Fact]
    public async Task AnOlderBackendHasNoRootAndAnythingButACoreRootIsRefused()
    {
        Func<HttpResponseMessage> answer = () => Text(HttpStatusCode.NotFound, "{}");
        var routes = new Routes { ["http://127.0.0.1:5711/core/api/tls/root.crt"] = () => answer() };
        var store = new MemoryStore();
        var trust = new LocalCertificateTrust(store, new HttpClient(routes));
        Assert.Null(await trust.FetchRootAsync(Local, TestContext.Current.CancellationToken));
        var (status, root) = await trust.CheckAsync(Local, TestContext.Current.CancellationToken);
        Assert.Equal(LocalTrustState.NoHttps, status.State);
        Assert.Null(root);
        Assert.False(status.CanTrust);

        // Something else on the port serving a root without Core's constraint.
        var other = Pem(Root());
        answer = () => Text(HttpStatusCode.OK, other);
        await Assert.ThrowsAsync<InvalidDataException>(() => trust.FetchRootAsync(Local, TestContext.Current.CancellationToken));
        (status, root) = await trust.CheckAsync(Local, TestContext.Current.CancellationToken);
        Assert.Equal(LocalTrustState.NoHttps, status.State);
        Assert.Null(root);
        Assert.Empty(store.Added);
    }

    [Fact]
    public async Task NoLocalBackendNoCheck()
    {
        var routes = new Routes();
        var trust = new LocalCertificateTrust(new MemoryStore(), new HttpClient(routes));
        var (status, root) = await trust.CheckAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(LocalTrustState.NoBackend, status.State);
        Assert.Null(root);
        (status, _) = await trust.CheckAsync(new BackendEndpoint("203.0.113.7", 5595), TestContext.Current.CancellationToken);
        Assert.Equal(LocalTrustState.NoBackend, status.State);
        Assert.Empty(routes.Asked);
    }

    [Fact]
    public async Task TrustingAddsTheRootAndRemovingTakesOutCoreRootsOnly()
    {
        var root = CoreRoot();
        var routes = new Routes { ["http://127.0.0.1:5711/core/api/tls/root.crt"] = () => Text(HttpStatusCode.OK, Pem(root)) };
        var unrelated = Root("Someone Else's Root");
        var store = new MemoryStore(unrelated);
        var trust = new LocalCertificateTrust(store, new HttpClient(routes));

        var (status, fetched) = await trust.CheckAsync(Local, TestContext.Current.CancellationToken);
        Assert.Equal(LocalTrustState.NotTrusted, status.State);
        Assert.True(status.CanTrust);
        Assert.False(status.CanRemove);
        Assert.Empty(store.Added);

        Assert.True(trust.Trust(fetched!));
        Assert.Equal([root.Thumbprint], store.Added.Select(certificate => certificate.Thumbprint));
        (status, _) = await trust.CheckAsync(Local, TestContext.Current.CancellationToken);
        Assert.Equal(LocalTrustState.Trusted, status.State);
        Assert.False(status.CanTrust);
        Assert.True(status.CanRemove);

        Assert.Equal(1, trust.RemoveAll());
        Assert.Equal([unrelated.Thumbprint], store.Held.Select(certificate => certificate.Thumbprint));
        (status, _) = await trust.CheckAsync(Local, TestContext.Current.CancellationToken);
        Assert.Equal(LocalTrustState.NotTrusted, status.State);
    }

    [Fact]
    public void OnlyACoreLocalRootIsEverAdded()
    {
        var store = new MemoryStore();
        var trust = new LocalCertificateTrust(store, new HttpClient(new Routes()));
        Assert.Throws<InvalidDataException>(() => trust.Trust(Root()));
        Assert.Throws<InvalidDataException>(() => trust.Trust(Root(constraints: WiderConstraints)));
        Assert.Empty(store.Added);
    }

    [Fact]
    public async Task AnEarlierRootIsOutdatedAndBothAreRemoved()
    {
        var earlier = CoreRoot();
        var current = CoreRoot();
        var routes = new Routes { ["http://127.0.0.1:5711/core/api/tls/root.crt"] = () => Text(HttpStatusCode.OK, Pem(current)) };
        var store = new MemoryStore(earlier);
        var trust = new LocalCertificateTrust(store, new HttpClient(routes));

        var (status, fetched) = await trust.CheckAsync(Local, TestContext.Current.CancellationToken);
        Assert.Equal(LocalTrustState.Outdated, status.State);
        Assert.True(status.CanTrust);
        Assert.True(status.CanRemove);
        Assert.True(trust.Trust(fetched!));
        (status, _) = await trust.CheckAsync(Local, TestContext.Current.CancellationToken);
        Assert.Equal(LocalTrustState.Trusted, status.State);
        Assert.Equal(1, status.OtherCoreRoots);

        Assert.Equal(2, trust.RemoveAll());
        Assert.Empty(store.Held);
    }

    [Fact]
    public async Task DecliningWindowsPromptChangesNothing()
    {
        var root = CoreRoot();
        var routes = new Routes { ["http://127.0.0.1:5711/core/api/tls/root.crt"] = () => Text(HttpStatusCode.OK, Pem(root)) };
        var store = new MemoryStore { Decline = true };
        var trust = new LocalCertificateTrust(store, new HttpClient(routes));

        var (_, fetched) = await trust.CheckAsync(Local, TestContext.Current.CancellationToken);
        Assert.False(trust.Trust(fetched!));
        Assert.Empty(store.Held);

        store.Decline = false;
        Assert.True(trust.Trust(fetched!));
        store.Decline = true;
        Assert.Equal(0, trust.RemoveAll());
        Assert.Single(store.Held);
        var (status, _) = await trust.CheckAsync(Local, TestContext.Current.CancellationToken);
        Assert.Equal(LocalTrustState.Trusted, status.State);
    }

    [Fact]
    public async Task NoBackendStillShowsATrustedRootCanBeRemoved()
    {
        var store = new MemoryStore(CoreRoot());
        var trust = new LocalCertificateTrust(store, new HttpClient(new Routes()));
        var (status, _) = await trust.CheckAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(LocalTrustState.NoBackend, status.State);
        Assert.False(status.CanTrust);
        Assert.True(status.CanRemove);
    }

    [Fact]
    public async Task APlayersStartSaysWhyThePurchasePageWouldNotOpen()
    {
        var earlier = CoreRoot();
        var current = CoreRoot();
        Func<HttpResponseMessage> answer = () => Text(HttpStatusCode.OK, Pem(current));
        var routes = new Routes { ["http://127.0.0.1:5711/core/api/tls/root.crt"] = () => answer() };
        var store = new MemoryStore();
        var trust = new LocalCertificateTrust(store, new HttpClient(routes));

        var (status, fetched) = await trust.CheckAsync(Local, TestContext.Current.CancellationToken);
        var warning = LocalCertificateTrust.PurchasePageWarning(status, Local.Port);
        Assert.StartsWith("Buying V-Bucks in this build opens https://127.0.0.1:5711, and Windows doesn't trust your backend's certificate", warning);
        Assert.Contains("PageLoadFail", warning);
        Assert.Contains("Settings → Backend → Local https", warning);

        Assert.True(trust.Trust(fetched!));
        (status, _) = await trust.CheckAsync(Local, TestContext.Current.CancellationToken);
        Assert.Null(LocalCertificateTrust.PurchasePageWarning(status, Local.Port));

        // The backend's data folder was reset: a new root, and the trusted one is the earlier.
        store.Held.Clear();
        store.Held.Add(earlier);
        (status, _) = await trust.CheckAsync(Local, TestContext.Current.CancellationToken);
        Assert.Contains("an earlier certificate of your backend", LocalCertificateTrust.PurchasePageWarning(status, Local.Port));

        // A Core-Backend from before local https.
        answer = () => Text(HttpStatusCode.NotFound, "{}");
        (status, _) = await trust.CheckAsync(Local, TestContext.Current.CancellationToken);
        Assert.Contains("doesn't answer https", LocalCertificateTrust.PurchasePageWarning(status, Local.Port));

        // The backend didn't answer.
        answer = () => throw new HttpRequestException("refused");
        (status, _) = await trust.CheckAsync(Local, TestContext.Current.CancellationToken);
        Assert.Contains("couldn't be checked", LocalCertificateTrust.PurchasePageWarning(status, Local.Port));
    }

    private static HttpResponseMessage Text(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.ASCII, "application/x-pem-file") };

    /// <summary>CurrentUser\Root's stand-in. Decline: as when the user answers No in Windows' prompt.</summary>
    private sealed class MemoryStore(params X509Certificate2[] initial) : IRootCertificateStore
    {
        public List<X509Certificate2> Held { get; } = [.. initial];

        public List<X509Certificate2> Added { get; } = [];

        public bool Decline { get; set; }

        public IReadOnlyList<X509Certificate2> Certificates() => [.. Held];

        public void Add(X509Certificate2 certificate)
        {
            if (Decline)
            {
                throw new CryptographicException("The operation was canceled by the user.");
            }

            Added.Add(certificate);
            if (Held.All(held => held.Thumbprint != certificate.Thumbprint))
            {
                Held.Add(certificate);
            }
        }

        public void Remove(X509Certificate2 certificate)
        {
            if (Decline)
            {
                throw new CryptographicException("The operation was canceled by the user.");
            }

            Held.RemoveAll(held => held.Thumbprint == certificate.Thumbprint);
        }
    }

    private sealed class Routes : HttpMessageHandler, IEnumerable<KeyValuePair<string, Func<HttpResponseMessage>>>
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = [];

        public List<string> Asked { get; } = [];

        public Func<HttpResponseMessage> this[string url]
        {
            set => _routes[url] = value;
        }

        public IEnumerator<KeyValuePair<string, Func<HttpResponseMessage>>> GetEnumerator() => _routes.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Asked.Add(url);
            return Task.FromResult(_routes.TryGetValue(url, out var respond) ? respond() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
