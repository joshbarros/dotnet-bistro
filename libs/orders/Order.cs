namespace Bistro.Orders;

public enum OrderStatus { Requested, Waitlisted, Held, Sold, Refunded, Rejected }

public class Order
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid SittingId { get; set; }
    public string SittingCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public int Covers { get; set; }
    public long TotalCents { get; set; }
    public OrderStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? ConfirmationNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CoversReleasedAt { get; set; }
}

public class OrderOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the refund race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public record CreateOrderRequest(string? SittingCode, string? Reference, long Covers, bool Waitlist = false);

public record OrderDto(
    Guid Id, Guid OwnerId, string SittingCode, string Reference, int Covers, long TotalCents, string Status,
    string? Reason, string? ConfirmationNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? CoversReleasedAt)
{
    public static OrderDto From(Order b) => new(
        b.Id, b.OwnerId, b.SittingCode, b.Reference, b.Covers, b.TotalCents, b.Status.ToString(),
        b.Reason, b.ConfirmationNumber, b.CreatedAt, b.UpdatedAt, b.CoversReleasedAt);
}
