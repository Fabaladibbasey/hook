using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

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
    }
}
