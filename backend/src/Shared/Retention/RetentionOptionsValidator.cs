using Microsoft.Extensions.Options;

namespace Hook.Shared.Retention;

// Cross-property guard the per-property Range attributes cannot express:
// PlatformAnswerDedupCleanupAfter must not exceed the global RetentionDays.
// Misconfig that flips that ordering would let dedup rows outlive global
// retention — defeating the PII bound that the short cleanup window enforces.
internal sealed class RetentionOptionsValidator : IValidateOptions<RetentionOptions>
{
    public ValidateOptionsResult Validate(string? name, RetentionOptions o)
    {
        var globalRetention = TimeSpan.FromDays(o.RetentionDays);
        if (o.PlatformAnswerDedupCleanupAfter > globalRetention)
            return ValidateOptionsResult.Fail(
                $"PlatformAnswerDedupCleanupAfter ({o.PlatformAnswerDedupCleanupAfter}) must not "
              + $"exceed RetentionDays ({globalRetention}).");
        return ValidateOptionsResult.Success;
    }
}
