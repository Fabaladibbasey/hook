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
    public void MatchPresenter_InstructsClient_HowToConnect()
    {
        var prompt = AiPrompts.MatchPresenterReplySystem;

        prompt.ShouldContain("Reply 1");
        prompt.ShouldContain("NEXT");
        prompt.ShouldContain("INCREASE");
        prompt.ShouldContain("masked", Case.Insensitive);
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
}
