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
        builder.PropertyAsStringEnum(m => m.Kind, 12).HasDefaultValueSql("'Exact'");
        builder.HasIndex(m => m.RequestId).HasDatabaseName("ix_matches_request_id");
        builder.HasIndex(m => m.ProviderPhone).HasDatabaseName("ix_matches_provider_phone");
        builder.HasIndex(m => new { m.RequestId, m.PickedAt }).HasDatabaseName("ix_matches_request_picked_at");
        // Covers MatchRepository.GetForRequestAsync ordering: WHERE RequestId = ?
        // ORDER BY Score DESC, DistanceKm, CreatedAt, Id. Avoids a sort step on hot
        // PICK/NEXT paths once a request accumulates many candidate rows.
        builder.HasIndex(m => new { m.RequestId, m.Score, m.DistanceKm, m.CreatedAt, m.Id })
               .HasDatabaseName("ix_matches_request_score_distance_created_id")
               .IsDescending(false, true, false, false, false);
        // Defense-in-depth against handler-retry duplicates: a Wolverine retry that
        // re-runs RunForRequestAsync after a partial commit would otherwise produce
        // a second Match row for the same provider, since ShownProviderPhones uses
        // an in-memory dedupe (ServiceRequest.RecordShown) and isn't enforced at
        // the DB level.
        builder.HasIndex(m => new { m.RequestId, m.ProviderPhone })
               .IsUnique()
               .HasDatabaseName(MatchConstants.RequestProviderUniqueIndexName);
        builder.HasOne<ServiceRequestEntity>()
               .WithMany()
               .HasForeignKey(m => m.RequestId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
