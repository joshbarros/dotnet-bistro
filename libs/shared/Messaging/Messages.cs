using System.Text.Json;

namespace Bistro.Shared.Messaging;

public record ReserveCovers(Guid OrderId, Guid OwnerId, Guid SittingId, int Covers, string Reference, bool Waitlist = false);
public record CoversHeld(Guid OrderId, Guid OwnerId, Guid SittingId, int Covers, string Reference);
public record CoversShort(Guid OrderId, string Reference, int RequestedCovers, int CoversLeft, string Reason = "stock", int PerGuestLimit = 0);
public record CoversWaitlisted(Guid OrderId, string Reference, int Covers, int Position);
public record ReleaseCovers(Guid OrderId, string Reason);
public record CoversReleased(Guid OrderId, int Covers, string Reason);
public record ConfirmationIssued(Guid OrderId, string Number);
public record ConfirmationRejected(Guid OrderId, string Reason);

public record Delivery(Guid MessageId, string RoutingKey, string Body);

public interface IMessageHandler
{
    Task HandleAsync(Delivery delivery, CancellationToken ct);
}

/// <summary>Committed as rejected; the message must be dead-lettered, not retried.</summary>
public class PoisonMessageException(string message) : Exception(message);

/// <summary>The message arrived before the state it depends on; retry later.</summary>
public class OutOfOrderException(string message) : Exception(message);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Parse<T>(string body) =>
        JsonSerializer.Deserialize<T>(body, Options) ?? throw new InvalidOperationException("Empty message body.");
}
