using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Core.Launcher.Hosting.Backend;

/// <summary>Where a Core-Backend answers: this PC's on a port, or a friend's at a host name or IP and port.</summary>
public sealed record BackendEndpoint(string Host, int Port)
{
    public const int DefaultPort = 5595;

    public static BackendEndpoint Local(int port) => new("127.0.0.1", port);

    /// <summary>Host and port as a URL or -httpproxy takes them; an IPv6 address is bracketed.</summary>
    public string Authority => $"{(Host.Contains(':') ? $"[{Host}]" : Host)}:{Port}";

    /// <summary>What CoreRedirect.dll sends Epic's hosts to (-CoreBackend=).</summary>
    public string Url => $"http://{Authority}";

    public override string ToString() => Authority;

    /// <summary>
    /// Reads what a player types for a friend's backend: <c>203.0.113.7</c>, <c>my-house.duckdns.org:5600</c>,
    /// <c>[2001:db8::1]:5595</c> or a pasted <c>http://host:port/</c>. The port defaults to 5595.
    /// </summary>
    public static bool TryParse(string? text, out BackendEndpoint endpoint, out string error)
    {
        endpoint = Local(DefaultPort);
        var value = (text ?? "").Trim();
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            error = "Core-Backend is reached over http://, not https://";
            return false;
        }

        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            value = value["http://".Length..];
        }

        var path = value.IndexOfAny(['/', '?', '#']);
        if (path >= 0)
        {
            value = value[..path];
        }

        if (value.Length == 0)
        {
            error = "Enter your friend's address, e.g. 203.0.113.7 or 203.0.113.7:5595";
            return false;
        }

        string host;
        var portText = "";
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close < 0 || (close + 1 < value.Length && value[close + 1] != ':'))
            {
                error = $"{value} isn't an address";
                return false;
            }

            host = value[1..close];
            portText = close + 1 < value.Length ? value[(close + 2)..] : "";
        }
        else if (value.Count(character => character == ':') > 1)
        {
            host = value;
        }
        else
        {
            var colon = value.IndexOf(':');
            host = colon < 0 ? value : value[..colon];
            portText = colon < 0 ? "" : value[(colon + 1)..];
        }

        var port = DefaultPort;
        if (portText.Length > 0 && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
        {
            error = $"{portText} isn't a port (1 to 65535)";
            return false;
        }

        if (host.Length == 0)
        {
            error = "Enter your friend's IP address or host name before the port";
            return false;
        }

        // IPAddress.TryParse also takes "1.2" (1.0.0.2) and "12345"; an IPv4 address here has its four parts.
        var numeric = host.All(character => char.IsAsciiDigit(character) || character == '.');
        if (numeric && host.Split('.').Length != 4)
        {
            error = $"{host} isn't an IP address";
            return false;
        }

        if (IPAddress.TryParse(host, out var ip) && (numeric || ip.AddressFamily == AddressFamily.InterNetworkV6))
        {
            if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            {
                error = $"{host} isn't an address another PC can be reached at";
                return false;
            }

            host = ip.ToString();
        }
        else if (Uri.CheckHostName(host) != UriHostNameType.Dns || !host.Any(char.IsLetter))
        {
            error = $"{host} isn't a host name or IP address";
            return false;
        }

        endpoint = new BackendEndpoint(host.ToLowerInvariant(), port);
        error = "";
        return true;
    }
}
