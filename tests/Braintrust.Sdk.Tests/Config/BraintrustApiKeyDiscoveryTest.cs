using Braintrust.Sdk.Config;

namespace Braintrust.Sdk.Tests.Config;

[Collection("BraintrustGlobals")]
public class BraintrustApiKeyDiscoveryTest : IDisposable
{
    private readonly string _originalCwd;
    private readonly string? _originalApiKey;
    private readonly string _tempDir;

    public BraintrustApiKeyDiscoveryTest()
    {
        _originalCwd = Directory.GetCurrentDirectory();
        _originalApiKey = Environment.GetEnvironmentVariable("BRAINTRUST_API_KEY");
        _tempDir = Directory.CreateTempSubdirectory("braintrust-env-").FullName;
        Environment.SetEnvironmentVariable("BRAINTRUST_API_KEY", null);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalCwd);
        Environment.SetEnvironmentVariable("BRAINTRUST_API_KEY", _originalApiKey);
        Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task FindsApiKeyInNearestParentBraintrustEnv()
    {
        var nested = Path.Combine(_tempDir, "packages", "app");
        Directory.CreateDirectory(nested);
        WriteBraintrustEnv(_tempDir, "BRAINTRUST_API_KEY=parent-key\n");
        Directory.SetCurrentDirectory(nested);

        var config = BraintrustConfig.FromEnvironment();

        Assert.Equal("parent-key", await config.GetRequiredApiKeyAsync());
    }

    [Fact]
    public async Task UsesNearestBraintrustEnvInsteadOfHigherParent()
    {
        var nested = Path.Combine(_tempDir, "packages", "app");
        var packageDir = Path.GetDirectoryName(nested)!;
        Directory.CreateDirectory(nested);
        WriteBraintrustEnv(_tempDir, "BRAINTRUST_API_KEY=root-key\n");
        WriteBraintrustEnv(packageDir, "BRAINTRUST_API_KEY=package-key\n");
        Directory.SetCurrentDirectory(nested);

        var config = BraintrustConfig.FromEnvironment();

        Assert.Equal("package-key", await config.GetRequiredApiKeyAsync());
    }

    [Theory]
    [InlineData("OTHER=value\n")]
    [InlineData("BRAINTRUST_API_KEY=\"   \"\n")]
    public async Task StopsAtNearestBraintrustEnvWhenApiKeyIsMissingOrBlank(string nearestContents)
    {
        var nested = Path.Combine(_tempDir, "packages", "app");
        var packageDir = Path.GetDirectoryName(nested)!;
        Directory.CreateDirectory(nested);
        WriteBraintrustEnv(_tempDir, "BRAINTRUST_API_KEY=root-key\n");
        WriteBraintrustEnv(packageDir, nearestContents);
        Directory.SetCurrentDirectory(nested);

        var config = BraintrustConfig.FromEnvironment();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => config.GetRequiredApiKeyAsync());

        Assert.Contains("BRAINTRUST_API_KEY is required", exception.Message);
    }

    [Fact]
    public async Task UsesProcessEnvironmentBeforeBraintrustEnv()
    {
        WriteBraintrustEnv(_tempDir, "BRAINTRUST_API_KEY=file-key\n");
        Environment.SetEnvironmentVariable("BRAINTRUST_API_KEY", "env-key");
        Directory.SetCurrentDirectory(_tempDir);

        var config = BraintrustConfig.FromEnvironment();

        Assert.Equal("env-key", await config.GetRequiredApiKeyAsync());
    }

    [Fact]
    public async Task FallsBackToBraintrustEnvWhenProcessEnvironmentIsBlank()
    {
        WriteBraintrustEnv(_tempDir, "BRAINTRUST_API_KEY=file-key\n");
        Environment.SetEnvironmentVariable("BRAINTRUST_API_KEY", "   ");
        Directory.SetCurrentDirectory(_tempDir);

        var config = BraintrustConfig.FromEnvironment();

        Assert.Equal("file-key", await config.GetRequiredApiKeyAsync());
    }

    [Fact]
    public async Task ExplicitApiKeyOverrideWinsOverEnvironmentAndBraintrustEnv()
    {
        WriteBraintrustEnv(_tempDir, "BRAINTRUST_API_KEY=file-key\n");
        Environment.SetEnvironmentVariable("BRAINTRUST_API_KEY", "env-key");
        Directory.SetCurrentDirectory(_tempDir);

        var config = BraintrustConfig.Of(("BRAINTRUST_API_KEY", "explicit-key"));

        Assert.Equal("explicit-key", await config.GetRequiredApiKeyAsync());
    }

    [Fact]
    public async Task ExplicitBlankApiKeyOverrideDoesNotFallBack()
    {
        WriteBraintrustEnv(_tempDir, "BRAINTRUST_API_KEY=file-key\n");
        Environment.SetEnvironmentVariable("BRAINTRUST_API_KEY", "env-key");
        Directory.SetCurrentDirectory(_tempDir);

        var config = BraintrustConfig.Of(("BRAINTRUST_API_KEY", "   "));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => config.GetRequiredApiKeyAsync());

        Assert.Contains("BRAINTRUST_API_KEY is required", exception.Message);
    }

    [Fact]
    public async Task SearchesCwdAndAtMost64ParentDirectories()
    {
        var segments = Enumerable.Range(0, 65).Select(i => $"d{i}").ToArray();
        var nested = Path.Combine(new[] { _tempDir }.Concat(segments).ToArray());
        Directory.CreateDirectory(nested);
        WriteBraintrustEnv(_tempDir, "BRAINTRUST_API_KEY=too-high\n");
        Directory.SetCurrentDirectory(nested);

        var config = BraintrustConfig.FromEnvironment();
        await Assert.ThrowsAsync<InvalidOperationException>(() => config.GetRequiredApiKeyAsync());

        WriteBraintrustEnv(Path.Combine(_tempDir, segments[0]), "BRAINTRUST_API_KEY=boundary-key\n");

        Assert.Equal("boundary-key", await config.GetRequiredApiKeyAsync());
    }

    [Fact]
    public async Task SupportsDotenvSyntaxWithoutMutatingEnvironment()
    {
        WriteBraintrustEnv(
            _tempDir,
            "OTHER=value\nexport BRAINTRUST_API_KEY=\"quoted-key\" # comment\n");
        Directory.SetCurrentDirectory(_tempDir);

        var config = BraintrustConfig.FromEnvironment();

        Assert.Equal("quoted-key", await config.GetRequiredApiKeyAsync());
        Assert.Null(Environment.GetEnvironmentVariable("OTHER"));
        Assert.Null(Environment.GetEnvironmentVariable("BRAINTRUST_API_KEY"));
    }

    [Fact]
    public async Task UnreadableNearestBraintrustEnvDoesNotCheckHigherParents()
    {
        var nested = Path.Combine(_tempDir, "packages", "app");
        var packageDir = Path.GetDirectoryName(nested)!;
        Directory.CreateDirectory(nested);
        WriteBraintrustEnv(_tempDir, "BRAINTRUST_API_KEY=root-key\n");
        Directory.CreateDirectory(Path.Combine(packageDir, ".env.braintrust"));
        Directory.SetCurrentDirectory(nested);

        var config = BraintrustConfig.FromEnvironment();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => config.GetRequiredApiKeyAsync());

        Assert.Contains("BRAINTRUST_API_KEY is required", exception.Message);
    }

    [Fact]
    public async Task CancellationDuringBraintrustEnvLookupIsPropagated()
    {
        WriteBraintrustEnv(_tempDir, "BRAINTRUST_API_KEY=file-key\n");
        Directory.SetCurrentDirectory(_tempDir);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var config = BraintrustConfig.FromEnvironment();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            config.GetRequiredApiKeyAsync(cancellation.Token));
    }

    [Fact]
    public async Task ConfigCreationDoesNotReadBraintrustEnvUntilApiKeyLookup()
    {
        Directory.SetCurrentDirectory(_tempDir);

        var config = BraintrustConfig.FromEnvironment();
        WriteBraintrustEnv(_tempDir, "BRAINTRUST_API_KEY=late-file-key\n");

        Assert.Equal("late-file-key", await config.GetRequiredApiKeyAsync());
    }

    private static void WriteBraintrustEnv(string dir, string contents)
    {
        File.WriteAllText(Path.Combine(dir, ".env.braintrust"), contents);
    }
}
