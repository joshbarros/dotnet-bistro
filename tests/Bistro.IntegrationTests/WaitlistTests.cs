using System.Collections.Concurrent;
using Bistro.Shared.Messaging;
using Bistro.Sittings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Bistro.IntegrationTests;

public class WaitlistTests
{
    private static ReserveCovers Ask(Guid order, Guid owner, Sitting sitting, int covers, bool waitlist = true) =>
        new(order, owner, sitting.Id, covers, $"RSV-{order:N}"[..12], waitlist);

    private static async Task<ReserveOutcome> ReserveAsync(ReserveCovers command)
    {
        await using var db = TestDatabase.Sittings();
        return await new SittingsHandler(db).ReserveAsync(Guid.CreateVersion7(), command);
    }

    private static async Task<bool> ReleaseAsync(Guid order, string reason = "refund")
    {
        await using var db = TestDatabase.Sittings();
        return await new SittingsHandler(db).ReleaseAsync(Guid.CreateVersion7(), new ReleaseCovers(order, reason));
    }

    [Fact]
    public async Task FullSitting_WithoutTheFlag_IsRefused_WithTheFlag_JoinsTheLine()
    {
        await TestDatabase.OpenAsync();
        var sitting = await TestDatabase.AddSittingAsync(coversTotal: 4);
        Assert.Equal(ReserveOutcome.Held, await ReserveAsync(Ask(Guid.CreateVersion7(), Guid.NewGuid(), sitting, 4)));

        Assert.Equal(ReserveOutcome.Short, await ReserveAsync(Ask(Guid.CreateVersion7(), Guid.NewGuid(), sitting, 2, waitlist: false)));
        var waiting = Guid.CreateVersion7();
        Assert.Equal(ReserveOutcome.Waitlisted, await ReserveAsync(Ask(waiting, Guid.NewGuid(), sitting, 2)));

        await using var check = TestDatabase.Sittings();
        Assert.Equal(waiting, await check.Waitlist.Select(w => w.OrderId).SingleAsync());
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.WaitlistedKey));
        Assert.Equal(0, await check.Sittings.Where(s => s.Id == sitting.Id).Select(s => s.CoversLeft).SingleAsync());
    }

    [Fact]
    public async Task AReleasedHold_PromotesTheLineInOrder()
    {
        await TestDatabase.OpenAsync();
        var sitting = await TestDatabase.AddSittingAsync(coversTotal: 4);
        var first = Guid.CreateVersion7();
        await ReserveAsync(Ask(first, Guid.NewGuid(), sitting, 4));
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        var c = Guid.CreateVersion7();
        await ReserveAsync(Ask(a, Guid.NewGuid(), sitting, 2));
        await ReserveAsync(Ask(b, Guid.NewGuid(), sitting, 2));
        await ReserveAsync(Ask(c, Guid.NewGuid(), sitting, 2));

        Assert.True(await ReleaseAsync(first));

        await using var check = TestDatabase.Sittings();
        var held = await check.Holds.Where(h => h.Status == HoldStatus.Held).Select(h => h.OrderId).ToListAsync();
        Assert.Equal(new[] { a, b }.OrderBy(x => x), held.OrderBy(x => x));
        Assert.Equal(c, await check.Waitlist.Select(w => w.OrderId).SingleAsync());
        Assert.Equal(0, await check.Sittings.Where(s => s.Id == sitting.Id).Select(s => s.CoversLeft).SingleAsync());
        Assert.Equal(2, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.HeldKey && o.Key != first.ToString()));
    }

    [Fact]
    public async Task TheHeadOfTheLineWaits_NobodyBehindItJumpsAhead()
    {
        await TestDatabase.OpenAsync();
        var sitting = await TestDatabase.AddSittingAsync(coversTotal: 4);
        var first = Guid.CreateVersion7();
        await ReserveAsync(Ask(first, Guid.NewGuid(), sitting, 4));
        var big = Guid.CreateVersion7();
        var small = Guid.CreateVersion7();
        await ReserveAsync(Ask(big, Guid.NewGuid(), sitting, 4));
        await ReserveAsync(Ask(small, Guid.NewGuid(), sitting, 1));

        // Two covers come back: the party of 4 at the head cannot sit yet, and the party of 1 must not pass it.
        var partial = Guid.CreateVersion7();
        await using (var db = TestDatabase.Sittings())
            await db.Sittings.Where(s => s.Id == sitting.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CoversLeft, 2));
        Assert.Equal(ReserveOutcome.Waitlisted, await ReserveAsync(Ask(partial, Guid.NewGuid(), sitting, 1)));

        await using var check = TestDatabase.Sittings();
        Assert.Equal(3, await check.Waitlist.CountAsync());
        Assert.Equal(1, await check.Holds.CountAsync());
    }

    [Fact]
    public async Task LeavingTheLine_RemovesThePlace_AndHoldsNothing()
    {
        await TestDatabase.OpenAsync();
        var sitting = await TestDatabase.AddSittingAsync(coversTotal: 2);
        var first = Guid.CreateVersion7();
        await ReserveAsync(Ask(first, Guid.NewGuid(), sitting, 2));
        var leaver = Guid.CreateVersion7();
        var stays = Guid.CreateVersion7();
        await ReserveAsync(Ask(leaver, Guid.NewGuid(), sitting, 2));
        await ReserveAsync(Ask(stays, Guid.NewGuid(), sitting, 2));

        Assert.True(await ReleaseAsync(leaver));
        Assert.False(await ReleaseAsync(leaver, "again")); // nothing left to remove, no hold to release

        Assert.True(await ReleaseAsync(first));
        await using var check = TestDatabase.Sittings();
        Assert.Equal(new[] { stays }, await check.Holds.Where(h => h.Status == HoldStatus.Held).Select(h => h.OrderId).ToArrayAsync());
        Assert.False(await check.Holds.AnyAsync(h => h.OrderId == leaver));
    }

    [Fact]
    public async Task ARefundAndALeave_RacingForTheSameCovers_NeverOverbook()
    {
        for (var round = 0; round < 5; round++)
        {
            await TestDatabase.OpenAsync();
            var sitting = await TestDatabase.AddSittingAsync(coversTotal: 2);
            var first = Guid.CreateVersion7();
            var waiter = Guid.CreateVersion7();
            await ReserveAsync(Ask(first, Guid.NewGuid(), sitting, 2));
            await ReserveAsync(Ask(waiter, Guid.NewGuid(), sitting, 2));

            var gate = new TaskCompletionSource();
            var tasks = new[] { first, waiter }.Select(o => Task.Run(async () => { await gate.Task; await ReleaseAsync(o); })).ToArray();
            gate.SetResult();
            await Task.WhenAll(tasks);

            await using var check = TestDatabase.Sittings();
            var left = await check.Sittings.Where(s => s.Id == sitting.Id).Select(s => s.CoversLeft).SingleAsync();
            var active = await check.Holds.Where(h => h.Status == HoldStatus.Held).SumAsync(h => h.Covers);
            Assert.Equal(2, left + active); // every cover is either free or held, never both, never lost
            Assert.Equal(0, await check.Waitlist.CountAsync());
            Assert.Equal(0, active); // both orders left, so nothing may stay held
        }
    }

    [Fact]
    public async Task PromotionPastTheGuestLimit_IsRefusedAsCap_AndTheCoversMoveOn()
    {
        await TestDatabase.OpenAsync();
        var sitting = await TestDatabase.AddSittingAsync(coversTotal: 4, maxPerGuest: 2);
        var first = Guid.CreateVersion7();
        var greedy = Guid.NewGuid();
        var other = Guid.CreateVersion7();
        await ReserveAsync(Ask(first, Guid.NewGuid(), sitting, 4 / 2));
        await ReserveAsync(Ask(Guid.CreateVersion7(), Guid.NewGuid(), sitting, 2));

        var late = Guid.CreateVersion7();
        var next = Guid.CreateVersion7();
        await ReserveAsync(Ask(late, greedy, sitting, 2));  // waits
        await ReserveAsync(Ask(next, Guid.NewGuid(), sitting, 2)); // waits behind
        // while waiting, the greedy guest gets 2 covers by another route
        await using (var db = TestDatabase.Sittings())
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""INSERT INTO sittings."SittingGuests" ("SittingId", "OwnerId", "Covers") VALUES ({sitting.Id}, {greedy}, 2)""");

        Assert.True(await ReleaseAsync(first));

        await using var check = TestDatabase.Sittings();
        Assert.False(await check.Holds.AnyAsync(h => h.OrderId == late));
        Assert.True(await check.Holds.AnyAsync(h => h.OrderId == next));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ShortKey && o.Key == late.ToString()));
    }
}
