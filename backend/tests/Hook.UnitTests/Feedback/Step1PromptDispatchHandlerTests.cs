using Hook.Features.Ai;
using Hook.Features.Ai.Models;
using Hook.Features.Feedback;
using Hook.Features.Feedback.Step1Prompt;
using Hook.Features.Whatsapp;
using Hook.Features.Whatsapp.Phone;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Hook.UnitTests.Feedback;

public class Step1PromptDispatchHandlerTests
{
    private readonly Mock<IConversationAi> _ai = new();
    private readonly Mock<IWhatsappClient> _whatsapp = new();
    private readonly Mock<IFeedbackRepository> _feedback = new();

    private Step1PromptDispatchHandler Build() =>
        new(_ai.Object, _whatsapp.Object, _feedback.Object,
            NullLogger<Step1PromptDispatchHandler>.Instance);

    private static PhoneNumber Phone() => PhoneNumber.Parse("+220300001");

    private static Step1PromptDispatchRequested Req(string picked = "") => new(
        FeedbackId: Guid.NewGuid(),
        MatchId: Guid.NewGuid(),
        RequestId: Guid.NewGuid(),
        ClientPhone: Phone(),
        ServiceSlug: "plumbing",
        PickedFormatted: picked);

    [Fact]
    public async Task Handle_AiReturnsText_SendsWhatsApp_DoesNotDeletePending()
    {
        var evt = Req();
        _ai.Setup(a => a.GenerateReplyAsync(It.IsAny<ReplyContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Did you find anyone?");

        await Build().Handle(evt, CancellationToken.None);

        _whatsapp.Verify(w => w.SendTextAsync(evt.ClientPhone, "Did you find anyone?", It.IsAny<CancellationToken>()), Times.Once);
        _feedback.Verify(f => f.DeletePendingAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_AiReturnsBlank_DeletesPending_DoesNotSendWhatsApp()
    {
        // AiReplyHelper maps blank/whitespace to null (logged as dropped) — handler
        // must unblock the partial unique by clearing the Pending row.
        var evt = Req();
        _ai.Setup(a => a.GenerateReplyAsync(It.IsAny<ReplyContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("   ");

        await Build().Handle(evt, CancellationToken.None);

        _feedback.Verify(f => f.DeletePendingAsync(evt.FeedbackId, It.IsAny<CancellationToken>()), Times.Once);
        _whatsapp.Verify(w => w.SendTextAsync(It.IsAny<PhoneNumber>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_AiThrows_DeletesPending_DoesNotSendWhatsApp()
    {
        var evt = Req();
        _ai.Setup(a => a.GenerateReplyAsync(It.IsAny<ReplyContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("ollama down"));

        await Build().Handle(evt, CancellationToken.None);

        _feedback.Verify(f => f.DeletePendingAsync(evt.FeedbackId, It.IsAny<CancellationToken>()), Times.Once);
        _whatsapp.Verify(w => w.SendTextAsync(It.IsAny<PhoneNumber>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_MultiPick_IncludesPickedProvidersInFacts()
    {
        var evt = Req(picked: "1) +220123, 2) +220456");
        ReplyContext? captured = null;
        _ai.Setup(a => a.GenerateReplyAsync(It.IsAny<ReplyContext>(), It.IsAny<CancellationToken>()))
            .Callback<ReplyContext, CancellationToken>((c, _) => captured = c)
            .ReturnsAsync("reply");

        await Build().Handle(evt, CancellationToken.None);

        var ctx = captured ?? throw new Xunit.Sdk.XunitException("ReplyContext not captured");
        var facts = ctx.Facts ?? throw new Xunit.Sdk.XunitException("Facts missing");
        Assert.Equal("1) +220123, 2) +220456", facts["pickedProviders"]);
    }
}
