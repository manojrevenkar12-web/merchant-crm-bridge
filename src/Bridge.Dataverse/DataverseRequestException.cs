namespace Bridge.Dataverse;

/// <summary>A Dataverse Web API error: HTTP status and the hex error code from the body.</summary>
public sealed class DataverseRequestException : Exception
{
    public DataverseRequestException(int status, string code, string message, TimeSpan? retryAfter = null) : base($"{status} {code}: {message}")
    {
        Status = status;
        Code = code;
        RetryAfter = retryAfter;
        Detail = message;
    }

    public DataverseRequestException() { }
    public DataverseRequestException(string message) : base(message) { }
    public DataverseRequestException(string message, Exception inner) : base(message, inner) { }

    public int Status { get; }
    public string Code { get; } = "";
    public string Detail { get; } = "";
    public TimeSpan? RetryAfter { get; }

    public const string ConcurrencyMismatch = "0x80060882";
    public const string DuplicateKey = "0x80040237";
    public const string NotFound = "0x80040217";
    public const string PluginAbort = "0x80040265";

    public bool IsTransient => Status is 429 or 408 or 502 or 503 or 504 or 500;
}
