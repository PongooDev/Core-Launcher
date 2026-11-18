using System.Text;
using Core.Launcher.Hosting.Backend;
using Core.Launcher.Hosting.Builds;

namespace Core.Launcher.Hosting.Processes;

/// <summary>
/// The Store's purchase page, in the build's FortniteGame\Config\NoRedist\DefaultGame.ini (Core-Backend #244). The
/// purchase flow reads its URL once, from the game config's [EpicPurchaseFlow] section under the MCP environment's name
/// ("Prod"): the front end's root UIManager does on its first frame (UFortMcpContext::RegisterForEpicPurchasingDisplay,
/// 3.1 0xD496C0, 3.5.2 0xDBEA40), before the backend's DefaultGame.ini hotfix applies, so a V-Bucks pack or the Starter
/// Pack opened Epic's shipped launcher-website page, which is black.
/// <para>
/// The player's saved Game.ini can't carry it: a cooked build loads only GameUserSettings.ini from Saved\Config and marks
/// every other config NoSave (FConfigCacheIni::LoadExternalIniFile, 3.5.2 0x12CB530, 3.1 0x11E7FD0). The source hierarchy
/// takes NoRedist\Default&lt;Name&gt;.ini after Default&lt;Name&gt;.ini (GetSourceIniHierarchyFilenames, 3.5.2 0x12C7F80),
/// and the pak file reads a loose file for anything that isn't a uasset, umap, ubulk or uexp (FPakPlatformFile::
/// IsNonPakFilenameAllowed, 3.5.2 0x269FC20, no filename security delegate bound), so a loose one overrides the pak's Prod.
/// From 6.20 at the latest that check refuses loose .ini files too, so for builds after 6.00 the same text also goes in a
/// pak (<see cref="ConfigPak"/>, Core-Backend #355). Clients only: servers show no Store.
/// </para>
/// <para>
/// The page's sign-in (Core-Backend #248): before loading the page the flow sets the player's token as a cookie, and the
/// cookie is always Secure (FCookie bSecure = 1, 3.5.1 0x6B362B, 3.1 0x6A7352), which CEF won't set for an http:// page.
/// The flow then falls back to single sign-on (FOnlineIdentityMcp::GenerateAuthRedirectUrl, 3.5.1 0x3AAFD0): it asks
/// /account/api/oauth/exchange for an exchange code and opens
/// &lt;Protocol&gt;://&lt;RedirectUrl&gt;/exchange?exchangeCode=...&amp;redirectUrl=&lt;the page&gt; (0x3AB800), with Protocol and
/// RedirectUrl from [OnlineSubsystemMcp.OnlineIdentityMcp] (0x4EEE00), which point at Epic's accounts site. So the
/// build's NoRedist\DefaultEngine.ini points them at the backend too. The identity service's own requests are
/// &lt;Protocol&gt;://&lt;Domain&gt;/account/...: Domain stays Epic's, and CoreRedirect sends them to the backend whatever the scheme.
/// </para>
/// </summary>
public static class PurchaseFlowConfig
{
    public const string Section = "EpicPurchaseFlow";

    public const string Key = "Prod";

    public const string IdentitySection = "OnlineSubsystemMcp.OnlineIdentityMcp";

    /// <summary>The build's NoRedist\DefaultGame.ini, layered over the pak's FortniteGame\Config\DefaultGame.ini.</summary>
    public static string GameIniPath(string buildRoot) => Path.Combine(buildRoot, "FortniteGame", "Config", "NoRedist", "DefaultGame.ini");

    /// <summary>The build's NoRedist\DefaultEngine.ini, layered over the pak's FortniteGame\Config\DefaultEngine.ini.</summary>
    public static string EngineIniPath(string buildRoot) => Path.Combine(buildRoot, "FortniteGame", "Config", "NoRedist", "DefaultEngine.ini");

    /// <summary>
    /// [OnlineSubsystemMcp.OnlineIdentityMcp]'s Protocol and RedirectUrl for the backend: the single sign-on page is
    /// Protocol://RedirectUrl/exchange (Core-Backend's /exchange), e.g. http and 127.0.0.1:5595.
    /// </summary>
    public static IReadOnlyList<(string Key, string Value)> IdentityEntries(BackendEndpoint backend, int changelist = 0)
    {
        var redirect = ("RedirectUrl", backend.Url[(backend.Scheme.Length + "://".Length)..]);
        return KeepsProtocol(changelist) ? [("Protocol", backend.Scheme), redirect] : [redirect];
    }

    /// <summary>
    /// Whether the build takes [OnlineSubsystemMcp.OnlineIdentityMcp] Protocol from the config. 7.00 (CL-4629139) sets any
    /// other Protocol than https back to "https" at load, and from 7.10 (CL-4667333) FBaseServiceMcp::GetBaseUrl calls
    /// FPlatformMisc::RequestExit when the protocol isn't https, so the client quit at login. From 7.00 the launcher
    /// writes no Protocol line (and removes one an earlier launcher wrote, <see cref="IdentityRemovals"/>).
    /// </summary>
    public static bool KeepsProtocol(int changelist) => changelist < KnownBuilds.Fortnite700;

    /// <summary>The keys of [OnlineSubsystemMcp.OnlineIdentityMcp] the launcher no longer writes for the build, to be removed from an existing file.</summary>
    public static IReadOnlyList<string> IdentityRemovals(int changelist) => KeepsProtocol(changelist) ? [] : ["Protocol"];

    /// <summary>
    /// The scheme the purchase page's sign-in opens with (Protocol://RedirectUrl/exchange): the backend's own before 7.00,
    /// and https from 7.00, which sets any other Protocol back to https (<see cref="KeepsProtocol"/>). On this PC's plain-http
    /// backend that is its local https, which the game's browser only opens once Windows trusts the backend's own root
    /// (Core-Backend #377, <see cref="LocalCertificateTrust"/>).
    /// </summary>
    public static string SignInScheme(BackendEndpoint backend, int changelist) => KeepsProtocol(changelist) ? backend.Scheme : "https";

    /// <summary>
    /// As Core-Backend's purchaseFlowUrl (src/services/fortnite/purchase.js): http:///host:port/purchase. The triple slash
    /// is deliberate: the flow takes the page's cookie domain by stripping "http://" or "https://" and cutting at the next
    /// "/", and an empty domain makes the cookie the page's own host; CEF reads the URL as http://host:port/purchase.
    /// </summary>
    public static string Url(BackendEndpoint backend) => $"{backend.Scheme}:///{backend.Url[(backend.Scheme.Length + "://".Length)..]}/purchase";

    /// <summary>
    /// Clears [EpicPurchaseFlow] ProdAltDomains, the page's other domains (Core-Backend #346). The flow reads them with
    /// Prod (6.20 0x11339F0) and, after the page's own cookie, sets the same cookie on each one (0x7F0150): it swaps the
    /// URL's domain (FGenericPlatformHttp::GetUrlDomain, 0x1FD08B0) for the other domain, and with <see cref="Url"/>'s
    /// triple slash that domain is empty, so the next cookie is never set and the flow never goes on (0x7E9400): the
    /// Store's black screen with a spinner. The pak's DefaultGame.ini has one (launcher-website-prod.ak.epicgames.com)
    /// from at least 6.02 to 6.21; cleared, the page's own cookie is the only one, and a build without any is unchanged.
    /// </summary>
    public static readonly (string Key, string Value) ClearAltDomains = ("!ProdAltDomains", "ClearArray");

    /// <summary>
    /// A config value in quotes, as the pak's own Prod is (Core-Backend #355): the ini reader takes a quoted value whole,
    /// with <c>\\</c> and <c>\"</c> for a backslash and a quote (FParse::QuotedString), while in an unquoted one a line
    /// reader that strips comments ends the value at the first <c>//</c> (FParse::LineExtended), which left a hotfixed
    /// URL as "http:" (Core-Backend #349).
    /// </summary>
    public static string Quoted(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    /// <summary>[EpicPurchaseFlow]'s entries: Prod, quoted (<see cref="Quoted"/>), and the line clearing ProdAltDomains.</summary>
    public static IReadOnlyList<(string Key, string Value)> Entries(string url) => [(Key, Quoted(url)), ClearAltDomains];

    /// <summary>
    /// Sets [EpicPurchaseFlow] Prod in <paramref name="path"/> and clears ProdAltDomains (<see cref="ClearAltDomains"/>),
    /// keeping its other sections and keys, its encoding and line endings; creates it when it's missing. Returns whether
    /// it wrote the file. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when it can't.
    /// </summary>
    public static bool Write(string path, string url) => Write(path, Section, Entries(url));

    /// <summary>Sets <paramref name="entries"/> in [<paramref name="section"/>] of <paramref name="path"/>, as <see cref="Write(string, string)"/> does Prod.</summary>
    public static bool Write(string path, string section, IReadOnlyList<(string Key, string Value)> entries, IReadOnlyList<string>? remove = null)
    {
        string existing = "";
        Encoding encoding = new UTF8Encoding(false);
        if (File.Exists(path))
        {
            using var reader = new StreamReader(path, encoding, detectEncodingFromByteOrderMarks: true);
            existing = reader.ReadToEnd();
            encoding = reader.CurrentEncoding;
        }

        var merged = Merge(existing, section, entries, remove);
        if (merged == existing && File.Exists(path))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, merged, encoding);
        return true;
    }

    /// <summary>
    /// Replaces the Prod line under [EpicPurchaseFlow] (dropping any repeats of it), or adds it, or adds the section; and
    /// the same for the line that clears ProdAltDomains.
    /// </summary>
    public static string Merge(string existing, string url) => Merge(existing, Section, Entries(url));

    /// <summary>Sets each key in [<paramref name="section"/>], as <see cref="Merge(string, string)"/> does Prod.</summary>
    public static string Merge(string existing, string section, IReadOnlyList<(string Key, string Value)> entries, IReadOnlyList<string>? remove = null)
    {
        foreach (var key in remove ?? [])
        {
            existing = RemoveKey(existing, section, key);
        }

        foreach (var (key, value) in entries)
        {
            existing = MergeOne(existing, section, key, value);
        }

        return existing;
    }

    /// <summary>Drops every <paramref name="key"/> line under [<paramref name="section"/>], keeping the rest as it is.</summary>
    private static string RemoveKey(string existing, string section, string key)
    {
        var newline = existing.Contains("\r\n") || !existing.Contains('\n') ? "\r\n" : "\n";
        var lines = existing.Replace("\r\n", "\n").Split('\n').ToList();
        var start = lines.FindIndex(line => line.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            return existing;
        }

        var end = lines.FindIndex(start + 1, line => line.TrimStart().StartsWith('['));
        end = end < 0 ? lines.Count : end;
        var found = false;
        for (var index = end - 1; index > start; index--)
        {
            if (IsKey(lines[index], key))
            {
                lines.RemoveAt(index);
                found = true;
            }
        }

        return found ? string.Join(newline, lines) : existing;
    }

    private static string MergeOne(string existing, string section, string key, string value)
    {
        var newline = existing.Contains("\r\n") || !existing.Contains('\n') ? "\r\n" : "\n";
        var lines = existing.Length == 0 ? [] : existing.Replace("\r\n", "\n").Split('\n').ToList();
        var endsWithNewline = existing.EndsWith('\n');
        if (endsWithNewline)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var entry = $"{key}={value}";
        var start = lines.FindIndex(line => line.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
            {
                lines.Add("");
            }

            lines.Add($"[{section}]");
            lines.Add(entry);
        }
        else
        {
            var end = lines.FindIndex(start + 1, line => line.TrimStart().StartsWith('['));
            end = end < 0 ? lines.Count : end;
            var keys = Enumerable.Range(start + 1, end - start - 1).Where(index => IsKey(lines[index], key)).ToList();
            if (keys.Count > 0)
            {
                lines[keys[0]] = entry;
                foreach (var index in keys.Skip(1).Reverse())
                {
                    lines.RemoveAt(index);
                }
            }
            else
            {
                var last = end - 1;
                while (last > start && lines[last].Trim().Length == 0)
                {
                    last--;
                }

                lines.Insert(last + 1, entry);
            }
        }

        return string.Join(newline, lines) + (endsWithNewline || start < 0 || existing.Length == 0 ? newline : "");
    }

    private static bool IsKey(string line, string key)
    {
        var equals = line.IndexOf('=');
        return equals > 0 && line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase);
    }
}
