using Hook.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hook.Features.ProviderAvailability.AvailabilityAggregate;

public class ProviderAvailabilityConfiguration : IEntityTypeConfiguration<ProviderAvailability>
{
    public void Configure(EntityTypeBuilder<ProviderAvailability> builder)
    {
        builder.ToTable("provider_availabilities");
        builder.HasKey(p => p.Phone);
        builder.Property(p => p.Phone).HasMaxLength(20);
        builder.HasJsonbArray(p => p.Services);
        builder.Property(p => p.Location).HasColumnType("geography (Point, 4326)").IsRequired();
        builder.Property(p => p.FormattedAddress).HasMaxLength(512);
        builder.Property(p => p.ExpiresAt).IsRequired();
        builder.HasIndex(p => p.ExpiresAt).HasDatabaseName("ix_provider_availabilities_expires_at");
        builder.HasIndex(p => p.Location).HasDatabaseName("ix_provider_availabilities_location").HasMethod("gist");
        // jsonb_path_ops keeps the index small and is what Npgsql's
        // `array.Any(slug => p.Services.Contains(slug))` translates the `?|`
        // predicate against. Required once hierarchy expansion multiplies the
        // jsonb membership check by `1 + parent + children.Count`.
        builder.HasIndex(p => p.Services)
            .HasMethod("gin")
            .HasOperators("jsonb_path_ops")
            .HasDatabaseName("ix_provider_availabilities_services_gin");
    }
}
