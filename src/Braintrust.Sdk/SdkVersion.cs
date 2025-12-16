using System;
using System.IO;
using System.Reflection;

namespace Braintrust.Sdk;

/// <summary>
/// Utility class to load the SDK version from the embedded resource.
/// The version is generated at build time from git state.
/// </summary>
internal static class SdkVersion
{
    private static readonly Lazy<string> _version = new Lazy<string>(LoadVersionFromResource);

    /// <summary>
    /// Get the SDK version. This is computed from git state at build time.
    ///
    /// Version format:
    /// - If on a tag (e.g., v0.0.3): "0.0.3"
    /// - If not on a tag: "{mostRecentTag}-{commitSHA}" (e.g., "0.0.3-c4af682")
    /// - If workspace is dirty: appends "-DIRTY"
    /// - If git is not available: "unknown"
    /// </summary>
    public static string Version => _version.Value;

    private static string LoadVersionFromResource()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "BraintrustVersion.txt";

            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    // Fallback to assembly version if resource not found
                    return assembly.GetName().Version?.ToString() ?? "unknown";
                }

                using (var reader = new StreamReader(stream))
                {
                    var version = reader.ReadToEnd().Trim();
                    return string.IsNullOrEmpty(version) ? "unknown" : version;
                }
            }
        }
        catch (Exception)
        {
            // Fallback to "unknown" if we can't read the resource
            return "unknown";
        }
    }
}
