using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Feedback;
using Hook.Features.Feedback.Step2Prompt;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Phone;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Hook.UnitTests.Feedback;

public class Step2PromptDispatchHandlerTests
{
    private readonly Mock<IConversationAi> _ai = new();
    private readonly Mock<IWhatsappClient> _whatsapp = new();
    private readonly Mock<IFeedbackRepository> _feedback = new();

    private Step2PromptDispatchHandler Build() =>
        new(_ai.Object, _whatsapp.Object, _feedback.Object,
            NullLogger<Step2PromptDispatchHandler>.Instance);

    private static Step2PromptDispatchRequested Req() => new(
        FeedbackId: Guid.NewGuid(),
        MatchId: Guid.NewGuid(),
        ClientPhone: PhoneNumber.Parse("+220300001"),
        ServiceSlug: "plumbing");

    [Fact]
    public async Task Handle_AiReturnsText_SendsWhatsApp_DoesNotDeletePending()
    {
        var evt = Req();
        _ai.Setup(a => a.GenerateReplyAsync(It.IsAny<ReplyContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Is the job done?");

        await Build().Handle(evt, CancellationToken.None);

        _whatsapp.Verify(w => w.SendTextAsync(evt.ClientPhone, "Is the job done?", It.IsAny<CancellationToken>()), Times.Once);
        _feedback.Verify(f => f.DeletePendingAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_AiReturnsBlank_DeletesPending_DoesNotSendWhatsApp()
    {
        var evt = Req();
        _ai.Setup(a => a.GenerateReplyAsync(It.IsAny<ReplyContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("");

        await Build().Handle(evt, CancellationToken.None);

        _feedback.Verify(f => f.DeletePendingAsync(evt.FeedbackId, It.IsAny<CancellationToken>()), Times.Once);
        _whatsapp.Verify(w => w.SendTextAsync(It.IsAny<PhoneNumber>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
