using System.Globalization;

namespace Wandur.Core.Settings;

public sealed record WorldAddress(string Host, int? Port)
{
    public static bool TryParse(string? input, out WorldAddress? address)
    {
        address = null;
        var value = input?.Trim() ?? "";
        if (value.StartsWith("telnet://", StringComparison.OrdinalIgnoreCase)) value = value[9..].TrimEnd('/');
        if (value.Length == 0 || value.Contains('/') || value.Contains('@') || value.Contains('?') || value.Contains('#')) return false;
        string host = value;
        string? portText = null;
        if (value.StartsWith('['))
        {
            int end = value.IndexOf(']');
            if (end < 0) return false;
            host = value[1..end];
            if (Uri.CheckHostName(host) != UriHostNameType.IPv6) return false;
            if (end + 1 < value.Length)
            {
                if (value[end + 1] != ':') return false;
                portText = value[(end + 2)..];
            }
        }
        else if (Uri.CheckHostName(value) != UriHostNameType.IPv6)
        {
            var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2) { host = parts[0]; portText = parts[1]; }
            else if (parts.Length != 1) return false;
            else if (value.Contains(':'))
            {
                int colon = value.IndexOf(':');
                host = value[..colon]; portText = value[(colon + 1)..];
            }
        }
        if (host.Length > 253 || Uri.CheckHostName(host) == UriHostNameType.Unknown) return false;
        int? port = null;
        if (portText is not null)
        {
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed is < 1 or > 65535) return false;
            port = parsed;
        }
        address = new(host, port);
        return true;
    }
}
