#:property RestorePackagesWithLockFile=false
#pragma warning disable CA1050 // File-based apps keep helper types at top level.

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

try
{
    return await RunAsync(args).ConfigureAwait(false);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Package publication failed: {exception.Message}");
    return 1;
}

static async Task<int> RunAsync(string[] args)
{
    var options = ParseOptions(args);
    var registry = Required(options, "--registry");
    var source = Required(options, "--source");
    var packagePath = Path.GetFullPath(Required(options, "--package"));
    var packageId = Required(options, "--package-id");
    var version = Required(options, "--version");
    var apiKey = RequiredEnvironmentVariable(Required(options, "--api-key-env"));
    var convergenceAttempts = ParsePositiveInt(options.GetValueOrDefault("--convergence-attempts"), "--convergence-attempts", 60);
    var convergenceDelay = TimeSpan.FromSeconds(ParseNonNegativeInt(options.GetValueOrDefault("--convergence-delay-seconds"), "--convergence-delay-seconds", 10));

    if (!File.Exists(packagePath))
    {
        throw new FileNotFoundException("Validated package was not found.", packagePath);
    }

    IRegistryClient client = CreateRegistryClient(options, source);
    Uri packageBaseAddress = await client.ResolvePackageBaseAddressAsync(source).ConfigureAwait(false);
    Uri packageUri = CreateFlatContainerPackageUri(packageBaseAddress, packageId, version);
    var dotnetCommand = Environment.GetEnvironmentVariable("RELIABLEWEBHOOKS_DOTNET_COMMAND");
    if (string.IsNullOrWhiteSpace(dotnetCommand))
    {
        dotnetCommand = "dotnet";
    }

    switch (registry)
    {
        case "NuGetOrg":
            RequiredEnvironmentVariable(options.GetValueOrDefault("--nuget-user-env"), "NUGET_USER is required for official NuGet.org publication.");
            await PublishNuGetOrgAsync(
                client,
                dotnetCommand,
                source,
                apiKey,
                packageId,
                version,
                packagePath,
                packageUri,
                options.GetValueOrDefault("--symbols"),
                convergenceAttempts,
                convergenceDelay).ConfigureAwait(false);
            return 0;

        case "GitHubPackages":
            IGitHubPackageVersionClient versionClient = CreateGitHubPackageVersionClient(options, source);
            await PublishGitHubPackagesAsync(
                client,
                versionClient,
                dotnetCommand,
                source,
                apiKey,
                packageId,
                version,
                packagePath,
                packageUri,
                convergenceAttempts,
                convergenceDelay).ConfigureAwait(false);
            return 0;

        default:
            throw new ArgumentException($"Unsupported registry '{registry}'. Expected NuGetOrg or GitHubPackages.");
    }
}

static async Task PublishNuGetOrgAsync(
    IRegistryClient client,
    string dotnetCommand,
    string source,
    string apiKey,
    string packageId,
    string version,
    string packagePath,
    Uri packageUri,
    string? symbolsPath,
    int convergenceAttempts,
    TimeSpan convergenceDelay)
{
    PackageLookup lookup = await client.GetPackageAsync(packageUri).ConfigureAwait(false);
    if (lookup.PackageBytes is not null)
    {
        AssertPackagesHaveSameContent(packageId, version, packagePath, lookup.PackageBytes, "NuGet.org");
        Console.WriteLine($"NuGet.org: validated existing {packageId} {version}; skipping primary package push.");
    }
    else
    {
        Console.WriteLine($"NuGet.org: {packageId} {version} is not published; submitting the validated primary package.");
        await InvokeDotNetNuGetPushAsync(
            dotnetCommand,
            source,
            apiKey,
            packageId,
            version,
            packagePath,
            "--no-symbols",
            "--skip-duplicate").ConfigureAwait(false);
        await WaitForRegistryPackageAsync(
            client,
            packageUri,
            packageId,
            version,
            packagePath,
            "NuGet.org",
            convergenceAttempts,
            convergenceDelay).ConfigureAwait(false);
    }

    if (!string.IsNullOrWhiteSpace(symbolsPath))
    {
        var fullSymbolsPath = Path.GetFullPath(symbolsPath);
        if (!File.Exists(fullSymbolsPath))
        {
            throw new FileNotFoundException("Validated symbol package was not found.", fullSymbolsPath);
        }

        Console.WriteLine("NuGet.org: submitting symbol package with duplicate-safe NuGet tooling semantics.");
        await InvokeDotNetNuGetPushAsync(
            dotnetCommand,
            source,
            apiKey,
            $"{packageId} symbols",
            version,
            fullSymbolsPath,
            "--skip-duplicate").ConfigureAwait(false);
    }

    Console.WriteLine($"NuGet.org publication completed for {packageId} {version}.");
}

static async Task PublishGitHubPackagesAsync(
    IRegistryClient client,
    IGitHubPackageVersionClient versionClient,
    string dotnetCommand,
    string source,
    string apiKey,
    string packageId,
    string version,
    string packagePath,
    Uri packageUri,
    int convergenceAttempts,
    TimeSpan convergenceDelay)
{
    if (await versionClient.VersionExistsAsync(packageId, version).ConfigureAwait(false))
    {
        PackageLookup lookup = await client.GetPackageAsync(packageUri).ConfigureAwait(false);
        if (lookup.PackageBytes is null)
        {
            throw new InvalidOperationException(
                $"GitHub Packages reports {packageId} {version} as published, but the package content is not available for artifact comparison.");
        }

        AssertPackagesHaveSameContent(packageId, version, packagePath, lookup.PackageBytes, "GitHub Packages");
        Console.WriteLine($"GitHub Packages: validated existing {packageId} {version}; skipping package push.");
        return;
    }

    Console.WriteLine($"GitHub Packages: {packageId} {version} is not currently published; submitting the validated package.");
    await InvokeDotNetNuGetPushAsync(
        dotnetCommand,
        source,
        apiKey,
        packageId,
        version,
        packagePath).ConfigureAwait(false);

    await WaitForGitHubPackageVisibleAsync(
        versionClient,
        packageId,
        version,
        convergenceAttempts,
        convergenceDelay).ConfigureAwait(false);

    Console.WriteLine($"GitHub Packages publication completed for {packageId} {version}.");
}

static async Task WaitForRegistryPackageAsync(
    IRegistryClient client,
    Uri packageUri,
    string packageId,
    string version,
    string packagePath,
    string registryLabel,
    int maxAttempts,
    TimeSpan delay)
{
    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        PackageLookup lookup = await client.GetPackageAsync(packageUri).ConfigureAwait(false);
        if (lookup.PackageBytes is not null)
        {
            AssertPackagesHaveSameContent(packageId, version, packagePath, lookup.PackageBytes, registryLabel);
            Console.WriteLine($"{registryLabel}: {packageId} {version} converged and matches the validated artifact.");
            return;
        }

        if (attempt < maxAttempts)
        {
            Console.WriteLine($"{registryLabel}: indexing is still pending for {packageId} {version} ({attempt}/{maxAttempts}); retrying in {delay.TotalSeconds:0} seconds.");
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay).ConfigureAwait(false);
            }
        }
    }

    throw new TimeoutException($"{registryLabel} package convergence timed out for {packageId} {version} after {maxAttempts} attempts.");
}

static async Task WaitForGitHubPackageVisibleAsync(
    IGitHubPackageVersionClient versionClient,
    string packageId,
    string version,
    int maxAttempts,
    TimeSpan delay)
{
    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        if (await versionClient.VersionExistsAsync(packageId, version).ConfigureAwait(false))
        {
            Console.WriteLine($"GitHub Packages: verified {packageId} {version} after publication.");
            return;
        }

        if (attempt < maxAttempts)
        {
            Console.WriteLine($"GitHub Packages: indexing is still pending for {packageId} {version} ({attempt}/{maxAttempts}); retrying in {delay.TotalSeconds:0} seconds.");
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay).ConfigureAwait(false);
            }
        }
    }

    throw new TimeoutException($"GitHub Packages package convergence timed out for {packageId} {version} after {maxAttempts} attempts.");
}

static async Task InvokeDotNetNuGetPushAsync(
    string dotnetCommand,
    string source,
    string apiKey,
    string packageId,
    string version,
    string packagePath,
    params string[] additionalArguments)
{
    var arguments = new List<string>
    {
        "nuget",
        "push",
        packagePath,
        "--source",
        source,
        "--api-key",
        apiKey
    };
    arguments.AddRange(additionalArguments);

    Console.WriteLine($"> dotnet nuget push {Path.GetFileName(packagePath)} --source {source} --api-key *** {string.Join(' ', additionalArguments)}");
    using var process = new Process();
    process.StartInfo.FileName = dotnetCommand;
    foreach (string argument in arguments)
    {
        process.StartInfo.ArgumentList.Add(argument);
    }

    process.StartInfo.RedirectStandardOutput = true;
    process.StartInfo.RedirectStandardError = true;
    process.StartInfo.UseShellExecute = false;
    process.Start();
    string standardOutput = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
    string standardError = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
    await process.WaitForExitAsync().ConfigureAwait(false);

    if (!string.IsNullOrWhiteSpace(standardOutput))
    {
        Console.WriteLine(standardOutput.TrimEnd());
    }

    if (!string.IsNullOrWhiteSpace(standardError))
    {
        Console.Error.WriteLine(standardError.TrimEnd());
    }

    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"{packageId} {version} publication command failed with exit code {process.ExitCode}.");
    }
}

static IRegistryClient CreateRegistryClient(IReadOnlyDictionary<string, string> options, string source)
{
    if (source.StartsWith("mock://", StringComparison.OrdinalIgnoreCase))
    {
        return new MockRegistryClient();
    }

    var client = new HttpClient();
    if (options.TryGetValue("--token-env", out string? tokenEnv))
    {
        string token = RequiredEnvironmentVariable(tokenEnv);
        string username = options.GetValueOrDefault("--username") ?? "token";
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{token}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encoded);
    }

    return new HttpRegistryClient(client);
}

static IGitHubPackageVersionClient CreateGitHubPackageVersionClient(IReadOnlyDictionary<string, string> options, string source)
{
    if (source.StartsWith("mock://", StringComparison.OrdinalIgnoreCase))
    {
        return new MockGitHubPackageVersionClient();
    }

    string owner = Required(options, "--username");
    string token = RequiredEnvironmentVariable(Required(options, "--token-env"));
    return new HttpGitHubPackageVersionClient(new HttpClient(), owner, token);
}

static Uri CreateFlatContainerPackageUri(Uri packageBaseAddress, string packageId, string version)
{
    string normalizedId = packageId.ToLowerInvariant();
    string normalizedVersion = version.ToLowerInvariant();
    return new Uri(packageBaseAddress, $"{normalizedId}/{normalizedVersion}/{normalizedId}.{normalizedVersion}.nupkg");
}

static void AssertPackagesHaveSameContent(
    string packageId,
    string version,
    string localPackagePath,
    byte[] remotePackageBytes,
    string registryLabel)
{
    IReadOnlyDictionary<string, string> localEntries = GetPackageEntryHashes(File.ReadAllBytes(localPackagePath));
    IReadOnlyDictionary<string, string> remoteEntries = GetPackageEntryHashes(remotePackageBytes);
    string[] missingEntries = localEntries.Keys.Where(entry => !remoteEntries.ContainsKey(entry)).ToArray();
    string[] unexpectedEntries = remoteEntries.Keys.Where(entry => !localEntries.ContainsKey(entry)).ToArray();
    string[] changedEntries = localEntries
        .Where(entry => remoteEntries.TryGetValue(entry.Key, out string? hash) && !string.Equals(hash, entry.Value, StringComparison.Ordinal))
        .Select(entry => entry.Key)
        .ToArray();

    if (missingEntries.Length > 0 || unexpectedEntries.Length > 0 || changedEntries.Length > 0)
    {
        var details = new List<string>();
        if (missingEntries.Length > 0)
        {
            details.Add($"missing entries: {string.Join(", ", missingEntries)}");
        }

        if (unexpectedEntries.Length > 0)
        {
            details.Add($"unexpected entries: {string.Join(", ", unexpectedEntries)}");
        }

        if (changedEntries.Length > 0)
        {
            details.Add($"changed entries: {string.Join(", ", changedEntries)}");
        }

        throw new InvalidOperationException($"{registryLabel} already has {packageId} {version}, but its package content does not match the validated artifact ({string.Join("; ", details)}).");
    }
}

static IReadOnlyDictionary<string, string> GetPackageEntryHashes(byte[] packageBytes)
{
    using var stream = new MemoryStream(packageBytes);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
    var entries = new Dictionary<string, string>(StringComparer.Ordinal);

    foreach (ZipArchiveEntry entry in archive.Entries
        .Where(static entry => !IsDirectoryEntry(entry) && !IsRepositorySignatureEntry(entry.FullName))
        .OrderBy(static entry => entry.FullName, StringComparer.Ordinal))
    {
        string normalizedName = entry.FullName.Replace('\\', '/');
        using Stream entryStream = entry.Open();
        entries.Add(normalizedName, Convert.ToHexString(SHA256.HashData(entryStream)).ToLowerInvariant());
    }

    return entries;
}

static bool IsRepositorySignatureEntry(string entryName)
{
    string normalized = entryName.Replace('\\', '/');
    return normalized.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase)
        || normalized.Equals("_rels/.rels", StringComparison.OrdinalIgnoreCase)
        || normalized.Equals(".signature.p7s", StringComparison.OrdinalIgnoreCase)
        || normalized.StartsWith("package/services/digital-signature/", StringComparison.OrdinalIgnoreCase);
}

static bool IsDirectoryEntry(ZipArchiveEntry entry)
{
    return entry.FullName.Length > 0
        && (entry.FullName[^1] == '/' || entry.FullName[^1] == '\\');
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var values = new Dictionary<string, string>(StringComparer.Ordinal);

    for (var index = 0; index < args.Length; index++)
    {
        string option = args[index];
        if (!option.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
        {
            throw new ArgumentException($"Invalid argument near '{option}'.");
        }

        values[option] = args[++index];
    }

    return values;
}

static string Required(IReadOnlyDictionary<string, string> options, string name)
{
    if (!options.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value))
    {
        throw new ArgumentException($"Required option missing or empty: {name}");
    }

    return value;
}

static string RequiredEnvironmentVariable(string? name, string? message = null)
{
    if (string.IsNullOrWhiteSpace(name))
    {
        throw new ArgumentException(message ?? "Required environment variable name is missing.");
    }

    string? value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException(message ?? $"Environment variable '{name}' is required.");
    }

    return value;
}

static int ParsePositiveInt(string? value, string option, int defaultValue)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return defaultValue;
    }

    if (!int.TryParse(value, out int parsed) || parsed <= 0)
    {
        throw new ArgumentException($"{option} must be a positive integer.");
    }

    return parsed;
}

static int ParseNonNegativeInt(string? value, string option, int defaultValue)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return defaultValue;
    }

    if (!int.TryParse(value, out int parsed) || parsed < 0)
    {
        throw new ArgumentException($"{option} must be a non-negative integer.");
    }

    return parsed;
}

internal interface IRegistryClient
{
    Task<Uri> ResolvePackageBaseAddressAsync(string source);

    Task<PackageLookup> GetPackageAsync(Uri packageUri);
}

internal interface IGitHubPackageVersionClient
{
    Task<bool> VersionExistsAsync(string packageId, string version);
}

internal sealed record PackageLookup(byte[]? PackageBytes);

internal sealed class HttpRegistryClient(HttpClient client) : IRegistryClient
{
    public async Task<Uri> ResolvePackageBaseAddressAsync(string source)
    {
        using HttpResponseMessage response = await client.GetAsync(new Uri(source, UriKind.Absolute)).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException($"Registry authentication failed for {source}: HTTP {(int)response.StatusCode}.");
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"Unexpected registry service-index response HTTP {(int)response.StatusCode} for {source}.");
        }

        string serviceIndexJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(serviceIndexJson);
        foreach (JsonElement resource in document.RootElement.GetProperty("resources").EnumerateArray())
        {
            if (!resource.TryGetProperty("@type", out JsonElement typeElement)
                || !resource.TryGetProperty("@id", out JsonElement idElement))
            {
                continue;
            }

            string? type = typeElement.GetString();
            if (type is not null
                && type.Split('/', StringSplitOptions.TrimEntries).Contains("PackageBaseAddress", StringComparer.OrdinalIgnoreCase))
            {
                string id = idElement.GetString()
                    ?? throw new InvalidOperationException("PackageBaseAddress resource has an empty @id.");
                return new Uri(id, UriKind.Absolute);
            }
        }

        throw new InvalidOperationException($"PackageBaseAddress resource was not found in service index: {source}");
    }

    public async Task<PackageLookup> GetPackageAsync(Uri packageUri)
    {
        using HttpResponseMessage response = await client.GetAsync(packageUri).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new PackageLookup(null);
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException($"Registry authentication failed for {packageUri}: HTTP {(int)response.StatusCode}.");
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"Unexpected registry package response HTTP {(int)response.StatusCode} for {packageUri}.");
        }

        return new PackageLookup(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
    }
}

internal sealed class HttpGitHubPackageVersionClient : IGitHubPackageVersionClient
{
    private readonly HttpClient client;
    private readonly string owner;

    public HttpGitHubPackageVersionClient(HttpClient client, string owner, string token)
    {
        this.client = client;
        this.owner = owner;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ReliableWebhooks-release-publish");
    }

    public async Task<bool> VersionExistsAsync(string packageId, string version)
    {
        string escapedOwner = Uri.EscapeDataString(owner);
        string escapedPackageId = Uri.EscapeDataString(packageId);
        var page = 1;

        while (true)
        {
            var uri = new Uri(
                $"https://api.github.com/users/{escapedOwner}/packages/nuget/{escapedPackageId}/versions?per_page=100&page={page}",
                UriKind.Absolute);
            using HttpResponseMessage response = await client.GetAsync(uri).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new InvalidOperationException(
                    $"GitHub Packages version lookup authentication failed for {packageId}: HTTP {(int)response.StatusCode}.");
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException(
                    $"Unexpected GitHub Packages version lookup response HTTP {(int)response.StatusCode} for {packageId}.");
            }

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("GitHub Packages version lookup returned an unexpected JSON payload.");
            }

            foreach (JsonElement item in root.EnumerateArray())
            {
                if (item.TryGetProperty("name", out JsonElement nameElement)
                    && string.Equals(nameElement.GetString(), version, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            if (root.GetArrayLength() < 100)
            {
                return false;
            }

            page++;
        }
    }
}

internal sealed class MockRegistryClient : IRegistryClient
{
    private readonly Queue<int> statuses = new(
        (Environment.GetEnvironmentVariable("RELIABLEWEBHOOKS_MOCK_PACKAGE_STATUSES") ?? "404")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(static status => int.Parse(status, System.Globalization.CultureInfo.InvariantCulture)));

    public Task<Uri> ResolvePackageBaseAddressAsync(string source)
    {
        return Task.FromResult(new Uri("mock://registry/flat/"));
    }

    public Task<PackageLookup> GetPackageAsync(Uri packageUri)
    {
        int status = statuses.Count > 0 ? statuses.Dequeue() : 404;
        return status switch
        {
            200 => Task.FromResult(new PackageLookup(File.ReadAllBytes(RequiredMockEnvironmentVariable("RELIABLEWEBHOOKS_MOCK_REMOTE_PACKAGE")))),
            404 => Task.FromResult(new PackageLookup(null)),
            _ => throw new InvalidOperationException($"Unexpected registry package response HTTP {status} for {packageUri}.")
        };
    }

    private static string RequiredMockEnvironmentVariable(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Environment variable '{name}' is required.");
        }

        return value;
    }
}

internal sealed class MockGitHubPackageVersionClient : IGitHubPackageVersionClient
{
    private readonly Queue<int> statuses;
    private int lastStatus;

    public MockGitHubPackageVersionClient()
    {
        string rawStatuses = Environment.GetEnvironmentVariable("RELIABLEWEBHOOKS_MOCK_GITHUB_VERSION_STATUSES")
            ?? Environment.GetEnvironmentVariable("RELIABLEWEBHOOKS_MOCK_PACKAGE_STATUSES")
            ?? "404";
        statuses = new Queue<int>(
            rawStatuses
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static status => int.Parse(status, System.Globalization.CultureInfo.InvariantCulture)));
        lastStatus = statuses.Count > 0 ? statuses.Peek() : 404;
    }

    public Task<bool> VersionExistsAsync(string packageId, string version)
    {
        if (statuses.Count > 0)
        {
            lastStatus = statuses.Dequeue();
        }

        return lastStatus switch
        {
            200 => Task.FromResult(true),
            404 => Task.FromResult(false),
            _ => throw new InvalidOperationException(
                $"Unexpected GitHub Packages version lookup response HTTP {lastStatus} for {packageId}.")
        };
    }
}
