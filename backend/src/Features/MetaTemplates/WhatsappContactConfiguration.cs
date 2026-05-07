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
        builder.HasIndex(c => c.LastInboundAt).HasDatabaseName("ix_whatsapp_contacts_last_inbound_at");
    }
}
