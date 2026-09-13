using System.Diagnostics;
using System.IO.Compression;
using Xunit;

namespace ReliableWebhooks.Tests;

public sealed class ReleasePublicationHelperTests
{
    private const string PackageId = "ReliableWebhooks";
    private const string Version = "1.0.0";

    [Fact]
    public async Task NuGetMissingVersionPublishesPrimaryAndSymbols()
    {
        using TempDirectory temp = new();
        TestPackages packages = CreatePackages(temp.Path);
        string pushLog = CreateSuccessfulDotNetStub(temp.Path);

        CommandResult result = await RunPublishHelperAsync(
            temp.Path,
            "NuGetOrg",
            packages.LocalPackage,
            packages.SymbolsPackage,
            "404,200",
            packages.LocalPackage,
            includeNuGetUser: true);

        Assert.Equal(0, result.ExitCode);
        string log = await File.ReadAllTextAsync(pushLog, TestContext.Current.CancellationToken);
        Assert.Contains($"{PackageId}.{Version}.nupkg", log, StringComparison.Ordinal);
        Assert.Contains($"{PackageId}.{Version}.snupkg", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NuGetExistingIdenticalVersionIsAcceptedWithoutPrimaryPush()
    {
        using TempDirectory temp = new();
        TestPackages packages = CreatePackages(temp.Path);
        string signedRemote = Path.Combine(temp.Path, "remote-signed.nupkg");
        WritePackage(signedRemote, signed: true, payload: "local");
        string pushLog = CreateSuccessfulDotNetStub(temp.Path);

        CommandResult result = await RunPublishHelperAsync(
            temp.Path,
            "NuGetOrg",
            packages.LocalPackage,
            symbolsPackage: null,
            "200",
            signedRemote,
            includeNuGetUser: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("validated existing", result.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(pushLog, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NuGetExistingDifferentVersionFailsClosed()
    {
        using TempDirectory temp = new();
        TestPackages packages = CreatePackages(temp.Path);
        string remote = Path.Combine(temp.Path, "remote-different.nupkg");
        WritePackage(remote, signed: false, payload: "different");
        _ = CreateSuccessfulDotNetStub(temp.Path);

        CommandResult result = await RunPublishHelperAsync(
            temp.Path,
            "NuGetOrg",
            packages.LocalPackage,
            symbolsPackage: null,
            "200",
            remote,
            includeNuGetUser: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not match the validated artifact", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NuGetTemporaryNotFoundAfterPushWaitsUntilConvergence()
    {
        using TempDirectory temp = new();
        TestPackages packages = CreatePackages(temp.Path);
        _ = CreateSuccessfulDotNetStub(temp.Path);

        CommandResult result = await RunPublishHelperAsync(
            temp.Path,
            "NuGetOrg",
            packages.LocalPackage,
            packages.SymbolsPackage,
            "404,404,200",
            packages.LocalPackage,
            includeNuGetUser: true,
            convergenceAttempts: 3);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("indexing is still pending", result.Output, StringComparison.Ordinal);
        Assert.Contains("converged and matches", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NuGetConvergenceTimeoutFails()
    {
        using TempDirectory temp = new();
        TestPackages packages = CreatePackages(temp.Path);
        _ = CreateSuccessfulDotNetStub(temp.Path);

        CommandResult result = await RunPublishHelperAsync(
            temp.Path,
            "NuGetOrg",
            packages.LocalPackage,
            symbolsPackage: null,
            "404,404,404",
            packages.LocalPackage,
            includeNuGetUser: true,
            convergenceAttempts: 2);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("convergence timed out", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedRegistryResponseFailsClosed()
    {
        using TempDirectory temp = new();
        TestPackages packages = CreatePackages(temp.Path);
        _ = CreateSuccessfulDotNetStub(temp.Path);

        CommandResult result = await RunPublishHelperAsync(
            temp.Path,
            "NuGetOrg",
            packages.LocalPackage,
            symbolsPackage: null,
            "500",
            packages.LocalPackage,
            includeNuGetUser: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unexpected registry package response HTTP 500", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHubPackagesMissingVersionPublishesAndWaitsForVisibility()
    {
        using TempDirectory temp = new();
        TestPackages packages = CreatePackages(temp.Path);
        string pushLog = CreateSuccessfulDotNetStub(temp.Path);

        CommandResult result = await RunPublishHelperAsync(
            temp.Path,
            "GitHubPackages",
            packages.LocalPackage,
            symbolsPackage: null,
            "404,200",
            packages.LocalPackage);

        Assert.Equal(0, result.ExitCode);
        string log = await File.ReadAllTextAsync(pushLog, TestContext.Current.CancellationToken);
        Assert.Contains($"{PackageId}.{Version}.nupkg", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHubPackagesExistingIdenticalVersionIsAcceptedOnRerun()
    {
        using TempDirectory temp = new();
        TestPackages packages = CreatePackages(temp.Path);
        string pushLog = CreateSuccessfulDotNetStub(temp.Path);

        CommandResult result = await RunPublishHelperAsync(
            temp.Path,
            "GitHubPackages",
            packages.LocalPackage,
            symbolsPackage: null,
            "200",
            packages.LocalPackage);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("validated existing", result.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(pushLog, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NuGetOfficialPublicationRequiresNuGetUser()
    {
        using TempDirectory temp = new();
        TestPackages packages = CreatePackages(temp.Path);
        _ = CreateSuccessfulDotNetStub(temp.Path);

        CommandResult result = await RunPublishHelperAsync(
            temp.Path,
            "NuGetOrg",
            packages.LocalPackage,
            symbolsPackage: null,
            "404",
            packages.LocalPackage,
            includeNuGetUser: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("NUGET_USER is required", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingTagIsCreatedAtValidatedSha()
    {
        using GitFixture git = await GitFixture.CreateAsync();

        CommandResult result = await git.RunTagHelperAsync("v1.0.0", git.HeadSha);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(git.HeadSha, await git.GetTagShaAsync("v1.0.0"));
    }

    [Fact]
    public async Task ExistingTagAtSameShaIsAccepted()
    {
        using GitFixture git = await GitFixture.CreateAsync();
        await git.CreateTagAsync("v1.0.0", git.HeadSha);

        CommandResult result = await git.RunTagHelperAsync("v1.0.0", git.HeadSha);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("already points to the validated commit", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingTagAtDifferentShaFails()
    {
        using GitFixture git = await GitFixture.CreateAsync();
        string firstSha = git.HeadSha;
        string secondSha = await git.CommitAsync("second");
        await git.CreateTagAsync("v1.0.0", firstSha);

        CommandResult result = await git.RunTagHelperAsync("v1.0.0", secondSha);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Refusing to move it", result.Output, StringComparison.Ordinal);
    }

    private static TestPackages CreatePackages(string directory)
    {
        string package = Path.Combine(directory, $"{PackageId}.{Version}.nupkg");
        string symbols = Path.Combine(directory, $"{PackageId}.{Version}.snupkg");
        WritePackage(package, signed: false, payload: "local");
        WritePackage(symbols, signed: false, payload: "symbols");
        return new TestPackages(package, symbols);
    }

    private static void WritePackage(string path, bool signed, string payload)
    {
        using FileStream stream = File.Create(path);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create);
        WriteEntry(archive, $"{PackageId}.nuspec", "<package />");
        WriteEntry(archive, "lib/net10.0/ReliableWebhooks.dll", payload);

        if (signed)
        {
            WriteEntry(archive, "[Content_Types].xml", "signed content types");
            WriteEntry(archive, "_rels/.rels", "signed relationships");
            WriteEntry(archive, ".signature.p7s", "signature");
            WriteEntry(archive, "package/services/digital-signature/origin.psmdcp", "signature origin");
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        using StreamWriter writer = new(entry.Open());
        writer.Write(content);
    }

    private static string CreateSuccessfulDotNetStub(string directory)
    {
        string log = Path.Combine(directory, "push.log");
        File.WriteAllText(log, string.Empty);

        if (OperatingSystem.IsWindows())
        {
            string stub = Path.Combine(directory, "dotnet-stub.cmd");
            File.WriteAllText(
                stub,
                """
                @echo off
                echo %*>> "%PUSH_LOG%"
                exit /b 0
                """);
            return log;
        }
        else
        {
            string stub = Path.Combine(directory, "dotnet-stub.sh");
            File.WriteAllText(
                stub,
                """
                #!/usr/bin/env bash
                printf '%s\n' "$*" >> "$PUSH_LOG"
                exit 0
                """);
            File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return log;
        }
    }

    private static string GetDotNetStubPath(string directory)
    {
        return OperatingSystem.IsWindows()
            ? Path.Combine(directory, "dotnet-stub.cmd")
            : Path.Combine(directory, "dotnet-stub.sh");
    }

    private static async Task<CommandResult> RunPublishHelperAsync(
        string tempDirectory,
        string registry,
        string package,
        string? symbolsPackage,
        string statuses,
        string remotePackage,
        bool includeNuGetUser = true,
        int convergenceAttempts = 3)
    {
        string repoRoot = FindRepoRoot();
        List<string> arguments =
        [
            "run",
            "--file",
            Path.Combine(repoRoot, "scripts", "publish-release-package.cs"),
            "--",
            "--registry",
            registry,
            "--source",
            "mock://registry/index.json",
            "--package",
            package,
            "--package-id",
            PackageId,
            "--version",
            Version,
            "--api-key-env",
            "TEST_API_KEY",
            "--convergence-attempts",
            convergenceAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--convergence-delay-seconds",
            "0"
        ];

        if (symbolsPackage is not null)
        {
            arguments.Add("--symbols");
            arguments.Add(symbolsPackage);
        }

        if (registry == "NuGetOrg")
        {
            arguments.Add("--nuget-user-env");
            arguments.Add("TEST_NUGET_USER");
        }
        else
        {
            arguments.Add("--username");
            arguments.Add("owner");
            arguments.Add("--token-env");
            arguments.Add("TEST_GITHUB_TOKEN");
        }

        Dictionary<string, string?> environment = new(StringComparer.Ordinal)
        {
            ["RELIABLEWEBHOOKS_DOTNET_COMMAND"] = GetDotNetStubPath(tempDirectory),
            ["RELIABLEWEBHOOKS_MOCK_PACKAGE_STATUSES"] = statuses,
            ["RELIABLEWEBHOOKS_MOCK_REMOTE_PACKAGE"] = remotePackage,
            ["TEST_API_KEY"] = "api-key",
            ["TEST_GITHUB_TOKEN"] = "github-token",
            ["PUSH_LOG"] = Path.Combine(tempDirectory, "push.log")
        };

        if (includeNuGetUser)
        {
            environment["TEST_NUGET_USER"] = "rodri-oliveira-dev";
        }

        return await RunCommandAsync("dotnet", arguments, repoRoot, environment);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ReliableWebhooks.slnx"))
                && Directory.Exists(Path.Combine(directory.FullName, "scripts")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root could not be resolved.");
    }

    private static async Task<CommandResult> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;

        if (environment is not null)
        {
            foreach ((string key, string? value) in environment)
            {
                process.StartInfo.Environment[key] = value;
            }
        }

        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        string error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new CommandResult(process.ExitCode, output + error);
    }

    private sealed record TestPackages(string LocalPackage, string SymbolsPackage);

    private sealed record CommandResult(int ExitCode, string Output);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"reliablewebhooks-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path
        {
            get;
        }

        public void Dispose()
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(Path))
                    {
                        foreach (string file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                        }

                        foreach (string directory in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories))
                        {
                            File.SetAttributes(directory, FileAttributes.Normal);
                        }
                    }

                    Directory.Delete(Path, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }

    private sealed class GitFixture : IDisposable
    {
        private readonly TempDirectory temp;

        private GitFixture(TempDirectory temp, string workTree, string headSha)
        {
            this.temp = temp;
            WorkTree = workTree;
            HeadSha = headSha;
        }

        public string WorkTree
        {
            get;
        }

        public string HeadSha
        {
            get; private set;
        }

        public static async Task<GitFixture> CreateAsync()
        {
            TempDirectory temp = new();
            string remote = Path.Combine(temp.Path, "remote.git");
            string workTree = Path.Combine(temp.Path, "work");
            await RunCommandAsync("git", ["init", "--bare", "--initial-branch=main", remote], temp.Path);
            await RunCommandAsync("git", ["clone", remote, workTree], temp.Path);
            await RunCommandAsync("git", ["config", "user.email", "tests@example.invalid"], workTree);
            await RunCommandAsync("git", ["config", "user.name", "Release Tests"], workTree);
            string headSha = await CommitFileAsync(workTree, "initial");
            await RunCommandAsync("git", ["push", "origin", "HEAD:main"], workTree);
            return new GitFixture(temp, workTree, headSha);
        }

        public async Task<string> CommitAsync(string content)
        {
            HeadSha = await CommitFileAsync(WorkTree, content);
            await RunCommandAsync("git", ["push", "origin", "HEAD:main"], WorkTree);
            return HeadSha;
        }

        public async Task CreateTagAsync(string tag, string sha)
        {
            await RunCommandAsync("git", ["tag", tag, sha], WorkTree);
            await RunCommandAsync("git", ["push", "origin", $"refs/tags/{tag}"], WorkTree);
        }

        public async Task<string> GetTagShaAsync(string tag)
        {
            CommandResult result = await RunCommandAsync("git", ["rev-list", "-n", "1", tag], WorkTree);
            Assert.Equal(0, result.ExitCode);
            return result.Output.Trim();
        }

        public Task<CommandResult> RunTagHelperAsync(string tag, string sha)
        {
            string script = Path.Combine(FindRepoRoot(), "scripts", "ensure-release-tag.sh");
            return RunCommandAsync(GetBashCommand(), [script, "--tag", tag, "--sha", sha], WorkTree);
        }

        public void Dispose()
        {
            temp.Dispose();
        }

        private static async Task<string> CommitFileAsync(string workTree, string content)
        {
            await File.WriteAllTextAsync(Path.Combine(workTree, "file.txt"), content, TestContext.Current.CancellationToken);
            await RunCommandAsync("git", ["add", "file.txt"], workTree);
            await RunCommandAsync("git", ["commit", "-m", content], workTree);
            CommandResult result = await RunCommandAsync("git", ["rev-parse", "HEAD"], workTree);
            Assert.Equal(0, result.ExitCode);
            return result.Output.Trim();
        }

        private static string GetBashCommand()
        {
            if (!OperatingSystem.IsWindows())
            {
                return "bash";
            }

            string gitBash = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Git",
                "bin",
                "bash.exe");
            return File.Exists(gitBash) ? gitBash : "bash";
        }
    }
}
