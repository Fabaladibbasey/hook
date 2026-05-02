using Hook.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hook.Features.ProviderAvailability.Register;

public class RegistrationDraftConfiguration : IEntityTypeConfiguration<RegistrationDraft>
{
    public void Configure(EntityTypeBuilder<RegistrationDraft> builder)
    {
        builder.ToTable("provider_registration_drafts");
        builder.HasKey(r => r.Phone);
        builder.Property(r => r.Phone).HasMaxLength(20);
        builder.PropertyAsStringEnum(r => r.Step, 32);
        builder.HasJsonbArray(r => r.DraftServices);
        builder.Property(r => r.DraftFormattedAddress).HasMaxLength(512);
    }
}
