using Bistro.Orders;
using Bistro.Sittings;
using Xunit;

namespace Bistro.UnitTests;

public class DtoTests
{
    [Fact]
    public void OrderDto_MapsEveryFieldAndTheStatusName()
    {
        var now = DateTimeOffset.UtcNow;
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            SittingId = Guid.NewGuid(),
            SittingCode = "S-1",
            Reference = "RSV-1",
            Covers = 3,
            TotalCents = 277_500,
            Status = OrderStatus.Sold,
            ConfirmationNumber = "BT-1",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var dto = OrderDto.From(order);

        Assert.Equal(order.Id, dto.Id);
        Assert.Equal(order.OwnerId, dto.OwnerId);
        Assert.Equal("Sold", dto.Status);
        Assert.Equal(277_500, dto.TotalCents);
        Assert.Equal("BT-1", dto.ConfirmationNumber);
    }

    [Fact]
    public void SittingDto_MapsTheScarceNumber()
    {
        var sitting = new Sitting { Id = Guid.NewGuid(), Code = "S-1", Name = "v", Room = "o", Chef = "d", CoversTotal = 10, CoversLeft = 4, MaxPerGuest = 3 };
        var dto = SittingDto.From(sitting);
        Assert.Equal(10, dto.CoversTotal);
        Assert.Equal(3, dto.MaxPerGuest);
        Assert.Equal(4, dto.CoversLeft);
    }
}
