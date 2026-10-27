using Bistro.Shared;
using Bistro.Shared.Auth;
using Bistro.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bistro.Sittings;

public record CreateSittingRequest(string? Code, string? Name, string? Room, string? Chef, DateTimeOffset? ServiceDate, long CoversTotal, long PriceCents, long? MaxPerGuest = null);

public record SittingDto(Guid Id, string Code, string Name, string Room, string Chef, DateTimeOffset ServiceDate, long PriceCents, int CoversTotal, int CoversLeft, int MaxPerGuest)
{
    public static SittingDto From(Sitting s) => new(s.Id, s.Code, s.Name, s.Room, s.Chef, s.ServiceDate, s.PriceCents, s.CoversTotal, s.CoversLeft, s.MaxPerGuest);
}

/// <summary>What other modules may ask Sittings, over Sittings's own tables.</summary>
public sealed class SittingCatalog(SittingsDbContext db) : ISittingCatalog
{
    public async Task<SittingInfo?> FindAsync(string code, CancellationToken ct) =>
        await db.Sittings.AsNoTracking().Where(s => s.Code == code).Select(s => new SittingInfo(s.Id, s.Code, s.PriceCents)).FirstOrDefaultAsync(ct);
}

[ApiController]
[Route("api/sittings")]
[Authorize]
public class SittingsController(SittingsDbContext db) : ControllerBase
{
    private const int MaxCoversTotal = 5000;
    private const long MaxPriceCents = 1_000_000;

    [HttpGet]
    public async Task<IEnumerable<SittingDto>> List(CancellationToken ct) =>
        (await db.Sittings.AsNoTracking().OrderBy(s => s.ServiceDate).ThenBy(s => s.Code).ToListAsync(ct)).Select(SittingDto.From);

    [HttpGet("{code}")]
    public async Task<ActionResult<SittingDto>> Get(string code, CancellationToken ct)
    {
        var sitting = await db.Sittings.AsNoTracking().FirstOrDefaultAsync(s => s.Code == code, ct);
        return sitting is null ? NotFound() : SittingDto.From(sitting);
    }

    /// <summary>Opens a sitting. Staff only. This is the only place CoversLeft is initialised; after that only the Sittings handler writes it.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.ManageCatalog)]
    public async Task<ActionResult<SittingDto>> Create(CreateSittingRequest request, CancellationToken ct)
    {
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 20)
            throw new DomainException(400, "Code is required and at most 20 characters.");
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Room) || string.IsNullOrWhiteSpace(request.Chef))
            throw new DomainException(400, "Name, room and chef are required.");
        if (request.CoversTotal < 1 || request.CoversTotal > MaxCoversTotal)
            throw new DomainException(400, $"Covers on sale must be between 1 and {MaxCoversTotal} covers.");
        if (request.PriceCents < 1 || request.PriceCents > MaxPriceCents)
            throw new DomainException(400, $"Price must be between 1 and {MaxPriceCents} cents per cover.");
        var cap = request.MaxPerGuest ?? Math.Min(8, request.CoversTotal);
        if (cap < 1 || cap > request.CoversTotal)
            throw new DomainException(400, "Per-guest cap must be at least 1 and no more than the covers on sale.");
        if (await db.Sittings.AnyAsync(s => s.Code == code, ct))
            throw new DomainException(409, $"Sitting '{code}' already exists.");

        var sitting = new Sitting
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Name = request.Name.Trim(),
            Room = request.Room.Trim(),
            Chef = request.Chef.Trim(),
            ServiceDate = request.ServiceDate ?? DateTimeOffset.UtcNow.AddDays(14),
            PriceCents = request.PriceCents,
            CoversTotal = (int)request.CoversTotal,
            CoversLeft = (int)request.CoversTotal,
            MaxPerGuest = (int)cap,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Sittings.Add(sitting);
        await db.SaveChangesAsync(ct);
        return Created($"/api/sittings/{sitting.Code}", SittingDto.From(sitting));
    }
}
