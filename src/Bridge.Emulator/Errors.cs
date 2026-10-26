namespace Bridge.Emulator;

/// <summary>A Dataverse-style error: HTTP status and the error code Dataverse returns in the body.</summary>
public sealed class DataverseException : Exception
{
    public DataverseException(int status, string code, string message) : base(message)
    {
        Status = status;
        Code = code;
    }

    public DataverseException() { }
    public DataverseException(string message) : base(message) { }
    public DataverseException(string message, Exception inner) : base(message, inner) { }

    public int Status { get; } = 400;
    public string Code { get; } = "0x80040216";

    // Codes as Dataverse returns them.
    public const string ConcurrencyMismatch = "0x80060882";   // The version of the existing record doesn't match the RowVersion property provided.
    public const string DuplicateKey = "0x80040237";          // A record with matching key values already exists.
    public const string NotFound = "0x80040217";              // Object does not exist.
    public const string PluginAbort = "0x80040265";           // ISV code aborted the operation (InvalidPluginExecutionException).
    public const string Throttled = "0x80072322";             // Number of requests exceeded the limit of {0} over time window of {1} seconds.
    public const string BadRequest = "0x80048d19";            // Error identified in Payload provided by the user.
    public const string Unauthorized = "0x80072560";
    public const string Forbidden = "0x80040220";             // Principal user is missing a privilege.

    public static DataverseException Invalid(string message) => new(400, BadRequest, message);
}
