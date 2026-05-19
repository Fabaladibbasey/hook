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
        builder.Property(s => s.ParentSlug).HasMaxLength(80);
        builder.HasJsonbArray(s => s.RawExamples)
               .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(s => s.Slug).HasDatabaseName("ix_services_slug_trgm")
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");

        builder.HasOne<Service>()
               .WithMany()
               .HasForeignKey(s => s.ParentSlug)
               .OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(s => s.ParentSlug).HasDatabaseName("ix_services_parent_slug");
    }
}
