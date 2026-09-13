using System.Diagnostics;
using System.IO.Compression;
using Xunit;

namespace ReliableWebhooks.Tests;

[Collection("GitHub package publication visibility")]
public sealed class GitHubPackagesPublicationVisibilityTests
{
    [Fact]
    public async Task NewlyPublishedPackageUsesGitHubVersionApiForConvergence()
    {
        using TempDirectory temp = new();
        string package = Path.Combine(temp.Path, "ReliableWebhooks.1.0.0.nupkg");
        WritePackage(package);
        string dotnetStub = CreateSuccessfulDotNetStub(temp.Path);
        string repoRoot = FindRepoRoot();

        List<string> arguments =
        [
            "run",
            "--file",
            Path.Combine(repoRoot, "scripts", "publish-release-package.cs"),
            "--",
            "--registry",
            "GitHubPackages",
            "--source",
            "mock://registry/index.json",
            "--package",
            package,
            "--package-id",
            "ReliableWebhooks",
            "--version",
            "1.0.0",
            "--api-key-env",
            "TEST_API_KEY",
            "--username",
            "owner",
            "--token-env",
            "TEST_GITHUB_TOKEN",
            "--convergence-attempts",
            "2",
            "--convergence-delay-seconds",
            "0"
        ];

        Dictionary<string, string?> environment = new(StringComparer.Ordinal)
        {
            ["RELIABLEWEBHOOKS_DOTNET_COMMAND"] = dotnetStub,
            ["RELIABLEWEBHOOKS_MOCK_PACKAGE_STATUSES"] = "404",
            ["RELIABLEWEBHOOKS_MOCK_GITHUB_VERSION_STATUSES"] = "404,200",
            ["RELIABLEWEBHOOKS_MOCK_REMOTE_PACKAGE"] = package,
            ["TEST_API_KEY"] = "api-key",
            ["TEST_GITHUB_TOKEN"] = "github-token"
        };

        CommandResult result = await RunCommandAsync("dotnet", arguments, repoRoot, environment);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("verified ReliableWebhooks 1.0.0 after publication", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingPublishedPackageWaitsForContentBeforeComparison()
    {
        using TempDirectory temp = new();
        string package = Path.Combine(temp.Path, "ReliableWebhooks.1.0.0.nupkg");
        WritePackage(package);
        string dotnetStub = CreateSuccessfulDotNetStub(temp.Path);
        string repoRoot = FindRepoRoot();

        List<string> arguments =
        [
            "run",
            "--file",
            Path.Combine(repoRoot, "scripts", "publish-release-package.cs"),
            "--",
            "--registry",
            "GitHubPackages",
            "--source",
            "mock://registry/index.json",
            "--package",
            package,
            "--package-id",
            "ReliableWebhooks",
            "--version",
            "1.0.0",
            "--api-key-env",
            "TEST_API_KEY",
            "--username",
            "owner",
            "--token-env",
            "TEST_GITHUB_TOKEN",
            "--convergence-attempts",
            "2",
            "--convergence-delay-seconds",
            "0"
        ];

        Dictionary<string, string?> environment = new(StringComparer.Ordinal)
        {
            ["RELIABLEWEBHOOKS_DOTNET_COMMAND"] = dotnetStub,
            ["RELIABLEWEBHOOKS_MOCK_PACKAGE_STATUSES"] = "404,200",
            ["RELIABLEWEBHOOKS_MOCK_GITHUB_VERSION_STATUSES"] = "200",
            ["RELIABLEWEBHOOKS_MOCK_REMOTE_PACKAGE"] = package,
            ["TEST_API_KEY"] = "api-key",
            ["TEST_GITHUB_TOKEN"] = "github-token"
        };

        CommandResult result = await RunCommandAsync("dotnet", arguments, repoRoot, environment);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("GitHub Packages: ReliableWebhooks 1.0.0 converged and matches the validated artifact.", result.Output, StringComparison.Ordinal);
        Assert.Contains("GitHub Packages: validated existing ReliableWebhooks 1.0.0; skipping package push.", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("> dotnet nuget push", result.Output, StringComparison.Ordinal);
    }

    private static void WritePackage(string path)
    {
        using FileStream stream = File.Create(path);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create);
        WriteEntry(archive, "ReliableWebhooks.nuspec", "<package />");
        WriteEntry(archive, "lib/net10.0/ReliableWebhooks.dll", "local");
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        using StreamWriter writer = new(entry.Open());
        writer.Write(content);
    }

    private static string CreateSuccessfulDotNetStub(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            string path = Path.Combine(directory, "dotnet-stub.cmd");
            File.WriteAllText(path, "@echo off\r\nexit /b 0\r\n");
            return path;
        }

        string stub = Path.Combine(directory, "dotnet-stub.sh");
        File.WriteAllText(stub, "#!/usr/bin/env bash\nexit 0\n");
        File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return stub;
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
        IReadOnlyDictionary<string, string?> environment)
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
        foreach ((string key, string? value) in environment)
        {
            process.StartInfo.Environment[key] = value;
        }

        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        string error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new CommandResult(process.ExitCode, output + error);
    }

    private sealed record CommandResult(int ExitCode, string Output);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"reliablewebhooks-github-packages-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path
        {
            get;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(
                    Path,
                    recursive: true);
            }
        }
    }
}
