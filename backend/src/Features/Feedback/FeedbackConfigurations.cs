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
        // Explicit concurrency token — bumped by every aggregate mutation so the
        // second of two racing scopes hits DbUpdateConcurrencyException, which
        // TrySaveAsync catches and surfaces as a false return.
        builder.Property(f => f.Version).IsConcurrencyToken();
        builder.PropertyAsStringEnum(f => f.Step, 20);
        builder.PropertyAsStringEnum(f => f.Answer, 20);
        builder.Property(f => f.Step1RecheckCount).HasDefaultValue(0);
        builder.Property(f => f.NoReason).HasMaxLength(FeedbackConstants.NoReasonMaxLength);
        builder.HasIndex(f => f.MatchId).HasDatabaseName("ix_match_feedback_match");
        builder.HasIndex(f => f.PromptedAt).HasDatabaseName("ix_match_feedback_prompted_at");
        builder.HasIndex(f => new { f.MatchId, f.Step })
               .HasDatabaseName(FeedbackConstants.PendingUniqueIndexName)
               .IsUnique()
               .HasFilter($"\"Answer\" = '{nameof(FeedbackAnswer.Pending)}'");
        builder.HasIndex(f => new { f.Answer, f.PromptedAt })
               .HasDatabaseName(FeedbackConstants.PendingPromptedAtIndexName)
               .IsDescending(false, true)
               .HasFilter($"\"Answer\" = '{nameof(FeedbackAnswer.Pending)}'");

        // Per-request dedupe for Step1: multi-PICK fans out N concurrent Step1
        // handlers; this partial unique forces all but one to fail at insert time.
        // Includes answered rows so a late sibling fired after Step1 was answered
        // also collides.
        builder.HasIndex(f => new { f.RequestId, f.Step })
               .HasDatabaseName(FeedbackConstants.RequestStep1UniqueIndexName)
               .IsUnique()
               .HasFilter($"\"Step\" = '{nameof(FeedbackStep.DidYouFind)}'");

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
