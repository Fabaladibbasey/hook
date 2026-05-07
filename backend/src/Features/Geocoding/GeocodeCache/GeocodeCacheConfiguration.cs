using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hook.Features.Geocoding.GeocodeCache;

public class GeocodeCacheConfiguration : IEntityTypeConfiguration<GeocodeCacheEntry>
{
    public void Configure(EntityTypeBuilder<GeocodeCacheEntry> builder)
    {
        builder.ToTable("geocode_cache");
        builder.HasKey(e => e.Key);
        builder.Property(e => e.Key).HasMaxLength(256);
        builder.Property(e => e.FormattedAddress).HasMaxLength(512);
        builder.Property(e => e.Provider).HasMaxLength(32);
        builder.Property(e => e.FetchedAt).IsRequired();
        builder.HasIndex(e => e.FetchedAt).HasDatabaseName("ix_geocode_cache_fetched_at");
    }
}
