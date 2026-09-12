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
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] >= 224 && bytes[0] <= 239);
    }

    private static bool IsRestrictedIPv6(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();

        return address.IsIPv6LinkLocal
            || address.IsIPv6Multicast
            || (bytes[0] & 0xfe) == 0xfc;
    }
}
