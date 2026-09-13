using ReliableWebhooks;
using Xunit;

namespace ReliableWebhooks.Tests;

public sealed class PublicNetworkWebhookDestinationPolicyTests
{
    [Theory]
    [InlineData("http://127.0.0.1/webhooks")]
    [InlineData("http://10.0.0.1/webhooks")]
    [InlineData("http://172.16.0.1/webhooks")]
    [InlineData("http://192.168.1.1/webhooks")]
    [InlineData("http://169.254.1.1/webhooks")]
    [InlineData("http://0.0.0.0/webhooks")]
    [InlineData("http://100.64.0.1/webhooks")]
    [InlineData("http://192.0.0.1/webhooks")]
    [InlineData("http://192.0.2.1/webhooks")]
    [InlineData("http://192.88.99.1/webhooks")]
    [InlineData("http://224.0.0.1/webhooks")]
    [InlineData("http://240.0.0.1/webhooks")]
    [InlineData("http://198.18.0.1/webhooks")]
    [InlineData("http://198.51.100.1/webhooks")]
    [InlineData("http://203.0.113.1/webhooks")]
    [InlineData("http://[::1]/webhooks")]
    [InlineData("http://[64:ff9b::1]/webhooks")]
    [InlineData("http://[64:ff9b:1::1]/webhooks")]
    [InlineData("http://[100::1]/webhooks")]
    [InlineData("http://[2001:2::1]/webhooks")]
    [InlineData("http://[2001:db8::1]/webhooks")]
    [InlineData("http://[2002::1]/webhooks")]
    [InlineData("http://[fe80::1]/webhooks")]
    [InlineData("http://[fc00::1]/webhooks")]
    [InlineData("http://[ff02::1]/webhooks")]
    public async Task AuthorizeAsyncDeniesRestrictedIpLiterals(string destination)
    {
        PublicNetworkWebhookDestinationPolicy policy = new();

        WebhookDestinationPolicyResult result = await policy.AuthorizeAsync(
            new Uri(destination),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsAllowed);
    }

    [Theory]
    [InlineData("https://8.8.8.8/webhooks")]
    [InlineData("https://[2606:4700:4700::1111]/webhooks")]
    public async Task AuthorizeAsyncAllowsPublicIpLiterals(string destination)
    {
        PublicNetworkWebhookDestinationPolicy policy = new();

        WebhookDestinationPolicyResult result = await policy.AuthorizeAsync(
            new Uri(destination),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task AuthorizeAsyncDeniesDnsNameThatResolvesToLoopback()
    {
        PublicNetworkWebhookDestinationPolicy policy = new();

        WebhookDestinationPolicyResult result = await policy.AuthorizeAsync(
            new Uri("http://localhost/webhooks"),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task AuthorizeAsyncAllowsExplicitlyAllowListedPrivateTarget()
    {
        PublicNetworkWebhookDestinationPolicy policy = new(["localhost"]);

        WebhookDestinationPolicyResult result = await policy.AuthorizeAsync(
            new Uri("http://localhost/webhooks"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task AuthorizeAsyncObservesCancellationBeforeDnsResolution()
    {
        PublicNetworkWebhookDestinationPolicy policy = new();
        using CancellationTokenSource source = new();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await policy.AuthorizeAsync(new Uri("https://example.test/webhooks"), source.Token));
    }
}
