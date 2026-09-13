using Xunit;

namespace ReliableWebhooks.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GitHubPackagePublicationVisibilityCollection
{
    public const string Name = "GitHub package publication visibility";
}
