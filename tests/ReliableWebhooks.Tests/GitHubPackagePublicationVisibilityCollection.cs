using Xunit;

namespace ReliableWebhooks.Tests;

[CollectionDefinition("GitHub package publication visibility", DisableParallelization = true)]
public sealed class GitHubPackagePublicationVisibilityCollection
{
    public const string Name = "GitHub package publication visibility";
}
