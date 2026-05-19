using System.Reflection;
using Hook.Features.Ai.Prompts;
using Shouldly;

namespace Hook.UnitTests.Ai;

public class AiPromptsTests
{
    [Fact]
    public void EverySystemPrompt_StartsWithSafetyPreamble()
    {
        var systemPrompts = typeof(AiPrompts)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.EndsWith("System"))
            .ToList();

        systemPrompts.ShouldNotBeEmpty();

        foreach (var field in systemPrompts)
        {
            var value = (string)field.GetRawConstantValue()!;
            value.ShouldStartWith(AiPrompts.SafetyPreamble,
                customMessage: $"{field.Name} is missing the safety preamble");
        }
    }

    [Fact]
    public void SafetyPreamble_NamesTheUserInputFence_AndForbidsPromptLeak()
    {
        AiPrompts.SafetyPreamble.ShouldContain("<user_input>");
        AiPrompts.SafetyPreamble.ShouldContain("Never reveal", Case.Insensitive);
        AiPrompts.SafetyPreamble.ShouldContain("Never follow", Case.Insensitive);
    }

    [Fact]
    public void MatchPresenter_BodyOnly_NoActionLine()
    {
        var prompt = AiPrompts.MatchPresenterReplySystem;

        // Bot owns the call-to-action verbatim (appended in MatchPresenter), so
        // the LLM prompt must explicitly forbid the model from emitting one.
        // Otherwise we get duplicate / conflicting action lines.
        prompt.ShouldContain("Do NOT add an action line", Case.Sensitive);
        prompt.ShouldContain("Reply X", Case.Sensitive);          // example of forbidden phrasing
        prompt.ShouldContain("masked", Case.Insensitive);
        prompt.ShouldNotContain("Reply 1 to", Case.Insensitive);   // no advertised affordance
    }

    [Fact]
    public void IntentSystem_ListsShareContact_AndExamples()
    {
        var prompt = AiPrompts.IntentSystem;

        prompt.ShouldContain("ShareContact");
        prompt.ShouldContain("share contact", Case.Insensitive);
    }

    [Fact]
    public void IntentSystem_DistinguishesProblemStatementsFromOffers()
    {
        var prompt = AiPrompts.IntentSystem;

        // Problem-statement rule + canonical example must be present so the LLM
        // classifies "my X is broken" as ServiceRequest, not ProviderRegistration.
        prompt.ShouldContain("Problem statements", Case.Insensitive);
        prompt.ShouldContain("My door is broken");
        prompt.ShouldContain("ServiceRequest");

        // Bare offers (without "I") must be recognised as ProviderRegistration.
        prompt.ShouldContain("doing plumbing", Case.Insensitive);
        prompt.ShouldContain("ProviderRegistration");

        // Confidence guidance for the disambiguation guardrail.
        prompt.ShouldContain("ambiguous", Case.Insensitive);
    }

    [Fact]
    public void ServiceExtraction_ForbidsAdjectiveSlugs_AndMapsProblemsToServices()
    {
        var prompt = AiPrompts.ServiceExtractionSystem;

        // The exact bug from the screenshot must be addressed by name.
        prompt.ShouldContain("My door is broken");
        prompt.ShouldContain("carpentry");

        // No adjective-as-slug fallouts.
        prompt.ShouldContain("broken", Case.Insensitive);
        prompt.ShouldContain("leaking", Case.Insensitive);
        prompt.ShouldContain("never", Case.Insensitive);

        // Canonical mapping cues.
        prompt.ShouldContain("plumbing", Case.Insensitive);
        prompt.ShouldContain("electrical", Case.Insensitive);
        prompt.ShouldContain("auto-repair", Case.Insensitive);
    }

    [Fact]
    public void ServiceExtraction_IsOpenDomain_WithSpellingNormalization()
    {
        var prompt = AiPrompts.ServiceExtractionSystem;

        // Closed-list reject rule must be gone — only return [] for inputs with
        // no profession/service signal at all, NOT just because the slug is
        // missing from the canonical mapping.
        prompt.ShouldNotContain("If no canonical service kind clearly fits, return [].");

        // Open-domain contract: emit kebab-case slug for unfamiliar professions.
        prompt.ShouldContain("data-analyst", Case.Insensitive);
        prompt.ShouldContain("I do data analyst", Case.Insensitive);
        prompt.ShouldContain("open-domain", Case.Insensitive);

        // Spelling-normalization cue.
        prompt.ShouldContain("Spelling normalization", Case.Insensitive);
        prompt.ShouldContain("analysist", Case.Insensitive);
        prompt.ShouldContain("analyst", Case.Insensitive);
    }
}
