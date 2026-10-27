using Bistro.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bistro.Sittings;

public enum ReserveOutcome { Held, Short, CapReached, Waitlisted, Duplicate }

/// <summary>
/// The only writer of Sitting.CoversLeft and of cover holds.
/// Two atomic steps in one transaction: a per-guest cap upsert that only moves while the new total fits, then one
/// conditional UPDATE that matches only while the remaining Covers still covers the request.
/// </summary>
public class SittingsHandler(SittingsDbContext db) : IMessageHandler
{
    public const string Name = "sittings";

    public Task HandleAsync(Delivery delivery, CancellationToken ct) => delivery.RoutingKey switch
    {
        Topology.ReserveKey => ReserveAsync(delivery.MessageId, Json.Parse<ReserveCovers>(delivery.Body), ct),
        Topology.ReleaseKey => ReleaseAsync(delivery.MessageId, Json.Parse<ReleaseCovers>(delivery.Body), ct),
        _ => throw new InvalidOperationException($"Sittings does not handle '{delivery.RoutingKey}'."),
    };

    public async Task<ReserveOutcome> ReserveAsync(Guid messageId, ReserveCovers command, CancellationToken ct = default)
    {
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            if (!await db.ClaimAsync(Name, messageId, ct))
            {
                await tx.CommitAsync(ct);
                return ReserveOutcome.Duplicate;
            }

            // 1. the per-guest cap: one atomic upsert that only moves while the new total still fits
            var withinCap = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO sittings."SittingGuests" ("SittingId", "OwnerId", "Covers")
                SELECT d."Id", {command.OwnerId}, {command.Covers} FROM sittings."Sittings" d
                WHERE d."Id" = {command.SittingId} AND d."MaxPerGuest" >= {command.Covers}
                ON CONFLICT ("SittingId", "OwnerId") DO UPDATE SET "Covers" = sittings."SittingGuests"."Covers" + {command.Covers}
                WHERE sittings."SittingGuests"."Covers" + {command.Covers} <=
                      (SELECT "MaxPerGuest" FROM sittings."Sittings" WHERE "Id" = {command.SittingId})
                """, ct);

            // 2. the remaining covers: one conditional UPDATE, the only writer of CoversLeft
            var updated = withinCap == 1
                ? await db.Sittings
                    // nobody jumps the waitlist: while someone is waiting, new reservations wait too (or are refused)
                    .Where(s => s.Id == command.SittingId && s.CoversLeft >= command.Covers
                        && !db.Waitlist.Any(w => w.SittingId == s.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CoversLeft, x => x.CoversLeft - command.Covers), ct)
                : 0;

            if (withinCap == 1 && updated == 1)
            {
                db.Holds.Add(new CoverHold
                {
                    OrderId = command.OrderId,
                    OwnerId = command.OwnerId,
                    SittingId = command.SittingId,
                    Covers = command.Covers,
                    Status = HoldStatus.Held,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                db.Enqueue("sittings", Topology.HeldKey, command.OrderId,
                    new CoversHeld(command.OrderId, command.OwnerId, command.SittingId, command.Covers, command.Reference));
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return ReserveOutcome.Held;
            }

            // Either the cap or the stock said no. Roll back, so a cap increment never outlives a failed reserve.
            await tx.RollbackAsync(ct);
        }

        // The refusal is recorded in a fresh transaction (the claim was rolled back with the attempt).
        db.ChangeTracker.Clear();
        await using var refusal = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await refusal.CommitAsync(ct);
            return ReserveOutcome.Duplicate;
        }

        var sitting = await db.Sittings.AsNoTracking().Where(s => s.Id == command.SittingId)
            .Select(s => new { s.CoversLeft, s.CoversTotal, s.MaxPerGuest }).FirstOrDefaultAsync(ct);
        var held = await db.Guests.AsNoTracking()
            .Where(c => c.SittingId == command.SittingId && c.OwnerId == command.OwnerId)
            .Select(c => c.Covers).FirstOrDefaultAsync(ct);
        var overCap = sitting is not null && held + command.Covers > sitting.MaxPerGuest;
        if (command.Waitlist && sitting is not null && !overCap && command.Covers <= sitting.CoversTotal)
        {
            var entry = new WaitlistEntry
            {
                OrderId = command.OrderId,
                OwnerId = command.OwnerId,
                SittingId = command.SittingId,
                Covers = command.Covers,
                Reference = command.Reference,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Waitlist.Add(entry);
            await db.SaveChangesAsync(ct);
            var position = await db.Waitlist.CountAsync(w => w.SittingId == command.SittingId && w.Seq <= entry.Seq, ct);
            db.Enqueue("sittings", Topology.WaitlistedKey, command.OrderId,
                new CoversWaitlisted(command.OrderId, command.Reference, command.Covers, position));
            await db.SaveChangesAsync(ct);
            await refusal.CommitAsync(ct);
            return ReserveOutcome.Waitlisted;
        }

        db.Enqueue("sittings", Topology.ShortKey, command.OrderId,
            new CoversShort(command.OrderId, command.Reference, command.Covers, sitting?.CoversLeft ?? 0,
                overCap ? "cap" : "stock", sitting?.MaxPerGuest ?? 0));
        await db.SaveChangesAsync(ct);
        await refusal.CommitAsync(ct);
        return overCap ? ReserveOutcome.CapReached : ReserveOutcome.Short;
    }

    /// <summary>Gives held covers back exactly once. A second release for the same order changes nothing.</summary>
    public async Task<bool> ReleaseAsync(Guid messageId, ReleaseCovers command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return false;
        }

        // An order still in the waitlist has no hold: leaving the line is a conditional DELETE. If a promotion won the
        // row first, this deletes nothing and the hold it created is released below.
        var left = await db.Waitlist.Where(w => w.OrderId == command.OrderId).ExecuteDeleteAsync(ct);
        if (left == 1)
        {
            db.Enqueue("sittings", Topology.ReleasedKey, command.OrderId, new CoversReleased(command.OrderId, 0, command.Reason));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        }

        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.OrderId == command.OrderId, ct);
        var released = false;
        if (hold is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var flipped = await db.Holds
                .Where(h => h.OrderId == command.OrderId && h.Status == HoldStatus.Held)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.Status, HoldStatus.Released)
                    .SetProperty(h => h.ReleasedAt, now)
                    .SetProperty(h => h.ReleaseReason, command.Reason), ct);
            if (flipped == 1)
            {
                await db.Sittings
                    .Where(s => s.Id == hold.SittingId && s.CoversLeft + hold.Covers <= s.CoversTotal)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CoversLeft, x => x.CoversLeft + hold.Covers), ct);
                await db.Guests
                    .Where(c => c.SittingId == hold.SittingId && c.OwnerId == hold.OwnerId && c.Covers >= hold.Covers)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Covers, x => x.Covers - hold.Covers), ct);
                db.Enqueue("sittings", Topology.ReleasedKey, command.OrderId,
                    new CoversReleased(command.OrderId, hold.Covers, command.Reason));
                await PromoteAsync(hold.SittingId, ct);
                released = true;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return released;
    }

    /// <summary>
    /// Serves the waitlist first come, first served while the covers that came back last. Strict order: if the head of
    /// the line does not fit yet, nobody behind it jumps ahead. Runs inside the release transaction, which already holds
    /// the sitting row, so two promotions for one sitting never run at once; the DELETE is the claim on each entry.
    /// </summary>
    private async Task PromoteAsync(Guid sittingId, CancellationToken ct)
    {
        while (true)
        {
            var head = await db.Waitlist.AsNoTracking().Where(w => w.SittingId == sittingId)
                .OrderBy(w => w.Seq).FirstOrDefaultAsync(ct);
            if (head is null)
                return;

            var taken = await db.Sittings
                .Where(s => s.Id == sittingId && s.CoversLeft >= head.Covers)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.CoversLeft, x => x.CoversLeft - head.Covers), ct);
            if (taken == 0)
                return;

            if (await db.Waitlist.Where(w => w.Seq == head.Seq).ExecuteDeleteAsync(ct) == 0)
            {
                await GiveBackAsync(sittingId, head.Covers, ct); // it left the line meanwhile
                continue;
            }

            var withinCap = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO sittings."SittingGuests" ("SittingId", "OwnerId", "Covers")
                SELECT d."Id", {head.OwnerId}, {head.Covers} FROM sittings."Sittings" d
                WHERE d."Id" = {sittingId} AND d."MaxPerGuest" >= {head.Covers}
                ON CONFLICT ("SittingId", "OwnerId") DO UPDATE SET "Covers" = sittings."SittingGuests"."Covers" + {head.Covers}
                WHERE sittings."SittingGuests"."Covers" + {head.Covers} <=
                      (SELECT "MaxPerGuest" FROM sittings."Sittings" WHERE "Id" = {sittingId})
                """, ct);
            if (withinCap == 0)
            {
                // The guest picked up other covers while waiting and would now pass the limit: the place is refused.
                await GiveBackAsync(sittingId, head.Covers, ct);
                var limit = await db.Sittings.AsNoTracking().Where(s => s.Id == sittingId).Select(s => s.MaxPerGuest).FirstAsync(ct);
                db.Enqueue("sittings", Topology.ShortKey, head.OrderId,
                    new CoversShort(head.OrderId, head.Reference, head.Covers, 0, "cap", limit));
                continue;
            }

            db.Holds.Add(new CoverHold
            {
                OrderId = head.OrderId,
                OwnerId = head.OwnerId,
                SittingId = sittingId,
                Covers = head.Covers,
                Status = HoldStatus.Held,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("sittings", Topology.HeldKey, head.OrderId,
                new CoversHeld(head.OrderId, head.OwnerId, sittingId, head.Covers, head.Reference));
            await db.SaveChangesAsync(ct);
        }
    }

    private Task<int> GiveBackAsync(Guid sittingId, int covers, CancellationToken ct) =>
        db.Sittings.Where(s => s.Id == sittingId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CoversLeft, x => x.CoversLeft + covers), ct);
}
