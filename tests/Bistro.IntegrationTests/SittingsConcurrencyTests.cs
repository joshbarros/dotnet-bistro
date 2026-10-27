using System.Collections.Concurrent;
using Bistro.Sittings;
using Bistro.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Bistro.IntegrationTests;

public class SittingsConcurrencyTests
{
    [Fact]
    public async Task LastCover_TwoConcurrentOrders_OneHeldOneShortNoHoldForTheLoser()
    {
        await TestDatabase.OpenAsync();
        var sitting = await TestDatabase.AddSittingAsync(coversTotal: 1);

        var outcomes = new ConcurrentBag<(Guid Order, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var orderId = Guid.CreateVersion7();
            await using var db = TestDatabase.Sittings();
            var outcome = await new SittingsHandler(db).ReserveAsync(
                Guid.CreateVersion7(), new ReserveCovers(orderId, Guid.NewGuid(), sitting.Id, 1, $"RSV-{n}"));
            outcomes.Add((orderId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await using var check = TestDatabase.Sittings();
        var remaining = await check.Sittings.Where(s => s.Id == sitting.Id).Select(s => s.CoversLeft).FirstAsync();
        var holds = await check.Holds.ToListAsync();
        var loser = outcomes.Single(o => o.Outcome == ReserveOutcome.Short).Order;

        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Held);
        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Short);
        Assert.Equal(0, remaining);
        Assert.Single(holds);
        Assert.DoesNotContain(holds, h => h.OrderId == loser);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.HeldKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ShortKey));
    }

    [Fact]
    public async Task Release_GivesTheCoversBackExactlyOnce()
    {
        await TestDatabase.OpenAsync();
        var sitting = await TestDatabase.AddSittingAsync(coversTotal: 4);
        var orderId = Guid.CreateVersion7();
        await using (var db = TestDatabase.Sittings())
            await new SittingsHandler(db).ReserveAsync(Guid.CreateVersion7(), new ReserveCovers(orderId, Guid.NewGuid(), sitting.Id, 3, "RSV-1"));

        // Two different commands (poison compensation and a refund) both ask for the same covers back.
        await using var first = TestDatabase.Sittings();
        await using var second = TestDatabase.Sittings();
        var one = await new SittingsHandler(first).ReleaseAsync(Guid.CreateVersion7(), new ReleaseCovers(orderId, "poison"));
        var two = await new SittingsHandler(second).ReleaseAsync(Guid.CreateVersion7(), new ReleaseCovers(orderId, "refund"));

        await using var check = TestDatabase.Sittings();
        Assert.True(one);
        Assert.False(two);
        Assert.Equal(4, await check.Sittings.Where(s => s.Id == sitting.Id).Select(s => s.CoversLeft).FirstAsync());
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        await TestDatabase.OpenAsync();
        var sitting = await TestDatabase.AddSittingAsync(coversTotal: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveCovers(Guid.CreateVersion7(), Guid.NewGuid(), sitting.Id, 2, "RSV-1");

        await using var a = TestDatabase.Sittings();
        await using var b = TestDatabase.Sittings();
        var first = await new SittingsHandler(a).ReserveAsync(messageId, command);
        var second = await new SittingsHandler(b).ReserveAsync(messageId, command);

        await using var check = TestDatabase.Sittings();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, await check.Sittings.Where(s => s.Id == sitting.Id).Select(s => s.CoversLeft).FirstAsync());
    }

    [Fact]
    public async Task TheDatabaseItself_RefusesANegativeRemainder()
    {
        await TestDatabase.OpenAsync();
        var sitting = await TestDatabase.AddSittingAsync(coversTotal: 2);
        await using var db = TestDatabase.Sittings();

        // A bug that skipped the WHERE clause would hit the check constraint instead of overselling.
        await Assert.ThrowsAnyAsync<Exception>(() => db.Sittings.Where(s => s.Id == sitting.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CoversLeft, x => x.CoversLeft - 3)));
    }

    [Fact]
    public async Task Cap_ConcurrentOrdersByTheSameGuest_NeverPassTheCap()
    {
        await TestDatabase.OpenAsync();
        var sitting = await TestDatabase.AddSittingAsync(coversTotal: 20, maxPerGuest: 2);
        var guest = Guid.NewGuid();

        var outcomes = new ConcurrentBag<ReserveOutcome>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 6).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            await using var db = TestDatabase.Sittings();
            outcomes.Add(await new SittingsHandler(db).ReserveAsync(
                Guid.CreateVersion7(), new ReserveCovers(Guid.CreateVersion7(), guest, sitting.Id, 1, $"RSV-{n}")));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await using var check = TestDatabase.Sittings();
        Assert.Equal(2, outcomes.Count(o => o == ReserveOutcome.Held));
        Assert.Equal(4, outcomes.Count(o => o == ReserveOutcome.CapReached));
        Assert.Equal(2, await check.Guests.Where(c => c.OwnerId == guest).Select(c => c.Covers).SingleAsync());
        Assert.Equal(18, await check.Sittings.Where(s => s.Id == sitting.Id).Select(s => s.CoversLeft).FirstAsync());
        Assert.Equal(2, await check.Holds.CountAsync());
    }

    [Fact]
    public async Task Cap_AtWholeRequestLevel_RefusesTheOrderThatWouldCrossIt()
    {
        await TestDatabase.OpenAsync();
        var sitting = await TestDatabase.AddSittingAsync(coversTotal: 10, maxPerGuest: 2);
        var guest = Guid.NewGuid();
        await using var db = TestDatabase.Sittings();
        var handler = new SittingsHandler(db);

        Assert.Equal(ReserveOutcome.Held, await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveCovers(Guid.CreateVersion7(), guest, sitting.Id, 1, "RSV-1")));
        // 1 held + 2 asked = 3 > 2: nothing moves, neither the stock nor the guest's count
        Assert.Equal(ReserveOutcome.CapReached, await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveCovers(Guid.CreateVersion7(), guest, sitting.Id, 2, "RSV-2")));
        // another guest is unaffected
        Assert.Equal(ReserveOutcome.Held, await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveCovers(Guid.CreateVersion7(), Guid.NewGuid(), sitting.Id, 2, "RSV-3")));

        await using var check = TestDatabase.Sittings();
        Assert.Equal(1, await check.Guests.Where(c => c.OwnerId == guest).Select(c => c.Covers).SingleAsync());
        Assert.Equal(7, await check.Sittings.Where(s => s.Id == sitting.Id).Select(s => s.CoversLeft).FirstAsync());
        var refusal = await check.Outbox.Where(o => o.RoutingKey == Topology.ShortKey).ToListAsync();
        Assert.Single(refusal);
        Assert.Contains("\"cap\"", refusal[0].Payload);
    }

    [Fact]
    public async Task Cap_StockShortAfterTheCapPassed_RollsTheCapBack()
    {
        await TestDatabase.OpenAsync();
        var sitting = await TestDatabase.AddSittingAsync(coversTotal: 1, maxPerGuest: 2);
        var guest = Guid.NewGuid();
        await using var db = TestDatabase.Sittings();

        var outcome = await new SittingsHandler(db).ReserveAsync(
            Guid.CreateVersion7(), new ReserveCovers(Guid.CreateVersion7(), guest, sitting.Id, 2, "RSV-1"));

        await using var check = TestDatabase.Sittings();
        Assert.Equal(ReserveOutcome.Short, outcome);
        Assert.False(await check.Guests.AnyAsync(c => c.OwnerId == guest));
        Assert.Equal(1, await check.Sittings.Where(s => s.Id == sitting.Id).Select(s => s.CoversLeft).FirstAsync());
    }

    [Fact]
    public async Task Cap_ReleaseGivesTheGuestTheirAllowanceBack()
    {
        await TestDatabase.OpenAsync();
        var sitting = await TestDatabase.AddSittingAsync(coversTotal: 10, maxPerGuest: 2);
        var guest = Guid.NewGuid();
        var first = Guid.CreateVersion7();
        await using var db = TestDatabase.Sittings();
        var handler = new SittingsHandler(db);
        await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveCovers(first, guest, sitting.Id, 2, "RSV-1"));
        Assert.Equal(ReserveOutcome.CapReached, await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveCovers(Guid.CreateVersion7(), guest, sitting.Id, 1, "RSV-2")));

        Assert.True(await handler.ReleaseAsync(Guid.CreateVersion7(), new ReleaseCovers(first, "refund")));
        Assert.False(await handler.ReleaseAsync(Guid.CreateVersion7(), new ReleaseCovers(first, "refund again")));

        Assert.Equal(ReserveOutcome.Held, await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveCovers(Guid.CreateVersion7(), guest, sitting.Id, 2, "RSV-3")));
        await using var check = TestDatabase.Sittings();
        Assert.Equal(2, await check.Guests.Where(c => c.OwnerId == guest).Select(c => c.Covers).SingleAsync());
    }
}
