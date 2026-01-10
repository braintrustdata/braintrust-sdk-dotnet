namespace Braintrust.Sdk.Api;

/// <summary>
/// Exception thrown when an API request fails.
/// </summary>
public class ApiException : Exception
{
    public int? StatusCode { get; }

    public ApiException(string message) : base(message) { }

    public ApiException(string message, Exception innerException) : base(message, innerException) { }

    public ApiException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}