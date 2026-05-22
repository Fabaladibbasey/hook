using Hook.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ServiceRequestEntity = Hook.Features.ServiceRequest.RequestAggregate.ServiceRequest;

namespace Hook.Features.Matching.MatchAggregate;

public class MatchConfiguration : IEntityTypeConfiguration<Match>
{
    public void Configure(EntityTypeBuilder<Match> builder)
    {
        builder.ToTable("matches");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.RequestId).IsRequired();
        builder.Property(m => m.ProviderPhone).HasMaxLength(20).IsRequired();
        builder.Property(m => m.ServiceSlug).HasMaxLength(80).IsRequired();
        // Schema-only safety net: code default (Match.Kind = Exact) drives every
        // insert. Kept so a raw SQL insert / future patch path can't store NULL.
        // Sentinel tells EF the CLR default (Exact) is "set" — without it EF treats
        // Exact as "unset" and lets the DB default fill it, producing a startup warning.
        builder.PropertyAsStringEnum(m => m.Kind, 12)
               .HasDefaultValueSql("'Exact'")
               .HasSentinel(MatchKind.Exact);
        builder.HasIndex(m => m.RequestId).HasDatabaseName("ix_matches_request_id");
        builder.HasIndex(m => m.ProviderPhone).HasDatabaseName("ix_matches_provider_phone");
        builder.HasIndex(m => new { m.RequestId, m.PickedAt }).HasDatabaseName("ix_matches_request_picked_at");
        // Covers MatchRepository.GetForRequestAsync ordering on PICK/NEXT paths.
        builder.HasIndex(m => new { m.RequestId, m.Score, m.DistanceKm, m.CreatedAt, m.Id })
               .HasDatabaseName("ix_matches_request_score_distance_created_id")
               .IsDescending(false, true, false, false, false);
        // DB guard against Wolverine retry producing a duplicate Match row.
        builder.HasIndex(m => new { m.RequestId, m.ProviderPhone })
               .IsUnique()
               .HasDatabaseName(MatchConstants.RequestProviderUniqueIndexName);
        builder.HasOne<ServiceRequestEntity>()
               .WithMany()
               .HasForeignKey(m => m.RequestId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
