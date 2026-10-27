using RabbitMQ.Client;

namespace Bistro.Shared.Messaging;

public static class Topology
{
    public const string Exchange = "bistro.work";
    public const string DeadLetterExchange = "bistro.dlx";
    public const string RequeueExchange = "bistro.requeue";
    public const string KafkaTopic = "bistro.events";

    public const int RetryDelayMs = 2000;
    public const int MaxAttempts = 3;

    public const string ReserveKey = "covers.reserve";
    public const string ReleaseKey = "covers.release";
    public const string HeldKey = "covers.held";
    public const string ShortKey = "covers.short";
    public const string WaitlistedKey = "covers.waitlisted";
    public const string ReleasedKey = "covers.released";
    public const string IssuedKey = "confirmation.issued";
    public const string RejectedKey = "confirmation.rejected";

    public const string SittingsQueue = "sittings.commands";
    public const string ConfirmationsQueue = "confirmations.work";
    public const string OrdersQueue = "orders.events";

    public static async Task DeclareAsync(IChannel channel, string queue, string[] routingKeys, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(RequeueExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);

        await channel.QueueDeclareAsync($"{queue}.dlq", durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync($"{queue}.dlq", DeadLetterExchange, queue, cancellationToken: ct);

        await channel.QueueDeclareAsync($"{queue}.retry", durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = RetryDelayMs,
                ["x-dead-letter-exchange"] = RequeueExchange,
                ["x-dead-letter-routing-key"] = queue,
            }, cancellationToken: ct);

        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = DeadLetterExchange,
                ["x-dead-letter-routing-key"] = queue,
            }, cancellationToken: ct);
        await channel.QueueBindAsync(queue, RequeueExchange, queue, cancellationToken: ct);
        foreach (var key in routingKeys)
            await channel.QueueBindAsync(queue, Exchange, key, cancellationToken: ct);
    }
}
