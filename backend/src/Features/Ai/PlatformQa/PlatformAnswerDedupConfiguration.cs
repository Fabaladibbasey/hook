using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hook.Features.Ai.PlatformQa;

public sealed class PlatformAnswerDedupConfiguration : IEntityTypeConfiguration<PlatformAnswerDedup>
{
    public void Configure(EntityTypeBuilder<PlatformAnswerDedup> builder)
    {
        builder.ToTable("platform_answer_dedup");
        builder.HasKey(d => new { d.Phone, d.QuestionHash });
        builder.Property(d => d.Phone).HasMaxLength(32);
        builder.Property(d => d.QuestionHash).IsRequired();
        builder.Property(d => d.AnsweredAt).IsRequired();
        builder.HasIndex(d => d.AnsweredAt).HasDatabaseName("ix_platform_answer_dedup_answered_at");
    }
}
