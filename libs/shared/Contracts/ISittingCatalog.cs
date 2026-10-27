namespace Bistro.Shared.Contracts;

public record SittingInfo(Guid Id, string Code, long PriceCents);

/// <summary>
/// What Orders may ask Sittings, without referencing it. Sittings implements it over its own tables;
/// Orders never reads another module's schema.
/// </summary>
public interface ISittingCatalog
{
    Task<SittingInfo?> FindAsync(string code, CancellationToken ct);
}
