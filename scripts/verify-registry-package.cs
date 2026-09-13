#:property RestorePackagesWithLockFile=false

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

try
{
    return await RunAsync(args);
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0)
    {
        PrintUsage();
        return 1;
    }

    var options = ParseOptions(args);
    var source = Required(options, "--source");
    var packagePath = Path.GetFullPath(Required(options, "--package"));
    var packageId = Required(options, "--package-id");
    var version = Required(options, "--version");
    var allowMissing = options.ContainsKey("--allow-missing");
    var retryCount = ParseNonNegativeInt(options.GetValueOrDefault("--retry-count"), "--retry-count");
    var retryDelaySeconds = ParseNonNegativeInt(options.GetValueOrDefault("--retry-delay-seconds"), "--retry-delay-seconds");

    if (!File.Exists(packagePath))
    {
        throw new FileNotFoundException("Validated package was not found.", packagePath);
    }

    using HttpClient client = CreateClient(options);
    Uri packageBaseAddress = await ResolvePackageBaseAddressAsync(client, source).ConfigureAwait(false);
    Uri remotePackageUri = CreateFlatContainerPackageUri(packageBaseAddress, packageId, version);
    byte[] expectedPackage = await File.ReadAllBytesAsync(packagePath).ConfigureAwait(false);
    string expectedArchiveHash = ComputeSha256(expectedPackage);
    string expectedContentHash = ComputePackageContentSha256(expectedPackage);
    byte[]? remotePackage = await DownloadWithRetryAsync(
        client,
        remotePackageUri,
        retryCount,
        TimeSpan.FromSeconds(retryDelaySeconds)).ConfigureAwait(false);

    if (remotePackage is null)
    {
        if (allowMissing)
        {
            WriteStatus("missing");
            Console.WriteLine($"Registry package is absent: {packageId} {version}");
            return 0;
        }

        throw new InvalidOperationException($"Registry package was not found: {remotePackageUri}");
    }

    string actualArchiveHash = ComputeSha256(remotePackage);
    string actualContentHash = ComputePackageContentSha256(remotePackage);
    if (!string.Equals(expectedContentHash, actualContentHash, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"Registry package content mismatch for {packageId} {version}: expected {expectedContentHash}, got {actualContentHash}. "
            + $"Archive SHA256 expected {expectedArchiveHash}, got {actualArchiveHash}.");
    }

    WriteStatus("exact");
    Console.WriteLine($"Registry package verified: {packageId} {version}");
    Console.WriteLine($"Package content SHA256: {actualContentHash}");
    Console.WriteLine($"Archive SHA256: {actualArchiveHash}");
    return 0;
}

static HttpClient CreateClient(IReadOnlyDictionary<string, string> options)
{
    var client = new HttpClient();

    if (options.TryGetValue("--token-env", out string? tokenEnv))
    {
        string? token = Environment.GetEnvironmentVariable(tokenEnv);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException($"Environment variable '{tokenEnv}' is required for registry authentication.");
        }

        string username = options.GetValueOrDefault("--username") ?? "token";
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{token}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encoded);
    }

    return client;
}

static async Task<Uri> ResolvePackageBaseAddressAsync(HttpClient client, string source)
{
    string serviceIndexJson = await ReadUriStringAsync(client, new Uri(source, UriKind.Absolute)).ConfigureAwait(false);
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

static Uri CreateFlatContainerPackageUri(Uri packageBaseAddress, string packageId, string version)
{
    string normalizedId = packageId.ToLowerInvariant();
    string normalizedVersion = version.ToLowerInvariant();
    string relative = $"{normalizedId}/{normalizedVersion}/{normalizedId}.{normalizedVersion}.nupkg";
    return new Uri(packageBaseAddress, relative);
}

static async Task<byte[]?> DownloadWithRetryAsync(
    HttpClient client,
    Uri uri,
    int retryCount,
    TimeSpan retryDelay)
{
    for (var attempt = 0; ; attempt++)
    {
        byte[]? result = await ReadUriBytesOrMissingAsync(client, uri).ConfigureAwait(false);
        if (result is not null || attempt >= retryCount)
        {
            return result;
        }

        await Task.Delay(retryDelay).ConfigureAwait(false);
    }
}

static async Task<string> ReadUriStringAsync(HttpClient client, Uri uri)
{
    if (uri.IsFile)
    {
        return await File.ReadAllTextAsync(uri.LocalPath).ConfigureAwait(false);
    }

    using HttpResponseMessage response = await client.GetAsync(uri).ConfigureAwait(false);
    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
    {
        throw new InvalidOperationException($"Registry authentication failed for {uri}: HTTP {(int)response.StatusCode}.");
    }

    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
}

static async Task<byte[]?> ReadUriBytesOrMissingAsync(HttpClient client, Uri uri)
{
    if (uri.IsFile)
    {
        return File.Exists(uri.LocalPath)
            ? await File.ReadAllBytesAsync(uri.LocalPath).ConfigureAwait(false)
            : null;
    }

    using HttpResponseMessage response = await client.GetAsync(uri).ConfigureAwait(false);
    if (response.StatusCode == HttpStatusCode.NotFound)
    {
        return null;
    }

    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
    {
        throw new InvalidOperationException($"Registry authentication failed for {uri}: HTTP {(int)response.StatusCode}.");
    }

    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
}

static string ComputeSha256(byte[] bytes)
{
    return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

static string ComputePackageContentSha256(byte[] packageBytes)
{
    using MemoryStream stream = new(packageBytes);
    using ZipArchive archive = new(stream, ZipArchiveMode.Read);
    using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    foreach (ZipArchiveEntry entry in archive.Entries
        .Where(static entry => !IsDirectoryEntry(entry) && !IsRepositorySignatureEntry(entry.FullName))
        .OrderBy(static entry => entry.FullName, StringComparer.Ordinal))
    {
        AppendUtf8(hash, entry.FullName.Replace('\\', '/'));
        AppendLength(hash, entry.Length);

        using Stream entryStream = entry.Open();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer.AsSpan(0, read));
        }
    }

    return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
}

static bool IsRepositorySignatureEntry(string entryName)
{
    return string.Equals(entryName, ".signature.p7s", StringComparison.OrdinalIgnoreCase);
}

static bool IsDirectoryEntry(ZipArchiveEntry entry)
{
    return entry.FullName.EndsWith('/')
        || entry.FullName.EndsWith('\\');
}

static void AppendUtf8(IncrementalHash hash, string value)
{
    byte[] bytes = Encoding.UTF8.GetBytes(value);
    AppendLength(hash, bytes.Length);
    hash.AppendData(bytes);
}

static void AppendLength(IncrementalHash hash, long value)
{
    Span<byte> buffer = stackalloc byte[sizeof(long)];
    System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(buffer, value);
    hash.AppendData(buffer);
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var values = new Dictionary<string, string>(StringComparer.Ordinal);

    for (var index = 0; index < args.Length; index++)
    {
        string option = args[index];
        if (option == "--allow-missing")
        {
            values[option] = "true";
            continue;
        }

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

static int ParseNonNegativeInt(string? value, string option)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return 0;
    }

    if (!int.TryParse(value, out int parsed) || parsed < 0)
    {
        throw new ArgumentException($"{option} must be a non-negative integer.");
    }

    return parsed;
}

static void WriteStatus(string status)
{
    Console.WriteLine($"Registry package status: {status}");

    string? outputPath = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
    if (!string.IsNullOrWhiteSpace(outputPath))
    {
        File.AppendAllText(outputPath, $"status={status}{Environment.NewLine}");
    }
}

static void PrintUsage()
{
    Console.Error.WriteLine(
        "Usage: dotnet run --file scripts/verify-registry-package.cs -- "
        + "--source <v3-index> --package <nupkg> --package-id <id> --version <version> "
        + "[--allow-missing] [--username <user>] [--token-env <env>] "
        + "[--retry-count <count>] [--retry-delay-seconds <seconds>]");
}
