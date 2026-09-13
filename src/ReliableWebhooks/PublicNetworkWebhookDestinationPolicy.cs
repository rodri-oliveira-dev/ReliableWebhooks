using System.Net;
using System.Net.Sockets;

namespace ReliableWebhooks;

/// <summary>
/// Authorizes destinations that resolve only to public unicast IP addresses.
/// </summary>
/// <remarks>
/// Exact host allow-list entries intentionally bypass the public-network check for applications that
/// deliberately deliver to known private or intranet endpoints.
/// </remarks>
public sealed class PublicNetworkWebhookDestinationPolicy : IWebhookDestinationPolicy
{
    private readonly HashSet<string> allowedHosts;

    /// <summary>
    /// Initializes a new instance of the <see cref="PublicNetworkWebhookDestinationPolicy"/> class.
    /// </summary>
    /// <param name="allowedHosts">
    /// Optional exact host names or IP literals that are explicitly allowed even when they resolve to
    /// private, loopback, link-local, multicast, or otherwise non-public addresses.
    /// </param>
    /// <exception cref="ArgumentException">An allow-list entry is empty or whitespace.</exception>
    public PublicNetworkWebhookDestinationPolicy(IEnumerable<string>? allowedHosts = null)
    {
        this.allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (allowedHosts is null)
        {
            return;
        }

        foreach (string host in allowedHosts)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("Allowed destination hosts must not be empty or whitespace.", nameof(allowedHosts));
            }

            _ = this.allowedHosts.Add(host.Trim());
        }
    }

    /// <inheritdoc />
    public async ValueTask<WebhookDestinationPolicyResult> AuthorizeAsync(
        Uri destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();

        if (IsAllowedHost(destination))
        {
            return WebhookDestinationPolicyResult.Allow();
        }

        string host = destination.IdnHost;
        IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        foreach (IPAddress address in addresses)
        {
            if (IsRestrictedAddress(address))
            {
                return WebhookDestinationPolicyResult.Deny();
            }
        }

        return WebhookDestinationPolicyResult.Allow();
    }

    private bool IsAllowedHost(Uri destination)
    {
        return allowedHosts.Contains(destination.Host)
            || allowedHosts.Contains(destination.IdnHost);
    }

    private static bool IsRestrictedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address)
            || IPAddress.Any.Equals(address)
            || IPAddress.IPv6Any.Equals(address)
            || IPAddress.Broadcast.Equals(address)
            || IPAddress.IPv6Loopback.Equals(address)
            || IPAddress.IPv6None.Equals(address))
        {
            return true;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsRestrictedIPv4(address),
            AddressFamily.InterNetworkV6 => IsRestrictedIPv6(address),
            _ => true,
        };
    }

    private static bool IsRestrictedIPv4(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();

        return bytes[0] == 0
            || bytes[0] == 10
            || bytes[0] == 127
            || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
            || (bytes[0] == 169 && bytes[1] == 254)
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
            || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2)
            || (bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19))
            || (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
            || (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
            || bytes[0] >= 224;
    }

    private static bool IsRestrictedIPv6(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();

        return address.IsIPv6LinkLocal
            || address.IsIPv6Multicast
            || (bytes[0] & 0xfe) == 0xfc
            || IsIPv6Range(bytes, [0x00, 0x64, 0xff, 0x9b, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], 96)
            || IsIPv6Range(bytes, [0x00, 0x64, 0xff, 0x9b, 0x00, 0x01], 48)
            || IsIPv6Range(bytes, [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], 64)
            || IsIPv6Range(bytes, [0x20, 0x01, 0x00], 23)
            || IsIPv6Range(bytes, [0x20, 0x01, 0x00, 0x02, 0x00, 0x00], 48)
            || IsIPv6Range(bytes, [0x20, 0x01, 0x0d, 0xb8], 32)
            || IsIPv6Range(bytes, [0x20, 0x02], 16);
    }

    private static bool IsIPv6Range(byte[] bytes, ReadOnlySpan<byte> prefix, int prefixLength)
    {
        int wholeBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;

        for (var index = 0; index < wholeBytes; index++)
        {
            if (bytes[index] != prefix[index])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        int mask = 0xff << (8 - remainingBits);
        return (bytes[wholeBytes] & mask) == (prefix[wholeBytes] & mask);
    }
}
