using Hook.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hook.Features.ServiceRequest.RequestAggregate;

public class ServiceRequestConfiguration : IEntityTypeConfiguration<ServiceRequest>
{
    public void Configure(EntityTypeBuilder<ServiceRequest> builder)
    {
        builder.ToTable("service_requests");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.ClientPhone).HasMaxLength(20).IsRequired();
        builder.Property(r => r.ServiceSlug).HasMaxLength(80).IsRequired();
        builder.Property(r => r.Location).HasColumnType("geography (Point, 4326)").IsRequired();
        builder.Property(r => r.FormattedAddress).HasMaxLength(512);
        builder.Property(r => r.Description).HasMaxLength(2000);
        builder.PropertyAsStringEnum(r => r.Status, 16);
        builder.HasJsonbArray(r => r.ShownProviderPhones);
        builder.Property(r => r.SharePhoneNumber).HasDefaultValue(false);

        builder.HasIndex(r => new { r.ClientPhone, r.Status })
               .HasDatabaseName("ix_service_requests_client_phone_status");
        builder.HasIndex(r => r.Location).HasDatabaseName("ix_service_requests_location").HasMethod("gist");
        builder.HasIndex(r => r.CreatedAt).HasDatabaseName("ix_service_requests_created_at");
        builder.HasIndex(r => r.CreatedAt)
               .HasDatabaseName("ix_service_requests_closed_created_at")
               .HasFilter($"\"Status\" = '{nameof(ServiceRequestStatus.Closed)}'");
    }
}
