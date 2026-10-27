using Bistro.Shared;
using Bistro.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bistro.Confirmations;

public enum ConfirmationStatus { Issued, Rejected }

public class Confirmation
{
    public Guid OrderId { get; set; }
    public Guid OwnerId { get; set; }
    public string? Number { get; set; }
    public ConfirmationStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class ConfirmationsDbContext(DbContextOptions<ConfirmationsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "confirmations";

    public DbSet<Confirmation> Confirmations => Set<Confirmation>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Confirmation>(e =>
        {
            e.ToTable("Confirmations");
            e.HasKey(x => x.OrderId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });
    }
}

public enum IssueOutcome { Issued, Rejected, Duplicate }

/// <summary>Issues the confirmation once the covers are held. A poison reference is committed as rejected.</summary>
public class ConfirmationsHandler(ConfirmationsDbContext db) : IMessageHandler
{
    public const string Name = "confirmations";

    public async Task HandleAsync(Delivery delivery, CancellationToken ct)
    {
        if (delivery.RoutingKey != Topology.HeldKey)
            throw new InvalidOperationException($"Confirmations does not handle '{delivery.RoutingKey}'.");

        var outcome = await IssueAsync(delivery.MessageId, Json.Parse<CoversHeld>(delivery.Body), ct);
        if (outcome == IssueOutcome.Rejected)
            throw new PoisonMessageException("Reference carries the poison token; rejection is committed.");
    }

    public async Task<IssueOutcome> IssueAsync(Guid messageId, CoversHeld held, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return IssueOutcome.Duplicate;
        }

        IssueOutcome outcome;
        if (PoisonRule.IsPoison(held.Reference))
        {
            const string reason = "Reference contains the poison token.";
            db.Confirmations.Add(new Confirmation
            {
                OrderId = held.OrderId,
                OwnerId = held.OwnerId,
                Status = ConfirmationStatus.Rejected,
                Reason = reason,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("confirmations", Topology.ReleaseKey, held.OrderId, new ReleaseCovers(held.OrderId, "poison"));
            db.Enqueue("confirmations", Topology.RejectedKey, held.OrderId, new ConfirmationRejected(held.OrderId, reason));
            outcome = IssueOutcome.Rejected;
        }
        else
        {
            var number = $"BT-{held.OrderId.ToString("N")[^12..].ToUpperInvariant()}";
            db.Confirmations.Add(new Confirmation
            {
                OrderId = held.OrderId,
                OwnerId = held.OwnerId,
                Number = number,
                Status = ConfirmationStatus.Issued,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("confirmations", Topology.IssuedKey, held.OrderId, new ConfirmationIssued(held.OrderId, number));
            outcome = IssueOutcome.Issued;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }
}
