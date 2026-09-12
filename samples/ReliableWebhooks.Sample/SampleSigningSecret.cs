using ReliableWebhooks;

namespace ReliableWebhooks.Sample;

internal sealed class SampleSigningSecret
{
    private readonly byte[] value;

    public SampleSigningSecret(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0)
        {
            throw new ArgumentException("The sample signing secret cannot be empty.", nameof(value));
        }

        this.value = value.ToArray();
    }

    public ReadOnlyMemory<byte> Value => value;
}

internal sealed class SampleSigningSecretProvider : IWebhookSigningSecretProvider
{
    private readonly SampleSigningSecret signingSecret;

    public SampleSigningSecretProvider(SampleSigningSecret signingSecret)
    {
        this.signingSecret = signingSecret;
    }

    public ValueTask<ReadOnlyMemory<byte>> GetSecretAsync(
        WebhookMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(signingSecret.Value);
    }
}
