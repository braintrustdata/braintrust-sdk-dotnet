using System.Diagnostics.CodeAnalysis;

namespace Braintrust.Sdk.Eval;

public static class DatasetCase
{
    /// <summary>
    /// Creates a new dataset case.
    /// </summary>
    public static DatasetCase<TInput, TOutput> Of<TInput, TOutput>(
        TInput input,
        TOutput expected,
        IReadOnlyList<string> tags,
        IReadOnlyDictionary<string, object> metadata)
        where TInput : notnull
        where TOutput : notnull
        => new(input, expected, tags, metadata);

    /// <summary>
    /// Creates a new dataset case.
    /// </summary>
    public static DatasetCase<TInput, TOutput> Of<TInput, TOutput>(
        TInput input,
        TOutput expected,
        IReadOnlyList<string> tags)
        where TInput : notnull
        where TOutput : notnull
        => new(input, expected, tags);

    /// <summary>
    /// Creates a new dataset case.
    /// </summary>
    public static DatasetCase<TInput, TOutput> Of<TInput, TOutput>(
        TInput input,
        TOutput expected)
        where TInput : notnull
        where TOutput : notnull
        => new(input, expected);
}

/// <summary>
/// A single row in a dataset.
/// </summary>
public record DatasetCase<TInput, TOutput>
    where TInput : notnull
    where TOutput : notnull
{
    public required TInput Input { get; init; }
    public required TOutput Expected { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }
    public required IReadOnlyDictionary<string, object> Metadata { get; init; }

    [SetsRequiredMembers]
    public DatasetCase(
        TInput input,
        TOutput expected,
        IReadOnlyList<string> tags,
        IReadOnlyDictionary<string, object> metadata)
    {
        this.Input = input;
        this.Expected = expected;
        this.Tags = tags;
        this.Metadata = metadata;
    }

    [SetsRequiredMembers]
    public DatasetCase(
        TInput input,
        TOutput expected,
        IReadOnlyList<string> tags)
        : this(input, expected, tags, new Dictionary<string, object>())
    {
    }

    [SetsRequiredMembers]
    public DatasetCase(
        TInput input,
        TOutput expected)
        : this(input, expected, [])
    {
    }
}
