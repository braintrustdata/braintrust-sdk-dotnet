using System;
using System.Collections.Generic;
using System.Linq;

namespace Braintrust.Sdk.Eval;

/// <summary>
/// A dataset held entirely in memory.
/// </summary>
internal class DatasetInMemoryImpl<TInput, TOutput> : Dataset<TInput, TOutput>
    where TInput : notnull
    where TOutput : notnull
{
    private readonly IReadOnlyList<DatasetCase<TInput, TOutput>> _cases;
    private readonly string _id;

    public DatasetInMemoryImpl(IEnumerable<DatasetCase<TInput, TOutput>> cases)
    {
        _cases = cases.ToList();
        _id = $"in-memory-dataset<{_cases.GetHashCode()}>";
    }

    public string Id => _id;

    public string Version => "0";

    public ICursor<DatasetCase<TInput, TOutput>> OpenCursor()
    {
        return new InMemoryCursor(_cases);
    }

    private class InMemoryCursor : ICursor<DatasetCase<TInput, TOutput>>
    {
        private readonly IReadOnlyList<DatasetCase<TInput, TOutput>> _cases;
        private int _nextIndex = 0;
        private bool _closed = false;

        public InMemoryCursor(IReadOnlyList<DatasetCase<TInput, TOutput>> cases)
        {
            _cases = cases;
        }

        public DatasetCase<TInput, TOutput>? Next()
        {
            if (_closed)
            {
                throw new InvalidOperationException("This method may not be invoked after Close");
            }

            if (_nextIndex < _cases.Count)
            {
                return _cases[_nextIndex++];
            }

            return default;
        }

        public void Close()
        {
            _closed = true;
        }

        public void Dispose()
        {
            Close();
        }
    }
}
