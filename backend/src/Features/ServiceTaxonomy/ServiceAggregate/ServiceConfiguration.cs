using Hook.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hook.Features.ServiceTaxonomy.ServiceAggregate;

public class ServiceConfiguration : IEntityTypeConfiguration<Service>
{
    public void Configure(EntityTypeBuilder<Service> builder)
    {
        builder.ToTable("services");
        builder.HasKey(s => s.Slug);
        builder.Property(s => s.Slug).HasMaxLength(80);
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.HasJsonbArray(s => s.RawExamples);

        builder.HasIndex(s => s.Slug).HasDatabaseName("ix_services_slug_trgm")
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");
    }
}
