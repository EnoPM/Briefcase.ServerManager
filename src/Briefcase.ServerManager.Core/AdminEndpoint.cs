using System.Globalization;

namespace Briefcase.ServerManager.Core;

public readonly record struct AdminEndpoint(string Host, int Port)
{
    public const int DefaultPort = 32189;
    public const string DefaultHost = "127.0.0.1";
    public const string DefaultValue = "127.0.0.1:32189";

    public static AdminEndpoint Parse(string value)
    {
        value = value.Trim();
        if (value.Length is 0 or > 255)
            throw new FormatException("Enter an administration address and port.");

        string host;
        string portText;
        if (value[0] == '[')
        {
            var close = value.IndexOf(']');
            if (close <= 1 || close + 2 >= value.Length || value[close + 1] != ':')
                throw new FormatException("Use [IPv6-address]:port for IPv6.");
            host = value[1..close];
            portText = value[(close + 2)..];
        }
        else
        {
            var separator = value.LastIndexOf(':');
            if (separator <= 0 || separator == value.Length - 1 || value[..separator].Contains(':'))
                throw new FormatException("Use host:port for the administration endpoint.");
            host = value[..separator];
            portText = value[(separator + 1)..];
        }

        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535 || host.Any(char.IsWhiteSpace))
            throw new FormatException("The administration endpoint is invalid.");
        return new(host, port);
    }

    public override string ToString() => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}
