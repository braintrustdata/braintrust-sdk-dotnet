namespace Braintrust.Sdk.Config;

internal static class BraintrustApiKeyDiscovery
{
    internal const int BraintrustEnvSearchParentLimit = 64;
    private const string BraintrustEnvFileName = ".env.braintrust";
    private const string BraintrustApiKeyName = "BRAINTRUST_API_KEY";

    internal static async Task<string?> FindInBraintrustEnvFileAsync(CancellationToken cancellationToken = default)
    {
        string currentDirectory;
        try
        {
            currentDirectory = Directory.GetCurrentDirectory();
        }
        catch
        {
            return null;
        }

        var paths = new List<string>();
        for (var dir = currentDirectory; paths.Count <= BraintrustEnvSearchParentLimit; dir = Path.GetDirectoryName(dir)!)
        {
            paths.Add(Path.Combine(dir, BraintrustEnvFileName));

            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, dir, StringComparison.Ordinal))
            {
                break;
            }
        }

        // Start the reads together, then await nearest-first so a parent file never beats a closer file.
        var reads = paths
            .Select((path, index) => File.ReadAllTextAsync(path, cancellationToken)
                .ContinueWith(
                    task => new
                    {
                        Index = index,
                        Contents = task.IsCompletedSuccessfully ? task.Result : null,
                        Error = task.IsCompletedSuccessfully
                            ? null
                            : task.IsCanceled
                                ? new OperationCanceledException(cancellationToken)
                                : task.Exception?.GetBaseException()
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default))
            .ToArray();

        for (var i = 0; i < reads.Length; i++)
        {
            var result = await reads[i].ConfigureAwait(false);
            if (result.Error == null)
            {
                var apiKey = ParseBraintrustApiKey(result.Contents!);
                return string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
            }

            if (result.Error is OperationCanceledException cancellation)
            {
                throw cancellation;
            }

            if (result.Error is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }

            return null;
        }

        return null;
    }

    internal static string? ParseBraintrustApiKey(string contents)
    {
        string? parsedApiKey = null;

        using var reader = new StringReader(contents);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var current = line.TrimStart();
            if (current.Length == 0 || current[0] == '#')
            {
                continue;
            }

            if (current.StartsWith("export", StringComparison.Ordinal))
            {
                var afterExport = current["export".Length..];
                if (afterExport.Length > 0 && char.IsWhiteSpace(afterExport[0]))
                {
                    current = afterExport.TrimStart();
                }
            }

            var equalsIndex = current.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            var key = current[..equalsIndex].Trim();
            if (!string.Equals(key, BraintrustApiKeyName, StringComparison.Ordinal))
            {
                continue;
            }

            var rawValue = current[(equalsIndex + 1)..].TrimStart();
            if (rawValue.Length == 0)
            {
                parsedApiKey = string.Empty;
                continue;
            }

            if (rawValue[0] is '"' or '\'')
            {
                var quote = rawValue[0];
                var value = new System.Text.StringBuilder();
                var escaped = false;
                var closed = false;

                for (var i = 1; i < rawValue.Length; i++)
                {
                    var ch = rawValue[i];
                    if (escaped)
                    {
                        value.Append(quote == '"' && ch == 'n' ? '\n'
                            : quote == '"' && ch == 'r' ? '\r'
                            : quote == '"' && ch == 't' ? '\t'
                            : ch);
                        escaped = false;
                    }
                    else if (ch == '\\')
                    {
                        escaped = true;
                    }
                    else if (ch == quote)
                    {
                        closed = true;
                        break;
                    }
                    else
                    {
                        value.Append(ch);
                    }
                }

                parsedApiKey = closed ? value.ToString() : rawValue[1..];
                continue;
            }

            var commentIndex = -1;
            for (var i = 0; i < rawValue.Length; i++)
            {
                if (rawValue[i] == '#' && (i == 0 || char.IsWhiteSpace(rawValue[i - 1])))
                {
                    commentIndex = i;
                    break;
                }
            }

            parsedApiKey = (commentIndex >= 0 ? rawValue[..commentIndex] : rawValue).Trim();
        }

        return parsedApiKey;
    }
}
