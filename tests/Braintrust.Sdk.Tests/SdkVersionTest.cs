using System;
using System.Reflection;

namespace Braintrust.Sdk.Tests;

public class SdkVersionTest
{
    [Fact]
    public void VersionIsNotNullOrEmpty()
    {
        var version = SdkVersion.Version;

        Assert.NotNull(version);
        Assert.NotEmpty(version);
    }

    [Fact]
    public void VersionIsLoadedFromResource()
    {
        var version = SdkVersion.Version;

        // Version should be either:
        // - A git tag (e.g., "0.0.3")
        // - Tag + commit SHA (e.g., "0.0.3-c4af682")
        // - Tag + commit SHA + DIRTY (e.g., "0.0.3-c4af682-DIRTY")
        // - Just commit SHA (if no tags exist)
        // - "unknown" (fallback if git not available or resource not found)

        // At minimum, it should not be null or empty
        Assert.NotNull(version);
        Assert.NotEmpty(version);

        // If it contains a dash, it should have at least two parts
        if (version.Contains('-'))
        {
            var parts = version.Split('-');
            Assert.True(parts.Length >= 2, $"Version with dash should have at least 2 parts: {version}");
        }
    }

    [Fact]
    public void VersionResourceExistsInAssembly()
    {
        var assembly = typeof(Braintrust).Assembly;
        var resourceNames = assembly.GetManifestResourceNames();

        // The BraintrustVersion.txt resource should be embedded
        Assert.Contains("BraintrustVersion.txt", resourceNames);
    }

    [Fact]
    public void VersionMatchesResourceContent()
    {
        var version = SdkVersion.Version;

        var assembly = typeof(Braintrust).Assembly;
        using var stream = assembly.GetManifestResourceStream("BraintrustVersion.txt");
        Assert.NotNull(stream);

        using var reader = new StreamReader(stream);
        var versionFromResource = reader.ReadToEnd().Trim();

        Assert.Equal(versionFromResource, version);
    }
}
