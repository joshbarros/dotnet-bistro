using Bistro.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bistro.Sittings;

public class SittingsDbContext(DbContextOptions<SittingsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "sittings";

    public DbSet<Sitting> Sittings => Set<Sitting>();
    public DbSet<WaitlistEntry> Waitlist => Set<WaitlistEntry>();
    public DbSet<CoverHold> Holds => Set<CoverHold>();
    public DbSet<SittingGuest> Guests => Set<SittingGuest>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Sitting>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(20);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Sittings_Remaining", "\"CoversLeft\" >= 0 AND \"CoversLeft\" <= \"CoversTotal\"");
                t.HasCheckConstraint("CK_Sittings_Cap", "\"MaxPerGuest\" >= 1");
            });
            e.Property(x => x.MaxPerGuest).HasDefaultValue(8);
        });

        b.Entity<SittingGuest>(e =>
        {
            e.ToTable("SittingGuests", t => t.HasCheckConstraint("CK_SittingGuests_Covers", "\"Covers\" >= 0"));
            e.HasKey(x => new { x.SittingId, x.OwnerId });
            e.HasOne<Sitting>().WithMany().HasForeignKey(x => x.SittingId);
        });

        b.Entity<WaitlistEntry>(e =>
        {
            e.ToTable("Waitlist");
            e.HasKey(x => x.Seq);
            e.Property(x => x.Seq).ValueGeneratedOnAdd();
            e.HasIndex(x => x.OrderId).IsUnique();
            e.HasIndex(x => new { x.SittingId, x.Seq });
            e.HasOne<Sitting>().WithMany().HasForeignKey(x => x.SittingId);
        });

        b.Entity<CoverHold>(e =>
        {
            e.ToTable("CoverHolds");
            e.HasKey(x => x.OrderId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne<Sitting>().WithMany().HasForeignKey(x => x.SittingId);
        });
    }
}
