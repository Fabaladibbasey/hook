using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hook.Features.ServiceTaxonomy.JudgeParent;

public sealed class JudgeParentDedupConfiguration : IEntityTypeConfiguration<JudgeParentDedup>
{
    public void Configure(EntityTypeBuilder<JudgeParentDedup> builder)
    {
        builder.ToTable("judge_parent_dedup");
        builder.HasKey(d => d.Slug);
        builder.Property(d => d.Slug).HasMaxLength(128);
        builder.Property(d => d.JudgedAt).IsRequired();
        builder.HasIndex(d => d.JudgedAt).HasDatabaseName("ix_judge_parent_dedup_judged_at");
    }
}
