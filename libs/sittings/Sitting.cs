namespace Bistro.Sittings;

public class Sitting
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Room { get; set; } = "";
    public string Chef { get; set; } = "";
    public DateTimeOffset ServiceDate { get; set; }
    public long PriceCents { get; set; }
    public int CoversTotal { get; set; }
    public int CoversLeft { get; set; }
    /// <summary>The most covers one guest may hold in this sitting at any time.</summary>
    public int MaxPerGuest { get; set; } = 8;
    public DateTimeOffset CreatedAt { get; set; }
}

public enum HoldStatus { Held, Released }

public class CoverHold
{
    public Guid OrderId { get; set; }
    public Guid OwnerId { get; set; }
    public Guid SittingId { get; set; }
    public int Covers { get; set; }
    public HoldStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}

/// <summary>How many covers one guest holds in one sitting. Written only by the Sittings handler.</summary>
public class SittingGuest
{
    public Guid SittingId { get; set; }
    public Guid OwnerId { get; set; }
    public int Covers { get; set; }
}

/// <summary>An order that found the sitting full and asked to wait. Served first-come first-served when covers come back.</summary>
public class WaitlistEntry
{
    public long Seq { get; set; }
    public Guid OrderId { get; set; }
    public Guid OwnerId { get; set; }
    public Guid SittingId { get; set; }
    public int Covers { get; set; }
    public string Reference { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
