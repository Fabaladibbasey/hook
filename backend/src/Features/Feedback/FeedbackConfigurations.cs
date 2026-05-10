using Hook.Features.Feedback.Models;
using Hook.Features.Feedback.ProviderStatsAggregate;
using Hook.Features.Matching.MatchAggregate;
using Hook.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hook.Features.Feedback;

public class MatchFeedbackConfiguration : IEntityTypeConfiguration<MatchFeedback>
{
    public void Configure(EntityTypeBuilder<MatchFeedback> builder)
    {
        builder.ToTable("match_feedback");
        builder.HasKey(f => f.Id);
        builder.PropertyAsStringEnum(f => f.Step, 20);
        builder.PropertyAsStringEnum(f => f.Answer, 16);
        builder.HasIndex(f => f.MatchId).HasDatabaseName("ix_match_feedback_match");
        builder.HasIndex(f => f.PromptedAt).HasDatabaseName("ix_match_feedback_prompted_at");
        builder.HasIndex(f => new { f.MatchId, f.Step })
               .HasDatabaseName(FeedbackConstants.PendingUniqueIndexName)
               .IsUnique()
               .HasFilter($"\"Answer\" = '{nameof(FeedbackAnswer.Pending)}'");

        // Non-partial covering index for AnyByRequestStepAsync, which scans by Step
        // for any match under a request and intentionally includes answered rows.
        // The partial unique index above only covers Pending rows, so this query
        // would otherwise sequence-scan the table.
        builder.HasIndex(f => new { f.Step, f.MatchId })
               .HasDatabaseName("ix_match_feedback_step_match");

        builder
            .HasOne<Match>()
            .WithMany()
            .HasForeignKey(f => f.MatchId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class ProviderStatsConfiguration : IEntityTypeConfiguration<ProviderStats>
{
    public void Configure(EntityTypeBuilder<ProviderStats> builder)
    {
        builder.ToTable("provider_stats");
        builder.HasKey(s => s.ProviderPhone);
        builder.Property(s => s.ProviderPhone).HasMaxLength(20);
        builder.Property(s => s.LastUpdated).IsConcurrencyToken();
    }
}
