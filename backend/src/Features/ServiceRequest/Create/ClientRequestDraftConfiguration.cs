using Hook.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hook.Features.ServiceRequest.Create;

public class ClientRequestDraftConfiguration : IEntityTypeConfiguration<ClientRequestDraft>
{
    public void Configure(EntityTypeBuilder<ClientRequestDraft> builder)
    {
        builder.ToTable("client_request_drafts");
        builder.HasKey(r => r.Phone);
        builder.Property(r => r.Phone).HasMaxLength(20);
        builder.PropertyAsStringEnum(r => r.Step, 32);
        builder.Property(r => r.DraftServiceSlug).HasMaxLength(80);
        builder.Property(r => r.DraftFormattedAddress).HasMaxLength(512);
        builder.Property(r => r.DraftDescription).HasMaxLength(2000);
        builder.HasIndex(r => r.UpdatedAt).HasDatabaseName("ix_client_request_drafts_updated_at");
    }
}
