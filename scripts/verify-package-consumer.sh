#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <package-directory> <version>" >&2
  exit 1
fi

package_dir="$(realpath "$1")"
version="$2"
consumer="$(mktemp -d)"
trap 'rm -rf "$consumer"' EXIT

dotnet new console --framework net10.0 --output "$consumer" >/dev/null
project="$(find "$consumer" -maxdepth 1 -name '*.csproj' -print -quit)"
test -n "$project"

cat > "$consumer/NuGet.Config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-release" value="$package_dir" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
EOF

dotnet add "$project" package ReliableWebhooks \
  --version "$version" >/dev/null

dotnet add "$project" package Microsoft.Extensions.DependencyInjection \
  --version 10.0.12 >/dev/null

cat > "$consumer/Program.cs" <<'EOF'
using Microsoft.Extensions.DependencyInjection;
using ReliableWebhooks;

ServiceCollection services = new();
services.AddSingleton<SharedStore>();
services.AddScoped<IWebhookDeliveryStore, CustomStore>();
services.AddReliableWebhooks();

using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
{
    ValidateOnBuild = true,
    ValidateScopes = true,
});

using (IServiceScope scope = provider.CreateScope())
{
    if (scope.ServiceProvider.GetRequiredService<IWebhookDeliveryStore>() is not CustomStore)
    {
        throw new InvalidOperationException("Custom scoped IWebhookDeliveryStore registration was not preserved.");
    }
}

IWebhookEnqueueService enqueue = provider.GetRequiredService<IWebhookEnqueueService>();
_ = provider.GetRequiredService<WebhookDispatcher>();
_ = await enqueue.EnqueueAsync(new WebhookMessage(
    "consumer-scoped-store",
    "consumer.created",
    new Uri("https://example.test/webhooks"),
    "{}"u8.ToArray(),
    "application/json"));
Console.WriteLine("ReliableWebhooks scoped custom-store consumer resolved successfully.");

file sealed class CustomStore : IWebhookDeliveryStore
{
    private readonly SharedStore shared;

    public CustomStore(SharedStore shared)
    {
        this.shared = shared;
    }

    public Task<WebhookEnqueueResult> EnqueueAsync(
        WebhookMessage message,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default) =>
        shared.Inner.EnqueueAsync(message, nextAttemptAt, cancellationToken);

    public Task<IReadOnlyList<WebhookDeliveryLease>> ClaimDueAsync(
        DateTimeOffset now,
        TimeSpan leaseDuration,
        int maxCount,
        CancellationToken cancellationToken = default) =>
        shared.Inner.ClaimDueAsync(now, leaseDuration, maxCount, cancellationToken);

    public Task<WebhookDeliveryLease> RenewLeaseAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default) =>
        shared.Inner.RenewLeaseAsync(lease, now, leaseDuration, cancellationToken);

    public Task MarkSucceededAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default) =>
        shared.Inner.MarkSucceededAsync(lease, completedAt, cancellationToken);

    public Task ScheduleRetryAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        DateTimeOffset nextAttemptAt,
        string? lastError,
        CancellationToken cancellationToken = default) =>
        shared.Inner.ScheduleRetryAsync(lease, completedAt, nextAttemptAt, lastError, cancellationToken);

    public Task MarkPermanentlyFailedAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        string? lastError,
        CancellationToken cancellationToken = default) =>
        shared.Inner.MarkPermanentlyFailedAsync(lease, completedAt, lastError, cancellationToken);

    public Task DeadLetterAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        string? lastError,
        CancellationToken cancellationToken = default) =>
        shared.Inner.DeadLetterAsync(lease, completedAt, lastError, cancellationToken);

    public Task<WebhookDeliverySnapshot?> GetAsync(
        string webhookId,
        CancellationToken cancellationToken = default) =>
        shared.Inner.GetAsync(webhookId, cancellationToken);
}

file sealed class SharedStore
{
    public InMemoryWebhookDeliveryStore Inner { get; } = new();
}
EOF

dotnet run --project "$project" --configuration Release
