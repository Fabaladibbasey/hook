using Hook.Features.ServiceRequest.Create;
using Shouldly;

namespace Hook.UnitTests.ServiceRequest;

public class ClientRequestStepPromptsTests
{
    // After the signature change `string? → string ""`, callers can omit the slug
    // for non-ConfirmService steps. Snapshot test pins that every enum value
    // returns non-empty so future appendages do not silently degrade to the
    // generic "continue with the request" fallback.
    [Theory]
    [InlineData(ClientRequestStep.AwaitingService)]
    [InlineData(ClientRequestStep.ResolvingService)]
    [InlineData(ClientRequestStep.ConfirmService)]
    [InlineData(ClientRequestStep.AwaitingLocation)]
    [InlineData(ClientRequestStep.ConfirmLocation)]
    [InlineData(ClientRequestStep.AwaitingDescription)]
    [InlineData(ClientRequestStep.AwaitingPhoneShareConsent)]
    [InlineData(ClientRequestStep.Done)]
    public void For_EveryStep_ReturnsNonEmpty(ClientRequestStep step) =>
        ClientRequestStepPrompts.For(step).ShouldNotBeNullOrWhiteSpace();

    [Fact]
    public void For_ConfirmService_FormatsSlugWithSpaces()
    {
        ClientRequestStepPrompts.For(ClientRequestStep.ConfirmService, "auto-repair")
            .ShouldContain("auto repair");
    }
}
