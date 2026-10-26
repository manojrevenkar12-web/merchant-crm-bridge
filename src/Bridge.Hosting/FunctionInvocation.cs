using System.Globalization;
using System.Text;
using System.Text.Json;
using Bridge.Core.Ports;
using Bridge.Core.Services;

namespace Bridge.Hosting;

/// <summary>
/// One Kafka-triggered function invocation as the consumer sees it. The Functions Kafka extension owns the
/// offsets: it checkpoints after a successful invocation and, with a retry policy on the function, invokes
/// again for the same message (holding the partition) when the invocation throws. So "commit" means
/// "return normally" and "rewind" means "throw"; the delivery count is the retry count plus one.
/// </summary>
public sealed class FunctionInvocation : IEventConsumer
{
    public bool Committed { get; private set; }
    public bool RewindRequested { get; private set; }

    public Task<LogMessage?> ConsumeAsync(TimeSpan wait, CancellationToken ct) =>
        throw new NotSupportedException("the Functions host delivers messages; nothing polls here");

    public void Commit(LogMessage message) => Committed = true;

    public void Rewind(LogMessage message) => RewindRequested = true;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Builds the message from the trigger's value and binding data (Key, Partition, Offset, Topic,
    /// Headers as a JSON array of {Key, Value} with base64 values).</summary>
    public static LogMessage Message(string value, IReadOnlyDictionary<string, object?> bindingData, int retryCount, string fallbackTopic)
    {
        static string? Text(IReadOnlyDictionary<string, object?> d, string key) => d.TryGetValue(key, out var v) && v is not null
            ? v is JsonElement e ? (e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText()) : Convert.ToString(v, CultureInfo.InvariantCulture)
            : null;

        var headers = new Dictionary<string, string>();
        var rawHeaders = Text(bindingData, "Headers");
        if (!string.IsNullOrWhiteSpace(rawHeaders) && rawHeaders.TrimStart().StartsWith('['))
        {
            using var doc = JsonDocument.Parse(rawHeaders);
            foreach (var h in doc.RootElement.EnumerateArray())
            {
                var key = h.TryGetProperty("Key", out var k) ? k.GetString() : null;
                var raw = h.TryGetProperty("Value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (key is null || raw is null) continue;
                headers[key] = TryBase64(raw);
            }
        }
        return new LogMessage(
            Text(bindingData, "Topic") ?? fallbackTopic,
            int.Parse(Text(bindingData, "Partition") ?? "0", CultureInfo.InvariantCulture),
            long.Parse(Text(bindingData, "Offset") ?? "0", CultureInfo.InvariantCulture),
            Text(bindingData, "Key") ?? "",
            value,
            headers,
            retryCount + 1);
    }

    private static string TryBase64(string raw)
    {
        var buffer = new byte[raw.Length];
        return Convert.TryFromBase64String(raw, buffer, out var n) ? Encoding.UTF8.GetString(buffer, 0, n) : raw;
    }

    /// <summary>Raises the exception that makes the host deliver the message again, after waiting out a
    /// Retry-After that is short enough to wait inside the invocation.</summary>
    public static async Task RetryIfAskedAsync(HandledMessage handled, TimeSpan maxInlineWait, TimeProvider clock, CancellationToken ct)
    {
        if (handled.Handling != Handling.RetryLater) return;
        if (handled.Delay > TimeSpan.Zero) await Task.Delay(handled.Delay < maxInlineWait ? handled.Delay : maxInlineWait, clock, ct).ConfigureAwait(false);
        throw new DeliverAgainException(handled.Detail);
    }
}

/// <summary>Thrown so the Functions retry policy delivers the same message again; never a failure by itself.</summary>
public sealed class DeliverAgainException : Exception
{
    public DeliverAgainException() : base("deliver again") { }

    public DeliverAgainException(string message) : base(message) { }

    public DeliverAgainException(string message, Exception inner) : base(message, inner) { }
}
