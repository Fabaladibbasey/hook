using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hook.Features.Whatsapp.ReceiveWebhook;

public class AmbiguousIntentDraftConfiguration : IEntityTypeConfiguration<AmbiguousIntentDraft>
{
    public void Configure(EntityTypeBuilder<AmbiguousIntentDraft> builder)
    {
        builder.ToTable("ambiguous_intent_drafts");
        builder.HasKey(d => d.Phone);
        builder.Property(d => d.Phone).HasMaxLength(20);
        builder.Property(d => d.OriginalText).HasMaxLength(2000).IsRequired();
        builder.HasIndex(d => d.CreatedAt).HasDatabaseName("ix_ambiguous_intent_drafts_created_at");
    }
}
