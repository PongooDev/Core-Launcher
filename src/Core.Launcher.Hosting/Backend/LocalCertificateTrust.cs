using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Core.Launcher.Hosting.Backend;

/// <summary>Where the launcher keeps the local backend's root: the user's trusted roots (CurrentUser\Root), or a test's.</summary>
public interface IRootCertificateStore
{
    IReadOnlyList<X509Certificate2> Certificates();

    /// <summary>Windows asks the user to confirm; a refusal throws <see cref="CryptographicException"/>.</summary>
    void Add(X509Certificate2 certificate);

    /// <summary>Windows asks the user to confirm; a refusal throws <see cref="CryptographicException"/>.</summary>
    void Remove(X509Certificate2 certificate);
}

/// <summary>
/// The user's own trusted roots, CurrentUser\Root. Adding or removing a root there makes Windows show its own security
/// warning, which the user answers; nothing here gets around it. No admin rights are needed and no other user is affected.
/// </summary>
public sealed class CurrentUserRootStore : IRootCertificateStore
{
    public IReadOnlyList<X509Certificate2> Certificates()
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return [.. store.Certificates];
    }

    public void Add(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);
    }

    public void Remove(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Remove(certificate);
    }
}

public enum LocalTrustState
{
    /// <summary>No local backend running here, so nothing to compare with.</summary>
    NoBackend,

    /// <summary>The local backend has no https (an older Core-Backend, or it couldn't make its certificate).</summary>
    NoHttps,

    NotTrusted,

    Trusted,

    /// <summary>A root of an earlier certificate is trusted, not the backend's current one (its data folder was reset).</summary>
    Outdated,
}

public sealed record LocalTrustStatus(LocalTrustState State, string Message, int OtherCoreRoots = 0)
{
    public bool CanTrust => State is LocalTrustState.NotTrusted or LocalTrustState.Outdated;

    public bool CanRemove => OtherCoreRoots > 0 || State == LocalTrustState.Trusted;
}

/// <summary>
/// The opt-in trust of the local Core-Backend's own https root (Core-Backend #377). From 7.00 the game opens its V-Bucks
/// purchase page at https://127.0.0.1:&lt;port&gt;, in a browser that only accepts a certificate Windows trusts. Each
/// Core-Backend makes its own root for it, limited by a critical name constraint to 127.0.0.1 and localhost, whose private
/// key it never keeps. The launcher fetches that root from the local backend (GET /core/api/tls/root.crt), checks it is
/// exactly such a root, and only when the user asks adds it to CurrentUser\Root (or removes it). Never for a friend's
/// backend: their root couldn't vouch for this PC's 127.0.0.1 anyway.
/// </summary>
public sealed class LocalCertificateTrust(IRootCertificateStore store, HttpClient http)
{
    public const string RootPath = "/core/api/tls/root.crt";

    /// <summary>The root's subject common name, as Core-Backend's src/tls/certificates.js makes it.</summary>
    public const string RootCommonName = "Core local backend (127.0.0.1 only)";

    private const string NameConstraintsOid = "2.5.29.30";

    // NameConstraints { permittedSubtrees [0] { { iPAddress 127.0.0.1/255.255.255.255 }, { dNSName "localhost" } } }:
    // the exact value Core-Backend writes, and the only one taken.
    private static readonly byte[] NameConstraints = Convert.FromHexString("301ba019300a87087f000001ffffffff300b82096c6f63616c686f7374");

    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(5) })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    /// <summary>
    /// Whether a certificate is a Core local backend root: self-signed with Core's name, a CA for one level only, and
    /// limited by the critical name constraint to 127.0.0.1 and localhost. Anything else is never added, and no other
    /// certificate in the store is ever removed.
    /// </summary>
    public static bool IsCoreLocalRoot(X509Certificate2 certificate)
    {
        if (certificate.HasPrivateKey || certificate.GetNameInfo(X509NameType.SimpleName, false) != RootCommonName
            || !certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData))
        {
            return false;
        }

        var basic = certificate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
        if (basic is not { Critical: true, CertificateAuthority: true, HasPathLengthConstraint: true, PathLengthConstraint: 0 })
        {
            return false;
        }

        var constraints = certificate.Extensions.Cast<X509Extension>().Where(extension => extension.Oid?.Value == NameConstraintsOid).ToList();
        if (constraints is not [{ Critical: true } constraint] || !constraint.RawData.AsSpan().SequenceEqual(NameConstraints))
        {
            return false;
        }

        try
        {
            return SignedBy(certificate, certificate);
        }
        catch (Exception exception) when (exception is CryptographicException or AsnContentException)
        {
            return false;
        }
    }

    // Whether the certificate's signature verifies with the issuer's key (a self-signed root: its own).
    private static bool SignedBy(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        var reader = new AsnReader(certificate.RawData, AsnEncodingRules.DER).ReadSequence();
        var tbs = reader.ReadEncodedValue().ToArray();
        var algorithm = reader.ReadSequence().ReadObjectIdentifier();
        var signature = reader.ReadBitString(out _);
        var hash = algorithm switch
        {
            "1.2.840.113549.1.1.11" or "1.2.840.10045.4.3.2" => HashAlgorithmName.SHA256,
            "1.2.840.113549.1.1.12" or "1.2.840.10045.4.3.3" => HashAlgorithmName.SHA384,
            "1.2.840.113549.1.1.13" or "1.2.840.10045.4.3.4" => HashAlgorithmName.SHA512,
            _ => default,
        };
        if (hash == default)
        {
            return false;
        }

        if (issuer.GetRSAPublicKey() is { } rsa)
        {
            using (rsa)
            {
                return rsa.VerifyData(tbs, signature, hash, RSASignaturePadding.Pkcs1);
            }
        }

        using var ecdsa = issuer.GetECDsaPublicKey();
        return ecdsa?.VerifyData(tbs, signature, hash, DSASignatureFormat.Rfc3279DerSequence) == true;
    }

    /// <summary>Only this PC's backend over plain http (the launcher's own, or one already running here).</summary>
    public static bool IsLocal(BackendEndpoint endpoint) =>
        !endpoint.Secure && IPAddress.TryParse(endpoint.Host, out var address) && IPAddress.IsLoopback(address);

    /// <summary>The local backend's root, or null when it has none (an older Core-Backend, or no certificate).</summary>
    public async Task<X509Certificate2?> FetchRootAsync(BackendEndpoint endpoint, CancellationToken cancellation = default)
    {
        if (!IsLocal(endpoint))
        {
            throw new InvalidOperationException("Only this PC's own backend's certificate can be trusted");
        }

        using var response = await http.GetAsync(endpoint.Url + RootPath, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var pem = await response.Content.ReadAsStringAsync(cancellation);
        var root = X509Certificate2.CreateFromPem(pem);
        if (!IsCoreLocalRoot(root))
        {
            root.Dispose();
            throw new InvalidDataException("The backend's root certificate isn't a Core local root limited to 127.0.0.1, so it wasn't trusted");
        }

        return root;
    }

    /// <summary>
    /// Fetches the local backend's root and says what the store holds for it. <paramref name="local"/> is the backend in
    /// use while it is this PC's and ready, else null. Root is null unless the backend gave a Core local root.
    /// </summary>
    public async Task<(LocalTrustStatus Status, X509Certificate2? Root)> CheckAsync(BackendEndpoint? local, CancellationToken cancellation = default)
    {
        if (local is null || !IsLocal(local))
        {
            return (Status(null, backendRunning: false), null);
        }

        try
        {
            var root = await FetchRootAsync(local, cancellation);
            return (Status(root), root);
        }
        catch (InvalidDataException exception)
        {
            return (new LocalTrustStatus(LocalTrustState.NoHttps, exception.Message, CoreRoots().Count), null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or CryptographicException && !cancellation.IsCancellationRequested)
        {
            return (new LocalTrustStatus(LocalTrustState.NoBackend, $"Couldn't get the backend's certificate ({exception.Message}).", CoreRoots().Count), null);
        }
    }

    private List<X509Certificate2> CoreRoots() => [.. store.Certificates().Where(IsCoreLocalRoot)];

    /// <summary>What the store holds for the backend's current root (null: no local backend running).</summary>
    public LocalTrustStatus Status(X509Certificate2? current, bool backendRunning = true)
    {
        var roots = CoreRoots();
        var trusted = current is not null && roots.Any(root => root.Thumbprint == current.Thumbprint);
        var others = roots.Count(root => current is null || root.Thumbprint != current.Thumbprint);
        if (!backendRunning)
        {
            return new(LocalTrustState.NoBackend, roots.Count > 0
                ? "Start your own backend to check its certificate. A Core local root is trusted."
                : "Start your own backend to check its certificate.", roots.Count);
        }

        if (current is null)
        {
            return new(LocalTrustState.NoHttps, "This backend has no https certificate (update Core-Backend).", others);
        }

        if (trusted)
        {
            return new(LocalTrustState.Trusted, "Trusted: V-Bucks purchases on 7.00 and later can open.", others);
        }

        return others > 0
            ? new(LocalTrustState.Outdated, "An earlier certificate of this backend is trusted, not its current one: trust it again.", others)
            : new(LocalTrustState.NotTrusted, "Not trusted.", 0);
    }

    /// <summary>Adds the root to the user's trusted roots (Windows asks first). False when the user said no there.</summary>
    public bool Trust(X509Certificate2 root)
    {
        if (!IsCoreLocalRoot(root))
        {
            throw new InvalidDataException("Only a Core local root limited to 127.0.0.1 is trusted");
        }

        try
        {
            store.Add(root);
        }
        catch (CryptographicException)
        {
            // The user answered No in Windows' dialog (ERROR_CANCELLED), or the store refused it.
            return false;
        }

        return CoreRoots().Any(trusted => trusted.Thumbprint == root.Thumbprint);
    }

    /// <summary>Removes every Core local root from the user's trusted roots (Windows asks for each). Returns how many went.</summary>
    public int RemoveAll()
    {
        var before = CoreRoots();
        foreach (var root in before)
        {
            try
            {
                store.Remove(root);
            }
            catch (CryptographicException)
            {
                // Kept: the user answered No in Windows' dialog.
            }
        }

        return before.Count - CoreRoots().Count;
    }
}
