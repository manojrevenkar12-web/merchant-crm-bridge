using Bridge.Core.Contracts;
using Bridge.Core.Services;
using Bridge.Hosting;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Bridge.Functions;

/// <summary>
/// Platform events into Dynamics 365. The Kafka extension delivers one partition's messages in order and,
/// with the retry policy below, delivers the same message again while an invocation throws, so a throttled
/// or briefly unavailable Dataverse holds the partition instead of reordering a merchant's events. Broken
/// events and merchants the CRM never learns about go to the dead-letter topic and the partition moves on.
/// </summary>
public sealed class PlatformEventsFunction(PlatformConsumer consumer, TimeProvider clock, ILogger<PlatformEventsFunction> logger)
{
    // Throttling waits inside the invocation up to this long (Dataverse asks for seconds); longer outages
    // are spaced out by the retry policy.
    private static readonly TimeSpan MaxInlineWait = TimeSpan.FromSeconds(30);

    [Function("platform-events")]
    [ExponentialBackoffRetry(-1, "00:00:01", "00:05:00")]
    public async Task RunAsync(
        [KafkaTrigger("KafkaBrokerList", Topics.PlatformEvents,
            ConsumerGroup = "%Bridge:Kafka:GroupId%",
            Protocol = BrokerProtocol.SaslSsl,
            AuthenticationMode = BrokerAuthenticationMode.Plain,
            Username = "KafkaUsername",
            Password = "KafkaPassword",
            IsBatched = false)] string value,
        FunctionContext context)
    {
        var message = TriggerDelivery.Message(value, context.BindingContext.BindingData, context.RetryContext?.RetryCount ?? 0, Topics.PlatformEvents);
        var invocation = new TriggerDelivery();
        var handled = await consumer.HandleAsync(invocation, message, context.CancellationToken);
        if (handled.Handling == Handling.RetryLater)
            logger.LogWarning("delivering {Topic}/{Partition}/{Offset} again (delivery {Delivery}): {Detail}",
                message.Topic, message.Partition, message.Offset, message.DeliveryCount, handled.Detail);
        await TriggerDelivery.RetryIfAskedAsync(handled, MaxInlineWait, clock, context.CancellationToken);
    }
}
