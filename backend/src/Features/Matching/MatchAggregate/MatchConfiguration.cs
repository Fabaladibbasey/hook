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
        builder.HasIndex(m => m.RequestId).HasDatabaseName("ix_matches_request_id");
        builder.HasIndex(m => m.ProviderPhone).HasDatabaseName("ix_matches_provider_phone");
        builder.HasIndex(m => new { m.RequestId, m.PickedAt }).HasDatabaseName("ix_matches_request_picked_at");
        builder.HasOne<ServiceRequestEntity>()
               .WithMany()
               .HasForeignKey(m => m.RequestId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
