using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hook.Features.MetaTemplates;

public class WhatsappContactConfiguration : IEntityTypeConfiguration<WhatsappContact>
{
    public void Configure(EntityTypeBuilder<WhatsappContact> builder)
    {
        builder.ToTable("whatsapp_contacts");
        builder.HasKey(c => c.Phone);
        builder.Property(c => c.Phone).HasMaxLength(20);
        builder.Property(c => c.LastTipKey).HasMaxLength(64).HasDefaultValue(string.Empty);
        builder.Property(c => c.LastTipAt).HasDefaultValueSql("'1970-01-01 00:00:00+00'");
        builder.HasIndex(c => c.LastInboundAt).HasDatabaseName("ix_whatsapp_contacts_last_inbound_at");
    }
}
