using System.Text.Json;
using Hook.Features.Tips;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Hook.Features.MetaTemplates;

public class WhatsappContactConfiguration : IEntityTypeConfiguration<WhatsappContact>
{
    private static readonly JsonSerializerOptions JsonOpts = new();

    private static readonly ValueConverter<Dictionary<TipTrigger, DateTimeOffset>, string> TipCooldownsConverter =
        new(
            v => JsonSerializer.Serialize(v, JsonOpts),
            v => JsonSerializer.Deserialize<Dictionary<TipTrigger, DateTimeOffset>>(v, JsonOpts)
                 ?? new Dictionary<TipTrigger, DateTimeOffset>());

    private static readonly ValueComparer<Dictionary<TipTrigger, DateTimeOffset>> TipCooldownsComparer =
        new(
            (a, b) => ReferenceEquals(a, b) || (a != null && b != null
                && a.Count == b.Count
                && !a.Except(b).Any()),
            v => v.Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key, kv.Value)),
            v => v.ToDictionary(kv => kv.Key, kv => kv.Value));

    public void Configure(EntityTypeBuilder<WhatsappContact> builder)
    {
        builder.ToTable("whatsapp_contacts");
        builder.HasKey(c => c.Phone);
        builder.Property(c => c.Phone).HasMaxLength(20);
        builder.Property(c => c.TipCooldowns)
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb")
            .HasConversion(TipCooldownsConverter, TipCooldownsComparer);
        builder.Property(c => c.PreferredLocale)
            .HasMaxLength(5)
            .HasDefaultValue(string.Empty);
        builder.HasIndex(c => c.LastInboundAt).HasDatabaseName("ix_whatsapp_contacts_last_inbound_at");
    }
}
